using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Unified screen-center crosshair — the single draw path replacing the old separate combat
/// teardrop and grapple reticle overlays. Composable layers, all state read from the owning
/// pawn's components:
///   base  — thin white ring; becomes a thick yellow ring while the grapple aim HUD is active
///           (<see cref="PlayerMovement.IsAimHudActive"/>: recently aimed/used or attached,
///           existing idle-hide timer)
///   arrow — directional attack teardrop when the active hotbar item has
///           <see cref="EquippedItemActions.PrimaryMelee"/> (build hammer shows the plain ring)
///   inner — small yellow lock ring when a grapple attach target is valid right now; slides
///           with aim assist to the actual attach point (also mid-grapple for re-targeting)
/// Hidden while any game menu is open.
/// </summary>
[Title( "Player Crosshair" )]
public sealed class PlayerCrosshair : Component
{
	[Property, Title( "Show Crosshair" )]
	public bool ShowCrosshair { get; set; } = true;

	const float BaseRadius = 7f;
	const float BaseLineWidth = 1.75f;
	const float GrappleRingRadius = 6f;
	const float GrappleRingWidth = 4.5f;
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

	PlayerMovement _movement;
	PlayerCombat _combat;
	PlayerEquipment _equipment;
	PlayerGameMenuController _menu;

	protected override void OnStart()
	{
		base.OnStart();
		// EverythingInSelf: PlayerEquipment disables PlayerCombat while hands are empty (spawn
		// state) — an enabled-only lookup here would cache null and the arrow would never draw.
		_movement = Components.Get<PlayerMovement>( FindMode.EverythingInSelf );
		_combat = Components.Get<PlayerCombat>( FindMode.EverythingInSelf );
		_equipment = Components.Get<PlayerEquipment>( FindMode.EverythingInSelf );
		_menu = Components.Get<PlayerGameMenuController>( FindMode.EverythingInSelf );
	}

	protected override void OnPreRender()
	{
		base.OnPreRender();

		if ( !ShowCrosshair || !IsLocalDriver() )
			return;

		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		var cam = BuildViewCamera.Resolve( GameObject );
		if ( !cam.IsValid() )
			return;

		var rect = cam.ScreenRect;
		var center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );

		var grappleMode = _movement?.IsAimHudActive == true;
		var weaponOut = _equipment?.MainHandHasAction( EquippedItemActions.PrimaryMelee ) == true;

		// Base: yellow donut in grapple context, thin white ring otherwise.
		float baseOuterEdge;
		if ( grappleMode )
		{
			DrawBorderedRing( cam, center, GrappleRingRadius, GrappleRingWidth, GrappleYellow );
			baseOuterEdge = GrappleRingRadius + GrappleRingWidth * 0.5f;
		}
		else
		{
			DrawBorderedRing( cam, center, BaseRadius, BaseLineWidth, CrosshairWhite );
			baseOuterEdge = BaseRadius + BaseLineWidth * 0.5f;
		}

		if ( weaponOut && _combat is not null )
			DrawArrow( cam, center, _combat.GetTeardropScreenDirection(), baseOuterEdge );

		if ( grappleMode && _movement.HasValidAimTarget )
			DrawInnerLockRing( cam, center );
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

	void DrawInnerLockRing( CameraComponent cam, Vector2 center )
	{
		var lockPos = center;
		if ( _movement.TryGetAimLockScreenPoint( out var assistPoint ) )
			lockPos = assistPoint;

		if ( (lockPos - center).Length <= InnerCenterSnapPixels )
			lockPos = center;

		DrawBorderedRing( cam, lockPos, InnerRingRadius, InnerRingWidth, GrappleYellow );
	}

	/// <summary>Black underlay stroke first, colored stroke on top — border on both edges.</summary>
	static void DrawBorderedRing( CameraComponent cam, Vector2 center, float radius, float lineWidth, Color color )
	{
		DrawRing( cam, center, radius, lineWidth + BorderWidth * 2f, BorderBlack );
		DrawRing( cam, center, radius, lineWidth, color );
	}

	static void DrawRing( CameraComponent cam, Vector2 center, float radius, float lineWidth, Color color )
	{
		const int segments = 40;
		var hud = cam.Overlay;
		var prev = center + new Vector2( radius, 0f );
		for ( var i = 1; i <= segments; i++ )
		{
			var a = i * ( MathF.PI * 2f / segments );
			var next = center + new Vector2( MathF.Cos( a ), MathF.Sin( a ) ) * radius;
			hud.DrawLine( prev, next, lineWidth, color );
			prev = next;
		}
	}

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

		const int fanSegments = 48;
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
