using System;

namespace Survival;

/// <summary>
/// 18 body augment sockets: 6 parts × 3 variations. Sockets unlock in enum order within a part
/// (eye → jaw → cranium, chest → abs → back, …) — see <see cref="AugmentBodyParts.SlotsOf"/>.
/// </summary>
public enum AugmentSlot
{
	HeadEye = 0,
	HeadJaw = 1,
	HeadCranium = 2,
	TorsoChest = 3,
	TorsoAbs = 4,
	TorsoBack = 5,
	ArmShoulders = 6,
	ArmForearms = 7,
	ArmElbow = 8,
	HandFront = 9,
	HandBack = 10,
	HandFingers = 11,
	LegQuads = 12,
	LegCalves = 13,
	LegKneecaps = 14,
	FeetHeel = 15,
	FeetAnkle = 16,
	FeetToes = 17,
}

/// <summary>The six enhanceable body parts. Each owns three <see cref="AugmentSlot"/> sockets.</summary>
public enum AugmentBodyPart
{
	Head = 0,
	Torso = 1,
	Arms = 2,
	Hands = 3,
	Legs = 4,
	Feet = 5,
}

public static class AugmentSlots
{
	public const int Count = 18;

	public static readonly (AugmentSlot Slot, string BodyPart, string Variation)[] Layout =
	{
		(AugmentSlot.HeadEye, "Head", "Eye"),
		(AugmentSlot.HeadJaw, "Head", "Jaw"),
		(AugmentSlot.HeadCranium, "Head", "Cranium"),
		(AugmentSlot.TorsoChest, "Torso", "Chest"),
		(AugmentSlot.TorsoAbs, "Torso", "Abs"),
		(AugmentSlot.TorsoBack, "Torso", "Back"),
		(AugmentSlot.ArmShoulders, "Arms", "Shoulders"),
		(AugmentSlot.ArmForearms, "Arms", "Forearms"),
		(AugmentSlot.ArmElbow, "Arms", "Elbow"),
		(AugmentSlot.HandFront, "Hands", "Front"),
		(AugmentSlot.HandBack, "Hands", "Back"),
		(AugmentSlot.HandFingers, "Hands", "Fingers"),
		(AugmentSlot.LegQuads, "Legs", "Quads"),
		(AugmentSlot.LegCalves, "Legs", "Calves"),
		(AugmentSlot.LegKneecaps, "Legs", "Kneecaps"),
		(AugmentSlot.FeetHeel, "Feet", "Heel"),
		(AugmentSlot.FeetAnkle, "Feet", "Ankle"),
		(AugmentSlot.FeetToes, "Feet", "Toes"),
	};

	public static bool TryParse( string value, out AugmentSlot slot )
	{
		slot = default;
		if ( string.IsNullOrWhiteSpace( value ) )
			return false;

		return Enum.TryParse( value.Trim(), ignoreCase: true, out slot ) && (int)slot >= 0 && (int)slot < Count;
	}

	/// <summary>"Head - Eye" — the form the station list and tooltips use.</summary>
	public static string Label( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= Layout.Length )
			return slot.ToString();

		return $"{Layout[i].BodyPart} - {Layout[i].Variation}";
	}

	public static string VariationLabel( AugmentSlot slot )
	{
		var i = (int)slot;
		return i < 0 || i >= Layout.Length ? slot.ToString() : Layout[i].Variation;
	}

	public static AugmentBodyPart PartOf( AugmentSlot slot ) =>
		(AugmentBodyPart)Math.Clamp( (int)slot / AugmentBodyParts.SlotsPerPart, 0, AugmentBodyParts.Count - 1 );

	/// <summary>0-based unlock order of this socket inside its body part (eye = 0, jaw = 1, cranium = 2).</summary>
	public static int IndexInPart( AugmentSlot slot ) => (int)slot % AugmentBodyParts.SlotsPerPart;
}

/// <summary>
/// Body-part tuning: enhancement (augment core) costs and installation (gold coin) costs.
/// Designer numbers from the station spec — head/torso are the deep tier, hands/feet the shallow one.
/// </summary>
public static class AugmentBodyParts
{
	public const int Count = 6;
	public const int SlotsPerPart = 3;

	/// <summary>Cores to open the first socket of a part; every further socket costs <see cref="AdditionalSlotCoreCost"/>.</summary>
	public static int FirstSlotCoreCost( AugmentBodyPart part ) => part switch
	{
		AugmentBodyPart.Head or AugmentBodyPart.Torso => 3,
		AugmentBodyPart.Arms or AugmentBodyPart.Legs => 2,
		_ => 1,
	};

	public const int AdditionalSlotCoreCost = 1;

	/// <summary>Cores to open the next socket given how many are already open (0..3). 0 when the part is full.</summary>
	public static int NextSlotCoreCost( AugmentBodyPart part, int unlockedCount )
	{
		if ( unlockedCount >= SlotsPerPart )
			return 0;

		return unlockedCount <= 0 ? FirstSlotCoreCost( part ) : AdditionalSlotCoreCost;
	}

	/// <summary>Part tier: 1 = hands/feet, 2 = arms/legs, 3 = head/torso.</summary>
	public static int Tier( AugmentBodyPart part ) => part switch
	{
		AugmentBodyPart.Head or AugmentBodyPart.Torso => 3,
		AugmentBodyPart.Arms or AugmentBodyPart.Legs => 2,
		_ => 1,
	};

	/// <summary>Gold coins to install a tier-1 augment into this part (×2 for tier 2, ×3 for tier 3 augments).</summary>
	public static int BaseInstallGoldCost( AugmentBodyPart part ) => part switch
	{
		AugmentBodyPart.Head or AugmentBodyPart.Torso => 100,
		AugmentBodyPart.Arms or AugmentBodyPart.Legs => 75,
		_ => 50,
	};

	public static int InstallGoldCost( AugmentBodyPart part, int augmentTier ) =>
		BaseInstallGoldCost( part ) * Math.Clamp( augmentTier, 1, AugmentDefinition.MaxTier );

	public static string Label( AugmentBodyPart part ) => part switch
	{
		AugmentBodyPart.Head => "Head",
		AugmentBodyPart.Torso => "Torso",
		AugmentBodyPart.Arms => "Arms",
		AugmentBodyPart.Hands => "Hands",
		AugmentBodyPart.Legs => "Legs",
		AugmentBodyPart.Feet => "Feet",
		_ => part.ToString(),
	};

	public static AugmentSlot SlotAt( AugmentBodyPart part, int indexInPart ) =>
		(AugmentSlot)((int)part * SlotsPerPart + Math.Clamp( indexInPart, 0, SlotsPerPart - 1 ));

	/// <summary>The three sockets of a part in unlock order.</summary>
	public static AugmentSlot[] SlotsOf( AugmentBodyPart part ) => new[]
	{
		SlotAt( part, 0 ),
		SlotAt( part, 1 ),
		SlotAt( part, 2 ),
	};
}

/// <summary>Item ids the station spends. Both are plain resources in <c>resources.json</c>.</summary>
public static class AugmentCurrency
{
	/// <summary>Enhancement currency — free to craft from the personal crafting menu for now.</summary>
	public const string CoreResourceId = "resource_augmentCore";

	/// <summary>Installation currency (conductive but unstable, so it is consumed by the augment step).</summary>
	public const string GoldResourceId = "resource_goldCoins";
}
