using System;
using System.Globalization;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Unified screen-center crosshair — the single draw path replacing the old separate combat
/// teardrop and grapple reticle overlays. Composable layers, all state read from the owning
/// pawn's components:
///   base  — thin white ring; while grapple aim HUD is active it stays a thin hollow white
///           donut (so the inner lock ring — where the attach ray will cast — stays readable)
///   arrow — directional attack teardrop when the active hotbar item has
///           <see cref="EquippedItemActions.PrimaryMelee"/> (build hammer shows the plain ring)
///   inner — small yellow lock ring when a grapple attach target is valid right now; slides
///           with aim assist to the actual attach point (also mid-grapple for re-targeting)
/// Hidden while any game menu is open.
/// <para>
/// The rings are UI panels (a bordered circle each) under the pawn's <see cref="PlayerScreenHud"/>
/// root, restyled only when their state changes. They used to be 40-segment
/// <c>HudPainter.DrawLine</c> polylines rebuilt every frame — 80 line draws for the idle ring alone,
/// measured at 2.5 ms a frame (Mark's <c>perf_components</c> table, 2026-09-29): a third of the
/// whole frame at 120 fps. Only the attack triangle still uses overlay lines, and only while a melee
/// weapon is out, with a fan small enough to cover 8 px.
/// </para>
/// </summary>
[Title( "Player Crosshair" )]
public sealed class PlayerCrosshair : Component
{
	[Property, Title( "Show Crosshair" )]
	public bool ShowCrosshair { get; set; } = true;

	const float BaseRadius = 7f;
	const float BaseLineWidth = 1.75f;
	const float GrappleRingRadius = 10f;
	const float GrappleRingWidth = 1.5f;
	const float ArrowTipLength = 8f;
	const float ArrowHalfWidth = 4f;
	const float InnerRingRadius = 3f;
	const float InnerRingWidth = 1.5f;
	/// <summary>Dead-center aim → keep the lock ring in the bullseye (avoid 1px projection jitter).</summary>
	const float InnerCenterSnapPixels = 3f;
	/// <summary>Black edge on each side of every shape so white/yellow forms read against any backdrop.</summary>
	const float BorderWidth = 1.25f;

	static readonly Color CrosshairWhite = Color.White.WithAlpha( 0.95f );
	static readonly Color GrappleYellow = new( 1f, 0.92f, 0.2f, 0.95f );
	static readonly Color BorderBlack = Color.Black.WithAlpha( 0.9f );
	static readonly Color BowDrawRing = Color.White.WithAlpha( 0.35f );

	PlayerMovement _movement;
	PlayerCombat _combat;
	PlayerEquipment _equipment;
	PlayerGameMenuController _menu;
	PlayerInventoryInteraction _interaction;
	PlayerScreenHud _hud;

	Panel _host;
	RingUi _base;
	RingUi _bow;
	RingUi _lock;
	bool? _hostShown;

	protected override void OnStart()
	{
		base.OnStart();
		// EverythingInSelf so a component disabled at spawn time still caches here rather than
		// sticking as null. The teardrop itself is gated on weaponOut below.
		_movement = Components.Get<PlayerMovement>( FindMode.EverythingInSelf );
		_combat = Components.Get<PlayerCombat>( FindMode.EverythingInSelf );
		_equipment = Components.Get<PlayerEquipment>( FindMode.EverythingInSelf );
		_menu = Components.Get<PlayerGameMenuController>( FindMode.EverythingInSelf );
		_interaction = Components.Get<PlayerInventoryInteraction>( FindMode.EverythingInSelf );
		_hud = Components.Get<PlayerScreenHud>( FindMode.EverythingInSelf );
	}

	protected override void OnDisabled()
	{
		base.OnDisabled();
		DestroyPanels();
	}

	protected override void OnDestroy()
	{
		DestroyPanels();
		base.OnDestroy();
	}

	protected override void OnPreRender()
	{
		base.OnPreRender();

		if ( !EnsurePanels() )
			return;

		var show = ShouldShow();
		SetHostShown( show );
		if ( !show )
			return;

		var grappleMode = _movement?.IsAimHudActive == true;
		var weaponOut = _equipment?.MainHandHasAction( EquippedItemActions.PrimaryMelee ) == true;
		var bowDrawing = _combat is not null && _combat.IsBowCharging;

		// Base: thin hollow white donut while grappling (hole shows the yellow lock helper); thin white ring otherwise.
		float baseOuterEdge;
		if ( grappleMode )
		{
			_base.Apply( true, GrappleRingRadius, GrappleRingWidth, CrosshairWhite, null );
			baseOuterEdge = GrappleRingRadius + GrappleRingWidth * 0.5f;
		}
		else
		{
			_base.Apply( true, BaseRadius, BaseLineWidth, CrosshairWhite, null );
			baseOuterEdge = BaseRadius + BaseLineWidth * 0.5f;
		}

		if ( bowDrawing )
			_bow.Apply( true, Math.Max( BaseRadius, _combat.GetBowDrawRingRadiusPixels() ), BaseLineWidth, BowDrawRing, null );
		else
			_bow.Apply( false, 0f, 0f, default, null );

		if ( grappleMode && _movement.HasValidAimTarget )
			ApplyInnerLockRing();
		else
			_lock.Apply( false, 0f, 0f, default, null );

		// The attack teardrop is a filled triangle — still overlay lines, only while a melee weapon is out.
		if ( weaponOut && _combat is not null )
		{
			var cam = BuildViewCamera.Resolve( GameObject );
			if ( cam.IsValid() )
			{
				var rect = cam.ScreenRect;
				var center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );
				DrawArrow( cam, center, _combat.GetTeardropScreenDirection(), baseOuterEdge );
			}
		}
	}

	bool ShouldShow()
	{
		if ( !ShowCrosshair || !IsLocalDriver() )
			return false;

		_menu ??= Components.Get<PlayerGameMenuController>( FindMode.EverythingInSelfAndAncestors );
		if ( _menu is not null && _menu.IsMenuOpen )
			return false;

		// The menu overlay drives a software cursor whenever it is open — belt and braces so the
		// aim ring can never show through the map page.
		if ( InventoryScreenPointer.SoftCursorActive )
			return false;

		// Cursor-owning modals: only the mouse cursor should show, not the aim ring.
		if ( _interaction is { IsTimeTrialMenuOpen: true } or { IsArenaMenuOpen: true } )
			return false;

		// Grapple control-scheme prompt (per-machine first-use choice).
		if ( GrappleControlSchemeStore.NeedsChoice && _movement?.HasGrappleEquipped() == true )
			return false;

		return true;
	}

	/// <summary>Same driver rule as PlayerMovement: owner client, or host for host/ownerless pawns.</summary>
	bool IsLocalDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is not { Active: true } net )
			return true;

		return net.Owner is null ? Networking.IsHost : net.IsOwner;
	}

	// ── Panels ───────────────────────────────────────────────────────────────────────────────

	/// <summary>Host panel under the HUD root; rebuilt if the HUD root was recreated (hotload / rebuild).</summary>
	bool EnsurePanels()
	{
		_hud ??= Components.Get<PlayerScreenHud>( FindMode.EverythingInSelf );
		var root = _hud?.Panel;
		if ( root is null || !root.IsValid() )
			return false;

		if ( _host is not null && _host.IsValid() && _host.Parent == root )
			return true;

		DestroyPanels();

		_host = new Panel { Parent = root };
		_host.Style.Set( "position", "absolute" );
		_host.Style.Set( "left", "0" );
		_host.Style.Set( "top", "0" );
		_host.Style.Set( "right", "0" );
		_host.Style.Set( "bottom", "0" );
		_host.Style.Set( "pointer-events", "none" );
		_hostShown = null;

		// Draw order = creation order: base, bow ring, lock ring on top.
		_base = new RingUi( _host );
		_bow = new RingUi( _host );
		_lock = new RingUi( _host );
		return true;
	}

	void DestroyPanels()
	{
		if ( _host is not null && _host.IsValid() )
			_host.Delete();
		_host = null;
		_base = null;
		_bow = null;
		_lock = null;
		_hostShown = null;
	}

	void SetHostShown( bool shown )
	{
		if ( _hostShown == shown )
			return;

		_hostShown = shown;
		_host.Style.Set( "display", shown ? "flex" : "none" );
	}

	void ApplyInnerLockRing()
	{
		Vector2? offset = null;
		if ( _movement.TryGetAimLockScreenPoint( out var assistPoint ) )
		{
			var cam = BuildViewCamera.Resolve( GameObject );
			if ( cam.IsValid() )
			{
				var rect = cam.ScreenRect;
				var center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );
				var delta = assistPoint - center;
				if ( delta.Length > InnerCenterSnapPixels )
				{
					// Screen pixels → panel units.
					var scale = MathF.Max( 0.001f, _host.ScaleToScreen );
					offset = delta / scale;
				}
			}
		}

		_lock.Apply( true, InnerRingRadius, InnerRingWidth, GrappleYellow, offset );
	}

	/// <summary>
	/// One bordered ring: a black circle-border panel with a coloured one on top, both centred on
	/// the screen (or offset from it). Styles are written only when a rounded value changes.
	/// </summary>
	sealed class RingUi
	{
		readonly Panel _black;
		readonly Panel _color;
		bool? _visible;
		float _radius = -1f;
		float _lineWidth = -1f;
		Color _tint;
		Vector2 _offset = new( float.NaN, float.NaN );

		public RingUi( Panel parent )
		{
			_black = MakeCircle( parent );
			_color = MakeCircle( parent );
		}

		static Panel MakeCircle( Panel parent )
		{
			var p = new Panel { Parent = parent };
			p.Style.Set( "position", "absolute" );
			p.Style.Set( "left", "50%" );
			p.Style.Set( "top", "50%" );
			p.Style.Set( "transform", "translate(-50%, -50%)" );
			p.Style.Set( "box-sizing", "border-box" );
			p.Style.Set( "background-color", "transparent" );
			p.Style.Set( "pointer-events", "none" );
			p.Style.Set( "display", "none" );
			return p;
		}

		public void Apply( bool visible, float radius, float lineWidth, Color tint, Vector2? offset )
		{
			if ( _visible != visible )
			{
				_visible = visible;
				_black.Style.Set( "display", visible ? "flex" : "none" );
				_color.Style.Set( "display", visible ? "flex" : "none" );
			}

			if ( !visible )
				return;

			radius = MathF.Round( radius * 4f ) * 0.25f;
			if ( MathF.Abs( radius - _radius ) > 1e-3f || MathF.Abs( lineWidth - _lineWidth ) > 1e-3f || tint != _tint )
			{
				_radius = radius;
				_lineWidth = lineWidth;
				_tint = tint;
				Shape( _black, radius, lineWidth + BorderWidth * 2f, BorderBlack );
				Shape( _color, radius, lineWidth, tint );
			}

			// Offset from screen centre (lock ring sliding to the aim-assist point): a margin on top of
			// the 50% anchor, rounded to half a panel pixel so a steady aim writes nothing.
			var off = offset ?? Vector2.Zero;
			off = new Vector2( MathF.Round( off.x * 2f ) * 0.5f, MathF.Round( off.y * 2f ) * 0.5f );
			if ( off != _offset )
			{
				_offset = off;
				_black.Style.Set( "margin-left", Px( off.x ) );
				_black.Style.Set( "margin-top", Px( off.y ) );
				_color.Style.Set( "margin-left", Px( off.x ) );
				_color.Style.Set( "margin-top", Px( off.y ) );
			}
		}

		static void Shape( Panel p, float radius, float lineWidth, Color color )
		{
			var size = 2f * (radius + lineWidth * 0.5f);
			p.Style.Width = Length.Pixels( size );
			p.Style.Height = Length.Pixels( size );
			p.Style.Set( "border-width", Px( lineWidth ) );
			p.Style.Set( "border-radius", Px( size ) );
			p.Style.Set( "border-color", Rgba( color ) );
		}

		static string Px( float v ) => v.ToString( "0.##", CultureInfo.InvariantCulture ) + "px";

		static string Rgba( Color c ) =>
			$"rgba({(int)MathF.Round( c.r * 255f )},{(int)MathF.Round( c.g * 255f )},{(int)MathF.Round( c.b * 255f )},{c.a.ToString( "0.###", CultureInfo.InvariantCulture )})";
	}

	// ── Attack teardrop (overlay lines, melee weapon out only) ───────────────────────────────

	/// <summary>Filled directional triangle off the base rim (attack teardrop), black-bordered.</summary>
	static void DrawArrow( CameraComponent cam, Vector2 center, Vector2 dir, float rimRadius )
	{
		var len = dir.Length;
		if ( len < 1e-5f )
			return;
		dir /= len;

		// Inflated black triangle behind, exact white triangle on top.
		DrawArrowFan( cam, center, dir, rimRadius - BorderWidth, ArrowTipLength + BorderWidth * 2f, ArrowHalfWidth + BorderWidth, BorderBlack );
		DrawArrowFan( cam, center, dir, rimRadius, ArrowTipLength, ArrowHalfWidth, CrosshairWhite );
	}

	static void DrawArrowFan( CameraComponent cam, Vector2 center, Vector2 dir, float rimRadius, float tipLength, float halfWidth, Color color )
	{
		var rim = center + dir * rimRadius;
		var perp = new Vector2( -dir.y, dir.x );
		var tipPos = center + dir * ( rimRadius + tipLength );
		var pLeft = rim + perp * halfWidth;
		var pRight = rim - perp * halfWidth;

		// 2 px lines across an 8 px base: 8 steps overlap fully; 48 was 6× the draws for the same pixels.
		const int fanSegments = 8;
		const float fanLineWidth = 2f;
		var hud = cam.Overlay;
		for ( var i = 0; i <= fanSegments; i++ )
		{
			var t = i / (float)fanSegments;
			var edge = Vector2.Lerp( pLeft, pRight, t );
			hud.DrawLine( tipPos, edge, fanLineWidth, color );
		}
	}
}
