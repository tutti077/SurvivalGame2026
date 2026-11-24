using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Lying in a <see cref="BuildBed"/> (the bed claims and admits the pawn; this is the pawn's half). Same shape as the
/// vehicle seat: the host writes <see cref="BedLockId"/> and tells the owner to apply; the owner turns its physics body
/// keyframed, switches the controller's input and animator pass off and rests its root on the mattress every frame
/// (<see cref="PlayerAnimation"/> lays the Body child down on every machine). The root is not parented under the bed -
/// a bed that raiders tear down must not take the pawn with it - and the colliders stay on, so a sleeper can be hit;
/// a hit, Use, Jump or a fresh move key gets them out (the host puts them down beside the bed, <see cref="BuildBed.GetRespawnTransform"/>).
/// <para>
/// Sleeping (Mark 2026-11-19): a pawn that has lain <see cref="BuildBed.SleepDelaySeconds"/> counts as asleep. Only when
/// every pawn in the world is asleep does the host set <see cref="BedSleeping"/> on all of them: the owner's screen fades
/// to black (<see cref="BedBlackout01"/>, drawn by <see cref="PlayerScreenHud"/>) and the day cycle runs fast until 06:00
/// (<see cref="EnvironmentDayNightCycle"/> asks <see cref="HostEveryoneAsleep"/>). One sleeper alone changes nothing.
/// At 06:00 everyone gets out and the quest event <c>slept_in_bed</c> fires.
/// </para>
/// </summary>
public sealed partial class PlayerMovement
{
	/// <summary>Host-synced: the bed this pawn lies in (<see cref="Guid.Empty"/> = up).</summary>
	[Sync( SyncFlags.FromHost )]
	public Guid BedLockId { get; private set; }

	/// <summary>Host-synced: every player is asleep and the night is running fast.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool BedSleeping { get; private set; }

	public bool IsInBed => BedLockId != Guid.Empty;

	/// <summary>Owner-local: 1 = fully black, 0 = clear.</summary>
	public float BedBlackout01 { get; private set; }

	/// <summary>Host: every pawn lying in a bed right now (pruned as it is read).</summary>
	static readonly List<PlayerMovement> HostBedOccupants = new();

	BuildBed _occupiedBed;
	Guid _occupiedBedCacheId;

	/// <summary>Host: scene time this pawn got into bed.</summary>
	float _bedEnteredAtHost;

	bool _bedAppliedLocal;
	/// <summary>Owner: the bed this pawn is resting on right now (<see cref="Guid.Empty"/> = up).</summary>
	Guid _bedAppliedId;
	/// <summary>Owner: an owner RPC was applied and <see cref="BedLockId"/> has not caught up with it yet.</summary>
	bool _bedAwaitingSync;
	bool _bedSavedGravity = true;
	bool _bedSavedUseInput = true;
	bool _bedSavedUseAnimator = true;
	float _bedFadeSeconds = 3f;
	/// <summary>Owner: the keys that got the pawn in are ignored until this real time.</summary>
	double _bedLeaveInputFrom;
	/// <summary>Owner: the move keys have been let go since lying down, so a new press means "get up".</summary>
	bool _bedMoveReleased;
	double _bedLeaveRequestedAt;

	/// <summary>The bed this pawn lies in, on any machine (null when up, or while the bed has not arrived yet).</summary>
	public BuildBed OccupiedBed
	{
		get
		{
			if ( !IsInBed )
				return null;

			if ( _occupiedBed is { IsValid: true } && _occupiedBedCacheId == BedLockId )
				return _occupiedBed;

			_occupiedBed = Scene.Directory.FindByGuid( BedLockId )?.Components.Get<BuildBed>();
			_occupiedBedCacheId = BedLockId;
			return _occupiedBed;
		}
	}

	bool HasBedHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Host: this pawn has lain long enough to count as asleep.</summary>
	public bool HostBedSleepReady
	{
		get
		{
			if ( !IsInBed )
				return false;

			var delay = OccupiedBed is { IsValid: true } bed ? Math.Max( 0f, bed.SleepDelaySeconds ) : 5f;
			return Time.Now - _bedEnteredAtHost >= delay;
		}
	}

	/// <summary>Host: lie down in <paramref name="bed"/> (<see cref="BuildBed.HostUse"/> has checked whose bed it is).</summary>
	public bool HostEnterBed( BuildBed bed )
	{
		if ( !HasBedHostAuthority || bed is null || !bed.IsValid() )
			return false;

		if ( IsInBed || IsSeated || PodLocked || TrapLocked || EventInputLocked || WingsuitDeployed )
			return false;

		BedLockId = bed.GameObject.Id;
		_bedEnteredAtHost = Time.Now;
		if ( !HostBedOccupants.Contains( this ) )
			HostBedOccupants.Add( this );

		ApplyBedLocal( BedLockId, default );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyBed( BedLockId, default );
		return true;
	}

	/// <summary>
	/// Host: get up. <paramref name="placeBeside"/> puts the feet on the free spot beside the bed; false leaves the pawn
	/// where it lies (a death or teleport is about to place it, or the bed is gone).
	/// </summary>
	public void HostLeaveBed( bool placeBeside = true )
	{
		if ( !HasBedHostAuthority || !IsInBed )
			return;

		var exit = default( Vector3 );
		if ( placeBeside && OccupiedBed is { IsValid: true } bed )
			bed.GetRespawnTransform( out exit, out _ );

		BedLockId = Guid.Empty;
		HostBedOccupants.Remove( this );

		ApplyBedLocal( Guid.Empty, exit );
		if ( GameObject.Network is { Active: true } )
			RpcOwnerApplyBed( Guid.Empty, exit );
	}

	/// <summary>Host: the bed is being removed (broken, picked up) - whoever lies in it gets up where they are.</summary>
	public static void HostEjectFromBed( Guid bedId )
	{
		for ( var i = HostBedOccupants.Count - 1; i >= 0; i-- )
		{
			var pawn = HostBedOccupants[i];
			if ( pawn is null || !pawn.IsValid() )
			{
				HostBedOccupants.RemoveAt( i );
				continue;
			}

			if ( pawn.BedLockId == bedId )
				pawn.HostLeaveBed( placeBeside: false );
		}
	}

	/// <summary>Host: is anyone lying in a bed? (The cheap gate in front of <see cref="HostEveryoneAsleep"/>.)</summary>
	public static bool HostAnyoneInBed
	{
		get
		{
			for ( var i = HostBedOccupants.Count - 1; i >= 0; i-- )
			{
				if ( HostBedOccupants[i] is not { IsValid: true, IsInBed: true } )
					HostBedOccupants.RemoveAt( i );
			}

			return HostBedOccupants.Count > 0;
		}
	}

	/// <summary>Host: every pawn in <paramref name="scene"/> lies in a bed and has done so for its sleep delay.</summary>
	public static bool HostEveryoneAsleep( Scene scene )
	{
		if ( scene is null || !scene.IsValid() || !HostAnyoneInBed )
			return false;

		var pawns = 0;
		foreach ( var pawn in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( pawn is null || !pawn.IsValid() )
				continue;
			if ( !pawn.HostBedSleepReady )
				return false;
			pawns++;
		}

		return pawns > 0;
	}

	/// <summary>
	/// Host: the night starts or stops running fast for everyone. <paramref name="hostCycle01"/> is the host's clock
	/// (0 = 06:00) for the clients to land on when it stops; <paramref name="dawn"/> = it stopped because 06:00 came:
	/// the sleepers get up and their sleep counts (<c>slept_in_bed</c>).
	/// </summary>
	public static void HostSetEveryoneSleeping( Scene scene, bool sleeping, float hostCycle01, bool dawn )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var pawn in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( pawn is null || !pawn.IsValid() || !pawn.HasBedHostAuthority )
				continue;

			if ( pawn.BedSleeping != sleeping )
			{
				pawn.BedSleeping = sleeping;
				pawn.ApplyBedSleepClockLocal( sleeping, hostCycle01 );
				if ( pawn.GameObject.Network is { Active: true } )
					pawn.RpcOwnerBedSleepClock( sleeping, hostCycle01 );
			}

			if ( sleeping || !dawn || !pawn.IsInBed )
				continue;

			pawn.Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.SleptInBed );
			pawn.HostLeaveBed();
		}
	}

	[Rpc.Owner]
	void RpcOwnerBedSleepClock( bool sleeping, float hostCycle01 )
	{
		ApplyBedSleepClockLocal( sleeping, hostCycle01 );
	}

	/// <summary>A client's own clock follows the host's sleep: fast while asleep, then set to the host's time. The host's clock is the authority and is left alone.</summary>
	void ApplyBedSleepClockLocal( bool sleeping, float hostCycle01 )
	{
		if ( !IsLocalMovementDriver() || HasBedHostAuthority )
			return;

		EnvironmentDayNightCycle.ClientSetSleepFastForward( sleeping, hostCycle01 );
	}

	[Rpc.Owner]
	void RpcOwnerApplyBed( Guid bedId, Vector3 exitWorldPos )
	{
		ApplyBedLocal( bedId, exitWorldPos );
	}

	void ApplyBedLocal( Guid bedId, Vector3 exitWorldPos )
	{
		// The pawn's transform is owner-authored: only the owner places itself.
		if ( !IsLocalMovementDriver() )
			return;

		if ( bedId == Guid.Empty )
		{
			ExitBedLocal( exitWorldPos );
		}
		else
		{
			var bed = Scene.Directory.FindByGuid( bedId )?.Components.Get<BuildBed>();
			if ( bed is not null && bed.IsValid() )
				EnterBedLocal( bed, bedId );
		}

		// Applied what was asked while the synced id still shows the state before it: the sync is in flight.
		// (A failed enter does not wait - TickBedPin retries it from the synced id once that arrives.)
		_bedAwaitingSync = _bedAppliedId == bedId && BedLockId != _bedAppliedId;
	}

	void EnterBedLocal( BuildBed bed, Guid bedId )
	{
		if ( _bedAppliedLocal )
			return;

		_bedAppliedLocal = true;
		_bedAppliedId = bedId;
		_bedFadeSeconds = Math.Max( 0.1f, bed.SleepFadeSeconds );
		_bedLeaveInputFrom = RealTime.GlobalNow + 0.4;
		_bedMoveReleased = false;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		if ( GrappleAttached )
			DetachGrappleForHitReaction();

		if ( body is not null && body.IsValid() )
		{
			_bedSavedGravity = body.Gravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			body.Gravity = false;
			body.MotionEnabled = false;   // keyframed: rests on the mattress, no simulation
		}

		if ( _controller is not null )
		{
			_bedSavedUseInput = _controller.UseInputControls;
			_controller.UseInputControls = false;
			// The controller's animator pass would stand the pawn back up; PlayerAnimation owns the pose in bed.
			_bedSavedUseAnimator = _controller.UseAnimatorControls;
			_controller.UseAnimatorControls = false;
		}

		GameObject.WorldPosition = bed.GetMattressTopCentre();
		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	void ExitBedLocal( Vector3 exitWorldPos )
	{
		if ( !_bedAppliedLocal )
			return;

		_bedAppliedLocal = false;
		_bedAppliedId = Guid.Empty;
		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();

		if ( exitWorldPos != default )
			GameObject.WorldPosition = exitWorldPos;

		if ( body is not null && body.IsValid() )
		{
			body.MotionEnabled = true;
			body.Gravity = _bedSavedGravity;
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
			if ( body.Sleeping )
				body.Sleeping = false;
		}

		if ( _controller is not null )
		{
			_controller.UseInputControls = _bedSavedUseInput;
			_controller.UseAnimatorControls = _bedSavedUseAnimator;
		}

		SnapBodyYawToLook();
		GameObject.Transform.ClearInterpolation();
		GameObject.Network?.ClearInterpolation();
	}

	/// <summary>Owner, every frame: keep the local bed rest in step with the host's synced one, and stay on the mattress while in it.</summary>
	void TickBedPin()
	{
		if ( BedLockId == _bedAppliedId )
		{
			_bedAwaitingSync = false;
		}
		else if ( !_bedAwaitingSync )
		{
			// The synced bed changed with no owner RPC applied here: the host cleared it before an exit position
			// reached us, or the enter RPC could not find the bed yet.
			if ( _bedAppliedLocal )
				ExitBedLocal( default );
			if ( IsInBed )
				ApplyBedLocal( BedLockId, default );
		}

		if ( !_bedAppliedLocal )
			return;

		var bed = _bedAwaitingSync ? null : OccupiedBed;
		if ( !_bedAwaitingSync && ( bed is null || !bed.IsValid() ) )
		{
			// The bed is gone (torn down) and the host's clear has not landed yet.
			ExitBedLocal( default );
			return;
		}

		if ( bed is not null )
		{
			var rest = bed.GetMattressTopCentre();
			if ( (GameObject.WorldPosition - rest).LengthSquared > 0.01f )
				GameObject.WorldPosition = rest;
		}

		_controller ??= Components.Get<PlayerController>();
		var body = _controller?.Body ?? Components.Get<Rigidbody>();
		if ( body is not null && body.IsValid() && body.MotionEnabled )
			body.MotionEnabled = false;
	}

	/// <summary>Owner, every frame: the screen goes black while everyone sleeps and clears again after.</summary>
	void TickBedBlackout()
	{
		var target = BedSleeping && _bedAppliedLocal ? 1f : 0f;
		if ( BedBlackout01 == target )
			return;

		var step = Time.Delta / Math.Max( 0.1f, _bedFadeSeconds );
		BedBlackout01 = target > BedBlackout01
			? MathF.Min( target, BedBlackout01 + step )
			: MathF.Max( target, BedBlackout01 - step );
	}

	/// <summary>
	/// Owner, from <see cref="PreInput"/>: runs the bed rest and the blackout. True while the pawn lies in bed - the
	/// caller then skips everything else (no move, jump, roll, grapple, swing or block from a bed).
	/// </summary>
	bool PreInputBed()
	{
		TickBedPin();
		TickBedBlackout();

		if ( !_bedAppliedLocal && !IsInBed )
			return false;

		var moving = Input.AnalogMove.LengthSquared > 0.09f;
		if ( !moving )
			_bedMoveReleased = true;

		// Use, Jump, or a move key pressed after lying down: get up.
		var wantsUp = RealTime.GlobalNow >= _bedLeaveInputFrom
			&& ( Input.Pressed( "Use" ) || Input.Pressed( JumpInputAction ) || ( moving && _bedMoveReleased ) );

		ClearActionIfPressed( JumpInputAction );
		ClearActionIfPressed( SneakInputAction );
		if ( !string.IsNullOrWhiteSpace( SprintInputAction ) )
			ClearActionIfPressed( SprintInputAction );
		ClearActionIfPressed( "Attack1" );
		ClearActionIfPressed( "Attack2" );

		if ( wantsUp && IsInBed && RealTime.GlobalNow - _bedLeaveRequestedAt > 0.5 )
		{
			_bedLeaveRequestedAt = RealTime.GlobalNow;
			if ( HasBedHostAuthority )
				HostLeaveBed();
			else
				RpcHostLeaveBed();
		}

		return true;
	}

	/// <summary>Owner → host: the player pressed a key to get up.</summary>
	[Rpc.Host]
	void RpcHostLeaveBed()
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		HostLeaveBed();
	}

	/// <summary>Usage: <c>bed_use</c> — the local pawn uses the nearest standing bed as if E were pressed on it (claims it if it can, lies down), or gets up when it is in bed.</summary>
	[ConCmd( "bed_use" )]
	public static void ConCmdBedUse()
	{
		var pawn = FindLocalBedPawn();
		if ( pawn is null )
		{
			Log.Warning( "[Bed] no local pawn." );
			return;
		}

		if ( pawn.IsInBed )
		{
			if ( pawn.HasBedHostAuthority )
				pawn.HostLeaveBed();
			else
				pawn.RpcHostLeaveBed();
			Log.Info( "[Bed] getting up." );
			return;
		}

		var bed = BuildBed.FindNearest( pawn.WorldPosition );
		if ( bed is null )
		{
			Log.Warning( "[Bed] no standing bed in the scene." );
			return;
		}

		var interaction = pawn.Components.Get<PlayerInventoryInteraction>();
		if ( interaction is null )
			return;

		interaction.OwnerUseBed( bed );
		Log.Info( $"[Bed] using {bed.GameObject.Name} ({bed.PromptTextFor( pawn.GameObject )}); the host refuses it past the bed's use reach." );
	}

	/// <summary>Usage: <c>bed_info</c> — every pawn's bed state, and the day clock's sleep state.</summary>
	[ConCmd( "bed_info" )]
	public static void ConCmdBedInfo()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var pawn in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( pawn is null || !pawn.IsValid() )
				continue;

			var claimed = BuildBed.TryFindClaimedBy( pawn.GameObject, out var own ) ? own.GameObject.Name : "none";
			var lain = pawn.IsInBed && pawn.HasBedHostAuthority ? $" lain={Time.Now - pawn._bedEnteredAtHost:0.0}s ready={pawn.HostBedSleepReady}" : string.Empty;
			Log.Info( $"[Bed] {pawn.GameObject.Name}: claimed={claimed} inBed={pawn.IsInBed}{lain} sleeping={pawn.BedSleeping} applied={pawn._bedAppliedLocal} blackout={pawn.BedBlackout01:0.00} pos={pawn.WorldPosition}" );
		}

		Log.Info( $"[Bed] clock: {EnvironmentDayNightCycle.DescribeSleepState()}" );
	}

	static PlayerMovement FindLocalBedPawn()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var pawn in scene.GetAllComponents<PlayerMovement>() )
		{
			if ( pawn is not null && pawn.IsValid() && pawn.IsLocalMovementDriver() )
				return pawn;
		}

		return null;
	}
}
