using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Resolves the active view camera for build rays (third-person safe).
/// </summary>
public static class BuildViewCamera
{
	public static bool TryGetViewRay( GameObject pawn, out Vector3 origin, out Vector3 direction )
	{
		origin = default;
		direction = default;
		if ( !pawn.IsValid() )
			return false;

		var cam = Resolve( pawn );
		if ( cam.IsValid() )
		{
			origin = cam.WorldPosition;
			direction = cam.WorldRotation.Forward.Normal;
			if ( direction.LengthSquared < 1e-8f )
				return false;

			// Third-person: start the build ray past the pawn so the body can't steal aim hits.
			if ( !IsFirstPersonViewCamera( pawn, cam ) )
				origin = PushRayOriginPastPawn( pawn, origin, direction );

			return true;
		}

		return false;
	}

	/// <summary>
	/// Move the ray start forward along the view until it clears the pawn capsule.
	/// Keep this tight — a large push put nearby/high seams behind the ray origin.
	/// </summary>
	static Vector3 PushRayOriginPastPawn( GameObject pawn, Vector3 rayOrigin, Vector3 rayDir )
	{
		var pawnPos = pawn.WorldPosition + Vector3.Up * 36f;
		var toPawn = pawnPos - rayOrigin;
		var along = Vector3.Dot( toPawn, rayDir );
		if ( along <= 0f )
			return rayOrigin;

		var radius = 16f;
		var pc = pawn.Components.Get<PlayerController>();
		if ( pc is not null && pc.IsValid() )
			radius = Math.Max( 12f, pc.BodyRadius );

		// Just past the torso along the view — TraceBuildRay still skips player hits.
		var clearPast = along + radius + 8f;
		return rayOrigin + rayDir * clearPast;
	}

	/// <summary>
	/// Prefer the scene render camera (third-person view), then pawn cameras — never pawn body facing.
	/// </summary>
	public static CameraComponent Resolve( GameObject pawn )
	{
		if ( !pawn.IsValid() )
			return default;

		var scene = pawn.Scene;
		if ( scene.IsValid() )
		{
			var sceneCam = scene.Camera;
			if ( sceneCam.IsValid() )
			{
				if ( IsLikelyViewCamera( pawn, sceneCam ) || IsFirstPersonViewCamera( pawn, sceneCam ) )
					return sceneCam;
			}
		}

		if ( TryFindFirstCameraInHierarchy( pawn, out var descendant ) && descendant.IsValid() )
			return descendant;

		for ( var go = pawn; go.IsValid(); go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is null )
				continue;

			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
				return embedded;
		}

		if ( scene.IsValid() )
		{
			var fallbackSceneCam = scene.Camera;
			if ( fallbackSceneCam.IsValid() )
				return fallbackSceneCam;
		}

		return default;
	}

	static bool IsLikelyViewCamera( GameObject pawn, CameraComponent cam )
	{
		if ( !pawn.IsValid() || !cam.IsValid() )
			return false;

		var offset = cam.WorldPosition - pawn.WorldPosition;
		return offset.Length > 32f;
	}

	static bool IsFirstPersonViewCamera( GameObject pawn, CameraComponent cam )
	{
		if ( !pawn.IsValid() || !cam.IsValid() )
			return false;

		for ( var go = cam.GameObject; go.IsValid(); go = go.Parent )
		{
			if ( go != pawn )
				continue;

			var offset = cam.WorldPosition - pawn.WorldPosition;
			return offset.Length <= 96f;
		}

		return false;
	}

	/// <summary>True when the pawn's active view camera is first-person (near the body).</summary>
	public static bool IsFirstPersonView( GameObject pawn )
	{
		var cam = Resolve( pawn );
		return cam.IsValid() && IsFirstPersonViewCamera( pawn, cam );
	}

	/// <summary>
	/// World-space horizontal forward for where the pawn is visually facing.
	/// Prefers camera yaw (third-person body aim) over physics-root rotation, which may stay locked.
	/// </summary>
	public static bool TryGetHorizontalFacingForward( GameObject pawn, out Vector3 forward )
	{
		forward = default;
		if ( !pawn.IsValid() )
			return false;

		var cam = Resolve( pawn );
		if ( cam.IsValid() )
		{
			var yaw = cam.WorldRotation.Angles().yaw;
			forward = new Angles( 0f, yaw, 0f ).ToRotation().Forward;
			if ( forward.LengthSquared > 1e-8f )
				return true;
		}

		var pc = pawn.Components.Get<PlayerController>();
		if ( pc?.Renderer is { IsValid: true } renderer && renderer.GameObject.IsValid() )
		{
			forward = renderer.GameObject.WorldRotation.Forward.WithZ( 0 ).Normal;
			if ( forward.LengthSquared > 1e-8f )
				return true;
		}

		forward = pawn.WorldRotation.Forward.WithZ( 0 ).Normal;
		return forward.LengthSquared > 1e-8f;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent cam )
	{
		cam = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			cam = self;
			return true;
		}

		foreach ( var child in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( child, out cam ) )
				return true;
		}

		return false;
	}
}
