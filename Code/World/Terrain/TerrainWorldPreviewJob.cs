namespace Survival;



/// <summary>Time-sliced PNG biome map raster — display export only, not streamed terrain.</summary>

public sealed class TerrainWorldPreviewJob

{

	readonly TerrainPreviewSettings _worldSettings;

	readonly ITerrainPreviewBackend _backend;

	readonly TerrainBiomeMapPreviewOptions _preview;

	readonly Color[] _colors;

	readonly TerrainPreviewBiomeId[] _biomeMap;

	readonly float[] _shadeMap;

	readonly float[] _heightMap;

	readonly bool[] _insideWorld;

	readonly bool[] _isWater;

	readonly int _resolution;



	int _nextRow;

	int _finalizeNextRow;

	bool _speckFilterApplied;

	bool _edgeJitterApplied;

	bool _colorsFinalized;



	public int Resolution => _resolution;

	public int RowsCompleted => _colorsFinalized ? _resolution : Math.Min( _nextRow, _finalizeNextRow );

	public bool IsComplete => _colorsFinalized;

	public float Progress01

	{

		get

		{

			if ( _resolution <= 0 )

				return 1f;



			if ( !_speckFilterApplied )

				return Math.Clamp( (float)_nextRow / _resolution, 0f, 0.85f ) * 0.85f;



			if ( !_colorsFinalized )

				return 0.85f + (Math.Clamp( (float)_finalizeNextRow / _resolution, 0f, 1f ) * 0.14f);



			return 1f;

		}

	}



	TerrainWorldPreviewJob(

		TerrainPreviewSettings worldSettings,

		ITerrainPreviewBackend backend,

		TerrainBiomeMapPreviewOptions preview,

		int resolution,

		Color[] colors,

		TerrainPreviewBiomeId[] biomeMap,

		float[] shadeMap,

		float[] heightMap,

		bool[] insideWorld,

		bool[] isWater )

	{

		_worldSettings = worldSettings;

		_backend = backend;

		_preview = preview;

		_resolution = resolution;

		_colors = colors;

		_biomeMap = biomeMap;

		_shadeMap = shadeMap;

		_heightMap = heightMap;

		_insideWorld = insideWorld;

		_isWater = isWater;

	}



	public static TerrainWorldPreviewJob Create(

		TerrainPreviewSettings worldSettings,

		ITerrainPreviewBackend backend,

		TerrainBiomeMapPreviewOptions preview,

		int resolution )

	{

		resolution = Math.Clamp( resolution, 64, 32768 );

		var pixelCount = resolution * resolution;



		return new TerrainWorldPreviewJob(

			worldSettings,

			backend,

			preview ?? new TerrainBiomeMapPreviewOptions(),

			resolution,

			new Color[pixelCount],

			new TerrainPreviewBiomeId[pixelCount],

			new float[pixelCount],

			new float[pixelCount],

			new bool[pixelCount],

			new bool[pixelCount] );

	}



	public int Step( int maxRows )

	{

		if ( IsComplete || maxRows <= 0 )

			return 0;



		if ( !_speckFilterApplied )

		{

			var startRow = _nextRow;

			var endRow = Math.Min( _nextRow + maxRows, _resolution );

			FillRows( startRow, endRow );

			_nextRow = endRow;



			if ( _nextRow < _resolution )

				return endRow - startRow;



			TerrainBiomeMapPreviewRaster.ApplyPreviewSpeckFilter(

				_biomeMap,

				_resolution,

				_worldSettings.WorldDiameterMeters,

				_preview );

			_speckFilterApplied = true;

			return endRow - startRow;

		}



		var finalizeStart = _finalizeNextRow;

		var finalizeEnd = Math.Min( _finalizeNextRow + maxRows, _resolution );

		FinalizeColorRows( finalizeStart, finalizeEnd );

		_finalizeNextRow = finalizeEnd;



		if ( _finalizeNextRow < _resolution )

			return finalizeEnd - finalizeStart;



		if ( !_edgeJitterApplied )

		{

			TerrainBiomeEdgeDisplay.ApplyShoreAndBiomeEdgeJitter(

				_worldSettings,

				_resolution,

				_resolution,

				_insideWorld,

				_isWater,

				_biomeMap,

				_colors );

			_edgeJitterApplied = true;

		}



		_colorsFinalized = true;

		return finalizeEnd - finalizeStart;

	}



	public Texture FinishTexture()

		=> FinishBitmap().ToTexture( false );



	public Bitmap FinishBitmap()

	{

		while ( !IsComplete )

			Step( _resolution );



		var bitmap = new Bitmap( _resolution, _resolution );

		bitmap.SetPixels( _colors );

		return bitmap;

	}



	void FillRows( int startRow, int endRow )

	{

		var res = _resolution;

		var radius = _worldSettings.WorldRadiusMeters;

		var diameter = _worldSettings.WorldDiameterMeters;



		for ( var py = startRow; py < endRow; py++ )

		{

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

				var sample = _backend.Sample( _worldSettings, wx, wy );

				_insideWorld[idx] = sample.IsInsideWorld;



				if ( !sample.IsInsideWorld )

				{

					_biomeMap[idx] = TerrainPreviewBiomeId.None;

					_shadeMap[idx] = 1f;

					_heightMap[idx] = 0f;

					_colors[idx] = Color.Black;

					continue;

				}



				var landResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( _worldSettings, sample, wx, wy );

				_biomeMap[idx] = landResolved.BiomeId;

				_shadeMap[idx] = landResolved.Shade01;

				_heightMap[idx] = sample.Height01;

				_colors[idx] = TerrainPreviewBiomeColors.ColorizeOverlay(

					_worldSettings,

					landResolved.BiomeId,

					landResolved.Shade01,

					sample.Height01 );

			}

		}

	}



	void FinalizeColorRows( int startRow, int endRow )

	{

		var res = _resolution;

		var radius = _worldSettings.WorldRadiusMeters;

		var diameter = _worldSettings.WorldDiameterMeters;



		for ( var py = startRow; py < endRow; py++ )

		{

			for ( var px = 0; px < res; px++ )

			{

				var idx = (py * res) + px;

				if ( !_insideWorld[idx] )

				{

					_colors[idx] = Color.Black;

					_isWater[idx] = false;

					continue;

				}



				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(

					px, py, res, radius, diameter, out var wx, out var wy );

				var sample = _backend.Sample( _worldSettings, wx, wy );

				var displayWater = TerrainShorelineDisplay.IsDisplayWaterColor( _worldSettings, wx, wy );



				_isWater[idx] = displayWater;

				if ( displayWater )

				{

					_biomeMap[idx] = TerrainPreviewBiomeId.Water;

					_colors[idx] = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );

					continue;

				}



				if ( _biomeMap[idx] == TerrainPreviewBiomeId.Blackwater )

				{

					_colors[idx] = Color.Black;

					continue;

				}



				var landResolved = TerrainPreviewBiomeResolver.ResolveLandOverlay( _worldSettings, sample, wx, wy );

				_biomeMap[idx] = landResolved.BiomeId;

				_colors[idx] = TerrainPreviewBiomeColors.SampleBiomeOverlay(

					_worldSettings, sample, wx, wy, landResolved );

			}

		}

	}

}

