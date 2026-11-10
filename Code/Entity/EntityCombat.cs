using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

	/// <summary>
	/// Host-only enemy attacks: random L/R/U telegraph (same as training dummy), aimed at the current target.
	/// Between swings the body turns toward the target at locomotion turn rate (no snap). The telegraph starts once the
	/// target is within <see cref="TelegraphStartDegrees"/> of the facing and keeps tracking it at
	/// <see cref="TelegraphTurnDegreesPerSecond"/>; yaw locks only from the release through the swing. After the swing the
	/// body is free again (turn, step, reposition) while <see cref="RecoverySeconds"/> (±40 %) runs down before the next
	/// telegraph (Mark 2026-11-04: scav AI "seems very artificial" — the old cycle froze yaw and feet from a ±3° lock to the
	/// end of a second, stacked recovery, so one sidestep beat every swing on a 2.9 s metronome).
	/// The body animates the attack on every peer with the pawn's one-handed sword (Mark 2026-11-04: "Scavs should just attack
	/// with the same standard sword as we do, ultimately it should be like a wooden club"): the human graph's sword build
	/// (<c>human_sword.vanmgrph</c>, the same <c>sword_pose</c> / <c>sword_attack</c> / <c>b_attack</c> the pawn drives in
	/// <c>PlayerAnimation.MeleeSwing.cs</c>), the cocked pose held through the telegraph — the windup a player reads — and the
	/// strike clip fired as the host swing's active window opens. The blade is the held model of the pawn's own sword item
	/// (<see cref="WeaponProfileId"/>, <c>club_wood</c> - the <c>sword</c> item), so a scav always holds what the player holds.
	/// </summary>
[Title( "Entity Combat" )]
public sealed class EntityCombat : Component
{
	[Property] public PlayerCombat Combat { get; set; }

	[Property, Group( "Timing" )] public float TelegraphSeconds { get; set; } = 0.85f;

	[Property, Group( "Timing" ), Title( "Gap between swings (free to move, ±40 %)" )]
	public float RecoverySeconds { get; set; } = 1f;

	/// <summary>The target must be this close to the facing before a telegraph starts.</summary>
	const float TelegraphStartDegrees = 25f;
	/// <summary>Turn rate while the telegraph tracks the target (the windup a player reads, still dodgeable by a sidestep).</summary>
	const float TelegraphTurnDegreesPerSecond = 90f;
	/// <summary>At the release the swing leads a moving target by its velocity × the class windup, at most this far off the facing.</summary>
	const float ReleaseLeadMaxDegrees = 30f;

	Vector3 _targetVelocity;
	Vector3 _targetSamplePos;
	double _targetSampleAt;

	[Property, Group( "Timing" )] public float HoldSeconds { get; set; } = 0.12f;

	[Property, Group( "Telegraph" )] public float AttackPathVerticalOffset { get; set; } = -24f;
	[Property, Group( "Telegraph" )] public float TelegraphEyeHeight { get; set; } = 64f;
	[Property, Group( "Telegraph" )] public float TelegraphLineLength { get; set; } = 92f;

	[Property, Group( "Debug" )] public bool ShowTelegraphDebug { get; set; } = false;

	readonly Random _rng = new();
	GameObject _attackTarget;
	Rotation _attackRotation;
	float _baseMeleeAttackZaxisStart;
	float _baseForwardPivotUpFromEye;
	byte _nextSwingDir = SwingDirs.Up;
	bool _engaged;
	bool _hasQueuedAttack;
	bool _telegraphActive;
	bool _rotationLocked;
	bool _waitingForSwingEnd;
	/// <summary>Host clock: no telegraph before this (the gap after a swing). Survives <see cref="ResetCycle"/> so a state change cannot skip it.</summary>
	double _nextAttackAllowedAt;
	Rotation _lockedAttackRotation;
	double _phaseEndsAt;
	ushort _intentSequence;

	public bool IsEngaged => _engaged;

	/// <summary>Brain → combat each frame: false holds the telegraph back (another attacker has the token, <see cref="EntityAttackSlots"/>).</summary>
	public bool AttackPermitted { get; set; } = true;

	/// <summary>The gap after the last swing has run out — a telegraph may start.</summary>
	public bool IsReadyToSwing => Time.NowDouble >= _nextAttackAllowedAt && !IsMovementLocked;

	/// <summary>Telegraph running: the body still tracks and may step in; the swing has not left yet.</summary>
	public bool IsTelegraphing => _telegraphActive;

	/// <summary>Released swing in flight (windup → active → the class recovery): feet planted, yaw locked.</summary>
	public bool IsSwingInFlight => _waitingForSwingEnd || (Combat?.ServerHasActiveMeleeAttackAction ?? false);

	// ---- sword presentation (host writes, every peer plays) ----

	/// <summary>Equipment profile whose held model the scav carries (Mark 2026-11-04: "Make the scav use the same sword model as us").</summary>
	[Property, Group( "Weapon" ), Title( "Weapon profile (equipment_profiles.json id)" )]
	public string WeaponProfileId { get; set; } = "club_wood";

	/// <summary>Host → everyone: a swing is cocked (telegraph through windup) — the sword holds the cocked pose of <see cref="SwingAttackType"/>.</summary>
	[Sync( SyncFlags.FromHost )] public bool SwordCocked { get; private set; }

	/// <summary>Host → everyone: <see cref="MeleeAttackTypes"/> of the cocked / striking swing.</summary>
	[Sync( SyncFlags.FromHost )] public byte SwingAttackType { get; private set; }

	/// <summary>Host → everyone: bumps once per swing start; each peer fires the strike clip <see cref="StrikeDelaySeconds"/> later.</summary>
	[Sync( SyncFlags.FromHost )] public int StrikeSerial { get; private set; }


	/// <summary>Host → everyone: seconds from the swing start to the active window (class windup − hold).</summary>
	[Sync( SyncFlags.FromHost )] public float StrikeDelaySeconds { get; private set; }

	/// <summary>Strike clip wall-clock length (PlayerAnimation Strike clip length, 0.2 s swoop + settle): the sword rests again after it.</summary>
	const float StrikeClipSeconds = 0.40f;
	/// <summary>Sword_Swap_Mid hold when the cocked side crosses the face from the shown shoulder.</summary>
	const float SwapMidSeconds = 0.08f;
	const int GraphHoldtypeMeleeWeapons = 6;
	const int GraphHandRight = 1;

	// Graph sword_pose / sword_attack values (holdtype_melee_sword.vsubgrph, PlayerAnimation.SwordPose / SwordAttack).
	const int PoseCockedL = 0, PoseRestL = 1, PoseRestR = 2, PoseCockedR = 3, PoseCockedOver = 4, PoseCockedStab = 5, PoseSwapMid = 6;
	const int StrikeR = 0, StrikeL = 1, StrikeOver = 2, StrikeStab = 3;

	SkinnedModelRenderer _body;
	CitizenAnimationHelper _animHelper;
	bool _bodySearched;
	bool _holdApplied;
	bool _strikeSeenInit;
	int _strikeSeen;
	byte _strikeType;
	double _strikeAt = double.MaxValue;
	double _strikeEndsAt;
	bool _restLeft;
	bool _cockedShown;
	double _swapMidUntil;
	int _appliedPose = -1;
	GameObject _sword;
	HeldModelFit _swordFit;
	bool _swordTried;

	public event Action AttackCycleFinished;

	/// <summary>Telegraph or swing in flight — the brain keeps the state (the gap after a swing is not locked).</summary>
	public bool IsMovementLocked =>
		IsAttackCommitted || _waitingForSwingEnd;

	/// <summary>Telegraph or active swing in progress — brain must not cancel mid-swing.</summary>
	public bool IsAttackCommitted =>
		_rotationLocked || _telegraphActive || (Combat?.ServerHasActiveMeleeAttackAction ?? false);

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

	public void ResetCycle()
	{
		if ( SwordCocked && !IsProxy )
			SwordCocked = false;
		_hasQueuedAttack = false;
		_telegraphActive = false;
		_rotationLocked = false;
		_waitingForSwingEnd = false;
		_phaseEndsAt = 0;
	}

	protected override void OnStart()
	{
		Combat ??= Components.Get<PlayerCombat>();
		if ( Combat is not null )
		{
			_baseMeleeAttackZaxisStart = Combat.MeleeAttackZaxisStart;
			_baseForwardPivotUpFromEye = Combat.MeleeAttackForwardPivotUpFromEye;
		}
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

		if ( _waitingForSwingEnd )
		{
			GameObject.WorldRotation = _lockedAttackRotation;
			if ( Combat is not null && Combat.ServerHasActiveMeleeAttackAction )
				return;

			_waitingForSwingEnd = false;
			FinishSwing();
			return;
		}

		if ( Combat is null || !Combat.Enabled || !_engaged )
			return;

		if ( _attackTarget is null || !_attackTarget.IsValid() )
		{
			if ( !IsAttackCommitted )
			{
				ResetCycle();
				return;
			}
		}

		if ( _rotationLocked )
			_attackRotation = _lockedAttackRotation;
		else if ( _attackTarget is { IsValid: true } )
			UpdateAttackRotation();

		SampleTargetVelocity();

		// Yaw is rigid only from the release on; the telegraph keeps tracking at a reduced rate, so a sidestep during the
		// windup is followed part of the way instead of beating the swing outright.
		if ( _rotationLocked )
		{
			GameObject.WorldRotation = _lockedAttackRotation;
		}
		else if ( _telegraphActive )
		{
			if ( _attackTarget is { IsValid: true } )
				SmoothFaceTowardAttackRotation( TelegraphTurnDegreesPerSecond );
		}
		else if ( _engaged && _attackTarget is { IsValid: true } )
		{
			// Between swings: turn toward the player at turn rate.
			var loco = Components.Get<EntityLocomotion>();
			SmoothFaceTowardAttackRotation( loco is not null ? Math.Max( 90f, loco.TurnDegreesPerSecond ) : 200f );
		}

		ApplyAttackPathVerticalOffset();

		if ( ShowTelegraphDebug && _telegraphActive )
			DrawTelegraphDebug();

		if ( Time.NowDouble < _phaseEndsAt )
			return;

		if ( _telegraphActive )
		{
			TryExecuteQueuedAttack();
			return;
		}

		if ( _engaged && AttackPermitted && Time.NowDouble >= _nextAttackAllowedAt && IsFacingAttackAim() )
			QueueNextAttack();
	}

	/// <summary>The swing (and the class's own recovery inside it) ended: free the body, start the gap to the next telegraph.</summary>
	void FinishSwing()
	{
		_engaged = false;
		ResetCycle();
		// ±40 % per cycle: raiders that reached the walls together swung in lock-step, and every swing's sweep traces
		// landed in the same frames once a second (Mark: "they all stutter on the second"); the wide spread also breaks
		// the metronome a single scav used to beat.
		_nextAttackAllowedAt = Time.NowDouble + Math.Max( 0.05f, RecoverySeconds ) * Sandbox.Game.Random.Float( 0.6f, 1.4f );
		AttackCycleFinished?.Invoke();
	}

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

	/// <summary>Yaw offset from the current facing toward where the target will be when the blade arrives (windup later).</summary>
	float ReleaseLeadYaw()
	{
		if ( _attackTarget is not { IsValid: true } || Combat is null )
			return 0f;

		var predicted = GetTargetAimPoint( _attackTarget ) + _targetVelocity * Combat.GetMeleeWindupSeconds();
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
		return MathF.Abs( delta ) <= TelegraphStartDegrees;
	}

	void QueueNextAttack()
	{
		UpdateAttackRotation();
		// No snap and no lock: the telegraph keeps turning toward the target; the release locks whatever facing it reached.
		_lockedAttackRotation = Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw );
		_rotationLocked = false;

		_nextSwingDir = RollSwingDir();
		_hasQueuedAttack = true;
		_telegraphActive = true;
		if ( Combat is not null )
			SwingAttackType = Combat.ResolveAttackTypeFromCursorDir( _nextSwingDir );
		SwordCocked = true;
		_phaseEndsAt = Time.NowDouble + Math.Max( 0.05f, TelegraphSeconds ) * Sandbox.Game.Random.Float( 0.8f, 1.2f );
	}

	void TryExecuteQueuedAttack()
	{
		if ( !_hasQueuedAttack || Combat is null )
			return;

		if ( !Combat.ServerCanBeginMeleeAttackAction() )
		{
			_phaseEndsAt = Time.NowDouble + 0.05;
			return;
		}

		_lockedAttackRotation = Rotation.FromYaw( GameObject.WorldRotation.Angles().yaw + ReleaseLeadYaw() );
		GameObject.WorldRotation = _lockedAttackRotation;
		_intentSequence++;
		var basisRot = _lockedAttackRotation;
		var view = basisRot.Forward.Normal;
		var basisAngles = basisRot.Angles();
		var camera = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var swingFrom = GetSwingFromXz( _nextSwingDir, view );
		var swingVert = _nextSwingDir == SwingDirs.Up ? 1f : 0f;
		var hold = Math.Max( 0f, HoldSeconds );

		var intent = new AttackReleaseIntent
		{
			PressedGlobalSeconds = RealTime.GlobalNow - hold,
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
			SwingVerticalHint = swingVert,
			SwingDir = _nextSwingDir,
			AttackType = Combat.ResolveAttackTypeFromCursorDir( _nextSwingDir ),
			CombatBasisYawDegrees = basisAngles.yaw,
			CombatBasisPitchDegrees = basisAngles.pitch
		};

		Combat.ServerStartMeleeAttackAction( intent, hold,
			$"entity {SwingDirs.Letter( _nextSwingDir )}" );

		SwingAttackType = intent.AttackType;
		StrikeDelaySeconds = Math.Max( 0f, Combat.GetMeleeWindupSeconds() - hold );
		StrikeSerial++;
		// The peers hold the cocked pose from the serial until the strike fires.
		SwordCocked = false;

		_telegraphActive = false;
		_hasQueuedAttack = false;
		_rotationLocked = true; // keep yaw frozen through the swing
		_engaged = false;
		_waitingForSwingEnd = true;
	}

	/// <summary>Every peer, every frame: sword hold, cocked pose through the telegraph, the strike on time, rest after it.</summary>
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
			_strikeType = SwingAttackType;
			_strikeAt = now + StrikeDelaySeconds;
		}

		if ( now >= _strikeAt )
		{
			_strikeAt = double.MaxValue;
			_strikeEndsAt = now + StrikeClipSeconds;
			_restLeft = _strikeType == MeleeAttackTypes.Left;
			_body.Set( "sword_attack", StrikeFor( _strikeType ) );
			_body.Set( "b_attack", true );
		}

		SetPose( ResolvePose( now ) );
	}

	int ResolvePose( double now )
	{
		var striking = now < _strikeEndsAt;
		var cocked = !striking && (SwordCocked || _strikeAt != double.MaxValue);
		if ( !cocked )
		{
			_cockedShown = false;
			return _restLeft ? PoseRestL : PoseRestR;
		}

		var type = _strikeAt != double.MaxValue ? _strikeType : SwingAttackType;
		if ( !_cockedShown )
		{
			_cockedShown = true;
			// Left shoulder <-> a right / overhead / stab wind-up crosses the face: a brief stop in front first (pawn rule).
			if ( (type == MeleeAttackTypes.Left) != _restLeft )
				_swapMidUntil = now + SwapMidSeconds;
		}

		if ( now < _swapMidUntil )
			return PoseSwapMid;

		_restLeft = type == MeleeAttackTypes.Left;
		return type switch
		{
			MeleeAttackTypes.Left => PoseCockedL,
			MeleeAttackTypes.Forward => PoseCockedOver,
			MeleeAttackTypes.Stab => PoseCockedStab,
			_ => PoseCockedR
		};
	}

	static int StrikeFor( byte type ) => type switch
	{
		MeleeAttackTypes.Left => StrikeL,
		MeleeAttackTypes.Forward => StrikeOver,
		MeleeAttackTypes.Stab => StrikeStab,
		_ => StrikeR
	};

	void SetPose( int pose )
	{
		if ( _appliedPose == pose )
			return;
		_appliedPose = pose;
		_body.Set( "sword_pose", pose );
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

		// Pawn rule (PlayerAnimation one-handed grip): head toward hold_R up, edge along its +X, butt of the handle in the fist.
		var frame = Rotation.LookAt( hold.Rotation.Up, hold.Rotation * Vector3.Forward );
		var modelRot = frame * Rotation.LookAt( _swordFit.HeadDir, _swordFit.EdgeDir ).Inverse;
		_sword.WorldRotation = modelRot;
		_sword.WorldPosition = hold.Position - modelRot * _swordFit.ButtPoint;
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
		if ( Combat is null )
			return;

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

	byte RollSwingDir() => _rng.Next( 0, 3 ) switch
	{
		0 => SwingDirs.Left,
		1 => SwingDirs.Right,
		_ => SwingDirs.Up
	};

	void DrawTelegraphDebug()
	{
		var start = GameObject.WorldPosition + Vector3.Up * TelegraphEyeHeight;
		var viewForward = _attackRotation.Forward.Normal;
		var right = Vector3.Cross( viewForward, Vector3.Up ).Normal;
		var color = _nextSwingDir == SwingDirs.Left
			? new Color( 0.50f, 0.78f, 1f, 0.95f )
			: _nextSwingDir == SwingDirs.Right
				? new Color( 1f, 0.85f, 0.20f, 0.95f )
				: new Color( 1f, 0.45f, 0.35f, 0.95f );

		Vector3 dir;
		if ( _nextSwingDir == SwingDirs.Left )
			dir = -right;
		else if ( _nextSwingDir == SwingDirs.Right )
			dir = right;
		else
			dir = (viewForward + Vector3.Up * 0.33f).Normal;

		var end = start + dir * Math.Max( 18f, TelegraphLineLength );
		var drawFor = MathF.Max( 0.03f, Time.Delta * 1.5f );
		DebugOverlay.Line( start, end, color, drawFor );
		DebugOverlay.Sphere( new Sphere( end, 3f ), color.WithAlpha( 0.75f ), drawFor );
	}
}
