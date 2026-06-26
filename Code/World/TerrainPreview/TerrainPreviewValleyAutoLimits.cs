namespace Survival;

[Flags]
public enum ValleyAutoUnmetGoal
{
	None = 0,
	SpawnLand = 1 << 0,
	InteriorOcean = 1 << 1,
	TotalOceanTooHigh = 1 << 2,
	NearWater = 1 << 3,
	InnerHalfOcean = 1 << 4,
	AbsoluteTotalOceanExceeded = 1 << 5,
	ExteriorOceanExceeded = 1 << 6,
	SpawnLandlocked = 1 << 7,
}

[Flags]
public enum ValleyAutoLimitHit
{
	None = 0,
	MaxValleyWeight = 1 << 0,
	MinValleyWeight = 1 << 1,
	MaxValleyFrequency = 1 << 2,
	TotalOceanCap = 1 << 3,
	GreedyIterationCap = 1 << 4,
	WaterSearchRange = 1 << 5,
	GridExhausted = 1 << 6,
	AbsoluteTotalOceanCap = 1 << 7,
	SearchTimedOut = 1 << 8,
	SearchIterationCap = 1 << 9,
	MaxExteriorOcean = 1 << 10,
	MaxInteriorWaterWeight = 1 << 11,
}

public static class TerrainPreviewValleyAutoLimits
{
	public static string FormatUnmetGoals( ValleyAutoUnmetGoal goals )
	{
		if ( goals == ValleyAutoUnmetGoal.None )
			return "none";

		var parts = new List<string>( 5 );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.SpawnLand ) )
			parts.Add( "spawn below solve threshold" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.SpawnLandlocked ) )
			parts.Add( "spawn surrounded by ocean" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.InteriorOcean ) )
			parts.Add( "interior ocean" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.TotalOceanTooHigh ) )
			parts.Add( "preferred total ocean" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.AbsoluteTotalOceanExceeded ) )
			parts.Add( "absolute total ocean" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.ExteriorOceanExceeded ) )
			parts.Add( "rim ocean over cap" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.NearWater ) )
			parts.Add( "near water" );
		if ( goals.HasFlag( ValleyAutoUnmetGoal.InnerHalfOcean ) )
			parts.Add( "inner-half ocean" );

		return string.Join( ", ", parts );
	}

	public static string FormatLimitsHit( ValleyAutoLimitHit limits )
	{
		if ( limits == ValleyAutoLimitHit.None )
			return "none";

		var parts = new List<string>( 6 );
		if ( limits.HasFlag( ValleyAutoLimitHit.MaxValleyWeight ) )
			parts.Add( "max weight" );
		if ( limits.HasFlag( ValleyAutoLimitHit.MinValleyWeight ) )
			parts.Add( "min weight" );
		if ( limits.HasFlag( ValleyAutoLimitHit.MaxValleyFrequency ) )
			parts.Add( "max frequency" );
		if ( limits.HasFlag( ValleyAutoLimitHit.TotalOceanCap ) )
			parts.Add( "preferred total ocean" );
		if ( limits.HasFlag( ValleyAutoLimitHit.AbsoluteTotalOceanCap ) )
			parts.Add( "absolute total ocean" );
		if ( limits.HasFlag( ValleyAutoLimitHit.GreedyIterationCap ) )
			parts.Add( "iteration cap" );
		if ( limits.HasFlag( ValleyAutoLimitHit.WaterSearchRange ) )
			parts.Add( "water search range" );
		if ( limits.HasFlag( ValleyAutoLimitHit.GridExhausted ) )
			parts.Add( "grid exhausted" );
		if ( limits.HasFlag( ValleyAutoLimitHit.SearchTimedOut ) )
			parts.Add( "search timed out" );
		if ( limits.HasFlag( ValleyAutoLimitHit.SearchIterationCap ) )
			parts.Add( "iteration cap" );
		if ( limits.HasFlag( ValleyAutoLimitHit.MaxExteriorOcean ) )
			parts.Add( "max rim ocean" );
		if ( limits.HasFlag( ValleyAutoLimitHit.MaxInteriorWaterWeight ) )
			parts.Add( "max interior water" );

		return string.Join( ", ", parts );
	}
}
