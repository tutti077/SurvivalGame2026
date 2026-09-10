using Sandbox;

namespace Survival;

/// <summary>
/// Runs the coalesced local nav rebakes queued by <see cref="BuildNavMeshSync"/> every frame, in
/// every scene. This used to be ticked from <c>TerrainWorldManager</c> only, so in a scene without
/// streamed terrain (testscene1) a placed wall never rebaked nav — agents kept the pre-wall mesh
/// and walked straight through the piece. Entities that walk on and breach structures need the
/// bake to land regardless of which world system the scene runs.
/// </summary>
public sealed class BuildNavBakeSystem : GameObjectSystem
{
	/// <summary>Let physics bodies settle after load before the one-time live generate.</summary>
	const double LiveNavDelaySeconds = 1.0;

	double _liveNavAt = -1d;

	public BuildNavBakeSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.SceneLoaded, 0, OnSceneLoaded, "BuildNavLiveOnLoad" );
		Listen( Stage.FinishUpdate, 0, Tick, "BuildNavBake" );
	}

	void OnSceneLoaded() => _liveNavAt = Time.NowDouble + LiveNavDelaySeconds;

	void Tick()
	{
		if ( Scene is null || !Scene.IsValid() )
			return;

		if ( _liveNavAt >= 0d && Time.NowDouble >= _liveNavAt )
		{
			// False = nav data not loaded yet (empty bounds) — ask again shortly.
			_liveNavAt = BuildNavMeshSync.EnsureLiveNavOnce( Scene ) ? -1d : Time.NowDouble + LiveNavDelaySeconds;
		}

		BuildNavMeshSync.TickFullRegenTiming( Scene );
		BuildNavMeshSync.TickTileVerify( Scene );
		BuildNavMeshSync.TickPendingLocalBakes( Scene );
	}
}
