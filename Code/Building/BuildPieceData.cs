using System.Collections.Generic;

namespace Survival;

public sealed class BuildPieceCost
{
	public string ResourceId { get; set; } = string.Empty;
	public int Amount { get; set; }
}

public sealed class BuildPieceData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string Icon { get; set; } = string.Empty;
	public string Prefab { get; set; } = string.Empty;
	public string FallbackColor { get; set; } = "0.55,0.52,0.48,1";
	/// <summary>Menu tool entry (repair) — no ghost prefab placement.</summary>
	public bool IsRepairTool { get; set; }
	/// <summary>Structural material id (see <see cref="BuildMaterialData"/>). Empty = exempt from structural integrity (furniture, stations).</summary>
	public string MaterialId { get; set; } = string.Empty;
	public bool AllowTerrainPlacement { get; set; } = true;
	public BuildSnapRole AnchorSnapRole { get; set; } = BuildSnapRole.CornerNorthEast;
	public float HalfWidth { get; set; } = 30f;
	public float HalfHeight { get; set; } = 4f;
	public float HalfDepth { get; set; } = 30f;
	public List<BuildSnapPointData> SnapPoints { get; set; } = new();
	public List<BuildPieceCost> Costs { get; set; } = new();

	public Vector3 PlacementHalfExtents => new( HalfWidth, HalfHeight, HalfDepth );

	/// <summary>Participates in structural integrity (has a material).</summary>
	public bool IsStructural => !string.IsNullOrWhiteSpace( MaterialId );
}
