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
	public float BowFullDrawLevelRangeMeters { get; set; } = 18.3f;

	/// <summary>Extra launch velocity after the range→speed solve (1 = ballistic solve only).</summary>
	[Property, Group( "Combat — Bow" ), Title( "Launch speed multiplier" ), Range( 0.5f, 4f ), Step( 0.1f )]
	public float BowLaunchSpeedMultiplier { get; set; } = 1f;

	[Property, Group( "Combat — Bow" ), Title( "Min draw power (0-1)" ), Range( 0.1f, 1f ), Step( 0.05f )]
	public float BowMinDrawPower { get; set; } = 0.35f;

	[Property, Group( "Combat — Bow" ), Title( "Max aim cone radius (px)" ), Description( "Draw-ring outer size at min charge; shrinks to the classic crosshair at full draw." ), Range( 16f, 120f ), Step( 1f )]
	public float BowMaxAimConePixels { get; set; } = 56f;

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

	/// <summary>True while bow ADS (Attack2 held). Camera pull-in is applied in <see cref="PlayerMovement"/> PostCameraSetup.</summary>
	public bool IsBowAdsActive => _bowAdsActive;

	/// <summary>Multiplies the current third-person zoom distance while ADS (1 = no change).</summary>
	public float GetBowAdsCameraDistanceMultiplier()
	{
		if ( !_bowAdsActive )
			return 1f;

		return Math.Clamp( BowAdsCameraDistanceScale, 0.25f, 1f );
	}

	/// <summary>Screen-space draw ring radius for the crosshair (pixels).</summary>
	public float GetBowDrawRingRadiusPixels()
	{
		if ( !_bowCharging )
			return 0f;

		const float minRadius = 7f;
		var maxRadius = Math.Max( minRadius + 1f, BowMaxAimConePixels );
		return MathX.Lerp( maxRadius, minRadius, BowDrawCharge01 );
	}

	void TickOwnerRangedInput()
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
			if ( !OwnerHasAmmoForEquippedBow() )
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
				OwnerRequestFireBow( held );
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
		BuildViewCamera.TryGetViewRay( GameObject, out var origin, out var direction );
		if ( direction.LengthSquared < 1e-8f )
			direction = WorldRotation.Forward;

		origin = ResolveBowMuzzleOrigin( origin, direction );

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
			PreferredAmmoResourceId = preferred ?? string.Empty,
			ClientSeed = (uint)( Time.NowDouble * 1000.0 ) ^ (uint)GameObject.Id.GetHashCode()
		};

		if ( GameObject.Network is not { Active: true } )
		{
			ServerTryFireBow( intent );
			return;
		}

		if ( Networking.IsHost )
			ServerTryFireBow( intent );
		else
			RpcHostFireBow( intent );
	}

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

		var weaponId = ResolveEquippedMainHandId();
		if ( !EquipmentCatalog.HasAction( weaponId, EquippedItemActions.PrimaryRanged ) )
			return;

		if ( !AmmoCatalog.TryGetWeaponAmmoType( weaponId, out var ammoType ) )
			return;

		var inventory = Components.Get<PlayerInventory>();
		var hotbar = Components.Get<PlayerHotbar>();
		if ( !AmmoCatalog.HostTryConsumeOneAmmo( inventory, hotbar, ammoType, intent.PreferredAmmoResourceId, out var ammoId ) )
			return;

		var charge01 = Math.Clamp( intent.Charge01, 0f, 1f );
		var power = MathX.Lerp( Math.Clamp( BowMinDrawPower, 0.05f, 1f ), 1f, charge01 );

		var ammoDamage = AmmoCatalog.GetAmmoDamage( ammoId );
		var weaponDamage = AmmoCatalog.GetWeaponDamage( weaponId );
		var weaponContribution = MathX.Lerp(
			Math.Clamp( BowMinWeaponDamageContribution, 0f, Math.Max( 0f, weaponDamage ) ),
			Math.Max( 0f, weaponDamage ),
			charge01 );
		var damage = ammoDamage + weaponContribution;

		var aimDir = new Vector3( intent.DirX, intent.DirY, intent.DirZ );
		if ( aimDir.LengthSquared < 1e-8f )
			aimDir = WorldRotation.Forward;
		aimDir = aimDir.Normal;

		var origin = new Vector3( intent.OriginX, intent.OriginY, intent.OriginZ );
		aimDir = ApplyBowInaccuracy( aimDir, charge01, intent.ClientSeed );

		var speed = ComputeFullDrawSpeed() * power * Math.Max( 0.1f, BowLaunchSpeedMultiplier );
		var velocity = aimDir * speed;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		ArrowProjectile.HostSpawn( scene, origin, velocity, damage, GameObject, this, ammoId );
	}

	Vector3 ApplyBowInaccuracy( Vector3 aimDir, float charge01, uint seed )
	{
		// Full draw ≈ perfect center; low charge = random point inside the draw cone.
		var spread01 = 1f - Math.Clamp( charge01, 0f, 1f );
		if ( spread01 <= 0.01f )
			return aimDir;

		var cam = BuildViewCamera.Resolve( GameObject );
		var fov = cam.IsValid() ? Math.Clamp( cam.FieldOfView, 20f, 110f ) : 80f;
		var screenH = cam.IsValid() ? Math.Max( 1f, cam.ScreenRect.Height ) : 1080f;

		var ringPx = MathX.Lerp( Math.Max( 8f, BowMaxAimConePixels ), 7f, charge01 );
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
	public uint ClientSeed;
}
