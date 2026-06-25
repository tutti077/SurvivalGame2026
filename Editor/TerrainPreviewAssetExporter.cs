using System.IO;
using System.Text.Json;

namespace Editor;

/// <summary>Writes incrementing preview PNG bundles under Assets/terrain/preview/.</summary>
static class TerrainPreviewAssetExporter
{
	const string IterationFileName = ".preview_iteration";
	const string ProjectFileName = "survivalgamebasics.sbproj";

	public static string ExportBitmap(
		Bitmap bitmap,
		TerrainPreviewSettings settings,
		out string bundleName,
		TerrainPreviewWaterCoverageStats waterCoverage = default )
	{
		var previewRoot = GetPreviewRootDirectory();
		Directory.CreateDirectory( previewRoot );

		var iteration = ReadIteration( previewRoot ) + 1;
		WriteIteration( previewRoot, iteration );

		bundleName = $"{iteration:000}_seed{settings.WorldSeed}";
		var bundleDir = Path.Combine( previewRoot, bundleName );
		Directory.CreateDirectory( bundleDir );

		var stem = TerrainPreviewGenerator.ModeFileStem( settings.PreviewMode );
		var pngPath = Path.Combine( bundleDir, $"{stem}.png" );
		var rootPreviewPath = Path.Combine( previewRoot, $"{bundleName}.png" );

		File.WriteAllBytes( pngPath, bitmap.ToPng() );
		File.WriteAllBytes( rootPreviewPath, bitmap.ToPng() );

		WriteSettingsSnapshot( bundleDir, settings, iteration, stem );
		WriteWaterCoverageSnapshot( bundleDir, settings, waterCoverage );

		return pngPath;
	}

	static void WriteWaterCoverageSnapshot(
		string bundleDir,
		TerrainPreviewSettings settings,
		TerrainPreviewWaterCoverageStats stats )
	{
		if ( stats.InsideWorldPixels <= 0 )
			return;

		var snapshot = new
		{
			insideWorldPixels = stats.InsideWorldPixels,
			landPixels = stats.LandPixels,
			oceanPixels = stats.OceanPixels,
			interiorOceanPixels = stats.InteriorOceanPixels,
			exteriorOceanPixels = stats.ExteriorOceanPixels,
			landFraction = stats.LandFraction01,
			oceanFraction = stats.OceanFraction01,
			interiorOceanFraction = stats.InteriorOceanFraction01,
			exteriorOceanFraction = stats.ExteriorOceanFraction01,
			interiorOceanOfOceanFraction = stats.InteriorOceanOfOceanFraction01,
			interiorZoneRadius = settings.InteriorZoneRadius01,
			targetTotalOceanFraction = settings.TargetTotalOceanFraction01,
			targetInteriorOceanFraction = settings.TargetInteriorOceanFraction01,
			belowTotalOceanTarget = stats.IsBelowTotalOceanTarget( settings.TargetTotalOceanFraction01 ),
			belowInteriorOceanTarget = stats.IsBelowInteriorOceanTarget( settings.TargetInteriorOceanFraction01 ),
			valleyWeight = settings.ValleyWeight,
			valleyOceanAutoWeight = settings.EnableValleyOceanAutoWeight,
			valleyOceanWeightStep = settings.ValleyOceanWeightStep,
			valleyOceanAutoMinInteriorFraction = settings.ValleyOceanAutoMinInteriorFraction01,
			valleyOceanAutoMaxTotalFraction = settings.ValleyOceanAutoMaxTotalFraction01,
			valleyOceanAbsoluteMaxTotalFraction = settings.ValleyOceanAbsoluteMaxTotalFraction01,
			valleyOceanMaxExteriorFraction = settings.ValleyOceanMaxExteriorFraction01,
			enableInteriorWaterLayer = settings.EnableInteriorWaterLayer,
			interiorWaterFrequency = settings.InteriorWaterFrequency,
			interiorWaterWeight = settings.InteriorWaterWeight,
			interiorWaterAutoStep = settings.InteriorWaterAutoStep,
			valleySpawnAutoFrequency = settings.EnableValleySpawnAutoFrequency,
			valleyAutoExhaustiveSearch = settings.EnableValleyAutoExhaustiveSearch,
			rejectSeedOnAutoFailure = settings.RejectSeedOnAutoFailure,
			valleyAutoSearchTimeoutSeconds = settings.ValleyAutoSearchTimeoutSeconds,
			valleyAutoMaxIterationsPerSeed = settings.ValleyAutoMaxIterationsPerSeed,
			incrementSeedOnAutoFailure = settings.RetrySeedsUntilSolved,
			retrySeedsUntilSolved = settings.RetrySeedsUntilSolved,
			valleyAutoMaxSeedAttempts = settings.ValleyAutoMaxSeedAttempts,
			valleyAutoTunePreviewResolution = settings.ValleyAutoTunePreviewResolution,
			valleyFrequency = settings.ValleyFrequency,
			valleySpawnLandRadiusMeters = settings.ValleySpawnLandRadiusMeters,
			valleySpawnMinLandFraction = settings.ValleySpawnMinLandFraction01,
			valleySpawnAcceptableLandFraction = settings.ValleySpawnAcceptableLandFraction01,
			valleyAutoFrequencyStep = settings.ValleyAutoFrequencyStep,
			valleyAutoFrequencyMin = settings.ValleyAutoFrequencyMin,
			valleyAutoFrequencyMax = settings.ValleyAutoFrequencyMax,
			valleyNearWaterMaxDistanceMeters = settings.ValleyNearWaterMaxDistanceMeters,
			valleyInnerHalfRadius = settings.ValleyInnerHalfRadius01,
			valleyInnerHalfMinOceanFraction = settings.ValleyInnerHalfMinOceanFraction01,
		};

		var json = JsonSerializer.Serialize( snapshot, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( Path.Combine( bundleDir, "water_coverage.json" ), json );
	}

	static void WriteSettingsSnapshot( string bundleDir, TerrainPreviewSettings settings, int iteration, string stem )
	{
		var snapshot = new
		{
			iteration,
			mode = settings.PreviewMode.ToString(),
			file = $"{stem}.png",
			backend = TerrainPreviewBackendRegistry.Active.GetType().Name,
			settings.WorldDiameterMeters,
			settings.PreviewResolution,
			settings.WorldSeed,
			settings.RandomizeSeedOnGenerate,
			settings.RetrySeedsUntilSolved,
			settings.ValleyAutoMaxSeedAttempts,
			settings.EnableContinentalLayer,
			settings.EnableHillLayer,
			settings.EnableValleyLayer,
			settings.EnableHeightCurveLayer,
			settings.EnableMountainLayer,
			settings.ContinentalFrequency,
			settings.ContinentalWeight,
			settings.HillFrequency,
			settings.HillWeight,
			settings.ValleyFrequency,
			settings.ValleyWeight,
			settings.EnableValleyOceanAutoWeight,
			settings.EnableValleySpawnAutoFrequency,
			settings.EnableValleyAutoExhaustiveSearch,
			settings.RejectSeedOnAutoFailure,
			settings.ValleyAutoSearchTimeoutSeconds,
			settings.ValleyAutoMaxIterationsPerSeed,
			settings.ValleyAutoTunePreviewResolution,
			settings.ValleyOceanWeightStep,
			settings.ValleyOceanAutoMinInteriorFraction01,
			settings.ValleyOceanAutoMaxTotalFraction01,
			settings.ValleyOceanAbsoluteMaxTotalFraction01,
			settings.ValleyOceanMaxExteriorFraction01,
			settings.EnableInteriorWaterLayer,
			settings.InteriorWaterFrequency,
			settings.InteriorWaterWeight,
			settings.InteriorWaterAutoStep,
			settings.InteriorWaterCenterInfluence01,
			settings.InteriorWaterFullInfluenceRadius01,
			settings.InteriorWaterFalloffPower,
			settings.InteriorWaterEdgeFade01,
			settings.ValleySpawnLandRadiusMeters,
			settings.ValleySpawnMinLandFraction01,
			settings.ValleySpawnAcceptableLandFraction01,
			settings.ValleyAutoFrequencyStep,
			settings.ValleyAutoFrequencyMin,
			settings.ValleyAutoFrequencyMax,
			settings.ValleyNearWaterMaxDistanceMeters,
			settings.ValleyInnerHalfRadius01,
			settings.ValleyInnerHalfMinOceanFraction01,
			settings.MountainThreshold,
			settings.MountainFrequency,
			settings.MountainInnerRadius01,
			settings.MountainOuterRadius01,
			settings.MountainBandFade01,
			settings.MountainInnerRadiusMeters,
			settings.MountainOuterRadiusMeters,
			settings.MountainBandFadeMeters,
			settings.MountainFalloffRimPower,
			settings.MountainPeakBoost,
			settings.MountainMinPeakHeight01,
			settings.MountainPeakVariationFrequency,
			settings.MountainFoothillSpread,
			settings.MountainFoothillBoost,
			settings.HeightCurvePower,
			settings.SeaLevelHeight01,
			settings.TargetTotalOceanFraction01,
			settings.TargetInteriorOceanFraction01,
			settings.InteriorZoneRadius01,
		};

		var json = JsonSerializer.Serialize( snapshot, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( Path.Combine( bundleDir, "preview_settings.json" ), json );
	}

	static string GetPreviewRootDirectory()
	{
		var projectRoot = FindProjectRoot();
		if ( string.IsNullOrEmpty( projectRoot ) )
			throw new InvalidOperationException( $"Could not locate {ProjectFileName} — open the project from its root folder." );

		return Path.Combine( projectRoot, "Assets", "terrain", "preview" );
	}

	static string FindProjectRoot()
	{
		if ( Project.Current?.RootDirectory is { Exists: true } root )
			return root.FullName;

		var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir.FullName, ProjectFileName ) ) )
				return dir.FullName;

			dir = dir.Parent;
		}

		return null;
	}

	static int ReadIteration( string previewRoot )
	{
		var path = Path.Combine( previewRoot, IterationFileName );
		if ( !File.Exists( path ) )
			return 0;

		var text = File.ReadAllText( path ).Trim();
		return int.TryParse( text, out var value ) ? value : 0;
	}

	static void WriteIteration( string previewRoot, int iteration )
	{
		var path = Path.Combine( previewRoot, IterationFileName );
		File.WriteAllText( path, iteration.ToString() );
	}
}
