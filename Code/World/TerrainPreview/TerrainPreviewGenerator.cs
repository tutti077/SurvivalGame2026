namespace Survival;

/// <summary>Rasterizes preview layers to CPU color buffers for editor PNG / texture display.</summary>
public static class TerrainPreviewGenerator
{
	public static TerrainPreviewGenerateResult Generate( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
			return default;

		TerrainPreviewMapIterationTracker.NotifyMapRasterized();
		backend ??= TerrainPreviewBackendRegistry.Active;

		var res = settings.ClampedResolution;
		var colors = new Color[res * res];
		var insideWorld = new bool[res * res];
		var ocean = new bool[res * res];
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;

		RasterOcean(
			settings,
			backend,
			res,
			radius,
			diameter,
			insideWorld,
			ocean,
			colors,
			fillColors: true );

		return BuildResult( settings, insideWorld, ocean, colors );
	}

	public static TerrainPreviewWaterCoverageStats MeasureWaterCoverage(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend = null )
	{
		if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
			return default;

		TerrainPreviewMapIterationTracker.NotifyMapRasterized();
		backend ??= TerrainPreviewBackendRegistry.Active;

		var res = TerrainPreviewAutoTuneScope.IsActive
			? TerrainPreviewAutoTuneScope.MeasureResolution( settings )
			: settings.ClampedResolution;
		var insideWorld = new bool[res * res];
		var ocean = new bool[res * res];
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;

		RasterOcean(
			settings,
			backend,
			res,
			radius,
			diameter,
			insideWorld,
			ocean,
			colors: null,
			fillColors: false );

		return BuildWaterCoverage( settings, insideWorld, ocean, res, radius, diameter );
	}

	static TerrainPreviewGenerateResult BuildResult(
		TerrainPreviewSettings settings,
		bool[] insideWorld,
		bool[] ocean,
		Color[] colors )
	{
		var res = TerrainPreviewAutoTuneScope.IsActive
			? TerrainPreviewAutoTuneScope.MeasureResolution( settings )
			: settings.ClampedResolution;
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;

		TerrainPreviewWaterCoverage.ClassifyOceanZones(
			ocean,
			insideWorld,
			res,
			settings,
			radius,
			diameter,
			out var interiorOcean,
			out var exteriorOcean );

		var waterCoverage = TerrainPreviewWaterCoverage.ComputeStats( ocean, interiorOcean, exteriorOcean, insideWorld );
		waterCoverage = waterCoverage.WithLandDiskLakeFraction(
			TerrainPreviewWaterCoverage.MeasureLandDiskLakeFraction( settings ) );
		var metrics = TerrainPreviewGenerationMetrics.Measure( settings );

		if ( settings.PreviewMode == TerrainPreviewMode.Water )
			ApplyWaterZoneColors( colors, ocean, interiorOcean, exteriorOcean, insideWorld );

		if ( settings.ShowPreviewDistanceRings )
		{
			TerrainPreviewDistanceRings.Stamp(
				colors,
				res,
				radius,
				diameter,
				insideWorld,
				settings.PreviewDistanceRingIntervalMeters );
		}

		StampSpawnMarker( colors, res );

		return new TerrainPreviewGenerateResult
		{
			Colors = colors,
			WaterCoverage = waterCoverage,
			Metrics = metrics,
		};
	}

	static TerrainPreviewWaterCoverageStats BuildWaterCoverage(
		TerrainPreviewSettings settings,
		bool[] insideWorld,
		bool[] ocean,
		int res,
		float radius,
		float diameter )
	{
		TerrainPreviewWaterCoverage.ClassifyOceanZones(
			ocean,
			insideWorld,
			res,
			settings,
			radius,
			diameter,
			out var interiorOcean,
			out var exteriorOcean );

		return TerrainPreviewWaterCoverage.ComputeStats( ocean, interiorOcean, exteriorOcean, insideWorld )
			.WithLandDiskLakeFraction( TerrainPreviewWaterCoverage.MeasureLandDiskLakeFraction( settings ) );
	}

	static void RasterOcean(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		bool[] insideWorld,
		bool[] ocean,
		Color[] colors,
		bool fillColors )
	{
		var metersPerPixel = diameter / res;
		TerrainPreviewGenerateProgress.SetStage( "Raster preview" );
		TerrainPreviewLandDiskFields.EnsureReady( settings );

		if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
			return;

		if ( fillColors && settings.PreviewMode == TerrainPreviewMode.Biomes )
		{
			for ( var py = 0; py < res; py++ )
			{
				if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
					return;

				TerrainPreviewGenerateProgress.ReportRaster( py + 1, res );

				for ( var px = 0; px < res; px++ )
				{
					var idx = (py * res) + px;
					TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
						px,
						py,
						res,
						radius,
						diameter,
						out var wx,
						out var wy );
					var sample = backend.Sample( settings, wx, wy );
					insideWorld[idx] = sample.IsInsideWorld;
					ocean[idx] = sample.IsInsideWorld && sample.OceanHeight01 > 0.5f;
				}
			}

			TerrainPreviewLandSpeckFilter.ApplyToOceanMask(
				ocean,
				insideWorld,
				res,
				res,
				metersPerPixel,
				settings );

			TerrainPreviewBiomeMapRaster.FillBiomeColors(
				settings,
				backend,
				res,
				radius,
				diameter,
				insideWorld,
				ocean,
				colors );
			return;
		}

		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				return;

			TerrainPreviewGenerateProgress.ReportRaster( py + 1, res );

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px,
					py,
					res,
					radius,
					diameter,
					out var wx,
					out var wy );

				var sample = backend.Sample( settings, wx, wy );
				insideWorld[idx] = sample.IsInsideWorld;
				ocean[idx] = sample.IsInsideWorld && sample.OceanHeight01 > 0.5f;

				if ( fillColors )
					colors[idx] = SampleToColor( settings, sample, wx, wy );
			}
		}

		TerrainPreviewLandSpeckFilter.ApplyToOceanMask(
			ocean,
			insideWorld,
			res,
			res,
			metersPerPixel,
			settings );

		if ( fillColors && settings.LandSpeckFilterEnabled )
			RefreshColorsAfterLandSpeck( settings, backend, res, radius, diameter, insideWorld, ocean, colors );

		if ( fillColors
			&& settings.MountainSpawnSpeckFilterEnabled
			&& settings.EnableMountainLayer
			&& settings.PreviewMode == TerrainPreviewMode.MountainMask )
		{
			ApplyMountainMaskSpeckFilter( settings, backend, res, radius, diameter, colors );
		}

		if ( fillColors
			&& settings.EnableInteriorWaterLayer
			&& settings.PreviewMode == TerrainPreviewMode.Lakes )
		{
			ApplyLakePreviewMask( settings, backend, res, diameter, colors );
		}
	}

	static void ApplyLakePreviewMask(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float diameter,
		Color[] colors )
	{
		_ = backend;
		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				return;

			TerrainPreviewGenerateProgress.ReportRaster( py + 1, res );

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, settings.WorldRadiusMeters, diameter, out var wx, out var wy );
				var isWater = TerrainPreviewLandDiskFields.IsOpenWater( settings, wx, wy );
				colors[idx] = isWater ? Color.White : Color.Black;
			}
		}
	}

	static void RefreshColorsAfterLandSpeck(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		bool[] insideWorld,
		bool[] ocean,
		Color[] colors )
	{
		if ( settings.PreviewMode is TerrainPreviewMode.Water or TerrainPreviewMode.Biomes )
			return;

		var seaColor = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );
		var seaHeight01 = TerrainPreviewOceanByHeight.MetersToHeight01( settings, settings.SeaLevelMeters );

		for ( var idx = 0; idx < ocean.Length; idx++ )
		{
			if ( !insideWorld[idx] || !ocean[idx] )
				continue;

			colors[idx] = settings.PreviewMode switch
			{
				TerrainPreviewMode.World => Grayscale( seaHeight01 ),
				TerrainPreviewMode.HeightCurve => Grayscale( seaHeight01 ),
				TerrainPreviewMode.BiomeShape => Grayscale( seaHeight01 ),
				_ => seaColor,
			};
		}
	}

	static void ApplyMountainMaskSpeckFilter(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int res,
		float radius,
		float diameter,
		Color[] colors )
	{
		var mask = new bool[res * res];
		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
				return;

			TerrainPreviewGenerateProgress.ReportRaster( py + 1, res );

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var sample = backend.Sample( settings, wx, wy );
				mask[idx] = sample.MountainMask01 >= 0.5f;
			}
		}

		var metersPerPixel = diameter / res;
		TerrainPreviewMountainSpeckFilter.RemoveSmallPatches(
			mask, res, res, metersPerPixel, settings.MountainSpawnMinPatchDiameterMeters );

		for ( var i = 0; i < mask.Length; i++ )
			colors[i] = mask[i] ? Color.White : Color.Black;
	}

	public static Color[] GenerateColors( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
		=> Generate( settings, backend ).Colors;

	public static Bitmap GenerateBitmap( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
		var res = TerrainPreviewAutoTuneScope.IsActive
			? TerrainPreviewAutoTuneScope.MeasureResolution( settings )
			: settings.ClampedResolution;
		var result = Generate( settings, backend );
		var bitmap = new Bitmap( res, res );
		bitmap.SetPixels( result.Colors );
		return bitmap;
	}

	public static string ModeDisplayName( TerrainPreviewMode mode ) => mode switch
	{
		TerrainPreviewMode.World => "World",
		TerrainPreviewMode.Continental => "Continental",
		TerrainPreviewMode.Hills => "Hills",
		TerrainPreviewMode.Valleys => "Valleys",
		TerrainPreviewMode.HeightCurve => "Height Curve",
		TerrainPreviewMode.Water => "Water",
		TerrainPreviewMode.Lakes => "Lakes",
		TerrainPreviewMode.MountainMask => "Mountain Mask",
		TerrainPreviewMode.MountainField => "Mountain Field",
		TerrainPreviewMode.MountainFalloff => "Mountain Falloff",
		TerrainPreviewMode.Biomes => "Biomes",
		TerrainPreviewMode.BiomeShape => "Biome Shape",
		TerrainPreviewMode.BiomeWeights => "Biome Weights",
		TerrainPreviewMode.Slope => "Slope",
		TerrainPreviewMode.BiomeTransition => "Biome Transition",
		_ => mode.ToString(),
	};

	public static string ModeFileStem( TerrainPreviewMode mode ) => mode switch
	{
		TerrainPreviewMode.World => "world",
		TerrainPreviewMode.Continental => "continental",
		TerrainPreviewMode.Hills => "hills",
		TerrainPreviewMode.Valleys => "valleys",
		TerrainPreviewMode.HeightCurve => "height_curve",
		TerrainPreviewMode.Water => "water",
		TerrainPreviewMode.Lakes => "lakes",
		TerrainPreviewMode.MountainMask => "mountain_mask",
		TerrainPreviewMode.MountainField => "mountain_field",
		TerrainPreviewMode.MountainFalloff => "mountain_falloff",
		TerrainPreviewMode.Biomes => "biomes",
		TerrainPreviewMode.BiomeShape => "biome_shape",
		TerrainPreviewMode.BiomeWeights => "biome_weights",
		TerrainPreviewMode.Slope => "slope",
		TerrainPreviewMode.BiomeTransition => "biome_transition",
		_ => "preview",
	};

	public static TerrainPreviewMode ModeForTabName( string tabName ) => tabName switch
	{
		"Continental" => TerrainPreviewMode.Continental,
		"Hills" => TerrainPreviewMode.Hills,
		"Valleys" => TerrainPreviewMode.Valleys,
		"Height Curve" => TerrainPreviewMode.HeightCurve,
		"Water" => TerrainPreviewMode.Water,
		"Lakes" => TerrainPreviewMode.Lakes,
		"Mountain Mask" => TerrainPreviewMode.MountainMask,
		"Mountain Field" => TerrainPreviewMode.MountainField,
		"Mountain Falloff" => TerrainPreviewMode.MountainFalloff,
		"Biomes" => TerrainPreviewMode.Biomes,
		"Biome Shape" => TerrainPreviewMode.BiomeShape,
		"Biome Weights" => TerrainPreviewMode.BiomeWeights,
		"Slope" => TerrainPreviewMode.Slope,
		"Biome Transition" => TerrainPreviewMode.BiomeTransition,
		_ => TerrainPreviewMode.World,
	};

	static Color SampleToColor( TerrainPreviewSettings settings, TerrainPreviewSample sample, float worldXMeters, float worldYMeters )
	{
		if ( !sample.IsInsideWorld )
			return Color.Black;

		if ( settings.PreviewMode == TerrainPreviewMode.Biomes )
			return TerrainPreviewBiomeColors.SampleBiomeOverlay( settings, sample, worldXMeters, worldYMeters );

		return settings.PreviewMode switch
		{
			TerrainPreviewMode.World => Grayscale( sample.Height01 ),
			TerrainPreviewMode.Continental => Grayscale( sample.ContinentalNoise01 ),
			TerrainPreviewMode.Hills => Grayscale( sample.HillsNoise01 ),
			TerrainPreviewMode.Valleys => Grayscale( sample.ValleysNoise01 ),
			TerrainPreviewMode.HeightCurve => Grayscale( sample.HeightAfterCurve01 ),
			TerrainPreviewMode.Water => Color.Black,
			TerrainPreviewMode.Lakes => Grayscale( sample.LakeMask01 ),
			TerrainPreviewMode.MountainMask => sample.MountainMask01 >= 0.5f ? Color.White : Color.Black,
			TerrainPreviewMode.MountainField => Grayscale( sample.MountainField01 ),
			TerrainPreviewMode.MountainFalloff => Grayscale( sample.MountainFalloff01 ),
			TerrainPreviewMode.BiomeShape => Grayscale( sample.HeightAfterBiomeShape01 ),
			TerrainPreviewMode.BiomeWeights => BiomeWeightColor( sample.LandWeights ),
			TerrainPreviewMode.Slope => SlopeColor( sample.MountainSlopeDegrees ),
			TerrainPreviewMode.BiomeTransition => Grayscale( sample.BiomeTransition01 ),
			_ => Color.Black,
		};
	}

	static Color BiomeWeightColor( TerrainPreviewBiomeResolver.LandBiomeWeights weights )
	{
		var total = weights.Total;
		if ( total <= 0.0001f )
			return Color.Black;

		var r = weights.Clover / total;
		var g = weights.Redwood / total;
		var b = weights.Amber / total;
		var gray = weights.Mountain / total;
		return new Color(
			Math.Clamp( r + (gray * 0.35f), 0f, 1f ),
			Math.Clamp( g + (gray * 0.35f), 0f, 1f ),
			Math.Clamp( b + (gray * 0.55f), 0f, 1f ) );
	}

	static Color SlopeColor( float slopeDegrees )
	{
		var t = Math.Clamp( slopeDegrees / 35f, 0f, 1f );
		return new Color( t, 0.15f, 1f - t );
	}

	static void ApplyWaterZoneColors(
		Color[] colors,
		bool[] ocean,
		bool[] interiorOcean,
		bool[] exteriorOcean,
		bool[] insideWorld )
	{
		for ( var i = 0; i < colors.Length; i++ )
		{
			if ( !insideWorld[i] )
			{
				colors[i] = Color.Black;
				continue;
			}

			if ( !ocean[i] )
			{
				colors[i] = new Color( 0.72f, 0.72f, 0.72f );
				continue;
			}

			colors[i] = interiorOcean[i]
				? new Color( 0.95f, 0.95f, 1f )
				: new Color( 0.08f, 0.08f, 0.1f );
		}
	}

	static Color Grayscale( float value )
	{
		value = Math.Clamp( value, 0f, 1f );
		return new Color( value, value, value );
	}

	static void StampSpawnMarker( Color[] colors, int res )
	{
		var cx = (res - 1) / 2;
		var cy = (res - 1) / 2;
		var red = new Color( 1f, 0f, 0f );

		for ( var dy = -1; dy <= 1; dy++ )
		{
			for ( var dx = -1; dx <= 1; dx++ )
			{
				if ( dx * dx + dy * dy > 2 )
					continue;

				var x = cx + dx;
				var y = cy + dy;
				if ( x < 0 || x >= res || y < 0 || y >= res )
					continue;

				colors[(y * res) + x] = red;
			}
		}
	}
}
