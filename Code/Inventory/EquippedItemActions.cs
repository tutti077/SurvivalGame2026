using System;

namespace Survival;

/// <summary>Input / behavior flags for the item in the active hotbar slot.</summary>
[Flags]
public enum EquippedItemActions
{
	None = 0,
	PrimaryMelee = 1 << 0,
	Block = 1 << 1,
	BuildHammer = 1 << 2,
	Grapple = 1 << 3,
	Wingsuit = 1 << 4,
	/// <summary>Hold-to-charge ranged fire (bow). Does not enable melee teardrop / sword paths.</summary>
	PrimaryRanged = 1 << 5,
	/// <summary>Fishing rod cast / minigame (<see cref="PlayerFishing"/>). No melee or ranged paths.</summary>
	Fish = 1 << 6,
}
