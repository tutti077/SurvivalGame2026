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
}
