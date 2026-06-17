using Sandbox;

namespace Survival;

/// <summary>Enemy health bar on a child anchor; panel rotation matches the local view camera (screen-horizontal).</summary>
[Title( "Enemy Health Bar" )]
public sealed class EnemyHealthBar : Component
{
	public const string AnchorObjectName = "HealthBarAnchor";
	public const string WorldUiObjectName = "HealthBarWorldUi";
	const float DefaultLocalAnchorHeight = 76f;
	const float WorldUiLocalRaise = 14f;
	const float RenderSupersample = 2f;
	static readonly Vector2 PanelPixelSize = new( 320f * RenderSupersample, 64f * RenderSupersample );
	const float DefaultWorldScale = 2.5f;
	const float PanelFaceYawCorrection = 180f;

	[Property] public EntityVitals Vitals { get; set; }

	[Property, Title( "Anchor child (uses HealthBarAnchor if unset)" )]
	public GameObject Anchor { get; set; }

	[Property, Group( "Display" ), Title( "World panel scale (GameObject local scale)" )]
	public float WorldScale { get; set; } = DefaultWorldScale;

	[Property, Group( "Display" ), Title( "Hide bar beyond this distance (0 = always show)" )]
	public float MaxDisplayDistance { get; set; } = 960f;

	GameObject _anchor;
	GameObject _worldUi;
	Sandbox.WorldPanel _worldPanel;
	EnemyHealthBarPanel _panel;
	string _cachedLabel = "";
	float _cachedFraction = -1f;

	protected override void OnStart()
	{
		Vitals ??= Components.Get<EntityVitals>();
		if ( Vitals is not null )
			Vitals.OnVitalsChanged += OnVitalsChanged;

		EnsureWorldUi();
		RefreshBinding();
	}

	protected override void OnDestroy()
	{
		if ( Vitals is not null )
			Vitals.OnVitalsChanged -= OnVitalsChanged;
	}

	protected override void OnUpdate()
	{
		SyncWorldPanelTransform();
		UpdateVisibility();
	}

	public void RefreshBinding()
	{
		Vitals ??= Components.Get<EntityVitals>();
		EnsureWorldUi();
		RefreshCache();
	}

	void OnVitalsChanged() => RefreshCache();

	void RefreshCache()
	{
		if ( Vitals is null )
			return;

		_cachedLabel = Vitals.GetDisplayName();
		_cachedFraction = Vitals.HealthFraction;
		_panel?.SetDisplay( _cachedLabel, _cachedFraction );
	}

	void EnsureWorldUi()
	{
		EnsureAnchor();
		if ( _worldUi is { IsValid: true } )
		{
			SyncWorldPanelTransform();
			return;
		}

		foreach ( var child in _anchor.Children )
		{
			if ( child is not { IsValid: true } || child.Name != WorldUiObjectName )
				continue;

			_worldUi = child;
			_worldPanel = child.Components.Get<Sandbox.WorldPanel>();
			_panel = child.Components.Get<EnemyHealthBarPanel>();
			SyncWorldPanelTransform();
			return;
		}

		_worldUi = new GameObject( true, WorldUiObjectName );
		_worldUi.Parent = _anchor;
		_worldUi.LocalPosition = Vector3.Up * WorldUiLocalRaise;
		_worldUi.LocalRotation = Rotation.Identity;
		_worldUi.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

		_worldPanel = _worldUi.Components.Create<Sandbox.WorldPanel>();
		_worldPanel.PanelSize = PanelPixelSize;
		_worldPanel.LookAtCamera = false;
		_worldPanel.RenderOptions.Game = false;
		_worldPanel.RenderOptions.Overlay = true;

		_panel = _worldUi.Components.Create<EnemyHealthBarPanel>();
		SyncWorldPanelTransform();
	}

	void SyncWorldPanelTransform()
	{
		if ( _worldUi is not { IsValid: true } )
			return;

		if ( _worldPanel is { IsValid: true } )
		{
			_worldPanel.PanelSize = PanelPixelSize;
			_worldPanel.LookAtCamera = false;
		}

		_panel?.SyncLayoutBounds( PanelPixelSize );

		var scale = Math.Max( 0.01f, WorldScale );
		_worldUi.LocalScale = Vector3.One * scale;
		_worldUi.LocalPosition = Vector3.Up * WorldUiLocalRaise;

		// Lock to the local view camera so name + bar stay screen-horizontal (no FOV tilt / stair-steps).
		var localPawn = FindLocalViewerPawn( Scene );
		if ( localPawn is null )
			return;

		var cam = BuildViewCamera.Resolve( localPawn );
		if ( !cam.IsValid() )
			return;

		_worldUi.WorldRotation = cam.WorldRotation * Rotation.FromYaw( PanelFaceYawCorrection );
	}

	void EnsureAnchor()
	{
		if ( Anchor is { IsValid: true } && Anchor.Parent == GameObject )
		{
			_anchor = Anchor;
			return;
		}

		if ( _anchor is { IsValid: true } && _anchor.Parent == GameObject )
			return;

		foreach ( var child in GameObject.Children )
		{
			if ( child is not { IsValid: true } || child.Name != AnchorObjectName )
				continue;

			_anchor = child;
			Anchor = child;
			return;
		}

		_anchor = new GameObject( true, AnchorObjectName );
		_anchor.Parent = GameObject;
		_anchor.LocalPosition = Vector3.Up * DefaultLocalAnchorHeight;
		_anchor.LocalRotation = Rotation.Identity;
		Anchor = _anchor;
	}

	void UpdateVisibility()
	{
		if ( _worldUi is not { IsValid: true } )
			return;

		var show = ShouldShowToLocalViewer();
		_worldUi.Enabled = show;
	}

	bool ShouldShowToLocalViewer()
	{
		if ( !GameObject.IsValid() || Vitals is { IsDead: true } )
			return false;

		var localPawn = FindLocalViewerPawn( Scene );
		if ( localPawn is null )
			return true;

		EnsureAnchor();
		var worldPos = _anchor.WorldPosition;

		var cam = BuildViewCamera.Resolve( localPawn );
		if ( cam.IsValid() )
		{
			if ( MaxDisplayDistance > 0f )
			{
				var dist = Vector3.DistanceBetween( cam.WorldPosition, worldPos );
				if ( dist > MaxDisplayDistance )
					return false;
			}

			var toPoint = worldPos - cam.WorldPosition;
			if ( Vector3.Dot( toPoint, cam.WorldRotation.Forward.Normal ) <= 0f )
				return false;
		}

		return true;
	}

	static GameObject FindLocalViewerPawn( Scene scene )
	{
		if ( !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}
}
