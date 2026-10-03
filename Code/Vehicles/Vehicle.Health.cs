using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hit points. Damage arrives through the prefab's <see cref="DamageReceiver"/>
/// (<see cref="DamageReceiver.TakeDamage"/> routes to <see cref="HostApplyDamage"/>). At zero the host
/// puts every rider out, scatters the storage contents (fuel included) on the ground as world drops,
/// and destroys the vehicle. <see cref="MaxHealth"/> 100 = ten light sword hits (Mark, 2026-10-02).
/// </summary>
public sealed partial class Vehicle
{
	[Property, Group( "Health" ), Title( "Max Health" )]
	public float MaxHealth { get; set; } = 100f;

	[Sync( SyncFlags.FromHost )]
	public float Health { get; private set; }

	[Sync( SyncFlags.FromHost )]
	public bool IsBroken { get; private set; }

	void HostSeedHealth()
	{
		if ( !HasHostAuthority )
			return;

		Health = Math.Max( 1f, MaxHealth );
		IsBroken = false;
	}

	/// <summary>Host: apply damage; returns the amount removed.</summary>
	public float HostApplyDamage( float amount, Component attacker )
	{
		if ( !HasHostAuthority || IsPreviewGhost || IsBroken || amount <= 0f )
			return 0f;

		var before = Health;
		Health = Math.Max( 0f, Health - amount );
		if ( Health <= 0.001f )
			HostBreak();

		return before - Health;
	}

	void HostBreak()
	{
		if ( IsBroken )
			return;

		IsBroken = true;
		HostEjectAll();
		HostDumpStorage();
		GameObject.Destroy();
	}

	/// <summary>Host: every stack in the storage box becomes a pickup around the wreck.</summary>
	void HostDumpStorage()
	{
		if ( _storage is null || !_storage.IsValid() )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var origin = WorldPosition + Vector3.Up * 20f;
		for ( var i = 0; i < _storage.SlotCount; i++ )
		{
			var slot = _storage.GetSlot( i );
			if ( slot.IsEmpty )
				continue;

			var yaw = Sandbox.Game.Random.Float( 0f, 360f );
			var outward = Rotation.FromYaw( yaw ).Forward;
			var offset = outward * Sandbox.Game.Random.Float( 30f, 70f ) + Vector3.Up * Sandbox.Game.Random.Float( 10f, 30f );
			var drop = HeldStackWorldDrop.TrySpawnWorldDrop(
				scene,
				slot.ResourceId,
				slot.Count,
				origin + offset,
				GameObject,
				applyDropperSelfPickupDelay: false,
				wear: slot.Wear,
				crafterName: slot.CrafterName );
			if ( drop is not null && drop.IsValid() )
				HeldStackWorldDrop.ApplyScatterBurst( drop, outward );
		}
	}
}
