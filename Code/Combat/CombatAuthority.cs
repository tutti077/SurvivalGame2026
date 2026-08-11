using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side melee validation and damage application. Place on your network / manager object so pawns only collect intent and prediction.
/// </summary>
[Title( "Combat Authority" )]
public sealed class CombatAuthority : Component
{
	public static CombatAuthority Instance { get; private set; }

	[Property, Group( "Combat — Server validation" )]
	public float ServerAttackRateLimitSeconds { get; set; }

	[Property, Group( "Combat — Server validation" )]
	public float MaxCameraPositionDelta { get; set; } = 384f;

	[Property, Group( "Combat — Server validation" )]
	public float MaxPlayerRootVsServerDelta { get; set; } = 256f;

	[Property, Group( "Combat — Debug" )]
	public bool LogMeleeStaminaSettlement { get; set; }

	readonly Dictionary<Guid, double> _lastAcceptedAttackByAttacker = new();

	protected override void OnEnabled()
	{
		if ( Instance is not null && Instance != this )
			Log.Warning( "[CombatAuthority] Multiple enabled CombatAuthority components — Instance points at the last enabled." );
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
	}

	protected override void OnUpdate()
	{
		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;

		// Proxy PlayerCombat often skips OnUpdate — tick swing overlays / block viz for every peer from here.
		// driveHostProxyAuthority: also advance host sweeps on remote-owned pawns.
		PlayerCombat.TickSceneCombatVisualizations( scene, driveHostProxyAuthority: true );

		// After Rpc.Host stacks unwind — push path overlays / swing anim via NetworkManager Broadcast.
		PlayerCombat.FlushDeferredSwingVisualBroadcasts( scene );
		PlayerAnimation.FlushDeferredSwingAnimBroadcasts( scene );
	}

	/// <summary>
	/// Host→all peers: play melee swing presentation on the named attacker.
	/// Called from <see cref="PlayerAnimation.FlushDeferredSwingAnimBroadcasts"/> after Rpc.Host unwinds.
	/// Lives on CombatAuthority (scene NetworkManager) so delivery isn't tied to attacker ownership.
	/// </summary>
	public void HostBroadcastMeleeSwingAnim( Guid attackerRootId, byte attackType )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		RpcBroadcastMeleeSwingAnim( attackerRootId, attackType );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable | NetFlags.SendImmediate )]
	void RpcBroadcastMeleeSwingAnim( Guid attackerRootId, byte attackType )
	{
		if ( Networking.IsHost )
			return;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var anim in scene.GetAllComponents<PlayerAnimation>() )
		{
			if ( anim is null || !anim.GameObject.IsValid() )
				continue;
			if ( anim.GameObject.Id != attackerRootId )
				continue;

			anim.ApplyRemoteMeleeSwingAttack( attackType );
			return;
		}
	}

	/// <summary>
	/// Host→all peers: hide a deterministic world-scatter tree that was chopped on the host.
	/// </summary>
	public void HostBroadcastScatterBroken( string stableKey )
	{
		if ( !Networking.IsHost || string.IsNullOrWhiteSpace( stableKey ) )
			return;

		RpcBroadcastScatterBroken( stableKey );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastScatterBroken( string stableKey )
	{
		WorldScatterIdentity.ApplyBrokenLocal( stableKey );
	}

	public void HostBroadcastScatterHarvestDepleted( string stableKey )
	{
		if ( !Networking.IsHost || string.IsNullOrWhiteSpace( stableKey ) )
			return;

		RpcBroadcastScatterHarvestDepleted( stableKey );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastScatterHarvestDepleted( string stableKey )
	{
		WorldScatterIdentity.ApplyHarvestDepletedLocal( stableKey );
	}

	/// <summary>Host-only: validate intent against <paramref name="attacker"/> root and apply damage. Caller must be the attacker owner when networked.</summary>
	public AttackReleaseResult ValidateAndApplyPrimaryMelee( GameObject attacker, AttackReleaseIntent intent )
	{
		if ( !attacker.IsValid() )
			return RejectResult( AttackReleaseDebugCode.RejectPlayerVsServerRoot, "attacker invalid" );

		if ( attacker.Network is { Active: true } && !Networking.IsHost )
			return RejectResult( AttackReleaseDebugCode.RejectNotHost, "ServerValidate while not host" );

		var pc = attacker.Components.Get<PlayerCombat>();
		if ( pc is null )
			return RejectResult( AttackReleaseDebugCode.RejectAttackerMissingPlayerCombat, "attacker has no PlayerCombat" );

		var prepaid = Math.Clamp( intent.StaminaPrepaidMax, 0f, pc.PrimaryAttackStaminaHeavyCost );
		var pv = attacker.Components.Get<PlayerVitals>();

		var refundPrepaidOnEarlyFail = prepaid > 1e-4f;
		void RefundPrimaryAttackPrepaid()
		{
			if ( !refundPrepaidOnEarlyFail || pv is null || !attacker.IsValid() )
				return;

			if ( VitalsAuthority.Instance is { } vAuthRefund )
				vAuthRefund.TryApplyDeltas( attacker, 0f, prepaid, pv );
			else
				pv.RequestVitalsDelta( 0f, prepaid );

			refundPrepaidOnEarlyFail = false;
		}

		AttackReleaseResult Fail( int code, string detail )
		{
			RefundPrimaryAttackPrepaid();
			return RejectResult( code, detail );
		}

		// The pawn keeps its PlayerCombat with empty hands (shove is a player ability), so the swing
		// needs its own gate. Host-side equipment is authoritative here — it is the same lookup that
		// drives the synced hold pose for remote pawns.
		var attackerEquipped = attacker.Components.Get<PlayerEquippedItem>();
		if ( attackerEquipped is not null && !attackerEquipped.HasAction( EquippedItemActions.PrimaryMelee ) )
			return Fail( AttackReleaseDebugCode.RejectNoMeleeItemEquipped, "attacker has no melee item equipped" );

		if ( attacker.Network is { Active: true } net && net.Owner is { } owner && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return Fail( AttackReleaseDebugCode.RejectOwnerMismatch, $"caller [{ConnectionIdentity.Format( caller )}] ≠ owner [{ConnectionIdentity.Format( owner )}]" );

		if ( ServerAttackRateLimitSeconds > 0f
		     && _lastAcceptedAttackByAttacker.TryGetValue( attacker.Id, out var last )
		     && RealTime.GlobalNow - last < ServerAttackRateLimitSeconds )
		{
			return Fail( AttackReleaseDebugCode.RejectRateLimit,
				$"rate d={(RealTime.GlobalNow - last):0.###}s < {ServerAttackRateLimitSeconds:0.###}s" );
		}

		var rootDelta = (intent.ClientPlayerWorldPosition - attacker.WorldPosition).Length;
		if ( rootDelta > MaxPlayerRootVsServerDelta )
		{
			return Fail( AttackReleaseDebugCode.RejectPlayerVsServerRoot,
				$"root d={rootDelta:0.#} max={MaxPlayerRootVsServerDelta:0.#}" );
		}

		var camPress = new Vector3( intent.ClientCameraPressX, intent.ClientCameraPressY, intent.ClientCameraPressZ );
		var camRel = new Vector3( intent.ClientCameraReleaseX, intent.ClientCameraReleaseY, intent.ClientCameraReleaseZ );
		var clientRoot = intent.ClientPlayerWorldPosition;
		var dPress = (camPress - clientRoot).Length;
		var dRel = (camRel - clientRoot).Length;
		if ( dPress > MaxCameraPositionDelta || dRel > MaxCameraPositionDelta )
		{
			return Fail( AttackReleaseDebugCode.RejectCameraVsPlayer,
				$"cam from root dPress={dPress:0.#} dRel={dRel:0.#} max={MaxCameraPositionDelta:0.#}" );
		}

		var dir = intent.ViewForwardOnPress;
		if ( dir.LengthSquared < 0.01f )
			return Fail( AttackReleaseDebugCode.RejectDirection, "view forward too small" );

		dir = dir.Normal;
		if ( intent.ViewForwardOnRelease.LengthSquared < 0.01f )
			return Fail( AttackReleaseDebugCode.RejectDirection, "view release too small" );

		var holdSeconds = Math.Max( 0f, (float)( intent.ReleasedGlobalSeconds - intent.PressedGlobalSeconds ) );
		if ( !double.IsFinite( intent.PressedGlobalSeconds ) || !double.IsFinite( intent.ReleasedGlobalSeconds ) )
			return Fail( AttackReleaseDebugCode.RejectDirection, "attack intent timing not finite" );

		if ( !pc.ServerCanBeginMeleeAttackAction() )
			return Fail( AttackReleaseDebugCode.RejectMeleeBusy, "melee attack action busy or cannot begin on host" );

		var staminaCost = pc.GetPrimaryAttackStaminaCostForHoldDuration( holdSeconds );
		if ( staminaCost > 1e-4f )
		{
			if ( pv is null )
			{
				return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
					$"need {staminaCost:0.#} stamina, attacker has no PlayerVitals" );
			}

			if ( prepaid > 1e-4f )
			{
				var settle = prepaid - staminaCost;
				refundPrepaidOnEarlyFail = false;
				if ( VitalsAuthority.Instance is { } vAuth )
				{
					if ( !vAuth.TryApplyDeltas( attacker, 0f, settle, pv ) )
					{
						refundPrepaidOnEarlyFail = true;
						return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
							$"stamina settle prepaid={prepaid:0.#} actual={staminaCost:0.#}" );
					}
				}
				else
				{
					if ( !pv.RequestVitalsDelta( 0f, settle ) )
					{
						refundPrepaidOnEarlyFail = true;
						return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
							"stamina settle rejected (no VitalsAuthority)" );
					}
				}

				if ( LogMeleeStaminaSettlement )
					Log.Info( $"[CombatAuthority/Stamina] {attacker.Name} settle prepaid={prepaid:0.#} hold={holdSeconds:0.###}s actual={staminaCost:0.#} Δ={settle:+0.#;-0.#;0} → st={pv.CurrentStamina:0.#}/{pv.CurrentStaminaMax:0.#}" );
			}
			else if ( VitalsAuthority.Instance is { } vAuthLegacy )
			{
				if ( !vAuthLegacy.TryApplyDeltas( attacker, 0f, -staminaCost, pv ) )
				{
					return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
						$"need {staminaCost:0.#} st, have {pv.CurrentStamina:0.#}" );
				}

				if ( LogMeleeStaminaSettlement )
					Log.Info( $"[CombatAuthority/Stamina] {attacker.Name} drain hold={holdSeconds:0.###}s cost={staminaCost:0.#} → st={pv.CurrentStamina:0.#}/{pv.CurrentStaminaMax:0.#}" );
			}
			else
			{
				if ( !pv.HasStaminaFor( staminaCost ) )
				{
					return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
						$"need {staminaCost:0.#} st, have {pv.CurrentStamina:0.#} (no VitalsAuthority)" );
				}

				if ( !pv.RequestVitalsDelta( 0f, -staminaCost ) )
				{
					return Fail( AttackReleaseDebugCode.RejectInsufficientStamina,
						"stamina spend rejected" );
				}

				if ( LogMeleeStaminaSettlement )
					Log.Info( $"[CombatAuthority/Stamina] {attacker.Name} drain hold={holdSeconds:0.###}s cost={staminaCost:0.#} → st={pv.CurrentStamina:0.#}/{pv.CurrentStaminaMax:0.#}" );
			}
		}

		var swingAuth = ServerNormalizeSwingFromXz( intent.SwingFromX, intent.SwingFromY, attacker.WorldRotation );
		var swingVert = ServerClampSwingVertical( intent.SwingVerticalHint );
		var isHeavy = pc.IsHeavyAttackForHoldDuration( holdSeconds );
		var attackType = pc.ResolveAttackTypeFromIntent( intent );
		var swingNote =
			$" {FormatSwingLog( swingAuth, swingVert, intent.SwingDir )} type={MeleeAttackTypes.Label( attackType )} heavy={isHeavy} hold={holdSeconds:0.###}s";

		pc.ServerStartMeleeAttackAction( intent, holdSeconds, isHeavy, swingNote );

		_lastAcceptedAttackByAttacker[attacker.Id] = RealTime.GlobalNow;

		return new AttackReleaseResult
		{
			Accepted = true,
			Hit = false,
			DamageDealt = 0f,
			TargetGameObjectId = Guid.Empty,
			DebugCode = AttackReleaseDebugCode.OkMeleeSweepStarted,
			DebugDetail = $"scheduled host sweep seq={intent.IntentSequence} type={MeleeAttackTypes.Label( attackType )} heavy={isHeavy} wind={pc.GetMeleeWindupDuration( isHeavy ):0.###}s active={MeleeAttackPath.GetActiveDurationSeconds( pc, attackType, isHeavy ):0.###}s rec={pc.MeleeRecoveryDuration:0.###}s{swingNote}"
		};
	}

	public static Vector2 ServerNormalizeSwingFromXz( float sx, float sy, Rotation pawnWorldRot )
	{
		var v = new Vector2( sx, sy );
		var len2 = v.LengthSquared;
		if ( len2 < 1e-6f )
		{
			var f = pawnWorldRot.Forward;
			var xz = new Vector2( f.x, f.z );
			return xz.LengthSquared < 1e-8f ? new Vector2( 0f, 1f ) : xz.Normal;
		}

		return v.Normal;
	}

	public static float ServerClampSwingVertical( float v ) => Math.Clamp( v, -1f, 1f );

	public static string FormatSwingLog( Vector2 xz, float verticalHint, byte cardinal ) =>
		$"swing={SwingDirs.Letter( cardinal )} xz=({xz.x:F2},{xz.y:F2}) v={verticalHint:F2}";

	public static SceneTraceResult RunAuthorityMeleeTrace( Vector3 origin, Vector3 directionUnit, GameObject ignoreRoot )
	{
		// Use the ignore object’s scene (GameObject.Scene) — never bare `Scene` in a Component subclass (resolves to Component.Scene / CS0120 in static context on some compilers).
		var scene = ignoreRoot.IsValid() && ignoreRoot.Scene.IsValid()
			? ignoreRoot.Scene
			: Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return default;

		var end = origin + directionUnit * AttackCombatConstants.DefaultMeleeRange;
		var solid = scene.Trace.Ray( origin, end ).IgnoreGameObjectHierarchy( ignoreRoot ).Run();
		if ( solid.Hit && solid.GameObject.IsValid() )
			return solid;
		return scene.Trace.Ray( origin, end ).UseHitboxes().IgnoreGameObjectHierarchy( ignoreRoot ).Run();
	}

	public static bool TryFindDamageable( GameObject hitObject, out Component receiver )
	{
		receiver = null;
		if ( !hitObject.IsValid() )
			return false;

		Component found = null;

		for ( var p = hitObject; p.IsValid(); p = p.Parent )
		{
			var self = p.Components.Get<DamageReceiver>();
			if ( self is not null )
			{
				found = self;
				receiver = found;
				return true;
			}
		}

		bool VisitChildren( GameObject go )
		{
			if ( !go.IsValid() )
				return false;

			foreach ( var ch in go.Children )
			{
				var c = ch.Components.Get<DamageReceiver>();
				if ( c is not null )
				{
					found = c;
					return true;
				}

				if ( VisitChildren( ch ) )
					return true;
			}

			return false;
		}

		if ( !VisitChildren( hitObject ) )
			return false;

		receiver = found;
		return true;
	}

	static AttackReleaseResult RejectResult( int code, string detail ) =>
		new AttackReleaseResult
		{
			Accepted = false,
			Hit = false,
			DamageDealt = 0f,
			TargetGameObjectId = Guid.Empty,
			DebugCode = code,
			DebugDetail = detail
		};

	static AttackReleaseResult AcceptMiss( string detail ) =>
		new AttackReleaseResult
		{
			Accepted = true,
			Hit = false,
			DamageDealt = 0f,
			TargetGameObjectId = Guid.Empty,
			DebugCode = AttackReleaseDebugCode.OkMiss,
			DebugDetail = detail
		};

	public static bool IsGameObjectUnderHierarchy( GameObject root, GameObject node )
	{
		if ( !root.IsValid() || !node.IsValid() )
			return false;

		for ( var p = node; p.IsValid(); p = p.Parent )
		{
			if ( p.Id == root.Id )
				return true;
		}

		return false;
	}

	public static PlayerVitals ResolvePlayerVitalsForDamageReceiver( DamageReceiver dmg )
	{
		if ( dmg is null || !dmg.GameObject.IsValid() )
			return null;

		var self = dmg.GameObject.Components.Get<PlayerVitals>();
		if ( self is not null && self.Enabled )
			return self;

		for ( var p = dmg.GameObject.Parent; p.IsValid(); p = p.Parent )
		{
			var v = p.Components.Get<PlayerVitals>();
			if ( v is not null && v.Enabled )
				return v;
		}

		return null;
	}

	public static EntityVitals ResolveEntityVitalsForDamageReceiver( DamageReceiver dmg )
	{
		if ( dmg is null || !dmg.GameObject.IsValid() )
			return null;

		var self = dmg.GameObject.Components.Get<EntityVitals>();
		if ( self is not null && self.Enabled )
			return self;

		for ( var p = dmg.GameObject.Parent; p.IsValid(); p = p.Parent )
		{
			var v = p.Components.Get<EntityVitals>();
			if ( v is not null && v.Enabled )
				return v;
		}

		return null;
	}

	public static bool IsDamageVictimAlive( DamageReceiver dmg )
	{
		var player = ResolvePlayerVitalsForDamageReceiver( dmg );
		if ( player is not null )
			return player.CurrentHealth > 0.001f;

		var entity = ResolveEntityVitalsForDamageReceiver( dmg );
		if ( entity is not null )
			return !entity.IsDead;

		var tree = ResolveChopableTreeForDamageReceiver( dmg );
		if ( tree is not null )
			return !tree.IsBroken;

		return true;
	}

	public static bool MayApplyMeleeDamageFromAttackerToReceiver( GameObject attackerRoot, DamageReceiver dmg )
	{
		var victimVitals = ResolvePlayerVitalsForDamageReceiver( dmg );
		if ( attackerRoot.Network is { Active: true }
		     && victimVitals is not null
		     && victimVitals.GameObject.Network is not { Active: true } )
			return false;

		return true;
	}

	public static Guid ResolveMeleeVictimDedupId( DamageReceiver dmg )
	{
		var player = ResolvePlayerVitalsForDamageReceiver( dmg );
		if ( player is not null && player.GameObject.IsValid() )
			return player.GameObject.Id;

		var entity = ResolveEntityVitalsForDamageReceiver( dmg );
		if ( entity is not null && entity.GameObject.IsValid() )
			return entity.GameObject.Id;

		var tree = ResolveChopableTreeForDamageReceiver( dmg );
		if ( tree is not null && tree.GameObject.IsValid() )
			return tree.GameObject.Id;

		return dmg.GameObject.Id;
	}

	public static ChopableTree ResolveChopableTreeForDamageReceiver( DamageReceiver dmg )
	{
		if ( dmg is null || !dmg.GameObject.IsValid() )
			return null;

		var self = dmg.GameObject.Components.Get<ChopableTree>();
		if ( self is not null && self.Enabled )
			return self;

		for ( var p = dmg.GameObject.Parent; p.IsValid(); p = p.Parent )
		{
			var t = p.Components.Get<ChopableTree>();
			if ( t is not null && t.Enabled )
				return t;
		}

		return null;
	}
}
