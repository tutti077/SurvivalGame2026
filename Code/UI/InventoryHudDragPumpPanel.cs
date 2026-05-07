using Sandbox.UI;

namespace Game;

/// <summary>
/// UI-frame pump: ghost is <see cref="Mouse.Position"/> only (no delta integration), safe to run here + in <see cref="PanelComponent.OnUpdate"/>.
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
		Style.Width = Length.Fraction( 1f );
		Style.Height = Length.Fraction( 1f );
		Style.Position = PositionMode.Absolute;
		Style.Left = Length.Pixels( 0f );
		Style.Top = Length.Pixels( 0f );
	}

	public override void Tick()
	{
		base.Tick();
		Host?.PumpDragPresentationUiTick();
	}
}
