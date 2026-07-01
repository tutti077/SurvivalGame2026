namespace Survival;

/// <summary>Noise-patch land biomes with optional soft distance falloff and clover spawn guarantee.</summary>
public static class TerrainPreviewBiomeResolver
{
	public readonly struct LandBiomeWeights
	{
		public float Clover { get; init; }
		public float Redwood { get; init; }
		public float Amber { get; init; }
		public float Mountain { get; init; }

		public float Total => Clover + Redwood + Amber + Mountain;
	}

	public readonly struct Result
	{
		public TerrainPreviewBiomeId BiomeId { get; init; }
		public float Shade01 { get; init; }
	}

	public static Result Resolve(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !sample.IsInsideWorld )
			return default;

		if ( sample.OceanHeight01 > 0.5f )
			return new Result { BiomeId = TerrainPreviewBiomeId.Water, Shade01 = 1f };

		var weights = sample.HasLandWeights
			? sample.LandWeights
			: SampleLandBiomeWeights( settings, sample, worldXMeters, worldYMeters );
		if ( QualifiesAsMountainBiome( settings, sample, weights ) )
			return new Result { BiomeId = TerrainPreviewBiomeId.Mountain, Shade01 = ShadeFromHeight( sample.Height01 ) };

		var landBiome = PickLandBiome( weights.Clover, weights.Redwood, weights.Amber );
		return new Result
		{
			BiomeId = landBiome,
			Shade01 = ShadeFromSample( settings, sample, worldXMeters, worldYMeters ),
		};
	}

	/// <summary>Caps picker frequency so noise patches are not narrower than min patch diameter.</summary>
	public static float GetEffectivePatchFrequency( TerrainPreviewSettings settings )
	{
		var minPatch = Math.Max( 20f, settings.BiomeMinPatchDiameterMeters );
		var maxFrequency = settings.WorldDiameterMeters / minPatch;
		return Math.Min( Math.Max( 0.5f, settings.BiomePickerFrequency ), maxFrequency );
	}

	static bool QualifiesAsMountainBiome(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		LandBiomeWeights weights )
	{
		_ = settings;
		_ = sample;
		return weights.Mountain >= 0.5f;
	}

	/// <summary>Binary spawn mask — ridged range field inside falloff band.</summary>
	public static float SampleMountainSpawnMask01(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> TerrainPreviewMountainSpawnMask.SampleMask01( settings, worldXMeters, worldYMeters );

	static float SampleMountainPlacementWeight(
		TerrainPreviewSettings settings,
		float distMeters,
		float nx,
		float ny,
		float baseHeight01,
		float worldXMeters,
		float worldYMeters )
	{
		_ = distMeters;
		_ = nx;
		_ = ny;
		_ = baseHeight01;
		return SampleMountainSpawnMask01( settings, worldXMeters, worldYMeters );
	}

	/// <summary>Patch placement weights from scatter + distance — independent of peak lift (used before biome shaping).</summary>
	public static LandBiomeWeights SamplePlacementWeights(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		float baseHeight01 )
	{
		if ( settings.WorldRadiusMeters <= 0f )
			return default;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		var diameter = settings.WorldDiameterMeters;
		var radius = settings.WorldRadiusMeters;
		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;

		var cloverW = 0f;
		var redwoodW = 0f;
		var amberW = 0f;

		if ( settings.BiomeCloverGuaranteeSpawn
			&& IsInsidePriorityBand(
				distMeters,
				settings.BiomeCloverPriorityStartMeters,
				settings.BiomeCloverPriorityEndMeters ) )
		{
			cloverW = 1f;
		}
		else
		{
			var patchFreq = GetEffectivePatchFrequency( settings );
			var scatterOctaves = Math.Clamp( settings.BiomeScatterOctaves, 1, 6 );
			var influenceScale = Math.Clamp( settings.BiomeDistanceInfluenceScale01, 0f, 1f );
			var cloverRampStart = Math.Max( 0f, settings.BiomeCloverPriorityEndMeters );

			cloverW = ComputeLandWeight(
				settings, distMeters, nx, ny, patchFreq, scatterOctaves, influenceScale,
				hardMinMeters: 0f,
				rampStartMeters: cloverRampStart,
				rampFullMeters: settings.BiomeCloverRampFullDistanceMeters,
				appearEndMeters: settings.BiomeCloverAppearEndMeters,
				skipHardZero: true,
				settings.BiomeCloverDistanceInfluenceStartMeters,
				settings.BiomeCloverDistanceInfluenceEndMeters,
				settings.BiomeCloverWeight,
				settings.BiomeCloverPriorityWeight,
				530 );

			redwoodW = ComputeLandWeight(
				settings, distMeters, nx, ny, patchFreq, scatterOctaves, influenceScale,
				hardMinMeters: settings.BiomeRedwoodHardMinDistanceMeters,
				rampStartMeters: settings.BiomeRedwoodHardMinDistanceMeters,
				rampFullMeters: settings.BiomeRedwoodRampFullDistanceMeters,
				appearEndMeters: settings.BiomeRedwoodAppearEndMeters,
				skipHardZero: false,
				settings.BiomeRedwoodPriorityStartMeters,
				settings.BiomeRedwoodPriorityEndMeters,
				settings.BiomeRedwoodWeight,
				settings.BiomeRedwoodPriorityWeight,
				531 );

			amberW = ComputeLandWeight(
				settings, distMeters, nx, ny, patchFreq, scatterOctaves, influenceScale,
				hardMinMeters: settings.BiomeAmberHardMinDistanceMeters,
				rampStartMeters: settings.BiomeAmberHardMinDistanceMeters,
				rampFullMeters: settings.BiomeAmberRampFullDistanceMeters,
				appearEndMeters: settings.BiomeAmberAppearEndMeters,
				skipHardZero: false,
				settings.BiomeAmberPriorityStartMeters,
				settings.BiomeAmberPriorityEndMeters,
				settings.BiomeAmberWeight,
				settings.BiomeAmberPriorityWeight,
				532 );

			ApplySpawnBlend( settings, distMeters, ref cloverW, ref redwoodW, ref amberW );
		}

		var mountainW = SampleMountainPlacementWeight(
			settings, distMeters, nx, ny, baseHeight01, worldXMeters, worldYMeters );
		var landScale = 1f - mountainW;
		cloverW *= landScale;
		redwoodW *= landScale;
		amberW *= landScale;

		var landTotal = cloverW + redwoodW + amberW;
		if ( landTotal <= 0.0001f )
		{
			cloverW = 1f - mountainW;
			redwoodW = 0f;
			amberW = 0f;
		}

		return new LandBiomeWeights
		{
			Clover = cloverW,
			Redwood = redwoodW,
			Amber = amberW,
			Mountain = mountainW,
		};
	}

	/// <summary>Patch weights for land biomes + soft mountain influence (shared by color pick and height blend).</summary>
	public static LandBiomeWeights SampleLandBiomeWeights(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !sample.IsInsideWorld || sample.OceanHeight01 > 0.5f )
			return default;

		if ( sample.HasLandWeights )
			return sample.LandWeights;

		return SamplePlacementWeights( settings, worldXMeters, worldYMeters, sample.HeightAfterCurve01 );
	}

	static TerrainPreviewBiomeId PickLandBiome( float cloverW, float redwoodW, float amberW )
	{
		if ( cloverW >= redwoodW && cloverW >= amberW )
			return TerrainPreviewBiomeId.CloverHills;
		if ( redwoodW >= amberW )
			return TerrainPreviewBiomeId.RedwoodForest;
		return TerrainPreviewBiomeId.AmberDunes;
	}

	/// <summary>Dominant biome from placement weights (mountain when mountain weight ≥ 0.5).</summary>
	public static TerrainPreviewBiomeId PickDominantPlacementBiome( LandBiomeWeights weights )
	{
		if ( weights.Mountain >= 0.5f )
			return TerrainPreviewBiomeId.Mountain;

		return PickLandBiome( weights.Clover, weights.Redwood, weights.Amber );
	}

	/// <summary>One-hot weights after speck-filtered biome raster merge.</summary>
	public static LandBiomeWeights WeightsFromDominantBiome( TerrainPreviewBiomeId biomeId )
	{
		return biomeId switch
		{
			TerrainPreviewBiomeId.CloverHills => new LandBiomeWeights { Clover = 1f },
			TerrainPreviewBiomeId.RedwoodForest => new LandBiomeWeights { Redwood = 1f },
			TerrainPreviewBiomeId.AmberDunes => new LandBiomeWeights { Amber = 1f },
			TerrainPreviewBiomeId.Mountain => new LandBiomeWeights { Mountain = 1f },
			_ => new LandBiomeWeights { Clover = 1f },
		};
	}

	/// <summary>Noise scatter is primary; distance falloff is a capped soft nudge.</summary>
	static float ComputeLandWeight(
		TerrainPreviewSettings settings,
		float distMeters,
		float nx,
		float ny,
		float patchFrequency,
		int scatterOctaves,
		float influenceScale,
		float hardMinMeters,
		float rampStartMeters,
		float rampFullMeters,
		float appearEndMeters,
		bool skipHardZero,
		float falloffStart,
		float falloffEnd,
		float scatterWeight,
		float distanceInfluence,
		int patchSeed )
	{
		var allow = BiomeZoneAllow01(
			settings,
			distMeters,
			hardMinMeters,
			rampStartMeters,
			rampFullMeters,
			appearEndMeters,
			skipHardZero,
			nx,
			ny,
			patchSeed + 17 );
		if ( allow <= 0.0001f )
			return 0f;

		var patch = ScatterPatch01( settings, nx, ny, patchSeed, patchFrequency, scatterOctaves );
		var scatter = Math.Clamp( scatterWeight, 0f, 1f ) * patch;
		var falloff = DistanceFalloff01( distMeters, falloffStart, falloffEnd )
			* Math.Clamp( distanceInfluence, 0f, 1f )
			* influenceScale;

		var maxFalloff = (scatter * 0.6f) + 0.06f;
		falloff = Math.Min( falloff, maxFalloff );
		return allow * (scatter + falloff);
	}

	/// <summary>
	/// Soft transition after the guaranteed spawn band: clover stays common, other biomes ramp in gradually.
	/// </summary>
	static void ApplySpawnBlend(
		TerrainPreviewSettings settings,
		float distMeters,
		ref float cloverW,
		ref float redwoodW,
		ref float amberW )
	{
		var blendStart = Math.Max( 0f, settings.BiomeCloverPriorityEndMeters );
		var blendEnd = Math.Max( blendStart + 50f, settings.BiomeSpawnBlendEndMeters );
		if ( distMeters <= blendStart )
			return;

		var blendOut = SpawnBlendOut01( distMeters, blendStart, blendEnd );
		var otherScale = blendOut * blendOut;
		var cloverBoost = 1f + ((1f - blendOut) * Math.Clamp( settings.BiomeSpawnCloverBlendBoost01, 0f, 1f ));

		cloverW *= cloverBoost;
		redwoodW *= otherScale;
		amberW *= otherScale;
	}

	static float SpawnBlendOut01( float distMeters, float blendStart, float blendEnd )
	{
		if ( distMeters <= blendStart )
			return 0f;
		if ( distMeters >= blendEnd )
			return 1f;

		var t = (distMeters - blendStart) / (blendEnd - blendStart);
		return Smoothstep01( t );
	}

	static float Smoothstep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	static float ScatterPatch01(
		TerrainPreviewSettings settings,
		float nx,
		float ny,
		int seedOffset,
		float frequency,
		int octaves )
	{
		var n = TerrainPreviewNoise.Fbm( settings.WorldSeed + seedOffset, nx * frequency, ny * frequency, octaves );
		return Math.Clamp( (n - 0.32f) / 0.45f, 0f, 1f );
	}

	/// <summary>Smooth 0–1 bump peaking at the midpoint of the falloff band.</summary>
	static float DistanceFalloff01( float distMeters, float falloffStart, float falloffEnd )
	{
		falloffStart = Math.Max( 0f, falloffStart );
		falloffEnd = Math.Max( falloffStart + 1f, falloffEnd );
		var peak = (falloffStart + falloffEnd) * 0.5f;
		var sigma = Math.Max( (falloffEnd - falloffStart) * 0.55f, 400f );
		var d = distMeters - peak;
		return MathF.Exp( -(d * d) / (2f * sigma * sigma) );
	}

	static bool IsInsidePriorityBand( float distMeters, float priorityStart, float priorityEnd )
	{
		priorityStart = Math.Max( 0f, priorityStart );
		priorityEnd = Math.Max( priorityStart, priorityEnd );
		return distMeters >= priorityStart && distMeters < priorityEnd;
	}

	/// <summary>
	/// Hard zero inside <paramref name="hardMinMeters"/> (unless skipped), soft ramp to
	/// <paramref name="rampFullMeters"/>, optional outer fade at <paramref name="appearEndMeters"/>.
	/// </summary>
	static float BiomeZoneAllow01(
		TerrainPreviewSettings settings,
		float distMeters,
		float hardMinMeters,
		float rampStartMeters,
		float rampFullMeters,
		float appearEndMeters,
		bool skipHardZero,
		float nx,
		float ny,
		int warpSeed )
	{
		hardMinMeters = Math.Max( 0f, hardMinMeters );
		rampStartMeters = Math.Max( 0f, rampStartMeters );
		rampFullMeters = Math.Max( rampStartMeters + 1f, rampFullMeters );
		appearEndMeters = Math.Max( rampFullMeters + 1f, appearEndMeters );

		var zoneActive = !skipHardZero && hardMinMeters > 0.5f
			|| rampFullMeters > rampStartMeters + 1f;
		if ( zoneActive )
		{
			var warpScale = Math.Max( hardMinMeters, rampFullMeters - rampStartMeters );
			var warpMeters = Math.Max( 200f, warpScale * 0.2f );
			var warp = TerrainPreviewNoise.Fbm( settings.WorldSeed + warpSeed, nx * 5.5f, ny * 5.5f, 2 );
			distMeters = Math.Max( 0f, distMeters + ((warp - 0.5f) * 2f * warpMeters) );
		}

		if ( distMeters > appearEndMeters )
			return 0f;

		if ( !skipHardZero && hardMinMeters > 0.5f && distMeters < hardMinMeters )
			return 0f;

		var rampStart = skipHardZero ? rampStartMeters : hardMinMeters;
		float innerAllow;
		if ( distMeters >= rampFullMeters )
		{
			innerAllow = 1f;
		}
		else if ( distMeters <= rampStart + 0.001f )
		{
			innerAllow = 0f;
		}
		else
		{
			var t = (distMeters - rampStart) / (rampFullMeters - rampStart);
			var ramp = Smoothstep01( t );
			var power = Math.Clamp( settings.BiomeAppearInnerRampPower, 1f, 4f );
			innerAllow = MathF.Pow( ramp, power );
		}

		const float edgeFadeMeters = 75f;
		var span = appearEndMeters - rampFullMeters;
		var fade = Math.Min( edgeFadeMeters, span * 0.1f );
		if ( fade <= 1f )
			return innerAllow;

		var endFade = Math.Clamp( (appearEndMeters - distMeters) / fade, 0f, 1f );
		return innerAllow * endFade;
	}

	static float ShadeFromHeight( float height01 )
		=> Math.Clamp( 0.45f + (height01 * 0.55f), 0.35f, 1f );

	static float ShadeFromSample(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var nx = (worldXMeters + radius) / diameter;
		var ny = (worldYMeters + radius) / diameter;
		var detail = TerrainPreviewNoise.Fbm( settings.WorldSeed + 540, nx * settings.BiomeNoiseFrequency * 2.5f, ny * settings.BiomeNoiseFrequency * 2.5f, 2 );
		var height = ShadeFromHeight( sample.Height01 );
		return Math.Clamp( (height * 0.65f) + (detail * 0.35f), 0.35f, 1f );
	}
}
