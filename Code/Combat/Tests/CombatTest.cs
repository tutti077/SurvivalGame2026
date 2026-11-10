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
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Up ) == MeleeAttackTypes.Forward
			&& MeleeAttackTypes.FromCursorDir( SwingDirs.Down ) == MeleeAttackTypes.Stab,
			"L/R/U/D cursor must map to Left/Right/Forward/Stab (jab) attacks" );

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
			t.WindupSeconds > 0f && t.ActiveSeconds > 0f
			&& t.RecoveryBlockedSeconds >= 0f && t.RecoveryParriedSeconds >= t.RecoveryBlockedSeconds
			&& t.ReachForwardMeters > 0f && t.StaminaCost >= 0f,
			$"{t.ClassId}: windup {t.WindupSeconds} active {t.ActiveSeconds} lockout blocked {t.RecoveryBlockedSeconds} / "
			+ $"parried {t.RecoveryParriedSeconds} reach {t.ReachForwardMeters} m stamina {t.StaminaCost}" );

		var block = MeleeBlockStaggerCatalog.Resolve( perfectParry: false );
		var parry = MeleeBlockStaggerCatalog.Resolve( perfectParry: true );
		Check( "parry costs nothing",
			parry.HealthDamage <= 0f && parry.StaminaCost <= 0f && parry.WasPerfectParry && !block.WasPerfectParry,
			"melee_block_stagger.json parry row must cost 0 HP / 0 stamina" );

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

		// Infinite stamina is a host-wide dev setting (on by default); the suite measures real costs.
		if ( go.Components.Get<PlayerVitals>() is { } vitals )
			vitals.DrainStaminaForTests = true;

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
		// Generous frame slack: the suite runs at whatever the editor frame rate is.
		const float slack = 0.06f;

		// 1. Hit on an open defender. The intent claims a 1 s hold: the host must still play the full windup.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var wear = AttackerWear;
			var expectedCost = _attacker.GetPrimaryAttackStaminaCost();
			var startedAt = Time.NowDouble;
			var res = Swing( claimedHoldSeconds: 1f );
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "swing accepted", res.Accepted && res.DebugCode == AttackReleaseDebugCode.OkMeleeSweepStarted,
				$"code {res.DebugCode}: {res.DebugDetail}" );
			Check( "swing spends class stamina", MathF.Abs( stSpent - expectedCost ) < 0.51f,
				$"spent {stSpent:0.##}, want {expectedCost:0.##}" );

			var busy = Swing();
			Check( "second swing refused while busy", !busy.Accepted && busy.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
				$"code {busy.DebugCode}: {busy.DebugDetail}" );

			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds - slack, 2f );
			Check( "no damage during the windup (a claimed hold never shortens it)",
				MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP {Time.NowDouble - startedAt:0.###} s after the click (windup {t.WindupSeconds} s)" );

			var rot = _attackerGo.WorldRotation;
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, t.ActiveSeconds + 1f );
			var victimAnim = _defenderGo.Components.Get<PlayerAnimation>();
			if ( _attacker.GetMeleeStagger() > 1e-4f && victimAnim is not null )
			{
				Check( "victim enters hit reaction", _defender.IsHitReactionActive, "defender not in hit reaction after a clean hit" );
				var victimSwing = Swing( _defender, _defenderGo );
				Check( "hit reaction locks the victim's attacks",
					!victimSwing.Accepted && victimSwing.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
					$"victim swing during hit reaction: code {victimSwing.DebugCode}: {victimSwing.DebugDetail}" );
				var reactionStart = Time.NowDouble;
				await WaitUntil( () => !_defender.IsHitReactionActive, 2f );
				var reactionLen = (float)(Time.NowDouble - reactionStart);
				Check( "hit reaction lasts the class victim time", reactionLen <= _attacker.GetMeleeStagger() + slack,
					$"flail lasted {reactionLen:0.###} s, want ≤ {_attacker.GetMeleeStagger():0.###} s" );
			}

			await WaitSwingDone();
			var swingLen = (float)(Time.NowDouble - startedAt);
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage();
			Check( "hit deals weapon damage", MathF.Abs( dealt - want ) < 0.51f, $"dealt {dealt:0.##}, want {want:0.##}" );
			Check( "hit not flagged blocked", !_attacker.LastMeleeHitResult.WasBlocked, "open defender reported as blocked" );
			Check( "a hit keeps the full damage swoop (attack speed unchanged)",
				swingLen >= t.WindupSeconds + t.ActiveSeconds - slack,
				$"swing ended {swingLen:0.###} s after the click, want ≥ {t.WindupSeconds + t.ActiveSeconds:0.###} s" );
			Check( "no lockout after a hit", !_attacker.IsCombatActionLocked, "attacker combat-locked after a landed hit" );
			Check( "a hit wears the weapon once", AttackerWear == wear + 1, $"wear {wear} → {AttackerWear}, want +1" );
			Check( "combat never rotates the pawn root", rot.Distance( _attackerGo.WorldRotation ) < 0.01f,
				$"root yaw moved {rot.Angles().yaw:0.#}° → {_attackerGo.WorldRotation.Angles().yaw:0.#}°" );
		}

		// 1b. The down jab (Mark 2026-11-05): a straight stab along the facing lands the weapon damage after the windup.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			var res = Swing( dir: SwingDirs.Down );
			Check( "down jab accepted", res.Accepted, $"code {res.DebugCode}: {res.DebugDetail}" );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds - slack, 2f );
			Check( "no jab damage during the windup", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP {Time.NowDouble - startedAt:0.###} s after the click" );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "down jab deals weapon damage", MathF.Abs( dealt - _attacker.GetMeleeDamage() ) < 0.51f,
				$"dealt {dealt:0.##}, want {_attacker.GetMeleeDamage():0.##}" );
		}

		// 1c. Static combo (dev toggle, Mark 2026-11-06): right, left, overhead in quick succession — the overhead hits
		//     for the finisher bonus; held, it is a heavy on top. The host recounts the combo itself.
		await Reset( close, defenderFacesAttacker: true );
		{
			var light = _attacker.GetMeleeDamage();
			var finisher = _attacker.GetMeleeDamage( scale: _attacker.ComboFinisherDamageMultiplier );
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right, comboStep: 1 );
			await WaitSwingDone();
			Swing( dir: SwingDirs.Left, comboStep: 2 );
			await WaitSwingDone();
			var beforeOverhead = _defenderVitals.CurrentHealth;
			Check( "combo right + left land light hits", MathF.Abs( hp - beforeOverhead - 2f * light ) < 1.01f,
				$"lost {hp - beforeOverhead:0.##}, want {2f * light:0.##}" );
			var res = Swing( dir: SwingDirs.Up, comboStep: 3 );
			Check( "combo overhead accepted", res.Accepted, $"code {res.DebugCode}: {res.DebugDetail}" );
			await WaitSwingDone();
			var dealt = beforeOverhead - _defenderVitals.CurrentHealth;
			Check( "combo overhead hits for the finisher bonus", MathF.Abs( dealt - finisher ) < 0.51f && finisher > light + 0.5f,
				$"dealt {dealt:0.##}, want {finisher:0.##} (light {light:0.##}, x{_attacker.ComboFinisherDamageMultiplier:0.##})" );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			// A client claiming the third combo swing without the two quick ones before it gets plain damage.
			await Task.DelaySeconds( _attacker.ComboChainWindowSeconds + 0.5f );
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Up, comboStep: 3 );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "a lone combo overhead gets no bonus", MathF.Abs( dealt - _attacker.GetMeleeDamage() ) < 0.51f,
				$"dealt {dealt:0.##}, want {_attacker.GetMeleeDamage():0.##}" );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var want = _attacker.GetMeleeDamage( isHeavy: true, scale: _attacker.ComboFinisherDamageMultiplier );
			Swing( dir: SwingDirs.Right, comboStep: 1 );
			await WaitSwingDone();
			Swing( dir: SwingDirs.Left, comboStep: 2 );
			await WaitSwingDone();
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing( heavy: true, dir: SwingDirs.Up, comboStep: 3 );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds + 0.05f, 3f );
			ReleaseHeavy();
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "light, light, heavy = heavy overhead with the finisher bonus", MathF.Abs( dealt - want ) < 0.51f,
				$"dealt {dealt:0.##}, want {want:0.##}" );
		}

		// 1d. Q special (static combo mode): the class stab winds up its own longer time and hits for the special multiplier.
		if ( t.HasStabSpecial )
		{
			await Reset( close, defenderFacesAttacker: true );
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			var res = Swing( dir: SwingDirs.Down, special: true );
			Check( "special stab accepted", res.Accepted, $"code {res.DebugCode}: {res.DebugDetail}" );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.SpecialWindupSeconds - slack, 3f );
			Check( "no special damage during its windup", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP {Time.NowDouble - startedAt:0.###} s after Q (windup {t.SpecialWindupSeconds} s)" );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( scale: t.SpecialDamageMultiplier );
			Check( "special stab deals the special multiplier", MathF.Abs( dealt - want ) < 0.51f,
				$"dealt {dealt:0.##}, want {want:0.##}" );
		}

		// 2. Out of reach = miss, no damage, and the next click is accepted the moment the swoop ends.
		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var wear = AttackerWear;
			var startedAt = Time.NowDouble;
			Swing();
			await WaitSwingDone();
			var swingLen = (float)(Time.NowDouble - startedAt);
			Check( "a miss does not wear the weapon", AttackerWear == wear, $"wear {wear} → {AttackerWear} on a miss" );
			Check( "out of reach misses", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP at {reach * 1.8f + 40f:0} u (reach {reach:0} u)" );
			Check( "a miss lasts windup + damage swoop", MathF.Abs( swingLen - (t.WindupSeconds + t.ActiveSeconds) ) <= slack * 2f,
				$"swing lasted {swingLen:0.###} s, want {t.WindupSeconds + t.ActiveSeconds:0.###} s" );
			var next = Swing();
			Check( "next click accepted right after a miss (no recovery)", next.Accepted,
				$"code {next.DebugCode}: {next.DebugDetail}" );
			await WaitSwingDone();
		}

		// 3. Standard block (raised well before the hit, outside the parry window).
		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( Time.NowDouble );
			Check( "guard needs the raise time before it blocks",
				MathF.Abs( (float)(_defender.ServerBlockActiveFromSandbox - _defender.ServerBlockStartedAtSandbox) - _defender.BlockRaiseSeconds ) < 1e-3f,
				$"active-from − start = {_defender.ServerBlockActiveFromSandbox - _defender.ServerBlockStartedAtSandbox:0.###} s, want {_defender.BlockRaiseSeconds:0.###} s" );
			await Task.DelaySeconds( _defender.BlockRaiseSeconds + _defender.MeleeBlockParryWindowSeconds + 0.3f );
			Check( "guard is up", _defender.IsAuthoritativeMeleeBlocking,
				$"defender block did not register on the host (controller enabled {_defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf )?.Enabled}, "
				+ $"combat enabled {_defender.Enabled}, active {_defender.Active})" );
			var defenderSwing = Swing( _defender, _defenderGo );
			Check( "blocking pawn cannot swing",
				!defenderSwing.Accepted && defenderSwing.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
				$"code {defenderSwing.DebugCode}: {defenderSwing.DebugDetail}" );

			var hp = _defenderVitals.CurrentHealth;
			var st = _defenderVitals.CurrentStamina;
			Swing();
			await WaitSwingDone();
			var endedAt = Time.NowDouble;
			var outcome = MeleeBlockStaggerCatalog.Resolve( perfectParry: false );
			var hit = _attacker.LastMeleeHitResult;
			Check( "front block blocks", hit.WasBlocked && !hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			Check( "block costs the block row",
				MathF.Abs( hp - _defenderVitals.CurrentHealth - outcome.HealthDamage ) < 0.51f
				&& MathF.Abs( st - _defenderVitals.CurrentStamina - outcome.StaminaCost ) < 1.01f,
				$"lost {hp - _defenderVitals.CurrentHealth:0.##} hp / {st - _defenderVitals.CurrentStamina:0.##} st, want {outcome.HealthDamage} / {outcome.StaminaCost}" );
			await AssertLockout( "blocked", t.RecoveryBlockedSeconds, endedAt, slack );
			LowerBlock();
		}

		// 4. Perfect parry: the guard comes up just as the damage swoop opens.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing();
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds - _defender.BlockRaiseSeconds, 2f );
			RaiseBlock( Time.NowDouble );
			await WaitSwingDone();
			var endedAt = Time.NowDouble;
			var hit = _attacker.LastMeleeHitResult;
			Check( "block inside the parry window parries", hit.WasBlocked && hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			Check( "parry costs no HP", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f, $"lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			await AssertLockout( "parried", t.RecoveryParriedSeconds, endedAt, slack );
			LowerBlock();
		}

		// 5. Heavy: held past the light windup → hangs while held, strikes on release, x class damage, heavy stamina.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var startedAt = Time.NowDouble;
			var res = Swing( heavy: true );
			Check( "heavy swing accepted", res.Accepted, $"code {res.DebugCode}: {res.DebugDetail}" );
			var holdUntil = t.HeavyWindupSeconds + 0.4f;
			await WaitUntil( () => Time.NowDouble - startedAt >= holdUntil, 3f );
			Check( "held heavy keeps hanging past the heavy windup (no damage while held)",
				MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f && _attacker.ServerHasActiveMeleeAttackAction,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP, swing active {_attacker.ServerHasActiveMeleeAttackAction}, {Time.NowDouble - startedAt:0.###} s after the click" );
			var releasedAt = Time.NowDouble;
			ReleaseHeavy();
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, 2f );
			var strikeDelay = (float)(Time.NowDouble - releasedAt);
			Check( "heavy strikes on release", strikeDelay <= t.ActiveSeconds + slack,
				$"first damage {strikeDelay:0.###} s after the release (swoop {t.ActiveSeconds} s)" );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( isHeavy: true );
			Check( "heavy deals class heavy damage", MathF.Abs( dealt - want ) < 0.51f && want > _attacker.GetMeleeDamage() + 0.5f,
				$"dealt {dealt:0.##}, want {want:0.##} (light {_attacker.GetMeleeDamage():0.##})" );
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "heavy spends heavy stamina in total", MathF.Abs( stSpent - t.StaminaHeavyCost ) < 0.51f,
				$"spent {stSpent:0.##}, want {t.StaminaHeavyCost:0.##}" );
		}

		// 6. A blocked heavy breaks the guard: the defender cannot block or attack for the class guard-break time.
		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( Time.NowDouble );
			await Task.DelaySeconds( _defender.BlockRaiseSeconds + _defender.MeleeBlockParryWindowSeconds + 0.3f );
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.05f, 2f );
			ReleaseHeavy();
			await WaitSwingDone();
			var endedAt = Time.NowDouble;
			var hit = _attacker.LastMeleeHitResult;
			Check( "front block blocks a heavy", hit.WasBlocked && !hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			if ( t.HeavyGuardBreakSeconds > slack )
			{
				Check( "blocked heavy breaks the guard", _defender.IsCombatActionLocked && !_defender.IsAuthoritativeMeleeBlocking,
					$"defender locked {_defender.IsCombatActionLocked}, still blocking {_defender.IsAuthoritativeMeleeBlocking}" );
				var defenderSwing = Swing( _defender, _defenderGo );
				Check( "guard-broken defender cannot swing", !defenderSwing.Accepted,
					$"code {defenderSwing.DebugCode}: {defenderSwing.DebugDetail}" );
				await WaitUntil( () => !_defender.IsCombatActionLocked, 2f );
				var broken = (float)(Time.NowDouble - endedAt);
				Check( "guard break lasts the class time", broken <= t.HeavyGuardBreakSeconds + slack,
					$"defender locked {broken:0.###} s, want ≤ {t.HeavyGuardBreakSeconds:0.###} s" );
			}

			LowerBlock();
		}

		// 7. A parried heavy locks the attacker out for the class parried-heavy time.
		await Reset( close, defenderFacesAttacker: true );
		{
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.05f, 2f );
			ReleaseHeavy();   // strikes at the heavy windup
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds - _defender.BlockRaiseSeconds, 2f );
			RaiseBlock( Time.NowDouble );
			await WaitSwingDone();
			var endedAt = Time.NowDouble;
			var hit = _attacker.LastMeleeHitResult;
			Check( "a heavy can be parried", hit.WasBlocked && hit.WasParried, $"blocked {hit.WasBlocked}, parried {hit.WasParried}" );
			await AssertLockout( "parried heavy", t.RecoveryParriedHeavySeconds, endedAt, slack );
			LowerBlock();
		}

		// 8. Right click (guard up) cancels a heavy that is being held: no strike, no damage.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.1f, 2f );
			_attacker.TestSubmitBlockIntent( true, Time.NowDouble, _attackerGo.WorldRotation.Angles().yaw );
			Check( "right click cancels a held heavy", !_attacker.ServerHasActiveMeleeAttackAction, "swing still active after the guard went up" );
			await Task.DelaySeconds( t.HeavyWindupSeconds + t.ActiveSeconds );
			Check( "a cancelled heavy deals no damage", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			_attacker.TestSubmitBlockIntent( false, 0d, _attackerGo.WorldRotation.Angles().yaw );
		}

		// 9. Turning: free through the windup (the swing aims where the player faces), slowed but unlimited during the
		//    strike — either way the arc turns with the player.
		await Reset( close, defenderFacesAttacker: true, defenderBearingDegrees: 120f );
		{
			var hp = _defenderVitals.CurrentHealth;
			Swing();
			await WaitSwingDone();
			Check( "a target behind the shoulder is out of the unturned arc", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender at 120° lost {hp - _defenderVitals.CurrentHealth:0.##} HP without a turn" );
		}

		await Reset( close, defenderFacesAttacker: true, defenderBearingDegrees: 120f );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing();
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds * 0.5f, 2f );
			await TurnAttackerLook( 90f );
			var liveYaw = _attacker.GetMeleeCombatBasisYaw( MeleeAttackTypes.Right );
			await WaitSwingDone();
			Check( "turning during the windup aims the swing", MathF.Abs( hp - _defenderVitals.CurrentHealth - _attacker.GetMeleeDamage() ) < 0.51f,
				$"turned 90° left in the windup: defender at 120° lost {hp - _defenderVitals.CurrentHealth:0.##} HP, want {_attacker.GetMeleeDamage():0.##} (swing yaw {liveYaw:0.#}°)" );
		}

		await Reset( close, defenderFacesAttacker: true, defenderBearingDegrees: 120f );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing();
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.01f, 2f );
			await TurnAttackerLook( 120f );
			await WaitSwingDone();
			Check( "turning during the strike carries the arc round", MathF.Abs( hp - _defenderVitals.CurrentHealth - _attacker.GetMeleeDamage() ) < 0.51f,
				$"turned 120° left during the swoop: defender at 120° lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
		}

		SetAttackerLookYaw( 0f );

		// 10. A hit never breaks an attack (Mark 2026-11-05): a swing hit in its windup, held as a heavy or in its damage
		//     swoop plays on and lands. The defender swings at the attacker; the attacker's "hit" is direct damage.
		await Reset( close, defenderFacesAttacker: true );
		{
			var attackerHp = _attackerVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing( _defender, _defenderGo );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds * 0.5f, 2f );
			_defenderVitals.ApplyDamageAfterArmor( 5f, _attacker );
			Check( "a hit during the windup does not cancel the swing", _defender.ServerHasActiveMeleeAttackAction,
				"defender's swing dropped after being hit in its windup" );
			await Task.DelaySeconds( t.WindupSeconds + t.ActiveSeconds );
			var dealt = attackerHp - _attackerVitals.CurrentHealth;
			Check( "a swing hit in its windup still lands", MathF.Abs( dealt - _defender.GetMeleeDamage() ) < 0.51f,
				$"attacker lost {dealt:0.##} HP, want {_defender.GetMeleeDamage():0.##}" );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var attackerHp = _attackerVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing( _defender, _defenderGo, heavy: true );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.15f, 2f );
			_defenderVitals.ApplyDamageAfterArmor( 5f, _attacker );
			Check( "a hit does not cancel a held heavy", _defender.ServerHasActiveMeleeAttackAction,
				"defender's held heavy dropped after being hit" );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds + 0.05f, 2f );
			_defender.HostNotePrimaryAttackReleased( _intentSequence );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 2f );
			var dealt = attackerHp - _attackerVitals.CurrentHealth;
			Check( "a heavy hit while held still lands", dealt > _defender.GetMeleeDamage() + 0.5f,
				$"attacker lost {dealt:0.##} HP, want a heavy (> {_defender.GetMeleeDamage():0.##})" );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var attackerHp = _attackerVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			Swing( _defender, _defenderGo );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + 0.02f, 2f );
			var activeWhenHit = _defender.ServerHasActiveMeleeAttackAction;
			_defenderVitals.ApplyDamageAfterArmor( 5f, _attacker );
			await Task.DelaySeconds( t.ActiveSeconds + 0.1f );
			Check( "a hit during the damage swoop does not cancel it",
				!activeWhenHit || attackerHp - _attackerVitals.CurrentHealth > 0.5f,
				$"swing active when hit {activeWhenHit}, attacker lost {attackerHp - _attackerVitals.CurrentHealth:0.##} HP" );
		}

		// 11. Punch (F): lands PunchWindupSeconds after the click, locks the puncher for windup + lock (no spam), breaks a
		//     guard, never cancels the target's attack and is never cancelled by a hit during its own windup.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var startedAt = Time.NowDouble;
			var thrown = _attacker.TestServerPunch();
			Check( "punch thrown", thrown, "TestServerPunch refused" );
			var again = _attacker.TestServerPunch();
			Check( "punch cannot be spammed", !again, "a second punch was accepted during the first" );
			await WaitUntil( () => Time.NowDouble - startedAt >= _attacker.PunchWindupSeconds - slack, 2f );
			Check( "punch does not land before its windup", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP {Time.NowDouble - startedAt:0.###} s after the punch" );
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, 1f );
			var landed = (float)(Time.NowDouble - startedAt);
			Check( "punch lands after its windup", MathF.Abs( hp - _defenderVitals.CurrentHealth - _attacker.ShoveDamage ) < 0.51f
				&& landed <= _attacker.PunchWindupSeconds + slack,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP at {landed:0.###} s (want {_attacker.ShoveDamage} at {_attacker.PunchWindupSeconds} s)" );
			await WaitUntil( () => !_attacker.IsCombatActionLocked, 2f );
			var lockLen = (float)(Time.NowDouble - startedAt);
			var wantLock = _attacker.PunchWindupSeconds + _attacker.RecoveryShoveCombatLockSeconds;
			Check( "punch lockout lasts windup + lock", lockLen >= wantLock - slack && lockLen <= wantLock + slack,
				$"locked {lockLen:0.###} s, want {wantLock:0.###} s" );
			if ( _attacker.PunchCooldownSeconds > lockLen + slack )
				Check( "no second punch inside the cooldown", !_attacker.TestServerPunch(),
					$"a punch was accepted {Time.NowDouble - startedAt:0.###} s after the last (cooldown {_attacker.PunchCooldownSeconds} s)" );
		}

		// The punch buys initiative, not a free hit: the puncher acts first, but a light swing it throws the moment it
		// is free can still be blocked by the punched pawn guarding the moment its stagger ends.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			_attacker.TestServerPunch();
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, 2f );
			Check( "the punched pawn is staggered", _defender.IsHitReactionActive, "defender not in hit reaction after the punch" );
			await WaitUntil( () => !_attacker.IsCombatActionLocked, 2f );
			Check( "the puncher acts before the punched pawn", _defender.IsHitReactionActive,
				"the punched pawn was free before the puncher" );
			Swing();
			await WaitUntil( () => !_defender.IsHitReactionActive, 2f );
			RaiseBlock( Time.NowDouble );
			await WaitSwingDone();
			var hit = _attacker.LastMeleeHitResult;
			Check( "the punched pawn can block the follow-up swing", hit.WasBlocked,
				$"follow-up swing blocked {hit.WasBlocked} (guard raised the moment the stagger ended)" );
			LowerBlock();
			await WaitUntil( () => _attacker.PunchCooldownRemaining <= 0f, 3f );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var attackerHp = _attackerVitals.CurrentHealth;
			Swing( _defender, _defenderGo );
			_attacker.TestServerPunch();
			await Task.DelaySeconds( _attacker.PunchWindupSeconds + slack );
			Check( "a punch does not cancel the opponent's windup", _defender.ServerHasActiveMeleeAttackAction,
				"defender's swing was dropped by the punch" );
			await Task.DelaySeconds( t.WindupSeconds + t.ActiveSeconds );
			var dealt = attackerHp - _attackerVitals.CurrentHealth;
			Check( "the punched pawn's swing still lands", MathF.Abs( dealt - _defender.GetMeleeDamage() ) < 0.51f,
				$"attacker lost {dealt:0.##} HP, want {_defender.GetMeleeDamage():0.##}" );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( Time.NowDouble );
			await Task.DelaySeconds( _defender.BlockRaiseSeconds + 0.1f );
			_attacker.TestServerPunch();
			await Task.DelaySeconds( _attacker.PunchWindupSeconds + slack );
			Check( "a punch breaks the guard", !_defender.IsAuthoritativeMeleeBlocking, "defender still blocking after the punch" );
			LowerBlock();
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			_attacker.TestServerPunch();
			await Task.Frame();
			_attackerVitals.ApplyDamageAfterArmor( 5f, _defender );
			await Task.DelaySeconds( _attacker.PunchWindupSeconds + slack );
			Check( "a hit during the punch windup does not cancel the punch",
				MathF.Abs( hp - _defenderVitals.CurrentHealth - _attacker.ShoveDamage ) < 0.51f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP, want {_attacker.ShoveDamage}" );
		}


		// 12. Guard up but the attacker is behind: the back arc is open.
		await Reset( close, defenderFacesAttacker: false );
		{
			RaiseBlock( Time.NowDouble );
			await Task.DelaySeconds( _defender.BlockRaiseSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			Swing();
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "hit from behind ignores the guard",
				!_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( dealt - _attacker.GetMeleeDamage() ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, dealt {dealt:0.##}" );
			LowerBlock();
		}
	}

	/// <summary>Attacker is locked out right after a blocked / parried swing and free again once the class lockout ran.</summary>
	async System.Threading.Tasks.Task AssertLockout( string what, float lockoutSeconds, double endedAt, float slack )
	{
		if ( lockoutSeconds > slack )
			Check( $"{what} swing locks the attacker out", _attacker.IsCombatActionLocked,
				$"attacker not combat-locked after a {what} swing (lockout {lockoutSeconds:0.###} s)" );

		await WaitUntil( () => !_attacker.IsCombatActionLocked, 2f );
		var locked = (float)(Time.NowDouble - endedAt);
		Check( $"{what} lockout lasts the class time", locked <= lockoutSeconds + slack,
			$"locked {locked:0.###} s, want ≤ {lockoutSeconds:0.###} s" );
	}

	/// <summary>Place both pawns (attacker at the origin facing +X), refill pools, wait out any lock.</summary>
	async System.Threading.Tasks.Task Reset( float distance, bool defenderFacesAttacker, float defenderBearingDegrees = 0f )
	{
		LowerBlock();
		await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction && !_attacker.IsCombatActionLocked
		                       && !_defender.ServerHasActiveMeleeAttackAction && !_defender.IsCombatActionLocked
		                       && _attacker.PunchCooldownRemaining <= 0f && _defender.PunchCooldownRemaining <= 0f, 4f );

		var inputFree = !_attackerGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ).Enabled
		                && !_defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ).Enabled;
		if ( !inputFree )
		{
			Fail( "test pawns ignore input", "a PlayerController came back on — the pawn would read real input; re-stripping" );
			MakeScripted( _attackerGo );
			MakeScripted( _defenderGo );
		}

		_attackerVitals.HostArenaRestoreAndTeleport( ArenaOrigin, Rotation.FromYaw( 0f ) );
		// Bearing: 0 = straight ahead of the attacker (+X), +90 = its left.
		var bearing = Rotation.FromYaw( defenderBearingDegrees );
		_defenderVitals.HostArenaRestoreAndTeleport( ArenaOrigin + bearing.Forward * distance,
			Rotation.FromYaw( defenderBearingDegrees + (defenderFacesAttacker ? 180f : 0f) ) );
		SetAttackerLookYaw( 0f );
		await Task.Frame();
	}

	void RaiseBlock( double pressedAt ) =>
		_defender.TestSubmitBlockIntent( true, pressedAt, _defenderGo.WorldRotation.Angles().yaw );

	void LowerBlock()
	{
		if ( _defender.IsValid() )
			_defender.TestSubmitBlockIntent( false, 0d, _defenderGo.WorldRotation.Angles().yaw );
	}

	AttackReleaseResult Swing( float claimedHoldSeconds = 0f, bool heavy = false, byte dir = SwingDirs.Right,
		byte comboStep = 0, bool special = false ) =>
		Swing( _attacker, _attackerGo, claimedHoldSeconds, heavy, dir, comboStep, special );

	/// <summary>The attacker's look yaw, as the synced PlayerController.EyeAngles the host reads for a remote owner.</summary>
	void SetAttackerLookYaw( float yawDegrees )
	{
		if ( _attackerGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is { } controller )
			controller.EyeAngles = new Angles( 0f, yawDegrees, 0f );
	}

	/// <summary>Turn the attacker's look to <paramref name="toYawDegrees"/> 30° per frame, like a fast mouse turn (no single-frame wrap).</summary>
	async System.Threading.Tasks.Task TurnAttackerLook( float toYawDegrees )
	{
		if ( _attackerGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is not { } controller )
			return;

		var yaw = controller.EyeAngles.yaw;
		while ( MathF.Abs( toYawDegrees - yaw ) > 0.01f )
		{
			yaw += Math.Clamp( toYawDegrees - yaw, -30f, 30f );
			controller.EyeAngles = new Angles( 0f, yaw, 0f );
			await Task.Frame();
		}
	}

	/// <summary>The attacker lets go of the button of its last swing (a held heavy strikes now).</summary>
	void ReleaseHeavy() => _attacker.HostNotePrimaryAttackReleased( _intentSequence );

	/// <summary>
	/// A right swing straight along the pawn's facing, submitted to the host as a click intent.
	/// <paramref name="claimedHoldSeconds"/> back-dates the press the way a cheating client could — the host must ignore it.
	/// The button is let go at once (a light) unless <paramref name="heavy"/>: then no release reaches the host until
	/// <see cref="ReleaseHeavy"/>, exactly like a player holding through the windup.
	/// </summary>
	AttackReleaseResult Swing( PlayerCombat pc, GameObject go, float claimedHoldSeconds = 0f, bool heavy = false,
		byte dir = SwingDirs.Right, byte comboStep = 0, bool special = false )
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
			PressedGlobalSeconds = now - claimedHoldSeconds,
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
			SwingDir = dir,
			AttackType = special ? MeleeAttackTypes.Stab : pc.ResolveAttackTypeFromCursorDir( dir ),
			CombatBasisYawDegrees = angles.yaw,
			CombatBasisPitchDegrees = 0f,
			ComboStep = comboStep,
			IsSpecial = special
		};

		var result = CombatAuthority.Instance.ValidateAndApplyPrimaryMelee( go, intent );
		if ( !heavy )
			pc.HostNotePrimaryAttackReleased( intent.IntentSequence );
		return result;
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
