using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Six small slots above the hotbar: the augments bound to keys 1–6, with a cooldown sweep,
/// a battery bar for toggles and a yellow edge while a toggle is on. Read-only feedback — the
/// assignment lives on the Augments menu page.
/// </summary>
public sealed class AugmentBindsHud
{
	const float SlotSize = 46f;
	const float SlotGap = 6f;
	const float KeyFont = 12f;

	readonly SlotUi[] _slots = new SlotUi[PlayerAugments.BindCount];

	Panel _host;
	PlayerAugments _augments;
	bool _visible = true;
	bool? _hostShown;

	public void Build( Panel root, PlayerAugments augments )
	{
		_augments = augments;

		_host = new Panel { Parent = root };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "0" );
		_host.Style.Set( "right", "0" );
		_host.Style.Set( "bottom", "96px" );
		_host.Style.Set( "flex-direction", "row" );
		_host.Style.Set( "gap", $"{SlotGap}px" );
		_host.Style.Set( "align-items", "center" );
		_host.Style.Set( "justify-content", "center" );
		_host.Style.Set( "pointer-events", "none" );

		for ( var i = 0; i < _slots.Length; i++ )
		{
			var slot = new Panel { Parent = _host };
			slot.Style.Width = Length.Pixels( SlotSize );
			slot.Style.Height = Length.Pixels( SlotSize );
			slot.Style.Set( "position", "relative" );
			UiStyle.Slot( slot );
			slot.Style.Set( "pointer-events", "none" );

			var icon = new Panel { Parent = slot };
			icon.Style.Set( "position", "absolute" );
			icon.Style.Set( "left", "4px" );
			icon.Style.Set( "top", "4px" );
			icon.Style.Set( "right", "4px" );
			icon.Style.Set( "bottom", "4px" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			icon.Style.Set( "display", "none" );

			var cooldown = new Panel { Parent = slot };
			cooldown.Style.Set( "position", "absolute" );
			cooldown.Style.Set( "left", "0" );
			cooldown.Style.Set( "right", "0" );
			cooldown.Style.Set( "bottom", "0" );
			cooldown.Style.Set( "height", "0%" );
			cooldown.Style.BackgroundColor = UiTheme.Colors.ShadowColor.WithAlpha( 0.7f );

			var battery = new Panel { Parent = slot };
			battery.Style.Set( "position", "absolute" );
			battery.Style.Set( "left", "0" );
			battery.Style.Set( "bottom", "0" );
			battery.Style.Set( "height", "3px" );
			battery.Style.Set( "width", "0%" );
			battery.Style.BackgroundColor = UiTheme.Colors.InfoColor;

			var key = new Label { Parent = slot, Text = i < PlayerAugments.BindKeyLabels.Length ? PlayerAugments.BindKeyLabels[i] : (i + 1).ToString() };
			key.Style.Set( "position", "absolute" );
			key.Style.Set( "right", "3px" );
			key.Style.Set( "top", "1px" );
			UiStyle.Text( key, UiText.Hud, KeyFont );
			key.Style.FontColor = UiTheme.Colors.TextSecondaryColor;

			_slots[i] = new SlotUi( slot, icon, cooldown, battery );
		}
	}

	public void SetVisible( bool visible )
	{
		_visible = visible;
		ApplyVisibility();
	}

	void ApplyVisibility()
	{
		if ( _host is null || !_host.IsValid() )
			return;

		// Nothing bound = nothing drawn; the strip appears with the first bind.
		var anyBound = false;
		for ( var i = 0; i < PlayerAugments.BindCount && !anyBound; i++ )
			anyBound = !string.IsNullOrWhiteSpace( _augments?.GetBind( i ) );

		var shown = _visible && anyBound;
		if ( _hostShown == shown )
			return;

		_hostShown = shown;
		_host.Style.Set( "display", shown ? "flex" : "none" );
	}

	public void Tick()
	{
		if ( _host is null || !_host.IsValid() || _augments is null )
			return;

		ApplyVisibility();
		if ( !_visible )
			return;

		for ( var i = 0; i < _slots.Length; i++ )
		{
			var ui = _slots[i];
			var id = _augments.GetBind( i );
			if ( string.IsNullOrWhiteSpace( id ) )
			{
				ui.SetIcon( null );
				ui.Apply( 0, 0, UiSlotState.Idle );
				continue;
			}

			ui.SetIcon( AugmentCatalog.GetIconPath( id ) );

			_augments.TryGetTriggerState( id, out var remaining, out var total, out var on, out var battery01 );
			var fill = total > 0.01f ? Math.Clamp( remaining / total, 0f, 1f ) : 0f;
			var batteryPct = AugmentCatalog.TryGet( id, out var def ) && def.HasBattery ? (int)MathF.Round( battery01 * 100f ) : 0;
			ui.Apply( (int)MathF.Round( fill * 100f ), batteryPct, on ? UiSlotState.Active : UiSlotState.Idle );
		}
	}

	public void Dispose()
	{
		_host?.Delete();
		_host = null;
	}

	sealed class SlotUi
	{
		public Panel Root { get; }
		public Panel Icon { get; }
		public Panel Cooldown { get; }
		public Panel Battery { get; }
		string _iconPath;
		// Last applied values: styles are only touched when the rounded percent / colour changes
		// (Style.Set every frame dirtied the HUD's styles and layout each frame).
		int _cooldownPct = -1;
		int _batteryPct = -1;
		UiSlotState? _state;

		public void Apply( int cooldownPct, int batteryPct, UiSlotState state )
		{
			if ( cooldownPct != _cooldownPct )
			{
				_cooldownPct = cooldownPct;
				Cooldown.Style.Set( "height", $"{cooldownPct}%" );
			}

			if ( batteryPct != _batteryPct )
			{
				_batteryPct = batteryPct;
				Battery.Style.Set( "width", $"{batteryPct}%" );
			}

			if ( _state != state )
			{
				_state = state;
				UiStyle.SetSlotState( Root, state );
			}
		}

		public SlotUi( Panel root, Panel icon, Panel cooldown, Panel battery )
		{
			Root = root;
			Icon = icon;
			Cooldown = cooldown;
			Battery = battery;
		}

		public void SetIcon( string path )
		{
			if ( string.Equals( path, _iconPath, StringComparison.OrdinalIgnoreCase ) )
				return;

			_iconPath = path;
			if ( string.IsNullOrWhiteSpace( path ) )
			{
				Icon.Style.Set( "display", "none" );
				return;
			}

			MenuUiTextures.ApplyBackground( Icon, path );
			Icon.Style.Set( "display", "flex" );
		}
	}
}
