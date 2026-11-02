namespace Survival;

/// <summary>
/// The voxel console commands. They brush whatever the active scene offers (<see cref="VoxelBrushTargets.Find"/>): the
/// prototype in voxelTest, the live world in terrainTest. Every row is in <c>help</c> (ConsoleHelp.cs).
/// </summary>
public static class VoxelCommands
{
	static IVoxelBrushTarget Find()
	{
		var t = VoxelBrushTargets.Find( global::Sandbox.Game.ActiveScene );
		if ( t is null )
			Log.Warning( "[Voxel] nothing to dig in the active scene (open voxelTest or terrainTest)" );
		return t;
	}

	static bool AimPoint( out Vector3 engine )
	{
		engine = default;
		var scene = global::Sandbox.Game.ActiveScene;
		var cam = scene.IsValid() ? scene.Camera : null;
		if ( cam is null || !cam.IsValid() )
			return false;
		var tr = scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + (cam.WorldRotation.Forward * 4000f) ).WithoutTags( "player", "water" ).Run();
		if ( !tr.Hit )
			return false;
		engine = tr.HitPosition;
		return true;
	}

	/// <summary>Usage: <c>voxel_dig [r] [x y z]</c> — dig a puck of radius r (m, default 1.5) at world metres x y z, or where the camera looks.</summary>
	[ConCmd( "voxel_dig" )]
	public static void Dig( float radius = 1.5f, float x = float.NaN, float y = float.NaN, float z = float.NaN ) => Brush( radius, x, y, z, VoxelBrushMode.Dig );

	/// <summary>Usage: <c>voxel_fill [r] [x y z]</c> — fill a puck of ground of radius r at world metres x y z, or where the camera looks.</summary>
	[ConCmd( "voxel_fill" )]
	public static void Fill( float radius = 1.5f, float x = float.NaN, float y = float.NaN, float z = float.NaN ) => Brush( radius, x, y, z, VoxelBrushMode.Fill );

	/// <summary>Usage: <c>voxel_flatten [r] [x y z]</c> — the rake, flat: level the ground within radius r to the surrounding surface height, at world metres x y z or where the camera looks.</summary>
	[ConCmd( "voxel_flatten" )]
	public static void Flatten( float radius = 2.5f, float x = float.NaN, float y = float.NaN, float z = float.NaN ) => Brush( radius, x, y, z, VoxelBrushMode.Flatten );

	/// <summary>Usage: <c>voxel_slope [r] [x y z]</c> — the rake, slanted: lay the ground within radius r on the surrounding slope (45° at most), at world metres x y z or where the camera looks.</summary>
	[ConCmd( "voxel_slope" )]
	public static void Slope( float radius = 2.5f, float x = float.NaN, float y = float.NaN, float z = float.NaN ) => Brush( radius, x, y, z, VoxelBrushMode.Slope );

	static void Brush( float radius, float x, float y, float z, VoxelBrushMode mode, bool square = false, float depth = 0f )
	{
		var t = Find();
		if ( t is null )
			return;
		Vector3 center;
		if ( float.IsNaN( x ) || float.IsNaN( y ) || float.IsNaN( z ) )
		{
			if ( !AimPoint( out center ) )
			{
				Log.Warning( "[Voxel] nothing under the camera's aim" );
				return;
			}
			var dir = global::Sandbox.Game.ActiveScene.Camera.WorldRotation.Forward;
			var bite = mode == VoxelBrushMode.Dig ? 0.35f : mode == VoxelBrushMode.Fill ? -0.35f : 0f;
			center += dir * (TerrainWorldUnits.MetersToEngine( radius ) * bite);
		}
		else
			center = TerrainWorldUnits.MetersToEngine( new Vector3( x, y, z ) );
		t.RequestBrush( center, radius, mode, square, depth );
		var m = TerrainWorldUnits.EngineToMeters( center );
		Log.Info( $"[Voxel] {mode} r {radius:0.00} m at {m.x:0.0},{m.y:0.0},{m.z:0.0} m" );
	}

	/// <summary>Usage: <c>voxel_chip [r] [depth]</c> - the pickaxe's stroke where the camera looks: a disc of radius r (m, default 0.6) lying on the surface, cut depth m (default 0.3) into it.</summary>
	[ConCmd( "voxel_chip" )]
	public static void Chip( float radius = 0.6f, float depth = 0.3f )
	{
		var t = Find();
		if ( t is null )
			return;
		var scene = global::Sandbox.Game.ActiveScene;
		var cam = scene.IsValid() ? scene.Camera : null;
		if ( cam is null || !cam.IsValid() )
			return;
		var tr = scene.Trace.Ray( cam.WorldPosition, cam.WorldPosition + (cam.WorldRotation.Forward * 4000f) ).WithoutTags( "player", "water" ).Run();
		if ( !tr.Hit )
		{
			Log.Warning( "[Voxel] nothing under the camera's aim" );
			return;
		}
		t.RequestChip( tr.HitPosition, tr.Normal, radius, depth );
		var m = TerrainWorldUnits.EngineToMeters( tr.HitPosition );
		Log.Info( $"[Voxel] Chip r {radius:0.00} m, {depth:0.00} m deep at {m.x:0.0},{m.y:0.0},{m.z:0.0} m, normal {tr.Normal.x:0.00},{tr.Normal.y:0.00},{tr.Normal.z:0.00}" );
	}

	/// <summary>Usage: <c>voxel_paint &lt;grass|bare|tilled|cobble&gt; [r] [circle|square] [x y z]</c> - the hoe's paint: the ground inside radius r (m, default 1.5; a square of side 2 r with square) becomes the biome's grass or bare soil, at world metres x y z or where the camera looks.</summary>
	[ConCmd( "voxel_paint" )]
	public static void Paint( string kind = "grass", float radius = 1.5f, string shape = "circle", float x = float.NaN, float y = float.NaN, float z = float.NaN )
	{
		var k = (kind ?? string.Empty).Trim().ToLowerInvariant();
		var mode = k == "bare" ? VoxelBrushMode.PaintBare : k == "tilled" ? VoxelBrushMode.PaintTilled : k == "cobble" ? VoxelBrushMode.PaintCobble : VoxelBrushMode.PaintGrass;
		var square = string.Equals( shape?.Trim(), "square", StringComparison.OrdinalIgnoreCase );
		Brush( radius, x, y, z, mode, square );
	}

	/// <summary>Usage: <c>voxel_raise [r] [h] [circle|square]</c> - the hoe's raise where the camera looks: the ground inside radius r (m, default 1.5) comes up by one layer h m thick (default 0.25).</summary>
	[ConCmd( "voxel_raise" )]
	public static void Raise( float radius = 1.5f, float height = 0.25f, string shape = "circle" )
	{
		var square = string.Equals( shape?.Trim(), "square", StringComparison.OrdinalIgnoreCase );
		Brush( radius, float.NaN, float.NaN, float.NaN, VoxelBrushMode.Raise, square, height );
	}

	/// <summary>Usage: <c>voxel_brush &lt;r&gt;</c> — set the digger's brush radius (m).</summary>
	[ConCmd( "voxel_brush" )]
	public static void BrushRadius( float radius )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var d = scene.IsValid() ? scene.GetAllComponents<VoxelDigger>().FirstOrDefault() : null;
		if ( d is null || !d.IsValid() )
			return;
		d.BrushRadiusMeters = Math.Clamp( radius, 0.25f, 4f );
		Log.Info( $"[Voxel] brush radius {d.BrushRadiusMeters:0.00} m" );
	}

	/// <summary>Usage: <c>voxel_info</c> — the field(s), chunk counts, vertices, queue and the last build / main-thread times.</summary>
	[ConCmd( "voxel_info" )]
	public static void Info()
	{
		var t = Find();
		if ( t is not null )
			Log.Info( t.DescribeVoxels() );
	}

	/// <summary>Usage: <c>voxel_probe &lt;x&gt; &lt;y&gt; [axis]</c> — trace straight down either side of a line through world metres x y (axis y: along x = const; default: along y = const) and log what is hit: object, ground z, normal. For checking a seam between two voxel columns.</summary>
	[ConCmd( "voxel_probe" )]
	public static void Probe( float x, float y, string axis = "y" )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;
		foreach ( var d in new[] { -1f, -0.5f, -0.2f, -0.05f, 0.05f, 0.2f, 0.5f, 1f } )
		{
			var px = axis == "x" ? x + d : x;
			var py = axis == "x" ? y : y + d;
			var top = TerrainWorldUnits.MetersToEngine( new Vector3( px, py, 400f ) );
			var tr = scene.Trace.Ray( top, top - new Vector3( 0f, 0f, TerrainWorldUnits.MetersToEngine( 800f ) ) ).WithoutTags( "player", "water" ).Run();
			if ( !tr.Hit )
			{
				Log.Info( $"[Voxel] probe {px:0.00},{py:0.00}: nothing" );
				continue;
			}
			var hit = TerrainWorldUnits.EngineToMeters( tr.HitPosition );
			Log.Info( $"[Voxel] probe {px:0.00},{py:0.00}: z {hit.z:0.000} m on '{tr.GameObject?.Name}' normal {tr.Normal.x:0.00},{tr.Normal.y:0.00},{tr.Normal.z:0.00}" );
		}
	}

	/// <summary>Usage: <c>voxel_reset</c> — forget every saved edit and put the original ground back.</summary>
	[ConCmd( "voxel_reset" )]
	public static void Reset() => Find()?.ResetVoxels();
}
