namespace Survival;

/// <summary>
/// Weapon/tool durability hooks for the phased melee and bow paths. Wear state lives on the
/// active hotbar slot (the MainHand mirror); rules live in <see cref="ToolDurability"/>.
/// Free swings cost nothing — only confirmed contact (melee) or a fired arrow (bow) ticks wear.
/// </summary>
public partial class PlayerCombat
{
	/// <summary>True when the equipped weapon/tool is out of durability (owner and host both read local hotbar state).</summary>
	public bool IsActiveMainHandBroken() => ToolDurability.IsActiveToolBroken( GameObject );

	/// <summary>Host: one durability tick on the equipped weapon/tool (melee contact, bow shot).</summary>
	public void HostAddWearToActiveMainHand( int amount = 1 ) =>
		ToolDurability.HostAddWearToActiveTool( GameObject, amount );

	/// <summary>Workbench upgrade tier of the held weapon (I when the hand is empty or the item has no tiers).</summary>
	public int GetActiveWeaponTier() =>
		EquipmentUpgrade.NormalizeTier( ToolDurability.GetActiveHotbarStack( GameObject ).Tier );
}
