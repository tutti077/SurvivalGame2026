namespace Survival;

/// <summary>
/// Builds the same solved <see cref="TerrainPreviewSettings"/> the editor preview tool uses.
/// Authoritative for world-meter height/biome sampling — PNG map resolution is separate.
/// </summary>
public static class TerrainPreviewSettingsResolver
{
	public static TerrainPreviewSettings ResolveForWorld(
		int worldSeed,
		float worldDiameterMeters,
		ITerrainPreviewBackend backend )
	{
		var settings = new TerrainPreviewSettings
		{
			WorldSeed = worldSeed,
			WorldDiameterMeters = worldDiameterMeters,
			PreviewMode = TerrainPreviewMode.Biomes,
		};

		if ( TerrainPreviewValleyAutoEvaluate.AutoActive( settings ) )
			TerrainPreviewValleyDefaults.ResetAutoBaselines( settings );

		TerrainPreviewValleyAutoPipeline.Run( settings, backend );
		settings.PreviewMode = TerrainPreviewMode.Biomes;
		return settings;
	}
}
