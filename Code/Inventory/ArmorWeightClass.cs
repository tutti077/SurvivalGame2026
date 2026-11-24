namespace Survival;

/// <summary>
/// Weight band of a worn piece (<c>armorWeight</c> in <c>equipment_profiles.json</c>), shown in the item tooltip.
/// It costs no speed: worn gear never slows the pawn (Mark 2026-11-17).
/// </summary>
public enum ArmorWeightClass
{
	None,
	Light,
	Medium,
	Heavy,
}
