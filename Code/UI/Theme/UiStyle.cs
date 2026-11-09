using System;
using System.Globalization;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Surface kinds a panel can take from the theme.</summary>
public enum UiSurface
{
	/// <summary>A menu column / page: panel colour, border, large radius, grain tile, drop shadow.</summary>
	Panel,
	/// <summary>A card inside a panel (detail box, list row box): raised colour, subtle border.</summary>
	Raised,
	/// <summary>A well inside a panel (list viewport, grid background): sunken colour, subtle border.</summary>
	Sunken,
	/// <summary>Translucent HUD readout pill (prompts, vehicle speed, readouts): panel colour, thin border, small radius.</summary>
	Pill,
	/// <summary>Hover tooltip: near-opaque, strong border.</summary>
	Tooltip,
	/// <summary>Framed hero panel: panel surface plus the strong accent border and corner ornaments.</summary>
	Frame,
}

public enum UiText
{
	/// <summary>Page / section title — heading font, uppercase + letter spacing per theme, accent colour.</summary>
	Title,
	/// <summary>Sub-heading inside a page ("Requirements", "Mode") — heading font, primary colour.</summary>
	Header,
	/// <summary>Regular copy.</summary>
	Body,
	/// <summary>Less important copy (meta lines, hints).</summary>
	Secondary,
	/// <summary>De-emphasised copy (footers, placeholders).</summary>
	Muted,
	/// <summary>Numbers and values the eye should land on — body size, value weight, primary colour.</summary>
	Value,
	/// <summary>Accent-coloured emphasis (costs, selected labels).</summary>
	Accent,
	/// <summary>Floating HUD text over the world: primary colour with the theme text shadow.</summary>
	Hud,
}

public enum UiButton
{
	Default,
	/// <summary>The one action the page is about (Craft, Load, Start).</summary>
	Primary,
	/// <summary>Destructive (Delete, Quit, Leave).</summary>
	Danger,
	/// <summary>No fill until hovered — toolbar / strip buttons.</summary>
	Ghost,
}

public enum UiSlotState
{
	Idle,
	Hover,
	Selected,
	/// <summary>Preferred ammo / highlighted stack: a lighter fill, idle border.</summary>
	Preferred,
	Locked,
	Pending,
	Active,
	Danger,
}

/// <summary>
/// Applies the active <see cref="UiTheme"/> to code-built panels. Every HUD / menu class styles its
/// panels through these calls instead of naming colours, radii or fonts; layout (position, size,
/// flex) stays in the owning class. A size argument is always in 1080p design pixels — pass the
/// section's own scale factor multiplied in, exactly as the old constants did.
/// </summary>
public static class UiStyle
{
	static UiThemeColors C => UiTheme.Colors;
	static UiThemeMetrics M => UiTheme.Metrics;
	static UiThemeFonts F => UiTheme.Fonts;

	// ── formatting ─────────────────────────────────────────────────────────────────────────────

	public static string Css( Color c ) =>
		string.Create( CultureInfo.InvariantCulture, $"rgba({(int)Math.Round( Math.Clamp( c.r, 0f, 1f ) * 255f )},{(int)Math.Round( Math.Clamp( c.g, 0f, 1f ) * 255f )},{(int)Math.Round( Math.Clamp( c.b, 0f, 1f ) * 255f )},{Math.Clamp( c.a, 0f, 1f ):0.###})" );

	public static string Px( float v ) => string.Create( CultureInfo.InvariantCulture, $"{v:0.##}px" );

	/// <summary>The theme's standard text shadow declaration.</summary>
	public static string TextShadowCss => $"0px 1px 2px {Css( C.TextShadowColor )}";

	/// <summary>The theme's panel drop shadow declaration.</summary>
	public static string ShadowCss => $"{M.Shadow} {Css( C.ShadowColor )}";

	// ── surfaces ───────────────────────────────────────────────────────────────────────────────

	/// <summary>Background, border and radius for a surface. Layout properties are left alone.</summary>
	public static void Surface( Panel p, UiSurface surface, float? radius = null )
	{
		if ( p is null || !p.IsValid() )
			return;

		switch ( surface )
		{
			case UiSurface.Panel:
				p.Style.BackgroundColor = C.PanelColor;
				SetBorder( p, M.BorderWidth, C.BorderColor, radius ?? M.RadiusLarge );
				p.Style.Set( "box-shadow", ShadowCss );
				ApplyTile( p );
				break;
			case UiSurface.Frame:
				p.Style.BackgroundColor = C.PanelColor;
				SetBorder( p, M.BorderWidthStrong, C.BorderStrongColor, radius ?? M.RadiusLarge );
				p.Style.Set( "box-shadow", ShadowCss );
				ApplyTile( p );
				AddCornerOrnaments( p );
				break;
			case UiSurface.Raised:
				p.Style.BackgroundColor = C.PanelRaisedColor;
				SetBorder( p, M.BorderWidth, C.BorderSubtleColor, radius ?? M.Radius );
				p.Style.Set( "box-shadow", "none" );
				break;
			case UiSurface.Sunken:
				p.Style.BackgroundColor = C.PanelSunkenColor;
				SetBorder( p, M.BorderWidth, C.BorderSubtleColor, radius ?? M.Radius );
				p.Style.Set( "box-shadow", "none" );
				break;
			case UiSurface.Pill:
				p.Style.BackgroundColor = C.PanelColor;
				SetBorder( p, M.BorderWidth, C.BorderColor, radius ?? M.Radius );
				p.Style.Set( "box-shadow", "none" );
				break;
			case UiSurface.Tooltip:
				p.Style.BackgroundColor = C.TooltipColor;
				SetBorder( p, M.BorderWidth, C.BorderStrongColor, radius ?? M.Radius );
				p.Style.Set( "box-shadow", ShadowCss );
				break;
		}
	}

	/// <summary>The theme padding on every side (a scale multiplies it).</summary>
	public static void Padding( Panel p, float scale = 1f )
	{
		if ( p is null || !p.IsValid() )
			return;

		var pad = M.PanelPadding * scale;
		p.Style.PaddingTop = Length.Pixels( pad );
		p.Style.PaddingBottom = Length.Pixels( pad );
		p.Style.PaddingLeft = Length.Pixels( pad );
		p.Style.PaddingRight = Length.Pixels( pad );
	}

	public static void SetBorder( Panel p, float width, Color color, float radius )
	{
		if ( p is null || !p.IsValid() )
			return;

		p.Style.Set( "border-width", Px( width ) );
		p.Style.Set( "border-color", Css( color ) );
		p.Style.Set( "border-radius", Px( radius ) );
	}

	static void ApplyTile( Panel p )
	{
		var tile = UiTheme.Textures.PanelTile;
		if ( string.IsNullOrWhiteSpace( tile ) )
		{
			p.Style.Set( "background-image", "none" );
			return;
		}

		var texture = MenuUiTextures.TryLoad( tile );
		if ( texture is null )
			return;

		p.Style.SetBackgroundImage( texture );
		p.Style.Set( "background-repeat", "repeat" );
		p.Style.Set( "background-size", $"{Px( texture.Width )} {Px( texture.Height )}" );
		p.Style.Set( "background-position", "0px 0px" );
		p.Style.Set( "background-tint", Css( C.PanelTileTintColor.WithAlpha( C.PanelTileTintColor.a * UiTheme.Textures.PanelTileOpacity ) ) );
		p.Style.Set( "image-rendering", "pixelated" );
	}

	/// <summary>Four corner brackets from the theme texture, absolute inside <paramref name="frame"/>. No-op without a texture.</summary>
	public static void AddCornerOrnaments( Panel frame )
	{
		var path = UiTheme.Textures.CornerOrnament;
		if ( frame is null || !frame.IsValid() || string.IsNullOrWhiteSpace( path ) )
			return;

		var texture = MenuUiTextures.TryLoad( path );
		if ( texture is null )
			return;

		var size = M.CornerOrnament;
		for ( var i = 0; i < 4; i++ )
		{
			var corner = new Panel { Parent = frame };
			corner.AddClass( "ui-corner" );
			corner.Style.Set( "position", "absolute" );
			corner.Style.Width = Length.Pixels( size );
			corner.Style.Height = Length.Pixels( size );
			corner.Style.Set( "pointer-events", "none" );
			corner.Style.Set( "z-index", "5" );
			corner.Style.SetBackgroundImage( texture );
			corner.Style.Set( "background-size", "contain" );
			corner.Style.Set( "background-repeat", "no-repeat" );
			corner.Style.Set( "image-rendering", "pixelated" );
			corner.Style.Set( "background-tint", Css( C.AccentColor ) );
			var inset = Px( -M.BorderWidthStrong );
			var right = (i & 1) != 0;
			var bottom = (i & 2) != 0;
			corner.Style.Set( right ? "right" : "left", inset );
			corner.Style.Set( bottom ? "bottom" : "top", inset );
			var rotation = (right, bottom) switch
			{
				(false, false) => 0,
				(true, false) => 90,
				(true, true) => 180,
				_ => 270,
			};
			if ( rotation != 0 )
				corner.Style.Set( "transform", $"rotate({rotation}deg)" );
		}
	}

	/// <summary>Full-screen dim behind a modal / menu: backdrop tint, vignette texture, optional blur.</summary>
	public static void Backdrop( Panel p )
	{
		if ( p is null || !p.IsValid() )
			return;

		p.Style.BackgroundColor = C.BackdropColor;
		var vignette = UiTheme.Textures.Vignette;
		if ( !string.IsNullOrWhiteSpace( vignette ) && MenuUiTextures.TryLoad( vignette ) is { } texture )
		{
			p.Style.SetBackgroundImage( texture );
			p.Style.Set( "background-size", "100% 100%" );
			p.Style.Set( "background-repeat", "no-repeat" );
			p.Style.Set( "background-position", "center" );
		}

		if ( M.BackdropBlur > 0f )
			p.Style.Set( "backdrop-filter", $"blur({Px( M.BackdropBlur )})" );
	}

	/// <summary>A 1px (or 2px) rule in the divider colour; with a divider texture it fades at both ends.</summary>
	public static Panel Divider( Panel parent, float thickness = 1f, float marginY = 6f )
	{
		var rule = new Panel { Parent = parent };
		rule.AddClass( "ui-divider" );
		rule.Style.Set( "width", "100%" );
		rule.Style.Height = Length.Pixels( thickness );
		rule.Style.Set( "flex-shrink", "0" );
		rule.Style.MarginTop = Length.Pixels( marginY );
		rule.Style.MarginBottom = Length.Pixels( marginY );
		rule.Style.Set( "pointer-events", "none" );
		var path = UiTheme.Textures.Divider;
		if ( !string.IsNullOrWhiteSpace( path ) && MenuUiTextures.TryLoad( path ) is { } texture )
		{
			rule.Style.BackgroundColor = Color.Transparent;
			rule.Style.SetBackgroundImage( texture );
			rule.Style.Set( "background-size", "100% 100%" );
			rule.Style.Set( "background-repeat", "no-repeat" );
			rule.Style.Set( "background-tint", Css( C.AccentColor ) );
		}
		else
		{
			rule.Style.BackgroundColor = C.DividerColor;
		}

		return rule;
	}

	// ── text ───────────────────────────────────────────────────────────────────────────────────

	/// <summary>Font, size, weight and colour for a label. Alignment / wrapping stay with the caller.</summary>
	public static void Text( Label label, UiText kind, float? size = null )
	{
		if ( label is null || !label.IsValid() )
			return;

		switch ( kind )
		{
			case UiText.Title:
				label.Style.Set( "font-family", F.Heading );
				label.Style.Set( "font-weight", F.HeadingWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontTitle );
				label.Style.FontColor = C.TextPrimaryColor;
				label.Style.Set( "letter-spacing", Px( F.HeadingLetterSpacing ) );
				label.Style.Set( "text-transform", F.HeadingUppercase ? "uppercase" : "none" );
				label.Style.Set( "text-shadow", TextShadowCss );
				break;
			case UiText.Header:
				label.Style.Set( "font-family", F.Heading );
				label.Style.Set( "font-weight", F.HeadingWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontHeader );
				label.Style.FontColor = C.AccentColor;
				label.Style.Set( "letter-spacing", Px( F.HeadingLetterSpacing * 0.6f ) );
				label.Style.Set( "text-transform", F.HeadingUppercase ? "uppercase" : "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Body:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.BodyWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontBody );
				label.Style.FontColor = C.TextPrimaryColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Secondary:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.BodyWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontSmall );
				label.Style.FontColor = C.TextSecondaryColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Muted:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.BodyWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontSmall );
				label.Style.FontColor = C.TextMutedColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Value:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.ValueWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontBody );
				label.Style.FontColor = C.TextPrimaryColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Accent:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.ValueWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontBody );
				label.Style.FontColor = C.AccentBrightColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", "none" );
				break;
			case UiText.Hud:
				label.Style.Set( "font-family", F.Body );
				label.Style.Set( "font-weight", F.ValueWeight.ToString( CultureInfo.InvariantCulture ) );
				label.Style.FontSize = Length.Pixels( size ?? M.FontBody );
				label.Style.FontColor = C.TextPrimaryColor;
				label.Style.Set( "letter-spacing", "0px" );
				label.Style.Set( "text-transform", "none" );
				label.Style.Set( "text-shadow", TextShadowCss );
				break;
		}
	}

	public static Label AddLabel( Panel parent, string text, UiText kind, float? size = null )
	{
		var label = new Label { Parent = parent, Text = text ?? string.Empty };
		Text( label, kind, size );
		return label;
	}

	/// <summary>Section title with the accent rule under it: the standard top of every menu page.</summary>
	public static Label AddTitle( Panel parent, string text, float? size = null, bool centered = true )
	{
		var label = AddLabel( parent, text, UiText.Title, size );
		label.Style.Set( "width", "100%" );
		label.Style.Set( "text-align", centered ? "center" : "left" );
		label.Style.Set( "flex-shrink", "0" );
		Divider( parent, 1f, 4f );
		return label;
	}

	// ── controls ───────────────────────────────────────────────────────────────────────────────

	/// <summary>Button surface at rest. Call <see cref="SetButtonState"/> for hover / pressed / disabled.</summary>
	public static void Button( Panel p, UiButton kind = UiButton.Default, float? radius = null )
	{
		if ( p is null || !p.IsValid() )
			return;

		p.Style.Set( "cursor", "pointer" );
		p.Style.Set( "align-items", "center" );
		p.Style.Set( "justify-content", "center" );
		p.Style.Set( "border-radius", Px( radius ?? M.Radius ) );
		SetButtonState( p, kind, hovered: false, pressed: false, disabled: false );
	}

	public static void SetButtonState( Panel p, UiButton kind, bool hovered, bool pressed, bool disabled )
	{
		if ( p is null || !p.IsValid() )
			return;

		Color fill, border;
		switch ( kind )
		{
			case UiButton.Primary:
				fill = pressed ? C.ButtonPrimaryPressedColor : hovered ? C.ButtonPrimaryHoverColor : C.ButtonPrimaryColor;
				border = C.ButtonPrimaryBorderColor;
				break;
			case UiButton.Danger:
				fill = pressed ? C.ButtonPressedColor : hovered ? C.ButtonDangerHoverColor : C.ButtonDangerColor;
				border = C.ButtonDangerBorderColor;
				break;
			case UiButton.Ghost:
				fill = pressed ? C.ButtonPressedColor : hovered ? C.ButtonHoverColor : Color.Transparent;
				border = hovered || pressed ? C.BorderStrongColor : C.BorderSubtleColor;
				break;
			default:
				fill = pressed ? C.ButtonPressedColor : hovered ? C.ButtonHoverColor : C.ButtonColor;
				border = hovered || pressed ? C.BorderStrongColor : C.ButtonBorderColor;
				break;
		}

		p.Style.BackgroundColor = fill;
		p.Style.Set( "border-width", Px( M.BorderWidth ) );
		p.Style.Set( "border-color", Css( border ) );
		p.Style.Set( "opacity", disabled ? "0.45" : "1" );
		p.Style.Set( "box-shadow", hovered && !pressed && !disabled ? $"0px 0px 10px {Css( C.AccentGlowColor )}" : "none" );
	}

	/// <summary>Button label: body font, value weight, colour by kind.</summary>
	public static void ButtonLabel( Label label, UiButton kind = UiButton.Default, float? size = null )
	{
		if ( label is null || !label.IsValid() )
			return;

		Text( label, UiText.Value, size );
		label.Style.FontColor = kind == UiButton.Primary ? C.TextPrimaryColor : C.TextPrimaryColor;
		label.Style.Set( "pointer-events", "none" );
	}

	/// <summary>Inventory / hotbar / bind / build slot at rest (sized by the caller).</summary>
	public static void Slot( Panel p, float? radius = null )
	{
		if ( p is null || !p.IsValid() )
			return;

		p.Style.Set( "box-sizing", "border-box" );
		p.Style.Set( "overflow", "hidden" );
		p.Style.Set( "border-radius", Px( radius ?? M.RadiusSmall ) );
		SetSlotState( p, UiSlotState.Idle );
	}

	public static void SetSlotState( Panel p, UiSlotState state )
	{
		if ( p is null || !p.IsValid() )
			return;

		var fill = C.SlotColor;
		var border = C.SlotBorderColor;
		var width = M.BorderWidth;
		var glow = "none";
		switch ( state )
		{
			case UiSlotState.Hover:
				border = C.SlotHoverColor;
				break;
			case UiSlotState.Selected:
				border = C.SlotSelectedColor;
				width = M.BorderWidthStrong;
				glow = $"0px 0px 10px {Css( C.AccentGlowColor )}";
				break;
			case UiSlotState.Preferred:
				fill = C.SlotPreferredColor;
				break;
			case UiSlotState.Locked:
				fill = C.SlotLockedColor;
				border = C.BorderSubtleColor;
				break;
			case UiSlotState.Pending:
				border = C.SlotPendingColor;
				width = M.BorderWidthStrong;
				break;
			case UiSlotState.Active:
				border = C.SlotActiveColor;
				width = M.BorderWidthStrong;
				break;
			case UiSlotState.Danger:
				border = C.NegativeColor;
				width = M.BorderWidthStrong;
				break;
		}

		p.Style.BackgroundColor = fill;
		p.Style.Set( "border-width", Px( width ) );
		p.Style.Set( "border-color", Css( border ) );
		p.Style.Set( "box-shadow", glow );
	}

	/// <summary>Stack count badge in a slot corner.</summary>
	public static void CountBadge( Label label, float? size = null )
	{
		if ( label is null || !label.IsValid() )
			return;

		Text( label, UiText.Value, size ?? M.FontSmall );
		label.Style.Set( "padding-left", "3px" );
		label.Style.Set( "padding-right", "3px" );
		label.Style.Set( "padding-top", "1px" );
		label.Style.Set( "padding-bottom", "1px" );
		label.Style.BackgroundColor = C.PanelSunkenColor;
		label.Style.Set( "border-radius", Px( M.RadiusSmall ) );
		label.Style.Set( "text-shadow", TextShadowCss );
	}

	/// <summary>Progress / vitals bar: track + fill colours, border, radius. Sizes come from the caller.</summary>
	public static void Bar( Panel track, Panel fill, Color fillColor, Color? depletedColor = null, float? radius = null )
	{
		if ( track is not null && track.IsValid() )
		{
			track.Style.BackgroundColor = depletedColor ?? C.BarTrackColor;
			SetBorder( track, M.BorderWidth, C.BarBorderColor, radius ?? M.RadiusSmall );
			track.Style.Set( "overflow", "hidden" );
			track.Style.Set( "box-shadow", $"0px 2px 6px {Css( C.ShadowColor )}" );
		}

		if ( fill is not null && fill.IsValid() )
		{
			fill.Style.BackgroundColor = fillColor;
			fill.Style.Set( "border-radius", Px( Math.Max( 0f, (radius ?? M.RadiusSmall) - M.BorderWidth ) ) );
		}
	}

	/// <summary>A gloss strip over the top half of a bar fill (child of the fill; sized to it).</summary>
	public static Panel AddBarGloss( Panel fill )
	{
		if ( fill is null || !fill.IsValid() )
			return null;

		var gloss = new Panel { Parent = fill };
		gloss.Style.Set( "position", "absolute" );
		gloss.Style.Set( "left", "0" );
		gloss.Style.Set( "right", "0" );
		gloss.Style.Set( "top", "0" );
		gloss.Style.Set( "height", "45%" );
		gloss.Style.BackgroundColor = new Color( 1f, 1f, 1f, 0.14f );
		gloss.Style.Set( "pointer-events", "none" );
		return gloss;
	}

	/// <summary>Text drawn over a bar: value weight, primary colour, shadow so it reads on any fill.</summary>
	public static void BarLabel( Label label, float? size = null )
	{
		if ( label is null || !label.IsValid() )
			return;

		Text( label, UiText.Value, size ?? M.FontSmall );
		label.Style.Set( "text-shadow", TextShadowCss );
		label.Style.Set( "text-align", "center" );
	}

	/// <summary>Keyboard key cap ("E") next to a prompt.</summary>
	public static void KeyCap( Panel cap, Label key )
	{
		if ( cap is not null && cap.IsValid() )
		{
			cap.Style.MinWidth = Length.Pixels( M.KeyCap );
			cap.Style.Height = Length.Pixels( M.KeyCap );
			cap.Style.Set( "align-items", "center" );
			cap.Style.Set( "justify-content", "center" );
			cap.Style.PaddingLeft = Length.Pixels( 6f );
			cap.Style.PaddingRight = Length.Pixels( 6f );
			cap.Style.BackgroundColor = C.KeyCapColor;
			SetBorder( cap, M.BorderWidth, C.BorderStrongColor, M.RadiusSmall );
			cap.Style.Set( "box-shadow", $"0px 2px 0px {Css( C.BorderStrongColor )}" );
		}

		if ( key is not null && key.IsValid() )
		{
			Text( key, UiText.Value, M.FontSmall + 2f );
			key.Style.FontColor = C.KeyCapTextColor;
			key.Style.Set( "text-shadow", "none" );
		}
	}

	/// <summary>Top page tab (icon box).</summary>
	public static void Tab( Panel tab, bool selected )
	{
		if ( tab is null || !tab.IsValid() )
			return;

		tab.Style.BackgroundColor = selected ? C.TabSelectedColor : C.TabIdleColor;
		tab.Style.Set( "border-width", Px( selected ? M.BorderWidthStrong : M.BorderWidth ) );
		tab.Style.Set( "border-color", Css( selected ? C.AccentColor : C.BorderColor ) );
		tab.Style.Set( "border-radius", Px( M.RadiusSmall ) );
		tab.Style.Set( "box-shadow", selected ? $"0px 0px 12px {Css( C.AccentGlowColor )}" : "none" );
	}

	/// <summary>Scrollbar track + thumb colours.</summary>
	public static void ScrollBar( Panel track, Panel thumb )
	{
		if ( track is not null && track.IsValid() )
		{
			track.Style.BackgroundColor = C.ScrollTrackColor;
			SetBorder( track, M.BorderWidth, C.BorderSubtleColor, M.RadiusSmall );
		}

		if ( thumb is not null && thumb.IsValid() )
		{
			thumb.Style.BackgroundColor = C.ScrollThumbColor;
			thumb.Style.Set( "border-radius", Px( M.RadiusSmall ) );
		}
	}

	/// <summary>Soft cursor dot drawn by the menu overlay.</summary>
	public static void SoftCursor( Panel cursor )
	{
		if ( cursor is null || !cursor.IsValid() )
			return;

		cursor.Style.BackgroundColor = C.SoftCursorColor;
		cursor.Style.Set( "border-width", Px( M.BorderWidthStrong ) );
		cursor.Style.Set( "border-color", Css( C.SoftCursorBorderColor ) );
		cursor.Style.Set( "box-shadow", $"0px 1px 4px {Css( C.ShadowColor )}" );
	}

	/// <summary>Text entry field.</summary>
	public static void Field( Panel entry )
	{
		if ( entry is null || !entry.IsValid() )
			return;

		entry.Style.BackgroundColor = C.PanelSunkenColor;
		SetBorder( entry, M.BorderWidth, C.BorderColor, M.RadiusSmall );
		entry.Style.FontColor = C.TextPrimaryColor;
		entry.Style.FontSize = Length.Pixels( M.FontBody );
		entry.Style.Set( "font-family", F.Body );
	}

	/// <summary>Status colour for a buff / debuff / neutral framing.</summary>
	public static Color StatusColor( bool? positive ) =>
		positive switch
		{
			true => C.PositiveColor,
			false => C.NegativeColor,
			_ => C.AccentColor,
		};
}
