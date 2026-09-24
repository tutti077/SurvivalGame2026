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
	public const int MaxTier = 3;

	/// <summary>Canonical item id (bank / bag / installed slot ResourceId).</summary>
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	[JsonPropertyName( "icon" )]
	public string Icon { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// Sockets this augment may install into (e.g. <c>["LegQuads"]</c>). Most augments list one;
	/// later augments may list several. An augment never fits a socket that is not listed here.
	/// </summary>
	[JsonPropertyName( "slots" )]
	public List<string> Slots { get; set; } = new();

	/// <summary>Augment tier 1..3 — multiplies the body part's base install cost (×1 / ×2 / ×3).</summary>
	[JsonPropertyName( "tier" )]
	public int Tier { get; set; } = 1;

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
	public int ResolvedTier => Math.Clamp( Tier, 1, MaxTier );
	public bool IsUnlockedByDefault => string.IsNullOrWhiteSpace( UnlockId );

	List<AugmentSlot> _parsedSlots;

	/// <summary>Parsed <see cref="Slots"/> (unparseable entries dropped). Cached after the first read.</summary>
	public IReadOnlyList<AugmentSlot> AllowedSlots
	{
		get
		{
			if ( _parsedSlots is not null )
				return _parsedSlots;

			var list = new List<AugmentSlot>();
			if ( Slots is not null )
			{
				for ( var i = 0; i < Slots.Count; i++ )
				{
					if ( AugmentSlots.TryParse( Slots[i], out var slot ) && !list.Contains( slot ) )
						list.Add( slot );
				}
			}

			_parsedSlots = list;
			return list;
		}
	}

	public bool AllowsSlot( AugmentSlot slot )
	{
		var allowed = AllowedSlots;
		for ( var i = 0; i < allowed.Count; i++ )
		{
			if ( allowed[i] == slot )
				return true;
		}

		return false;
	}

	/// <summary>First listed socket — used as the list-group key and the quick-move fallback.</summary>
	public bool TryGetPrimarySlot( out AugmentSlot slot )
	{
		var allowed = AllowedSlots;
		slot = allowed.Count > 0 ? allowed[0] : default;
		return allowed.Count > 0;
	}

	/// <summary>Gold coins the augment step charges to put this augment into <paramref name="slot"/>.</summary>
	public int InstallGoldCost( AugmentSlot slot ) =>
		AugmentBodyParts.InstallGoldCost( AugmentSlots.PartOf( slot ), ResolvedTier );

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
