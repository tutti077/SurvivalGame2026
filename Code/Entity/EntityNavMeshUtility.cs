using System;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

public enum NavProjectTier
{
	/// <summary>Runtime chase / repath — a few local samples only.</summary>
	Fast,
	/// <summary>Spawn / recovery when fast projection fails.</summary>
	Full
}

/// <summary>Query baked/runtime nav — never regenerate world tiles at spawn (that overwrites editor bake).</summary>
static class EntityNavMeshUtility
{
	static readonly float[] ProjectRadii = { 64f, 128f, 256f, 512f, 1024f };
	static readonly float[] FastProjectRadii = { 64f, 128f, 256f };
	static readonly float[] HeightOffsets = { 0f, 32f, 64f, 96f, 128f, -32f, -64f };
	static readonly float[] FastHeightOffsets = { 0f, 32f, -32f };

	public static bool TryProjectToNavMesh( Scene scene, Vector3 near, out Vector3 onNav, NavProjectTier tier = NavProjectTier.Full, float maxRadius = 1024f )
	{
		onNav = default;
		if ( !scene.IsValid() )
			return false;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return false;

		if ( TryProjectFast( navMesh, near, out onNav, maxRadius ) )
			return true;

		if ( tier == NavProjectTier.Fast )
			return false;

		return TryProjectFull( navMesh, near, out onNav, maxRadius );
	}

	static bool TryProjectFast( NavMesh navMesh, Vector3 near, out Vector3 onNav, float maxRadius )
	{
		onNav = default;
		Vector3? best = null;
		var bestDist = float.MaxValue;

		foreach ( var height in FastHeightOffsets )
		{
			var probe = near + Vector3.Up * height;

			foreach ( var radius in FastProjectRadii )
			{
				if ( radius > maxRadius )
					break;

				var sphereSample = navMesh.GetRandomPoint( probe, radius );
				if ( !sphereSample.HasValue )
					continue;

				var dist = Vector3.DistanceBetween( sphereSample.Value, near );
				if ( dist > radius || dist >= bestDist )
					continue;

				bestDist = dist;
				best = sphereSample.Value;
			}
		}

		if ( !best.HasValue )
			return false;

		onNav = best.Value;
		return true;
	}

	static bool TryProjectFull( NavMesh navMesh, Vector3 near, out Vector3 onNav, float maxRadius )
	{
		onNav = default;
		Vector3? best = null;
		var bestDist = float.MaxValue;

		foreach ( var height in HeightOffsets )
		{
			var probe = near + Vector3.Up * height;

			foreach ( var radius in ProjectRadii )
			{
				if ( radius > maxRadius )
					break;

				var vertical = Math.Max( 96f, radius * 0.35f );
				var bbox = new BBox(
					probe - new Vector3( radius, radius, vertical ),
					probe + new Vector3( radius, radius, vertical ) );

				for ( var attempt = 0; attempt < 24; attempt++ )
				{
					var sample = navMesh.GetRandomPoint( bbox );
					if ( !sample.HasValue )
						continue;

					var dist = Vector3.DistanceBetween( sample.Value, near );
					if ( dist > radius || dist >= bestDist )
						continue;

					bestDist = dist;
					best = sample.Value;
				}

				var sphereSample = navMesh.GetRandomPoint( probe, radius );
				if ( sphereSample.HasValue )
				{
					var dist = Vector3.DistanceBetween( sphereSample.Value, near );
					if ( dist <= radius && dist < bestDist )
					{
						bestDist = dist;
						best = sphereSample.Value;
					}
				}
			}
		}

		if ( !best.HasValue )
			return false;

		onNav = best.Value;
		return true;
	}

	/// <summary>
	/// Is there walkable nav right where <paramref name="feet"/> is — the floor / roof / terrain the
	/// player is standing on? Samples a tight box around the feet (never the ground a storey below),
	/// so unlike the radius projections it cannot answer "yes" with a point under a roof edge or
	/// "no" by random chance. Returns the closest sample found.
	/// </summary>
	public static bool TryFindNavAtFeet( Scene scene, Vector3 feet, out Vector3 onNav, float horizontal = 40f, float vertical = 32f, int attempts = 6 )
	{
		onNav = default;
		if ( !scene.IsValid() )
			return false;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return false;

		var box = new BBox(
			feet - new Vector3( horizontal, horizontal, vertical ),
			feet + new Vector3( horizontal, horizontal, vertical ) );

		var bestDist = float.MaxValue;
		var found = false;
		for ( var i = 0; i < attempts; i++ )
		{
			var sample = navMesh.GetRandomPoint( box );
			if ( !sample.HasValue )
				continue;

			var dist = Vector3.DistanceBetween( sample.Value, feet );
			if ( dist >= bestDist )
				continue;

			bestDist = dist;
			onNav = sample.Value;
			found = true;
		}

		return found;
	}

	/// <summary>
	/// Put the agent on nav next to where it already is. Default: a tight box (±<paramref name="maxSnap"/>
	/// u) that must be reachable without crossing a solid — the entity never moves more than a body
	/// width, and never through a wall. The old behaviour (random sample out to 1024 u, then
	/// WorldPosition = sample) ran on every nav rebake: the moment a wall was placed on the entity or
	/// the wall it was hitting fell, it was yanked across the wall or 5 m away ("teleports away").
	/// Spawn placement passes a large <paramref name="maxSnap"/> and keeps the wide search.
	/// </summary>
	public static bool EnsureAgentOnNavMesh( Scene scene, NavMeshAgent agent, Vector3 near, float maxSnap = 48f )
	{
		if ( agent is null || !agent.IsValid() || !scene.IsValid() )
			return false;

		if ( maxSnap > 128f )
		{
			if ( !TryProjectToNavMesh( scene, near, out var far, NavProjectTier.Full, maxSnap ) )
				return false;

			agent.GameObject.WorldPosition = far;
			agent.SetAgentPosition( far );
			return true;
		}

		// Tight first; then a body-length wider. The strict version alone failed after a landing
		// beside a wall, and a failed snap left UpdatePosition off — the agent kept pathing while
		// the body never moved ("wedged" every 9 s with a complete route).
		if ( !TryFindNavAtFeet( scene, near, out var onNav, horizontal: maxSnap, vertical: 48f )
		     && !TryFindNavAtFeet( scene, near, out onNav, horizontal: maxSnap * 2.5f, vertical: 64f ) )
			return false;

		// Same side of any wall: a thin piece can sit inside a 48 u box.
		var from = near + Vector3.Up * 40f;
		var to = onNav + Vector3.Up * 40f;
		if ( Vector3.DistanceBetween( from, to ) > 4f )
		{
			var trace = scene.Trace.Ray( from, to )
				.UsePhysicsWorld()
				.IgnoreGameObjectHierarchy( agent.GameObject )
				.Run();
			if ( trace.Hit && trace.GameObject.IsValid() && trace.Normal.z < 0.55f )
				return false;
		}

		agent.GameObject.WorldPosition = onNav;
		agent.SetAgentPosition( onNav );
		return true;
	}
}
