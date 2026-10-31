namespace Survival;

/// <summary>
/// Massif shape (Mark 2026-10-30, after the plateau-and-peak pass: "look at those mountains and tell me they even
/// remotely resemble the average mountain … the plateau is SO clearly artificial"). There is no top any more: the
/// mountain is a dome that rises continuously from the lowland at the blob edge to one summit near the centre, cut by
/// ridges that radiate from the summit with valleys between them — the way a real massif is drained.
/// Everything comes from the blob a point is in (<see cref="TerrainPreviewMountainSpawnMask.SampleBlob"/>):
/// <list type="bullet">
/// <item><b>envelope</b>: (1 − d)^p from the edge (d = 1) to the summit (d = 0), p rolled per mountain around 1 by
/// <c>Mountains - Profile Variation</c> around 0.85 — mostly convex, a steep face out of the lowland rounding into the
/// summit, sometimes a straight cone or a gentle-footed concave climb; a <c>Cliff Chance</c> roll makes p ≈ 0.4, a dome standing on a near-vertical
/// foot ("massive dropoffs"); a slow noise across the mountain tilts the dome so the summit sits off-centre;</item>
/// <item><b>ridges</b>: ridged noise in polar coordinates around the centre, spurs about <c>Ridge Spacing (m)</c> apart
/// in metres at every height (branching as they run down, never converging into a spiky crown), valleys between them
/// cut down to <c>Ridge Depth</c> of the local height (sharp crests, rounded valley floors); a ~900 m knob noise along
/// the crests makes false summits, shoulders and saddles;</item>
/// <item>the summit reaches <c>Summit Height (m)</c> × a per-mountain roll (<c>Height Variation</c>) × a size scale, so
/// a small blob is a hill.</item>
/// </list>
/// </summary>
static class TerrainPreviewMountainHeight
{
	public readonly struct Profile
	{
		/// <summary>0 at the foot (and outside the mountain zone), 1 at the summit: the fraction of the way from the lowland to <see cref="TopMeters"/>.</summary>
		public float Rise01 { get; init; }

		/// <summary>Absolute height the summit of this mountain aims at, metres.</summary>
		public float TopMeters { get; init; }

		public bool Any => Rise01 > 0f;
	}

	/// <summary>Where the ridges fade out under the summit (fraction of the radius), so the apex is one clean pyramid.</summary>
	const float SummitSmooth01 = 0.12f;

	/// <summary>Every knob the shape reads — the water fingerprint hashes it because the mountain decides which lakes it fills (TerrainPreviewMountainLakes).</summary>
	public static void AddSettingsFingerprint( ref HashCode hash, TerrainPreviewSettings settings )
	{
		hash.Add( settings.MaxTerrainHeightMeters );
		hash.Add( settings.MountainSummitHeightMeters );
		hash.Add( settings.MountainHeightVariation01 );
		hash.Add( settings.MountainCliffChance01 );
		hash.Add( settings.MountainProfileVariation01 );
		hash.Add( settings.MountainRidgeSpacingMeters );
		hash.Add( settings.MountainRidgeDepth01 );
		hash.Add( settings.MountainTarnChance01 );
		hash.Add( settings.MountainInnerRadius01 );
		hash.Add( settings.MountainOuterRadius01 );
		hash.Add( settings.MountainBandFade01 );
		hash.Add( settings.MountainFalloffRimPower );
	}

	public static Profile Sample(
		TerrainPreviewSettings settings,
		in TerrainPreviewMountainSpawnMask.Blob blob,
		float mountainZone01,
		float nx,
		float ny,
		int seed )
	{
		if ( mountainZone01 <= 0f || blob.Field01 <= 0f )
			return default;

		var hash = blob.Hash01;
		var d = Math.Clamp( blob.Dist01, 0f, 1f );
		var zone = Math.Clamp( mountainZone01, 0f, 1f );
		var diameter = Math.Max( 500f, settings.WorldDiameterMeters );
		var wx = nx * diameter;
		var wy = ny * diameter;
		var radius = Math.Max( 100f, blob.RadiusMeters );

		// Summit height: Summit Height × per-mountain roll × size (blobs from half of Mountain Radius Max up get the full
		// height; smaller ones scale down with their radius so a 500 m blob is a hill, not a spike).
		var maxHeight = Math.Max( 50f, settings.MaxTerrainHeightMeters );
		var summit = Math.Clamp( settings.MountainSummitHeightMeters, 0f, maxHeight );
		var variation = Math.Clamp( settings.MountainHeightVariation01, 0f, 0.8f );
		var perMountain = 1f - (variation * hash);
		var sizeScale = Math.Clamp( blob.Radius01 / 0.5f, 0.3f, 1f );
		var top = summit * perMountain * sizeScale;

		// Envelope: (1 - d)^p. Profile Variation spreads p around 0.85 (0.6-1.2 at 0.7): mostly convex - the face rises
		// steeply out of the lowland and rounds into the summit, the way an alpine massif stands over its valley - with
		// the odd straight cone or gentle-footed concave climb. Cliff mountains take p = 0.35-0.5, a dome on a
		// near-vertical foot.
		var profileVar = Math.Clamp( settings.MountainProfileVariation01, 0f, 1f );
		var p = 0.85f * MathF.Pow( 2f, (((hash * 31.7f) % 1f) - 0.5f) * 1.4f * profileVar );
		if ( (hash * 13.37f) % 1f < Math.Clamp( settings.MountainCliffChance01, 0f, 1f ) )
			p = 0.35f + (0.15f * ((hash * 53.3f) % 1f));
		var envelope = MathF.Pow( 1f - d, p );

		// Tilt: a slow noise about the size of the mountain (0.75-1) lowers one side, so the summit sits off-centre and
		// the two flanks have different lengths.
		var tiltWave = Math.Max( 400f, radius * 1.2f );
		var tilt = 0.75f + (0.25f * TerrainPreviewNoise.Fbm( seed + 913, (wx / tiltWave) + (hash * 23f), (wy / tiltWave) + (hash * 41f), 2 ));

		// Ridges: ridged noise sampled on a circle around the centre whose radius grows with the distance out, so the
		// spurs stay Ridge Spacing apart in metres at every height - they branch as they run down instead of converging
		// into a 50 m sawtooth under the summit (the first pass did that: a spiky crown on every apex). Two octaves, so
		// the crests are sharp but nothing is finer than the mesh. Valleys between them cut Ridge Depth of the local
		// height (RidgedFbm is 1 on the crests, so 1 - ridge is the cut); the cut fades out under the summit
		// (SummitSmooth01) so the apex is one clean pyramid, and is at half strength at the foot so the spurs fan out.
		var spacing = Math.Max( 100f, settings.MountainRidgeSpacingMeters );
		var rho = Math.Max( 0.6f, d * radius / spacing );
		var ca = MathF.Cos( blob.Angle );
		var sa = MathF.Sin( blob.Angle );
		var ridge = TerrainPreviewNoise.RidgedFbm( seed + 914, (ca * rho) + (d * 0.8f) + (hash * 19f), (sa * rho) + (d * 0.3f) + (hash * 7f), 2 );
		var depth = Math.Clamp( settings.MountainRidgeDepth01, 0f, 0.9f ) * SmoothStep01( d / SummitSmooth01 ) * (1f - (0.5f * SmoothStep01( (d - 0.7f) / 0.3f )));
		var ridgeFactor = 1f - (depth * (1f - ridge));

		// Knobs along the crests: a ~900 m noise (0.75-1) so a spur has false summits, shoulders and saddles instead of
		// a straight climb and the silhouette is lumpy rather than a cone, plus a fine ~150 m grain (± 3 %).
		var knob = 0.75f + (0.25f * TerrainPreviewNoise.Fbm( seed + 915, (wx / 900f) + (hash * 11f), (wy / 900f) + (hash * 3f), 3 ));
		var grain = 1f + (0.06f * (TerrainPreviewNoise.Fbm( seed + 916, wx / 150f, wy / 150f, 2 ) - 0.5f));

		var rise = Math.Clamp( envelope * tilt * ridgeFactor * knob * grain, 0f, 1f );
		return new Profile
		{
			Rise01 = rise * zone,
			TopMeters = top,
		};
	}

	static float SmoothStep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
