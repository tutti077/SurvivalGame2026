namespace Survival;

/// <summary>
/// Structural material class for build pieces (Valheim-style support propagation).
/// Values live in <c>Assets/data/build_pieces.json</c> under the top-level "materials" array;
/// pieces reference one via <see cref="BuildPieceData.MaterialId"/>.
/// </summary>
public sealed class BuildMaterialData
{
	public string Id { get; set; } = string.Empty;

	/// <summary>Support a grounded piece starts with — also the cap on what a piece can receive.</summary>
	public float MaxSupport { get; set; } = 100f;

	/// <summary>A piece whose computed support falls below this collapses.</summary>
	public float MinSupport { get; set; } = 10f;

	/// <summary>Fractional support lost per meter when carried straight up from the parent.</summary>
	public float VerticalLoss { get; set; } = 0.125f;

	/// <summary>Fractional support lost per meter sideways — hanging below also uses this.</summary>
	public float HorizontalLoss { get; set; } = 0.2f;

	/// <summary>Valheim wood — the fallback when JSON carries no materials.</summary>
	public static BuildMaterialData DefaultWood => new()
	{
		Id = "wood",
		MaxSupport = 100f,
		MinSupport = 10f,
		VerticalLoss = 0.125f,
		HorizontalLoss = 0.2f,
	};
}
