using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Hover tooltip for item slots (bag / hotbar / paperdoll / containers): after
/// <see cref="TooltipHoverDelaySeconds"/> over a stack, a mouse-following popup shows the item
/// name, description, weapon/tool stats, food effects, durability, and — for crafted equipment —
/// who made it. Sized by its content; hidden while dragging or holding a cursor stack.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	public const float TooltipHoverDelaySeconds = 0.5f;
	const float TooltipMaxWidth = 340f;

	Panel _tooltipRoot;
	InventorySlotPanel _tooltipHoverSlot;
	double _tooltipHoverStartedAt;
	string _tooltipContentKey;

	static readonly Color TooltipNameColor = Color.White;
	static readonly Color TooltipTypeColor = new( 0.62f, 0.65f, 0.72f );
	static readonly Color TooltipDescriptionColor = new( 0.78f, 0.8f, 0.84f );
	static readonly Color TooltipStatColor = new( 0.85f, 0.86f, 0.9f );
	static readonly Color TooltipFoodColor = new( 0.55f, 0.85f, 0.55f );
	static readonly Color TooltipBrokenColor = new( 0.9f, 0.35f, 0.3f );
	static readonly Color TooltipDurabilityColor = new( 0.87f, 0.76f, 0.38f );
	static readonly Color TooltipCrafterColor = new( 0.55f, 0.62f, 0.75f );

	void TickItemTooltip()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		if ( !menuOpen || _leftDragActive || !_held.IsEmpty )
		{
			HideItemTooltip();
			return;
		}

		var pointer = GetDropProbeScreenPosition();
		var slotPanel = FindSlotAtScreenPosition( pointer );
		var slot = slotPanel is not null
			? slotPanel.GridHost?.GetSlot( slotPanel.SlotIndex ) ?? InventorySlot.Empty
			: InventorySlot.Empty;

		if ( slotPanel is null || slot.IsEmpty )
		{
			HideItemTooltip();
			return;
		}

		if ( !ReferenceEquals( slotPanel, _tooltipHoverSlot ) )
		{
			// New slot under the pointer — restart the dwell timer.
			_tooltipHoverSlot = slotPanel;
			_tooltipHoverStartedAt = Time.NowDouble;
			SetTooltipVisible( false );
			return;
		}

		if ( Time.NowDouble - _tooltipHoverStartedAt < TooltipHoverDelaySeconds )
			return;

		ShowItemTooltip( slot, pointer );
	}

	void HideItemTooltip()
	{
		_tooltipHoverSlot = null;
		SetTooltipVisible( false );
	}

	void SetTooltipVisible( bool visible )
	{
		if ( _tooltipRoot is not null && _tooltipRoot.IsValid() )
			_tooltipRoot.Style.Set( "display", visible ? "flex" : "none" );
	}

	void ShowItemTooltip( in InventorySlot slot, Vector2 pointerScreen )
	{
		EnsureDragLayer();
		if ( _dragLayer is null || !_dragLayer.IsValid() )
			return;

		EnsureTooltipPanel();

		var key = $"{slot.ResourceId}|{slot.Count}|{slot.Wear}|{slot.CrafterName}";
		if ( !string.Equals( key, _tooltipContentKey, StringComparison.Ordinal ) )
		{
			_tooltipContentKey = key;
			BuildTooltipContent( slot );
		}

		SetTooltipVisible( true );
		PositionTooltip( pointerScreen );
	}

	void EnsureTooltipPanel()
	{
		if ( _tooltipRoot is not null && _tooltipRoot.IsValid() )
			return;

		_tooltipRoot = new Panel { Parent = _dragLayer };
		_tooltipRoot.Style.Set( "position", "absolute" );
		_tooltipRoot.Style.Set( "flex-direction", "column" );
		_tooltipRoot.Style.Set( "gap", "4px" );
		_tooltipRoot.Style.Set( "padding-left", "12px" );
		_tooltipRoot.Style.Set( "padding-right", "12px" );
		_tooltipRoot.Style.Set( "padding-top", "9px" );
		_tooltipRoot.Style.Set( "padding-bottom", "9px" );
		_tooltipRoot.Style.Set( "max-width", $"{TooltipMaxWidth}px" );
		_tooltipRoot.Style.BackgroundColor = new Color( 0.05f, 0.06f, 0.08f, 0.96f );
		_tooltipRoot.Style.Set( "border-radius", "6px" );
		_tooltipRoot.Style.Set( "border-width", "1px" );
		_tooltipRoot.Style.Set( "border-color", "#4a5160" );
		_tooltipRoot.Style.Set( "pointer-events", "none" );
		_tooltipRoot.Style.Set( "display", "none" );
		_tooltipContentKey = null;
	}

	void PositionTooltip( Vector2 pointerScreen )
	{
		if ( _tooltipRoot is null || !_tooltipRoot.IsValid() )
			return;

		var local = ScreenToDragLayerLocal( pointerScreen + new Vector2( 18f, 22f ) );

		// Flip against the layer edges using last frame's measured size (0 on the first frame).
		var layerRect = _dragLayer.Box.Rect;
		var scale = MathF.Max( 0.001f, _dragLayer.ScaleToScreen );
		var layerSize = new Vector2( layerRect.Width, layerRect.Height ) / scale;
		var tipRect = _tooltipRoot.Box.Rect;
		var tipSize = new Vector2( tipRect.Width, tipRect.Height ) / scale;

		if ( tipSize.x > 1f && layerSize.x > 1f && local.x + tipSize.x > layerSize.x )
			local.x = MathF.Max( 0f, local.x - tipSize.x - 36f );
		if ( tipSize.y > 1f && layerSize.y > 1f && local.y + tipSize.y > layerSize.y )
			local.y = MathF.Max( 0f, local.y - tipSize.y - 36f );

		_tooltipRoot.Style.Left = Length.Pixels( local.x );
		_tooltipRoot.Style.Top = Length.Pixels( local.y );
	}

	void BuildTooltipContent( in InventorySlot slot )
	{
		_tooltipRoot.DeleteChildren();

		var id = ResourceCatalog.NormalizeResourceId( slot.ResourceId );
		var recipe = CraftingRecipeCatalog.Get( id );
		FoodCatalog.TryGet( id, out var food );
		var hasProfile = EquipmentCatalog.TryGet( id, out var profile );

		var name = ResolveTooltipDisplayName( id, recipe, food );
		if ( slot.Count > 1 )
			name = $"{name}  ×{slot.Count}";
		AddTooltipLine( name, TooltipNameColor, 22f );

		var type = GetRecipeStat( recipe, "Type" );
		if ( !string.IsNullOrWhiteSpace( type ) )
			AddTooltipLine( type, TooltipTypeColor, 14f );

		var description = ResolveTooltipDescription( id, recipe );
		if ( !string.IsNullOrWhiteSpace( description ) )
			AddTooltipLine( description, TooltipDescriptionColor, 16f );

		if ( hasProfile && profile is not null )
			AddEquipmentLines( slot, profile );

		if ( food is not null )
			AddFoodLines( food );

		if ( recipe is not null && !string.IsNullOrWhiteSpace( recipe.AmmoType ) )
			AddTooltipLine( $"Ammo ({recipe.AmmoType}) — +{recipe.Damage:0.#} damage per hit", TooltipStatColor, 16f );

		if ( hasProfile && !string.IsNullOrWhiteSpace( slot.CrafterName ) )
			AddTooltipLine( $"Crafted by {slot.CrafterName}", TooltipCrafterColor, 14f );
	}

	void AddEquipmentLines( in InventorySlot slot, EquipmentProfileData profile )
	{
		var lines = new List<(string Text, Color Color)>();

		if ( profile.StatModifiers is { } stats && stats.Damage > 0f )
			lines.Add( ($"Damage: {stats.Damage:0.#}", TooltipStatColor) );

		var combat = Components.Get<PlayerCombat>();
		if ( EquipmentCatalog.HasAction( profile.ResourceId, EquippedItemActions.PrimaryMelee ) )
		{
			// Timings come from this item's weapon class row, not from whatever is currently in hand.
			var timings = MeleeWeaponClassCatalog.Resolve( profile.WeaponClass, profile.MeleeOverrides );
			lines.Add( ($"Windup {timings.WindupSeconds:0.00}s · charge {timings.ChargeSeconds:0.00}s · reach {timings.ReachForwardMeters:0.0}m", TooltipStatColor) );
		}

		if ( EquipmentCatalog.HasAction( profile.ResourceId, EquippedItemActions.PrimaryRanged ) && combat is not null )
			lines.Add( ($"Full draw: {combat.BowFullDrawSeconds:0.0}s", TooltipStatColor) );

		if ( !string.IsNullOrWhiteSpace( profile.HarvestToolType ) )
			lines.Add( ($"Harvest tool: {profile.HarvestToolType} (tier {profile.HarvestToolTier})", TooltipStatColor) );

		if ( profile.TwoHanded )
			lines.Add( ("Two-handed", TooltipTypeColor) );

		var max = ToolDurability.GetMax( profile.ResourceId );
		if ( max > 0 )
		{
			if ( ToolDurability.IsBroken( slot ) )
				lines.Add( ("BROKEN — repair at a workbench", TooltipBrokenColor) );
			else
				lines.Add( ($"Durability: {Math.Max( 0, max - slot.Wear )}/{max}", TooltipDurabilityColor) );

			var drain = ToolDurability.GetEquippedDrainSeconds( profile.ResourceId );
			if ( drain > 0f )
				lines.Add( ($"Burns 1 durability every {drain:0.#}s while held", TooltipTypeColor) );
		}

		foreach ( var (text, color) in lines )
			AddTooltipLine( text, color, 16f );
	}

	void AddFoodLines( FoodItemData food )
	{
		if ( !food.Edible )
		{
			AddTooltipLine( "Raw ingredient — cook at a campfire first", TooltipTypeColor, 15f );
			return;
		}

		if ( food.RestoreHealth > 0f )
			AddTooltipLine( $"Restores {food.RestoreHealth:0.#} HP", TooltipFoodColor, 16f );

		if ( food.RestoreStamina > 0f )
			AddTooltipLine( $"Restores {food.RestoreStamina:0.#} stamina", TooltipFoodColor, 16f );

		if ( food.HealthRegenPerSecond > 0f )
			AddTooltipLine( $"+{food.HealthRegenPerSecond:0.#} HP/s for {food.DurationSeconds:0}s", TooltipFoodColor, 16f );

		if ( food.MaxHealth > 0f || food.MaxStamina > 0f )
		{
			var parts = new List<string>();
			if ( food.MaxHealth > 0f )
				parts.Add( $"+{food.MaxHealth:0.#} max HP" );
			if ( food.MaxStamina > 0f )
				parts.Add( $"+{food.MaxStamina:0.#} max stamina" );

			AddTooltipLine( $"{string.Join( ", ", parts )} for {food.DurationSeconds:0}s", TooltipFoodColor, 16f );
		}
	}

	void AddTooltipLine( string text, Color color, float fontSize )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return;

		var label = new Label { Parent = _tooltipRoot, Text = text };
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( fontSize );
		label.Style.Set( "white-space", "normal" );
		label.Style.Set( "pointer-events", "none" );
	}

	static string ResolveTooltipDisplayName( string id, CraftingRecipe recipe, FoodItemData food )
	{
		if ( recipe is not null && !string.IsNullOrWhiteSpace( recipe.DisplayName ) )
			return recipe.DisplayName;

		if ( food is not null && !string.IsNullOrWhiteSpace( food.DisplayName ) )
			return food.DisplayName;

		if ( ResourceDefinitionCatalog.TryGet( id, out var def ) && !string.IsNullOrWhiteSpace( def.DisplayName ) )
			return def.DisplayName;

		return ResourceCatalog.Resolve( id ).DisplayName;
	}

	static string ResolveTooltipDescription( string id, CraftingRecipe recipe )
	{
		var fromRecipe = GetRecipeStat( recipe, "Description" );
		if ( !string.IsNullOrWhiteSpace( fromRecipe ) )
			return fromRecipe;

		if ( ResourceDefinitionCatalog.TryGet( id, out var def ) && !string.IsNullOrWhiteSpace( def.Description ) )
			return def.Description;

		return null;
	}

	static string GetRecipeStat( CraftingRecipe recipe, string label )
	{
		if ( recipe?.Stats is null )
			return null;

		for ( var i = 0; i < recipe.Stats.Count; i++ )
		{
			var stat = recipe.Stats[i];
			if ( stat is not null && string.Equals( stat.Label, label, StringComparison.OrdinalIgnoreCase ) )
				return stat.Value;
		}

		return null;
	}
}
