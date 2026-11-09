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
		UiStyle.Backdrop( this );
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
		UiStyle.Surface( card, UiSurface.Frame );

		UiStyle.AddTitle( card, "Grapple controls", 22f, centered: false );

		var body = new Label
		{
			Parent = card,
			Text = "User reports of difficult grappling have constituted a \"Training wheels\" mode for the controls. Please select below which control method you'd like.",
		};
		UiStyle.Text( body, UiText.Body, 15f );
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

		// Two equal choices: neither is the page's primary action.
		var btn = new GrappleControlChoiceButton { Parent = col };
		btn.Style.Width = Length.Pixels( 210f );
		btn.Style.Height = Length.Pixels( 44f );
		UiStyle.Button( btn, UiButton.Default );
		btn.Clicked = () => GrappleControlSchemeStore.Set( scheme );

		var text = new Label { Parent = btn, Text = label };
		UiStyle.ButtonLabel( text, UiButton.Default, 16f );

		var hintLabel = new Label { Parent = col, Text = hint };
		UiStyle.Text( hintLabel, UiText.Muted, 12f );
		hintLabel.Style.Set( "pointer-events", "none" );
	}
}

sealed class GrappleControlChoiceButton : Panel
{
	public Action Clicked;

	public override bool WantsMouseInput() => true;

	protected override void OnMouseOver( MousePanelEvent e )
	{
		base.OnMouseOver( e );
		UiStyle.SetButtonState( this, UiButton.Default, hovered: true, pressed: false, disabled: false );
	}

	protected override void OnMouseOut( MousePanelEvent e )
	{
		base.OnMouseOut( e );
		UiStyle.SetButtonState( this, UiButton.Default, hovered: false, pressed: false, disabled: false );
	}

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
