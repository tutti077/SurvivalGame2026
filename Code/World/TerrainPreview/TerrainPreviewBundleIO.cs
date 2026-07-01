using System.Text.Json;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>Loads tuned generation settings from editor preview bundles under <c>terrain/preview/</c>.</summary>
public static class TerrainPreviewBundleIO
{
	const string PreviewRoot = "terrain/preview";
	const string LatestRelativePath = "terrain/preview/.latest_preview.json";
	const string SettingsFileName = "preview_settings.json";

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
	};

	sealed class LatestPreviewPointer
	{
		public string Bundle { get; set; }
	}

	public static bool TryLoadLatestGenerationSettings(
		out TerrainPreviewSettings settings,
		out string bundleName,
		out string status )
	{
		settings = null;
		bundleName = null;
		status = null;

		if ( !FileSystem.Mounted.FileExists( LatestRelativePath ) )
		{
			status = "missing .latest_preview.json";
			return false;
		}

		LatestPreviewPointer latest;
		try
		{
			latest = JsonSerializer.Deserialize<LatestPreviewPointer>(
				FileSystem.Mounted.ReadAllText( LatestRelativePath ), JsonOptions );
		}
		catch ( Exception e )
		{
			status = $"invalid .latest_preview.json ({e.Message})";
			return false;
		}

		if ( latest is null || string.IsNullOrWhiteSpace( latest.Bundle ) )
		{
			status = "latest preview pointer has no bundle";
			return false;
		}

		return TryLoadBundleGenerationSettings( latest.Bundle, out settings, out bundleName, out status );
	}

	public static bool TryLoadBundleGenerationSettings(
		string bundleName,
		out TerrainPreviewSettings settings,
		out string resolvedBundleName,
		out string status )
	{
		settings = null;
		resolvedBundleName = bundleName?.Trim();
		status = null;

		if ( string.IsNullOrWhiteSpace( resolvedBundleName ) )
		{
			status = "empty bundle name";
			return false;
		}

		var settingsPath = $"{PreviewRoot}/{resolvedBundleName}/{SettingsFileName}";
		if ( !FileSystem.Mounted.FileExists( settingsPath ) )
		{
			status = $"missing {settingsPath}";
			return false;
		}

		try
		{
			settings = DeserializeSettingsSnapshot( FileSystem.Mounted.ReadAllText( settingsPath ) );
		}
		catch ( Exception e )
		{
			status = $"invalid preview_settings.json ({e.Message})";
			return false;
		}

		if ( settings is null )
		{
			status = "preview_settings.json deserialized empty";
			return false;
		}

		status = $"ok ({settingsPath})";
		return true;
	}

	static TerrainPreviewSettings DeserializeSettingsSnapshot( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			return null;

		using var doc = JsonDocument.Parse( json );
		var root = doc.RootElement;
		if ( root.TryGetProperty( "generation", out var generation ) )
			return JsonSerializer.Deserialize<TerrainPreviewSettings>( generation.GetRawText(), JsonOptions );

		return JsonSerializer.Deserialize<TerrainPreviewSettings>( json, JsonOptions );
	}
}
