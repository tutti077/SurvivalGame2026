using Sandbox.UI;

namespace Game;

/// <summary>
/// Sandbox runs <see cref="Panel.Tick"/> on UI frames; gameplay <see cref="Sandbox.Component.OnUpdate"/> and
/// <see cref="Sandbox.UI.Panel.InvokeOnce"/> scheduling are often clamped to the sim/update rate — drag-follow would stutter.
/// </summary>
public sealed class InventoryHudDragPumpPanel : Panel
{
	[Property]
	public PlayerInventoryHud Host { get; set; }

	public InventoryHudDragPumpPanel()
	{
		AddClass( "inv-drag-pump" );
		Style.PointerEvents = PointerEvents.None;
		Style.Opacity = 0f;
		Style.Width = Length.Pixels( 1f );
		Style.Height = Length.Pixels( 1f );
		Style.Position = PositionMode.Absolute;
		Style.Left = Length.Pixels( 0f );
		Style.Top = Length.Pixels( 0f );
	}

	public override void Tick()
	{
		base.Tick();

		if ( Host is null || !Host.IsDragPresentationPumpActive )
			return;

		// Slot hover/source classes only — drag PNG follows pointer events, not this tick.
		Host.OnDragPresentationPumpTick();
	}
}
