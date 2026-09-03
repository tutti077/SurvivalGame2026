using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Structural-support readout under the crosshair while the build hammer is out and aimed at a
/// placed piece: "Support 74%", colored with the same gradient the piece is tinted with. The
/// number exists so the feedback is not color-only (colorblind players).
/// </summary>
public sealed class BuildSupportReadoutHud
{
	const float LabelFontSize = 17f * BuildMenuHud.Scale;

	readonly PlayerEquipment _equipment;

	Panel _root;
	Label _label;
	string _lastText;

	public BuildSupportReadoutHud( PlayerEquipment equipment ) => _equipment = equipment;

	public void Build( Panel root )
	{
		_root = new Panel { Parent = root };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "50%" );
		_root.Style.Set( "top", "58%" );
		_root.Style.Set( "transform", "translateX(-50%)" );
		_root.Style.Set( "flex-direction", "column" );
		_root.Style.Set( "align-items", "center" );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		_label = new Label { Parent = _root };
		_label.Style.FontSize = Length.Pixels( LabelFontSize );
		_label.Style.Set( "font-weight", "bold" );
		_label.Style.Set( "text-shadow", "0px 1px 3px rgba(0,0,0,0.9)" );
	}

	public void Tick()
	{
		if ( _root is null )
			return;

		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		var show = hammer is not null && hammer.HasHoverSupport;
		_root.Style.Set( "display", show ? "flex" : "none" );
		if ( !show )
			return;

		var text = $"Support {hammer.HoverSupportPercent}%  ({hammer.HoverSupportValue:0.#} / {hammer.HoverSupportMax:0})";
		if ( text == _lastText )
			return;

		_lastText = text;
		_label.Text = text;
		var color = hammer.HoverSupportColor;
		_label.Style.Set(
			"color",
			$"rgba({(int)(color.r * 255)},{(int)(color.g * 255)},{(int)(color.b * 255)},0.95)" );
	}
}
