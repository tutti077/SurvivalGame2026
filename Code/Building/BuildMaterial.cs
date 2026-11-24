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
	public float VerticalLoss { get; set; } = 0.0672f;

	/// <summary>Fractional support lost per meter sideways — hanging below also uses this.</summary>
	public float HorizontalLoss { get; set; } = 0.1135f;

	/// <summary>Hit points a placed piece of this material starts with (entity attacks only — see <see cref="BuildPiece.HostApplyDamage"/>).</summary>
	public float Health { get; set; } = 100f;

	/// <summary>
	/// Wood — the fallback when JSON carries no materials. Twice Valheim's reach (Mark 2026-11-16:
	/// "allow players to build much bigger and higher"): each per-piece factor is the square root of
	/// Valheim's (0.7375 → 0.8589 up, 0.58 → 0.7617 sideways), so 16 pieces tall / 8 out, and every
	/// mixed path also reaches twice as many pieces.
	/// </summary>
	public static BuildMaterialData DefaultWood => new()
	{
		Id = "wood",
		MaxSupport = 100f,
		MinSupport = 10f,
		VerticalLoss = 0.0672f,
		HorizontalLoss = 0.1135f,
		Health = 100f,
	};
}
