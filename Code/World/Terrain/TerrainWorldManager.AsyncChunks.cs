using System.Threading.Tasks;

namespace Survival;

/// <summary>
/// Off-thread chunk geometry (Mark 2026-10-13: streaming must stay clean at wingsuit speed). The sampling —
/// ~1000 pipeline samples per full chunk, the dominant cost of a chunk load — runs on a worker through
/// <see cref="GameTask.RunInThreadAsync"/>; only the engine side (model, collider, GameObject, scatter
/// planning) happens on the main thread, inside the stream build budget. Nothing blocks: completed tasks
/// are picked up on later frames. A geometry that comes back empty for a chunk that should be inside the
/// world (the failure mode seen on an earlier attempt) is rebuilt synchronously and logged once.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Chunks" ), Title( "Chunk Unloads Per Frame" ), Range( 1, 32 ), Step( 1 ), Description( "Destroying a chunk destroys its trees too; a whole column leaving the ring at once (17 chunks on a 40 km world) used to land in one frame." )]
	public int StreamUnloadsPerFrame { get; set; } = 6;

	[Property, Group( "Chunks" ), Title( "LOD Remeshes Per Frame" ), Range( 1, 8 ), Step( 1 ), Description( "Synchronous LOD remeshes allowed per frame; the rest wait for the next frame." )]
	public int StreamRemeshesPerFrame { get; set; } = 2;

	readonly Queue<TerrainChunkCoord> _unloadQueue = new();
	readonly HashSet<TerrainChunkCoord> _unloadQueued = new();
	bool _staleReloadPending;

	/// <summary>Drain a few queued unloads per frame; a chunk back inside the keep ring is simply kept.</summary>
	void TickChunkUnloads()
	{
		var max = Math.Clamp( StreamUnloadsPerFrame, 1, 32 );
		var done = 0;
		while ( _unloadQueue.Count > 0 && done < max )
		{
			var coord = _unloadQueue.Dequeue();
			_unloadQueued.Remove( coord );
			if ( _streamKeep.Contains( coord ) )
				continue;
			UnloadChunk( coord );
			done++;
		}
	}

	/// <summary>Remesh / scatter passes that hit their per-frame cap run again next frame until nothing is stale.</summary>
	void TickStaleReloads()
	{
		if ( !_staleReloadPending || _isWorldLoading )
			return;
		if ( !TryGetStreamTransform( out var streamPosEngine, out _ ) )
			return;
		var settings = BuildGenerationSettings();
		QueueStaleChunkReloads( TerrainWorldUnits.EngineToMeters( streamPosEngine ), settings, Math.Max( 32f, ChunkSizeMeters ) );
	}

	[Property, Group( "Chunks" ), Title( "Async Chunk Sampling" ), Description( "Sample streamed chunks on worker threads; the main thread only builds the mesh. Off = the old synchronous load (one chunk can cost a whole frame)." )]
	public bool StreamAsyncGeometry { get; set; } = true;

	[Property, Group( "Chunks" ), Title( "Async Chunk Builds In Flight" ), Range( 1, 16 ), Step( 1 ), Description( "How many chunks may be sampling on workers at once." )]
	public int StreamAsyncBuildsInFlight { get; set; } = 6;

	sealed class PendingChunkBuild
	{
		public TerrainChunkCoord Coord;
		public Task<TerrainMeshBuilder.ChunkGeometry> Task;
		public int VerticesPerSide;
		public bool Visible;
		/// <summary>A LOD rebuild of a chunk that is already loaded (apply to its existing GameObject).</summary>
		public bool Remesh;
	}

	readonly Dictionary<TerrainChunkCoord, PendingChunkBuild> _pendingBuilds = new();
	readonly List<TerrainChunkCoord> _pendingScratch = new();
	bool _loggedAsyncFallback;

	bool IsChunkBuildPending( TerrainChunkCoord coord ) => _pendingBuilds.ContainsKey( coord );

	/// <summary>Start sampling a chunk on a worker. False when it is already pending or the in-flight cap is reached.</summary>
	bool TryBeginChunkBuild( TerrainChunkCoord coord, TerrainPreviewSettings settings, Vector3 streamPos, bool visible )
	{
		if ( _pendingBuilds.ContainsKey( coord ) )
			return false;
		if ( _pendingBuilds.Count >= Math.Clamp( StreamAsyncBuildsInFlight, 1, 16 ) )
			return false;

		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var lodDistance = ChunkLodDistanceMeters( coord, streamPos, settings, chunkSize );
		var verticesPerSide = ResolveChunkVerticesPerSide( lodDistance, chunkSize, useStreamLod: true );
		var smoothPasses = verticesPerSide < ChunkVerticesPerSide ? 0 : ChunkHeightSmoothPasses;
		var smoothStrength = ChunkHeightSmoothStrength01;
		var border = ResolveBorderLatticeVerticesPerSide( verticesPerSide );
		var backend = _backend;
		var chunkSizeMeters = ChunkSizeMeters;

		var task = GameTask.RunInThreadAsync( () => TerrainMeshBuilder.BuildChunkGeometry(
			settings, backend, coord, chunkSizeMeters, verticesPerSide, smoothPasses, smoothStrength, border ) );

		_pendingBuilds[coord] = new PendingChunkBuild
		{
			Coord = coord,
			Task = task,
			VerticesPerSide = verticesPerSide,
			Visible = visible,
		};
		return true;
	}

	/// <summary>Start a LOD rebuild of a loaded chunk on a worker. False when pending or the in-flight cap is reached.</summary>
	bool TryBeginChunkRemesh( LoadedChunk entry, TerrainPreviewSettings settings, Vector3 streamPos, int verticesPerSide )
	{
		var coord = entry.Coord;
		if ( _pendingBuilds.ContainsKey( coord ) )
			return false;
		if ( _pendingBuilds.Count >= Math.Clamp( StreamAsyncBuildsInFlight, 1, 16 ) )
			return false;

		var smoothPasses = verticesPerSide < ChunkVerticesPerSide ? 0 : ChunkHeightSmoothPasses;
		var smoothStrength = ChunkHeightSmoothStrength01;
		var border = ResolveBorderLatticeVerticesPerSide( verticesPerSide );
		var backend = _backend;
		var chunkSizeMeters = ChunkSizeMeters;

		var task = GameTask.RunInThreadAsync( () => TerrainMeshBuilder.BuildChunkGeometry(
			settings, backend, coord, chunkSizeMeters, verticesPerSide, smoothPasses, smoothStrength, border ) );

		_pendingBuilds[coord] = new PendingChunkBuild
		{
			Coord = coord,
			Task = task,
			VerticesPerSide = verticesPerSide,
			Visible = true,
			Remesh = true,
		};
		return true;
	}

	/// <summary>
	/// Finish every completed worker build the budget allows (model + GameObject + scatter plan on the main
	/// thread). Returns how many chunks were finished.
	/// </summary>
	int TickPendingChunkBuilds( TerrainPreviewSettings settings, Vector3 streamPos, System.Diagnostics.Stopwatch stopwatch, float budgetMs, int maxChunks )
	{
		if ( _pendingBuilds.Count == 0 )
			return 0;

		var finished = 0;
		_pendingScratch.Clear();
		foreach ( var pair in _pendingBuilds )
		{
			if ( pair.Value.Task.IsCompleted )
				_pendingScratch.Add( pair.Key );
		}

		foreach ( var coord in _pendingScratch )
		{
			if ( finished >= maxChunks || (finished > 0 && stopwatch.Elapsed.TotalMilliseconds >= budgetMs) )
				break;

			var pending = _pendingBuilds[coord];
			_pendingBuilds.Remove( coord );

			if ( pending.Remesh )
			{
				if ( !_loaded.TryGetValue( coord, out var remeshEntry ) || !_streamNeeded.Contains( coord ) )
					continue;   // unloaded while it was sampling
				if ( remeshEntry.VerticesPerSide == pending.VerticesPerSide )
					continue;   // already at that LOD

				TerrainMeshBuilder.ChunkGeometry remeshGeometry = pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
				var chunkSize = Math.Max( 32f, ChunkSizeMeters );
				var distance = ChunkDistanceMeters( coord, streamPos, settings.TotalWorldRadiusMeters, chunkSize );
				if ( remeshGeometry is null || !GeometryLooksSampled( remeshGeometry, settings ) )
				{
					RemeshChunkLod( remeshEntry, settings, streamPos, pending.VerticesPerSide, distance );
					finished++;
					continue;
				}

				var remeshBuilt = TerrainMeshBuilder.BuildModelFromGeometry( remeshGeometry, ChunkSizeMeters, MaxTerrainHeightMeters, withCollision: true, sinkMeters: 0f );
				ApplyChunkRemesh( remeshEntry, remeshBuilt, pending.VerticesPerSide, distance );
				finished++;
				continue;
			}

			if ( !_streamNeeded.Contains( coord ) || _loaded.ContainsKey( coord ) )
				continue;   // moved on while it was sampling

			TerrainMeshBuilder.ChunkGeometry geometry = null;
			if ( pending.Task.IsCompletedSuccessfully )
				geometry = pending.Task.Result;

			if ( geometry is null || !GeometryLooksSampled( geometry, settings ) )
			{
				if ( !_loggedAsyncFallback )
				{
					_loggedAsyncFallback = true;
					var reason = pending.Task.IsFaulted ? pending.Task.Exception?.GetBaseException().Message : "empty samples";
					Log.Warning( $"[TerrainWorldManager] async chunk sampling failed ({reason}) — building chunk {coord} synchronously (logged once)." );
				}
				LoadChunk( coord, settings, streamPos, pending.Visible, useStreamLod: true );
				finished++;
				continue;
			}

			var built = TerrainMeshBuilder.BuildModelFromGeometry( geometry, ChunkSizeMeters, MaxTerrainHeightMeters, withCollision: true, sinkMeters: 0f );
			FinishChunkLoad( coord, settings, streamPos, pending.Visible, pending.VerticesPerSide, built );
			finished++;
		}

		return finished;
	}

	/// <summary>A chunk inside the land disc must have at least one in-world sample; a worker that could not reach the caches returns none.</summary>
	bool GeometryLooksSampled( TerrainMeshBuilder.ChunkGeometry geometry, TerrainPreviewSettings settings )
	{
		if ( geometry.Samples is null || geometry.Heights is null || geometry.Samples.Length == 0 )
			return false;

		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var cx = geometry.ChunkMinX + (chunkSize * 0.5f);
		var cy = geometry.ChunkMinY + (chunkSize * 0.5f);
		var inside = (cx * cx) + (cy * cy) <= settings.TotalWorldRadiusMeters * settings.TotalWorldRadiusMeters;
		if ( !inside )
			return true;   // rim ocean: empty is legitimate

		foreach ( var sample in geometry.Samples )
		{
			if ( sample.IsInsideWorld )
				return true;
		}

		return false;
	}

	void CancelPendingChunkBuilds() => _pendingBuilds.Clear();
}
