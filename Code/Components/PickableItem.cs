using Sandbox;

namespace Game;

/// <summary>
/// Attach to a prop with a <see cref="Rigidbody"/> (and collider). <see cref="PlayerItemPickup"/> on the player runs traces and grab/drop.
/// Per-weapon grip offsets live on <see cref="MeleeWeapon"/> on this object or children.
/// </summary>
public sealed class PickableItem : Component
{
	[Property] public string DisplayName { get; set; } = "Item";

	/// <summary>Optional tag so traces can filter pickup layers.</summary>
	[Property] public string PickupTag { get; set; } = "pickup";

	/// <summary>Weapon classification for UI / inventory (non-weapons stay <see cref="WeaponType.None"/>).</summary>
	[Property] public WeaponType ItemWeaponType { get; set; } = WeaponType.None;

	/// <summary>
	/// If true, finds a <see cref="Rigidbody"/> on this object or children and disables motion and gravity until
	/// <see cref="PlayerItemPickup"/> picks it up (then drop restores the saved motion state).
	/// </summary>
	[Property] public bool StaticUntilPickedUp { get; set; } = true;

	protected override void OnEnabled()
	{
		if ( !StaticUntilPickedUp )
			return;

		var rb = FindRigidbodyInHierarchy( GameObject );
		if ( rb is null || !rb.IsValid() )
			return;

		rb.Gravity = false;
		rb.MotionEnabled = false;
		rb.Velocity = Vector3.Zero;
		rb.AngularVelocity = Vector3.Zero;
	}

	private static Rigidbody FindRigidbodyInHierarchy( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return null;

		var rb = root.Components.Get<Rigidbody>();
		if ( rb is not null )
			return rb;

		foreach ( var child in root.Children )
		{
			var found = FindRigidbodyInHierarchy( child );
			if ( found is not null )
				return found;
		}

		return null;
	}
}
