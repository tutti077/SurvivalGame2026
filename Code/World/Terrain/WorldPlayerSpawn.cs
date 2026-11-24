using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>
/// main.scene: the player's pawn, spawned once the world has loaded and the load screen has gone (host / offline,
/// once per scene load). Mark 2026-11-10: "when i load into the world i want it to do the stasis pod crash landing
/// and start from there, main should NOT use the flycamera ever" - the scene camera has no fly control, the pawn's
/// controller owns it from the frame the pawn exists. terrainTest keeps <see cref="TerrainTestPlayerSpawn"/> (L).
/// <para>
/// <b>Position save</b> (Mark 2026-11-11: "add player position saver"): where the pawn stands goes to the player's
/// <see cref="PlayerWorldSave"/> for this save's game ID - every <see cref="SaveIntervalSeconds"/> when it has moved,
/// and when the scene ends (quit to menu). The first visit to a save (no file) arrives by stasis pod; every later
/// load puts the pawn back where it stood, and the initial terrain load streams around that spot
/// (<see cref="TryGetStartMeters"/>) so the ground is there under it.
/// </para>
/// </summary>
[Title( "World Player Spawn" )]
public sealed class WorldPlayerSpawn : Component
{
	[Property] public string PlayerPrefabPath { get; set; } = "prefabs/player/basicplayer.prefab";

	[Property, Title( "Position save interval (s)" ), Range( 1f, 60f ), Step( 1f )]
	public float SaveIntervalSeconds { get; set; } = 5f;

	/// <summary>A save is only rewritten once the pawn has moved this far (m) since the last one.</summary>
	const float SaveMinMoveMeters = 0.5f;

	static GameObject _localPawn;
	static GameObject _podPassenger;

	/// <summary>The pawn this spawned (null before the world is ready) - the one real player of an offline main.scene.</summary>
	public static GameObject LocalPawn => _localPawn is { IsValid: true } ? _localPawn : null;

	/// <summary>The local pawn when it arrives by pod (first visit to this save); null when it was put back at its saved spot.</summary>
	public static GameObject PodPassenger => _podPassenger is { IsValid: true } ? _podPassenger : null;

	bool _spawned;
	bool _savedResolved;
	PlayerWorldSave _saved;
	TerrainWorldManager _manager;
	TimeUntil _nextSave;
	Vector3 _lastSavedPos;
	/// <summary>Where the pawn stood last frame (out of the pod): the scene-end save uses it, the pawn may already be gone by then.</summary>
	Vector3 _livePos;
	float _liveYaw;
	bool _hasLive;

	/// <summary>Host: where this player last stood in this save (metres, xy), for the initial terrain load. False on a first visit.</summary>
	public bool TryGetStartMeters( TerrainWorldManager manager, out Vector2 xyMeters )
	{
		xyMeters = default;
		var saved = ResolveSaved( manager );
		if ( saved is null )
			return false;

		var meters = TerrainWorldUnits.EngineToMeters( saved.Position );
		xyMeters = new Vector2( meters.x, meters.y );
		return true;
	}

	PlayerWorldSave ResolveSaved( TerrainWorldManager manager )
	{
		if ( _savedResolved || manager is null || string.IsNullOrWhiteSpace( manager.WorldId ) )
			return _saved;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return null;

		_savedResolved = true;
		_saved = PlayerWorldSave.Load( QuestTracker.PlayerKey, manager.WorldId );
		return _saved;
	}

	protected override void OnUpdate()
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( _spawned )
		{
			TrackPawn();
			if ( _nextSave <= 0f )
			{
				_nextSave = SaveIntervalSeconds;
				SavePosition( force: false );
			}
			return;
		}

		_manager ??= Scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( _manager is null || !_manager.IsValid() || !_manager.IsWorldReady )
			return;

		_spawned = true;
		Spawn( _manager );
	}

	/// <summary>Scene ends (quit to menu, load another save): write where the pawn stands.</summary>
	protected override void OnDestroy()
	{
		if ( _spawned && (GameObject.Network is not { Active: true } || Networking.IsHost) )
			SavePosition( force: true );
		base.OnDestroy();
	}

	void TrackPawn()
	{
		var pawn = LocalPawn;
		if ( pawn is null )
			return;
		// Riding in the pod: nothing to save yet (no file = the next load flies in again).
		if ( pawn.Components.Get<PlayerMovement>() is { PodLocked: true } )
			return;

		_livePos = pawn.WorldPosition;
		_liveYaw = pawn.Components.Get<PlayerController>()?.EyeAngles.yaw ?? pawn.WorldRotation.Yaw();
		_hasLive = true;
	}

	void SavePosition( bool force )
	{
		if ( !_hasLive || _manager is null || string.IsNullOrWhiteSpace( _manager.WorldId ) )
			return;
		if ( !force && _livePos.Distance( _lastSavedPos ) < TerrainWorldUnits.MetersToEngine( SaveMinMoveMeters ) )
			return;

		if ( PlayerWorldSave.Save( QuestTracker.PlayerKey, _manager.WorldId, _manager.WorldName, _livePos, _liveYaw ) )
			_lastSavedPos = _livePos;
	}

	void Spawn( TerrainWorldManager manager )
	{
		var instance = BuildPrefabUtility.GetTemplate( PlayerPrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[WorldPlayerSpawn] Failed to clone '{PlayerPrefabPath}'." );
			return;
		}

		instance.Name = "Player";
		instance.NetworkMode = NetworkMode.Never;
		instance.Parent = Scene;

		var saved = ResolveSaved( manager );
		var controller = instance.Components.Get<PlayerController>( FindMode.EverythingInSelfAndDescendants );
		Vector3 at;
		if ( saved is not null )
		{
			// Back where it stood (a hair up so the capsule never starts in the ground); no pod, no dark spawn grace.
			at = saved.Position;
			instance.WorldPosition = at + Vector3.Up * 4f;
			instance.WorldRotation = Rotation.FromYaw( saved.Yaw );
			if ( controller is not null && controller.IsValid() )
				controller.EyeAngles = new Angles( 0f, saved.Yaw, 0f );
			instance.Components.Get<PlayerMovement>( FindMode.EverythingInSelfAndDescendants )?.SkipPodSpawnGrace();
			_podPassenger = null;
		}
		else
		{
			// First visit: the pod spawner picks the pawn up within half a second and parents it into the pod; until
			// then it waits (in the dark - PlayerMovement's spawn grace) on the spawn centre.
			at = StasisPodSpawner.Current is { IsValid: true } spawner ? spawner.Centre() : Vector3.Zero;
			instance.WorldPosition = at + Vector3.Up * 40f;
			instance.WorldRotation = Rotation.Identity;
			_podPassenger = instance;
		}

		if ( controller is not null && controller.IsValid() )
		{
			controller.Enabled = true;
			controller.UseInputControls = true;
			controller.UseCameraControls = true;
			controller.UseLookControls = true;
		}

		instance.Enabled = true;
		_localPawn = instance;
		_lastSavedPos = at;
		_nextSave = SaveIntervalSeconds;
		manager.InvalidateMinimapHostCache();
		Log.Info( saved is not null
			? $"[WorldPlayerSpawn] Player back at saved {at} (yaw {saved.Yaw:0}) in '{manager.WorldName}' ({manager.WorldId}), saved {saved.SavedUtc}."
			: $"[WorldPlayerSpawn] First visit to '{manager.WorldName}' ({manager.WorldId}) - {(StasisPodSpawner.Current is { IsValid: true } && GameHacks.PodArrivals ? "arriving by pod" : "no pod spawner / arrivals off")}." );
	}
}
