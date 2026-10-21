using System.Diagnostics;
using System.Threading.Tasks;

namespace Survival;

/// <summary>
/// Distant terrain tier (Mark 2026-10-07: the 20 km world "felt like 2 km" because only Stream Radius ×
/// chunk size — 512 m — of ground existed around the camera and nothing beyond it). Big coarse chunks
/// (<see cref="FarChunkSizeMeters"/> at <see cref="FarVerticesPerSide"/>) cover everything out to
/// <see cref="FarViewDistanceMeters"/>: render only — no collider, no trees, no nav, no shadows. They go
/// through the same <see cref="TerrainMeshBuilder.BuildChunkGeometry"/> as the near chunks, so hills, lakes,
/// ledges and biome colours match. Sampling (~1100 pipeline samples + the canopy-tint noise per 512 m chunk)
/// runs on worker threads like the near chunks (TerrainWorldManager.AsyncChunks.cs); only the model upload +
/// GameObject happen on the main thread, nearest first under a per-frame budget. The synchronous build was the
/// once-a-second 30-46 ms frame while flying (perf_watch phase timings, 2026-10-15: every other phase stayed
/// under 12 ms) and is kept only as the fallback for a worker that returns empty samples. The
/// far surface is sunk <see cref="FarSinkMeters"/> so it never pokes through the detailed near mesh where
/// the two overlap at the edge of the near bubble, and a far chunk the near tier fully covers is hidden.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Far Terrain" ), Title( "Far Terrain" ), Description( "Coarse render-only terrain beyond the streamed chunks, out to Far View Distance." )]
	public bool FarTerrainEnabled { get; set; } = true;

	[Property, Group( "Far Terrain" ), Title( "Far Chunk Size (m)" ), Range( 256f, 2048f ), Step( 64f )]
	public float FarChunkSizeMeters { get; set; } = 512f;

	[Property, Group( "Far Terrain" ), Title( "Far Verts Per Side" ), Range( 9, 65 ), Step( 8 ), Description( "33 on a 512 m chunk = 16 m steps." )]
	public int FarVerticesPerSide { get; set; } = 33;

	[Property, Group( "Far Terrain" ), Title( "Far View Distance (m)" ), Range( 1000f, 20000f ), Step( 500f )]
	public float FarViewDistanceMeters { get; set; } = 8000f;

	[Property, Group( "Far Terrain" ), Title( "Canopy Tint (0-1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Darken far-tier ground where the forest patches are, so a forest still reads as a forest past the impostor ring (2-8 km, Mark 2026-10-14)." )]
	public float FarCanopyTint01 { get; set; } = 0.45f;

	[Property, Group( "Far Terrain" ), Title( "Far Sink (m)" ), Range( 0f, 10f ), Step( 0.5f ), Description( "The far surface sits this far below the true ground so it never pokes through the near mesh at the bubble edge." )]
	public float FarSinkMeters { get; set; } = 2.5f;

	[Property, Group( "Far Terrain" ), Title( "Far Build Budget (ms)" ), Range( 2f, 24f ), Step( 1f ), Description( "Main-thread time per frame spent finishing far chunks (model upload + GameObject; at least one per frame while any are ready)." )]
	public float FarBuildBudgetMs { get; set; } = 6f;

	[Property, Group( "Far Terrain" ), Title( "Far Async Builds In Flight" ), Range( 1, 16 ), Step( 1 ), Description( "How many far chunks may be sampling on workers at once." )]
	public int FarAsyncBuildsInFlight { get; set; } = 4;

	/// <summary>Impostor cards baked into the loaded far chunks (Stats.FarImpostorInstances).</summary>
	int _farCardCount;

	sealed class FarChunk
	{
		public GameObject GameObject;
		public ModelRenderer Renderer;
		public TerrainChunkCoord Coord;
		/// <summary>Impostor cards baked into this chunk's model (TerrainWorldManager.FarImpostors.cs).</summary>
		public int CardCount;
		/// <summary>Current FarCardFade (0 hidden … 1 shown), stepped by TickFarCardFades.</summary>
		public float CardFade;
		public float BuiltAt;
	}

	/// <summary>What the far-tier worker returns: the geometry and the impostors planted on it.</summary>
	sealed class FarSample
	{
		public TerrainMeshBuilder.ChunkGeometry Geometry;
		/// <summary>Impostor cards as chunk-local vertex / index arrays, built on the worker (null without cards).</summary>
		public Vertex[] CardVertices;
		public int[] CardIndices;
		public int CardCount;
	}

	readonly Dictionary<TerrainChunkCoord, FarChunk> _far = new();
	readonly HashSet<TerrainChunkCoord> _farNeeded = new();
	readonly List<TerrainChunkCoord> _farQueue = new();
	readonly List<TerrainChunkCoord> _farScratch = new();
	readonly Dictionary<TerrainChunkCoord, Task<FarSample>> _farPending = new();
	bool _loggedFarAsyncFallback;
	double _nextFarRefreshAt;
	TerrainChunkCoord _farLastCenter = new( int.MinValue, int.MinValue );
	bool _farStatsLogged;

	/// <summary>Loaded far chunks (inspector / perf readout).</summary>
	[Property, Group( "Far Terrain" ), ReadOnly] public int FarChunkCount { get; private set; }

	void TickFarTerrain()
	{
		if ( !FarTerrainEnabled )
		{
			if ( _far.Count > 0 || _farQueue.Count > 0 )
				UnloadFarTerrain();
			return;
		}

		if ( !TryGetStreamTransform( out var streamPosEngine, out _ ) )
			return;

		var settings = BuildGenerationSettings();
		var streamPos = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		var size = Math.Max( 256f, FarChunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var center = TerrainChunkStreaming.WorldToChunkCoord( streamPos.x, streamPos.y, worldRadius, size );

		if ( Time.NowDouble >= _nextFarRefreshAt || !center.Equals( _farLastCenter ) )
		{
			_nextFarRefreshAt = Time.NowDouble + 1.0;
			_farLastCenter = center;
			RefreshFarNeeded( settings, streamPos, size, worldRadius, center );
		}

		BuildQueuedFarChunks( settings, size, worldRadius );
		UpdateFarVisibility( settings, size, worldRadius, streamPos );
		EnsureCameraReachesFarTier();
		FarChunkCount = _far.Count;
	}

	/// <summary>
	/// The scene cameras shipped with a 50 000 u far clip (1 250 m), which is exactly why the far tier was
	/// invisible on first try: raise the main camera's far clip to cover Far View Distance (never lowered).
	/// </summary>
	void EnsureCameraReachesFarTier()
	{
		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return;

		var needed = TerrainWorldUnits.MetersToEngine( Math.Max( 1000f, FarViewDistanceMeters ) + 1500f );
		if ( cam.ZFar < needed )
			cam.ZFar = needed;
	}

	void RefreshFarNeeded( TerrainPreviewSettings settings, Vector3 streamPos, float size, float worldRadius, TerrainChunkCoord center )
	{
		var view = Math.Max( size, FarViewDistanceMeters );
		var radiusChunks = (int)MathF.Ceiling( view / size ) + 1;
		_farNeeded.Clear();
		for ( var dy = -radiusChunks; dy <= radiusChunks; dy++ )
		{
			for ( var dx = -radiusChunks; dx <= radiusChunks; dx++ )
			{
				var coord = new TerrainChunkCoord( center.X + dx, center.Y + dy );
				if ( !TerrainChunkStreaming.IsChunkInsideWorld( coord, settings, size ) )
					continue;
				if ( FarChunkDistanceMeters( coord, streamPos, worldRadius, size ) > view )
					continue;
				_farNeeded.Add( coord );
			}
		}

		// Unload what fell out of range (one chunk of hysteresis so the edge does not churn).
		_farScratch.Clear();
		foreach ( var pair in _far )
		{
			if ( !_farNeeded.Contains( pair.Key ) && FarChunkDistanceMeters( pair.Key, streamPos, worldRadius, size ) > view + size )
				_farScratch.Add( pair.Key );
		}
		foreach ( var coord in _farScratch )
			UnloadFarChunk( coord );

		// Queue the rest, nearest first.
		_farQueue.Clear();
		foreach ( var coord in _farNeeded )
		{
			if ( !_far.ContainsKey( coord ) )
				_farQueue.Add( coord );
		}
		_farQueue.Sort( ( a, b ) =>
			FarChunkDistanceMeters( a, streamPos, worldRadius, size ).CompareTo( FarChunkDistanceMeters( b, streamPos, worldRadius, size ) ) );
	}

	/// <summary>
	/// Finish the worker builds that completed (main-thread half, under the budget), then hand the next queued
	/// far chunks to workers up to the in-flight cap. Nothing samples on the main thread unless a worker failed.
	/// </summary>
	void BuildQueuedFarChunks( TerrainPreviewSettings settings, float size, float worldRadius )
	{
		var budgetMs = Math.Clamp( FarBuildBudgetMs, 2f, 24f );
		var stopwatch = Stopwatch.StartNew();
		var built = 0;

		if ( _farPending.Count > 0 )
		{
			_farScratch.Clear();
			foreach ( var pair in _farPending )
			{
				if ( pair.Value.IsCompleted )
					_farScratch.Add( pair.Key );
			}

			foreach ( var coord in _farScratch )
			{
				if ( built > 0 && stopwatch.Elapsed.TotalMilliseconds >= budgetMs )
					break;

				var task = _farPending[coord];
				_farPending.Remove( coord );
				if ( _far.ContainsKey( coord ) || !_farNeeded.Contains( coord ) )
					continue;   // flew out of range while it was sampling

				var sampled = task.IsCompletedSuccessfully ? task.Result : null;
				if ( sampled?.Geometry is null || !FarGeometryLooksSampled( sampled.Geometry, settings, size ) )
				{
					if ( !_loggedFarAsyncFallback )
					{
						_loggedFarAsyncFallback = true;
						var reason = task.IsFaulted ? task.Exception?.GetBaseException().Message : "empty samples";
						Log.Warning( $"[TerrainWorldManager] async far chunk sampling failed ({reason}) — building far chunk {coord} synchronously (logged once)." );
					}
					sampled = SampleFarChunk( coord, settings, size, _backend ?? TerrainPreviewBackendRegistry.Active, BuildVegetationOptions(), FarCanopyTint01, Math.Clamp( FarVerticesPerSide, 9, 65 ), FarImpostorsEnabled, FarImpostorHeightMeters, Math.Max( 32f, ChunkSizeMeters ) );
					if ( sampled?.Geometry is null )
						continue;
				}

				FinishFarChunk( coord, sampled, size, worldRadius );
				built++;
			}
		}

		var maxInFlight = Math.Clamp( FarAsyncBuildsInFlight, 1, 16 );
		while ( _farQueue.Count > 0 && _farPending.Count < maxInFlight )
		{
			var coord = _farQueue[0];
			_farQueue.RemoveAt( 0 );
			if ( _far.ContainsKey( coord ) || _farPending.ContainsKey( coord ) || !_farNeeded.Contains( coord ) )
				continue;

			var backend = _backend ?? TerrainPreviewBackendRegistry.Active;
			var options = BuildVegetationOptions();
			var tint = FarCanopyTint01;
			var verts = Math.Clamp( FarVerticesPerSide, 9, 65 );
			var impostors = FarImpostorsEnabled;
			var impostorHeight = FarImpostorHeightMeters;
			var nearChunkSize = Math.Max( 32f, ChunkSizeMeters );
			_farPending[coord] = GameTask.RunInThreadAsync( () => SampleFarChunk( coord, settings, size, backend, options, tint, verts, impostors, impostorHeight, nearChunkSize ) );
		}
	}

	/// <summary>A far chunk inside the land disc must carry at least one in-world sample (a worker that could not reach the caches returns none).</summary>
	static bool FarGeometryLooksSampled( TerrainMeshBuilder.ChunkGeometry g, TerrainPreviewSettings settings, float size )
	{
		if ( g.Samples is null || g.Heights is null || g.Samples.Length == 0 )
			return false;

		var cx = g.ChunkMinX + (size * 0.5f);
		var cy = g.ChunkMinY + (size * 0.5f);
		if ( (cx * cx) + (cy * cy) > settings.TotalWorldRadiusMeters * settings.TotalWorldRadiusMeters )
			return true;   // rim ocean: empty is legitimate

		foreach ( var sample in g.Samples )
		{
			if ( sample.IsInsideWorld )
				return true;
		}

		return false;
	}

	/// <summary>Worker half: geometry samples + canopy tint. Pure (settings, backend caches, noise); no engine objects.</summary>
	static FarSample SampleFarChunk(
		TerrainChunkCoord coord,
		TerrainPreviewSettings settings,
		float size,
		ITerrainPreviewBackend backend,
		TerrainVegetationScatter.Options options,
		float tint,
		int verts,
		bool impostors,
		float impostorHeightMeters,
		float nearChunkSize )
	{
		// riverBedAsGround false: the far tier has no water strip, so its river creases stay palette-blue.
		var g = TerrainMeshBuilder.BuildChunkGeometry( settings, backend, coord, size, verts, 0, 0f, 0, riverBedAsGround: false );
		if ( g is null )
			return null;

		ApplyFarCanopyTint( g, settings, options, tint );
		var cards = impostors
			? PlanFarCards( settings, backend, coord, size, nearChunkSize, options, impostorHeightMeters )
			: Array.Empty<Transform>();
		BuildFarCardGeometry( cards, out var cardVertices, out var cardIndices );
		return new FarSample { Geometry = g, CardVertices = cardVertices, CardIndices = cardIndices, CardCount = cards.Length };
	}

	/// <summary>
	/// Forest canopy as colour: where the real scatter's forest patches are, the far ground darkens toward a
	/// canopy green, so the 2-8 km band shows forests without a single tree drawn. Same patch noise as the
	/// tree planner (<see cref="TerrainVegetationScatter.SampleForestCover01"/>), evaluated at the far vertices.
	/// </summary>
	static void ApplyFarCanopyTint( TerrainMeshBuilder.ChunkGeometry g, TerrainPreviewSettings settings, TerrainVegetationScatter.Options options, float tint01 )
	{
		var tint = Math.Clamp( tint01, 0f, 1f );
		if ( g.Colors is null || g.Samples is null )
			return;

		var n = g.VerticesPerSide;
		var canopy = new Color( 0.09f, 0.22f, 0.07f );
		for ( var i = 0; i < g.Colors.Length; i++ )
		{
			var sample = g.Samples[i];
			if ( !sample.IsInsideWorld || !sample.IsOnLand || sample.OceanHeight01 > 0.5f || !sample.HasLandWeights )
				continue;

			var biome = TerrainPreviewBiomeResolver.PickDominantPlacementBiome( sample.LandWeights );
			var wx = g.ChunkMinX + ((i % n) * g.Step) + g.OffsetX[i];
			var wy = g.ChunkMinY + ((i / n) * g.Step) + g.OffsetY[i];
			var cover = TerrainVegetationScatter.SampleForestCover01( settings, options, biome, wx, wy );
			if ( cover <= 0.01f )
				continue;

			if ( tint > 0.001f )
				g.Colors[i] = Color.Lerp( g.Colors[i], canopy, cover * tint );
		}
	}

	/// <summary>Main-thread half: model upload + GameObject for sampled far geometry.</summary>
	void FinishFarChunk( TerrainChunkCoord coord, FarSample sampled, float size, float worldRadius )
	{
		var g = sampled.Geometry;
		if ( !_farStatsLogged )
		{
			_farStatsLogged = true;
			var lo = float.MaxValue;
			var hi = float.MinValue;
			var inside = 0;
			for ( var k = 0; k < g.Heights.Length; k++ )
			{
				lo = Math.Min( lo, g.Heights[k] );
				hi = Math.Max( hi, g.Heights[k] );
				if ( g.Samples[k].IsInsideWorld )
					inside++;
			}
			Log.Info( $"[TerrainWorldManager] first far chunk {coord}: heights {lo:0.#}..{hi:0.#} m, inside {inside}/{g.Heights.Length}" );
		}

		var cardMesh = BuildFarCardMesh( sampled.CardVertices, sampled.CardIndices, size, MaxTerrainHeightMeters );
		var built = TerrainMeshBuilder.BuildModelFromGeometry(
			g, size, MaxTerrainHeightMeters, withCollision: false, sinkMeters: Math.Max( 0f, FarSinkMeters ), extraMesh: cardMesh );
		if ( built.Model is null || !built.Model.IsValid )
			return;

		var minX = -worldRadius + (coord.X * size);
		var minY = -worldRadius + (coord.Y * size);
		var go = new GameObject( true, $"TerrainFar {coord}" );
		go.Flags |= GameObjectFlags.NotSaved;
		go.Parent = GameObject;
		go.WorldPosition = TerrainWorldUnits.MetersToEngine( new Vector3( minX, minY, 0f ) );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = built.Model;
		// No MaterialOverride here: the model carries two materials (ground + the baked impostor cards); each mesh keeps its own.
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		var chunk = new FarChunk { GameObject = go, Renderer = renderer, Coord = coord, CardCount = cardMesh is null ? 0 : sampled.CardCount, BuiltAt = Time.Now };
		_far[coord] = chunk;
		_farCardCount += chunk.CardCount;
		BeginFarCardFade( chunk );
	}

	/// <summary>
	/// A far chunk every near chunk of which is loaded is hidden (the near mesh draws there); any other far
	/// chunk near the bubble stays visible so the frontier never shows a hole while near chunks stream in.
	/// </summary>
	void UpdateFarVisibility( TerrainPreviewSettings settings, float size, float worldRadius, Vector3 streamPos )
	{
		var nearSize = Math.Max( 32f, ChunkSizeMeters );
		var bubble = (Math.Max( 1, StreamRadiusChunks ) + Math.Max( 0, StreamUnloadMarginChunks ) + 1) * nearSize;
		var perSide = Math.Max( 1, (int)MathF.Round( size / nearSize ) );

		foreach ( var pair in _far )
		{
			var chunk = pair.Value;
			if ( chunk.Renderer is null || !chunk.Renderer.IsValid() )
				continue;

			var visible = true;
			if ( FarChunkDistanceMeters( pair.Key, streamPos, worldRadius, size ) <= bubble + size )
			{
				var minX = -worldRadius + (pair.Key.X * size);
				var minY = -worldRadius + (pair.Key.Y * size);
				var near0 = TerrainChunkStreaming.WorldToChunkCoord( minX + (nearSize * 0.5f), minY + (nearSize * 0.5f), worldRadius, nearSize );
				var covered = true;
				for ( var iy = 0; iy < perSide && covered; iy++ )
				{
					for ( var ix = 0; ix < perSide; ix++ )
					{
						if ( !_loaded.ContainsKey( new TerrainChunkCoord( near0.X + ix, near0.Y + iy ) ) )
						{
							covered = false;
							break;
						}
					}
				}

				visible = !covered;
			}

			if ( chunk.Renderer.Enabled != visible )
				chunk.Renderer.Enabled = visible;
		}
	}

	static float FarChunkDistanceMeters( TerrainChunkCoord coord, Vector3 streamPos, float worldRadius, float size )
	{
		var cx = -worldRadius + ((coord.X + 0.5f) * size);
		var cy = -worldRadius + ((coord.Y + 0.5f) * size);
		var dx = cx - streamPos.x;
		var dy = cy - streamPos.y;
		return MathF.Sqrt( (dx * dx) + (dy * dy) );
	}

	void UnloadFarChunk( TerrainChunkCoord coord )
	{
		if ( !_far.TryGetValue( coord, out var chunk ) )
			return;

		_far.Remove( coord );
		_farCardCount -= chunk.CardCount;
		if ( chunk.GameObject is not null && chunk.GameObject.IsValid() )
			chunk.GameObject.Destroy();
	}

	void UnloadFarTerrain()
	{
		_farScratch.Clear();
		_farScratch.AddRange( _far.Keys );
		foreach ( var coord in _farScratch )
			UnloadFarChunk( coord );
		_farQueue.Clear();
		_farPending.Clear();
		_farNeeded.Clear();
		_farLastCenter = new TerrainChunkCoord( int.MinValue, int.MinValue );
		_farStatsLogged = false;
		FarChunkCount = 0;
	}
}
