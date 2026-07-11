using System.Collections.Generic;

namespace Survival;

/// <summary>Active <see cref="WorldDroppedResource"/> instances — avoids per-scan <c>GetAllComponents</c>.</summary>
internal static class WorldDroppedResourceRegistry
{
	static readonly List<WorldDroppedResource> Active = new();

	public static IReadOnlyList<WorldDroppedResource> Drops => Active;

	internal static void Register( WorldDroppedResource drop )
	{
		if ( drop is null || Active.Contains( drop ) )
			return;

		Active.Add( drop );
	}

	internal static void Unregister( WorldDroppedResource drop )
	{
		if ( drop is null )
			return;

		Active.Remove( drop );
	}
}
