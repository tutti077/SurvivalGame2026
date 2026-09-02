using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Arena ghost-spectate: a dead arena participant turns into an invisible, collision-free
/// fly-cam pawn confined to the arena bubble until the battle ends. Every machine hides the
/// ghost from the synced flag; only the owning client flies it.
/// </summary>
partial class PlayerMovement
{
	[Property, Group( "Arena Spectate" ), Title( "Spectate Fly Speed (m/s)" ), Range( 2f, 40f )]
	public float ArenaSpectateFlySpeedMeters { get; set; } = 12f;

	/// <summary>Host-synced: this pawn is dead in an arena battle and ghost-spectating.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool ArenaSpectateActive { get; private set; }

	[Sync( SyncFlags.FromHost )] public Vector3 ArenaSpectateCenter { get; private set; }
	[Sync( SyncFlags.FromHost )] public float ArenaSpectateRadius { get; private set; }

	bool _arenaSpectateApplied;
	bool _arenaSpectateSavedGravity = true;
	readonly List<ModelRenderer> _arenaHiddenRenderers = new();
	readonly List<ModelRenderer.ShadowRenderType> _arenaHiddenRenderTypes = new();
	readonly List<Collider> _arenaDisabledColliders = new();

	public void HostSetArenaSpectate( bool active, Vector3 centerWorld, float radiusUnits )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ArenaSpectateCenter = centerWorld;
		ArenaSpectateRadius = radiusUnits;
		ArenaSpectateActive = active;
		// Application happens in TickArenaSpectate on every machine (including this one).
	}

	/// <summary>Runs on all machines from <c>OnUpdate</c> — applies/undoes ghost state, flies the owner.</summary>
	void TickArenaSpectate()
	{
		if ( ArenaSpectateActive != _arenaSpectateApplied )
		{
			if ( ArenaSpectateActive )
				ApplyArenaGhost();
			else
				RestoreArenaGhost();
		}

		if ( !ArenaSpectateActive )
			return;

		// Re-assert the hide — PlayerAnimation's look-up fade can restore RenderType once.
		for ( var i = 0; i < _arenaHiddenRenderers.Count; i++ )
		{
			if ( _arenaHiddenRenderers[i] is { IsValid: true } renderer
			     && renderer.RenderType != ModelRenderer.ShadowRenderType.Off )
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		if ( IsLocalMovementDriver() )
			FlyArenaGhost();
	}

	void ApplyArenaGhost()
	{
		_arenaSpectateApplied = true;

		if ( GrappleAttached )
			DetachGrappleForHitReaction();

		_arenaHiddenRenderers.Clear();
		_arenaHiddenRenderTypes.Clear();
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null || !renderer.IsValid() )
				continue;
			_arenaHiddenRenderers.Add( renderer );
			_arenaHiddenRenderTypes.Add( renderer.RenderType );
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		_arenaDisabledColliders.Clear();
		foreach ( var collider in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( collider is null || !collider.IsValid() || !collider.Enabled )
				continue;
			_arenaDisabledColliders.Add( collider );
			collider.Enabled = false;
		}

		if ( IsLocalMovementDriver() )
		{
			_controller ??= Components.Get<PlayerController>();
			var body = _controller?.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() )
			{
				_arenaSpectateSavedGravity = body.Gravity;
				body.Gravity = false;
				body.Velocity = Vector3.Zero;
			}
		}
	}

	void RestoreArenaGhost()
	{
		_arenaSpectateApplied = false;

		for ( var i = 0; i < _arenaHiddenRenderers.Count; i++ )
		{
			if ( _arenaHiddenRenderers[i] is { IsValid: true } renderer )
				renderer.RenderType = _arenaHiddenRenderTypes[i];
		}

		_arenaHiddenRenderers.Clear();
		_arenaHiddenRenderTypes.Clear();

		foreach ( var collider in _arenaDisabledColliders )
		{
			if ( collider is { IsValid: true } )
				collider.Enabled = true;
		}

		_arenaDisabledColliders.Clear();

		if ( IsLocalMovementDriver() )
		{
			_controller ??= Components.Get<PlayerController>();
			var body = _controller?.Body ?? Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() )
			{
				body.Gravity = _arenaSpectateSavedGravity;
				body.Velocity = Vector3.Zero;
			}
		}
	}

	void FlyArenaGhost()
	{
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() )
			body.Velocity = Vector3.Zero;

		var look = _controller is not null
			? Rotation.From( _controller.EyeAngles )
			: GameObject.WorldRotation;

		var wish = Vector3.Zero;
		if ( Input.Down( "Forward" ) ) wish += look.Forward;
		if ( Input.Down( "Backward" ) ) wish -= look.Forward;
		if ( Input.Down( "Left" ) ) wish += look.Left;
		if ( Input.Down( "Right" ) ) wish += look.Right;
		if ( Input.Down( JumpInputAction ) ) wish += Vector3.Up;
		if ( Input.Down( SneakInputAction ) ) wish -= Vector3.Up;

		if ( wish.LengthSquared < 1e-6f )
			return;

		var speed = TerrainWorldUnits.MetersToEngine( Math.Max( 2f, ArenaSpectateFlySpeedMeters ) );
		var next = GameObject.WorldPosition + wish.Normal * speed * Time.Delta;

		// Spectators stay inside the arena bubble too.
		if ( ArenaSpectateRadius > 1f )
		{
			var fromCenter = next - ArenaSpectateCenter;
			if ( fromCenter.Length > ArenaSpectateRadius )
				next = ArenaSpectateCenter + fromCenter.Normal * ArenaSpectateRadius;
		}

		GameObject.WorldPosition = next;
	}

	/// <summary>Owner input gate while ghost-spectating — no jump/sprint/attacks feed gameplay.</summary>
	bool PreInputArenaSpectate()
	{
		if ( !ArenaSpectateActive )
			return false;

		ClearActionIfPressed( JumpInputAction );
		ClearActionIfPressed( SneakInputAction );
		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
			ClearActionIfPressed( SprintInputAction );
		if ( Input.Pressed( "Attack1" ) || Input.Down( "Attack1" ) )
			Input.SetAction( "Attack1", false );
		if ( Input.Pressed( "Attack2" ) || Input.Down( "Attack2" ) )
			Input.SetAction( "Attack2", false );
		return true;
	}
}
