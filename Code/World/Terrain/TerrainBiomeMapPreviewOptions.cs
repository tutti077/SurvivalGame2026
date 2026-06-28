namespace Survival;

/// <summary>
/// Inspector / PNG biome map options only. Does not affect height, biomes, or meshes —
/// those always come from <see cref="ITerrainPreviewBackend.Sample"/> at world meters.
/// </summary>
public sealed class TerrainBiomeMapPreviewOptions
{
	public bool SpeckFilterEnabled { get; set; } = true;
	public float MinPatchDiameterMeters { get; set; } = 200f;

	public static TerrainBiomeMapPreviewOptions FromSettings( TerrainPreviewSettings settings )
		=> new()
		{
			SpeckFilterEnabled = settings.BiomeSpeckFilterEnabled,
			MinPatchDiameterMeters = settings.BiomeMinPatchDiameterMeters,
		};
}
