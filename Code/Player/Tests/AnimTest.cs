using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>anim_test</c>: automated anatomy suite for the one-handed sword presentation. Run it in play mode in any
/// scene. It clones a real <c>basicplayer</c> far above the scene, equips the sword and drives every state the
/// player can produce — both shoulder rests, the shoulder swap both ways, a full look sweep (yaw and pitch, the
/// look-behind case included), every wind-up and strike from both sides, cross-side presses — through the live
/// path (controller animation pass, look limits, swap sequence, strike clips). Every frame it measures the
/// actual skinned bones and fails on: the sword moving relative to the fist, the blade inside the head / neck /
/// torso / pelvis, the forearm or free hand inside the body, an elbow folded or bent past human range, the
/// chest or head twisted past human range relative to the hips, and any bone that snaps between frames.
/// The same limits are checked offline on the authored clips by <c>create_sword_anims.py -- check</c>.
/// Console: one <c>[AnimTest] PASS|FAIL</c> line per scenario, then <c>[AnimTest] RESULT …</c>.
/// </summary>
public sealed class AnimTest : Component
{
	const string Tag = "[AnimTest]";
	const string PawnPrefab = "prefabs/player/basicplayer.prefab";
	const string WeaponId = "club_wood";
	static readonly Vector3 ArenaOrigin = new( 0f, 3000f, 30000f );

	/// <summary>Citizen human: built in centimetres and scaled 0.3937 → 39.37 units per metre.</summary>
	const float M = 39.37f;

	// Limits — mirror LIMITS in Blender/scripts/create_sword_anims.py.
	const float BladeHeadClear = 0.04f, BladeNeckClear = 0.03f, BladeTorsoClear = 0.02f, BladePelvisClear = 0.02f;
	const float ArmTorsoClear = 0.0f, ElbowMin = 35f, ElbowMax = 178f;
	const float TorsoR = 0.12f, HeadR = 0.11f, NeckR = 0.06f, PelvisR = 0.15f, ArmR = 0.035f, HandR = 0.04f;
	/// <summary>65°: a full swing thrown while looking 70° to the side (chest aim + strike twist).</summary>
	const float ChestTwistMax = 65f, HeadTwistMax = 110f;
	/// <summary>Per-frame speed caps relative to the pawn (m/s, deg/s): faster than this reads as a snap.</summary>
	/// The strike is 0.30 s (Mark 2026-11-01: half the time between attacks): a full-speed cut peaks around 19.5 m/s at
	/// the fist and ~960°/s at the chest, athletic but human; the snaps this exists to catch measured 1750–2800°/s.
	const float HandSpeedMax = 22f, PelvisSpeedMax = 1.5f, HeadSpeedMax = 4.5f, PelvisTurnMax = 400f, ChestTurnMax = 1200f, HeadTurnMax = 1200f;
	/// <summary>The sword must stay rigid in the fist.</summary>
	const float GripSlipMax = 0.01f, GripTurnMax = 2f;

	readonly List<string> _failures = new();
	int _passed;
	GameObject _pawn;
	PlayerAnimation _anim;
	PlayerCombat _combat;
	PlayerController _controller;
	SkinnedModelRenderer _body;

	// per-scenario
	string _scenario;
	readonly List<string> _problems = new();
	bool _havePrev;
	Vector3 _prevHand, _prevPelvis, _prevHead;
	Rotation _prevPelvisRot, _prevChestRot, _prevHeadRot;
	Rotation _restChestRel, _restHeadRel;
	bool _haveRest;
	global::Transform _gripRef;
	bool _haveGrip;
	int _frames;
	/// <summary>Last frames' summaries, logged once per scenario around its first bad frame.</summary>
	readonly Queue<string> _trace = new();
	bool _traceLogged;

	static AnimTest _running;

	[ConCmd( "anim_test" )]
	public static void ConCmdAnimTest()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || !Sandbox.Game.IsPlaying )
		{
			Log.Warning( $"{Tag} enter play mode first." );
			return;
		}

		if ( _running.IsValid() )
		{
			Log.Warning( $"{Tag} already running." );
			return;
		}

		var go = scene.CreateObject();
		go.Name = "AnimTest";
		go.NetworkMode = NetworkMode.Never;
		_running = go.Components.Create<AnimTest>();
	}

	protected override void OnStart() => _ = RunAsync();

	protected override void OnDestroy() => _pawn?.Destroy();

	async System.Threading.Tasks.Task RunAsync()
	{
		Log.Info( $"{Tag} starting" );
		try
		{
			if ( !Spawn() )
				Fail( "spawn", $"could not clone {PawnPrefab} with PlayerAnimation + PlayerController" );
			else
			{
				var armed = false;
				var until = Time.NowDouble + 3.0;
				while ( !armed && Time.NowDouble < until )
				{
					await Task.Frame();
					armed = Equip();
				}

				Check( "pawn holds the sword", armed, $"could not equip {WeaponId}" );
				if ( armed )
					await RunScenarios();
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

	// ── Pawn ────────────────────────────────────────────────────────────────────────────────

	bool Spawn()
	{
		var template = BuildPrefabUtility.GetTemplate( PawnPrefab );
		if ( template is null )
			return false;

		_pawn = template.Clone( new global::Transform( ArenaOrigin ), name: "AnimTest_Pawn" );
		_pawn.NetworkMode = NetworkMode.Never;
		// PlayerSpawnSeparation culls local-only player pawns as ghosts; this one is local on purpose.
		_pawn.Tags.Add( "test_pawn" );

		_controller = _pawn.Components.Get<PlayerController>();
		_anim = _pawn.Components.Get<PlayerAnimation>();
		_combat = _pawn.Components.Get<PlayerCombat>();
		if ( _controller is null || _anim is null || _combat is null )
			return false;

		// The controller stays enabled (its animation pass is part of what is tested) but reads no input;
		// the look is scripted through EyeAngles. Free-look orbit, as PlayerMovement runs it in third person.
		_controller.UseInputControls = false;
		_controller.UseCameraControls = false;
		_controller.UseLookControls = false;
		_controller.RotationAngleLimit = 180f;
		if ( _pawn.Components.Get<Rigidbody>() is { } rb )
			rb.MotionEnabled = false;

		// PlayerCombat stays enabled (the stance logic reads it, as in the game) but reads no input, so the
		// editor's mouse can never steer the test. Disabling it made the stance silently ignore the scripted aim.
		_combat.InputSuppressedForTests = true;
		DisableAll<ScreenPanel>();
		DisableAll<PlayerScreenHud>();
		DisableAll<PlayerCrosshair>();
		DisableAll<PlayerGameMenuController>();
		DisableAll<PlayerHotbarController>();
		DisableAll<PlayerInventoryInteraction>();
		DisableAll<PlayerSpawnSeparation>();
		DisableAll<PlayerMovement>();
		if ( _pawn.Components.Get<CameraComponent>( FindMode.EverythingInSelfAndDescendants ) is { } cam )
			cam.Enabled = false;
		return true;
	}

	void DisableAll<T>() where T : Component
	{
		foreach ( var c in _pawn.Components.GetAll<T>( FindMode.EverythingInSelfAndDescendants ) )
			c.Enabled = false;
	}

	bool Equip()
	{
		var hotbar = _pawn.Components.Get<PlayerHotbar>();
		var equipment = _pawn.Components.Get<PlayerEquipment>();
		if ( hotbar is null || equipment is null )
			return false;

		if ( _pawn.Components.Get<PlayerEquippedItem>() is { } already && already.HasAction( EquippedItemActions.PrimaryMelee ) )
		{
			_body = _anim.BodyRendererForTests;
			return _body is not null && _anim.MeleeHeldPropForTests is { } prop && prop.IsValid();
		}

		hotbar.SetBinding( 0, WeaponId );
		hotbar.SetActiveSlot( 0 );
		if ( hotbar.GetSlot( 0 ).IsEmpty && hotbar.TryAddResourcePickup( WeaponId, 1 ) != 0 )
			return false;

		equipment.EquipFromHotbarSlot( 0 );
		return false;
	}

	/// <summary>Look set by the test, relative to the pawn (the controller's EyeAngles getter throws on a scripted pawn).</summary>
	float _lookYaw, _lookPitch;

	void Look( float yawRel, float pitch )
	{
		_lookYaw = yawRel;
		_lookPitch = pitch;
		_controller.EyeAngles = new Angles( pitch, _pawn.WorldRotation.Yaw() + yawRel, 0f );
	}

	// ── Scenarios ───────────────────────────────────────────────────────────────────────────

	async System.Threading.Tasks.Task RunScenarios()
	{
		// Neutral reference for chest / head twist: rest right, looking straight ahead.
		Look( 0f, 0f );
		_combat.TestSetLiveSwingDir( SwingDirs.Right );
		await Hold( 1.0f, sample: false );
		CaptureRest();

		await Scenario( "rest right, standing", async () => await Hold( 1.0f ) );

		await Scenario( "swap right to left shoulder", async () => await Aim( SwingDirs.Left, 1.0f ) );

		await Scenario( "swap left to right shoulder", async () => await Aim( SwingDirs.Right, 1.0f ) );

		await Scenario( "swap back and forth quickly", async () =>
		{
			for ( var i = 0; i < 4; i++ )
			{
				_combat.TestSetLiveSwingDir( i % 2 == 0 ? SwingDirs.Left : SwingDirs.Right );
				await Hold( 0.12f );
			}

			await Aim( SwingDirs.Right, 0.8f );
		} );

		await Scenario( "raise for overhead and back", async () =>
		{
			await Aim( SwingDirs.Up, 0.8f );
			await Aim( SwingDirs.Right, 0.8f );
		} );

		await Scenario( "raised to left shoulder and back", async () =>
		{
			await Aim( SwingDirs.Up, 0.8f );
			await Aim( SwingDirs.Left, 0.8f );
			await Aim( SwingDirs.Up, 0.8f );
		} );

		await Scenario( "look sweep all the way round (raised)", async () => await SweepLook( 0f ) );
		_combat.TestSetLiveSwingDir( SwingDirs.Right );
		await Hold( 0.8f, sample: false );

		await Scenario( "look sweep all the way round (rest right)", async () => await SweepLook( 0f ) );
		_combat.TestSetLiveSwingDir( SwingDirs.Left );
		await Hold( 0.8f, sample: false );
		await Scenario( "look sweep all the way round (rest left)", async () => await SweepLook( 0f ) );
		await Scenario( "look up / down while turned", async () =>
		{
			foreach ( var (yaw, pitch) in new[] { (90f, -70f), (90f, 70f), (-150f, -70f), (-150f, 70f), (179f, 0f), (-179f, 0f) } )
			{
				await LookTo( yaw, pitch, 0.4f );
				await Hold( 0.15f );
			}

			await LookTo( 0f, 0f, 0.4f );
		} );

		Look( 0f, 0f );
		foreach ( var (type, dir, label) in new[]
		         {
			         (MeleeAttackTypes.Right, SwingDirs.Right, "right attack"),
			         (MeleeAttackTypes.Left, SwingDirs.Left, "left attack"),
			         (MeleeAttackTypes.Forward, SwingDirs.Up, "overhead attack"),
			         (MeleeAttackTypes.Stab, SwingDirs.Right, "stab")
		         } )
		{
			_combat.TestSetLiveSwingDir( dir );
			await Hold( 0.8f, sample: false );
			await Scenario( label, async () => await Attack( type, heavy: false, hold: 0.25f ) );
		}

		_combat.TestSetLiveSwingDir( SwingDirs.Right );
		await Hold( 0.8f, sample: false );
		await Scenario( "heavy right attack", async () => await Attack( MeleeAttackTypes.Right, heavy: true, hold: 0.25f ) );

		await Scenario( "aim flip then instant left attack", async () =>
		{
			_combat.TestSetLiveSwingDir( SwingDirs.Left );
			await Hold( 0.05f );
			await Attack( MeleeAttackTypes.Left, heavy: false, hold: 0.1f );
		} );

		await Scenario( "overhead from the left shoulder", async () =>
		{
			_combat.TestSetLiveSwingDir( SwingDirs.Left );
			await Hold( 0.8f );
			await Attack( MeleeAttackTypes.Forward, heavy: false, hold: 0.2f );
		} );

		// ── Crouched: the same stances, swaps, look limits and attacks (the crouch input used to ignore the stance).
		_combat.TestSetLiveSwingDir( SwingDirs.Right );
		await LookTo( 0f, 0f, 0.2f );
		_controller.IsDucking = true;
		await Hold( 1.0f, sample: false );
		CaptureRest();

		await Scenario( "crouched: right, left, raised and back", async () =>
		{
			foreach ( var dir in new[] { SwingDirs.Left, SwingDirs.Up, SwingDirs.Right, SwingDirs.Up, SwingDirs.Left, SwingDirs.Right } )
				await Aim( dir, 0.7f );
		} );

		await Scenario( "crouched: stance actually follows the cursor", async () =>
		{
			var seen = new HashSet<int>();
			foreach ( var dir in new[] { SwingDirs.Right, SwingDirs.Left, SwingDirs.Up } )
			{
				_combat.TestSetLiveSwingDir( dir );
				await Hold( 0.7f );
				seen.Add( _body.GetInt( "sword_pose" ) );
			}

			if ( seen.Count < 3 )
				_problems.Add( $"sword_pose took {seen.Count} distinct values for right / left / up" );
			_combat.TestSetLiveSwingDir( SwingDirs.Right );
			await Hold( 0.7f );
		} );

		await Scenario( "crouched: look sweep all the way round", async () => await SweepLook( 0f ) );

		foreach ( var (type, dir, label) in new[]
		         {
			         (MeleeAttackTypes.Right, SwingDirs.Right, "crouched: right attack"),
			         (MeleeAttackTypes.Left, SwingDirs.Left, "crouched: left attack"),
			         (MeleeAttackTypes.Forward, SwingDirs.Up, "crouched: overhead attack"),
			         (MeleeAttackTypes.Stab, SwingDirs.Right, "crouched: stab")
		         } )
		{
			_combat.TestSetLiveSwingDir( dir );
			await Hold( 0.8f, sample: false );
			await Scenario( label, async () => await Attack( type, heavy: false, hold: 0.2f ) );
		}

		await Scenario( "crouch and stand while on the left shoulder", async () =>
		{
			_combat.TestSetLiveSwingDir( SwingDirs.Left );
			await Hold( 0.8f );
			_controller.IsDucking = false;
			await Hold( 0.8f );
			_controller.IsDucking = true;
			await Hold( 0.8f );
		} );

		_controller.IsDucking = false;
		_combat.TestSetLiveSwingDir( SwingDirs.Right );
		await Hold( 1.0f, sample: false );
		CaptureRest();

		await Scenario( "attack while looking sideways", async () =>
		{
			_combat.TestSetLiveSwingDir( SwingDirs.Right );
			await LookTo( 70f, 0f, 0.3f );
			await Attack( MeleeAttackTypes.Right, heavy: false, hold: 0.2f );
			await LookTo( -70f, 0f, 0.3f );
			await Attack( MeleeAttackTypes.Right, heavy: false, hold: 0.2f );
			await LookTo( 0f, 0f, 0.3f );
		} );
	}

	async System.Threading.Tasks.Task Scenario( string name, Func<System.Threading.Tasks.Task> body )
	{
		_scenario = name;
		_problems.Clear();
		_havePrev = false;
		_haveGrip = false;
		_frames = 0;
		_trace.Clear();
		_traceLogged = false;
		await body();
		Check( name, _problems.Count == 0,
			$"{_problems.Count} bad frames of {_frames}; first: {string.Join( " | ", _problems.GetRange( 0, Math.Min( 3, _problems.Count ) ) )}" );
	}

	/// <summary>Set the aim side, hold, and require the graph's stance to have reached the expected pose.</summary>
	async System.Threading.Tasks.Task Aim( byte dir, float seconds )
	{
		_combat.TestSetLiveSwingDir( dir );
		await Hold( seconds );
		var want = dir == SwingDirs.Left ? 1 : dir == SwingDirs.Up ? 7 : 2;
		var got = _body.GetInt( "sword_pose" );
		if ( got != want )
			_problems.Add( $"stance for aim {dir} is sword_pose {got}, want {want}" );
	}

	async System.Threading.Tasks.Task Hold( float seconds, bool sample = true )
	{
		var until = Time.NowDouble + seconds;
		while ( Time.NowDouble < until )
		{
			await Task.Frame();
			if ( sample )
				Sample();
		}
	}

	async System.Threading.Tasks.Task LookTo( float yaw, float pitch, float seconds )
	{
		var y0 = Angles.NormalizeAngle( _lookYaw );
		var p0 = _lookPitch;
		var t0 = Time.NowDouble;
		while ( Time.NowDouble < t0 + seconds )
		{
			var k = (float)((Time.NowDouble - t0) / seconds);
			Look( y0 + (yaw - y0) * k, p0 + (pitch - p0) * k );
			await Task.Frame();
			Sample();
		}

		Look( yaw, pitch );
	}

	/// <summary>Mouse-speed turn: a full circle in 4 s, both directions, then back to front.</summary>
	async System.Threading.Tasks.Task SweepLook( float pitch )
	{
		await LookTo( 0f, pitch, 0.1f );
		for ( var a = 0f; a <= 360f; a += 90f * Time.Delta )
		{
			Look( a, pitch );
			await Task.Frame();
			Sample();
		}

		for ( var a = 360f; a >= -360f; a -= 120f * Time.Delta )
		{
			Look( a, pitch );
			await Task.Frame();
			Sample();
		}

		await LookTo( 0f, 0f, 0.3f );
		await Hold( 0.3f );
	}

	async System.Threading.Tasks.Task Attack( byte type, bool heavy, float hold )
	{
		_anim.BeginMeleeSwingSnap( type );
		await Hold( hold );
		_anim.ReleaseMeleeSwingSnap( type, heavy, 0f );
		await Hold( 1.3f );
	}

	// ── Measurements ───────────────────────────────────────────────────────────────────────

	Vector3 Bone( string name ) => _body.SceneModel.GetBoneWorldTransform( name ).Position;
	Rotation BoneRot( string name ) => _body.SceneModel.GetBoneWorldTransform( name ).Rotation;

	/// <summary>Re-find the human body renderer; false (and a reason) when the pawn or its model is gone.</summary>
	bool EnsureBody( out string why )
	{
		why = null;
		if ( _pawn is null || !_pawn.IsValid() )
		{
			why = "test pawn destroyed";
			return false;
		}

		if ( _body is null || !_body.IsValid() || _body.SceneModel is null )
			_body = _anim.BodyRendererForTests;
		if ( _body is null || !_body.IsValid() || _body.SceneModel is null )
		{
			why = $"no body model (pawn active {_pawn.Active}, renderer {(_body is null ? "null" : $"enabled {_body.Enabled}")})";
			return false;
		}

		return true;
	}

	void CaptureRest()
	{
		if ( !EnsureBody( out var why ) )
		{
			Fail( "body for reference pose", why );
			return;
		}

		var pelvis = BoneRot( "pelvis" );
		_restChestRel = pelvis.Inverse * BoneRot( "spine_2" );
		_restHeadRel = pelvis.Inverse * BoneRot( "head" );
		_haveRest = true;
	}

	static float AngleDeg( Rotation a, Rotation b )
	{
		var d = MathF.Abs( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w );
		return 2f * MathF.Acos( Math.Clamp( d, 0f, 1f ) ) * (180f / MathF.PI);
	}

	static float SegSeg( Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2 )
	{
		var d1 = q1 - p1;
		var d2 = q2 - p2;
		var r = p1 - p2;
		var a = d1.Dot( d1 );
		var e = d2.Dot( d2 );
		var f = d2.Dot( r );
		float s, t;
		if ( a <= 1e-6f && e <= 1e-6f )
			return (p1 - p2).Length;
		if ( a <= 1e-6f )
		{
			s = 0f;
			t = Math.Clamp( f / e, 0f, 1f );
		}
		else
		{
			var c = d1.Dot( r );
			if ( e <= 1e-6f )
			{
				t = 0f;
				s = Math.Clamp( -c / a, 0f, 1f );
			}
			else
			{
				var b = d1.Dot( d2 );
				var den = a * e - b * b;
				s = den > 1e-6f ? Math.Clamp( (b * f - c * e) / den, 0f, 1f ) : 0f;
				t = (b * s + f) / e;
				if ( t < 0f )
				{
					t = 0f;
					s = Math.Clamp( -c / a, 0f, 1f );
				}
				else if ( t > 1f )
				{
					t = 1f;
					s = Math.Clamp( (b - c) / a, 0f, 1f );
				}
			}
		}

		return ((p1 + d1 * s) - (p2 + d2 * t)).Length;
	}

	static float Elbow( Vector3 s, Vector3 e, Vector3 h )
	{
		var a = (s - e).Normal;
		var b = (h - e).Normal;
		return MathF.Acos( Math.Clamp( a.Dot( b ), -1f, 1f ) ) * (180f / MathF.PI);
	}

	void Sample()
	{
		if ( !EnsureBody( out var why ) )
		{
			_frames++;
			_problems.Add( $"f{_frames} {why}" );
			return;
		}

		_frames++;
		var issues = new List<string>();
		var root = _pawn.WorldTransform;

		var neck = Bone( "neck_0" );
		var head = Bone( "head" );
		var up = (head - neck).Normal;
		var headC = head + up * (0.09f * M);
		var torso0 = Bone( "spine_0" );
		var torso1 = neck - up * (0.06f * M);
		var pelvis = Bone( "pelvis" );
		var hold = _body.SceneModel.GetBoneWorldTransform( "hold_R" );
		var blade0 = hold.Position + hold.Rotation.Up * (0.07f * M);
		var blade1 = hold.Position + hold.Rotation.Up * (0.92f * M);
		var shR = Bone( "arm_upper_R" );
		var elR = Bone( "arm_lower_R" );
		var haR = Bone( "hand_R" );
		var shL = Bone( "arm_upper_L" );
		var elL = Bone( "arm_lower_L" );
		var haL = Bone( "hand_L" );

		void Clear( string what, float distUnits, float radius, float min )
		{
			var clear = distUnits / M - radius;
			if ( clear < min )
				issues.Add( $"{what} {clear:0.000}m<{min:0.00}" );
		}

		Clear( "blade-head", SegSeg( blade0, blade1, headC, headC ), HeadR, BladeHeadClear );
		Clear( "blade-neck", SegSeg( blade0, blade1, neck, head ), NeckR, BladeNeckClear );
		Clear( "blade-torso", SegSeg( blade0, blade1, torso0, torso1 ), TorsoR, BladeTorsoClear );
		Clear( "blade-pelvis", SegSeg( blade0, blade1, pelvis, pelvis ), PelvisR, BladePelvisClear );
		Clear( "forearmR-torso", SegSeg( elR, haR, torso0, torso1 ), TorsoR + ArmR, ArmTorsoClear );
		Clear( "handL-torso", SegSeg( haL, haL, torso0, torso1 ), TorsoR + HandR, ArmTorsoClear );
		Clear( "handL-pelvis", SegSeg( haL, haL, pelvis, pelvis ), PelvisR + HandR, ArmTorsoClear );

		foreach ( var (label, a) in new[] { ("elbowR", Elbow( shR, elR, haR )), ("elbowL", Elbow( shL, elL, haL )) } )
		{
			if ( a < ElbowMin )
				issues.Add( $"{label} folded {a:0}°" );
			if ( a > ElbowMax )
				issues.Add( $"{label} hyperextended {a:0}°" );
		}

		var pelvisRot = BoneRot( "pelvis" );
		var chestRot = BoneRot( "spine_2" );
		var headRot = BoneRot( "head" );
		if ( _haveRest )
		{
			var chestTwist = AngleDeg( _restChestRel, pelvisRot.Inverse * chestRot );
			var headTwist = AngleDeg( _restHeadRel, pelvisRot.Inverse * headRot );
			if ( chestTwist > ChestTwistMax )
				issues.Add( $"chest twisted {chestTwist:0}° from the hips" );
			if ( headTwist > HeadTwistMax )
				issues.Add( $"head turned {headTwist:0}° from the hips" );
		}

		// The sword must not move in the fist.
		if ( _anim.MeleeHeldPropForTests is { } prop && prop.IsValid() )
		{
			var rel = hold.ToLocal( prop.WorldTransform );
			if ( !_haveGrip )
			{
				_gripRef = rel;
				_haveGrip = true;
			}
			else
			{
				var slip = (rel.Position - _gripRef.Position).Length / M;
				var turn = AngleDeg( rel.Rotation, _gripRef.Rotation );
				if ( slip > GripSlipMax || turn > GripTurnMax )
					issues.Add( $"sword moved in the fist {slip * 100f:0.0}cm {turn:0}°" );
			}
		}
		else
		{
			issues.Add( "no held sword" );
		}

		// Continuity: positions relative to the pawn, rotations as world rotations (the pawn never turns here).
		var hand = root.PointToLocal( haR );
		var pel = root.PointToLocal( pelvis );
		var hd = root.PointToLocal( head );
		// A short frame must not inflate the speed: a real snap is a big jump within one 60 fps frame.
		var dt = MathF.Max( Time.Delta, 1f / 60f );
		if ( _havePrev )
		{
			void Speed( string what, Vector3 a, Vector3 b, float max )
			{
				var v = (a - b).Length / M / dt;
				if ( v > max )
					issues.Add( $"{what} jumped {v:0.0}m/s" );
			}

			void Turn( string what, Rotation a, Rotation b, float max )
			{
				var w = AngleDeg( a, b ) / dt;
				if ( w > max )
					issues.Add( $"{what} snapped {w:0}°/s" );
			}

			Speed( "hand", hand, _prevHand, HandSpeedMax );
			Speed( "pelvis", pel, _prevPelvis, PelvisSpeedMax );
			Speed( "head", hd, _prevHead, HeadSpeedMax );
			Turn( "pelvis", pelvisRot, _prevPelvisRot, PelvisTurnMax );
			Turn( "chest", chestRot, _prevChestRot, ChestTurnMax );
			Turn( "head", headRot, _prevHeadRot, HeadTurnMax );
		}

		_prevHand = hand;
		_prevPelvis = pel;
		_prevHead = hd;
		_prevPelvisRot = pelvisRot;
		_prevChestRot = chestRot;
		_prevHeadRot = headRot;
		_havePrev = true;

		var summary = $"f{_frames} dt={Time.Delta * 1000f:0.0}ms pose={_body.GetInt( "sword_pose" )} atk={_body.GetInt( "sword_attack" )} " +
		              $"duck={_body.GetFloat( "duck" ):0.0} chestVsHips={AngleDeg( pelvisRot, chestRot ):0} bodyYaw={_body.WorldRotation.Yaw():0} " +
		              $"hand=({hand.x / M:0.00},{hand.y / M:0.00},{hand.z / M:0.00})";
		_trace.Enqueue( summary );
		while ( _trace.Count > 6 )
			_trace.Dequeue();

		if ( issues.Count > 0 )
		{
			_problems.Add( $"f{_frames} {string.Join( ", ", issues )}" );
			if ( !_traceLogged )
			{
				_traceLogged = true;
				Log.Info( $"{Tag} trace '{_scenario}': {string.Join( " || ", _trace )} || ISSUE {string.Join( ", ", issues )}" );
			}
		}
	}

	// ── Reporting ──────────────────────────────────────────────────────────────────────────

	void Check( string name, bool ok, string detail )
	{
		if ( ok )
		{
			_passed++;
			Log.Info( $"{Tag} PASS {name}" );
			return;
		}

		Fail( name, detail );
	}

	void Fail( string name, string detail )
	{
		_failures.Add( name );
		Log.Warning( $"{Tag} FAIL {name} — {detail}" );
	}
}
