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
		TerrainPreviewWaterCoverageStats waterCoverage = default,
		TerrainPreviewGenerationMetrics metrics = default )
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
		WriteGenerationMetricsSnapshot( previewRoot, bundleDir, bundleName, iteration, settings, metrics );

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
			waterOnLandFraction = stats.LandDiskLakeFraction01,
			targetLakeCoverageOnLand = settings.TargetLakeCoverageOnLand01,
			oceanFractionWholeMap = stats.OceanFraction01,
			interiorOceanFraction = stats.InteriorOceanFraction01,
			exteriorOceanFraction = stats.ExteriorOceanFraction01,
			lakeCoverageOnLand = TerrainPreviewLandDiskFields.GetLakeCoverageOnLand01( settings ),
			lakeOffsetXMeters = settings.LakeOffsetXMeters,
			lakeOffsetYMeters = settings.LakeOffsetYMeters,
			lakeMacroFrequency = settings.LakeMacroFrequency,
			lakeMediumFrequency = settings.LakeMediumFrequency,
		};

		var json = JsonSerializer.Serialize( snapshot, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( Path.Combine( bundleDir, "water_coverage.json" ), json );
	}

	static void WriteGenerationMetricsSnapshot(
		string previewRoot,
		string bundleDir,
		string bundleName,
		int iteration,
		TerrainPreviewSettings settings,
		TerrainPreviewGenerationMetrics metrics )
	{
		var snapshot = new
		{
			iteration,
			seed = settings.WorldSeed,
			bundle = bundleName,
			previewMode = settings.PreviewMode.ToString(),
			waterOnLandFraction = metrics.WaterOnLandFraction01,
			lakePatchCount = metrics.LakePatchCount,
			medianLakeDiameterMeters = metrics.MedianLakeDiameterMeters,
			meanLakeDiameterMeters = metrics.MeanLakeDiameterMeters,
			lakeArchipelagoScore = metrics.LakeArchipelagoScore,
			mountainLandFraction = metrics.MountainLandFraction01,
			nearestShowcaseWaterMeters = TerrainPreviewSpawnLandCheck.MeasureNearestOpenWaterMeters(
				settings, settings.LakeSpawnShowcaseWaterRadiusMeters ),
			showcaseWaterRadiusMeters = settings.LakeSpawnShowcaseWaterRadiusMeters,
			lakesLookCohesive = metrics.LakesLookCohesive,
			mountainsLookReasonable = metrics.MountainsLookReasonable,
			targets = new
			{
				lakePatchCountMax = TerrainPreviewGenerationMetrics.TargetLakePatchCountMax,
				medianLakeDiameterMetersMin = TerrainPreviewGenerationMetrics.TargetMedianLakeDiameterMetersMin,
				lakeArchipelagoScoreMax = TerrainPreviewGenerationMetrics.TargetLakeArchipelagoScoreMax,
				mountainLandFractionMax = TerrainPreviewGenerationMetrics.TargetMountainLandFractionMax,
			},
		};

		var json = JsonSerializer.Serialize( snapshot, new JsonSerializerOptions { WriteIndented = true } );
		File.WriteAllText( Path.Combine( bundleDir, "generation_metrics.json" ), json );

		var latest = new
		{
			bundle = bundleName,
			iteration,
			seed = settings.WorldSeed,
			png = $"{TerrainPreviewGenerator.ModeFileStem( settings.PreviewMode )}.png",
			metricsFile = "generation_metrics.json",
		};
		File.WriteAllText(
			Path.Combine( previewRoot, ".latest_preview.json" ),
			JsonSerializer.Serialize( latest, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	static void WriteSettingsSnapshot( string bundleDir, TerrainPreviewSettings settings, int iteration, string stem )
	{
		var snapshot = new
		{
			iteration,
			mode = settings.PreviewMode.ToString(),
			file = $"{stem}.png",
			backend = TerrainPreviewBackendRegistry.Active.GetType().Name,
			generation = settings.CloneForGenerate( false ),
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
