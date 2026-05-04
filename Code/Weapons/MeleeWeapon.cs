using Sandbox;

namespace Game;

/// <summary>
/// Put on a held weapon (same hierarchy as <see cref="PickableItem"/>). Does not run pickup — <see cref="PlayerItemPickup"/> + <see cref="PickableItem"/> handle that.
/// This component only supplies <see cref="HeldLocalOffset"/> / <see cref="HeldLocalAngles"/> so the prop sits correctly in the hand.
/// </summary>
[Title( "Melee Weapon" )]
[Category( "Weapons" )]
public sealed class MeleeWeapon : Component
{
	[Property] public WeaponType WeaponKind { get; set; } = WeaponType.Sword;

	/// <summary>Extra hold position in the same local basis as <see cref="PlayerItemPickup.HoldOffset"/> (view forward, right, up).</summary>
	[Property] public Vector3 HeldLocalOffset { get; set; }

	/// <summary>Extra rotation after the pickup hold rotation (degrees, in local hold space).</summary>
	[Property] public Angles HeldLocalAngles { get; set; }

	/// <summary>Stamina removed on <c>attack1</c> while this weapon is held (see <see cref="PlayerStamina"/>). Attacks do nothing at 0 stamina.</summary>
	[Property] public float AttackStaminaCost { get; set; } = 5f;
}
