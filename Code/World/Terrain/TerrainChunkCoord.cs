namespace Survival;

public readonly struct TerrainChunkCoord : IEquatable<TerrainChunkCoord>
{
	public int X { get; init; }
	public int Y { get; init; }

	public TerrainChunkCoord( int x, int y )
	{
		X = x;
		Y = y;
	}

	public bool Equals( TerrainChunkCoord other ) => X == other.X && Y == other.Y;

	public override bool Equals( object obj ) => obj is TerrainChunkCoord other && Equals( other );

	public override int GetHashCode() => HashCode.Combine( X, Y );

	public static bool operator ==( TerrainChunkCoord a, TerrainChunkCoord b ) => a.Equals( b );

	public static bool operator !=( TerrainChunkCoord a, TerrainChunkCoord b ) => !a.Equals( b );

	public override string ToString() => $"({X},{Y})";
}
