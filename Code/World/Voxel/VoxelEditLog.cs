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

	[JsonIgnore] public VoxelBrushMode BrushMode => Mode >= 0 ? (VoxelBrushMode)Mode : (Dig ? VoxelBrushMode.Dig : VoxelBrushMode.Fill);
	[JsonIgnore] public bool IsRake => BrushMode is VoxelBrushMode.Flatten or VoxelBrushMode.Slope;
	[JsonIgnore] public Vector3 CenterMeters => new( X, Y, Z );
	[JsonIgnore] public VoxelPlane Plane => new( new Vector2( X, Y ), PlaneZ, new Vector2( PlaneGx, PlaneGy ) );

	/// <summary>How far (m) in x / y the stroke can change samples: the brush plus the field's clamp band.</summary>
	[JsonIgnore] public float ReachMeters => R + VoxelField.ClampMeters;

	public static VoxelEdit Brush( Vector3 centerMeters, float r, VoxelBrushMode mode )
		=> new() { X = centerMeters.x, Y = centerMeters.y, Z = centerMeters.z, R = r, Dig = mode == VoxelBrushMode.Dig, Mode = (int)mode };

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

/// <summary>
/// Something the digger and the voxel console commands can brush: the standalone prototype (<see cref="VoxelTerrain"/>,
/// voxelTest) or the live world (<see cref="TerrainWorldManager"/>, which swaps touched chunks for voxel ground). Positions
/// are engine units at this boundary; both convert once to metres inside.
/// </summary>
public interface IVoxelBrushTarget
{
	bool IsReady { get; }
	/// <summary>The rake's plan at a world point for the preview ring — the same plane the stroke will lay. False when there is nothing to level.</summary>
	bool TryPlanFlatten( Vector3 centerEngine, float radiusMeters, bool slope, out VoxelPlane plane );
	/// <summary>Client intent: a brush at a world point. Offline / host applies at once, a client asks the host.</summary>
	void RequestBrush( Vector3 centerEngine, float radiusMeters, VoxelBrushMode mode );
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
