namespace Survival;

/// <summary>Height unit helpers for terrain preview. Inland water is mask-driven — not height-based.</summary>
static class TerrainPreviewOceanByHeight
{
	public static float MaxHeightMeters( TerrainPreviewSettings settings )
		=> Math.Max( 50f, settings.MaxTerrainHeightMeters );

	public static float MetersToHeight01( TerrainPreviewSettings settings, float heightMeters )
		=> Math.Clamp( heightMeters / MaxHeightMeters( settings ), 0f, 1f );

	public static float Height01ToMeters( TerrainPreviewSettings settings, float height01 )
		=> height01 * MaxHeightMeters( settings );
}
