using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side melee ring around each player an entity is fighting (Mark 2026-11-04, scav AI cleanup: groups arrived on one
/// line and stacked on the player's feet, all swinging on their own clocks). Two things per target:
/// <list type="bullet">
/// <item>a <b>bearing</b> per attacker — the free angle nearest the side it came from, at least
/// <see cref="SlotSpacingDegrees"/> from every other attacker's — so the close-range chase goal is a point on a ring
/// around the player instead of the player's own position;</item>
/// <item>at most <see cref="MaxActiveAttackers"/> <b>attack tokens</b> — only a token holder may start a telegraph; the
/// others hold their slot and face the player until a token frees up.</item>
/// </list>
/// Every brain runs on the host, so this static table is the whole check (same pattern as <see cref="BreachClaims"/>).
/// Entries are released on leaving the melee states and on death / destroy; invalid brains are pruned on every query.
/// </summary>
public static class EntityAttackSlots
{
	public const int MaxActiveAttackers = 2;
	public const float SlotSpacingDegrees = 55f;

	sealed class Ring
	{
		public readonly Dictionary<EntityBrain, float> Bearings = new();
		public readonly List<EntityBrain> Tokens = new();
	}

	static readonly Dictionary<GameObject, Ring> _rings = new();
	static readonly Dictionary<EntityBrain, GameObject> _targetOf = new();

	/// <summary>The bearing (world yaw, degrees, from the target out to the attacker's slot) this brain holds around <paramref name="target"/>.</summary>
	public static float ClaimBearing( GameObject target, EntityBrain brain, float wantBearing )
	{
		if ( target is null || !target.IsValid() || brain is null )
			return wantBearing;

		SwitchTarget( brain, target );
		var ring = GetRing( target );
		if ( ring.Bearings.TryGetValue( brain, out var held ) )
			return held;

		// The free bearing closest to where it came from: try the wanted angle, then step out either side.
		var best = wantBearing;
		for ( var step = 0; step <= 6; step++ )
		{
			var candidateA = wantBearing + step * SlotSpacingDegrees * 0.5f;
			if ( IsFree( ring, brain, candidateA ) ) { best = candidateA; break; }
			var candidateB = wantBearing - step * SlotSpacingDegrees * 0.5f;
			if ( IsFree( ring, brain, candidateB ) ) { best = candidateB; break; }
		}

		best = Angles.NormalizeAngle( best );
		ring.Bearings[brain] = best;
		return best;
	}

	/// <summary>Take (or keep) one of the target's attack tokens. False while <see cref="MaxActiveAttackers"/> others hold them.</summary>
	public static bool TryTakeToken( GameObject target, EntityBrain brain )
	{
		if ( target is null || !target.IsValid() || brain is null )
			return true;

		SwitchTarget( brain, target );
		var ring = GetRing( target );
		if ( ring.Tokens.Contains( brain ) )
			return true;
		if ( ring.Tokens.Count >= MaxActiveAttackers )
			return false;

		ring.Tokens.Add( brain );
		return true;
	}

	/// <summary>Give the token back (swing finished): the next waiting attacker may take it.</summary>
	public static void ReturnToken( EntityBrain brain )
	{
		if ( brain is null || !_targetOf.TryGetValue( brain, out var target ) || !_rings.TryGetValue( target, out var ring ) )
			return;

		ring.Tokens.Remove( brain );
	}

	/// <summary>Leave the ring entirely (left the melee states, died, destroyed).</summary>
	public static void Release( EntityBrain brain )
	{
		if ( brain is null || !_targetOf.TryGetValue( brain, out var target ) )
			return;

		_targetOf.Remove( brain );
		if ( !_rings.TryGetValue( target, out var ring ) )
			return;

		ring.Bearings.Remove( brain );
		ring.Tokens.Remove( brain );
		if ( ring.Bearings.Count == 0 && ring.Tokens.Count == 0 )
			_rings.Remove( target );
	}

	static void SwitchTarget( EntityBrain brain, GameObject target )
	{
		if ( _targetOf.TryGetValue( brain, out var current ) && current == target )
			return;

		Release( brain );
		_targetOf[brain] = target;
	}

	static Ring GetRing( GameObject target )
	{
		if ( !_rings.TryGetValue( target, out var ring ) )
		{
			ring = new Ring();
			_rings[target] = ring;
		}

		Prune( ring );
		PruneTargets();
		return ring;
	}

	static readonly List<EntityBrain> _pruneScratch = new();

	static void Prune( Ring ring )
	{
		_pruneScratch.Clear();
		foreach ( var b in ring.Bearings.Keys )
			if ( b is null || !b.IsValid() )
				_pruneScratch.Add( b );
		foreach ( var b in _pruneScratch )
		{
			ring.Bearings.Remove( b );
			_targetOf.Remove( b );
		}

		ring.Tokens.RemoveAll( b => b is null || !b.IsValid() );
	}

	static readonly List<GameObject> _targetScratch = new();

	/// <summary>Drop rings whose player is gone (left, scene change).</summary>
	static void PruneTargets()
	{
		_targetScratch.Clear();
		foreach ( var t in _rings.Keys )
			if ( t is null || !t.IsValid() )
				_targetScratch.Add( t );
		foreach ( var t in _targetScratch )
			_rings.Remove( t );
	}

	static bool IsFree( Ring ring, EntityBrain self, float bearing )
	{
		foreach ( var (other, b) in ring.Bearings )
		{
			if ( other == self )
				continue;
			if ( MathF.Abs( Angles.NormalizeAngle( b - bearing ) ) < SlotSpacingDegrees )
				return false;
		}

		return true;
	}
}
