using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Number keys select hotbar slots (including while the game menu is open).
/// Mouse wheel is not used for hotbar — it drives camera zoom / build rotate / menu scroll instead.
/// </summary>
[Title( "Player Hotbar Controller" )]
public sealed class PlayerHotbarController : Component
{
	static readonly string[] SlotActions =
	{
		"Slot1", "Slot2", "Slot3", "Slot4", "Slot5",
		"Slot6", "Slot7", "Slot8", "Slot9", "Slot0"
	};

	PlayerVitals _vitals;
	PlayerHotbar _hotbar;
	PlayerEquipment _equipment;

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

		PollSlotKeys();
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

	void PollSlotKeys()
	{
		for ( var i = 0; i < SlotActions.Length; i++ )
		{
			if ( !Input.Pressed( SlotActions[i] ) )
				continue;

			ExitBuildModeForHotbarSwap();
			_hotbar.SetActiveSlot( i );
			_equipment?.SyncEquipFromActiveHotbar();
			return;
		}
	}

	void ExitBuildModeForHotbarSwap()
	{
		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		if ( hammer is null )
			return;

		hammer.SetBuildMenuOpen( false );
		hammer.ClearSelectedPiece();
	}
}
