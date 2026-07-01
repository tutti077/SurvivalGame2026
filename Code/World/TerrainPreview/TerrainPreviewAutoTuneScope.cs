namespace Survival;

/// <summary>Lowers preview raster resolution during valley auto search; final generate stays full res.</summary>
public static class TerrainPreviewAutoTuneScope
{
	static bool _active;
	static int _measureResolution;

	public static bool IsActive => _active;

	public static int MeasureResolution( TerrainPreviewSettings settings )
		=> _active ? _measureResolution : settings.ClampedResolution;

	public static IDisposable Begin( TerrainPreviewSettings settings )
	{
		var tune = Math.Clamp( 256, 64, settings.ClampedResolution );
		_active = true;
		_measureResolution = tune;
		return new Scope();
	}

	sealed class Scope : IDisposable
	{
		public void Dispose() => _active = false;
	}
}
