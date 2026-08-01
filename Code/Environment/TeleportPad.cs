using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Debug teleporter pad: snaps any locally-simulated player pawn standing on it to
/// <see cref="Destination"/> (usually another pad). Detection is a plain position-in-box test
/// (axis-aligned; pads are meant to sit flat) — no physics triggers, so it cannot miss the
/// player controller. The box is centered on the pad's visible platform (first ModelRenderer in
/// the hierarchy), so viewport-dragging the purple mesh moves the detection volume with it.
/// Two guards stop ping-pong loops:
///   1. hold-until-exit — the destination pad latches the arriving pawn and refuses to teleport
///      them until they step out of its volume;
///   2. a short per-pawn cooldown shared across all pads.
/// Author pads in the scene (teleport1a / teleport1b, teleport2a / …) and point each pad's
/// Destination at its partner.
/// </summary>
[Title( "Teleport Pad" )]
public sealed class TeleportPad : Component
{
	/// <summary>Pawns land at this object's position plus <see cref="ArrivalHeightMeters"/>.</summary>
	[Property] public GameObject Destination { get; set; }

	[Property, Title( "Arrival Height (meters)" )]
	public float ArrivalHeightMeters { get; set; } = 0.25f;

	/// <summary>Half of the pad footprint on X and Y (default matches the 2 m × 2 m visual).</summary>
	[Property, Title( "Pad Half Width (meters)" )]
	public float PadHalfWidthMeters { get; set; } = 1f;

	/// <summary>How far above the pad a pawn still counts as standing on it.</summary>
	[Property, Title( "Detect Height (meters)" )]
	public float DetectHeightMeters { get; set; } = 1.2f;

	/// <summary>Backup guard on top of the hold-until-exit latch (covers overlapping volumes).</summary>
	[Property, Title( "Per-Pawn Cooldown (seconds)" )]
	public float CooldownSeconds { get; set; } = 1f;

	[Property, Group( "Debug" )] public bool LogTeleports { get; set; }

	/// <summary>Give the arriving pawn a moment to register inside the volume before the latch may release.</summary>
	const double LatchMinHoldSeconds = 0.75;
	/// <summary>Pawns may sink slightly into the pad — accept feet a little below its origin.</summary>
	const float DetectBelowMeters = 0.35f;

	/// <summary>Pawn id → latch time. Latched pawns arrived here and have not stepped off yet.</summary>
	readonly Dictionary<Guid, double> _holdUntilExit = new();
	/// <summary>Shared across all pads so chained pads can't relay a pawn instantly.</summary>
	static readonly Dictionary<Guid, double> NextTeleportAllowedAt = new();

	bool _warnedMissingDestination;

	protected override void OnUpdate()
	{
		var touching = CollectPawnsOnPad();

		// Release latched pawns once they leave (after the arrival grace window).
		foreach ( var id in _holdUntilExit.Keys.ToArray() )
		{
			if ( touching.ContainsKey( id ) )
				continue;
			if ( Time.NowDouble < _holdUntilExit[id] + LatchMinHoldSeconds )
				continue;
			_holdUntilExit.Remove( id );
		}

		if ( touching.Count == 0 )
			return;

		if ( Destination is null || !Destination.IsValid() )
		{
			if ( !_warnedMissingDestination )
			{
				_warnedMissingDestination = true;
				Log.Warning( $"[TeleportPad] {GameObject.Name}: pawn on pad but Destination is not set." );
			}

			return;
		}

		foreach ( var (id, pawn) in touching )
		{
			// Pawn transforms are owner-simulated — teleport on the machine that drives this pawn.
			if ( pawn.IsProxy )
				continue;
			if ( _holdUntilExit.ContainsKey( id ) )
				continue;
			if ( NextTeleportAllowedAt.TryGetValue( id, out var allowedAt ) && Time.NowDouble < allowedAt )
				continue;

			NextTeleportAllowedAt[id] = Time.NowDouble + Math.Max( 0.1, CooldownSeconds );
			TeleportPawn( pawn );
		}
	}

	void TeleportPawn( GameObject pawn )
	{
		// Land on the destination's visible platform (its root may not be where its mesh is).
		var destPad = Destination.Components.Get<TeleportPad>( FindMode.EverythingInSelf );
		var arrivalBase = destPad is not null ? destPad.PadCenterWorld() : Destination.WorldPosition;
		var arrivalPos = arrivalBase
			+ Vector3.Up * TerrainWorldUnits.MetersToEngine( Math.Max( 0f, ArrivalHeightMeters ) );

		pawn.WorldPosition = arrivalPos;
		pawn.Transform.ClearInterpolation();
		if ( pawn.Network is { Active: true } )
			pawn.Network.ClearInterpolation();

		var rb = pawn.Components.Get<Rigidbody>();
		if ( rb is not null )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}

		// Latch on the destination pad so standing there doesn't bounce the pawn straight back.
		destPad?.HoldUntilExit( pawn.Id );

		if ( LogTeleports )
			Log.Info( $"[TeleportPad] {GameObject.Name}: teleported {pawn.Name} → {Destination.Name} @ {arrivalPos}" );
	}

	/// <summary>Called by the source pad on arrival: don't teleport this pawn until it steps off.</summary>
	public void HoldUntilExit( Guid pawnId ) => _holdUntilExit[pawnId] = Time.NowDouble;

	Dictionary<Guid, GameObject> CollectPawnsOnPad()
	{
		var found = new Dictionary<Guid, GameObject>();
		if ( !Scene.IsValid() )
			return found;

		foreach ( var movement in Scene.GetAllComponents<PlayerMovement>() )
		{
			if ( movement is null || !movement.GameObject.IsValid() )
				continue;

			if ( IsInsidePadVolume( movement.GameObject.WorldPosition ) )
				found[movement.GameObject.Id] = movement.GameObject;
		}

		return found;
	}

	/// <summary>
	/// Where the pad visibly is: the first ModelRenderer under this object (viewport drags often
	/// move the mesh child, not the root), falling back to the root position.
	/// </summary>
	public Vector3 PadCenterWorld()
	{
		var renderer = Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		return renderer is not null && renderer.GameObject.IsValid()
			? renderer.GameObject.WorldPosition
			: GameObject.WorldPosition;
	}

	/// <summary>Axis-aligned box around the visible platform: footprint on X/Y, feet-height band on Z.</summary>
	bool IsInsidePadVolume( Vector3 worldPosition )
	{
		var local = worldPosition - PadCenterWorld();
		var half = TerrainWorldUnits.MetersToEngine( Math.Max( 0.1f, PadHalfWidthMeters ) );
		if ( Math.Abs( local.x ) > half || Math.Abs( local.y ) > half )
			return false;

		var below = TerrainWorldUnits.MetersToEngine( DetectBelowMeters );
		var above = TerrainWorldUnits.MetersToEngine( Math.Max( 0.2f, DetectHeightMeters ) );
		return local.z >= -below && local.z <= above;
	}
}
