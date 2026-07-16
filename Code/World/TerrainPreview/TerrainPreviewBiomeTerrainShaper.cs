namespace Survival;

/// <summary>
/// Per-biome terrain personality — each biome sculpts base height rather than only capping at the end.
/// </summary>
static class TerrainPreviewBiomeTerrainShaper
{
	public static float ApplyBlendedShape01(
		TerrainPreviewSettings settings,
		float baseHeight01,
		TerrainPreviewBiomeResolver.LandBiomeWeights weights,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float blendedDetail01 )
	{
		blendedDetail01 = 0f;
		var total = weights.Total;
		if ( total <= 0.0001f )
			return baseHeight01;

		var sum = 0f;
		var detailSum = 0f;

		if ( weights.Clover > 0.0001f )
		{
			var shaped = ShapeCloverHills( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Clover * shaped;
			detailSum += weights.Clover * detail;
		}

		if ( weights.Redwood > 0.0001f )
		{
			var shaped = ShapeRedwoodForest( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Redwood * shaped;
			detailSum += weights.Redwood * detail;
		}

		if ( weights.Amber > 0.0001f )
		{
			var shaped = ShapeAmberDunes( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Amber * shaped;
			detailSum += weights.Amber * detail;
		}

		if ( weights.Mountain > 0.0001f )
		{
			var shaped = ShapeMountainBase( settings, baseHeight01, nx, ny, seed, maxTerrainHeightMeters, out var detail );
			sum += weights.Mountain * shaped;
			detailSum += weights.Mountain * detail;
		}

		blendedDetail01 = detailSum / total;
		return Math.Clamp( sum / total, 0f, 1f );
	}

	/// <summary>
	/// Voronoi hill cells (~400 m): peaks capped by clover max, then <b>surface grit after SoftCap</b>
	/// so micro is not clipped away on plateaus.
	/// </summary>
	static float ShapeCloverHills(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var diameter = Math.Max( 1f, settings.WorldDiameterMeters );
		var radius = diameter * 0.5f;
		// Match Pipeline: nx = (worldX + landRadius) / diameter → worldX = nx * diameter - radius
		var worldX = (nx * diameter) - radius;
		var worldY = (ny * diameter) - radius;
		var spacing = Math.Clamp( settings.BiomeCloverHillSpacingMeters, 250f, 400f );
		var gx = worldX / spacing;
		var gy = worldY / spacing;

		var cloverMaxM = Math.Clamp( settings.BiomeCloverMaxHeightMeters, 20f, 100f );
		var maxH = Math.Max( 50f, maxTerrainHeightMeters );
		var peakMin01 = Math.Clamp( settings.BiomeCloverPlateauHeightMin01, 0.15f, 0.85f );
		var peakMax01 = Math.Clamp( settings.BiomeCloverPlateauHeightMax01, peakMin01 + 0.05f, 1f );
		var plateauR = Math.Clamp( settings.BiomeCloverPlateauRadius01, 0.08f, 0.55f );
		var falloffR = Math.Clamp( settings.BiomeCloverHillFalloffRadius01, plateauR + 0.15f, 1.45f );
		var slopeSmooth = Math.Clamp( settings.BiomeCloverSlopeSmooth01, 0f, 1f );
		falloffR = Math.Min( 1.45f, falloffR + (slopeSmooth * 0.28f) );

		var hillMeters = SampleCloverVoronoiHills(
			seed + 600,
			gx,
			gy,
			cloverMaxM,
			peakMin01,
			peakMax01,
			Math.Clamp( settings.BiomeCloverGapFloorMin01, 0.05f, 0.5f ),
			Math.Clamp( settings.BiomeCloverGapFloorMax01, 0.08f, 0.55f ),
			Math.Clamp( settings.BiomeCloverHillDensity01, 0.15f, 1f ),
			plateauR,
			falloffR,
			slopeSmooth,
			out var cellContrast );

		var warpM = (TerrainPreviewNoise.Fbm( seed + 608, gx * 0.55f, gy * 0.55f, 3 ) - 0.5f)
			* cloverMaxM
			* Math.Clamp( settings.BiomeCloverHillWarpAmplitude01, 0.02f, 0.2f );
		hillMeters = Math.Clamp( hillMeters + warpM, 0f, cloverMaxM );

		var baseMeters = baseHeight01 * maxH;
		var blend = Math.Clamp( settings.BiomeCloverShapeBlend01, 0.25f, 1f );
		var shapedMeters = Lerp( baseMeters, hillMeters, blend );

		// SoftCap the hill body first — do NOT clamp grit into cloverMax (that erased bumps on peaks).
		var softMeters = TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shapedMeters, TerrainPreviewBiomeId.CloverHills, maxTerrainHeightMeters );

		var microWave = ResolveCloverMicroWavelengthMeters( settings.BiomeCloverMicroWavelengthMeters );
		var microAmp = ResolveCloverMicroAmplitudeMeters( settings.BiomeCloverMicroAmplitudeMeters );
		var gritMeters = SampleCloverSurfaceGritMeters( seed + 612, worldX, worldY, microWave, microAmp );
		var finalMeters = Math.Max( 0f, softMeters + gritMeters );

		detail01 = Math.Clamp( cellContrast * 0.35f + (Math.Abs( gritMeters ) / Math.Max( 0.5f, microAmp )), 0f, 1f );
		TryLogCloverMicroProbe( softMeters, gritMeters, microWave, microAmp, worldX, worldY );

		return finalMeters / maxH;
	}

	/// <summary>
	/// Choppy surface grit in world meters. Mix of smooth FBM + hash cells so bumps read on ~2 m verts.
	/// </summary>
	static float SampleCloverSurfaceGritMeters( int seed, float worldX, float worldY, float waveMeters, float ampMeters )
	{
		waveMeters = Math.Max( 2.5f, waveMeters );
		ampMeters = Math.Max( 0.5f, ampMeters );

		var fx = worldX / waveMeters;
		var fy = worldY / waveMeters;
		var fine = TerrainPreviewNoise.Fbm( seed, fx, fy, 4, 2.2f, 0.55f );
		var finer = TerrainPreviewNoise.Fbm( seed + 3, fx * 2.4f, fy * 2.4f, 3, 2.3f, 0.5f );

		// Sharp cell noise (less smoothed) — this is what you feel underfoot.
		var cx = (int)MathF.Floor( worldX / (waveMeters * 0.55f) );
		var cy = (int)MathF.Floor( worldY / (waveMeters * 0.55f) );
		var sharp = TerrainPreviewNoise.Hash01( seed + 9, cx, cy );
		var sharp2 = TerrainPreviewNoise.Hash01( seed + 11, cx / 2, cy / 2 );

		var signed = ((fine - 0.5f) * 2f * 0.4f)
			+ ((finer - 0.5f) * 2f * 0.35f)
			+ ((sharp - 0.5f) * 2f * 0.35f)
			+ ((sharp2 - 0.5f) * 2f * 0.2f);
		return signed * ampMeters;
	}

	static int _cloverMicroProbeLogged;

	static void TryLogCloverMicroProbe(
		float softMeters,
		float gritMeters,
		float waveMeters,
		float ampMeters,
		float worldX,
		float worldY )
	{
		if ( System.Threading.Interlocked.Exchange( ref _cloverMicroProbeLogged, 1 ) != 0 )
			return;

		Log.Info(
			$"[CloverMicro] probe wx={worldX:0.#} wy={worldY:0.#} soft={softMeters:0.##}m grit={gritMeters:0.##}m "
			+ $"(wave={waveMeters:0.#}m amp={ampMeters:0.#}m) — grit must be non-zero if bumps are live" );
	}

	static float SampleCloverVoronoiHills(
		int seed,
		float gx,
		float gy,
		float cloverMaxM,
		float peakMin01,
		float peakMax01,
		float gapMin01,
		float gapMax01,
		float hillDensity01,
		float plateauR,
		float falloffR,
		float slopeSmooth,
		out float cellContrast )
	{
		if ( gapMax01 < gapMin01 )
			(gapMin01, gapMax01) = (gapMax01, gapMin01);

		var x0 = (int)MathF.Floor( gx );
		var y0 = (int)MathF.Floor( gy );
		var best = 0f;
		var second = 0f;

		for ( var oy = -1; oy <= 1; oy++ )
		{
			for ( var ox = -1; ox <= 1; ox++ )
			{
				var cx = x0 + ox;
				var cy = y0 + oy;
				var px = cx + TerrainPreviewNoise.Hash01( seed + 19, cx, cy );
				var py = cy + TerrainPreviewNoise.Hash01( seed + 91, cx, cy );
				var dx = px - gx;
				var dy = py - gy;
				var d = MathF.Sqrt( (dx * dx) + (dy * dy) );

				var hash = TerrainPreviewNoise.Hash01( seed + 17, cx, cy );
				var role = TerrainPreviewNoise.Hash01( seed + 55, cx, cy );
				float peak01;
				float cellPlateauR;
				float cellFalloffR;
				if ( role < hillDensity01 )
				{
					// Big hill cell — broader tops so 400 m cells feel like large rolling rises.
					peak01 = Lerp( peakMin01, peakMax01, hash );
					var peakWarp = TerrainPreviewNoise.Hash01( seed + 44, cx, cy );
					peak01 = Math.Clamp( peak01 + ((peakWarp - 0.5f) * 0.08f), peakMin01 * 0.9f, peakMax01 );
					cellPlateauR = plateauR;
					cellFalloffR = falloffR;
				}
				else
				{
					// Skipped cell → low plateau / gap floor (not another competing hill).
					peak01 = Lerp( gapMin01, gapMax01, hash );
					cellPlateauR = Math.Min( 0.55f, plateauR + 0.18f );
					cellFalloffR = Math.Min( 1.35f, falloffR * 0.85f );
				}

				var contrib = CellHillProfile( d, cellPlateauR, cellFalloffR, slopeSmooth ) * peak01 * cloverMaxM;
				if ( contrib > best )
				{
					second = best;
					best = contrib;
				}
				else if ( contrib > second )
				{
					second = contrib;
				}
			}
		}

		// Soft winner — wider mix band so neighboring heights melt into saddles.
		var mixBand = cloverMaxM * Lerp( 0.22f, 0.42f, slopeSmooth );
		var blendW = Math.Clamp( (best - second) / Math.Max( 1f, mixBand ), 0f, 1f );
		var hill = Lerp( (best + second) * 0.5f, best, blendW );
		cellContrast = Math.Clamp( Math.Abs( best - second ) / Math.Max( 1f, cloverMaxM ), 0f, 1f );
		return Math.Clamp( hill, 0f, cloverMaxM );
	}

	/// <summary>
	/// Flat top → soft skirt. <paramref name="slopeSmooth"/> biases toward a flatter quintic ease
	/// so low→high across a cell doesn't read as a steep cone.
	/// </summary>
	static float CellHillProfile( float distanceCells, float plateauR, float falloffR, float slopeSmooth )
	{
		if ( distanceCells <= plateauR )
			return 1f;

		if ( distanceCells >= falloffR )
			return 0f;

		var u = (distanceCells - plateauR) / Math.Max( 0.001f, falloffR - plateauR );
		u = Math.Clamp( u, 0f, 1f );
		// Classic smoothstep vs flatter quintic (Perlin's improved fade).
		var cubic = u * u * (3f - (2f * u));
		var quintic = u * u * u * (u * ((u * 6f) - 15f) + 10f);
		var eased = Lerp( cubic, quintic, slopeSmooth );
		return 1f - eased;
	}

	static float ShapeRedwoodForest(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var hillFreq = Math.Clamp( settings.BiomeRedwoodHillFrequency, 1.5f, 14f );
		var hills = TerrainPreviewNoise.Fbm( seed + 610, nx * hillFreq, ny * hillFreq, 4 );
		var ridge = TerrainPreviewNoise.RidgedFbm( seed + 611, nx * hillFreq * 0.65f, ny * hillFreq * 0.65f, 3 );

		var hillAmp = Math.Clamp( settings.BiomeRedwoodHillAmplitude01, 0.03f, 0.22f );
		var ridgeAmp = Math.Clamp( settings.BiomeRedwoodRidgeAmplitude01, 0.01f, 0.12f );
		var shaped = baseHeight01 + ((hills - 0.5f) * hillAmp) + (ridge * ridgeAmp);

		detail01 = ridge * 0.65f + Math.Abs( hills - 0.5f );
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.RedwoodForest, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	static float ShapeAmberDunes(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var duneFreq = Math.Clamp( settings.BiomeAmberDuneFrequency, 0.75f, 8f );
		var warpX = nx + (TerrainPreviewNoise.Fbm( seed + 620, nx * 2f, ny * 2f, 2 ) - 0.5f) * 0.08f;
		var dune = TerrainPreviewNoise.Fbm( seed + 621, warpX * duneFreq, ny * duneFreq * 0.55f, 4 );
		var flow = TerrainPreviewNoise.Fbm( seed + 622, warpX * duneFreq * 0.35f, ny * duneFreq * 0.35f, 2 );

		var duneFloor = Math.Clamp( settings.BiomeAmberDuneFloor01, 0.05f, 0.45f );
		var duneAmp = Math.Clamp( settings.BiomeAmberDuneAmplitude01, 0.08f, 0.35f );
		var duneHeight = duneFloor + (dune * duneAmp);
		var blend = Math.Clamp( settings.BiomeAmberDuneReshapeBlend01, 0.35f, 0.95f );
		var shaped = Lerp( baseHeight01, duneHeight, blend ) + ((flow - 0.5f) * duneAmp * 0.12f);

		detail01 = Math.Abs( dune - flow ) * 0.35f;
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.AmberDunes, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	/// <summary>Pre-peak mountain biome base — ruggedness comes from mountain lift pass.</summary>
	static float ShapeMountainBase(
		TerrainPreviewSettings settings,
		float baseHeight01,
		float nx,
		float ny,
		int seed,
		float maxTerrainHeightMeters,
		out float detail01 )
	{
		var rugged = TerrainPreviewNoise.RidgedFbm( seed + 630, nx * 5f, ny * 5f, 3 );
		var lift = rugged * Math.Clamp( settings.BiomeMountainBaseRuggedAmplitude01, 0.02f, 0.14f );
		var shaped = baseHeight01 + lift;

		detail01 = rugged;
		return TerrainPreviewBiomeHeightCap.SoftCapForBiome(
			settings, shaped * maxTerrainHeightMeters, TerrainPreviewBiomeId.Mountain, maxTerrainHeightMeters )
			/ Math.Max( 50f, maxTerrainHeightMeters );
	}

	static float Lerp( float a, float b, float t ) => a + ((b - a) * t);

	/// <summary>
	/// Tuned preview JSON often omits micro fields → deserialize as 0. Treat invalid as loud defaults
	/// so grit is obvious without requiring a re-Generate.
	/// </summary>
	static float ResolveCloverMicroWavelengthMeters( float configured )
	{
		if ( configured < 2.5f || configured > 80f )
			return 4f;
		return configured;
	}

	static float ResolveCloverMicroAmplitudeMeters( float configured )
	{
		if ( configured < 0.4f || configured > 12f )
			return 6f;
		return configured;
	}
}
