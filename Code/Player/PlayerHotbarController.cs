using Sandbox;

namespace Survival;

/// <summary>Local hotbar selection: number keys and mouse wheel (when the game menu is closed).</summary>
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
	PlayerGameMenuController _menu;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_hotbar = Components.Get<PlayerHotbar>();
		_menu = Components.Get<PlayerGameMenuController>();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !CanControl() || _hotbar is null )
			return;

		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		PollSlotKeys();
		PollMouseWheel();
	}

	bool CanControl()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();
		if ( _hotbar is null )
			_hotbar = Components.Get<PlayerHotbar>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn()
		       && _hotbar is not null && _hotbar.IsLocalManagingClient();
	}

	void PollSlotKeys()
	{
		for ( var i = 0; i < SlotActions.Length; i++ )
		{
			if ( !Input.Pressed( SlotActions[i] ) )
				continue;

			_hotbar.SetActiveSlot( i );
			return;
		}
	}

	void PollMouseWheel()
	{
		var scroll = Input.MouseWheel.y;
		if ( scroll > 0.01f )
			_hotbar.StepActiveSlot( -1 );
		else if ( scroll < -0.01f )
			_hotbar.StepActiveSlot( 1 );
	}
}
