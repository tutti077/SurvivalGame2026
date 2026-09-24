using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hammer-placed lever (<c>circuit_lever</c> in <c>data/build_pieces.json</c>). Look + E flips it;
/// the host owns <see cref="IsOn"/> (<c>[Sync]</c>); while it is on, every circuit its
/// <see cref="CircuitNode"/> is wired into is live. A source: its wired input is ignored.
/// </summary>
[Title( "Circuit Lever" )]
public sealed class CircuitLever : Component, ICircuitDevice
{
	[Property, Group( "Lever" ), Title( "Use reach (m)" ), Range( 1f, 8f )]
	public float UseReachMeters { get; set; } = 3f;

	[Property, Group( "Lever" ), Title( "On color" )]
	public Color OnColor { get; set; } = new( 0.62f, 0.62f, 0.66f );

	[Property, Group( "Lever" ), Title( "Off color" )]
	public Color OffColor { get; set; } = new( 0.4f, 0.4f, 0.44f );

	[Property, Group( "Debug" ), Title( "Log lever" )]
	public bool LogLever { get; set; }

	/// <summary>Host → everyone: the lever is thrown.</summary>
	[Sync] public bool IsOn { get; private set; }

	public string PromptText => IsOn ? "Switch Off" : "Switch On";

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	ModelRenderer _renderer;
	bool _visualOn;
	bool _visualApplied;

	bool IsPreviewGhost => Components.Get<BuildPiece>() is { IsPreviewGhost: true } || GameObject.Tags.Has( "buildpreview" );

	public bool ComputeOutput( bool powered ) => IsOn;

	protected override void OnStart()
	{
		base.OnStart();
		ApplyVisual( force: true );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		ApplyVisual( force: false );
	}

	/// <summary>Host: E on the lever. Reach is the caller's job (owner trace, host re-check in the RPC).</summary>
	public void HostToggle( GameObject user )
	{
		if ( !HasHostAuthority || IsPreviewGhost )
			return;

		IsOn = !IsOn;
		CircuitRegistry.MarkDirty();

		if ( LogLever )
			Log.Info( $"[CircuitLever] {GameObject.Name}: {(IsOn ? "on" : "off")}{(user.IsValid() ? $" by {user.Name}" : string.Empty)}" );
	}

	public bool IsWithinUseReach( GameObject user )
	{
		if ( user is null || !user.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseReachMeters ) ) + 48f;
		return (user.WorldPosition - GameObject.WorldPosition).LengthSquared <= reach * reach;
	}

	void ApplyVisual( bool force )
	{
		if ( !force && _visualApplied && _visualOn == IsOn )
			return;

		_visualApplied = true;
		_visualOn = IsOn;

		if ( IsPreviewGhost )
			return;

		_renderer ??= Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( _renderer is null || !_renderer.IsValid() )
			return;

		_renderer.Tint = IsOn ? OnColor : OffColor;
	}

	/// <summary>Placed lever under the viewer's crosshair within <paramref name="reachMeters"/> (ghosts never count).</summary>
	public static bool TryFindFocusedLever( GameObject viewer, float reachMeters, out CircuitLever lever )
	{
		lever = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var direction ) )
			return false;

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = scene.Trace.Ray( origin, origin + direction * reach )
			.IgnoreGameObjectHierarchy( viewer )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
		{
			var candidate = go.Components.Get<CircuitLever>();
			if ( candidate is not null && candidate.Enabled && !candidate.IsPreviewGhost )
			{
				lever = candidate;
				return true;
			}
		}

		return false;
	}
}
