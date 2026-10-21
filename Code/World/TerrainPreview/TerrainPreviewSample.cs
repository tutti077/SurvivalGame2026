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
	public float HeightAfterCurve01 { get; init; }
	public float HeightAfterBiomeShape01 { get; init; }
	public float BiomeTransition01 { get; init; }
	public float LakeMask01 { get; init; }
	public bool IsInsideWorld { get; init; }
	public bool IsOnLand { get; init; }
	public bool HasLandWeights { get; init; }
	public TerrainPreviewBiomeResolver.LandBiomeWeights LandWeights { get; init; }
	/// <summary>Nearest sharp biome ledge (Redwood terraces) — lets the chunk mesh snap vertices onto the drop line.</summary>
	public TerrainBiomeLedgeInfo Ledge { get; init; }

	/// <summary>Inside a river channel: the height is the sloped water surface, not ground.</summary>
	public bool RiverWater { get; init; }
}
