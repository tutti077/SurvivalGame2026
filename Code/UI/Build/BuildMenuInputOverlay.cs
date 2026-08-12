using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Mouse capture layer while the build piece picker is open.</summary>
public sealed class BuildMenuInputOverlay : Panel
{
	PlayerEquipment _equipment;
	PlayerController _controller;
	bool _isOpen;
	bool _savedUseLookControls = true;

	public void Bind( PlayerEquipment equipment ) => _equipment = equipment;

	public override bool WantsMouseInput() => _isOpen;

	public Action TickWhileOpen;

	public void SetOpen( bool open )
	{
		var wasOpen = _isOpen;
		_isOpen = open;
		Style.Set( "display", open ? "flex" : "none" );
		Style.Set( "pointer-events", open ? "all" : "none" );
		AcceptsFocus = false;

		if ( open && !wasOpen )
			ApplyUiCapture();
		else if ( !open && wasOpen )
			RestoreGameplayPointer();
	}

	void ApplyUiCapture()
	{
		ResolveController();
		if ( _controller is not null )
		{
			_savedUseLookControls = _controller.UseLookControls;
			_controller.UseLookControls = false;
		}

		Mouse.Visibility = MouseVisibility.Auto;

		var pawn = ResolvePawn();
		if ( pawn is not null && pawn.IsValid() )
			InventoryScreenPointer.ClampMouseToView( pawn );
	}

	void RestoreGameplayPointer()
	{
		var pawn = ResolvePawn();
		var menu = pawn?.Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
			return;

		ResolveController();
		if ( _controller is not null )
			_controller.UseLookControls = _savedUseLookControls;

		Mouse.Visibility = MouseVisibility.Hidden;
	}

	void ResolveController()
	{
		if ( _controller is not null && _controller.IsValid() )
			return;

		_controller = ResolvePawn()?.Components.Get<PlayerController>();
	}

	GameObject ResolvePawn() => _equipment?.GameObject;

	ToolBuildHammer ResolveBuildHammer() => _equipment?.GetActiveTool<ToolBuildHammer>();

	public override void Tick()
	{
		base.Tick();
		if ( !_isOpen || _equipment is null || !_equipment.GameObject.IsValid() )
			return;

		ResolveController();
		if ( _controller is not null )
			_controller.UseLookControls = false;

		if ( Mouse.Visibility != MouseVisibility.Auto )
			Mouse.Visibility = MouseVisibility.Auto;

		var pawn = ResolvePawn();
		if ( pawn is not null && pawn.IsValid() )
			InventoryScreenPointer.ClampMouseToView( pawn );

		TickWhileOpen?.Invoke();
	}

	protected override void OnEscape( PanelEvent e )
	{
		if ( !_isOpen )
			return;

		Input.EscapePressed = false;
		ResolveBuildHammer()?.SetBuildMenuOpen( false );
	}

	protected override void OnRightClick( MousePanelEvent e )
	{
		base.OnRightClick( e );
		if ( !_isOpen )
			return;

		e.StopPropagation();
		ResolveBuildHammer()?.SetBuildMenuOpen( false );
	}
}
