namespace Survival;

/// <summary>
/// Every enabled pawn — owner and proxies alike — is a possible ripple maker for the river water (Mark 2026-10-22:
/// rings and a wake around a swimmer). The pawn only registers itself; the river surface
/// (<see cref="TerrainWorldManager"/>, RiverWater) picks the few nearest the camera each frame and the shader decides
/// from the water height whether the body actually cuts the surface, so wading makes ripples too and no swim state
/// has to be synced.
/// </summary>
public sealed partial class PlayerMovement
{
	static readonly List<PlayerMovement> _waterRippleMakers = new();

	/// <summary>Enabled pawns, for the river surface's ripple pick. Read-only by convention.</summary>
	public static IReadOnlyList<PlayerMovement> WaterRippleMakers => _waterRippleMakers;

	protected override void OnEnabled()
	{
		if ( !_waterRippleMakers.Contains( this ) )
			_waterRippleMakers.Add( this );
	}

	protected override void OnDisabled()
	{
		_waterRippleMakers.Remove( this );
	}
}
