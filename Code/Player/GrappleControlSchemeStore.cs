using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

public enum GrappleControlScheme
{
	Unset = 0,
	Pro = 1,
	TrainingWheels = 2,
}

/// <summary>
/// Client-local Pro vs Training Wheels grapple winch binds.
/// A choice is requested every time the hook is equipped (playtest-friendly); last pick is still saved for Settings.
/// </summary>
public static class GrappleControlSchemeStore
{
	const string FileName = "grapple_control_scheme.json";

	static GrappleControlScheme _cached;
	static bool _loaded;
	static bool _choicePending;

	public static event Action Changed;

	public static GrappleControlScheme Current
	{
		get
		{
			EnsureLoaded();
			return _cached;
		}
	}

	public static bool NeedsChoice
	{
		get
		{
			EnsureLoaded();
			return _choicePending;
		}
	}

	public static bool IsTrainingWheels => Current == GrappleControlScheme.TrainingWheels;

	public static bool IsPro => Current == GrappleControlScheme.Pro;

	public static void RequestChoice()
	{
		EnsureLoaded();
		if ( _choicePending )
			return;

		_choicePending = true;
		Changed?.Invoke();
	}

	public static void Set( GrappleControlScheme scheme )
	{
		if ( scheme == GrappleControlScheme.Unset )
			return;

		EnsureLoaded();
		_choicePending = false;
		if ( _cached != scheme )
		{
			_cached = scheme;
			Save();
		}

		Changed?.Invoke();
	}

	static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		_cached = GrappleControlScheme.Unset;

		try
		{
			if ( !FileSystem.Data.FileExists( FileName ) )
				return;

			var json = FileSystem.Data.ReadAllText( FileName );
			var data = JsonSerializer.Deserialize<SchemeFile>( json );
			if ( data is null )
				return;

			_cached = data.Scheme == GrappleControlScheme.Pro || data.Scheme == GrappleControlScheme.TrainingWheels
				? data.Scheme
				: GrappleControlScheme.Unset;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[GrappleControl] Failed to load: {ex.Message}" );
		}
	}

	static void Save()
	{
		try
		{
			var json = JsonSerializer.Serialize( new SchemeFile { Scheme = _cached }, JsonOptions );
			FileSystem.Data.WriteAllText( FileName, json );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[GrappleControl] Failed to save: {ex.Message}" );
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	sealed class SchemeFile
	{
		public GrappleControlScheme Scheme { get; set; }
	}
}
