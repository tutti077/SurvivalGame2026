using System.Text.Json;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>
/// One saved brush stroke (metres). The log is replayed in order on load — exact, tiny, and the chunk data never has to
/// be written. A rake stroke (<see cref="VoxelBrushMode.Flatten"/> / <see cref="VoxelBrushMode.Slope"/>) carries the plane it
/// laid, so the replay lands the same ground whatever the surroundings look like by then (planning again on load would
/// read neighbours that are not voxel yet, or were edited later).
/// </summary>
public sealed class VoxelEdit
{
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	public float R { get; set; }
	/// <summary>Older logs: true = dig, false = fill (before <see cref="Mode"/>).</summary>
	public bool Dig { get; set; }
	/// <summary>VoxelBrushMode; -1 in logs written before the rake existed (then <see cref="Dig"/> decides).</summary>
	public int Mode { get; set; } = -1;
	/// <summary>Rake strokes: the plane's height at the centre and its gradient (rise per metre in x and y); <see cref="HasPlane"/> says whether they were logged.</summary>
	public bool HasPlane { get; set; }
	public float PlaneZ { get; set; }
	public float PlaneGx { get; set; }
	public float PlaneGy { get; set; }
	/// <summary>Chip strokes (the pickaxe): the surface normal the disc lies on (unit). Up on every other mode.</summary>
	public float Nx { get; set; }
	public float Ny { get; set; }
	public float Nz { get; set; } = 1f;
	/// <summary>Chip strokes: how deep (m) the disc cuts under the surface. Raise strokes: how thick (m) the layer is. 0 on every other mode.</summary>
	public float D { get; set; }
	/// <summary>Paint strokes: an axis-aligned square of side 2 R (snapped to the cell grid) instead of the circle.</summary>
	public bool Sq { get; set; }

	[JsonIgnore] public VoxelBrushMode BrushMode => Mode >= 0 ? (VoxelBrushMode)Mode : (Dig ? VoxelBrushMode.Dig : VoxelBrushMode.Fill);
	[JsonIgnore] public bool IsRake => BrushMode is VoxelBrushMode.Flatten or VoxelBrushMode.Slope;
	[JsonIgnore] public bool IsChip => BrushMode == VoxelBrushMode.Chip;
	[JsonIgnore] public bool IsPaint => BrushMode is VoxelBrushMode.PaintGrass or VoxelBrushMode.PaintBare or VoxelBrushMode.PaintTilled or VoxelBrushMode.PaintCobble;
	[JsonIgnore] public bool IsRaise => BrushMode == VoxelBrushMode.Raise;
	[JsonIgnore] public VoxelPaint Paint => BrushMode switch
	{
		VoxelBrushMode.PaintGrass => VoxelPaint.Grass,
		VoxelBrushMode.PaintBare => VoxelPaint.Bare,
		VoxelBrushMode.PaintTilled => VoxelPaint.Tilled,
		VoxelBrushMode.PaintCobble => VoxelPaint.Cobble,
		_ => VoxelPaint.None,
	};
	[JsonIgnore] public Vector3 CenterMeters => new( X, Y, Z );
	[JsonIgnore] public Vector3 Normal => new( Nx, Ny, Nz );
	[JsonIgnore] public VoxelPlane Plane => new( new Vector2( X, Y ), PlaneZ, new Vector2( PlaneGx, PlaneGy ) );

	/// <summary>How far (m) in x / y the stroke can change samples: the brush (a chip's tilted puck: its bounding sphere) plus the field's clamp band.</summary>
	[JsonIgnore] public float ReachMeters => IsChip ? MathF.Sqrt( (R * R) + (MathF.Max( 2f * D, R ) * MathF.Max( 2f * D, R )) ) + VoxelField.ClampMeters : R + VoxelField.ClampMeters;

	public static VoxelEdit Brush( Vector3 centerMeters, float r, VoxelBrushMode mode, bool square = false, float depth = 0f )
		=> new() { X = centerMeters.x, Y = centerMeters.y, Z = centerMeters.z, R = r, Dig = mode == VoxelBrushMode.Dig, Mode = (int)mode, Sq = square, D = mode == VoxelBrushMode.Raise ? depth : 0f };

	/// <summary>The highest <see cref="VoxelBrushMode"/> value a wire message may carry.</summary>
	public const int MaxMode = (int)VoxelBrushMode.PaintCobble;

	/// <summary>A request / broadcast row from its wire fields (mode clamped; the normal and depth only matter to a chip).</summary>
	public static VoxelEdit FromWire( Vector3 centerMeters, float r, int mode, Vector3 normal, float depth, bool square )
	{
		var m = (VoxelBrushMode)Math.Clamp( mode, 0, MaxMode );
		return m == VoxelBrushMode.Chip ? Chip( centerMeters, normal, r, depth ) : Brush( centerMeters, r, m, square, depth );
	}

	/// <summary>The pickaxe's disc: centre on the surface, <paramref name="normal"/> the surface normal there, radius r, cutting <paramref name="depth"/> in.</summary>
	public static VoxelEdit Chip( Vector3 centerMeters, Vector3 normal, float r, float depth )
	{
		var n = normal.LengthSquared > 1e-6f ? normal.Normal : Vector3.Up;
		var e = Brush( centerMeters, r, VoxelBrushMode.Chip );
		e.Nx = n.x;
		e.Ny = n.y;
		e.Nz = n.z;
		e.D = depth;
		return e;
	}

	public static VoxelEdit Rake( Vector3 centerMeters, float r, VoxelBrushMode mode, VoxelPlane plane )
	{
		var e = Brush( centerMeters, r, mode );
		e.HasPlane = true;
		e.PlaneZ = plane.ZAt( centerMeters.x, centerMeters.y );
		e.PlaneGx = plane.Gradient.x;
		e.PlaneGy = plane.Gradient.y;
		return e;
	}
}

/// <summary>The edit log file (<c>voxel_edits.json</c> in the world's save folder).</summary>
public sealed class VoxelEditLog
{
	public int Version { get; set; } = 1;
	public List<VoxelEdit> Edits { get; set; } = new();

	public const string FileName = "voxel_edits.json";
	static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

	static string PathFor( string worldName ) => $"{WorldSaveIO.GetWorldFolder( worldName )}/{FileName}";

	/// <summary>Read the world's log into <paramref name="into"/> (cleared first). Missing file = no edits.</summary>
	public static void Load( string worldName, List<VoxelEdit> into )
	{
		into.Clear();
		var path = PathFor( worldName );
		try
		{
			if ( !FileSystem.Data.FileExists( path ) )
				return;
			var log = JsonSerializer.Deserialize<VoxelEditLog>( FileSystem.Data.ReadAllText( path ), Json );
			if ( log?.Edits is not null )
				into.AddRange( log.Edits );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Voxel] could not read {path}: {e.Message}" );
		}
	}

	public static void Save( string worldName, List<VoxelEdit> edits )
	{
		var path = PathFor( worldName );
		try
		{
			WorldSaveIO.EnsureWorldDirectory( worldName );
			FileSystem.Data.WriteAllText( path, JsonSerializer.Serialize( new VoxelEditLog { Edits = edits }, Json ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Voxel] could not write {path}: {e.Message}" );
		}
	}
}

/// <summary>One logged stroke onto one field - the same code for the prototype, every world column, the replay on load and every peer.</summary>
public static class VoxelReplay
{
	/// <summary>Apply <paramref name="e"/> to <paramref name="field"/>; returns the mesh chunks that changed. A rake row without its plane plans again on this field.</summary>
	public static HashSet<(int X, int Y, int Z)> Apply( VoxelField field, VoxelEdit e, float flatness )
	{
		if ( e.IsRake && e.HasPlane )
			return field.ApplyPlane( e.CenterMeters, e.R, e.Plane );
		if ( e.IsChip )
			return field.ApplyChip( e.CenterMeters, e.Normal, e.R, e.D );
		if ( e.IsPaint )
			return field.ApplyPaint( e.CenterMeters, e.R, e.Paint, e.Sq );
		if ( e.IsRaise )
			return field.ApplyRaise( e.CenterMeters, e.R, e.D, e.Sq );
		return field.ApplyBrush( e.CenterMeters, e.R, e.BrushMode, flatness );
	}
}

/// <summary>
/// Something the digger, the pawn's tools (through <see cref="PlayerTerraform"/>) and the voxel console commands can brush:
/// the standalone prototype (<see cref="VoxelTerrain"/>, voxelTest) or the live world (<see cref="TerrainWorldManager"/>, which
/// swaps touched chunks for voxel ground). Positions are engine units at this boundary; both convert once to metres inside.
/// </summary>
public interface IVoxelBrushTarget
{
	bool IsReady { get; }
	/// <summary>The rake's plan at a world point for the preview ring — the same plane the stroke will lay. False when there is nothing to level.</summary>
	bool TryPlanFlatten( Vector3 centerEngine, float radiusMeters, bool slope, out VoxelPlane plane );
	/// <summary>Client intent: a dig / fill / rake / paint brush at a world point. Offline / host applies at once and returns whether the stroke landed; a client asks the host (true = sent).</summary>
	bool RequestBrush( Vector3 centerEngine, float radiusMeters, VoxelBrushMode mode, bool square = false, float depth = 0f );
	/// <summary>Tilled coverage (0..1) of the ground under a world point (engine units): 0 where no voxel ground stands. Farming sows from 0.5 up.</summary>
	float TilledAt( Vector3 pointEngine );
	/// <summary>Rock share (0..1) of the ground at a world point (engine units) - what the surface there is painted as (<see cref="VoxelMesher.RockShare"/>). The pickaxe yields stone from 0.5 up, dirt below.</summary>
	float RockAt( Vector3 pointEngine );
	/// <summary>Client intent: the pickaxe's disc at a surface point with its normal (engine units / direction), radius and depth in metres. Same return as <see cref="RequestBrush"/>.</summary>
	bool RequestChip( Vector3 centerEngine, Vector3 normal, float radiusMeters, float depthMeters );
	/// <summary>One line for <c>voxel_info</c>.</summary>
	string DescribeVoxels();
	/// <summary>Forget every saved edit and put the original ground back.</summary>
	void ResetVoxels();
}

public static class VoxelBrushTargets
{
	/// <summary>The brush target in the active scene: the prototype when the scene has one, else the live world.</summary>
	public static IVoxelBrushTarget Find( Scene scene )
	{
		if ( scene is null || !scene.IsValid() )
			return null;
		var proto = scene.GetAllComponents<VoxelTerrain>().FirstOrDefault();
		if ( proto is not null && proto.IsValid() )
			return proto;
		var world = scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		return world is not null && world.IsValid() ? world : null;
	}
}
