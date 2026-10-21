using System.Text.Json;

namespace Survival;

/// <summary>
/// Stable fingerprint of a resolved generation settings set, so a baked biome map can say which world it
/// belongs to (Mark 2026-10-08: the minimap, the inspector map and the live terrain all showed different
/// worlds — the PNG was baked from settings that had since changed). Serialises the generation clone and
/// hashes the text with FNV-1a; <see cref="HashCode"/> is randomised per process and cannot be persisted.
/// </summary>
public static class TerrainPreviewSettingsFingerprint
{
	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	public static string Compute( TerrainPreviewSettings settings )
	{
		if ( settings is null )
			return "none";

		var json = JsonSerializer.Serialize( settings.CloneForGenerate( false ), JsonOptions );
		ulong hash = 14695981039346656037UL;
		foreach ( var c in json )
		{
			hash ^= c;
			hash *= 1099511628211UL;
		}

		return hash.ToString( "x16" );
	}
}
