namespace Survival;

/// <summary>
/// Redwood Forest sculpt: a continuously sloped hillside (domain-warped macro slopes + rounded
/// ravines) cut by rare contour-following <b>ledges</b> — a sharp drop of 3–15 m (slow noise picks
/// the height along the line) every <see cref="TerrainPreviewSettings.BiomeRedwoodLedgeRiseIntervalMeters"/>
/// of climb, above which the hillside simply resumes. Every ledge is passable again within a short
/// distance in two ways: short walk-up notches from two perpendicular families of seed-rotated,
/// gently warped "ramp streets" (any ledge meets one within ~1.4× <see cref="TerrainPreviewSettings.BiomeRedwoodLedgeRampSpacingMeters"/>),
/// and long <b>blend</b> stretches where the terrace dissolves completely so the ground is just the
/// hillside — the drive-up places. All inputs are world meters; defaults were calibrated in a
/// standalone harness (median hillside ≈ 12°, p90 ≈ 23°, a ledge line every ~200 m, walk-up within 20–30 m).
/// </summary>
static partial class TerrainPreviewBiomeTerrainShaper
{
	static float ShapeRedwoodForest(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out TerrainBiomeLedgeInfo ledgeInfo )
	{
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var radius = diameter * 0.5f;
		var worldX = (nx * diameter) - radius;
		var worldY = (ny * diameter) - radius;
		var maxH = Math.Max( 50f, maxTerrainHeightMeters );

		var hillside = SampleRedwoodHillsideMeters( settings, seed, worldX, worldY );

		// Hillside rides on the world base; SoftCap the body, then add ledges + duff so the cap never squashes a drop.
		var baseMeters = baseHeight01 * maxH;
		var softMeters = TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, baseMeters + hillside, TerrainPreviewBiomeId.RedwoodForest, maxTerrainHeightMeters );

		// Ledge lines are contours of the REAL pre-ledge ground (world base + hillside swells, capped) minus the
		// ravine creases - not of the hillside noise alone. The base can out-slope the hillside, and a contour of
		// the hillside alone then runs across ground that falls the other way, so the step raised the low side
		// (Mark 2026-10-06: "a ledge facing the wrong way up a hill"). The ravines are left out because their
		// V creases kink every contour at each gully ("very zig-zaggy"); a gully now simply cuts across a bench.
		var ledge = SampleRedwoodLedgeMeters( settings, seed, worldX, worldY, nx, ny, maxH, maxTerrainHeightMeters, out _, out _, out ledgeInfo );

		var microAmp = Math.Clamp( settings.BiomeRedwoodMicroAmplitudeMeters, 0f, 3f );
		var grit = microAmp < 0.25f ? 0f : SampleSurfaceGritMeters( seed + 730, worldX, worldY, 20f, microAmp );
		var finalMeters = Math.Max( 0f, softMeters + ledge + grit );

		return finalMeters / maxH;
	}

	/// <summary>Hillside relief (m, centred near 0): domain-warped macro slopes plus rounded ravines.</summary>
	/// <summary>Dev probe: the ledge field, its gradient magnitude, the contour phase u, the signed distance d and the final offset at a world point.</summary>
	public static string DebugRedwoodLedge( TerrainPreviewSettings settings, float worldX, float worldY )
	{
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var radius = diameter * 0.5f;
		var nx = (worldX + radius) / diameter;
		var ny = (worldY + radius) / diameter;
		var maxH = Math.Max( 50f, settings.MaxTerrainHeightMeters );
		var seed = settings.WorldSeed;
		var field = SampleRedwoodLedgeFieldMeters( settings, seed, worldX, worldY, nx, ny, maxH, settings.MaxTerrainHeightMeters );
		var hx = SampleRedwoodLedgeFieldMeters( settings, seed, worldX + 1f, worldY, nx + (1f / diameter), ny, maxH, settings.MaxTerrainHeightMeters );
		var hy = SampleRedwoodLedgeFieldMeters( settings, seed, worldX, worldY + 1f, nx, ny + (1f / diameter), maxH, settings.MaxTerrainHeightMeters );
		var interval = Math.Clamp( settings.BiomeRedwoodLedgeRiseIntervalMeters, 8f, 200f );
		var offsetK = TerrainPreviewNoise.Hash01( seed + 710, 1, 1 ) * interval;
		var f = (field + offsetK) / interval;
		var u = f - MathF.Floor( f );
		var ledge = SampleRedwoodLedgeMeters( settings, seed, worldX, worldY, nx, ny, maxH, settings.MaxTerrainHeightMeters, out var passable, out var d, out var info );
		return $"field={field:0.000} gx={hx - field:0.0000} gy={hy - field:0.0000} u={u:0.0000} d={d:0.00} ledge={ledge:0.00} pass={passable:0.00} half={info.HalfFaceWidthMeters:0.00}";
	}

	static float SampleRedwoodHillsideMeters( TerrainPreviewSettings settings, int seed, float worldX, float worldY )
	{
		var slopes = SampleRedwoodMacroSlopesMeters( settings, seed, worldX, worldY, 4 );

		var ravineWave = Math.Clamp( settings.BiomeRedwoodRavineWavelengthMeters, 40f, 400f );
		var ravineDepth = Math.Clamp( settings.BiomeRedwoodRavineDepthMeters, 0f, 40f );
		var ridged = TerrainPreviewNoise.RidgedFbm( seed + 703, worldX / ravineWave, worldY / ravineWave, 3 );
		var ravine = -(ridged - 0.5f) * ravineDepth;
		return slopes + ravine;
	}

	/// <summary>
	/// The hillside's macro swells alone (m, centred near 0). The ground uses all 4 octaves; the ledge lines
	/// follow the first <see cref="TerrainPreviewSettings.BiomeRedwoodLedgeLineOctaves"/> of the same noise,
	/// so with fewer octaves they are long smooth sweeps over the same hills (same seed, warp and phase).
	/// </summary>
	static float SampleRedwoodMacroSlopesMeters( TerrainPreviewSettings settings, int seed, float worldX, float worldY, int octaves )
	{
		var macroWave = Math.Clamp( settings.BiomeRedwoodHillsideWavelengthMeters, 200f, 2000f );
		var relief = Math.Clamp( settings.BiomeRedwoodHillsideReliefMeters, 10f, 300f );
		var mx = worldX / macroWave;
		var my = worldY / macroWave;
		var warpX = (TerrainPreviewNoise.Fbm( seed + 701, mx * 0.7f, my * 0.7f, 2 ) - 0.5f) * 0.6f;
		var warpY = (TerrainPreviewNoise.Fbm( seed + 702, mx * 0.7f, my * 0.7f, 2 ) - 0.5f) * 0.6f;
		var macro = (TerrainPreviewNoise.Fbm( seed + 700, mx + warpX, my + warpY, Math.Clamp( octaves, 1, 4 ) ) - 0.5f) * 2f;
		macro = SoftClip( macro * 1.6f );
		return macro * relief * 0.5f;
	}

	/// <summary>
	/// The ground the ledge lines are contours of: world base + macro swells, soft-capped like the body -
	/// everything that decides which way the hill really falls, without the ravine creases and the duff.
	/// Three base samples per vertex (here and the two gradient probes); chunk build only.
	/// </summary>
	static float SampleRedwoodLedgeFieldMeters(
		TerrainPreviewSettings settings,
		int seed,
		float worldX,
		float worldY,
		float nx,
		float ny,
		float maxH,
		float maxTerrainHeightMeters )
	{
		var base01 = TerrainPreviewBaseHeight.SampleAfterCurve01( settings, worldX, worldY, nx, ny, seed, out _ );
		var macro = SampleRedwoodMacroSlopesMeters( settings, seed, worldX, worldY, settings.BiomeRedwoodLedgeLineOctaves );
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, (base01 * maxH) + macro, TerrainPreviewBiomeId.RedwoodForest, maxTerrainHeightMeters );
	}

	/// <summary>
	/// Ledge height at this point (m): slow noise between the min and max knobs, gained so both extremes
	/// actually occur and squared so tall drops are the rarer ones.
	/// </summary>
	static float SampleRedwoodLedgeHeightMeters( TerrainPreviewSettings settings, int seed, float worldX, float worldY )
	{
		var minH = Math.Clamp( settings.BiomeRedwoodLedgeHeightMinMeters, 0f, 30f );
		var maxH = Math.Clamp( settings.BiomeRedwoodLedgeHeightMaxMeters, minH, 30f );
		var wave = Math.Clamp( settings.BiomeRedwoodLedgeHeightWavelengthMeters, 20f, 600f );
		var n = ((TerrainPreviewNoise.Fbm( seed + 711, worldX / wave, worldY / wave, 3 ) - 0.5f) * 3f) + 0.5f;
		n = Math.Clamp( n, 0f, 1f );
		n *= n;
		return Lerp( minH, maxH, n );
	}

	/// <summary>
	/// Terrace offset (m) added to the hillside — zero on average, ±height/2 either side of a ledge
	/// line, so the biome's mean elevation and caps are untouched. Ledge lines are contour lines of the
	/// real pre-ledge ground (world base + hillside swells, no ravines) every LedgeRiseInterval m of rise. The drop is a hard step LedgeFaceWidth wide
	/// (1 m → it lands inside one or two 2 m quads, 51–82° depending on height and where the grid
	/// falls, above the pawn's 50° walkable limit) except where it is made passable: inside ramp
	/// streets it is stretched into a local LedgeRampGrade notch, and inside blend stretches the drop
	/// height fades to zero over ~50 m so the ground returns to plain hillside. Costs two extra
	/// hillside samples for the gradient — chunk build only, never per frame.
	/// </summary>
	static float SampleRedwoodLedgeMeters(
		TerrainPreviewSettings settings,
		int seed,
		float worldX,
		float worldY,
		float nx,
		float ny,
		float maxH,
		float maxTerrainHeightMeters,
		out float passable01,
		out float signedDistanceMeters,
		out TerrainBiomeLedgeInfo info )
	{
		passable01 = 0f;
		signedDistanceMeters = float.MaxValue;
		info = TerrainBiomeLedgeInfo.None;

		var interval = Math.Clamp( settings.BiomeRedwoodLedgeRiseIntervalMeters, 8f, 200f );
		if ( settings.BiomeRedwoodLedgeHeightMaxMeters < 0.25f )
			return 0f;

		// Contour field and its gradient on the real pre-ledge ground (see SampleRedwoodLedgeFieldMeters).
		const float probe = 1f;
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var probeN = probe / diameter;
		var hillsideMeters = SampleRedwoodLedgeFieldMeters( settings, seed, worldX, worldY, nx, ny, maxH, maxTerrainHeightMeters );
		var hx = SampleRedwoodLedgeFieldMeters( settings, seed, worldX + probe, worldY, nx + probeN, ny, maxH, maxTerrainHeightMeters );
		var hy = SampleRedwoodLedgeFieldMeters( settings, seed, worldX, worldY + probe, nx, ny + probeN, maxH, maxTerrainHeightMeters );
		var gx = (hx - hillsideMeters) / probe;
		var gy = (hy - hillsideMeters) / probe;
		var grade = Math.Max( 0.03f, MathF.Sqrt( (gx * gx) + (gy * gy) ) );

		var offset = TerrainPreviewNoise.Hash01( seed + 710, 1, 1 ) * interval;
		var f = (hillsideMeters + offset) / interval;
		var k = MathF.Floor( f );
		var u = f - k;
		// Signed ground distance to the nearest ledge line (+ = uphill side).
		var d = (u - 0.5f) * interval / grade;
		signedDistanceMeters = d;

		// Height never exceeds 85 % of the rise interval, or the ground between ledges would tilt back downhill.
		var height = Math.Min( SampleRedwoodLedgeHeightMeters( settings, seed, worldX, worldY ), interval * 0.85f );
		if ( height < 0.25f )
			return 0f;

		// Contours that close around a LOW (a basin) would make an inward-facing ring — a sinkhole with a
		// cliff wall (Mark 2026-10-07). Ledges only nest around highs: where the swell field curves up like a
		// bowl (positive Laplacian) tighter than ~250 m against its slope, the drop fades out.
		{
			const float lp = 10f;
			var mc = SampleRedwoodMacroSlopesMeters( settings, seed, worldX, worldY, settings.BiomeRedwoodLedgeLineOctaves );
			var mxp = SampleRedwoodMacroSlopesMeters( settings, seed, worldX + lp, worldY, settings.BiomeRedwoodLedgeLineOctaves );
			var mxm = SampleRedwoodMacroSlopesMeters( settings, seed, worldX - lp, worldY, settings.BiomeRedwoodLedgeLineOctaves );
			var myp = SampleRedwoodMacroSlopesMeters( settings, seed, worldX, worldY + lp, settings.BiomeRedwoodLedgeLineOctaves );
			var mym = SampleRedwoodMacroSlopesMeters( settings, seed, worldX, worldY - lp, settings.BiomeRedwoodLedgeLineOctaves );
			var laplacian = ((mxp + mxm - (2f * mc)) + (myp + mym - (2f * mc))) / (lp * lp);
			var bowl = laplacian * 250f / Math.Max( 0.02f, grade );
			var basin01 = SmoothStep01( Math.Clamp( (bowl - 0.6f) / 0.8f, 0f, 1f ) );
			height *= 1f - basin01;
			if ( height < 0.25f )
				return 0f;
		}

		// Blend stretches shrink the drop itself (over ~50 m, so the ±height/2 terrace tilt eases out as a gentle bank).
		// The terrace offset is a sawtooth whose amplitude is the ledge height, so anything that changes the ground
		// between ledges must change slowly — stretching the step across the whole gap (an earlier attempt) put a
		// wall along every street and blend edge.
		var blend01 = SampleRedwoodBlendMask01( settings, seed, worldX, worldY );
		height *= 1f - blend01;
		passable01 = blend01;
		if ( height < 0.05f )
			return 0f;

		// Taller drops get wider, further-apart notches (up to 2.5×) so the notch sides stay ~50° gully banks.
		var notchScale = Math.Clamp( height / 5f, 1f, 2.5f );
		var ramp01 = SampleRedwoodRampMask01( settings, seed, worldX, worldY, notchScale, height );
		// Near-flat ground (< ~6 % grade): contour loops shrink to knobs, so the drop softens into a slope there.
		ramp01 = Math.Max( ramp01, SmoothStep01( Math.Clamp( (0.1f - grade) / 0.05f, 0f, 1f ) ) );
		passable01 = Math.Max( passable01, ramp01 );

		// A tall ledge spreads its face so it is never steeper than Face Slope: 30 m at 70° is an 11 m face the
		// 2 m grid resolves over several rows, instead of a 1 m step the vertex snapping had to fake (fins).
		var faceSlopeTan = MathF.Tan( Math.Clamp( settings.BiomeRedwoodLedgeFaceSlopeDegrees, 45f, 89f ) * MathF.PI / 180f );
		var faceWidth = Math.Max( Math.Clamp( settings.BiomeRedwoodLedgeFaceWidthMeters, 0.2f, 6f ), height / faceSlopeTan );
		var rampGrade = Math.Clamp( settings.BiomeRedwoodLedgeRampGradeDegrees, 5f, 45f ) * MathF.PI / 180f;
		// The ramp profile below peaks at 1.15× its mean slope; 1.15× the run keeps the notch centre at the knob's grade.
		var rampRun = 1.15f * height / MathF.Tan( rampGrade );
		// A notch stays local: never more than 45 % of the gap to the next ledge line, or it would merge into it.
		var localRun = Math.Min( Math.Max( 1f, rampRun ), 0.45f * interval / grade );
		var width = Lerp( faceWidth, localRun, ramp01 );

		// Ledge line nearby → tell the mesh builder which side this point is on (always, so triangulation can keep
		// terraces apart even at notch edges) and how wide the drop is (it only moves vertices onto sharp drops).
		var gradLength = MathF.Sqrt( (gx * gx) + (gy * gy) );
		if ( Math.Abs( d ) < 8f && gradLength > 0.01f )
		{
			info = new TerrainBiomeLedgeInfo
			{
				Present = true,
				DistanceMeters = d,
				DirectionX = gx / gradLength,
				DirectionY = gy / gradLength,
				HalfFaceWidthMeters = width * 0.5f,
			};
		}

		var t = Math.Clamp( (d / width) + 0.5f, 0f, 1f );
		var profile = (0.3f * SmoothStep01( t )) + (0.7f * t);
		return (k + profile - f) * height;
	}

	/// <summary>
	/// 1 inside a blend stretch (the ledge melts into plain hillside), 0 outside. Slow noise thresholded by
	/// coverage with soft edges, so the stretches are LedgeBlendLength-sized patches along the ledge lines.
	/// </summary>
	static float SampleRedwoodBlendMask01( TerrainPreviewSettings settings, int seed, float worldX, float worldY )
	{
		var coverage = Math.Clamp( settings.BiomeRedwoodLedgeBlendCoverage01, 0f, 1f );
		if ( coverage <= 0.001f )
			return 0f;

		var wave = Math.Clamp( settings.BiomeRedwoodLedgeBlendLengthMeters, 20f, 600f );
		var n = TerrainPreviewNoise.Fbm( seed + 712, worldX / wave, worldY / wave, 3 );
		// Three-octave FBM sits in roughly 0.2–0.8; coverage maps to a threshold inside that band. The wide 0.25
		// transition (~40 % of the wavelength, ~50 m at default) is what keeps the fade-out bank gentle.
		var threshold = 0.8f - (coverage * 0.6f);
		return SmoothStep01( Math.Clamp( ((n - threshold) / 0.25f) + 0.5f, 0f, 1f ) );
	}

	/// <summary>
	/// 1 inside a ramp street, 0 outside. Two perpendicular street families on a seed-rotated, gently
	/// warped grid: whatever direction a ledge line runs, it meets a street within ~1.4× spacing of
	/// travel (warp is capped at 0.2× spacing so that bound holds). Each street segment gets its own
	/// width (0.6–1.4×) so the walk-ups read as varied notches rather than a dashed line.
	/// </summary>
	static float SampleRedwoodRampMask01( TerrainPreviewSettings settings, int seed, float worldX, float worldY, float notchScale, float ledgeHeightMeters )
	{
		// The street grid is FIXED in world space. It used to be scaled by the (height-dependent) notch scale,
		// which slid the whole pattern past any point far from the origin as the ledge height drifted — the mask
		// flipped 0→1→0 within half a metre and every tall ledge grew spikes (found with terrain_probe, 2026-10-07).
		var spacing = Math.Clamp( settings.BiomeRedwoodLedgeRampSpacingMeters, 10f, 120f );
		var width = Math.Clamp( settings.BiomeRedwoodLedgeRampWidthMeters * notchScale, 2f, spacing * 0.8f );
		var angle = TerrainPreviewNoise.Hash01( seed + 720, 3, 7 ) * MathF.PI;
		var c = MathF.Cos( angle );
		var s = MathF.Sin( angle );
		var sx = (worldX * c) - (worldY * s);
		var sy = (worldX * s) + (worldY * c);
		var warpAmp = spacing * 0.2f;
		var warpScale = spacing * 2.5f;
		sx += (TerrainPreviewNoise.Fbm( seed + 721, worldX / warpScale, worldY / warpScale, 2 ) - 0.5f) * 2f * warpAmp;
		sy += (TerrainPreviewNoise.Fbm( seed + 722, worldX / warpScale, worldY / warpScale, 2 ) - 0.5f) * 2f * warpAmp;

		var cellX = (int)MathF.Floor( sx / spacing );
		var cellY = (int)MathF.Floor( sy / spacing );
		// Each street's width varies per cell, blended smoothly ALONG the street between neighbouring cells. A
		// hard per-cell width made the notch mask jump at every cell boundary — a wall across the notch and
		// alternating vertices (the "spiky" ledge stretches, 2026-10-07).
		var ty = SmoothStep01( (sy / spacing) - cellY );
		var tx = SmoothStep01( (sx / spacing) - cellX );
		var widthX = width * (0.6f + (0.8f * Lerp( TerrainPreviewNoise.Hash01( seed + 723, cellX, cellY ), TerrainPreviewNoise.Hash01( seed + 723, cellX, cellY + 1 ), ty )));
		var widthY = width * (0.6f + (0.8f * Lerp( TerrainPreviewNoise.Hash01( seed + 724, cellX, cellY ), TerrainPreviewNoise.Hash01( seed + 724, cellX + 1, cellY ), tx )));
		// Notch banks run at least half the ledge height so the sides stay ~50° gullies on tall ledges too.
		var bank = Math.Max( width * 0.35f, ledgeHeightMeters * 0.5f );
		return Math.Max( RedwoodStreetProfile( sx, spacing, widthX, bank ), RedwoodStreetProfile( sy, spacing, widthY, bank ) );
	}

	/// <summary>Flat 1 over the inner 30 % of a street, smooth to 0 over <paramref name="bankMeters"/> each side (gully banks, not walls).</summary>
	static float RedwoodStreetProfile( float coord, float spacing, float width, float bankMeters )
	{
		var u = coord / spacing;
		u -= MathF.Floor( u );
		var dist = Math.Abs( u - 0.5f ) * spacing;
		var t = 1f - Math.Clamp( (dist - (width * 0.15f)) / Math.Max( 1f, bankMeters ), 0f, 1f );
		return SmoothStep01( t );
	}

	static float SmoothStep01( float t ) => t * t * (3f - (2f * t));
}
