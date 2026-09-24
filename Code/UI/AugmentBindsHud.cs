using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Six small slots above the hotbar: the augments bound to keys 1–6, with a cooldown sweep,
/// a battery bar for toggles and a green edge while a toggle is on. Read-only feedback — the
/// assignment lives on the Augments menu page.
/// </summary>
public sealed class AugmentBindsHud
{
	const float SlotSize = 46f;
	const float SlotGap = 6f;
	const float KeyFont = 12f;

	static readonly Color SlotBg = new( 0.08f, 0.09f, 0.11f, 0.85f );
	static readonly Color CooldownShade = new( 0f, 0f, 0f, 0.7f );
	static readonly Color BatteryColor = new( 0.4f, 0.75f, 1f, 0.95f );
	const string BorderIdle = "#2a3140";
	const string BorderOn = "#5ec46a";

	readonly SlotUi[] _slots = new SlotUi[PlayerAugments.BindCount];

	Panel _host;
	PlayerAugments _augments;
	bool _visible = true;

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
			slot.Style.Set( "box-sizing", "border-box" );
			slot.Style.BackgroundColor = SlotBg;
			slot.Style.Set( "border-width", "1px" );
			slot.Style.Set( "border-color", BorderIdle );
			slot.Style.Set( "border-radius", "5px" );
			slot.Style.Set( "overflow", "hidden" );
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
			cooldown.Style.BackgroundColor = CooldownShade;

			var battery = new Panel { Parent = slot };
			battery.Style.Set( "position", "absolute" );
			battery.Style.Set( "left", "0" );
			battery.Style.Set( "bottom", "0" );
			battery.Style.Set( "height", "3px" );
			battery.Style.Set( "width", "0%" );
			battery.Style.BackgroundColor = BatteryColor;

			var key = new Label { Parent = slot, Text = (i + 1).ToString() };
			key.Style.Set( "position", "absolute" );
			key.Style.Set( "right", "3px" );
			key.Style.Set( "top", "1px" );
			key.Style.FontColor = new Color( 0.75f, 0.78f, 0.82f, 0.9f );
			key.Style.FontSize = Length.Pixels( KeyFont );
			key.Style.Set( "text-shadow", "1px 1px 2px rgba(0,0,0,0.85)" );

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

		_host.Style.Set( "display", _visible && anyBound ? "flex" : "none" );
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
				ui.Cooldown.Style.Set( "height", "0%" );
				ui.Battery.Style.Set( "width", "0%" );
				ui.Root.Style.Set( "border-color", BorderIdle );
				continue;
			}

			ui.SetIcon( AugmentCatalog.GetIconPath( id ) );

			_augments.TryGetTriggerState( id, out var remaining, out var total, out var on, out var battery01 );
			var fill = total > 0.01f ? Math.Clamp( remaining / total, 0f, 1f ) : 0f;
			ui.Cooldown.Style.Set( "height", $"{fill * 100f:0}%" );
			ui.Battery.Style.Set( "width", AugmentCatalog.TryGet( id, out var def ) && def.HasBattery ? $"{battery01 * 100f:0}%" : "0%" );
			ui.Root.Style.Set( "border-color", on ? BorderOn : BorderIdle );
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
