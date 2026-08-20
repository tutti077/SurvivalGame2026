namespace Survival;

/// <summary>
/// Where a snap sits on a piece. <b>Corner*</b> roles are the midpoint of the thin edge at each
/// footprint corner (plates: floors, walls, roofs, stairs). <b>Axis*</b> roles are a single snap
/// centred on each end of a long piece — a post's bottom and top — so beams mate end-to-end
/// instead of by corner.
/// </summary>
public enum BuildSnapRole
{
	Unknown = 0,
	CornerNorthEast,
	CornerNorthWest,
	CornerSouthEast,
	CornerSouthWest,

	/// <summary>Negative end of the piece's long axis (bottom of a vertical beam).</summary>
	AxisStart,

	/// <summary>Positive end of the piece's long axis (top of a vertical beam).</summary>
	AxisEnd,
}
