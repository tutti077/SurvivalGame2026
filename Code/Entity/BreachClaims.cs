using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side registry of which entities are hitting which build piece. Every entity brain runs
/// on the host, so this static table IS the "is this piece available?" check — no RPC needed.
/// At most <see cref="MaxPerPiece"/> entities claim one piece; the others pick another piece of
/// the same structure. A claim is a slot index (0..2) the claimant uses to take a different stand
/// along the piece face, so they spread out instead of shoving each other through the wall.
/// Claims are released on piece change, on leaving Breaching, and on death / destroy; dead or
/// re-targeted claimants are pruned whenever a piece is queried.
/// </summary>
public static class BreachClaims
{
	public const int MaxPerPiece = 3;

	static readonly Dictionary<BuildPiece, List<EntityBrain>> _claims = new();

	/// <summary>Could <paramref name="brain"/> claim this piece right now (already holding it counts as yes)?</summary>
	public static bool CanClaim( BuildPiece piece, EntityBrain brain )
	{
		if ( piece is null || !piece.IsValid() )
			return false;

		var list = Prune( piece );
		return list.Contains( brain ) || list.Count < MaxPerPiece;
	}

	/// <summary>Take (or keep) a slot on the piece. False when it is full.</summary>
	public static bool TryClaim( BuildPiece piece, EntityBrain brain, out int slot )
	{
		slot = 0;
		if ( piece is null || !piece.IsValid() || brain is null )
			return false;

		var list = Prune( piece );
		var existing = list.IndexOf( brain );
		if ( existing >= 0 )
		{
			slot = existing;
			return true;
		}

		if ( list.Count >= MaxPerPiece )
			return false;

		list.Add( brain );
		slot = list.Count - 1;
		return true;
	}

	/// <summary>The slot <paramref name="brain"/> holds on the piece, else the slot it would get next.</summary>
	public static int NextSlot( BuildPiece piece, EntityBrain brain )
	{
		if ( piece is null || !piece.IsValid() )
			return 0;

		var list = Prune( piece );
		var existing = list.IndexOf( brain );
		return existing >= 0 ? existing : list.Count;
	}

	/// <summary>
	/// Is any claimed piece (one an entity is hitting right now — breach or raid) inside
	/// <paramref name="bounds"/> grown by <paramref name="padding"/>? Per Mark: a wall coming down only
	/// warrants a nav rebake when it is the wall someone is working on or the one next to it.
	/// </summary>
	public static bool AnyClaimNear( BBox bounds, float padding )
	{
		var mins = bounds.Mins - new Vector3( padding, padding, padding );
		var maxs = bounds.Maxs + new Vector3( padding, padding, padding );
		foreach ( var entry in _claims )
		{
			var piece = entry.Key;
			if ( piece is null || !piece.IsValid() || !piece.GameObject.IsValid() || entry.Value.Count == 0 )
				continue;

			var p = piece.GameObject.WorldPosition;
			if ( p.x >= mins.x && p.x <= maxs.x && p.y >= mins.y && p.y <= maxs.y && p.z >= mins.z && p.z <= maxs.z )
				return true;
		}

		return false;
	}

	/// <summary>Drop every claim this entity holds.</summary>
	public static void Release( EntityBrain brain )
	{
		if ( brain is null )
			return;

		List<BuildPiece> empty = null;
		foreach ( var entry in _claims )
		{
			if ( entry.Value.Remove( brain ) && entry.Value.Count == 0 )
			{
				empty ??= new List<BuildPiece>();
				empty.Add( entry.Key );
			}
		}

		if ( empty is null )
			return;

		foreach ( var piece in empty )
			_claims.Remove( piece );
	}

	/// <summary>Live claimants only: dead / destroyed entities and ones that moved on to another piece drop out.</summary>
	static List<EntityBrain> Prune( BuildPiece piece )
	{
		if ( !_claims.TryGetValue( piece, out var list ) )
		{
			list = new List<EntityBrain>();
			_claims[piece] = list;
			return list;
		}

		list.RemoveAll( b => b is null || !b.IsValid() || !b.Enabled || b.ClaimedPiece != piece );
		return list;
	}
}
