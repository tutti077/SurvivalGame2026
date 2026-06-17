using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>Nav path queries and chase goal helpers.</summary>
static class EntityChaseRouting
{
	public readonly struct NavPathQuery
	{
		public float Length { get; init; }
		public NavMeshPathStatus Status { get; init; }
		public List<Vector3> Points { get; init; }
		public bool HasPath => Points is not null && Points.Count >= 2;
	}

	public static NavPathQuery QueryPath(
		Scene scene,
		Vector3 start,
		Vector3 target,
		NavMeshAgent agent,
		NavProjectTier tier = NavProjectTier.Fast,
		Vector3? startOnNav = null )
	{
		var result = new NavPathQuery
		{
			Length = float.MaxValue,
			Status = NavMeshPathStatus.PathNotFound,
			Points = null
		};

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled || agent is null || !agent.IsValid() )
			return result;

		var pathStart = startOnNav ?? start;
		if ( !startOnNav.HasValue )
		{
			if ( !EntityNavMeshUtility.TryProjectToNavMesh( scene, start, out var projectedStart, tier ) )
			{
				if ( tier == NavProjectTier.Fast
				     && EntityNavMeshUtility.TryProjectToNavMesh( scene, start, out projectedStart, NavProjectTier.Full ) )
				{
					pathStart = projectedStart;
				}
				else
				{
					return new NavPathQuery
					{
						Length = float.MaxValue,
						Status = NavMeshPathStatus.StartNotFound,
						Points = null
					};
				}
			}
			else
			{
				pathStart = projectedStart;
			}
		}

		if ( !EntityNavMeshUtility.TryProjectToNavMesh( scene, target, out var pathTarget, tier ) )
		{
			if ( tier == NavProjectTier.Fast
			     && EntityNavMeshUtility.TryProjectToNavMesh( scene, target, out pathTarget, NavProjectTier.Full ) )
			{
				// fall through
			}
			else
			{
				return new NavPathQuery
				{
					Length = float.MaxValue,
					Status = NavMeshPathStatus.TargetNotFound,
					Points = null
				};
			}
		}

		var path = navMesh.CalculatePath( new CalculatePathRequest
		{
			Start = pathStart,
			Target = pathTarget,
			Agent = agent
		} );

		if ( !path.IsValid || path.Points is null || path.Points.Count < 2 )
			return result;

		var length = 0f;
		for ( var i = 0; i < path.Points.Count - 1; i++ )
			length += Vector3.DistanceBetween( path.Points[i].Position, path.Points[i + 1].Position );

		var points = new List<Vector3>( path.Points.Count );
		foreach ( var point in path.Points )
			points.Add( point.Position );

		return new NavPathQuery
		{
			Length = length,
			Status = path.Status,
			Points = points
		};
	}

	public static Vector3 OffsetChaseGoal( Vector3 navGoal, Vector3 enemyAnchor, float standOff )
	{
		var flat = (navGoal - enemyAnchor).WithZ( 0 );
		if ( flat.Length <= standOff + 1f )
			return navGoal;

		return navGoal - flat.Normal * standOff;
	}
}
