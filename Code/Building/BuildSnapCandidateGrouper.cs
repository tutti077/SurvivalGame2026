using System.Collections.Generic;
using Sandbox;

namespace Survival;

static class BuildSnapCandidateGrouper
{
	public const float LockBreakScoreMargin = 18f;

	public static void FinalizeCandidates(
		List<BuildSnapCandidate> candidates,
		string placingPieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps )
	{
		if ( candidates.Count == 0 )
			return;

		var buckets = new Dictionary<BuildSnapGroupKey, List<BuildSnapCandidate>>();
		for ( var i = 0; i < candidates.Count; i++ )
		{
			var candidate = candidates[i];
			if ( candidate.TargetPiece is null || !candidate.TargetPiece.IsValid() )
				continue;

			var key = GetGroupKey( candidate );
			var anchorRole = GetAnchorRole( placingSnaps, candidate.AnchorSnapIndex );
			var targetRole = candidate.TargetPiece.SnapPoints[candidate.TargetSnapIndex].Role;
			var priorityIndex = BuildSnapAutoRules.GetAnchorPriorityIndex(
				placingPieceId,
				candidate.TargetPiece.PieceId,
				anchorRole,
				targetRole,
				candidate.IsEdgeSnap );

			var member = candidate with
			{
				GroupKey = key,
				AnchorPriority = priorityIndex,
				Score = candidate.Score + BuildSnapAutoRules.ScoreAnchorPriority( priorityIndex ),
			};

			if ( !buckets.TryGetValue( key, out var list ) )
			{
				list = new List<BuildSnapCandidate>();
				buckets[key] = list;
			}

			list.Add( member );
		}

		var orderedGroups = new List<( BuildSnapGroupKey Key, float BestScore, List<BuildSnapCandidate> Members )>();
		foreach ( var pair in buckets )
		{
			var best = float.MaxValue;
			for ( var i = 0; i < pair.Value.Count; i++ )
				best = System.Math.Min( best, pair.Value[i].Score );

			orderedGroups.Add( ( pair.Key, best, pair.Value ) );
		}

		orderedGroups.Sort( static ( a, b ) => a.BestScore.CompareTo( b.BestScore ) );

		candidates.Clear();
		for ( var g = 0; g < orderedGroups.Count; g++ )
		{
			var members = orderedGroups[g].Members;
			members.Sort( static ( a, b ) =>
			{
				var byPriority = a.AnchorPriority.CompareTo( b.AnchorPriority );
				return byPriority != 0 ? byPriority : a.Score.CompareTo( b.Score );
			} );

			for ( var m = 0; m < members.Count; m++ )
				candidates.Add( members[m] with { AnchorVariantIndex = m } );
		}
	}

	public static bool TryPickCandidate(
		IReadOnlyList<BuildSnapCandidate> candidates,
		int anchorVariantIndex,
		BuildSnapGroupKey? lockedGroup,
		string placingPieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange,
		out BuildSnapCandidate selected,
		out int variantCount )
	{
		selected = default;
		variantCount = 0;
		if ( candidates is null || candidates.Count == 0 )
			return false;

		var bestGroup = candidates[0].GroupKey;
		var bestScore = candidates[0].Score;

		if ( lockedGroup is { } locked
		     && TryGetGroupBestScore( candidates, locked, out var lockedScore )
		     && ShouldKeepLockedGroup(
			     candidates,
			     locked,
			     lockedScore,
			     bestGroup,
			     bestScore,
			     placingPieceId,
			     placingSnaps,
			     rayOrigin,
			     rayDirection,
			     maxRange ) )
		{
			bestGroup = locked;
		}

		variantCount = CountVariants( candidates, bestGroup );
		var clamped = variantCount > 0
			? ( anchorVariantIndex % variantCount + variantCount ) % variantCount
			: 0;

		for ( var i = 0; i < candidates.Count; i++ )
		{
			if ( !candidates[i].GroupKey.Equals( bestGroup ) )
				continue;

			if ( candidates[i].AnchorVariantIndex == clamped )
			{
				selected = candidates[i];
				return true;
			}
		}

		for ( var i = 0; i < candidates.Count; i++ )
		{
			if ( candidates[i].GroupKey.Equals( bestGroup ) )
			{
				selected = candidates[i];
				return true;
			}
		}

		selected = candidates[0];
		return true;
	}

	static bool ShouldKeepLockedGroup(
		IReadOnlyList<BuildSnapCandidate> candidates,
		BuildSnapGroupKey locked,
		float lockedScore,
		BuildSnapGroupKey bestGroup,
		float bestScore,
		string placingPieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		Vector3 rayOrigin,
		Vector3 rayDirection,
		float maxRange )
	{
		if ( !TryGetBuiltFocusPoint( candidates, locked, placingPieceId, placingSnaps, out var builtWorld, out var mateWorld ) )
			return false;

		if ( !BuildSnapCrosshair.IsMateReachable( rayOrigin, rayDirection, builtWorld, mateWorld, maxRange ) )
			return false;

		if ( locked.Equals( bestGroup ) )
			return true;

		return lockedScore <= bestScore + LockBreakScoreMargin;
	}

	static bool TryGetGroupBestScore(
		IReadOnlyList<BuildSnapCandidate> candidates,
		BuildSnapGroupKey group,
		out float bestScore )
	{
		bestScore = float.MaxValue;
		var found = false;
		for ( var i = 0; i < candidates.Count; i++ )
		{
			if ( !candidates[i].GroupKey.Equals( group ) )
				continue;

			bestScore = System.Math.Min( bestScore, candidates[i].Score );
			found = true;
		}

		return found;
	}

	static bool TryGetBuiltFocusPoint(
		IReadOnlyList<BuildSnapCandidate> candidates,
		BuildSnapGroupKey group,
		string placingPieceId,
		IReadOnlyList<BuildSnapPoint> placingSnaps,
		out Vector3 builtWorld,
		out Vector3 mateWorld )
	{
		builtWorld = default;
		mateWorld = default;
		for ( var i = 0; i < candidates.Count; i++ )
		{
			if ( !candidates[i].GroupKey.Equals( group ) )
				continue;

			builtWorld = BuildSnapPlacement.GetBuiltFocusPoint( candidates[i] );
			mateWorld = BuildSnapPlacement.GetMateFocusPoint( candidates[i], placingPieceId, placingSnaps );
			return true;
		}

		return false;
	}

	static int CountVariants( IReadOnlyList<BuildSnapCandidate> candidates, BuildSnapGroupKey group )
	{
		var count = 0;
		for ( var i = 0; i < candidates.Count; i++ )
		{
			if ( candidates[i].GroupKey.Equals( group ) )
				count++;
		}

		return count;
	}

	static BuildSnapGroupKey GetGroupKey( BuildSnapCandidate candidate )
	{
		if ( candidate.IsEdgeSnap )
			return BuildSnapGroupKey.ForEdge( candidate.TargetPiece, candidate.TargetEdgeId );

		var role = candidate.TargetPiece.SnapPoints[candidate.TargetSnapIndex].Role;
		return BuildSnapGroupKey.ForCorner( candidate.TargetPiece, role );
	}

	static BuildSnapRole GetAnchorRole( IReadOnlyList<BuildSnapPoint> placingSnaps, int anchorIndex )
	{
		if ( placingSnaps is null || anchorIndex < 0 || anchorIndex >= placingSnaps.Count )
			return BuildSnapRole.Unknown;

		return placingSnaps[anchorIndex].Role;
	}
}
