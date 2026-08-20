using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Small line under the crosshair naming the snap the ghost is hanging from ("Auto", "Top Left", …)
/// and its Q/E position on the current seam. Visible only while a place-piece ghost is up.
/// </summary>
public sealed class BuildSnapReadoutHud
{
	const float LabelFontSize = 17f * BuildMenuHud.Scale;
	const float HintFontSize = 13f * BuildMenuHud.Scale;

	readonly PlayerEquipment _equipment;

	Panel _root;
	Label _label;
	Label _hint;
	string _lastText;

	public BuildSnapReadoutHud( PlayerEquipment equipment ) => _equipment = equipment;

	public void Build( Panel root )
	{
		_root = new Panel { Parent = root };
		_root.Style.Set( "position", "absolute" );
		_root.Style.Set( "left", "50%" );
		_root.Style.Set( "top", "54%" );
		_root.Style.Set( "transform", "translateX(-50%)" );
		_root.Style.Set( "flex-direction", "column" );
		_root.Style.Set( "align-items", "center" );
		_root.Style.Set( "pointer-events", "none" );
		_root.Style.Set( "display", "none" );

		_label = new Label { Parent = _root };
		_label.Style.FontSize = Length.Pixels( LabelFontSize );
		_label.Style.Set( "font-weight", "bold" );
		_label.Style.Set( "color", "rgba(255,236,180,0.95)" );
		_label.Style.Set( "text-shadow", "0px 1px 3px rgba(0,0,0,0.9)" );

		_hint = new Label { Parent = _root };
		_hint.Text = "Q / E  cycle snap";
		_hint.Style.FontSize = Length.Pixels( HintFontSize );
		_hint.Style.Set( "color", "rgba(255,255,255,0.55)" );
		_hint.Style.Set( "text-shadow", "0px 1px 3px rgba(0,0,0,0.9)" );
	}

	public void Tick()
	{
		if ( _root is null )
			return;

		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		var show = hammer is not null && hammer.IsPreviewingPlacePiece;
		_root.Style.Set( "display", show ? "flex" : "none" );
		if ( !show )
			return;

		var text = hammer.IsSnappedToStructure
			? BuildVariantText( hammer )
			: "Free placement";

		if ( text == _lastText )
			return;

		_lastText = text;
		_label.Text = text;
	}

	static string BuildVariantText( ToolBuildHammer hammer )
	{
		var label = hammer.SnapVariantLabel;
		var count = hammer.SnapVariantCount;
		return count > 1
			? $"Snap: {label}   ({hammer.SnapVariantNumber}/{count})"
			: $"Snap: {label}";
	}
}
