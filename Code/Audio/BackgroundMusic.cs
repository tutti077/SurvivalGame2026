using Sandbox;

namespace Survival;

/// <summary>
/// Quiet looping 2D track via <see cref="MusicPlayer"/>. Attach once per playable scene.
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

		if ( Math.Abs( Volume - _appliedVolume ) < 0.0001f )
			return;

		_music.Volume = Volume;
		_appliedVolume = Volume;
	}

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
		_music.Volume = Volume;
		_appliedVolume = Volume;
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
