using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Base raid HUD: a full-width banner when a raid starts or ends ("Your base is being raided!" for
/// the owners of the raided beds, "A base is being raided!" for everyone else), and a small red
/// status line under the boss bar while it runs. Reads the <c>[Sync]</c> state on
/// <see cref="BaseRaidSession"/>; the minimap ring lives on <see cref="TerrainWorldMapFace"/>.
/// </summary>
public sealed class BaseRaidHud
{
	const float BannerSeconds = 4f;

	Panel _banner;
	Label _bannerText;
	double _bannerUntil;
	/// <summary>Raid event waiting to be worded on the next tick (a start waits briefly for the synced raid state).</summary>
	BaseRaidEvent? _pendingEvent;
	double _pendingSince;
	/// <summary>Did the running raid hit this viewer's bed? Kept after the beds are gone for the end banner.</summary>
	bool _ownBase;

	Panel _status;
	Label _statusText;
	bool _statusVisible;
	string _appliedStatus = string.Empty;

	public void Build( Panel parent )
	{
		if ( _banner is not null )
			return;

		// Same strip as the boss banner, a little lower so the two never overlap.
		_banner = new Panel { Parent = parent };
		_banner.Style.Set( "position", "absolute" );
		_banner.Style.Set( "left", "0" );
		_banner.Style.Set( "right", "0" );
		_banner.Style.Set( "top", "24%" );
		_banner.Style.Set( "height", "64px" );
		_banner.Style.Set( "align-items", "center" );
		_banner.Style.Set( "justify-content", "center" );
		_banner.Style.BackgroundColor = new Color( 0.35f, 0.03f, 0.02f, 0.78f );
		_banner.Style.Set( "pointer-events", "none" );
		_banner.Style.Set( "display", "none" );

		_bannerText = new Label { Parent = _banner, Text = "" };
		_bannerText.Style.FontColor = Color.White;
		_bannerText.Style.FontSize = Length.Pixels( 34f );
		_bannerText.Style.Set( "font-weight", "bold" );
		_bannerText.Style.Set( "text-shadow", "2px 2px 3px black" );

		_status = new Panel { Parent = parent };
		_status.Style.Set( "position", "absolute" );
		_status.Style.Set( "left", "50%" );
		_status.Style.Set( "top", "96px" );
		_status.Style.Set( "transform", "translateX(-50%)" );
		_status.Style.Set( "padding", "4px 12px" );
		_status.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.78f );
		_status.Style.Set( "border-radius", "6px" );
		_status.Style.Set( "border-width", "1px" );
		_status.Style.Set( "border-color", "#c0392b" );
		_status.Style.Set( "pointer-events", "none" );
		_status.Style.Set( "display", "none" );

		_statusText = new Label { Parent = _status, Text = "" };
		_statusText.Style.FontColor = new Color( 1f, 0.45f, 0.4f );
		_statusText.Style.FontSize = Length.Pixels( 18f );
		_statusText.Style.Set( "font-weight", "bold" );

		BaseRaidSession.RaidEventRaised += OnRaidEvent;
		BaseRaidSession.LocalNoticeRaised += ShowBanner;
	}

	public void Dispose()
	{
		BaseRaidSession.RaidEventRaised -= OnRaidEvent;
		BaseRaidSession.LocalNoticeRaised -= ShowBanner;
		_banner?.Delete();
		_banner = null;
		_bannerText = null;
		_status?.Delete();
		_status = null;
		_statusText = null;
	}

	/// <summary>Per frame from the local pawn's HUD.</summary>
	public void Tick( GameObject pawn )
	{
		if ( _banner is null || !_banner.IsValid() )
			return;

		var raid = BaseRaidSession.Instance;
		var active = raid is not null && raid.IsValid() && raid.IsRaidActive;
		if ( active )
			_ownBase = raid.IsRaidingBaseOf( pawn );

		if ( _pendingEvent is { } pending )
		{
			// The start RPC can beat the [Sync] raid state to a client — give it a second to land.
			var waitForState = pending == BaseRaidEvent.Started && !active && Time.NowDouble - _pendingSince < 1d;
			if ( !waitForState )
			{
				_pendingEvent = null;
				ShowBanner( BannerText( pending, _ownBase ) );
			}
		}

		if ( _bannerUntil > 0d && Time.NowDouble >= _bannerUntil )
		{
			_bannerUntil = 0d;
			_banner.Style.Set( "display", "none" );
		}

		if ( active != _statusVisible )
		{
			_statusVisible = active;
			_status.Style.Set( "display", active ? "flex" : "none" );
		}

		if ( !active )
			return;

		var text = _ownBase
			? $"Your base is under attack — {raid.RaidersRemaining} raiders left"
			: $"Base under attack — {raid.RaidersRemaining} raiders left";
		if ( string.Equals( text, _appliedStatus, StringComparison.Ordinal ) )
			return;

		_appliedStatus = text;
		_statusText.Text = text;
	}

	void OnRaidEvent( BaseRaidEvent raidEvent )
	{
		_pendingEvent = raidEvent;
		_pendingSince = Time.NowDouble;
	}

	static string BannerText( BaseRaidEvent raidEvent, bool ownBase ) => raidEvent switch
	{
		BaseRaidEvent.Started => ownBase ? "Your base is being raided!" : "A base is being raided!",
		BaseRaidEvent.Defeated => "Raid defeated!",
		BaseRaidEvent.BedsDestroyed => ownBase ? "Your base has fallen" : "The raided base has fallen",
		BaseRaidEvent.Cancelled => "Raid called off",
		_ => string.Empty,
	};

	void ShowBanner( string text )
	{
		if ( _banner is null || !_banner.IsValid() || string.IsNullOrWhiteSpace( text ) )
			return;

		_bannerText.Text = text;
		_banner.Style.Set( "display", "flex" );
		_bannerUntil = Time.NowDouble + BannerSeconds;
	}
}
