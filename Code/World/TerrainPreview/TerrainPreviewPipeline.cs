namespace Survival;

/// <summary>
/// Single orchestrator for terrain preview sampling. Stage order and contracts are documented in
/// <c>docs/TERRAIN_PREVIEW.md</c>.
/// </summary>
public static class TerrainPreviewPipeline
{
	public static TerrainPreviewSample Sample(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
		=> Sample( settings, worldXMeters, worldYMeters, applyRivers: true );

	/// <summary>Dry-land height before the river carve — what the river router walks downhill on.</summary>
	public static float SampleDryLandHeightMetersNoRivers( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var sample = Sample( settings, worldXMeters, worldYMeters, applyRivers: false );
		return sample.IsInsideWorld ? sample.HeightMeters : settings.SeaLevelMeters;
	}

	/// <summary>
	/// The ground as the dry stages build it with every lake ignored (no lake floor, no shore bank, no lake drain): what a
	/// lake on a mountain top would sit on. For <see cref="TerrainPreviewMountainLakes"/>, which runs before the lake
	/// fields exist. Sea level outside the land.
	/// </summary>
	public static float SampleGroundIgnoringWater( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var distMeters = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters) );
		if ( settings.LandRadiusMeters <= 0f || distMeters >= settings.LandRadiusMeters )
			return settings.SeaLevelMeters;
		var dry = SampleDryStages( settings, worldXMeters, worldYMeters, distMeters, includeLakeDrain: false );
		return dry.HeightMeters;
	}

	/// <summary>How far up a mountain a point is: the profile's rise × the height-influence blend — 0 off the mountain, 1 on the top.</summary>
	public static float SampleMountainRise01( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		var distMeters = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters) );
		if ( settings.LandRadiusMeters <= 0f || distMeters >= settings.LandRadiusMeters )
			return 0f;
		var dry = SampleDryStages( settings, worldXMeters, worldYMeters, distMeters, includeLakeDrain: false );
		return Math.Clamp( dry.Mountain.Rise01, 0f, 1f ) * dry.MountainBlend01;
	}

	/// <summary>Everything up to and including the coastal smoothing — the land before any water touches it.</summary>
	readonly struct DryStages
	{
		public TerrainPreviewBaseHeight.Layers Base { get; init; }
		public TerrainPreviewBiomeResolver.LandBiomeWeights Placement { get; init; }
		public TerrainPreviewBiomeResolver.LandBiomeWeights Display { get; init; }
		public float LowlandShaped01 { get; init; }
		public TerrainBiomeLedgeInfo Ledge { get; init; }
		public float MountainInfluence01 { get; init; }
		/// <summary>SmoothStep of the influence: how much of the mountain profile the ground takes.</summary>
		public float MountainBlend01 { get; init; }
		public TerrainPreviewMountainHeight.Profile Mountain { get; init; }
		public float HeightMeters { get; init; }
	}

	static DryStages SampleDryStages( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float distMeters, bool includeLakeDrain )
	{
		var landRadius = settings.LandRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var nx = (worldXMeters + landRadius) / diameter;
		var ny = (worldYMeters + landRadius) / diameter;
		var seed = settings.WorldSeed;
		var maxHeight = Math.Max( 50f, settings.MaxTerrainHeightMeters );

		var baseLayers = TerrainPreviewBaseHeight.Sample( settings, worldXMeters, worldYMeters, nx, ny, seed );
		var heightAfterCurve = baseLayers.AfterCurve01;

		var placementWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.SamplePlacementWeights( settings, worldXMeters, worldYMeters, heightAfterCurve )
			: TerrainPreviewLandDiskFields.GetFilteredPlacementWeights( settings, worldXMeters, worldYMeters );

		// Mask / field / falloff / slope previews are editor PNG modes; TerrainPreviewGenerator computes them
		// directly. The runtime path only needs the height influence (the mask alone was ~60 % of a mountain sample).
		var mountainHeightInfluence = TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01( settings, worldXMeters, worldYMeters );

		// Height shaping keeps the raw continuous weights: the speck merge below only decides what a point
		// LOOKS like (colour, trees). Shaping with merged one-hot weights raised whole raster cells into
		// flat-topped blocks with vertical walls where a clover island became redwood (Mark 2026-10-07).
		var lowlandWeights = TerrainPreviewBiomeResolver.LandWeightsWithoutMountain( placementWeights );
		var displayWeights = settings.UseContinuousBiomePlacementAtSample
			? TerrainPreviewBiomeResolver.ApplySpeckMerge( settings, worldXMeters, worldYMeters, placementWeights )
			: placementWeights;
		var lowlandShaped01 = TerrainPreviewBiomeTerrainShaper.ApplyBlendedShape01(
			settings, heightAfterCurve, lowlandWeights, nx, ny, seed, maxHeight, mountainHeightInfluence, out var ledgeInfo );

		var mountainProfile = TerrainPreviewBiomeMountainPeaks.SampleProfile( settings, mountainHeightInfluence, distMeters, nx, ny, seed );
		var mountainBlend = TerrainPreviewLandHeightDisplay.MountainBlend01( mountainHeightInfluence );

		var heightMeters = TerrainPreviewLandHeightDisplay.ApplyDryLandMeters(
			settings, heightAfterCurve, lowlandShaped01, mountainProfile, mountainHeightInfluence, maxHeight );

		heightMeters = TerrainPreviewCoastalSmoothing.ApplyMeters(
			settings, heightMeters, worldXMeters, worldYMeters, distMeters, nx, ny, seed, mountainBlend, includeLakeDrain );

		heightMeters = TerrainPreviewLakeCombine.Apply( settings, heightMeters, isFilteredOpenWater: false ).HeightMeters;

		return new DryStages
		{
			Base = baseLayers,
			Placement = placementWeights,
			Display = displayWeights,
			LowlandShaped01 = lowlandShaped01,
			Ledge = ledgeInfo,
			MountainInfluence01 = mountainHeightInfluence,
			MountainBlend01 = mountainBlend,
			Mountain = mountainProfile,
			HeightMeters = heightMeters,
		};
	}

	static TerrainPreviewSample Sample(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		bool applyRivers )
	{
		var landRadius = settings.LandRadiusMeters;
		var totalRadius = settings.TotalWorldRadiusMeters;
		if ( landRadius <= 0f || totalRadius <= 0f )
			return default;

		var distMeters = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( distMeters > totalRadius )
			return default;

		// >= (not >): a sample exactly on the land edge took the land path and came back ~100 m high — a pillar on the
		// coast wherever a chunk vertex landed on the circle (x = ±20 000 m on the axes, 2026-10-22)
		if ( distMeters >= landRadius )
		{
			var ocean = SampleRimOcean( settings, worldXMeters, worldYMeters, distMeters );
			if ( !applyRivers || !settings.EnableRivers )
				return ocean;
			var weights = TerrainPreviewBiomeResolver.SamplePlacementWeights( settings, worldXMeters, worldYMeters, 0f );
			var floor = CarryRiverBed( settings, worldXMeters, worldYMeters, ocean.HeightMeters, weights );
			return floor < ocean.HeightMeters ? ocean with { HeightMeters = floor } : ocean;
		}

		var maxHeight = Math.Max( 50f, settings.MaxTerrainHeightMeters );

		// One cache verification per sample (the shore / coastal / water lookups below then skip theirs),
		// and it guarantees the lake grid below is built rather than falling back to per-sample lake noise.
		TerrainPreviewLandDiskFields.BeginSample( settings );

		var rawLakeMask = TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld(
			settings, worldXMeters, worldYMeters, out var cachedLakeMask )
			? cachedLakeMask
			: TerrainPreviewLakeMap.SampleMaskAtWorldMeters( settings, worldXMeters, worldYMeters, settings.WorldSeed );

		var dry = SampleDryStages( settings, worldXMeters, worldYMeters, distMeters, includeLakeDrain: true );
		var placementWeights = dry.Placement;
		var dryLandHeightMeters = dry.HeightMeters;

		// The water this point's shore belongs to: sea level, or a tarn's level on a mountain top
		// (TerrainPreviewMountainLakes — the nearest lake's level, so the bank, beach and wet band ease to it).
		var waterLevel = TerrainPreviewLandDiskFields.SampleWaterLevelMeters( settings, worldXMeters, worldYMeters );

		var submerged = TerrainPreviewLakeShoreHeight.IsSubmergedByLakeMask( settings, rawLakeMask, worldXMeters, worldYMeters );
		// Lakes are bowls (Mark 2026-10-22): under the water the bed IS the bowl, whatever the biome shaping did there —
		// min( ground, bowl ) kept Redwood's lower terraces under the lake, and their crease along the biome border
		// showed through the clear water as a straight line. At the shore threshold the bowl is the water level, where
		// the shore easing has already brought the land, so the two meet without a step.
		if ( submerged )
		{
			dryLandHeightMeters = LakeFloorMeters( settings, rawLakeMask, worldXMeters, worldYMeters, waterLevel );
			if ( applyRivers )
				dryLandHeightMeters = CarryRiverBed( settings, worldXMeters, worldYMeters, dryLandHeightMeters, placementWeights );
		}
		else
		{
			// Sea cliffs (TerrainPreviewSeaCliffs): on an ocean shore under a cliff stretch the bank is a wall, not a ramp
			var cliffGate = 0f;
			if ( settings.CoastCliffChance01 > 0f && dryLandHeightMeters > waterLevel + 3f )
			{
				var toWater = MetersToShore( settings, worldXMeters, worldYMeters, rawLakeMask, landRadius - distMeters );
				if ( toWater < ShoreBankMaxRunMeters && TerrainPreviewSeaCliffs.IsOceanShore( settings, worldXMeters, worldYMeters, toWater, landRadius - distMeters ) )
					cliffGate = TerrainPreviewSeaCliffs.Gate01( settings, worldXMeters, worldYMeters );
			}
			dryLandHeightMeters = ApplyShoreBank( settings, dryLandHeightMeters, worldXMeters, worldYMeters, rawLakeMask, landRadius - distMeters, waterLevel, cliffGate );
			dryLandHeightMeters = ApplyBeachSlope( settings, dryLandHeightMeters, worldXMeters, worldYMeters, rawLakeMask, landRadius - distMeters, waterLevel );
		}

		// Rivers (after lakes, before the sea clamp below): inside the channel the ground IS the sloped water
		// surface — flat across, falling along — and the bank blends from just above the water up to the land.
		var riverWater = false;
		var riverBank = false;   // the slide wall: ground, but no trees / scatter standing in the water's edge
		var riverWet = 0f;   // the wet band (Wet01): river slide wall here, lake / sea shores below
		var riverBeach = 0f; // the beach band (Beach01): river slide wall here, lake / sea shores below
		if ( applyRivers && !submerged && settings.EnableRivers )
		{
			// Every channel that reaches this point carves it, the lowest wins: at a confluence the nearest-river lookup
			// switched between the two rivers' profiles along a line through the junction — a wall out of the water
			var river = TerrainPreviewLandDiskFields.SampleRiver( settings, worldXMeters, worldYMeters );
			if ( river.Near )
			{
				Carve( river );
				var partner = TerrainPreviewLandDiskFields.SampleRiverPartner( settings, worldXMeters, worldYMeters, river );
				if ( partner.Near )
					Carve( partner );
				if ( riverWater )
					riverBank = false;
			}

			void Carve( TerrainPreviewRivers.Hit hit )
			{
				var level = Math.Max( settings.SeaLevelMeters, hit.Level );
				var slide = Math.Clamp( settings.RiverBankSlideWidthMeters, 0.5f, 30f );
				var rim = level + Math.Clamp( settings.RiverBankHeightMeters, 0f, 10f );
				if ( hit.Distance <= hit.HalfWidth )
				{
					// The bed: a parabola from the water's edge down to Bed Depth under the surface at the centre
					// (Mark 2026-10-07: "actually carve out the land, slope inward, not a hard drop"). The water
					// mesh fills it to the level. The depth is capped per biome (Mark 2026-10-21: Clover Hills at
					// most 4 m, other biomes gentle for now) on the continuous Clover weight, so the cap never
					// steps at a biome border.
					var total = placementWeights.Total;
					var cloverShare = total > 1e-4f ? Math.Clamp( placementWeights.Clover / total, 0f, 1f ) : 1f;
					var bedCap = settings.RiverBedMaxDepthOtherMeters + ((settings.RiverBedMaxDepthCloverMeters - settings.RiverBedMaxDepthOtherMeters) * cloverShare);
					var bedDepth = Math.Clamp( hit.BedDepthMeters, 0f, Math.Max( 0f, bedCap ) );
					var u = hit.Distance / Math.Max( 0.5f, hit.HalfWidth );
					var bed = level - (bedDepth * (1f - (u * u)));   // deeper in pools
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, bed );
					riverWater = true;
				}
				else if ( hit.Distance <= hit.HalfWidth + slide )
				{
					// The slide (Mark 2026-10-21: "a water-slide shape I can fill with water" — easing the land
					// straight down to the water's edge gave a 50 m dish with no channel): a curved wall from the
					// water's edge up to the rim, gentle at the water and steepest at the rim. Cut only.
					var t = (hit.Distance - hit.HalfWidth) / slide;
					var wall = level + ((rim - level) * t * t);
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, wall );
					riverBank = true;
					riverBeach = Math.Max( riverBeach, 1f - (t * t * (3f - (2f * t))) );
					// The wet band just above the waterline (Mark 2026-10-22): full at the edge, gone at the wet line height
					if ( settings.WetLineHeightMeters > 0f )
					{
						var above = Math.Clamp( (dryLandHeightMeters - level) / settings.WetLineHeightMeters, 0f, 1f );
						riverWet = Math.Max( riverWet, 1f - (above * above * (3f - (2f * above))) );
					}
				}
				else
				{
					// Cut only (never lift the ground into a levee): the bank is the ground eased down to the
					// rim over the bank width; land already lower than that stays as it is.
					var t = Math.Clamp( (hit.Distance - hit.HalfWidth - slide) / Math.Max( 0.5f, hit.BankWidth ), 0f, 1f );
					t = t * t * (3f - (2f * t));
					var eased = rim + ((dryLandHeightMeters - rim) * t);
					dryLandHeightMeters = Math.Min( dryLandHeightMeters, eased );
				}
			}
		}

		// Lake and sea shores: the wet sand just above the water (Mark 2026-10-22, with the sea surface). Dry land never
		// sits lower than the water + the dry-land margin, so the band starts there; only within a few tens of metres of
		// the water (<see cref="MetersToShore"/>), so low meadows inland stay dry.
		// The beach: land near a lake / sea shore blends from sand into the biome ground over Beach Width — a gradient
		// over several vertices, so the paint never switches between two neighbours (that drew the zigzag). Only
		// lowland near the water pays for the shore distance (the traced shoreline field).
		if ( !submerged && !riverWater && !riverBank )
		{
			var margin = Math.Max( 0f, settings.InlandDryLandSeaMarginMeters );
			var above = dryLandHeightMeters - waterLevel - margin;
			var beachWidth = Math.Max( 0f, settings.BeachWidthMeters );
			var reach = Math.Max( beachWidth, 25f );
			var nearWater = (landRadius - distMeters) < reach
				|| (settings.EnableInteriorWaterLayer && rawLakeMask > TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings ) - 0.15f);
			if ( nearWater && above < 3f )
			{
				var toWater = MetersToShore( settings, worldXMeters, worldYMeters, rawLakeMask, landRadius - distMeters );
				if ( beachWidth > 0f )
				{
					// …fading out between 2 and 3 m above the water, so the height gate above never cuts the band off
					var b = 1f - Math.Clamp( toWater / beachWidth, 0f, 1f );
					var high = Math.Clamp( above - 2f, 0f, 1f );
					riverBeach = Math.Max( riverBeach, b * b * (3f - (2f * b)) * (1f - high) );
				}
				if ( settings.WetLineHeightMeters > 0f && above < settings.WetLineHeightMeters * 3f )
				{
					// The wet band hugs the water, eased over ~6 m of distance and three wet-line heights: on a bank the wet
					// line height alone is crossed within one 2 m vertex, and the darkening drew the triangle grid as
					// teeth along the beach (2026-10-22)
					var n = Math.Clamp( (toWater - 0.5f) / 6f, 0f, 1f );
					var near = 1f - (n * n * (3f - (2f * n)));
					var h = Math.Clamp( above / (settings.WetLineHeightMeters * 3f), 0f, 1f );
					riverWet = Math.Max( riverWet, near * (1f - (h * h * (3f - (2f * h)))) );
				}
			}
		}

		var height01 = Math.Clamp( dryLandHeightMeters / maxHeight, 0f, 1f );
		var sea = settings.SeaLevelMeters;

		return new TerrainPreviewSample
		{
			Height01 = height01,
			HeightMeters = dryLandHeightMeters,
			OceanHeight01 = submerged ? 1f : 0f,
			ContinentalNoise01 = settings.EnableContinentalLayer ? dry.Base.Continent01 : 0f,
			HillsNoise01 = settings.EnableHillLayer ? dry.Base.Hills01 : 0f,
			ValleysNoise01 = settings.EnableValleyLayer ? dry.Base.Valleys01 : 0f,
			HeightAfterCurve01 = dry.Base.AfterCurve01,
			HeightAfterBiomeShape01 = dry.LowlandShaped01,
			Ledge = submerged ? TerrainBiomeLedgeInfo.None : dry.Ledge,
			BiomeTransition01 = ComputeBiomeTransition01( dry.Display ),
			LakeMask01 = rawLakeMask,
			IsInsideWorld = true,
			IsOnLand = !submerged && !riverWater && !riverBank && dryLandHeightMeters > sea + 0.05f,
			RiverWater = riverWater,
			// Beds under water are wet too, so the wet colour is continuous across the waterline
			Wet01 = (submerged || riverWater) ? 1f : riverWet,
			Beach01 = riverBeach,
			HasLandWeights = true,
			LandWeights = dry.Display,
		};
	}

	static TerrainPreviewSample BuildOpenWaterSample(
		TerrainPreviewSettings settings,
		float maxHeight,
		float rawLakeMask,
		float floorMeters )
	{
		var floor01 = Math.Clamp( floorMeters / maxHeight, 0f, 1f );
		return new TerrainPreviewSample
		{
			Height01 = floor01,
			HeightMeters = floorMeters,
			OceanHeight01 = 1f,
			LakeMask01 = rawLakeMask,
			IsInsideWorld = true,
			IsOnLand = false,
			Wet01 = 1f,   // the open-water bed is wet, like the shore above it
		};
	}

	/// <summary>
	/// River mouths (Mark 2026-10-24: "a wall of height … clearly breaks them up, they need to blend together"): the lake
	/// and sea floors start at the waterline, a few centimetres deep, while the river bed reaching them is metres deep —
	/// a sill across every mouth. Under open water the river's channel carries on (the network runs into the water it
	/// feeds): the bed parabola at the river's level, min with the lake / sea floor, so the channel fades into the bowl
	/// as the bowl gets deeper. Same per-biome bed cap as on land, so there is no step at the shoreline.
	/// </summary>
	static float CarryRiverBed( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float floorMeters, TerrainPreviewBiomeResolver.LandBiomeWeights weights )
	{
		if ( !settings.EnableRivers )
			return floorMeters;
		var river = TerrainPreviewLandDiskFields.SampleRiver( settings, worldXMeters, worldYMeters );
		if ( !river.Near || river.Distance > river.HalfWidth )
			return floorMeters;
		var level = Math.Max( settings.SeaLevelMeters, river.Level );
		var total = weights.Total;
		var cloverShare = total > 1e-4f ? Math.Clamp( weights.Clover / total, 0f, 1f ) : 1f;
		var bedCap = settings.RiverBedMaxDepthOtherMeters + ((settings.RiverBedMaxDepthCloverMeters - settings.RiverBedMaxDepthOtherMeters) * cloverShare);
		var bedDepth = Math.Clamp( river.BedDepthMeters, 0f, Math.Max( 0f, bedCap ) );
		var u = river.Distance / Math.Max( 0.5f, river.HalfWidth );
		return Math.Min( floorMeters, level - (bedDepth * (1f - (u * u))) );
	}

	/// <summary>
	/// The rim ocean, carved (Mark 2026-10-22: "carve out the ocean … a cheap bowl"): the floor eases from the beach (sea
	/// level at the land edge) down to Ocean Max Depth over Ocean Shelf Width.
	/// </summary>
	static TerrainPreviewSample SampleRimOcean( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float distMeters )
	{
		return BuildOpenWaterSample( settings, Math.Max( 50f, settings.MaxTerrainHeightMeters ), 0f, settings.SeaLevelMeters - RimOceanDepthMeters( settings, worldXMeters, worldYMeters, distMeters, relief: true ) );
	}

	/// <summary>
	/// Depth (m) of the rim ocean at a point past the land edge: the radial profile off a coast (<see cref="OceanDepthMeters"/>),
	/// but never shallower than the sea inside the land disc is where this point's radius crosses the edge - an ocean
	/// that reaches the edge carries on outward at the depth it has there instead of rising to a ridge on the circle.
	/// </summary>
	public static float RimOceanDepthMeters( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float distMeters, bool relief )
	{
		var radial = OceanDepthMeters( settings, distMeters - settings.LandRadiusMeters );
		var pull = (settings.LandRadiusMeters - 1f) / Math.Max( 1f, distMeters );
		return Math.Max( radial, InteriorWaterDepthMeters( settings, worldXMeters * pull, worldYMeters * pull, relief ) );
	}

	/// <summary>Depth (m) of the lake / interior sea over a point inside the land disc; 0 on dry land.</summary>
	public static float InteriorWaterDepthMeters( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, bool relief )
	{
		if ( !settings.EnableInteriorWaterLayer || !TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, worldXMeters, worldYMeters, out var mask ) )
			return 0f;
		if ( !TerrainPreviewLakeShoreHeight.IsSubmergedByLakeMask( settings, mask, worldXMeters, worldYMeters ) )
			return 0f;
		return WaterDepthMeters( settings, worldXMeters, worldYMeters, MetersIntoLake( settings, worldXMeters, worldYMeters, mask ), relief );
	}

	/// <summary>
	/// Rim ocean depth (m) at a distance past the land edge: the continental shelf falls from the beach to Ocean Shelf
	/// Depth on <see cref="BowlProfile"/> over Ocean Shelf Width, then the drop-off falls to Ocean Max Depth over
	/// Ocean Drop-off Width on the same profile - steepest right at the shelf edge, levelling out in the deep (Mark 2026-11-18:
	/// "when it hits 200m it starts REALLY dropping off"; a smoothstep started flat and read as one long ramp). seawater.shader, water_common.hlsl and
	/// <see cref="SeaWaves"/> repeat it.
	/// </summary>
	public static float OceanDepthMeters( TerrainPreviewSettings settings, float metersPastLandEdge )
	{
		var shelfDepth = Math.Max( 0f, settings.OceanShelfDepthMeters );
		var maxDepth = Math.Max( shelfDepth, settings.OceanMaxDepthMeters );
		var shelfWidth = Math.Max( 1f, settings.OceanShelfWidthMeters );
		var shelf = shelfDepth * BowlProfile( Math.Clamp( metersPastLandEdge / shelfWidth, 0f, 1f ) );
		var d = Math.Clamp( (metersPastLandEdge - shelfWidth) / Math.Max( 1f, settings.OceanDropOffWidthMeters ), 0f, 1f );
		return shelf + ((maxDepth - shelfDepth) * BowlProfile( d ));
	}

	/// <summary>
	/// Depth profile of the lake / sea bowls, 0 at the shore … 1 at full depth: falls away from the waterline at once and
	/// levels out in the deep (t·(2−t)). A smoothstep was flat at the shore — for tens of metres the bed sat within
	/// centimetres of the water surface and the two z-fought in stripes (2026-10-22). seawater.shader and
	/// water_common.hlsl repeat it.
	/// </summary>
	public static float BowlProfile( float t ) => t * (2f - t);

	/// <summary>
	/// Beaches reach the water over this run (m): the dry-land margin over it is about 1:30, a real beach. Much flatter and
	/// the sand sits within centimetres of the water surface over metres and z-fights it.
	/// </summary>
	const float BeachRunMeters = 8f;

	/// <summary>Bank run per metre of height above the water (4 = about 1:4 at its steepest half, ~14°), and its limits.</summary>
	const float ShoreBankRunPerMeter = 4f;
	const float ShoreBankMaxRunMeters = 40f;

	/// <summary>
	/// Banks into lakes and the sea (Mark 2026-10-22: "terrain around the water tearing and splitting … triangles jutting
	/// out"): the floor is carved where the mask says water and the land beside it was left at its own height, so every
	/// shore above the water was a cliff — a 2.5 m drop between two 2 m vertices, drawn along the vertex grid as a
	/// staircase of fins. Land within a run of the shoreline (<see cref="MetersToShore"/>: the traced shoreline, the
	/// same contour the lake floor and the water use) now eases down to the water level at the waterline on a smoothstep.
	/// The distance is only looked up inside the mask band near a lake (or near the land edge), so the bank fades out
	/// across that band's outer edge instead of stopping at it. A mountain wall meeting a lake keeps its height to within
	/// the run of the shore — a cliff into the water, which is what a lake at a mountain's foot should be.
	/// </summary>
	static float ApplyShoreBank( TerrainPreviewSettings settings, float heightMeters, float worldXMeters, float worldYMeters, float rawLakeMask, float metersToLandEdge, float waterLevelMeters, float seaCliffGate01 )
	{
		var excess = heightMeters - waterLevelMeters;
		if ( excess <= 0f )
			return heightMeters;
		// Only the distance decides — it is smooth everywhere (the mask's shortfall over its gradient a raster cell
		// apart). Gating it on a band of the mask itself switched the bank on along a mask contour, and the mask is
		// bilinear on 40–150 m cells: straight-edged ridges and steps near every lake (Mark 2026-10-24: "square edges").
		// Under a sea cliff the run collapses to ~3 m: the headland drops into the sea as a wall (Mark 2026-10-30).
		var run = Math.Clamp( excess * ShoreBankRunPerMeter, BeachRunMeters, ShoreBankMaxRunMeters ) * (1f - (0.92f * Math.Clamp( seaCliffGate01, 0f, 1f )));
		if ( metersToLandEdge > run && (!settings.EnableInteriorWaterLayer || rawLakeMask <= 0f) )
			return heightMeters;
		var toWater = MetersToShore( settings, worldXMeters, worldYMeters, rawLakeMask, metersToLandEdge );
		if ( toWater >= run )
			return heightMeters;
		var t = Math.Clamp( toWater / run, 0f, 1f );
		return waterLevelMeters + (excess * t * t * (3f - (2f * t)));
	}

	/// <summary>
	/// Shores slope into the water (Mark 2026-10-22, with the sea surface): dry land is never lower than the water level +
	/// the dry-land margin (so inland lowlands do not flood under the one sea surface), which left a 0.25 m step at every
	/// shore. Within <see cref="BeachRunMeters"/> of open water (<see cref="MetersToShore"/>)
	/// that margin eases to zero at the waterline, where the lake / sea floor carries on down. Only land sitting on the
	/// margin is touched, so the distance lookup runs for lowlands alone.
	/// </summary>
	static float ApplyBeachSlope( TerrainPreviewSettings settings, float heightMeters, float worldXMeters, float worldYMeters, float rawLakeMask, float metersToLandEdge, float waterLevelMeters )
	{
		var margin = Math.Max( 0f, settings.InlandDryLandSeaMarginMeters );
		if ( margin <= 0f || heightMeters > waterLevelMeters + margin + 0.01f )
			return heightMeters;
		var toWater = MetersToShore( settings, worldXMeters, worldYMeters, rawLakeMask, metersToLandEdge );
		if ( toWater >= BeachRunMeters )
			return heightMeters;
		var t = Math.Clamp( toWater / BeachRunMeters, 0f, 1f );
		return Math.Min( heightMeters, waterLevelMeters + (margin * t) );
	}

	/// <summary>Dev (terrain_bank_probe): the shore-bank inputs at a point.</summary>
	public static string DebugShore( TerrainPreviewSettings settings, float x, float y )
	{
		TerrainPreviewLandDiskFields.TrySampleLakeMask01AtWorld( settings, x, y, out var mask );
		var threshold = TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings );
		var dist = MathF.Sqrt( (x * x) + (y * y) );
		var river = TerrainPreviewLandDiskFields.SampleRiver( settings, x, y );
		var partner = TerrainPreviewLandDiskFields.SampleRiverPartner( settings, x, y, river );
		var level = TerrainPreviewLandDiskFields.SampleWaterLevelMeters( settings, x, y );
		return $"mask {mask:0.0000} thr {threshold:0.0000} toShore {MetersToShore( settings, x, y, mask, settings.LandRadiusMeters - dist ):0.0} m, water level {level:0.00}, noRivers h {SampleDryLandHeightMetersNoRivers( settings, x, y ):0.00}, river near {river.Near} #{river.River} dist {river.Distance:0.0} half {river.HalfWidth:0.0} bank {river.BankWidth:0.0} level {river.Level:0.00}, partner {(partner.Near ? $"#{partner.River} dist {partner.Distance:0.0} half {partner.HalfWidth:0.0} level {partner.Level:0.00}" : "none")}, h {Sample( settings, x, y ).HeightMeters:0.00}";
	}

	/// <summary>
	/// Distance (m) from a dry point to the nearest shore: the land edge for the ocean, and for lakes the traced shoreline
	/// (<see cref="TerrainPreviewLakeShoreDistance"/>) — smooth and exactly on the shoreline the carve uses.
	/// </summary>
	static float MetersToShore( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float rawLakeMask, float metersToLandEdge )
	{
		var toShore = Math.Max( 0f, metersToLandEdge );
		if ( !settings.EnableInteriorWaterLayer )
			return toShore;
		// The traced shoreline (TerrainPreviewLakeShoreDistance), not the mask's shortfall over its gradient: that ratio
		// collapsed to 0 over whole patches where the mask lies flat at the threshold — the water-level squares
		return Math.Min( toShore, Math.Max( 0f, TerrainPreviewLakeShoreDistance.SignedMeters( worldXMeters, worldYMeters ) ) );
	}

	/// <summary>Lake floor under a submerged point: a bowl from the shore down to Lake Max Depth under the lake's water level toward the middle.</summary>
	static float LakeFloorMeters( TerrainPreviewSettings settings, float rawLakeMask, float worldXMeters, float worldYMeters, float waterLevelMeters )
		=> waterLevelMeters - WaterDepthMeters( settings, worldXMeters, worldYMeters, MetersIntoLake( settings, worldXMeters, worldYMeters, rawLakeMask ), relief: true );

	/// <summary>Metres of run the deep bed takes per metre it falls past the shelf (1.5: a 1000 m ocean reaches its floor ~1.5 km past the shelf).</summary>
	public const float DeepRunPerMeter = 1.5f;

	/// <summary>
	/// Depth (m) of the water inside the land disc at a distance in from the shore. Two kinds of water, one shape
	/// (Mark 2026-11-18): water "totally encircled with land" goes down to Lake Max Depth (50 m), an ocean - water joined
	/// to the rim, "between a biome and the caldera wall" - to Ocean Max Depth (1000 m)
	/// (<see cref="TerrainPreviewLandDiskFields.SampleOceanness01"/>). Both: a shelf to Lake Shelf Depth over Lake Shelf
	/// Width, then the bed tips down and keeps falling, <see cref="DeepRunPerMeter"/> of run per metre of depth (never
	/// less than Lake Drop-off Width), on <see cref="BowlProfile"/>.
	/// <para>
	/// With <paramref name="relief"/> (the carved bed) nothing about it is regular - "never a perfectly sloped piece of
	/// terrain": the distance is stretched and squeezed by a slow noise, so the drop starts nearer the shore here and
	/// further out there; the fall is terraced, steep runs between flatter benches; and reef relief - mounds, hollows and
	/// knolls a few metres high - covers the lot, scaled by the depth so the waterline stays where it is. Without it:
	/// the smooth shape, which sea_waves.hlsl repeats for the waves (the GPU never needs the bumps).
	/// </para>
	/// </summary>
	public static float WaterDepthMeters( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float metersFromShore, bool relief )
	{
		var lakeMax = Math.Max( 0f, settings.LakeMaxDepthMeters );
		var oceanMax = Math.Max( lakeMax, settings.OceanMaxDepthMeters );
		var ocean = Math.Clamp( TerrainPreviewLandDiskFields.SampleOceanness01( settings, worldXMeters, worldYMeters ), 0f, 1f );
		var maxDepth = lakeMax + ((oceanMax - lakeMax) * ocean * ocean * (3f - (2f * ocean)));
		var shelfDepth = Math.Clamp( settings.LakeShelfDepthMeters, 0f, maxDepth );
		var shelfWidth = Math.Max( 1f, settings.LakeShelfWidthMeters );
		var dropWidth = Math.Max( Math.Max( 1f, settings.LakeDropOffWidthMeters ), (maxDepth - shelfDepth) * DeepRunPerMeter );
		var seed = settings.WorldSeed;
		var from = Math.Max( 0f, metersFromShore );
		if ( relief )
			from *= 0.65f + (0.7f * TerrainPreviewNoise.Fbm( seed + 9101, worldXMeters / 420f, worldYMeters / 420f, 2 ));
		var d = Math.Clamp( (from - shelfWidth) / dropWidth, 0f, 1f );
		if ( relief && d > 0f )
		{
			// Benches: five along the fall, their phase drifting across the sea so no two stretches of coast step alike
			const float benches = 5f;
			var phase = MathF.Tau * ((d * benches) + (2f * TerrainPreviewNoise.Fbm( seed + 9203, worldXMeters / 900f, worldYMeters / 900f, 2 )));
			d = Math.Clamp( d + (0.75f * (MathF.Sin( phase ) - MathF.Sin( phase - (MathF.Tau * d * benches) )) / (MathF.Tau * benches)), 0f, 1f );
		}
		var depth = (shelfDepth * BowlProfile( Math.Clamp( from / shelfWidth, 0f, 1f ) )) + ((maxDepth - shelfDepth) * BowlProfile( d ));
		if ( !relief || depth <= 0f )
			return depth;
		// Reef relief: broad mounds and hollows, and knolls standing up off the bed. Never more than 30 % of the depth, so
		// nothing breaks the surface and the first metres off the beach stay a beach.
		var amp = Math.Min( depth * 0.3f, 6f );
		var mounds = (TerrainPreviewNoise.Fbm( seed + 9307, worldXMeters / 55f, worldYMeters / 55f, 3 ) - 0.5f) * 2f;
		var knolls = Math.Max( 0f, TerrainPreviewNoise.RidgedFbm( seed + 9411, worldXMeters / 28f, worldYMeters / 28f, 2 ) - 0.62f ) * 2.6f;
		// ...and the deep is country of its own: rises and basins hundreds of metres across, up to an eighth of the depth
		// (±125 m on the 1000 m floor), so the ocean bed is never a plain
		var swells = (TerrainPreviewNoise.Fbm( seed + 9517, worldXMeters / 700f, worldYMeters / 700f, 3 ) - 0.5f) * 2f * depth * 0.125f;
		return Math.Max( depth * 0.5f, depth + swells + (amp * ((mounds * 0.7f) - knolls)) );
	}

	/// <summary>
	/// Distance (m) in from a lake's shore: the traced shoreline's signed distance (<see cref="TerrainPreviewLakeShoreDistance"/>),
	/// 0 on dry land — the in-water twin of <see cref="MetersToShore"/>. seawater.shader reads the same field.
	/// </summary>
	public static float MetersIntoLake( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters, float rawLakeMask )
	{
		if ( rawLakeMask <= TerrainPreviewLandDiskFields.GetOpenWaterThreshold01( settings ) )
			return 0f;
		return Math.Max( 0f, -TerrainPreviewLakeShoreDistance.SignedMeters( worldXMeters, worldYMeters ) );
	}

	static float ComputeBiomeTransition01( TerrainPreviewBiomeResolver.LandBiomeWeights weights )
	{
		var total = weights.Total;
		if ( total <= 0.0001f )
			return 0f;

		var maxW = Math.Max(
			Math.Max( weights.Clover, weights.Redwood ),
			Math.Max( weights.Amber, weights.Mountain ) );
		return Math.Clamp( 1f - (maxW / total), 0f, 1f );
	}
}
