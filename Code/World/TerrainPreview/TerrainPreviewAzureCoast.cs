namespace Survival;

/// <summary>
/// Teal coast patches (Mark 2026-10-30: "the chance for azure coast seems too low, it's a required biome … make it not
/// follow the coastline perfectly, like a kinda blob but it needs to be connected to an ocean coast"). The map is cut
/// into cells of <c>Biomes - Azure Blob Spacing (m)</c>; each cell rolls a presence chance (<c>Azure Blob Chance</c>, faded
/// out toward spawn around <c>Azure Coast Inner Fade</c>), a jittered centre and a radius between <c>Azure Blob Radius
/// Min / Max (m)</c>, the outline wobbled by low-frequency noise. A blob counts only if it reaches the OCEAN — its centre
/// lies within 0.9 × its radius of an ocean shore (the rim ring or water joined to it, never an inland lake) — and the
/// teal covers the dry land inside it no further than <c>Azure Max Inland (m)</c> from that shore. Hard biome edge, like
/// every other biome patch. The old shore band (width / stretch / shore chance) lives on as legacy keys only.
/// Callers must invoke <see cref="TerrainPreviewLandDiskFields.EnsureReady"/> before hot-path sampling.
/// </summary>
static class TerrainPreviewAzureCoast
{
	public const int SampleVersion = 12;

	/// <summary>Half width of the radial chance fade, as a fraction of the world radius.</summary>
	const float InnerFadeHalfWidth01 = 0.15f;
	/// <summary>How much of its radius a blob's centre may sit inland of the ocean shore and still count as coastal.</summary>
	const float CentreReach = 0.9f;
	const float OutlineWobble01 = 0.35f;

	readonly struct Blob
	{
		public bool Inside { get; init; }
		public float CentreX { get; init; }
		public float CentreY { get; init; }
		public float RadiusMeters { get; init; }
	}

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

		var blob = SampleBlob( settings, worldXMeters, worldYMeters );
		if ( !blob.Inside )
			return 0f;

		// Connected to the ocean: the blob's centre within reach of an ocean shore (its own distance, not this point's)
		var centreInland = TerrainShorelineDisplay.SampleInlandDistanceFromOceanShoreMeters( settings, blob.CentreX, blob.CentreY );
		if ( !float.IsFinite( centreInland ) || centreInland > blob.RadiusMeters * CentreReach )
			return 0f;

		var inlandMeters = TerrainShorelineDisplay.SampleInlandDistanceFromOceanShoreMeters( settings, worldXMeters, worldYMeters );
		if ( !float.IsFinite( inlandMeters ) || inlandMeters <= 0.001f )
			return 0f;   // on the water
		if ( inlandMeters > Math.Max( 50f, settings.AzureCoastMaxInlandMeters ) )
			return 0f;

		return 1f;
	}

	/// <summary>The teal blob a point is in, if any: cell-hash scatter with a per-cell chance faded toward spawn, jitter, radius and a wobbled outline.</summary>
	static Blob SampleBlob( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var chance = Math.Clamp( settings.AzureCoastBlobChance01, 0f, 1f );
		if ( chance <= 0f )
			return default;
		var seed = settings.WorldSeed;
		var worldRadius = Math.Max( 1f, settings.WorldRadiusMeters );
		var spacing = Math.Max( 500f, settings.AzureCoastBlobSpacingMeters );
		var radiusMin = Math.Max( 100f, settings.AzureCoastBlobRadiusMinMeters );
		var radiusMax = Math.Clamp( settings.AzureCoastBlobRadiusMaxMeters, radiusMin, spacing );
		var fadeCentre = Math.Clamp( settings.AzureCoastMinDistanceFromSpawn01, 0f, 1f );

		// Metres from the map corner (always positive, so the cell hash never sees a negative coordinate).
		var wx = worldXMeters + worldRadius;
		var wy = worldYMeters + worldRadius;
		var cellX = (int)MathF.Floor( wx / spacing );
		var cellY = (int)MathF.Floor( wy / spacing );
		var wobbleWavelength = radiusMax * 0.5f;
		var wobble = TerrainPreviewNoise.Fbm( seed + 872, wx / wobbleWavelength, wy / wobbleWavelength, 2 );
		var wobbleScale = 1f + ((wobble - 0.5f) * 2f * OutlineWobble01);

		for ( var oy = -1; oy <= 1; oy++ )
		{
			for ( var ox = -1; ox <= 1; ox++ )
			{
				var gx = cellX + ox;
				var gy = cellY + oy;
				if ( gx < 0 || gy < 0 )
					continue;
				var centreX = (gx + 0.2f + (TerrainPreviewNoise.Hash01( seed + 873, gx, gy ) * 0.6f)) * spacing;
				var centreY = (gy + 0.2f + (TerrainPreviewNoise.Hash01( seed + 874, gx, gy ) * 0.6f)) * spacing;
				// Radial fade: no blobs inside (centre − half width) × R, full chance beyond (centre + half width) × R —
				// judged at the blob's centre so a whole blob is in or out, never cut by a ring.
				var cdx = centreX - worldRadius;
				var cdy = centreY - worldRadius;
				var radial = SmoothStep01( ((MathF.Sqrt( (cdx * cdx) + (cdy * cdy) ) / worldRadius) - (fadeCentre - InnerFadeHalfWidth01)) / (2f * InnerFadeHalfWidth01) );
				if ( TerrainPreviewNoise.Hash01( seed + 871, gx, gy ) >= chance * radial )
					continue;   // this cell rolled no patch
				var radius = (radiusMin + ((radiusMax - radiusMin) * TerrainPreviewNoise.Hash01( seed + 875, gx, gy ))) * wobbleScale;
				var dx = wx - centreX;
				var dy = wy - centreY;
				if ( (dx * dx) + (dy * dy) >= radius * radius )
					continue;
				return new Blob
				{
					Inside = true,
					CentreX = centreX - worldRadius,
					CentreY = centreY - worldRadius,
					RadiusMeters = radius,
				};
			}
		}
		return default;
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
