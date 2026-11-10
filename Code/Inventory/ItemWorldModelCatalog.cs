using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Survival;

/// <summary>
/// Item id → the model a world pickup of it draws (<c>data/item_world_models.json</c>; Mark 2026-11-03: "use the models the
/// claude cloud agent made for all the items instead of just spheres"). The models are Tools/item_models' generated set under
/// <c>Assets/models/&lt;category&gt;/&lt;name&gt;/</c>, authored at the project's 40 u/m, so a drop with a model is drawn at scale 1.
/// An id that is not listed keeps the tinted dev rock. A value is one model path, or a list of variants of which each drop
/// picks one (<c>resource_stone</c>: the four <c>stone_drop*_v1</c> stones). Loaded once (and again when the file changes) -
/// never per frame.
/// </summary>
public static class ItemWorldModelCatalog
{
	const string DataPath = "data/item_world_models.json";

	static readonly Dictionary<string, string[]> ByResourceId = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;
	static int _loadedHash;

	sealed class FileData
	{
		[JsonPropertyName( "models" )]
		public Dictionary<string, JsonElement> Models { get; set; }
	}

	/// <summary>The model for an item id; <paramref name="variantRoll"/> picks among the id's variants (any int, wrapped).</summary>
	public static bool TryGetModelPath( string resourceId, out string modelPath, int variantRoll = 0 )
	{
		modelPath = null;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		EnsureLoaded();
		if ( !ByResourceId.TryGetValue( ResourceCatalog.NormalizeResourceId( resourceId ), out var paths ) || paths.Length == 0 )
			return false;
		modelPath = paths[(int)((uint)variantRoll % (uint)paths.Length)];
		return true;
	}

	/// <summary>The file is re-read for edits at most this often (a burst of drops reads it once).</summary>
	const float ChangeCheckSeconds = 2f;
	static float _lastCheck = float.NegativeInfinity;

	public static void EnsureLoaded()
	{
		if ( _loaded && RealTime.Now - _lastCheck < ChangeCheckSeconds )
			return;
		_lastCheck = RealTime.Now;

		var json = FileSystem.Mounted.FileExists( DataPath ) ? FileSystem.Mounted.ReadAllText( DataPath ) : null;
		var hash = json?.GetHashCode() ?? 0;
		if ( _loaded && hash == _loadedHash )
			return;

		_loaded = true;
		_loadedHash = hash;
		ByResourceId.Clear();
		if ( string.IsNullOrWhiteSpace( json ) )
			return;

		try
		{
			var file = JsonSerializer.Deserialize<FileData>( json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true } );
			if ( file?.Models is null )
				return;
			var variants = new List<string>();
			foreach ( var (id, value) in file.Models )
			{
				if ( string.IsNullOrWhiteSpace( id ) )
					continue;
				variants.Clear();
				if ( value.ValueKind == JsonValueKind.String )
					variants.Add( value.GetString() );
				else if ( value.ValueKind == JsonValueKind.Array )
				{
					foreach ( var item in value.EnumerateArray() )
					{
						if ( item.ValueKind == JsonValueKind.String )
							variants.Add( item.GetString() );
					}
				}
				variants.RemoveAll( string.IsNullOrWhiteSpace );
				if ( variants.Count > 0 )
					ByResourceId[ResourceCatalog.NormalizeResourceId( id )] = variants.ConvertAll( v => v.Trim() ).ToArray();
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[ItemWorldModelCatalog] {DataPath}: {e.Message}" );
		}
	}
}
