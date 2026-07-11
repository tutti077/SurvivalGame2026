using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Drop target shown under the bag while the cursor holds a real item stack.</summary>
public sealed class InventoryPlayerDropZonePanel : Panel
{
	public PlayerInventoryInteraction Interaction { get; }

	public bool IsDisplayed { get; private set; }

	public InventoryPlayerDropZonePanel( PlayerInventoryInteraction interaction )
	{
		Interaction = interaction;
		ButtonInput = PanelInputType.UI;
	}

	public override bool WantsMouseInput() => false;

	public void SetDisplayed( bool displayed )
	{
		if ( IsDisplayed == displayed )
			return;

		IsDisplayed = displayed;
		Style.Set( "display", displayed ? "flex" : "none" );
	}

	public void SetHighlighted( bool highlighted )
	{
		Style.Set( "border-color", highlighted ? "#c9a227" : "#5a4a32" );
		Style.Set( "background-color", highlighted ? "rgba(42,32,18,0.92)" : "rgba(24,20,14,0.88)" );
	}

	public override void Tick()
	{
		base.Tick();
		Interaction?.NotifyPlayerDropZoneHover( this );
	}

	protected override void OnMouseUp( MousePanelEvent e )
	{
		base.OnMouseUp( e );
		if ( !IsPrimaryMouseButton( e.Button ) )
			return;

		e.StopPropagation();
		Interaction?.TryReleaseOnPlayerDropZone();
	}

	protected override void OnRightClick( MousePanelEvent e )
	{
		e.StopPropagation();
		Interaction?.TryReleaseOneOnPlayerDropZone();
	}

	public override void OnButtonEvent( ButtonEvent e )
	{
		base.OnButtonEvent( e );
		if ( e.Pressed )
			return;

		if ( IsPrimaryMouseButton( e.Button ) )
		{
			e.StopPropagation = true;
			Interaction?.TryReleaseOnPlayerDropZone();
			return;
		}

		if ( !IsSecondaryMouseButton( e.Button ) )
			return;

		e.StopPropagation = true;
		Interaction?.TryReleaseOneOnPlayerDropZone();
	}

	static bool IsSecondaryMouseButton( string button ) =>
		string.Equals( button, "mouseright", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse2", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack2", StringComparison.OrdinalIgnoreCase );

	static bool IsPrimaryMouseButton( string button ) =>
		string.Equals( button, "mouseleft", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "mouse1", StringComparison.OrdinalIgnoreCase )
		|| string.Equals( button, "Attack1", StringComparison.OrdinalIgnoreCase );
}
