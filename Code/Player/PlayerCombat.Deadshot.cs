using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Deadshot volley: the host lands one shot on every tag the owner placed — direct damage at the
/// tagged point plus a stuck arrow riding the victim there, one ammo and one bow wear per shot.
/// Guaranteed by design (no projectile flight), so the host re-checks each tag: a living damageable
/// victim, inside the augment's reach, with ammo still in the bag.
/// </summary>
public partial class PlayerCombat
{
	const float DeadshotRangeSlack = 1.25f;

	/// <summary>Owner: fire the volley at the given hit objects (Guids) and their local tag offsets (x,y,z triples).</summary>
	public void OwnerRequestDeadshotVolley( Guid[] hitObjects, float[] localOffsets )
	{
		if ( !IsLocalCombatDriver() || hitObjects is null || localOffsets is null )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			ServerFireDeadshotVolley( hitObjects, localOffsets );
		else
			RpcHostDeadshotVolley( hitObjects, localOffsets );
	}

	[Rpc.Host]
	void RpcHostDeadshotVolley( Guid[] hitObjects, float[] localOffsets )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		ServerFireDeadshotVolley( hitObjects, localOffsets );
	}

	void ServerFireDeadshotVolley( Guid[] hitObjects, float[] localOffsets )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( IsCombatActionLocked || hitObjects is null || localOffsets is null )
			return;

		var augments = Components.Get<PlayerAugments>();
		if ( augments is null || !augments.TryGetActiveDefinition( AugmentAbility.Deadshot, out var def ) )
			return;

		var weaponId = ResolveEquippedMainHandId();
		if ( !EquipmentCatalog.HasAction( weaponId, EquippedItemActions.PrimaryRanged ) || IsActiveMainHandBroken() )
			return;

		if ( !AmmoCatalog.TryGetWeaponAmmoType( weaponId, out var ammoType ) )
			return;

		var maxShots = def.EffectScale >= 1f ? Math.Clamp( (int)MathF.Round( def.EffectScale ), 1, 8 ) : 5;
		var count = Math.Min( Math.Min( hitObjects.Length, localOffsets.Length / 3 ), maxShots );
		if ( count <= 0 )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var inventory = Components.Get<PlayerInventory>();
		var hotbar = Components.Get<PlayerHotbar>();
		var preferred = Components.Get<PlayerAmmoPreference>()?.PreferredAmmoResourceId ?? string.Empty;

		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is not null && controller.IsValid() ? Math.Max( 24f, controller.BodyHeight ) : 72f;
		var unitsPerMeter = bodyHeight / 1.8f;
		var shaftLength = 0.95f * unitsPerMeter;
		var muzzle = WorldPosition + Vector3.Up * ( bodyHeight * 0.55f );

		var rangeMeters = def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : 40f;
		var maxRange = TerrainWorldUnits.MetersToEngine( rangeMeters ) * DeadshotRangeSlack;
		var weaponDamage = Math.Max( 0f, AmmoCatalog.GetWeaponDamage( weaponId ) );

		for ( var i = 0; i < count; i++ )
		{
			var hit = scene.Directory.FindByGuid( hitObjects[i] );
			if ( hit is null || !hit.IsValid() )
				continue;

			if ( !CombatAuthority.TryFindDamageable( hit, out var receiver ) || receiver is not DamageReceiver dmg )
				continue;

			if ( !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( GameObject, dmg ) || !CombatAuthority.IsDamageVictimAlive( dmg ) )
				continue;

			var local = new Vector3( localOffsets[i * 3], localOffsets[i * 3 + 1], localOffsets[i * 3 + 2] );
			var world = hit.WorldTransform.PointToWorld( local );
			if ( Vector3.DistanceBetween( muzzle, world ) > maxRange )
				continue;

			// Out of arrows: the volley stops here, whatever was tagged.
			if ( !AmmoCatalog.HostTryConsumeOneAmmo( inventory, hotbar, ammoType, preferred, out var ammoId ) )
				break;

			var damage = AmmoCatalog.GetAmmoDamage( ammoId ) + weaponDamage;
			dmg.TakeDamage( damage, this );

			var dir = ( world - muzzle );
			var rotation = dir.LengthSquared > 1e-6f ? Rotation.LookAt( dir.Normal, Vector3.Up ) : WorldRotation;
			var stillAlive = CombatAuthority.IsDamageVictimAlive( dmg );
			ArrowProjectile.HostSpawnStuckPickup( scene, world, rotation, ammoId, shaftLength,
				stillAlive ? hit : null, stillAlive ? dmg : null );

			HostAddWearToActiveMainHand();
		}
	}
}
