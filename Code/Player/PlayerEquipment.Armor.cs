using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Worn-equipment aggregate: armor points, weight-band piece counts, set membership and the clothing
/// the pawn should be dressed in. Recomputed once per paperdoll change (never per hit / per frame).
/// Consumers: <see cref="PlayerVitals.ApplyDamageAfterArmor"/> (host), <see cref="PlayerMovement.ArmorSpeedScale"/>
/// (movement driver), <see cref="PlayerAnimation"/> (clothing on every peer via <see cref="NetworkedWornClothing"/>).
/// </summary>
public sealed partial class PlayerEquipment
{
	/// <summary>The six armor-set slots. Cloak / OffHand still contribute armor points and weight, but not to this divisor.</summary>
	public const int ArmorSlotCount = 6;

	/// <summary>
	/// Pieces of one <c>armorSet</c> worn before the set counts as complete (set bonus + the
	/// <c>armor_set_equipped</c> quest event). Pieces 4–6 are reserved for minor per-piece extras.
	/// </summary>
	[Property, Group( "Armor" ), Title( "Set bonus pieces" ), Range( 1, ArmorSlotCount )]
	public int ArmorSetPieceThreshold { get; set; } = 3;

	/// <summary>Sum of <c>statModifiers.armor</c> over every worn slot (MainHand excluded — it mirrors the hotbar, it is not worn).</summary>
	public float TotalArmor { get; private set; }

	public int LightArmorPieces { get; private set; }
	public int MediumArmorPieces { get; private set; }
	public int HeavyArmorPieces { get; private set; }

	/// <summary>Set id with the most worn pieces (empty when nothing worn belongs to a set).</summary>
	public string ArmorSetId { get; private set; } = string.Empty;

	/// <summary>Worn pieces belonging to <see cref="ArmorSetId"/>.</summary>
	public int ArmorSetPieces { get; private set; }

	/// <summary>True once <see cref="ArmorSetPieces"/> reaches <see cref="ArmorSetPieceThreshold"/>. Set bonus effects hang off this (none defined yet).</summary>
	public bool HasArmorSetBonus =>
		!string.IsNullOrWhiteSpace( ArmorSetId ) && ArmorSetPieces >= Math.Max( 1, ArmorSetPieceThreshold );

	/// <summary>
	/// Host→all peers: <c>|</c>-joined clothing resource paths of the worn pieces, in slot order.
	/// Slots stay owner-private; every peer dresses the citizen from this string alone.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public string NetworkedWornClothing { get; set; } = string.Empty;

	bool _armorSetReported;

	/// <summary>Worn pieces of the given set (tooltip "3/6").</summary>
	public int CountArmorSetPieces( string setId )
	{
		if ( string.IsNullOrWhiteSpace( setId ) )
			return 0;

		var count = 0;
		for ( var i = 0; i < SlotCount; i++ )
		{
			var slot = (EquipmentSlot)i;
			if ( slot == EquipmentSlot.MainHand )
				continue;

			var stack = _slots[i];
			if ( stack.IsEmpty || !EquipmentCatalog.TryGet( stack.ResourceId, out var profile ) )
				continue;

			if ( string.Equals( profile.ArmorSet, setId, StringComparison.OrdinalIgnoreCase ) )
				count++;
		}

		return count;
	}

	void RefreshArmorState()
	{
		var armor = 0f;
		int light = 0, medium = 0, heavy = 0;
		List<(string SetId, int Count)> sets = null;
		List<string> clothing = null;

		for ( var i = 0; i < SlotCount; i++ )
		{
			var slot = (EquipmentSlot)i;
			if ( slot == EquipmentSlot.MainHand )
				continue;

			var stack = _slots[i];
			if ( stack.IsEmpty || !EquipmentCatalog.TryGet( stack.ResourceId, out var profile ) || profile is null )
				continue;

			armor += EquipmentCatalog.GetArmor( profile );

			switch ( EquipmentCatalog.ParseArmorWeight( profile.ArmorWeight ) )
			{
				case ArmorWeightClass.Light:
					light++;
					break;
				case ArmorWeightClass.Medium:
					medium++;
					break;
				case ArmorWeightClass.Heavy:
					heavy++;
					break;
			}

			if ( !string.IsNullOrWhiteSpace( profile.ArmorSet ) )
			{
				sets ??= new List<(string, int)>();
				var found = false;
				for ( var s = 0; s < sets.Count; s++ )
				{
					if ( !string.Equals( sets[s].SetId, profile.ArmorSet, StringComparison.OrdinalIgnoreCase ) )
						continue;

					sets[s] = (sets[s].SetId, sets[s].Count + 1);
					found = true;
					break;
				}

				if ( !found )
					sets.Add( (profile.ArmorSet.Trim().ToLowerInvariant(), 1) );
			}

			if ( !string.IsNullOrWhiteSpace( profile.Clothing ) )
			{
				clothing ??= new List<string>();
				clothing.Add( profile.Clothing.Trim() );
			}
		}

		TotalArmor = armor;
		LightArmorPieces = light;
		MediumArmorPieces = medium;
		HeavyArmorPieces = heavy;

		var bestSet = string.Empty;
		var bestCount = 0;
		if ( sets is not null )
		{
			for ( var s = 0; s < sets.Count; s++ )
			{
				if ( sets[s].Count <= bestCount )
					continue;

				bestSet = sets[s].SetId;
				bestCount = sets[s].Count;
			}
		}

		ArmorSetId = bestSet;
		ArmorSetPieces = bestCount;

		if ( HasHostAuthority )
			NetworkedWornClothing = clothing is null ? string.Empty : string.Join( "|", clothing );

		ReportArmorSetQuest();
	}

	/// <summary>Owner-local quest trigger on the rising edge of a complete set (same pattern as augment install).</summary>
	void ReportArmorSetQuest()
	{
		if ( !HasArmorSetBonus )
		{
			_armorSetReported = false;
			return;
		}

		if ( _armorSetReported || !IsLocalManagingClient() )
			return;

		_armorSetReported = true;
		Components.Get<PlayerQuests>()?.OwnerReport( QuestEventIds.ArmorSetEquipped, ArmorSetId );
	}
}
