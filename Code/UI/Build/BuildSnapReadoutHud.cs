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
		UiStyle.Text( _label, UiText.Hud, LabelFontSize );
		_label.Style.FontColor = UiTheme.Colors.AccentBrightColor;

		_hint = new Label { Parent = _root };
		_hint.Text = "Q / E  cycle snap";
		UiStyle.Text( _hint, UiText.Hud, HintFontSize );
		_hint.Style.FontColor = UiTheme.Colors.TextSecondaryColor;
	}

	bool? _shown;

	public void Tick()
	{
		if ( _root is null )
			return;

		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		var show = hammer is not null && hammer.IsPreviewingPlacePiece;
		if ( _shown != show )
		{
			_shown = show;
			_root.Style.Set( "display", show ? "flex" : "none" );
		}

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
