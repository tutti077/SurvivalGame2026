namespace Survival;

/// <summary>Time-sliced biome map raster — one <see cref="Color"/> buffer, no speck pass.</summary>
public sealed class TerrainWorldPreviewJob
{
	readonly TerrainPreviewSettings _settings;
	readonly ITerrainPreviewBackend _backend;
	readonly Color[] _colors;
	readonly int _resolution;

	int _nextRow;

	public int Resolution => _resolution;
	public int RowsCompleted => _nextRow;
	public bool IsComplete => _nextRow >= _resolution;
	public float Progress01 => _resolution > 0 ? Math.Clamp( (float)_nextRow / _resolution, 0f, 1f ) : 1f;

	TerrainWorldPreviewJob(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int resolution,
		Color[] colors )
	{
		_settings = settings;
		_backend = backend;
		_resolution = resolution;
		_colors = colors;
	}

	public static TerrainWorldPreviewJob Create(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int resolution )
	{
		resolution = Math.Clamp( resolution, 64, 32768 );
		var colors = new Color[resolution * resolution];
		return new TerrainWorldPreviewJob( settings, backend, resolution, colors );
	}

	/// <summary>Raster up to <paramref name="maxRows"/> scanlines; returns rows completed.</summary>
	public int Step( int maxRows )
	{
		if ( IsComplete || maxRows <= 0 )
			return 0;

		var startRow = _nextRow;
		var endRow = Math.Min( _nextRow + maxRows, _resolution );
		FillRows( startRow, endRow );
		_nextRow = endRow;
		return endRow - startRow;
	}

	public Texture FinishTexture()
	{
		return FinishBitmap().ToTexture( false );
	}

	public Bitmap FinishBitmap()
	{
		if ( !IsComplete )
			Step( _resolution );

		var bitmap = new Bitmap( _resolution, _resolution );
		bitmap.SetPixels( _colors );
		return bitmap;
	}

	void FillRows( int startRow, int endRow )
	{
		var res = _resolution;
		var radius = _settings.WorldRadiusMeters;
		var diameter = _settings.WorldDiameterMeters;

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
				var sample = _backend.Sample( _settings, wx, wy );

				if ( !sample.IsInsideWorld )
				{
					_colors[idx] = Color.Black;
					continue;
				}

				var resolved = TerrainPreviewBiomeResolver.Resolve( _settings, sample, wx, wy );
				_colors[idx] = TerrainPreviewBiomeColors.ColorizeOverlay(
					_settings,
					resolved.BiomeId,
					resolved.Shade01,
					sample.Height01 );
			}
		}
	}
}
