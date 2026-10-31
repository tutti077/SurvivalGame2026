namespace Survival;

using System.Collections.Generic;

/// <summary>
/// Lakes that land on a mountain (Mark 2026-10-30: "when water would spawn inside a mountain I don't think we should
/// slope the terrain toward the water, the water should sit on top of the mountain, like a frozen lake above the
/// world"). Runs once per world on the open-water raster, right after the lake threshold and speck pass:
/// <list type="bullet">
/// <item>a lake whose every cell lies on a mountain top (the profile's climb finished under it) becomes a <b>tarn</b>
/// with <c>Mountains - Mountain Lake Chance</c>: its surface sits just under the lowest ground around it (the lake cells
/// and a ~100 m ring, as the dry stages would build them), the bed is the usual bowl under that; a lake that loses the
/// roll is filled in by the mountain;</item>
/// <item>any other lake loses the cells where the mountain's climb stands (the mountain fills them) and keeps the rest at
/// sea level, so a lake ends at the steep foot instead of a bowl sloped into the wall; a remainder under the speck
/// size is filled too.</item>
/// </list>
/// Every lake that touches a mountain and survives is a <b>mountain-foot lake</b> (Mark 2026-10-30: "when a lake would
/// sit between 2 biomes … trim the mountain terrain a little so it's clearly not a cutout of the mountain where the lake
/// spawns and maybe throw a waterfall in here"): the <see cref="Stats.FootLakeCells"/> it keeps feed the river router,
/// which sends a river down the face into each one, and the <c>footLakeNear</c> raster gates the apron in
/// <see cref="TerrainPreviewCoastalSmoothing"/> that trims the mountain's foot down to a beach around them.
/// Two rasters come out of it. The <b>water level</b> of the nearest water for every land cell (sea unless a tarn is
/// nearest): the lake floor, shore bank, beach and wet band ease to it and the river router reads it. The <b>water
/// surface</b> the sea shader lifts its sheet to: the tarn level on the tarn and its ring only, sea everywhere else, so
/// no sheet floats over a saddle further along the top.
/// </summary>
public static class TerrainPreviewMountainLakes
{
	/// <summary>Lake cells on a climb at or above this share of the rise are filled by the mountain.</summary>
	const float ClimbCutRise01 = 0.15f;
	/// <summary>Every cell of a tarn must be this far up the mountain (the upper half: a cirque lake, since there is no top).</summary>
	const float OnTopRise01 = 0.5f;
	/// <summary>The ring of dry cells around a tarn that sets (and carries) its level.</summary>
	const float RingMeters = 100f;
	/// <summary>The tarn surface sits this far under the lowest ground of the lake and its ring.</summary>
	const float TarnLevelDropMeters = 0.5f;
	/// <summary>How far from a mountain-foot lake the apron gate reaches (the apron itself is Mountains - Lake Apron, at most 800 m).</summary>
	const float FootLakeGateMeters = 1200f;

	public readonly struct Stats
	{
		/// <summary>Inland lakes (not the ocean) before any of this.</summary>
		public int Lakes { get; init; }
		/// <summary>Lakes that sit on a mountain top now.</summary>
		public int Tarns { get; init; }
		/// <summary>Lakes the mountain filled in completely.</summary>
		public int Filled { get; init; }
		/// <summary>Lakes that lost their part on a mountain and kept the rest at sea level.</summary>
		public int Trimmed { get; init; }
		public float HighestTarnMeters { get; init; }
		/// <summary>Lakes at a mountain's foot (touching the mountain, not a tarn, not filled): trimmed ones and ones the mountain only reaches.</summary>
		public int FootLakes { get; init; }
		/// <summary>Per mountain-foot lake: the raster indices of its water cells after the trim — the river router aims a waterfall at each.</summary>
		public IReadOnlyList<int[]> FootLakeCells { get; init; }
	}

	/// <summary>
	/// Classifies every inland lake, fills <paramref name="mask"/> / <paramref name="openWater"/> where the mountain wins
	/// and builds the two level rasters (metres, absolute) plus the mountain-foot lake gate (<paramref name="footLakeNear"/>:
	/// land within <see cref="FootLakeGateMeters"/> of a foot lake). Needs the land disc, the thresholded open water and the mask.
	/// </summary>
	public static Stats Build(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		bool[] openWater,
		float[] mask,
		int res,
		float threshold01,
		out float[] waterLevelMeters,
		out float[] waterSurfaceMeters,
		out bool[] footLakeNear )
	{
		var count = res * res;
		var sea = settings.SeaLevelMeters;
		waterLevelMeters = new float[count];
		waterSurfaceMeters = new float[count];
		footLakeNear = new bool[count];
		Array.Fill( waterLevelMeters, sea );
		Array.Fill( waterSurfaceMeters, sea );
		if ( !settings.EnableMountainLayer || landDisk.Length != count || openWater.Length != count || mask.Length != count )
			return default;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var cellMeters = diameter / Math.Max( 1, res );
		var seed = settings.WorldSeed;
		var chance = Math.Clamp( settings.MountainTarnChance01, 0f, 1f );
		var speckCells = Math.Max( 4, (int)MathF.Round( MathF.Pow( Math.Max( 40f, settings.SpeckMinPatchDiameterMeters ) / cellMeters, 2f ) * 0.6f ) );
		var ring = Math.Max( 1, (int)MathF.Ceiling( RingMeters / cellMeters ) );

		var label = new int[count];
		Array.Fill( label, -1 );
		var cells = new List<int>();
		var queue = new Queue<int>();
		var fill = new List<int>();
		var climbCells = new List<int>();
		var ringCells = new HashSet<int>();
		var footLakes = new List<List<int>>();
		var lakes = 0;
		var tarns = 0;
		var filled = 0;
		var trimmed = 0;
		var highest = 0f;

		for ( var start = 0; start < count; start++ )
		{
			if ( label[start] >= 0 || !openWater[start] || !landDisk[start] )
				continue;
			if ( !Flood( start, landDisk, openWater, label, res, cells, queue ) )
				continue;   // joined to the rim: the ocean
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				break;

			lakes++;
			var onTop = 0;
			var touching = 0;
			climbCells.Clear();
			foreach ( var i in cells )
			{
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters( i % res, i / res, res, radius, diameter, out var wx, out var wy );
				if ( TerrainPreviewMountainSpawnMask.SampleMountainHeightInfluence01( settings, wx, wy ) <= 0.001f )
					continue;
				touching++;
				var rise = TerrainPreviewPipeline.SampleMountainRise01( settings, wx, wy );
				if ( rise >= OnTopRise01 )
					onTop++;
				if ( rise >= ClimbCutRise01 )
					climbCells.Add( i );
			}
			if ( touching == 0 )
				continue;   // an ordinary lowland lake
			if ( climbCells.Count == 0 )
			{
				footLakes.Add( new List<int>( cells ) );   // the mountain reaches it but does not stand in it
				continue;
			}

			if ( onTop == cells.Count )
			{
				var roll = TerrainPreviewNoise.Hash01( seed + 930, start % res, start / res );
				if ( roll < chance && TryLevelTarn( settings, landDisk, openWater, cells, ring, res, radius, diameter, ringCells, out var level ) && level > sea + 1f )
				{
					foreach ( var i in cells )
					{
						waterLevelMeters[i] = level;
						waterSurfaceMeters[i] = level;
					}
					foreach ( var i in ringCells )
					{
						waterLevelMeters[i] = level;
						waterSurfaceMeters[i] = level;
					}
					tarns++;
					highest = Math.Max( highest, level );
					continue;
				}
				fill.AddRange( cells );
				filled++;
				continue;
			}

			if ( cells.Count - climbCells.Count < speckCells )
			{
				fill.AddRange( cells );
				filled++;
			}
			else
			{
				fill.AddRange( climbCells );
				trimmed++;
				var kept = new List<int>( cells.Count - climbCells.Count );
				var cut = new HashSet<int>( climbCells );
				foreach ( var i in cells )
				{
					if ( !cut.Contains( i ) )
						kept.Add( i );
				}
				footLakes.Add( kept );
			}
		}

		Fill( fill, openWater, mask, threshold01 );

		// What the trim left may have split into pieces; drop the specks (the ocean never counts, nor a tarn).
		Array.Fill( label, -1 );
		fill.Clear();
		for ( var start = 0; start < count; start++ )
		{
			if ( label[start] >= 0 || !openWater[start] || !landDisk[start] )
				continue;
			if ( !Flood( start, landDisk, openWater, label, res, cells, queue ) )
				continue;
			if ( cells.Count < speckCells && waterSurfaceMeters[cells[0]] <= sea + 0.5f )
				fill.AddRange( cells );
		}
		Fill( fill, openWater, mask, threshold01 );

		SpreadNearestLevel( landDisk, openWater, res, waterLevelMeters, queue );

		// Mountain-foot lakes: what the speck pass left of each, and the gate around them for the apron.
		var footCells = new List<int[]>( footLakes.Count );
		foreach ( var lake in footLakes )
		{
			lake.RemoveAll( i => !openWater[i] );
			if ( lake.Count > 0 )
				footCells.Add( lake.ToArray() );
		}
		MarkNear( footCells, landDisk, res, Math.Max( 1, (int)MathF.Ceiling( FootLakeGateMeters / cellMeters ) ), footLakeNear, queue );

		return new Stats
		{
			Lakes = lakes,
			Tarns = tarns,
			Filled = filled,
			Trimmed = trimmed,
			HighestTarnMeters = highest,
			FootLakes = footCells.Count,
			FootLakeCells = footCells,
		};
	}

	/// <summary>Every land cell within <paramref name="steps"/> raster steps (4-neighbour) of a foot lake's water: the apron gate.</summary>
	static void MarkNear( List<int[]> lakes, bool[] landDisk, int res, int steps, bool[] near, Queue<int> queue )
	{
		var count = res * res;
		var depth = new int[count];
		Array.Fill( depth, -1 );
		queue.Clear();
		foreach ( var lake in lakes )
		{
			foreach ( var i in lake )
			{
				depth[i] = 0;
				near[i] = true;
				queue.Enqueue( i );
			}
		}
		while ( queue.Count > 0 )
		{
			var i = queue.Dequeue();
			if ( depth[i] >= steps )
				continue;
			var px = i % res;
			var py = i / res;
			Push( px > 0 ? i - 1 : -1, depth[i] + 1 );
			Push( px < res - 1 ? i + 1 : -1, depth[i] + 1 );
			Push( py > 0 ? i - res : -1, depth[i] + 1 );
			Push( py < res - 1 ? i + res : -1, depth[i] + 1 );
		}

		void Push( int n, int d )
		{
			if ( n < 0 || depth[n] >= 0 || !landDisk[n] )
				return;
			depth[n] = d;
			near[n] = true;
			queue.Enqueue( n );
		}
	}

	/// <summary>Flood one open-water component into <paramref name="cells"/>; false when it touches the outside of the land disc (ocean).</summary>
	static bool Flood( int start, bool[] landDisk, bool[] openWater, int[] label, int res, List<int> cells, Queue<int> queue )
	{
		cells.Clear();
		var ocean = false;
		label[start] = start;
		queue.Enqueue( start );
		while ( queue.Count > 0 )
		{
			var i = queue.Dequeue();
			cells.Add( i );
			var px = i % res;
			var py = i / res;
			if ( px == 0 || py == 0 || px == res - 1 || py == res - 1 )
				ocean = true;
			Visit( px > 0 ? i - 1 : -1 );
			Visit( px < res - 1 ? i + 1 : -1 );
			Visit( py > 0 ? i - res : -1 );
			Visit( py < res - 1 ? i + res : -1 );
		}
		return !ocean;

		void Visit( int n )
		{
			if ( n < 0 )
				return;
			if ( !landDisk[n] )
			{
				ocean = true;
				return;
			}
			if ( !openWater[n] || label[n] >= 0 )
				return;
			label[n] = start;
			queue.Enqueue( n );
		}
	}

	/// <summary>
	/// The tarn's level: the lowest ground the dry stages build over the lake cells and a ring of dry cells around them,
	/// a little lower. The ring is what keeps the water in — nothing in it is lower than the surface.
	/// </summary>
	static bool TryLevelTarn( TerrainPreviewSettings settings, bool[] landDisk, bool[] openWater, List<int> cells, int ring, int res, float radius, float diameter, HashSet<int> ringCells, out float level )
	{
		ringCells.Clear();
		foreach ( var i in cells )
		{
			var px = i % res;
			var py = i / res;
			for ( var oy = -ring; oy <= ring; oy++ )
			{
				for ( var ox = -ring; ox <= ring; ox++ )
				{
					var nx = px + ox;
					var ny = py + oy;
					if ( nx < 0 || ny < 0 || nx >= res || ny >= res )
						continue;
					var n = (ny * res) + nx;
					if ( !landDisk[n] || openWater[n] )
						continue;
					ringCells.Add( n );
				}
			}
		}

		var lowest = float.MaxValue;
		foreach ( var i in cells )
			lowest = Math.Min( lowest, GroundAt( i ) );
		foreach ( var i in ringCells )
			lowest = Math.Min( lowest, GroundAt( i ) );
		level = lowest - TarnLevelDropMeters;
		return lowest < float.MaxValue;

		float GroundAt( int i )
		{
			TerrainBiomeMapCoordinates.RasterPixelToWorldMeters( i % res, i / res, res, radius, diameter, out var wx, out var wy );
			return TerrainPreviewPipeline.SampleGroundIgnoringWater( settings, wx, wy );
		}
	}

	/// <summary>The mountain takes these cells: no longer open water, mask pulled under the shore threshold.</summary>
	static void Fill( List<int> cells, bool[] openWater, float[] mask, float threshold01 )
	{
		var dry = threshold01 - 0.08f;
		foreach ( var i in cells )
		{
			openWater[i] = false;
			mask[i] = Math.Min( mask[i], dry );
		}
	}

	/// <summary>Every land cell takes the level of the nearest water cell (multi-source breadth-first over the raster).</summary>
	static void SpreadNearestLevel( bool[] landDisk, bool[] openWater, int res, float[] level, Queue<int> queue )
	{
		var count = res * res;
		var seen = new bool[count];
		queue.Clear();
		for ( var i = 0; i < count; i++ )
		{
			if ( !landDisk[i] || openWater[i] )
			{
				seen[i] = true;
				queue.Enqueue( i );
			}
		}
		while ( queue.Count > 0 )
		{
			var i = queue.Dequeue();
			var px = i % res;
			var py = i / res;
			Push( px > 0 ? i - 1 : -1, level[i] );
			Push( px < res - 1 ? i + 1 : -1, level[i] );
			Push( py > 0 ? i - res : -1, level[i] );
			Push( py < res - 1 ? i + res : -1, level[i] );
		}

		void Push( int n, float value )
		{
			if ( n < 0 || seen[n] )
				return;
			seen[n] = true;
			level[n] = value;
			queue.Enqueue( n );
		}
	}
}
