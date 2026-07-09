namespace Survival;

/// <summary>
/// Teal shore band — world-meter inland distance from display water with hard biome edges
/// and a shallow (~15°) taper only at true band tips (not bay centers).
/// Callers must invoke <see cref="TerrainPreviewLandDiskFields.EnsureReady"/> before hot-path sampling.
/// </summary>
static class TerrainPreviewAzureCoast
{
	public const int SampleVersion = 10;

	const float TaperAngleDegrees = 15f;
	const float TaperProbeFraction = 0.12f;
	const float MinTaperProbeMeters = 16f;
	const float MaxTaperProbeMeters = 64f;
	const float TaperInlandEdgeStartFraction = 0.78f;
	const float TipAlongShoreFraction = 0.28f;

	public static bool SampleAtWorldMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> SampleCoverage01( settings, worldXMeters, worldYMeters ) > 0.5f;

	public static float SampleCoverage01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( settings is null || !settings.EnableAzureCoastBiome )
			return 0f;

		var distFromCenter = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters) );
		if ( distFromCenter > settings.LandRadiusMeters )
			return 0f;

		if ( distFromCenter < settings.AzureCoastMinDistanceFromSpawnMeters )
			return 0f;

		var widthMeters = ResolveWidthMeters( settings );
		var inlandMeters = TerrainShorelineDisplay.SampleInlandDistanceFromDisplayWaterMeters(
			settings, worldXMeters, worldYMeters );
		if ( !float.IsFinite( inlandMeters ) || inlandMeters <= 0.001f )
			return 0f;

		var maxInlandMeters = ComputeTaperedMaxInlandMeters(
			settings, worldXMeters, worldYMeters, widthMeters, inlandMeters );
		if ( inlandMeters > maxInlandMeters )
			return 0f;

		return 1f;
	}

	public static float SampleInlandShoreDistanceMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> TerrainShorelineDisplay.SampleInlandDistanceFromDisplayWaterMeters(
			settings, worldXMeters, worldYMeters );

	public static float ResolveWidthMeters( TerrainPreviewSettings settings )
		=> Math.Max( 1f, settings.AzureCoastWidthMeters );

	static float ComputeTaperedMaxInlandMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float widthMeters,
		float inlandMeters )
	{
		if ( inlandMeters < widthMeters * TaperInlandEdgeStartFraction )
			return widthMeters;

		var probeMeters = Math.Clamp(
			widthMeters * TaperProbeFraction,
			MinTaperProbeMeters,
			MaxTaperProbeMeters );
		var taperSlope = MathF.Tan( TaperAngleDegrees * (MathF.PI / 180f) );

		var inlandXp = SampleInlandShoreDistanceMeters( settings, worldXMeters + probeMeters, worldYMeters );
		var inlandXm = SampleInlandShoreDistanceMeters( settings, worldXMeters - probeMeters, worldYMeters );
		var inlandYp = SampleInlandShoreDistanceMeters( settings, worldXMeters, worldYMeters + probeMeters );
		var inlandYm = SampleInlandShoreDistanceMeters( settings, worldXMeters, worldYMeters - probeMeters );

		var gradX = (inlandXp - inlandXm) / (2f * probeMeters);
		var gradY = (inlandYp - inlandYm) / (2f * probeMeters);
		var gradLenSq = (gradX * gradX) + (gradY * gradY);
		if ( gradLenSq < 1e-10f )
			return widthMeters;

		var gradLen = MathF.Sqrt( gradLenSq );
		var tanX = -gradY / gradLen;
		var tanY = gradX / gradLen;

		var inlandAlongPlus = SampleInlandShoreDistanceMeters(
			settings,
			worldXMeters + (tanX * probeMeters),
			worldYMeters + (tanY * probeMeters) );
		var inlandAlongMinus = SampleInlandShoreDistanceMeters(
			settings,
			worldXMeters - (tanX * probeMeters),
			worldYMeters - (tanY * probeMeters) );

		var minAlongInland = Math.Min(
			SanitizeInland( inlandAlongPlus, inlandMeters ),
			SanitizeInland( inlandAlongMinus, inlandMeters ) );

		// Bay centers also have low along-shore inland — only taper true tips near the inland edge.
		if ( minAlongInland > widthMeters * TipAlongShoreFraction )
			return widthMeters;

		var taperedReach = minAlongInland + (probeMeters * taperSlope);
		return Math.Min( widthMeters, taperedReach );
	}

	static float SanitizeInland( float value, float fallback )
		=> float.IsFinite( value ) ? value : fallback;
}
