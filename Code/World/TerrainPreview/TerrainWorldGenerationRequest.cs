namespace Survival;

public readonly struct TerrainWorldGenerationRequest
{
	public int WorldSeed { get; init; }
	public float WorldDiameterMeters { get; init; }
	public float MaxTerrainHeightMeters { get; init; }
	public float OceanRingWidthMeters { get; init; }
	public string WorldName { get; init; }
	public TerrainWorldSettingsSource Source { get; init; }
	public bool OverrideWorldScalarsFromComponent { get; init; }
	public bool RunLakeSpawnSolveOnLoad { get; init; }
}
