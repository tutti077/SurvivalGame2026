using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>
/// Terrain-test only:
/// <list type="bullet">
/// <item><b>J</b> (<c>SpawnPlayer</c>) — scale-reference doll (no controller; fly cam stays).</item>
/// <item><b>L</b> (<c>SpawnPlayablePlayer</c>) — full playable pawn; fly input off, PlayerController owns the scene camera.</item>
/// </list>
/// </summary>
[Title( "Terrain Test Player Spawn" )]
public sealed class TerrainTestPlayerSpawn : Component
{
	public const string DefaultGrappleResourceId = "basic_hook";
	public const string DefaultWingsuitResourceId = "basic_wingsuit";

	[Property, Title( "View camera (fly cam)" )] public GameObject ViewCamera { get; set; }

	[Property] public string PlayerPrefabPath { get; set; } = "prefabs/player/basicplayer.prefab";

	[Property] public bool DisableFallDamage { get; set; } = true;

	[Property, Title( "Equip grapple on L spawn" )]
	public bool EquipGrappleOnPlayable { get; set; } = true;

	[Property, Title( "Grapple resource id" )]
	public string GrappleResourceId { get; set; } = DefaultGrappleResourceId;

	[Property, Title( "Equip wingsuit on L spawn" )]
	public bool EquipWingsuitOnPlayable { get; set; } = true;

	[Property, Title( "Wingsuit resource id" )]
	public string WingsuitResourceId { get; set; } = DefaultWingsuitResourceId;

	[Property, Title( "Infinite stamina on L spawn" )]
	public bool InfiniteStaminaOnPlayable { get; set; } = true;

	readonly List<GameObject> _spawnedScaleDolls = new();
	GameObject _playablePlayer;
	GameObject _flyView;
	TerrainTestFlyCamera _flyControl;
	CameraComponent _sceneCamera;

	protected override void OnUpdate()
	{
		if ( !Active || !GameObject.IsValid )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		PruneDestroyedSpawns();

		if ( IsPlayableActive() )
			MaintainPlayableControl();
		else if ( _spawnedScaleDolls.Count > 0 )
			MaintainFlyCamera();

		if ( WasPlayableSpawnPressed() )
		{
			SpawnPlayableAtCamera();
			return;
		}

		if ( !Input.Pressed( "SpawnPlayer" ) )
			return;

		SpawnScaleDollAtCamera();
	}

	static bool WasPlayableSpawnPressed()
	{
		if ( Input.Pressed( "SpawnPlayablePlayer" ) )
			return true;

		// Fallback if Input.config wasn't reloaded after adding the action.
		return Input.Keyboard.Pressed( "L" );
	}

	void SpawnPlayableAtCamera()
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

		DestroyPlayableIfAny();

		var instance = BuildPrefabUtility.GetTemplate( PlayerPrefabPath )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[TerrainTestPlayerSpawn] Failed to clone '{PlayerPrefabPath}'." );
			return;
		}

		instance.Name = "TerrainTestPlayablePlayer";
		instance.NetworkMode = NetworkMode.Never;
		instance.Parent = scene;

		var spawnPos = view.WorldPosition;
		var spawnYaw = view.WorldRotation.Angles().yaw;
		TryPlaceOnGround( ref spawnPos );
		instance.WorldPosition = spawnPos;
		instance.WorldRotation = Rotation.FromYaw( spawnYaw );

		ConfigurePlayablePawn( instance );
		instance.Enabled = true;
		_playablePlayer = instance;

		TakeControlFromFlyCamera( view );

		Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault()?.InvalidateMinimapHostCache();

		Log.Info( "[TerrainTestPlayerSpawn] Playable control (L) — fly cam input disabled; middle-mouse grapple." );
	}

	void SpawnScaleDollAtCamera()
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
		_spawnedScaleDolls.Add( instance );

		CacheFlyRefs( view );
		AssertSceneCameraMain( view );

		Log.Info( $"[TerrainTestPlayerSpawn] Scale doll #{_spawnedScaleDolls.Count} at camera (J)." );
	}

	bool IsPlayableActive() => _playablePlayer is not null && _playablePlayer.IsValid();

	void DestroyPlayableIfAny()
	{
		if ( !IsPlayableActive() )
		{
			_playablePlayer = null;
			return;
		}

		_playablePlayer.Destroy();
		_playablePlayer = null;
	}

	void PruneDestroyedSpawns()
	{
		for ( var i = _spawnedScaleDolls.Count - 1; i >= 0; i-- )
		{
			if ( !_spawnedScaleDolls[i].IsValid() )
				_spawnedScaleDolls.RemoveAt( i );
		}

		if ( _playablePlayer is not null && !_playablePlayer.IsValid() )
		{
			_playablePlayer = null;
			RestoreFlyControl();
		}
	}

	GameObject ResolveViewCamera()
	{
		if ( ViewCamera is { IsValid: true } )
			return ViewCamera;

		if ( _flyView is { IsValid: true } )
			return _flyView;

		var scene = Scene;
		if ( !scene.IsValid() )
			return null;

		foreach ( var cam in scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() )
				continue;

			if ( cam.GameObject.Components.Get<TerrainTestFlyCamera>() is not null )
				return cam.GameObject;
		}

		foreach ( var cam in scene.GetAllComponents<CameraComponent>() )
		{
			if ( cam is null || !cam.IsValid() || !cam.Enabled )
				continue;

			if ( cam.GameObject.Tags.Has( "maincamera" ) )
				return cam.GameObject;
		}

		return scene.Camera?.GameObject;
	}

	void TryPlaceOnGround( ref Vector3 worldPos )
	{
		var manager = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
			return;

		var meters = TerrainWorldUnits.EngineToMeters( worldPos );
		if ( !manager.TrySampleGroundMeters( meters.x, meters.y, out var groundZMeters ) )
			return;

		var groundEngine = TerrainWorldUnits.MetersToEngine( groundZMeters );
		// Stand a bit above the mesh so the capsule isn't buried.
		worldPos = new Vector3( worldPos.x, worldPos.y, groundEngine + 40f );
	}

	void ConfigurePlayablePawn( GameObject root )
	{
		if ( DisableFallDamage )
			DisableImpactDamage( root );

		EnsureRigidbodyFalls( root );

		var controller = root.Components.Get<PlayerController>( FindMode.EverythingInSelfAndDescendants );
		if ( controller is not null && controller.IsValid() )
		{
			controller.Enabled = true;
			controller.UseInputControls = true;
			controller.UseCameraControls = true;
			controller.UseLookControls = true;
		}

		var vitals = root.Components.Get<PlayerVitals>( FindMode.EverythingInSelfAndDescendants );
		if ( vitals is not null && vitals.IsValid() && InfiniteStaminaOnPlayable )
			vitals.InfiniteStaminaDebug = true;

		if ( !EquipGrappleOnPlayable && !EquipWingsuitOnPlayable )
			return;

		EquipmentCatalog.EnsureLoaded();

		var equipment = root.Components.Get<PlayerEquipment>( FindMode.EverythingInSelfAndDescendants );
		if ( equipment is null || !equipment.IsValid() )
		{
			Log.Warning( "[TerrainTestPlayerSpawn] Playable has no PlayerEquipment — cannot equip hook/wingsuit." );
			return;
		}

		if ( EquipGrappleOnPlayable )
		{
			var grappleId = string.IsNullOrWhiteSpace( GrappleResourceId )
				? DefaultGrappleResourceId
				: GrappleResourceId.Trim();

			if ( !equipment.HostAcceptClientGrappleEquip( grappleId ) )
				Log.Warning( $"[TerrainTestPlayerSpawn] Failed to equip '{grappleId}' on playable." );
			else
				Log.Info( $"[TerrainTestPlayerSpawn] Equipped '{grappleId}' in Grapple slot." );
		}

		if ( EquipWingsuitOnPlayable )
		{
			var wingsuitId = string.IsNullOrWhiteSpace( WingsuitResourceId )
				? DefaultWingsuitResourceId
				: WingsuitResourceId.Trim();

			if ( !equipment.HostAcceptClientWingsuitEquip( wingsuitId ) )
				Log.Warning( $"[TerrainTestPlayerSpawn] Failed to equip '{wingsuitId}' on playable." );
			else
				Log.Info( $"[TerrainTestPlayerSpawn] Equipped '{wingsuitId}' in Wingsuit slot." );
		}
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

	void CacheFlyRefs( GameObject view )
	{
		_flyView = view;
		_flyControl = view.Components.Get<TerrainTestFlyCamera>();
		_sceneCamera = view.Components.Get<CameraComponent>();
	}

	/// <summary>
	/// PlayerController drives the scene's main <see cref="CameraComponent"/> — keep the fly
	/// camera component alive, only disable <see cref="TerrainTestFlyCamera"/> input.
	/// </summary>
	void TakeControlFromFlyCamera( GameObject flyView )
	{
		CacheFlyRefs( flyView );

		if ( _flyControl is not null && _flyControl.IsValid() )
		{
			_flyControl.SetInputLocked( true );
			_flyControl.Enabled = false;
		}

		AssertSceneCameraMain( flyView );
	}

	void MaintainPlayableControl()
	{
		if ( !IsPlayableActive() )
			return;

		if ( _flyView is null || !_flyView.IsValid() )
		{
			var view = ResolveViewCamera();
			if ( view is null || !view.IsValid() )
				return;
			CacheFlyRefs( view );
			AssertSceneCameraMain( view );
		}

		if ( _flyControl is not null && _flyControl.IsValid() )
		{
			if ( _flyControl.Enabled )
				_flyControl.Enabled = false;
			if ( !_flyControl.InputLocked )
				_flyControl.SetInputLocked( true );
		}

		var controller = _playablePlayer.Components.Get<PlayerController>( FindMode.EverythingInSelfAndDescendants );
		if ( controller is not null && controller.IsValid() )
		{
			if ( !controller.Enabled )
				controller.Enabled = true;
			controller.UseInputControls = true;
			controller.UseCameraControls = true;
			controller.UseLookControls = true;
		}
	}

	void RestoreFlyControl()
	{
		var view = ResolveViewCamera();
		if ( view is null )
			return;

		CacheFlyRefs( view );
		AssertSceneCameraMain( view );

		if ( _flyControl is not null && _flyControl.IsValid() )
		{
			_flyControl.Enabled = true;
			_flyControl.SetInputLocked( false );
		}
	}

	void MaintainFlyCamera()
	{
		if ( IsPlayableActive() )
			return;

		if ( _flyView is null || !_flyView.IsValid() )
		{
			var view = ResolveViewCamera();
			if ( view is null )
				return;
			CacheFlyRefs( view );
			AssertSceneCameraMain( view );
		}

		if ( _flyControl is not null && _flyControl.IsValid() )
		{
			if ( !_flyControl.Enabled )
				_flyControl.Enabled = true;
			if ( _flyControl.InputLocked )
				_flyControl.SetInputLocked( false );
		}
	}

	static void AssertSceneCameraMain( GameObject view )
	{
		var cam = view.Components.Get<CameraComponent>();
		if ( cam is null || !cam.IsValid() )
			return;

		cam.Enabled = true;
		cam.IsMainCamera = true;

		var scene = view.Scene;
		if ( !scene.IsValid() )
			return;

		foreach ( var other in scene.GetAllComponents<CameraComponent>() )
		{
			if ( other is null || !other.IsValid() || other == cam )
				continue;

			other.IsMainCamera = false;
		}
	}
}
