using Sandbox;

namespace Survival;

/// <summary>
/// Placeable workbench: Use-key look trace opens the crafting menu filtered to workbench
/// recipes, with the free tool-repair button at the top. No fuel — always usable.
/// </summary>
[Title( "Workbench" )]
public sealed class Workbench : Component
{
	public const string StationId = "workbench";

	public static bool TryFindOnHierarchy( GameObject hitObject, out Workbench workbench )
	{
		workbench = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var current = hitObject; current.IsValid(); current = current.Parent )
		{
			var candidate = current.Components.Get<Workbench>();
			if ( candidate is null || !candidate.Enabled )
				continue;

			if ( candidate.GameObject.Tags.Has( "buildpreview" ) )
				continue;

			var piece = candidate.Components.Get<BuildPiece>();
			if ( piece is not null && ( piece.IsPreviewGhost || piece.IsBlueprint ) )
				continue;

			workbench = candidate;
			return true;
		}

		return false;
	}
}
