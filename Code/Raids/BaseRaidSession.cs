using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>What happened to the raid, for the banner every machine shows.</summary>
public enum BaseRaidEvent
{
	Started = 0,
	/// <summary>Every raider is dead.</summary>
	Defeated = 1,
	/// <summary>Every bed in the ring was destroyed; the surviving raiders left.</summary>
	BedsDestroyed = 2,
	/// <summary>Called off with the raid key.</summary>
	Cancelled = 3,
}

/// <summary>
/// Base raid event (one per scene, on the raid object — the whole system lives here). The raid key
/// (L by default) starts <see cref="RaidId"/> from <c>data/raids.json</c> on the presser's claimed
/// <see cref="BuildBed"/>, else the bed nearest to them; pressing it again calls the raid off.
/// Every bed within the base radius of that bed is a target. Host: spawns the first raiders at once
/// and a wave every interval on a ring out from the base, then ends the raid when every raider is
/// dead (defeated) or every bed in the ring is gone (the survivors wander where they are and despawn later, out of sight). Everyone: a red ring
/// on the ground around the base while the raid runs, the minimap ring
/// (<see cref="TerrainWorldMapFace"/>) and the banner / status line (<see cref="BaseRaidHud"/>)
/// read the <c>[Sync]</c> state here.
/// </summary>
[Title( "Base Raid Session" )]
public sealed class BaseRaidSession : Component
{
	public static BaseRaidSession Instance { get; private set; }

	/// <summary>Every machine: a raid started / ended — the HUD turns it into a banner.</summary>
	public static event Action<BaseRaidEvent> RaidEventRaised;

	/// <summary>This machine only: why the raid key did nothing (no bed placed, …).</summary>
	public static event Action<string> LocalNoticeRaised;

	[Property, Title( "Raid id (data/raids.json)" )]
	public string RaidId { get; set; } = "scav_raid_test";

	[Property, Title( "Input action" )]
	public string InputAction { get; set; } = "StartBaseRaid";

	[Property, Group( "Debug" ), Title( "Log raid" )]
	public bool LogRaid { get; set; } = false;

	[Sync( SyncFlags.FromHost )] public bool IsRaidActive { get; private set; }
	/// <summary>World position of the raided bed — the ring's center.</summary>
	[Sync( SyncFlags.FromHost )] public Vector3 RaidCenter { get; private set; }
	[Sync( SyncFlags.FromHost )] public float RaidRadiusUnits { get; private set; }
	/// <summary>Raiders alive plus raiders still to come.</summary>
	[Sync( SyncFlags.FromHost )] public int RaidersRemaining { get; private set; }

	/// <summary>Host: a player this close to a raider pulls it off the beds (units).</summary>
	public float AggroRangeUnits { get; private set; }
	/// <summary>Host: a pulled raider whose player is further than this goes back to the beds (units).</summary>
	public float DropAggroRangeUnits { get; private set; }

	/// <summary>Raid status checks run this often — not per frame.</summary>
	const float HostCheckSeconds = 0.5f;
	/// <summary>A straggler further than this from every player is out of sight regardless of view (units — 100 m).</summary>
	const float StragglerSightRangeUnits = 4000f;
	/// <summary>Half-angle of a player's view cone for "is looking at it" (wider than the screen, so the screen edge counts).</summary>
	const float StragglerViewConeHalfDegrees = 60f;
	/// <summary>Per Mark: stragglers stay in the world at least this long before they may vanish (and only while nobody is looking).</summary>
	const float StragglerMinSeconds = 30f;
	/// <summary>Stragglers someone keeps watching are removed after this long anyway.</summary>
	const float StragglerMaxSeconds = 120f;
	const float PlayerEyeHeight = 64f;
	const int SpawnPointAttempts = 10;
	const int RingSegments = 64;
	const float RingLift = 10f;
	const int PostEvery = 4;
	const float PostHeight = 200f;
	const float ProbeUp = 2048f;
	const float ProbeDown = 4096f;

	bool HasHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	// Host state
	BaseRaidData _data;
	readonly List<GameObject> _raiders = new();
	/// <summary>Survivors of an ended raid, wandering — despawned once they have been out a while and no player can see them.</summary>
	readonly List<(GameObject raider, double since)> _stragglers = new();
	double _nextStragglerCheckAt;
	int _spawned;
	double _nextWaveAt;
	double _nextCheckAt;
	/// <summary>First wave still to spawn — held until the nav grown for this raid has finished generating.</summary>
	bool _initialWavePending;

	// Every machine: ground ring sampled once per raid (terrain does not move).
	Vector3[] _ring;
	Vector3 _ringCenter;
	float _ringRadius;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
		base.OnDisabled();
	}

	protected override void OnStart()
	{
		base.OnStart();
		// Object-mode so [Sync] raid state + broadcast RPCs reach joining clients.
		if ( Networking.IsHost )
			HostNetworkSpawn.TrySpawn( GameObject );
	}

	protected override void OnUpdate()
	{
		if ( IsRaidActive )
			DrawGroundRing();

		if ( !HasHostAuthority )
			return;

		if ( !string.IsNullOrWhiteSpace( InputAction ) && Input.Pressed( InputAction ) )
		{
			if ( IsRaidActive )
				HostEndRaid( BaseRaidEvent.Cancelled );
			else
				HostStartRaid( FindLocalPawn() );
		}

		if ( IsRaidActive && Time.NowDouble >= _nextCheckAt )
		{
			_nextCheckAt = Time.NowDouble + HostCheckSeconds;
			HostTickRaid();
		}

		if ( _stragglers.Count > 0 && Time.NowDouble >= _nextStragglerCheckAt )
		{
			_nextStragglerCheckAt = Time.NowDouble + HostCheckSeconds;
			HostTickStragglers();
		}
	}

	/// <summary>Standing beds inside the ring — what the raiders are here for.</summary>
	public BuildBed FindNearestTargetBed( Vector3 from )
	{
		BuildBed best = null;
		var bestDistSq = float.MaxValue;
		foreach ( var bed in BuildBed.All )
		{
			if ( !IsTargetBed( bed ) )
				continue;

			var distSq = (bed.GameObject.WorldPosition - from).LengthSquared;
			if ( distSq >= bestDistSq )
				continue;

			bestDistSq = distSq;
			best = bed;
		}

		return best;
	}

	/// <summary>Is <paramref name="bed"/> one of the beds under raid right now?</summary>
	public bool IsTargetBed( BuildBed bed ) =>
		IsRaidActive && bed is not null && bed.IsStanding
		&& Vector3.DistanceBetween( bed.GameObject.WorldPosition.WithZ( 0f ), RaidCenter.WithZ( 0f ) ) <= RaidRadiusUnits;

	/// <summary>Does any bed under raid belong to <paramref name="pawn"/>? (Banner wording — "your base".)</summary>
	public bool IsRaidingBaseOf( GameObject pawn )
	{
		var key = TimeTrialSession.ResolvePlayerKey( pawn );
		if ( key == default )
			return false;

		foreach ( var bed in BuildBed.All )
		{
			if ( bed.OwnerPlayerId == key && IsTargetBed( bed ) )
				return true;
		}

		return false;
	}

	// ------------------------------------------------------------------
	// Host
	// ------------------------------------------------------------------

	void HostStartRaid( GameObject presser )
	{
		BaseRaidCatalog.Reload();
		if ( !BaseRaidCatalog.TryGet( RaidId, out var data ) )
		{
			Log.Warning( $"[BaseRaid] Unknown raid '{RaidId}'." );
			return;
		}

		BuildBed bed = null;
		if ( presser.IsValid() && !BuildBed.TryFindClaimedBy( presser, out bed ) )
			bed = BuildBed.FindNearest( presser.WorldPosition );
		bed ??= BuildBed.FindNearest( GameObject.WorldPosition );

		if ( bed is null )
		{
			Log.Warning( "[BaseRaid] No bed to raid — place a bed first." );
			LocalNoticeRaised?.Invoke( "Place a bed to raid" );
			return;
		}

		_data = data;
		_raiders.Clear();
		_spawned = 0;

		RaidCenter = bed.GameObject.WorldPosition;
		RaidRadiusUnits = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, data.BaseRadiusMeters ) );
		AggroRangeUnits = TerrainWorldUnits.MetersToEngine( Math.Max( 0f, data.AggroRangeMeters ) );
		DropAggroRangeUnits = Math.Max( AggroRangeUnits, TerrainWorldUnits.MetersToEngine( Math.Max( 0f, data.DropAggroRangeMeters ) ) );
		IsRaidActive = true;

		BuildNavMeshSync.EnsureBuildTraversalSettings( Scene );
		// Hand-built scenes only mesh a bubble around the first pawn — the base and the whole spawn
		// ring (plus a margin for the leaving run-off) must be on nav or raiders cannot path in.
		BuildNavMeshSync.EnsureNavCoversArea( Scene, RaidCenter,
			TerrainWorldUnits.MetersToEngine( Math.Max( data.SpawnMaxDistanceMeters, data.BaseRadiusMeters ) + 20f ) );
		// Per Mark: nav first, then the raiders, so they run in seamlessly. If the grow left the mesh
		// generating, the first wave waits for it (checked every 0.5 s in HostTickRaid).
		_initialWavePending = true;
		_nextWaveAt = Time.NowDouble + Math.Max( 1f, data.WaveIntervalSeconds );
		_nextCheckAt = Time.NowDouble;
		RaidersRemaining = data.TotalEnemies;
		HostTrySpawnInitialWave();

		if ( LogRaid )
			Log.Info( $"[BaseRaid] '{data.Id}' on {bed.GameObject.Name} ({(string.IsNullOrWhiteSpace( bed.OwnerName ) ? "unclaimed" : bed.OwnerName)}) at {RaidCenter}: {_spawned}/{data.TotalEnemies} raiders in{(_initialWavePending ? " (first wave waiting for nav)" : string.Empty)}, aggro {data.AggroRangeMeters} m / drop {data.DropAggroRangeMeters} m." );

		RpcBroadcastRaidEvent( BaseRaidEvent.Started );
	}

	/// <summary>First wave once the mesh is not generating — the raiders never spawn onto tiles still being built.</summary>
	void HostTrySpawnInitialWave()
	{
		if ( !_initialWavePending || BuildNavMeshSync.IsNavGenerating( Scene ) )
			return;

		_initialWavePending = false;
		HostSpawnRaiders( Math.Min( _data.InitialEnemies, _data.TotalEnemies ) );
		_nextWaveAt = Time.NowDouble + Math.Max( 1f, _data.WaveIntervalSeconds );
		if ( LogRaid )
			Log.Info( $"[BaseRaid] First wave in — {_spawned}/{_data.TotalEnemies} spawned." );
	}

	void HostTickRaid()
	{
		HostTrySpawnInitialWave();
		_raiders.RemoveAll( r => r is null || !r.IsValid() );

		if ( FindNearestTargetBed( RaidCenter ) is null )
		{
			HostEndRaid( BaseRaidEvent.BedsDestroyed );
			return;
		}

		if ( !_initialWavePending && _spawned < _data.TotalEnemies && Time.NowDouble >= _nextWaveAt )
		{
			_nextWaveAt = Time.NowDouble + Math.Max( 1f, _data.WaveIntervalSeconds );
			HostSpawnRaiders( Math.Min( Math.Max( 1, _data.WaveSize ), _data.TotalEnemies - _spawned ) );
			if ( LogRaid )
				Log.Info( $"[BaseRaid] Wave in — {_spawned}/{_data.TotalEnemies} spawned, {_raiders.Count} alive." );
		}

		RaidersRemaining = _raiders.Count + (_data.TotalEnemies - _spawned);
		if ( RaidersRemaining <= 0 )
			HostEndRaid( BaseRaidEvent.Defeated );
	}

	void HostEndRaid( BaseRaidEvent outcome )
	{
		if ( !IsRaidActive )
			return;

		IsRaidActive = false;
		RaidersRemaining = 0;
		_initialWavePending = false;
		// The dirty areas the raid left behind keep trickling out (one every few seconds, see
		// BuildNavMeshSync.TickDeferredRemovalBakes) — flushing them all here was part of the hitch
		// the moment the bed fell.

		// Beds gone / called off: the survivors settle into wander where they stand and vanish later, once nobody is looking.
		var now = Time.NowDouble;
		foreach ( var raider in _raiders )
		{
			if ( !raider.IsValid() )
				continue;

			var brain = raider.Components.Get<EntityBrain>();
			if ( brain is null )
			{
				raider.Destroy();
				continue;
			}

			brain.EndRaid();
			_stragglers.Add( (raider, now) );
		}

		_raiders.Clear();

		if ( LogRaid )
			Log.Info( $"[BaseRaid] Raid over: {outcome}." );

		RpcBroadcastRaidEvent( outcome );
	}

	void HostSpawnRaiders( int count )
	{
		for ( var i = 0; i < count; i++ )
		{
			// Counted even when a spawn fails, so a bad spot can never stall the raid.
			_spawned++;
			if ( TryPickRaider( out var enemy ) && TryPickSpawnPoint( out var position ) )
				TrySpawnRaider( enemy, position );
		}
	}

	bool TryPickRaider( out BaseRaidEnemyData enemy )
	{
		enemy = null;
		var total = 0f;
		foreach ( var entry in _data.Enemies )
		{
			if ( entry is not null && entry.Weight > 0f )
				total += entry.Weight;
		}

		if ( total <= 0f )
		{
			Log.Warning( $"[BaseRaid] Raid '{_data.Id}' has no enemies with weight > 0." );
			return false;
		}

		var roll = Sandbox.Game.Random.Float( 0f, total );
		foreach ( var entry in _data.Enemies )
		{
			if ( entry is null || entry.Weight <= 0f )
				continue;

			enemy = entry;
			roll -= entry.Weight;
			if ( roll <= 0f )
				break;
		}

		return enemy is not null;
	}

	/// <summary>Standable ground on the spawn ring; a few random tries so a cliff or the map edge is skipped.</summary>
	bool TryPickSpawnPoint( out Vector3 position )
	{
		position = default;
		var min = TerrainWorldUnits.MetersToEngine( Math.Max( 1f, _data.SpawnMinDistanceMeters ) );
		var max = Math.Max( min, TerrainWorldUnits.MetersToEngine( _data.SpawnMaxDistanceMeters ) );

		for ( var attempt = 0; attempt < SpawnPointAttempts; attempt++ )
		{
			var dir = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ) * Vector3.Forward;
			var flat = RaidCenter + dir * Sandbox.Game.Random.Float( min, max );
			var tr = Scene.Trace.Ray( flat.WithZ( RaidCenter.z + ProbeUp ), flat.WithZ( RaidCenter.z - ProbeDown ) )
				.Radius( 8f )
				.UsePhysicsWorld()
				.WithoutTags( "player", "enemy", "buildpreview", ShelterProbe.BuildPieceTag, "water" )
				.Run();

			if ( !tr.Hit || tr.Normal.z < 0.35f )
				continue;

			position = tr.HitPosition;
			return true;
		}

		Log.Warning( $"[BaseRaid] No standable ground {min:0}–{max:0}u around {RaidCenter} — raider skipped." );
		return false;
	}

	void TrySpawnRaider( BaseRaidEnemyData enemy, Vector3 position )
	{
		var enemyType = enemy.ResolveEnemyType();
		var instance = BuildPrefabUtility.GetTemplate( enemy.Prefab )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BaseRaid] Failed to clone prefab '{enemy.Prefab}' for {enemyType}." );
			return;
		}

		instance.Parent = Scene;
		instance.WorldPosition = position;
		instance.WorldRotation = Rotation.LookAt( (RaidCenter - position).WithZ( 0f ), Vector3.Up );

		EntityEnemySetup.Configure( instance, enemyType, enemy.Tier, enemy.Health );
		instance.Components.Get<EntityBrain>()?.JoinRaid( this );

		// Clone alone is host-local — remotes never see the entity without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[BaseRaid] NetworkSpawn failed for '{enemy.Prefab}' — destroying local clone." );
			instance.Destroy();
			return;
		}

		_raiders.Add( instance );
	}

	/// <summary>Despawn stragglers nobody can see (or that have been out too long).</summary>
	void HostTickStragglers()
	{
		var now = Time.NowDouble;
		for ( var i = _stragglers.Count - 1; i >= 0; i-- )
		{
			var (raider, since) = _stragglers[i];
			if ( !raider.IsValid() )
			{
				_stragglers.RemoveAt( i );
				continue;
			}

			var running = now - since;
			if ( running < StragglerMinSeconds )
				continue;

			if ( running < StragglerMaxSeconds && IsSeenByAnyPlayer( raider ) )
				continue;

			raider.Destroy();
			_stragglers.RemoveAt( i );
		}
	}

	/// <summary>
	/// Is <paramref name="raider"/> in any live player's view cone, within sight range, with a clear
	/// line? Host-side eye-aim test (the host has no camera for remote players), like the animals' stare check.
	/// </summary>
	bool IsSeenByAnyPlayer( GameObject raider )
	{
		var target = raider.WorldPosition + Vector3.Up * 40f;
		var coneCos = MathF.Cos( StragglerViewConeHalfDegrees * (MathF.PI / 180f) );
		foreach ( var vitals in Scene.GetAllComponents<PlayerVitals>() )
		{
			var pawn = vitals?.GameObject;
			if ( pawn is null || !pawn.IsValid() || vitals.CurrentHealth <= 0.001f
			     || pawn.Components.Get<EntityBrain>() is not null )
				continue;

			var controller = pawn.Components.Get<PlayerController>();
			if ( controller is null )
				continue;

			var eye = pawn.WorldPosition + Vector3.Up * PlayerEyeHeight;
			var toRaider = target - eye;
			var dist = toRaider.Length;
			if ( dist > StragglerSightRangeUnits )
				continue;

			if ( dist > 1f && Vector3.Dot( controller.EyeAngles.ToRotation().Forward, toRaider / dist ) < coneCos )
				continue;

			if ( EntitySight.HasClearLos( Scene, target, raider, pawn, PlayerEyeHeight ) )
				return true;
		}

		return false;
	}

	/// <summary>The host's own pawn — the one whose keyboard pressed the raid key.</summary>
	GameObject FindLocalPawn()
	{
		foreach ( var vitals in Scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals?.GameObject is { IsValid: true } root && !root.IsProxy
			     && root.Components.Get<EntityBrain>() is null )
				return root;
		}

		return null;
	}

	[Rpc.Broadcast]
	void RpcBroadcastRaidEvent( BaseRaidEvent raidEvent )
	{
		RaidEventRaised?.Invoke( raidEvent );
	}

	// ------------------------------------------------------------------
	// Ground ring (every machine)
	// ------------------------------------------------------------------

	void DrawGroundRing()
	{
		if ( _ring is null || _ringCenter != RaidCenter || MathF.Abs( _ringRadius - RaidRadiusUnits ) > 0.5f )
		{
			_ringCenter = RaidCenter;
			_ringRadius = RaidRadiusUnits;
			_ring = BuildGroundRing( RaidCenter, RaidRadiusUnits );
		}

		if ( _ring.Length < 2 )
			return;

		var color = new Color( 0.95f, 0.12f, 0.08f );
		var postColor = color.WithAlpha( 0.45f );
		for ( var i = 0; i < _ring.Length; i++ )
		{
			var a = _ring[i];
			var b = _ring[(i + 1) % _ring.Length];
			DebugOverlay.Line( a, b, color, 0f );

			if ( i % PostEvery == 0 )
				DebugOverlay.Line( a, a + Vector3.Up * PostHeight, postColor, 0f );
		}
	}

	Vector3[] BuildGroundRing( Vector3 center, float radius )
	{
		if ( radius <= 0f || !Scene.IsValid() )
			return Array.Empty<Vector3>();

		var points = new Vector3[RingSegments];
		for ( var i = 0; i < RingSegments; i++ )
		{
			var flat = center + Rotation.FromYaw( i * (360f / RingSegments) ) * new Vector3( radius, 0f, 0f );
			var tr = Scene.Trace.Ray( flat.WithZ( center.z + ProbeUp ), flat.WithZ( center.z - ProbeDown ) )
				.UsePhysicsWorld()
				.WithoutTags( "player", "enemy", "buildpreview", ShelterProbe.BuildPieceTag )
				.Run();
			points[i] = (tr.Hit ? tr.HitPosition : flat) + Vector3.Up * RingLift;
		}

		return points;
	}
}
