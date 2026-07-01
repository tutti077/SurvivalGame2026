namespace Survival;

/// <summary>
/// Per-biome elevation soft caps (m) — cap each biome independently, blend smoothly at borders.
/// </summary>
static class TerrainPreviewBiomeHeightCap
{
	enum SoftCapProfile
	{
		FlatLand,
		Forest,
		Mountain,
	}

	public static float ApplyBlendedHeightMeters(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters,
		float heightMeters,
		float maxTerrainHeightMeters )
	{
		if ( sample.HasLandWeights )
			return ApplyBlendedHeightMeters( settings, sample.LandWeights, heightMeters, maxTerrainHeightMeters, sample );

		if ( !settings.EnableBiomeHeightBlend )
		{
			var biome = TerrainPreviewBiomeResolver.Resolve( settings, sample, worldXMeters, worldYMeters );
			return SoftCapForBiome( settings, heightMeters, biome.BiomeId, maxTerrainHeightMeters );
		}

		var weights = TerrainPreviewBiomeResolver.SampleLandBiomeWeights(
			settings, sample, worldXMeters, worldYMeters );
		return ApplyBlendedHeightMeters( settings, weights, heightMeters, maxTerrainHeightMeters, sample );
	}

	public static float ApplyBlendedHeightMeters(
		TerrainPreviewSettings settings,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float heightMeters,
		float maxTerrainHeightMeters,
		TerrainPreviewSample sample = default )
	{
		if ( !settings.EnableBiomeHeightBlend )
			return heightMeters;

		var landCapped = CapWithWeights(
			settings,
			NormalizeLandWeights( weights ),
			heightMeters,
			maxTerrainHeightMeters );

		var mountainInfluence = ComputeMountainCapInfluence( settings, sample, weights );
		if ( mountainInfluence <= 0.0001f )
			return landCapped;

		var fullCapped = CapWithWeights(
			settings,
			SharpenCapWeights( weights, Math.Clamp( settings.BiomeHeightCapBorderSharpness, 1f, 4f ) ),
			heightMeters,
			maxTerrainHeightMeters );

		return Lerp( landCapped, fullCapped, SmoothStep01( mountainInfluence ) );
	}

	static float CapWithWeights(
		TerrainPreviewSettings settings,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float heightMeters,
		float maxTerrainHeightMeters )
	{
		var total = weights.Total;
		if ( total <= 0.0001f )
			return heightMeters;

		var sum =
			(weights.Clover * SoftCapForBiome( settings, heightMeters, TerrainPreviewBiomeId.CloverHills, maxTerrainHeightMeters ))
			+ (weights.Redwood * SoftCapForBiome( settings, heightMeters, TerrainPreviewBiomeId.RedwoodForest, maxTerrainHeightMeters ))
			+ (weights.Amber * SoftCapForBiome( settings, heightMeters, TerrainPreviewBiomeId.AmberDunes, maxTerrainHeightMeters ))
			+ (weights.Mountain * SoftCapForBiome( settings, heightMeters, TerrainPreviewBiomeId.Mountain, maxTerrainHeightMeters ));

		return sum / total;
	}

	static float ComputeMountainCapInfluence(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights )
	{
		if ( weights.Mountain <= 0.0001f )
			return 0f;

		var minSlope = Math.Clamp( settings.BiomeMountainMinSlopeDegrees, 2f, 45f );
		var slopeGate = SmoothStep01( sample.MountainSlopeDegrees / minSlope );
		var peakGate = SmoothStep01( sample.MountainPeakHeight01 / Math.Max( 0.05f, settings.BiomeMountainMinPeakLift01 ) );
		var shapeGate = Math.Max( slopeGate, peakGate );

		var weightGate = SmoothStep01( weights.Mountain / Math.Max( 0.08f, settings.BiomeMountainCapWeightFullAt01 ) );
		var mountainMin = Math.Clamp( settings.BiomeMountainMinHeight01, 0.1f, 0.95f );
		var heightGate = SmoothStep01( (sample.Height01 - (mountainMin * 0.82f)) / 0.1f );

		return Math.Clamp( shapeGate * weightGate * heightGate, 0f, 1f );
	}

	static TerrainPreviewBiomeResolver.LandBiomeWeights NormalizeLandWeights(
		TerrainPreviewBiomeResolver.LandBiomeWeights weights )
	{
		var landTotal = weights.Clover + weights.Redwood + weights.Amber;
		if ( landTotal <= 0.0001f )
			return new TerrainPreviewBiomeResolver.LandBiomeWeights { Clover = 1f };

		return new TerrainPreviewBiomeResolver.LandBiomeWeights
		{
			Clover = weights.Clover / landTotal,
			Redwood = weights.Redwood / landTotal,
			Amber = weights.Amber / landTotal,
			Mountain = 0f,
		};
	}

	static TerrainPreviewBiomeResolver.LandBiomeWeights SharpenCapWeights(
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float power )
	{
		var clover = MathF.Pow( Math.Max( 0f, weights.Clover ), power );
		var redwood = MathF.Pow( Math.Max( 0f, weights.Redwood ), power );
		var amber = MathF.Pow( Math.Max( 0f, weights.Amber ), power );
		var mountain = MathF.Pow( Math.Max( 0f, weights.Mountain ), power );
		var total = clover + redwood + amber + mountain;
		if ( total <= 0.0001f )
			return weights;

		return new TerrainPreviewBiomeResolver.LandBiomeWeights
		{
			Clover = clover / total,
			Redwood = redwood / total,
			Amber = amber / total,
			Mountain = mountain / total,
		};
	}

	public static float SoftCapForBiome(
		TerrainPreviewSettings settings,
		float heightMeters,
		TerrainPreviewBiomeId biomeId,
		float maxTerrainHeightMeters )
	{
		var capMeters = CapLimitMeters( settings, biomeId, maxTerrainHeightMeters );
		return SoftCapMeters( settings, heightMeters, capMeters, ProfileForBiome( biomeId ) );
	}

	static SoftCapProfile ProfileForBiome( TerrainPreviewBiomeId biomeId )
		=> biomeId switch
		{
			TerrainPreviewBiomeId.RedwoodForest => SoftCapProfile.Forest,
			TerrainPreviewBiomeId.Mountain => SoftCapProfile.Mountain,
			_ => SoftCapProfile.FlatLand,
		};

	static float SoftCapMeters(
		TerrainPreviewSettings settings,
		float heightMeters,
		float capMeters,
		SoftCapProfile profile )
	{
		if ( capMeters <= 0.001f )
			return 0f;

		if ( heightMeters <= 0f )
			return heightMeters;

		return profile switch
		{
			SoftCapProfile.Mountain => SoftCapMountain( settings, heightMeters, capMeters ),
			SoftCapProfile.Forest => SoftCapForest( settings, heightMeters, capMeters ),
			_ => SoftCapFlatLand( settings, heightMeters, capMeters ),
		};
	}

	static float SoftCapFlatLand( TerrainPreviewSettings settings, float heightMeters, float capMeters )
	{
		if ( heightMeters <= capMeters )
			return heightMeters;

		var retain = Math.Clamp( settings.BiomeFlatExcessRetention01, 0f, 0.12f );
		return capMeters + ((heightMeters - capMeters) * retain);
	}

	static float SoftCapForest( TerrainPreviewSettings settings, float heightMeters, float capMeters )
	{
		var knee = capMeters * Math.Clamp( settings.BiomeForestSoftCapKneeStart01, 0.8f, 0.98f );
		var retain = Math.Clamp( settings.BiomeForestExcessRetention01, 0.02f, 0.15f );

		if ( heightMeters <= knee )
			return heightMeters;

		if ( heightMeters <= capMeters )
		{
			var bandSpan = Math.Max( 0.001f, capMeters - knee );
			var bandT = (heightMeters - knee) / bandSpan;
			var eased = knee + (bandSpan * SmoothStep01( bandT ));
			var blend = Math.Clamp( settings.BiomeForestApproachBlend01, 0f, 0.5f );
			return Lerp( heightMeters, eased, blend );
		}

		return capMeters + ((heightMeters - capMeters) * retain);
	}

	static float SoftCapMountain( TerrainPreviewSettings settings, float heightMeters, float capMeters )
	{
		if ( heightMeters <= capMeters )
			return heightMeters;

		var retain = Math.Clamp( settings.BiomeMountainExcessRetention01, 0.05f, 0.35f );
		return capMeters + ((heightMeters - capMeters) * retain);
	}

	public static float CapLimitMeters(
		TerrainPreviewSettings settings,
		TerrainPreviewBiomeId biomeId,
		float maxTerrainHeightMeters )
	{
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );

		return biomeId switch
		{
			TerrainPreviewBiomeId.CloverHills => Math.Max( 1f, settings.BiomeCloverMaxHeightMeters ),
			TerrainPreviewBiomeId.RedwoodForest => Math.Max( 1f, settings.BiomeRedwoodMaxHeightMeters ),
			TerrainPreviewBiomeId.AmberDunes => Math.Max( 1f, settings.BiomeAmberMaxHeightMeters ),
			TerrainPreviewBiomeId.Mountain => Math.Min(
				maxTerrainHeightMeters,
				Math.Max( 1f, settings.BiomeMountainMaxHeightMeters ) ),
			TerrainPreviewBiomeId.Water => 0f,
			_ => maxTerrainHeightMeters,
		};
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t );
}
