using Sandbox;

namespace Survival;

/// <summary>
/// World crafting bench for augments. Look + Use opens the full-screen augment station menu
/// (same interaction pattern as <see cref="ContainerInventory"/> / chest).
/// </summary>
[Title( "Augment Station" )]
public sealed class AugmentStation : Component
{
	[Property] public string DisplayName { get; set; } = "Augment Station";

	public static bool TryFindOnHierarchy( GameObject hitObject, out AugmentStation station )
	{
		station = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		var root = hitObject.Root ?? hitObject;
		station = root.Components.Get<AugmentStation>( FindMode.EverythingInSelfAndDescendants )
		          ?? hitObject.Components.Get<AugmentStation>( FindMode.EverythingInSelfAndDescendants );
		return station is not null && station.IsValid();
	}
}
