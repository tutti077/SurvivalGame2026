using System.Collections.Generic;

namespace Survival;

/// <summary>Active world-harvest <see cref="ResourceItemDefinition"/> instances — avoids per-scan <c>GetAllComponents</c>.</summary>
internal static class ResourceHarvestRegistry
{
	static readonly List<ResourceItemDefinition> Active = new();

	public static IReadOnlyList<ResourceItemDefinition> Nodes => Active;

	internal static void Register( ResourceItemDefinition node )
	{
		if ( node is null || !node.Harvestable || Active.Contains( node ) )
			return;
		Active.Add( node );
	}

	internal static void Unregister( ResourceItemDefinition node )
	{
		if ( node is null )
			return;
		Active.Remove( node );
	}
}
