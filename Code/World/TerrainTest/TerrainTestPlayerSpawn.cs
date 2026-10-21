using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>
/// Terrain-test only: <b>L</b> (<c>SpawnPlayablePlayer</c>) toggles between the full playable pawn (fly input off,
/// PlayerController owns the scene camera) and the fly camera, which resumes from the pawn's eyes. (The J scale-reference doll — a stripped, T-posed player clone — was deleted 2026-10-13.)
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

		if ( !WasPlayableSpawnPressed() )
			return;

		TogglePawn();
	}

	Transform? _pendingFlyEye;

	/// <summary>L / <c>pawn_toggle</c>: fly camera → pawn, pawn → fly camera (resumes from the pawn's eyes).</summary>
	public void TogglePawn()
	{
		if ( IsPlayableActive() )
		{
			// Destroy is deferred to the end of the frame and the pawn's controller releases the camera / mouse in
			// its own teardown, so restoring the fly camera NOW gets undone a moment later (Mark 2026-10-13: "can't
			// be flown"). Mark the pawn for destruction and let PruneDestroyedSpawns restore next frame, after it is gone.
			_pendingFlyEye = Scene?.Camera?.WorldTransform;
			_playablePlayer.Destroy();
			Log.Info( "[TerrainTestPlayerSpawn] Pawn removed (L) — fly camera resumes next frame." );
			return;
		}

		SpawnPlayableAtCamera();
	}

	/// <summary>Dev: the same as pressing L.</summary>
	[ConCmd( "pawn_toggle" )]
	public static void ConCmdPawnToggle()
	{
		var spawn = Sandbox.Game.ActiveScene?.GetAllComponents<TerrainTestPlayerSpawn>().FirstOrDefault();
		if ( spawn is null )
		{
			Log.Warning( "[TerrainTestPlayerSpawn] no TerrainTestPlayerSpawn in the active scene." );
			return;
		}
		spawn.TogglePawn();
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
		if ( _playablePlayer is not null && !_playablePlayer.IsValid() )
		{
			_playablePlayer = null;
			RestoreFlyControl();
			// The fly camera owns its transform every frame — hand it the pawn's last eye through its own teleport.
			if ( _pendingFlyEye.HasValue && _flyControl is { IsValid: true } )
				_flyControl.TeleportTo( _pendingFlyEye.Value.Position, _pendingFlyEye.Value.Position + (_pendingFlyEye.Value.Forward * 1000f) );
			_pendingFlyEye = null;
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

	static void EnsureRigidbodyFalls( GameObject root )
	{
		var body = root.Components.Get<Rigidbody>( FindMode.EverythingInSelfAndDescendants );
		if ( body is null || !body.IsValid() )
			return;

		body.MotionEnabled = true;
		body.Gravity = true;
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
		// EverythingInSelf: the fly control is DISABLED while the pawn is up, and the default (enabled-only) lookup
		// returned null on restore, so it was never switched back on — the "can't fly after L" bug (2026-10-13).
		_flyControl = view.Components.Get<TerrainTestFlyCamera>( FindMode.EverythingInSelf );
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

		Log.Info( $"[TerrainTestPlayerSpawn] Fly camera restored: control {(_flyControl.IsValid() ? (_flyControl.Enabled ? "on" : "DISABLED") : "missing")}, input {(_flyControl.IsValid() && _flyControl.InputLocked ? "LOCKED" : "free")}, scene camera '{Scene?.Camera?.GameObject?.Name}', mouse {Mouse.Visibility}." );
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
