namespace Survival;

/// <summary>
/// What the ground IS at each terrain vertex, for the per-pixel biome blend (pixel_lit F_TERRAIN_SPLAT, Mark 2026-10-24:
/// "weird areas where the texture kinda blurs" — biome colour lived on the vertices, so a border was either the 2 m grid's
/// stairs or a 100 m smear). The vertex only says what is there; the pixel shader decides what it looks like, with the
/// same rule on the near, far and horizon tiers:
/// <list type="bullet">
/// <item>colour RGB = the Clover / Redwood / Amber shares of the land weights (sum 1), A = the Mountain weight — the
/// shader picks Mountain from 0.5 and otherwise the largest share, exactly as <see cref="TerrainPreviewBiomeResolver"/>
/// does, so the ground matches the map, the trees and the minimap;</item>
/// <item>splat X = sand (1 on lake / river / sea beds, the beach band <see cref="TerrainPreviewSample.Beach01"/> on the
/// shore), Y = wet (<see cref="TerrainPreviewSample.Wet01"/>, 1 under water), Z = far-forest canopy darkening (written by
/// the far tier).</item>
/// </list>
/// Azure Coast is a coast overlay, not a ground: it shows the land under it plus the beach. Blackwater has no land
/// weight at all (the shader draws it near-black).
/// </summary>
public static class TerrainGroundSplat
{
	public static void Fill(
		TerrainPreviewSettings settings,
		TerrainPreviewSample[] samples,
		float chunkMinX,
		float chunkMinY,
		float step,
		int verticesPerSide,
		Color[] weights,
		Vector4[] splat )
	{
		var count = verticesPerSide * verticesPerSide;
		for ( var i = 0; i < count; i++ )
		{
			var sample = samples[i];
			var wx = chunkMinX + ((i % verticesPerSide) * step);
			var wy = chunkMinY + ((i / verticesPerSide) * step);
			if ( !sample.IsInsideWorld )
			{
				// Past the world edge: the ocean floor
				weights[i] = new Color( 1f, 0f, 0f, 0f );
				splat[i] = new Vector4( 1f, 1f, 0f, 0f );
				continue;
			}

			var bed = TerrainShorelineDisplay.IsDisplayWaterColor( settings, wx, wy );
			var land = TerrainPreviewBiomeResolver.SampleLandBiomeWeights( settings, sample, wx, wy );
			var landSum = land.Clover + land.Redwood + land.Amber;
			if ( !bed && TerrainPreviewLandDiskFields.IsBlackwater( settings, wx, wy ) )
				weights[i] = new Color( 0f, 0f, 0f, 0f );
			else if ( landSum <= 1e-4f )
				weights[i] = new Color( 1f, 0f, 0f, Math.Clamp( land.Mountain, 0f, 1f ) );
			else
				weights[i] = new Color( land.Clover / landSum, land.Redwood / landSum, land.Amber / landSum, Math.Clamp( land.Mountain, 0f, 1f ) );

			var wet = Math.Clamp( sample.Wet01, 0f, 1f );
			splat[i] = bed
				? new Vector4( 1f, 1f, 0f, 0f )
				: new Vector4( Math.Clamp( sample.Beach01, 0f, 1f ), wet * wet * (3f - (2f * wet)), 0f, 0f );
		}
	}

	/// <summary>
	/// Generated tilled soil (<see cref="TerrainGroundPatch"/>, the wild-wheat beds) on a near chunk's vertices: the same contract
	/// as the hoe's tilled paint on voxel ground (<c>VoxelMesher</c>) — sand under it (X, so pixel_lit's darkening turns to
	/// earth brown instead of canopy green) and the darkening channel (Z), both = coverage × 1.5 (saturated). Equal on purpose:
	/// the shader turns Z brown only where X ≥ 0.5, so sand ahead of the darkening drew a pale sand ring round the soil
	/// (2026-11-02 first look); equal channels leave a soft dark fringe instead. Near tier only: Z is the canopy on the far tier.
	/// </summary>
	public static void ApplyTilledPatches( TerrainGroundPatch[] patches, float chunkMinX, float chunkMinY, float step, int verticesPerSide, Vector4[] splat )
	{
		if ( patches is null || patches.Length == 0 )
			return;
		var count = verticesPerSide * verticesPerSide;
		for ( var i = 0; i < count; i++ )
		{
			var c = TerrainGroundPatch.CoverageAt( patches, chunkMinX + ((i % verticesPerSide) * step), chunkMinY + ((i / verticesPerSide) * step) );
			if ( c <= 0f )
				continue;
			var s = splat[i];
			var t = Math.Clamp( c * 1.5f, 0f, 1f );
			splat[i] = new Vector4( MathF.Max( s.x, t ), s.y, MathF.Max( s.z, t ), s.w );
		}
	}
}
