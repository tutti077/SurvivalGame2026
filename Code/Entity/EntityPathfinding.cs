using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>Nav queries, wander points, and dynamic structure blocking checks.</summary>
static class EntityPathfinding
{
	const float BlockTraceRadius = 14f;
	const float BlockTraceLift = 32f;

	public static bool TryFindWanderPoint( Scene scene, Vector3 origin, float radius, NavMeshAgent agent, out Vector3 point )
	{
		point = default;
		if ( !scene.IsValid() || agent is null || !agent.IsValid() )
			return false;

		var nav = scene.NavMesh;
		if ( nav is null || !nav.IsEnabled )
			return false;

		for ( var attempt = 0; attempt < 8; attempt++ )
		{
			var offset = new Vector3(
				Sandbox.Game.Random.Float( -radius, radius ),
				Sandbox.Game.Random.Float( -radius, radius ),
				0f );

			var probe = origin + offset;
			var sample = nav.GetRandomPoint( probe, Math.Max( 32f, radius * 0.35f ) );
			if ( !sample.HasValue )
				continue;

			if ( Vector3.DistanceBetween( sample.Value.WithZ( 0f ), origin.WithZ( 0f ) ) > radius )
				continue;

			var path = EntityChaseRouting.QueryPath( scene, origin, sample.Value, agent );
			if ( !path.HasPath )
				continue;

			point = sample.Value;
			return true;
		}

		return false;
	}

	public static BuildPiece TryFindBlockingStructure( Scene scene, Vector3 from, Vector3 to, GameObject ignoreRoot )
	{
		if ( !scene.IsValid() )
			return null;

		var start = from + Vector3.Up * BlockTraceLift;
		var end = to + Vector3.Up * BlockTraceLift;
		var trace = scene.Trace.Ray( start, end )
			.Radius( BlockTraceRadius )
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();

		if ( !trace.Hit )
			return null;

		return FindBlockingBuildPiece( trace.GameObject );
	}

	public static BuildPiece FindBlockingBuildPiece( GameObject hit )
	{
		for ( var current = hit; current.IsValid(); current = current.Parent )
		{
			var piece = current.Components.Get<BuildPiece>();
			if ( piece is null || !piece.Enabled || !piece.GameObject.IsValid() )
				continue;

			if ( piece.IsPreviewGhost || piece.IsBlueprint )
				continue;

			if ( BuildPieceNavPolicy.GetCategory( piece.PieceId ) == BuildNavCategory.WalkablePath )
				continue;

			return piece;
		}

		return null;
	}

	public static bool IsRouteBlockedByStructure(
		Scene scene,
		EntityChaseRouting.NavPathQuery path,
		Vector3 from,
		Vector3 to,
		GameObject ignoreRoot )
	{
		if ( path.Status == NavMeshPathStatus.PathNotFound )
			return TryFindBlockingStructure( scene, from, to, ignoreRoot ) is not null;

		if ( path.Status != NavMeshPathStatus.Partial )
			return false;

		return TryFindBlockingStructure( scene, from, to, ignoreRoot ) is not null;
	}
}
