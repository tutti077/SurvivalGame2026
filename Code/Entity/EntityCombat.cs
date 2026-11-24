using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

	/// <summary>
	/// Host-only scav fighter. It plays the directional exchange through the same host intake a remote player's click and
	/// guard report take, so its <see cref="PlayerCombat"/> runs the class rows of the weapon it holds exactly as for a
	/// player — windup, combo windup, release, recovery, heavy, counter windup, staggers, chamber, feint, stamina (its
	/// <see cref="EntityVitals"/> bar). This component only decides what to press, when, and when to step away, from the
	/// current difficulty level (<see cref="ScavCombatStyle"/>, <c>Assets/data/scav_combat_styles.json</c>): a ladder from 1, the
	/// scav's real difficulty (the dumbest fighter), through Medium (easy one at a time, Mark 2026-11-14: "the goal is to be
	/// able to handle 2-4 scavs at once") to God (a stand-in for a human opponent: "very close to fighting rumple"), picked
	/// by the host's Dev tools level (<see cref="DevToolsSettings.ScavDifficulty"/>).
	/// <list type="bullet">
	/// <item><b>Defense</b> — an opponent swing within reach and aimed at it is read after a human reaction time from the
	/// click: the guard on the side the blade comes in on (sometimes the wrong side), a backstep out of reach, or — only
	/// when its own windup still fits after that reaction — a timed same-side swing (a chamber). The guard follows a chain
	/// and drops a moment after the swing ends.</item>
	/// <item><b>Offense</b> (needs the attack token) — an opener after a think pause, sides weighted away from its last one
	/// and from the opponent's guard; sometimes a 2nd / 3rd chained hit (pressed in the recovery so it gets the combo
	/// windup), a heavy, a feint into another side, a counter after blocking a light; it steps out of reach between
	/// sequences and to catch its breath below its stamina reserve.</item>
	/// <item><b>Stagger</b> — a landed hit cuts its swing and drops its guard (<see cref="HostOnStaggered"/>).</item>
	/// </list>
	/// Backsteps are carried out by the brain (<see cref="IsBackingOff"/>, <see cref="BackstepDistanceUnits"/>). Yaw locks
	/// from the click through the release; the body turns toward the target between swings and in the recovery. Against a
	/// target without <see cref="PlayerCombat"/> (a wall it breaches) it swings single lights.
	/// The body animates on every peer with the pawn's sword graph (<c>human_sword.vanmgrph</c>: <c>sword_pose</c>,
	/// <c>sword_attack</c>, <c>b_attack</c>, <c>b_sword_guard</c>, <c>sword_guard_dir</c>, as <c>PlayerAnimation.MeleeSwing.cs</c>
	/// drives them): the cocked pose through the host's windup, the strike when the host's release opens, the guard pose
	/// while the host guard is up. The blade is the held model of <see cref="WeaponProfileId"/> (the club) and the prefab's
	/// PlayerCombat <c>FallbackWeaponClass</c> is that item's class (<c>oneHanded</c>), so a scav holds and swings what the
	/// player holds.
	/// </summary>
[Title( "Entity Combat" )]
public sealed class EntityCombat : Component
{
	[Property] public PlayerCombat Combat { get; set; }

	/// <summary>Its own difficulty level on the <c>scav_combat_styles.json</c> ladder (1 = the real scav); 0 follows the
	/// host's Dev tools level. The hook for scaling other melee fighters (Mark 2026-11-15); <c>scav_brawl</c> sets it.</summary>
	[Property, Group( "Duel" ), Title( "Difficulty level (0 = host setting)" ), Range( 0, 7 )]
	public int DifficultyLevel { get; set; }

	/// <summary>The difficulty level right now: its own, else the host's Dev tools level.</summary>
	ScavCombatStyle Style => DifficultyLevel > 0 ? ScavCombatStyleCatalog.Get( DifficultyLevel ) : ScavCombatStyleCatalog.Current;

	/// <summary>The target must be this close to the facing before an opener goes out.</summary>
	const float SwingStartDegrees = 25f;
	/// <summary>At the click the swing leads a moving target by its velocity × the windup, at most this far off the facing.</summary>
	const float ReleaseLeadMaxDegrees = 30f;
	/// <summary>Turn rate in the recovery (the body re-aims for the chained swing).</summary>
	const float RecoveryTurnDegreesPerSecond = 220f;
	/// <summary>A chained follow-up is pressed this long before the recovery ends, so it aims at where the target is now.</summary>
	const double ChainPressLeadSeconds = 0.1;
	/// <summary>An incoming blade reaches the body about this far into its release (the chamber aims its own release here).</summary>
	const double ChamberContactLeadSeconds = 0.08;
	/// <summary>Its own release must beat the incoming one by this much to keep swinging instead of guarding.</summary>
	const double LandsFirstMarginSeconds = 0.05;
	/// <summary>The opponent's swing counts as aimed at it inside this cone of its basis.</summary>
	const float ThreatConeDegrees = 75f;
	/// <summary>Extra reach (units) on top of the opponent's weapon reach before its swing counts as a threat.</summary>
	const float ThreatReachSlackUnits = 24f;

	[Property, Group( "Swing" )] public float AttackPathVerticalOffset { get; set; } = -24f;

	/// <summary>Log every duel decision to the console (<c>[Duel]</c>: guard / chamber / opener / chain / feint / heavy / counter / stagger).</summary>
	[Property, Group( "Debug" ), Title( "Log duel decisions" )]
	public bool LogDuel { get; set; }

	/// <summary>Height of the swing's view origin above the feet (the intent's camera).</summary>
	[Property, Group( "Swing" )] public float EyeHeight { get; set; } = 64f;

	readonly Random _rng = new();
	GameObject _attackTarget;
	Rotation _attackRotation;
	Rotation _lockedAttackRotation;
	float _baseMeleeAttackZaxisStart;
	float _baseForwardPivotUpFromEye;
	bool _engaged;
	ushort _intentSequence;

	Vector3 _targetVelocity;
	Vector3 _targetSamplePos;
	double _targetSampleAt;

	// ---- host: opponent
	GameObject _foeOf;
	PlayerCombat _foe;
	ushort _threatSeq;
	/// <summary><see cref="MeleeAttackTypes"/> of the tracked swing when last read (a heavy morph changes it mid-swing).</summary>
	byte _threatType;
	bool _threatTracked;
	MeleeSwingPhase _threatPhase;
	/// <summary>Whose swing the defense is reading: the target, or another fighter whose swing lands first (a player
	/// watches every attacker, not only the one they swing at — Mark 2026-11-15 brawl: a level 5 took every second
	/// attacker's hit unseen).</summary>
	PlayerCombat _threatFoe;
	/// <summary>Hostile fighters within <see cref="ThreatScanMeters"/> (the target first), refreshed every 0.25 s.</summary>
	readonly System.Collections.Generic.List<PlayerCombat> _nearFoes = new();
	double _nearFoesAt;
	EntityBrain _brain;
	const float ThreatScanMeters = 4f;
	enum ThreatResponse : byte { None, Block, Chamber, Backstep }
	ThreatResponse _response;
	bool _wrongSide;
	bool _guardSideChosen;
	double _reactAt;

	// ---- host: guard (what this fighter "holds"; PlayerCombat decides when it actually stands)
	bool _wantGuard;
	byte _wantGuardDir = SwingDirs.Up;
	double _guardDropAt = double.MaxValue;
	/// <summary>A broken guard is let go, then pressed again no sooner than this (a human release + press).</summary>
	double _guardRepressAt;
	bool _guardSent;
	byte _guardSentDir = byte.MaxValue;
	float _guardSentYaw;
	/// <summary>A one-frame guard press (the feint) — released on the next frame.</summary>
	bool _guardPulse;
	bool _counterArmed;
	/// <summary>The current counter window was already rolled for.</summary>
	bool _counterWindowSeen;

	// ---- host: offense
	ushort _ownSeq;
	ushort _planSeq;
	int _comboPlanned = 1;
	int _chainPressedAfterStep;
	bool _heavyPlanned;
	double _heavyReleaseAfter;
	bool _releaseSent;
	bool _feintPlanned;
	double _feintAfter;
	byte _lastSwingDir = SwingDirs.Up;
	double _nextOffenseAt;
	bool _sequenceActive;
	bool _ownWasActive;
	double _staggeredUntil;
	/// <summary>Hit a moment ago: guards more and quicker, holds its openers until this (or until the opponent raises a guard).</summary>
	double _defensiveUntil;

	bool IsDefensive( double now ) => now < _defensiveUntil;

	// ---- host → every peer: presentation
	ushort _presentedSeq;
	MeleeSwingPhase _presentedPhase;

	public bool IsEngaged => _engaged;

	double _backstepUntil;
	/// <summary>Out of breath (stamina under the style's reserve): no openers, it keeps out of reach until it has refilled.</summary>
	bool _catchingBreath;
	EntityVitals _vitals;

	/// <summary>Host: it wants to step back out of the opponent's reach (dodge, spacing, catching its breath) — the brain moves it.</summary>
	public bool IsBackingOff => Time.NowDouble < _backstepUntil;

	/// <summary>Host: how far from the target (units, centre to centre) the backstep aims.</summary>
	public float BackstepDistanceUnits { get; private set; } = 100f;

	void BeginBackstep( float seconds, string why )
	{
		var reach = _foe is not null ? _foe.GetMeleeAttackRangeUnits( MeleeAttackTypes.Right ) : Combat.GetMeleeAttackRangeUnits( MeleeAttackTypes.Right );
		BackstepDistanceUnits = reach + TerrainWorldUnits.MetersToEngine( Math.Max( 0f, Style.BackstepMeters ) );
		_backstepUntil = Math.Max( _backstepUntil, Time.NowDouble + Math.Max( 0.1f, seconds ) );
		Duel( $"backstep {seconds:0.00}s to {BackstepDistanceUnits / 40f:0.0} m ({why})" );
	}

	/// <summary>Brain → combat each frame: false holds openers back (another attacker has the token, <see cref="EntityAttackSlots"/>). Defense never waits for it.</summary>
	public bool AttackPermitted { get; set; } = true;

	/// <summary>Seconds a token it hands back stays shut to the other attackers of the same target (<see cref="EntityAttackSlots"/>).</summary>
	internal float RollTakeoverPause()
	{
		var st = Style;
		return RandRange( st.TakeoverPauseMinSeconds, Math.Max( st.TakeoverPauseMinSeconds, st.TakeoverPauseMaxSeconds ) );
	}

	/// <summary>Free for the next opener (the think pause after its last sequence has run out).</summary>
	public bool IsReadyToSwing => Time.NowDouble >= _nextOffenseAt && !IsMovementLocked && Time.NowDouble >= _staggeredUntil;

	/// <summary>Host: its own swing is winding up (the body may still step in).</summary>
	public bool IsWindingUp => Combat is not null && Combat.TryGetHostSwing( out var s ) && s.Phase == MeleeSwingPhase.Windup;

	/// <summary>Host: its own swing is in the release or the recovery (feet planted).</summary>
	public bool IsSwingInFlight => Combat is not null && Combat.TryGetHostSwing( out var s ) && s.Phase != MeleeSwingPhase.Windup;

	/// <summary>Any of its own swings in flight, or one stored to chain — the brain must not cancel or retarget.</summary>
	public bool IsMovementLocked => IsAttackCommitted;

	/// <summary>A swing in flight or a follow-up stored to fire.</summary>
	public bool IsAttackCommitted =>
		Combat is not null && (Combat.ServerHasActiveMeleeAttackAction || Combat.HostHasPendingSwing);

	// ---- sword presentation (host writes, every peer plays)

	/// <summary>Equipment profile whose held model the scav carries (Mark 2026-11-04: "Make the scav use the same sword model as us").</summary>
	[Property, Group( "Weapon" ), Title( "Weapon profile (equipment_profiles.json id)" )]
	public string WeaponProfileId { get; set; } = "club_wood";

	/// <summary>Host → everyone: its swing is winding up — the sword holds the cocked pose of <see cref="SwingAttackType"/>.</summary>
	[Sync( SyncFlags.FromHost )] public bool SwordCocked { get; private set; }

	/// <summary>Host → everyone: <see cref="MeleeAttackTypes"/> of the cocked / striking swing.</summary>
	[Sync( SyncFlags.FromHost )] public byte SwingAttackType { get; private set; }

	/// <summary>Host → everyone: bumps when a release opens; each peer fires the strike clip.</summary>
	[Sync( SyncFlags.FromHost )] public int StrikeSerial { get; private set; }

	/// <summary>Host → everyone: the windup of the swing now cocked; each peer draws the wind-up clip over it like a pawn's.</summary>
	[Sync( SyncFlags.FromHost )] public float SwingWindupSeconds { get; private set; }

	/// <summary>
	/// Strike clip wall-clock length - the pawn's (Mark 2026-11-17: scavs "arent using the same animations ... I want them
	/// to have the exact same speeds as players"; was 0.40 s, so a scav's settle and its next wind-up ran on another clock).
	/// </summary>
	const float StrikeClipSeconds = PlayerAnimation.StrikeClipLengthSeconds;
	const int GraphHoldtypeMeleeWeapons = 6;
	const int GraphHandRight = 1;

	// Graph sword_pose / sword_attack values (PlayerAnimation.SwordPose / SwordAttack).
	const int PoseCockedL = PlayerAnimation.SwordPose.CockedL, PoseRest = PlayerAnimation.SwordPose.Rest,
		PoseCockedR = PlayerAnimation.SwordPose.CockedR, PoseCockedOver = PlayerAnimation.SwordPose.CockedOver;
	const int StrikeR = 0, StrikeL = 1, StrikeOver = 2;

	SkinnedModelRenderer _body;
	CitizenAnimationHelper _animHelper;
	bool _bodySearched;
	bool _holdApplied;
	bool _strikeSeenInit;
	int _strikeSeen;
	double _strikeEndsAt;
	int _appliedPose = -1;
	float _appliedWind = -1f;
	bool _appliedInterrupt;
	bool _cockShown;
	double _cockShownAt;
	float _windFrom;
	int _appliedGuard = -1;
	int _appliedGuardDir = -1;
	GameObject _sword;
	HeldModelFit _swordFit;
	/// <summary>Where the fist sits on the held model, as a fraction of its length up from the butt (the pawn's MeleeDemoStickGripAlongLength).</summary>
	const float SwordGripAlongLength = 0.12f;
	bool _swordTried;

	/// <summary>Its own attack sequence (an opener and any chain / feint follow-up) ended — the brain returns the attack token.</summary>
	public event Action AttackCycleFinished;

	/// <summary>Every enabled scav fighter on this machine, for the pawn HUD's incoming-attack icon (a static list, no scene scan).</summary>
	static readonly System.Collections.Generic.List<EntityCombat> _fighters = new();
	public static System.Collections.Generic.IReadOnlyList<EntityCombat> Fighters => _fighters;

	/// <summary>Every peer: the cocked wind-up a player reads is on screen.</summary>
	public bool IsWindupShown { get; private set; }

	/// <summary><see cref="MeleeAttackTypes"/> of the wind-up on screen (valid while <see cref="IsWindupShown"/>).</summary>
	public byte WindupAttackType { get; private set; }

	protected override void OnEnabled()
	{
		base.OnEnabled();
		_fighters.Add( this );
	}

	protected override void OnDisabled()
	{
		_fighters.Remove( this );
		IsWindupShown = false;
		base.OnDisabled();
	}

	public void SetEngaged( bool engaged )
	{
		if ( _engaged == engaged )
			return;

		if ( !engaged && IsMovementLocked )
			return;

		_engaged = engaged;
		if ( !engaged )
			ResetCycle();
	}

	public void SetAttackTarget( GameObject target ) => _attackTarget = target;

	public void TickCombat( GameObject target )
	{
		SetAttackTarget( target );
		SetEngaged( true );
	}

	/// <summary>Out of the fight (state change): drop the guard and every plan; a swing in flight finishes on its own.</summary>
	public void ResetCycle()
	{
		_backstepUntil = 0;
		_defensiveUntil = 0;
		_wantGuard = false;
		_guardPulse = false;
		_counterArmed = false;
		_response = ThreatResponse.None;
		_feintPlanned = false;
		_heavyPlanned = false;
		if ( !IsProxy && Combat is not null && _guardSent )
			SendGuard( false );
	}

	protected override void OnStart()
	{
		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is null )
			return;

		_baseMeleeAttackZaxisStart = Combat.MeleeAttackZaxisStart;
		_baseForwardPivotUpFromEye = Combat.MeleeAttackForwardPivotUpFromEye;
	}

	/// <summary>Host: a landed hit staggered it (its PlayerCombat already cut the swing): guard down, no attack until it ends.</summary>
	internal void HostOnStaggered( float seconds )
	{
		var now = Time.NowDouble;
		_staggeredUntil = Math.Max( _staggeredUntil, now + seconds );
		Combat?.ApplyAttackLock( seconds );
		_wantGuard = false;
		_guardPulse = false;
		_counterArmed = false;
		_feintPlanned = false;
		_heavyPlanned = false;
		_response = ThreatResponse.None;
		if ( Combat is not null && _guardSent )
			SendGuard( false );
		_nextOffenseAt = Math.Max( _nextOffenseAt, _staggeredUntil + RandRange( 0.06f, 0.31f ) );
		var defensive = Style.DefensiveSeconds;
		if ( defensive > 0f )
			_defensiveUntil = Math.Max( _defensiveUntil, _staggeredUntil + defensive );
		Duel( $"staggered {seconds:0.00}s{(defensive > 0f ? $", defensive {defensive:0.0}s" : "")}" );
	}

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid() )
			return;

		TickAttackPresentation();

		if ( GameObject.IsProxy )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is null || !Combat.Enabled )
			return;

		var now = Time.NowDouble;
		var ownActive = Combat.TryGetHostSwing( out var own );
		PresentOwnSwing( ownActive, in own );
		TickSequenceEnd( ownActive, now );

		ApplyAttackPathVerticalOffset();

		// Locked from the click through the release, whatever the brain does.
		if ( ownActive && own.Phase is MeleeSwingPhase.Windup or MeleeSwingPhase.Release )
			GameObject.WorldRotation = _lockedAttackRotation;

		if ( !_engaged || _attackTarget is not { IsValid: true } )
		{
			if ( _guardSent && !IsAttackCommitted )
				ResetCycle();
			return;
		}

		UpdateAttackRotation();
		SampleTargetVelocity();
		if ( !ownActive || own.Phase == MeleeSwingPhase.Recovery )
		{
			if ( _wantGuard && _threatFoe is not null && _threatFoe != _foe && _threatFoe.IsValid() )
				TurnToward( _threatFoe.WorldPosition );
			else
				TurnTowardTarget( ownActive );
		}

		RefreshFoe();
		if ( now < _staggeredUntil )
		{
			if ( _guardSent )
				SendGuard( false );
			return;
		}

		if ( _foe is not null )
			TickDefense( now, ownActive );

		TickOffense( now, ownActive, in own );
		TickGuardOutput( now );
	}

	// ------------------------------------------------------------------ host: presentation of its own swing

	/// <summary>Host: cocked while the host's windup runs, the strike serial bumps when the release opens.</summary>
	void PresentOwnSwing( bool ownActive, in HostSwingView own )
	{
		var phase = ownActive ? own.Phase : MeleeSwingPhase.None;
		var cocked = phase == MeleeSwingPhase.Windup;
		if ( ownActive )
			SwingAttackType = own.AttackType;
		// Written before SwordCocked so a peer gets both in the same update.
		if ( cocked && !SwordCocked )
			SwingWindupSeconds = own.WindupSeconds;

		if ( ownActive && phase == MeleeSwingPhase.Release
		     && (_presentedSeq != own.Sequence || _presentedPhase == MeleeSwingPhase.Windup) )
			StrikeSerial++;

		if ( SwordCocked != cocked )
			SwordCocked = cocked;
		_presentedSeq = ownActive ? own.Sequence : _presentedSeq;
		_presentedPhase = phase;
	}

	/// <summary>Host: its attack sequence ended (no swing, nothing stored) — the think pause starts, the token goes back.</summary>
	void TickSequenceEnd( bool ownActive, double now )
	{
		var busy = ownActive || Combat.HostHasPendingSwing;
		if ( busy )
		{
			_ownWasActive = true;
			return;
		}

		if ( !_ownWasActive )
			return;

		_ownWasActive = false;
		_heavyPlanned = false;
		_releaseSent = false;
		if ( _feintPlanned || _counterArmed )
			return;   // the feint's follow-up / the counter is part of the same sequence

		if ( _sequenceActive )
		{
			_sequenceActive = false;
			var st = Style;
			var gap = _foe is not null ? RandRange( st.ThinkMinSeconds, st.ThinkMaxSeconds ) : st.BreachSwingGapSeconds * RandRange( 0.6f, 1.4f );
			_nextOffenseAt = Math.Max( _nextOffenseAt, now + gap );
			if ( _foe is not null && !_wantGuard && Roll( st.SpacingChance ) )
				BeginBackstep( Math.Min( gap, RandRange( 0.4f, 0.8f ) ), "spacing" );
			AttackCycleFinished?.Invoke();
		}
	}

	// ------------------------------------------------------------------ host: defense

	void RefreshFoe()
	{
		if ( _foeOf == _attackTarget )
			return;

		_foeOf = _attackTarget;
		_nearFoesAt = 0;
		_foe = _attackTarget.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants );
		if ( _foe == Combat )
			_foe = null;
		_threatTracked = false;
		_response = ThreatResponse.None;
	}

	/// <summary>Host: read the opponent's swing — answer a new one (guard / chamber / take it), follow a chain, drop the guard after.</summary>
	void TickDefense( double now, bool ownActive )
	{
		var threat = PickThreatFoe( now );
		if ( threat != _threatFoe )
		{
			_threatFoe = threat;
			_threatTracked = false;
		}

		if ( !_threatFoe.TryGetHostSwing( out var sw ) )
		{
			_threatPhase = MeleeSwingPhase.None;
			if ( _wantGuard && _guardDropAt == double.MaxValue )
				_guardDropAt = now + Style.GuardLingerSeconds * RandRange( 0.6f, 1.4f );
			return;
		}

		if ( !_threatTracked || sw.Sequence != _threatSeq )
		{
			_threatTracked = true;
			_threatSeq = sw.Sequence;
			_threatType = sw.AttackType;
			_response = ThreatResponse.None;
			_wrongSide = false;
			_guardSideChosen = false;
			var st = Style;
			// Hit a moment ago (the Rupu pattern): it is watching the opponent, so it reads quicker and guards more often.
			var defensive = IsDefensive( now );
			var reaction = defensive
				? RandRange( st.DefensiveReactionMinSeconds, st.DefensiveReactionMaxSeconds )
				: RandRange( st.ReactionMinSeconds, st.ReactionMaxSeconds );
			var blockChance = defensive ? Math.Max( st.BlockChance, st.DefensiveBlockChance ) : st.BlockChance;
			_reactAt = now + reaction;
			if ( IsThreatening( _threatFoe, in sw ) )
			{
				var chamberable = !sw.IsHeavy && !sw.IsHeavyHolding && !ownActive && !Combat.IsAttackLocked
				                  && ChamberFitsAfter( in sw, now + reaction );
				if ( chamberable && Roll( st.ChamberChance ) )
					_response = ThreatResponse.Chamber;
				else if ( Roll( blockChance ) )
				{
					_response = ThreatResponse.Block;
					_wrongSide = Roll( st.WrongGuardChance );
				}
				else if ( !ownActive && Roll( st.BackstepChance ) )
				{
					// Stepping back needs no read of the side: a quicker reaction than the guard's.
					_response = ThreatResponse.Backstep;
					_reactAt = now + RandRange( st.BackstepReactionMinSeconds, st.BackstepReactionMaxSeconds );
				}

				if ( _response != ThreatResponse.None && OwnSwingLandsFirst( now, _threatFoe, in sw ) )
				{
					_response = ThreatResponse.None;
					Duel( "its own swing lands first - keeps swinging" );
				}
			}

			Duel( $"foe swing {(_threatFoe == _foe ? "" : $"({_threatFoe.GameObject.Name}, not its target) ")}seq={sw.Sequence} {MeleeAttackTypes.Label( sw.AttackType )} step={sw.ComboStep} -> {_response}{(_wrongSide ? " (wrong side)" : "")} react {(_reactAt - now):0.00}s{(IsThreatening( _threatFoe, in sw ) ? "" : $" (not aimed at it: {ThreatReason( _threatFoe, in sw )})")}" );
			// A guard already up for the last swing of a chain stays up and re-reads the side after the reaction.
			if ( _response != ThreatResponse.Block && _wantGuard )
				_guardDropAt = now + Style.GuardLingerSeconds * RandRange( 0.6f, 1.4f );
		}
		else if ( sw.AttackType != _threatType )
		{
			// The foe morphed its held heavy to another side (PlayerCombat.HeavyMorph.cs): a guard that already picked a
			// side re-reads it after a fresh reaction (and may misread again); meanwhile it stays on the old side.
			_threatType = sw.AttackType;
			if ( _response == ThreatResponse.Block && _guardSideChosen )
			{
				var st = Style;
				_guardSideChosen = false;
				_wrongSide = Roll( st.WrongGuardChance );
				_reactAt = now + (IsDefensive( now )
					? RandRange( st.DefensiveReactionMinSeconds, st.DefensiveReactionMaxSeconds )
					: RandRange( st.ReactionMinSeconds, st.ReactionMaxSeconds ));
				Duel( $"foe morphed its heavy to {MeleeAttackTypes.Label( sw.AttackType )} - re-reads the side in {(_reactAt - now):0.00}s" );
			}
		}

		// The swing ended its release: the guard lingers a moment, then drops (a chained swing re-arms it).
		if ( sw.Phase == MeleeSwingPhase.Recovery && _threatPhase != MeleeSwingPhase.Recovery && _wantGuard )
			_guardDropAt = now + Style.GuardLingerSeconds * RandRange( 0.6f, 1.4f );
		_threatPhase = sw.Phase;

		if ( _response == ThreatResponse.Chamber )
		{
			TryChamber( now, in sw );
			return;
		}

		if ( _response == ThreatResponse.Backstep )
		{
			if ( now < _reactAt )
				return;
			_response = ThreatResponse.None;
			if ( !Combat.ServerHasActiveMeleeAttackAction )
				BeginBackstep( Style.BackstepSeconds, "dodging its swing" );
			return;
		}

		if ( _response != ThreatResponse.Block || now < _reactAt || sw.Phase == MeleeSwingPhase.Recovery )
			return;

		// The side is fixed by the attacker's basis at its click: read it once per swing.
		if ( !_guardSideChosen )
		{
			_guardSideChosen = true;
			var incoming = MeleeBlockResolution.ResolveIncomingSideLatched( Combat, _threatFoe.GameObject, sw.AttackType );
			_wantGuardDir = _wrongSide ? OtherSide( incoming ) : incoming;
			Duel( $"guard {SwingDirs.Letter( _wantGuardDir )} (blade from {SwingDirs.Letter( incoming )})" );
		}

		// Its own opener still early in the windup: the guard press feints it (PlayerCombat); later the guard waits for
		// its swing to end.
		_wantGuard = true;
		_guardDropAt = double.MaxValue;
		_counterArmed = false;
		_feintPlanned = false;
	}

	/// <summary>Host: start its own swing so its release meets the incoming blade from the same side (± the chamber tolerance).</summary>
	void TryChamber( double now, in HostSwingView sw )
	{
		if ( double.IsNaN( sw.ReleaseStartAt ) )
		{
			_response = ThreatResponse.Block;
			return;
		}

		var t = Combat.GetMeleeWeaponTimings();
		var windup = Combat.HostCounterWindowOpen ? t.CounterWindupSeconds : t.WindupSeconds;
		var idealPress = sw.ReleaseStartAt + ChamberContactLeadSeconds - windup;
		if ( now < Math.Max( idealPress, _reactAt ) )
			return;

		_response = ThreatResponse.None;
		var late = now - idealPress > Math.Max( 0.02f, Combat.ChamberToleranceSeconds * 0.6f );
		if ( late || Combat.ServerHasActiveMeleeAttackAction || Combat.IsAttackLocked || Combat.IsAuthoritativeMeleeBlocking )
		{
			// Too late to meet it: guard instead.
			_response = ThreatResponse.Block;
			_wrongSide = false;
			return;
		}

		// A chamber mirrors the incoming attack: overhead vs overhead, left vs right (MeleeAttackTypes.Mirror).
		var mirror = MeleeAttackTypes.Mirror( sw.AttackType );
		var dir = CursorDirForAttackType( mirror );
		if ( PressAttack( dir, comboStep: 1, heavy: false, note: "chamber" ) )
		{
			Duel( $"chamber swing {MeleeAttackTypes.Label( mirror )} vs {MeleeAttackTypes.Label( sw.AttackType )} (release due {(sw.ReleaseStartAt + ChamberContactLeadSeconds - now):0.000}s)" );
			_comboPlanned = 1;
			_feintPlanned = false;
			_sequenceActive = true;
		}
	}

	/// <summary>Its own swing (in its windup, or the armed counter about to go) releases before the incoming one: a landed hit
	/// staggers the attacker and cuts its swing, so it keeps the initiative instead of guarding — or feinting its own swing to
	/// guard (Mark 2026-11-15: scavs that blocked an overhead gave the turn straight back).</summary>
	bool OwnSwingLandsFirst( double now, PlayerCombat foe, in HostSwingView sw )
	{
		if ( !InReach() )
			return false;

		double ownAt;
		if ( Combat.TryGetHostSwing( out var own ) )
		{
			if ( own.Phase != MeleeSwingPhase.Windup || own.IsHeavyHolding || double.IsNaN( own.ReleaseStartAt ) )
				return false;
			ownAt = own.ReleaseStartAt;
		}
		else if ( _counterArmed && Combat.HostCounterWindowOpen )
			ownAt = now + (Combat.IsAttackLocked ? 0.1 : 0.02) + Combat.GetMeleeWeaponTimings().CounterWindupSeconds;
		else
			return false;

		var incomingAt = double.IsNaN( sw.ReleaseStartAt ) ? sw.StartedAt + foe.GetMeleeWeaponTimings().HeavyWindupSeconds : sw.ReleaseStartAt;
		return ownAt + LandsFirstMarginSeconds <= incomingAt;
	}

	/// <summary>Its own swing pressed at <paramref name="earliestPress"/> (after a human reaction) could still release on the
	/// incoming blade: with equal windups only from its counter window, never off a fresh read of a click.</summary>
	bool ChamberFitsAfter( in HostSwingView sw, double earliestPress )
	{
		if ( double.IsNaN( sw.ReleaseStartAt ) )
			return false;

		var t = Combat.GetMeleeWeaponTimings();
		var windup = Combat.HostCounterWindowOpen ? t.CounterWindupSeconds : t.WindupSeconds;
		var latestPress = sw.ReleaseStartAt + ChamberContactLeadSeconds - windup + Combat.ChamberToleranceSeconds * 0.6f;
		return earliestPress <= latestPress;
	}

	/// <summary>Hostile fighters near it, the target first (cached for 0.25 s: positions change slowly next to a swing).</summary>
	void RefreshNearFoes( double now )
	{
		if ( now < _nearFoesAt )
			return;

		_nearFoesAt = now + 0.25;
		_nearFoes.Clear();
		if ( _foe is not null )
			_nearFoes.Add( _foe );
		_brain ??= Components.Get<EntityBrain>();
		if ( _brain is null )
			return;

		var range = TerrainWorldUnits.MetersToEngine( ThreatScanMeters );
		foreach ( var pc in Scene.GetAllComponents<PlayerCombat>() )
		{
			if ( pc == Combat || pc == _foe || Vector3.DistanceBetween( pc.WorldPosition, WorldPosition ) > range )
				continue;
			if ( _brain.IsHostileFighter( pc.GameObject ) )
				_nearFoes.Add( pc );
		}
	}

	/// <summary>The swing to defend: of every hostile swing in flight and aimed at it, the one that releases first (the one
	/// it is already reading wins near-ties); with none, the one it was reading (so the guard lingers out), else the target.</summary>
	PlayerCombat PickThreatFoe( double now )
	{
		RefreshNearFoes( now );
		PlayerCombat best = null;
		var bestAt = double.MaxValue;
		foreach ( var pc in _nearFoes )
		{
			if ( !pc.IsValid() || !pc.TryGetHostSwing( out var sw ) || sw.Phase == MeleeSwingPhase.Recovery || !IsThreatening( pc, in sw ) )
				continue;

			var at = double.IsNaN( sw.ReleaseStartAt ) ? sw.StartedAt + 5.0 : sw.ReleaseStartAt;
			if ( pc == _threatFoe )
				at -= 0.12;
			if ( at < bestAt )
			{
				bestAt = at;
				best = pc;
			}
		}

		if ( best is not null )
			return best;
		return _threatFoe is not null && _threatFoe.IsValid() && _threatFoe.TryGetHostSwing( out _ ) ? _threatFoe : _foe;
	}

	bool IsThreatening( PlayerCombat foe, in HostSwingView sw )
	{
		var to = (GameObject.WorldPosition - foe.GameObject.WorldPosition).WithZ( 0f );
		var dist = to.Length;
		if ( dist > foe.GetMeleeAttackRangeUnits( sw.AttackType ) + ThreatReachSlackUnits )
			return false;
		if ( dist < 1f )
			return true;

		var basisYaw = foe.GetMeleeCombatBasisYaw( sw.AttackType );
		var toYaw = Rotation.LookAt( to.Normal, Vector3.Up ).Angles().yaw;
		return MathF.Abs( Angles.NormalizeAngle( toYaw - basisYaw ) ) <= ThreatConeDegrees;
	}

	string ThreatReason( PlayerCombat foe, in HostSwingView sw )
	{
		var to = (GameObject.WorldPosition - foe.GameObject.WorldPosition).WithZ( 0f );
		var basisYaw = foe.GetMeleeCombatBasisYaw( sw.AttackType );
		var off = to.LengthSquared < 1f ? 0f : Angles.NormalizeAngle( Rotation.LookAt( to.Normal, Vector3.Up ).Angles().yaw - basisYaw );
		return $"{to.Length / 40f:0.00} m, reach {foe.GetMeleeAttackRangeUnits( sw.AttackType ) / 40f:0.00} m, {off:0} deg off its swing";
	}

	// ------------------------------------------------------------------ host: offense

	void TickOffense( double now, bool ownActive, in HostSwingView own )
	{
		if ( ownActive )
		{
			TickOwnSwing( now, in own );
			return;
		}

		if ( Combat.HostHasPendingSwing )
			return;

		// Blocked a light: drop the guard and swing back inside the counter window (the light block reaction locks the
		// first 0.1 s; the class counter windup is shorter).
		var counterOpen = Combat.HostCounterWindowOpen;
		if ( !counterOpen )
			_counterWindowSeen = false;
		else if ( !_counterWindowSeen && _foe is not null )
		{
			// A block hands it the initiative (Mark 2026-11-15: "they dont use their block initiative when they block the
			// overhead"): the defensive wait ends; it counters in the window or swings back shortly after.
			_counterWindowSeen = true;
			_defensiveUntil = 0;
			var st = Style;
			if ( Roll( st.CounterChance ) )
			{
				_counterArmed = true;
				_wantGuard = false;
				Duel( "blocked a light - counter armed" );
			}
			else
			{
				_nextOffenseAt = now + RandRange( st.AfterBlockSwingMinSeconds, Math.Max( st.AfterBlockSwingMinSeconds, st.AfterBlockSwingMaxSeconds ) );
				Duel( $"blocked a light - swings back in {(_nextOffenseAt - now):0.00}s" );
			}
		}

		// A dodge about to start (the backstep's reaction is running) wins over an opener.
		if ( _wantGuard || _guardPulse || Combat.IsAuthoritativeMeleeBlocking || Combat.IsAttackLocked || IsBackingOff
		     || _response == ThreatResponse.Backstep )
			return;

		if ( _foe is not null && IsCatchingBreath() )
		{
			_counterArmed = false;
			_feintPlanned = false;
			if ( InReach() )
				BeginBackstep( 0.6f, "catching its breath" );
			return;
		}

		var counter = _counterArmed;
		if ( _counterArmed && !Combat.HostCounterWindowOpen )
			_counterArmed = false;

		// Defensive after a hit: no openers until it runs out - unless the opponent raises a guard, then it walks in and
		// swings (the bait players use on Rupu: drop the charge into a block, step back, punish the miss).
		if ( !counter && _foe is not null && IsDefensive( now ) )
		{
			if ( !_foe.IsAuthoritativeMeleeBlocking )
				return;
			_defensiveUntil = 0;
			_nextOffenseAt = now + RandRange( 0.19f, 0.44f );
			Duel( "opponent guards - advancing to swing" );
			return;
		}

		var feintFollowUp = _feintPlanned;
		var opener = counter || feintFollowUp || (AttackPermitted && now >= _nextOffenseAt);
		var fights = _foe is not null;
		// A wall it breaches: the brain already stands it in reach (its bounds centre can be far off).
		if ( !opener || (fights && !InReach()) || (!counter && !feintFollowUp && !IsFacingAttackAim()) )
			return;

		var heavy = fights && !counter && !feintFollowUp
		            && Roll( _foe.IsAuthoritativeMeleeBlocking ? Math.Min( 1f, Style.HeavyChance * 2f ) : Style.HeavyChance );
		var dir = ChooseSwingDir( avoid: feintFollowUp ? _lastSwingDir : byte.MaxValue );
		var planFeint = fights && !heavy && !counter && !feintFollowUp && Roll( Style.FeintChance );
		if ( !PressAttack( dir, comboStep: 1, heavy: heavy && !planFeint, note: counter ? "counter" : feintFollowUp ? "after feint" : "opener" ) )
			return;

		_counterArmed = false;
		_sequenceActive = true;
		_planSeq = _ownSeq;
		_chainPressedAfterStep = 0;
		_comboPlanned = !fights || heavy ? 1 : 1 + (Roll( Style.SecondHitChance ) ? 1 + (Roll( Style.ThirdHitChance ) ? 1 : 0) : 0);
		_heavyPlanned = heavy && !planFeint;
		var t = Combat.GetMeleeWeaponTimings();
		_heavyReleaseAfter = t.HeavyWindupSeconds + RandRange( 0f, 0.44f );
		_feintPlanned = planFeint;
		_feintAfter = t.WindupSeconds * RandRange( 0.3f, 0.6f );
		if ( planFeint )
			_comboPlanned = 1;
		Duel( $"{(counter ? "counter" : feintFollowUp ? "after-feint" : "opener")} {SwingDirs.Letter( dir )} seq={_ownSeq} heavy={_heavyPlanned} feint={_feintPlanned} combo={_comboPlanned}" );
	}

	/// <summary>Host: its swing in flight — release a heavy when charged, feint when planned, press the chained follow-up in the recovery.</summary>
	void TickOwnSwing( double now, in HostSwingView own )
	{
		var elapsed = now - own.StartedAt;

		if ( own.Sequence == _planSeq && _heavyPlanned && !_releaseSent && own.Phase == MeleeSwingPhase.Windup
		     && elapsed >= _heavyReleaseAfter )
		{
			_releaseSent = true;
			Combat.HostNotePrimaryAttackReleased( own.Sequence );
			Duel( $"heavy released after {elapsed:0.00}s (heavy={own.IsHeavy})" );
		}

		if ( own.Sequence == _planSeq && _feintPlanned && !_guardPulse && own.Phase == MeleeSwingPhase.Windup
		     && elapsed >= _feintAfter )
		{
			// The guard press in the early windup feints it (PlayerCombat.HostTryFeint); the guard drops next frame and the
			// follow-up swings from another side once the feint lock (and the guard's minimum hold) is over.
			_lastSwingDir = DirOfType( own.AttackType );
			_guardPulse = true;
			SendGuard( true );
			Duel( $"feint at {elapsed:0.00}s" );
			return;
		}

		if ( own.Phase != MeleeSwingPhase.Recovery || own.ComboStep >= _comboPlanned || _chainPressedAfterStep == own.ComboStep
		     || double.IsNaN( own.RecoveryEndAt ) || own.RecoveryEndAt - now > ChainPressLeadSeconds )
			return;

		_chainPressedAfterStep = own.ComboStep;
		if ( _foe is null || _wantGuard || !InReach() )
			return;

		var chainDir = ChooseSwingDir( avoid: _lastSwingDir );
		if ( PressAttack( chainDir, comboStep: own.ComboStep + 1, heavy: false, note: $"chain {own.ComboStep + 1}" ) )
			Duel( $"chain {own.ComboStep + 1} {SwingDirs.Letter( chainDir )} pressed {(own.RecoveryEndAt - now):0.000}s before the recovery end" );
	}

	/// <summary>Host: click like a player (the host intake picks the windup: class, combo, counter); a light lets go at once.</summary>
	bool PressAttack( byte swingDir, int comboStep, bool heavy, string note )
	{
		var t = Combat.GetMeleeWeaponTimings();
		var windupGuess = comboStep > 1 ? t.ComboWindupSeconds : heavy ? t.HeavyWindupSeconds : t.WindupSeconds;
		_lockedAttackRotation = Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw + ReleaseLeadYaw( windupGuess ) );
		GameObject.WorldRotation = _lockedAttackRotation;

		_intentSequence++;
		var basisRot = _lockedAttackRotation;
		var view = basisRot.Forward.Normal;
		var basisAngles = basisRot.Angles();
		var camera = GameObject.WorldPosition + Vector3.Up * EyeHeight;
		var swingFrom = GetSwingFromXz( swingDir, view );
		var attackType = Combat.ResolveAttackTypeFromCursorDir( swingDir );

		var intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = RealTime.GlobalNow,
			ReleasedGlobalSeconds = RealTime.GlobalNow,
			ClientCameraPressX = camera.x,
			ClientCameraPressY = camera.y,
			ClientCameraPressZ = camera.z,
			ClientCameraReleaseX = camera.x,
			ClientCameraReleaseY = camera.y,
			ClientCameraReleaseZ = camera.z,
			ViewForwardOnPress = view,
			ViewForwardOnRelease = view,
			ClientPlayerWorldPosition = GameObject.WorldPosition,
			ClientPlayerWorldRotation = basisRot,
			IntentSequence = _intentSequence,
			SwingFromX = swingFrom.x,
			SwingFromY = swingFrom.y,
			SwingVerticalHint = swingDir == SwingDirs.Up ? 1f : 0f,
			SwingDir = swingDir,
			AttackType = attackType,
			ComboStep = (byte)Math.Clamp( comboStep, 1, 250 ),
			CombatBasisYawDegrees = basisAngles.yaw,
			CombatBasisPitchDegrees = basisAngles.pitch
		};

		var result = Combat.HostSubmitPlayerSwing( in intent, $"entity {note} {SwingDirs.Letter( swingDir )}" );
		if ( !result.Accepted )
			return false;

		_ownSeq = _intentSequence;
		_lastSwingDir = swingDir;
		_releaseSent = !heavy;
		if ( !heavy )
			Combat.HostNotePrimaryAttackReleased( _intentSequence );
		return true;
	}

	/// <summary>A side for the next swing: not <paramref name="avoid"/> when possible, rarely the side the opponent guards.</summary>
	byte ChooseSwingDir( byte avoid )
	{
		Span<byte> dirs = stackalloc byte[] { SwingDirs.Left, SwingDirs.Right, SwingDirs.Up };
		Span<float> weight = stackalloc float[3];
		var guarded = _foe is { IsAuthoritativeMeleeBlocking: true } ? _foe.AuthoritativeMeleeBlockDirection : byte.MaxValue;
		var total = 0f;
		for ( var i = 0; i < 3; i++ )
		{
			var w = dirs[i] == avoid ? 0.35f : 1f;
			if ( guarded != byte.MaxValue
			     && MeleeBlockResolution.ResolveIncomingSide( _foe, GameObject, Combat.ResolveAttackTypeFromCursorDir( dirs[i] ) ) == guarded )
				w *= 0.25f;
			weight[i] = w;
			total += w;
		}

		var pick = (float)_rng.NextDouble() * total;
		for ( var i = 0; i < 3; i++ )
		{
			pick -= weight[i];
			if ( pick <= 0f )
				return dirs[i];
		}

		return dirs[2];
	}

	/// <summary>The cursor direction that throws <paramref name="attackType"/> (southpaw-aware).</summary>
	byte CursorDirForAttackType( byte attackType )
	{
		foreach ( var dir in new[] { SwingDirs.Left, SwingDirs.Right, SwingDirs.Up } )
		{
			if ( Combat.ResolveAttackTypeFromCursorDir( dir ) == attackType )
				return dir;
		}

		return DirOfType( attackType );
	}

	static byte DirOfType( byte attackType ) => attackType switch
	{
		MeleeAttackTypes.Left => SwingDirs.Left,
		MeleeAttackTypes.Right => SwingDirs.Right,
		_ => SwingDirs.Up
	};

	static byte OtherSide( byte side ) => side switch
	{
		SwingDirs.Left => Sandbox.Game.Random.Int( 0, 1 ) == 0 ? SwingDirs.Right : SwingDirs.Up,
		SwingDirs.Right => Sandbox.Game.Random.Int( 0, 1 ) == 0 ? SwingDirs.Left : SwingDirs.Up,
		_ => Sandbox.Game.Random.Int( 0, 1 ) == 0 ? SwingDirs.Left : SwingDirs.Right
	};

	/// <summary>Stamina under the style's reserve: hold off until it has the reserve + 25 back (0 reserve = never).</summary>
	bool IsCatchingBreath()
	{
		var reserve = Style.StaminaReserve;
		_vitals ??= Components.Get<EntityVitals>();
		if ( reserve <= 0f || _vitals is null || DevToolsSettings.InfiniteStamina )
			return _catchingBreath = false;

		if ( !_catchingBreath && _vitals.CurrentStamina < reserve )
		{
			_catchingBreath = true;
			Duel( $"stamina {_vitals.CurrentStamina:0} under {reserve:0} - catching its breath" );
		}
		else if ( _catchingBreath && _vitals.CurrentStamina >= reserve + 25f )
			_catchingBreath = false;
		return _catchingBreath;
	}

	bool InReach()
	{
		var reach = Combat.GetMeleeAttackRangeUnits( MeleeAttackTypes.Right );
		var flat = (GetTargetAimPoint( _attackTarget ) - GameObject.WorldPosition).WithZ( 0f ).Length;
		return flat <= reach + 12f;
	}

	// ------------------------------------------------------------------ host: guard output

	/// <summary>Host: report the held guard to its PlayerCombat when it changes (side, or the facing it covers turned).</summary>
	void TickGuardOutput( double now )
	{
		if ( _guardPulse )
		{
			// The feint press: let go the frame after (the minimum hold keeps the guard up briefly — the feint reads).
			if ( _guardSent && !(Combat.TryGetHostSwing( out var s ) && s.Sequence == _planSeq) )
			{
				_guardPulse = false;
				SendGuard( false );
			}
			return;
		}

		if ( _wantGuard && now >= _guardDropAt )
		{
			_wantGuard = false;
			_guardDropAt = double.MaxValue;
		}

		// A blocked blow or a hit broke the guard: like a player it lets go, then presses again after a human beat.
		if ( _guardSent && Combat.HostGuardAwaitingRelease )
		{
			SendGuard( false );
			_guardRepressAt = now + RandRange( 0.15f, 0.31f );
			return;
		}

		if ( _wantGuard && !_guardSent && now < _guardRepressAt )
			return;

		var yaw = GameObject.WorldRotation.Angles().yaw;
		if ( _wantGuard != _guardSent
		     || (_wantGuard && (_wantGuardDir != _guardSentDir || MathF.Abs( Angles.NormalizeAngle( yaw - _guardSentYaw ) ) > 4f)) )
			SendGuard( _wantGuard );
	}

	void SendGuard( bool up )
	{
		var dir = up ? _wantGuardDir : _guardSentDir == byte.MaxValue ? SwingDirs.Up : _guardSentDir;
		var yaw = GameObject.WorldRotation.Angles().yaw;
		Combat.HostSubmitGuard( up, dir, yaw );
		_guardSent = up;
		_guardSentDir = dir;
		_guardSentYaw = yaw;
	}

	// ------------------------------------------------------------------ host: facing

	void UpdateAttackRotation()
	{
		if ( _attackTarget is null || !_attackTarget.IsValid() )
			return;

		// Aim at body center so the player sits in the middle of forward LOS (not feet).
		var aimPoint = GetTargetAimPoint( _attackTarget );
		var toTarget = (aimPoint - GameObject.WorldPosition).WithZ( 0f );
		if ( toTarget.LengthSquared > 1f )
			_attackRotation = Rotation.LookAt( toTarget.Normal, Vector3.Up );
	}

	/// <summary>Turn the body toward a point at the locomotion's turn rate (guarding an attacker that is not its target).</summary>
	void TurnToward( Vector3 point )
	{
		var to = (point - WorldPosition).WithZ( 0f );
		if ( to.LengthSquared < 1f )
			return;

		var loco = Components.Get<EntityLocomotion>();
		var rate = loco is not null ? Math.Max( 90f, loco.TurnDegreesPerSecond ) : 200f;
		var current = GameObject.WorldRotation.Angles().yaw;
		var wanted = Rotation.LookAt( to.Normal, Vector3.Up ).Angles().yaw;
		var delta = Math.Clamp( Angles.NormalizeAngle( wanted - current ), -rate * Time.Delta, rate * Time.Delta );
		GameObject.WorldRotation = Rotation.FromYaw( current + delta );
	}

	void TurnTowardTarget( bool inRecovery )
	{
		if ( inRecovery )
		{
			SmoothFaceTowardAttackRotation( RecoveryTurnDegreesPerSecond );
			return;
		}

		var loco = Components.Get<EntityLocomotion>();
		SmoothFaceTowardAttackRotation( loco is not null ? Math.Max( 90f, loco.TurnDegreesPerSecond ) : 200f );
	}

	/// <summary>Smoothed flat velocity of the attack target (for the release lead).</summary>
	void SampleTargetVelocity()
	{
		if ( _attackTarget is not { IsValid: true } )
		{
			_targetSampleAt = 0d;
			_targetVelocity = Vector3.Zero;
			return;
		}

		var now = Time.NowDouble;
		var pos = _attackTarget.WorldPosition;
		if ( _targetSampleAt <= 0d || now - _targetSampleAt > 0.5 )
		{
			_targetSamplePos = pos;
			_targetSampleAt = now;
			_targetVelocity = Vector3.Zero;
			return;
		}

		var dt = (float)(now - _targetSampleAt);
		if ( dt < 0.05f )
			return;

		var v = ((pos - _targetSamplePos) / dt).WithZ( 0f );
		_targetVelocity = Vector3.Lerp( _targetVelocity, v, 0.5f );
		_targetSamplePos = pos;
		_targetSampleAt = now;
	}

	/// <summary>Yaw offset from the current facing toward where the target will be when the blade arrives (<paramref name="windup"/> later).</summary>
	float ReleaseLeadYaw( float windup )
	{
		if ( _attackTarget is not { IsValid: true } )
			return 0f;

		var predicted = GetTargetAimPoint( _attackTarget ) + _targetVelocity * windup;
		var to = (predicted - GameObject.WorldPosition).WithZ( 0f );
		if ( to.LengthSquared < 1f )
			return 0f;

		var delta = Angles.NormalizeAngle( Rotation.LookAt( to.Normal, Vector3.Up ).Angles().yaw - GameObject.WorldRotation.Angles().yaw );
		return Math.Clamp( delta, -ReleaseLeadMaxDegrees, ReleaseLeadMaxDegrees );
	}

	static Vector3 GetTargetAimPoint( GameObject target )
	{
		var bounds = target.GetBounds();
		if ( bounds.Size.LengthSquared > 1f )
			return bounds.Center;

		return target.WorldPosition + Vector3.Up * 40f;
	}

	void SmoothFaceTowardAttackRotation( float degPerSec )
	{
		const float hitchCapSeconds = 0.05f;
		var currentYaw = GameObject.WorldRotation.Angles().yaw;
		var targetYaw = _attackRotation.Angles().yaw;
		var delta = Angles.NormalizeAngle( targetYaw - currentYaw );
		var frameBudget = degPerSec * Math.Min( Math.Max( Time.Delta, 1e-4f ), hitchCapSeconds );
		var step = Math.Clamp( delta, -frameBudget, frameBudget );
		if ( MathF.Abs( step ) < 0.01f )
		{
			GameObject.WorldRotation = _attackRotation;
			return;
		}

		GameObject.WorldRotation = Rotation.FromYaw( currentYaw + step );
	}

	bool IsFacingAttackAim()
	{
		var delta = Angles.NormalizeAngle(
			_attackRotation.Angles().yaw - GameObject.WorldRotation.Angles().yaw );
		return MathF.Abs( delta ) <= SwingStartDegrees;
	}

	void Duel( string what )
	{
		if ( LogDuel )
			Log.Info( $"[Duel] {GameObject.Name} t={Time.Now:0.000} {what}" );
	}

	bool Roll( float chance ) => chance > 0f && _rng.NextDouble() < chance;

	float RandRange( float a, float b ) => b <= a ? a : a + (float)_rng.NextDouble() * (b - a);

	// ------------------------------------------------------------------ every peer: body + sword

	/// <summary>Every peer, every frame: sword hold, cocked pose through the windup, the strike at the release, the guard pose, rest after.</summary>
	void TickAttackPresentation()
	{
		if ( !_bodySearched )
		{
			_bodySearched = true;
			_body = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		}

		if ( _body is null || !_body.IsValid() )
			return;

		if ( !_holdApplied )
		{
			_holdApplied = true;
			_animHelper ??= Components.Get<CitizenAnimationHelper>();
			if ( _animHelper is not null && _animHelper.IsValid() )
			{
				_animHelper.HoldType = CitizenAnimationHelper.HoldTypes.Swing;
				_animHelper.Handedness = CitizenAnimationHelper.Hand.Right;
				_animHelper.IsWeaponLowered = false;
			}
			_body.Set( "holdtype", GraphHoldtypeMeleeWeapons );
			_body.Set( "holdtype_handedness", GraphHandRight );
			_body.Set( "b_weapon_lower", false );
		}

		var now = Time.NowDouble;
		// The first serial a peer sees (a join, a restore) is history, not a swing to play.
		if ( !_strikeSeenInit )
		{
			_strikeSeenInit = true;
			_strikeSeen = StrikeSerial;
		}

		if ( StrikeSerial != _strikeSeen )
		{
			_strikeSeen = StrikeSerial;
			_strikeEndsAt = now + StrikeClipSeconds;
			_body.Set( "sword_attack", StrikeFor( SwingAttackType ) );
			_body.Set( "b_attack", true );
		}

		// The host's guard (synced to every peer by PlayerCombat's block broadcast) covers the stance under it.
		var guard = Combat is not null && Combat.IsAuthoritativeMeleeBlocking;
		SetGuard( guard, guard ? Combat.AuthoritativeMeleeBlockDirection : SwingDirs.Up );
		SetPose( ResolvePose( now, guard ) );
	}

	int ResolvePose( double now, bool guard )
	{
		// The pawn's presentation, step for step (PlayerAnimation.MeleeSwing.cs): a new wind-up cuts a strike still
		// settling (b_sword_interrupt) and draws the wind-up clip over the swing's windup - from the waist, or from the
		// wind frame out of a strike or the guard - holding the cocked frame once it is drawn (a held heavy).
		var cocked = !guard && SwordCocked;
		if ( cocked && !_cockShown )
		{
			_cockShown = true;
			_cockShownAt = now;
			_windFrom = now < _strikeEndsAt || _appliedGuard == 1 ? PlayerAnimation.WindFrameShare : 0f;
			_strikeEndsAt = Math.Min( _strikeEndsAt, now );
		}
		else if ( !cocked )
			_cockShown = false;

		var striking = now < _strikeEndsAt;
		IsWindupShown = cocked;
		SetInterrupt( cocked );
		if ( cocked )
		{
			var t = (float)((now - _cockShownAt) / Math.Max( 0.05f, SwingWindupSeconds ));
			SetWind( _windFrom + (1f - _windFrom) * PlayerAnimation.WindDrawCurve( t ) );
		}

		// Under a strike the hidden pose stays on the strike's cocked pose until the settle, so a guard raised out of it
		// does not drag the hand through the waist (PlayerAnimation.ResolveSwordPose).
		var underStrike = striking && now < _strikeEndsAt - StrikeClipSeconds * 0.3f;
		if ( !cocked && !underStrike )
			return PoseRest;

		var type = SwingAttackType;
		if ( cocked )
			WindupAttackType = type;
		return type switch
		{
			MeleeAttackTypes.Left => PoseCockedL,
			MeleeAttackTypes.Forward => PoseCockedOver,
			_ => PoseCockedR
		};
	}

	static int StrikeFor( byte type ) => type switch
	{
		MeleeAttackTypes.Left => StrikeL,
		MeleeAttackTypes.Forward => StrikeOver,
		_ => StrikeR
	};

	void SetWind( float wind )
	{
		if ( MathF.Abs( _appliedWind - wind ) < 0.002f )
			return;
		_appliedWind = wind;
		_body.Set( "sword_wind", wind );
	}

	void SetInterrupt( bool interrupt )
	{
		if ( _appliedInterrupt == interrupt )
			return;
		_appliedInterrupt = interrupt;
		_body.Set( "b_sword_interrupt", interrupt );
	}

	void SetPose( int pose )
	{
		if ( _appliedPose == pose )
			return;
		_appliedPose = pose;
		_body.Set( "sword_pose", pose );
	}

	/// <summary>Guard pose: <c>b_sword_guard</c> over the stance, <c>sword_guard_dir</c> = the guard's <see cref="SwingDirs"/> (Left 0 / Right 1 / Up 2).</summary>
	void SetGuard( bool guard, byte direction )
	{
		var g = guard ? 1 : 0;
		if ( _appliedGuard != g )
		{
			_appliedGuard = g;
			_body.Set( "b_sword_guard", guard );
		}

		var dir = direction is SwingDirs.Left or SwingDirs.Right ? direction : SwingDirs.Up;
		if ( guard && _appliedGuardDir != dir )
		{
			_appliedGuardDir = dir;
			_body.Set( "sword_guard_dir", (int)dir );
		}
	}

	/// <summary>After animation: the sword rides the right hand's grip bone, blade along <c>hold_R</c> up, edge along its +X (pawn rule).</summary>
	protected override void OnPreRender()
	{
		if ( _body is null || !_body.IsValid() || !_body.Enabled )
			return;

		if ( !_body.TryGetBoneTransform( "hold_R", out var hold ) || hold.Position.LengthSquared <= 1e-6f )
			return;

		EnsureSword();
		if ( _sword is null || !_sword.IsValid() )
			return;

		// Pawn rule (PlayerAnimation one-handed grip): head toward hold_R up, edge along its +X, the handle in the fist.
		var frame = Rotation.LookAt( hold.Rotation.Up, hold.Rotation * Vector3.Forward );
		var modelRot = frame * Rotation.LookAt( _swordFit.HeadDir, _swordFit.EdgeDir ).Inverse;
		_sword.WorldRotation = modelRot;
		// The fist closes on the handle, not the pommel: the same 12 % up from the butt as the pawn (basicplayer MeleeDemoStickGripAlongLength).
		_sword.WorldPosition = hold.Position - modelRot * (_swordFit.ButtPoint + _swordFit.HeadDir * (_swordFit.Length * SwordGripAlongLength));

		// The heavy-ready glint rides this blade (a pawn's comes from PlayerAnimation).
		if ( Combat is not null && Combat.WantsHeavyFlashBlade )
		{
			var heel = _sword.WorldPosition + modelRot * _swordFit.ButtPoint;
			var tip = heel + modelRot * _swordFit.HeadDir * _swordFit.Length;
			Combat.FeedHeavyFlashBlade( heel, tip );
		}
	}

	void EnsureSword()
	{
		if ( _swordTried )
			return;
		_swordTried = true;

		var path = EquipmentCatalog.TryGet( WeaponProfileId, out var profile ) ? profile?.HeldModel?.Trim() : null;
		var model = string.IsNullOrEmpty( path ) ? null : Model.Load( path );
		if ( model is null || model.IsError )
		{
			Log.Warning( $"[EntityCombat] {GameObject.Name}: weapon profile '{WeaponProfileId}' has no loadable held model ('{path}')." );
			return;
		}

		_swordFit = HeldModelFit.FromModel( model );
		_sword = new GameObject( false, "EntitySword" );
		_sword.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_sword.Parent = _body.GameObject;
		_sword.Components.Create<ModelRenderer>().Model = model;
		// The model is in world units (vmdl import scale): the scav root's 0.85 must not shrink the player's sword.
		_sword.WorldScale = Vector3.One;
		_sword.Enabled = true;
	}

	protected override void OnDestroy()
	{
		if ( _sword is not null && _sword.IsValid() )
			_sword.Destroy();
	}

	void ApplyAttackPathVerticalOffset()
	{
		Combat.MeleeAttackZaxisStart = _baseMeleeAttackZaxisStart + AttackPathVerticalOffset;
		Combat.MeleeAttackForwardPivotUpFromEye = _baseForwardPivotUpFromEye + AttackPathVerticalOffset;
	}

	Vector2 GetSwingFromXz( byte swingDir, Vector3 viewForward )
	{
		var f = new Vector2( viewForward.x, viewForward.z );
		if ( f.LengthSquared < 1e-8f )
			f = new Vector2( _attackRotation.Forward.x, _attackRotation.Forward.z );
		f = f.LengthSquared < 1e-8f ? new Vector2( 0f, 1f ) : f.Normal;
		var right = new Vector2( f.y, -f.x );

		if ( swingDir == SwingDirs.Left )
			return -right;
		if ( swingDir == SwingDirs.Right )
			return right;
		return f;
	}
}
