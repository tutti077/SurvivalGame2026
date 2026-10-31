namespace Survival;

/// <summary>
/// Designer-facing knobs for the editor terrain noise preview.
/// Shared with future full-world generation via <see cref="TerrainPreviewBackendRegistry"/>.
/// Architecture: <c>docs/TERRAIN_PREVIEW.md</c>. Legacy JSON fields live in <c>TerrainPreviewSettings.Legacy.cs</c>.
/// </summary>
public sealed partial class TerrainPreviewSettings
{
	[Property, Title( "Land Diameter (m)" ), Range( 1000, 100000 ), Step( 500 ), Description( "Playable land disk — ocean ring sits outside this." )]
	public float WorldDiameterMeters { get; set; } = 40000f;

	[Property, Title( "Ocean Ring Width (m)" ), Range( 0f, 10000f ), Step( 250f ), Description( "Flat water band on each side; total world = land + 2× ring (default 25 km)." )]
	public float OceanRingWidthMeters { get; set; } = 2500f;

	[Property, Title( "Max Terrain Height (m)" ), Range( 50f, 10000f ), Step( 50f ), Description( "Mountain / peak ceiling in world meters." )]
	public float MaxTerrainHeightMeters { get; set; } = 1000f;

	[Property, Group( "World" ), Title( "Lowland Height Max (m)" ), Range( 20f, 2000f ), Step( 10f ), Description( "Non-mountain dry land scales sculpted base height to this ceiling (default 200 m). Mountains blend to Max Terrain Height." )]
	public float LandRollingHeightMaxMeters { get; set; } = 200f;


	[Property, Group( "World" ), Title( "Hill Wavelength (m)" ), Range( 80f, 5000f ), Step( 20f ), Description( "Rolling hill size in world meters (~400 m shows relief on 64 m chunks)." )]
	public float HillWavelengthMeters { get; set; } = 400f;

	[Property, Group( "World" ), Title( "Valley Wavelength (m)" ), Range( 80f, 8000f ), Step( 20f ), Description( "Valley carve size in world meters." )]
	public float ValleyWavelengthMeters { get; set; } = 550f;

	[Property, Group( "Biomes" ), Title( "Continuous Placement At Sample" ), Description( "Blobby biome patches from live noise (not square land-disk cells). Color still uses dominant biome with edge-only soften." )]
	public bool UseContinuousBiomePlacementAtSample { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Edge Color Blend (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "How much colors mix only at biome borders. 0 = hard patches; ~0.35 = soften square edges without blurring interiors." )]
	public float BiomeEdgeColorBlend01 { get; set; } = 0.35f;

	[Property, Group( "Biomes" ), Title( "Edge Blend Start (0–1)" ), Range( 0.05f, 0.5f ), Step( 0.02f ), Description( "Mixing begins when biome transition exceeds this (higher = narrower border blend)." )]
	public float BiomeEdgeBlendStart01 { get; set; } = 0.22f;

	[Property, Group( "Biomes" ), Title( "Azure Coast" ), Description( "Teal patches on dry land touching the OCEAN only — the rim ring and any water joined to it — never inland lakes (Mark 2026-10-29). Blobs scattered along the coast (Mark 2026-10-30: a kinda blob, connected to an ocean coast, not following the coastline perfectly)." )]
	public bool EnableAzureCoastBiome { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Azure Coast Inner Fade (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Centre of a soft fade in the blob chance: none inside (this − 0.15) × the world radius, full chance beyond (this + 0.15), judged at each blob's centre. Keeps bays reaching toward spawn mostly plain; no visible line (Mark 2026-10-29)." )]
	public float AzureCoastMinDistanceFromSpawn01 { get; set; } = 0.7f;

	[Property, Group( "Biomes" ), Title( "Azure Blob Spacing (m)" ), Range( 1000f, 10000f ), Step( 100f ), Description( "Cell size of the teal scatter: every cell may hold one patch. Only patches whose centre lies within 0.9 × their radius of an ocean shore show, so inland cells never do." )]
	public float AzureCoastBlobSpacingMeters { get; set; } = 3000f;

	[Property, Group( "Biomes" ), Title( "Azure Blob Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Chance that a cell holds a patch (before the inner fade). Higher = more of the ocean coast is teal." )]
	public float AzureCoastBlobChance01 { get; set; } = 0.55f;

	[Property, Group( "Biomes" ), Title( "Azure Blob Radius Min (m)" ), Range( 100f, 3000f ), Step( 25f ), Description( "Smallest patch radius; the outline wobbles ± 35 %." )]
	public float AzureCoastBlobRadiusMinMeters { get; set; } = 600f;

	[Property, Group( "Biomes" ), Title( "Azure Blob Radius Max (m)" ), Range( 100f, 4000f ), Step( 25f ), Description( "Largest patch radius (clamped to the spacing)." )]
	public float AzureCoastBlobRadiusMaxMeters { get; set; } = 1500f;

	[Property, Group( "Biomes" ), Title( "Azure Max Inland (m)" ), Range( 50f, 4000f ), Step( 50f ), Description( "Teal never reaches further inland from the ocean shore than this, however big the patch." )]
	public float AzureCoastMaxInlandMeters { get; set; } = 1500f;

	[Property, Group( "Biomes" ), Title( "Blackwater" ), Description( "Named biome only — black circular patches punched over land biomes (not lakes)." )]
	public bool EnableBlackwaterBiome { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Blackwater Spot Count" ), Range( 0, 32 ), Step( 1 ), Description( "How many black circles to place per world seed. Each spot claims one angular wedge around spawn for even spread." )]
	public int BlackwaterSpotCount { get; set; } = 15;

	[Property, Group( "Biomes" ), Title( "Blackwater Min Diameter (m)" ), Range( 40f, 2000f ), Step( 10f )]
	public float BlackwaterMinDiameterMeters { get; set; } = 300f;

	[Property, Group( "Biomes" ), Title( "Blackwater Max Diameter (m)" ), Range( 40f, 2000f ), Step( 10f )]
	public float BlackwaterMaxDiameterMeters { get; set; } = 1000f;

	[Property, Group( "Biomes" ), Title( "Blackwater Min Distance From Spawn (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BlackwaterMinDistanceFromSpawnMeters { get; set; } = 5000f;

	[Property, Group( "Biomes" ), Title( "Blackwater Max Distance From Spawn (m)" ), Range( 0f, 20000f ), Step( 50f ), Description( "0 = use full land disk to the edge." )]
	public float BlackwaterMaxDistanceFromSpawnMeters { get; set; }

	[Property, Group( "Biomes" ), Title( "Blackwater Mountain Clearance (m)" ), Range( 0f, 2000f ), Step( 25f ), Description( "Circles cannot overlap water or sit within this distance of mountain biome." )]
	public float BlackwaterMountainClearanceMeters { get; set; } = 300f;

	[Property, Group( "Biomes" ), Title( "Blackwater Min Distance From Other (m)" ), Range( 0f, 5000f ), Step( 25f ), Description( "Minimum gap between circle edges — spots closer than this are rejected." )]
	public float BlackwaterMinDistanceFromOtherMeters { get; set; } = 400f;

	[Property, Title( "Preview Resolution" ), Range( 64, 4096 ), Step( 64 ), Description( "PNG raster size for editor preview only — higher = sharper but slower generate." )]
	public int PreviewResolution { get; set; } = 1024;

	[Property, Title( "World Seed" ), Description( "Offsets all procedural noise (land, biomes, lakes). Same seed = same world." )]
	public int WorldSeed { get; set; } = 1337;

	[Property, Title( "Random Seed Each Generate" )]
	public bool RandomizeSeedOnGenerate { get; set; }

	[Property, Group( "Lakes" ), Title( "Solve Spawn On Generate" ), Description( "Slides the lake mask in X/Y (up to Max Offset) so spawn sits on dry land. Retries seeds when Retry Seeds is on." )]
	public bool EnableLakeSpawnSolveOnGenerate { get; set; } = true;

	[Property, Title( "Retry Seeds Until Spawn (+1)" ), Description( "When on, bumps World Seed until spawn solve succeeds or Max Seed Attempts is hit." )]
	public bool RetryLakeSeedsUntilSpawn { get; set; }

	[Property, Group( "Lakes" ), Title( "Max Seed Attempts" ), Range( 1, 256 ), Step( 1 )]
	public int LakeMaxSeedAttempts { get; set; } = 100;

	[Property, Title( "Preview Layer" )]
	public TerrainPreviewMode PreviewMode { get; set; } = TerrainPreviewMode.Biomes;

	[Property, Title( "Show Distance Rings" )]
	public bool ShowPreviewDistanceRings { get; set; }

	[Property, Title( "Distance Ring Interval (m)" ), Range( 250f, 5000f ), Step( 250f )]
	public float PreviewDistanceRingIntervalMeters { get; set; } = 1000f;

	[Property, Group( "Layers" ), Title( "Continental" ), Description( "Large-scale landmass bumps — low frequency, sets overall high/low regions." )]
	public bool EnableContinentalLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Hills" ), Description( "Mid-scale rolling hills on top of continental base." )]
	public bool EnableHillLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Valleys" ), Description( "Carving valleys in the height stack — does not create inland water (lakes use a separate mask)." )]
	public bool EnableValleyLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Height Curve" ), Description( "Power curve on combined base noise — reshapes contrast before biomes." )]
	public bool EnableHeightCurveLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Mountains" ), Description( "Peak lift and mountain mask sampling on sculpted land." )]
	public bool EnableMountainLayer { get; set; } = true;

	[Property, Group( "Continental" ), Title( "Frequency" ), Range( 0.25f, 32f ), Step( 0.05f ), Description( "Noise scale — lower = broader continents; higher = more, smaller blobs." )]
	public float ContinentalFrequency { get; set; } = 1.75f;

	[Property, Group( "Continental" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f ), Description( "How strongly continental noise affects base height (0 = flat macro shape)." )]
	public float ContinentalWeight { get; set; } = 0.55f;

	[Property, Group( "Hills" ), Title( "Frequency" ), Range( 0.5f, 64f ), Step( 0.1f ), Description( "Legacy fallback when Hill Wavelength (m) is unset — use World - Hill Wavelength for streamed terrain." )]
	public float HillFrequency { get; set; } = 12f;

	[Property, Group( "Hills" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f ), Description( "Hill amplitude on base height." )]
	public float HillWeight { get; set; } = 0.25f;

	[Property, Group( "Valleys" ), Title( "Frequency (higher = smaller valleys)" ), Range( 0.5f, 64f ), Step( 0.5f ), Description( "Valley carve wavelength — higher = narrower, more frequent valleys in the heightmap only." )]
	public float ValleyFrequency { get; set; } = 20f;

	[Property, Group( "Valleys" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f ), Description( "How deep valleys cut into base height — does not flood lakes." )]
	public float ValleyWeight { get; set; } = 0.2f;

	[Property, Group( "Mountains" ), Title( "Summit Height (m)" ), Range( 50f, 2500f ), Step( 10f ), Description( "Absolute height a full-size mountain's summit aims at (the dome rises to it from the lowland at the blob edge; the tilt, knob and summit rolls land the real apex at 60-85 % of it: 1000 gives 650-850 m summits). Clamped to Max Terrain Height; blobs under half of Mountain Radius Max scale down with their radius, Height Variation lowers some mountains further." )]
	public float MountainSummitHeightMeters { get; set; } = 1000f;

	[Property, Group( "Mountains" ), Title( "Ridge Spacing (m)" ), Range( 150f, 1500f ), Step( 25f ), Description( "Distance between the spurs that run down from the summit, held in metres at every height (they branch on the way down). Mark 2026-10-30: real massifs, not a plateau with a peak." )]
	public float MountainRidgeSpacingMeters { get; set; } = 450f;

	[Property, Group( "Mountains" ), Title( "Ridge Depth (0–1)" ), Range( 0f, 0.9f ), Step( 0.05f ), Description( "How deep the valleys between the spurs cut, as a fraction of the mountain's local height (0.55 = a valley floor mid-slope sits 55 % lower than the crests beside it). Fades out under the summit so the apex is one pyramid." )]
	public float MountainRidgeDepth01 { get; set; } = 0.55f;

	[Property, Group( "Mountains" ), Title( "Height Variation (0–1)" ), Range( 0f, 0.8f ), Step( 0.05f ), Description( "Per-mountain height roll: 0.2 = each mountain is 80–100 % of its full height." )]
	public float MountainHeightVariation01 { get; set; } = 0.2f;

	[Property, Group( "Mountains" ), Title( "Cliff Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Per-mountain roll for a dome standing on a near-vertical foot (envelope exponent 0.35-0.5; Mark 2026-10-30: some mountains are like cliffs, massive dropoffs)." )]
	public float MountainCliffChance01 { get; set; } = 0.2f;

	[Property, Group( "Mountains" ), Title( "Profile Variation (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Per-mountain spread of the dome's curve around a straight cone: 0 = every mountain the same; 0.7 = exponents 0.6-1.2, mostly convex (a steep face out of the lowland rounding into the summit) with the odd straight cone or gentle-footed concave climb (Mark 2026-10-30: in some cases not perfectly sloped)." )]
	public float MountainProfileVariation01 { get; set; } = 0.7f;

	[Property, Group( "Mountains" ), Title( "Mountain Lake Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "A lake that lands entirely on a mountain top becomes a tarn with this chance - its water sits at the top's own level, a frozen lake above the world (Mark 2026-10-30); otherwise the mountain fills it in. Lakes that only touch a mountain are cut back to its foot either way, so no lake slopes into a wall." )]
	public float MountainTarnChance01 { get; set; } = 0.7f;

	[Property, Group( "Mountains" ), Title( "Lake Apron (m)" ), Range( 0f, 800f ), Step( 10f ), Description( "Around a lake at a mountain's foot the mountain is trimmed down to a beach within this distance of the water (a bench, then the face rises), so the lake is not a cutout of the mountain (Mark 2026-10-30). 0 = the face meets the water." )]
	public float MountainLakeApronMeters { get; set; } = 250f;

	[Property, Group( "Mountains" ), Title( "Frequency" ), Range( 0.25f, 32f ), Step( 0.05f )]
	public float MountainFrequency { get; set; } = 8f;

	[Property, Group( "Mountains" ), Title( "Inner Radius (0–1 dist)" ), Range( 0f, 0.99f ), Step( 0.01f )]
	public float MountainInnerRadius01 { get; set; } = 0.075f;

	[Property, Group( "Mountains" ), Title( "Outer Radius (0–1 dist)" ), Range( 0.01f, 1f ), Step( 0.01f )]
	public float MountainOuterRadius01 { get; set; } = 1f;

	[Property, Group( "Mountains" ), Title( "Band Edge Fade (0–1 dist)" ), Range( 0.001f, 0.5f ), Step( 0.005f )]
	public float MountainBandFade01 { get; set; } = 0.1f;

	[Property, Group( "Mountains" ), Title( "Mid-Map Emphasis (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Peak height only — does not affect mountain spawn mask. Non-zero can create a radial ring on Mountain Falloff preview." )]
	public float MountainMidMapEmphasis01 { get; set; }

	[Property, Group( "Mountains" ), Title( "Mid-Map Peak Radius (0–1 dist)" ), Range( 0.12f, 0.75f ), Step( 0.01f )]
	public float MountainMidMapRadialPeak01 { get; set; } = 0.36f;

	[Property, Group( "Mountains" ), Title( "Mid-Map Peak Spread (0–1 dist)" ), Range( 0.06f, 0.45f ), Step( 0.01f )]
	public float MountainMidMapRadialSpread01 { get; set; } = 0.22f;

	[Property, Group( "Mountains" ), Title( "Mid-Map Radial Floor (0–1)" ), Range( 0.05f, 0.85f ), Step( 0.05f )]
	public float MountainMidMapRadialFloor01 { get; set; } = 0.18f;

	[Property, Group( "Mountains" ), Title( "Falloff Edge Power" ), Range( 0.25f, 4f ), Step( 0.05f )]
	public float MountainFalloffRimPower { get; set; } = 1.35f;

	[Property, Group( "Mountains" ), Title( "Slope Sample Step (m)" ), Range( 24f, 256f ), Step( 8f )]
	public float MountainSlopeSampleStepMeters { get; set; } = 96f;

	[Property, Group( "Mountains" ), Title( "Height Influence Low (0–1 field)" ), Range( 0.02f, 0.45f ), Step( 0.01f ), Description( "Mountain field where foothill lift begins — wide ramp avoids biome-edge cliffs." )]
	public float MountainHeightInfluenceLow01 { get; set; } = 0.02f;

	[Property, Group( "Mountains" ), Title( "Height Influence High (0–1 field)" ), Range( 0.25f, 0.98f ), Step( 0.01f ), Description( "Mountain field where the profile fully owns the height - keep it below Slope Start + a bit, or this ramp (not the profile) sets the climb angle." )]
	public float MountainHeightInfluenceHigh01 { get; set; } = 0.3f;

	[Property, Group( "Mountain Mask" ), Title( "Mountain Spacing (m)" ), Range( 800f, 8000f ), Step( 100f ), Description( "Cell size of the mountain scatter: every cell may hold one round mountain (Mark 2026-10-29: little circles all over, like Valheim). Lower = more mountains." )]
	public float MountainBlobSpacingMeters { get; set; } = 3900f;

	[Property, Group( "Mountain Mask" ), Title( "Mountain Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Chance that a cell holds a mountain. 0.5 ≈ half the cells." )]
	public float MountainBlobChance01 { get; set; } = 0.34f;

	[Property, Group( "Mountain Mask" ), Title( "Mountain Radius Min (m)" ), Range( 100f, 3000f ), Step( 25f ), Description( "Smallest blob radius (foothill edge). The biome mask starts where the bump passes Biomes - Min Mountain Mask, about 0.8 × the radius at 0.38." )]
	public float MountainBlobRadiusMinMeters { get; set; } = 750f;

	[Property, Group( "Mountain Mask" ), Title( "Mountain Radius Max (m)" ), Range( 100f, 4000f ), Step( 25f ), Description( "Largest blob radius; clamped to the spacing so neighbours stay separate blobs." )]
	public float MountainBlobRadiusMaxMeters { get; set; } = 2100f;

	[Property, Group( "Mountain Mask" ), Title( "Outline Wobble (0–1)" ), Range( 0f, 0.6f ), Step( 0.05f ), Description( "Noise on the blob radius so the circles read as knobbly roundish mountains, not compass circles." )]
	public float MountainBlobEdgeWobble01 { get; set; } = 0.4f;

	[Property, Group( "Mountain Mask" ), Title( "Elongation (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Each massif is stretched along its own random axis by up to 1 + this (real ranges are seldom round). Changes the biome mask too." )]
	public float MountainBlobElongation01 { get; set; } = 0.6f;

	[Property, Group( "Mountain Mask" ), Title( "Drop Isolated Specks" )]
	public bool MountainSpawnSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Mountain Mask" ), Title( "Min Patch Diameter (m)" ), Range( 40f, 800f ), Step( 10f ), Description( "Drop mask islands smaller than this (world meters)." )]
	public float MountainSpawnMinPatchDiameterMeters { get; set; } = 600f;

	[Property, Group( "Mountain Mask" ), Title( "Min Patch Support (0–1)" ), Range( 0.2f, 0.95f ), Step( 0.02f ), Description( "Fraction of samples inside the patch disk that must pass threshold." )]
	public float MountainSpawnMinPatchSupport01 { get; set; } = 0.48f;

	[Property, Group( "Mountain Mask" ), Title( "Min Patch Grid Steps" ), Range( 3, 7 ), Step( 1 ), Description( "Sample grid density across the patch diameter disk." )]
	public int MountainSpawnMinPatchGridSteps { get; set; } = 4;

	[Property, Group( "Height Curve" ), Title( "Power" ), Range( 0.5f, 3f ), Step( 0.05f ), Description( "Exponent on normalized base height — below 1 lifts lows; above 1 sharpens highs." )]
	public float HeightCurvePower { get; set; } = 1.1f;

	[Property, Group( "Water" ), Title( "Lake Map" ), Description( "Master switch for inland lakes. Open water comes only from the lake mask — not from land height below sea level." )]
	public bool EnableInteriorWaterLayer { get; set; } = true;

	[Property, Group( "Water" ), Title( "Dry Land Sea Margin (m)" ), Range( 0.05f, 5f ), Step( 0.05f ), Description( "Inland dry land is clamped to at least sea level + this before lakes combine." )]
	public float InlandDryLandSeaMarginMeters { get; set; } = 0.25f;

	[Property, Group( "Lakes" ), Title( "Target Lake Coverage (land disk)" ), Range( 0.08f, 0.33f ), Step( 0.01f ), Description( "Fraction of the land circle converted to open lake water (after speck filters). Generate Stats ▸ Water on land shows this same value." )]
	public float TargetLakeCoverageOnLand01 { get; set; } = 0.26f;

	[Property, Group( "Lakes" ), Title( "Auto Lake Threshold" ), Description( "When on, cutoff = coverage quantile from one mask sample. Speck runs once after — no threshold iteration." )]
	public bool LakeAutoThreshold { get; set; } = true;

	[Property, Group( "Lakes" ), Title( "Manual Mask Threshold (0–1)" ), Range( 0.05f, 0.95f ), Step( 0.01f ), Description( "Mask strength required for open water when Auto Lake Threshold is off. Lower = more/larger lakes." )]
	public float LakeMaskThreshold01 { get; set; } = 0.45f;

	[Property, Group( "Lakes" ), Title( "Macro Frequency" ), Range( 0.05f, 8f ), Step( 0.05f ), Description( "Lake basin scale — higher = smaller lakes. 1.0 ≈ 2.2 km basins on a 20 km world (same scale as the old wavelength default)." )]
	public float LakeMacroFrequency { get; set; } = 1.2f;

	[Property, Group( "Lakes" ), Title( "Medium Frequency" ), Range( 0.25f, 12f ), Step( 0.05f ), Description( "Shore/depth variation inside basins. 1.0 ≈ 650 m detail on a 20 km world." )]
	public float LakeMediumFrequency { get; set; } = 0.8f;

	[Property, Group( "Lakes" ), Title( "Macro Octaves" ), Range( 1, 5 ), Step( 1 ), Description( "Noise detail on macro basins. Lower = smoother, rounder lakes; higher risks speckle." )]
	public int LakeMacroOctaves { get; set; } = 2;

	[Property, Group( "Lakes" ), Title( "Shore Detail (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Ridged shore wiggle and island detail. Islands narrower than Min Speck Diameter are flooded." )]
	public float LakeShoreDetail01 { get; set; } = 0.10f;

	[Property, Group( "Lakes" ), Title( "Ocean Zone Start (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Fraction of the land radius where the lake basins start cross-fading into ocean-sized basins (Mark 2026-10-30: massive oceans outward, ponds near spawn)." )]
	public float LakeOceanZoneStart01 { get; set; } = 0.4f;

	[Property, Group( "Lakes" ), Title( "Ocean Zone Full (0–1)" ), Range( 0.05f, 1.2f ), Step( 0.05f ), Description( "Fraction of the land radius where the basins are fully ocean-sized." )]
	public float LakeOceanZoneEnd01 { get; set; } = 0.95f;

	[Property, Group( "Lakes" ), Title( "Ocean Basin Size (× lake)" ), Range( 1f, 12f ), Step( 0.5f ), Description( "How many times bigger the outer basins are than the inner lake basins (Macro Frequency). 4 on a 1.8 km lake = ~7 km oceans." )]
	public float LakeOceanBasinSizeMul { get; set; } = 5f;

	[Property, Group( "Lakes" ), Title( "Ocean Lift (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Raises the outer basin field so more of the outer land is water (the coverage target then lands more of its water out there)." )]
	public float LakeOceanLift01 { get; set; } = 0.3f;

	[Property, Group( "Lakes" ), Title( "Mask Offset X (m)" ), Range( -4000f, 4000f ), Step( 50f ), Description( "Slides the lake noise field east (+) / west (−). Auto solve sets this on generate." )]
	public float LakeOffsetXMeters { get; set; }

	[Property, Group( "Lakes" ), Title( "Mask Offset Y (m)" ), Range( -4000f, 4000f ), Step( 50f ), Description( "Slides the lake noise field north (+) / south (−). Auto solve sets this on generate." )]
	public float LakeOffsetYMeters { get; set; }

	[Property, Group( "Lakes" ), Title( "Max Auto Offset (m)" ), Range( 0f, 3000f ), Step( 50f ), Description( "Spawn solve searches within this radius to place lakes off spawn." )]
	public float LakeMaxOffsetMeters { get; set; } = 1500f;

	[Property, Group( "Lakes" ), Title( "Spawn Check Radius (m)" ), Range( 10f, 200f ), Step( 5f ), Description( "Disk around (0,0) that must be mostly dry land after lake solve." )]
	public float LakeSpawnCheckRadiusMeters { get; set; } = 50f;

	[Property, Group( "Lakes" ), Title( "Showcase Water Radius (m)" ), Range( 50f, 2000f ), Step( 25f ), Description( "Open lake water within this distance of spawn (0,0) counts as showcase water. Spawn stays dry; lakes farther away do not count." )]
	public float LakeSpawnShowcaseWaterRadiusMeters { get; set; } = 300f;

	[Property, Group( "Rivers" ), Title( "Rivers" ), Description( "River network from high ground down to lakes and the sea (Mark 2026-10-07). Rivers are carved at sample time: inside the width the ground is the sloped water surface, banks blend up to the land." )]
	public bool EnableRivers { get; set; } = true;

	[Property, Group( "Rivers" ), Title( "River Count" ), Range( 0, 80 ), Step( 1 ), Description( "How many rivers to build. Each starts on high ground at least Source Spacing from the others; sources that cannot reach water are skipped." )]
	public int RiverCount { get; set; } = 80;

	[Property, Group( "Rivers" ), Title( "River Width at Source (m)" ), Range( 4f, 60f ), Step( 1f )]
	public float RiverWidthMinMeters { get; set; } = 8f;

	[Property, Group( "Rivers" ), Title( "River Width at Mouth (m)" ), Range( 8f, 120f ), Step( 1f )]
	public float RiverWidthMaxMeters { get; set; } = 28f;

	[Property, Group( "Rivers" ), Title( "River Bank (× width)" ), Range( 0.25f, 6f ), Step( 0.25f ), Description( "How far from the water the ground blends back to the surrounding land, as a multiple of the river width." )]
	public float RiverBankWidthScale { get; set; } = 1.5f;

	[Property, Group( "Rivers" ), Title( "River Min Grade (%)" ), Range( 0.05f, 5f ), Step( 0.05f ), Description( "The water level always falls at least this much per 100 m along the river, so it reads as flowing." )]
	public float RiverMinGradePercent { get; set; } = 0.3f;

	[Property, Group( "Rivers" ), Title( "River Max Cut (m)" ), Range( 2f, 80f ), Step( 1f ), Description( "A route that would have to cut deeper than this through a ridge (real ground as well as the smoothed routing field) is abandoned instead of carving a canyon. 25 let rivers climb hills in 20-40 m trenches for kilometres (Mark 2026-10-30: \"it goes uphill\"); 8 keeps them in real valleys." )]
	public float RiverMaxCutMeters { get; set; } = 8f;

	[Property, Group( "Rivers" ), Title( "River Source Spacing (m)" ), Range( 300f, 6000f ), Step( 100f )]
	public float RiverSourceSpacingMeters { get; set; } = 1200f;

	[Property, Group( "Rivers" ), Title( "Mountain Source Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Share of river sources on a mountain top that are kept (seeded roll per spot). A river born on a top runs over the wall as a waterfall (Mark 2026-10-30) - at 1 every eligible top sheds rivers, at 0 none do." )]
	public float RiverMountainSourceChance01 { get; set; } = 0.5f;

	[Property, Group( "Rivers" ), Title( "Mountain Lake Falls (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Share of lakes at a mountain's foot that get a river of their own down the face into them - a waterfall where the face is steep (Mark 2026-10-30: 'I want them for sure'). Tried from up to six spots above the lake; counted outside Mountain Source Chance." )]
	public float RiverLakeWaterfallChance01 { get; set; } = 1f;

	[Property, Group( "Rivers" ), Title( "River Route Smoothing (m)" ), Range( 0f, 400f ), Step( 10f ), Description( "Rounds the raster route into meanders; 0 keeps the raw cell path." )]
	public float RiverSmoothingMeters { get; set; } = 120f;

	[Property, Group( "Rivers" ), Title( "River Depth (m)" ), Range( 0.25f, 12f ), Step( 0.25f ), Description( "The water surface sits this far below the lowest ground around the route, so a river always cuts in and never lifts the land." )]
	public float RiverDepthMeters { get; set; } = 2f;

	[Property, Group( "Rivers" ), Title( "River Bank Max Slope (deg)" ), Range( 8f, 60f ), Step( 1f ), Description( "A deep cut spreads its bank until the side is no steeper than this, so no river becomes a canyon." )]
	public float RiverBankMaxSlopeDegrees { get; set; } = 25f;

	[Property, Group( "Rivers" ), Title( "River Bed Depth (m)" ), Range( 0.5f, 10f ), Step( 0.25f ), Description( "How far the carved bed dips below the water surface at the centre line (a parabola from the edges). The water mesh fills it." )]
	public float RiverBedDepthMeters { get; set; } = 2.5f;

	[Property, Group( "Rivers" ), Title( "River Bed Max Depth, Clover Hills (m)" ), Range( 0.5f, 12f ), Step( 0.25f ), Description( "Deepest the bed may dip below the water in Clover Hills, pools included (Mark 2026-10-21: gentle rivers, 4 m at most). Blends to the other-biome cap across a biome border." )]
	public float RiverBedMaxDepthCloverMeters { get; set; } = 4f;

	[Property, Group( "Rivers" ), Title( "River Bed Max Depth, Other Biomes (m)" ), Range( 0.5f, 30f ), Step( 0.25f ), Description( "Deepest the bed may dip below the water outside Clover Hills. Kept gentle for now; raise it for wild redwood / mountain rivers." )]
	public float RiverBedMaxDepthOtherMeters { get; set; } = 6f;

	[Property, Group( "Rivers" ), Title( "River Bank Height (m)" ), Range( 0f, 10f ), Step( 0.25f ), Description( "The channel rim stands this far above the water: the trough you can see (Mark 2026-10-21: a water-slide shape, not a dish). Cut only — ground already lower than the rim is left alone." )]
	public float RiverBankHeightMeters { get; set; } = 1.5f;

	[Property, Group( "Rivers" ), Title( "River Bank Slide Width (m)" ), Range( 0.5f, 30f ), Step( 0.5f ), Description( "Horizontal width of the curved wall from the water's edge up to the rim (gentle at the water, steepest at the rim). Beyond it the ground blends out over River Bank (× width)." )]
	public float RiverBankSlideWidthMeters { get; set; } = 4f;

	[Property, Group( "Rivers" ), Title( "River Flow Speed (m/s)" ), Range( 0f, 10f ), Step( 0.1f ), Description( "The current along every river, source to mouth: scrolls the water surface ripples and sets the swim volume's fluid velocity." )]
	public float RiverFlowSpeedMetersPerSecond { get; set; } = 1.5f;

	[Property, Group( "Water" ), Title( "Sea Level (m)" ), Range( -50f, 200f ), Step( 1f ), Description( "Flat height for all water (rim ocean + lakes). Default 0 — every water surface is exactly this world meter." )]
	public float SeaLevelMeters { get; set; }

	[Property, Group( "Water" ), Title( "Wet Line Height (m)" ), Range( 0f, 2f ), Step( 0.05f ), Description( "Ground just above any water — river banks, lake and sea shores — is painted darker, like wet soil and sand, up to this height (Mark 2026-10-22). 0 = no wet band." )]
	public float WetLineHeightMeters { get; set; } = 0.45f;

	[Property, Group( "Water" ), Title( "Beach Width (m)" ), Range( 0f, 40f ), Step( 0.5f ), Description( "Land within this distance of a lake or sea shore blends from the sand of the bed into the biome ground (Mark 2026-10-22: a nice blend, not a zigzag where the ground paint switched between two 2 m vertices). 0 = no beach." )]
	public float BeachWidthMeters { get; set; } = 10f;

	[Property, Group( "Water" ), Title( "Ocean Max Depth (m)" ), Range( 0f, 1000f ), Step( 5f ), Description( "The deep ocean floor, below sea level, past the shelf and its drop-off (Mark 2026-10-22: 300 m). 0 = flat at sea level." )]
	public float OceanMaxDepthMeters { get; set; } = 300f;

	[Property, Group( "Water" ), Title( "Ocean Shelf Depth (m)" ), Range( 0f, 200f ), Step( 1f ), Description( "Depth at the outer edge of the continental shelf: the floor falls gently from the beach to this over Ocean Shelf Width, then drops off to Ocean Max Depth over Ocean Drop-off Width." )]
	public float OceanShelfDepthMeters { get; set; } = 30f;

	[Property, Group( "Water" ), Title( "Ocean Shelf Width (m)" ), Range( 20f, 2500f ), Step( 10f ), Description( "Distance past the land edge over which the floor falls from the beach to Ocean Shelf Depth — steepest at the shore (900 m and 30 m: about 1:15 at the beach), level by the shelf edge." )]
	public float OceanShelfWidthMeters { get; set; } = 900f;

	[Property, Group( "Water" ), Title( "Ocean Drop-off Width (m)" ), Range( 20f, 2500f ), Step( 10f ), Description( "Past the shelf the floor falls from Ocean Shelf Depth to Ocean Max Depth over this distance." )]
	public float OceanDropOffWidthMeters { get; set; } = 700f;

	[Property, Group( "Water" ), Title( "Lake Max Depth (m)" ), Range( 0f, 60f ), Step( 0.5f ), Description( "Lakes are carved as bowls sloping down toward their middle, to this depth below sea level. 0 = flat at sea level." )]
	public float LakeMaxDepthMeters { get; set; } = 8f;

	[Property, Group( "Water" ), Title( "Lake Shelf Width (m)" ), Range( 5f, 1000f ), Step( 5f ), Description( "Distance from a lake's shore over which its floor falls to Lake Max Depth — steepest at the shore (100 m and 8 m: about 1:6). Measured from the shoreline itself, so a lake is as deep in the middle however its mask is shaped (the old mask-based ramp left most lakes under a metre deep)." )]
	public float LakeShelfWidthMeters { get; set; } = 100f;

	[Property, Group( "Coast" ), Title( "Beach Blend Band (m)" ), Range( 100f, 3000f ), Step( 50f ), Description( "Width from land edge where height eases toward sea for beach coasts." )]
	public float CoastalBeachBlendBandMeters { get; set; } = 800f;

	[Property, Group( "Coast" ), Title( "Max Shore Height (m)" ), Range( 5f, 200f ), Step( 5f ), Description( "Beach coasts: the land within the blend bands is capped at sea level + this × (distance / band) - the gentle run down to the sand." )]
	public float CoastalMaxShoreHeightMeters { get; set; } = 50f;

	[Property, Group( "Coast" ), Title( "Sea Cliff Chance (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Roughly the share of OCEAN shore (the rim and bays joined to it, never inland lakes) that is a sea cliff - the land rises to a headland at the water's edge and drops into the sea as a wall (Mark 2026-10-30: the Cliffs of Moher) instead of draining to a beach. Laid out in seeded stretches." )]
	public float CoastCliffChance01 { get; set; } = 0.3f;

	[Property, Group( "Coast" ), Title( "Sea Cliff Stretch (m)" ), Range( 500f, 12000f ), Step( 100f ), Description( "Wavelength of the cliff presence noise along the coast: how long a cliff stretch and the beach after it typically run." )]
	public float CoastCliffStretchMeters { get; set; } = 4000f;

	[Property, Group( "Coast" ), Title( "Sea Cliff Height (m)" ), Range( 0f, 300f ), Step( 5f ), Description( "Height of the headland at the cliff edge above sea level (each stretch 60-100 % of it). The wall is the shore bank squeezed to ~3 m of run." )]
	public float CoastCliffHeightMeters { get; set; } = 90f;

	[Property, Group( "Coast" ), Title( "Sea Cliff Band (m)" ), Range( 20f, 1500f ), Step( 10f ), Description( "How far inland the headland is held up before the land is its own height again (the wedge rising toward the edge)." )]
	public float CoastCliffBandMeters { get; set; } = 300f;

	[Property, Group( "Coast" ), Title( "Inland Lake Shore Band (m)" ), Range( 80f, 2000f ), Step( 25f ), Description( "Dry land within this distance of open lake water eases height toward sea level." )]
	public float CoastalInlandBeachBandMeters { get; set; } = 450f;

	[Property, Group( "Water" ), Title( "Min Speck Diameter (m)" ), Range( 80f, 500f ), Step( 5f ), Description( "Minimum width for lake/water patches and rim-ocean land islands." )]
	public float SpeckMinPatchDiameterMeters { get; set; } = 150f;

	[Property, Group( "Water" ), Title( "Drop Land Islands In Water" ), Description( "Rim ocean only: floods dry land patches in water narrower than Min Speck Diameter (preview raster + chunk mesh)." )]
	public bool LandSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Mountain Placement Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeMountainPlacementStrength01 { get; set; } = 1f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Hill Spacing (m)" ), Range( 250f, 400f ), Step( 10f ), Description( "Voronoi cell size — hill centers about this far apart (400 m = large rolling rises)." )]
	public float BiomeCloverHillSpacingMeters { get; set; } = 400f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Hill Density (0–1)" ), Range( 0.15f, 1f ), Step( 0.05f ), Description( "Fraction of Voronoi cells that become big hills. Others stay low plateaus / gaps (seeded skip)." )]
	public float BiomeCloverHillDensity01 { get; set; } = 0.4f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Gap Floor Min (0–1 of clover max)" ), Range( 0.05f, 0.5f ), Step( 0.05f ), Description( "Height when a cell is skipped (low plateau between hills)." )]
	public float BiomeCloverGapFloorMin01 { get; set; } = 0.1f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Gap Floor Max (0–1 of clover max)" ), Range( 0.08f, 0.55f ), Step( 0.05f )]
	public float BiomeCloverGapFloorMax01 { get; set; } = 0.26f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Plateau Height Min (0–1 of clover max)" ), Range( 0.15f, 0.8f ), Step( 0.05f )]
	public float BiomeCloverPlateauHeightMin01 { get; set; } = 0.45f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Plateau Height Max (0–1 of clover max)" ), Range( 0.25f, 1f ), Step( 0.05f )]
	public float BiomeCloverPlateauHeightMax01 { get; set; } = 1f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Plateau Radius (cells)" ), Range( 0.08f, 0.55f ), Step( 0.02f ), Description( "Flat-ish top of each hill (fraction of a Voronoi cell)." )]
	public float BiomeCloverPlateauRadius01 { get; set; } = 0.34f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Hill Falloff Radius (cells)" ), Range( 0.4f, 1.45f ), Step( 0.05f ), Description( "How far the skirt reaches — higher = gentler low→high across the cell." )]
	public float BiomeCloverHillFalloffRadius01 { get; set; } = 1.2f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Hill Warp Amp (0–1 of clover max)" ), Range( 0.02f, 0.2f ), Step( 0.01f ), Description( "Extra FBM so hills aren't perfect radial dishes." )]
	public float BiomeCloverHillWarpAmplitude01 { get; set; } = 0.08f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Micro Wavelength (m)" ), Range( 8f, 80f ), Step( 1f ), Description( "Rolling ground-bump size. Below ~8 m the 2 m vertex grid turns bumps into spikes. 0 in old bundles → default 24." )]
	public float BiomeCloverMicroWavelengthMeters { get; set; } = 24f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Micro Amplitude (m)" ), Range( 0.25f, 6f ), Step( 0.25f ), Description( "Ground-bump height (±meters), varied across the biome by a slow envelope. 0 in old bundles → default 1.5." )]
	public float BiomeCloverMicroAmplitudeMeters { get; set; } = 1.5f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Shape Blend (0–1)" ), Range( 0.25f, 1f ), Step( 0.05f )]
	public float BiomeCloverShapeBlend01 { get; set; } = 0.95f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Slope Smooth (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Widens skirts + flatter ease curve so 400 m cells don't read as steep cones." )]
	public float BiomeCloverSlopeSmooth01 { get; set; } = 0.85f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Grass Tint Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Vertex-color grass mottling so Clover reads clearly on the mesh/stamp." )]
	public float BiomeCloverGrassTintStrength01 { get; set; } = 0.9f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Hillside Wavelength (m)" ), Range( 200f, 2000f ), Step( 50f ), Description( "Size of one hillside swell (crest to crest). Shorter = more, smaller hills." )]
	public float BiomeRedwoodHillsideWavelengthMeters { get; set; } = 600f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Hillside Relief (m)" ), Range( 10f, 300f ), Step( 10f ), Description( "Crest-to-trough height of the hillside swells. With the 600 m wavelength, 170 m gives a median slope of ~12° and steep stretches of ~25–40°." )]
	public float BiomeRedwoodHillsideReliefMeters { get; set; } = 170f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ravine Wavelength (m)" ), Range( 40f, 400f ), Step( 10f ), Description( "Spacing of the rounded gullies laid over the hillside." )]
	public float BiomeRedwoodRavineWavelengthMeters { get; set; } = 160f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ravine Depth (m)" ), Range( 0f, 40f ), Step( 1f ), Description( "Depth of the gullies. 0 = pure hillside." )]
	public float BiomeRedwoodRavineDepthMeters { get; set; } = 18f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Micro Amplitude (m)" ), Range( 0f, 3f ), Step( 0.1f ), Description( "Forest-floor duff bumps (±m at ~20 m wavelength). Below 0.25 m the layer is off." )]
	public float BiomeRedwoodMicroAmplitudeMeters { get; set; } = 0.6f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Height Min (m)" ), Range( 0f, 30f ), Step( 0.5f ), Description( "Shortest drop a ledge gets. Height varies along each ledge between Min and Max; Max 0 = no ledges." )]
	public float BiomeRedwoodLedgeHeightMinMeters { get; set; } = 3f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Height Max (m)" ), Range( 0f, 30f ), Step( 0.5f ), Description( "Tallest drop a ledge gets (tall stretches are the rarer ones). Capped at 85 % of the Rise Interval." )]
	public float BiomeRedwoodLedgeHeightMaxMeters { get; set; } = 30f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Height Wavelength (m)" ), Range( 20f, 600f ), Step( 10f ), Description( "How fast the drop height changes along a ledge: one tall-to-short cycle per this distance." )]
	public float BiomeRedwoodLedgeHeightWavelengthMeters { get; set; } = 150f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Rise Interval (m)" ), Range( 8f, 200f ), Step( 1f ), Description( "A ledge every this many meters of hillside climb. 70 m on a 12° slope ≈ one ledge line every ~330 m of ground (Mark 2026-10-06: not so close together); steeper stretches get them closer, flats get none. Ledge heights are capped at 85 % of this." )]
	public float BiomeRedwoodLedgeRiseIntervalMeters { get; set; } = 70f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Line Detail (octaves)" ), Range( 1, 4 ), Step( 1 ), Description( "How much of the hillside's wrinkle the ledge lines follow. 1-2 = long smooth sweeps that nest like contour lines on a topo map (Mark's sketch, 2026-10-06); 4 = the lines bend with every swell the ground has. The ground itself always keeps its full detail." )]
	public int BiomeRedwoodLedgeLineOctaves { get; set; } = 2;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Face Width (m)" ), Range( 0.2f, 6f ), Step( 0.1f ), Description( "Horizontal width of the drop. 1 m = a hard step inside one or two 2 m quads (51–82°). Below ~1 m the cliff edge turns into a zig-zag in plan view because the 2 m vertex grid cannot follow a sharper line." )]
	public float BiomeRedwoodLedgeFaceWidthMeters { get; set; } = 1f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Face Slope (deg)" ), Range( 45f, 89f ), Step( 1f ), Description( "Steepest a ledge face may be. The face width is the larger of Face Width and height ÷ tan(this), so a tall ledge spreads over several grid rows and the 2 m mesh can follow it without fins (2026-10-07: 1 m faces on 30 m ledges were spiky). 70° is still a cliff (pawn limit 50°)." )]
	public float BiomeRedwoodLedgeFaceSlopeDegrees { get; set; } = 70f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Ramp Spacing (m)" ), Range( 10f, 120f ), Step( 2f ), Description( "Grid spacing of the walk-up notches for a 5 m ledge; taller ledges get wider, further-apart notches (up to 2.5×). Any ledge has a notch within ~1.4× its spacing (28 m → within ~40 m for 5 m ledges, ~100 m for 15 m ledges; typically 10 m)." )]
	public float BiomeRedwoodLedgeRampSpacingMeters { get; set; } = 28f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Ramp Width (m)" ), Range( 2f, 40f ), Step( 1f ), Description( "Width of each walk-up notch along the ledge (varies 0.6–1.4× per notch)." )]
	public float BiomeRedwoodLedgeRampWidthMeters { get; set; } = 6f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Ramp Grade (deg)" ), Range( 5f, 45f ), Step( 1f ), Description( "Extra slope a walk-up notch adds on top of the hillside (the run scales with the ledge height). 12° on a typical 20° ledge-side hillside ≈ 30° in the notch." )]
	public float BiomeRedwoodLedgeRampGradeDegrees { get; set; } = 12f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Blend Coverage (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Share of ledge length where the drop fades to nothing over ~50 m and the ground is plain hillside — the drive-up stretches. 0.3 ≈ 8–16 % of each ledge." )]
	public float BiomeRedwoodLedgeBlendCoverage01 { get; set; } = 0.3f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ledge Blend Length (m)" ), Range( 20f, 600f ), Step( 10f ), Description( "Typical size of one blended stretch along a ledge." )]
	public float BiomeRedwoodLedgeBlendLengthMeters { get; set; } = 120f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Frequency" ), Range( 0.75f, 8f ), Step( 0.25f )]
	public float BiomeAmberDuneFrequency { get; set; } = 2.2f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Floor (0–1)" ), Range( 0.05f, 0.45f ), Step( 0.01f )]
	public float BiomeAmberDuneFloor01 { get; set; } = 0.14f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Amplitude (0–1)" ), Range( 0.08f, 0.35f ), Step( 0.01f )]
	public float BiomeAmberDuneAmplitude01 { get; set; } = 0.2f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Reshape Blend (0–1)" ), Range( 0.35f, 0.95f ), Step( 0.05f )]
	public float BiomeAmberDuneReshapeBlend01 { get; set; } = 0.78f;

	[Property, Group( "Biomes" ), Title( "Clover Max Height (m)" ), Range( 20f, 100f ), Step( 5f ), Description( "Hard ceiling for Clover hills (0–100 m)." )]
	public float BiomeCloverMaxHeightMeters { get; set; } = 100f;

	[Property, Group( "Biomes" ), Title( "Redwood Max Height (m)" ), Range( 50f, 1000f ), Step( 10f )]
	public float BiomeRedwoodMaxHeightMeters { get; set; } = 300f;

	[Property, Group( "Biomes" ), Title( "Amber Max Height (m)" ), Range( 50f, 800f ), Step( 10f )]
	public float BiomeAmberMaxHeightMeters { get; set; } = 200f;

	[Property, Group( "Biomes" ), Title( "Mountain Max Height (m)" ), Range( 200f, 2000f ), Step( 25f )]
	public float BiomeMountainMaxHeightMeters { get; set; } = 1000f;

	[Property, Group( "Biomes" ), Title( "Flat Biome Excess Retention (0–1)" ), Range( 0f, 0.12f ), Step( 0.01f ), Description( "Clover / amber — fraction kept above max (keep low)." )]
	public float BiomeFlatExcessRetention01 { get; set; } = 0.04f;

	[Property, Group( "Biomes" ), Title( "Forest Knee Start (0–1)" ), Range( 0.8f, 0.98f ), Step( 0.01f )]
	public float BiomeForestSoftCapKneeStart01 { get; set; } = 0.92f;

	[Property, Group( "Biomes" ), Title( "Forest Approach Blend (0–1)" ), Range( 0f, 0.5f ), Step( 0.05f )]
	public float BiomeForestApproachBlend01 { get; set; } = 0.22f;

	[Property, Group( "Biomes" ), Title( "Forest Excess Retention (0–1)" ), Range( 0.02f, 0.15f ), Step( 0.01f )]
	public float BiomeForestExcessRetention01 { get; set; } = 0.07f;

	[Property, Group( "Biomes" ), Title( "Mountain Excess Retention (0–1)" ), Range( 0.05f, 0.35f ), Step( 0.01f ), Description( "Only applies above mountain max — peaks stay sharp below it." )]
	public float BiomeMountainExcessRetention01 { get; set; } = 0.2f;

	[Property, Group( "Biomes" ), Title( "Overlay Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeOverlayStrength01 { get; set; } = 0.82f;

	[Property, Group( "Biomes" ), Title( "Shade Noise Frequency" ), Range( 0.5f, 32f ), Step( 0.25f )]
	public float BiomeNoiseFrequency { get; set; } = 6f;

	[Property, Group( "Biomes" ), Title( "Regions Per Biome" ), Range( 1, 24 ), Step( 1 ), Description( "Clover, Redwood and Amber each get this many seeded sites spread evenly over the land disc; every point belongs to its nearest site, so a biome is at most this many separate sections (Mark 2026-10-29: multiple pockets of each biome — 16, ~4–5 km across on 40 km)." )]
	public int BiomeRegionsPerBiome { get; set; } = 16;

	[Property, Group( "Biomes" ), Title( "Region Border Warp (m)" ), Range( 0f, 6000f ), Step( 100f ), Description( "Two-layer domain warp (5-cycle swells + 18-cycle wobble) on the region distance so pieces are weird blobs, not Voronoi polygons." )]
	public float BiomeRegionBorderWarpMeters { get; set; } = 2500f;

	[Property, Group( "Biomes" ), Title( "Region Edge Blend (m)" ), Range( 50f, 3000f ), Step( 50f ), Description( "Width of the soft transition where two regions meet." )]
	public float BiomeRegionEdgeBlendMeters { get; set; } = 600f;

	[Property, Group( "Biomes" ), Title( "Distance Influence Scale (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeDistanceInfluenceScale01 { get; set; } = 0.25f;

	[Property, Group( "Biomes" ), Title( "Merge Small Patches" ), Description( "Speck merge on PNG export; runtime height uses Min Patch Diameter for weight smoothing." )]
	public bool BiomeSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Min Patch Diameter (m)" ), Range( 10f, 500f ), Step( 5f ), Description( "After lakes and mountains are placed, dry-land biome patches (Clover, Redwood, Amber, Ivory Alps) narrower than this merge into the biome around them - on the live terrain and trees too, not only the map (2026-10-06)." )]
	public float BiomeMinPatchDiameterMeters { get; set; } = 400f;

	[Property, Group( "Biomes" ), Title( "Min Mountain Mask (0–1)" ), Range( 0.1f, 0.95f ), Step( 0.01f ), Description( "Mountain Field must exceed this (bright enough) to become white on Mountain Mask / mountain biome." )]
	public float BiomeMinMountainMask01 { get; set; } = 0.38f;

	[Property, Group( "Biomes" ), Title( "Guarantee Clover At Spawn" ), Description( "The region site under the world origin is Clover, so spawn lands in the middle of an ordinary irregular Clover pocket (no stamped disc, no visible circle - Mark 2026-10-30)." )]
	public bool BiomeCloverGuaranteeSpawn { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Clover Map Edge Fade End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverAppearEndMeters { get; set; } = 20000f;

	[Property, Group( "Biomes" ), Title( "Clover Scatter Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeCloverWeight { get; set; } = 0.55f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence Start (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverDistanceInfluenceStartMeters { get; set; } = 150f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverDistanceInfluenceEndMeters { get; set; } = 2000f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeCloverPriorityWeight { get; set; } = 0.4f;

	[Property, Group( "Biomes" ), Title( "Redwood Map Edge Fade End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeRedwoodAppearEndMeters { get; set; } = 20000f;

	[Property, Group( "Biomes" ), Title( "Redwood Scatter Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeRedwoodWeight { get; set; } = 0.58f;

	[Property, Group( "Biomes" ), Title( "Redwood Distance Influence Start (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeRedwoodPriorityStartMeters { get; set; } = 800f;

	[Property, Group( "Biomes" ), Title( "Redwood Distance Influence End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeRedwoodPriorityEndMeters { get; set; } = 2200f;

	[Property, Group( "Biomes" ), Title( "Redwood Distance Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeRedwoodPriorityWeight { get; set; } = 0.22f;

	[Property, Group( "Biomes" ), Title( "Amber Min Distance From Spawn (m)" ), Range( 0f, 15000f ), Step( 250f ), Description( "Region sites closer than this to spawn are never Amber Dunes, so a new player crosses Clover and Redwood before the desert; the borders stay irregular (Mark 2026-10-30)." )]
	public float BiomeAmberHardMinDistanceMeters { get; set; } = 9000f;

	[Property, Group( "Biomes" ), Title( "Amber Map Edge Fade End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberAppearEndMeters { get; set; } = 20000f;

	[Property, Group( "Biomes" ), Title( "Amber Scatter Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeAmberWeight { get; set; } = 0.4f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence Start (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberPriorityStartMeters { get; set; } = 1500f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberPriorityEndMeters { get; set; } = 2500f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeAmberPriorityWeight { get; set; } = 0.3f;

	public float WorldRadiusMeters => WorldDiameterMeters * 0.5f;

	public float LandRadiusMeters => WorldRadiusMeters;

	public float TotalWorldDiameterMeters => WorldDiameterMeters + (OceanRingWidthMeters * 2f);

	public float TotalWorldRadiusMeters => TotalWorldDiameterMeters * 0.5f;

	public float MountainInnerRadiusMeters => MountainInnerRadius01 * WorldRadiusMeters;

	public float MountainOuterRadiusMeters => MountainOuterRadius01 * WorldRadiusMeters;

	public float MountainBandFadeMeters => MountainBandFade01 * WorldRadiusMeters;

	public int ClampedResolution => Math.Clamp( PreviewResolution, 64, 4096 );
}
