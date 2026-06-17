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

	/// <summary>Only for walkable build pieces — not world/spawn queries.</summary>
	public static void RequestBuildNavTiles( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		navMesh.RequestTilesGeneration( bounds );
	}

	/// <summary>
	/// Runtime tile generation from physics — use only when placing stairs/ramps etc.
	/// Do not call at enemy spawn: scenes with <c>BakedDataPath</c> already have nav; GenerateTiles
	/// rebuilds from non-static colliders and can wipe the baked mesh.
	/// </summary>
	public static void GenerateBuildNavTiles( Scene scene, BBox bounds )
	{
		if ( !scene.IsValid() )
			return;

		var navMesh = scene.NavMesh;
		if ( navMesh is null || !navMesh.IsEnabled )
			return;

		var physics = scene.PhysicsWorld;
		if ( physics is null )
		{
			RequestBuildNavTiles( scene, bounds );
			return;
		}

		navMesh.GenerateTiles( physics, bounds );
	}

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

	public static bool EnsureAgentOnNavMesh( Scene scene, NavMeshAgent agent, Vector3 near )
	{
		if ( agent is null || !agent.IsValid() || !scene.IsValid() )
			return false;

		if ( !TryProjectToNavMesh( scene, near, out var onNav, NavProjectTier.Full ) )
			return false;
		agent.GameObject.WorldPosition = onNav;
		agent.SetAgentPosition( onNav );
		return true;
	}
}
