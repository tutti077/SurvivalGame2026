namespace Survival;

/// <summary>
/// Designer-facing knobs for the editor terrain noise preview.
/// Shared with future full-world generation via <see cref="TerrainPreviewBackendRegistry"/>.
/// Architecture: <c>docs/TERRAIN_PREVIEW.md</c>. Legacy JSON fields live in <c>TerrainPreviewSettings.Legacy.cs</c>.
/// </summary>
public sealed partial class TerrainPreviewSettings
{
	[Property, Title( "Land Diameter (m)" ), Range( 1000, 100000 ), Step( 500 ), Description( "Playable land disk — ocean ring sits outside this." )]
	public float WorldDiameterMeters { get; set; } = 20000f;

	[Property, Title( "Ocean Ring Width (m)" ), Range( 0f, 10000f ), Step( 250f ), Description( "Flat water band on each side; total world = land + 2× ring (default 25 km)." )]
	public float OceanRingWidthMeters { get; set; } = 2500f;

	[Property, Title( "Max Terrain Height (m)" ), Range( 50f, 10000f ), Step( 50f ), Description( "Mountain / peak ceiling in world meters." )]
	public float MaxTerrainHeightMeters { get; set; } = 700f;

	[Property, Group( "World" ), Title( "Lowland Height Max (m)" ), Range( 20f, 2000f ), Step( 10f ), Description( "Non-mountain dry land scales sculpted base height to this ceiling (default 200 m). Mountains blend to Max Terrain Height." )]
	public float LandRollingHeightMaxMeters { get; set; } = 200f;

	[Property, Group( "World" ), Title( "Land Rolling Macro Frequency" ), Range( 0.1f, 4f ), Step( 0.05f ), Description( "Legacy — unused; rolling hills come from continental/hill/valley base noise." )]
	public float LandRollingMacroFrequency { get; set; } = 0.55f;

	[Property, Group( "World" ), Title( "Land Rolling Macro Octaves" ), Range( 1, 6 ), Step( 1 ), Description( "Legacy — unused." )]
	public int LandRollingMacroOctaves { get; set; } = 4;

	[Property, Group( "World" ), Title( "Land Rolling Detail Frequency Scale" ), Range( 1f, 6f ), Step( 0.25f ), Description( "Legacy — unused." )]
	public float LandRollingDetailFrequencyScale { get; set; } = 2.5f;

	[Property, Group( "World" ), Title( "Land Rolling Detail Amplitude (0–1)" ), Range( 0f, 0.3f ), Step( 0.01f ), Description( "Legacy — unused." )]
	public float LandRollingDetailAmplitude01 { get; set; } = 0.12f;

	[Property, Group( "World" ), Title( "Normalize Land Rolling Heights" ), Description( "Legacy — no longer changes output. Lowlands always use sculpted base height capped at Lowland Height Max." )]
	public bool NormalizeLandRollingHeights { get; set; }

	[Property, Group( "World" ), Title( "Hill Wavelength (m)" ), Range( 80f, 5000f ), Step( 20f ), Description( "Rolling hill size in world meters (~400 m shows relief on 64 m chunks)." )]
	public float HillWavelengthMeters { get; set; } = 400f;

	[Property, Group( "World" ), Title( "Valley Wavelength (m)" ), Range( 80f, 8000f ), Step( 20f ), Description( "Valley carve size in world meters." )]
	public float ValleyWavelengthMeters { get; set; } = 550f;

	[Property, Group( "Biome Terrain" ), Title( "Slope Smoothing" ), Description( "Softens high-frequency sculpt detail. Off by default — it was flattening streamed chunks." )]
	public bool EnableBiomeSlopeSmoothing { get; set; }

	[Property, Group( "Biomes" ), Title( "Continuous Placement At Sample" ), Description( "Blobby biome patches from live noise (not square land-disk cells). Color still uses dominant biome with edge-only soften." )]
	public bool UseContinuousBiomePlacementAtSample { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Edge Color Blend (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "How much colors mix only at biome borders. 0 = hard patches; ~0.35 = soften square edges without blurring interiors." )]
	public float BiomeEdgeColorBlend01 { get; set; } = 0.35f;

	[Property, Group( "Biomes" ), Title( "Edge Blend Start (0–1)" ), Range( 0.05f, 0.5f ), Step( 0.02f ), Description( "Mixing begins when biome transition exceeds this (higher = narrower border blend)." )]
	public float BiomeEdgeBlendStart01 { get; set; } = 0.22f;

	[Property, Group( "Biomes" ), Title( "Azure Coast" ), Description( "Continuous teal shore band on dry land within Azure Coast Width of rim ocean and inland lakes (far from spawn)." )]
	public bool EnableAzureCoastBiome { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Azure Coast — Rim Ocean" ), Description( "Teal band on dry land facing the outer ocean ring (map edge), not only inland lakes." )]
	public bool AzureCoastIncludeRimOcean { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Azure Coast Width (m)" ), Range( 50f, 500f ), Step( 5f ), Description( "Solid teal band inland from display-water shore (world-meter distance, smooth at sample)." )]
	public float AzureCoastWidthMeters { get; set; } = 200f;

	[Property, Group( "Biomes" ), Title( "Azure Coast Min Distance From Spawn (m)" ), Range( 0f, 15000f ), Step( 100f )]
	public float AzureCoastMinDistanceFromSpawnMeters { get; set; } = 5500f;

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

	[Property, Group( "Mountains" ), Title( "Threshold" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Range-field level before peak lift applies." )]
	public float MountainThreshold { get; set; } = 0.55f;

	[Property, Group( "Mountains" ), Title( "Frequency" ), Range( 0.25f, 32f ), Step( 0.05f )]
	public float MountainFrequency { get; set; } = 8f;

	[Property, Group( "Mountains" ), Title( "Inner Radius (0–1 dist)" ), Range( 0f, 0.99f ), Step( 0.01f )]
	public float MountainInnerRadius01 { get; set; } = 0.30f;

	[Property, Group( "Mountains" ), Title( "Outer Radius (0–1 dist)" ), Range( 0.01f, 1f ), Step( 0.01f )]
	public float MountainOuterRadius01 { get; set; } = 0.95f;

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

	[Property, Group( "Mountains" ), Title( "Peak Boost" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainPeakBoost { get; set; } = 0.62f;

	[Property, Group( "Mountains" ), Title( "Min Peak Height (0–1)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainMinPeakHeight01 { get; set; } = 0.42f;

	[Property, Group( "Mountains" ), Title( "Peak Variation Frequency" ), Range( 0.25f, 16f ), Step( 0.05f )]
	public float MountainPeakVariationFrequency { get; set; } = 14f;

	[Property, Group( "Mountains" ), Title( "Peak Rarity Power" ), Range( 1f, 6f ), Step( 0.25f ), Description( "Higher = fewer tall sub-peaks within ranges." )]
	public float MountainPeakRarityPower { get; set; } = 2.2f;

	[Property, Group( "Mountains" ), Title( "Typical Peak Max (0–1)" ), Range( 0.2f, 0.95f ), Step( 0.01f ), Description( "Most mountain terrain caps near this × max height (m)." )]
	public float MountainTypicalPeakMax01 { get; set; } = 0.78f;

	[Property, Group( "Mountains" ), Title( "World Summit Max (0–1)" ), Range( 0.7f, 1f ), Step( 0.005f ), Description( "Rare ridged summits approach this × max height — keep below 1 for headroom." )]
	public float MountainAbsolutePeakMax01 { get; set; } = 0.985f;

	[Property, Group( "Mountains" ), Title( "Summit Macro Frequency" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float MountainSummitMacroFrequency { get; set; } = 0.22f;

	[Property, Group( "Mountains" ), Title( "Summit Macro Threshold (0–1)" ), Range( 0.7f, 0.99f ), Step( 0.01f )]
	public float MountainSummitMacroThreshold01 { get; set; } = 0.82f;

	[Property, Group( "Mountains" ), Title( "Summit Local Peak Min (0–1)" ), Range( 0.1f, 0.95f ), Step( 0.01f )]
	public float MountainSummitLocalPeakMin01 { get; set; } = 0.38f;

	[Property, Group( "Mountains" ), Title( "Peak Band Width (0–1 shape)" ), Range( 0.05f, 0.45f ), Step( 0.01f ), Description( "Ridged-noise span above threshold treated as peak core." )]
	public float MountainPeakBandWidth01 { get; set; } = 0.22f;

	[Property, Group( "Mountains" ), Title( "Peak Sharpness Power" ), Range( 1f, 3.5f ), Step( 0.1f ), Description( "Higher = sharper isolated peaks, less plateau lift." )]
	public float MountainPeakSharpnessPower { get; set; } = 1.85f;

	[Property, Group( "Mountains" ), Title( "Summit Extra Lift (0–1)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float MountainSummitExtraLift01 { get; set; } = 0.28f;

	[Property, Group( "Mountains" ), Title( "Slope Sample Step (m)" ), Range( 24f, 256f ), Step( 8f )]
	public float MountainSlopeSampleStepMeters { get; set; } = 96f;

	[Property, Group( "Mountains" ), Title( "Foothill Spread" ), Range( 0f, 0.55f ), Step( 0.01f )]
	public float MountainFoothillSpread { get; set; } = 0.10f;

	[Property, Group( "Mountains" ), Title( "Foothill Boost" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainFoothillBoost { get; set; } = 0.38f;

	[Property, Group( "Mountains" ), Title( "Height Influence Low (0–1 field)" ), Range( 0.02f, 0.45f ), Step( 0.01f ), Description( "Mountain field where foothill lift begins — wide ramp avoids biome-edge cliffs." )]
	public float MountainHeightInfluenceLow01 { get; set; } = 0.08f;

	[Property, Group( "Mountains" ), Title( "Height Influence High (0–1 field)" ), Range( 0.25f, 0.98f ), Step( 0.01f ), Description( "Mountain field where full peak headroom applies." )]
	public float MountainHeightInfluenceHigh01 { get; set; } = 0.72f;

	[Property, Group( "Mountains" ), Title( "Peak Lift To Sculpt (0–1)" ), Range( 0.08f, 0.55f ), Step( 0.02f ), Description( "Peak boost strength before meter lerp." )]
	public float MountainPeakLiftToSculpt01 { get; set; } = 0.28f;

	[Property, Group( "Mountains" ), Title( "Ridge Peak Spacing (m)" ), Range( 120f, 900f ), Step( 25f ), Description( "Summits along streaky mountain ridges." )]
	public float MountainPeakChainSpacingMeters { get; set; } = 420f;

	[Property, Group( "Mountains" ), Title( "Ridge Peak Cross Tightness" ), Range( 0.12f, 1.5f ), Step( 0.05f )]
	public float MountainPeakChainCrossTightness { get; set; } = 0.42f;

	[Property, Group( "Mountains" ), Title( "Ridge Peak Cross Falloff" ), Range( 0.8f, 3.5f ), Step( 0.1f )]
	public float MountainPeakChainCrossFalloff { get; set; } = 1.8f;

	[Property, Group( "Mountains" ), Title( "Chunky Cluster Spacing (m)" ), Range( 160f, 800f ), Step( 25f ), Description( "Peak groups inside blobby (non-streak) mountain patches." )]
	public float MountainPeakClusterSpacingMeters { get; set; } = 380f;

	[Property, Group( "Mountains" ), Title( "Chunky Cluster Rarity Power" ), Range( 1.1f, 3.5f ), Step( 0.1f ), Description( "Higher = fewer peaks per cluster." )]
	public float MountainPeakClusterRarityPower { get; set; } = 1.75f;

	[Property, Group( "Mountains" ), Title( "Shape Probe (m)" ), Range( 80f, 500f ), Step( 10f ), Description( "Sample radius for ridge-vs-chunky detection." )]
	public float MountainPeakShapeProbeMeters { get; set; } = 220f;

	[Property, Group( "Mountains" ), Title( "Ridge Shape Blend Start (0–1)" ), Range( 0.2f, 0.7f ), Step( 0.02f ), Description( "Below = chunky clusters; above = ridge chains." )]
	public float MountainPeakShapeBlendStart01 { get; set; } = 0.40f;

	[Property, Group( "Mountains" ), Title( "Peak Placement Strength (0–1)" ), Range( 0.35f, 1f ), Step( 0.05f )]
	public float MountainPeakPlacementStrength01 { get; set; } = 0.92f;

	[Property, Group( "Mountain Mask" ), Title( "Macro Octaves" ), Range( 1, 6 ), Step( 1 )]
	public int MountainSpawnMacroOctaves { get; set; } = 4;

	[Property, Group( "Mountain Mask" ), Title( "Medium Octaves" ), Range( 1, 5 ), Step( 1 )]
	public int MountainSpawnMediumOctaves { get; set; } = 3;

	[Property, Group( "Mountain Mask" ), Title( "Macro Wavelength (m)" ), Range( 600f, 6000f ), Step( 50f ), Description( "Average width of a mountain range patch — lower = more patches on the map." )]
	public float MountainSpawnMacroWavelengthMeters { get; set; } = 2200f;

	[Property, Group( "Mountain Mask" ), Title( "Medium Wavelength (m)" ), Range( 200f, 2500f ), Step( 25f ), Description( "Branch ridge detail size on top of macro ranges." )]
	public float MountainSpawnMediumWavelengthMeters { get; set; } = 780f;

	[Property, Group( "Mountain Mask" ), Title( "Ridge Sharpness" ), Range( 1f, 4f ), Step( 0.1f ), Description( "Higher = thinner bright ridges on Mountain Field (less blurry blobs)." )]
	public float MountainSpawnRidgeSharpness { get; set; } = 2.2f;

	[Property, Group( "Mountain Mask" ), Title( "Field Floor (0–1)" ), Range( 0f, 0.45f ), Step( 0.02f ), Description( "Cuts weak gray — raises Min Mountain Mask effect on Mountain Field." )]
	public float MountainSpawnFieldFloor01 { get; set; } = 0.08f;

	[Property, Group( "Mountain Mask" ), Title( "Medium Frequency Scale" ), Range( 1.2f, 5f ), Step( 0.1f ), Description( "Branch ridge scale when Medium Wavelength (m) is unset; otherwise wavelength wins." )]
	public float MountainSpawnMediumFrequencyScale { get; set; } = 3.4f;

	[Property, Group( "Mountain Mask" ), Title( "Medium Ridge Mix (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float MountainSpawnMediumMix01 { get; set; } = 0.62f;

	[Property, Group( "Mountain Mask" ), Title( "Breaker Frequency Scale" ), Range( 1.5f, 6f ), Step( 0.1f ), Description( "Higher-frequency ridged layer that carves gaps between ranges." )]
	public float MountainSpawnBreakerFrequencyScale { get; set; } = 2.8f;

	[Property, Group( "Mountain Mask" ), Title( "Breaker Min (0–1)" ), Range( 0.2f, 0.85f ), Step( 0.01f )]
	public float MountainSpawnBreakerMin01 { get; set; } = 0.42f;

	[Property, Group( "Mountain Mask" ), Title( "Breaker Span (0–1)" ), Range( 0.04f, 0.25f ), Step( 0.01f )]
	public float MountainSpawnBreakerSpan01 { get; set; } = 0.1f;

	[Property, Group( "Mountain Mask" ), Title( "Breaker Strength (0–1)" ), Range( 0f, 0.9f ), Step( 0.05f ), Description( "How deeply breaker noise splits large masses." )]
	public float MountainSpawnBreakerStrength01 { get; set; } = 0.25f;

	[Property, Group( "Mountain Mask" ), Title( "Domain Warp (0–1)" ), Range( 0f, 0.65f ), Step( 0.02f ), Description( "Bends ranges without smearing into one blob." )]
	public float MountainSpawnWarpStrength01 { get; set; } = 0.22f;

	[Property, Group( "Mountain Mask" ), Title( "Range Stretch" ), Range( 1f, 4f ), Step( 0.1f ), Description( "Elongates chains — lower = less single mega-mass." )]
	public float MountainSpawnRangeStretch01 { get; set; } = 1.65f;

	[Property, Group( "Mountain Mask" ), Title( "Range Power" ), Range( 0.55f, 1.6f ), Step( 0.05f ), Description( "Higher = thinner ridges, more separated patches." )]
	public float MountainSpawnRangePower01 { get; set; } = 1.18f;

	[Property, Group( "Mountain Mask" ), Title( "Drop Isolated Specks" )]
	public bool MountainSpawnSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Mountain Mask" ), Title( "Min Patch Diameter (m)" ), Range( 40f, 800f ), Step( 10f ), Description( "Drop mask islands smaller than this (world meters)." )]
	public float MountainSpawnMinPatchDiameterMeters { get; set; } = 220f;

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
	public float TargetLakeCoverageOnLand01 { get; set; } = 0.10f;

	[Property, Group( "Lakes" ), Title( "Auto Lake Threshold" ), Description( "When on, cutoff = coverage quantile from one mask sample. Speck runs once after — no threshold iteration." )]
	public bool LakeAutoThreshold { get; set; } = true;

	[Property, Group( "Lakes" ), Title( "Manual Mask Threshold (0–1)" ), Range( 0.05f, 0.95f ), Step( 0.01f ), Description( "Mask strength required for open water when Auto Lake Threshold is off. Lower = more/larger lakes." )]
	public float LakeMaskThreshold01 { get; set; } = 0.45f;

	[Property, Group( "Lakes" ), Title( "Macro Frequency" ), Range( 0.05f, 8f ), Step( 0.05f ), Description( "Lake basin scale — higher = smaller lakes. 1.0 ≈ 2.2 km basins on a 20 km world (same scale as the old wavelength default)." )]
	public float LakeMacroFrequency { get; set; } = 1f;

	[Property, Group( "Lakes" ), Title( "Medium Frequency" ), Range( 0.25f, 12f ), Step( 0.05f ), Description( "Shore/depth variation inside basins. 1.0 ≈ 650 m detail on a 20 km world." )]
	public float LakeMediumFrequency { get; set; } = 0.4f;

	[Property, Group( "Lakes" ), Title( "Macro Octaves" ), Range( 1, 5 ), Step( 1 ), Description( "Noise detail on macro basins. Lower = smoother, rounder lakes; higher risks speckle." )]
	public int LakeMacroOctaves { get; set; } = 2;

	[Property, Group( "Lakes" ), Title( "Shore Detail (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Ridged shore wiggle and island detail. Islands narrower than Min Speck Diameter are flooded." )]
	public float LakeShoreDetail01 { get; set; } = 0.10f;

	[Property, Group( "Lakes" ), Title( "Mask Offset X (m)" ), Range( -3000f, 3000f ), Step( 50f ), Description( "Slides the lake noise field east (+) / west (−). Auto solve sets this on generate." )]
	public float LakeOffsetXMeters { get; set; }

	[Property, Group( "Lakes" ), Title( "Mask Offset Y (m)" ), Range( -3000f, 3000f ), Step( 50f ), Description( "Slides the lake noise field north (+) / south (−). Auto solve sets this on generate." )]
	public float LakeOffsetYMeters { get; set; }

	[Property, Group( "Lakes" ), Title( "Max Auto Offset (m)" ), Range( 0f, 3000f ), Step( 50f ), Description( "Spawn solve searches within this radius to place lakes off spawn." )]
	public float LakeMaxOffsetMeters { get; set; } = 1500f;

	[Property, Group( "Lakes" ), Title( "Spawn Check Radius (m)" ), Range( 10f, 200f ), Step( 5f ), Description( "Disk around (0,0) that must be mostly dry land after lake solve." )]
	public float LakeSpawnCheckRadiusMeters { get; set; } = 50f;

	[Property, Group( "Lakes" ), Title( "Showcase Water Radius (m)" ), Range( 50f, 2000f ), Step( 25f ), Description( "Open lake water within this distance of spawn (0,0) counts as showcase water. Spawn stays dry; lakes farther away do not count." )]
	public float LakeSpawnShowcaseWaterRadiusMeters { get; set; } = 300f;

	[Property, Group( "Water" ), Title( "Sea Level (m)" ), Range( -50f, 200f ), Step( 1f ), Description( "Flat height for all water (rim ocean + lakes). Default 0 — every water surface is exactly this world meter." )]
	public float SeaLevelMeters { get; set; }

	[Property, Group( "Coast" ), Title( "Beach Blend Band (m)" ), Range( 100f, 3000f ), Step( 50f ), Description( "Width from land edge where height eases toward sea for beach coasts." )]
	public float CoastalBeachBlendBandMeters { get; set; } = 800f;

	[Property, Group( "Coast" ), Title( "Cliff Blend Band (m)" ), Range( 50f, 1500f ), Step( 25f ), Description( "Narrower blend for cliff personality — sharper shoreline transition." )]
	public float CoastalCliffBlendBandMeters { get; set; } = 180f;

	[Property, Group( "Coast" ), Title( "Max Shore Height (m)" ), Range( 5f, 200f ), Step( 5f ), Description( "Cliff coasts cap near this; beaches lerp toward sea level." )]
	public float CoastalMaxShoreHeightMeters { get; set; } = 50f;

	[Property, Group( "Coast" ), Title( "Cliff Threshold (0–1)" ), Range( 0.5f, 0.99f ), Step( 0.01f ), Description( "Coastal personality above this → cliff shore on outer ocean only (~0.92 ≈ 8% cliffs)." )]
	public float CoastalCliffThreshold01 { get; set; } = 0.92f;

	[Property, Group( "Coast" ), Title( "Personality Frequency" ), Range( 0.25f, 8f ), Step( 0.25f ), Description( "Noise scale for beach vs cliff choice along the outer ocean rim." )]
	public float CoastalPersonalityFrequency { get; set; } = 2f;

	[Property, Group( "Coast" ), Title( "Inland Height Margin (0–1)" ), Range( 0.01f, 0.25f ), Step( 0.005f ), Description( "Legacy coastal hint — low land near rim; inland lakes ignore height for water." )]
	public float CoastalInlandHeightMargin01 { get; set; } = 0.12f;

	[Property, Group( "Coast" ), Title( "Inland Lake Shore Band (m)" ), Range( 80f, 2000f ), Step( 25f ), Description( "Dry land within this distance of open lake water eases height toward sea level." )]
	public float CoastalInlandBeachBandMeters { get; set; } = 450f;

	[Property, Group( "Water" ), Title( "Min Speck Diameter (m)" ), Range( 80f, 500f ), Step( 5f ), Description( "Minimum width for lake/water patches and rim-ocean land islands." )]
	public float SpeckMinPatchDiameterMeters { get; set; } = 300f;

	[Property, Group( "Water" ), Title( "Drop Land Islands In Water" ), Description( "Rim ocean only: floods dry land patches in water narrower than Min Speck Diameter (preview raster + chunk mesh)." )]
	public bool LandSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Blend Height At Biome Borders" ), Description( "Soft-cap toward a weight-blended biome max (m) instead of hard per-biome cliffs." )]
	public bool EnableBiomeHeightBlend { get; set; } = true;

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

	[Property, Group( "Biome Terrain" ), Title( "Clover Micro Wavelength (m)" ), Range( 2.5f, 40f ), Step( 0.5f ), Description( "Tiny grit cell size. ~4 m reads on 2 m verts. 0 in old bundles → default 4." )]
	public float BiomeCloverMicroWavelengthMeters { get; set; } = 4f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Micro Amplitude (m)" ), Range( 0.5f, 12f ), Step( 0.25f ), Description( "Surface grit height (±meters). Loud on purpose. 0 in old bundles → default 6." )]
	public float BiomeCloverMicroAmplitudeMeters { get; set; } = 6f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Shape Blend (0–1)" ), Range( 0.25f, 1f ), Step( 0.05f )]
	public float BiomeCloverShapeBlend01 { get; set; } = 0.95f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Slope Smooth (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Widens skirts + flatter ease curve so 400 m cells don't read as steep cones." )]
	public float BiomeCloverSlopeSmooth01 { get; set; } = 0.85f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Grass Tint Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Vertex-color grass mottling so Clover reads clearly on the mesh/stamp." )]
	public float BiomeCloverGrassTintStrength01 { get; set; } = 0.9f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Ground Texture Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "How strongly bush.png modulates Clover Hills chunk vertex colors (streamed terrain only)." )]
	public float BiomeCloverGroundTextureStrength01 { get; set; } = 0.85f;

	[Property, Group( "Biome Terrain" ), Title( "Clover Ground Texture Tile (m)" ), Range( 4f, 64f ), Step( 1f ), Description( "World meters per bush.png tile on Clover ground." )]
	public float BiomeCloverGroundTextureTileMeters { get; set; } = 12f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Hill Frequency" ), Range( 1.5f, 14f ), Step( 0.5f )]
	public float BiomeRedwoodHillFrequency { get; set; } = 6f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Hill Amplitude (0–1)" ), Range( 0.03f, 0.22f ), Step( 0.01f )]
	public float BiomeRedwoodHillAmplitude01 { get; set; } = 0.11f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Ridge Amplitude (0–1)" ), Range( 0.01f, 0.12f ), Step( 0.005f )]
	public float BiomeRedwoodRidgeAmplitude01 { get; set; } = 0.045f;

	[Property, Group( "Biome Terrain" ), Title( "Redwood Slope Smooth (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeRedwoodSlopeSmooth01 { get; set; } = 0.38f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Frequency" ), Range( 0.75f, 8f ), Step( 0.25f )]
	public float BiomeAmberDuneFrequency { get; set; } = 2.2f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Floor (0–1)" ), Range( 0.05f, 0.45f ), Step( 0.01f )]
	public float BiomeAmberDuneFloor01 { get; set; } = 0.14f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Amplitude (0–1)" ), Range( 0.08f, 0.35f ), Step( 0.01f )]
	public float BiomeAmberDuneAmplitude01 { get; set; } = 0.2f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Dune Reshape Blend (0–1)" ), Range( 0.35f, 0.95f ), Step( 0.05f )]
	public float BiomeAmberDuneReshapeBlend01 { get; set; } = 0.78f;

	[Property, Group( "Biome Terrain" ), Title( "Amber Slope Smooth (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeAmberSlopeSmooth01 { get; set; } = 0.82f;

	[Property, Group( "Biome Terrain" ), Title( "Mountain Base Rugged Amp (0–1)" ), Range( 0.02f, 0.14f ), Step( 0.01f )]
	public float BiomeMountainBaseRuggedAmplitude01 { get; set; } = 0.06f;

	[Property, Group( "Biome Terrain" ), Title( "Mountain Slope Smooth (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeMountainSlopeSmooth01 { get; set; } = 0.12f;

	[Property, Group( "Biome Terrain" ), Title( "Summit Flatten Start (0–1 cap)" ), Range( 0.75f, 0.98f ), Step( 0.01f )]
	public float BiomeMountainSummitFlattenStart01 { get; set; } = 0.88f;

	[Property, Group( "Biome Terrain" ), Title( "Summit Flatten Strength (0–1)" ), Range( 0.1f, 0.9f ), Step( 0.05f )]
	public float BiomeMountainSummitFlattenStrength01 { get; set; } = 0.55f;

	[Property, Group( "Biome Terrain" ), Title( "Slope Detail Gate (0–1)" ), Range( 0.03f, 0.35f ), Step( 0.01f )]
	public float BiomeSlopeDetailGate01 { get; set; } = 0.12f;

	[Property, Group( "Biomes" ), Title( "Clover Max Height (m)" ), Range( 20f, 100f ), Step( 5f ), Description( "Hard ceiling for Clover hills (0–100 m)." )]
	public float BiomeCloverMaxHeightMeters { get; set; } = 100f;

	[Property, Group( "Biomes" ), Title( "Redwood Max Height (m)" ), Range( 50f, 1000f ), Step( 10f )]
	public float BiomeRedwoodMaxHeightMeters { get; set; } = 300f;

	[Property, Group( "Biomes" ), Title( "Amber Max Height (m)" ), Range( 50f, 800f ), Step( 10f )]
	public float BiomeAmberMaxHeightMeters { get; set; } = 200f;

	[Property, Group( "Biomes" ), Title( "Mountain Max Height (m)" ), Range( 200f, 2000f ), Step( 25f )]
	public float BiomeMountainMaxHeightMeters { get; set; } = 700f;

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

	[Property, Group( "Biomes" ), Title( "Height Cap Border Sharpness" ), Range( 1f, 4f ), Step( 0.25f ), Description( "Higher = narrower blend at mountain ↔ lowland borders." )]
	public float BiomeHeightCapBorderSharpness { get; set; } = 1.35f;

	[Property, Group( "Biomes" ), Title( "Mountain Min Slope (°)" ), Range( 2f, 30f ), Step( 0.5f ), Description( "Requires peak slope or lift to classify as mountain." )]
	public float BiomeMountainMinSlopeDegrees { get; set; } = 8f;

	[Property, Group( "Biomes" ), Title( "Mountain Min Peak Lift (0–1)" ), Range( 0.04f, 0.5f ), Step( 0.01f )]
	public float BiomeMountainMinPeakLift01 { get; set; } = 0.12f;

	[Property, Group( "Biomes" ), Title( "Mountain Cap Weight Full At" ), Range( 0.1f, 0.8f ), Step( 0.05f ), Description( "Mountain patch weight where height cap fully applies." )]
	public float BiomeMountainCapWeightFullAt01 { get; set; } = 0.35f;

	[Property, Group( "Biomes" ), Title( "Overlay Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeOverlayStrength01 { get; set; } = 0.82f;

	[Property, Group( "Biomes" ), Title( "Shade Noise Frequency" ), Range( 0.5f, 32f ), Step( 0.25f )]
	public float BiomeNoiseFrequency { get; set; } = 6f;

	[Property, Group( "Biomes" ), Title( "Boundary Noise Frequency" ), Range( 0.5f, 32f ), Step( 0.25f )]
	public float BiomeBoundaryNoiseFrequency { get; set; } = 8f;

	[Property, Group( "Biomes" ), Title( "Distance Warp (m)" ), Range( 0f, 2500f ), Step( 25f )]
	public float BiomeDistanceWarpMeters { get; set; } = 0f;

	[Property, Group( "Biomes" ), Title( "Weight Noise Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeWeightNoiseStrength01 { get; set; } = 0f;

	[Property, Group( "Biomes" ), Title( "Scatter Octaves" ), Range( 1, 6 ), Step( 1 )]
	public int BiomeScatterOctaves { get; set; } = 3;

	[Property, Group( "Biomes" ), Title( "Picker Octaves" ), Range( 1, 6 ), Step( 1 )]
	public int BiomePickerOctaves { get; set; } = 3;

	[Property, Group( "Biomes" ), Title( "Patch Frequency" ), Range( 0.5f, 48f ), Step( 0.5f )]
	public float BiomePickerFrequency { get; set; } = 30f;

	[Property, Group( "Biomes" ), Title( "Distance Influence Scale (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeDistanceInfluenceScale01 { get; set; } = 0.25f;

	[Property, Group( "Biomes" ), Title( "Merge Small Patches" ), Description( "Speck merge on PNG export; runtime height uses Min Patch Diameter for weight smoothing." )]
	public bool BiomeSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Min Patch Diameter (m)" ), Range( 10f, 500f ), Step( 5f ), Description( "After lakes and mountains are placed, dry-land biome patches narrower than this merge into neighboring biomes." )]
	public float BiomeMinPatchDiameterMeters { get; set; } = 200f;

	[Property, Group( "Biomes" ), Title( "Mountain Min Height (0–1)" ), Range( 0.2f, 0.95f ), Step( 0.01f )]
	public float BiomeMountainMinHeight01 { get; set; } = 0.34f;

	[Property, Group( "Biomes" ), Title( "Min Mountain Mask (0–1)" ), Range( 0.1f, 0.95f ), Step( 0.01f ), Description( "Mountain Field must exceed this (bright enough) to become white on Mountain Mask / mountain biome." )]
	public float BiomeMinMountainMask01 { get; set; } = 0.38f;

	[Property, Group( "Biomes" ), Title( "Appear Inner Ramp Power" ), Range( 1f, 4f ), Step( 0.25f )]
	public float BiomeAppearInnerRampPower { get; set; } = 2.5f;

	[Property, Group( "Biomes" ), Title( "Guarantee Clover In Spawn Band" )]
	public bool BiomeCloverGuaranteeSpawn { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Spawn Blend End (m)" ), Range( 150f, 5000f ), Step( 25f )]
	public float BiomeSpawnBlendEndMeters { get; set; } = 900f;

	[Property, Group( "Biomes" ), Title( "Spawn Clover Blend Boost (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeSpawnCloverBlendBoost01 { get; set; } = 0.8f;

	[Property, Group( "Biomes" ), Title( "Clover Ramp Full (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverRampFullDistanceMeters { get; set; } = 900f;

	[Property, Group( "Biomes" ), Title( "Clover Map Edge Fade End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverAppearEndMeters { get; set; } = 20000f;

	[Property, Group( "Biomes" ), Title( "Clover Spawn Band Start (m)" ), Range( 0f, 5000f ), Step( 25f )]
	public float BiomeCloverPriorityStartMeters { get; set; }

	[Property, Group( "Biomes" ), Title( "Clover Spawn Band End (m)" ), Range( 0f, 5000f ), Step( 25f )]
	public float BiomeCloverPriorityEndMeters { get; set; } = 150f;

	[Property, Group( "Biomes" ), Title( "Clover Scatter Weight (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeCloverWeight { get; set; } = 0.55f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence Start (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverDistanceInfluenceStartMeters { get; set; } = 150f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeCloverDistanceInfluenceEndMeters { get; set; } = 2000f;

	[Property, Group( "Biomes" ), Title( "Clover Distance Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeCloverPriorityWeight { get; set; } = 0.4f;

	[Property, Group( "Biomes" ), Title( "Redwood Hard Min Distance (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeRedwoodHardMinDistanceMeters { get; set; } = 400f;

	[Property, Group( "Biomes" ), Title( "Redwood Ramp Full (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeRedwoodRampFullDistanceMeters { get; set; } = 900f;

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

	[Property, Group( "Biomes" ), Title( "Amber Hard Min Distance (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberHardMinDistanceMeters { get; set; } = 2000f;

	[Property, Group( "Biomes" ), Title( "Amber Ramp Full (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberRampFullDistanceMeters { get; set; } = 2800f;

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
