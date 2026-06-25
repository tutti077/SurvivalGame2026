namespace Survival;

/// <summary>Counts full preview-map raster passes during a generate session (auto tuning + final image).</summary>
public static class TerrainPreviewMapIterationTracker
{
	static int _sessionActive;
	static int _count;
	static int _totalCount;
	static int _maxIterations;
	static double _sessionStartGlobalNow;
	static float _timeoutSeconds;
	static bool _timedOut;
	static bool _iterationCapped;
	static int _currentSeedAttempt;
	static int _maxSeedAttempts;

	public static int Count => _count;

	public static int TotalCount => _totalCount;

	public static int MaxIterations => _maxIterations;

	public static int CurrentSeedAttempt => _currentSeedAttempt;

	public static int MaxSeedAttempts => _maxSeedAttempts;

	public static bool TimedOut => _timedOut;

	public static bool IterationCapped => _iterationCapped;

	public static float ElapsedSeconds
		=> _sessionActive == 0 ? 0f : (float)( RealTime.GlobalNow - _sessionStartGlobalNow );

	public static void ResetTotal()
	{
		_totalCount = 0;
		_currentSeedAttempt = 0;
		_maxSeedAttempts = 0;
	}

	public static void BeginSeedSearch( int maxSeedAttempts )
	{
		_maxSeedAttempts = Math.Max( 1, maxSeedAttempts );
		_currentSeedAttempt = 0;
	}

	public static void NotifySeedAttempt( int attemptOneBased )
		=> _currentSeedAttempt = Math.Max( 0, attemptOneBased );

	public static IDisposable BeginSession( float timeoutSeconds = 0f, int maxIterations = 0 )
	{
		_count = 0;
		_sessionActive = 1;
		_timedOut = false;
		_iterationCapped = false;
		_timeoutSeconds = Math.Max( 0f, timeoutSeconds );
		_maxIterations = Math.Max( 0, maxIterations );
		_sessionStartGlobalNow = RealTime.GlobalNow;
		return new SessionScope();
	}

	public static bool IsAbortRequested
	{
		get
		{
			if ( _sessionActive == 0 )
				return false;

			if ( _timedOut || _iterationCapped )
				return true;

			if ( _maxIterations > 0 && _count >= _maxIterations )
			{
				_iterationCapped = true;
				return true;
			}

			if ( _timeoutSeconds > 0f && ElapsedSeconds >= _timeoutSeconds )
			{
				_timedOut = true;
				return true;
			}

			return false;
		}
	}

	public static void NotifyMapRasterized()
	{
		if ( _sessionActive == 0 || IsAbortRequested )
			return;

		_count++;
		_totalCount++;
	}

	sealed class SessionScope : IDisposable
	{
		public void Dispose() => _sessionActive = 0;
	}
}
