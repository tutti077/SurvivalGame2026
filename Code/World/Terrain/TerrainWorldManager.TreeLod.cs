namespace Survival;

/// <summary>
/// Tree LOD switching. Large trees take their LOD from here instead of the engine's chain: each level is its own
/// single-LOD model beside the prefab's (<c>&lt;model&gt;_lod0 / _lod1 / _lod2 / _far.vmdl</c>, written by the elm
/// script), the level is chosen by distance in metres with hysteresis and a per-tree jitter, and a change is a
/// HARD swap of the renderer's model in one frame — the new level is in place the same frame the old one goes, no
/// frame without a tree and no dither (Mark 2026-10-20: "trees should be always present ... spawn in the new one
/// THEN remove the old, no fade"; fades are for the small stuff without LODs — rocks, sticks, saplings). A tree
/// whose renderer no longer shows one of its levels (felled, stump) drops out of the scheme.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Vegetation" ), Title( "Tree LOD 1 At (m)" ), Range( 20f, 600f ), Step( 5f ), Description( "Distance at which a large tree shows its second level (the elm: 70 % of the leaf cards)." )]
	public float TreeLod1Meters { get; set; } = 180f;

	[Property, Group( "Vegetation" ), Title( "Tree LOD 2 At (m)" ), Range( 40f, 900f ), Step( 5f ), Description( "Distance at which a large tree shows its third level (40 % of the cards)." )]
	public float TreeLod2Meters { get; set; } = 270f;

	[Property, Group( "Vegetation" ), Title( "Tree LOD 3 At (m)" ), Range( 60f, 1200f ), Step( 5f ), Description( "Distance at which a large tree shows its far level (20 % of the cards; the same mesh the far ring draws)." )]
	public float TreeLod3Meters { get; set; } = 330f;

	/// <summary>Levels only change once the distance is this far past the threshold (fraction), so a tree at a boundary never ping-pongs.</summary>
	const float TreeLodHysteresis = 0.08f;
	/// <summary>Seconds between distance checks of one chunk's trees (chunks are staggered).</summary>
	const float TreeLodCheckSeconds = 0.25f;

	sealed class TreeLodState
	{
		public GameObject Go;
		public ModelRenderer Renderer;
		public Model[] Levels;
		public int Level;
		/// <summary>World position in metres (trees never move).</summary>
		public Vector3 PosMeters;
		/// <summary>0.9-1.1 from the tree's position: its LOD distances are scaled by this so a forest never swaps as one wave (Mark 2026-10-20: looking down and rising, "trees popping").</summary>
		public float Jitter = 1f;
	}

	static float LodJitter( Vector3 posMeters )
	{
		var h = TerrainPreviewNoise.Hash01( 9101, (int)MathF.Floor( posMeters.x * 2f ), (int)MathF.Floor( posMeters.y * 2f ) );
		return 0.9f + (0.2f * h);
	}

	/// <summary>Per model: its four single-LOD models, or null when the model has no level files beside it. Keyed by every level too.</summary>
	readonly Dictionary<Model, Model[]> _lodSetByModel = new();
	int _lodCheckCursor;

	/// <summary>The level models for a prefab's model (cached), or null when it has no _lod0.._far siblings.</summary>
	Model[] ResolveLodSet( Model model )
	{
		if ( model is null )
			return null;
		if ( _lodSetByModel.TryGetValue( model, out var set ) )
			return set;

		set = null;
		var path = model.ResourcePath;
		if ( !string.IsNullOrEmpty( path ) && path.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) )
		{
			var stem = path[..^5];
			var paths = new[] { stem + "_lod0.vmdl", stem + "_lod1.vmdl", stem + "_lod2.vmdl", stem + "_far.vmdl" };
			var all = true;
			foreach ( var p in paths )
			{
				if ( !FileSystem.Mounted.FileExists( p ) && !FileSystem.Mounted.FileExists( p + "_c" ) )
				{
					all = false;
					break;
				}
			}

			if ( all )
			{
				set = new Model[4];
				for ( var i = 0; i < 4; i++ )
				{
					set[i] = Model.Load( paths[i] );
					if ( set[i] is null || set[i].IsError )
					{
						set = null;
						break;
					}
				}
			}
		}

		_lodSetByModel[model] = set;
		if ( set is not null )
		{
			foreach ( var level in set )
				_lodSetByModel[level] = set;
		}
		return set;
	}

	int TreeLodLevelFor( float distanceMeters, int current )
	{
		// Hysteresis: stepping up needs the distance past the threshold by the margin, stepping down needs it under by the margin.
		var d1 = TreeLod1Meters;
		var d2 = Math.Max( d1 + 5f, TreeLod2Meters );
		var d3 = Math.Max( d2 + 5f, TreeLod3Meters );
		var level = distanceMeters < d1 ? 0 : distanceMeters < d2 ? 1 : distanceMeters < d3 ? 2 : 3;
		if ( level == current )
			return current;

		var threshold = level > current
			? (current == 0 ? d1 : current == 1 ? d2 : d3) * (1f + TreeLodHysteresis)
			: (current == 1 ? d1 : current == 2 ? d2 : d3) * (1f - TreeLodHysteresis);
		if ( level > current && distanceMeters < threshold )
			return current;
		if ( level < current && distanceMeters > threshold )
			return current;
		return level;
	}

	/// <summary>Adopt a chunk's large trees once their clones all stand: the right level at once (no fade), then distance checks take over.</summary>
	void CollectLodTrees( LoadedChunk entry )
	{
		entry.LodTrees.Clear();
		if ( entry.GameObject is null || !entry.GameObject.IsValid() )
			return;

		TryGetStreamTransform( out var camEngine, out _ );
		var cam = TerrainWorldUnits.EngineToMeters( camEngine );
		foreach ( var child in entry.GameObject.Children )
		{
			if ( !child.Name.StartsWith( "veg_", StringComparison.Ordinal ) )
				continue;

			var renderer = child.Components.Get<ModelRenderer>();
			if ( renderer is null || !renderer.IsValid() )
				continue;

			var set = ResolveLodSet( renderer.Model );
			if ( set is null )
				continue;

			var pos = TerrainWorldUnits.EngineToMeters( child.WorldPosition );
			var jitter = LodJitter( pos );
			var level = TreeLodLevelFor( (pos - cam).Length * jitter, 3 );
			renderer.Model = set[level];
			entry.LodTrees.Add( new TreeLodState
			{
				Go = child,
				Renderer = renderer,
				Levels = set,
				Level = level,
				PosMeters = pos,
				Jitter = jitter,
			} );
		}

		entry.NextLodCheck = Time.Now + (TreeLodCheckSeconds * (((entry.Coord.X * 31) + (entry.Coord.Y * 17)) & 7) / 8f);
	}

	/// <summary>Every frame: check a slice of the chunks' trees for a level change (a hard model swap).</summary>
	void TickTreeLods()
	{
		if ( !TryGetStreamTransform( out var camEngine, out _ ) || _loaded.Count == 0 )
			return;
		var cam = TerrainWorldUnits.EngineToMeters( camEngine );
		var now = Time.Now;

		// A slice of chunks per frame; each chunk re-checks every TreeLodCheckSeconds.
		var perFrame = Math.Max( 1, _loaded.Count / 12 );
		var checkedCount = 0;
		var index = 0;
		var start = _lodCheckCursor % Math.Max( 1, _loaded.Count );
		foreach ( var entry in _loaded.Values )
		{
			if ( index++ < start )
				continue;
			if ( checkedCount++ >= perFrame )
				break;
			if ( entry.LodTrees.Count == 0 || now < entry.NextLodCheck )
				continue;

			entry.NextLodCheck = now + TreeLodCheckSeconds;
			for ( var i = entry.LodTrees.Count - 1; i >= 0; i-- )
			{
				var state = entry.LodTrees[i];
				if ( state.Renderer is null || !state.Renderer.IsValid() || state.Renderer.Model is null || !_lodSetByModel.ContainsKey( state.Renderer.Model ) )
				{
					// Felled, broken or gone: the engine (or ChopableTree) owns it now.
					entry.LodTrees.RemoveAt( i );
					continue;
				}

				var desired = TreeLodLevelFor( (state.PosMeters - cam).Length * state.Jitter, state.Level );
				if ( desired != state.Level )
				{
					state.Renderer.Model = state.Levels[desired];   // one frame: the new level stands as the old one goes
					state.Level = desired;
					Stats.LodFades++;
				}
			}
		}
		_lodCheckCursor = (start + checkedCount) % Math.Max( 1, _loaded.Count );
	}

}
