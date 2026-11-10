using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Bola throw (Mark 2026-11-02, prompt 15). With a bola in the main hand, holding the block button
/// (right mouse) winds it up: the pawn is held to the block-walk speed and the stones spin faster and
/// faster in the hand (<c>PlayerAnimation.Bola.cs</c>). Release before <see cref="BolaMinChargeSeconds"/>
/// just stops the spin; a longer hold throws it on a forward-and-down arc whose range grows with the
/// hold up to <see cref="BolaFullChargeSeconds"/>. Host validates the item, takes one bola from the
/// slot and spawns the <see cref="BolaProjectile"/>, which roots small creatures at the feet.
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Bola" ), Title( "Min hold to throw (s)" ), Range( 0.2f, 3f ), Step( 0.05f )]
	public float BolaMinChargeSeconds { get; set; } = 1f;

	[Property, Group( "Combat — Bola" ), Title( "Full charge (s)" ), Range( 1f, 6f ), Step( 0.05f )]
	public float BolaFullChargeSeconds { get; set; } = 3f;

	[Property, Group( "Combat — Bola" ), Title( "Range at min charge (m)" ), Range( 2f, 30f ), Step( 0.5f )]
	public float BolaMinRangeMeters { get; set; } = 4f;

	[Property, Group( "Combat — Bola" ), Title( "Range at full charge (m)" ), Range( 4f, 60f ), Step( 0.5f )]
	public float BolaFullRangeMeters { get; set; } = 12f;

	[Property, Group( "Combat — Bola" ), Title( "Launch lift (°)" ), Description( "Pitch added above the aim so the arc comes down onto the legs." ), Range( 0f, 30f ), Step( 1f )]
	public float BolaLaunchLiftDegrees { get; set; } = 4f;

	[Property, Group( "Combat — Bola" ), Title( "Hold on the victim (s)" ), Range( 1f, 15f ), Step( 0.5f )]
	public float BolaRootSeconds { get; set; } = 5f;

	bool _bolaCharging;
	double _bolaChargeStartedAt;

	public bool IsBolaCharging => _bolaCharging;

	/// <summary>Seconds the wind-up has run (0 when not charging).</summary>
	public float BolaChargeSeconds => _bolaCharging ? (float)(Time.NowDouble - _bolaChargeStartedAt) : 0f;

	/// <summary>0 at the start of the wind-up, 1 at full charge.</summary>
	public float BolaCharge01 => _bolaCharging
		? Math.Clamp( BolaChargeSeconds / Math.Max( 0.2f, BolaFullChargeSeconds ), 0f, 1f )
		: 0f;

	/// <summary>True once the hold is long enough that a release will throw.</summary>
	public bool IsBolaReadyToThrow => _bolaCharging && BolaChargeSeconds >= Math.Max( 0.1f, BolaMinChargeSeconds );

	bool HasBolaEquipped() =>
		Components.Get<PlayerEquippedItem>() is { } equipped && equipped.HasAction( EquippedItemActions.Bola );

	void TickOwnerBolaInput()
	{
		if ( !HasBolaEquipped() || IsCombatActionLocked || IsActiveMainHandBroken() )
		{
			_bolaCharging = false;
			return;
		}

		if ( Input.Pressed( BlockAction ) && !_bolaCharging )
		{
			_bolaCharging = true;
			_bolaChargeStartedAt = Time.NowDouble;
		}

		if ( !_bolaCharging )
			return;

		if ( Input.Down( BlockAction ) )
			return;

		var held = BolaChargeSeconds;
		_bolaCharging = false;
		if ( held < Math.Max( 0.1f, BolaMinChargeSeconds ) )
			return; // let go too soon: the stones just stop spinning

		OwnerRequestThrowBola( held );
	}

	void OwnerRequestThrowBola( float holdSeconds )
	{
		BuildViewCamera.TryGetViewRay( GameObject, out var origin, out var direction );
		if ( direction.LengthSquared < 1e-8f )
			direction = WorldRotation.Forward;

		origin = ResolveBowMuzzleOrigin( origin, direction );
		var intent = new BolaThrowIntent
		{
			HoldSeconds = holdSeconds,
			OriginX = origin.x,
			OriginY = origin.y,
			OriginZ = origin.z,
			DirX = direction.x,
			DirY = direction.y,
			DirZ = direction.z,
		};

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
			ServerTryThrowBola( intent );
		else
			RpcHostThrowBola( intent );
	}

	[Rpc.Host]
	void RpcHostThrowBola( BolaThrowIntent intent )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		ServerTryThrowBola( intent );
	}

	void ServerTryThrowBola( in BolaThrowIntent intent )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( IsCombatActionLocked || Components.Get<PlayerMovement>() is { IsSeatedDriver: true } or { PodLocked: true } )
			return;

		var weaponId = ResolveEquippedMainHandId();
		if ( !EquipmentCatalog.HasAction( weaponId, EquippedItemActions.Bola ) )
			return;

		// The host re-checks the hold once; the charge curve is the same on both sides.
		var minHold = Math.Max( 0.1f, BolaMinChargeSeconds );
		if ( intent.HoldSeconds < minHold )
			return;

		var hotbar = Components.Get<PlayerHotbar>();
		if ( hotbar is null || !hotbar.HostTryTakeOne( hotbar.ActiveSlotIndex ) )
			return;

		var charge01 = Math.Clamp( (intent.HoldSeconds - minHold) / Math.Max( 0.1f, BolaFullChargeSeconds - minHold ), 0f, 1f );
		var rangeMeters = MathX.Lerp( BolaMinRangeMeters, Math.Max( BolaMinRangeMeters, BolaFullRangeMeters ), charge01 );

		var aim = new Vector3( intent.DirX, intent.DirY, intent.DirZ );
		aim = aim.LengthSquared < 1e-8f ? WorldRotation.Forward : aim.Normal;
		var lifted = (Rotation.FromAxis( Vector3.Cross( aim, Vector3.Up ).Normal, -BolaLaunchLiftDegrees ) * aim).Normal;

		var origin = new Vector3( intent.OriginX, intent.OriginY, intent.OriginZ );
		var speed = ComputeBolaSpeedForRange( rangeMeters );
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		BolaProjectile.HostSpawn( scene, origin, lifted * speed, GameObject, BolaRootSeconds );
	}

	/// <summary>Level-shot range → launch speed (same ballistic solve as the bow, hand height ≈ 55 % of the body).</summary>
	float ComputeBolaSpeedForRange( float rangeMeters )
	{
		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is not null && controller.IsValid() ? Math.Max( 24f, controller.BodyHeight ) : 72f;
		var unitsPerMeter = bodyHeight / 1.8f;
		var range = Math.Max( 1f, rangeMeters ) * unitsPerMeter;
		var handHeight = bodyHeight * 0.55f;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var gravity = scene?.PhysicsWorld?.Gravity ?? new Vector3( 0f, 0f, -800f );
		var g = Math.Abs( gravity.z );
		if ( g < 1f )
			g = 800f;

		var fallTime = MathF.Sqrt( 2f * Math.Max( 8f, handHeight ) / g );
		return Math.Max( 60f, range / Math.Max( 0.05f, fallTime ) );
	}
}

public struct BolaThrowIntent
{
	public float HoldSeconds;
	public float OriginX;
	public float OriginY;
	public float OriginZ;
	public float DirX;
	public float DirY;
	public float DirZ;
}
