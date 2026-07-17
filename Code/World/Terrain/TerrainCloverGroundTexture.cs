namespace Survival;

/// <summary>
/// Temporary ground detail for Clover Hills: tiles <c>bush.png</c> into chunk vertex colors.
/// Keeps the existing vertex-color material; no second material path.
/// </summary>
public static class TerrainCloverGroundTexture
{
	const string TexturePath = "materials/environment/bush.png";

	static Color[] _pixels;
	static int _width;
	static int _height;
	static bool _loadAttempted;
	static bool _loggedMissing;

	/// <summary>
	/// Multiplies Clover vertex colors by a tiled sample of bush.png (world meters).
	/// </summary>
	public static void ApplyToChunkColors(
		TerrainPreviewSettings settings,
		Color[] colors,
		TerrainPreviewBiomeId[] biomeMap,
		float worldMinX,
		float worldMinY,
		float metersPerCell,
		int verticesPerSide )
	{
		if ( colors is null || biomeMap is null )
			return;

		var strength = Math.Clamp( settings.BiomeCloverGroundTextureStrength01, 0f, 1f );
		if ( strength <= 0.001f )
			return;

		if ( !EnsurePixels() )
			return;

		var tile = Math.Max( 2f, settings.BiomeCloverGroundTextureTileMeters );
		var count = verticesPerSide * verticesPerSide;
		if ( colors.Length < count || biomeMap.Length < count )
			return;

		for ( var iy = 0; iy < verticesPerSide; iy++ )
		{
			for ( var ix = 0; ix < verticesPerSide; ix++ )
			{
				var idx = (iy * verticesPerSide) + ix;
				if ( biomeMap[idx] != TerrainPreviewBiomeId.CloverHills )
					continue;

				var wx = worldMinX + (ix * metersPerCell);
				var wy = worldMinY + (iy * metersPerCell);
				var tex = SampleTiled( wx, wy, tile );
				// Keep biome tint; bush.png adds scribbly grass detail.
				var detailed = new Color(
					colors[idx].r * tex.r,
					colors[idx].g * tex.g,
					colors[idx].b * tex.b,
					colors[idx].a );
				colors[idx] = Color.Lerp( colors[idx], detailed, strength );
			}
		}
	}

	static bool EnsurePixels()
	{
		if ( _pixels is not null )
			return true;

		if ( _loadAttempted )
			return false;

		_loadAttempted = true;

		var texture = Texture.Load( FileSystem.Mounted, TexturePath );
		if ( texture is null || !texture.IsValid() )
			texture = Texture.LoadFromFileSystem( TexturePath, FileSystem.Mounted, warnOnMissing: false );

		if ( texture is null || !texture.IsValid() )
		{
			if ( !_loggedMissing )
			{
				_loggedMissing = true;
				Log.Warning( $"[Terrain] Clover ground texture missing: '{TexturePath}'." );
			}

			return false;
		}

		var bitmap = texture.GetBitmap( 0 );
		if ( bitmap is null || bitmap.Width < 1 || bitmap.Height < 1 )
		{
			if ( !_loggedMissing )
			{
				_loggedMissing = true;
				Log.Warning( $"[Terrain] Clover ground texture '{TexturePath}' has no readable bitmap." );
			}

			return false;
		}

		_width = bitmap.Width;
		_height = bitmap.Height;
		_pixels = bitmap.GetPixels();
		if ( _pixels is null || _pixels.Length < _width * _height )
		{
			_pixels = null;
			return false;
		}

		return true;
	}

	static Color SampleTiled( float worldXMeters, float worldYMeters, float tileMeters )
	{
		var u = worldXMeters / tileMeters;
		var v = worldYMeters / tileMeters;
		u -= MathF.Floor( u );
		v -= MathF.Floor( v );

		var x = Math.Clamp( (int)(u * _width), 0, _width - 1 );
		var y = Math.Clamp( (int)(v * _height), 0, _height - 1 );
		return _pixels[(y * _width) + x];
	}
}
