using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Screen-space biome minimap at the top of the view (camera ScreenPanel).</summary>
[Title( "Terrain Minimap Screen" )]
public sealed class TerrainMinimapScreen : PanelComponent
{
	readonly TerrainMinimapHud _hud = new();
	bool _built;
	float _sinceVisuals = float.MaxValue;

	/// <summary>
	/// Map face, button styles and wind arrow refresh rate — the same split as the pawn HUD's passive tick
	/// (<see cref="PlayerScreenHud"/>): the +/- click edge is polled every frame, the redraw is not. Every frame it
	/// was ~0.2 ms of UI in the fly camera (2026-11-10 fps routine).
	/// </summary>
	const float VisualsTickSeconds = 0.1f;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		EnsureBuilt();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		EnsureBuilt();
		if ( !_built )
			return;

		_hud.TickInput();
		_sinceVisuals += Time.Delta;
		if ( _sinceVisuals < VisualsTickSeconds )
			return;

		_sinceVisuals = 0f;
		_hud.SetVisible( !ShouldHideForFullscreenMenu() );
		_hud.TickVisuals();
	}

	void EnsureBuilt()
	{
		if ( _built )
			return;

		Panel.Style.Set( "position", "absolute" );
		Panel.Style.Set( "left", "0" );
		Panel.Style.Set( "top", "0" );
		Panel.Style.Set( "width", "100%" );
		Panel.Style.Set( "height", "100%" );
		Panel.Style.Set( "pointer-events", "none" );
		_hud.Build( Panel );
		_built = true;
	}

	bool ShouldHideForFullscreenMenu()
	{
		var scene = Scene;
		if ( scene is null || !scene.IsValid() )
			return false;

		foreach ( var menu in scene.GetAllComponents<PlayerGameMenuController>() )
		{
			if ( menu is null || !menu.IsValid() || !menu.IsMenuOpen )
				continue;

			var vitals = menu.Components.Get<PlayerVitals>( FindMode.EverythingInSelfAndAncestors );
			if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
				continue;

			var panels = menu.VisiblePanels;
			if ( (panels & MenuPanelFlags.Map) != 0 || (panels & MenuPanelFlags.Settings) != 0 )
				return true;
		}

		return false;
	}
}
