namespace Survival;

/// <summary>Rasterizes preview layers to CPU color buffers for editor PNG / texture display.</summary>
public static class TerrainPreviewGenerator
{
	public static TerrainPreviewGenerateResult Generate( TerrainPreviewSettings settings, ITerrainPreviewBackend backend = null )
	{
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

		return TerrainPreviewWaterCoverage.ComputeStats( ocean, interiorOcean, exteriorOcean, insideWorld );
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
		if ( fillColors && settings.PreviewMode == TerrainPreviewMode.Biomes )
		{
			for ( var py = 0; py < res; py++ )
			{
				for ( var px = 0; px < res; px++ )
				{
					var idx = (py * res) + px;
					var wx = (px + 0.5f) / res * diameter - radius;
					var wy = (py + 0.5f) / res * diameter - radius;
					var sample = backend.Sample( settings, wx, wy );
					insideWorld[idx] = sample.IsInsideWorld;
					ocean[idx] = sample.IsInsideWorld && sample.OceanHeight01 > 0.5f;
				}
			}

			TerrainPreviewBiomeMapRaster.FillBiomeColors(
				settings,
				backend,
				res,
				radius,
				diameter,
				insideWorld,
				colors );
			return;
		}

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				var wx = (px + 0.5f) / res * diameter - radius;
				var wy = (py + 0.5f) / res * diameter - radius;

				var sample = backend.Sample( settings, wx, wy );
				insideWorld[idx] = sample.IsInsideWorld;
				ocean[idx] = sample.IsInsideWorld && sample.OceanHeight01 > 0.5f;

				if ( fillColors )
					colors[idx] = SampleToColor( settings, sample, wx, wy );
			}
		}
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
		TerrainPreviewMode.MountainMask => "Mountain Mask",
		TerrainPreviewMode.MountainFalloff => "Mountain Falloff",
		TerrainPreviewMode.Biomes => "Biomes",
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
		TerrainPreviewMode.MountainMask => "mountain_mask",
		TerrainPreviewMode.MountainFalloff => "mountain_falloff",
		TerrainPreviewMode.Biomes => "biomes",
		_ => "preview",
	};

	public static TerrainPreviewMode ModeForTabName( string tabName ) => tabName switch
	{
		"Continental" => TerrainPreviewMode.Continental,
		"Hills" => TerrainPreviewMode.Hills,
		"Valleys" => TerrainPreviewMode.Valleys,
		"Height Curve" => TerrainPreviewMode.HeightCurve,
		"Water" => TerrainPreviewMode.Water,
		"Mountain Mask" => TerrainPreviewMode.MountainMask,
		"Mountain Falloff" => TerrainPreviewMode.MountainFalloff,
		"Biomes" => TerrainPreviewMode.Biomes,
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
			TerrainPreviewMode.MountainMask => Grayscale( sample.MountainMask01 ),
			TerrainPreviewMode.MountainFalloff => Grayscale( sample.MountainFalloff01 ),
			_ => Color.Black,
		};
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
