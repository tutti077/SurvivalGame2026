using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-side lifecycle for an arrow pickup embedded in something that can move or die
/// (entity, player, tree, build piece). While attached the pickup is a child of the hit
/// object so it rides along; when the victim dies the arrow detaches and falls with physics.
/// Arrows stuck in static world (terrain, rocks) never get this component.
/// </summary>
[Title( "Stuck Arrow" )]
public sealed class StuckArrow : Component
{
	const double AlivePollIntervalSeconds = 0.25;

	DamageReceiver _receiver;
	EntityVitals _entityVitals;
	float _shaftLength;
	float _thickness;
	bool _released;
	double _nextAliveCheckAt;

	bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Host: parent this pickup to <paramref name="hitObject"/> (world pose kept) and watch the victim.</summary>
	public void HostAttach( GameObject hitObject, DamageReceiver receiver, float shaftLength, float thickness )
	{
		_receiver = receiver;
		_shaftLength = shaftLength;
		_thickness = thickness;
		_released = false;

		if ( hitObject.IsValid() )
			GameObject.SetParent( hitObject, true );

		_entityVitals = CombatAuthority.ResolveEntityVitalsForDamageReceiver( receiver );
		if ( _entityVitals is not null )
			_entityVitals.OnDied += OnVictimDied;

		_nextAliveCheckAt = Time.NowDouble + AlivePollIntervalSeconds;

		if ( receiver is null || !receiver.IsValid() || !CombatAuthority.IsDamageVictimAlive( receiver ) )
			Release();
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( _released || !IsHostAuthority )
			return;

		// Entities fire OnDied for the fast path; everything is also polled a few times a second
		// so an arrow attached to something already dead still comes loose.
		if ( Time.NowDouble < _nextAliveCheckAt )
			return;

		_nextAliveCheckAt = Time.NowDouble + AlivePollIntervalSeconds;

		if ( _receiver is null || !_receiver.IsValid() || !CombatAuthority.IsDamageVictimAlive( _receiver ) )
			Release();
	}

	void OnVictimDied() => Release();

	/// <summary>Detach from the victim and let the arrow fall.</summary>
	public void Release()
	{
		if ( _released )
			return;

		_released = true;
		Unsubscribe();

		if ( !GameObject.IsValid() )
			return;

		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene.IsValid() && GameObject.Parent != scene )
			GameObject.SetParent( scene, true );

		ArrowProjectile.ApplyLoosePhysics( GameObject, _shaftLength, _thickness );
	}

	protected override void OnDestroy()
	{
		// Victim object removed outright (build piece torn down, entity despawned) while we
		// were still riding it — Destroy() takes the children too, so re-spawn a loose arrow.
		var respawn = !_released
		              && IsHostAuthority
		              && GameObject.IsValid()
		              && GameObject.Parent.IsValid()
		              && GameObject.Parent != Scene
		              && Components.Get<WorldDroppedResource>() is { Count: > 0 }
		              && Scene.IsValid();

		Unsubscribe();

		if ( !respawn )
		{
			base.OnDestroy();
			return;
		}

		_released = true;
		var pickup = Components.Get<WorldDroppedResource>();
		var loose = ArrowProjectile.HostSpawnStuckPickup(
			Scene,
			WorldPosition,
			WorldRotation,
			pickup.ResourceId,
			_shaftLength,
			hitObject: null,
			receiver: null );
		if ( loose.IsValid() )
			ArrowProjectile.ApplyLoosePhysics( loose, _shaftLength, _thickness );

		base.OnDestroy();
	}

	void Unsubscribe()
	{
		if ( _entityVitals is not null )
			_entityVitals.OnDied -= OnVictimDied;
		_entityVitals = null;
	}
}
