namespace Survival;

/// <summary>
/// Low-frequency carve noise masked to the interior disk with a gentle radial falloff (not a hard spawn cutoff).
/// </summary>
static class TerrainPreviewInteriorWater
{
	public static float SampleCarve01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		int seed )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 0f;

		var radius = settings.WorldRadiusMeters;
		if ( radius <= 0f )
			return 0f;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		var dist01 = distMeters / radius;
		var interiorRadius01 = Math.Clamp( settings.InteriorZoneRadius01, 0.1f, 0.95f );
		var edgeFade01 = Math.Clamp( settings.InteriorWaterEdgeFade01, 0.01f, 0.35f );
		var outerStart01 = Math.Max( 0.01f, interiorRadius01 - edgeFade01 );

		if ( dist01 > interiorRadius01 )
			return 0f;

		var mask = SampleRadialMask(
			dist01,
			settings.InteriorWaterCenterInfluence01,
			settings.InteriorWaterFullInfluenceRadius01,
			settings.InteriorWaterFalloffPower );

		if ( dist01 > outerStart01 )
		{
			var edgeT = (interiorRadius01 - dist01) / Math.Max( 0.0001f, edgeFade01 );
			mask *= SmoothStep( edgeT );
		}

		if ( mask <= 0.0001f )
			return 0f;

		var diameter = settings.WorldDiameterMeters;
		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		var freq = Math.Clamp( settings.InteriorWaterFrequency, 0.25f, 32f );
		var noise = TerrainPreviewNoise.Fbm( seed + 450, nx * freq, ny * freq, 4 );
		var weight = Math.Clamp( settings.InteriorWaterWeight, 0f, 2f );

		return noise * weight * mask;
	}

	/// <summary>
	/// Soft ramp from a low center floor to full strength by <paramref name="fullInfluenceRadius01"/> (default 35% world radius).
	/// </summary>
	public static float SampleRadialMask(
		float dist01,
		float centerInfluence01,
		float fullInfluenceRadius01,
		float falloffPower )
	{
		var floor = Math.Clamp( centerInfluence01, 0f, 0.5f );
		var fullAt01 = Math.Clamp( fullInfluenceRadius01, 0.08f, 0.95f );

		if ( dist01 <= 0f )
			return floor;

		if ( dist01 >= fullAt01 )
			return 1f;

		var t = dist01 / fullAt01;
		var eased = SmoothStep( t );
		var power = Math.Clamp( falloffPower, 0.15f, 2.5f );
		eased = MathF.Pow( eased, power );

		return floor + ((1f - floor) * eased);
	}

	static float SmoothStep( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t );
	}
}
