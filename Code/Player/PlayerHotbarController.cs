using Sandbox;

namespace Survival;

/// <summary>Number keys always; scroll swaps hotbar unless a build piece preview owns scroll.</summary>
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
	PlayerGameMenuController _menu;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_hotbar = Components.Get<PlayerHotbar>();
		_equipment = Components.Get<PlayerEquipment>();
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

		if ( IsBuildPreviewOwningScroll() )
			return;

		PollMouseWheel();
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
			_equipment?.EquipFromHotbarSlot( i );
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

	bool IsBuildPreviewOwningScroll()
	{
		var hammer = _equipment?.GetActiveTool<ToolBuildHammer>();
		return hammer is not null && hammer.IsPreviewingPlacePiece;
	}

	void PollMouseWheel()
	{
		var scroll = Input.MouseWheel.y;
		if ( scroll > 0.01f )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( -1 );
			_equipment?.EquipMainHandFromActiveHotbar();
		}
		else if ( scroll < -0.01f )
		{
			ExitBuildModeForHotbarSwap();
			_hotbar.StepActiveSlot( 1 );
			_equipment?.EquipMainHandFromActiveHotbar();
		}
	}
}
