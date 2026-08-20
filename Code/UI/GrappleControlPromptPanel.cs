using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>First-time hook equip: pick Pro (E/Q) or Training Wheels (Space/Ctrl).</summary>
public sealed class GrappleControlPromptPanel : Panel
{
	bool _open;
	bool _built;

	public GrappleControlPromptPanel()
	{
		Style.Set( "position", "absolute" );
		Style.Set( "left", "0" );
		Style.Set( "top", "0" );
		Style.Set( "right", "0" );
		Style.Set( "bottom", "0" );
		Style.Set( "align-items", "center" );
		Style.Set( "justify-content", "center" );
		Style.Set( "pointer-events", "all" );
		Style.Set( "display", "none" );
		Style.Set( "z-index", "20000" );
		Style.BackgroundColor = new Color( 0f, 0f, 0f, 0.55f );
		AcceptsFocus = false;
	}

	public override bool WantsMouseInput() => _open;

	public void SetOpen( bool open )
	{
		if ( _open == open )
			return;

		_open = open;
		Style.Set( "display", open ? "flex" : "none" );
		if ( open )
			EnsureBuilt();
	}

	void EnsureBuilt()
	{
		if ( _built )
			return;

		_built = true;

		var card = new Panel { Parent = this };
		card.Style.Width = Length.Pixels( 520f );
		card.Style.Set( "flex-direction", "column" );
		card.Style.Set( "align-items", "stretch" );
		card.Style.Set( "gap", "16px" );
		card.Style.PaddingLeft = Length.Pixels( 22f );
		card.Style.PaddingRight = Length.Pixels( 22f );
		card.Style.PaddingTop = Length.Pixels( 20f );
		card.Style.PaddingBottom = Length.Pixels( 20f );
		card.Style.BackgroundColor = new Color( 0.07f, 0.08f, 0.1f, 0.96f );
		card.Style.Set( "border-radius", "8px" );
		card.Style.Set( "border-width", "1px" );
		card.Style.Set( "border-color", "#4a5564" );

		var title = new Label { Parent = card, Text = "Grapple controls" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( 22f );
		title.Style.Set( "text-align", "left" );

		var body = new Label
		{
			Parent = card,
			Text = "User reports of difficult grappling have constituted a \"Training wheels\" mode for the controls. Please select below which control method you'd like.",
		};
		body.Style.FontColor = new Color( 0.86f, 0.88f, 0.9f );
		body.Style.FontSize = Length.Pixels( 15f );
		body.Style.Set( "white-space", "normal" );
		body.Style.Set( "text-align", "left" );
		body.Style.Set( "padding-right", "28px" );

		var row = new Panel { Parent = card };
		row.Style.Set( "flex-direction", "row" );
		row.Style.Set( "justify-content", "center" );
		row.Style.Set( "gap", "12px" );
		row.Style.Set( "margin-top", "6px" );

		AddChoiceButton( row, "Pro", "E retract  ·  Q detract", GrappleControlScheme.Pro );
		AddChoiceButton( row, "Training Wheels", "Space retract  ·  Ctrl detract", GrappleControlScheme.TrainingWheels );
	}

	void AddChoiceButton( Panel parent, string label, string hint, GrappleControlScheme scheme )
	{
		var col = new Panel { Parent = parent };
		col.Style.Set( "flex-direction", "column" );
		col.Style.Set( "align-items", "center" );
		col.Style.Set( "gap", "6px" );

		var btn = new GrappleControlChoiceButton { Parent = col };
		btn.Style.Width = Length.Pixels( 210f );
		btn.Style.Height = Length.Pixels( 44f );
		btn.Style.Set( "align-items", "center" );
		btn.Style.Set( "justify-content", "center" );
		btn.Style.BackgroundColor = new Color( 0.18f, 0.24f, 0.34f );
		btn.Style.Set( "border-radius", "6px" );
		btn.Style.Set( "cursor", "pointer" );
		btn.Clicked = () => GrappleControlSchemeStore.Set( scheme );

		var text = new Label { Parent = btn, Text = label };
		text.Style.FontColor = Color.White;
		text.Style.FontSize = Length.Pixels( 16f );
		text.Style.Set( "pointer-events", "none" );

		var hintLabel = new Label { Parent = col, Text = hint };
		hintLabel.Style.FontColor = new Color( 0.7f, 0.73f, 0.76f );
		hintLabel.Style.FontSize = Length.Pixels( 12f );
		hintLabel.Style.Set( "pointer-events", "none" );
	}
}

sealed class GrappleControlChoiceButton : Panel
{
	public Action Clicked;

	public override bool WantsMouseInput() => true;

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button is "mouseleft" or "mouse1" or "Attack1" )
		{
			Clicked?.Invoke();
			e.StopPropagation();
		}
	}
}
