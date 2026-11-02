using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Number row 1-0, mouse wheel and mouse4 / mouse5 select hotbar slots in the bank on screen; Alt swaps the
/// bank (Mark 2026-11-02: numbers and the wheel pick items, augments moved to Z X C V G T). A selected shield
/// equips the offhand and keeps the primary, anything else is the primary; a number key on a slot that is
/// already selected unequips it (<see cref="PlayerHotbar.SelectOrToggleSlot"/>).
/// Camera zoom uses +/- keys — not the wheel.
/// </summary>
[Title( "Player Hotbar Controller" )]
public sealed class PlayerHotbarController : Component
{
	static readonly string[] SlotActions = { "Slot1", "Slot2", "Slot3", "Slot4", "Slot5", "Slot6", "Slot7", "Slot8", "Slot9", "Slot0" };

	[Property, Group( "Input" )] public string BankToggleAction { get; set; } = "HotbarBank";
	[Property, Group( "Input" )] public string SlotPrevAction { get; set; } = "SlotPrev";
	[Property, Group( "Input" )] public string SlotNextAction { get; set; } = "SlotNext";

	PlayerVitals _vitals;
	PlayerHotbar _hotbar;
	PlayerEquipment _equipment;
	PlayerGameMenuController _menu;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_hotbar = Components.Get<PlayerHotbar>();
		_equipment = Components.Get<PlayerEquipment>();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !CanControl() || _hotbar is null )
			return;

		PollMouseWheel();
		PollKeys();
	}

	/// <summary>Number row + mouse4/5 + Alt. Keys work with the menu open too (not while typing a pin name).</summary>
	void PollKeys()
	{
		_menu ??= Components.Get<PlayerGameMenuController>();
		if ( _menu is { IsTextCaptureActive: true } )
			return;

		if ( !string.IsNullOrWhiteSpace( BankToggleAction ) && Input.Pressed( BankToggleAction ) )
			_hotbar.ToggleBank();

		for ( var i = 0; i < SlotActions.Length && i < PlayerHotbar.BankSize; i++ )
		{
			if ( !Input.Pressed( SlotActions[i] ) )
				continue;

			ExitBuildModeForHotbarSwap();
			_hotbar.SelectOrToggleSlot( _hotbar.ActiveBank * PlayerHotbar.BankSize + i );
			_equipment?.SyncEquipFromActiveHotbar();
			return;
		}

		if ( !string.IsNullOrWhiteSpace( SlotPrevAction ) && Input.Pressed( SlotPrevAction ) )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( -1 );
			_equipment?.SyncEquipFromActiveHotbar();
		}
		else if ( !string.IsNullOrWhiteSpace( SlotNextAction ) && Input.Pressed( SlotNextAction ) )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( 1 );
			_equipment?.SyncEquipFromActiveHotbar();
		}
	}

	bool CanControl()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();
		if ( _hotbar is null )
			_hotbar = Components.Get<PlayerHotbar>();
		if ( _equipment is null )
			_equipment = Components.Get<PlayerEquipment>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn()
		       && _hotbar is not null && _hotbar.IsLocalManagingClient();
	}


	void PollMouseWheel()
	{
		if ( Components.Get<PlayerGameMenuController>() is { IsMenuOpen: true } )
			return;

		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		if ( hammer is not null && hammer.IsPreviewingPlacePiece )
			return;

		var scroll = Input.MouseWheel.y;
		if ( scroll > 0.01f )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( -1 );
			_equipment?.SyncEquipFromActiveHotbar();
		}
		else if ( scroll < -0.01f )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( 1 );
			_equipment?.SyncEquipFromActiveHotbar();
		}
	}

	void ExitBuildModeForHotbarSwap()
	{
		_equipment?.GetActiveTool<ToolHoe>()?.SetMenuOpen( false );
		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		if ( hammer is null )
			return;

		hammer.SetBuildMenuOpen( false );
		hammer.ClearSelectedPiece();
	}
}
