using System.Collections.Generic;
using Sandbox;
using Sandbox.Navigation;

namespace Survival;

/// <summary>Logs enemy nav/AI to console on change only. Flip <see cref="Enabled"/> to false to silence.</summary>
[Title( "Nav Mesh Debug Draw" )]
public sealed class NavMeshDebugDraw : Component
{
	/// <summary>Set false when you no longer want [NavDebug] console output.</summary>
	public static bool Enabled = true;

	const double MinLogGapSeconds = 4.0;

	static readonly Dictionary<int, string> _lastSnapshot = new();
	static readonly Dictionary<int, double> _lastLogAt = new();

	PlayerVitals _vitals;
	double _nextScanAt;

	protected override void OnStart()
	{
		_vitals = FindOnAncestors<PlayerVitals>();
	}

	protected override void OnUpdate()
	{
		if ( !Enabled || _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
			return;

		if ( Time.NowDouble < _nextScanAt )
			return;

		_nextScanAt = Time.NowDouble + 1.5;
		LogEnemyNavigation();
	}

	void LogEnemyNavigation()
	{
		var scene = Scene;
		if ( !scene.IsValid() )
			return;

		var now = Time.NowDouble;
		var navEnabled = scene.NavMesh is { IsEnabled: true };
		var navGenerating = BuildNavMeshSync.IsNavGenerating( scene );

		var count = 0;
		foreach ( var brain in scene.GetAllComponents<EntityBrain>() )
		{
			if ( brain is null || !brain.GameObject.IsValid() )
				continue;

			count++;
			TryLogBrain( brain, navEnabled, navGenerating, now );
		}

		if ( count == 0 && ShouldLogGlobal( "noBrains", now ) )
			Log.Info( $"[NavDebug] No EntityBrain in scene. nav={navEnabled} generating={navGenerating}" );
	}

	void TryLogBrain( EntityBrain brain, bool navEnabled, bool navGenerating, double now )
	{
		var go = brain.GameObject;
		var id = go.GetHashCode();
		var agent = brain.Agent ?? go.Components.Get<NavMeshAgent>();
		var isNavigating = agent is { IsValid: true } && agent.IsNavigating;

		var snapshot =
			$"{brain.CurrentState}|{brain.LastNavBlockReason}|{brain.LastPathStatus}|{isNavigating}|{brain.LastNavGoal}";

		_lastSnapshot.TryGetValue( id, out var previous );
		_lastLogAt.TryGetValue( id, out var lastAt );

		var changed = previous != snapshot;
		var heartbeat = now - lastAt >= MinLogGapSeconds;
		if ( !changed && !heartbeat )
			return;

		_lastSnapshot[id] = snapshot;
		_lastLogAt[id] = now;
		LogBrain( brain, navEnabled, navGenerating, isNavigating );
	}

	static bool ShouldLogGlobal( string key, double now )
	{
		var id = key.GetHashCode();
		_lastLogAt.TryGetValue( id, out var lastAt );
		if ( now - lastAt < MinLogGapSeconds )
			return false;

		_lastLogAt[id] = now;
		return true;
	}

	void LogBrain( EntityBrain brain, bool navEnabled, bool navGenerating, bool isNavigating )
	{
		var go = brain.GameObject;
		var agent = brain.Agent ?? go.Components.Get<NavMeshAgent>();
		var locomotion = brain.Locomotion ?? go.Components.Get<EntityLocomotion>();
		var rootPos = go.WorldPosition;
		var agentPos = agent is { IsValid: true } ? agent.AgentPosition : rootPos;
		var wishSpeed = agent is { IsValid: true } ? agent.WishVelocity.Length : 0f;

		var blockers = new System.Text.StringBuilder();
		if ( locomotion is not null && locomotion.IsAirborne )
			blockers.Append( "airborne " );
		if ( navGenerating )
			blockers.Append( "navGenerating " );
		if ( !navEnabled )
			blockers.Append( "navOff " );

		var targetName = brain.ChaseTarget.IsValid() ? brain.ChaseTarget.Name : "none";
		var pathPts = brain.LastPathPoints?.Count ?? 0;

		Log.Info(
			$"[NavDebug] {go.Name} state={brain.CurrentState} nav={isNavigating} wishSpd={wishSpeed:0} " +
			$"path={brain.LastPathStatus} pts={pathPts} lastNav={brain.LastNavBlockReason} " +
			$"root={FormatVec( rootPos )} goal={FormatVec( brain.LastNavGoal )} target={targetName} [{blockers}]" );
	}

	static string FormatVec( Vector3 v ) => $"({v.x:0}, {v.y:0}, {v.z:0})";

	T FindOnAncestors<T>() where T : Component
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var c = go.Components.Get<T>();
			if ( c is not null )
				return c;
		}

		return null;
	}
}
