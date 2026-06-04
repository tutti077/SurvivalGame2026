using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Local pawn: toggles the inventory menu via <see cref="ToggleMenuAction"/> (default <c>InventoryMenu</c> / Tab).
/// </summary>
[Title( "Player Game Menu Controller" )]
public sealed class PlayerGameMenuController : Component, PlayerController.IEvents
{
	[Property, Group( "Input" )]
	public string ToggleMenuAction { get; set; } = "InventoryMenu";

	public bool IsMenuOpen { get; private set; }

	public event Action<bool> MenuOpenChanged;

	PlayerVitals _vitals;
	PlayerController _controller;
	bool _savedUseLookControls = true;
	MouseVisibility _savedMouseVisibility = MouseVisibility.Auto;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		ResolveController();
		EnsureGameplayLookControls();
	}

	protected override void OnDisabled()
	{
		EnsureGameplayLookControls();
		base.OnDisabled();
	}

	public void PreInput()
	{
		if ( !IsMenuOpen || !IsLocalInputOwnedPawn() )
			return;

		ResolveController();
		if ( _controller is not null )
			_controller.UseLookControls = false;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();

		if ( !IsLocalInputOwnedPawn() )
			return;

		if ( IsMenuOpen && Input.EscapePressed )
		{
			SetMenuOpen( false );
			return;
		}

		if ( !WasInventoryTogglePressed() )
			return;

		SetMenuOpen( !IsMenuOpen );
	}

	public void SetMenuOpen( bool open )
	{
		if ( IsMenuOpen == open )
			return;

		ResolveController();

		if ( _controller is not null )
		{
			if ( open )
			{
				_savedUseLookControls = _controller.UseLookControls;
				_controller.UseLookControls = false;
			}
			else
			{
				_controller.UseLookControls = _savedUseLookControls;
			}
		}

		if ( open )
		{
			_savedMouseVisibility = Mouse.Visibility;
			Mouse.Visibility = MouseVisibility.Auto;
			InventoryScreenPointer.CenterMouseOnCrosshair( GameObject );
			InventoryScreenPointer.ClampMouseToView( GameObject );
		}
		else
		{
			Mouse.Visibility = _savedMouseVisibility;
		}

		IsMenuOpen = open;
		MenuOpenChanged?.Invoke( IsMenuOpen );

		if ( !open )
			EnsureGameplayLookControls();
	}

	bool WasInventoryTogglePressed()
	{
		if ( !string.IsNullOrWhiteSpace( ToggleMenuAction ) && Input.Pressed( ToggleMenuAction ) )
			return true;

		// Prefab/scene may still use Score while Input.config maps Tab → InventoryMenu.
		if ( !string.Equals( ToggleMenuAction, "Score", StringComparison.OrdinalIgnoreCase ) && Input.Pressed( "Score" ) )
			return true;

		if ( !string.Equals( ToggleMenuAction, "InventoryMenu", StringComparison.OrdinalIgnoreCase ) && Input.Pressed( "InventoryMenu" ) )
			return true;

		return false;
	}

	void EnsureGameplayLookControls()
	{
		if ( IsMenuOpen )
			return;

		ResolveController();
		if ( _controller is null )
			return;

		_controller.UseLookControls = true;
		_savedUseLookControls = true;
	}

	void ResolveController()
	{
		if ( _controller is not null && _controller.IsValid() )
			return;

		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
			{
				_controller = pc;
				return;
			}
		}
	}

	bool IsLocalInputOwnedPawn()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
	}
}
