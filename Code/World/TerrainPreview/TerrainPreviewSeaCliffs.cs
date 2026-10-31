namespace Survival;

/// <summary>
/// Sea cliffs (Mark 2026-10-30: "in some cases the biomes shouldn't perfectly slope right into the water, like the
/// Cliffs of Moher"): along the OCEAN shore — the rim circle and any water joined to it, never an inland lake —
/// a presence noise of wavelength <c>Coast - Sea Cliff Stretch (m)</c> against <c>Sea Cliff Chance</c> picks stretches
/// of coast where the land does not drain to the beach but rises to a headland of <c>Sea Cliff Height (m)</c> (× 0.6–1 by
/// a slower noise) at the water's edge and drops to the sea in a wall (the shore bank's run shrinks to ~3 m there).
/// <see cref="TerrainPreviewCoastalSmoothing"/> holds the headland over <c>Sea Cliff Band (m)</c> inland of the shore;
/// <c>TerrainPreviewPipeline.ApplyShoreBank</c> makes the wall.
/// </summary>
public static class TerrainPreviewSeaCliffs
{
	/// <summary>Presence-noise band over which a cliff stretch fades in / out (short, so a wall never starts half way up).</summary>
	const float GateBand = 0.06f;

	public static void AddSettingsFingerprint( ref HashCode hash, TerrainPreviewSettings settings )
	{
		hash.Add( settings.CoastCliffChance01 );
		hash.Add( settings.CoastCliffStretchMeters );
		hash.Add( settings.CoastCliffHeightMeters );
		hash.Add( settings.CoastCliffBandMeters );
	}

	/// <summary>0 = a sloping shore / beach here, 1 = a sea cliff. Position noise, so it is continuous along and across the coast.</summary>
	public static float Gate01( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var chance = Math.Clamp( settings.CoastCliffChance01, 0f, 1f );
		if ( chance <= 0f || settings.CoastCliffHeightMeters <= 0f )
			return 0f;
		var stretch = Math.Max( 500f, settings.CoastCliffStretchMeters );
		// Two-octave FBM sits mostly in 0.3–0.7: chance 1 → threshold 0.25 (always), 0.5 → 0.5, 0 → 0.75 (never).
		var noise = TerrainPreviewNoise.Fbm( settings.WorldSeed + 931, worldXMeters / stretch, worldYMeters / stretch, 2 );
		var threshold = 0.5f + ((0.5f - chance) * 0.5f);
		return SmoothStep01( (noise - threshold) / GateBand );
	}

	/// <summary>The headland's height above sea level here (m): Sea Cliff Height × 0.6–1 by a slower noise.</summary>
	public static float TopMeters( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var height = Math.Max( 0f, settings.CoastCliffHeightMeters );
		var stretch = Math.Max( 500f, settings.CoastCliffStretchMeters ) * 0.7f;
		var noise = TerrainPreviewNoise.Fbm( settings.WorldSeed + 932, worldXMeters / stretch, worldYMeters / stretch, 2 );
		return height * (0.6f + (0.4f * Math.Clamp( (noise - 0.25f) * 2f, 0f, 1f )));
	}

	/// <summary>
	/// Is the nearest shore of a dry point an ocean shore — the rim (nearer than any lake), or a lake joined to the rim?
	/// </summary>
	public static bool IsOceanShore( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float metersToShore, float metersToLandEdge )
	{
		if ( metersToLandEdge <= metersToShore + 0.5f )
			return true;
		return settings.EnableInteriorWaterLayer
			&& TerrainPreviewLandDiskFields.IsOceanWaterWithinMeters( settings, worldXMeters, worldYMeters, metersToShore + 120f );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
