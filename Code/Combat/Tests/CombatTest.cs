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
/// player's swing takes. Expected numbers are read from the live tuning (weapon class, exchange rules, block
/// outcomes), so retuning never fails it; broken behaviour does. Covers the directional exchange (Mark 2026-11-14):
/// windup / release / recovery, chaining + combo windups + combo end, input buffer, feint, directional block and its
/// counter window, heavy charge, blocked-heavy stagger, hit interrupts, stagger protection, chamber, turn rate,
/// exhaustion, and the punch.
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

		Check( "only the release deals damage",
			MeleeAttackStates.DealsDamage( MeleeAttackStates.Active )
			&& !MeleeAttackStates.DealsDamage( MeleeAttackStates.Windup )
			&& !MeleeAttackStates.DealsDamage( MeleeAttackStates.Recovery ),
			"windup / recovery must not deal damage" );

		var t = MeleeWeaponClassCatalog.Resolve( MeleeWeaponClassCatalog.DefaultClassId );
		Check( "default class timings sane",
			t.WindupSeconds > 0f && t.ComboWindupSeconds > 0f && t.ActiveSeconds > 0f && t.RecoverySeconds > 0f
			&& t.ComboEndRecoverySeconds >= t.RecoverySeconds && t.HeavyWindupSeconds >= t.WindupSeconds
			&& t.HeavyDamageMultiplier > 1f && t.HitStaggerSeconds > 0f && t.HeavyHitStaggerSeconds >= t.HitStaggerSeconds
			&& t.ReachForwardMeters > 0f && t.StaminaHeavyCost >= t.StaminaCost,
			$"{t.ClassId}: windup {t.WindupSeconds} combo {t.ComboWindupSeconds} release {t.ActiveSeconds} recovery {t.RecoverySeconds} "
			+ $"combo end {t.ComboEndRecoverySeconds} heavy {t.HeavyWindupSeconds} x{t.HeavyDamageMultiplier}" );

		Check( "a blocked heavy keeps the attacker's initiative",
			t.RecoveryBlockedHeavySeconds < t.HeavyBlockStaggerSeconds,
			$"blocked heavy recovery {t.RecoveryBlockedHeavySeconds} s must be shorter than the blocker's stagger {t.HeavyBlockStaggerSeconds} s" );

		var block = MeleeBlockStaggerCatalog.Resolve();
		var chamber = MeleeBlockStaggerCatalog.Chamber();
		Check( "a correct block prevents damage; a chamber costs nothing",
			block.HealthDamage <= 0f && block.StaminaCost > 0f && chamber.WasChambered && chamber.StaminaCost <= 0f && chamber.HealthDamage <= 0f,
			$"block row {block.HealthDamage} HP / {block.StaminaCost} st; chamber {chamber.HealthDamage} HP / {chamber.StaminaCost} st" );

		// Incoming angle: defender at origin facing +X (yaw 0); +Y is the defender's left in s&box.
		var front = MeleeBlockResolution.ComputeIncomingHitAngleDegrees( Vector3.Zero, 0f, new Vector3( 100f, 0f, 0f ) );
		var back = MeleeBlockResolution.ComputeIncomingHitAngleDegrees( Vector3.Zero, 0f, new Vector3( -100f, 0f, 0f ) );
		var side = MeleeBlockResolution.ComputeIncomingHitAngleDegrees( Vector3.Zero, 90f, new Vector3( 0f, 100f, 0f ) );
		Check( "incoming angle front / back / turned",
			MathF.Abs( front ) < 1f && MathF.Abs( back ) > 179f && MathF.Abs( side ) < 1f,
			$"front {front:0.#}° (want 0), back {back:0.#}° (want ±180), straight ahead of a defender facing +Y {side:0.#}° (want 0)" );
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

		// A scripted pawn must not load or overwrite the player's saved hotbar bindings.
		if ( go.Components.Get<PlayerHotbar>() is { } hotbar )
			hotbar.UsesSavedBindings = false;

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

	/// <summary>The cursor direction that throws <paramref name="attackType"/> for this pawn (southpaw-aware).</summary>
	static byte DirForAttackType( PlayerCombat pc, byte attackType )
	{
		foreach ( var d in new[] { SwingDirs.Left, SwingDirs.Right, SwingDirs.Up } )
			if ( pc.ResolveAttackTypeFromCursorDir( d ) == attackType )
				return d;
		return SwingDirs.Up;
	}

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
		var light = _attacker.GetMeleeDamage();
		var fullSwing = t.WindupSeconds + t.ActiveSeconds + t.RecoverySeconds;

		// 1. Hit on an open defender. The intent claims a 1 s hold: the host must still play the full windup.
		await Reset( close, defenderFacesAttacker: true );
		double contactDelay;
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var wear = AttackerWear;
			var cues = _attacker.TestCueCount;
			var startedAt = Time.NowDouble;
			var res = Swing( claimedHoldSeconds: 1f );
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "swing accepted", res.Accepted && res.DebugCode == AttackReleaseDebugCode.OkMeleeSweepStarted,
				$"code {res.DebugCode}: {res.DebugDetail}" );
			Check( "swing spends class stamina", MathF.Abs( stSpent - t.StaminaCost ) < 0.51f,
				$"spent {stSpent:0.##}, want {t.StaminaCost:0.##}" );
			Check( "a swing from neutral winds up the class windup", MathF.Abs( _attacker.TestSwingWindupSeconds - t.WindupSeconds ) < 1e-3f,
				$"windup {_attacker.TestSwingWindupSeconds:0.###} s, want {t.WindupSeconds:0.###} s" );

			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds - slack, 2f );
			Check( "no damage during the windup (a claimed hold never shortens it)",
				MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP {Time.NowDouble - startedAt:0.###} s after the click (windup {t.WindupSeconds} s)" );

			var rot = _attackerGo.WorldRotation;
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, t.ActiveSeconds + 1f );
			contactDelay = Time.NowDouble - startedAt;
			Check( "a hit on a player sounds only the player-hit thud",
				_attacker.TestCueCount == cues + 1 && _attacker.TestLastCue == CombatAudioCue.HitPlayer,
				$"{_attacker.TestCueCount - cues} cues, last {_attacker.TestLastCue}, want 1: {CombatAudioCue.HitPlayer}" );
			Check( "victim is staggered", _defender.IsHitReactionActive, "defender not in hit reaction after a clean hit" );
			var victimSwing = Swing( _defender, _defenderGo );
			Check( "a staggered victim cannot start a swing now",
				_defender.TestSwingPhase == MeleeSwingPhase.None,
				$"victim swing during the stagger: code {victimSwing.DebugCode}: {victimSwing.DebugDetail}, phase {_defender.TestSwingPhase}" );
			var reactionStart = Time.NowDouble;
			await WaitUntil( () => !_defender.IsHitReactionActive, 2f );
			var reactionLen = (float)(Time.NowDouble - reactionStart);
			Check( "a light hit staggers for the class hit stagger", reactionLen <= Math.Max( 0.1f, t.HitStaggerSeconds ) + slack,
				$"stagger lasted {reactionLen:0.###} s, want ≤ {t.HitStaggerSeconds:0.###} s" );

			await WaitUntil( () => _attacker.TestSwingPhase == MeleeSwingPhase.Recovery, 2f );
			Check( "a landed hit still recovers", _attacker.TestSwingPhase == MeleeSwingPhase.Recovery && !_attacker.IsAuthoritativeMeleeBlocking,
				$"phase {_attacker.TestSwingPhase} after the release" );
			await WaitSwingDone();
			var swingLen = (float)(Time.NowDouble - startedAt);
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "hit deals weapon damage", MathF.Abs( dealt - light ) < 0.51f, $"dealt {dealt:0.##}, want {light:0.##}" );
			Check( "hit not flagged blocked", !_attacker.LastMeleeHitResult.WasBlocked, "open defender reported as blocked" );
			Check( "a swing lasts windup + release + recovery", MathF.Abs( swingLen - fullSwing ) <= slack * 2f,
				$"swing ended {swingLen:0.###} s after the click, want {fullSwing:0.###} s" );
			Check( "a hit wears the weapon once", AttackerWear == wear + 1, $"wear {wear} → {AttackerWear}, want +1" );
			Check( "combat never rotates the pawn root", rot.Distance( _attackerGo.WorldRotation ) < 0.01f,
				$"root yaw moved {rot.Angles().yaw:0.#}° → {_attackerGo.WorldRotation.Angles().yaw:0.#}°" );
		}

		// 2. Out of reach = miss; a click early in the windup is not chainable and expires from the input buffer.
		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var wear = AttackerWear;
			var cues = _attacker.TestCueCount;
			Swing();
			await Task.Frame();
			var early = Swing( dir: SwingDirs.Left );
			Check( "an early click is held, not fired", early.Accepted && _attacker.TestComboStep == 1,
				$"code {early.DebugCode}: {early.DebugDetail}, combo step {_attacker.TestComboStep}" );
			await WaitSwingDone();
			await Task.DelaySeconds( _attacker.AttackInputBufferSeconds + _attacker.HostLatencySlackSeconds + 0.05f );
			Check( "a click outside the chain window expires from the buffer", !_attacker.ServerHasActiveMeleeAttackAction,
				"a swing started from a click made early in the previous windup" );
			Check( "a miss does not wear the weapon", AttackerWear == wear, $"wear {wear} → {AttackerWear} on a miss" );
			Check( "a miss is silent (swoosh removed, Mark 2026-11-16)", _attacker.TestCueCount == cues,
				$"{_attacker.TestCueCount - cues} cues, last {_attacker.TestLastCue}" );
			Check( "out of reach misses", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP at {reach * 1.8f + 40f:0} u (reach {reach:0} u)" );
		}

		// 3. Combo: right → left → overhead chained (end of the release, then mid-recovery) — each chained swing winds up
		//    when the previous recovery (the follow-through) ends, the direction-switched windups are the combo windup, and
		//    the third swing ends in the combo-end recovery (Mark 2026-11-14, Last Oasis footage).
		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var dirs = new[] { SwingDirs.Right, SwingDirs.Left, SwingDirs.Up };
			Swing( dir: dirs[0], comboStep: 1 );
			var windups = new List<float> { _attacker.TestSwingWindupSeconds };
			var chainedOk = true;
			var gapNote = "";
			var gapOk = true;
			var startedAt = Time.NowDouble;
			for ( var step = 1; step < dirs.Length; step++ )
			{
				var windup = _attacker.TestSwingWindupSeconds;
				var recoveryEnd = windup + t.ActiveSeconds + t.RecoverySeconds;
				// Step 2 clicks at the end of the release, step 3 halfway through the recovery: both chain.
				var clickAt = step == 1
					? windup + t.ActiveSeconds - _attacker.ChainInputWindowSeconds * 0.5f
					: windup + t.ActiveSeconds + t.RecoverySeconds * 0.5f;
				var from = startedAt;
				await WaitUntil( () => Time.NowDouble - from >= clickAt, 2f );
				Swing( dir: dirs[step], comboStep: (byte)(step + 1) );
				await WaitUntil( () => _attacker.TestComboStep == step + 1, 2f );
				var nextStart = (float)(Time.NowDouble - from);
				startedAt = Time.NowDouble;
				chainedOk &= _attacker.TestComboStep == step + 1 && _attacker.TestSwingPhase == MeleeSwingPhase.Windup;
				gapOk &= MathF.Abs( nextStart - recoveryEnd ) <= slack;
				gapNote += $" step {step + 1} at {nextStart:0.###} s (want {recoveryEnd:0.###})";
				windups.Add( _attacker.TestSwingWindupSeconds );
			}

			Check( "three swings chain (end-of-release and mid-recovery clicks)", chainedOk, $"combo step {_attacker.TestComboStep}" );
			Check( "a chained swing winds up when the follow-through ends", gapOk, gapNote.Trim() );
			Check( "chained swings wind up the combo windup",
				windups.Count == 3 && MathF.Abs( windups[0] - t.WindupSeconds ) < 1e-3f
				&& MathF.Abs( windups[1] - t.ComboWindupSeconds ) < 1e-3f && MathF.Abs( windups[2] - t.ComboWindupSeconds ) < 1e-3f,
				$"windups {string.Join( " → ", windups.ConvertAll( w => $"{w:0.###}" ) )} s, want {t.WindupSeconds} → {t.ComboWindupSeconds} → {t.ComboWindupSeconds}" );

			// A fourth chained click is refused by the combo cap: the third swing recovers for the combo end first.
			await WaitUntil( () => _attacker.TestSwingPhase == MeleeSwingPhase.Release, 2f );
			Swing( dir: SwingDirs.Right, comboStep: 4 );
			await WaitUntil( () => _attacker.TestSwingPhase == MeleeSwingPhase.Recovery, 2f );
			var recoveryAt = Time.NowDouble;
			Check( "the third swing cannot chain (combo cap)", _attacker.TestComboStep == 3 && _attacker.TestSwingPhase == MeleeSwingPhase.Recovery,
				$"combo step {_attacker.TestComboStep}, phase {_attacker.TestSwingPhase}" );
			await WaitUntil( () => _attacker.TestSwingPhase != MeleeSwingPhase.Recovery, 2f );
			var rec = (float)(Time.NowDouble - recoveryAt);
			Check( "the combo ends in the combo-end recovery", MathF.Abs( rec - t.ComboEndRecoverySeconds ) <= slack,
				$"recovery {rec:0.###} s, want {t.ComboEndRecoverySeconds:0.###} s" );
			await WaitSwingDone();
		}

		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var startedAt = Time.NowDouble;
			Swing( dir: SwingDirs.Right, comboStep: 1 );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds + t.ActiveSeconds - _attacker.ChainInputWindowSeconds * 0.5f, 2f );
			Swing( dir: SwingDirs.Right, comboStep: 2 );
			await WaitUntil( () => _attacker.TestComboStep == 2, 2f );
			Check( "a same-direction chain winds up the combo windup too", MathF.Abs( _attacker.TestSwingWindupSeconds - t.ComboWindupSeconds ) < 1e-3f,
				$"windup {_attacker.TestSwingWindupSeconds:0.###} s, want {t.ComboWindupSeconds:0.###} s" );
			await WaitSwingDone();
		}

		// 4. Feint: the guard button in the early windup cancels the swing (stamina, short attack lock); in the release it cannot.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var seq = (ushort)(_intentSequence + 1);
			Swing();
			var feinted = _attacker.HostTryFeint( seq );
			Check( "a feint early in the windup cancels the swing", feinted && !_attacker.ServerHasActiveMeleeAttackAction,
				$"feint {feinted}, swing active {_attacker.ServerHasActiveMeleeAttackAction}" );
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "a feint costs the swing plus the feint stamina", MathF.Abs( stSpent - (t.StaminaCost + _attacker.FeintStaminaCost) ) < 0.51f,
				$"spent {stSpent:0.##}, want {t.StaminaCost + _attacker.FeintStaminaCost:0.##}" );
			Check( "a feint locks attacks briefly", _attacker.IsAttackLocked, "attacker not attack-locked after the feint" );
			var lockedAt = Time.NowDouble;
			await WaitUntil( () => !_attacker.IsAttackLocked, 2f );
			Check( "the feint lock lasts the feint recovery", Time.NowDouble - lockedAt <= _attacker.FeintRecoverySeconds + slack,
				$"locked {Time.NowDouble - lockedAt:0.###} s, want ≤ {_attacker.FeintRecoverySeconds:0.###} s" );
			await Task.DelaySeconds( t.WindupSeconds + t.ActiveSeconds );
			Check( "a feinted swing deals no damage", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );

			var startedAt = Time.NowDouble;
			seq = (ushort)(_intentSequence + 1);
			Swing();
			await WaitUntil( () => _attacker.TestSwingPhase == MeleeSwingPhase.Release, 2f );
			Check( "no feint once the release started", !_attacker.HostTryFeint( seq ) && _attacker.ServerHasActiveMeleeAttackAction,
				$"swing cancelled {Time.NowDouble - startedAt:0.###} s after the click" );
			await WaitSwingDone();
		}

		// 4b. Still holding the attack button, the guard ditches the swing at any point before it turns heavy (Mark
		//     2026-11-16); a held heavy cannot be feinted - the guard button morphs it instead.
		await Reset( close, defenderFacesAttacker: true );
		{
			var seq = (ushort)(_intentSequence + 1);
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			var lateAt = t.WindupSeconds * 0.9f;
			await WaitUntil( () => Time.NowDouble - startedAt >= lateAt, 2f );
			var feinted = _attacker.HostTryFeint( seq );
			Check( "a light still held is feinted late in the windup", feinted && !_attacker.ServerHasActiveMeleeAttackAction,
				$"feint {feinted} {Time.NowDouble - startedAt:0.###} s after the click (windup {t.WindupSeconds} s)" );
			await WaitUntil( () => !_attacker.IsAttackLocked, 2f );

			seq = (ushort)(_intentSequence + 1);
			Swing( heavy: true );
			await WaitUntil( () => _attacker.TestSwingHeavyHolding, 2f );
			await Task.DelaySeconds( _attacker.HostLatencySlackSeconds + 0.05f );
			Check( "a held heavy is not feinted (the guard morphs it)", !_attacker.HostTryFeint( seq ) && _attacker.TestSwingHeavyHolding,
				$"heavy holding {_attacker.TestSwingHeavyHolding}, phase {_attacker.TestSwingPhase}" );
			ReleaseHeavy();
			await WaitSwingDone();
		}

		// 5. Directional block: the guard on the side the blade comes in from stops it (no damage, block stamina, the
		//    guard breaks and needs a fresh press (Mark 2026-11-16), the attacker recovers for the blocked recovery, the
		//    blocker gets the counter window).
		await Reset( close, defenderFacesAttacker: true );
		{
			var incoming = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
			var incomingLeft = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Left );
			var incomingUp = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Forward );
			Check( "left and right swings come in on opposite sides; the overhead from above",
				incoming != incomingLeft && incoming is SwingDirs.Left or SwingDirs.Right && incomingUp == SwingDirs.Up,
				$"right swing → {SwingDirs.Letter( incoming )}, left swing → {SwingDirs.Letter( incomingLeft )}, overhead → {SwingDirs.Letter( incomingUp )}" );

			RaiseBlock( incoming );
			Check( "guard needs the activation time before it blocks",
				MathF.Abs( (float)(_defender.ServerBlockActiveFromSandbox - _defender.ServerBlockStartedAtSandbox) - _defender.BlockActivationSeconds ) < 1e-3f,
				$"active-from − start = {_defender.ServerBlockActiveFromSandbox - _defender.ServerBlockStartedAtSandbox:0.###} s, want {_defender.BlockActivationSeconds:0.###} s" );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			Check( "guard is up", _defender.IsAuthoritativeMeleeBlocking,
				$"defender block did not register on the host (combat enabled {_defender.Enabled}, active {_defender.Active})" );
			var defenderSwing = Swing( _defender, _defenderGo );
			Check( "blocking pawn cannot swing",
				!defenderSwing.Accepted && defenderSwing.DebugCode == AttackReleaseDebugCode.RejectMeleeBusy,
				$"code {defenderSwing.DebugCode}: {defenderSwing.DebugDetail}" );

			var hp = _defenderVitals.CurrentHealth;
			var st = _defenderVitals.CurrentStamina;
			Swing( dir: SwingDirs.Right );
			await WaitUntil( () => _attacker.LastMeleeHitResult.WasBlocked || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			var endedAt = Time.NowDouble;
			var outcome = MeleeBlockStaggerCatalog.Resolve();
			var hit = _attacker.LastMeleeHitResult;
			Check( "the correct guard blocks", hit.WasBlocked && !hit.WasChambered, $"blocked {hit.WasBlocked}, chambered {hit.WasChambered}" );
			Check( "the block clack plays through the attacker", _attacker.TestLastCue == CombatAudioCue.Blocked,
				$"attacker's last cue {_attacker.TestLastCue}" );
			Check( "a block costs the block row (no damage)",
				MathF.Abs( hp - _defenderVitals.CurrentHealth - outcome.HealthDamage ) < 0.51f
				&& MathF.Abs( st - _defenderVitals.CurrentStamina - outcome.StaminaCost ) < 1.01f,
				$"lost {hp - _defenderVitals.CurrentHealth:0.##} hp / {st - _defenderVitals.CurrentStamina:0.##} st, want {outcome.HealthDamage} / {outcome.StaminaCost}" );
			Check( "a block breaks the guard", !_defender.IsAuthoritativeMeleeBlocking, "the guard is still up after the block" );
			RaiseBlock( incoming );
			Check( "a broken guard stays down while block is still held", !_defender.IsAuthoritativeMeleeBlocking,
				"the guard came back without a fresh press" );
			await AssertLockout( "blocked", t.RecoveryBlockedSeconds, endedAt, slack );

			// Priority: the blocker drops the guard and counters with the counter windup inside the counter window.
			LowerBlock();
			await WaitUntil( () => !_defender.IsAttackLocked, 1f );
			Swing( _defender, _defenderGo );
			Check( "the blocker's counter winds up the counter windup", MathF.Abs( _defender.TestSwingWindupSeconds - t.CounterWindupSeconds ) < 1e-3f,
				$"counter windup {_defender.TestSwingWindupSeconds:0.###} s, want {t.CounterWindupSeconds:0.###} s" );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 3f );
		}

		// 5a. Initiative can be traded back (Mark 2026-11-17: a scav's counter after its block always landed - "from the
		//     moment the player knows the attack is coming to when it can damage me is just simply too fast"): the blocker
		//     counters the moment it may, the blocked attacker raises its guard a 0.15 s reaction after its own lock ends,
		//     and the counter is blocked.
		await Reset( close, defenderFacesAttacker: true );
		{
			const float reactionSeconds = 0.15f;
			RaiseBlock( MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right ) );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			// The last hit result still holds the previous case's block: wait for this swing's own result.
			var previousHit = _attacker.LastMeleeHitResult;
			var previousCounterHit = _defender.LastMeleeHitResult;
			// The attacker's own machine predicts the swing too (a real click); a held guard waits on that prediction.
			_attacker.TestBeginOwnerSwing( (ushort)(_intentSequence + 1) );
			Swing( dir: SwingDirs.Right );
			await WaitUntil( () => !_attacker.LastMeleeHitResult.Equals( previousHit ) || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			var blockedAt = Time.NowDouble;
			var attackBlocked = _attacker.LastMeleeHitResult.WasBlocked && !_attacker.LastMeleeHitResult.Equals( previousHit );
			// Mark 2026-11-17: "i hold block and it goes right through" - the owner's prediction ran on through the full
			// release + recovery and kept the held guard down; a block must end it.
			// The lockout (and with it the owner's notice) starts as the host finishes the stopped swing.
			await WaitUntil( () => _attacker.IsCombatActionLocked, 0.5f );
			Check( "a blocked swing ends the owner's predicted swing",
				attackBlocked && _attacker.IsCombatActionLocked && _attacker.TestOwnerSwingPhase == MeleeSwingPhase.None,
				$"blocked {attackBlocked}, locked {_attacker.IsCombatActionLocked}, owner still predicts {_attacker.TestOwnerSwingPhase} after its swing was blocked" );
			LowerBlock();
			await WaitUntil( () => !_defender.IsAttackLocked, 1f );
			var counter = Swing( _defender, _defenderGo, dir: SwingDirs.Right );
			var counterType = _defender.ResolveAttackTypeFromCursorDir( SwingDirs.Right );
			var attackerHp = _attackerVitals.CurrentHealth;
			var sawHitReaction = false;
			await WaitUntil( () =>
			{
				sawHitReaction |= _attacker.IsHitReactionActive;
				return !_attacker.ServerHasActiveMeleeAttackAction && !_attacker.IsCombatActionLocked;
			}, 2f );
			var freeAfter = (float)(Time.NowDouble - blockedAt);
			var counterPhaseAtFree = _defender.TestSwingPhase;
			await Task.DelaySeconds( reactionSeconds );
			_attacker.TestSubmitBlockIntent( true, _attackerGo.WorldRotation.Angles().yaw,
				MeleeBlockResolution.ResolveIncomingSide( _attacker, _defenderGo, counterType ) );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 3f );
			Check( "a blocked attacker can guard the blocker's counter",
				attackBlocked && counter.Accepted && !_defender.LastMeleeHitResult.Equals( previousCounterHit ) && _defender.LastMeleeHitResult.WasBlocked
				&& attackerHp - _attackerVitals.CurrentHealth < 0.51f,
				$"attack blocked {attackBlocked}, counter accepted {counter.Accepted}, blocked {_defender.LastMeleeHitResult.WasBlocked}, attacker lost {attackerHp - _attackerVitals.CurrentHealth:0.##} HP; "
				+ $"attacker free {freeAfter:0.###} s after the block (+{reactionSeconds} s reaction, {_attacker.BlockActivationSeconds} s guard rise), "
				+ $"counter windup {t.CounterWindupSeconds:0.###} s after a {_defender.LightBlockReactionSeconds:0.###} s block reaction; "
				+ $"attacker staggered {sawHitReaction}, counter phase when free {counterPhaseAtFree}, counter hit {_defender.LastMeleeHitResult.DamageApplied:0.#} dmg on {_defender.LastMeleeHitResult.TargetsHitCount}" );
			_attacker.TestSubmitBlockIntent( false, _attackerGo.WorldRotation.Angles().yaw );
		}

		// 5b. The guard side is locked when the windup starts (Mark 2026-11-17: the red arrow "frequently flips to the other
		//     side last second"; he chose "lock side per swing"): the defender turns 40° mid-windup - far enough that the
		//     flank rule alone would now ask for the other side - and the side latched at the start still blocks.
		await Reset( close, defenderFacesAttacker: true );
		{
			Swing( dir: SwingDirs.Right );
			await Task.Frame();
			var latched = MeleeBlockResolution.ResolveIncomingSideLatched( _defender, _attackerGo, MeleeAttackTypes.Right );
			var rootYaw = _defenderGo.WorldRotation.Angles().yaw;
			var turnedYaw = rootYaw;
			var fresh = latched;
			foreach ( var turn in new[] { 40f, -40f } )
			{
				turnedYaw = rootYaw + turn;
				if ( _defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is { } dc )
					dc.EyeAngles = new Angles( 0f, turnedYaw, 0f );
				_defenderGo.Components.Get<PlayerMovement>( FindMode.EverythingInSelf )?.SnapBodyYawToLook();
				fresh = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
				if ( fresh != latched )
					break;
			}

			var hp = _defenderVitals.CurrentHealth;
			_defender.TestSubmitBlockIntent( true, turnedYaw, latched );
			var previousHit = _attacker.LastMeleeHitResult;
			await WaitUntil( () => !_attacker.LastMeleeHitResult.Equals( previousHit ) || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			Check( "the guard side is locked at the windup start",
				fresh != latched && _attacker.LastMeleeHitResult.WasBlocked && hp - _defenderVitals.CurrentHealth < 0.51f,
				$"latched {SwingDirs.Letter( latched )}, re-read after the turn {SwingDirs.Letter( fresh )}, blocked {_attacker.LastMeleeHitResult.WasBlocked}, "
				+ $"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			LowerBlock();
			await WaitSwingDone();
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var incoming = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
			var wrong = incoming == SwingDirs.Left ? SwingDirs.Right : SwingDirs.Left;
			RaiseBlock( wrong );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right );
			await WaitSwingDone();
			Check( "the wrong guard direction does not block",
				!_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth - light ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP (guard {SwingDirs.Letter( wrong )}, blade from {SwingDirs.Letter( incoming )})" );
			LowerBlock();
		}

		// 5b2. Block pivot (Mark 2026-11-18: "if the sword would hit dead center on the player and their sword physically
		//      blocks it because theyre doing the block pivot thing then it should block the attack"): the swing starts at
		//      a defender facing it, so the marker's side is locked; then the defender raises the OTHER side's guard and
		//      turns the body toward the blade, which carries the raised weapon into its way. The marker still calls the
		//      first side - the block is the weapon physically met (MeleeGuardGeometry).
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right );
			await Task.Frame();
			var called = MeleeBlockResolution.ResolveIncomingSideLatched( _defender, _attackerGo, MeleeAttackTypes.Right );
			var wrong = called == SwingDirs.Left ? SwingDirs.Right : SwingDirs.Left;
			var rootYaw = _defenderGo.WorldRotation.Angles().yaw;
			var turnedYaw = rootYaw;
			var pivotTurn = 0f;
			var pivotLog = "";
			foreach ( var turn in new[] { 20f, -20f, 30f, -30f, 40f, -40f } )
			{
				turnedYaw = rootYaw + turn;
				if ( _defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is { } dc )
					dc.EyeAngles = new Angles( 0f, turnedYaw, 0f );
				_defenderGo.Components.Get<PlayerMovement>( FindMode.EverythingInSelf )?.SnapBodyYawToLook();
				pivotLog += $" {turn:0}°:{MeleeGuardGeometry.ClosestCrossingDistance( _defender, wrong, _attacker, MeleeAttackTypes.Right ):0}u";
				if ( MeleeGuardGeometry.GuardInterceptsSwing( _defender, wrong, _attacker, MeleeAttackTypes.Right ) )
				{
					pivotTurn = turn;
					break;
				}
			}

			_defender.TestSubmitBlockIntent( true, turnedYaw, wrong );
			var previousHit = _attacker.LastMeleeHitResult;
			await WaitUntil( () => !_attacker.LastMeleeHitResult.Equals( previousHit ) || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			Check( "a guard turned into the blade during the swing blocks it (block pivot)",
				pivotTurn != 0f && called != wrong && _attacker.LastMeleeHitResult.WasBlocked && hp - _defenderVitals.CurrentHealth < 0.51f,
				$"marker side {SwingDirs.Letter( called )} locked at the windup start, guard {SwingDirs.Letter( wrong )} with the body turned {pivotTurn:0}° "
				+ $"(nearest the weapon came to the bar per turn:{pivotLog}; catch {_defender.GuardCatchRadiusMetersOrDefault * _defender.GetPawnUnitsPerMeter():0}u), "
				+ $"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			LowerBlock();
			await WaitSwingDone();
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( SwingDirs.Up );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Up );
			await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction || _attacker.LastMeleeHitResult.WasBlocked, 2f );
			Check( "an overhead guard blocks the overhead", _attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			await WaitSwingDone();
			LowerBlock();
		}

		// 5b3. The guarding weapon is a physical bar wherever it is (Mark 2026-11-23: "the block hitbox on an overhead isnt
		//      where the weapon is ... if a weapons arc were to collide with a weapon AS its going to its block position
		//      then it should count as a block"): the overhead guard has a bar over the head, and a guard on its way up
		//      stops a blade that runs into it - one pressed at the instant of contact does not.
		await Reset( close, defenderFacesAttacker: true );
		{
			var side = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
			var catchUnits = _defender.GuardCatchRadiusMetersOrDefault * _defender.GetPawnUnitsPerMeter();
			float Gap( byte guard, byte attack ) => MeleeGuardGeometry.ClosestCrossingDistance( _defender, guard, _attacker, attack );
			Check( "the overhead guard's bar is in an overhead's way, not in a side swing's",
				MeleeGuardGeometry.GuardInterceptsSwing( _defender, SwingDirs.Up, _attacker, MeleeAttackTypes.Forward )
				&& !MeleeGuardGeometry.GuardInterceptsSwing( _defender, SwingDirs.Up, _attacker, MeleeAttackTypes.Right )
				&& !MeleeGuardGeometry.GuardInterceptsSwing( _defender, SwingDirs.Up, _attacker, MeleeAttackTypes.Left ),
				$"overhead bar to the weapon: overhead {Gap( SwingDirs.Up, MeleeAttackTypes.Forward ):0}u, right swing {Gap( SwingDirs.Up, MeleeAttackTypes.Right ):0}u, "
				+ $"left swing {Gap( SwingDirs.Up, MeleeAttackTypes.Left ):0}u (catch {catchUnits:0}u)" );
			Check( "a side guard's bar is in the way of the swing from its side",
				MeleeGuardGeometry.GuardInterceptsSwing( _defender, side, _attacker, MeleeAttackTypes.Right ),
				$"{SwingDirs.Letter( side )} bar to the right swing's weapon {Gap( side, MeleeAttackTypes.Right ):0}u (catch {catchUnits:0}u)" );

			var rise = _defender.BlockActivationSeconds;
			if ( rise > 0.02f )
			{
				RaiseBlock( side );
				await Task.Frame();
				var start = _defender.ServerBlockStartedAtSandbox;
				MeleeBlockContact At( double time ) => new()
				{
					AttackerRoot = _attackerGo,
					AttackerPosition = _attackerGo.WorldPosition,
					DefenderRoot = _defenderGo,
					DefenderCombat = _defender,
					HitPosition = _defenderGo.WorldPosition,
					AttackType = MeleeAttackTypes.Right,
					AttackSwingDir = SwingDirs.Right,
					HitSandboxTime = time,
					AttackRayGeometryValidated = true
				};
				var justPressed = At( start + rise * 0.05 );
				var nearlyUp = At( start + rise * 0.9 );
				var earlyBlocked = _defender.TryServerResolveBlock( in justPressed, false, out _, out var earlyReason );
				var lateBlocked = _defender.TryServerResolveBlock( in nearlyUp, false, out _, out var lateReason );
				var lateMeet = MeleeGuardGeometry.TryMeetGuard( _defender, _attacker, MeleeAttackTypes.Right, start + rise * 0.9 );
				Check( "a guard still rising stops a blade that runs into it; one just pressed does not",
					_defender.IsAuthoritativeMeleeBlocking && !earlyBlocked && earlyReason == MeleeBlockRejectReason.BlockStartedAfterHit && lateBlocked,
					$"guard up {_defender.IsAuthoritativeMeleeBlocking}; 5% risen: blocked {earlyBlocked} ({earlyReason}); 90% risen: blocked {lateBlocked} ({lateReason}), "
					+ $"weapon {lateMeet.Distance:0}u from the rising guard (catch {catchUnits * 0.9f:0}u there)" );
				LowerBlock();
			}
		}

		// 5c. Flank (Mark 2026-11-16: "if they do overhead or anything else, right block. because they're to my RIGHT"):
		//     an attacker off to the defender's side is blocked with that side's guard whatever the swing.
		//     Runs only while the flank zone exists: the flank angle sits inside the front arc.
		if ( _defender.MeleeBlockFlankDegrees > 0f
		     && _defender.MeleeBlockFlankDegrees + 4f < MeleeBlockResolution.GetFrontHalfArcDegrees( _defender ) )
		{
			// Halfway between the flank angle and the edge of the front arc.
			var turn = (_defender.MeleeBlockFlankDegrees + MeleeBlockResolution.GetFrontHalfArcDegrees( _defender )) * 0.5f;
			foreach ( var sign in new[] { 1f, -1f } )
			{
				await Reset( close, defenderFacesAttacker: true );
				// Guard basis turned away from the attacker: yaw grows to the left, so +turn puts the attacker on the right.
				var basisYaw = _defenderGo.WorldRotation.Angles().yaw + sign * turn;
				var want = sign > 0f ? SwingDirs.Right : SwingDirs.Left;
				_defender.TestSubmitBlockIntent( true, basisYaw, SwingDirs.Up );
				await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
				var sideR = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
				var sideL = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Left );
				var sideU = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Forward );
				var label = want == SwingDirs.Right ? "right" : "left";
				Check( $"an attacker on the {label} comes in on the {label} whatever the swing",
					sideR == want && sideL == want && sideU == want,
					$"right swing → {SwingDirs.Letter( sideR )}, left swing → {SwingDirs.Letter( sideL )}, overhead → {SwingDirs.Letter( sideU )}, want {SwingDirs.Letter( want )} "
					+ $"(attacker at {MeleeBlockResolution.ComputeIncomingAngleFromAttacker( _defender, _attackerGo.WorldPosition ):0.#}°, flank {_defender.MeleeBlockFlankDegrees:0.#}°, "
					+ $"guard up {_defender.IsAuthoritativeMeleeBlocking}, basis yaw {_defender.GetBlockCombatBasisYaw():0.#}°, sent {basisYaw:0.#}°)" );

				var hp = _defenderVitals.CurrentHealth;
				Swing( dir: SwingDirs.Up );
				await WaitSwingDone();
				// Mark 2026-11-23 ("the block hitbox on an overhead isnt where the weapon is"): the marker still calls the
				// flank side, but the overhead guard's blade is level over the head, in the chop's way - it stops it.
				// (Until then this case held "an overhead guard does not stop an overhead from the flank".)
				Check( $"an overhead guard over the head stops an overhead from the {label} (the blade is in its way)",
					_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.51f,
					$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP, "
					+ $"overhead bar to the chop {MeleeGuardGeometry.ClosestCrossingDistance( _defender, SwingDirs.Up, _attacker, MeleeAttackTypes.Forward ):0}u" );
				LowerBlock();

				await Reset( close, defenderFacesAttacker: true );
				_defender.TestSubmitBlockIntent( true, basisYaw, want );
				await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
				hp = _defenderVitals.CurrentHealth;
				Swing( dir: SwingDirs.Up );
				await WaitSwingDone();
				Check( $"the {label} guard stops an overhead from the {label}",
					_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.51f,
					$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
				LowerBlock();
			}
		}

		// 5b. Long guard (Mark 2026-11-16: "Holding block shouldn't eat stamina btw, that costs nothing unless you actually
		//     block"): a held guard costs nothing however long and whatever side it is moved to; blocking a blow or taking a
		//     hit breaks it; a fresh press raises it again; no regen while it is up.
		await Reset( close, defenderFacesAttacker: true );
		{
			const float hold = 3f;
			var incoming = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
			var other = incoming == SwingDirs.Left ? SwingDirs.Right : SwingDirs.Left;
			var upAt = Time.NowDouble;
			RaiseBlock( incoming );
			await Task.Frame();
			var st = _defenderVitals.CurrentStamina;
			await WaitUntil( () => Time.NowDouble - upAt >= hold * 0.4f, hold + 1f );
			RaiseBlock( SwingDirs.Up );
			await WaitUntil( () => Time.NowDouble - upAt >= hold * 0.7f, hold + 1f );
			RaiseBlock( other );
			await WaitUntil( () => Time.NowDouble - upAt >= hold, hold + 1f );
			RaiseBlock( incoming );
			var drained = st - _defenderVitals.CurrentStamina;
			Check( "a held guard costs no stamina, however long, side swaps included", MathF.Abs( drained ) <= 0.3f,
				$"stamina moved {drained:0.##} over {Time.NowDouble - upAt:0.##} s of guard" );

			await Task.DelaySeconds( _defender.BlockSwitchSeconds + 0.05f );
			// LastMeleeHitResult still holds an earlier round's block: wait for this swing to bounce off (it ends on contact).
			var hpBefore = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right );
			await Task.Frame();
			await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			await Task.Frame();
			Check( "blocking a blow breaks the guard (the drain stops)",
				_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hpBefore - _defenderVitals.CurrentHealth ) < 0.51f
				&& !_defender.IsAuthoritativeMeleeBlocking,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, guard still up {_defender.IsAuthoritativeMeleeBlocking}" );

			// Let go and press again: a fresh guard, and no regen while it is up.
			LowerBlock();
			await Task.Frame();
			var repressAt = Time.NowDouble;
			RaiseBlock( incoming );
			await Task.Frame();
			st = _defenderVitals.CurrentStamina;
			await WaitUntil( () => Time.NowDouble - repressAt >= 1.5f, 2.5f );
			var spentAfterRepress = st - _defenderVitals.CurrentStamina;
			Check( "a fresh press raises the guard again, for free", _defender.IsAuthoritativeMeleeBlocking && spentAfterRepress <= 0.3f,
				$"guard up {_defender.IsAuthoritativeMeleeBlocking}, spent {spentAfterRepress:0.##} in 1.5 s" );
			// Mark 2026-11-16: "Raised guard blocks regen" - a held guard costs nothing and gives nothing back.
			Check( "a raised guard does not regenerate stamina", spentAfterRepress >= -0.3f,
				$"stamina rose {-spentAfterRepress:0.##} in 1.5 s with the guard up" );

			_defenderVitals.ApplyDamageAfterArmor( 5f, _attacker );
			Check( "taking a hit breaks the guard", !_defender.IsAuthoritativeMeleeBlocking, "the guard is still up after the hit" );
			LowerBlock();
			st = _defenderVitals.CurrentStamina;
			await Task.DelaySeconds( 1.5f );
			Check( "stamina regenerates once the guard is down", _defenderVitals.CurrentStamina > st + 1f,
				$"stamina {st:0.##} -> {_defenderVitals.CurrentStamina:0.##} in 1.5 s with the guard down" );
			await WaitSwingDone();
		}

		// 6. Heavy: held past the light windup → hangs while held, strikes on release, x class damage, heavy stamina;
		//    held on it never strikes by itself (Mark 2026-11-16) and the hold drains stamina past the drain point.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var st = _attackerVitals.CurrentStamina;
			var startedAt = Time.NowDouble;
			var res = Swing( heavy: true );
			Check( "heavy swing accepted", res.Accepted && _attacker.ServerHasActiveMeleeAttackAction,
				$"code {res.DebugCode}: {res.DebugDetail}, running {_attacker.ServerHasActiveMeleeAttackAction}, stamina {_attackerVitals.CurrentStamina:0.#}" );
			var holdUntil = MathF.Min( t.HeavyWindupSeconds + 0.03f, _attacker.HeavyHoldDrainAfterSeconds );
			await WaitUntil( () => Time.NowDouble - startedAt >= holdUntil, 3f );
			var heldWindup = _attacker.TestSwingWindupSeconds;
			Check( "a held heavy hangs past the heavy windup (no damage while held)",
				MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f && _attacker.TestSwingPhase == MeleeSwingPhase.Windup,
				$"defender lost {hp - _defenderVitals.CurrentHealth:0.##} HP, phase {_attacker.TestSwingPhase}, {Time.NowDouble - startedAt:0.###} s after the click" );
			var releasedAt = Time.NowDouble;
			ReleaseHeavy();
			await WaitUntil( () => _defenderVitals.CurrentHealth < hp - 0.01f, 2f );
			var strikeDelay = (float)(Time.NowDouble - releasedAt);
			// Read the cost as the heavy lands: the recovery that follows is long enough for stamina regen to start.
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "heavy strikes on release", strikeDelay <= t.HeavyActiveSeconds + slack,
				$"first damage {strikeDelay:0.###} s after the release (release {t.HeavyActiveSeconds} s)" );
			Check( "a heavy hit staggers for the heavy stagger", _defender.IsHitReactionActive, "victim not staggered by the heavy" );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( isHeavy: true );
			Check( "heavy deals class heavy damage", MathF.Abs( dealt - want ) < 0.51f && want > light + 0.5f,
				$"dealt {dealt:0.##}, want {want:0.##} (light {light:0.##}); windup {heldWindup:0.###} s at the hold check" );
			Check( "heavy spends heavy stamina in total", MathF.Abs( stSpent - t.StaminaHeavyCost ) < 0.75f,
				$"spent {stSpent:0.##}, want {t.StaminaHeavyCost:0.##}" );
		}

		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var st = _attackerVitals.CurrentStamina;
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			var holdFor = MathF.Max( 2f, t.HeavyWindupSeconds + 1.2f );
			await WaitUntil( () => Time.NowDouble - startedAt >= holdFor, holdFor + 1f );
			var heldFor = (float)(Time.NowDouble - startedAt);
			Check( "a held heavy never strikes by itself", _attacker.TestSwingHeavyHolding && _attacker.TestSwingPhase == MeleeSwingPhase.Windup,
				$"phase {_attacker.TestSwingPhase}, heavy holding {_attacker.TestSwingHeavyHolding} {heldFor:0.###} s after the click" );
			var drain = MathF.Max( 0f, heldFor - _attacker.HeavyHoldDrainAfterSeconds ) * _attacker.HeavyHoldDrainPerSecond;
			var stSpent = st - _attackerVitals.CurrentStamina;
			Check( "holding a heavy past the drain point drains stamina", MathF.Abs( stSpent - (t.StaminaHeavyCost + drain) ) < 1.01f,
				$"spent {stSpent:0.##}, want {t.StaminaHeavyCost + drain:0.##}" );
			ReleaseHeavy();
			await WaitSwingDone();
		}

		// 6b. Heavy morph (Mark 2026-11-16): a tapped light cannot be morphed; a held heavy moved from the right to the
		//     overhead strikes from overhead (the overhead guard stops it), never sooner than the morph settle.
		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			var seq = (ushort)(_intentSequence + 1);
			Swing( dir: SwingDirs.Right );
			Check( "a tapped light cannot be morphed",
				!_attacker.HostTryHeavyMorph( seq, SwingDirs.Up ) && _attacker.TestSwingAttackType == MeleeAttackTypes.Right,
				$"attack type {MeleeAttackTypes.Label( _attacker.TestSwingAttackType )}" );
			await WaitSwingDone();
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( SwingDirs.Up );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			var seq = (ushort)(_intentSequence + 1);
			var startedAt = Time.NowDouble;
			Swing( heavy: true, dir: SwingDirs.Right );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds + 0.1f, 3f );
			var overhead = _attacker.ResolveAttackTypeFromCursorDir( SwingDirs.Up );
			var morphedAt = Time.NowDouble;
			var morphed = _attacker.HostTryHeavyMorph( seq, SwingDirs.Up );
			Check( "a held heavy morphs to the new side", morphed && _attacker.TestSwingAttackType == overhead && _attacker.TestSwingHeavyHolding,
				$"morph {morphed}, attack type {MeleeAttackTypes.Label( _attacker.TestSwingAttackType )}, holding {_attacker.TestSwingHeavyHolding}" );
			ReleaseHeavy();
			await WaitUntil( () => _attacker.TestSwingPhase != MeleeSwingPhase.Windup, 2f );
			var struckAfter = (float)(Time.NowDouble - morphedAt);
			Check( "a morphed heavy strikes once the weapon settled", struckAfter >= _attacker.HeavyMorphSettleSeconds - 0.02f,
				$"released {struckAfter:0.###} s after the side change, settle {_attacker.HeavyMorphSettleSeconds:0.###} s" );
			// Mark 2026-11-17: let go before the re-arm, it goes as a light.
			Check( "a morph let go before the re-arm strikes as a light", !_attacker.TestSwingIsHeavy,
				$"heavy {_attacker.TestSwingIsHeavy} {struckAfter:0.###} s after the side change, re-arm {_attacker.HeavyMorphRearmSeconds:0.###} s" );
			await WaitUntil( () => _attacker.LastMeleeHitResult.WasBlocked || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			Check( "the guard on the morphed side blocks it",
				_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP (overhead guard)" );
			await WaitSwingDone();
			LowerBlock();
		}

		// Held past the re-arm after the side change, the morphed swing is a heavy again and lands heavy damage.
		await Reset( close, defenderFacesAttacker: true );
		{
			var hp = _defenderVitals.CurrentHealth;
			var seq = (ushort)(_intentSequence + 1);
			var startedAt = Time.NowDouble;
			Swing( heavy: true, dir: SwingDirs.Right );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds + 0.1f, 3f );
			var overhead = _attacker.ResolveAttackTypeFromCursorDir( SwingDirs.Up );
			var morphed = _attacker.HostTryHeavyMorph( seq, SwingDirs.Up );
			var morphedAt = Time.NowDouble;
			await WaitUntil( () => Time.NowDouble - morphedAt >= _attacker.HeavyMorphRearmSeconds + 0.05f, 2f );
			ReleaseHeavy();
			await WaitUntil( () => _attacker.TestSwingPhase != MeleeSwingPhase.Windup, 2f );
			Check( "a morph held past the re-arm strikes as a heavy", morphed && _attacker.TestSwingIsHeavy && _attacker.TestSwingAttackType == overhead,
				$"morph {morphed}, heavy {_attacker.TestSwingIsHeavy}, type {MeleeAttackTypes.Label( _attacker.TestSwingAttackType )}" );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			var want = _attacker.GetMeleeDamage( isHeavy: true );
			Check( "the re-armed morph deals heavy damage", MathF.Abs( dealt - want ) < 0.51f,
				$"dealt {dealt:0.##}, want {want:0.##}" );
		}

		// 7. A blocked heavy costs the heavy block row, breaks the guard and staggers the blocker (attacks locked) longer than
		//    the attacker's blocked recovery.
		await Reset( close, defenderFacesAttacker: true );
		{
			var incoming = MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right );
			RaiseBlock( incoming );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			var st = _defenderVitals.CurrentStamina;
			var startedAt = Time.NowDouble;
			Swing( heavy: true );
			await WaitUntil( () => Time.NowDouble - startedAt >= t.HeavyWindupSeconds + 0.02f, 2f );
			ReleaseHeavy();
			await WaitUntil( () => _attacker.LastMeleeHitResult.WasBlocked || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			var endedAt = Time.NowDouble;
			Check( "the correct guard blocks a heavy", _attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, lost {hp - _defenderVitals.CurrentHealth:0.##} HP" );
			Check( "a blocked heavy staggers the blocker and breaks the guard",
				_defender.IsAttackLocked && !_defender.IsAuthoritativeMeleeBlocking,
				$"defender attack-locked {_defender.IsAttackLocked}, still blocking {_defender.IsAuthoritativeMeleeBlocking}" );
			var heavyRow = MeleeBlockStaggerCatalog.Resolve( heavy: true );
			var lightRow = MeleeBlockStaggerCatalog.Resolve();
			Check( "a blocked heavy costs the heavy block row (more than a light)",
				MathF.Abs( st - _defenderVitals.CurrentStamina - heavyRow.StaminaCost ) < 1.01f && heavyRow.StaminaCost > lightRow.StaminaCost,
				$"spent {st - _defenderVitals.CurrentStamina:0.##}, want {heavyRow.StaminaCost} (light block {lightRow.StaminaCost})" );
			await WaitUntil( () => !_attacker.IsCombatActionLocked, 2f );
			Check( "the heavy attacker is free before the blocker (keeps the initiative)", _defender.IsAttackLocked,
				$"attacker free {Time.NowDouble - endedAt:0.###} s after contact, blocker already free" );
			await WaitUntil( () => !_defender.IsAttackLocked, 2f );
			Check( "the block stagger lasts the class time", Time.NowDouble - endedAt <= t.HeavyBlockStaggerSeconds + slack,
				$"blocker locked {Time.NowDouble - endedAt:0.###} s, want ≤ {t.HeavyBlockStaggerSeconds:0.###} s" );
			LowerBlock();
		}

		// 8. A stagger interrupts the victim's swing: the attacker's hit lands during the defender's windup.
		await Reset( close, defenderFacesAttacker: true );
		{
			var attackerHp = _attackerVitals.CurrentHealth;
			var defenderHp = _defenderVitals.CurrentHealth;
			Swing();
			var startedAt = Time.NowDouble;
			// The defender clicks so its release would come well after the attacker's blade lands.
			await WaitUntil( () => Time.NowDouble - startedAt >= Math.Max( 0.0, contactDelay - t.WindupSeconds * 0.5 ), 2f );
			Swing( _defender, _defenderGo );
			await WaitUntil( () => _defenderVitals.CurrentHealth < defenderHp - 0.01f, 2f );
			Check( "a hit interrupts the victim's windup", !_defender.ServerHasActiveMeleeAttackAction,
				$"defender's swing still running (phase {_defender.TestSwingPhase})" );
			await Task.DelaySeconds( t.WindupSeconds + t.ActiveSeconds );
			Check( "the interrupted swing never lands", MathF.Abs( attackerHp - _attackerVitals.CurrentHealth ) < 0.01f,
				$"attacker lost {attackerHp - _attackerVitals.CurrentHealth:0.##} HP" );
			await WaitSwingDone();
		}

		// 9. Hard-stagger protection: a second hard stagger inside the window does not stagger.
		await Reset( close, defenderFacesAttacker: true );
		{
			var first = _defender.HostTryStagger( t.HeavyHitStaggerSeconds, hard: true );
			var second = _defender.HostTryStagger( t.HeavyHitStaggerSeconds, hard: true );
			var light2 = _defender.HostTryStagger( t.HitStaggerSeconds, hard: false );
			Check( "repeated hard staggers are protected", first && !second && light2,
				$"first {first}, second {second} (want false inside {_defender.HardStaggerProtectionSeconds} s), light {light2}" );
		}

		// 10. Chamber: the defender's own swing releases as the blade arrives and mirrors the attack (Mark 2026-11-16:
		//     "Overhead to overhead then left attack vs right and right attack vs left").
		Check( "a chamber mirrors the attack",
			MeleeAttackTypes.Mirror( MeleeAttackTypes.Forward ) == MeleeAttackTypes.Forward
			&& MeleeAttackTypes.Mirror( MeleeAttackTypes.Left ) == MeleeAttackTypes.Right
			&& MeleeAttackTypes.Mirror( MeleeAttackTypes.Right ) == MeleeAttackTypes.Left,
			"overhead must meet overhead, left must meet right, right must meet left" );
		await Reset( close, defenderFacesAttacker: true );
		{
			var chamberDir = DirForAttackType( _defender, MeleeAttackTypes.Mirror( _attacker.ResolveAttackTypeFromCursorDir( SwingDirs.Right ) ) );

			var attackerHp = _attackerVitals.CurrentHealth;
			var defenderHp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right );
			var startedAt = Time.NowDouble;
			await WaitUntil( () => Time.NowDouble - startedAt >= contactDelay - t.WindupSeconds, 2f );
			Swing( _defender, _defenderGo, dir: chamberDir );
			await WaitUntil( () => _attacker.LastMeleeHitResult.WasBlocked || _defenderVitals.CurrentHealth < defenderHp - 0.01f
			                       || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			Check( "a timed mirrored swing chambers", _attacker.LastMeleeHitResult.WasChambered,
				$"chambered {_attacker.LastMeleeHitResult.WasChambered}, defender lost {defenderHp - _defenderVitals.CurrentHealth:0.##} HP "
				+ $"(attack R, chamber swing {SwingDirs.Letter( chamberDir )})" );
			Check( "a chamber costs the defender nothing", MathF.Abs( defenderHp - _defenderVitals.CurrentHealth ) < 0.01f && !_defender.IsHitReactionActive,
				$"defender lost {defenderHp - _defenderVitals.CurrentHealth:0.##} HP, staggered {_defender.IsHitReactionActive}" );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 2f );
			Check( "the chambering swing lands on the locked-out attacker", attackerHp - _attackerVitals.CurrentHealth > 0.5f,
				$"attacker lost {attackerHp - _attackerVitals.CurrentHealth:0.##} HP" );
			await WaitSwingDone();
		}

		// 10b. Held chamber (Mark 2026-11-18: "if were holding left click when the matching attack is coming ... it should
		//      block the hit and then swing the sword after ... its kinda 2 phases"): the defender holds the mirrored swing
		//      (never lets go); the blow is stopped while that swing is still cocked, and only then does it strike.
		await Reset( close, defenderFacesAttacker: true );
		{
			var chamberDir = DirForAttackType( _defender, MeleeAttackTypes.Mirror( _attacker.ResolveAttackTypeFromCursorDir( SwingDirs.Right ) ) );
			var attackerHp = _attackerVitals.CurrentHealth;
			var defenderHp = _defenderVitals.CurrentHealth;
			Swing( _defender, _defenderGo, heavy: true, dir: chamberDir );
			Swing( dir: SwingDirs.Right );
			var previousHit = _attacker.LastMeleeHitResult;
			await WaitUntil( () => !_attacker.LastMeleeHitResult.Equals( previousHit ) || !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			var phaseAtChamber = _defender.TestSwingPhase;
			var attackerHpAtChamber = _attackerVitals.CurrentHealth;
			Check( "a held mirrored swing chambers the blow before it strikes",
				_attacker.LastMeleeHitResult.WasChambered && MathF.Abs( defenderHp - _defenderVitals.CurrentHealth ) < 0.01f
				&& phaseAtChamber == MeleeSwingPhase.Windup && MathF.Abs( attackerHp - attackerHpAtChamber ) < 0.01f,
				$"chambered {_attacker.LastMeleeHitResult.WasChambered}, defender lost {defenderHp - _defenderVitals.CurrentHealth:0.##} HP, "
				+ $"defender swing phase at the chamber {phaseAtChamber}, attacker had lost {attackerHp - attackerHpAtChamber:0.##} HP by then" );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 3f );
			Check( "the held swing strikes after its chamber, still held", attackerHp - _attackerVitals.CurrentHealth > 0.5f,
				$"attacker lost {attackerHp - _attackerVitals.CurrentHealth:0.##} HP after the chamber" );
			await WaitSwingDone();
		}

		// The same timing with a swing that does not mirror (right vs right): no chamber.
		await Reset( close, defenderFacesAttacker: true );
		{
			var sameDir = DirForAttackType( _defender, _attacker.ResolveAttackTypeFromCursorDir( SwingDirs.Right ) );
			Swing( dir: SwingDirs.Right );
			var startedAt = Time.NowDouble;
			await WaitUntil( () => Time.NowDouble - startedAt >= contactDelay - t.WindupSeconds, 2f );
			Swing( _defender, _defenderGo, dir: sameDir );
			await WaitUntil( () => !_attacker.ServerHasActiveMeleeAttackAction, 2f );
			Check( "a timed swing that does not mirror does not chamber", !_attacker.LastMeleeHitResult.WasChambered,
				$"chambered with {SwingDirs.Letter( sameDir )} against R" );
			await WaitSwingDone();
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 2f );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			var defenderHp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Right );
			var startedAt = Time.NowDouble;
			// Far too early: the defender's release is over long before the blade arrives — no chamber.
			Swing( _defender, _defenderGo, dir: SwingDirs.Up );
			await WaitUntil( () => _defenderVitals.CurrentHealth < defenderHp - 0.01f || Time.NowDouble - startedAt > contactDelay + 0.3, 2f );
			Check( "a mistimed swing does not chamber", !_attacker.LastMeleeHitResult.WasChambered,
				"chambered with the defender's release long over" );
			await WaitSwingDone();
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 2f );
		}

		// 11. Turning: the swing basis follows the look at the phase turn rate; the camera is never held back.
		await Reset( close, defenderFacesAttacker: true, defenderBearingDegrees: 120f );
		{
			var hp = _defenderVitals.CurrentHealth;
			Swing();
			await WaitSwingDone();
			Check( "a target behind the shoulder is out of the unturned arc", MathF.Abs( hp - _defenderVitals.CurrentHealth ) < 0.01f,
				$"defender at 120° lost {hp - _defenderVitals.CurrentHealth:0.##} HP without a turn" );
		}

		await Reset( reach * 1.8f + 40f, defenderFacesAttacker: true );
		{
			Swing();
			await Task.Frame();
			var yaw0 = _attacker.GetMeleeCombatBasisYaw( MeleeAttackTypes.Right );
			var at0 = Time.NowDouble;
			SetAttackerLookYaw( 170f );
			await Task.Frame();
			await Task.Frame();
			var turned = MathF.Abs( Angles.NormalizeAngle( _attacker.GetMeleeCombatBasisYaw( MeleeAttackTypes.Right ) - yaw0 ) );
			var allowed = _attackerMovement.BodyTurnDegreesPerSecond * (float)(Time.NowDouble - at0) + 2f;
			Check( "the swing turns no faster than the body turn rate", turned <= allowed && turned > 0.5f,
				$"turned {turned:0.#}° in {Time.NowDouble - at0:0.###} s, max {allowed:0.#}°" );
			await WaitSwingDone();
			SetAttackerLookYaw( 0f );
		}

		// 11b. Turn damage (Mark 2026-11-17): a right swing travels right -> left (+yaw); the body turning left with it at
		//      the full turn rate hits harder, turning right against it softer (the target sits where the slowed blade
		//      still reaches it inside the release). The scale is checked against the dealt damage.
		Check( "turn damage spans the min..max scale",
			MathF.Abs( _attacker.TurnDamageScale( MeleeAttackTypes.Right, _attackerMovement.BodyTurnDegreesPerSecond ) - _attacker.TurnDamageMaxScale ) < 0.01f
			&& MathF.Abs( _attacker.TurnDamageScale( MeleeAttackTypes.Left, _attackerMovement.BodyTurnDegreesPerSecond ) - _attacker.TurnDamageMinScale ) < 0.01f
			&& MathF.Abs( _attacker.TurnDamageScale( MeleeAttackTypes.Forward, _attackerMovement.BodyTurnDegreesPerSecond ) - 1f ) < 0.01f,
			"full-rate turn with a right swing must give the max, with a left swing the min, an overhead 1" );
		await TurnDamageCase( "a swing turned with the blade hits harder", close, turnSign: 1f, bearing: 0f, wantAbove: true );
		await TurnDamageCase( "a swing turned against the blade hits softer", close, turnSign: -1f, bearing: -60f, wantAbove: false );

		// 12. Exhaustion: an empty bar refuses swings until it refills to the recovery threshold.
		await Reset( close, defenderFacesAttacker: true );
		{
			VitalsAuthority.Instance?.TryApplyDeltas( _attackerGo, 0f, -_attackerVitals.CurrentStamina, _attackerVitals );
			await Task.Frame();
			await Task.Frame();
			var res = Swing();
			Check( "an exhausted pawn cannot swing", !res.Accepted && _attackerVitals.IsExhausted,
				$"exhausted {_attackerVitals.IsExhausted}, swing code {res.DebugCode}: {res.DebugDetail}" );
		}

		// 13. Punch (F): lands PunchWindupSeconds after the click, locks the puncher for windup + lock (no spam), breaks a
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

		// The punch buys a free follow-up (Mark 2026-11-12): the puncher acts first, and a light swing it throws the moment
		// it is free lands before the punched pawn - guarding the moment its stagger ends - can block.
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
			RaiseBlock( MeleeBlockResolution.ResolveIncomingSide( _defender, _attackerGo, MeleeAttackTypes.Right ) );
			await WaitSwingDone();
			var hit = _attacker.LastMeleeHitResult;
			Check( "the punch's follow-up swing lands unblocked", !hit.WasBlocked,
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
			await WaitUntil( () => attackerHp - _attackerVitals.CurrentHealth > 0.5f || !_defender.ServerHasActiveMeleeAttackAction, 2f );
			var dealt = attackerHp - _attackerVitals.CurrentHealth;
			Check( "the punched pawn's swing still lands", MathF.Abs( dealt - _defender.GetMeleeDamage() ) < 0.51f,
				$"attacker lost {dealt:0.##} HP, want {_defender.GetMeleeDamage():0.##}" );
			await WaitUntil( () => !_defender.ServerHasActiveMeleeAttackAction, 2f );
		}

		await Reset( close, defenderFacesAttacker: true );
		{
			RaiseBlock( SwingDirs.Up );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
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

		// 14. Guard up but the attacker is behind: the back arc is open.
		await Reset( close, defenderFacesAttacker: false );
		{
			RaiseBlock( SwingDirs.Up );
			await Task.DelaySeconds( _defender.BlockActivationSeconds + 0.1f );
			var hp = _defenderVitals.CurrentHealth;
			Swing( dir: SwingDirs.Up );
			await WaitSwingDone();
			var dealt = hp - _defenderVitals.CurrentHealth;
			Check( "hit from behind ignores the guard",
				!_attacker.LastMeleeHitResult.WasBlocked && MathF.Abs( dealt - light ) < 0.51f,
				$"blocked {_attacker.LastMeleeHitResult.WasBlocked}, dealt {dealt:0.##}" );
			LowerBlock();
		}
	}

	/// <summary>Attacker is locked out right after a blocked / chambered swing and free again once the class lockout ran.</summary>
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

	/// <summary>Place both pawns (attacker at the origin facing +X), refill pools, wait out any lock and exchange window.</summary>
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

		_attacker.TestResetExchange();
		_defender.TestResetExchange();
		_attackerVitals.HostArenaRestoreAndTeleport( ArenaOrigin, Rotation.FromYaw( 0f ) );
		// Bearing: 0 = straight ahead of the attacker (+X), +90 = its left.
		var bearing = Rotation.FromYaw( defenderBearingDegrees );
		_defenderVitals.HostArenaRestoreAndTeleport( ArenaOrigin + bearing.Forward * distance,
			Rotation.FromYaw( defenderBearingDegrees + (defenderFacesAttacker ? 180f : 0f) ) );
		SetAttackerLookYaw( 0f );
		// Teleported pawns face their new look at once (the body tracker would otherwise turn there at its cap).
		_attackerMovement?.SnapBodyYawToLook();
		if ( _defenderGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is { } defenderController )
			defenderController.EyeAngles = new Angles( 0f, _defenderGo.WorldRotation.Yaw(), 0f );
		_defenderGo.Components.Get<PlayerMovement>( FindMode.EverythingInSelf )?.SnapBodyYawToLook();
		await Task.Frame();
		await Task.Frame();
	}

	void RaiseBlock( byte direction ) =>
		_defender.TestSubmitBlockIntent( true, _defenderGo.WorldRotation.Angles().yaw, direction );

	void LowerBlock()
	{
		if ( _defender.IsValid() )
			_defender.TestSubmitBlockIntent( false, _defenderGo.WorldRotation.Angles().yaw );
	}

	AttackReleaseResult Swing( float claimedHoldSeconds = 0f, bool heavy = false, byte dir = SwingDirs.Right, byte comboStep = 1 ) =>
		Swing( _attacker, _attackerGo, claimedHoldSeconds, heavy, dir, comboStep );

	/// <summary>The attacker's look yaw, as the synced PlayerController.EyeAngles the host reads for a remote owner.</summary>
	void SetAttackerLookYaw( float yawDegrees )
	{
		if ( _attackerGo.Components.Get<PlayerController>( FindMode.EverythingInSelf ) is { } controller )
			controller.EyeAngles = new Angles( 0f, yawDegrees, 0f );
	}

	PlayerMovement _attackerMovement => _attackerGo.Components.Get<PlayerMovement>( FindMode.EverythingInSelf );

	/// <summary>
	/// A light right swing whose body turns at the full turn rate (+1 left / -1 right) from just before its release until
	/// the swing is over, at a defender standing at <paramref name="bearing"/>: the hit must land and its damage carry the
	/// turn scale (above / below 1).
	/// </summary>
	async System.Threading.Tasks.Task TurnDamageCase( string name, float distance, float turnSign, float bearing, bool wantAbove )
	{
		await Reset( distance, defenderFacesAttacker: true, defenderBearingDegrees: bearing );
		var t = _attacker.GetMeleeWeaponTimings();
		var hp = _defenderVitals.CurrentHealth;
		_attacker.LastTurnDamageScale = 1f;
		var startedAt = Time.NowDouble;
		Swing( dir: SwingDirs.Right );
		await WaitUntil( () => Time.NowDouble - startedAt >= t.WindupSeconds - 0.04f, 2f );
		while ( _attacker.ServerHasActiveMeleeAttackAction && _attacker.TestSwingPhase != MeleeSwingPhase.Recovery
		        && _defenderVitals.CurrentHealth >= hp - 0.01f )
		{
			// The look keeps running ahead, so the body turns at its cap every frame.
			SetAttackerLookYaw( _attackerMovement.BodyYawDegrees + turnSign * 170f );
			await Task.Frame();
		}

		SetAttackerLookYaw( _attackerMovement.BodyYawDegrees );
		await WaitSwingDone();
		var dealt = hp - _defenderVitals.CurrentHealth;
		var scale = _attacker.LastTurnDamageScale;
		var want = _attacker.GetMeleeDamage( turnScale: scale );
		Check( name, dealt > 0.5f && (wantAbove ? scale > 1.1f : scale < 0.9f) && MathF.Abs( dealt - want ) < 0.51f,
			$"dealt {dealt:0.##} (want {want:0.##} at x{scale:0.###}, unturned {_attacker.GetMeleeDamage():0.##}), defender at {bearing:0}°" );
		SetAttackerLookYaw( 0f );
	}

	/// <summary>The attacker lets go of the button of its last swing (a held heavy strikes now).</summary>
	void ReleaseHeavy() => _attacker.HostNotePrimaryAttackReleased( _intentSequence );

	/// <summary>
	/// A swing straight along the pawn's facing, submitted to the host as a click intent (<paramref name="comboStep"/> 2+
	/// = the owner fired it as a chain). <paramref name="claimedHoldSeconds"/> back-dates the press the way a cheating
	/// client could — the host must ignore it. The button is let go at once (a light) unless <paramref name="heavy"/>:
	/// then no release reaches the host until <see cref="ReleaseHeavy"/>, exactly like a player holding through the windup.
	/// </summary>
	AttackReleaseResult Swing( PlayerCombat pc, GameObject go, float claimedHoldSeconds = 0f, bool heavy = false,
		byte dir = SwingDirs.Right, byte comboStep = 1 )
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
			AttackType = pc.ResolveAttackTypeFromCursorDir( dir ),
			CombatBasisYawDegrees = angles.yaw,
			CombatBasisPitchDegrees = 0f,
			ComboStep = comboStep
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
