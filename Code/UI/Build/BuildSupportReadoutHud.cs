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
		UiStyle.Text( _label, UiText.Hud, LabelFontSize );
	}

	bool? _shown;

	public void Tick()
	{
		if ( _root is null )
			return;

		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		var show = hammer is not null && hammer.HasHoverSupport;
		if ( _shown != show )
		{
			_shown = show;
			_root.Style.Set( "display", show ? "flex" : "none" );
		}

		if ( !show )
			return;

		var text = $"Support {hammer.HoverSupportPercent}%  ({hammer.HoverSupportValue:0.#} / {hammer.HoverSupportMax:0})";
		if ( hammer.HoverHealthMax > 0f )
			text += $"   HP {hammer.HoverHealthValue:0} / {hammer.HoverHealthMax:0}";
		if ( text == _lastText )
			return;

		_lastText = text;
		_label.Text = text;
		// The support gradient is data (the same tint the piece gets), not theme chrome.
		_label.Style.FontColor = hammer.HoverSupportColor.WithAlpha( 0.95f );
	}
}
