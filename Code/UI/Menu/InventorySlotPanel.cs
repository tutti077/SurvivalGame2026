using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>One inventory grid cell with pointer input forwarded to <see cref="PlayerInventoryInteraction"/>.</summary>
public sealed class InventorySlotPanel : Panel
{
	public int SlotIndex { get; }
	public IInventoryGridHost GridHost { get; }
	public PlayerInventoryInteraction Interaction { get; }

	public InventorySlotPanel( int slotIndex, IInventoryGridHost gridHost, PlayerInventoryInteraction interaction )
	{
		SlotIndex = slotIndex;
		GridHost = gridHost;
		Interaction = interaction;
		ButtonInput = PanelInputType.UI;
	}

	/// <summary>Never claim UI mouse mode — that disables <see cref="PlayerController.UseLookControls"/>.</summary>
	public override bool WantsMouseInput() => false;

	public bool IsHotbarSlot => GridHost?.GridId == "hotbar";

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( IsSecondaryMouseButton( e.Button ) )
		{
			e.StopPropagation();
			return;
		}

		e.StopPropagation();
		Interaction?.ProcessSlotPress( this, e.Button, pressed: true );
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( IsSecondaryMouseButton( e.Button ) )
		{
			e.StopPropagation();
			return;
		}

		e.StopPropagation();
		Interaction?.ProcessSlotPress( this, e.Button, pressed: false );
	}

	public override void OnButtonEvent( ButtonEvent e )
	{
		base.OnButtonEvent( e );
		if ( !IsHotbarSlot || Interaction is null )
			return;

		if ( IsSecondaryMouseButton( e.Button ) )
		{
			if ( e.Pressed )
				return;

			e.StopPropagation = true;
			Interaction.ProcessSlotRightClick( this );
			return;
		}

		if ( e.Button is not ( "mouseleft" or "mouse1" or "Attack1" ) )
			return;

		e.StopPropagation = true;
		Interaction.ProcessSlotPress( this, e.Button, e.Pressed );
	}

	public override void Tick()
	{
		base.Tick();
		Interaction?.NotifyDropHover( this );
	}

	static bool IsSecondaryMouseButton( string button ) =>
		string.Equals( button, "mouseright", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse2", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack2", StringComparison.OrdinalIgnoreCase );
}
