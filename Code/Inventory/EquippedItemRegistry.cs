using System;

namespace Survival;

/// <summary>Maps resource ids to gameplay actions — thin wrapper over <see cref="EquipmentCatalog"/>.</summary>
public static class EquippedItemRegistry
{
	public static EquippedItemActions GetActions( string resourceId ) =>
		EquipmentCatalog.GetActions( resourceId );

	public static bool HasAction( string resourceId, EquippedItemActions action ) =>
		EquipmentCatalog.HasAction( resourceId, action );

	[Obsolete( "Profiles are loaded from equipment_profiles.json." )]
	public static void RegisterProfile( string resourceId, EquippedItemActions actions )
	{
	}
}
