namespace Survival;

/// <summary>
/// Weight band of a worn piece (<c>armorWeight</c> in <c>equipment_profiles.json</c>). Each worn piece
/// carries one sixth of its band's run-speed penalty (<see cref="PlayerMovement.ArmorSpeedScale"/>),
/// so a full set of one band lands exactly on that band's scale.
/// </summary>
public enum ArmorWeightClass
{
	None,
	Light,
	Medium,
	Heavy,
}
