using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Incoming-attack marker (Mark 2026-11-16: "I want it to work like last oasis ... the game says 'Block right' and if you
/// do in time then great"; the PvE-only rule of 2026-11-14 is gone). While a scav (<see cref="EntityCombat"/>) or another
/// player within reach is winding up a swing and facing this pawn, the owner's crosshair shows a red marker on the side
/// the guard has to be on, in the guard teardrop's own frame (left / right / top). The side comes from
/// <see cref="MeleeBlockResolution.ResolveIncomingSide"/> - the same function the host resolves the block with - so
/// raising the guard toward the marker blocks it.
/// Owner presentation only: resolved <see cref="IncomingIconRefreshSeconds"/> apart from the local fighter list and the
/// player pawns' shown wind-up pose (<see cref="PlayerAnimation.TryGetShownSwordWindup"/>), no host round trip.
/// <para>The side is locked per swing (Mark 2026-11-17: it "frequently flips to the other side last second"; he chose
/// "lock side per swing"): the host latches it at the swing's windup start and on every heavy morph
/// (<see cref="TryGetLatchedIncomingSide"/>) and sends it to each defender's owner (<see cref="HostSendIncomingSide"/>),
/// so host and clients show the side the host blocks with. Only until that arrives (or for an attacker that was out of
/// range at its windup start) the marker latches its own the first time it sees that wind-up.</para>
/// </summary>
public partial class PlayerCombat
{
	/// <summary>An attacker further than this (horizontal) never raises the marker.</summary>
	const float IncomingIconRangeMeters = 4.5f;
	/// <summary>The attacker's facing must point within this of the pawn (it is swinging at this pawn, not a neighbour).</summary>
	const float IncomingIconFacingDegrees = 50f;
	/// <summary>The marker is re-resolved this often, not every frame.</summary>
	const float IncomingIconRefreshSeconds = 0.05f;

	float _incomingIconAt = -1f;
	bool _incomingIconHas;
	byte _incomingIconSide;

	/// <summary>Client-side latch for wind-ups this machine runs no swing for: attacker → (attack type, side, last seen).</summary>
	readonly System.Collections.Generic.Dictionary<GameObject, (byte Type, byte Side, float SeenAt)> _incomingLatch = new();

	/// <summary>
	/// Host: the guard side this pawn's swing in flight latched for <paramref name="defender"/> at its windup start. False
	/// when no host swing runs here or the defender was out of range then. A client reads the side the host sent it
	/// (<see cref="HostSendIncomingSide"/>), never a latch of its own replay.
	/// </summary>
	internal bool TryGetLatchedIncomingSide( PlayerCombat defender, out byte side )
	{
		side = SwingDirs.Up;
		return _serverMeleeAttack is { } runtime && runtime.TryGetLatchedSide( defender, out side );
	}

	/// <summary>Client: the side the host latched for each attacker's swing at this pawn (attacker id → type, side, received).</summary>
	readonly System.Collections.Generic.Dictionary<Guid, (byte Type, byte Side, float At)> _hostIncomingSides = new();
	/// <summary>A sent side older than this is from a swing long over.</summary>
	const float HostIncomingSideMaxAgeSeconds = 8f;

	/// <summary>
	/// Host: this pawn is the defender - tell its remote owner which guard side the attacker's swing latched (at the
	/// windup start and again on every heavy morph), so the red marker shows exactly the side the host will block with
	/// (playtest 2026-11-18: after a morph the marker "is wrong and gets stuck being wrong" - a client only had its own
	/// guess, taken a trip later against its own facing). Once per swing / morph per defender in range.
	/// </summary>
	internal void HostSendIncomingSide( Guid attackerId, byte attackType, byte side )
	{
		if ( !Networking.IsHost || GameObject.Network is not { Active: true, Owner: not null } || IsLocalCombatDriver() )
			return;

		RpcOwnerIncomingSide( attackerId, attackType, side );
	}

	[Rpc.Owner]
	void RpcOwnerIncomingSide( Guid attackerId, byte attackType, byte side )
	{
		_hostIncomingSides[attackerId] = (attackType, side, Time.Now);
	}

	/// <summary>This machine's swing clip for this pawn fired its strike: its slash-ribbon replay releases now.</summary>
	internal void OnSwingStrikeShown( byte attackType ) => _clientSwingTracePlayback?.StrikeShown( attackType );

	/// <summary>The marker side for one wind-up: the swing's latched side, else this machine's own latch from its first sight.</summary>
	byte LatchedMarkerSide( GameObject attacker, byte attackType, float now )
	{
		if ( TryGetAttackerSwingLatch( attacker, out var hostSide ) )
			return hostSide;

		if ( _hostIncomingSides.TryGetValue( attacker.Id, out var sent ) && sent.Type == attackType
		     && now >= sent.At && now - sent.At < HostIncomingSideMaxAgeSeconds )
			return sent.Side;

		if ( _incomingLatch.TryGetValue( attacker, out var latch ) && latch.Type == attackType && now - latch.SeenAt < 0.25f )
		{
			_incomingLatch[attacker] = (latch.Type, latch.Side, now);
			return latch.Side;
		}

		var side = MeleeBlockResolution.ResolveIncomingSide( this, attacker, attackType );
		_incomingLatch[attacker] = (attackType, side, now);
		return side;
	}

	bool TryGetAttackerSwingLatch( GameObject attacker, out byte side )
	{
		side = SwingDirs.Up;
		return attacker.IsValid() && attacker.Components.Get<PlayerCombat>( FindMode.EverythingInSelfAndDescendants ) is { } combat
		       && combat.TryGetLatchedIncomingSide( this, out side );
	}

	/// <summary>
	/// Owner HUD: the guard side (<see cref="SwingDirs.Left"/> / <see cref="SwingDirs.Right"/> / <see cref="SwingDirs.Up"/>)
	/// that stops the nearest swing winding up at this pawn. False when nothing threatens.
	/// </summary>
	public bool TryGetIncomingAttackSide( out byte side )
	{
		var now = Time.Now;
		if ( now < _incomingIconAt || now - _incomingIconAt >= IncomingIconRefreshSeconds )
		{
			_incomingIconAt = now;
			_incomingIconHas = ResolveIncomingAttack( out _incomingIconSide );
		}

		side = _incomingIconSide;
		return _incomingIconHas;
	}

	bool ResolveIncomingAttack( out byte side )
	{
		side = SwingDirs.Up;
		var now = Time.Now;
		var range = IncomingIconRangeMeters * GetPawnUnitsPerMeter();
		var bestSq = range * range;
		var cosFacing = MathF.Cos( IncomingIconFacingDegrees * (MathF.PI / 180f) );
		var me = GameObject.WorldPosition;
		GameObject best = null;
		byte bestType = 0;

		var fighters = EntityCombat.Fighters;
		for ( var i = 0; i < fighters.Count; i++ )
		{
			var f = fighters[i];
			if ( f is null || !f.IsValid() || !f.IsWindupShown )
				continue;

			// Every wind-up in range is latched as soon as it is seen, not only once it is the nearest.
			var fighterInRange = (me - f.GameObject.WorldPosition).WithZ( 0f ).LengthSquared <= range * range;
			if ( fighterInRange )
				LatchedMarkerSide( f.GameObject, f.WindupAttackType, now );

			if ( IsCloserThreat( f.GameObject, f.GameObject.WorldRotation.Forward, me, cosFacing, ref bestSq ) )
			{
				best = f.GameObject;
				bestType = f.WindupAttackType;
			}
		}

		// Other players: the wind-up pose this machine is showing on their body.
		foreach ( var anim in Scene.GetAllComponents<PlayerAnimation>() )
		{
			if ( anim is null || !anim.IsValid() || anim.GameObject == GameObject || !anim.TryGetShownSwordWindup( out var type ) )
				continue;

			var combat = anim.Components.Get<PlayerCombat>();
			if ( combat is null )
				continue;

			if ( (me - anim.GameObject.WorldPosition).WithZ( 0f ).LengthSquared <= range * range )
				LatchedMarkerSide( anim.GameObject, type, now );

			if ( IsCloserThreat( anim.GameObject, combat.GetMeleeCombatBasisRotation( type ).Forward, me, cosFacing, ref bestSq ) )
			{
				best = anim.GameObject;
				bestType = type;
			}
		}

		if ( best is null )
			return false;

		// Behind the guard's front arc nothing blocks it: no marker rather than a side that cannot work.
		if ( MeleeBlockResolution.IsOutsideFrontArc( this, MeleeBlockResolution.ComputeIncomingAngleFromAttacker( this, best.WorldPosition ) ) )
			return false;

		side = LatchedMarkerSide( best, bestType, now );
		PruneIncomingLatch( now );
		return true;
	}

	void PruneIncomingLatch( float now )
	{
		if ( _incomingLatch.Count < 8 )
			return;

		foreach ( var key in new System.Collections.Generic.List<GameObject>( _incomingLatch.Keys ) )
		{
			if ( !key.IsValid() || now - _incomingLatch[key].SeenAt > 1f )
				_incomingLatch.Remove( key );
		}
	}

	/// <summary>The attacker is inside the best distance so far and faces this pawn; narrows <paramref name="bestSq"/> when so.</summary>
	static bool IsCloserThreat( GameObject attacker, Vector3 attackerForward, Vector3 me, float cosFacing, ref float bestSq )
	{
		var toMe = (me - attacker.WorldPosition).WithZ( 0f );
		var distSq = toMe.LengthSquared;
		if ( distSq > bestSq || distSq < 1f )
			return false;

		var facing = attackerForward.WithZ( 0f );
		if ( facing.LengthSquared < 1e-6f || Vector3.Dot( facing.Normal, toMe.Normal ) < cosFacing )
			return false;

		bestSq = distSq;
		return true;
	}
}
