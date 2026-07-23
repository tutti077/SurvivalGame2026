using System;
using Sandbox;

namespace Survival;

/// <summary>Throttled console diagnostics for entity AI state (debug only).</summary>
public static class EntityPerceptionDebug
{
	public static bool Enabled { get; set; } = true;

	/// <summary>Heartbeat interval when state is unchanged.</summary>
	public static float IntervalSeconds { get; set; } = 1.5f;

	static double _nextPlayerLogAt;
	static double _nextBrainLogAt;
	static double _nextNoiseLogAt;
	static string _lastBrainStateKey = "";

	public static void LogPlayer( string message )
	{
		if ( !Enabled || Time.NowDouble < _nextPlayerLogAt )
			return;

		_nextPlayerLogAt = Time.NowDouble + IntervalSeconds;
		Log.Info( $"[EntSense:Player] {message}" );
	}

	/// <summary>Logs immediately on state change; otherwise at most once per <see cref="IntervalSeconds"/>.</summary>
	public static void LogBrainState( string entityName, string state, string detail = null )
	{
		if ( !Enabled )
			return;

		var key = $"{entityName}|{state}";
		var changed = !string.Equals( key, _lastBrainStateKey, StringComparison.Ordinal );
		if ( !changed && Time.NowDouble < _nextBrainLogAt )
			return;

		_lastBrainStateKey = key;
		_nextBrainLogAt = Time.NowDouble + IntervalSeconds;
		Log.Info( string.IsNullOrWhiteSpace( detail )
			? $"[EntSense] {entityName} → {state}"
			: $"[EntSense] {entityName} → {state} ({detail})" );
	}

	public static void LogBrain( string message )
	{
		if ( !Enabled || Time.NowDouble < _nextBrainLogAt )
			return;

		_nextBrainLogAt = Time.NowDouble + IntervalSeconds;
		Log.Info( $"[EntSense:Brain] {message}" );
	}

	public static void LogNoise( string message )
	{
		if ( !Enabled || Time.NowDouble < _nextNoiseLogAt )
			return;

		_nextNoiseLogAt = Time.NowDouble + IntervalSeconds;
		Log.Info( $"[EntSense:Noise] {message}" );
	}

	public static void LogLos( string message )
	{
		// LOS traces are hot-path — keep off unless someone explicitly calls with Enabled + long interval.
	}
}
