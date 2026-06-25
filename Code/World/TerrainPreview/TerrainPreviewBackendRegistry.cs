namespace Survival;

/// <summary>
/// Selects which sampler the preview tool uses. Full terrain can register later without UI changes.
/// </summary>
public static class TerrainPreviewBackendRegistry
{
	static ITerrainPreviewBackend _active = new SimpleTerrainPreviewBackend();

	public static ITerrainPreviewBackend Active => _active;

	public static void UseBackend( ITerrainPreviewBackend backend )
	{
		_active = backend ?? new SimpleTerrainPreviewBackend();
	}

	public static void UseSimplePreview() => _active = new SimpleTerrainPreviewBackend();
}
