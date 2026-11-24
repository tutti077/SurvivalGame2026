using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Bow / hold-to-charge ranged fire. Weapon + ammo must share <c>ammoType</c> (e.g. <c>bow</c>).
/// Host owns projectile spawn, ammo consume, and hit damage — clients only send fire intent.
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Bow" ), Title( "Min hold to fire (s)" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float BowMinHoldToFireSeconds { get; set; } = 0.2f;

	[Property, Group( "Combat — Bow" ), Title( "Full draw time (s)" ), Range( 0.3f, 3f ), Step( 0.05f )]
	public float BowFullDrawSeconds { get; set; } = 1.0f;

	[Property, Group( "Combat — Bow" ), Title( "Level-shot range at full draw (m)" ), Description( "Horizontal distance before ground contact when aimed level from hand height. Designer meters → pawn units via BodyHeight/1.8." ), Range( 2f, 40f ), Step( 0.5f )]
	public float BowFullDrawLevelRangeMeters { get; set; } = 30f;

	/// <summary>Extra launch velocity after the range→speed solve (1 = ballistic solve only).</summary>
	[Property, Group( "Combat — Bow" ), Title( "Launch speed multiplier" ), Range( 0.5f, 4f ), Step( 0.1f )]
	public float BowLaunchSpeedMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Bow" ), Title( "Min draw power (0-1)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float BowMinDrawPower { get; set; } = 0.35f;

	[Property, Group( "Combat — Bow" ), Title( "Max aim cone radius (px)" ), Description( "Draw-ring outer size through the whole power phase; shrinks to the classic crosshair over the accuracy phase." ), Range( 16f, 120f ), Step( 1f )]
	public float BowMaxAimConePixels { get; set; } = 56f;

	/// <summary>
	/// The draw is two phases (Mark 2026-11-18): this share of <see cref="BowFullDrawSeconds"/> is POWER (launch speed and
	/// bow damage go min → max, the cone stays widest), the rest is ACCURACY (power stays full, the cone narrows).
	/// </summary>
	[Property, Group( "Combat — Bow" ), Title( "Power phase share of the draw (0-1)" ), Description( "First part of the full draw time: range and damage charge up, the aim cone stays widest. The rest of the draw narrows the cone at full power." ), Range( 0.1f, 0.9f ), Step( 0.05f )]
	public float BowPowerPhaseFraction { get; set; } = 0.5f;

	/// <summary>
	/// Multiplies current third-person camera distance while holding Attack2 with a bow.
	/// FOV alone is overwritten by PlayerController — ADS is a real camera pull-in.
	/// </summary>
	[Property, Group( "Combat — Bow" ), Title( "ADS camera distance scale" ), Range( 0.25f, 1f ), Step( 0.01f )]
	public float BowAdsCameraDistanceScale { get; set; } = 0.55f;

	[Property, Group( "Combat — Bow" ), Title( "Min bow damage contribution" ), Description( "Weapon damage added at min fireable charge (ammo damage is always full)." ), Range( 0f, 50f ), Step( 1f )]
	public float BowMinWeaponDamageContribution { get; set; } = 1f;

	bool _bowCharging;
	double _bowChargeStartedAt;
	bool _bowAdsActive;

	/// <summary>Owner → every peer: the draw (0 = not drawing, quantised) so all peers pose the archer stance.</summary>
	[Sync] public float BowDrawSynced { get; private set; }

	/// <summary>Owner → every peer: +1 per arrow loosed (not per cancel) — the release follow-through.</summary>
	[Sync] public int BowShotCounter { get; private set; }

	/// <summary>Draw 0..1 as this peer should present it: the live charge on the driver, the synced value elsewhere.</summary>
	public float PresentationBowDraw01 => IsLocalCombatDriver()
		? (_bowCharging ? MathF.Max( 0.001f, BowDrawCharge01 ) : 0f)
		: BowDrawSynced;

	const double ArrowHitConfirmSeconds = 0.3;
	double _arrowHitConfirmAt = -1d;

	/// <summary>Local shooter: one of this pawn's arrows hit something that takes damage (crosshair hit tick).</summary>
	internal void NotifyOwnArrowHit() => _arrowHitConfirmAt = Time.NowDouble;

	/// <summary>1 right after an own arrow hit, falling to 0 over <see cref="ArrowHitConfirmSeconds"/>.</summary>
	public float ArrowHitConfirm01 => _arrowHitConfirmAt < 0d
		? 0f
		: Math.Clamp( 1f - (float)((Time.NowDouble - _arrowHitConfirmAt) / ArrowHitConfirmSeconds), 0f, 1f );

	void PublishBowDraw()
	{
		// Twentieths are plenty for a pose; a steady value writes nothing.
		var draw = _bowCharging ? MathF.Max( 0.05f, MathF.Round( BowDrawCharge01 * 20f ) / 20f ) : 0f;
		if ( MathF.Abs( draw - BowDrawSynced ) > 1e-3f )
			BowDrawSynced = draw;
	}

	/// <summary>0 = just started charging, 1 = full draw. 0 when not charging.</summary>
	public float BowDrawCharge01
	{
		get
		{
			if ( !_bowCharging )
				return 0f;

			var held = (float)( Time.NowDouble - _bowChargeStartedAt );
			var full = Math.Max( 0.05f, BowFullDrawSeconds );
			return Math.Clamp( held / full, 0f, 1f );
		}
	}

	public bool IsBowCharging => _bowCharging;

	/// <summary>Aiming an arrow: drawing it (Attack1 held) or ADS (Attack2 held). Movement takes <see cref="PlayerMovement.BowAimSpeedPenalty"/> instead of the plain weapon-out penalty.</summary>
	public bool IsBowAiming => _bowCharging || _bowAdsActive;

	/// <summary>True while bow ADS (Attack2 held). Camera pull-in is applied in <see cref="PlayerMovement"/> PostCameraSetup.</summary>
	public bool IsBowAdsActive => _bowAdsActive;

	/// <summary>Multiplies the current third-person zoom distance while ADS (1 = no change).</summary>
	public float GetBowAdsCameraDistanceMultiplier()
	{
		if ( !_bowAdsActive )
			return 1f;

		return Math.Clamp( BowAdsCameraDistanceScale, 0.25f, 1f );
	}

	/// <summary>Where the power phase ends on the 0..1 draw. A scene saved before the property existed loads it as 0.</summary>
	float BowPowerPhaseSplit01 => BowPowerPhaseFraction > 0f ? Math.Clamp( BowPowerPhaseFraction, 0.1f, 0.9f ) : 0.5f;

	/// <summary>
	/// POWER of a shot at <paramref name="draw01"/> of the full draw: 0 → 1 across the power phase, 1 from there on.
	/// The one mapping the launch speed, the bow damage and the crosshair's yellow charge all read
	/// (owner prediction, host, predicted client arrow, HUD).
	/// </summary>
	public float BowPowerFraction( float draw01 ) =>
		Math.Clamp( Math.Clamp( draw01, 0f, 1f ) / BowPowerPhaseSplit01, 0f, 1f );

	/// <summary>
	/// ACCURACY of a shot at <paramref name="draw01"/> of the full draw: 0 (widest cone) through the power phase, then
	/// 0 → 1 across the rest. The one mapping the shot spread and the crosshair's draw ring both read.
	/// </summary>
	public float BowAccuracyFraction( float draw01 )
	{
		var split = BowPowerPhaseSplit01;
		return Math.Clamp( (Math.Clamp( draw01, 0f, 1f ) - split) / (1f - split), 0f, 1f );
	}

	/// <summary>Aim cone radius on screen (px) at <paramref name="draw01"/>: the draw ring's size and the spread's outer bound.</summary>
	float BowAimConeRadiusPixels( float draw01 )
	{
		const float minRadius = 7f;
		var maxRadius = Math.Max( minRadius + 1f, BowMaxAimConePixels );
		return MathX.Lerp( maxRadius, minRadius, BowAccuracyFraction( draw01 ) );
	}

	/// <summary>Screen-space draw ring radius for the crosshair (pixels).</summary>
	public float GetBowDrawRingRadiusPixels() => _bowCharging ? BowAimConeRadiusPixels( BowDrawCharge01 ) : 0f;

	void TickOwnerRangedInput()
	{
		TickOwnerRangedInputCore();
		PublishBowDraw();
	}

	void TickOwnerRangedInputCore()
	{
		var equipped = Components.Get<PlayerEquippedItem>();
		var hasBow = equipped is not null && equipped.HasAction( EquippedItemActions.PrimaryRanged );

		TickBowAdsState( hasBow );

		if ( !hasBow )
		{
			CancelBowCharge();
			return;
		}

		if ( IsCombatActionLocked )
		{
			CancelBowCharge();
			return;
		}

		// Right-click press or release while drawing cancels the shot (ADS toggle mid-draw = cancel).
		if ( _bowCharging && ( Input.Pressed( BlockAction ) || Input.Released( BlockAction ) ) )
		{
			CancelBowCharge();
			return;
		}

		if ( Input.Pressed( PrimaryAttackAction ) && !_bowCharging )
		{
			// Deadshot with tags placed: the press releases the volley instead of drawing.
			if ( Components.Get<PlayerAugments>() is { } augments && augments.TryOwnerFireDeadshot() )
				return;

			if ( !OwnerHasAmmoForEquippedBow() || IsActiveMainHandBroken() )
				return;

			_bowCharging = true;
			_bowChargeStartedAt = Time.NowDouble;
		}

		if ( !_bowCharging )
			return;

		if ( !Input.Down( PrimaryAttackAction ) )
		{
			var held = (float)( Time.NowDouble - _bowChargeStartedAt );
			_bowCharging = false;

			if ( held >= Math.Max( 0.05f, BowMinHoldToFireSeconds ) && OwnerHasAmmoForEquippedBow() )
			{
				OwnerRequestFireBow( held );
				BowShotCounter++;
			}
		}
	}

	void CancelBowCharge()
	{
		_bowCharging = false;
	}

	void TickBowAdsState( bool hasBow )
	{
		_bowAdsActive = hasBow && Input.Down( BlockAction ) && !IsCombatActionLocked;
	}

	bool OwnerHasAmmoForEquippedBow()
	{
		var weaponId = ResolveEquippedMainHandId();
		if ( !AmmoCatalog.TryGetWeaponAmmoType( weaponId, out var ammoType ) )
			return false;

		var inventory = Components.Get<PlayerInventory>();
		var hotbar = Components.Get<PlayerHotbar>();
		return AmmoCatalog.HasAnyAmmoForType( inventory, hotbar, ammoType );
	}

	string ResolveEquippedMainHandId()
	{
		var equipment = Components.Get<PlayerEquipment>();
		if ( equipment is not null )
		{
			var id = equipment.GetSlotResourceId( EquipmentSlot.MainHand );
			if ( !string.IsNullOrWhiteSpace( id ) )
				return ResourceCatalog.NormalizeResourceId( id );
		}

		var equipped = Components.Get<PlayerEquippedItem>();
		return equipped?.EquippedResourceId ?? string.Empty;
	}

	void OwnerRequestFireBow( float holdSeconds )
	{
		var charge01 = Math.Clamp( holdSeconds / Math.Max( 0.05f, BowFullDrawSeconds ), 0f, 1f );
		ResolveBowShot( out var origin, out var direction );
		// The spread is rolled here, on the machine whose crosshair shows the cone: host and shooter then fly the
		// same arrow (the host has no view of a remote owner's screen to size the cone with).
		direction = ApplyBowInaccuracy( direction, charge01, (uint)( Time.NowDouble * 1000.0 ) ^ (uint)GameObject.Id.GetHashCode() );

		var preferred = Components.Get<PlayerAmmoPreference>()?.PreferredAmmoResourceId ?? string.Empty;
		var intent = new BowFireIntent
		{
			Charge01 = charge01,
			OriginX = origin.x,
			OriginY = origin.y,
			OriginZ = origin.z,
			DirX = direction.x,
			DirY = direction.y,
			DirZ = direction.z,
			PreferredAmmoResourceId = preferred ?? string.Empty
		};

		if ( GameObject.Network is not { Active: true } )
		{
			ServerTryFireBow( intent );
			return;
		}

		if ( Networking.IsHost )
		{
			ServerTryFireBow( intent );
			return;
		}

		RpcHostFireBow( intent );

		// Client: fly the arrow now instead of waiting a round trip for the host's (Mark 2026-11-17: "arrows are barely
		// working for him, can you make his game predict them?"). Same launch as the host computes; the host's arrow
		// still decides the hit and stays hidden on this machine (ArrowProjectile).
		if ( IsCombatActionLocked || IsHandsOffCombat )
			return;

		var ammoId = string.IsNullOrWhiteSpace( preferred ) ? "arrow_wood" : preferred;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		ArrowProjectile.ClientSpawnPredicted( scene, origin, ComputeBowLaunchVelocity( direction, charge01 ), GameObject, ammoId );
	}

	/// <summary>Launch velocity of a shot at <paramref name="charge01"/> draw along <paramref name="aimDir"/> (host and predicting shooter).</summary>
	Vector3 ComputeBowLaunchVelocity( Vector3 aimDir, float charge01 )
	{
		var power = MathX.Lerp( Math.Clamp( BowMinDrawPower, 0.05f, 1f ), 1f, BowPowerFraction( charge01 ) );
		return aimDir * (ComputeFullDrawSpeed() * power * Math.Max( 0.1f, BowLaunchSpeedMultiplier ));
	}

	/// <summary>Farthest the camera ray looks for the point under the crosshair (m).</summary>
	const float BowAimTraceMeters = 120f;

	/// <summary>
	/// The arrow leaves the BOW (the nock the archer stance holds, <see cref="PlayerAnimation.TryGetBowNockWorld"/>)
	/// and flies toward whatever is under the crosshair — not from the camera, where it shot straight away from the
	/// viewer and its arc could not be read (Mark 2026-11-06). Nothing under the crosshair: the camera ray's far point.
	/// </summary>
	void ResolveBowShot( out Vector3 origin, out Vector3 direction )
	{
		BuildViewCamera.TryGetViewRay( GameObject, out var camOrigin, out var camDir );
		if ( camDir.LengthSquared < 1e-8f )
			camDir = WorldRotation.Forward;
		camDir = camDir.Normal;

		origin = Components.Get<PlayerAnimation>() is { } anim && anim.TryGetBowNockWorld( out var nock )
			? nock
			: ResolveBowHandEstimate( camDir );

		var unitsPerMeter = ResolveBowUnitsPerMeter();
		var far = camOrigin + camDir * (BowAimTraceMeters * unitsPerMeter);
		var tr = Scene.Trace.Ray( camOrigin, far )
			.IgnoreGameObjectHierarchy( GameObject )
			.WithoutTags( "trigger", "worlddrop" )
			.Run();
		var aimPoint = tr.Hit ? tr.HitPosition : far;

		// A point under the crosshair that sits behind or right at the bow (wall hugging) would turn the shot round.
		direction = aimPoint - origin;
		if ( Vector3.Dot( direction, camDir ) < unitsPerMeter )
			direction = camDir;
		direction = direction.Normal;
	}

	float ResolveBowUnitsPerMeter()
	{
		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is not null && controller.IsValid() ? Math.Max( 24f, controller.BodyHeight ) : 72f;
		return bodyHeight / 1.8f;
	}

	/// <summary>Bow hand when no stance pose is available (no body renderer): chest height, a step toward the aim.</summary>
	Vector3 ResolveBowHandEstimate( Vector3 aimDir )
	{
		var bodyHeight = ResolveBowUnitsPerMeter() * 1.8f;
		var flat = aimDir.WithZ( 0f );
		if ( flat.LengthSquared < 1e-6f )
			flat = WorldRotation.Forward.WithZ( 0f );
		return WorldPosition + Vector3.Up * (bodyHeight * 0.8f) + flat.Normal * (bodyHeight * 0.3f);
	}

	/// <summary>Bola throw origin: the camera ray origin when it is clear of the pawn, else the hand.</summary>
	Vector3 ResolveBowMuzzleOrigin( Vector3 cameraOrigin, Vector3 viewDir )
	{
		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is not null && controller.IsValid()
			? Math.Max( 24f, controller.BodyHeight )
			: 72f;

		// Approx hand height (~55% body) in front of the torso.
		var hand = WorldPosition + Vector3.Up * ( bodyHeight * 0.55f );
		var forward = viewDir.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared > 1e-6f )
			hand += forward.Normal * ( bodyHeight * 0.2f );

		// Prefer camera ray origin pushed past the pawn when third-person; else hand.
		if ( ( cameraOrigin - WorldPosition ).Length > bodyHeight * 0.75f )
			return cameraOrigin;

		return hand;
	}

	[Rpc.Host]
	void RpcHostFireBow( BowFireIntent intent )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		ServerTryFireBow( intent );
	}

	void ServerTryFireBow( in BowFireIntent intent )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( IsCombatActionLocked )
			return;

		if ( IsHandsOffCombat )
			return;

		var weaponId = ResolveEquippedMainHandId();
		if ( !EquipmentCatalog.HasAction( weaponId, EquippedItemActions.PrimaryRanged ) )
			return;

		if ( IsActiveMainHandBroken() )
			return;

		if ( !AmmoCatalog.TryGetWeaponAmmoType( weaponId, out var ammoType ) )
			return;

		var inventory = Components.Get<PlayerInventory>();
		var hotbar = Components.Get<PlayerHotbar>();
		if ( !AmmoCatalog.HostTryConsumeOneAmmo( inventory, hotbar, ammoType, intent.PreferredAmmoResourceId, out var ammoId ) )
			return;

		var charge01 = Math.Clamp( intent.Charge01, 0f, 1f );

		var ammoDamage = AmmoCatalog.GetAmmoDamage( ammoId );
		var weaponDamage = EquipmentUpgrade.ScaleStat( AmmoCatalog.GetWeaponDamage( weaponId ), GetActiveWeaponTier() );
		var weaponContribution = MathX.Lerp(
			Math.Clamp( BowMinWeaponDamageContribution, 0f, Math.Max( 0f, weaponDamage ) ),
			Math.Max( 0f, weaponDamage ),
			BowPowerFraction( charge01 ) );
		var damage = ammoDamage + weaponContribution;

		var aimDir = new Vector3( intent.DirX, intent.DirY, intent.DirZ );
		if ( aimDir.LengthSquared < 1e-8f )
			aimDir = WorldRotation.Forward;
		aimDir = aimDir.Normal;

		var origin = new Vector3( intent.OriginX, intent.OriginY, intent.OriginZ );
		// The shot leaves the archer's own bow: an origin further than a body height from the chest is not one.
		var bodyHeight = ResolveBowUnitsPerMeter() * 1.8f;
		if ( (origin - (WorldPosition + Vector3.Up * (bodyHeight * 0.6f))).Length > bodyHeight )
			origin = ResolveBowHandEstimate( aimDir );

		var velocity = ComputeBowLaunchVelocity( aimDir, charge01 );

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var arrow = ArrowProjectile.HostSpawn( scene, origin, velocity, damage, GameObject, this, ammoId );
		// A remote owner let go one trip ago: the host's arrow makes up that flight time first.
		if ( arrow.IsValid() && HostOwnerLatencySeconds() is var late and > 0f )
			arrow.Components.Get<ArrowProjectile>()?.HostCatchUp( late );

		// Bow durability: 1 tick per shot fired, hit or miss.
		HostAddWearToActiveMainHand();
	}

	Vector3 ApplyBowInaccuracy( Vector3 aimDir, float charge01, uint seed )
	{
		// Full draw ≈ perfect center; through the power phase and early in the accuracy phase = a random point inside the draw cone.
		var spread01 = 1f - BowAccuracyFraction( charge01 );
		if ( spread01 <= 0.01f )
			return aimDir;

		var cam = BuildViewCamera.Resolve( GameObject );
		var fov = cam.IsValid() ? Math.Clamp( cam.FieldOfView, 20f, 110f ) : 80f;
		var screenH = cam.IsValid() ? Math.Max( 1f, cam.ScreenRect.Height ) : 1080f;

		var ringPx = BowAimConeRadiusPixels( charge01 );
		// Random radius inside circle (sqrt for uniform disk).
		var rng = new Random( (int)seed );
		var u = (float)rng.NextDouble();
		var v = (float)rng.NextDouble();
		var r = MathF.Sqrt( u ) * ringPx * spread01;
		var theta = v * MathF.PI * 2f;
		var offsetPxX = MathF.Cos( theta ) * r;
		var offsetPxY = MathF.Sin( theta ) * r;

		var degPerPx = fov / screenH;
		var yaw = offsetPxX * degPerPx;
		var pitch = offsetPxY * degPerPx;

		var basis = Rotation.LookAt( aimDir, Vector3.Up );
		return ( basis * new Angles( pitch, yaw, 0f ).ToRotation() ).Forward.Normal;
	}

	float ComputeFullDrawSpeed()
	{
		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is not null && controller.IsValid()
			? Math.Max( 24f, controller.BodyHeight )
			: 72f;
		var unitsPerMeter = bodyHeight / 1.8f;
		var range = Math.Max( 1f, BowFullDrawLevelRangeMeters ) * unitsPerMeter;
		var handHeight = bodyHeight * 0.55f;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var gravity = scene?.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		var g = Math.Abs( gravity.z );
		if ( g < 1f )
			g = 800f;

		// Level shot: R = v * sqrt(2h/g) → v = R / sqrt(2h/g)
		var fallTime = MathF.Sqrt( 2f * Math.Max( 8f, handHeight ) / g );
		return Math.Max( 80f, range / Math.Max( 0.05f, fallTime ) );
	}
}

public struct BowFireIntent
{
	public float Charge01;
	public float OriginX;
	public float OriginY;
	public float OriginZ;
	public float DirX;
	public float DirY;
	public float DirZ;
	public string PreferredAmmoResourceId;
}
