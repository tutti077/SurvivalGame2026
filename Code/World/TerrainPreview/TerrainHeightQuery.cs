namespace Survival;

/// <summary>Authoritative height sample at world meters — same path as streamed terrain meshes.</summary>
public static class TerrainHeightQuery
{
	public static bool TrySampleGroundMeters(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		out float groundZMeters )
	{
		groundZMeters = 0f;
		var backend = TerrainPreviewBackendRegistry.Active;
		if ( backend is null )
			return false;

		var sample = backend.Sample( settings, worldXMeters, worldYMeters );
		if ( !sample.IsInsideWorld )
			return false;

		groundZMeters = sample.Height01 * settings.MaxTerrainHeightMeters;
		return true;
	}
}
