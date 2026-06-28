using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>
/// Terrain-test only: press SpawnPlayer (J) to drop a basic player directly below the fly camera for scale reference.
/// Fly camera stays active; spawned pawn does not take fall damage.
/// </summary>
[Title( "Terrain Test Player Spawn" )]
public sealed class TerrainTestPlayerSpawn : Component
{
	[Property, Title( "View camera (fly cam)" )] public GameObject ViewCamera { get; set; }

	[Property] public string PlayerPrefabPath { get; set; } = "prefabs/player/basicplayer.prefab";

	[Property, Title( "Drop distance below camera (m)" ), Range( 16f, 2000f ), Step( 8f )]
	public float SpawnDropDistanceMeters { get; set; } = 128f;

	[Property] public bool DisableFallDamage { get; set; } = true;

	GameObject _spawnedPlayer;
	CameraComponent _flyCamera;

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		if ( _spawnedPlayer is { IsValid: true } )
			MaintainFlyCamera();

		if ( !Input.Pressed( "SpawnPlayer" ) )
			return;

		SpawnBelowCamera();
	}

	void SpawnBelowCamera()
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

		if ( _spawnedPlayer is { IsValid: true } )
		{
			_spawnedPlayer.Destroy();
			_spawnedPlayer = null;
		}

		var instance = BuildPrefabUtility.GetTemplate( PlayerPrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[TerrainTestPlayerSpawn] Failed to clone '{PlayerPrefabPath}'." );
			return;
		}

		instance.Parent = scene;
		instance.WorldPosition = view.WorldPosition - (Vector3.Up * SpawnDropDistanceMeters);
		instance.WorldRotation = ResolveSpawnRotation( view );

		ConfigureScaleReferencePawn( instance );

		instance.Enabled = true;
		_spawnedPlayer = instance;

		_flyCamera = view.Components.Get<CameraComponent>();
		AssertFlyCameraMain( view );

		Log.Info( $"[TerrainTestPlayerSpawn] Dropped player at {instance.WorldPosition} ({SpawnDropDistanceMeters:0.#} m below camera)." );
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

	static Rotation ResolveSpawnRotation( GameObject view )
	{
		var flatForward = view.WorldRotation.Forward.WithZ( 0f );
		if ( flatForward.LengthSquared < 1e-8f )
			return view.WorldRotation;

		return Rotation.LookAt( flatForward.Normal, Vector3.Up );
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
