using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Screen-top boss bar: the boss's name over a bar whose black label reads "current/max".
/// A boss is shown to this viewer once their pawn comes within the boss's health-bar range of it —
/// at spawn or later, when they walk into range; once engaged it stays up until the boss is gone.
/// Every viewer also gets a 3 s full-width banner when a boss spawns, is killed, or despawns.
/// Reads the <c>[Sync]</c> mirror on <see cref="BossEntity"/>, so host and clients see the same
/// numbers. Second form: the fill jumps back to full and takes that form's colour from bosses.json.
/// </summary>
public sealed class BossHealthBarHud
{
	const float BarWidth = 480f;
	const float BarHeight = 28f;
	static readonly Color TrackColor = new( 0.82f, 0.82f, 0.83f );
	/// <summary>How long the spawn banner stays on screen.</summary>
	const float SpawnBannerSeconds = 3f;
	/// <summary>Range checks for not-yet-engaged bosses run this often, not every frame.</summary>
	const float RangeCheckInterval = 0.25f;

	Panel _root;
	Label _name;
	Panel _fill;
	Label _text;

	Panel _banner;
	Label _bannerText;
	double _bannerUntil;

	/// <summary>Bosses this viewer has been told about (banner shown), by GameObject id.</summary>
	readonly HashSet<Guid> _announced = new();
	/// <summary>Bosses this viewer is engaged with — the bar shows for these.</summary>
	readonly HashSet<Guid> _engaged = new();
	double _nextRangeCheck;

	bool _visible;
	string _appliedName = string.Empty;
	string _appliedText = string.Empty;
	string _appliedColor = string.Empty;
	float _appliedFillWidth = -1f;

	public void Build( Panel parent )
	{
		if ( _root is not null )
			return;

		_root = new Panel { Parent = parent };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "50%" );
		_root.Style.Set( "top", "28px" );
		_root.Style.Set( "transform", "translateX(-50%)" );
		_root.Style.Set( "flex-direction", "column" );
		_root.Style.Set( "align-items", "center" );
		_root.Style.Set( "gap", "4px" );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		_name = new Label { Parent = _root, Text = "" };
		_name.Style.FontColor = Color.White;
		_name.Style.FontSize = Length.Pixels( 26f );
		_name.Style.Set( "font-weight", "bold" );
		_name.Style.Set( "text-shadow", "1px 1px 0px #000, -1px -1px 0px #000, 1px -1px 0px #000, -1px 1px 0px #000" );

		var track = new Panel { Parent = _root };
		track.Style.Set( "position", "relative" );
		track.Style.Width = Length.Pixels( BarWidth );
		track.Style.Height = Length.Pixels( BarHeight );
		track.Style.BackgroundColor = TrackColor;
		track.Style.Set( "border-width", "2px" );
		track.Style.Set( "border-color", "#0d0d0d" );
		track.Style.Set( "border-radius", "2px" );
		track.Style.Set( "overflow", "hidden" );

		_fill = new Panel { Parent = track };
		_fill.Style.Set( "position", "absolute" );
		_fill.Style.Set( "top", "0" );
		_fill.Style.Set( "left", "0" );
		_fill.Style.Set( "height", "100%" );
		_fill.Style.Set( "z-index", "0" );
		_fill.Style.Width = Length.Pixels( BarWidth );

		_text = new Label { Parent = track, Text = "" };
		_text.Style.Set( "position", "absolute" );
		_text.Style.Set( "left", "0" );
		_text.Style.Set( "top", "0" );
		_text.Style.Set( "width", "100%" );
		_text.Style.Set( "height", "100%" );
		_text.Style.Set( "align-items", "center" );
		_text.Style.Set( "justify-content", "center" );
		_text.Style.Set( "z-index", "1" );
		_text.Style.FontColor = Color.Black;
		_text.Style.FontSize = Length.Pixels( 16f );
		_text.Style.Set( "font-weight", "bold" );

		// Spawn banner: a strip right across the screen, a little above centre so it never covers the crosshair.
		_banner = new Panel { Parent = parent };
		_banner.Style.Set( "position", "absolute" );
		_banner.Style.Set( "left", "0" );
		_banner.Style.Set( "right", "0" );
		_banner.Style.Set( "top", "34%" );
		_banner.Style.Set( "height", "64px" );
		_banner.Style.Set( "align-items", "center" );
		_banner.Style.Set( "justify-content", "center" );
		_banner.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.72f );
		_banner.Style.Set( "pointer-events", "none" );
		_banner.Style.Set( "display", "none" );

		_bannerText = new Label { Parent = _banner, Text = "" };
		_bannerText.Style.FontColor = Color.White;
		_bannerText.Style.FontSize = Length.Pixels( 34f );
		_bannerText.Style.Set( "font-weight", "bold" );
		_bannerText.Style.Set( "text-shadow", "2px 2px 3px black" );

		BossEntity.Announced += OnBossAnnounced;
	}

	public void Dispose()
	{
		BossEntity.Announced -= OnBossAnnounced;
		_root?.Delete();
		_root = null;
		_name = null;
		_fill = null;
		_text = null;
		_banner?.Delete();
		_banner = null;
		_bannerText = null;
		_announced.Clear();
		_engaged.Clear();
	}

	/// <summary>Per frame from the local pawn's HUD.</summary>
	public void Tick( Vector3 viewerPosition )
	{
		if ( _root is null || !_root.IsValid() )
			return;

		TickSpawnBanner();

		var boss = PickBoss( viewerPosition );
		if ( boss is null )
		{
			SetVisible( false );
			return;
		}

		SetVisible( true );

		var name = boss.DisplayName ?? string.Empty;
		if ( !string.Equals( _appliedName, name, StringComparison.Ordinal ) )
		{
			_appliedName = name;
			_name.Text = name;
		}

		var max = Math.Max( 1f, boss.MaxHealth );
		var current = Math.Clamp( boss.CurrentHealth, 0f, max );
		var text = $"{MathF.Ceiling( current ):0}/{MathF.Ceiling( max ):0}";
		if ( !string.Equals( _appliedText, text, StringComparison.Ordinal ) )
		{
			_appliedText = text;
			_text.Text = text;
		}

		var fillWidth = BarWidth * (current / max);
		if ( Math.Abs( fillWidth - _appliedFillWidth ) > 0.25f )
		{
			_appliedFillWidth = fillWidth;
			_fill.Style.Width = Length.Pixels( fillWidth );
		}

		var color = string.IsNullOrWhiteSpace( boss.BarColorHex ) ? BossEntity.FirstFormBarColorHex : boss.BarColorHex;
		if ( !string.Equals( _appliedColor, color, StringComparison.OrdinalIgnoreCase ) )
		{
			_appliedColor = color;
			_fill.Style.BackgroundColor = ParseBarColor( color );
		}
	}

	/// <summary>"#rgb" / "#rrggbb" / "#rrggbbaa" from bosses.json, or "r,g,b" floats; anything unreadable is first-form red.</summary>
	static Color ParseBarColor( string value )
	{
		var fallback = new Color( 0.92f, 0.08f, 0.06f );
		if ( string.IsNullOrWhiteSpace( value ) )
			return fallback;

		var text = value.Trim();
		if ( text.Contains( ',' ) )
			return ResourceDefinitionCatalog.ParseFallbackColor( text );

		if ( text.StartsWith( '#' ) )
			text = text[1..];

		if ( text.Length == 3 )
			text = $"{text[0]}{text[0]}{text[1]}{text[1]}{text[2]}{text[2]}";

		if ( text.Length is not (6 or 8) )
			return fallback;

		if ( !TryHexByte( text, 0, out var r ) || !TryHexByte( text, 2, out var g ) || !TryHexByte( text, 4, out var b ) )
			return fallback;

		var a = 255;
		if ( text.Length == 8 && !TryHexByte( text, 6, out a ) )
			a = 255;

		return new Color( r / 255f, g / 255f, b / 255f, a / 255f );
	}

	static bool TryHexByte( string text, int index, out int value ) =>
		int.TryParse( text.AsSpan( index, 2 ), System.Globalization.NumberStyles.HexNumber, null, out value );

	/// <summary>
	/// The nearest boss this viewer is engaged with. A boss engages the moment the pawn is within its
	/// health-bar range of it — checked at spawn and then every <see cref="RangeCheckInterval"/> until it is.
	/// Newly seen bosses also raise the spawn banner.
	/// </summary>
	BossEntity PickBoss( Vector3 viewerPosition )
	{
		BossEntity best = null;
		var bestDistance = float.MaxValue;
		var now = Time.NowDouble;
		var checkRange = now >= _nextRangeCheck;
		if ( checkRange )
			_nextRangeCheck = now + RangeCheckInterval;

		var active = BossEntity.Active;
		for ( var i = 0; i < active.Count; i++ )
		{
			var boss = active[i];
			if ( boss is null || !boss.IsValid() || !boss.GameObject.IsValid() || boss.IsDefeated )
				continue;

			// A fresh proxy may tick before its sync payload lands — a zero range means "not yet".
			if ( boss.HealthBarRange <= 0f || string.IsNullOrEmpty( boss.BossId ) )
				continue;

			var id = boss.GameObject.Id;
			if ( _announced.Add( id ) )
				ShowSpawnBanner( boss.DisplayName );

			var engaged = _engaged.Contains( id );
			if ( !engaged && checkRange
			     && Vector3.DistanceBetween( viewerPosition, boss.GameObject.WorldPosition ) <= boss.HealthBarRange )
			{
				_engaged.Add( id );
				engaged = true;
			}

			if ( !engaged )
				continue;

			var distance = Vector3.DistanceBetween( viewerPosition, boss.GameObject.WorldPosition );
			if ( distance < bestDistance )
			{
				bestDistance = distance;
				best = boss;
			}
		}

		return best;
	}

	void ShowSpawnBanner( string bossName ) => ShowBanner( bossName, "spawned" );

	/// <summary>Host says the boss was killed or despawned — the bar drops on its own once the object is gone.</summary>
	void OnBossAnnounced( string bossName, string verb ) => ShowBanner( bossName, verb );

	void ShowBanner( string bossName, string verb )
	{
		if ( _banner is null || _bannerText is null )
			return;

		var name = string.IsNullOrWhiteSpace( bossName ) ? "Boss" : bossName;
		_bannerText.Text = $"{name} {verb}";
		_bannerUntil = Time.NowDouble + SpawnBannerSeconds;
		_banner.Style.Set( "display", "flex" );
	}

	void TickSpawnBanner()
	{
		if ( _banner is null || _bannerUntil <= 0 || Time.NowDouble < _bannerUntil )
			return;

		_bannerUntil = 0;
		_banner.Style.Set( "display", "none" );
	}

	void SetVisible( bool visible )
	{
		if ( _visible == visible )
			return;

		_visible = visible;
		_root.Style.Set( "display", visible ? "flex" : "none" );
	}
}
