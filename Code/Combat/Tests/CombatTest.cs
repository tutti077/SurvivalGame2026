using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>combat_test</c>: automated regression suite for melee combat. Run it in play mode in any scene
/// (testscene1 is the reference; a scene without a <see cref="CombatAuthority"/> gets a temporary one
/// on the test object for the run, so the suite never needs the user's scene). Rule checks run first, then two
/// real <c>basicplayer</c> pawns (input, camera and HUD off) fight scripted rounds far above the
/// scene through <see cref="CombatAuthority.ValidateAndApplyPrimaryMelee"/> — the same host path a
/// player's swing takes. Expected numbers are read from the live tuning (weapon class, block
/// outcomes), so retuning never fails it; broken behaviour does.
/// Console: one <c>[CombatTest] PASS|FAIL</c> line per case, then <c>[CombatTest] RESULT …</c>.
/// </summary>
public sealed class CombatTest : Component
{
	const string Tag = "[CombatTest]";
	const string PawnPrefab = "prefabs/player/basicplayer.prefab";

	/// <summary>Weapon both pawns hold: a real hotbar item, so class timings, the melee-item gate and durability all run.</summary>
	const string WeaponId = "club_wood";

	/// <summary>Arena origin: far above any scene content so nothing else is in the sweep.</summary>
	static readonly Vector3 ArenaOrigin = new( 0f, 0f, 30000f );

	readonly List<string> _failures = new();
	int _passed;
	GameObject _attackerGo;
	GameObject _defenderGo;
	PlayerCombat _attacker;
	PlayerCombat _defender;
	PlayerVitals _attackerVitals;
	PlayerVitals _defenderVitals;
	ushort _intentSequence;

	static CombatTest _running;

	[ConCmd( "combat_test" )]
	public static void ConCmdCombatTest()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || !Sandbox.Game.IsPlaying )
		{
			Log.Warning( $"{Tag} enter play mode first (testscene1 is the reference scene)." );
			return;
		}

		if ( _running.IsValid() )
		{
			Log.Warning( $"{Tag} already running." );
			return;
		}

		var go = scene.CreateObject();
		go.Name = "CombatTest";
		go.NetworkMode = NetworkMode.Never;
		_running = go.Components.Create<CombatTest>();
	}

	protected override void OnStart() => _ = RunAsync();

	protected override void OnDestroy()
	{
		_attackerGo?.Destroy();
		_defenderGo?.Destroy();
	}

	async System.Threading.Tasks.Task RunAsync()
	{
		Log.Info( $"{Tag} starting" );
		try
		{
			RunRuleChecks();

			if ( CombatAuthority.Instance is null || !CombatAuthority.Instance.IsValid() )
			{
				Log.Info( $"{Tag} scene has no CombatAuthority — using a temporary one for this run" );
				GameObject.Components.Create<CombatAuthority>();
			}

			if ( !SpawnPawns() )
				Fail( "spawn", $"could not clone {PawnPrefab} with PlayerCombat + PlayerVitals" );
			else
			{
				// Hotbar / equipment wire up in OnStart, which can land a few frames after the clone.
				var armed = false;
				var until = Time.NowDouble + 2.0;
				while ( !armed && Time.NowDouble < until )
				{
					await Task.Frame();
					armed = EquipWeapon( _attackerGo ) & EquipWeapon( _defenderGo );
				}

				Check( "pawns hold a melee weapon", armed, $"could not equip {WeaponId} from the hotbar" );
				if ( armed )
					await RunLiveRounds();
			}
		}
		catch ( Exception e )
		{
			Fail( "exception", e.ToString() );
		}

		var total = _passed + _failures.Count;
		var verdict = _failures.Count == 0 ? "ALL PASS" : $"{_failures.Count} FAILED: {string.Join( ", ", _failures )}";
		Log.Info( $"{Tag} RESULT {_passed}/{total} passed — {verdict}" );
		GameObject.Destroy();
	}

	// ── Rule checks (no pawns) ─────────────────────────────────────────────────────────────

	void RunRuleChecks()
	{
		Check( "attack type from cursor",
			MeleeAttackTypes.FromCursorDir( SwingDirs.Left ) == MeleeAttackTypes.Left
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Right ) == MeleeAttackTypes.Right
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Up ) == MeleeAttackTypes.Forward,
			"L/R/U cursor must map to Left/Right/Forward attacks" );

		Check( "southpaw swaps only L/R",
			MeleeAttackTypes.FromCursorDir( SwingDirs.Left, southpawSwing: true ) == MeleeAttackTypes.Right
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Right, southpawSwing: true ) == MeleeAttackTypes.Left
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Up, southpawSwing: true ) == MeleeAttackTypes.Forward,
			"southpaw must swap Left/Right and leave overhead alone" );

		Check( "only the active phase deals damage",
			MeleeAttackStates.DealsDamage( MeleeAttackStates.Active )
			&& !MeleeAttackStates.DealsDamage( MeleeAttackStates.Windup )
			&& !MeleeAttackStates.DealsDamage( MeleeAttackStates.Recovery ),
			"windup / recovery must not deal damage" );

		var t = MeleeWeaponClassCatalog.Resolve( MeleeWeaponClassCatalog.DefaultClassId );
		Check( "default class timings sane",
			t.WindupSeconds > 0f && t.ActiveSeconds > 0f && t.ChargeSeconds >= 0f
			&& t.InitiativeWindupSeconds <= t.WindupSeconds && t.RecoveryHitSeconds <= t.RecoveryMissSeconds
			&& t.ReachForwardMeters > 0f && t.StaminaHeavyCost >= t.StaminaLightCost,
			$"{t.ClassId}: windup {t.WindupSeconds} initiative {t.InitiativeWindupSeconds} active {t.ActiveSeconds} "
			+ $"recovery hit {t.RecoveryHitSeconds} / miss {t.RecoveryMissSeconds} reach {t.ReachForwardMeters} m stamina {t.StaminaLightCost}/{t.StaminaHeavyCost}" );

		var lightBlock = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy: false, perfectParry: false );
		var lightParry = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy: false, perfectParry: true );
		var heavyBlock = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy: true, perfectParry: false );
		var heavyParry = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy: true, perfectParry: true );
		Check( "parry costs nothing",
			lightParry.HealthDamage <= 0f && lightParry.StaminaCost <= 0f && heavyParry.HealthDamage <= 0f && heavyParry.StaminaCost <= 0f
			&& lightParry.WasPerfectParry && heavyParry.WasPerfectParry,
			"melee_block_stagger.json parry rows must cost 0 HP / 0 stamina" );
		Check( "heavy block costs at least a light block",
			heavyBlock.HealthDamage >= lightBlock.HealthDamage && heavyBlock.StaminaCost >= lightBlock.StaminaCost,
			$"light {lightBlock.HealthDamage} hp / {lightBlock.StaminaCost} st, heavy {heavyBlock.HealthDamage} hp / {heavyBlock.StaminaCost} st" );

		// Incoming angle: defender at origin facing +X (yaw 0).
		var front = MeleeBlockResolution.ComputeIncomingHitAngleDegrees( Vector3.Zero, 0f, new Vector3( 100f, 0f, 0f ) );
		var back = MeleeBlockResolution.ComputeIncomingHitAngleDegrees( Vector3.Zero, 0f, new Vector3( -100f, 0f, 0f ) );
		Check( "incoming angle front / back",
			MathF.Abs( front ) < 1f && MathF.Abs( back ) > 179f,
			$"front {front:0.#}° (want 0), back {back:0.#}° (want ±180)" );
	}

	// ── Live rounds ─────────────────────────────────────────────────────────────────────────

	bool SpawnPawns()
	{
		_attackerGo = SpawnPawn( "CombatTest_Attacker" );
		_defenderGo = SpawnPawn( "CombatTest_Defender" );
		_attacker = _attackerGo?.Components.Get<PlayerCombat>();
		_defender = _defenderGo?.Components.Get<PlayerCombat>();
		_attackerVitals = _attackerGo?.Components.Get<PlayerVitals>();
		_defenderVitals = _defenderGo?.Components.Get<PlayerVitals>();
		return _attacker is not null && _defender is not null && _attackerVitals is not null && _defenderVitals is not null;
	}

	GameObject SpawnPawn( string name )
	{
		var template = BuildPrefabUtility.GetTemplate( PawnPrefab );
		if ( template is null )
			return null;

		var go = template.Clone( new Transform( ArenaOrigin ), name: name );
		go.NetworkMode = NetworkMode.Never;
		MakeScripted( go );
		return go;
	}

	/// <summary>Strip a cloned pawn down to what the host simulates for a remote client. Component toggles
	/// made while the clone was still disabled did not stick, so this runs on the live object.</summary>
	static void MakeScripted( GameObject go )
	{
		// A pawn without an enabled PlayerController never reads input (PlayerCombat.IsLocalCombatDriver),
		// so the test drives it like the host drives a remote client's pawn. Everything that only
		// serves the local player (camera, HUD, menus, hotbar keys, interaction) is switched off.
		if ( go.Components.Get<PlayerController>() is { } controller )
		{
			controller.UseInputControls = false;
			controller.UseCameraControls = false;
			controller.UseLookControls = false;
			controller.Enabled = false;
		}

		if ( go.Components.Get<Rigidbody>() is { } body )
			body.MotionEnabled = false;

		// The prefab ships with the infinite-stamina dev toggle on; the suite measures real costs.
		if ( go.Components.Get<PlayerVitals>() is { } vitals )
			vitals.InfiniteStaminaDebug = false;

		DisableAll<ScreenPanel>( go );
		DisableAll<PlayerScreenHud>( go );
		DisableAll<PlayerCrosshair>( go );
		DisableAll<PlayerGameMenuController>( go );
		DisableAll<PlayerHotbarController>( go );
		DisableAll<PlayerInventoryInteraction>( go );
		DisableAll<PlayerSpawnSeparation>( go );
		DisableAll<PlayerMovement>( go );
	}

	/// <summary>Weapon into hotbar slot 0 and equipped from there, the way a number key equips it.</summary>
	static bool EquipWeapon( GameObject go )
	{
		var hotbar = go.Components.Get<PlayerHotbar>();
		var equipment = go.Components.Get<PlayerEquipment>();
		if ( hotbar is null || equipment is null )
			return false;

		// Pickups only land in a slot bound to that item; the return value is what did not fit.
		if ( go.Components.Get<PlayerEquippedItem>() is { } already && already.HasAction( EquippedItemActions.PrimaryMelee ) )
			return true;

		hotbar.SetBinding( 0, WeaponId );
		hotbar.SetActiveSlot( 0 );
		if ( hotbar.GetSlot( 0 ).IsEmpty && hotbar.TryAddResourcePickup( WeaponId, 1 ) != 0 )
			return false;

		equipment.EquipFromHotbarSlot( 0 );
		return go.Components.Get<PlayerEquippedItem>() is { } held && held.HasAction( EquippedItemActions.PrimaryMelee );
	}

	int AttackerWear => _attackerGo.Components.Get<PlayerHotbar>()?.GetSlot( 0 ).Wear ?? -1;

	static void DisableAll<T>( GameObject go ) where T : Component
	{
		foreach ( var c in go.Components.GetAll<T>( FindMode.EverythingInSelfAndDescendants ) )
			c.Enabled = false;
	}

	async System.Threading.Tasks.Task RunLiveRounds()
	{
		var reach = _attacker.GetMeleeAttackRangeUnits( MeleeAttackTypes.Right );
		var close = Math.Max( 24f, reach * 0.55f );
		var t = _attacker.GetMeleeWeaponTimings();
		var lightHold = 0f;
		var heavyHold = t.HeavyHoldThresholdSeconds + 0.05f;

		// 1. Light hit on an open defender.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var wear = AttackerWear;
			var expectedCost = _attacker.GetPrimaryAttackStaminaCostForHoldDuration( lightHold );
			var res = Swing( lightHold );
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "light swing accepted", res.Accepted && res.DebugCode == AttackReleaseDebugCode.OkMeleeSweepStarted,
				$"code {res.DebugCode}: {res.DebugDetail}" );
			Check( "light swing spends class stamina", MathF.Abs( stSpent - expectedCost ) < 0.51f,
				$"spent {stSpent:0.##}, want {expectedCost:0.##}" );

			var busy = Swing( lightHold );
			Check( "second swing refused while busy", !busy.Accepted && busy.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
				$"code {busy.DebugCode}: {busy.DebugDetail}" );

			var rot = _attackerGo.WorldRotation;
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( isHeavy: false );
			Check( "light hit deals light damage", MathF.Abs( dealt - want ) < 0.51f, $"dealt {dealt:0.##}, want {want:0.##}" );
			Check( "hit not flagged blocked", !_attacker.LastMeleeHitResult.WasBlocked, "open defender reported as blocked" );
			Check( "a hit wears the weapon once", AttackerWear == wear + 1, $"wear {wear} → {AttackerWear}, want +1" );
			Check( "combat never rotates the pawn root", rot.Distance( _attackerGo.WorldRotation ) < 0.01f,
				$"root yaw moved {rot.Angles().yaw:0.#}° → {_attackerGo.WorldRotation.Angles().yaw:0.#}°" );
			Check( "clean hit arms initiative", _attacker.ServerIsInitiativeArmed, "initiative not armed after a clean hit" );

			if ( _attacker.GetMeleeStagger() > 1e-4f )
			{
				Check( "victim enters hit reaction", _defender.IsHitReactionActive, "defender not in hit reaction after a clean hit" );
				var victimSwing = Swing( lightHold, _defender, _defenderGo );
				Check( "hit reaction locks the victim's attacks",
					!victimSwing.Accepted && victimSwing.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
					$"victim swing during hit reaction: code {victimSwing.DebugCode}: {victimSwing.DebugDetail}" );
			}
		}

		// 2. Heavy hit (hold past windup + charge).
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var res = Swing( heavyHold );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( isHeavy: true );
			Check( "heavy hit deals heavy damage", res.Accepted && MathF.Abs( dealt - want ) < 0.51f && _attacker.LastMeleeHitResult.IsHeavy,
				$"accepted {res.Accepted}, dealt {dealt:0.##}, want {want:0.##}, heavy flag {_attacker.LastMeleeHitResult.IsHeavy}" );
			Check( "heavy out-damages light", want > _attacker.GetMeleeDamage( isHeavy: false ), "heavy damage ≤ light damage" );
		}

		// 3. Out of reach = miss, no damage.
		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var wear = AttackerWear;
			Swing( lightHold );
			await WaitSwingDone();
			Check( "a miss does not wear the weapon", AttackerWear == wear, $"wear {wear} → {AttackerWear} on a miss" );
			Check( "out of reach misses", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP at {reach * 1.8f + 40f:0} u (reach {reach:0} u)" );
			Check( "miss does not arm initiative", !_attacker.ServerIsInitiativeArmed, "initiative armed after a miss" );
		}

		// 4. Standard block (raised well before the hit, outside the parry window).
		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( Time.NowDouble );
			await Task.DelaySeconds( _defender.MeleeBlockParryWindowSeconds + 0.3f );
			Check( "guard is up", _defender.IsAuthoritativeMeleeBlocking,
				$"defender block did not register on the host (controller enabled {_defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf )?.Enabled}, "
				+ $"combat enabled {_defender.Enabled}, active {_defender.Active})" );
			var defenderSwing = Swing( lightHold, _defender, _defenderGo );
			Check( "blocking pawn cannot swing",
				!defenderSwing.Accepted && defenderSwing.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
				$"code {defenderSwing.DebugCode}: {defenderSwing.DebugDetail}" );

			var hp = _defenderVitals.CurrentHealth;
			var st = _defenderVitals.CurrentStamina;
			Swing( lightHold );
			await WaitSwingDone();
			var outcome = MeleeBlockStaggerCatalog.Resolve( attackWasHeavy: false, perfectParry: false );
			var hit = _attacker.LastMeleeHitResult;
			Check( "front block blocks", hit.WasBlocked && !hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			Check( "block costs the lightBlock row",
				MathF.Abs( hp - _defenderVitals.CurrentHealth - outcome.HealthDamage ) < 0.51f
				&& MathF.Abs( st - _defenderVitals.CurrentStamina - outcome.StaminaCost ) < 1.01f,
				$"lost {hp - _defenderVitals.CurrentHealth:0.##} hp / {st - _defenderVitals.CurrentStamina:0.##} st, want {outcome.HealthDamage} / {outcome.StaminaCost}" );
			Check( "blocked hit clears initiative", !_attacker.ServerIsInitiativeArmed, "initiative armed after a blocked hit" );
			LowerBlock();
		}

		// 5. Perfect parry (guard raised the same frame the swing releases into its active sweep).
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			RaiseBlock( Time.NowDouble );
			Swing( t.WindupSeconds );
			await WaitSwingDone();
			var hit = _attacker.LastMeleeHitResult;
			Check( "block inside the parry window parries", hit.WasBlocked && hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			Check( "parry costs no HP", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f, $"lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			LowerBlock();
		}

		// 6. Guard up but the attacker is behind: the back arc is open.
		await Reset( close, defenderFacesAttacker: false );
		{
			RaiseBlock( Time.NowDouble - 1.0 );
			await Task.DelaySeconds( 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			Swing( lightHold );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "hit from behind ignores the guard",
				!_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( dealt - _attacker.GetMeleeDamage( false ) ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, dealt {dealt:0.##}" );
			LowerBlock();
		}
	}

	/// <summary>Place both pawns (attacker at the origin facing +X), refill pools, wait out any lock.</summary>
	async System.Threading.Tasks.Task Reset( float distance, bool defenderFacesAttacker )
	{
		LowerBlock();
		await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction && !_attacker.IsCombatActionLocked
		                       && !_defender.ServerHasActiveMeleeAttackAction && !_defender.IsCombatActionLocked, 4f );
		// Initiative from the previous round would shorten windups and shift the heavy threshold.
		await WaitUntil( () => !_attacker.ServerIsInitiativeArmed, _attacker.InitiativeWindowSeconds + 0.5f );

		var inputFree = !_attackerGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ).Enabled
		                && !_defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ).Enabled;
		if ( !inputFree )
		{
			Fail( "test pawns ignore input", "a PlayerController came back on — the pawn would read real input; re-stripping" );
			MakeScripted( _attackerGo );
			MakeScripted( _defenderGo );
		}

		_attackerVitals.HostArenaRestoreAndTeleport( ArenaOrigin, Rotation.FromYaw( 0f ) );
		_defenderVitals.HostArenaRestoreAndTeleport( ArenaOrigin + Vector3.Forward * distance,
			Rotation.FromYaw( defenderFacesAttacker ? 180f : 0f ) );
		await Task.Frame();
	}

	void RaiseBlock( double pressedAt ) =>
		_defender.TestSubmitBlockIntent( true, pressedAt, _defenderGo.WorldRotation.Angles().yaw );

	void LowerBlock()
	{
		if ( _defender.IsValid() )
			_defender.TestSubmitBlockIntent( false, 0d, _defenderGo.WorldRotation.Angles().yaw );
	}

	AttackReleaseResult Swing( float holdSeconds ) => Swing( holdSeconds, _attacker, _attackerGo );

	/// <summary>A right swing straight along the pawn's facing, submitted as release intent to the host.</summary>
	AttackReleaseResult Swing( float holdSeconds, PlayerCombat pc, GameObject go )
	{
		var rot = go.WorldRotation;
		var view = rot.Forward;
		var cam = go.WorldPosition + Vector3.Up * 64f;
		var now = RealTime.GlobalNow;
		var angles = rot.Angles();
		var flat = new Vector2( view.x, view.z );
		flat = flat.LengthSquared < 1e-8f ? new Vector2( 0f, 1f ) : flat.Normal;
		var swingFrom = new Vector2( flat.y, -flat.x );

		var intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = now - holdSeconds,
			ReleasedGlobalSeconds = now,
			ClientCameraPressX = cam.x,
			ClientCameraPressY = cam.y,
			ClientCameraPressZ = cam.z,
			ClientCameraReleaseX = cam.x,
			ClientCameraReleaseY = cam.y,
			ClientCameraReleaseZ = cam.z,
			ViewForwardOnPress = view,
			ViewForwardOnRelease = view,
			ClientPlayerWorldPosition = go.WorldPosition,
			ClientPlayerWorldRotation = rot,
			IntentSequence = ++_intentSequence,
			SwingFromX = swingFrom.x,
			SwingFromY = swingFrom.y,
			SwingVerticalHint = 0f,
			SwingDir = SwingDirs.Right,
			AttackType = pc.ResolveAttackTypeFromCursorDir( SwingDirs.Right ),
			CombatBasisYawDegrees = angles.yaw,
			CombatBasisPitchDegrees = 0f
		};

		return CombatAuthority.Instance.ValidateAndApplyPrimaryMelee( go, intent );
	}

	async System.Threading.Tasks.Task WaitSwingDone()
	{
		await Task.Frame();
		await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction, 3f );
		await Task.Frame();
	}

	async System.Threading.Tasks.Task WaitUntil( Func<bool> done, float timeoutSeconds )
	{
		var until = Time.NowDouble + timeoutSeconds;
		while ( !done() && Time.NowDouble < until )
			await Task.Frame();
	}

	void Check( string name, bool ok, string detail )
	{
		if ( ok )
		{
			_passed++;
			Log.Info( $"{Tag} PASS {name}" );
		}
		else
			Fail( name, detail );
	}

	void Fail( string name, string detail )
	{
		_failures.Add( name );
		Log.Warning( $"{Tag} FAIL {name} — {detail}" );
	}
}
