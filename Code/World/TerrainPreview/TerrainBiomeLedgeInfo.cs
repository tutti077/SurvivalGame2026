namespace Survival;

/// <summary>
/// Where a sample sits relative to the nearest sharp biome ledge (Redwood terraces today). The chunk
/// mesh builder uses it to slide the vertices on either side of a ledge onto the ledge line so the
/// drop renders as a true vertical face instead of a 2 m-grid staircase.
/// </summary>
public readonly struct TerrainBiomeLedgeInfo
{
	public static readonly TerrainBiomeLedgeInfo None = default;

	/// <summary>True when a sharp (unramped, unblended) ledge line runs within a few metres.</summary>
	public bool Present { get; init; }

	/// <summary>Signed ground distance to the ledge line in metres; positive on the uphill (upper) side.</summary>
	public float DistanceMeters { get; init; }

	/// <summary>Unit ground direction in which <see cref="DistanceMeters"/> increases (toward the upper side).</summary>
	public float DirectionX { get; init; }

	/// <summary>See <see cref="DirectionX"/>.</summary>
	public float DirectionY { get; init; }

	/// <summary>Half the horizontal width of the drop in the sampled field: sampling this far past the line lands on the full upper / lower height.</summary>
	public float HalfFaceWidthMeters { get; init; }
}
