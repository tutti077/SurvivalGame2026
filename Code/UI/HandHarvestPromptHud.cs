using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Center-screen "[F] Harvest" prompt when <see cref="PlayerHandHarvest"/> has a valid hand-harvest target.</summary>
[Title( "Hand Harvest Prompt HUD" )]
public sealed class HandHarvestPromptHud : PanelComponent
{
	const string DefaultPromptText = "Harvest";

	PlayerHandHarvest _handHarvest;
	Label _promptLabel;
	Panel _promptRoot;
	bool _built;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		TryBuildPrompt();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !_built )
			TryBuildPrompt();

		if ( _promptRoot is null )
			return;

		if ( _handHarvest is null || !_handHarvest.IsValid() )
			_handHarvest = FindHandHarvest();

		var show = _handHarvest is not null && _handHarvest.FocusedNode is not null;
		_promptRoot.Style.Set( "display", show ? "flex" : "none" );

		if ( !show || _promptLabel is null )
			return;

		_promptLabel.Text = DefaultPromptText;
	}

	void TryBuildPrompt()
	{
		if ( _built )
			return;

		var vitals = FindVitals();
		if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
			return;

		_handHarvest = FindHandHarvest();
		if ( _handHarvest is null )
		{
			Log.Warning( $"[HandHarvestPromptHud] {GameObject.Name}: no PlayerHandHarvest — prompt hidden." );
			_built = true;
			Panel.Style.Set( "display", "none" );
			return;
		}

		Panel.Style.Set( "position", "absolute" );
		Panel.Style.Set( "left", "50%" );
		Panel.Style.Set( "top", "58%" );
		Panel.Style.Set( "transform", "translate(-50%, -50%)" );
		Panel.Style.Set( "pointer-events", "none" );
		Panel.Style.Set( "display", "flex" );

		_promptRoot = new Panel { Parent = Panel };
		_promptRoot.Style.Set( "flex-direction", "row" );
		_promptRoot.Style.Set( "align-items", "center" );
		_promptRoot.Style.Set( "justify-content", "center" );
		_promptRoot.Style.Set( "gap", "10px" );
		_promptRoot.Style.PaddingLeft = Length.Pixels( 14f );
		_promptRoot.Style.PaddingRight = Length.Pixels( 14f );
		_promptRoot.Style.PaddingTop = Length.Pixels( 8f );
		_promptRoot.Style.PaddingBottom = Length.Pixels( 8f );
		_promptRoot.Style.BackgroundColor = new Color( 0.06f, 0.06f, 0.07f, 0.82f );
		_promptRoot.Style.Set( "border-radius", "6px" );
		_promptRoot.Style.Set( "display", "none" );

		var keyCap = new Panel { Parent = _promptRoot };
		keyCap.Style.MinWidth = Length.Pixels( 28f );
		keyCap.Style.Height = Length.Pixels( 28f );
		keyCap.Style.Set( "align-items", "center" );
		keyCap.Style.Set( "justify-content", "center" );
		keyCap.Style.BackgroundColor = new Color( 0.92f, 0.92f, 0.94f );
		keyCap.Style.Set( "border-radius", "4px" );

		var keyLabel = new Label { Parent = keyCap, Text = "F" };
		keyLabel.Style.FontColor = Color.Black;
		keyLabel.Style.FontSize = Length.Pixels( 15f );

		_promptLabel = new Label { Parent = _promptRoot, Text = DefaultPromptText };
		_promptLabel.Style.FontColor = Color.White;
		_promptLabel.Style.FontSize = Length.Pixels( 18f );

		_built = true;
	}

	PlayerVitals FindVitals()
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var v = go.Components.Get<PlayerVitals>();
			if ( v is not null )
				return v;
		}

		return null;
	}

	PlayerHandHarvest FindHandHarvest()
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var h = go.Components.Get<PlayerHandHarvest>();
			if ( h is not null )
				return h;
		}

		return null;
	}
}
