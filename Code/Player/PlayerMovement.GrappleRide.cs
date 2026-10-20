using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Riding anchors (Mark 2026-10-08: "when I grapple to the platform then move the platform the grapple anchor
/// point stays floating in the air"). A surface hook that lands on something that moves — the hover platform,
/// a buggy, a piece built on the platform's deck, any loose physics prop — is stored in that body's local
/// space, like a player or tow hook, and every machine resolves it from the body's live transform. The rope
/// then rides the body and drags or carries the holder with it. Static world (terrain, trees, placed bases)
/// keeps the fixed world point it always had.
///
/// Decided once, on the host, at attach time: one small physics overlap at the validated point picks the
/// collider under the hook and walks up to the first enabled, simulated <see cref="Rigidbody"/>. Players and
/// tow-tagged logs keep their own attach kinds and never reach this.
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Host-synced: the moving body a surface hook rides (<see cref="Guid.Empty"/> = a fixed world point). Offset is <see cref="GrappleAttachLocalOffset"/>.</summary>
	[Sync( SyncFlags.FromHost )] public Guid GrappleAttachRideId { get; private set; }

	/// <summary>Probe half-size around the validated hook point when looking for the collider it sits on (units, 0.1 m).</summary>
	const float RideProbeUnits = 4f;

	/// <summary>Host, once at attach: if the hook point sits on a simulated rigidbody, ride it.</summary>
	void HostTryBindGrappleRide( Vector3 validatedPoint )
	{
		var carrier = FindGrappleRideCarrier( validatedPoint );
		if ( carrier is null )
			return;

		GrappleAttachRideId = carrier.Id;
		GrappleAttachLocalOffset = carrier.WorldTransform.PointToLocal( validatedPoint );
		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: hook rides '{carrier.Name}'." );
	}

	GameObject FindGrappleRideCarrier( Vector3 point )
	{
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return null;

		GameObject best = null;
		var bestDist = RideProbeUnits * 1.5f;
		foreach ( var go in scene.FindInPhysics( BBox.FromPositionAndSize( point, RideProbeUnits * 2f ) ) )
		{
			if ( go is null || !go.IsValid() || go.Root == GameObject.Root )
				continue;
			if ( TryGetGrapplePlayerRoot( go, out _ ) )
				continue;

			var carrier = FindSimulatedBodyOnHierarchy( go );
			if ( carrier is null || ObjectHasTowTag( carrier ) )
				continue;

			foreach ( var col in go.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
			{
				if ( col is null || !col.IsValid() || !col.Enabled || col.IsTrigger )
					continue;

				var d = Vector3.DistanceBetween( col.FindClosestPoint( point ), point );
				if ( d >= bestDist )
					continue;

				bestDist = d;
				best = carrier;
			}
		}

		return best;
	}

	/// <summary>The object that moves this one: the first ancestor (or itself) with an enabled, simulated rigidbody.</summary>
	static GameObject FindSimulatedBodyOnHierarchy( GameObject go )
	{
		for ( var cur = go; cur is not null && cur.IsValid(); cur = cur.Parent )
		{
			var body = cur.Components.Get<Rigidbody>();
			if ( body is not null && body.Enabled && body.MotionEnabled )
				return cur;
		}

		return null;
	}

	bool TryResolveGrappleRideTarget( Guid id, out GameObject target )
	{
		target = null;
		if ( id == Guid.Empty )
			return false;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		target = scene.Directory.FindByGuid( id );
		return target.IsValid();
	}

	/// <summary>Host tick: the ridden body was destroyed (wrecked platform) or flew impossibly far — drop the rope.</summary>
	void TickGrappleRideValidity()
	{
		if ( !GrappleAttached || GrappleAttachRideId == Guid.Empty )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( !TryResolveGrappleRideTarget( GrappleAttachRideId, out var target ) )
		{
			ServerDetach( "ridden body lost" );
			return;
		}

		if ( Vector3.DistanceBetween( GameObject.WorldPosition, target.WorldTransform.PointToWorld( GrappleAttachLocalOffset ) ) > GetMaxRangeEngine() * 1.25f )
			ServerDetach( "ridden body out of range" );
	}

	void ClearGrappleRideAttachState()
	{
		GrappleAttachRideId = Guid.Empty;
	}
}
