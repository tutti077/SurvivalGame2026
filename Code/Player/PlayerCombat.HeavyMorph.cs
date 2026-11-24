using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Heavy morph (Mark 2026-11-16: "If we're holding a heavy (left click) and while still holding we hit right click then
/// that has initiated the morph. Players can then move the mouse in a new direction and it should move the weapon to
/// that side of the players body while still maintaining the heavy attack. When they finally release it'll come from the
/// new position."). The guard button on a held heavy starts the morph instead of a feint or a guard; the first side the
/// attack cursor (the teardrop) then moves to is the new side, and it stays there (2026-11-17: one pick per guard press;
/// another press on the still-held heavy morphs again). The release strikes from where it is. The owner moves its own pose at once and sends each side change; the host re-checks (its own swing,
/// a heavy still held), moves the damage arc and the side a defender must guard, and shows every other peer the new
/// cocked pose. The strike never starts before <see cref="HeavyMorphSettleSeconds"/> after the last side change, so it
/// always leaves from the new pose. No extra stamina: the hold drain keeps running.
/// <para>Re-arm (Mark 2026-11-17: "it has like a half second after you pick the new direction before itll be a heavy
/// hit. It should do the sparkle animation thing just sooner than if you hadnt morphed"): every side change disarms the
/// heavy for <see cref="HeavyMorphRearmSeconds"/>; then the heavy-ready glint plays again and a release is a heavy. Let go
/// before that and the swing strikes from the new side as a light (light damage, stagger, release and recovery).</para>
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Exchange" ), Title( "Heavy morph settle (s)" ), Description( "A morphed heavy strikes no sooner than this after its last side change (the weapon has to get there)." ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float HeavyMorphSettleSeconds { get; set; } = 0.15f;

	[Property, Group( "Combat — Exchange" ), Title( "Heavy morph re-arm (s)" ), Description( "After each side change the morphed swing is a heavy again only after this long (the glint plays then); let go sooner and it strikes as a light." ), Range( 0f, 1.5f ), Step( 0.05f )]
	public float HeavyMorphRearmSeconds { get; set; } = 0.5f;

	/// <summary>Host-synced: a held heavy hangs on this pawn - remote peers keep its cocked pose up however long it is held.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool HeavyHoldShown { get; private set; }

	// Owner: the morph is on (guard pressed on the held heavy), the side the swing comes from now, the last side change.
	bool _ownerMorphing;
	byte _ownerSwingType;
	double _ownerMorphedAt = double.NegativeInfinity;

	/// <summary>Owner: a new swing went out - its side, no morph yet.</summary>
	void BeginOwnerHeavyMorphTracking( byte attackType )
	{
		_ownerSwingType = attackType;
		_ownerMorphing = false;
		_ownerMorphedAt = double.NegativeInfinity;
	}

	void EndOwnerHeavyMorph() => _ownerMorphing = false;

	/// <summary>Owner: the guard button went down. On a heavy still held it starts the morph (and is not a feint or a guard).</summary>
	bool OwnerTryBeginHeavyMorph()
	{
		if ( !_ownerSwingTracking || !_ownerSwingHeavy || !_ownerSwingHolding )
			return false;

		if ( !_ownerMorphing )
		{
			_ownerMorphing = true;
			LogCombatDiag( "CLIENT / OWNER", $"heavy morph on seq={_ownerSwingSeq}" );
		}

		return true;
	}

	/// <summary>Owner, every frame after the cursor update: a morphing heavy follows the attack cursor to its side.</summary>
	void TickOwnerHeavyMorph()
	{
		if ( !_ownerMorphing )
			return;

		if ( !_ownerSwingTracking || !_ownerSwingHolding || !_ownerSwingHeavy )
		{
			_ownerMorphing = false;
			return;
		}

		var dir = _primaryLiveSwingDir;
		var type = ResolveAttackTypeFromCursorDir( dir );
		if ( type == _ownerSwingType )
			return;

		_ownerSwingType = type;
		_ownerMorphedAt = Time.NowDouble;
		// One pick per morph (Mark 2026-11-17: "you have 1 chance to select the new direction and once its selected thats
		// where it stays. If they want to morph again they have to repeat the process"): the side is set, the next one
		// needs another guard press on the held heavy.
		_ownerMorphing = false;
		// Disarmed until the re-arm runs out; the glint plays again then (TickOwnerHeavyHold).
		_ownerHeavyFlashPlayed = false;
		Components.Get<PlayerAnimation>()?.MorphCockedSwing( type );

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			RpcHostHeavyMorph( _ownerSwingSeq, dir );
		else
			HostTryHeavyMorph( _ownerSwingSeq, dir );

		LogCombatDiag( "CLIENT / OWNER", $"heavy morph seq={_ownerSwingSeq} -> {SwingDirs.Letter( dir )}" );
	}

	bool OwnerHasMorphed => !double.IsNegativeInfinity( _ownerMorphedAt );

	/// <summary>Owner: when the held swing is (again) a heavy - the heavy windup, or the re-arm after the last side change.</summary>
	double OwnerHeavyArmedAt() => OwnerHasMorphed
		? _ownerMorphedAt + Math.Max( 0f, HeavyMorphRearmSeconds )
		: _ownerSwingClickAt + GetMeleeWeaponTimings().HeavyWindupSeconds;

	/// <summary>Owner: the earliest the held swing may strike - the heavy windup, or (morphed) the weapon settled on its new side.</summary>
	double OwnerHeavyStrikeFloor() => OwnerHasMorphed
		? _ownerMorphedAt + Math.Max( 0f, HeavyMorphSettleSeconds )
		: _ownerSwingClickAt + GetMeleeWeaponTimings().HeavyWindupSeconds;

	/// <summary>Owner: the hold drain emptied the bar (the host lets the heavy go then; the owner follows).</summary>
	bool OwnerHeavyHoldOutOfStamina() =>
		Time.NowDouble - _ownerSwingClickAt > Math.Max( 0f, HeavyHoldDrainAfterSeconds )
		&& HeavyHoldDrainPerSecond > 1e-4f
		&& Components.Get<PlayerVitals>() is { } vitals && vitals.CurrentStamina <= 0.5f;

	[Rpc.Host( NetFlags.Reliable )]
	void RpcHostHeavyMorph( ushort seq, byte swingDir )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostTryHeavyMorph( seq, swingDir );
	}

	/// <summary>
	/// Host: move the held heavy <paramref name="seq"/> to <paramref name="swingDir"/>'s side. Only its own swing, still
	/// held (a heavy, or a light the owner is holding into one - the owner may decide heavy a frame before the host).
	/// </summary>
	internal bool HostTryHeavyMorph( ushort seq, byte swingDir )
	{
		var runtime = _serverMeleeAttack;
		if ( runtime is null || runtime.IntentSequence != seq || !runtime.CanMorph || HostWasPrimaryAttackReleased( seq ) )
			return false;

		if ( swingDir is not (SwingDirs.Left or SwingDirs.Right or SwingDirs.Up) )
			return false;

		var type = ResolveAttackTypeFromCursorDir( swingDir );
		if ( type == runtime.AttackType )
			return false;

		runtime.MorphTo( swingDir, type, HeavyMorphSettleSeconds, HeavyMorphRearmSeconds );
		if ( !IsLocalCombatDriver() )
			Components.Get<PlayerAnimation>()?.MorphCockedSwing( type );
		if ( GameObject.Network is { Active: true } )
			RpcBroadcastHeavyMorph( seq, swingDir, type );

		LogMeleePhasePulse( "Heavy morph", $"seq={seq} -> {MeleeAttackTypes.Label( type )}" );
		return true;
	}

	/// <summary>Host → every other peer: the held heavy moved - the cocked pose and the swing replay follow.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastHeavyMorph( ushort seq, byte swingDir, byte attackType )
	{
		if ( Networking.IsHost || IsLocalCombatDriver() )
			return;

		if ( _clientSwingTracePlayback is { } replay && replay.IntentSequence == seq )
			replay.MorphTo( swingDir, attackType, 0f, 0f );
		Components.Get<PlayerAnimation>()?.MorphCockedSwing( attackType );
	}

	/// <summary>Host, every tick: publish whether a held heavy hangs on this pawn (<see cref="HeavyHoldShown"/>).</summary>
	void HostPublishHeavyHold()
	{
		var holding = _serverMeleeAttack is { IsHeavyHolding: true };
		if ( HeavyHoldShown != holding )
			HeavyHoldShown = holding;
	}
}
