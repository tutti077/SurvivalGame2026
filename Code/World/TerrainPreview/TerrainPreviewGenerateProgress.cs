namespace Survival;

/// <summary>Coarse stage text for editor generate status (polled from background work).</summary>
public static class TerrainPreviewGenerateProgress
{
	static string _stage = "";
	static int _seedAttempt;
	static int _maxSeeds;
	static int _offsetIndex;
	static int _offsetTotal;
	static int _rasterRow;
	static int _rasterRows;

	public static void Reset()
	{
		_stage = "";
		_seedAttempt = 0;
		_maxSeeds = 0;
		_offsetIndex = 0;
		_offsetTotal = 0;
		_rasterRow = 0;
		_rasterRows = 0;
	}

	public static void SetStage( string stage ) => _stage = stage ?? "";

	public static void ReportSeedSearch( int attemptOneBased, int maxSeeds )
	{
		_seedAttempt = attemptOneBased;
		_maxSeeds = maxSeeds;
	}

	public static void ReportOffsetSearch( int indexOneBased, int total )
	{
		_offsetIndex = indexOneBased;
		_offsetTotal = total;
	}

	public static void ReportRaster( int row, int rows )
	{
		_rasterRow = row;
		_rasterRows = rows;
	}

	public static string FormatStatusLine( int worldSeed )
	{
		if ( TerrainPreviewMapIterationTracker.UserAbortRequested )
			return "Cancelling…";

		var stage = string.IsNullOrWhiteSpace( _stage ) ? "Working" : _stage;
		var line = $"Generating… {stage} · seed {worldSeed}";

		if ( _maxSeeds > 1 && _seedAttempt > 0 )
			line += $" · seed try {_seedAttempt}/{_maxSeeds}";

		if ( _offsetTotal > 0 && _offsetIndex > 0 )
			line += $" · offset {_offsetIndex}/{_offsetTotal}";

		if ( _rasterRows > 0 )
			line += $" · raster {_rasterRow}/{_rasterRows}";

		return line;
	}

	public static bool ShouldAbort()
		=> TerrainPreviewMapIterationTracker.UserAbortRequested;
}
