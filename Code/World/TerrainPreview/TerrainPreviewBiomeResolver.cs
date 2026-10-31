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

		return ResolveLandOverlay( settings, sample, worldXMeters, worldYMeters );
	}

	/// <summary>Land/azure/blackwater only — for display when the continuous lake mask says dry but the boolean raster says wet.</summary>
	public static Result ResolveLandOverlay(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !sample.IsInsideWorld )
			return default;

		var azureCoverage = TerrainPreviewAzureCoast.SampleCoverage01( settings, worldXMeters, worldYMeters );
		if ( azureCoverage > 0.5f )
		{
			return new Result
			{
				BiomeId = TerrainPreviewBiomeId.AzureCoast,
				Shade01 = azureCoverage,
			};
		}

		return ResolveUnderAzureCoast( settings, sample, worldXMeters, worldYMeters );
	}

	/// <summary>Land biome underneath azure — skips azure coast so colors can blend.</summary>
	public static Result ResolveUnderAzureCoast(
		TerrainPreviewSettings settings,
		TerrainPreviewSample sample,
		float worldXMeters,
		float worldYMeters )
	{
		if ( !sample.IsInsideWorld )
			return default;

		if ( TerrainPreviewLandDiskFields.IsBlackwater( settings, worldXMeters, worldYMeters ) )
			return new Result { BiomeId = TerrainPreviewBiomeId.Blackwater, Shade01 = 1f };

		var weights = sample.HasLandWeights
			? sample.LandWeights
			: SampleLandBiomeWeights( settings, sample, worldXMeters, worldYMeters );
		if ( QualifiesAsMountainBiome( weights ) )
			return new Result { BiomeId = TerrainPreviewBiomeId.Mountain, Shade01 = ShadeFromHeight( sample.Height01 ) };

		var landBiome = PickLandBiome( weights.Clover, weights.Redwood, weights.Amber );
		return new Result
		{
			BiomeId = landBiome,
			Shade01 = ShadeFromSample( settings, sample, worldXMeters, worldYMeters ),
		};
	}

	static bool QualifiesAsMountainBiome( LandBiomeWeights weights )
		=> weights.Mountain >= 0.5f;

	/// <summary>Land biome weights with mountain removed — used for lowland height before meter lerp.</summary>
	public static LandBiomeWeights LandWeightsWithoutMountain( LandBiomeWeights weights )
	{
		var landSum = weights.Clover + weights.Redwood + weights.Amber;
		if ( landSum <= 0.0001f )
			return new LandBiomeWeights { Clover = 1f };

		return new LandBiomeWeights
		{
			Clover = weights.Clover / landSum,
			Redwood = weights.Redwood / landSum,
			Amber = weights.Amber / landSum,
			Mountain = 0f,
		};
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
		return TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01(
			settings, worldXMeters, worldYMeters );
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

		// Spawn rules live in the region layout (GetRegionLayout: the site under spawn is Clover, sites inside
		// Amber Min Distance From Spawn are never Amber), so there is no distance band, ramp or boost here:
		// the old 150 m band + 900 m boost drew a round clover lobe on the map (Mark 2026-10-30: "very clearly
		// a circle"), and a hard-min ring for Amber would draw one too.
		var influenceScale = Math.Clamp( settings.BiomeDistanceInfluenceScale01, 0f, 1f );

		var cloverW = ComputeLandWeight(
			settings, distMeters, nx, ny, influenceScale,
			appearEndMeters: settings.BiomeCloverAppearEndMeters,
			settings.BiomeCloverDistanceInfluenceStartMeters,
			settings.BiomeCloverDistanceInfluenceEndMeters,
			settings.BiomeCloverWeight,
			settings.BiomeCloverPriorityWeight,
			530 );

		var redwoodW = ComputeLandWeight(
			settings, distMeters, nx, ny, influenceScale,
			appearEndMeters: settings.BiomeRedwoodAppearEndMeters,
			settings.BiomeRedwoodPriorityStartMeters,
			settings.BiomeRedwoodPriorityEndMeters,
			settings.BiomeRedwoodWeight,
			settings.BiomeRedwoodPriorityWeight,
			531 );

		var amberW = ComputeLandWeight(
			settings, distMeters, nx, ny, influenceScale,
			appearEndMeters: settings.BiomeAmberAppearEndMeters,
			settings.BiomeAmberPriorityStartMeters,
			settings.BiomeAmberPriorityEndMeters,
			settings.BiomeAmberWeight,
			settings.BiomeAmberPriorityWeight,
			532 );

		var mountainW = SampleMountainPlacementWeight(
			settings, distMeters, nx, ny, baseHeight01, worldXMeters, worldYMeters );
		// Never scale the lowland weights to exactly zero: the shaper normalises by their total, so a trace keeps the
		// neighbour biome's shape under the mountain. At zero it fell back to the raw base height, which is ~100 m
		// lower than the shaped lowland, and that step drew a moat around every mountain foot (Mark 2026-10-30:
		// "an indent at the start of the mountain ranges").
		var landScale = Math.Max( 0.001f, 1f - mountainW );
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

	/// <summary>
	/// Speck merge for what a point LOOKS like (Mark 2026-10-06): where the filtered raster merged a small
	/// island into the biome around it, the island's biome loses its weight so colour, trees and map show
	/// the surround. Height shaping must keep the raw weights (see the pipeline) or the merged cells turn
	/// into flat-topped blocks.
	/// </summary>
	public static LandBiomeWeights ApplySpeckMerge( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, LandBiomeWeights weights )
	{
		if ( !TerrainPreviewLandDiskFields.TryGetSpeckMerge( settings, worldXMeters, worldYMeters, out var speck, out var surround, out var strength ) )
			return SnapSubCellSpeckToRaster( settings, worldXMeters, worldYMeters, weights );

		// Feathered: the speck biome fades out and the surround fades in with the bilinear merge strength.
		var keep = 1f - strength;
		var cloverW = weights.Clover;
		var redwoodW = weights.Redwood;
		var amberW = weights.Amber;
		var mountainW = weights.Mountain;
		switch ( speck )
		{
			case TerrainPreviewBiomeId.CloverHills: cloverW *= keep; break;
			case TerrainPreviewBiomeId.RedwoodForest: redwoodW *= keep; break;
			case TerrainPreviewBiomeId.AmberDunes: amberW *= keep; break;
			case TerrainPreviewBiomeId.Mountain: mountainW = Math.Min( mountainW, MathX.Lerp( mountainW, 0.45f, strength ) ); break;
		}

		var floor = 0.6f * strength;
		switch ( surround )
		{
			case TerrainPreviewBiomeId.CloverHills: cloverW = Math.Max( cloverW, floor ); break;
			case TerrainPreviewBiomeId.RedwoodForest: redwoodW = Math.Max( redwoodW, floor ); break;
			case TerrainPreviewBiomeId.AmberDunes: amberW = Math.Max( amberW, floor ); break;
			case TerrainPreviewBiomeId.Mountain: mountainW = Math.Max( mountainW, 0.55f * strength ); break;
		}

		return new LandBiomeWeights { Clover = cloverW, Redwood = redwoodW, Amber = amberW, Mountain = mountainW };
	}

	/// <summary>
	/// Specks smaller than a raster cell: where the four cells around the point agree on a biome and the
	/// continuous pick says otherwise, the surround's weight is lifted to the pick's weight. The pick's own
	/// weight varies continuously across the speck, so the result stays smooth; at a real boundary the two
	/// weights are already close and the change is tiny.
	/// </summary>
	static LandBiomeWeights SnapSubCellSpeckToRaster( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, LandBiomeWeights weights )
	{
		var pick = PickDominantPlacementBiome( weights );
		if ( !TerrainPreviewLandDiskFields.TryGetUniformRasterBiome( settings, worldXMeters, worldYMeters, out var uniform ) || uniform == pick )
			return weights;

		var pickWeight = pick switch
		{
			TerrainPreviewBiomeId.CloverHills => weights.Clover,
			TerrainPreviewBiomeId.RedwoodForest => weights.Redwood,
			TerrainPreviewBiomeId.AmberDunes => weights.Amber,
			TerrainPreviewBiomeId.Mountain => weights.Mountain,
			_ => 0f,
		};
		var lift = pickWeight + 0.02f;
		return uniform switch
		{
			TerrainPreviewBiomeId.CloverHills => weights with { Clover = Math.Max( weights.Clover, lift ) },
			TerrainPreviewBiomeId.RedwoodForest => weights with { Redwood = Math.Max( weights.Redwood, lift ) },
			TerrainPreviewBiomeId.AmberDunes => weights with { Amber = Math.Max( weights.Amber, lift ) },
			TerrainPreviewBiomeId.Mountain => weights with { Mountain = Math.Max( weights.Mountain, 0.55f ) },
			_ => weights,
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

	/// <summary>Seeded region is primary; distance falloff is a capped soft nudge.</summary>
	static float ComputeLandWeight(
		TerrainPreviewSettings settings,
		float distMeters,
		float nx,
		float ny,
		float influenceScale,
		float appearEndMeters,
		float falloffStart,
		float falloffEnd,
		float scatterWeight,
		float distanceInfluence,
		int patchSeed )
	{
		var allow = BiomeAppearAllow01( settings, distMeters, appearEndMeters, nx, ny, patchSeed + 17 );
		if ( allow <= 0.0001f )
			return 0f;

		var patch = RegionPatch01( settings, nx, ny, patchSeed - 530 );
		var scatter = Math.Clamp( scatterWeight, 0f, 1f ) * patch;
		var falloff = DistanceFalloff01( distMeters, falloffStart, falloffEnd )
			* Math.Clamp( distanceInfluence, 0f, 1f )
			* influenceScale;

		var maxFalloff = (scatter * 0.6f) + 0.06f;
		falloff = Math.Min( falloff, maxFalloff );
		return allow * (scatter + falloff);
	}

	static float Smoothstep01( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	/// <summary>
	/// Seeded regions (Mark 2026-10-08: "biomes spanning many kilometres, at most 5 sections each"). The three
	/// land biomes share 3 × <c>Regions Per Biome</c> sites on a golden-angle spiral over the land disc (even
	/// spread, seeded jitter). Biomes are handed to sites greedily so each new site takes the biome whose
	/// existing sites are farthest away — same-biome sites never sit side by side, so the pieces stay
	/// separate and the count really is N per biome (a plain interleave merged neighbours into 2 blobs).
	/// A point belongs to its nearest site measured in a two-layer domain-warped frame (coarse swells plus
	/// fine wobble, <c>Region Border Warp (m)</c>) so the pieces are weird blobs rather than Voronoi polygons;
	/// the weight is 1 inside the piece and crosses 0.5 at the border over <c>Region Edge Blend (m)</c>.
	/// </summary>
	static float RegionPatch01( TerrainPreviewSettings settings, float nx, float ny, int biome )
	{
		var layout = GetRegionLayout( settings );
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		WarpRegionPoint( settings, nx, ny, out var wx, out var wy );

		var bestOwn = float.MaxValue;
		var bestOther = float.MaxValue;
		for ( var i = 0; i < layout.X.Length; i++ )
		{
			var dx = wx - layout.X[i];
			var dy = wy - layout.Y[i];
			var d = (dx * dx) + (dy * dy);
			if ( layout.Biome[i] == biome )
				bestOwn = Math.Min( bestOwn, d );
			else
				bestOther = Math.Min( bestOther, d );
		}

		bestOwn = MathF.Sqrt( bestOwn );
		bestOther = MathF.Sqrt( bestOther );
		var blend = Math.Max( 50f, settings.BiomeRegionEdgeBlendMeters ) / diameter;
		var t = (bestOther - bestOwn) / blend;   // > 0 inside this biome's region, 0 on the border
		return Smoothstep01( (t + 1f) * 0.5f );
	}

	static float RegionWarpN( TerrainPreviewSettings settings )
	{
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		return Math.Clamp( settings.BiomeRegionBorderWarpMeters, 0f, diameter * 0.25f ) / diameter;
	}

	/// <summary>The two-layer domain warp every region lookup goes through (coarse swells + fine wobble).</summary>
	static void WarpRegionPoint( TerrainPreviewSettings settings, float nx, float ny, out float wx, out float wy )
	{
		var seed = settings.WorldSeed;
		var warpN = RegionWarpN( settings );
		var coarseX = (TerrainPreviewNoise.Fbm( seed + 611, nx * 5f, ny * 5f, 3 ) - 0.5f) * 2f * warpN;
		var coarseY = (TerrainPreviewNoise.Fbm( seed + 612, nx * 5f, ny * 5f, 3 ) - 0.5f) * 2f * warpN;
		var fineX = (TerrainPreviewNoise.Fbm( seed + 617, nx * 18f, ny * 18f, 2 ) - 0.5f) * 0.5f * warpN;
		var fineY = (TerrainPreviewNoise.Fbm( seed + 618, nx * 18f, ny * 18f, 2 ) - 0.5f) * 0.5f * warpN;
		wx = nx + coarseX + fineX;
		wy = ny + coarseY + fineY;
	}

	sealed class RegionLayout
	{
		public int Seed;
		public int PerBiome;
		public float WarpN;
		public bool SpawnClover;
		public float AmberMinMeters;
		public float[] X = Array.Empty<float>();
		public float[] Y = Array.Empty<float>();
		public int[] Biome = Array.Empty<int>();
	}

	static RegionLayout _regionLayout;

	/// <summary>
	/// Site positions + biome assignment, built once per (seed, count, warp, guarantee); immutable so a stale
	/// read is still consistent. <c>Guarantee Clover At Spawn</c>: the innermost spiral site is moved onto
	/// the warped image of the world origin and handed Clover, so spawn sits in the middle of an ordinary
	/// irregular Clover region instead of under a stamped disc (Mark 2026-10-30: the old band was "very
	/// clearly a circle on the map"). The site is exactly where spawn lands in the warped frame, so spawn is
	/// never closer to another site than to its own. <c>Amber Min Distance From Spawn (m)</c>: sites closer
	/// than that to spawn are never Amber, so the pockets a new player walks through are Clover and Redwood
	/// and the desert starts further out — with ordinary irregular borders, not a ring (Mark 2026-10-30:
	/// "redwood forest is found before amber dunes").
	/// </summary>
	static RegionLayout GetRegionLayout( TerrainPreviewSettings settings )
	{
		var perBiome = Math.Clamp( settings.BiomeRegionsPerBiome, 1, 24 );
		var warpN = RegionWarpN( settings );
		var spawnClover = settings.BiomeCloverGuaranteeSpawn;
		var amberMin = Math.Max( 0f, settings.BiomeAmberHardMinDistanceMeters );
		var cached = _regionLayout;
		if ( cached is not null && cached.Seed == settings.WorldSeed && cached.PerBiome == perBiome
			&& cached.WarpN == warpN && cached.SpawnClover == spawnClover && cached.AmberMinMeters == amberMin )
			return cached;

		var seed = settings.WorldSeed;
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var total = perBiome * 3;
		var layout = new RegionLayout
		{
			Seed = seed, PerBiome = perBiome, WarpN = warpN, SpawnClover = spawnClover, AmberMinMeters = amberMin,
			X = new float[total], Y = new float[total], Biome = new int[total],
		};

		var rotation = TerrainPreviewNoise.Hash01( seed + 613, 1, 1 ) * MathF.PI * 2f;
		const float goldenAngle = 2.399963f;
		const float discRadius = 0.46f;   // land disc in normalised [0,1] map space
		for ( var i = 0; i < total; i++ )
		{
			var r = discRadius * MathF.Sqrt( (i + 0.5f) / total );
			var a = (i * goldenAngle) + rotation;
			layout.X[i] = 0.5f + (MathF.Cos( a ) * r) + ((TerrainPreviewNoise.Hash01( seed + 615, i, 3 ) - 0.5f) * 0.05f);
			layout.Y[i] = 0.5f + (MathF.Sin( a ) * r) + ((TerrainPreviewNoise.Hash01( seed + 616, i, 5 ) - 0.5f) * 0.05f);
			layout.Biome[i] = -1;
		}

		// Greedy spread: every site takes the biome (with quota left) whose already-placed sites are farthest.
		var quota = new[] { perBiome, perBiome, perBiome };
		var firstBiome = (int)(TerrainPreviewNoise.Hash01( seed + 614, 2, 2 ) * 3f) % 3;
		var first = 0;
		if ( spawnClover )
		{
			WarpRegionPoint( settings, 0.5f, 0.5f, out layout.X[0], out layout.Y[0] );
			layout.Biome[0] = 0;   // Clover (patch seed 530 - 530)
			quota[0]--;
			first = 1;
		}

		for ( var i = first; i < total; i++ )
		{
			var bestBiome = -1;
			var bestScore = float.MinValue;
			var dxC = layout.X[i] - 0.5f;
			var dyC = layout.Y[i] - 0.5f;
			var nearSpawn = MathF.Sqrt( (dxC * dxC) + (dyC * dyC) ) * diameter < amberMin;
			for ( var step = 0; step < 3; step++ )
			{
				var b = (firstBiome + step) % 3;
				if ( quota[b] <= 0 )
					continue;
				if ( b == 2 && nearSpawn )
					continue;   // Amber (patch seed 532 - 530) stays outside Amber Min Distance From Spawn

				var nearest = float.MaxValue;
				for ( var j = 0; j < i; j++ )
				{
					if ( layout.Biome[j] != b )
						continue;
					var dx = layout.X[i] - layout.X[j];
					var dy = layout.Y[i] - layout.Y[j];
					nearest = Math.Min( nearest, (dx * dx) + (dy * dy) );
				}

				if ( nearest > bestScore )
				{
					bestScore = nearest;
					bestBiome = b;
				}
			}

			layout.Biome[i] = bestBiome < 0 ? 0 : bestBiome;
			quota[layout.Biome[i]]--;
		}

		_regionLayout = layout;
		return layout;
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

	/// <summary>Outer fade at <paramref name="appearEndMeters"/> (the Map Edge Fade End knobs), warped ±400 m so it is not a ring.</summary>
	static float BiomeAppearAllow01(
		TerrainPreviewSettings settings,
		float distMeters,
		float appearEndMeters,
		float nx,
		float ny,
		int warpSeed )
	{
		appearEndMeters = Math.Max( 1f, appearEndMeters );
		var warp = TerrainPreviewNoise.Fbm( settings.WorldSeed + warpSeed, nx * 5.5f, ny * 5.5f, 2 );
		distMeters = Math.Max( 0f, distMeters + ((warp - 0.5f) * 2f * 400f) );
		if ( distMeters > appearEndMeters )
			return 0f;

		const float edgeFadeMeters = 75f;
		return Math.Clamp( (appearEndMeters - distMeters) / edgeFadeMeters, 0f, 1f );
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
