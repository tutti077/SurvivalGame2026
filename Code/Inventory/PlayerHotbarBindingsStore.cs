using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Client-local persistence for which resource belongs in each hotbar slot.</summary>
public static class PlayerHotbarBindingsStore
{
	public const int SlotCount = PlayerHotbar.SlotCount;

	const string FileName = "hotbar_bindings.json";

	public static string[] Load()
	{
		var empty = CreateEmpty();

		try
		{
			if ( !FileSystem.Data.FileExists( FileName ) )
				return empty;

			var json = FileSystem.Data.ReadAllText( FileName );
			var data = JsonSerializer.Deserialize<HotbarBindingsFile>( json );
			if ( data?.Bindings is null || data.Bindings.Length == 0 )
				return empty;

			for ( var i = 0; i < SlotCount; i++ )
				empty[i] = i < data.Bindings.Length ? data.Bindings[i] ?? string.Empty : string.Empty;

			return empty;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[HotbarBindings] Failed to load: {ex.Message}" );
			return empty;
		}
	}

	public static void Save( string[] bindings )
	{
		if ( bindings is null || bindings.Length != SlotCount )
			return;

		try
		{
			var data = new HotbarBindingsFile { Bindings = bindings };
			var json = JsonSerializer.Serialize( data, JsonOptions );
			FileSystem.Data.WriteAllText( FileName, json );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[HotbarBindings] Failed to save: {ex.Message}" );
		}
	}

	public static string[] CreateEmpty()
	{
		var bindings = new string[SlotCount];
		for ( var i = 0; i < SlotCount; i++ )
			bindings[i] = string.Empty;
		return bindings;
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	sealed class HotbarBindingsFile
	{
		public string[] Bindings { get; set; } = Array.Empty<string>();
	}
}
