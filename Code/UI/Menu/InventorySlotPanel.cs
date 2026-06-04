using Sandbox.UI;

namespace Survival;

/// <summary>One inventory grid cell with pointer input forwarded to <see cref="PlayerInventoryInteraction"/>.</summary>
public sealed class InventorySlotPanel : Panel
{
	public int SlotIndex { get; }
	public PlayerInventoryInteraction Interaction { get; }

	public InventorySlotPanel( int slotIndex, PlayerInventoryInteraction interaction )
	{
		SlotIndex = slotIndex;
		Interaction = interaction;
	}

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		Interaction?.OnSlotMouseDown( this, e );
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		Interaction?.OnSlotMouseUp( this, e );
	}

	public override void Tick()
	{
		base.Tick();
		Interaction?.NotifyDropHover( SlotIndex, this );
	}
}
