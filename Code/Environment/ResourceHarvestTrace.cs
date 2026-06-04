namespace Survival;

/// <summary>Ray-hit helpers for world-harvest <see cref="ResourceItemDefinition"/>.</summary>
public static class ResourceHarvestTrace
{
	public static bool TryFindOnHierarchy( GameObject hitObject, out ResourceItemDefinition node )
	{
		node = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var p = hitObject; p.IsValid(); p = p.Parent )
		{
			var n = p.Components.Get<ResourceItemDefinition>();
			if ( n is null || !n.Enabled || !n.Harvestable )
				continue;

			node = n;
			return true;
		}

		return false;
	}
}
