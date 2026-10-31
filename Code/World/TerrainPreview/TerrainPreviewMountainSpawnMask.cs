namespace Survival;

/// <summary>
/// Mountain biome spawn mask — scattered round blobs (Mark 2026-10-29: mountains "generally rounded in shape, kinda
/// like Valheim, just little circles all over the place", no stripes). The map is cut into cells of
/// <c>Mountain Mask - Mountain Spacing (m)</c>; each cell rolls a seeded presence chance, a centre jittered inside the
/// cell and a radius between <c>Mountain Radius Min / Max (m)</c>. The field is the soft bump 1 − (d / r)² of the
/// nearest present blob, with the outline wobbled by low-frequency noise (<c>Outline Wobble</c>) so the circles are
/// not compass-drawn. The biome mask is the bump above <c>Biomes - Min Mountain Mask</c>; the height influence ramps
/// on the same bump, so every blob is one rounded mountain with its summit cluster near the centre.
/// The ridged range / belt / breaker / stretch field it replaces lives on as legacy JSON keys only.
/// </summary>
static class TerrainPreviewMountainSpawnMask
{
	public static void AddSettingsFingerprint( ref HashCode hash, TerrainPreviewSettings settings )
	{
		hash.Add( settings.EnableMountainLayer );
		hash.Add( settings.BiomeMinMountainMask01 );
		hash.Add( settings.BiomeMountainPlacementStrength01 );
		hash.Add( settings.MountainInnerRadius01 );
		hash.Add( settings.MountainOuterRadius01 );
		hash.Add( settings.MountainBandFade01 );
		hash.Add( settings.MountainBlobSpacingMeters );
		hash.Add( settings.MountainBlobChance01 );
		hash.Add( settings.MountainBlobRadiusMinMeters );
		hash.Add( settings.MountainBlobRadiusMaxMeters );
		hash.Add( settings.MountainBlobEdgeWobble01 );
		hash.Add( settings.MountainBlobElongation01 );
		hash.Add( settings.MountainSpawnSpeckFilterEnabled );
		hash.Add( settings.MountainSpawnMinPatchDiameterMeters );
		hash.Add( settings.MountainSpawnMinPatchSupport01 );
		hash.Add( settings.MountainSpawnMinPatchGridSteps );
		hash.Add( settings.MountainHeightInfluenceLow01 );
		hash.Add( settings.MountainHeightInfluenceHigh01 );
	}

	/// <summary>
	/// Wide soft ramp from the blob field — height/peaks only, no binary mask threshold.
	/// Keeps foothills continuous with neighboring biomes.
	/// </summary>
	public static float SampleMountainHeightInfluence01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		if ( placement <= 0.0001f )
			return 0f;

		var rampLow = Math.Clamp( settings.MountainHeightInfluenceLow01, 0.02f, 0.45f );
		var rampHigh = Math.Clamp(
			settings.MountainHeightInfluenceHigh01,
			rampLow + 0.08f,
			0.98f );
		var t = SmoothStep01( (placement - rampLow) / Math.Max( 0.001f, rampHigh - rampLow ) );
		t = SmoothStep01( t );
		return t * Math.Clamp( settings.BiomeMountainPlacementStrength01, 0f, 1f );
	}

	public static float SampleMask01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		if ( placement <= 0.0001f )
			return 0f;

		var threshold = Math.Clamp( settings.BiomeMinMountainMask01, 0.05f, 0.95f );
		if ( placement < threshold )
			return 0f;

		if ( settings.MountainSpawnSpeckFilterEnabled
			&& !PassesMinPatchDisk( settings, worldXMeters, worldYMeters, threshold ) )
			return 0f;

		return 1f;
	}

	static bool PassesThreshold01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float threshold )
	{
		var placement = SamplePlacement01( settings, worldXMeters, worldYMeters );
		return placement >= threshold;
	}

	static bool PassesMinPatchDisk(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float threshold )
	{
		var patchDiameter = Math.Max( 40f, settings.MountainSpawnMinPatchDiameterMeters );
		var gridSteps = Math.Clamp( settings.MountainSpawnMinPatchGridSteps, 3, 7 );
		var step = patchDiameter / gridSteps;
		var pass = 0;
		var total = 0;
		var half = gridSteps * 0.5f;

		for ( var iy = 0; iy <= gridSteps; iy++ )
		{
			for ( var ix = 0; ix <= gridSteps; ix++ )
			{
				total++;
				var ox = worldXMeters + ((ix - half) * step);
				var oy = worldYMeters + ((iy - half) * step);
				if ( PassesThreshold01( settings, ox, oy, threshold ) )
					pass++;
			}
		}

		var required = Math.Clamp( settings.MountainSpawnMinPatchSupport01, 0.2f, 0.95f );
		return pass >= MathF.Ceiling( total * required );
	}

	public static float SamplePlacement01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !settings.EnableMountainLayer )
			return 0f;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( radius <= 0f || diameter <= 0f )
			return 0f;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( TerrainPreviewMountainFalloff.SampleSpawnBand01( settings, distMeters ) <= 0.0001f )
			return 0f;

		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		var field = SampleRangeField01( settings, nx, ny, diameter );
		return field * Math.Clamp( settings.BiomeMountainPlacementStrength01, 0f, 1f );
	}

	/// <summary>The blob a point is in: its bump value, its radius as a fraction of Mountain Radius Max, and a per-blob hash.</summary>
	public readonly struct Blob
	{
		/// <summary>0 outside every mountain, 1 at the blob centre (1 − (d / r)²).</summary>
		public float Field01 { get; init; }

		/// <summary>Blob radius / Mountain Radius Max (0 when no blob).</summary>
		public float Radius01 { get; init; }

		/// <summary>Seeded 0–1 hash of the blob's cell — one value per mountain.</summary>
		public float Hash01 { get; init; }

		/// <summary>Distance from the blob centre over its (elongated, wobbled) radius: 0 at the centre, 1 at the edge.</summary>
		public float Dist01 { get; init; }

		/// <summary>Angle around the blob centre in the blob's own frame, radians.</summary>
		public float Angle { get; init; }

		/// <summary>The blob's base radius in metres (before elongation and wobble).</summary>
		public float RadiusMeters { get; init; }
	}

	/// <summary>
	/// The blob field, 0 outside every mountain and 1 at a blob centre. Pure function of (settings, nx, ny): nine
	/// cell hashes and one FBM, cheap enough that the old per-thread frame memo is gone.
	/// </summary>
	public static float SampleRangeField01( TerrainPreviewSettings settings, float nx, float ny, float worldDiameterMeters )
		=> SampleBlob( settings, nx, ny, worldDiameterMeters ).Field01;

	/// <summary>The nearest present blob (highest bump) at a point, with its radius and per-mountain hash for the height profile.</summary>
	public static Blob SampleBlob( TerrainPreviewSettings settings, float nx, float ny, float worldDiameterMeters )
	{
		var seed = settings.WorldSeed;
		var spacing = Math.Max( 500f, settings.MountainBlobSpacingMeters );
		var radiusMin = Math.Max( 100f, settings.MountainBlobRadiusMinMeters );
		var radiusMax = Math.Clamp( settings.MountainBlobRadiusMaxMeters, radiusMin, spacing );
		var chance = Math.Clamp( settings.MountainBlobChance01, 0f, 1f );
		if ( chance <= 0f )
			return default;

		// Metres from the map corner (always positive, so the cell hash never sees a negative coordinate).
		var wx = nx * worldDiameterMeters;
		var wy = ny * worldDiameterMeters;
		var cellX = (int)MathF.Floor( wx / spacing );
		var cellY = (int)MathF.Floor( wy / spacing );

		// One outline wobble for the whole map at about the blob scale plus finer notches: scales the radius seen
		// from this point, so a circle reads as a knobbly irregular massif instead of a compass circle.
		var wobbleAmp = Math.Clamp( settings.MountainBlobEdgeWobble01, 0f, 0.6f );
		var wobbleScale = 1f;
		if ( wobbleAmp > 0f )
		{
			var wobbleWavelength = radiusMax * 0.55f;
			var wobble = TerrainPreviewNoise.Fbm( seed + 647, wx / wobbleWavelength, wy / wobbleWavelength, 2 );
			var notchWavelength = radiusMax * 0.18f;
			var notch = TerrainPreviewNoise.Fbm( seed + 655, wx / notchWavelength, wy / notchWavelength, 2 );
			wobbleScale = 1f + ((wobble - 0.5f) * 2f * wobbleAmp) + ((notch - 0.5f) * 2f * wobbleAmp * 0.35f);
		}
		var elongation = Math.Clamp( settings.MountainBlobElongation01, 0f, 1f );

		var best = 0f;
		var bestRadius = 0f;
		var bestHash = 0f;
		var bestDist = 0f;
		var bestAngle = 0f;
		var bestRadiusMeters = 0f;
		for ( var oy = -1; oy <= 1; oy++ )
		{
			for ( var ox = -1; ox <= 1; ox++ )
			{
				var gx = cellX + ox;
				var gy = cellY + oy;
				if ( gx < 0 || gy < 0 )
					continue;
				if ( TerrainPreviewNoise.Hash01( seed + 648, gx, gy ) >= chance )
					continue;   // this cell rolled no mountain

				var jitterX = TerrainPreviewNoise.Hash01( seed + 649, gx, gy );
				var jitterY = TerrainPreviewNoise.Hash01( seed + 650, gx, gy );
				var centreX = (gx + 0.2f + (jitterX * 0.6f)) * spacing;
				var centreY = (gy + 0.2f + (jitterY * 0.6f)) * spacing;
				var radiusRoll = TerrainPreviewNoise.Hash01( seed + 651, gx, gy );
				var baseRadius = radiusMin + ((radiusMax - radiusMin) * radiusRoll);
				var radius = baseRadius * wobbleScale;
				// Elongation: each massif is stretched along its own axis (real ranges are seldom round).
				var axis = TerrainPreviewNoise.Hash01( seed + 653, gx, gy ) * MathF.PI;
				var stretch = 1f + (elongation * TerrainPreviewNoise.Hash01( seed + 654, gx, gy ));
				var dx = wx - centreX;
				var dy = wy - centreY;
				var cosA = MathF.Cos( axis );
				var sinA = MathF.Sin( axis );
				var u = ((dx * cosA) + (dy * sinA)) / stretch;
				var v = (dy * cosA) - (dx * sinA);
				var d2 = (u * u) + (v * v);
				var r2 = radius * radius;
				if ( d2 >= r2 )
					continue;

				var bump = 1f - (d2 / r2);
				if ( bump <= best )
					continue;

				best = bump;
				bestRadius = baseRadius / radiusMax;
				bestHash = TerrainPreviewNoise.Hash01( seed + 652, gx, gy );
				bestDist = MathF.Sqrt( d2 / r2 );
				bestAngle = MathF.Atan2( v, u );
				bestRadiusMeters = baseRadius;
			}
		}

		return new Blob
		{
			Field01 = best,
			Radius01 = bestRadius,
			Hash01 = bestHash,
			Dist01 = bestDist,
			Angle = bestAngle,
			RadiusMeters = bestRadiusMeters,
		};
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}

}
