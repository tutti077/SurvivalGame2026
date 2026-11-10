using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sandbox;

namespace Survival;

/// <summary>One drying-rack process: an input item that becomes <see cref="OutputAmount"/> of <see cref="Output"/> after <see cref="Hours"/> game-clock hours.</summary>
public sealed class DryingRackProcess
{
	[JsonPropertyName( "input" )] public string Input { get; set; } = string.Empty;
	[JsonPropertyName( "output" )] public string Output { get; set; } = string.Empty;
	[JsonPropertyName( "outputAmount" )] public int OutputAmount { get; set; } = 1;
	[JsonPropertyName( "hours" )] public float Hours { get; set; } = 6f;
}

/// <summary>
/// <c>Assets/data/drying_rack.json</c>: what a <see cref="DryingRack"/> turns into what, and how long it
/// takes (Mark 2026-11-08: leather 6 h, jerky 12 h once a jerky item exists). Loaded once; adding a
/// process is a JSON edit.
/// </summary>
public static class DryingRackCatalog
{
	const string FilePath = "data/drying_rack.json";

	static readonly List<DryingRackProcess> _processes = new();
	static readonly Dictionary<string, DryingRackProcess> _byInput = new( StringComparer.OrdinalIgnoreCase );
	static bool _loaded;

	public static IReadOnlyList<DryingRackProcess> Processes
	{
		get
		{
			EnsureLoaded();
			return _processes;
		}
	}

	public static bool TryGetProcess( string inputId, out DryingRackProcess process )
	{
		EnsureLoaded();
		process = null;
		return !string.IsNullOrWhiteSpace( inputId ) && _byInput.TryGetValue( inputId, out process );
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		_processes.Clear();
		_byInput.Clear();

		string json = null;
		foreach ( var path in new[] { FilePath, "assets/" + FilePath, "/" + FilePath } )
		{
			try
			{
				// Do NOT gate on FileExists — it returns false on joining clients while ReadAllText still works.
				json = FileSystem.Mounted.ReadAllText( path );
			}
			catch
			{
				json = null;
			}

			if ( !string.IsNullOrWhiteSpace( json ) )
				break;
		}

		if ( string.IsNullOrWhiteSpace( json ) )
		{
			Log.Warning( $"[DryingRackCatalog] {FilePath} not found — the rack processes nothing." );
			return;
		}

		try
		{
			var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
			var file = JsonSerializer.Deserialize<DryingRackFile>( json, options );
			if ( file?.Processes is null )
				return;

			foreach ( var process in file.Processes )
			{
				if ( process is null || string.IsNullOrWhiteSpace( process.Input ) || string.IsNullOrWhiteSpace( process.Output ) )
					continue;

				process.Input = ResourceCatalog.NormalizeResourceId( process.Input );
				process.Output = ResourceCatalog.NormalizeResourceId( process.Output );
				process.OutputAmount = Math.Max( 1, process.OutputAmount );
				_processes.Add( process );
				_byInput[process.Input] = process;
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[DryingRackCatalog] failed to parse {FilePath}: {e.Message}" );
		}
	}

	sealed class DryingRackFile
	{
		[JsonPropertyName( "processes" )] public List<DryingRackProcess> Processes { get; set; } = new();
	}
}
