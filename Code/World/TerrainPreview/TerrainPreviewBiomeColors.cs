namespace Survival;

public static class TerrainPreviewBiomeColors
{
	public static Color ColorizeOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewBiomeId biomeId,
		float shade01,
		float height01 )
	{
		var heightGray = Grayscale( height01 );
		var biomeColor = PaletteColor( biomeId, shade01 );
		var overlay = Math.Clamp( settings.BiomeOverlayStrength01, 0f, 1f );
		return Color.Lerp( heightGray, biomeColor, overlay );
	}

	public static Color SampleBiomeOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !sample.IsInsideWorld )
			return Color.Black;

		var resolved = TerrainPreviewBiomeResolver.Resolve( settings, sample, worldXMeters, worldYMeters );
		return ColorizeOverlay( settings, resolved.BiomeId, resolved.Shade01, sample.Height01 );
	}

	public static Color PaletteColor( TerrainPreviewBiomeId biomeId, float shade01 )
	{
		shade01 = Math.Clamp( shade01, 0.35f, 1f );
		var baseColor = biomeId switch
		{
			TerrainPreviewBiomeId.Water => new Color( 0.12f, 0.38f, 0.92f ),
			TerrainPreviewBiomeId.CloverHills => new Color( 0.18f, 0.72f, 0.22f ),
			TerrainPreviewBiomeId.RedwoodForest => new Color( 0.45f, 0.12f, 0.18f ),
			TerrainPreviewBiomeId.AmberDunes => new Color( 0.92f, 0.62f, 0.18f ),
			TerrainPreviewBiomeId.Mountain => new Color( 0.92f, 0.92f, 0.94f ),
			_ => new Color( 0.5f, 0.5f, 0.5f ),
		};

		if ( biomeId == TerrainPreviewBiomeId.Water )
			return baseColor;

		return new Color(
			baseColor.r * shade01,
			baseColor.g * shade01,
			baseColor.b * shade01 );
	}

	static Color Grayscale( float value )
	{
		value = Math.Clamp( value, 0f, 1f );
		return new Color( value, value, value );
	}
}
