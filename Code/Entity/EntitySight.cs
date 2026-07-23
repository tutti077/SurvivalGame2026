using System;
using Sandbox;

namespace Survival;

/// <summary>FOV + geometric LOS helpers for entity sight (true 3D eye→eye, including hills).</summary>
public static class EntitySight
{
	const float LosProbeRadius = 2f;

	public static bool IsInFov( Vector3 originFlat, Vector3 facingFlat, Vector3 targetFlat, float fovDegrees )
	{
		facingFlat = facingFlat.WithZ( 0f );
		if ( facingFlat.LengthSquared < 1e-8f )
			return true;

		var toTarget = (targetFlat - originFlat).WithZ( 0f );
		if ( toTarget.LengthSquared < 1e-8f )
			return true;

		var half = Math.Clamp( fovDegrees, 1f, 360f ) * 0.5f;
		var minDot = MathF.Cos( half * (MathF.PI / 180f) );
		return Vector3.Dot( facingFlat.Normal, toTarget.Normal ) >= minDot;
	}

	/// <summary>
	/// True only when a physics probe from eye → target eye first hits the target (or clear air).
	/// Uses the physics world so static walls/posts/terrain block. Full 3D (vertical hills OK).
	/// </summary>
	public static bool HasClearLos(
		Scene scene,
		Vector3 eye,
		GameObject selfRoot,
		GameObject targetRoot,
		float targetEyeHeight,
		out string debugDetail )
	{
		debugDetail = "invalid";
		if ( scene is null || !scene.IsValid() || selfRoot is null || !selfRoot.IsValid()
		     || targetRoot is null || !targetRoot.IsValid() )
			return false;

		var targetEye = targetRoot.WorldPosition + Vector3.Up * Math.Max( 0f, targetEyeHeight );
		var delta = targetEye - eye;
		var dist = delta.Length;
		if ( dist <= 1e-3f )
		{
			debugDetail = "coincident";
			return true;
		}

		var dir = delta / dist;
		// Start just outside our own body so we don't self-hit.
		var start = eye + dir * 12f;
		if ( Vector3.DistanceBetween( start, targetEye ) < 8f )
			start = eye;

		var tr = TraceLos( scene, start, targetEye, selfRoot );
		var distMeters = TerrainWorldUnits.EngineToMeters( dist );

		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
		{
			debugDetail = $"clear(noHit) dist={distMeters:0.00}m";
			return true;
		}

		var hitName = tr.GameObject.Name ?? "?";
		var hitDistM = TerrainWorldUnits.EngineToMeters( tr.Distance );

		if ( CombatAuthority.IsGameObjectUnderHierarchy( targetRoot, tr.GameObject ) )
		{
			debugDetail = $"clear(hitTarget:{hitName}) dist={distMeters:0.00}m hitAt={hitDistM:0.00}m";
			return true;
		}

		debugDetail = $"blocked({hitName}) dist={distMeters:0.00}m hitAt={hitDistM:0.00}m";
		return false;
	}

	public static bool HasClearLos(
		Scene scene,
		Vector3 eye,
		GameObject selfRoot,
		GameObject targetRoot,
		float targetEyeHeight ) =>
		HasClearLos( scene, eye, selfRoot, targetRoot, targetEyeHeight, out _ );

	static SceneTraceResult TraceLos( Scene scene, Vector3 start, Vector3 end, GameObject ignoreRoot )
	{
		// Prefer physics world so static ModelColliders (terrain, posts, props) are included.
		var physics = scene.Trace.Ray( start, end )
			.Radius( LosProbeRadius )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();

		if ( physics.Hit && physics.GameObject.IsValid() )
			return physics;

		// Fallback: default scene trace (some setups register colliders only here).
		var fallback = scene.Trace.Ray( start, end )
			.Radius( LosProbeRadius )
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();

		if ( fallback.Hit && fallback.GameObject.IsValid() )
			return fallback;

		// Thin ray last — catches skinny posts the radius probe can tunnel past.
		var thin = scene.Trace.Ray( start, end )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();

		if ( thin.Hit && thin.GameObject.IsValid() )
			return thin;

		return scene.Trace.Ray( start, end )
			.IgnoreGameObjectHierarchy( ignoreRoot )
			.Run();
	}
}
