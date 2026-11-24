namespace Survival;

/// <summary>
/// The sea surface on the CPU — the same depth and Gerstner sum shaders/sea_waves.hlsl draws (Mark 2026-10-22: "waves
/// now, boat later"): a boat, a buoy or a swimmer asks <see cref="SurfaceHeightMeters"/> and rides the wave the player
/// sees. Keep every constant here in step with the include.
/// <para>
/// The sea is a small spectrum: 14 swells from ~17 to ~190 m and 10 chop waves at spread headings, random phases and
/// non-harmonic wavelengths, each rising and fading in its own wave groups (<see cref="GroupEnvelope"/>). The weather
/// (<see cref="Wind.Energy01"/>: the base strength plus the gusts, followed slowly) sets how tall the sea is AND which
/// wavelengths carry it — swell and chop alike — by weighting the fixed components (<see cref="Spectrum"/>), never by
/// changing a wavelength, so a change of weather never slides the pattern.
/// </para>
/// <para>
/// Rebuilt 2026-10-26 (the shore "bend" was a lerp of two phases — bands across the shelf, one parallel train, flat
/// surf): every swell's phase is k × S − ωt with S the travel distance from <see cref="SeaSwellTravelField"/>, so crests
/// refract toward the beach as one continuous line; each swell shoals on its own (linear shoaling coefficient), the set
/// is capped at the breaking height 0.39 × depth and the excess becomes the breaking amount (white water, sharper crests).
/// </para>
/// <para>
/// 2026-11-19 (Mark: "baseline hardcoded water wiggle … the waves crashing on the shore look really bad"): as the set
/// nears the breaking height every swell leans forward (second and third harmonic, <see cref="Lean"/>) into a bore; the
/// sideways (Gerstner) motion is budgeted against the crests' real spacing in the shallows, so the surface never loops
/// through itself; the chop's wavelengths follow the weather like the swell's.
/// </para>
/// </summary>
public static class SeaWaves
{
	/// <summary>Designer wave knobs (TerrainWorldManager "Sea" group), published to the shader.</summary>
	public readonly struct Tuning
	{
		public float SwellAmplitudeMeters { get; init; }
		public float ChopAmplitudeMeters { get; init; }
		public float LakeChopShare { get; init; }
		public float ShoreLapMeters { get; init; }
		public float SwellMinDepthMeters { get; init; }
		public float SwellFullDepthMeters { get; init; }
	}

	/// <summary>The wind the waves follow (WindSystem: direction, base strength, current gust) and the sea's slow energy.</summary>
	public readonly struct Wind
	{
		public Vector2 Direction { get; init; }
		public float BaseStrength01 { get; init; }
		public float Gust01 { get; init; }
		/// <summary>What the swell is built from: base + gusts, followed over tens of seconds (TerrainWorldManager.SeaWater).</summary>
		public float Energy01 { get; init; }
	}

	/// <summary>The swell amplitudes for this weather (m, one per swell component), the crest factor Q and their sum.</summary>
	public sealed class SwellState
	{
		public float[] Amplitudes { get; init; }
		public float Crest { get; init; }
		public float Total { get; init; }
		/// <summary>The swell carrying the most of the sea: its crests time the laps on the ocean's beaches.</summary>
		public int Peak { get; init; }
		/// <summary>Root-mean-square of the swell amplitudes (m).</summary>
		public float Rms { get; init; }
		/// <summary>The share of the chop amplitude each of the 10 chop waves carries in this weather (sum 1).</summary>
		public float[] ChopShares { get; init; }
		/// <summary>The chop's crest factor Q (its sharpness stays inside <see cref="ChopCrestBudget"/> at full wind).</summary>
		public float ChopCrest { get; init; }
		/// <summary>The chop wave carrying the most (0..9): its crests time the small laps at every shore.</summary>
		public int ChopPeak { get; init; }
		internal float ChopSetting { get; init; }
		internal float Energy { get; init; }
		internal float Setting { get; init; }
	}

	public const int SwellCount = 14;
	public const int ChopCount = 10;
	/// <summary>A crest may stand this fraction of the depth before it breaks (H/d 0.78 for the wave height).</summary>
	public const float BreakingRatio = 0.39f;

	// Heading off the wind (rad), wavelength (m), phase (rad), then the wave group: envelope heading off the wave's
	// (rad), envelope length (m), phase — the shader's c_vSeaWaves / c_vSeaGroups. Seeded table (2026-10-25): 14 swells
	// 17–192 m and 10 chop 2–15 m, log-spread with jitter (no harmonics), spread headings.
	static readonly (float Angle, float Lambda, float Phase, float GroupAngle, float GroupLength, float GroupPhase)[] Waves =
	{
		(0.061f, 17.03f, 3.054f, -1.464f, 222.1f, 5.520f),
		(0.402f, 21.79f, 0.055f, -2.837f, 218.2f, 0.362f),
		(-0.406f, 25.71f, 4.065f, 2.376f, 342.4f, 6.113f),
		(0.500f, 30.37f, 3.130f, 0.935f, 422.6f, 1.161f),
		(1.200f, 36.92f, 0.332f, -0.927f, 367.2f, 1.358f),
		(-0.997f, 43.66f, 3.119f, -0.351f, 454.7f, 4.986f),
		(0.285f, 55.61f, 2.435f, -0.059f, 527.5f, 1.087f),
		(-0.078f, 69.53f, 1.935f, 1.191f, 630.0f, 5.576f),
		(-0.333f, 82.04f, 0.440f, 2.953f, 1121.0f, 2.837f),
		(0.247f, 99.29f, 4.725f, -2.648f, 701.4f, 2.094f),
		(-0.245f, 109.09f, 4.500f, 2.867f, 1249.3f, 3.006f),
		(0.436f, 146.17f, 3.002f, 2.005f, 1311.0f, 5.245f),
		(-0.420f, 163.12f, 0.960f, -1.933f, 1378.3f, 4.677f),
		(0.613f, 191.73f, 0.978f, -1.494f, 2313.7f, 0.998f),
		(-0.677f, 2.09f, 0.382f, 1.290f, 24.3f, 2.461f),
		(-0.278f, 2.60f, 2.480f, 0.166f, 25.2f, 5.611f),
		(0.091f, 3.55f, 4.212f, -0.843f, 39.1f, 2.019f),
		(-0.719f, 4.36f, 2.844f, 1.474f, 49.5f, 5.527f),
		(-0.873f, 4.87f, 1.755f, -0.267f, 32.5f, 6.064f),
		(0.843f, 6.72f, 4.059f, 1.764f, 72.0f, 3.376f),
		(-1.052f, 7.94f, 3.728f, -0.515f, 101.5f, 5.212f),
		(-0.535f, 11.00f, 4.510f, -0.274f, 150.5f, 3.908f),
		(-1.089f, 13.07f, 0.502f, -0.855f, 147.5f, 4.050f),
		(0.067f, 14.75f, 0.509f, 0.500f, 114.3f, 0.993f),
	};
	/// <summary>Envelope peak over its mean: amplitudes are scaled up by this so the groups keep the sea's height.</summary>
	const float GroupBoost = 1.3f;
	/// <summary>Sum of the swells' crest sharpness (Gerstner Q·k·a) at most — under 1, where a crest loops over (the shader's SEA_CREST_BUDGET).</summary>
	const float CrestBudget = 0.8f;
	/// <summary>Sum of the chop's crest sharpness at full wind.</summary>
	const float ChopCrestBudget = 0.3f;
	/// <summary>How far a swell at the breaking height leans forward: 0 = a sine, 1 = a sawtooth (the shader's SEA_LEAN).</summary>
	const float Lean = 0.9f;
	/// <summary>Width of the chop's spectrum in log-wavelength.</summary>
	const float ChopSpectrumWidth = 0.7f;
	/// <summary>Width of the spectrum in log-wavelength: wide enough that the 100–190 m swells carry real height offshore.</summary>
	const float SpectrumWidth = 0.9f;

	static SwellState _spectrumCache;

	/// <summary>
	/// The swell for this weather. Energy 0 = calm (0.3× the setting, peak ~35 m), 0.25 = the default sea (1×, peak
	/// ~70 m), 1 = storm (2.8×, peak ~160 m). The chop's shares peak at ~2.6 m in a calm, ~4 m in the default sea and ~9 m
	/// in a storm (a breeze raises short steep wavelets; it takes a blow to build long chop). Cached: the same inputs
	/// return the same instance (no per-frame allocation).
	/// </summary>
	public static SwellState Spectrum( Tuning tuning, float energy01 )
	{
		var b = Math.Clamp( energy01, 0f, 1f );
		var cached = _spectrumCache;
		if ( cached is not null && MathF.Abs( cached.Energy - b ) < 1e-4f && MathF.Abs( cached.Setting - tuning.SwellAmplitudeMeters ) < 1e-4f
			&& MathF.Abs( cached.ChopSetting - tuning.ChopAmplitudeMeters ) < 1e-4f )
			return cached;
		var height = b < 0.25f ? Lerp( 0.3f, 1f, b / 0.25f ) : 1f + ((b - 0.25f) / 0.75f * 1.8f);
		var peak = b < 0.25f ? Lerp( 35f, 70f, b / 0.25f ) : Lerp( 70f, 160f, (b - 0.25f) / 0.75f );
		var weights = new float[SwellCount];
		var sum = 0f;
		for ( var i = 0; i < SwellCount; i++ )
		{
			var x = MathF.Log( Waves[i].Lambda / peak ) / SpectrumWidth;
			weights[i] = MathF.Exp( -(x * x) );
			sum += weights[i];
		}
		var total = tuning.SwellAmplitudeMeters * height * 1.2f * GroupBoost;
		var kaSum = 0f;
		for ( var i = 0; i < SwellCount; i++ )
		{
			weights[i] = total * weights[i] / Math.Max( sum, 1e-6f );
			kaSum += MathF.Tau / Waves[i].Lambda * weights[i];
		}
		var strongest = 0;
		for ( var i = 1; i < SwellCount; i++ )
		{
			if ( weights[i] > weights[strongest] )
				strongest = i;
		}
		var squares = 0f;
		foreach ( var a in weights )
			squares += a * a;

		// The chop: shares of the chop amplitude round a peak wavelength that grows with the weather
		var chopPeakMeters = Lerp( 2.6f, 9f, b );
		var shares = new float[ChopCount];
		var shareSum = 0f;
		for ( var i = 0; i < ChopCount; i++ )
		{
			var x = MathF.Log( Waves[SwellCount + i].Lambda / chopPeakMeters ) / ChopSpectrumWidth;
			shares[i] = MathF.Exp( -(x * x) );
			shareSum += shares[i];
		}
		var chopKaSum = 0f;
		var chopStrongest = 0;
		for ( var i = 0; i < ChopCount; i++ )
		{
			shares[i] /= Math.Max( shareSum, 1e-6f );
			chopKaSum += MathF.Tau / Waves[SwellCount + i].Lambda * shares[i];
			if ( shares[i] > shares[chopStrongest] )
				chopStrongest = i;
		}
		// At full wind the chop stands 1.6 × the setting (WaveSum)
		var chopCrest = Math.Min( 1f, ChopCrestBudget / Math.Max( chopKaSum * tuning.ChopAmplitudeMeters * 1.6f, 1e-6f ) );
		return _spectrumCache = new SwellState
		{
			Amplitudes = weights,
			Crest = Math.Min( 1f, CrestBudget / Math.Max( kaSum, 1e-6f ) ),
			Total = total,
			Peak = strongest,
			Rms = MathF.Sqrt( squares ),
			ChopShares = shares,
			ChopCrest = chopCrest,
			ChopPeak = chopStrongest,
			ChopSetting = tuning.ChopAmplitudeMeters,
			Energy = b,
			Setting = tuning.SwellAmplitudeMeters,
		};
	}

	/// <summary>The warped position every wave's phase is taken at — the shader's SeaWarp (bends crests, no lattice).</summary>
	static Vector2 Warp( Vector2 p ) => p + new Vector2(
		(MathF.Sin( (p.y * 0.0571f) + 1.3f ) * 2.4f) + (MathF.Sin( ((p.x + p.y) * 0.0233f) + 4.1f ) * 1.6f),
		(MathF.Sin( (p.x * 0.0613f) + 0.7f ) * 2.4f) + (MathF.Sin( ((p.x - p.y) * 0.0197f) + 2.2f ) * 1.6f) );

	/// <summary>A wave's group envelope at a point and time, 0.15 … 1 — the shader's SeaGroupEnvelope.</summary>
	static float GroupEnvelope( int w, Vector2 p, Vector2 dir, float omega, float k, float time )
	{
		var g = Waves[w];
		var s = MathF.Sin( g.GroupAngle );
		var c = MathF.Cos( g.GroupAngle );
		var e = new Vector2( (dir.x * c) - (dir.y * s), (dir.x * s) + (dir.y * c) );
		var ke = MathF.Tau / g.GroupLength;
		var travel = 0.5f * (omega / k) * time * ((e.x * dir.x) + (e.y * dir.y));
		return 0.575f + (0.425f * MathF.Sin( (ke * (((e.x * p.x) + (e.y * p.y)) - travel)) + g.GroupPhase ));
	}

	/// <summary>Linear shoaling coefficient of a wave of wavenumber k over depth d (1 in deep water, growing as it shallows) — the shader's SeaShoal.</summary>
	static float Shoal( float k, float depth )
	{
		var kd = k * Math.Max( depth, 0.05f );
		if ( kd > 6f )
			return 1f;
		var th = MathF.Tanh( kd );
		var s2 = MathF.Sinh( 2f * kd );
		var n = 1f + ((2f * kd) / Math.Max( s2, 1e-4f ));
		return Math.Min( 1.5f, 1f / MathF.Sqrt( Math.Max( th * n, 1e-3f ) ) );
	}

	/// <summary>Water depth under a point (m) as the terrain carve made it — 0 on land; openness 0 shallow … 1 deep enough for swell.</summary>
	public static float DepthMeters( TerrainPreviewSettings settings, Tuning tuning, float xMeters, float yMeters, out float openness )
	{
		openness = 0f;
		var dist = MathF.Sqrt( (xMeters * xMeters) + (yMeters * yMeters) );
		float depth;
		if ( dist > settings.LandRadiusMeters )
			depth = TerrainPreviewPipeline.RimOceanDepthMeters( settings, xMeters, yMeters, dist, relief: false );
		else
		{
			if ( !TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, xMeters, yMeters, out var mask ) )
				return 0f;
			depth = TerrainPreviewPipeline.WaterDepthMeters( settings, xMeters, yMeters, TerrainPreviewPipeline.MetersIntoLake( settings, xMeters, yMeters, mask ), relief: false );
		}
		// The ocean's swell runs all the way to the beach; lakes build swell only over deep water. The beaches lie inside
		// the land disc, in water joined to the ocean: that water carries the swell too, fading out 0.6 - 2 km in from the
		// land edge (the shader's SeaDepthMetersOpen)
		if ( dist > settings.LandRadiusMeters )
			openness = 1f;
		else
		{
			var reach = Math.Clamp( TerrainPreviewLandDiskFields.SampleOceanness01( settings, xMeters, yMeters ), 0f, 1f )
				* SmoothStep( settings.LandRadiusMeters - 2000f, settings.LandRadiusMeters - 600f, dist );
			openness = Math.Max( SmoothStep( tuning.SwellMinDepthMeters, Math.Max( tuning.SwellMinDepthMeters + 0.1f, tuning.SwellFullDepthMeters ), depth ), reach );
		}
		return depth;
	}

	/// <summary>
	/// Height of the water surface (m, relative to Sea Level) at a point and time — what a floating body rides. Gerstner
	/// waves also move the water sideways, so the undisplaced point whose wave lands here is found first (three
	/// fixed-point steps), then its height taken. Time is the shader's SeaClock (Time.Now). Laps and the lake breakers are
	/// not included (centimetres, in the last metres of the shore).
	/// </summary>
	public static float SurfaceHeightMeters( TerrainPreviewSettings settings, Tuning tuning, Wind wind, float xMeters, float yMeters, float time )
	{
		var depth = DepthMeters( settings, tuning, xMeters, yMeters, out var openness );
		if ( depth <= 0f )
			return 0f;
		var sea = Spectrum( tuning, wind.Energy01 );
		var field = SeaSwellTravelField.Current;
		var target = new Vector2( xMeters, yMeters );
		var p = target;
		var z = 0f;
		for ( var it = 0; it < 4; it++ )
		{
			z = WaveSum( tuning, wind, sea, field, p, depth, openness, time, out var shift );
			if ( it == 3 )
				break;
			p = target - shift;
		}
		return z;
	}

	/// <summary>
	/// The wave sum at an undisplaced point: height (m) and the sideways shift (m) — the shader's SeaWaveSum without
	/// the mesh fade. Swells: amplitude from the weather × the field's exposure × each wave's own shoaling, the set capped at
	/// <see cref="BreakingRatio"/> × depth; phase k × (dir·warped p + detour) − ωt; travel along dir + ∇S − wind, whose
	/// length is how tightly the field packs that wave's crests here; a forward lean (second / third harmonic) as the set
	/// nears the breaking height.
	/// </summary>
	static float WaveSum( Tuning tuning, Wind wind, SwellState sea, SeaSwellTravelField.Field field, Vector2 p, float depth, float openness, float time, out Vector2 shift )
	{
		shift = Vector2.Zero;
		var liveWind = wind.Direction.LengthSquared > 1e-6f ? wind.Direction.Normal : new Vector2( 1f, 0f );
		var swellWind = field?.WindDirection ?? liveWind;
		var (detour, grad, exposure) = SeaSwellTravelField.Sample( field, swellWind, p.x, p.y );
		var pw = Warp( p );

		// Chop from the live wind, depth-faded in the shallows (the shore belongs to the swell running up)
		var windAmt = Math.Clamp( wind.BaseStrength01 + (wind.Gust01 * 0.5f), 0f, 1f );
		var chop = tuning.ChopAmplitudeMeters * Lerp( 0.3f, 1.6f, windAmt ) * Lerp( tuning.LakeChopShare, 1f, Math.Clamp( openness * 4f, 0f, 1f ) );
		chop *= Lerp( 0.15f, 1f, SmoothStep( 0.5f, 4f, depth ) );
		chop = Math.Min( chop, 0.3f * depth );

		// Each swell's amplitude here before the groups; the set's crest estimate against the breaking height
		// …and the set's steepness with the crests as close together as the travel field packs them here
		var index = Math.Max( 1f, grad.Length );
		Span<float> amp = stackalloc float[SwellCount];
		var squares = 0f;
		var kaSum = 0f;
		for ( var w = 0; w < SwellCount; w++ )
		{
			var k = MathF.Tau / Waves[w].Lambda;
			amp[w] = sea.Amplitudes[w] * openness * exposure * Shoal( k, depth );
			squares += amp[w] * amp[w];
			kaSum += k * amp[w];
		}
		var crestEstimate = 2f * MathF.Sqrt( squares );
		var cap = (BreakingRatio * depth) + tuning.ShoreLapMeters;
		var scale = Math.Min( 1f, cap / Math.Max( crestEstimate, 1e-3f ) );
		// 0 in deep water … 1 from where the set reaches the breaking height, all the way in
		var steep = Math.Clamp( crestEstimate / Math.Max( cap, 1e-3f ), 0f, 1f );
		// The crest factor: the weather's in deep water, the whole loop-free budget once the set breaks
		var budget = CrestBudget / Math.Max( kaSum * scale * index, 1e-4f );
		var crestQ = Math.Min( Lerp( sea.Crest, budget, SmoothStep( 0.6f, 1f, steep ) ), budget );
		// The lean grows through the surf zone, and only the long swells take it (the shader's SeaWaveSum)
		var lean = Lean * SmoothStep( 0.35f, 1f, steep ) * Lerp( 1f, 0.35f, SmoothStep( 1.5f, 6f, depth ) );

		var z = 0f;
		for ( var w = 0; w < Waves.Length; w++ )
		{
			var (angle, lambda, phase0, _, _, _) = Waves[w];
			var s = MathF.Sin( angle );
			var c = MathF.Cos( angle );
			var k = MathF.Tau / lambda;
			var omega = MathF.Sqrt( 9.81f * k );
			Vector2 dir, travel;
			// kl = the wave's wavenumber here (the field packs the crests), h2 / h3 = its second / third harmonic
			float a, basePhase, qka, kl, h2, h3;
			if ( w < SwellCount )
			{
				dir = new Vector2( (swellWind.x * c) - (swellWind.y * s), (swellWind.x * s) + (swellWind.y * c) );
				a = amp[w] * scale * GroupEnvelope( w, p, dir, omega, k, time );
				basePhase = k * ((dir.x * pw.x) + (dir.y * pw.y) + detour);
				var along = dir + grad - swellWind;
				var packed = Math.Max( along.Length, 0.5f );
				travel = along / packed;
				kl = k * packed;
				qka = crestQ * kl * a;
				var waveLean = lean * SmoothStep( 30f, 60f, lambda );
				h2 = 0.5f * waveLean;
				h3 = 0.33f * waveLean * waveLean;
			}
			else
			{
				dir = new Vector2( (liveWind.x * c) - (liveWind.y * s), (liveWind.x * s) + (liveWind.y * c) );
				a = sea.ChopShares[w - SwellCount] * chop * GroupEnvelope( w, p, dir, omega, k, time );
				basePhase = k * ((dir.x * pw.x) + (dir.y * pw.y));
				travel = dir;
				kl = k;
				qka = sea.ChopCrest * k * a;
				h2 = 0f;
				h3 = 0f;
			}
			var phase = basePhase - (omega * time) + phase0;
			var sp = MathF.Sin( phase );
			var cp = MathF.Cos( phase );
			shift += travel * (qka / kl * cp);
			// sin 2φ = 2 sp cp, sin 3φ = sp (3 − 4 sp²)
			z += a * (sp - (h2 * 2f * sp * cp) + (h3 * sp * (3f - (4f * sp * sp))));
		}
		return z;
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);

	static float SmoothStep( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / Math.Max( 1e-6f, b - a ), 0f, 1f );
		return t * t * (3f - (2f * t));
	}
}
