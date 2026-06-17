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
			return direction.LengthSquared > 1e-8f;
		}

		return false;
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
