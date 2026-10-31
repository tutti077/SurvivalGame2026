namespace Survival;

/// <summary>
/// The sea surface on the CPU — the same depth and Gerstner sum shaders/sea_waves.hlsl draws (Mark 2026-10-22: "waves
/// now, boat later"): a boat, a buoy or a swimmer asks <see cref="SurfaceHeightMeters"/> and rides the wave the player
/// sees. Keep every constant here in step with the include.
/// <para>
/// The sea is a small spectrum: 14 swells from ~17 to ~190 m and 10 chop waves at spread headings, random phases and
/// non-harmonic wavelengths, each rising and fading in its own wave groups (<see cref="GroupEnvelope"/>). The weather
/// (<see cref="Wind.Energy01"/>: the base strength plus the gusts, followed slowly) sets how tall the sea is AND which
/// wavelengths carry it by weighting the fixed components (<see cref="Spectrum"/>) — never by changing a wavelength, so
/// a change of weather never slides the pattern.
/// </para>
/// <para>
/// Rebuilt 2026-10-26 (the shore "bend" was a lerp of two phases — bands across the shelf, one parallel train, flat
/// surf): every swell's phase is k × S − ωt with S the travel distance from <see cref="SeaSwellTravelField"/>, so crests
/// refract toward the beach as one continuous line; each swell shoals on its own (linear shoaling coefficient), the set
/// is capped at the breaking height 0.39 × depth and the excess becomes the breaking amount (white water, sharper crests).
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
		internal float Energy { get; init; }
		internal float Setting { get; init; }
	}

	public const int SwellCount = 14;
	/// <summary>A crest may stand this fraction of the depth before it breaks (H/d 0.78 for the wave height).</summary>
	public const float BreakingRatio = 0.39f;

	// Heading off the wind (rad), wavelength (m), phase (rad), chop share (0 on the swells), then the wave group: envelope
	// heading off the wave's (rad), envelope length (m), phase — the shader's c_vSeaWaves / c_vSeaGroups. Seeded table
	// (2026-10-25): 14 swells 17–192 m and 10 chop 2–15 m, log-spread with jitter (no harmonics), spread headings.
	static readonly (float Angle, float Lambda, float Phase, float ChopShare, float GroupAngle, float GroupLength, float GroupPhase)[] Waves =
	{
		(0.061f, 17.03f, 3.054f, 0.00f, -1.464f, 222.1f, 5.520f),
		(0.402f, 21.79f, 0.055f, 0.00f, -2.837f, 218.2f, 0.362f),
		(-0.406f, 25.71f, 4.065f, 0.00f, 2.376f, 342.4f, 6.113f),
		(0.500f, 30.37f, 3.130f, 0.00f, 0.935f, 422.6f, 1.161f),
		(1.200f, 36.92f, 0.332f, 0.00f, -0.927f, 367.2f, 1.358f),
		(-0.997f, 43.66f, 3.119f, 0.00f, -0.351f, 454.7f, 4.986f),
		(0.285f, 55.61f, 2.435f, 0.00f, -0.059f, 527.5f, 1.087f),
		(-0.078f, 69.53f, 1.935f, 0.00f, 1.191f, 630.0f, 5.576f),
		(-0.333f, 82.04f, 0.440f, 0.00f, 2.953f, 1121.0f, 2.837f),
		(0.247f, 99.29f, 4.725f, 0.00f, -2.648f, 701.4f, 2.094f),
		(-0.245f, 109.09f, 4.500f, 0.00f, 2.867f, 1249.3f, 3.006f),
		(0.436f, 146.17f, 3.002f, 0.00f, 2.005f, 1311.0f, 5.245f),
		(-0.420f, 163.12f, 0.960f, 0.00f, -1.933f, 1378.3f, 4.677f),
		(0.613f, 191.73f, 0.978f, 0.00f, -1.494f, 2313.7f, 0.998f),
		(-0.677f, 2.09f, 0.382f, 0.06f, 1.290f, 24.3f, 2.461f),
		(-0.278f, 2.60f, 2.480f, 0.08f, 0.166f, 25.2f, 5.611f),
		(0.091f, 3.55f, 4.212f, 0.10f, -0.843f, 39.1f, 2.019f),
		(-0.719f, 4.36f, 2.844f, 0.12f, 1.474f, 49.5f, 5.527f),
		(-0.873f, 4.87f, 1.755f, 0.13f, -0.267f, 32.5f, 6.064f),
		(0.843f, 6.72f, 4.059f, 0.13f, 1.764f, 72.0f, 3.376f),
		(-1.052f, 7.94f, 3.728f, 0.12f, -0.515f, 101.5f, 5.212f),
		(-0.535f, 11.00f, 4.510f, 0.11f, -0.274f, 150.5f, 3.908f),
		(-1.089f, 13.07f, 0.502f, 0.09f, -0.855f, 147.5f, 4.050f),
		(0.067f, 14.75f, 0.509f, 0.06f, 0.500f, 114.3f, 0.993f),
	};
	/// <summary>Envelope peak over its mean: amplitudes are scaled up by this so the groups keep the sea's height.</summary>
	const float GroupBoost = 1.3f;
	/// <summary>Sum of the swells' crest sharpness (Gerstner Q·k·a) a full sea reaches — just under 1, a crest looping over.</summary>
	const float CrestBudget = 0.85f;
	/// <summary>The chop's crest sharpness cap per wave (the shader's SEA_CHOP_CREST).</summary>
	const float ChopCrest = 0.03f;
	/// <summary>Width of the spectrum in log-wavelength: wide enough that the 100–190 m swells carry real height offshore.</summary>
	const float SpectrumWidth = 0.9f;

	static SwellState _spectrumCache;

	/// <summary>
	/// The swell for this weather. Energy 0 = calm (0.3× the setting, peak ~35 m), 0.25 = the default sea (1×, peak
	/// ~70 m), 1 = storm (2.8×, peak ~160 m). Cached: the same inputs return the same instance (no per-frame allocation).
	/// </summary>
	public static SwellState Spectrum( Tuning tuning, float energy01 )
	{
		var b = Math.Clamp( energy01, 0f, 1f );
		var cached = _spectrumCache;
		if ( cached is not null && MathF.Abs( cached.Energy - b ) < 1e-4f && MathF.Abs( cached.Setting - tuning.SwellAmplitudeMeters ) < 1e-4f )
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
		return _spectrumCache = new SwellState
		{
			Amplitudes = weights,
			Crest = Math.Min( 1f, CrestBudget / Math.Max( kaSum, 1e-6f ) ),
			Total = total,
			Peak = strongest,
			Rms = MathF.Sqrt( squares ),
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
			depth = TerrainPreviewPipeline.OceanDepthMeters( settings, dist - settings.LandRadiusMeters );
		else
		{
			if ( !TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, xMeters, yMeters, out var mask ) )
				return 0f;
			depth = TerrainPreviewPipeline.LakeDepthMeters( settings, TerrainPreviewPipeline.MetersIntoLake( settings, xMeters, yMeters, mask ) );
		}
		// The ocean's swell runs all the way to the beach; lakes build swell only over deep water
		openness = dist > settings.LandRadiusMeters
			? 1f
			: SmoothStep( tuning.SwellMinDepthMeters, Math.Max( tuning.SwellMinDepthMeters + 0.1f, tuning.SwellFullDepthMeters ), depth );
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
	/// The Gerstner sum at an undisplaced point: height (m) and the sideways shift (m) — the shader's SeaWaveSum without
	/// the mesh fade. Swells: amplitude from the weather × the field's exposure × each wave's own shoaling, the set capped at
	/// <see cref="BreakingRatio"/> × depth; phase k × (dir·warped p + detour) − ωt; travel along dir + ∇S − wind.
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
		Span<float> amp = stackalloc float[SwellCount];
		var squares = 0f;
		for ( var w = 0; w < SwellCount; w++ )
		{
			var k = MathF.Tau / Waves[w].Lambda;
			amp[w] = sea.Amplitudes[w] * openness * exposure * Shoal( k, depth );
			squares += amp[w] * amp[w];
		}
		var crestEstimate = 2f * MathF.Sqrt( squares );
		var cap = (BreakingRatio * depth) + tuning.ShoreLapMeters;
		var scale = Math.Min( 1f, cap / Math.Max( crestEstimate, 1e-3f ) );
		var breaking = Math.Clamp( (crestEstimate / Math.Max( cap, 1e-3f )) - 1f, 0f, 1f );

		var z = 0f;
		for ( var w = 0; w < Waves.Length; w++ )
		{
			var (angle, lambda, phase0, share, _, _, _) = Waves[w];
			var s = MathF.Sin( angle );
			var c = MathF.Cos( angle );
			var k = MathF.Tau / lambda;
			var omega = MathF.Sqrt( 9.81f * k );
			Vector2 dir, travel;
			float a, basePhase, qka;
			if ( w < SwellCount )
			{
				dir = new Vector2( (swellWind.x * c) - (swellWind.y * s), (swellWind.x * s) + (swellWind.y * c) );
				a = amp[w] * scale * GroupEnvelope( w, p, dir, omega, k, time );
				basePhase = k * ((dir.x * pw.x) + (dir.y * pw.y) + detour);
				var t = dir + grad - swellWind;
				travel = t.LengthSquared > 1e-6f ? t.Normal : dir;
				qka = Math.Min( Lerp( sea.Crest * k * a, 0.4f, breaking * Math.Clamp( a / 0.2f, 0f, 1f ) ), 0.45f );
			}
			else
			{
				dir = new Vector2( (liveWind.x * c) - (liveWind.y * s), (liveWind.x * s) + (liveWind.y * c) );
				a = share * chop * GroupEnvelope( w, p, dir, omega, k, time );
				basePhase = k * ((dir.x * pw.x) + (dir.y * pw.y));
				travel = dir;
				qka = Math.Min( ChopCrest, 6f * k * a );
			}
			var phase = basePhase - (omega * time) + phase0;
			shift += travel * (qka / k * MathF.Cos( phase ));
			z += a * MathF.Sin( phase );
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
