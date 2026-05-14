using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// Hazard volume: first overlap applies <see cref="DamagePerTick"/> immediately, then the same amount every
/// <see cref="DamageTickIntervalSeconds"/> while a <see cref="PlayerVitals"/> stays inside any trigger
/// <see cref="Collider"/> on this object or its descendants. Uses <see cref="VitalsAuthority.TryApplyDeltas"/> on the host.
/// Polls <see cref="Collider.Touching"/> each frame so overlap works even if <see cref="Component.ITriggerListener"/> does not fire.
/// </summary>
[Title( "Damage Over Time Trap" )]
public sealed class DamageOverTimeTrap : Component, Component.ITriggerListener
{
	[Property] public float DamagePerTick { get; set; } = 5f;

	[Property] public float DamageTickIntervalSeconds { get; set; } = 1f;

	[Property, Group( "Debug" )] public bool LogTrap { get; set; } = true;

	[Property, Group( "Setup" )]
	public bool AutoEnableTriggerOnFirstBoxCollider { get; set; } = true;

	readonly List<Collider> _triggerColliders = new();
	readonly Dictionary<Guid, PlayerVitals> _touching = new();
	readonly HashSet<Guid> _wasInside = new();
	readonly Dictionary<Guid, double> _nextDamageAt = new();

	bool MayApplyDamage =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		if ( AutoEnableTriggerOnFirstBoxCollider )
			TryAutoConfigureTrigger();
		RefreshTriggerColliderList();
		if ( LogTrap && _triggerColliders.Count == 0 )
			Log.Warning( $"[DamageOverTimeTrap] {GameObject.Name}: no trigger colliders found under this object. Add a BoxCollider (etc.), enable Is Trigger, or disable AutoEnableTriggerOnFirstBoxCollider if you use a non-box collider." );
	}

	void TryAutoConfigureTrigger()
	{
		foreach ( var go in SelfAndDescendants( GameObject ) )
		{
			var box = go.Components.Get<BoxCollider>();
			if ( box is null || !box.Enabled )
				continue;
			if ( box.IsTrigger )
				continue;
			box.IsTrigger = true;
			if ( LogTrap )
				Log.Info( $"[DamageOverTimeTrap] {GameObject.Name}: set IsTrigger on BoxCollider '{go.Name}' (was solid)." );
			return;
		}
	}

	static IEnumerable<GameObject> SelfAndDescendants( GameObject root )
	{
		if ( !root.IsValid() )
			yield break;
		yield return root;
		foreach ( var ch in root.Children )
		{
			foreach ( var d in SelfAndDescendants( ch ) )
				yield return d;
		}
	}

	static void TryAddTriggerToList( Collider c, List<Collider> list )
	{
		if ( c is null || !c.Enabled || !c.IsTrigger )
			return;
		if ( !list.Contains( c ) )
			list.Add( c );
	}

	void CollectIntoList( GameObject go )
	{
		if ( !go.IsValid() || !go.Enabled )
			return;
		TryAddTriggerToList( go.Components.Get<BoxCollider>(), _triggerColliders );
		TryAddTriggerToList( go.Components.Get<SphereCollider>(), _triggerColliders );
		TryAddTriggerToList( go.Components.Get<CapsuleCollider>(), _triggerColliders );
		foreach ( var ch in go.Children )
			CollectIntoList( ch );
	}

	void RefreshTriggerColliderList()
	{
		_triggerColliders.Clear();
		CollectIntoList( GameObject );
	}

	public void OnTriggerEnter( Collider other ) => RegisterOverlap( other?.GameObject );

	public void OnTriggerExit( Collider other ) => UnregisterOverlap( other?.GameObject );

	void RegisterOverlap( GameObject start )
	{
		if ( !MayApplyDamage || start is null || !start.IsValid() )
			return;
		if ( !TryFindVitalsRoot( start, out var vitals ) )
			return;
		_touching[vitals.GameObject.Id] = vitals;
	}

	void UnregisterOverlap( GameObject start )
	{
		if ( start is null || !start.IsValid() )
			return;
		if ( !TryFindVitalsRoot( start, out var vitals ) )
			return;
		_touching.Remove( vitals.GameObject.Id );
	}

	protected override void OnUpdate()
	{
		if ( !MayApplyDamage || DamagePerTick <= 0f || !GameObject.IsValid() )
			return;

		var wasInsideSnapshot = new HashSet<Guid>( _wasInside );

		_touching.Clear();
		RefreshTriggerColliderList();

		foreach ( var trigger in _triggerColliders )
		{
			if ( trigger is null || !trigger.Enabled || !trigger.IsTrigger )
				continue;

			foreach ( var other in trigger.Touching )
			{
				if ( other is null || !other.GameObject.IsValid() )
					continue;
				RegisterOverlap( other.GameObject );
			}
		}

		if ( _touching.Count == 0 )
		{
			_wasInside.Clear();
			_nextDamageAt.Clear();
			return;
		}

		foreach ( var id in _nextDamageAt.Keys.ToArray() )
		{
			if ( !_touching.ContainsKey( id ) )
				_nextDamageAt.Remove( id );
		}

		var interval = Math.Max( 0.05, DamageTickIntervalSeconds );
		var deltaHp = -Math.Abs( DamagePerTick );

		foreach ( var id in _touching.Keys.ToArray() )
		{
			if ( !_touching.TryGetValue( id, out var vitals ) || vitals is null || !vitals.GameObject.IsValid() )
			{
				_touching.Remove( id );
				continue;
			}

			var newContact = !wasInsideSnapshot.Contains( id );
			if ( newContact )
			{
				ApplyTrapDamageToVitals( vitals, deltaHp );
				_nextDamageAt[id] = Time.NowDouble + interval;
				continue;
			}

			if ( !_nextDamageAt.TryGetValue( id, out var nextAt ) )
			{
				_nextDamageAt[id] = Time.NowDouble + interval;
				continue;
			}

			if ( Time.NowDouble < nextAt )
				continue;

			ApplyTrapDamageToVitals( vitals, deltaHp );
			_nextDamageAt[id] = Time.NowDouble + interval;
		}

		_wasInside.Clear();
		foreach ( var id in _touching.Keys )
			_wasInside.Add( id );
	}

	/// <summary>Host / offline world damage — must not use <see cref="PlayerVitals.RequestVitalsDelta"/> alone (proxy pawns skip it).</summary>
	void ApplyTrapDamageToVitals( PlayerVitals vitals, float healthDelta )
	{
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var auth = VitalsAuthority.Instance;
		if ( auth is not null )
		{
			if ( auth.TryApplyDeltas( vitals.GameObject, healthDelta, 0f, vitals ) && LogTrap )
				Log.Info( $"[DamageOverTimeTrap] authority Δhp={healthDelta:0.##} on {vitals.GameObject.Name} → HP={vitals.CurrentHealth:0.#}/{vitals.CurrentHealthMax:0.#}" );
			return;
		}

		if ( vitals.GameObject.IsProxy )
			return;

		vitals.RequestVitalsDelta( healthDelta, 0f );
	}

	static bool TryFindVitalsRoot( GameObject start, out PlayerVitals vitals )
	{
		for ( var go = start; go.IsValid(); go = go.Parent )
		{
			vitals = go.Components.Get<PlayerVitals>();
			if ( vitals is not null )
				return true;
		}

		vitals = null;
		return false;
	}
}
