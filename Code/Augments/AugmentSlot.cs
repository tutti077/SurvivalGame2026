namespace Survival;

/// <summary>
/// 18 body augment sockets: 6 parts × 3 variations (eye/jaw/cranium, …, quads/calves/kneecaps, …).
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
		(AugmentSlot.ArmShoulders, "Arm", "Shoulders"),
		(AugmentSlot.ArmForearms, "Arm", "Forearms"),
		(AugmentSlot.ArmElbow, "Arm", "Elbow"),
		(AugmentSlot.HandFront, "Hand", "Front"),
		(AugmentSlot.HandBack, "Hand", "Back"),
		(AugmentSlot.HandFingers, "Hand", "Fingers"),
		(AugmentSlot.LegQuads, "Leg", "Quads"),
		(AugmentSlot.LegCalves, "Leg", "Calves"),
		(AugmentSlot.LegKneecaps, "Leg", "Kneecaps"),
		(AugmentSlot.FeetHeel, "Feet", "Heel"),
		(AugmentSlot.FeetAnkle, "Feet", "Ankle"),
		(AugmentSlot.FeetToes, "Feet", "Toes"),
	};

	public static bool TryParse( string value, out AugmentSlot slot )
	{
		slot = default;
		if ( string.IsNullOrWhiteSpace( value ) )
			return false;

		return System.Enum.TryParse( value.Trim(), ignoreCase: true, out slot );
	}

	public static string Label( AugmentSlot slot )
	{
		var i = (int)slot;
		if ( i < 0 || i >= Layout.Length )
			return slot.ToString();

		return $"{Layout[i].BodyPart} · {Layout[i].Variation}";
	}
}
