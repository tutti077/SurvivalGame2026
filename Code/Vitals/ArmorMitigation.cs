using System;

namespace Survival;

/// <summary>
/// Physical damage → health loss after armor. Flat below half the hit, then a hyperbolic tail so armor
/// never fully cancels a hit: 100 dmg vs 20 armor → 80, vs 50 → 50, vs 100 → 25; 20 dmg vs 50 armor → 2.
/// </summary>
public static class ArmorMitigation
{
	public static float Apply( float damage, float armor )
	{
		if ( damage <= 0f )
			return 0f;

		if ( armor <= 0f )
			return damage;

		if ( armor < damage * 0.5f )
			return damage - armor;

		return (damage * damage) / (4f * armor);
	}
}
