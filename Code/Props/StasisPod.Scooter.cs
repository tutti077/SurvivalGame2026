using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The folded scooter docked under the pod's seat (Mark 2026-11-19: "a kinda small moped scooter inside the pod
/// all folded up that the player can pick up and deploy whenever they want"). Every pod carries one: the
/// <see cref="Scooter"/> child shows the folded model in its cradle until someone takes it. E on it once the pod
/// has landed hands the pawn <see cref="ScooterItemId"/> (the ground beside them if the bag is full) and the
/// cradle stays empty for everyone. The item unfolds into the vehicle through <see cref="Vehicle.HostTryDeployFromItem"/>.
/// </summary>
public sealed partial class StasisPod
{
	/// <summary>Child holding the folded scooter's renderer + box collider; disabled once taken.</summary>
	[Property, Group( "Scooter" )]
	public GameObject Scooter { get; set; }

	[Property, Group( "Scooter" ), Title( "Scooter Item Id" )]
	public string ScooterItemId { get; set; } = "vehicle_pod_scooter_folded";

	/// <summary>How close a pawn must stand to the pod to take the scooter (m).</summary>
	[Property, Group( "Scooter" ), Title( "Scooter Take Reach (m)" )]
	public float ScooterTakeReachMeters { get; set; } = 3.5f;

	[Sync] public bool ScooterTaken { get; private set; }

	/// <summary>Every enabled pod, so the focus trace is skipped in a scene with none.</summary>
	public static readonly List<StasisPod> Registered = new();

	public bool HasScooter => !ScooterTaken && Scooter is { IsValid: true } && !string.IsNullOrWhiteSpace( ScooterItemId );

	/// <summary>The scooter can be lifted out once the pod stands on the ground (not in flight).</summary>
	public bool CanTakeScooter => HasScooter && HasLanded;

	public string ScooterPromptText
		=> $"Take {ResourceCatalog.Resolve( ScooterItemId ).DisplayName}";

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Registered.Contains( this ) )
			Registered.Add( this );
	}

	void UpdateScooter()
	{
		if ( Scooter is { IsValid: true } && Scooter.Enabled == ScooterTaken )
			Scooter.Enabled = !ScooterTaken;
	}

	/// <summary>Host: hand the docked scooter to this pawn. A full bag puts it on the ground at their feet.</summary>
	public bool HostTryTakeScooter( GameObject pawn )
	{
		if ( !HasHostAuthority || !CanTakeScooter || pawn is null || !pawn.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, ScooterTakeReachMeters ) );
		if ( Vector3.DistanceBetween( pawn.WorldPosition, WorldPosition ) > reach )
			return false;

		var inventory = pawn.Components.Get<PlayerInventory>();
		if ( inventory is null || !inventory.HostTryAddResource( ScooterItemId, 1 ) )
		{
			var drop = HeldStackWorldDrop.TrySpawnWorldDrop( Scene, ScooterItemId, 1, pawn.WorldPosition + Vector3.Up * 30f, pawn );
			if ( drop is null || !drop.IsValid() )
				return false;
		}

		ScooterTaken = true;
		return true;
	}

	/// <summary>Host (dev): put the scooter back in the cradle.</summary>
	public void HostRestockScooter()
	{
		if ( HasHostAuthority )
			ScooterTaken = false;
	}

	/// <summary>Pod whose docked scooter is under the viewer's crosshair within <paramref name="reachMeters"/>.</summary>
	public static bool TryFindFocusedScooter( GameObject viewer, float reachMeters, out StasisPod pod )
	{
		pod = null;
		if ( Registered.Count == 0 || viewer is null || !viewer.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var dir ) )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = viewer.Scene.Trace.Ray( origin, origin + dir * reach )
			.IgnoreGameObjectHierarchy( viewer.Root )
			.Run();
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return false;

		var found = tr.GameObject.Components.Get<StasisPod>( FindMode.EverythingInSelfAndAncestors );
		if ( found is null || !found.IsValid() || !found.CanTakeScooter )
			return false;

		// The hit is the scooter's own collider, or (if the trace reports the pod) lands inside its box.
		var onScooter = false;
		for ( var go = tr.GameObject; go is not null && go.IsValid(); go = go.Parent )
		{
			if ( go == found.Scooter )
			{
				onScooter = true;
				break;
			}
			if ( go == found.GameObject )
				break;
		}

		if ( !onScooter && found.Scooter.Components.Get<BoxCollider>() is { IsValid: true } box )
		{
			var local = found.Scooter.WorldTransform.PointToLocal( tr.HitPosition ) - box.Center;
			var half = box.Scale * 0.5f + 3f;
			onScooter = MathF.Abs( local.x ) <= half.x && MathF.Abs( local.y ) <= half.y && MathF.Abs( local.z ) <= half.z;
		}

		if ( !onScooter )
			return false;

		pod = found;
		return true;
	}
}
