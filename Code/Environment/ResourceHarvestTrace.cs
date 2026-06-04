namespace Survival;

/// <summary>Ray-hit helpers for <see cref="ResourceHarvestNode"/>.</summary>
public static class ResourceHarvestTrace
{
	public static bool TryFindOnHierarchy( GameObject hitObject, out ResourceHarvestNode node )
	{
		node = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var p = hitObject; p.IsValid(); p = p.Parent )
		{
			var n = p.Components.Get<ResourceHarvestNode>();
			if ( n is null || !n.Enabled )
				continue;

			node = n;
			return true;
		}

		return false;
	}
}
