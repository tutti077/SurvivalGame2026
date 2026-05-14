using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Bottom-left health (red fill) + stamina (yellow fill) bars; unfilled width shows light grey (lost vs max). Black labels. Add with <see cref="ScreenPanel"/> (or on the pawn HUD).
/// </summary>
[Title( "Player Vitals HUD" )]
public sealed class PlayerVitalsHud : PanelComponent
{
	const float BarWidth = 360f;
	const float BarHeight = 22f;
	const float BarGap = 6f;

	// Full bar width = max; fill covers current/max from the left; remainder shows this (depleted).
	static readonly Color DepletedPortionColor = new Color( 0.82f, 0.82f, 0.83f );

	PlayerVitals _vitals;
	ScreenPanel _hudScreen;
	bool _deferScreenPanelCamera;
	Label _healthText;
	Panel _healthRoot;
	Panel _healthFill;
	Label _staminaText;
	Panel _staminaRoot;
	Panel _staminaFill;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();

		var resolved = FindVitals();
		if ( resolved is null || !resolved.IsLocalInputOwnedPawn() )
		{
			Panel.Style.Set( "display", "none" );
			_vitals = null;
			if ( resolved is null )
				Log.Warning( $"[PlayerVitalsHud] {GameObject.Name}: no PlayerVitals on this object or parents — HUD hidden." );
			return;
		}

		_vitals = resolved;

		var screen = Components.Get<ScreenPanel>();
		_hudScreen = screen;
		// ScreenPanel can NRE in native/interop when reading TargetCamera before the scene main camera
		// (or backup camera path) is ready — especially right after a recompile. Keep it off until bound.
		if ( screen is not null )
			screen.Enabled = false;

		Panel.Style.Set( "position", "absolute" );
		Panel.Style.Set( "left", "16px" );
		Panel.Style.Set( "bottom", "16px" );

		// Host uses explicit height + absolute bar slots so health/stamina never overlap
		// (root flex column was collapsing both bars to the same origin, yellow over red).
		var hostHeight = BarHeight * 2f + BarGap;
		var barsHost = new Panel { Parent = Panel };
		barsHost.Style.Width = Length.Pixels( BarWidth );
		barsHost.Style.Height = Length.Pixels( hostHeight );
		barsHost.Style.Set( "position", "relative" );

		_healthRoot = new Panel { Parent = barsHost };
		_healthRoot.Style.Set( "position", "absolute" );
		_healthRoot.Style.Set( "left", "0" );
		_healthRoot.Style.Set( "top", "0" );
		_healthRoot.Style.Width = Length.Pixels( BarWidth );
		_healthRoot.Style.Height = Length.Pixels( BarHeight );
		_healthRoot.Style.BackgroundColor = DepletedPortionColor;
		_healthRoot.Style.Set( "overflow", "hidden" );

		_healthFill = new Panel { Parent = _healthRoot };
		_healthFill.Style.Set( "position", "absolute" );
		_healthFill.Style.Set( "top", "0" );
		_healthFill.Style.Set( "left", "0" );
		_healthFill.Style.Set( "height", "100%" );
		_healthFill.Style.Set( "z-index", "0" );
		_healthFill.Style.BackgroundColor = new Color( 0.92f, 0.18f, 0.14f );

		_healthText = new Label { Parent = _healthRoot };
		_healthText.Style.Set( "position", "absolute" );
		_healthText.Style.Set( "width", "100%" );
		_healthText.Style.Set( "height", "100%" );
		_healthText.Style.Set( "align-items", "center" );
		_healthText.Style.Set( "justify-content", "center" );
		_healthText.Style.Set( "z-index", "1" );
		_healthText.Style.FontColor = Color.Black;
		_healthText.Style.FontSize = Length.Pixels( 14f );

		_staminaRoot = new Panel { Parent = barsHost };
		_staminaRoot.Style.Set( "position", "absolute" );
		_staminaRoot.Style.Set( "left", "0" );
		_staminaRoot.Style.Set( "top", $"{BarHeight + BarGap}px" );
		_staminaRoot.Style.Width = Length.Pixels( BarWidth );
		_staminaRoot.Style.Height = Length.Pixels( BarHeight );
		_staminaRoot.Style.BackgroundColor = DepletedPortionColor;
		_staminaRoot.Style.Set( "overflow", "hidden" );

		_staminaFill = new Panel { Parent = _staminaRoot };
		_staminaFill.Style.Set( "position", "absolute" );
		_staminaFill.Style.Set( "top", "0" );
		_staminaFill.Style.Set( "left", "0" );
		_staminaFill.Style.Set( "height", "100%" );
		_staminaFill.Style.Set( "z-index", "0" );
		_staminaFill.Style.BackgroundColor = new Color( 0.98f, 0.86f, 0.2f );

		_staminaText = new Label { Parent = _staminaRoot };
		_staminaText.Style.Set( "position", "absolute" );
		_staminaText.Style.Set( "width", "100%" );
		_staminaText.Style.Set( "height", "100%" );
		_staminaText.Style.Set( "align-items", "center" );
		_staminaText.Style.Set( "justify-content", "center" );
		_staminaText.Style.Set( "z-index", "1" );
		_staminaText.Style.FontColor = Color.Black;
		_staminaText.Style.FontSize = Length.Pixels( 14f );

		if ( screen is not null )
		{
			if ( TryBindScreenPanelCamera( screen ) )
			{
				screen.Enabled = true;
				_deferScreenPanelCamera = false;
			}
			else
			{
				_deferScreenPanelCamera = true;
				Log.Warning( $"[PlayerVitalsHud] {GameObject.Name}: ScreenPanel.TargetCamera not bound yet — will retry when PlayerController/Scene camera is valid (interop-safe)." );
			}
		}

		_vitals.OnVitalsChanged += RefreshAll;

		RefreshAll();
	}

	protected override void OnDestroy()
	{
		if ( _vitals is not null )
			_vitals.OnVitalsChanged -= RefreshAll;
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _vitals is null || !_vitals.GameObject.IsValid() )
			_vitals = FindVitals();

		if ( !_deferScreenPanelCamera || _hudScreen is null || !_hudScreen.IsValid() )
			return;

		if ( TryBindScreenPanelCamera( _hudScreen ) )
		{
			_hudScreen.Enabled = true;
			_deferScreenPanelCamera = false;
		}
	}

	PlayerVitals FindVitals()
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var v = go.Components.Get<PlayerVitals>();
			if ( v is not null )
				return v;
		}

		return null;
	}

	void RefreshAll()
	{
		if ( _vitals is null )
			return;

		var hMax = Math.Max( 1f, _vitals.CurrentHealthMax );
		var sMax = Math.Max( 1e-3f, _vitals.CurrentStaminaMax );
		var hFrac = Math.Clamp( _vitals.CurrentHealth / hMax, 0f, 1f );
		var sFrac = Math.Clamp( _vitals.CurrentStamina / sMax, 0f, 1f );

		_healthText.Text = $"{_vitals.CurrentHealth:0}/{_vitals.CurrentHealthMax:0}";
		_healthFill.Style.Width = Length.Pixels( BarWidth * hFrac );

		_staminaText.Text = $"{_vitals.CurrentStamina:0}/{_vitals.CurrentStaminaMax:0}";
		_staminaFill.Style.Width = Length.Pixels( BarWidth * sFrac );
	}

	/// <summary>
	/// Assigns <see cref="ScreenPanel.TargetCamera"/> when a valid camera exists. Uses try/catch because the property can NRE in native code before the world camera is ready.
	/// </summary>
	bool TryBindScreenPanelCamera( ScreenPanel screen )
	{
		if ( screen is null )
			return true;
		if ( !screen.IsValid() )
			return false;

		try
		{
			if ( TryResolveHudTargetCamera( GameObject, out var cam ) && cam.IsValid() )
			{
				screen.TargetCamera = cam;
				return true;
			}

			var scene = Scene;
			if ( scene is not null )
			{
				var sceneCam = scene.Camera;
				if ( sceneCam is not null && sceneCam.IsValid() )
				{
					screen.TargetCamera = sceneCam;
					return true;
				}
			}

			var existing = screen.TargetCamera;
			return existing is not null && existing.IsValid();
		}
		catch ( NullReferenceException )
		{
			return false;
		}
	}

	/// <summary>
	/// Prefer <see cref="CameraComponent"/> on <see cref="PlayerController"/> (s&box embeds it on the controller), then any camera under an ancestor.
	/// </summary>
	static bool TryResolveHudTargetCamera( GameObject from, out CameraComponent found )
	{
		found = default;
		if ( !from.IsValid() )
			return false;

		for ( var go = from; go.IsValid(); go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is null )
				continue;

			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
			{
				found = embedded;
				return true;
			}
		}

		for ( var go = from; go.IsValid(); go = go.Parent )
		{
			if ( TryFindFirstCameraInHierarchy( go, out found ) && found.IsValid() )
				return true;
		}

		return false;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent found )
	{
		found = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			found = self;
			return true;
		}

		foreach ( var ch in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( ch, out found ) )
				return true;
		}

		return false;
	}
}
