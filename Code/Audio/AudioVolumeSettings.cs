using System;
using System.Text.Json;
using Sandbox;
using Sandbox.Audio;

namespace Survival;

/// <summary>
/// Client-local volume sliders (Settings → Audio), both 0–1, default 1 (100 %), saved to
/// <c>audio_settings.json</c> in <see cref="FileSystem.Data"/>.
/// Music: <see cref="BackgroundMusic"/> multiplies its own volume by <see cref="MusicVolume"/> and plays on the
/// engine's Music mixer. Sound effects: every other mixer under Master (Game, UI, …) except Voice gets
/// <see cref="SfxVolume"/> as its output volume, so every game sound is covered without touching call sites.
/// </summary>
public static class AudioVolumeSettings
{
	const string FileName = "audio_settings.json";
	const string MusicMixerName = "Music";
	const string VoiceMixerName = "Voice";

	static float _music = 1f;
	static float _sfx = 1f;
	static bool _loaded;

	public static float MusicVolume
	{
		get
		{
			EnsureLoaded();
			return _music;
		}
	}

	public static float SfxVolume
	{
		get
		{
			EnsureLoaded();
			return _sfx;
		}
	}

	/// <summary>The mixer music plays on, so the sound effects volume never scales it. Null if the project has none.</summary>
	public static Mixer MusicMixer => Mixer.FindMixerByName( MusicMixerName );

	/// <summary>Loads the saved values and pushes them to the mixers. Call once when the HUD builds.</summary>
	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;

		try
		{
			if ( FileSystem.Data.FileExists( FileName ) )
			{
				var data = JsonSerializer.Deserialize<VolumeFile>( FileSystem.Data.ReadAllText( FileName ) );
				if ( data is not null )
				{
					_music = Math.Clamp( data.Music, 0f, 1f );
					_sfx = Math.Clamp( data.Sfx, 0f, 1f );
				}
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Audio] Failed to load volume settings: {ex.Message}" );
		}

		ApplySfxToMixers();
	}

	/// <summary>Live change (slider drag). Call <see cref="Save"/> once the change is final.</summary>
	public static void SetMusicVolume( float value )
	{
		EnsureLoaded();
		_music = Math.Clamp( value, 0f, 1f );
	}

	/// <inheritdoc cref="SetMusicVolume"/>
	public static void SetSfxVolume( float value )
	{
		EnsureLoaded();
		_sfx = Math.Clamp( value, 0f, 1f );
		ApplySfxToMixers();
	}

	public static void Save()
	{
		try
		{
			var json = JsonSerializer.Serialize( new VolumeFile { Music = _music, Sfx = _sfx }, JsonOptions );
			FileSystem.Data.WriteAllText( FileName, json );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Audio] Failed to save volume settings: {ex.Message}" );
		}
	}

	static void ApplySfxToMixers()
	{
		var master = Mixer.Master;
		if ( master is null )
			return;

		foreach ( var child in master.GetChildren() )
		{
			if ( child is null )
				continue;

			if ( string.Equals( child.Name, MusicMixerName, StringComparison.OrdinalIgnoreCase )
			     || string.Equals( child.Name, VoiceMixerName, StringComparison.OrdinalIgnoreCase ) )
				continue;

			child.Volume = _sfx;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true
	};

	sealed class VolumeFile
	{
		public float Music { get; set; } = 1f;
		public float Sfx { get; set; } = 1f;
	}
}
