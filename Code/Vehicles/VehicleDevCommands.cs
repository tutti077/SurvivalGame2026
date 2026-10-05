using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Console helpers for filming and testing vehicles from the editor, which cannot press keys (same
/// idea as the <c>swing</c> hack): <c>vehicle_spawn</c> drops a dune buggy in front of the local pawn
/// through the normal hammer placement path, <c>vehicle_enter</c> / <c>vehicle_exit</c> seat and
/// unseat the local pawn, <c>vehicle_fuel [n]</c> fills the tank, and
/// <c>vehicle_drive &lt;throttle&gt; &lt;steer&gt; [seconds]</c> holds simulated pedals and wheel for the
/// local driver. Host / offline only, like the other hacks.
/// </summary>
public static partial class VehicleDevCommands
{
	static float _simThrottle;
	static float _simSteer;
	static double _simUntilGlobal;

	/// <summary><see cref="Vehicle"/> owner input tick: simulated pedals while a <c>vehicle_drive</c> hold or a <c>vehicle_test</c> runs.</summary>
	public static bool TryGetSimulatedInput( out float throttle, out float steer )
	{
		throttle = 0f;
		steer = 0f;

		if ( _test is not null )
			return _test.Tick( out throttle, out steer );

		if ( RealTime.GlobalNow >= _simUntilGlobal )
			return false;

		throttle = _simThrottle;
		steer = _simSteer;
		return true;
	}

	static ScriptedTest _test;

	/// <summary>
	/// Usage: <c>vehicle_test</c> — on the local driver's vehicle, runs a timed script and logs the numbers
	/// the console cannot time by hand: (1) full throttle 3 s, (2) S while rolling for 1.2 s — must brake,
	/// never reverse, (3) S from a stop for 3 s — must reverse, (4) W + A for 3 s — turns left, chassis
	/// stays upright, (5) fuel burn with the tank timer shortened to 2 s per unit.
	/// </summary>
	[ConCmd( "vehicle_test" )]
	public static void ConCmdTest()
	{
		var pawn = FindLocalPawn();
		var vehicle = pawn?.Components.Get<PlayerMovement>()?.SeatedVehicle;
		if ( vehicle is null || !vehicle.IsValid() )
		{
			Log.Warning( "[Vehicle] vehicle_test: the local pawn must be driving (vehicle_spawn, vehicle_enter, vehicle_fuel first)." );
			return;
		}

		_test = new ScriptedTest( vehicle );
		Log.Info( "[Vehicle] test started." );
	}

	class ScriptedTest
	{
		protected readonly Vehicle _vehicle;
		readonly float _savedSecondsPerUnit;
		int _fuelAtStart;
		int _phase;
		double _phaseStartedAt = -1;
		float _minSignedSpeedWhileBraking = float.MaxValue;
		float _speedAtBrakeEnd;
		float _reverseSpeed;
		float _yawAtTurnStart;
		float _yawAccumulated;
		float _lastYaw;
		float _minUpZ = 1f;
		float _peakSpeed;
		float _nextSampleAt;

		public ScriptedTest( Vehicle vehicle )
		{
			_vehicle = vehicle;
			_savedSecondsPerUnit = vehicle.SecondsPerFuelUnit;
			_fuelAtStart = -1;

			// Known stretch of testscene1 ground, facing +x: the full-throttle leg covers ~80 m.
			var start = new Vector3( 600f, 0f, 0f );
			var tr = vehicle.Scene.Trace.Ray( start + Vector3.Up * 400f, start - Vector3.Up * 400f )
				.IgnoreGameObjectHierarchy( vehicle.GameObject )
				.WithoutTags( "player", "trigger", "worlddrop" )
				.Run();
			var rest = TerrainWorldUnits.MetersToEngine( vehicle.SuspensionLengthMeters + vehicle.WheelRadiusMeters );
			vehicle.WorldPosition = ( tr.Hit ? tr.HitPosition : start ) + Vector3.Up * rest;
			vehicle.WorldRotation = Rotation.Identity;
			vehicle.GameObject.Transform.ClearInterpolation();
			var body = vehicle.Components.Get<Rigidbody>();
			if ( body is not null && body.IsValid() )
			{
				body.Velocity = Vector3.Zero;
				body.AngularVelocity = Vector3.Zero;
			}
		}

		public bool Tick( out float throttle, out float steer )
		{
			throttle = 0f;
			steer = 0f;
			if ( _vehicle is null || !_vehicle.IsValid() )
			{
				_test = null;
				return false;
			}

			var now = RealTime.GlobalNow;
			if ( _phaseStartedAt < 0 )
			{
				_phaseStartedAt = now;
				_nextSampleAt = 0f;
			}
			var t = (float)( now - _phaseStartedAt );
			var speed = _vehicle.SyncedSpeedMetersPerSecond;
			_minUpZ = Math.Min( _minUpZ, _vehicle.WorldRotation.Up.z );
			if ( _fuelAtStart < 0 )
			{
				_fuelAtStart = _vehicle.FuelUnits;
				_lastYaw = _vehicle.WorldRotation.Yaw();
			}
			var yawNow = _vehicle.WorldRotation.Yaw();
			_yawAccumulated += MathX.DeltaDegrees( _lastYaw, yawNow );
			_lastYaw = yawNow;

			if ( t >= _nextSampleAt )
			{
				_nextSampleAt += 0.5f;
				Log.Info( $"[Vehicle test]   phase {_phase + 1} t={t:0.0}s speed={speed:0.00} m/s yaw={_vehicle.WorldRotation.Yaw():0} up.z={_vehicle.WorldRotation.Up.z:0.00} fuel={_vehicle.FuelUnits} {_vehicle.DebugWheels()}" );
			}

			OnTick( speed );

			if ( GetType() != typeof( ScriptedTest ) )
			{
				var keep = RunPhases( t, speed, out throttle, out steer );
				if ( !keep )
					_test = null;
				return keep;
			}

			switch ( _phase )
			{
				case 0:   // full throttle
					throttle = 1f;
					_peakSpeed = Math.Max( _peakSpeed, speed );
					if ( t >= 3f )
					{
						Log.Info( $"[Vehicle test] 1 throttle 3 s: {speed:0.0} m/s ({speed * 2.23694f:0} mph), peak {_peakSpeed:0.0}" );
						Next();
					}
					return true;
				case 1:   // S while rolling: brake, no reverse
					throttle = -1f;
					_minSignedSpeedWhileBraking = Math.Min( _minSignedSpeedWhileBraking, speed );
					if ( t >= 1.2f )
					{
						_speedAtBrakeEnd = speed;
						Log.Info( $"[Vehicle test] 2 S while rolling 1.2 s: end {speed:0.00} m/s (reverse after the stop is by design when S stays held; the samples above must show speed falling to ~0 before it goes negative)" );
						Next();
					}
					return true;
				case 2:   // let go briefly so the stopped band is clean
					if ( t >= 0.4f )
						Next();
					return true;
				case 3:   // S from a stop: reverse
					throttle = -1f;
					_reverseSpeed = Math.Min( _reverseSpeed, speed );
					if ( t >= 3f )
					{
						var ok = _reverseSpeed < -2f;
						Log.Info( $"[Vehicle test] 3 S from stop 3 s: {speed:0.0} m/s — {( ok ? "OK reverses" : "FAIL no reverse" )}" );
						_yawAtTurnStart = _yawAccumulated;
						Next();
					}
					return true;
				case 4:   // W + A: turn left, stay upright
					throttle = 1f;
					steer = 1f;
					if ( t >= 3f )
					{
						var dyaw = _yawAccumulated - _yawAtTurnStart;   // accumulated, so a spin past 180° still reads right
						Log.Info( $"[Vehicle test] 4 W+A 3 s: yaw change {dyaw:0} deg (positive = left), speed {speed:0.0} m/s, lowest up.z {_minUpZ:0.00} — {( dyaw > 10f ? "OK turns" : "FAIL no turn" )} / {( _minUpZ > 0.9f ? "OK upright" : "FAIL leaned" )}" );
						_vehicle.SecondsPerFuelUnit = 2f;
						Next();
					}
					return true;
				case 5:   // fuel burn at 2 s / unit for 5 s of throttle
					throttle = 1f;
					if ( t >= 5f )
					{
						_vehicle.SecondsPerFuelUnit = _savedSecondsPerUnit;
						Log.Info( $"[Vehicle test] 5 fuel: {_fuelAtStart} -> {_vehicle.FuelUnits} after 5 s at 2 s/unit — {( _vehicle.FuelUnits <= _fuelAtStart - 2 ? "OK burns" : "FAIL no burn" )}" );
						Log.Info( "[Vehicle test] done." );
						_test = null;
					}
					return true;
			}

			_test = null;
			return false;
		}

		protected int Phase => _phase;

		protected void NextPhase() => Next();

		void Next()
		{
			_phase++;
			_phaseStartedAt = -1;
		}

		/// <summary>Per-step hook before the phase switch (the demo moves its camera here).</summary>
		protected virtual void OnTick( float speed ) { }

		/// <summary>Override to replace the phase script; return false when finished.</summary>
		protected virtual bool RunPhases( float t, float speed, out float throttle, out float steer )
		{
			throttle = 0f;
			steer = 0f;
			return false;
		}
	}

	/// <summary>Usage: <c>vehicle_stash &lt;resourceId&gt; [count]</c> — host drops a stack into the nearest vehicle's storage (wreck-drop test).</summary>
	[ConCmd( "vehicle_stash" )]
	public static void ConCmdStash( string resourceId = "resource_woodBasic", int count = 5 )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		var storage = vehicle?.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( storage is null )
		{
			Log.Warning( "[Vehicle] vehicle_stash: no vehicle storage nearby." );
			return;
		}

		var placed = storage.HostDepositStack( resourceId, Math.Max( 1, count ) );
		Log.Info( $"[Vehicle] stashed {placed} x {resourceId}." );
	}

	/// <summary>Usage: <c>vehicle_storage</c> — opens the nearest vehicle's storage box on the local pawn's screen (UI check).</summary>
	[ConCmd( "vehicle_storage" )]
	public static void ConCmdStorage()
	{
		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		var storage = vehicle?.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		var interaction = pawn?.Components.Get<PlayerInventoryInteraction>();
		if ( storage is null || interaction is null )
		{
			Log.Warning( "[Vehicle] vehicle_storage: no pawn or no vehicle storage nearby." );
			return;
		}

		interaction.DevOpenContainer( storage );
		Log.Info( "[Vehicle] storage opened." );
	}

	/// <summary>Usage: <c>vehicle_damage [amount]</c> — hits the nearest vehicle through its DamageReceiver (the melee / arrow path).</summary>
	[ConCmd( "vehicle_damage" )]
	public static void ConCmdDamage( float amount = 10f )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_damage: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		var receiver = vehicle?.Components.Get<DamageReceiver>();
		if ( vehicle is null || receiver is null )
		{
			Log.Warning( "[Vehicle] vehicle_damage: no vehicle with a DamageReceiver." );
			return;
		}

		var removed = receiver.TakeDamage( amount, pawn?.Components.Get<PlayerCombat>() );
		Log.Info( $"[Vehicle] damage {amount:0} -> removed {removed:0}, hp now {( vehicle.IsValid() ? vehicle.Health : 0f ):0}, broken={( vehicle.IsValid() && vehicle.IsBroken )}" );
	}

	[ConCmd( "vehicle_spawn" )]
	public static void ConCmdSpawn( string pieceId = "vehicle_dune_buggy", float metersAhead = 5f )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_spawn: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		if ( pawn is null )
		{
			Log.Warning( "[Vehicle] vehicle_spawn: no local player pawn." );
			return;
		}

		var forward = pawn.WorldRotation.Forward.WithZ( 0f ).Normal;
		if ( forward.LengthSquared < 0.01f )
			forward = Vector3.Forward;

		var ahead = pawn.WorldPosition + forward * TerrainWorldUnits.MetersToEngine( Math.Max( 2f, metersAhead ) );
		var tr = pawn.Scene.Trace.Ray( ahead + Vector3.Up * 200f, ahead - Vector3.Up * 400f )
			.IgnoreGameObjectHierarchy( pawn )
			.WithoutTags( "player", "trigger" )
			.Run();
		var ground = tr.Hit ? tr.HitPosition : ahead;
		var sit = BuildModuleDimensions.GetGroundSitHalfExtent( pieceId, Rotation.FromYaw( pawn.WorldRotation.Yaw() ) );
		var transform = new Transform( ground + Vector3.Up * sit, Rotation.FromYaw( pawn.WorldRotation.Yaw() ) );

		if ( !BuildAuthority.HostPlacePiece( pawn.Scene, pieceId, transform, blueprint: false, out var spawned ) || spawned is null )
		{
			Log.Warning( $"[Vehicle] vehicle_spawn: HostPlacePiece failed for '{pieceId}'." );
			return;
		}

		Log.Info( $"[Vehicle] spawned {spawned.Name} at {spawned.WorldPosition}." );
	}

	[ConCmd( "vehicle_enter" )]
	public static void ConCmdEnter()
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_enter: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		if ( pawn is null || vehicle is null )
		{
			Log.Warning( "[Vehicle] vehicle_enter: no pawn or no vehicle in the scene." );
			return;
		}

		var ok = vehicle.HostTryEnter( pawn );
		Log.Info( ok ? $"[Vehicle] entered {vehicle.DisplayName} (seat {vehicle.GetSeatIndexOf( pawn.Id )})." : "[Vehicle] vehicle_enter: refused (full, wrecked or already seated)." );
	}

	[ConCmd( "vehicle_exit" )]
	public static void ConCmdExit()
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_exit: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		if ( pawn is null )
			return;

		Vehicle.HostEjectIfSeated( pawn );
		Log.Info( "[Vehicle] exit requested." );
	}

	[ConCmd( "vehicle_fuel" )]
	public static void ConCmdFuel( int units = 5 )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Vehicle] vehicle_fuel: host only." );
			return;
		}

		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		var storage = vehicle?.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( vehicle is null || storage is null )
		{
			Log.Warning( "[Vehicle] vehicle_fuel: no vehicle with storage nearby." );
			return;
		}

		var added = storage.HostTryDepositIntoSlot( storage.RestrictedSlotIndex, vehicle.FuelResourceId, Math.Max( 1, units ) );
		Log.Info( $"[Vehicle] tank now holds {storage.GetSlot( storage.RestrictedSlotIndex ).Count} (added {added})." );
	}

	[ConCmd( "vehicle_drive" )]
	public static void ConCmdDrive( float throttle = 1f, float steer = 0f, float seconds = 3f )
	{
		_simThrottle = Math.Clamp( throttle, -1f, 1f );
		_simSteer = Math.Clamp( steer, -1f, 1f );
		_simUntilGlobal = RealTime.GlobalNow + Math.Clamp( seconds, 0.05f, 60f );
		Log.Info( $"[Vehicle] simulated drive throttle={_simThrottle:0.##} steer={_simSteer:0.##} for {seconds:0.##}s." );
	}

	/// <summary>Usage: <c>vehicle_look &lt;pitch&gt; &lt;yaw&gt;</c> — points the local pawn's eyes (seat head-tracking check from the editor, which has no mouse).</summary>
	[ConCmd( "vehicle_look" )]
	public static void ConCmdLook( float pitch = 0f, float yaw = 0f )
	{
		var controller = FindLocalPawn()?.Components.Get<PlayerController>();
		if ( controller is null )
			return;

		controller.EyeAngles = new Angles( pitch, yaw, 0f );
		Log.Info( $"[Vehicle] eyes set to pitch {pitch:0} yaw {yaw:0}." );
	}

	[ConCmd( "vehicle_info" )]
	public static void ConCmdInfo()
	{
		var pawn = FindLocalPawn();
		var vehicle = FindNearestVehicle( pawn );
		if ( vehicle is null )
		{
			Log.Info( "[Vehicle] none in scene." );
			return;
		}

		var body = vehicle.Components.Get<Rigidbody>();
		var store = vehicle.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( store is not null )
		{
			var slots = new string[store.SlotCount];
			for ( var i = 0; i < store.SlotCount; i++ )
			{
				var slot = store.GetSlot( i );
				slots[i] = slot.IsEmpty ? "-" : $"{slot.ResourceId}x{slot.Count}";
			}
			Log.Info( $"[Vehicle] storage [{string.Join( " ", slots )}] fuelSlot={store.RestrictedSlotIndex}" );
		}
		// What a look-trace at the storage box reports (the E-on-chest path): from 2 m behind the box, aimed at its centre.
		GameObject storageGo = null;
		foreach ( var child in vehicle.GameObject.GetAllObjects( true ) )
		{
			if ( child.Tags.Has( Vehicle.StorageTag ) )
			{
				storageGo = child;
				break;
			}
		}
		if ( storageGo is not null && storageGo.IsValid() )
		{
			var target = storageGo.WorldPosition;
			var from = target - vehicle.WorldRotation.Forward * 80f + Vector3.Up * 20f;
			var tr = vehicle.Scene.Trace.Ray( from, target ).WithoutTags( "player" ).Run();
			Log.Info( $"[Vehicle] storage trace: hit={tr.Hit} object={tr.GameObject?.Name ?? "-"} at={tr.HitPosition} storageHit={( tr.Hit && Vehicle.IsStorageHit( tr.GameObject, tr.HitPosition ) )}" );
		}

		var open = pawn?.Components.Get<PlayerInventoryInteraction>()?.OpenContainer;
		var storageBox = vehicle.StorageBox;
		Log.Info( $"[Vehicle] chest open={( open is not null ? open.DisplayName : "none" )} pawn->box={( pawn is not null && storageBox is not null ? Vector3.DistanceBetween( pawn.WorldPosition, storageBox.WorldPosition ) / 40f : 0f ):0.0} m pawn->root={( pawn is not null ? Vector3.DistanceBetween( pawn.WorldPosition, vehicle.WorldPosition ) / 40f : 0f ):0.0} m" );

		var driver = vehicle.GetOccupant( 0 );
		var anim = driver is null ? "no driver" : $"{driver.Components.Get<PlayerAnimation>()?.SeatedPoseDebug} parent={driver.Parent?.Name} seated={driver.Components.Get<PlayerMovement>()?.IsSeated}";
		Log.Info( $"[Vehicle] driver anim: {anim}" );
		Log.Info( $"[Vehicle] {vehicle.DisplayName} pos={vehicle.WorldPosition} speed={vehicle.SyncedSpeedMetersPerSecond:0.0} m/s ({vehicle.SpeedMph:0} mph) fuel={vehicle.FuelUnits} hp={vehicle.Health:0}/{vehicle.MaxHealth:0} mass={body?.Mass:0} dt={vehicle.LastStepDelta:0.####} seats={string.Join( ",", vehicle.SeatOccupants )} owner={vehicle.GameObject.Network?.Owner?.DisplayName ?? "host"} up={vehicle.WorldRotation.Up} vel={body?.Velocity} {vehicle.DebugWheels()}" );
	}

	static GameObject FindLocalPawn()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}

	static Vehicle FindNearestVehicle( GameObject pawn )
	{
		Vehicle best = null;
		var bestDist = float.MaxValue;
		foreach ( var v in Vehicle.Registered )
		{
			if ( v is null || !v.IsValid() || v.IsPreviewGhost )
				continue;

			var d = pawn is { IsValid: true } ? Vector3.DistanceBetween( pawn.WorldPosition, v.WorldPosition ) : 0f;
			if ( d < bestDist )
			{
				bestDist = d;
				best = v;
			}
		}

		return best;
	}
}
