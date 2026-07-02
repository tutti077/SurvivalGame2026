namespace Survival;

/// <summary>Intermediate layers at one world position for preview export.</summary>
public readonly struct TerrainPreviewSample
{
	public float Height01 { get; init; }
	/// <summary>Final world-meter elevation after the full pipeline (use for meshes — avoids 01 round-trip).</summary>
	public float HeightMeters { get; init; }
	public float OceanHeight01 { get; init; }
	public float ContinentalNoise01 { get; init; }
	public float HillsNoise01 { get; init; }
	public float ValleysNoise01 { get; init; }
	public float BaseHeightBeforeCurve01 { get; init; }
	public float HeightAfterCurve01 { get; init; }
	public float HeightAfterBiomeShape01 { get; init; }
	public float TerrainDetail01 { get; init; }
	public float BiomeTransition01 { get; init; }
	public float MountainMask01 { get; init; }
	public float MountainField01 { get; init; }
	public float MountainFalloff01 { get; init; }
	public float MountainPeakHeight01 { get; init; }
	public float MountainFoothillLift01 { get; init; }
	public float MountainSlopeDegrees { get; init; }
	public float LakeMask01 { get; init; }
	public bool IsInsideWorld { get; init; }
	public bool IsOnLand { get; init; }
	public bool HasLandWeights { get; init; }
	public TerrainPreviewBiomeResolver.LandBiomeWeights LandWeights { get; init; }
}
