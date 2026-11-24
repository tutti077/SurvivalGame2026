using Sandbox;

namespace Survival;

/// <summary>
/// Quiet looping 2D track via <see cref="MusicPlayer"/>. Attach once per playable scene.
/// Scaled by the player's music volume (<see cref="AudioVolumeSettings"/>).
/// </summary>
[Title( "Background Music" )]
[Category( "Audio" )]
public sealed class BackgroundMusic : Component
{
	/// <summary>Path relative to mounted Assets (e.g. sounds/music/track.mp3).</summary>
	[Property, Title( "Track Path" )]
	public string TrackPath { get; set; } = "sounds/music/clover_hills_sketch_2.mp3";

	[Property, Range( 0f, 1f ), Title( "Volume" )]
	public float Volume { get; set; } = 0.18f;

	[Property]
	public bool Loop { get; set; } = true;

	[Property, Title( "Play On Start" )]
	public bool PlayOnStart { get; set; } = true;

	MusicPlayer _music;
	float _appliedVolume = -1f;

	protected override void OnStart()
	{
		if ( PlayOnStart )
			Play();
	}

	protected override void OnUpdate()
	{
		if ( _music is null )
			return;

		var volume = EffectiveVolume;
		if ( Math.Abs( volume - _appliedVolume ) < 0.0001f )
			return;

		_music.Volume = volume;
		_appliedVolume = volume;
	}

	/// <summary>The track's own level times the player's Settings → Audio → Music slider.</summary>
	float EffectiveVolume => Volume * AudioVolumeSettings.MusicVolume;

	protected override void OnDestroy()
	{
		Stop();
	}

	public void Play()
	{
		Stop();

		if ( string.IsNullOrWhiteSpace( TrackPath ) )
			return;

		if ( !FileSystem.Mounted.FileExists( TrackPath ) )
		{
			Log.Warning( $"[BackgroundMusic] Missing track '{TrackPath}'." );
			return;
		}

		_music = MusicPlayer.Play( FileSystem.Mounted, TrackPath );
		if ( _music is null )
		{
			Log.Warning( $"[BackgroundMusic] Failed to play '{TrackPath}'." );
			return;
		}

		// Force non-spatial / global — without ListenLocal the track sits at world Position
		// (default origin) and fades with distance like a proximity emitter.
		_music.ListenLocal = true;
		_music.Repeat = Loop;

		// On the Music mixer, so the sound effects slider (every other mixer) never scales it.
		var mixer = AudioVolumeSettings.MusicMixer;
		if ( mixer is not null )
			_music.TargetMixer = mixer;

		_music.Volume = EffectiveVolume;
		_appliedVolume = _music.Volume;
	}

	public void Stop()
	{
		if ( _music is null )
			return;

		_music.Stop();
		_music.Dispose();
		_music = null;
		_appliedVolume = -1f;
	}
}
