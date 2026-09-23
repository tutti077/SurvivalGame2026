namespace Survival;

/// <summary>Paperdoll equip slots on <see cref="PlayerEquipment"/>. Append new slots at the end — indices ride equipment RPCs.</summary>
public enum EquipmentSlot
{
	Head,
	Chest,
	Arms,
	Hands,
	Legs,
	Feet,
	MainHand,
	OffHand,
	Backpack,
	Grapple,
	Wingsuit,
	/// <summary>Cloaks / capes. Worn like armor (armor points, weight, clothing) but outside the six armor-set slots.</summary>
	Cloak,
}
