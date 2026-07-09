namespace Survival;

public static class TerrainPreviewBiomeColors
{
	public static Color ColorizeOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewBiomeId biomeId,
		float shade01,
		float height01 )
	{
		if ( biomeId == TerrainPreviewBiomeId.Blackwater )
			return Color.Black;

		var heightGray = Grayscale( height01 );
		var biomeColor = PaletteColor( biomeId, shade01 );
		var overlay = Math.Clamp( settings.BiomeOverlayStrength01, 0f, 1f );
		return Color.Lerp( heightGray, biomeColor, overlay );
	}

	/// <summary>Shared land tint for preview PNG and streamed chunk meshes — hard water, soft land-biome edges only.</summary>
	public static Color UnifiedDisplayColor(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
		=> SampleBiomeOverlay( settings, sample, worldXMeters, worldYMeters );

	public static Color SampleBiomeOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		var resolved = TerrainPreviewBiomeResolver.Resolve( settings, sample, worldXMeters, worldYMeters );
		return SampleBiomeOverlay( settings, sample, worldXMeters, worldYMeters, resolved );
	}

	public static Color SampleBiomeOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters,
		TerrainPreviewBiomeResolver.Result resolved )
	{
		if ( !sample.IsInsideWorld )
			return Color.Black;

		if ( resolved.BiomeId is TerrainPreviewBiomeId.Water
			or TerrainPreviewBiomeId.Blackwater )
			return ColorizeOverlay( settings, resolved.BiomeId, resolved.Shade01, sample.Height01 );

		if ( resolved.BiomeId == TerrainPreviewBiomeId.AzureCoast )
			return PaletteColor( TerrainPreviewBiomeId.AzureCoast, 1f );

		var dominant = ColorizeOverlay( settings, resolved.BiomeId, resolved.Shade01, sample.Height01 );

		if ( !settings.UseContinuousBiomePlacementAtSample )
			return dominant;

		return SoftenBiomeEdgeColor( settings, sample, worldXMeters, worldYMeters, dominant );
	}

	static Color SoftenBiomeEdgeColor(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters,
		Color dominantColor )
	{
		var edgeStrength = Math.Clamp( settings.BiomeEdgeColorBlend01, 0f, 1f );
		if ( edgeStrength <= 0.0001f )
			return dominantColor;

		var edgeStart = Math.Clamp( settings.BiomeEdgeBlendStart01, 0.05f, 0.5f );
		var transition = sample.BiomeTransition01;
		if ( transition <= edgeStart )
			return dominantColor;

		var weights = TerrainPreviewBiomeResolver.SampleLandBiomeWeights(
			settings, sample, worldXMeters, worldYMeters );
		var total = weights.Total;
		if ( total <= 0.0001f )
			return dominantColor;

		var span = Math.Max( 0.05f, 1f - edgeStart );
		var edgeT = Math.Clamp( (transition - edgeStart) / span, 0f, 1f );
		edgeT = edgeT * edgeT * (3f - (2f * edgeT));
		var edgeMix = edgeT * edgeStrength;

		var softened = WeightedPaletteColor( weights, total );
		var overlay = Math.Clamp( settings.BiomeOverlayStrength01, 0f, 1f );
		var softenedOverlay = Color.Lerp( Grayscale( sample.Height01 ), softened, overlay );

		return Color.Lerp( dominantColor, softenedOverlay, edgeMix );
	}

	static Color WeightedPaletteColor(
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float total )
	{
		var clover = PaletteColor( TerrainPreviewBiomeId.CloverHills, 1f );
		var redwood = PaletteColor( TerrainPreviewBiomeId.RedwoodForest, 1f );
		var amber = PaletteColor( TerrainPreviewBiomeId.AmberDunes, 1f );
		var mountain = PaletteColor( TerrainPreviewBiomeId.Mountain, 1f );

		return new Color(
			((weights.Clover * clover.r) + (weights.Redwood * redwood.r) + (weights.Amber * amber.r) + (weights.Mountain * mountain.r)) / total,
			((weights.Clover * clover.g) + (weights.Redwood * redwood.g) + (weights.Amber * amber.g) + (weights.Mountain * mountain.g)) / total,
			((weights.Clover * clover.b) + (weights.Redwood * redwood.b) + (weights.Amber * amber.b) + (weights.Mountain * mountain.b)) / total );
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
			TerrainPreviewBiomeId.AzureCoast => new Color( 0.12f, 0.68f, 0.62f ),
			TerrainPreviewBiomeId.Blackwater => Color.Black,
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
