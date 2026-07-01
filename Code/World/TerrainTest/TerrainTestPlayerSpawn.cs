using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>
/// Terrain-test only: press SpawnPlayer (J) to clone a basic player at the fly camera transform (scale reference).
/// Fly camera stays active; each press adds another pawn — previous dolls are kept.
/// </summary>
[Title( "Terrain Test Player Spawn" )]
public sealed class TerrainTestPlayerSpawn : Component
{
	[Property, Title( "View camera (fly cam)" )] public GameObject ViewCamera { get; set; }

	[Property] public string PlayerPrefabPath { get; set; } = "prefabs/player/basicplayer.prefab";

	[Property] public bool DisableFallDamage { get; set; } = true;

	readonly List<GameObject> _spawnedPlayers = new();
	CameraComponent _flyCamera;

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		PruneDestroyedSpawns();
		if ( _spawnedPlayers.Count > 0 )
			MaintainFlyCamera();

		if ( !Input.Pressed( "SpawnPlayer" ) )
			return;

		SpawnAtCamera();
	}

	void SpawnAtCamera()
	{
		var view = ResolveViewCamera();
		if ( view is null || !view.IsValid() )
		{
			Log.Warning( "[TerrainTestPlayerSpawn] No view camera — assign FlyCamera or tag a CameraComponent." );
			return;
		}

		var scene = Scene;
		if ( !scene.IsValid() )
			return;

		var instance = BuildPrefabUtility.GetTemplate( PlayerPrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[TerrainTestPlayerSpawn] Failed to clone '{PlayerPrefabPath}'." );
			return;
		}

		instance.Parent = scene;
		instance.WorldPosition = view.WorldPosition;
		instance.WorldRotation = view.WorldRotation;

		ConfigureScaleReferencePawn( instance );

		instance.Enabled = true;
		_spawnedPlayers.Add( instance );

		_flyCamera = view.Components.Get<CameraComponent>();
		AssertFlyCameraMain( view );

		Log.Info( $"[TerrainTestPlayerSpawn] Spawned player #{_spawnedPlayers.Count} at camera transform {instance.WorldPosition}." );
	}

	void PruneDestroyedSpawns()
	{
		for ( var i = _spawnedPlayers.Count - 1; i >= 0; i-- )
		{
			if ( !_spawnedPlayers[i].IsValid() )
				_spawnedPlayers.RemoveAt( i );
		}
	}

	GameObject ResolveViewCamera()
	{
		if ( ViewCamera is { IsValid: true } )
			return ViewCamera;

		var scene = Scene;
		if ( !scene.IsValid() )
			return null;

		foreach ( var cam in scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() || !cam.Enabled )
				continue;

			if ( cam.GameObject.Tags.Has( "maincamera" ) )
				return cam.GameObject;
		}

		return scene.Camera?.GameObject;
	}

	void ConfigureScaleReferencePawn( GameObject root )
	{
		StripCameraDrivingSystems( root );

		if ( DisableFallDamage )
			DisableImpactDamage( root );

		EnsureRigidbodyFalls( root );
	}

	static void StripCameraDrivingSystems( GameObject root )
	{
		DestroyComponent<PlayerController>( root );
		DestroyComponent<PlayerGameMenuController>( root );
		DestroyComponent<PlayerScreenHud>( root );
		DestroyComponent<ScreenPanel>( root );
		DestroyComponent<MoveModeWalk>( root );
		DestroyComponent<MoveModeSwim>( root );
		DestroyComponent<MoveModeLadder>( root );

		foreach ( var cam in root.Components.GetAll<CameraComponent>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( cam is null || !cam.IsValid() )
				continue;

			cam.Destroy();
		}
	}

	static void EnsureRigidbodyFalls( GameObject root )
	{
		var body = root.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndDescendants );
		if ( body is null || !body.IsValid() )
			return;

		body.MotionEnabled = true;
		body.Gravity = true;
	}

	static void DestroyComponent<T>( GameObject root ) where T : Component
	{
		foreach ( var component in root.Components.GetAll<T>( FindMode.EverythingInSelfAndDescendants ) )
			component?.Destroy();
	}

	static void DisableImpactDamage( GameObject root )
	{
		foreach ( var body in root.Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is null || !body.IsValid() )
				continue;

			body.EnableImpactDamage = false;
		}
	}

	void MaintainFlyCamera()
	{
		if ( _flyCamera is null || !_flyCamera.IsValid() )
		{
			var view = ResolveViewCamera();
			if ( view is null )
				return;

			_flyCamera = view.Components.Get<CameraComponent>();
		}

		if ( _flyCamera is null || !_flyCamera.IsValid() )
			return;

		AssertFlyCameraMain( _flyCamera.GameObject );

		var fly = _flyCamera.Components.Get<TerrainTestFlyCamera>();
		if ( fly is not null && fly.IsValid() && fly.InputLocked )
			fly.SetInputLocked( false );
	}

	static void AssertFlyCameraMain( GameObject view )
	{
		var flyCam = view.Components.Get<CameraComponent>();
		if ( flyCam is null || !flyCam.IsValid() )
			return;

		flyCam.Enabled = true;
		flyCam.IsMainCamera = true;

		var scene = view.Scene;
		if ( !scene.IsValid() )
			return;

		foreach ( var cam in scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() || cam == flyCam )
				continue;

			cam.IsMainCamera = false;
		}
	}
}
