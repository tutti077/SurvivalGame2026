using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// The active UI theme: every HUD element and menu panel reads its colours, fonts, corner radii and
/// textures from here (through <see cref="UiStyle"/> for code-built panels, through the generated
/// stylesheet in <see cref="UiThemeStyleSheet"/> for razor panels). Themes live in
/// <c>data/ui_themes.json</c>; the per-machine choice is saved to <c>ui_theme.json</c> in
/// <c>FileSystem.Data</c>. Swap with <c>ui_theme &lt;id&gt;</c>. Nothing in a panel should name a colour,
/// font or radius of its own — add a field to <see cref="UiThemeColors"/> / <see cref="UiThemeMetrics"/>
/// instead so every theme can set it.
/// </summary>
public static class UiTheme
{
	const string CatalogPath = "data/ui_themes.json";
	const string ChoiceFileName = "ui_theme.json";

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	static readonly List<UiThemeData> _themes = new();
	static UiThemeData _current;
	static bool _loaded;
	static int _version;

	/// <summary>Raised after the active theme changes. Built panels re-style themselves; the stylesheet re-attaches.</summary>
	public static event Action Changed;

	/// <summary>Bumps on every theme change — cheap "is my cached style stale" check.</summary>
	public static int Version
	{
		get
		{
			EnsureLoaded();
			return _version;
		}
	}

	public static UiThemeData Current
	{
		get
		{
			EnsureLoaded();
			return _current;
		}
	}

	public static UiThemeColors Colors => Current.Colors;
	public static UiThemeMetrics Metrics => Current.Metrics;
	public static UiThemeFonts Fonts => Current.Fonts;
	public static UiThemeTextures Textures => Current.Textures;

	public static IReadOnlyList<UiThemeData> All
	{
		get
		{
			EnsureLoaded();
			return _themes;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		_themes.Clear();

		UiThemeCatalogFile file = null;
		try
		{
			var json = ReadCatalogJson();
			if ( !string.IsNullOrWhiteSpace( json ) )
				file = JsonSerializer.Deserialize<UiThemeCatalogFile>( json, JsonOptions );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[UiTheme] Could not read {CatalogPath}: {ex.Message} — built-in fallback theme in use." );
		}

		if ( file?.Themes is not null )
		{
			foreach ( var theme in file.Themes )
			{
				if ( theme is null || string.IsNullOrWhiteSpace( theme.Id ) )
					continue;

				theme.Resolve();
				_themes.Add( theme );
			}
		}

		if ( _themes.Count == 0 )
		{
			var fallback = new UiThemeData { Id = "fallback", DisplayName = "Fallback" };
			fallback.Resolve();
			_themes.Add( fallback );
		}

		var wanted = ReadSavedChoice() ?? file?.DefaultTheme;
		_current = Find( wanted ) ?? Find( file?.DefaultTheme ) ?? _themes[0];
		_version++;
	}

	/// <summary>Host-synced copy first (joining clients have no mounted data folder), then the mounted file.</summary>
	static string ReadCatalogJson()
	{
		if ( SyncedUiContent.HasFile( CatalogPath ) )
		{
			try
			{
				var dataPath = SyncedUiContent.ToDataPath( CatalogPath );
				if ( !string.IsNullOrWhiteSpace( dataPath ) && FileSystem.Data.FileExists( dataPath ) )
					return FileSystem.Data.ReadAllText( dataPath );
			}
			catch
			{
				// fall through to the mounted file
			}
		}

		return FileSystem.Mounted.FileExists( CatalogPath ) ? FileSystem.Mounted.ReadAllText( CatalogPath ) : null;
	}

	public static UiThemeData Find( string id )
	{
		if ( string.IsNullOrWhiteSpace( id ) )
			return null;

		EnsureLoaded();
		for ( var i = 0; i < _themes.Count; i++ )
		{
			if ( string.Equals( _themes[i].Id, id, StringComparison.OrdinalIgnoreCase ) )
				return _themes[i];
		}

		return null;
	}

	/// <summary>Switch the active theme and remember it on this machine. False when no theme has that id.</summary>
	public static bool TrySet( string id, bool save = true )
	{
		var theme = Find( id );
		if ( theme is null )
			return false;

		if ( !ReferenceEquals( theme, _current ) )
		{
			_current = theme;
			_version++;
			Changed?.Invoke();
		}

		if ( save )
			SaveChoice( theme.Id );

		return true;
	}

	/// <summary>Re-read the JSON (editing a theme while the game runs) and re-apply the current id.</summary>
	public static void Reload()
	{
		var keep = _current?.Id;
		_loaded = false;
		_themes.Clear();
		EnsureLoaded();
		if ( keep is not null && Find( keep ) is { } same )
			_current = same;
		_version++;
		Changed?.Invoke();
	}

	static string ReadSavedChoice()
	{
		try
		{
			if ( !FileSystem.Data.FileExists( ChoiceFileName ) )
				return null;

			var choice = JsonSerializer.Deserialize<ChoiceFile>( FileSystem.Data.ReadAllText( ChoiceFileName ), JsonOptions );
			return string.IsNullOrWhiteSpace( choice?.Theme ) ? null : choice.Theme;
		}
		catch
		{
			return null;
		}
	}

	static void SaveChoice( string id )
	{
		try
		{
			FileSystem.Data.WriteAllText( ChoiceFileName, JsonSerializer.Serialize( new ChoiceFile { Theme = id } ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[UiTheme] Could not save theme choice: {ex.Message}" );
		}
	}

	[ConCmd( "ui_theme" )]
	public static void ConCmdUiTheme( string id = "" )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( id ) )
		{
			Log.Info( $"[UiTheme] Active: {_current.Id} ({_current.DisplayName})" );
			foreach ( var theme in _themes )
				Log.Info( $"[UiTheme]   {theme.Id} — {theme.DisplayName}" );
			Log.Info( "[UiTheme] ui_theme <id> switches (saved per machine); ui_theme reload re-reads data/ui_themes.json." );
			return;
		}

		if ( string.Equals( id, "reload", StringComparison.OrdinalIgnoreCase ) )
		{
			Reload();
			Log.Info( $"[UiTheme] Reloaded {_themes.Count} theme(s); active {_current.Id}." );
			return;
		}

		if ( !TrySet( id ) )
		{
			Log.Warning( $"[UiTheme] No theme '{id}'. Known: {string.Join( ", ", _themes.ConvertAll( t => t.Id ) )}" );
			return;
		}

		Log.Info( $"[UiTheme] Theme set to {_current.Id}. Menus built before the switch restyle on their next open; the HUD restyles live where it can." );
	}

	sealed class UiThemeCatalogFile
	{
		public string DefaultTheme { get; set; }
		public List<UiThemeData> Themes { get; set; }
	}

	sealed class ChoiceFile
	{
		public string Theme { get; set; }
	}
}

/// <summary>One theme from <c>data/ui_themes.json</c>.</summary>
public sealed class UiThemeData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public UiThemeFonts Fonts { get; set; } = new();
	public UiThemeColors Colors { get; set; } = new();
	public UiThemeMetrics Metrics { get; set; } = new();
	public UiThemeTextures Textures { get; set; } = new();

	/// <summary>Parses the colour strings once; missing fields keep their built-in default.</summary>
	public void Resolve()
	{
		Fonts ??= new UiThemeFonts();
		Colors ??= new UiThemeColors();
		Metrics ??= new UiThemeMetrics();
		Textures ??= new UiThemeTextures();
		Colors.Resolve();
	}
}

public sealed class UiThemeFonts
{
	public string Body { get; set; } = "Poppins";
	public string Heading { get; set; } = "Poppins";
	public int HeadingWeight { get; set; } = 600;
	public float HeadingLetterSpacing { get; set; } = 1.6f;
	public bool HeadingUppercase { get; set; } = true;
	public int BodyWeight { get; set; } = 400;
	public int ValueWeight { get; set; } = 600;
	public string Mono { get; set; } = "Roboto Mono";
}

public sealed class UiThemeMetrics
{
	public float Radius { get; set; } = 6f;
	public float RadiusSmall { get; set; } = 3f;
	public float RadiusLarge { get; set; } = 10f;
	public float BorderWidth { get; set; } = 1f;
	public float BorderWidthStrong { get; set; } = 2f;
	public float PanelPadding { get; set; } = 18f;
	public float Gap { get; set; } = 10f;
	public float FontTitle { get; set; } = 28f;
	public float FontHeader { get; set; } = 20f;
	public float FontBody { get; set; } = 17f;
	public float FontSmall { get; set; } = 13f;
	public float FontMicro { get; set; } = 11f;
	public float IconButton { get; set; } = 52f;
	public float Slot { get; set; } = 64f;
	public float BarHeight { get; set; } = 22f;
	public float KeyCap { get; set; } = 28f;
	public float CornerOrnament { get; set; } = 22f;
	/// <summary>Offset + blur part of the panel drop shadow ("0px 6px 18px"); the colour is <see cref="UiThemeColors.Shadow"/>.</summary>
	public string Shadow { get; set; } = "0px 6px 18px";
	/// <summary>backdrop-filter blur radius (px) behind full-screen menus; 0 = none.</summary>
	public float BackdropBlur { get; set; }
}

public sealed class UiThemeTextures
{
	/// <summary>Repeating grain tile drawn over panel backgrounds (empty = flat colour).</summary>
	public string PanelTile { get; set; } = string.Empty;
	public float PanelTileOpacity { get; set; } = 0.28f;
	/// <summary>Corner bracket drawn in the four corners of a framed panel (empty = none).</summary>
	public string CornerOrnament { get; set; } = string.Empty;
	/// <summary>Radial vignette stretched over the menu backdrop (empty = flat tint).</summary>
	public string Vignette { get; set; } = string.Empty;
	/// <summary>Horizontal rule under titles, fading at both ends (empty = flat divider colour).</summary>
	public string Divider { get; set; } = string.Empty;
	public string MainMenuBackground { get; set; } = "ui/menu/mainMenuBackground.png";
}

/// <summary>
/// Theme palette. JSON strings parse into <see cref="Color"/>s in <see cref="Resolve"/>; the
/// <c>*Css</c> helpers in <see cref="UiStyle"/> format them for <c>Style.Set</c>.
/// </summary>
public sealed class UiThemeColors
{
	// JSON-facing strings (named like the colours; the parsed Color is the same name without "Hex").
	public string Backdrop { get; set; } = "#05070ab0";
	public string Panel { get; set; } = "#11161bee";
	public string PanelRaised { get; set; } = "#1a2128f5";
	public string PanelSunken { get; set; } = "#090c0ff2";
	public string PanelTileTint { get; set; } = "#ffffff40";
	public string Border { get; set; } = "#3a3426";
	public string BorderStrong { get; set; } = "#8a6a2e";
	public string BorderSubtle { get; set; } = "#23282e";
	public string Accent { get; set; } = "#d9a441";
	public string AccentBright { get; set; } = "#f4c66a";
	public string AccentDim { get; set; } = "#8a6a2e";
	public string AccentGlow { get; set; } = "#d9a44173";
	public string TextPrimary { get; set; } = "#f1ebdd";
	public string TextSecondary { get; set; } = "#b9b2a3";
	public string TextMuted { get; set; } = "#7d786d";
	public string TextOnAccent { get; set; } = "#1a1309";
	public string TextShadow { get; set; } = "#000000d9";
	public string Positive { get; set; } = "#7fc46a";
	public string Negative { get; set; } = "#e05a4a";
	public string Warning { get; set; } = "#e8b84a";
	public string Info { get; set; } = "#6fb0e0";
	public string Health { get; set; } = "#c43a2f";
	public string HealthDepleted { get; set; } = "#3b1512";
	public string Stamina { get; set; } = "#e2b43a";
	public string StaminaDepleted { get; set; } = "#3e3112";
	public string Oxygen { get; set; } = "#4fa6d8";
	public string BarTrack { get; set; } = "#0a0c0ed9";
	public string BarBorder { get; set; } = "#000000cc";
	public string Slot { get; set; } = "#0d1013f0";
	public string SlotBorder { get; set; } = "#2c2a26";
	public string SlotHover { get; set; } = "#8a6a2e";
	public string SlotSelected { get; set; } = "#d9a441";
	public string SlotPreferred { get; set; } = "#2a2620f2";
	public string SlotLocked { get; set; } = "#1a1c20";
	public string SlotPending { get; set; } = "#e0b84a";
	public string SlotActive { get; set; } = "#5ec46a";
	public string Button { get; set; } = "#1c2228f5";
	public string ButtonHover { get; set; } = "#262e36";
	public string ButtonPressed { get; set; } = "#0f1316";
	public string ButtonBorder { get; set; } = "#3a4046";
	public string ButtonPrimary { get; set; } = "#8a5f1ef5";
	public string ButtonPrimaryHover { get; set; } = "#a8752a";
	public string ButtonPrimaryPressed { get; set; } = "#6b4714";
	public string ButtonPrimaryBorder { get; set; } = "#d9a441";
	public string ButtonDanger { get; set; } = "#6b2520f5";
	public string ButtonDangerHover { get; set; } = "#8a2f28";
	public string ButtonDangerBorder { get; set; } = "#b0423a";
	public string Tooltip { get; set; } = "#0b0e11f7";
	public string KeyCap { get; set; } = "#e9e2d2";
	public string KeyCapText { get; set; } = "#1a1309";
	public string Divider { get; set; } = "#d9a44140";
	public string ScrollTrack { get; set; } = "#0a0c0ef2";
	public string ScrollThumb { get; set; } = "#8a6a2ef2";
	public string SoftCursor { get; set; } = "#f1ebdd";
	public string SoftCursorBorder { get; set; } = "#1a1309";
	public string TabIdle { get; set; } = "#161b21f5";
	public string TabSelected { get; set; } = "#2a2419fa";
	public string Shadow { get; set; } = "#0000008c";

	// Parsed colours — filled by Resolve(). System.Text.Json ignores fields.
	public Color BackdropColor, PanelColor, PanelRaisedColor, PanelSunkenColor, PanelTileTintColor;
	public Color BorderColor, BorderStrongColor, BorderSubtleColor;
	public Color AccentColor, AccentBrightColor, AccentDimColor, AccentGlowColor;
	public Color TextPrimaryColor, TextSecondaryColor, TextMutedColor, TextOnAccentColor, TextShadowColor;
	public Color PositiveColor, NegativeColor, WarningColor, InfoColor;
	public Color HealthColor, HealthDepletedColor, StaminaColor, StaminaDepletedColor, OxygenColor, BarTrackColor, BarBorderColor;
	public Color SlotColor, SlotBorderColor, SlotHoverColor, SlotSelectedColor, SlotPreferredColor, SlotLockedColor, SlotPendingColor, SlotActiveColor;
	public Color ButtonColor, ButtonHoverColor, ButtonPressedColor, ButtonBorderColor;
	public Color ButtonPrimaryColor, ButtonPrimaryHoverColor, ButtonPrimaryPressedColor, ButtonPrimaryBorderColor;
	public Color ButtonDangerColor, ButtonDangerHoverColor, ButtonDangerBorderColor;
	public Color TooltipColor, KeyCapColor, KeyCapTextColor, DividerColor, ScrollTrackColor, ScrollThumbColor;
	public Color SoftCursorColor, SoftCursorBorderColor, TabIdleColor, TabSelectedColor, ShadowColor;

	public void Resolve()
	{
		BackdropColor = ParseColor( Backdrop, new Color( 0.02f, 0.03f, 0.04f, 0.69f ) );
		PanelColor = ParseColor( Panel, new Color( 0.07f, 0.09f, 0.11f, 0.93f ) );
		PanelRaisedColor = ParseColor( PanelRaised, new Color( 0.10f, 0.13f, 0.16f, 0.96f ) );
		PanelSunkenColor = ParseColor( PanelSunken, new Color( 0.035f, 0.047f, 0.06f, 0.95f ) );
		PanelTileTintColor = ParseColor( PanelTileTint, new Color( 1f, 1f, 1f, 0.25f ) );
		BorderColor = ParseColor( Border, new Color( 0.23f, 0.20f, 0.15f ) );
		BorderStrongColor = ParseColor( BorderStrong, new Color( 0.54f, 0.42f, 0.18f ) );
		BorderSubtleColor = ParseColor( BorderSubtle, new Color( 0.14f, 0.16f, 0.18f ) );
		AccentColor = ParseColor( Accent, new Color( 0.85f, 0.64f, 0.25f ) );
		AccentBrightColor = ParseColor( AccentBright, new Color( 0.96f, 0.78f, 0.42f ) );
		AccentDimColor = ParseColor( AccentDim, new Color( 0.54f, 0.42f, 0.18f ) );
		AccentGlowColor = ParseColor( AccentGlow, new Color( 0.85f, 0.64f, 0.25f, 0.45f ) );
		TextPrimaryColor = ParseColor( TextPrimary, new Color( 0.95f, 0.92f, 0.87f ) );
		TextSecondaryColor = ParseColor( TextSecondary, new Color( 0.73f, 0.70f, 0.64f ) );
		TextMutedColor = ParseColor( TextMuted, new Color( 0.49f, 0.47f, 0.43f ) );
		TextOnAccentColor = ParseColor( TextOnAccent, new Color( 0.10f, 0.07f, 0.04f ) );
		TextShadowColor = ParseColor( TextShadow, new Color( 0f, 0f, 0f, 0.85f ) );
		PositiveColor = ParseColor( Positive, new Color( 0.50f, 0.77f, 0.42f ) );
		NegativeColor = ParseColor( Negative, new Color( 0.88f, 0.35f, 0.29f ) );
		WarningColor = ParseColor( Warning, new Color( 0.91f, 0.72f, 0.29f ) );
		InfoColor = ParseColor( Info, new Color( 0.44f, 0.69f, 0.88f ) );
		HealthColor = ParseColor( Health, new Color( 0.77f, 0.23f, 0.18f ) );
		HealthDepletedColor = ParseColor( HealthDepleted, new Color( 0.23f, 0.08f, 0.07f ) );
		StaminaColor = ParseColor( Stamina, new Color( 0.89f, 0.71f, 0.23f ) );
		StaminaDepletedColor = ParseColor( StaminaDepleted, new Color( 0.24f, 0.19f, 0.07f ) );
		OxygenColor = ParseColor( Oxygen, new Color( 0.31f, 0.65f, 0.85f ) );
		BarTrackColor = ParseColor( BarTrack, new Color( 0.04f, 0.05f, 0.055f, 0.85f ) );
		BarBorderColor = ParseColor( BarBorder, new Color( 0f, 0f, 0f, 0.8f ) );
		SlotColor = ParseColor( Slot, new Color( 0.05f, 0.063f, 0.075f, 0.94f ) );
		SlotBorderColor = ParseColor( SlotBorder, new Color( 0.17f, 0.16f, 0.15f ) );
		SlotHoverColor = ParseColor( SlotHover, new Color( 0.54f, 0.42f, 0.18f ) );
		SlotSelectedColor = ParseColor( SlotSelected, new Color( 0.85f, 0.64f, 0.25f ) );
		SlotPreferredColor = ParseColor( SlotPreferred, new Color( 0.16f, 0.15f, 0.125f, 0.95f ) );
		SlotLockedColor = ParseColor( SlotLocked, new Color( 0.10f, 0.11f, 0.125f ) );
		SlotPendingColor = ParseColor( SlotPending, new Color( 0.88f, 0.72f, 0.29f ) );
		SlotActiveColor = ParseColor( SlotActive, new Color( 0.37f, 0.77f, 0.42f ) );
		ButtonColor = ParseColor( Button, new Color( 0.11f, 0.13f, 0.16f, 0.96f ) );
		ButtonHoverColor = ParseColor( ButtonHover, new Color( 0.15f, 0.18f, 0.21f ) );
		ButtonPressedColor = ParseColor( ButtonPressed, new Color( 0.06f, 0.075f, 0.086f ) );
		ButtonBorderColor = ParseColor( ButtonBorder, new Color( 0.23f, 0.25f, 0.27f ) );
		ButtonPrimaryColor = ParseColor( ButtonPrimary, new Color( 0.54f, 0.37f, 0.12f, 0.96f ) );
		ButtonPrimaryHoverColor = ParseColor( ButtonPrimaryHover, new Color( 0.66f, 0.46f, 0.16f ) );
		ButtonPrimaryPressedColor = ParseColor( ButtonPrimaryPressed, new Color( 0.42f, 0.28f, 0.08f ) );
		ButtonPrimaryBorderColor = ParseColor( ButtonPrimaryBorder, new Color( 0.85f, 0.64f, 0.25f ) );
		ButtonDangerColor = ParseColor( ButtonDanger, new Color( 0.42f, 0.145f, 0.125f, 0.96f ) );
		ButtonDangerHoverColor = ParseColor( ButtonDangerHover, new Color( 0.54f, 0.18f, 0.16f ) );
		ButtonDangerBorderColor = ParseColor( ButtonDangerBorder, new Color( 0.69f, 0.26f, 0.23f ) );
		TooltipColor = ParseColor( Tooltip, new Color( 0.043f, 0.055f, 0.067f, 0.97f ) );
		KeyCapColor = ParseColor( KeyCap, new Color( 0.91f, 0.89f, 0.82f ) );
		KeyCapTextColor = ParseColor( KeyCapText, new Color( 0.10f, 0.07f, 0.04f ) );
		DividerColor = ParseColor( Divider, new Color( 0.85f, 0.64f, 0.25f, 0.25f ) );
		ScrollTrackColor = ParseColor( ScrollTrack, new Color( 0.04f, 0.05f, 0.055f, 0.95f ) );
		ScrollThumbColor = ParseColor( ScrollThumb, new Color( 0.54f, 0.42f, 0.18f, 0.95f ) );
		SoftCursorColor = ParseColor( SoftCursor, new Color( 0.95f, 0.92f, 0.87f ) );
		SoftCursorBorderColor = ParseColor( SoftCursorBorder, new Color( 0.10f, 0.07f, 0.04f ) );
		TabIdleColor = ParseColor( TabIdle, new Color( 0.086f, 0.106f, 0.13f, 0.96f ) );
		TabSelectedColor = ParseColor( TabSelected, new Color( 0.165f, 0.14f, 0.10f, 0.98f ) );
		ShadowColor = ParseColor( Shadow, new Color( 0f, 0f, 0f, 0.55f ) );
	}

	/// <summary>"#rgb", "#rrggbb", "#rrggbbaa" or "r,g,b[,a]" in 0–1. Anything else = <paramref name="fallback"/>.</summary>
	public static Color ParseColor( string text, Color fallback )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return fallback;

		text = text.Trim();
		if ( text.StartsWith( '#' ) )
		{
			var hex = text[1..];
			if ( hex.Length == 3 )
				hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
			if ( hex.Length is not (6 or 8) )
				return fallback;

			if ( !int.TryParse( hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r )
			     || !int.TryParse( hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g )
			     || !int.TryParse( hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b ) )
				return fallback;

			var a = 255;
			if ( hex.Length == 8 && !int.TryParse( hex[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a ) )
				return fallback;

			return new Color( r / 255f, g / 255f, b / 255f, a / 255f );
		}

		var parts = text.Split( ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length is 3 or 4
		     && float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var fr )
		     && float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fg )
		     && float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fb ) )
		{
			var fa = 1f;
			if ( parts.Length == 4 && !float.TryParse( parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out fa ) )
				return fallback;

			return new Color( fr, fg, fb, fa );
		}

		return fallback;
	}
}
