namespace Survival;

/// <summary>
/// Designer-facing knobs for the editor terrain noise preview.
/// Shared with future full-world generation via <see cref="TerrainPreviewBackendRegistry"/>.
/// </summary>
public sealed class TerrainPreviewSettings
{
	[Property, Title( "World Diameter (m)" ), Range( 1000, 100000 ), Step( 500 )]
	public float WorldDiameterMeters { get; set; } = 20000f;

	[Property, Title( "Preview Resolution" ), Range( 64, 4096 ), Step( 64 )]
	public int PreviewResolution { get; set; } = 1024;

	[Property, Title( "World Seed" )]
	public int WorldSeed { get; set; } = 1337;

	[Property, Title( "Random Seed Each Generate" )]
	public bool RandomizeSeedOnGenerate { get; set; }

	[Property, Title( "Retry Seeds Until Solved (+1)" )]
	public bool RetrySeedsUntilSolved { get; set; } = true;

	[Property, Group( "Valley Auto" ), Title( "Max Seed Attempts" ), Range( 1, 256 ), Step( 1 )]
	public int ValleyAutoMaxSeedAttempts { get; set; } = 100;

	[Property, Title( "Preview Layer" )]
	public TerrainPreviewMode PreviewMode { get; set; } = TerrainPreviewMode.Biomes;

	[Property, Title( "Show Distance Rings" )]
	public bool ShowPreviewDistanceRings { get; set; }

	[Property, Title( "Distance Ring Interval (m)" ), Range( 250f, 5000f ), Step( 250f )]
	public float PreviewDistanceRingIntervalMeters { get; set; } = 1000f;

	[Property, Group( "Layers" ), Title( "Continental" )]
	public bool EnableContinentalLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Hills" )]
	public bool EnableHillLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Valleys" )]
	public bool EnableValleyLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Height Curve" )]
	public bool EnableHeightCurveLayer { get; set; } = true;

	[Property, Group( "Layers" ), Title( "Mountains" )]
	public bool EnableMountainLayer { get; set; } = true;

	[Property, Group( "Valley Auto" ), Title( "Auto Weight" )]
	public bool EnableValleyOceanAutoWeight { get; set; } = true;

	[Property, Group( "Valley Auto" ), Title( "Auto Frequency (Spawn)" )]
	public bool EnableValleySpawnAutoFrequency { get; set; } = true;

	[Property, Group( "Valley Auto" ), Title( "Exhaustive Grid Search" )]
	public bool EnableValleyAutoExhaustiveSearch { get; set; }

	[Property, Group( "Valley Auto" ), Title( "Reject Seed On Hard Fail" )]
	public bool RejectSeedOnAutoFailure { get; set; } = true;

	[Property, Group( "Valley Auto" ), Title( "Auto Search Timeout (s, 0=off)" ), Range( 0f, 120f ), Step( 5f )]
	public float ValleyAutoSearchTimeoutSeconds { get; set; }

	[Property, Group( "Valley Auto" ), Title( "Max Iterations Per Seed" ), Range( 10, 500 ), Step( 10 )]
	public int ValleyAutoMaxIterationsPerSeed { get; set; } = 100;

	[Property, Group( "Valley Auto" ), Title( "Auto Tune Resolution" ), Range( 64, 1024 ), Step( 64 )]
	public int ValleyAutoTunePreviewResolution { get; set; } = 256;

	[Property, Group( "Continental" ), Title( "Frequency" ), Range( 0.25f, 32f ), Step( 0.05f )]
	public float ContinentalFrequency { get; set; } = 1.75f;

	[Property, Group( "Continental" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f )]
	public float ContinentalWeight { get; set; } = 0.55f;

	[Property, Group( "Hills" ), Title( "Frequency" ), Range( 0.5f, 64f ), Step( 0.1f )]
	public float HillFrequency { get; set; } = 12f;

	[Property, Group( "Hills" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f )]
	public float HillWeight { get; set; } = 0.25f;

	[Property, Group( "Valleys" ), Title( "Frequency (higher = smaller valleys)" ), Range( 0.5f, 64f ), Step( 0.5f )]
	public float ValleyFrequency { get; set; } = 20f;

	[Property, Group( "Valleys" ), Title( "Weight" ), Range( 0f, 2f ), Step( 0.01f )]
	public float ValleyWeight { get; set; } = 0.2f;

	[Property, Group( "Valleys" ), Title( "Auto Weight Step" ), Range( 0.01f, 0.25f ), Step( 0.01f )]
	public float ValleyOceanWeightStep { get; set; } = 0.1f;

	[Property, Group( "Valleys" ), Title( "Min Interior Ocean To Start Auto (0–1)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float ValleyOceanAutoMinInteriorFraction01 { get; set; } = 0.08f;

	[Property, Group( "Valleys" ), Title( "Preferred Max Total Ocean (0–1)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float ValleyOceanAutoMaxTotalFraction01 { get; set; } = 0.13f;

	[Property, Group( "Valleys" ), Title( "Absolute Max Total Ocean (0–1)" ), Range( 0.05f, 0.75f ), Step( 0.01f )]
	public float ValleyOceanAbsoluteMaxTotalFraction01 { get; set; } = 0.25f;

	[Property, Group( "Valleys" ), Title( "Spawn Land Radius (m)" ), Range( 10f, 200f ), Step( 5f )]
	public float ValleySpawnLandRadiusMeters { get; set; } = 50f;

	[Property, Group( "Valleys" ), Title( "Spawn Guard Target Land (0–1)" ), Range( 0.5f, 1f ), Step( 0.01f )]
	public float ValleySpawnMinLandFraction01 { get; set; } = 0.5f;

	[Property, Group( "Valleys" ), Title( "Spawn Land Solve Threshold (0–1)" ), Range( 0.5f, 1f ), Step( 0.01f )]
	public float ValleySpawnAcceptableLandFraction01 { get; set; } = 0.5f;

	[Property, Group( "Valleys" ), Title( "Require Spawn Land Escape" )]
	public bool SpawnRequireLandEscape { get; set; } = true;

	[Property, Group( "Valleys" ), Title( "Spawn Escape Min Distance (m)" ), Range( 100f, 2000f ), Step( 25f )]
	public float SpawnEscapeMinDistanceMeters { get; set; } = 400f;

	[Property, Group( "Valleys" ), Title( "Max Exterior Ocean (0–1)" ), Range( 0.05f, 0.5f ), Step( 0.01f )]
	public float ValleyOceanMaxExteriorFraction01 { get; set; } = 0.17f;

	[Property, Group( "Valleys" ), Title( "Auto Frequency Step" ), Range( 0.5f, 16f ), Step( 0.5f )]
	public float ValleyAutoFrequencyStep { get; set; } = 4f;

	[Property, Group( "Valleys" ), Title( "Auto Frequency Floor" ), Range( 4f, 32f ), Step( 0.5f )]
	public float ValleyAutoFrequencyMin { get; set; } = 16f;

	[Property, Group( "Valleys" ), Title( "Auto Frequency Max" ), Range( 12f, 64f ), Step( 0.5f )]
	public float ValleyAutoFrequencyMax { get; set; } = 40f;

	[Property, Group( "Valleys" ), Title( "Near Water Max Distance (m)" ), Range( 100f, 20000f ), Step( 100f )]
	public float ValleyNearWaterMaxDistanceMeters { get; set; } = 5000f;

	[Property, Group( "Valleys" ), Title( "Inner Half Radius (0–1)" ), Range( 0.25f, 1f ), Step( 0.05f )]
	public float ValleyInnerHalfRadius01 { get; set; } = 0.5f;

	[Property, Group( "Valleys" ), Title( "Inner Half Min Ocean (0–1)" ), Range( 0f, 0.2f ), Step( 0.005f )]
	public float ValleyInnerHalfMinOceanFraction01 { get; set; } = 0.02f;

	[Property, Group( "Mountains" ), Title( "Threshold" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainThreshold { get; set; } = 0.75f;

	[Property, Group( "Mountains" ), Title( "Frequency" ), Range( 0.25f, 32f ), Step( 0.05f )]
	public float MountainFrequency { get; set; } = 8f;

	[Property, Group( "Mountains" ), Title( "Inner Radius (0–1 dist)" ), Range( 0f, 0.99f ), Step( 0.01f )]
	public float MountainInnerRadius01 { get; set; } = 0.1f;

	[Property, Group( "Mountains" ), Title( "Outer Radius (0–1 dist)" ), Range( 0.01f, 1f ), Step( 0.01f )]
	public float MountainOuterRadius01 { get; set; } = 0.85f;

	[Property, Group( "Mountains" ), Title( "Band Edge Fade (0–1 dist)" ), Range( 0.001f, 0.5f ), Step( 0.005f )]
	public float MountainBandFade01 { get; set; } = 0.08f;

	[Property, Group( "Mountains" ), Title( "Falloff Edge Power" ), Range( 0.25f, 4f ), Step( 0.05f )]
	public float MountainFalloffRimPower { get; set; } = 1.35f;

	[Property, Group( "Mountains" ), Title( "Peak Boost" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainPeakBoost { get; set; } = 0.9f;

	[Property, Group( "Mountains" ), Title( "Min Peak Height (0–1)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainMinPeakHeight01 { get; set; } = 0.45f;

	[Property, Group( "Mountains" ), Title( "Peak Variation Frequency" ), Range( 0.25f, 16f ), Step( 0.05f )]
	public float MountainPeakVariationFrequency { get; set; } = 16f;

	[Property, Group( "Mountains" ), Title( "Foothill Spread" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float MountainFoothillSpread { get; set; } = 0.12f;

	[Property, Group( "Mountains" ), Title( "Foothill Boost" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MountainFoothillBoost { get; set; } = 0.25f;

	[Property, Group( "Height Curve" ), Title( "Power" ), Range( 0.5f, 3f ), Step( 0.05f )]
	public float HeightCurvePower { get; set; } = 1.1f;

	[Property, Group( "Water" ), Title( "Interior Water Layer" )]
	public bool EnableInteriorWaterLayer { get; set; } = true;

	[Property, Group( "Water" ), Title( "Interior Water Frequency" ), Range( 0.25f, 16f ), Step( 0.25f )]
	public float InteriorWaterFrequency { get; set; } = 3f;

	[Property, Group( "Water" ), Title( "Interior Water Weight" ), Range( 0f, 2f ), Step( 0.01f )]
	public float InteriorWaterWeight { get; set; }

	[Property, Group( "Water" ), Title( "Interior Water Auto Step" ), Range( 0.01f, 0.2f ), Step( 0.01f )]
	public float InteriorWaterAutoStep { get; set; } = 0.05f;

	[Property, Group( "Water" ), Title( "Interior Water Center Floor (0–1)" ), Range( 0f, 0.5f ), Step( 0.02f )]
	public float InteriorWaterCenterInfluence01 { get; set; } = 0.08f;

	[Property, Group( "Water" ), Title( "Full Influence Radius (0–1 dist)" ), Range( 0.1f, 0.7f ), Step( 0.02f )]
	public float InteriorWaterFullInfluenceRadius01 { get; set; } = 0.35f;

	[Property, Group( "Water" ), Title( "Interior Water Falloff Power" ), Range( 0.15f, 2.5f ), Step( 0.05f )]
	public float InteriorWaterFalloffPower { get; set; } = 0.4f;

	[Property, Group( "Water" ), Title( "Interior Water Edge Fade (0–1 dist)" ), Range( 0.01f, 0.35f ), Step( 0.01f )]
	public float InteriorWaterEdgeFade01 { get; set; } = 0.12f;

	[Property, Group( "Water" ), Title( "Sea Level Height (0–1)" ), Range( 0f, 0.75f ), Step( 0.01f )]
	public float SeaLevelHeight01 { get; set; } = 0.25f;

	[Property, Group( "Water" ), Title( "Target Total Ocean Fraction" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float TargetTotalOceanFraction01 { get; set; } = 0.08f;

	[Property, Group( "Water" ), Title( "Target Interior Ocean Fraction" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float TargetInteriorOceanFraction01 { get; set; } = 0.08f;

	[Property, Group( "Water" ), Title( "Interior Zone Radius (0–1)" ), Range( 0.1f, 0.95f ), Step( 0.01f )]
	public float InteriorZoneRadius01 { get; set; } = 0.7f;

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
	public int BiomeScatterOctaves { get; set; } = 5;

	[Property, Group( "Biomes" ), Title( "Picker Octaves" ), Range( 1, 6 ), Step( 1 )]
	public int BiomePickerOctaves { get; set; } = 5;

	[Property, Group( "Biomes" ), Title( "Patch Frequency" ), Range( 0.5f, 48f ), Step( 0.5f )]
	public float BiomePickerFrequency { get; set; } = 40f;

	[Property, Group( "Biomes" ), Title( "Distance Influence Scale (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeDistanceInfluenceScale01 { get; set; } = 0.25f;

	[Property, Group( "Biomes" ), Title( "Merge Small Patches (PNG preview only)" ), Description( "Inspector/export biome map cleanup in world meters. Does not change streamed terrain meshes." )]
	public bool BiomeSpeckFilterEnabled { get; set; } = true;

	[Property, Group( "Biomes" ), Title( "Min Patch Diameter (m, PNG preview only)" ), Range( 10f, 500f ), Step( 5f ), Description( "World-meter speck merge on exported biome map only." )]
	public float BiomeMinPatchDiameterMeters { get; set; } = 200f;

	[Property, Group( "Biomes" ), Title( "Mountain Min Height (0–1)" ), Range( 0.2f, 0.95f ), Step( 0.01f )]
	public float BiomeMountainMinHeight01 { get; set; } = 0.58f;

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
	public float BiomeRedwoodWeight { get; set; } = 0.48f;

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
	public float BiomeAmberWeight { get; set; } = 0.55f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence Start (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberPriorityStartMeters { get; set; } = 1500f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence End (m)" ), Range( 0f, 20000f ), Step( 50f )]
	public float BiomeAmberPriorityEndMeters { get; set; } = 2500f;

	[Property, Group( "Biomes" ), Title( "Amber Distance Influence (0–1)" ), Range( 0f, 1f ), Step( 0.05f )]
	public float BiomeAmberPriorityWeight { get; set; } = 0.3f;

	public float WorldRadiusMeters => WorldDiameterMeters * 0.5f;

	public float MountainInnerRadiusMeters => MountainInnerRadius01 * WorldRadiusMeters;

	public float MountainOuterRadiusMeters => MountainOuterRadius01 * WorldRadiusMeters;

	public float MountainBandFadeMeters => MountainBandFade01 * WorldRadiusMeters;

	public int ClampedResolution => Math.Clamp( PreviewResolution, 64, 4096 );
}
