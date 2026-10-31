using Sandbox;

namespace Survival;

/// <summary>
/// Dev diagnostics for chunk-border steps (console <c>terrain_seams</c>): every loaded chunk's meshed border heights are
/// compared with its +X and +Y neighbour's along the shared edge, and the pairs that disagree are logged with each
/// side's LOD, voxel state, water flag and fine-border sides — so a wall where a river crosses a chunk border can be
/// read off (which chunk is coarse, which one snapped, which one lost its channel) instead of guessed at.
/// Nothing here runs unless the command is typed.
/// </summary>
public sealed partial class TerrainWorldManager
{
	internal void LogBorderSeams( float minGapMeters )
	{
		var settings = _loadSettings;
		if ( settings is null )
		{
			Log.Info( "[TerrainSeams] no world loaded." );
			return;
		}

		var chunkSize = Math.Max( 32f, ChunkSizeMeters );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var pairs = 0;
		var bad = 0;
		var worstGap = 0f;
		foreach ( var (coord, a) in _loaded )
		{
			if ( a.GrassHeightsZ is null || a.GrassHeightsSide < 2 )
				continue;
			for ( var axis = 0; axis < 2; axis++ )
			{
				var alongX = axis == 0;   // alongX: A's +X column against B's -X column; else A's +Y row against B's -Y row
				var nc = alongX ? new TerrainChunkCoord( coord.X + 1, coord.Y ) : new TerrainChunkCoord( coord.X, coord.Y + 1 );
				if ( !_loaded.TryGetValue( nc, out var b ) || b.GrassHeightsZ is null || b.GrassHeightsSide < 2 )
					continue;
				pairs++;
				var gap = BorderGapMeters( a, b, alongX, out var tAlong );
				worstGap = Math.Max( worstGap, gap );
				if ( gap < minGapMeters )
					continue;
				bad++;
				var minX = -worldRadius + (coord.X * chunkSize);
				var minY = -worldRadius + (coord.Y * chunkSize);
				var px = alongX ? minX + chunkSize : minX + (tAlong * chunkSize);
				var py = alongX ? minY + (tAlong * chunkSize) : minY + chunkSize;
				Log.Info( $"[TerrainSeams] {gap:0.0} m step at ({px:0}, {py:0}) between {Describe( coord, a, settings, chunkSize )} and {Describe( nc, b, settings, chunkSize )}" );
			}
		}
		Log.Info( $"[TerrainSeams] {_loaded.Count} chunks, {pairs} shared borders, {bad} with a step >= {minGapMeters:0.0} m (worst {worstGap:0.0} m)." );
	}

	string Describe( TerrainChunkCoord c, LoadedChunk e, TerrainPreviewSettings settings, float chunkSize )
	{
		var fine = ResolveFineBorderSides( c, settings, chunkSize );
		var fineText = fine == 0 ? "none"
			: ((fine & TerrainMeshBuilder.BorderMinY) != 0 ? "-Y " : "") + ((fine & TerrainMeshBuilder.BorderMaxY) != 0 ? "+Y " : "")
			+ ((fine & TerrainMeshBuilder.BorderMinX) != 0 ? "-X " : "") + ((fine & TerrainMeshBuilder.BorderMaxX) != 0 ? "+X" : "");
		return $"[{c.X},{c.Y}: {e.VerticesPerSide} verts (grid {e.GrassHeightsSide}), water={ChunkTouchesWater( c, settings, chunkSize )}, fine={fineText.Trim()}{(e.Voxel is not null ? ", VOXEL" : "")}]";
	}

	/// <summary>
	/// Largest height difference (m) along the edge A shares with its +X (or +Y) neighbour B: A's last column (row)
	/// against B's first, sampled at the finer side's vertices with the coarser side read linearly between its own.
	/// Both grids are chunk-local engine z on the same ground plane, so they compare directly.
	/// </summary>
	static float BorderGapMeters( LoadedChunk a, LoadedChunk b, bool alongX, out float tAtWorst )
	{
		var na = a.GrassHeightsSide;
		var nb = b.GrassHeightsSide;
		var fine = Math.Max( na, nb );
		var worst = 0f;
		tAtWorst = 0f;
		for ( var k = 0; k < fine; k++ )
		{
			var t = k / (float)(fine - 1);
			var za = EdgeHeight( a.GrassHeightsZ, na, alongX, last: true, t );
			var zb = EdgeHeight( b.GrassHeightsZ, nb, alongX, last: false, t );
			var gap = Math.Abs( za - zb ) / TerrainWorldUnits.MetersToEngine( 1f );
			if ( gap > worst )
			{
				worst = gap;
				tAtWorst = t;
			}
		}
		return worst;
	}

	/// <summary>Height on one border line of a grid (the +X column / +Y row when <paramref name="last"/>, else -X / -Y) at fraction t along it.</summary>
	static float EdgeHeight( float[] heights, int n, bool alongX, bool last, float t )
	{
		var f = t * (n - 1);
		var i0 = Math.Clamp( (int)MathF.Floor( f ), 0, n - 1 );
		var i1 = Math.Min( i0 + 1, n - 1 );
		var u = f - i0;
		var edge = last ? n - 1 : 0;
		// alongX: the column x = edge, walking y; else the row y = edge, walking x
		var h0 = alongX ? heights[(i0 * n) + edge] : heights[(edge * n) + i0];
		var h1 = alongX ? heights[(i1 * n) + edge] : heights[(edge * n) + i1];
		return h0 + ((h1 - h0) * u);
	}
}
