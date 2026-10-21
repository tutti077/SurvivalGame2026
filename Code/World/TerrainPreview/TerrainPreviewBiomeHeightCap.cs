namespace Survival;

/// <summary>
/// Per-biome elevation soft caps (m). Each biome shaper caps its own body through <see cref="SoftCapForBiome"/>;
/// the old blended post-cap (slope / peak gated) had no caller and was removed 2026-10-05.
/// </summary>
static class TerrainPreviewBiomeHeightCap
{
	enum SoftCapProfile
	{
		FlatLand,
		Forest,
		Mountain,
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
