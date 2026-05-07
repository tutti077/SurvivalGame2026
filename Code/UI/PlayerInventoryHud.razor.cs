namespace Game;

partial class PlayerInventoryHud
{
	// Stub: some Razor toolchains still emit references to this symbol; keep it assigned but unread.
#pragma warning disable CS0169, CS0414
	private int _interactionSlot = -1;
#pragma warning restore CS0169, CS0414

	private string InvBackpackSlotClass( int idx )
		=> $"inv-slot sidx-{idx}";

	private string InvHotbarSlotClass( int idx )
	{
		var s = $"inv-slot sidx-{idx}";
		if ( Inventory is not null && Inventory.HotbarSelectedIndex == idx )
			s += " inv-slot-equipped";
		return s;
	}

	// Back-compat: stale generated Razor files may still reference this symbol.
	private void SyncDragSlotDecorationClassesFromPointerCore( int resolvedHoverIdx )
		=> ApplyDragDecorations( sourceIdx: _dragFrom, hoverIdx: resolvedHoverIdx );
}
