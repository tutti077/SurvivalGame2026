using System.Diagnostics;

namespace Survival;

/// <summary>
/// Budgeted scatter spawning (Mark 2026-10-13: trees popping in must not stutter the scene — wingsuits will
/// cross the map at 120 mph). A chunk's scatter is PLANNED when the chunk loads (cheap, deterministic:
/// positions, prefab, rotation, scale, identity key) and the prefab clones — the expensive part — are
/// spawned here, a few per frame under a time budget, nearest chunk first. Colliders / shadow flags are
/// collected once a chunk's plans have all landed.
///
/// Hand-over from the far ring (2026-10-15, flash probe): the real trees are cloned VISIBLE over the far ones,
/// and the far ring is told only when the chunk's LAST clone has landed; the far trees then linger until the
/// engine applies the rebuilt tile (1-4 frames, more under load). At the ring's edge (512 m) the real tree is
/// already its LOD3 — the same mesh and transform as the far one — so the overlap is invisible, while any gap is
/// a flash. Telling the ring at the FIRST clone still flashed: the chunk's other trees are cloned over the
/// following frames under the per-frame cap and lost their far stand-ins before they stood. Every scheme that
/// tried to time a reveal to the engine's tile apply (hidden clones, frame slack) guessed wrong under load.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Vegetation" ), Title( "Scatter Spawn Budget (ms)" ), Range( 0.5f, 8f ), Step( 0.5f ), Description( "Main-thread time per frame spent cloning tree / rock / stick prefabs. Chunks keep their terrain and get their scatter over the following frames." )]
	public float VegetationSpawnBudgetMs { get; set; } = 2f;

	[Property, Group( "Vegetation" ), Title( "Scatter Spawns Per Frame" ), Range( 1, 64 ), Step( 1 ), Description( "Hard cap on prefab clones per frame regardless of the budget." )]
	public int VegetationSpawnsPerFrame { get; set; } = 16;

	readonly List<LoadedChunk> _spawnPending = new();

	/// <summary>Tree colliders waiting to come online (chunk, collider): a few per frame, each one builds a physics shape.</summary>
	readonly List<(LoadedChunk Chunk, ModelCollider Collider)> _vegColliderEnableQueue = new();
	const int VegCollidersEnabledPerFrame = 2;

	/// <summary>Enable a few queued tree colliders; a chunk that went out of range again while waiting keeps them off.</summary>
	void TickVegetationColliderEnables()
	{
		var done = 0;
		while ( _vegColliderEnableQueue.Count > 0 && done < VegCollidersEnabledPerFrame )
		{
			var (chunk, col) = _vegColliderEnableQueue[^1];
			_vegColliderEnableQueue.RemoveAt( _vegColliderEnableQueue.Count - 1 );
			if ( col is null || !col.IsValid() || chunk.Collider is null || !chunk.Collider.IsValid() || !chunk.Collider.Enabled )
				continue;
			if ( !col.Enabled )
			{
				col.Enabled = true;
				done++;
			}
		}
	}

	/// <summary>Hand a chunk's planned scatter to the per-frame spawner; a chunk with nothing to spawn is finalised at once.</summary>
	void QueueVegetationSpawns( LoadedChunk entry )
	{
		if ( entry.PendingSpawns.Count == 0 )
		{
			if ( entry.HasLargeTrees )
				TryBeginFarHandover( entry );   // nothing to clone, but the ring must still drop the chunk
			CollectVegetationColliders( entry );
			return;
		}

		if ( !_spawnPending.Contains( entry ) )
			_spawnPending.Add( entry );
	}

	void TickVegetationSpawns()
	{
		Stats.FrameIndex = _frameIndex;
		Stats.SpawnPendingChunks = _spawnPending.Count;
		Stats.SpawnPendingPlans = 0;
		Stats.SpawnGateBlocked = 0;
		foreach ( var pending in _spawnPending )
			Stats.SpawnPendingPlans += pending.PendingSpawns.Count;
		if ( _spawnPending.Count == 0 )
			return;

		if ( !TryGetStreamTransform( out var streamPosEngine, out _ ) )
			return;

		var streamPos = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = BuildGenerationSettings().TotalWorldRadiusMeters;

		// Nearest chunk first: the trees around the player land before the ones at the edge of the ring.
		if ( _spawnPending.Count > 1 )
		{
			_spawnPending.Sort( ( a, b ) =>
				ChunkDistanceMeters( a.Coord, streamPos, worldRadius, chunkSize )
					.CompareTo( ChunkDistanceMeters( b.Coord, streamPos, worldRadius, chunkSize ) ) );
		}

		var budgetMs = Math.Clamp( VegetationSpawnBudgetMs, 0.5f, 8f );
		var maxSpawns = Math.Clamp( VegetationSpawnsPerFrame, 1, 64 );
		var stopwatch = Stopwatch.StartNew();
		var spawned = 0;

		var index = 0;
		while ( index < _spawnPending.Count && spawned < maxSpawns && stopwatch.Elapsed.TotalMilliseconds < budgetMs )
		{
			var entry = _spawnPending[index];
			if ( entry.GameObject is null || !entry.GameObject.IsValid() || !_loaded.ContainsKey( entry.Coord ) )
			{
				// Unloaded while waiting: nothing to spawn any more.
				entry.PendingSpawns.Clear();
				_spawnPending.RemoveAt( index );
				continue;
			}

			var plans = entry.PendingSpawns;
			while ( plans.Count > 0 && spawned < maxSpawns && stopwatch.Elapsed.TotalMilliseconds < budgetMs )
			{
				var plan = plans[^1];
				plans.RemoveAt( plans.Count - 1 );
				var before = stopwatch.Elapsed.TotalMilliseconds;
				var instance = TerrainVegetationScatter.SpawnPlanned( entry.GameObject, plan, entry.Collider is not null && entry.Collider.IsValid() && entry.Collider.Enabled );
				if ( instance is not null && instance.IsValid() )
				{
					if ( plan.Sapling )
						entry.Saplings.Add( new SaplingState { Go = instance, PosMeters = TerrainWorldUnits.EngineToMeters( instance.WorldPosition ), Renderers = instance.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ).ToArray() } );
					else if ( plan.Kind != "tree" )
						BeginSmallFadeIn( instance );   // rocks, sticks: no LODs, so they may fade in (Mark 2026-10-20)
				}
				var oneMs = (float)(stopwatch.Elapsed.TotalMilliseconds - before);
				if ( oneMs > Stats.SpawnOneMs )
				{
					Stats.SpawnOneMs = oneMs;
					Stats.SpawnOneName = plan.Name;
				}
				spawned++;
				Stats.ScatterSpawns++;
			}

			if ( plans.Count == 0 )
			{
				_spawnPending.RemoveAt( index );
				CollectVegetationColliders( entry );
				if ( entry.HasLargeTrees )
					TryBeginFarHandover( entry );   // every real tree stands: the far ring may drop the chunk now
			}
		}
		Stats.SpawnGateBlocked = _unloadingChunks.Count;
	}
}
