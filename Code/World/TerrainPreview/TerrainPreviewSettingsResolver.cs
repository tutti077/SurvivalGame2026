namespace Survival;

public static class TerrainPreviewSettingsResolver
{
	public static TerrainPreviewSettings ResolveForWorld(
		int worldSeed,
		float worldDiameterMeters,
		float maxTerrainHeightMeters,
		float oceanRingWidthMeters,
		ITerrainPreviewBackend backend,
		bool runLakeSpawnSolve = false )
		=> ResolveForWorldGeneration( new TerrainWorldGenerationRequest
		{
			WorldSeed = worldSeed,
			WorldDiameterMeters = worldDiameterMeters,
			MaxTerrainHeightMeters = maxTerrainHeightMeters,
			OceanRingWidthMeters = oceanRingWidthMeters,
			Source = TerrainWorldSettingsSource.ComponentDefaultsOnly,
			OverrideWorldScalarsFromComponent = true,
			RunLakeSpawnSolveOnLoad = runLakeSpawnSolve,
		} );

	public static TerrainPreviewSettings ResolveForWorldGeneration( TerrainWorldGenerationRequest request )
	{
		_ = TerrainPreviewBackendRegistry.Active;

		var settings = new TerrainPreviewSettings();
		var sourceLabel = "component defaults";

		if ( TryResolveFromSource( request, out var loaded, out var label ) )
		{
			settings = loaded.CloneForGenerate( false );
			sourceLabel = label;
		}

		if ( request.OverrideWorldScalarsFromComponent )
		{
			settings.WorldSeed = request.WorldSeed;
			settings.WorldDiameterMeters = request.WorldDiameterMeters;
			settings.MaxTerrainHeightMeters = request.MaxTerrainHeightMeters;
			settings.OceanRingWidthMeters = request.OceanRingWidthMeters;
		}

		settings.PreviewMode = TerrainPreviewMode.Biomes;
		settings.EnableLakeSpawnSolveOnGenerate = false;

		if ( request.RunLakeSpawnSolveOnLoad && request.Source == TerrainWorldSettingsSource.ComponentDefaultsOnly )
			TerrainPreviewLakeSpawnSolver.Run( settings );

		TerrainPreviewLandDiskFields.InvalidateCache();
		Log.Info( $"[Terrain] Generation settings from {sourceLabel} — seed {settings.WorldSeed} · lake offset ({settings.LakeOffsetXMeters:0},{settings.LakeOffsetYMeters:0})" );
		return settings;
	}

	static bool TryResolveFromSource(
		TerrainWorldGenerationRequest request,
		out TerrainPreviewSettings settings,
		out string sourceLabel )
	{
		settings = null;
		sourceLabel = null;

		switch ( request.Source )
		{
			case TerrainWorldSettingsSource.ComponentDefaultsOnly:
				return false;

			case TerrainWorldSettingsSource.WorldRecipeFirst:
				if ( TryLoadRecipeSettings( request.WorldName, out settings, out sourceLabel ) )
					return true;

				return TryLoadLatestBundle( out settings, out sourceLabel );

			case TerrainWorldSettingsSource.TunedPreviewFirst:
			default:
				if ( TryLoadLatestBundle( out settings, out sourceLabel ) )
					return true;

				return TryLoadRecipeSettings( request.WorldName, out settings, out sourceLabel );
		}
	}

	static bool TryLoadLatestBundle( out TerrainPreviewSettings settings, out string sourceLabel )
	{
		if ( TerrainPreviewBundleIO.TryLoadLatestGenerationSettings( out settings, out var bundle, out var status ) )
		{
			sourceLabel = $"preview bundle {bundle}";
			return true;
		}

		settings = null;
		sourceLabel = null;
		Log.Info( $"[Terrain] Preview bundle not used ({status})" );
		return false;
	}

	static bool TryLoadRecipeSettings( string worldName, out TerrainPreviewSettings settings, out string sourceLabel )
	{
		settings = null;
		sourceLabel = null;

		var recipe = WorldSaveIO.TryReadRecipe( worldName );
		if ( recipe?.PreviewSettings is null )
			return false;

		settings = recipe.PreviewSettings.CloneForGenerate( false );
		sourceLabel = $"world recipe {worldName}";
		return true;
	}
}
