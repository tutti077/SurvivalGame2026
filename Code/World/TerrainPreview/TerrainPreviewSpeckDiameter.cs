namespace Survival;

/// <summary>Shared minimum patch width for land-island and inland-water speck removal.</summary>
static class TerrainPreviewSpeckDiameter
{
	public const float AbsoluteMinimumMeters = 80f;

	public static float ResolveMeters( TerrainPreviewSettings settings )
		=> Math.Max( AbsoluteMinimumMeters, settings.SpeckMinPatchDiameterMeters );
}
