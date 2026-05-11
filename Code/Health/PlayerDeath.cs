using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox;

namespace Game;

/// <summary>
/// Add on the same player hierarchy as <see cref="EntityHealthFeature"/> (any parent/child of the pawn is fine).
/// <see cref="Health"/> is auto-found if left empty. Listens for death, then (host only, or offline) respawns at the captured start pose.
/// </summary>
[Title( "Player Death" )]
[Category( "Health" )]
public class PlayerDeath : Component
{
	/// <summary>If unset, searches this object, parents, and descendants for <see cref="EntityHealthFeature"/>.</summary>
	[Property] public EntityHealthFeature Health { get; set; }

	[Property] public float RespawnDelaySeconds { get; set; } = 0.5f;

	/// <summary>If set, respawn uses this object&apos;s world transform (updated each death) instead of the captured start pose.</summary>
	[Property] public GameObject RespawnPoint { get; set; }

	private Vector3 _spawnPosition;
	private Rotation _spawnRotation;
	private bool _routineRunning;
	private bool _startPoseCaptured;
	private bool _subscribedToHealth;
	private bool _warnedMissingHealth;

	protected override void OnEnabled()
	{
		TryBindHealthListener();
	}

	protected override void OnDisabled()
	{
		UnbindHealthListener();
	}

	protected override void OnStart()
	{
		TryBindHealthListener();
		_ = CaptureStartingPoseAsync();
	}

	protected override void OnDestroy()
	{
		UnbindHealthListener();
	}

	private void TryBindHealthListener()
	{
		Health ??= FindPlayerHealthOnHierarchy();

		if ( Health is null || !Health.IsValid() )
		{
			if ( !_warnedMissingHealth )
			{
				_warnedMissingHealth = true;
				Log.Warning( $"PlayerDeath on '{GameObject.Name}': no EntityHealthFeature found on this object, parents, or children — assign Health or move the component. Respawn will not run." );
			}

			return;
		}

		if ( _subscribedToHealth )
			return;

		Health.OnDied += OnDied;
		_subscribedToHealth = true;
	}

	private void UnbindHealthListener()
	{
		if ( Health is not null && Health.IsValid() && _subscribedToHealth )
			Health.OnDied -= OnDied;

		_subscribedToHealth = false;
	}

	private EntityHealthFeature FindPlayerHealthOnHierarchy()
	{
		foreach ( var go in EnumerateSelfAndAncestors( GameObject ) )
		{
			var h = go.Components.Get<EntityHealthFeature>();
			if ( h is not null && h.IsValid() )
				return h;
		}

		foreach ( var go in EnumerateDescendants( GameObject ) )
		{
			var h = go.Components.Get<EntityHealthFeature>();
			if ( h is not null && h.IsValid() )
				return h;
		}

		var pc = FindPlayerController();
		if ( pc?.GameObject is not null && pc.GameObject.IsValid() )
		{
			foreach ( var go in EnumerateSelfAndDescendants( pc.GameObject ) )
			{
				var h = go.Components.Get<EntityHealthFeature>();
				if ( h is not null && h.IsValid() )
					return h;
			}
		}

		return null;
	}

	private static IEnumerable<GameObject> EnumerateSelfAndAncestors( GameObject start )
	{
		for ( var go = start; go is not null; go = go.Parent )
			yield return go;
	}

	private static IEnumerable<GameObject> EnumerateDescendants( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		foreach ( var child in root.Children )
		{
			yield return child;
			foreach ( var d in EnumerateDescendants( child ) )
				yield return d;
		}
	}

	private static IEnumerable<GameObject> EnumerateSelfAndDescendants( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		yield return root;

		foreach ( var d in EnumerateDescendants( root ) )
			yield return d;
	}

	private async Task CaptureStartingPoseAsync()
	{
		CaptureSpawnTransform();

		await GameTask.Yield();
		if ( !GameObject.IsValid() )
			return;

		if ( RespawnPoint is null || !RespawnPoint.IsValid() )
			CaptureSpawnTransform();

		_startPoseCaptured = true;
	}

	private void CaptureSpawnTransform()
	{
		if ( RespawnPoint is not null && RespawnPoint.IsValid() )
		{
			_spawnPosition = RespawnPoint.WorldPosition;
			_spawnRotation = RespawnPoint.WorldRotation;
			return;
		}

		var pc = FindPlayerController();
		var body = pc?.Body?.GameObject;
		if ( body is not null && body.IsValid() )
		{
			_spawnPosition = body.WorldPosition;
			_spawnRotation = body.WorldRotation;
		}
		else
		{
			_spawnPosition = GameObject.WorldPosition;
			_spawnRotation = GameObject.WorldRotation;
		}
	}

	private void OnDied()
	{
		OnPlayerDeath();

		if ( !CanRespawnAuthority() )
			return;

		if ( _routineRunning )
			return;

		_ = RespawnRoutineAsync();
	}

	protected virtual void OnPlayerDeath()
	{
	}

	/// <summary>Offline / listen-server host: run respawn here. Pure clients wait for the host to move &amp; heal the pawn via sync.</summary>
	private static bool CanRespawnAuthority()
	{
		if ( !Networking.IsActive )
			return true;

		return Networking.IsHost;
	}

	private async Task RespawnRoutineAsync()
	{
		_routineRunning = true;
		try
		{
			var delay = Math.Max( 0f, RespawnDelaySeconds );
			if ( delay > 0f )
				await GameTask.DelaySeconds( delay );

			if ( !GameObject.IsValid() )
				return;

			Health ??= FindPlayerHealthOnHierarchy();
			if ( Health is null || !Health.IsValid() )
				return;

			if ( !_startPoseCaptured )
				CaptureSpawnTransform();

			if ( RespawnPoint is not null && RespawnPoint.IsValid() )
			{
				_spawnPosition = RespawnPoint.WorldPosition;
				_spawnRotation = RespawnPoint.WorldRotation;
			}

			ApplyRespawnTransform();
			Health.ResetToFull();
		}
		finally
		{
			_routineRunning = false;
		}
	}

	private void ApplyRespawnTransform()
	{
		var pc = FindPlayerController();
		if ( pc is null )
		{
			if ( GameObject.IsValid() )
			{
				GameObject.WorldPosition = _spawnPosition;
				GameObject.WorldRotation = _spawnRotation;
				ZeroRigidbodyVelocities( GameObject );
			}

			return;
		}

		var rootGo = pc.GameObject;
		if ( rootGo is not null && rootGo.IsValid() )
		{
			rootGo.WorldPosition = _spawnPosition;
			rootGo.WorldRotation = _spawnRotation;
			ZeroRigidbodyVelocities( rootGo );
		}

		var bodyGo = pc.Body?.GameObject;
		if ( bodyGo is not null && bodyGo.IsValid() && bodyGo != rootGo )
		{
			bodyGo.WorldPosition = _spawnPosition;
			bodyGo.WorldRotation = _spawnRotation;
			ZeroRigidbodyVelocities( bodyGo );
		}
	}

	private PlayerController FindPlayerController()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return pc;
		}

		return GameObject.Components.Get<PlayerController>();
	}

	private static void ZeroRigidbodyVelocities( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return;

		foreach ( var rb in root.Components.GetAll<Rigidbody>() )
		{
			if ( rb is null || !rb.IsValid() )
				continue;
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}

		foreach ( var child in root.Children )
			ZeroRigidbodyVelocities( child );
	}
}
