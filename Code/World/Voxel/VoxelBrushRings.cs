namespace Survival;

/// <summary>
/// The debug-overlay circles the brushes draw where they will act, shared by the scene digger (<see cref="VoxelDigger"/>)
/// and the pawn's tools (<see cref="ToolPickaxe"/>, <see cref="ToolHoe"/>): the rake's ring on the plane it will lay, and a
/// disc outline lying on a surface (the pickaxe's chip, the hoe's paint). Positions in engine units, radii in metres.
/// </summary>
public static class VoxelBrushRings
{
	const int Segments = 48;

	public static readonly Color FlatColor = new( 0.3f, 0.7f, 1f );
	public static readonly Color SlopeColor = Color.Orange;
	public static readonly Color BlockedColor = Color.Red;

	/// <summary>The circle the rake will level, drawn on the plane it will lay: blue = flat, orange = slope, red = nothing to level here. Returns whether a plane was planned.</summary>
	public static bool DrawRakeRing( Scene scene, IVoxelBrushTarget target, Vector3 hitEngine, float radiusMeters, bool slope )
	{
		VoxelPlane plane = default;
		var planned = target is not null && target.TryPlanFlatten( hitEngine, radiusMeters, slope, out plane );
		var color = !planned ? BlockedColor : slope ? SlopeColor : FlatColor;
		var hitM = TerrainWorldUnits.EngineToMeters( hitEngine );
		Vector3 Point( int k )
		{
			var a = k * (MathF.PI * 2f / Segments);
			var x = hitM.x + (MathF.Cos( a ) * radiusMeters);
			var y = hitM.y + (MathF.Sin( a ) * radiusMeters);
			var z = planned ? plane.ZAt( x, y ) : hitM.z;
			return TerrainWorldUnits.MetersToEngine( new Vector3( x, y, z + 0.05f ) );
		}
		Polyline( scene, Point, color );
		return planned;
	}

	/// <summary>A circle of the radius lying on the surface at a point: its plane is perpendicular to <paramref name="normal"/>, lifted a few centimetres off the ground so it is not z-fought away.</summary>
	public static void DrawSurfaceRing( Scene scene, Vector3 centerEngine, Vector3 normal, float radiusMeters, Color color )
	{
		var n = normal.LengthSquared > 1e-6f ? normal.Normal : Vector3.Up;
		var t = MathF.Abs( n.z ) < 0.9f ? Vector3.Cross( n, Vector3.Up ).Normal : Vector3.Cross( n, Vector3.Forward ).Normal;
		var b = Vector3.Cross( n, t ).Normal;
		var r = TerrainWorldUnits.MetersToEngine( radiusMeters );
		var c = centerEngine + (n * TerrainWorldUnits.MetersToEngine( 0.05f ));
		Vector3 Point( int k )
		{
			var a = k * (MathF.PI * 2f / Segments);
			return c + (t * (MathF.Cos( a ) * r)) + (b * (MathF.Sin( a ) * r));
		}
		Polyline( scene, Point, color );
	}

	/// <summary>The hoe's square patch: an axis-aligned square of half side <paramref name="halfMeters"/>, its centre snapped to the 0.5 m voxel grid like the stroke itself, lying flat at the hit height.</summary>
	public static void DrawSurfaceSquare( Scene scene, Vector3 centerEngine, float halfMeters, Color color )
	{
		var cell = TerrainWorldUnits.MetersToEngine( 0.5f );
		var cx = MathF.Round( centerEngine.x / cell ) * cell;
		var cy = MathF.Round( centerEngine.y / cell ) * cell;
		var z = centerEngine.z + TerrainWorldUnits.MetersToEngine( 0.05f );
		var h = TerrainWorldUnits.MetersToEngine( halfMeters );
		Vector3 Point( int k )
		{
			// 4 sides, Segments / 4 steps each, walked round the square
			var side = (k * 4) / Segments % 4;
			var t = ((k * 4) % Segments) / (float)Segments;
			return side switch
			{
				0 => new Vector3( cx - h + (2f * h * t), cy - h, z ),
				1 => new Vector3( cx + h, cy - h + (2f * h * t), z ),
				2 => new Vector3( cx + h - (2f * h * t), cy + h, z ),
				_ => new Vector3( cx - h, cy + h - (2f * h * t), z ),
			};
		}
		Polyline( scene, Point, color );
	}

	static void Polyline( Scene scene, Func<int, Vector3> point, Color color )
	{
		if ( scene is null || !scene.IsValid() )
			return;
		var overlay = scene.DebugOverlay;
		var prev = point( 0 );
		for ( var k = 1; k <= Segments; k++ )
		{
			var next = point( k );
			overlay.Line( prev, next, color, 0f );
			prev = next;
		}
	}
}
