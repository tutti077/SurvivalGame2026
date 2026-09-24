using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Wire stripper tool (spawned from <c>prefabs/tools/wire_stripper_tool.prefab</c> while a
/// <c>tool_wireStripper</c> item is in the active hotbar slot; <see cref="EquippedItemActions.Wire"/>).
/// <para>
/// With it out, every circuit-enabled object in range shows its coloured sphere
/// (<see cref="CircuitWireOverlay"/>). Press Attack1 on one sphere, walk, release on another: a wire
/// is laid from the first to the second in the selected cable colour, or removed if one already ran
/// that way. Attack2 opens the cable colour menu (<see cref="WireCableMenuHud"/>), like the hammer's
/// build menu. The owner only sends intent; the host validates once in <see cref="CircuitRegistry.HostToggleLink"/>.
/// </para>
/// </summary>
[Title( "Tool Wire Stripper" )]
public sealed class ToolWireStripper : Component
{
	[Property, Group( "Input" )] public string LinkAction { get; set; } = "Attack1";
	[Property, Group( "Input" ), Title( "Cable menu action" )] public string CableMenuAction { get; set; } = "Attack2";

	/// <summary>How far a sphere can be to press or release on it.</summary>
	[Property, Group( "Wiring" ), Title( "Reach (m)" ), Range( 1f, 12f ), Step( 0.5f )]
	public float ReachMeters { get; set; } = 6f;

	/// <summary>Spheres and wires are drawn for nodes within this distance of the pawn.</summary>
	[Property, Group( "Wiring" ), Title( "Show range (m)" ), Range( 5f, 100f ), Step( 1f )]
	public float ShowRangeMeters { get; set; } = 30f;

	[Property, Group( "Debug" )] public bool LogWiring { get; set; }

	/// <summary>Cable colour the next wire is laid in.</summary>
	public CircuitCable SelectedCable { get; private set; } = CircuitCable.Red;

	public bool IsCableMenuOpen { get; private set; }

	/// <summary>Sphere under the crosshair this frame (drawn 10% bigger).</summary>
	public CircuitNode HoveredNode { get; private set; }

	/// <summary>Sphere Attack1 went down on; a wire is being dragged from it.</summary>
	public CircuitNode DragSource { get; private set; }

	public bool IsDragging => DragSource is { IsValid: true };

	public event Action SelectedCableChanged;
	public event Action CableMenuOpenChanged;

	GameObject _pawn;
	PlayerVitals _vitals;
	PlayerGameMenuController _menu;
	CircuitWireOverlay _overlay;
	double _nextDiagnosticAt;

	public void BindPawn( GameObject pawn ) => _pawn = pawn;

	protected override void OnDestroy()
	{
		if ( IsCableMenuOpen )
			SetCableMenuOpen( false );

		_overlay?.Dispose();
		_overlay = null;
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsLocalDriver() )
			return;

		if ( _menu is not null && _menu.IsMenuOpen )
		{
			DragSource = null;
			HoveredNode = null;
			if ( IsCableMenuOpen )
				SetCableMenuOpen( false );
			_overlay?.HideAll();
			return;
		}

		if ( !string.IsNullOrWhiteSpace( CableMenuAction ) && Input.Pressed( CableMenuAction ) )
			SetCableMenuOpen( !IsCableMenuOpen );

		var scene = ResolveScene();
		if ( !BuildViewCamera.TryGetViewRay( Pawn, out var origin, out var direction ) || scene is null || !scene.IsValid() )
		{
			HoveredNode = null;
			_overlay?.HideAll();
			return;
		}

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ReachMeters ) );
		HoveredNode = IsCableMenuOpen ? null : CircuitRegistry.FindNodeUnderView( origin, direction, reach, out _ );

		if ( !IsCableMenuOpen )
			PollLinkInput();

		var dragEnd = origin + direction * reach;
		if ( IsDragging )
		{
			if ( HoveredNode is { IsValid: true } && HoveredNode != DragSource )
				dragEnd = HoveredNode.SphereWorldPosition;
			else
			{
				// Local feedback only: the loose end of the wire follows whatever is under the crosshair.
				var tr = scene.Trace.Ray( origin, origin + direction * reach ).IgnoreGameObjectHierarchy( Pawn ).Run();
				if ( tr.Hit )
					dragEnd = tr.HitPosition;
			}
		}

		_overlay ??= new CircuitWireOverlay();
		_overlay.Tick(
			scene,
			Pawn.WorldPosition,
			TerrainWorldUnits.MetersToEngine( Math.Max( 1f, ShowRangeMeters ) ),
			HoveredNode,
			DragSource,
			dragEnd,
			SelectedCable );

		if ( LogWiring && Time.NowDouble >= _nextDiagnosticAt )
		{
			_nextDiagnosticAt = Time.NowDouble + 1.0;
			var enabled = 0;
			for ( var i = 0; i < CircuitNode.All.Count; i++ )
			{
				var n = CircuitNode.All[i];
				if ( n is { IsValid: true } && !n.IsPreviewGhost && n.IsCircuitEnabled )
					enabled++;
			}

			Log.Info( $"[ToolWireStripper] nodes={CircuitNode.All.Count} circuitEnabled={enabled} drawn={_overlay.VisibleCount} hovered={(HoveredNode.IsValid() ? HoveredNode.GameObject.Name : "-")}" );
		}
	}

	void PollLinkInput()
	{
		if ( string.IsNullOrWhiteSpace( LinkAction ) )
			return;

		if ( Input.Pressed( LinkAction ) )
		{
			DragSource = HoveredNode is { IsValid: true } ? HoveredNode : null;
			if ( LogWiring && DragSource is not null )
				Log.Info( $"[ToolWireStripper] Wire from {DragSource.GameObject.Name} ({SelectedCable.DisplayName()})" );
			return;
		}

		if ( !Input.Released( LinkAction ) )
			return;

		var source = DragSource;
		DragSource = null;
		if ( source is null || !source.IsValid() )
			return;

		var target = HoveredNode;
		if ( target is null || !target.IsValid() || target == source )
		{
			if ( LogWiring )
				Log.Info( "[ToolWireStripper] Wire dropped — released off a sphere." );
			return;
		}

		if ( ToolDurability.IsActiveToolBroken( Pawn ) )
		{
			if ( LogWiring )
				Log.Info( "[ToolWireStripper] Wire rejected: stripper broken — repair at a workbench." );
			return;
		}

		var interaction = Pawn.Components.Get<PlayerInventoryInteraction>();
		if ( interaction is null )
		{
			Log.Warning( "[ToolWireStripper] Pawn has no PlayerInventoryInteraction component — add it to the player prefab." );
			return;
		}

		interaction.OwnerRequestToggleCircuitLink( source, target, SelectedCable, ReachMeters );
		if ( LogWiring )
			Log.Info( $"[ToolWireStripper] Wire {source.GameObject.Name} → {target.GameObject.Name} requested ({SelectedCable.DisplayName()})." );
	}

	public void SetCableMenuOpen( bool open )
	{
		if ( IsCableMenuOpen == open )
			return;

		IsCableMenuOpen = open;
		if ( open )
			DragSource = null;

		CableMenuOpenChanged?.Invoke();
	}

	public void SetCable( CircuitCable cable )
	{
		if ( SelectedCable == cable )
			return;

		SelectedCable = cable;
		SelectedCableChanged?.Invoke();

		if ( LogWiring )
			Log.Info( $"[ToolWireStripper] Cable: {cable.DisplayName()}" );
	}

	Scene ResolveScene() => Pawn.Scene.IsValid() ? Pawn.Scene : Sandbox.Game.ActiveScene;

	GameObject Pawn => _pawn is { IsValid: true } ? _pawn : GameObject;

	bool IsLocalDriver()
	{
		if ( _pawn is null || !_pawn.IsValid() )
			return false;

		_vitals ??= _pawn.Components.Get<PlayerVitals>();
		_menu ??= _pawn.Components.Get<PlayerGameMenuController>();
		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
	}
}
