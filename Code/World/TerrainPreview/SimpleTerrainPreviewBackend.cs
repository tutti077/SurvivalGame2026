namespace Survival;

/// <summary>Biome-driven terrain sampler — delegates to <see cref="TerrainPreviewPipeline"/>.</summary>
public sealed class SimpleTerrainPreviewBackend : ITerrainPreviewBackend
{
	public TerrainPreviewSample Sample( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
		=> TerrainPreviewPipeline.Sample( settings, worldXMeters, worldYMeters );
}
