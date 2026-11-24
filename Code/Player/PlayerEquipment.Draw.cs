using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Drawing an item (Mark 2026-11-12): anything put in a hand from the hotbar - weapon, shield, tool, food - takes
/// <see cref="DrawSeconds"/> (0.2 s since 2026-11-17, Mark: "make swords equip in .2s without any bar across the bottom?
/// Also ok to equip while running" - every hotbar item, no draw bar, sprinting no longer holds it back). The hand is empty
/// while it is drawn, so no attack, block, tool use or held model can start before it is out; every system that reads the
/// hand waits on its own. Putting an item away is instant, and re-selecting what is already in (or being drawn into) a
/// hand never restarts the draw. Owner-driven like the rest of the hotbar equip: the item reaches the hand (and the host)
/// only when the draw is done.
/// State is plain struct fields (a default <see cref="InventorySlot"/> is empty), so a hot-reloaded instance never
/// reads a null - field initialisers do not run on hotload.
/// </summary>
public sealed partial class PlayerEquipment
{
	[Property, Group( "Equip" ), Title( "Draw time (s)" ), Description( "Seconds from picking a hotbar item until it is in the hand and usable. 0 = instant." ), Range( 0f, 5f ), Step( 0.1f )]
	public float DrawSeconds { get; set; } = 0.2f;

	InventorySlot _drawMain, _drawOff;
	double _drawMainStartedAt, _drawOffStartedAt;

	/// <summary>True while an item is being drawn into either hand.</summary>
	public bool IsDrawing => !_drawMain.IsEmpty || !_drawOff.IsEmpty;

	static bool SameItem( InventorySlot a, InventorySlot b ) =>
		!a.IsEmpty && !b.IsEmpty && string.Equals( a.ResourceId, b.ResourceId, StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Owner: put <paramref name="stack"/> in <paramref name="hand"/>. Empty, the same item (a wear / count update) or
	/// <paramref name="instant"/> apply at once; a different item empties the hand and arrives after <see cref="DrawSeconds"/>.
	/// </summary>
	void OwnerDrawIntoHand( EquipmentSlot hand, InventorySlot stack, bool instant = false )
	{
		var off = hand == EquipmentSlot.OffHand;
		var pending = off ? _drawOff : _drawMain;
		var current = GetSlot( hand );

		if ( stack.IsEmpty || instant || DrawSeconds <= 0f || SameItem( current, stack ) )
		{
			SetDrawPending( off, InventorySlot.Empty, 0 );
			OwnerSetHandSlot( hand, stack );
			return;
		}

		if ( SameItem( pending, stack ) )
		{
			// Still drawing it: keep the timer, take the fresh wear / count.
			SetDrawPending( off, stack, off ? _drawOffStartedAt : _drawMainStartedAt );
			return;
		}

		SetDrawPending( off, stack, Time.NowDouble );
		if ( !current.IsEmpty )
			OwnerSetHandSlot( hand, InventorySlot.Empty );
	}

	void SetDrawPending( bool off, InventorySlot stack, double startedAt )
	{
		if ( off )
		{
			_drawOff = stack;
			_drawOffStartedAt = startedAt;
		}
		else
		{
			_drawMain = stack;
			_drawMainStartedAt = startedAt;
		}
	}

	/// <summary>Owner, every frame: finish draws whose time is up.</summary>
	void TickHandDraw()
	{
		if ( !IsDrawing )
			return;

		if ( !CanSyncFromHotbar() )
		{
			_drawMain = _drawOff = InventorySlot.Empty;
			return;
		}

		var seconds = Math.Max( 0f, DrawSeconds );
		if ( !_drawMain.IsEmpty && Time.NowDouble - _drawMainStartedAt >= seconds )
		{
			var stack = _drawMain;
			_drawMain = InventorySlot.Empty;
			OwnerSetHandSlot( EquipmentSlot.MainHand, stack );
		}

		if ( !_drawOff.IsEmpty && Time.NowDouble - _drawOffStartedAt >= seconds )
		{
			var stack = _drawOff;
			_drawOff = InventorySlot.Empty;
			OwnerSetHandSlot( EquipmentSlot.OffHand, stack );
		}
	}
}
