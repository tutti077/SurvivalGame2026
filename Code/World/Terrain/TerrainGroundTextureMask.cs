namespace Survival;

/// <summary>
/// Writes the ground-texture mask into the chunk vertex ALPHA for materials/terrain/terrain_ground.vmat
/// (pixel_lit F_VERTEX_PAINT: alpha 1 = the world-tiled ground texture, 0 = the painted biome vertex colour).
/// Clover Hills vertices get Clover Ground Texture Blend, every other biome 0. Replaces the old bush.png
/// vertex-colour tiling (2026-10-12): a 2 m vertex grid cannot carry a texture, the shader can.
/// </summary>
public static class TerrainGroundTextureMask
{
	public static void Apply( TerrainPreviewSettings settings, Color[] colors, TerrainPreviewBiomeId[] biomeMap, int verticesPerSide )
	{
		if ( colors is null || biomeMap is null )
			return;

		var count = verticesPerSide * verticesPerSide;
		if ( colors.Length < count || biomeMap.Length < count )
			return;

		var blend = Math.Clamp( settings.BiomeCloverGroundTextureStrength01, 0f, 1f );
		for ( var i = 0; i < count; i++ )
			colors[i] = colors[i].WithAlpha( biomeMap[i] == TerrainPreviewBiomeId.CloverHills ? blend : 0f );
	}
}
