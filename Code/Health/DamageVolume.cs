using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Game;

/// <summary>
/// Put on the same <see cref="GameObject"/> as a trigger <see cref="Collider"/> (IsTrigger = true), or a parent of trigger colliders.
/// On host, removes health from <see cref="PlayerHealth"/> on the object that entered. Optional per-target cooldown.
/// If <see cref="DamageTags"/> is empty, any player in the volume can be damaged. If non-empty, the other object must have
/// <b>at least one</b> of those tags on itself or an ancestor.
/// </summary>
[Title( "Damage Volume" )]
[Category( "Health" )]
public sealed class DamageVolume : Component
{
	[Property] public float Damage { get; set; } = 10f;

	[Property] public float CooldownSeconds { get; set; } = 0.35f;

	/// <summary>
	/// If empty, any <see cref="PlayerHealth"/> hit is damaged. Otherwise the entering hierarchy must include at least one of these tags.
	/// </summary>
	[Property] public List<string> DamageTags { get; set; } = new() { "player" };

	private readonly Dictionary<GameObject, TimeSince> _lastDamage = new();

	protected override void OnEnabled()
	{
		BindTriggersRecursive( GameObject, subscribe: true );
	}

	protected override void OnDisabled()
	{
		BindTriggersRecursive( GameObject, subscribe: false );
	}

	private void BindTriggersRecursive( GameObject go, bool subscribe )
	{
		if ( go is null || !go.IsValid() )
			return;

		foreach ( var col in go.Components.GetAll<Collider>() )
		{
			if ( col is null || !col.IsValid() )
				continue;

			if ( subscribe )
			{
				col.OnTriggerEnter += OnColliderTriggerEnter;
				col.OnTriggerExit += OnColliderTriggerExit;
			}
			else
			{
				col.OnTriggerEnter -= OnColliderTriggerEnter;
				col.OnTriggerExit -= OnColliderTriggerExit;
			}
		}

		foreach ( var child in go.Children )
			BindTriggersRecursive( child, subscribe );
	}

	private void OnColliderTriggerEnter( Collider other )
	{
		if ( Networking.IsActive && !Networking.IsHost )
			return;

		ApplyDamageIfAllowed( other?.GameObject );
	}

	private void OnColliderTriggerExit( Collider other )
	{
		var root = FindDamageRoot( other?.GameObject );
		if ( root is not null && root.IsValid() )
			_lastDamage.Remove( root );
	}

	private void ApplyDamageIfAllowed( GameObject other )
	{
		if ( other is null || !other.IsValid() )
			return;

		if ( DamageTags is not null && DamageTags.Count > 0 && !HasAnyTagInHierarchy( other, DamageTags ) )
			return;

		var root = FindDamageRoot( other );
		if ( root is null || !root.IsValid() )
			return;

		if ( CooldownSeconds > 0f && _lastDamage.TryGetValue( root, out var since ) && since < CooldownSeconds )
			return;

		var health = FindPlayerHealthInHierarchy( root );
		if ( health is null || !health.IsValid() )
			return;

		health.RemoveHealth( Damage );
		if ( CooldownSeconds > 0f )
			_lastDamage[root] = 0f;
	}

	private static bool HasAnyTagInHierarchy( GameObject obj, IEnumerable<string> tags )
	{
		if ( tags is null )
			return true;

		var set = tags.Where( t => !string.IsNullOrWhiteSpace( t ) ).Select( t => t.Trim() ).ToHashSet();
		if ( set.Count == 0 )
			return true;

		for ( var go = obj; go is not null; go = go.Parent )
		{
			foreach ( var t in set )
			{
				if ( go.Tags.Has( t ) )
					return true;
			}
		}

		return false;
	}

	private static GameObject FindDamageRoot( GameObject hit )
	{
		for ( var go = hit; go is not null; go = go.Parent )
		{
			if ( go.Components.Get<PlayerController>() is not null )
				return go;
		}

		return null;
	}

	private static PlayerHealth FindPlayerHealthInHierarchy( GameObject root )
	{
		for ( var go = root; go is not null; go = go.Parent )
		{
			var h = go.Components.Get<PlayerHealth>();
			if ( h is not null )
				return h;
		}

		return null;
	}
}
