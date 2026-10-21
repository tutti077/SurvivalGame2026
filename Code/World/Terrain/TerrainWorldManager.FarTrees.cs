using System.Threading.Tasks;
using Sandbox.Rendering;

namespace Survival;

/// <summary>
/// Far forest (Mark 2026-10-13: trees vanish past the stream ring and the land reads flat forever). Beyond the
/// loaded chunks every large tree the real scatter would plant is drawn as THAT TREE'S OWN LOWEST LOD: the
/// elm's <c>&lt;model&gt;_far.vmdl</c> is the LOD3 render mesh (300 wood tris + 20 % of the leaf cards, the same
/// bark / leaf materials) pulled from the same fbx the prefab uses, so a far elm is an elm — no separate art
/// (Mark 2026-10-15: "Enshrouded looks amazing from a distance"). A prefab whose model has no _far sibling uses
/// <see cref="VegetationFarCardModel"/>.
///
/// DRAWN BY HAND, NOT BY THE ENGINE'S CLUTTER (2026-10-20). The first versions went through one Clutter
/// component in infinite mode, and every hand-over to real trees meant invalidating a tile and waiting for the
/// engine to regenerate it — which it applies 1-4 frames after its generator runs, more under load, with no way
/// to ask when. Whatever the timing rule, some tree had no model for a frame or two ("trees flash when they
/// pop in / out", Mark, four rounds of flash-probe rigs). Now each chunk's far trees are a per-model array of
/// transforms built once from the cached plan, and a <see cref="CommandList"/> on the camera's opaque stage,
/// rebuilt every frame, draws every chunk in range with DrawModelInstanced: a chunk's far trees stop in EXACTLY the frame this code
/// decides (two frames after its last real clone stood; the moment it starts unloading) — a deliberate overlap
/// of identical LOD3 meshes, never a gap. No shadows, no colliders.
///
/// Planning a chunk's trees costs ~100 pipeline samples, so plans are computed on worker threads ahead of the
/// camera into a per-chunk cache (<see cref="TickFarTreePlanning"/>); the main thread only turns a landed plan
/// into transform arrays. Past <see cref="VegetationFarCardRangeMeters"/> nothing is drawn.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Vegetation" ), Title( "Far Trees — Enabled" ), Description( "Draw the forest beyond the loaded chunks as each tree's lowest LOD at the real tree positions." )]
	public bool VegetationFarCardsEnabled { get; set; } = true;

	[Property, Group( "Vegetation" ), Title( "Far Trees — Range (m)" ), Range( 600f, 8000f ), Step( 100f ), Description( "Far trees are drawn out to this distance from the camera; nothing beyond." )]
	public float VegetationFarCardRangeMeters { get; set; } = 1200f;

	[Property, Group( "Vegetation" ), Title( "Far Trees — Fallback Model" ), Description( "Used for a tree prefab whose model has no <model>_far.vmdl beside it (the elm script writes one per variant)." )]
	public string VegetationFarCardModel { get; set; } = "models/environment/tests/environment_elm1_v76_far.vmdl";

	[Property, Group( "Vegetation" ), Title( "Far Trees — Plan Tasks In Flight" ), Range( 1, 8 ), Step( 1 ), Description( "Worker tasks planning far-tree chunks at once (each plans a batch of chunks)." )]
	public int VegetationFarPlanTasks { get; set; } = 6;

	const int FarPlanBatchChunks = 12;
	const float FarPlanScanSeconds = 0.25f;
	/// <summary>Plans are kept this far beyond the drawn ring so a chunk is ready before it comes into range.</summary>
	const float FarPlanAheadMeters = 1000f;
	/// <summary>Perf switch (<c>perf_far_ring 0|1</c>): skip the far ring draw while its plans stay cached.</summary>
	public bool FarRingHidden
	{
		get => _farRingHidden;
		set { _farRingHidden = value; _farDirty = true; }
	}
	bool _farRingHidden;

	/// <summary>
	/// The command list is rebuilt only when something it draws changed (<see cref="_farDirty"/>: a plan cached or
	/// evicted, a hand-over frame reached, a chunk unloading / its trees leaving, a fade in progress, the camera
	/// moving a chunk-quarter or turning 10°), never more often than every <see cref="FarRebuildMinFrames"/> frames and
	/// at least every <see cref="FarRebuildMaxFrames"/> as a safety net. Each rebuild copies every transform array
	/// into the engine (~130 KB per model), which was 4-8 ms at 7 500 instances when it ran every other frame
	/// (2026-10-20); standing still it now runs about never. Hand-over overlaps are sized so a stale list never shows a gap.
	/// </summary>
	/// <summary>10: each rebuild hands the engine a fresh Transform[] per draw (~0.5 MB at 11 k instances); every 3 frames while flying that was 50 MB / 4 s of garbage and 8 ms GC pauses (2026-10-20).</summary>
	const int FarRebuildMinFrames = 10;
	const int FarRebuildMaxFrames = 90;
	const int FarRebuildEveryFrames = FarRebuildMinFrames;
	bool _farDirty = true;
	int _farLastBuildFrame = -1000;
	Vector3 _farBuildCamMeters;
	Vector3 _farBuildCamForward;
	/// <summary>Hand-over frames still ahead: the frame one is reached, ChunkHasRealTrees flips and the list must rebuild.</summary>
	readonly List<int> _farHandoverDue = new();

	/// <summary>Something the far ring draws changed: rebuild its command list at the next opportunity.</summary>
	internal void MarkFarRingDirty() => _farDirty = true;
	/// <summary>Frames a chunk's far trees keep drawing after its last real clone stood (overlap, never a gap).</summary>
	const int FarHandoverOverlapFrames = 2 + FarRebuildEveryFrames;
	/// <summary>Frames a leaving chunk's real trees keep standing after its far trees resumed (overlap, never a gap; covers a stale list).</summary>
	const int FarUnloadOverlapFrames = 3 + FarRebuildEveryFrames;

	/// <summary>One instanced draw: every far tree of one chunk that uses this model.</summary>
	sealed class FarDrawBatch
	{
		public Model Model;
		public Transform[] Transforms;
	}

	bool _farActive;
	CameraComponent _farHookCamera;
	CommandList _farCommands;
	/// <summary>Dev: 0 = instanced far model, 1 = one DrawModel per tree (first 400), 2 = instanced dev box at the tree transforms.</summary>
	internal static int FarDrawMode;
	Model _farDebugBox;
	/// <summary>Per frame: every in-range chunk's transforms gathered per model and fade band, so the list holds ONE instanced draw per model per band (2765 per-chunk draws ran at 6 fps, 2026-10-20).</summary>
	readonly Dictionary<Model, List<Transform>[]> _farGather = new();
	/// <summary>
	/// A chunk's far trees fade in over <see cref="VegetationFarFadeSeconds"/> from the moment they first become drawable
	/// (planned and inside the range) and fade out over the same time when the chunk leaves the range — a fade in TIME,
	/// so a tree is always on its way to fully opaque and never parked half-drawn by its distance (Mark 2026-10-20).
	/// Real trees taking over at the ring's inner edge drop the far ones at once (identical mesh). Quantised to
	/// <see cref="FarFadeBands"/> steps, one instanced draw per model per step.
	/// </summary>
	const int FarFadeBands = 8;
	RenderAttributes[] _farBandAttributes;
	readonly Dictionary<TerrainChunkCoord, float> _farShownSince = new();
	readonly Dictionary<TerrainChunkCoord, float> _farLeavingSince = new();

	[Property, Group( "Vegetation" ), Title( "Far Trees — Fade (s)" ), Range( 0f, 3f ), Step( 0.05f ), Description( "Seconds a chunk's far trees take to fade fully in when they appear, and out when they leave the range." )]
	public float VegetationFarFadeSeconds { get; set; } = 0f;
	Model _farFallbackModel;
	Dictionary<string, Model> _farModelsByPrefab;
	Vector3 _farCameraMeters;

	// Plan cache + the workers filling it. Main thread owns the dictionaries; workers only build arrays.
	readonly Dictionary<TerrainChunkCoord, TerrainVegetationScatter.FarTree[]> _farPlans = new();
	readonly Dictionary<TerrainChunkCoord, FarDrawBatch[]> _farBatches = new();
	readonly HashSet<TerrainChunkCoord> _farPlanInFlight = new();
	readonly List<TerrainChunkCoord> _farPlanWanted = new();
	readonly List<Task<List<(TerrainChunkCoord Coord, TerrainVegetationScatter.FarTree[] Trees)>>> _farPlanTasks = new();
	RealTimeSince _sinceFarPlanScan = 999f;
	RealTimeSince _sinceFarPlanEvict;

	/// <summary>A loaded chunk whose real trees have taken over (hand-over frame reached) and that is not on its way out: nothing far over it.</summary>
	bool ChunkHasRealTrees( TerrainChunkCoord coord )
		=> _loaded.TryGetValue( coord, out var entry ) && entry.FarHandoverFrame >= 0 && _frameIndex >= entry.FarHandoverFrame && !entry.Unloading && !entry.TreesLeaving;

	/// <summary>
	/// Hand a chunk over from the far ring to its real trees. Called when the chunk's LAST real clone has landed:
	/// the far trees keep drawing for <see cref="FarHandoverOverlapFrames"/> more frames so the clones are on
	/// screen before their stand-ins go (identical LOD3 meshes overlap invisibly; a gap flashes).
	/// </summary>
	bool TryBeginFarHandover( LoadedChunk entry )
	{
		if ( entry.FarHandoverFrame < 0 )
		{
			entry.FarHandoverFrame = _frameIndex + FarHandoverOverlapFrames;
			_farHandoverDue.Add( entry.FarHandoverFrame );
		}
		return true;
	}

	/// <summary>True once a leaving chunk's far trees have been drawing for <see cref="FarUnloadOverlapFrames"/> frames (its real trees may go).</summary>
	bool FarTreesCoverUnload( LoadedChunk entry )
		=> !_farActive || _frameIndex - entry.UnloadFrame >= FarUnloadOverlapFrames;

	/// <summary>Create (or drop) the far ring to match the flag, keep the plan cache filled ahead of the camera, keep the render hook on the live camera.</summary>
	void EnsureFarTreeCards()
	{
		var want = VegetationFarCardsEnabled && VegetationScatterEnabled && !_vegetationHidden && !_isWorldLoading && _loadSettings is not null;
		if ( want != _farActive )
		{
			if ( want )
				StartFarTrees();
			else
				DestroyFarTreeCards();
		}

		if ( !_farActive )
			return;

		var haveCamera = TryGetStreamTransform( out var streamPosEngine, out var streamRotation );
		if ( haveCamera )
			_farCameraMeters = TerrainWorldUnits.EngineToMeters( streamPosEngine );
		EnsureFarTreeHook();
		TickFarTreePlanning();
		TickFarCardFades( _farCameraMeters );
		Stats.FarImpostorInstances = _farCardCount;
		if ( !haveCamera )
			return;

		for ( var i = _farHandoverDue.Count - 1; i >= 0; i-- )
		{
			if ( _farHandoverDue[i] > _frameIndex )
				continue;
			_farHandoverDue.RemoveAt( i );
			_farDirty = true;
		}
		var forward = streamRotation.Forward;
		var chunkQuarter = Math.Max( 32f, ChunkSizeMeters ) * 0.25f;
		if ( !_farDirty )
		{
			if ( _frameIndex - _farLastBuildFrame >= FarRebuildMaxFrames )
				_farDirty = true;
			else if ( (_farCameraMeters - _farBuildCamMeters).LengthSquared > chunkQuarter * chunkQuarter )
				_farDirty = true;
			else if ( Vector3.Dot( forward, _farBuildCamForward ) < 0.985f )   // ~10 degrees
				_farDirty = true;
		}
		if ( _farDirty && _frameIndex - _farLastBuildFrame >= FarRebuildMinFrames )
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			_farDirty = false;
			_farLastBuildFrame = _frameIndex;
			_farBuildCamMeters = _farCameraMeters;
			_farBuildCamForward = forward;
			BuildFarCommandList( _farCameraMeters, streamRotation );
			Stats.FarBuildMs = Math.Max( Stats.FarBuildMs, (float)sw.Elapsed.TotalMilliseconds );
		}
	}

	void StartFarTrees()
	{
		_farFallbackModel = Model.Load( VegetationFarCardModel );
		if ( _farFallbackModel is null || _farFallbackModel.IsError )
		{
			Log.Info( $"[TerrainWorldManager] far trees: fallback model '{VegetationFarCardModel}' failed to load" );
			VegetationFarCardsEnabled = false;
			return;
		}

		_farModelsByPrefab = BuildFarTreeModels( _farFallbackModel );
		_farActive = true;
		_sinceFarPlanScan = 999f;
		Log.Info( $"[TerrainWorldManager] far trees: instanced draw hook, {_farModelsByPrefab.Values.Distinct().Count()} far models for {_farModelsByPrefab.Count} tree prefabs, range {VegetationFarCardRangeMeters:0} m, plans on workers" );
	}

	/// <summary>The command list follows the scene camera (the fly camera and the pawn's camera are different components).</summary>
	void EnsureFarTreeHook()
	{
		var cam = Scene.Camera;
		_farCommands ??= new CommandList( "FarTrees" );
		if ( cam == _farHookCamera && _farHookCamera is not null && _farHookCamera.IsValid() )
			return;

		if ( _farHookCamera is not null && _farHookCamera.IsValid() )
		{
			_farHookCamera.RemoveCommandList( _farCommands );
		}
		_farHookCamera = null;
		if ( cam is null || !cam.IsValid() )
			return;

		cam.AddCommandList( _farCommands, Stage.BeforePostProcess, -100 );
		_farHookCamera = cam;
		_farDirty = true;
	}

	/// <summary>Rebuild the far-tree command list for this frame: every cached chunk in range whose real trees are not standing, one instanced draw per model and fade step.</summary>
	void BuildFarCommandList( Vector3 camMeters, Rotation camRotation )
	{
		if ( _farCommands is null )
			return;
		_farCommands.Reset();
		Stats.FarHookCalls++;
		if ( FarRingHidden || !_farActive || _loadSettings is null || _farBatches.Count == 0 )
			return;

		var swGather = System.Diagnostics.Stopwatch.StartNew();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var forward = camRotation.Forward;
		var range = Math.Max( 600f, VegetationFarCardRangeMeters );
		var rangeSq = range * range;
		var half = chunkSize * 0.75f;
		var drawCalls = 0;
		var instances = 0;
		var now = Time.Now;
		var fadeSeconds = Math.Max( 0.001f, VegetationFarFadeSeconds );
		var fading = false;
		var outerSq = (range + chunkSize) * (range + chunkSize);

		foreach ( var pair in _farBatches )
		{
			var coord = pair.Key;
			var cx = -worldRadius + ((coord.X + 0.5f) * chunkSize) - camMeters.x;
			var cy = -worldRadius + ((coord.Y + 0.5f) * chunkSize) - camMeters.y;
			var dSq = (cx * cx) + (cy * cy);
			// Most cached plans sit beyond the range (plan-ahead): arithmetic only for those, no dictionary work.
			if ( dSq > outerSq && !_farLeavingSince.ContainsKey( coord ) && !_farShownSince.ContainsKey( coord ) )
				continue;
			if ( ChunkHasRealTrees( coord ) )
			{
				// The real trees stand here (same mesh at this distance): no fade, the far ones simply stop.
				_farShownSince.Remove( coord );
				_farLeavingSince.Remove( coord );
				continue;
			}

			float fade;
			if ( dSq <= rangeSq )
			{
				_farLeavingSince.Remove( coord );
				if ( !_farShownSince.TryGetValue( coord, out var since ) )
				{
					since = now;
					_farShownSince[coord] = since;
				}
				fade = Math.Min( 1f, (now - since) / fadeSeconds );
				if ( fade < 1f )
					fading = true;
			}
			else
			{
				if ( !_farShownSince.ContainsKey( coord ) )
					continue;   // never shown: nothing to fade out
				if ( !_farLeavingSince.TryGetValue( coord, out var leaving ) )
				{
					leaving = now;
					_farLeavingSince[coord] = leaving;
				}
				fade = 1f - ((now - leaving) / fadeSeconds);
				if ( fade <= 0f )
				{
					_farShownSince.Remove( coord );
					_farLeavingSince.Remove( coord );
					continue;
				}
				fading = true;
			}

			// Behind the camera (with a chunk of slack): nothing to draw, but the fade clock keeps running.
			if ( dSq > half * half * 4f && ((cx * forward.x) + (cy * forward.y)) < -half )
				continue;

			var band = Math.Clamp( (int)MathF.Ceiling( fade * FarFadeBands ) - 1, 0, FarFadeBands - 1 );

			foreach ( var batch in pair.Value )
			{
				if ( !_farGather.TryGetValue( batch.Model, out var lists ) || lists.Length != FarFadeBands )
				{
					lists = new List<Transform>[FarFadeBands];
					for ( var b = 0; b < FarFadeBands; b++ )
						lists[b] = new List<Transform>( b == FarFadeBands - 1 ? 4096 : 256 );
					_farGather[batch.Model] = lists;
				}
				lists[band].AddRange( batch.Transforms );
			}
		}

		if ( _farBandAttributes is null || _farBandAttributes.Length != FarFadeBands )
		{
			_farBandAttributes = new RenderAttributes[FarFadeBands];
			for ( var b = 0; b < FarFadeBands; b++ )
			{
				_farBandAttributes[b] = new RenderAttributes();
				_farBandAttributes[b].Set( "TreeFade", (b + 1) / (float)FarFadeBands );
			}
		}

		Stats.FarGatherMs = Math.Max( Stats.FarGatherMs, (float)swGather.Elapsed.TotalMilliseconds );
		if ( fading )
			_farDirty = true;   // bands move while a chunk fades: keep rebuilding at the minimum cadence
		foreach ( var pair in _farGather )
		{
			for ( var b = 0; b < FarFadeBands; b++ )
			{
				var list = pair.Value[b];
				if ( list.Count == 0 )
					continue;
				var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( list );
				switch ( FarDrawMode )
				{
					case 1:
						for ( var i = 0; i < span.Length && i < 400; i++ )
							_farCommands.DrawModel( pair.Key, span[i], _farBandAttributes[b] );
						break;
					case 2:
						_farDebugBox ??= Model.Load( "models/dev/box.vmdl" );
						_farCommands.DrawModelInstanced( _farDebugBox, span, _farBandAttributes[b] );
						break;
					default:
						_farCommands.DrawModelInstanced( pair.Key, span, _farBandAttributes[b] );
						break;
				}
				drawCalls++;
				instances += span.Length;
				list.Clear();
			}
		}

		Stats.FarDrawCalls = Math.Max( Stats.FarDrawCalls, drawCalls );
		Stats.FarDrawInstances = Math.Max( Stats.FarDrawInstances, instances );
	}

	/// <summary>
	/// One far model per distinct tree prefab the scatter profiles list: its model's <c>_far.vmdl</c> sibling, or the
	/// fallback when there is none. Resolved once when the ring starts.
	/// </summary>
	Dictionary<string, Model> BuildFarTreeModels( Model fallback )
	{
		var byPrefab = new Dictionary<string, Model>( StringComparer.OrdinalIgnoreCase );
		var byPath = new Dictionary<string, Model>( StringComparer.OrdinalIgnoreCase );
		var options = BuildVegetationOptions();
		if ( options.Profiles is null )
			return byPrefab;

		foreach ( var profile in options.Profiles )
		{
			if ( profile.Prefabs is null )
				continue;

			foreach ( var raw in profile.Prefabs )
			{
				var prefabPath = TerrainVegetationScatter.NormalizePrefabPath( raw );
				if ( string.IsNullOrEmpty( prefabPath ) || byPrefab.ContainsKey( prefabPath ) )
					continue;

				var farPath = ResolveFarModelPath( prefabPath );
				if ( farPath is null )
				{
					byPrefab[prefabPath] = fallback;
					continue;
				}

				if ( !byPath.TryGetValue( farPath, out var model ) )
				{
					model = Model.Load( farPath );
					if ( model is null || model.IsError )
						model = fallback;
					byPath[farPath] = model;
				}

				byPrefab[prefabPath] = model;
			}
		}

		return byPrefab;
	}

	/// <summary>"models/x/tree.vmdl" of the prefab's renderer → "models/x/tree_far.vmdl" when that file exists, else null.</summary>
	static string ResolveFarModelPath( string prefabPath )
	{
		var template = GameObject.GetPrefab( prefabPath );
		if ( template is null || !template.IsValid )
			return null;

		var renderer = template.Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		var modelPath = renderer?.Model?.ResourcePath;
		if ( string.IsNullOrEmpty( modelPath ) || !modelPath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
			return null;

		var farPath = modelPath[..^5] + "_far.vmdl";
		return FileSystem.Mounted.FileExists( farPath ) || FileSystem.Mounted.FileExists( farPath + "_c" ) ? farPath : null;
	}

	/// <summary>A landed plan becomes per-model transform arrays (engine units), ready for the hook. Main thread, once per chunk.</summary>
	FarDrawBatch[] BuildFarBatches( TerrainVegetationScatter.FarTree[] trees )
	{
		if ( trees.Length == 0 )
			return Array.Empty<FarDrawBatch>();

		var groups = new Dictionary<Model, List<Transform>>();
		foreach ( var tree in trees )
		{
			var model = _farFallbackModel;
			if ( tree.PrefabPath is not null && _farModelsByPrefab is not null && _farModelsByPrefab.TryGetValue( tree.PrefabPath, out var own ) )
				model = own;

			if ( !groups.TryGetValue( model, out var list ) )
			{
				list = new List<Transform>();
				groups[model] = list;
			}

			var position = TerrainWorldUnits.MetersToEngine( new Vector3( tree.X, tree.Y, tree.HeightMeters ) );
			list.Add( new Transform( position, Rotation.FromYaw( tree.YawDegrees ), tree.ScaleMul ) );
		}

		var batches = new FarDrawBatch[groups.Count];
		var i = 0;
		foreach ( var pair in groups )
			batches[i++] = new FarDrawBatch { Model = pair.Key, Transforms = pair.Value.ToArray() };
		return batches;
	}

	/// <summary>
	/// Every quarter second: list the uncached chunks inside the ring (plus the plan-ahead margin), nearest first;
	/// keep a few worker tasks planning batches of them; merge finished batches into the cache as draw batches.
	/// </summary>
	void TickFarTreePlanning()
	{
		if ( !_farActive || _loadSettings is null )
			return;

		for ( var i = _farPlanTasks.Count - 1; i >= 0; i-- )
		{
			var task = _farPlanTasks[i];
			if ( !task.IsCompleted )
				continue;

			_farPlanTasks.RemoveAt( i );
			if ( !task.IsCompletedSuccessfully )
			{
				Log.Info( $"[TerrainWorldManager] far trees: plan batch failed: {task.Exception?.GetBaseException().Message}" );
				_farPlanInFlight.Clear();
				continue;
			}

			foreach ( var (coord, trees) in task.Result )
			{
				_farPlanInFlight.Remove( coord );
				_farPlans[coord] = trees;
				_farBatches[coord] = BuildFarBatches( trees );
				_farDirty = true;
			}
		}
		Stats.FarCachedChunks = _farBatches.Count;

		if ( _sinceFarPlanScan >= FarPlanScanSeconds )
		{
			_sinceFarPlanScan = 0f;
			ScanFarPlanWanted();
		}

		var maxTasks = Math.Clamp( VegetationFarPlanTasks, 1, 8 );
		while ( _farPlanTasks.Count < maxTasks && _farPlanWanted.Count > 0 )
		{
			var batch = new List<TerrainChunkCoord>( FarPlanBatchChunks );
			while ( batch.Count < FarPlanBatchChunks && _farPlanWanted.Count > 0 )
			{
				var coord = _farPlanWanted[0];
				_farPlanWanted.RemoveAt( 0 );
				if ( _farPlans.ContainsKey( coord ) || _farPlanInFlight.Contains( coord ) )
					continue;
				_farPlanInFlight.Add( coord );
				batch.Add( coord );
			}

			if ( batch.Count == 0 )
				break;

			var settings = _loadSettings;
			var backend = _backend;
			var options = BuildVegetationOptions();
			var chunkSize = Math.Max( 32f, ChunkSizeMeters );
			_farPlanTasks.Add( GameTask.RunInThreadAsync( () =>
			{
				var results = new List<(TerrainChunkCoord, TerrainVegetationScatter.FarTree[])>( batch.Count );
				var scratch = new List<TerrainVegetationScatter.FarTree>();
				foreach ( var coord in batch )
				{
					scratch.Clear();
					TerrainVegetationScatter.EnumerateTrees( settings, backend, coord, chunkSize, options, scratch );
					results.Add( (coord, scratch.ToArray()) );
				}
				return results;
			} ) );
		}

		if ( _sinceFarPlanEvict >= 5f )
		{
			_sinceFarPlanEvict = 0f;
			EvictFarPlans();
		}
	}

	/// <summary>
	/// Uncached chunks within the ring plus the plan-ahead margin, nearest the camera first. Chunks deep inside the
	/// loaded square (one chunk short of the stream radius, Chebyshev) are skipped: their real trees stand there
	/// and the plan is only needed once they unload behind the camera.
	/// </summary>
	void ScanFarPlanWanted()
	{
		_farPlanWanted.Clear();
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var cam = _farCameraMeters;
		var range = Math.Max( 600f, VegetationFarCardRangeMeters ) + FarPlanAheadMeters;
		var innerSquare = Math.Max( 0f, VegetationObjectRangeMeters - chunkSize );
		var camX = (int)MathF.Floor( (cam.x + worldRadius) / chunkSize );
		var camY = (int)MathF.Floor( (cam.y + worldRadius) / chunkSize );
		var r = (int)MathF.Ceiling( range / chunkSize );
		var rangeSq = range * range;

		for ( var dy = -r; dy <= r; dy++ )
		{
			for ( var dx = -r; dx <= r; dx++ )
			{
				var coord = new TerrainChunkCoord { X = camX + dx, Y = camY + dy };
				if ( _farPlans.ContainsKey( coord ) || _farPlanInFlight.Contains( coord ) )
					continue;
				var cx = -worldRadius + ((coord.X + 0.5f) * chunkSize) - cam.x;
				var cy = -worldRadius + ((coord.Y + 0.5f) * chunkSize) - cam.y;
				if ( (cx * cx) + (cy * cy) > rangeSq )
					continue;
				if ( MathF.Max( MathF.Abs( cx ), MathF.Abs( cy ) ) < innerSquare )
					continue;
				_farPlanWanted.Add( coord );
			}
		}

		_farPlanWanted.Sort( ( a, b ) =>
		{
			var ax = -worldRadius + ((a.X + 0.5f) * chunkSize) - cam.x;
			var ay = -worldRadius + ((a.Y + 0.5f) * chunkSize) - cam.y;
			var bx = -worldRadius + ((b.X + 0.5f) * chunkSize) - cam.x;
			var by = -worldRadius + ((b.Y + 0.5f) * chunkSize) - cam.y;
			return ((ax * ax) + (ay * ay)).CompareTo( (bx * bx) + (by * by) );
		} );
	}

	/// <summary>Drop cached plans well outside the ring so the cache never grows with everything ever flown over.</summary>
	void EvictFarPlans()
	{
		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var cam = _farCameraMeters;
		var keep = Math.Max( 600f, VegetationFarCardRangeMeters ) + FarPlanAheadMeters + 600f;
		var keepSq = keep * keep;
		List<TerrainChunkCoord> drop = null;
		foreach ( var coord in _farPlans.Keys )
		{
			var cx = -worldRadius + ((coord.X + 0.5f) * chunkSize) - cam.x;
			var cy = -worldRadius + ((coord.Y + 0.5f) * chunkSize) - cam.y;
			if ( (cx * cx) + (cy * cy) > keepSq )
				(drop ??= new List<TerrainChunkCoord>()).Add( coord );
		}

		if ( drop is null )
			return;
		foreach ( var coord in drop )
		{
			_farPlans.Remove( coord );
			_farBatches.Remove( coord );
			_farShownSince.Remove( coord );
			_farLeavingSince.Remove( coord );
		}
		_farDirty = true;
	}

	void DestroyFarTreeCards()
	{
		_farShownSince.Clear();
		_farLeavingSince.Clear();
		if ( _farHookCamera is not null && _farHookCamera.IsValid() )
		{
			if ( _farCommands is not null )
				_farHookCamera.RemoveCommandList( _farCommands );
		}
		_farCommands?.Reset();
		_farHandoverDue.Clear();
		_farDirty = true;
		_farHookCamera = null;
		_farActive = false;
		_farPlans.Clear();
		_farBatches.Clear();
		_farPlanInFlight.Clear();
		_farPlanWanted.Clear();
		_farPlanTasks.Clear();
	}
}
