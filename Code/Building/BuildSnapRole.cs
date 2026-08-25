namespace Survival;

/// <summary>
/// Where a snap sits on a piece. <b>Corner*</b> roles are the midpoint of the thin edge at each
/// footprint corner (plates: floors, walls, roofs); on a triangle one of the four is cut away by
/// the hypotenuse and simply does not exist, and on a flight of stairs they ride the walking
/// surface — North* on the low entry edge, South* on the high exit edge; folded roof corners use
/// <b>Fold0–Fold3</b> at the four mesh corners. <b>Axis*</b> roles are a
/// single snap centred on each end of a long piece — a post's bottom and top — so beams mate
/// end-to-end instead of by corner.
/// <para>
/// Which corners a given piece actually has is <see cref="BuildSnapLayout.GetRoles"/>; where they
/// sit is <c>BuildColliderSnap.GetCornerSnapLocal</c>.
/// </para>
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

	/// <summary>Folded roof corner — the four mesh corners of a hip / valley piece (Fold0–Fold3).</summary>
	Fold0,
	Fold1,
	Fold2,
	Fold3,
}
