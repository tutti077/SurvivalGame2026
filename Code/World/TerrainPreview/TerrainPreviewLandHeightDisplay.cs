namespace Survival;

/// <summary>
/// Converts sculpted height to world meters — one smooth lerp between lowland and mountain elevation.
/// <para>
/// <paramref name="lowlandShaped01"/> from biome sculpt is SoftCap meters / <paramref name="maxTerrainHeightMeters"/>.
/// Remapping that 01 through only <see cref="TerrainPreviewSettings.LandRollingHeightMaxMeters"/> crushed Clover
/// (e.g. 200 m soft-cap → ~50 m on the mesh). Recover meters first, then clamp to the lowland ceiling.
/// </para>
/// </summary>
static class TerrainPreviewLandHeightDisplay
{
	public static float ApplyDryLandMeters(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float lowlandShaped01,
		in TerrainPreviewMountainHeight.Profile mountain,
		float mountainInfluence01,
		float maxTerrainHeightMeters )
	{
		maxTerrainHeightMeters = Math.Max( 50f, maxTerrainHeightMeters );
		_ = baseHeight01; // already baked into lowlandShaped01 via ShapeBlend
		lowlandShaped01 = Math.Clamp( lowlandShaped01, 0f, 1f );
		mountainInfluence01 = Math.Clamp( mountainInfluence01, 0f, 1f );

		var floor = settings.SeaLevelMeters + Math.Max( 0.05f, settings.InlandDryLandSeaMarginMeters );
		var lowlandCeiling = ResolveLowlandCeilingMeters( settings, maxTerrainHeightMeters );
		// Headroom so post-SoftCap clover grit (±micro amp) is not flattened by the ceiling clamp.
		var microHeadroom = Math.Clamp( settings.BiomeCloverMicroAmplitudeMeters, 0f, 12f );
		if ( microHeadroom < 0.4f )
			microHeadroom = 6f;
		lowlandCeiling = Math.Min( maxTerrainHeightMeters, lowlandCeiling + microHeadroom );

		// SoftCap(m) / max → meters (so BiomeCloverMaxHeightMeters can actually appear).
		var lowlandMeters = Math.Clamp( lowlandShaped01 * maxTerrainHeightMeters, floor, lowlandCeiling );

		// Massif: the ground rises from the lowland to the mountain's summit height over Rise01 (the dome, ridges and
		// valleys are all in the rise); the whole thing blends in over the mountain height influence ramp.
		var mountainMeters = lowlandMeters;
		if ( mountain.Any )
		{
			var top = Math.Max( lowlandMeters, mountain.TopMeters );
			mountainMeters = Lerp( lowlandMeters, top, Math.Clamp( mountain.Rise01, 0f, 1f ) );
			mountainMeters = Math.Clamp( mountainMeters, lowlandMeters, maxTerrainHeightMeters );
		}

		var blendT = MountainBlend01( mountainInfluence01 );
		return Lerp( lowlandMeters, mountainMeters, blendT );
	}

	/// <summary>How much of the mountain profile the ground takes at this height influence (the lowland → mountain lerp weight).</summary>
	public static float MountainBlend01( float mountainInfluence01 ) => SmoothStep01( Math.Clamp( mountainInfluence01, 0f, 1f ) );

	/// <summary>
	/// Absolute elevation ceiling for non-mountain land — must reach every lowland biome max.
	/// </summary>
	public static float ResolveLowlandCeilingMeters( TerrainPreviewSettings settings, float maxTerrainHeightMeters )
	{
		var ceiling = Math.Max( settings.LandRollingHeightMaxMeters, settings.BiomeCloverMaxHeightMeters );
		ceiling = Math.Max( ceiling, settings.BiomeAmberMaxHeightMeters );
		ceiling = Math.Max( ceiling, settings.BiomeRedwoodMaxHeightMeters );
		return Math.Clamp( ceiling, 20f, Math.Max( 50f, maxTerrainHeightMeters ) );
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);
}
