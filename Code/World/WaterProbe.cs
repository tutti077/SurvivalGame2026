using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// "Is this point under water?" for gameplay that is not the swim controller: the torch flame, the wet
/// status. Two sources, either counts: the terrain world's sea / lake / river surfaces
/// (<see cref="TerrainWorldManager.TryGetWaterDepthMeters"/>), and any <c>water</c>-tagged trigger
/// volume (the test scenes' swim boxes). Cheap enough for a few probes a second, not for every frame.
/// </summary>
public static class WaterProbe
{
	public const string WaterTag = "water";

	public static bool IsPointUnderWater( Scene scene, Vector3 worldPos )
	{
		if ( scene is null || !scene.IsValid() )
			return false;

		if ( TerrainWorldManager.TryGetWaterDepthMeters( scene, worldPos, out var depthMeters ) && depthMeters > 0f )
			return true;

		foreach ( var go in scene.FindInPhysics( new Sphere( worldPos, 1f ) ) )
		{
			if ( go is { IsValid: true } && go.Tags.Has( WaterTag ) )
				return true;
		}

		return false;
	}
}
