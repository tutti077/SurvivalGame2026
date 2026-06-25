namespace Survival;

/// <summary>
/// Hook for preview sampling. Swap implementations when full terrain generation is ready.
/// </summary>
public interface ITerrainPreviewBackend
{
	TerrainPreviewSample Sample( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters );
}
