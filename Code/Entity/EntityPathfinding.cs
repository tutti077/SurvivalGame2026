using System;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>Nav queries and wander points.</summary>
static class EntityPathfinding
{
	public static bool TryFindWanderPoint( Scene scene, Vector3 origin, float radius, NavMeshAgent agent, out Vector3 point )
	{
		_ = agent;
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

			// Accept the sample without a path pre-check — MoveTo / manual wander handle travel.
			point = sample.Value;
			return true;
		}

		return false;
	}
}
