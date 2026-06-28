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
	readonly int _resolution;

	int _nextRow;
	bool _colorsFinalized;

	public int Resolution => _resolution;
	public int RowsCompleted => _nextRow;
	public bool IsComplete => _nextRow >= _resolution;
	public float Progress01 => _resolution > 0 ? Math.Clamp( (float)_nextRow / _resolution, 0f, 1f ) : 1f;

	TerrainWorldPreviewJob(
		TerrainPreviewSettings worldSettings,
		ITerrainPreviewBackend backend,
		TerrainBiomeMapPreviewOptions preview,
		int resolution,
		Color[] colors,
		TerrainPreviewBiomeId[] biomeMap,
		float[] shadeMap,
		float[] heightMap,
		bool[] insideWorld )
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
			new bool[pixelCount] );
	}

	public int Step( int maxRows )
	{
		if ( IsComplete || maxRows <= 0 )
			return 0;

		var startRow = _nextRow;
		var endRow = Math.Min( _nextRow + maxRows, _resolution );
		FillRows( startRow, endRow );
		_nextRow = endRow;

		if ( IsComplete )
			FinalizeColors();

		return endRow - startRow;
	}

	public Texture FinishTexture()
		=> FinishBitmap().ToTexture( false );

	public Bitmap FinishBitmap()
	{
		if ( !IsComplete )
			Step( _resolution );

		FinalizeColors();

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

				var resolved = TerrainPreviewBiomeResolver.Resolve( _worldSettings, sample, wx, wy );
				_biomeMap[idx] = resolved.BiomeId;
				_shadeMap[idx] = resolved.Shade01;
				_heightMap[idx] = sample.Height01;
				_colors[idx] = TerrainPreviewBiomeColors.ColorizeOverlay(
					_worldSettings,
					resolved.BiomeId,
					resolved.Shade01,
					sample.Height01 );
			}
		}
	}

	void FinalizeColors()
	{
		if ( _colorsFinalized )
			return;

		TerrainBiomeMapPreviewRaster.ApplyPreviewSpeckFilter(
			_biomeMap,
			_resolution,
			_worldSettings.WorldDiameterMeters,
			_preview );

		for ( var i = 0; i < _colors.Length; i++ )
		{
			if ( !_insideWorld[i] )
			{
				_colors[i] = Color.Black;
				continue;
			}

			_colors[i] = TerrainPreviewBiomeColors.ColorizeOverlay(
				_worldSettings,
				_biomeMap[i],
				_shadeMap[i],
				_heightMap[i] );
		}

		_colorsFinalized = true;
	}
}
