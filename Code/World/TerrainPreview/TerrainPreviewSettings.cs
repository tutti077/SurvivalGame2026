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
	public TerrainPreviewMode PreviewMode { get; set; } = TerrainPreviewMode.World;

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
	public float MountainInnerRadius01 { get; set; } = 0.3f;

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

	public float WorldRadiusMeters => WorldDiameterMeters * 0.5f;

	public float MountainInnerRadiusMeters => MountainInnerRadius01 * WorldRadiusMeters;

	public float MountainOuterRadiusMeters => MountainOuterRadius01 * WorldRadiusMeters;

	public float MountainBandFadeMeters => MountainBandFade01 * WorldRadiusMeters;

	public int ClampedResolution => Math.Clamp( PreviewResolution, 64, 4096 );
}
