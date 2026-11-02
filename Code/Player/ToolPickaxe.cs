using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Pickaxe tool (spawned from <c>prefabs/tools/pickaxe_tool.prefab</c> while a <c>Mine</c> item is in the active hotbar slot).
/// Mark 2026-11-02: "like the tool we used in the voxeltest scene, where it removes just a little bit at a time in a circle
/// projected onto the surface of where you're looking. not a sphere! Thats how pickaxes work." Aim at the ground within
/// reach: a ring shows the circle the pick will take, lying on the surface (flat on a floor, upright on a cliff). Attack1
/// chips a shallow disc of that radius and <see cref="ChipDepthMeters"/> deep out of it (<see cref="VoxelBrushMode.Chip"/>);
/// holding the button swings again every <see cref="SwingSeconds"/>. The stroke goes through <see cref="PlayerTerraform"/>
/// (host re-checks reach and tool, one durability tick per stroke that changed the ground). Does nothing in a scene without
/// voxel ground.
/// </summary>
[Title( "Tool Pickaxe" )]
public sealed class ToolPickaxe : Component
{
	[Property, Group( "Input" )] public string SwingAction { get; set; } = "Attack1";

	[Property, Group( "Chip" ), Title( "Reach (m)" ), Range( 1f, 10f ), Step( 0.5f )]
	public float ReachMeters { get; set; } = 4f;

	[Property, Group( "Chip" ), Title( "Chip Radius (m)" ), Range( 0.25f, 2f ), Step( 0.05f ), Description( "Radius of the circle the pick takes off the surface." )]
	public float ChipRadiusMeters { get; set; } = 0.6f;

	[Property, Group( "Chip" ), Title( "Chip Depth (m)" ), Range( 0.1f, 1f ), Step( 0.05f ), Description( "How deep under the surface one swing cuts - a little bit at a time." )]
	public float ChipDepthMeters { get; set; } = 0.3f;

	[Property, Group( "Chip" ), Title( "Swing Interval (s)" ), Range( 0.1f, 2f ), Step( 0.05f ), Description( "Holding the button swings again this often." )]
	public float SwingSeconds { get; set; } = 0.5f;

	[Property, Group( "Chip" ), Title( "Show Ring" )] public bool ShowRing { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogChips { get; set; }

	static readonly Color RingColor = new( 0.95f, 0.85f, 0.4f );

	GameObject _pawn;
	PlayerVitals _vitals;
	PlayerGameMenuController _menu;
	PlayerTerraform _terraform;
	double _nextSwingAt;

	public void BindPawn( GameObject pawn ) => _pawn = pawn;

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsLocalDriver() )
			return;
		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		// Dev: console `swing` holds the attack action for a few frames (GameHacks) - the tool may update before PlayerCombat does
		GameHacks.ApplySimulatedInput( SwingAction );

		_terraform ??= Pawn.Components.Get<PlayerTerraform>();
		var target = _terraform?.Target;
		if ( target is null || !target.IsReady )
			return;

		if ( !BuildViewCamera.TryGetViewRay( Pawn, out var origin, out var direction ) )
			return;
		var scene = Pawn.Scene.IsValid() ? Pawn.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ReachMeters ) );
		var tr = FarmingRules.TraceView( scene, Pawn, origin, direction, reach );
		if ( !tr.Hit || !IsGround( tr.GameObject ) )
			return;

		var normal = tr.Normal.LengthSquared > 1e-6f ? tr.Normal.Normal : Vector3.Up;
		var affordable = _terraform is null || _terraform.CanAffordStroke( VoxelBrushMode.Chip, out _ );
		if ( ShowRing )
			VoxelBrushRings.DrawSurfaceRing( scene, tr.HitPosition, normal, ChipRadiusMeters, affordable ? RingColor : VoxelBrushRings.BlockedColor );

		var pressed = Input.Pressed( SwingAction );
		var held = Input.Down( SwingAction ) && Time.NowDouble >= _nextSwingAt;
		if ( LogChips && Input.Down( SwingAction ) )
			Log.Info( $"[ToolPickaxe] down (pressed {pressed}) aim {tr.GameObject?.Name} at {tr.Distance:0} u" );
		if ( !pressed && !held )
			return;
		_nextSwingAt = Time.NowDouble + Math.Max( 0.1f, SwingSeconds );

		if ( ToolDurability.IsActiveToolBroken( Pawn ) )
		{
			if ( LogChips )
				Log.Info( "[ToolPickaxe] swing rejected: pickaxe broken - repair at a workbench." );
			return;
		}
		if ( !affordable )
		{
			if ( LogChips )
				Log.Info( "[ToolPickaxe] swing rejected: too tired." );
			return;
		}
		if ( _terraform is null )
		{
			Log.Warning( "[ToolPickaxe] Pawn has no PlayerTerraform component - add it to the player prefab." );
			return;
		}
		_terraform.OwnerRequestChip( tr.HitPosition, normal, ChipRadiusMeters, ChipDepthMeters, ReachMeters );
		if ( LogChips )
			Log.Info( $"[ToolPickaxe] chip at {TerrainWorldUnits.EngineToMeters( tr.HitPosition )} m, normal {normal}" );
	}

	/// <summary>Only the world's ground (heightfield chunks and voxel chunks) takes a chip - not build pieces, props or rocks.</summary>
	public static bool IsGround( GameObject hit )
	{
		for ( var g = hit; g.IsValid(); g = g.Parent )
		{
			if ( g.Tags.Has( TerrainWorldManager.TerrainTag ) || g.Tags.Has( "voxel" ) )
				return true;
		}
		return false;
	}

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
