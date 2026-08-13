using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Valheim-style food: equip edible on hotbar, Attack1 to eat. Up to 3 active foods;
/// one of each type; raises max HP/stamina for the food duration.
/// </summary>
[Title( "Player Food" )]
public sealed class PlayerFood : Component
{
	public const int MaxFoodSlots = 3;

	[Sync] public string FoodSlot0Id { get; private set; } = string.Empty;
	[Sync] public string FoodSlot1Id { get; private set; } = string.Empty;
	[Sync] public string FoodSlot2Id { get; private set; } = string.Empty;
	[Sync] public double FoodSlot0Expires { get; private set; }
	[Sync] public double FoodSlot1Expires { get; private set; }
	[Sync] public double FoodSlot2Expires { get; private set; }

	public event Action FoodChanged;

	PlayerVitals _vitals;
	PlayerHotbar _hotbar;
	PlayerEquippedItem _equipped;
	PlayerGameMenuController _menu;
	float _regenCarry;
	string _lastHudKey = string.Empty;

	bool IsLocalDriver()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is not { Active: true } net )
			return true;

		return net.Owner is null ? Networking.IsHost : net.IsOwner;
	}

	bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_hotbar = Components.Get<PlayerHotbar>();
		_equipped = Components.Get<PlayerEquippedItem>();
		_menu = Components.Get<PlayerGameMenuController>();
		FoodCatalog.EnsureLoaded();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( HasHostAuthority )
			TickExpireAndRegen( Time.Delta );

		NotifyHudIfChanged();

		if ( !IsLocalDriver() )
			return;

		_menu ??= Components.Get<PlayerGameMenuController>();
		if ( _menu is not null && _menu.IsMenuOpen )
			return;

		if ( !Input.Pressed( "Attack1" ) )
			return;

		TryOwnerEatActiveHotbarFood();
	}

	void NotifyHudIfChanged()
	{
		var key = $"{FoodSlot0Id}:{FoodSlot0Expires:0}|{FoodSlot1Id}:{FoodSlot1Expires:0}|{FoodSlot2Id}:{FoodSlot2Expires:0}";
		if ( key == _lastHudKey )
			return;

		_lastHudKey = key;
		FoodChanged?.Invoke();
	}

	void TickExpireAndRegen( float dt )
	{
		var changed = false;
		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out var id, out var expires );
			if ( string.IsNullOrWhiteSpace( id ) )
				continue;

			if ( Time.NowDouble < expires )
				continue;

			SetSlot( i, string.Empty, 0 );
			changed = true;
		}

		if ( changed )
			HostRecalculateFoodCaps();

		var regen = 0f;
		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out var id, out var expires );
			if ( string.IsNullOrWhiteSpace( id ) || Time.NowDouble >= expires )
				continue;

			if ( FoodCatalog.TryGet( id, out var food ) )
				regen += Math.Max( 0f, food.HealthRegenPerSecond );
		}

		if ( regen <= 1e-6f || _vitals is null )
			return;

		_regenCarry += regen * dt;
		if ( _regenCarry < 0.25f )
			return;

		var apply = _regenCarry;
		_regenCarry = 0f;
		_vitals.RequestVitalsDelta( apply, 0f );
	}

	void TryOwnerEatActiveHotbarFood()
	{
		_equipped ??= Components.Get<PlayerEquippedItem>();
		_hotbar ??= Components.Get<PlayerHotbar>();
		if ( _equipped is null || _hotbar is null )
			return;

		if ( _equipped.HasAction( EquippedItemActions.PrimaryMelee )
		     || _equipped.HasAction( EquippedItemActions.PrimaryRanged )
		     || _equipped.HasAction( EquippedItemActions.BuildHammer ) )
			return;

		var resourceId = _equipped.ActiveHotbarResourceId;
		if ( string.IsNullOrWhiteSpace( resourceId ) || !FoodCatalog.IsEdible( resourceId ) )
			return;

		if ( GameObject.Network is not { Active: true } )
		{
			HostTryEat( resourceId, _hotbar.ActiveSlotIndex );
			return;
		}

		if ( Networking.IsHost )
			HostTryEat( resourceId, _hotbar.ActiveSlotIndex );
		else
			RpcHostEatFood( resourceId, _hotbar.ActiveSlotIndex );
	}

	[Rpc.Host]
	void RpcHostEatFood( string resourceId, int hotbarSlot )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		HostTryEat( resourceId, hotbarSlot );
	}

	bool HostTryEat( string resourceId, int hotbarSlot )
	{
		if ( !HasHostAuthority )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		if ( !FoodCatalog.TryGet( resourceId, out var food ) || !food.Edible )
			return false;

		_hotbar ??= Components.Get<PlayerHotbar>();
		_vitals ??= Components.Get<PlayerVitals>();
		if ( _hotbar is null || _vitals is null )
			return false;

		var consumed = false;
		var slotStack = _hotbar.GetSlot( hotbarSlot );
		if ( !slotStack.IsEmpty && ResourceCatalog.ResourceIdsMatch( slotStack.ResourceId, resourceId ) )
			consumed = _hotbar.TryConsumeResource( resourceId, 1 ) == 0;

		if ( !consumed )
		{
			var inventory = Components.Get<PlayerInventory>();
			if ( inventory is null )
				return false;

			consumed = inventory.HostTryConsumeResources( new[]
			{
				new CraftingIngredient { ResourceId = resourceId, Amount = 1 }
			} );
		}

		if ( !consumed )
			return false;

		ApplyFoodSlot( food );
		if ( food.RestoreHealth > 0f || food.RestoreStamina > 0f )
			_vitals.RequestVitalsDelta( food.RestoreHealth, food.RestoreStamina );

		HostRecalculateFoodCaps();
		return true;
	}

	void ApplyFoodSlot( FoodItemData food )
	{
		var expires = Time.NowDouble + Math.Max( 1f, food.DurationSeconds );

		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out var id, out _ );
			if ( ResourceCatalog.ResourceIdsMatch( id, food.ResourceId ) )
			{
				SetSlot( i, food.ResourceId, expires );
				return;
			}
		}

		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out var id, out _ );
			if ( string.IsNullOrWhiteSpace( id ) )
			{
				SetSlot( i, food.ResourceId, expires );
				return;
			}
		}

		var worst = 0;
		var worstRem = float.MaxValue;
		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out _, out var exp );
			var rem = (float)Math.Max( 0, exp - Time.NowDouble );
			if ( rem < worstRem )
			{
				worstRem = rem;
				worst = i;
			}
		}

		SetSlot( worst, food.ResourceId, expires );
	}

	void HostRecalculateFoodCaps()
	{
		_vitals ??= Components.Get<PlayerVitals>();
		if ( _vitals is null || !HasHostAuthority )
			return;

		var bonusHp = 0f;
		var bonusSt = 0f;
		for ( var i = 0; i < MaxFoodSlots; i++ )
		{
			GetSlot( i, out var id, out var expires );
			if ( string.IsNullOrWhiteSpace( id ) || Time.NowDouble >= expires )
				continue;

			if ( !FoodCatalog.TryGet( id, out var food ) )
				continue;

			bonusHp += Math.Max( 0f, food.MaxHealth );
			bonusSt += Math.Max( 0f, food.MaxStamina );
		}

		_vitals.HostSetPoolMaxes( _vitals.MaxHealth + bonusHp, _vitals.MaxStamina + bonusSt );
	}

	void GetSlot( int index, out string id, out double expires )
	{
		switch ( index )
		{
			case 0:
				id = FoodSlot0Id;
				expires = FoodSlot0Expires;
				return;
			case 1:
				id = FoodSlot1Id;
				expires = FoodSlot1Expires;
				return;
			default:
				id = FoodSlot2Id;
				expires = FoodSlot2Expires;
				return;
		}
	}

	void SetSlot( int index, string id, double expires )
	{
		id ??= string.Empty;
		switch ( index )
		{
			case 0:
				FoodSlot0Id = id;
				FoodSlot0Expires = expires;
				break;
			case 1:
				FoodSlot1Id = id;
				FoodSlot1Expires = expires;
				break;
			default:
				FoodSlot2Id = id;
				FoodSlot2Expires = expires;
				break;
		}
	}

	public float GetSlotRemainingSeconds( int index )
	{
		GetSlot( index, out var id, out var expires );
		if ( string.IsNullOrWhiteSpace( id ) )
			return -1f;

		return (float)Math.Max( 0, expires - Time.NowDouble );
	}

	public string GetSlotResourceId( int index )
	{
		GetSlot( index, out var id, out _ );
		return id ?? string.Empty;
	}
}
