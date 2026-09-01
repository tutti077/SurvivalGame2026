using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>Which gameplay ability an installed augment grants.</summary>
public enum AugmentAbility
{
	None = 0,
	JumpHeight = 1,
	// 2 was LateralDash — retired when the dodge roll became core movement.
	DoubleJump = 3,
}

public sealed class AugmentDefinitionFile
{
	[JsonPropertyName( "augments" )]
	public List<AugmentDefinition> Augments { get; set; } = new();
}

public sealed class AugmentDefinition
{
	/// <summary>Canonical item id (bank / bag / installed slot ResourceId).</summary>
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	[JsonPropertyName( "icon" )]
	public string Icon { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;

	/// <summary>Exact socket this augment installs into (e.g. <c>LegQuads</c>).</summary>
	[JsonPropertyName( "slot" )]
	public string Slot { get; set; } = string.Empty;

	[JsonPropertyName( "ability" )]
	public string Ability { get; set; } = string.Empty;

	[JsonPropertyName( "jumpHeightMultiplier" )]
	public float JumpHeightMultiplier { get; set; } = 1f;

	public List<CraftingIngredient> Ingredients { get; set; } = new();
	public List<CraftingStatLine> Stats { get; set; } = new();

	[JsonPropertyName( "maxStack" )]
	public int MaxStack { get; set; } = 1;

	public string UnlockId { get; set; } = string.Empty;

	public int ResolvedMaxStack => MaxStack > 0 ? MaxStack : 1;
	public bool IsUnlockedByDefault => string.IsNullOrWhiteSpace( UnlockId );

	public bool TryGetSlot( out AugmentSlot slot ) => AugmentSlots.TryParse( Slot, out slot );

	public AugmentAbility ResolvedAbility
	{
		get
		{
			if ( string.IsNullOrWhiteSpace( Ability ) )
				return AugmentAbility.None;

			return Enum.TryParse( Ability.Trim(), ignoreCase: true, out AugmentAbility ability )
				? ability
				: AugmentAbility.None;
		}
	}
}
