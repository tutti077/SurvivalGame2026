using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>grapple_test</c>: regression suite for the grapple on players, the landing skip (Mark 2026-11-16) and the vehicle
/// tow (Mark 2026-11-19: a pawn hooked to a moving body rides behind it at its speed and slings wide on a turn). Run it in
/// play mode in any scene: two real <c>basicplayer</c> pawns (input, camera and HUD off; movement on, steered through
/// <see cref="PlayerController.WishVelocity"/> from their own walk speed, the way a held W key steers) stand on a
/// temporary floor far above the scene. The shooter hooks the other through the host attach a client's
/// <c>RpcRequestAttach</c> runs; the hooked pawn then applies the slow from the synced state, as its
/// owner does, and the shooter must keep its full speed. Expected numbers come from the live tuning (<see cref="PlayerMovement.PlayerTetherSeconds"/>,
/// <see cref="PlayerMovement.HookedPlayerSpeedScale"/>, the Grapple Landing Skip group), so retuning never fails it.
/// Console: one <c>[GrappleTest] PASS|FAIL</c> line per case, then <c>[GrappleTest] RESULT …</c>.
/// </summary>
public sealed class GrappleTest : Component
{
	const string Tag = "[GrappleTest]";
	const string PawnPrefab = "prefabs/player/basicplayer.prefab";

	/// <summary>Far above any scene content, and apart from <c>combat_test</c>'s arena.</summary>
	static readonly Vector3 Origin = new( 6000f, 0f, 30000f );

	readonly List<string> _failures = new();
	int _passed;
	GameObject _floor;
	GameObject _shooterGo;
	GameObject _targetGo;
	PlayerMovement _shooter;
	PlayerMovement _target;
	PlayerController _shooterCtl;
	PlayerController _targetCtl;
	Vector3 _shooterWish;
	Vector3 _targetWish;
	GameObject _sled;
	Rigidbody _sledBody;
	/// <summary>Velocity the tow sled is held at every physics step (units/s).</summary>
	Vector3 _sledVelocity;
	/// <summary>Turn rate of the sled's velocity (radians/s, + = toward +Y).</summary>
	float _sledTurnRate;

	static GrappleTest _running;

	[ConCmd( "grapple_test" )]
	public static void ConCmdGrappleTest()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || !Sandbox.Game.IsPlaying )
		{
			Log.Warning( $"{Tag} enter play mode first (any scene)." );
			return;
		}

		if ( _running.IsValid() )
		{
			Log.Warning( $"{Tag} already running." );
			return;
		}

		var go = scene.CreateObject();
		go.Name = "GrappleTest";
		go.NetworkMode = NetworkMode.Never;
		_running = go.Components.Create<GrappleTest>();
	}

	protected override void OnStart() => _ = RunAsync();

	protected override void OnDestroy()
	{
		_shooterGo?.Destroy();
		_targetGo?.Destroy();
		_floor?.Destroy();
		_sled?.Destroy();
	}

	/// <summary>The tow sled is a body on rails: its velocity is written every physics step, as a driven vehicle's would be.</summary>
	protected override void OnFixedUpdate()
	{
		if ( !_sledBody.IsValid() )
			return;

		if ( MathF.Abs( _sledTurnRate ) > 1e-5f )
			_sledVelocity = Rotation.FromYaw( _sledTurnRate * Time.Delta * (180f / MathF.PI) ) * _sledVelocity;
		_sledBody.Velocity = _sledVelocity;
		_sledBody.AngularVelocity = Vector3.Zero;
	}

	/// <summary>Steer both pawns every frame: a direction × their own current walk speed (what a held key asks for).</summary>
	protected override void OnUpdate()
	{
		if ( _shooterCtl.IsValid() )
			_shooterCtl.WishVelocity = _shooterWish * _shooterCtl.WalkSpeed;
		if ( _targetCtl.IsValid() )
			_targetCtl.WishVelocity = _targetWish * _targetCtl.WalkSpeed;
	}

	async System.Threading.Tasks.Task RunAsync()
	{
		Log.Info( $"{Tag} starting" );
		try
		{
			SpawnFloor();
			_shooterGo = SpawnPawn( "GrappleTest_Shooter", Origin + new Vector3( -200f, 0f, 14f ) );
			_targetGo = SpawnPawn( "GrappleTest_Target", Origin + new Vector3( 200f, 0f, 14f ) );
			_shooter = _shooterGo?.Components.Get<PlayerMovement>();
			_target = _targetGo?.Components.Get<PlayerMovement>();
			_shooterCtl = _shooterGo?.Components.Get<PlayerController>();
			_targetCtl = _targetGo?.Components.Get<PlayerController>();
			if ( _shooter is null || _target is null || _shooterCtl is null || _targetCtl is null )
				Fail( "spawn", $"could not clone {PawnPrefab} with PlayerMovement + PlayerController" );
			else
			{
				await Seconds( 0.6f );   // land on the floor, OnStart wiring
				await RunTether();
				await RunSkip();
				await RunVehicleTow();
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

	// ── Player tether ────────────────────────────────────────────────────────────────────────

	async System.Threading.Tasks.Task RunTether()
	{
		// Both walk +X (the target away from the shooter): free speeds first.
		_shooterWish = Vector3.Forward;
		_targetWish = Vector3.Forward;
		await Seconds( 0.6f );
		var freeWalk = _targetCtl.WalkSpeed;
		var shooterFreeWalk = _shooterCtl.WalkSpeed;
		var (freeSpeed, shooterFreeSpeed) = await AverageFlatSpeeds( 0.3f );

		var hooked = _shooter.TestHostHookPlayer( _targetGo );
		var hookedAt = Time.NowDouble;
		Check( "a hook on a player lands (host attach path)", hooked && _target.IsHeldByPlayerGrapple,
			$"shooter attached {_shooter.GrappleAttached}, player id {_shooter.GrappleAttachPlayerId}, target held {_target.IsHeldByPlayerGrapple}" );
		if ( !hooked )
			return;

		var tether = Math.Max( 0.05f, _shooter.PlayerTetherSeconds );
		var scale = Math.Clamp( _target.HookedPlayerSpeedScale, 0.05f, 1f );
		var distStart = Vector3.DistanceBetween( _shooterGo.WorldPosition, _targetGo.WorldPosition );

		// The shooter turns and walks away from the hooked pawn, so the rope has to stretch.
		_shooterWish = -Vector3.Forward;
		await Seconds( Math.Min( 0.25f, tether * 0.3f ) );
		var heldWalk = _targetCtl.WalkSpeed;
		var shooterHeldWalk = _shooterCtl.WalkSpeed;
		var (heldSpeed, shooterHeldSpeed) = await AverageFlatSpeeds( Math.Min( 0.3f, tether * 0.4f ) );
		Check( "the hooked player walks at the hooked speed",
			MathF.Abs( heldWalk - freeWalk * scale ) <= freeWalk * 0.02f,
			$"walk {heldWalk:0.#} u/s while hooked, {freeWalk:0.#} free, want x{scale:0.##} = {freeWalk * scale:0.#}" );
		Check( "the hooked player really moves slower",
			freeSpeed > 1f && MathF.Abs( heldSpeed / freeSpeed - scale ) <= 0.15f,
			$"measured {heldSpeed:0.#} u/s hooked vs {freeSpeed:0.#} free (x{heldSpeed / Math.Max( 1f, freeSpeed ):0.##}, want x{scale:0.##})" );
		Check( "the grappler keeps full speed while the hook holds",
			shooterFreeWalk > 1f && MathF.Abs( shooterHeldWalk - shooterFreeWalk ) <= shooterFreeWalk * 0.02f
			&& shooterFreeSpeed > 1f && MathF.Abs( shooterHeldSpeed / shooterFreeSpeed - 1f ) <= 0.15f,
			$"shooter walk {shooterHeldWalk:0.#} u/s while hooking, {shooterFreeWalk:0.#} free; measured {shooterHeldSpeed:0.#} u/s vs {shooterFreeSpeed:0.#} free (x{shooterHeldSpeed / Math.Max( 1f, shooterFreeSpeed ):0.##}, want x1)" );
		Check( "nobody is reeled in: the rope stretches",
			Vector3.DistanceBetween( _shooterGo.WorldPosition, _targetGo.WorldPosition ) > distStart + 4f,
			$"distance {distStart:0.#} -> {Vector3.DistanceBetween( _shooterGo.WorldPosition, _targetGo.WorldPosition ):0.#} u" );

		var until = hookedAt + tether + 1.0;
		while ( _shooter.GrappleAttached && Time.NowDouble < until )
			await Task.Frame();
		var held = Time.NowDouble - hookedAt;
		Check( "the hook breaks by itself after the tether time",
			!_shooter.GrappleAttached && held >= tether - 0.05 && held <= tether + 0.25,
			$"held {held:0.###} s, tether {tether:0.##} s, still attached {_shooter.GrappleAttached}" );

		await Seconds( 0.15f );
		Check( "the freed player walks at full speed again",
			!_target.IsHeldByPlayerGrapple && MathF.Abs( _targetCtl.WalkSpeed - freeWalk ) <= freeWalk * 0.02f,
			$"held {_target.IsHeldByPlayerGrapple}, walk {_targetCtl.WalkSpeed:0.#} u/s, free {freeWalk:0.#}" );

		_shooterWish = Vector3.Zero;
		_targetWish = Vector3.Zero;
	}

	// ── Landing skip ─────────────────────────────────────────────────────────────────────────

	async System.Threading.Tasks.Task RunSkip()
	{
		var unitsPerMeter = Math.Max( 1f, _targetCtl.BodyHeight ) / 1.8f;
		var launch = _target.GrappleSkipMinSpeedMeters * unitsPerMeter * 2.5f;
		var minSkip = _target.GrappleSkipMinSpeedMeters * unitsPerMeter;

		// Control: the same fall without a rope release lands normally.
		var plain = await Fall( armed: false, launch, 1.5f );
		// Released a rope in the air: skips.
		var skip = await Fall( armed: true, launch, 1.5f );

		Check( "a fast touchdown after a rope release skips",
			skip.Skips >= 2,
			$"{skip.Skips} skips (launch {launch / unitsPerMeter:0.#} m/s, min {_target.GrappleSkipMinSpeedMeters:0.#} m/s)" );
		var keep = Math.Clamp( _target.GrappleSkipKeep, 0f, 1f );
		Check( "the first skip keeps its share of the speed",
			skip.SpeedAfterFirst >= launch * keep * 0.85f,
			$"{skip.SpeedAfterFirst:0.#} u/s after the first touchdown, launch {launch:0.#} x keep {keep:0.##} = {launch * keep:0.#}" );
		Check( "a plain landing still stops at run speed",
			plain.Skips == 0 && plain.SpeedAfterFirst <= _target.DesignRunSpeedForTests + 30f,
			$"{plain.Skips} skips, {plain.SpeedAfterFirst:0.#} u/s after landing" );
		Check( "skipping carries the pawn much further",
			skip.Distance > plain.Distance * 1.5f,
			$"skip {skip.Distance / unitsPerMeter:0.#} m vs plain {plain.Distance / unitsPerMeter:0.#} m in 1.5 s" );

		await Seconds( 1.5f );
		Check( "the skip ends in a normal landing",
			!_target.IsGrappleSkipArmed && FlatSpeed( _targetCtl ) < minSkip,
			$"armed {_target.IsGrappleSkipArmed}, {FlatSpeed( _targetCtl ):0.#} u/s ({_target.GrappleSkipCount} skips)" );
	}

	readonly struct FallResult
	{
		public FallResult( int skips, float speedAfterFirst, float distance )
		{
			Skips = skips;
			SpeedAfterFirst = speedAfterFirst;
			Distance = distance;
		}

		public int Skips { get; }
		public float SpeedAfterFirst { get; }
		public float Distance { get; }
	}

	/// <summary>Drop the target from 3 m, falling 8 m/s and moving <paramref name="speed"/> along +Y, no input held.</summary>
	async System.Threading.Tasks.Task<FallResult> Fall( bool armed, float speed, float seconds )
	{
		var unitsPerMeter = Math.Max( 1f, _targetCtl.BodyHeight ) / 1.8f;
		_targetWish = Vector3.Zero;
		var start = Origin + new Vector3( armed ? 400f : -400f, -2400f, 14f + 3f * unitsPerMeter );
		_targetGo.WorldPosition = start;
		_targetGo.Transform.ClearInterpolation();
		_targetCtl.PreventGrounding( 0.15f );
		_targetCtl.Body.Velocity = new Vector3( 0f, speed, -8f * unitsPerMeter );
		_target.TestSetGrappleSkipArmed( armed );

		var wasGround = false;
		var landed = false;
		var speedAfterFirst = 0f;
		var landedAt = 0.0;
		var end = Time.NowDouble + seconds;
		while ( Time.NowDouble < end )
		{
			await Task.Frame();
			var ground = _targetCtl.IsOnGround;
			if ( !landed && ((ground && !wasGround) || _target.GrappleSkipCount > 0) )
			{
				landed = true;
				landedAt = Time.NowDouble;
			}

			// Read the speed a few physics steps after the first touchdown (the land scrub runs for several).
			if ( landed && speedAfterFirst <= 0f && Time.NowDouble - landedAt >= 0.06 )
				speedAfterFirst = FlatSpeed( _targetCtl );
			wasGround = ground;
		}

		return new FallResult( _target.GrappleSkipCount, speedAfterFirst, FlatDistance( _targetGo.WorldPosition, start ) );
	}

	// ── Vehicle tow ──────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// A grapple-tagged rigidbody (what a buggy or the hover platform is to the hook) drives straight, then round a
	/// circle, then stops, with the shooter hooked to its tail from 6 m behind.
	/// </summary>
	async System.Threading.Tasks.Task RunVehicleTow()
	{
		var unitsPerMeter = TerrainWorldUnits.MetersToEngine( 1f );
		var floorTop = Origin.z + 10f;
		var towSpeed = MathF.Max( _shooter.RideTowSlideSpeedMeters * 2.4f, 10f ) * unitsPerMeter;
		var turnRadius = 10f * unitsPerMeter;
		var start = new Vector3( Origin.x - 3000f, Origin.y + 2000f, floorTop );

		_shooterWish = Vector3.Zero;
		_shooterGo.WorldPosition = start + Vector3.Up * 4f;
		_shooterGo.Transform.ClearInterpolation();
		_shooterCtl.Body.Velocity = Vector3.Zero;

		_sled = Scene.CreateObject();
		_sled.Name = "GrappleTest_TowSled";
		_sled.NetworkMode = NetworkMode.Never;
		_sled.Tags.Add( PlayerMovement.GrappleSurfaceTag );
		_sled.WorldPosition = start + new Vector3( 6f * unitsPerMeter + 30f, 0f, 2.2f * unitsPerMeter );
		var box = _sled.Components.Create<BoxCollider>();
		box.Scale = new Vector3( 60f, 40f, 20f );
		_sledBody = _sled.Components.Create<Rigidbody>();
		_sledBody.Gravity = false;
		_sledVelocity = Vector3.Zero;
		_sledTurnRate = 0f;
		await Seconds( 0.5f );

		var hookLocal = new Vector3( -30f, 0f, 0f );
		var hooked = _shooter.TestHostHookRide( _sled, _sled.WorldTransform.PointToWorld( hookLocal ) );
		Check( "a hook on a moving body rides it", hooked,
			$"attached {_shooter.GrappleAttached}, ride id {_shooter.GrappleAttachRideId}, sled {_sled.Id}" );
		if ( !hooked )
			return;

		var rope = _shooter.GrappleRopeLengthEngine;

		// Straight: 2.5 s at tow speed.
		_sledVelocity = Vector3.Forward * towSpeed;
		await Seconds( 1.5f );
		var speedSum = 0f;
		var n = 0;
		var maxHeight = 0f;
		var maxStretch = 0f;
		var slid = true;
		var end = Time.NowDouble + 1.0;
		while ( Time.NowDouble < end )
		{
			await Task.Frame();
			speedSum += FlatSpeed( _shooterCtl );
			n++;
			maxHeight = MathF.Max( maxHeight, _shooterGo.WorldPosition.z - floorTop );
			maxStretch = MathF.Max( maxStretch, Vector3.DistanceBetween( _shooterGo.WorldPosition, _sled.WorldTransform.PointToWorld( hookLocal ) ) - rope );
			slid &= _shooter.IsGrappleTowSliding;
		}

		var towed = n > 0 ? speedSum / n : 0f;
		Check( "a towed pawn inherits the tow speed",
			MathF.Abs( towed / towSpeed - 1f ) <= 0.15f,
			$"pawn {towed / unitsPerMeter:0.#} m/s behind a body at {towSpeed / unitsPerMeter:0.#} m/s" );
		Check( "a towed pawn slides on the ground: no kite",
			slid && maxHeight <= 1f * unitsPerMeter,
			$"sliding {slid}, up to {maxHeight / unitsPerMeter:0.##} m off the floor" );
		Check( "the rope holds its length without snapping the pawn",
			maxStretch <= 1f * unitsPerMeter,
			$"stretched {maxStretch / unitsPerMeter:0.##} m past {rope / unitsPerMeter:0.#} m" );

		// Turn: the body goes round a circle toward +Y; the pawn has to swing to the outside of it and speed up.
		var centre = _sled.WorldPosition.WithZ( 0f ) + Vector3.Left * turnRadius;
		_sledTurnRate = towSpeed / turnRadius;
		var maxOutside = 0f;
		var peakSpeed = 0f;
		var minSpeed = float.MaxValue;
		var turnHeight = 0f;
		end = Time.NowDouble + 4.0;
		while ( Time.NowDouble < end )
		{
			await Task.Frame();
			var pawnRadius = FlatDistance( _shooterGo.WorldPosition, centre );
			var sledRadius = FlatDistance( _sled.WorldPosition, centre );
			maxOutside = MathF.Max( maxOutside, pawnRadius - sledRadius );
			var sp = FlatSpeed( _shooterCtl );
			peakSpeed = MathF.Max( peakSpeed, sp );
			minSpeed = MathF.Min( minSpeed, sp );
			turnHeight = MathF.Max( turnHeight, _shooterGo.WorldPosition.z - floorTop );
		}

		Log.Info( $"{Tag} tow turn: outside {maxOutside / unitsPerMeter:0.##} m, peak {peakSpeed / unitsPerMeter:0.#} m/s, min {minSpeed / unitsPerMeter:0.#} m/s, body {towSpeed / unitsPerMeter:0.#} m/s, up {turnHeight / unitsPerMeter:0.##} m" );
		Check( "a turn slings the towed pawn to the outside",
			maxOutside >= 1f * unitsPerMeter,
			$"pawn got {maxOutside / unitsPerMeter:0.##} m outside the body's circle (rope {rope / unitsPerMeter:0.#} m)" );
		Check( "the whip is faster than the body",
			peakSpeed >= towSpeed * 1.08f,
			$"peak {peakSpeed / unitsPerMeter:0.#} m/s, body {towSpeed / unitsPerMeter:0.#} m/s" );
		Check( "the pawn is never stuck in the turn",
			minSpeed >= towSpeed * 0.5f && turnHeight <= 1.5f * unitsPerMeter,
			$"slowest {minSpeed / unitsPerMeter:0.#} m/s of {towSpeed / unitsPerMeter:0.#} m/s, up to {turnHeight / unitsPerMeter:0.##} m off the floor" );

		// Stop: the body halts, the pawn slides out and stands again.
		_sledTurnRate = 0f;
		_sledVelocity = Vector3.Zero;
		await Seconds( 4f );
		Check( "the pawn stands again once the tow stops",
			!_shooter.IsGrappleTowSliding && _shooterCtl.IsOnGround && _shooter.GrappleAttached,
			$"sliding {_shooter.IsGrappleTowSliding}, grounded {_shooterCtl.IsOnGround}, attached {_shooter.GrappleAttached}, {FlatSpeed( _shooterCtl ):0.#} u/s" );

		_shooter.TestHostDetach();
		_sledBody = null;
		_sled.Destroy();
		_sled = null;
	}

	// ── Setup + helpers ──────────────────────────────────────────────────────────────────────

	void SpawnFloor()
	{
		_floor = Scene.CreateObject();
		_floor.Name = "GrappleTest_Floor";
		_floor.NetworkMode = NetworkMode.Never;
		_floor.WorldPosition = Origin;
		var box = _floor.Components.Create<BoxCollider>();
		box.Scale = new Vector3( 8000f, 8000f, 20f );
		box.Static = true;
	}

	static GameObject SpawnPawn( string name, Vector3 at )
	{
		var template = BuildPrefabUtility.GetTemplate( PawnPrefab );
		if ( template is null )
			return null;

		var go = template.Clone( new Transform( at ), name: name );
		go.NetworkMode = NetworkMode.Never;
		// The live player's PlayerSpawnSeparation culls local-only pawns unless they are tagged as test pawns.
		go.Tags.Add( "test_pawn" );

		// Movement stays on (it is what is under test); input, look and camera come from the suite, not the keyboard.
		if ( go.Components.Get<PlayerController>() is { } controller )
		{
			controller.UseInputControls = false;
			controller.UseLookControls = false;
			controller.UseCameraControls = false;
			controller.EyeAngles = new Angles( 0f, 0f, 0f );
		}

		// A scripted pawn must not load or overwrite the player's saved hotbar bindings.
		if ( go.Components.Get<PlayerHotbar>() is { } hotbar )
			hotbar.UsesSavedBindings = false;

		DisableAll<CameraComponent>( go );
		DisableAll<ScreenPanel>( go );
		DisableAll<PlayerScreenHud>( go );
		DisableAll<PlayerCrosshair>( go );
		DisableAll<PlayerGameMenuController>( go );
		DisableAll<PlayerHotbarController>( go );
		DisableAll<PlayerInventoryInteraction>( go );
		DisableAll<PlayerSpawnSeparation>( go );
		return go;
	}

	static void DisableAll<T>( GameObject go ) where T : Component
	{
		foreach ( var c in go.Components.GetAll<T>( FindMode.EverythingInSelfAndDescendants ) )
			c.Enabled = false;
	}

	static float FlatSpeed( PlayerController ctl ) => ctl.Velocity.WithZ( 0f ).Length;

	static float FlatDistance( Vector3 a, Vector3 b ) => (a - b).WithZ( 0f ).Length;

	/// <summary>Average ground speed of the target and the shooter over the same frames.</summary>
	async System.Threading.Tasks.Task<(float Target, float Shooter)> AverageFlatSpeeds( float seconds )
	{
		var target = 0f;
		var shooter = 0f;
		var n = 0;
		var end = Time.NowDouble + seconds;
		while ( Time.NowDouble < end )
		{
			await Task.Frame();
			target += FlatSpeed( _targetCtl );
			shooter += FlatSpeed( _shooterCtl );
			n++;
		}

		return n > 0 ? (target / n, shooter / n) : (0f, 0f);
	}

	async System.Threading.Tasks.Task Seconds( float seconds )
	{
		var end = Time.NowDouble + seconds;
		while ( Time.NowDouble < end )
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
