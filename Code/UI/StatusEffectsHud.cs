using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Buff / debuff strip above the health and stamina bars: one icon per active
/// <see cref="StatusEffectView"/> with its mm:ss timer. While any menu is open, the soft cursor
/// resting on an icon pops a tooltip naming the effect and what it is doing to you
/// ("POISONED · 4s — -5 HP/s"). The strip keeps a fixed height whether or not anything is active,
/// so the bars below never move.
/// </summary>
public sealed class StatusEffectsHud
{
	public const float IconSize = 36f;
	public const float IconGap = 6f;
	/// <summary>Space between the strip and the bars — part of the strip's reserved height.</summary>
	public const float RowBottomGap = 8f;
	const float TooltipMaxWidth = 300f;

	const string BuffBorderHex = "#73cc73";
	const string DebuffBorderHex = "#e6594d";
	static readonly Color TooltipTitleColor = Color.White;
	static readonly Color TooltipDescriptionColor = new( 0.78f, 0.8f, 0.84f );
	static readonly Color TooltipBuffColor = new( 0.55f, 0.85f, 0.55f );
	static readonly Color TooltipDebuffColor = new( 0.95f, 0.55f, 0.5f );
	static readonly Color TooltipComfortColor = new( 0.87f, 0.76f, 0.38f );

	sealed class Slot
	{
		public string Id;
		public Panel Root;
		public Label Timer;
		public string AppliedTimer = string.Empty;
	}

	readonly PlayerVitals _vitals;
	readonly List<Slot> _slots = new();

	Panel _row;
	Panel _tooltip;
	string _builtKey = string.Empty;
	string _tooltipKey;
	bool _tooltipVisible;

	public StatusEffectsHud( PlayerVitals vitals )
	{
		_vitals = vitals;
	}

	public void Build( Panel parent )
	{
		if ( _row is not null )
			return;

		StatusEffectCatalog.EnsureLoaded();

		_row = new Panel { Parent = parent };
		_row.Style.Set( "position", "relative" );
		_row.Style.Set( "flex-direction", "row" );
		_row.Style.Set( "align-items", "flex-end" );
		_row.Style.Set( "gap", $"{IconGap}px" );
		_row.Style.Height = Length.Pixels( IconSize );
		_row.Style.MinHeight = Length.Pixels( IconSize );
		_row.Style.MaxHeight = Length.Pixels( IconSize );
		_row.Style.MarginBottom = Length.Pixels( RowBottomGap );
		_row.Style.Set( "flex-shrink", "0" );
		_row.Style.Set( "pointer-events", "none" );

		_tooltip = new Panel { Parent = _row };
		_tooltip.Style.Set( "position", "absolute" );
		_tooltip.Style.Set( "left", "0" );
		_tooltip.Style.Set( "bottom", $"{IconSize + 8f}px" );
		_tooltip.Style.Set( "flex-direction", "column" );
		_tooltip.Style.Set( "gap", "4px" );
		_tooltip.Style.Set( "padding-left", "12px" );
		_tooltip.Style.Set( "padding-right", "12px" );
		_tooltip.Style.Set( "padding-top", "9px" );
		_tooltip.Style.Set( "padding-bottom", "9px" );
		_tooltip.Style.Set( "max-width", $"{TooltipMaxWidth}px" );
		_tooltip.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.96f );
		_tooltip.Style.Set( "border-radius", "6px" );
		_tooltip.Style.Set( "border-width", "1px" );
		_tooltip.Style.Set( "border-color", "#4a5160" );
		_tooltip.Style.Set( "pointer-events", "none" );
		_tooltip.Style.Set( "z-index", "10" );
		_tooltip.Style.Set( "display", "none" );

		if ( _vitals is not null )
			_vitals.StatusEffectsChanged += OnEffectsChanged;

		RebuildSlots();
	}

	public void Dispose()
	{
		if ( _vitals is not null )
			_vitals.StatusEffectsChanged -= OnEffectsChanged;

		_row?.Delete();
		_row = null;
		_tooltip = null;
		_slots.Clear();
	}

	void OnEffectsChanged() => RebuildSlots();

	/// <summary>Per frame: timers, plus the hover tooltip while a menu is open.</summary>
	public void Tick( bool menuOpen )
	{
		if ( _row is null || !_row.IsValid() || _vitals is null )
			return;

		// Reading the mirror parses any host change first, so a rebuild (via StatusEffectsChanged)
		// lands here rather than mid-loop.
		_ = _vitals.ActiveStatusEffects;

		for ( var i = 0; i < _slots.Count; i++ )
		{
			var slot = _slots[i];
			var text = FormatTimer( _vitals.GetStatusEffectRemainingSeconds( slot.Id ) );
			if ( string.Equals( slot.AppliedTimer, text, StringComparison.Ordinal ) )
				continue;

			slot.AppliedTimer = text;
			slot.Timer.Text = text;
		}

		TickTooltip( menuOpen );
	}

	string CurrentKey()
	{
		var effects = _vitals.ActiveStatusEffects;
		if ( effects.Count == 0 )
			return string.Empty;

		var sb = new StringBuilder();
		for ( var i = 0; i < effects.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( '|' );
			sb.Append( effects[i].Id );
		}

		return sb.ToString();
	}

	void RebuildSlots()
	{
		if ( _row is null || !_row.IsValid() || _vitals is null )
			return;

		var key = CurrentKey();
		if ( string.Equals( key, _builtKey, StringComparison.Ordinal ) )
			return;

		_builtKey = key;
		for ( var i = 0; i < _slots.Count; i++ )
			_slots[i].Root?.Delete();
		_slots.Clear();
		HideTooltip();

		var effects = _vitals.ActiveStatusEffects;
		for ( var i = 0; i < effects.Count; i++ )
		{
			if ( !StatusEffectCatalog.TryGet( effects[i].Id, out var effect ) )
				continue;

			var root = new Panel { Parent = _row };
			root.Style.Width = Length.Pixels( IconSize );
			root.Style.Height = Length.Pixels( IconSize );
			root.Style.BackgroundColor = StatusEffectCatalog.ResolveFallbackColor( effect ).WithAlpha( 0.55f );
			root.Style.Set( "border-width", "1px" );
			root.Style.Set( "border-color", effect.IsBuff ? BuffBorderHex : DebuffBorderHex );
			root.Style.Set( "border-radius", "4px" );
			root.Style.Set( "overflow", "hidden" );
			root.Style.Set( "flex-shrink", "0" );
			root.Style.Set( "pointer-events", "none" );

			var icon = new Panel { Parent = root };
			icon.Style.Set( "position", "absolute" );
			icon.Style.Set( "left", "3px" );
			icon.Style.Set( "top", "1px" );
			icon.Style.Set( "right", "3px" );
			icon.Style.Set( "bottom", "11px" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			icon.Style.Set( "pointer-events", "none" );
			MenuUiTextures.ApplyBackground( icon, effect.Icon );

			var timer = new Label { Parent = root, Text = "" };
			timer.Style.Set( "position", "absolute" );
			timer.Style.Set( "left", "0" );
			timer.Style.Set( "right", "0" );
			timer.Style.Set( "bottom", "0" );
			timer.Style.Set( "align-items", "center" );
			timer.Style.Set( "justify-content", "center" );
			timer.Style.FontColor = Color.White;
			timer.Style.FontSize = Length.Pixels( 10f );
			timer.Style.Set( "text-shadow", "1px 1px 2px black" );
			timer.Style.Set( "pointer-events", "none" );

			_slots.Add( new Slot { Id = effect.Id, Root = root, Timer = timer } );
		}

	}

	void TickTooltip( bool menuOpen )
	{
		if ( !menuOpen || _slots.Count == 0 )
		{
			HideTooltip();
			return;
		}

		var pointer = InventoryScreenPointer.GetMenuOrMousePosition();
		Slot hovered = null;
		for ( var i = 0; i < _slots.Count; i++ )
		{
			if ( InventoryScreenPointer.PanelBoxContainsScreen( _slots[i].Root, pointer ) )
			{
				hovered = _slots[i];
				break;
			}
		}

		if ( hovered is null )
		{
			HideTooltip();
			return;
		}

		ShowTooltip( hovered );
	}

	void ShowTooltip( Slot slot )
	{
		if ( _tooltip is null || !_tooltip.IsValid() || !StatusEffectCatalog.TryGet( slot.Id, out var effect ) )
			return;

		var key = $"{slot.Id}|{slot.AppliedTimer}|{_vitals.ComfortLevel}|{(int)_vitals.ComfortSources}";
		if ( !string.Equals( key, _tooltipKey, StringComparison.Ordinal ) )
		{
			_tooltipKey = key;
			BuildTooltipContent( effect, slot.AppliedTimer );
		}

		if ( _tooltipVisible )
			return;

		_tooltipVisible = true;
		_tooltip.Style.Set( "display", "flex" );
	}

	void HideTooltip()
	{
		if ( !_tooltipVisible || _tooltip is null || !_tooltip.IsValid() )
			return;

		_tooltipVisible = false;
		_tooltip.Style.Set( "display", "none" );
	}

	void BuildTooltipContent( StatusEffectData effect, string timer )
	{
		_tooltip.DeleteChildren();

		var name = string.IsNullOrWhiteSpace( effect.DisplayName ) ? effect.Id : effect.DisplayName;
		AddLine( $"{name.ToUpperInvariant()} · {timer}", TooltipTitleColor, 18f );

		if ( !string.IsNullOrWhiteSpace( effect.Description ) )
			AddLine( effect.Description, TooltipDescriptionColor, 14f );

		var modifierColor = effect.IsBuff ? TooltipBuffColor : TooltipDebuffColor;
		foreach ( var line in effect.DescribeModifiers() )
			AddLine( line, modifierColor, 15f );

		if ( string.Equals( effect.Id, StatusEffectCatalog.RestedId, StringComparison.OrdinalIgnoreCase ) )
			AddLine( DescribeComfort(), TooltipComfortColor, 14f );
	}

	string DescribeComfort()
	{
		var level = _vitals.ComfortLevel;
		if ( level <= 0 )
			return "Left comfort — timer counting down";

		var sources = _vitals.ComfortSources;
		var parts = new List<string>();
		if ( (sources & ComfortSourceFlags.Roof) != 0 )
			parts.Add( "roof" );
		if ( (sources & ComfortSourceFlags.Enclosed) != 0 )
			parts.Add( "enclosed" );
		if ( (sources & ComfortSourceFlags.Campfire) != 0 )
			parts.Add( "campfire" );

		var detail = parts.Count > 0 ? $" ({string.Join( ", ", parts )})" : string.Empty;
		return $"Comfort {level}{detail} — timer held";
	}

	void AddLine( string text, Color color, float fontSize )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return;

		var label = new Label { Parent = _tooltip, Text = text };
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( fontSize );
		label.Style.Set( "white-space", "normal" );
		label.Style.Set( "pointer-events", "none" );
	}

	/// <summary>mm:ss (19:59); short effects under a minute read as 0:04.</summary>
	static string FormatTimer( float seconds )
	{
		if ( seconds < 0f )
			return "";

		var total = (int)Math.Ceiling( seconds );
		var minutes = total / 60;
		var secs = total % 60;
		return $"{minutes}:{secs:00}";
	}
}
