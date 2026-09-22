using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Marks an entity as a boss. Authored on the boss prefab next to the usual entity components;
/// <see cref="BossSpawner"/> tunes it from <c>data/bosses.json</c>.
/// Host: owns the form logic — a hit that would empty the pool in a form that has a successor
/// refills the pool to the next form's health instead of killing the boss
/// (<see cref="EntityVitals.LethalHitInterceptor"/>). Mirrors name, form, pool and spawn point to
/// every client through <c>[Sync]</c> so <see cref="BossHealthBarHud"/> can draw the screen-top bar.
/// </summary>
[Title( "Boss Entity" )]
public sealed class BossEntity : Component
{
	/// <summary>First-form bar colour — every boss starts red.</summary>
	public const string FirstFormBarColorHex = "#eb1510";
	const string DefaultSecondFormBarColorHex = "#2ecc40";

	/// <summary>Abandon checks run this often on the host — a boss fight does not need per-frame player scans.</summary>
	const float AbandonCheckInterval = 1f;

	/// <summary>Every enabled boss in the scene. Proxies enable too, so clients can read this list.</summary>
	public static IReadOnlyList<BossEntity> Active => ActiveList;
	static readonly List<BossEntity> ActiveList = new();

	/// <summary>Raised on every machine (host RPC) with the boss's display name when the host despawns an abandoned boss.</summary>
	public static event Action<string> Despawned;

	[Property] public EntityVitals Vitals { get; set; }

	[Sync( SyncFlags.FromHost )] public string BossId { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string DisplayName { get; private set; } = string.Empty;
	/// <summary>1 = first form, 2 = second form.</summary>
	[Sync( SyncFlags.FromHost )] public int Form { get; private set; } = 1;
	[Sync( SyncFlags.FromHost )] public float CurrentHealth { get; private set; }
	[Sync( SyncFlags.FromHost )] public float MaxHealth { get; private set; }
	/// <summary>Where the boss stood when it appeared — the screen-top bar range is measured from here.</summary>
	[Sync( SyncFlags.FromHost )] public Vector3 SpawnPosition { get; private set; }
	/// <summary>Screen-top bar range in world units (converted once from <see cref="BossData.HealthBarRangeMeters"/>).</summary>
	[Sync( SyncFlags.FromHost )] public float HealthBarRange { get; private set; }
	/// <summary>Bar fill colour for the current form.</summary>
	[Sync( SyncFlags.FromHost )] public string BarColorHex { get; private set; } = FirstFormBarColorHex;
	[Sync( SyncFlags.FromHost )] public bool IsDefeated { get; private set; }

	BossData _data;
	/// <summary>True while our <see cref="OnLethalHit"/> is installed on the vitals (the whitelist forbids reading Delegate.Target to check).</summary>
	bool _interceptorBound;
	double _nextAbandonCheck;
	/// <summary>When the last in-range live player left; negative while someone is in range.</summary>
	double _abandonedSince = -1;
	bool _despawning;

	protected override void OnEnabled()
	{
		if ( !ActiveList.Contains( this ) )
			ActiveList.Add( this );
	}

	protected override void OnDisabled() => ActiveList.Remove( this );

	protected override void OnDestroy()
	{
		ActiveList.Remove( this );
		if ( Vitals is null )
			return;

		Vitals.OnVitalsChanged -= MirrorPool;
		Vitals.OnDied -= OnDied;
		if ( _interceptorBound )
		{
			_interceptorBound = false;
			Vitals.LethalHitInterceptor = null;
		}
	}

	/// <summary>Host: bind to the boss row after <see cref="EntityEnemySetup.Configure"/> has tuned the entity.</summary>
	public void HostConfigure( BossData data, Vector3 spawnPosition )
	{
		Vitals ??= Components.Get<EntityVitals>();
		_data = data;

		BossId = data.Id;
		DisplayName = data.DisplayName;
		Form = 1;
		SpawnPosition = spawnPosition;
		HealthBarRange = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, data.HealthBarRangeMeters ) );
		BarColorHex = FirstFormBarColorHex;
		IsDefeated = false;

		if ( Vitals is null )
		{
			Log.Warning( $"[BossEntity] '{GameObject.Name}' has no EntityVitals — add it to the boss prefab." );
			return;
		}

		Vitals.DisplayNameOverride = data.DisplayName;
		Vitals.MaxHealth = Math.Max( 1f, data.Health );
		Vitals.ResetToFull();

		Vitals.LethalHitInterceptor = OnLethalHit;
		_interceptorBound = true;
		Vitals.OnVitalsChanged += MirrorPool;
		Vitals.OnDied += OnDied;
		MirrorPool();
	}

	void MirrorPool()
	{
		if ( Vitals is null )
			return;

		CurrentHealth = Vitals.CurrentHealth;
		MaxHealth = Vitals.CurrentHealthMax;
	}

	/// <summary>Host: the first form's pool just hit 0. Refill into the second form when the row has one.</summary>
	bool OnLethalHit( Component attacker )
	{
		if ( _data is null || Vitals is null || Form != 1 || !_data.HasSecondForm || _data.SecondFormHealth <= 0f )
			return false;

		Form = 2;
		BarColorHex = string.IsNullOrWhiteSpace( _data.SecondFormBarColor ) ? DefaultSecondFormBarColorHex : _data.SecondFormBarColor;
		Vitals.MaxHealth = _data.SecondFormHealth;
		Vitals.ResetToFull();

		Log.Info( $"[BossEntity] '{DisplayName}' enters form 2 with {Vitals.CurrentHealthMax:0} HP." );
		return true;
	}

	void OnDied()
	{
		IsDefeated = true;
		MirrorPool();
	}

	/// <summary>Host: despawn once no live player has been inside the bar range for the row's abandon time.</summary>
	protected override void OnUpdate()
	{
		// _data is only ever set on the host — proxies never get here.
		if ( _data is null || _despawning || IsDefeated || _data.AbandonDespawnSeconds <= 0f )
			return;

		if ( GameObject.IsProxy || (GameObject.Network is { Active: true } && !Networking.IsHost) )
			return;

		var now = Time.NowDouble;
		if ( now < _nextAbandonCheck )
			return;

		_nextAbandonCheck = now + AbandonCheckInterval;

		if ( AnyLivePlayerWithinRange() )
		{
			_abandonedSince = -1;
			return;
		}

		if ( _abandonedSince < 0 )
		{
			_abandonedSince = now;
			return;
		}

		if ( now - _abandonedSince < _data.AbandonDespawnSeconds )
			return;

		HostDespawn();
	}

	bool AnyLivePlayerWithinRange()
	{
		var range = HealthBarRange;
		if ( range <= 0f )
			return true;

		var here = GameObject.WorldPosition;
		foreach ( var player in Scene.GetAllComponents<PlayerVitals>() )
		{
			if ( player is null || !player.IsValid() || !player.GameObject.IsValid() || player.CurrentHealth <= 0.001f )
				continue;

			if ( Vector3.DistanceBetween( here, player.GameObject.WorldPosition ) <= range )
				return true;
		}

		return false;
	}

	void HostDespawn()
	{
		_despawning = true;
		Log.Info( $"[BossEntity] '{DisplayName}' abandoned for {_data.AbandonDespawnSeconds:0} s — despawning." );
		// Announce first: the reliable broadcast lands before the destroy that follows it.
		RpcAnnounceDespawn( DisplayName );
		GameObject.Destroy();
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcAnnounceDespawn( string bossName ) => Despawned?.Invoke( bossName );
}
