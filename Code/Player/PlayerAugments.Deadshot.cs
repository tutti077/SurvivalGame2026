using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Deadshot (wheel toggle, needs a ranged weapon in hand). While on, looking at an enemy plants a
/// red tag where the crosshair touched it — once per enemy, up to EffectScale tags. The tag rides
/// the hit object, so it follows a moving target. The volley fires when the last tag lands or the
/// player presses fire with any tags placed: every tag is hit exactly, no travel, no miss
/// (<see cref="PlayerCombat.OwnerRequestDeadshotVolley"/>). Deadshot then switches off and cools down.
/// Tags are owner-local; the host only receives them at fire time and re-checks each one.
/// </summary>
public sealed partial class PlayerAugments
{
	const string DeadshotDotModel = "models/dev/sphere.vmdl";
	const float DeadshotDotScale = 0.12f;
	const float DeadshotDefaultRangeMeters = 40f;
	const int DeadshotDefaultMaxTags = 5;

	static readonly Color DeadshotDotColor = new( 1f, 0.12f, 0.08f );

	readonly List<DeadshotTag> _deadshotTags = new();

	sealed class DeadshotTag
	{
		public GameObject Victim;
		public GameObject HitObject;
		public GameObject Marker;
		public Vector3 LocalOffset;
	}

	public int DeadshotTagCount => _deadshotTags.Count;

	public int DeadshotMaxTags =>
		TryGetActiveDefinition( AugmentAbility.Deadshot, out var def ) && def.EffectScale >= 1f
			? Math.Clamp( (int)MathF.Round( def.EffectScale ), 1, 8 )
			: DeadshotDefaultMaxTags;

	/// <summary>Abilities with an equipment prerequisite refuse to switch on without it.</summary>
	bool CanActivateAbility( AugmentDefinition def ) => def?.ResolvedAbility switch
	{
		AugmentAbility.Deadshot => HasRangedWeaponEquipped(),
		_ => true,
	};

	bool HasRangedWeaponEquipped() =>
		Components.Get<PlayerEquippedItem>() is { } equipped && equipped.HasAction( EquippedItemActions.PrimaryRanged );

	/// <summary>Owner, every frame: keep Deadshot honest (weapon still out), prune dead tags, plant a tag under the crosshair.</summary>
	void TickDeadshot()
	{
		if ( !IsAbilityOn( AugmentAbility.Deadshot ) )
		{
			if ( _deadshotTags.Count > 0 )
				ClearDeadshotTags();
			return;
		}

		if ( !TryGetActiveDefinition( AugmentAbility.Deadshot, out var def ) )
			return;

		// Holstered the bow: the eye shuts off and the tags go with it.
		if ( !HasRangedWeaponEquipped() )
		{
			SwitchToggleOff( def );
			ClearDeadshotTags();
			return;
		}

		PruneDeadshotTags();

		if ( _deadshotTags.Count >= DeadshotMaxTags )
			return;

		if ( !BuildViewCamera.TryGetViewRay( GameObject, out var origin, out var direction ) || direction.LengthSquared < 1e-8f )
			return;

		var rangeMeters = def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : DeadshotDefaultRangeMeters;
		var range = TerrainWorldUnits.MetersToEngine( rangeMeters );
		var tr = Scene.Trace.Ray( origin, origin + direction.Normal * range )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( "buildpreview" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var receiver ) || receiver is not DamageReceiver dmg )
			return;

		if ( !IsDeadshotVictim( dmg, out var victim ) || !CombatAuthority.IsDamageVictimAlive( dmg ) )
			return;

		for ( var i = 0; i < _deadshotTags.Count; i++ )
		{
			if ( _deadshotTags[i].Victim == victim )
				return;
		}

		PlaceDeadshotTag( victim, tr.GameObject, tr.HitPosition );

		if ( _deadshotTags.Count >= DeadshotMaxTags )
			FireDeadshotVolley();
	}

	/// <summary>Living things only — enemies, animals and other players. Never yourself, never trees or build pieces.</summary>
	bool IsDeadshotVictim( DamageReceiver dmg, out GameObject victim )
	{
		victim = null;
		for ( var p = dmg.GameObject; p.IsValid(); p = p.Parent )
		{
			if ( p.Components.Get<EntityVitals>() is not null )
			{
				victim = p;
				return true;
			}

			if ( p.Components.Get<PlayerVitals>() is not null )
			{
				if ( p == GameObject || p == GameObject.Root )
					return false;

				victim = p;
				return true;
			}
		}

		return false;
	}

	void PlaceDeadshotTag( GameObject victim, GameObject hitObject, Vector3 worldPoint )
	{
		var marker = new GameObject( true, "deadshot_tag" );
		marker.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		marker.NetworkMode = NetworkMode.Never;
		marker.WorldPosition = worldPoint;
		marker.WorldScale = Vector3.One * DeadshotDotScale;

		var renderer = marker.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( DeadshotDotModel );
		renderer.Tint = DeadshotDotColor;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		// Ride the part that was hit so the dot stays on a moving target.
		marker.SetParent( hitObject, true );

		_deadshotTags.Add( new DeadshotTag
		{
			Victim = victim,
			HitObject = hitObject,
			Marker = marker,
			LocalOffset = hitObject.WorldTransform.PointToLocal( worldPoint ),
		} );
	}

	void PruneDeadshotTags()
	{
		for ( var i = _deadshotTags.Count - 1; i >= 0; i-- )
		{
			var tag = _deadshotTags[i];
			var alive = tag.Victim.IsValid() && tag.HitObject.IsValid()
			            && CombatAuthority.TryFindDamageable( tag.HitObject, out var recv )
			            && recv is DamageReceiver dmg && CombatAuthority.IsDamageVictimAlive( dmg );
			if ( alive )
				continue;

			if ( tag.Marker.IsValid() )
				tag.Marker.Destroy();
			_deadshotTags.RemoveAt( i );
		}
	}

	/// <summary>Combat calls this on a fire press: with tags placed the volley replaces the normal shot.</summary>
	public bool TryOwnerFireDeadshot()
	{
		if ( !IsLocalManagingClient() || !IsAbilityOn( AugmentAbility.Deadshot ) )
			return false;

		PruneDeadshotTags();
		if ( _deadshotTags.Count == 0 )
			return false;

		FireDeadshotVolley();
		return true;
	}

	void FireDeadshotVolley()
	{
		var combat = Components.Get<PlayerCombat>();
		if ( combat is null || _deadshotTags.Count == 0 )
		{
			ClearDeadshotTags();
			return;
		}

		var hitObjects = new Guid[_deadshotTags.Count];
		var offsets = new float[_deadshotTags.Count * 3];
		for ( var i = 0; i < _deadshotTags.Count; i++ )
		{
			var tag = _deadshotTags[i];
			hitObjects[i] = tag.HitObject.IsValid() ? tag.HitObject.Id : Guid.Empty;
			offsets[i * 3] = tag.LocalOffset.x;
			offsets[i * 3 + 1] = tag.LocalOffset.y;
			offsets[i * 3 + 2] = tag.LocalOffset.z;

			// Owner-side tracer so the flurry reads instantly, before the host's stuck arrows arrive.
			if ( tag.HitObject.IsValid() && BuildViewCamera.TryGetViewRay( GameObject, out var origin, out _ ) )
				DebugOverlay.Line( origin, tag.HitObject.WorldTransform.PointToWorld( tag.LocalOffset ), DeadshotDotColor, 0.35f );
		}

		combat.OwnerRequestDeadshotVolley( hitObjects, offsets );
		ClearDeadshotTags();

		if ( TryGetActiveDefinition( AugmentAbility.Deadshot, out var def ) )
			SwitchToggleOff( def );
	}

	void SwitchToggleOff( AugmentDefinition def )
	{
		var state = GetOrCreateState( def );
		if ( !state.On )
			return;

		state.On = false;
		state.CooldownUntil = Time.NowDouble + def.ResolvedCooldownSeconds;
	}

	void ClearDeadshotTags()
	{
		for ( var i = 0; i < _deadshotTags.Count; i++ )
		{
			if ( _deadshotTags[i].Marker.IsValid() )
				_deadshotTags[i].Marker.Destroy();
		}

		_deadshotTags.Clear();
	}
}
