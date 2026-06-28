namespace Survival;

/// <summary>
/// Hook for authoritative world sampling at (x, y) meters.
/// Height, biomes, and collision always use this — never biome preview PNG textures.
/// </summary>
public interface ITerrainPreviewBackend
{
	TerrainPreviewSample Sample( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters );
}
