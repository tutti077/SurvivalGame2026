namespace Survival;

/// <summary>
/// Dev probe for "trees flashing" (Mark 2026-10-15: keep iterating until it is gone — so measure it). Every
/// frame the main camera is rendered into a small texture and read back; the probe reports how much of the
/// image changed abruptly since the previous frame and how much of it changed and then RETURNED to a state
/// seen within the last few frames (a flash: something vanished and came back). With the camera parked and the
/// wind off nothing should change at all — any change is a flash — and <c>terrain_stream_offset</c> moves the
/// stream origin instead of the camera so chunk hand-overs happen in front of a still camera. The frame pair
/// with the biggest change is written as PPM files to the game data folder for inspection.
/// Console: <c>flash_probe [seconds]</c> (0 stops).
/// </summary>
public sealed class TerrainFlashProbe : Component
{
	const int Width = 320;
	const int Height = 180;
	const int History = 8;
	const int BigDiff = 40;
	const int SmallDiff = 24;

	Texture _target;
	readonly List<Color32[]> _history = new();
	float _secondsLeft;
	/// <summary>Seconds at the start during which spikes are reported but not recorded (a rig's start transient).</summary>
	float _ignoreSeconds;
	RealTimeSince _sinceLog;
	int _frames, _frameIndex;
	float _maxChanged, _sumChanged, _maxFlash;
	int _maxChangedFrame, _maxFlashFrame;
	Color32[] _bestBefore, _bestDuring;
	float _bestScore;
	/// <summary>The frames around the biggest vanish-and-return spike: prev3, prev2, prev1, cur, next (next filled on the following frame).</summary>
	readonly List<Color32[]> _flashSeq = new();
	float _bestFlash;
	int _flashSeqFrame;
	bool _flashSeqWantsNext;
	int _bestMinX, _bestMinY, _bestMaxX, _bestMaxY;

	public static TerrainFlashProbe Start( Scene scene, float seconds, float ignoreSeconds = 0f )
	{
		var existing = scene.GetAllComponents<TerrainFlashProbe>().FirstOrDefault();
		if ( existing is not null && existing.IsValid() )
		{
			existing._secondsLeft = seconds;
			existing._ignoreSeconds = ignoreSeconds;
			return existing;
		}

		var go = new GameObject( true, "FlashProbe" );
		go.Flags |= GameObjectFlags.NotSaved;
		var probe = go.Components.Create<TerrainFlashProbe>();
		probe._secondsLeft = seconds;
		probe._ignoreSeconds = ignoreSeconds;
		Log.Info( $"[FlashProbe] started for {seconds:0} s — {Width}x{Height} readback every frame; big diff > {BigDiff}, return within {History} frames < {SmallDiff}" );
		return probe;
	}

	protected override void OnUpdate()
	{
		_secondsLeft -= Time.Delta;
		_ignoreSeconds -= Time.Delta;
		var record = _ignoreSeconds <= 0f;
		if ( _secondsLeft <= 0f )
		{
			Finish();
			return;
		}

		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return;

		_target ??= Texture.CreateRenderTarget( "flashprobe", ImageFormat.RGBA8888, new Vector2( Width, Height ) );
		cam.RenderToTexture( _target );
		var pixels = _target.GetPixels();
		if ( pixels is null || pixels.Length != Width * Height )
			return;

		_frameIndex++;
		if ( _history.Count > 0 )
		{
			var prev = _history[^1];
			var changed = 0;
			var flash = 0;
			int minX = Width, minY = Height, maxX = -1, maxY = -1;
			for ( var i = 0; i < pixels.Length; i++ )
			{
				if ( MaxDiff( pixels[i], prev[i] ) <= BigDiff )
					continue;
				changed++;
				var x = i % Width;
				var y = i / Width;
				if ( x < minX ) minX = x;
				if ( x > maxX ) maxX = x;
				if ( y < minY ) minY = y;
				if ( y > maxY ) maxY = y;
				for ( var h = _history.Count - 2; h >= 0; h-- )
				{
					if ( MaxDiff( pixels[i], _history[h][i] ) < SmallDiff )
					{
						flash++;
						break;
					}
				}
			}

			var changed01 = changed / (float)pixels.Length;
			var flash01 = flash / (float)pixels.Length;
			_frames++;
			_sumChanged += changed01;
			if ( changed01 > _maxChanged )
			{
				_maxChanged = changed01;
				_maxChangedFrame = _frameIndex;
			}
			if ( flash01 > _maxFlash )
			{
				_maxFlash = flash01;
				_maxFlashFrame = _frameIndex;
			}
			if ( _flashSeqWantsNext )
			{
				_flashSeq.Add( pixels.ToArray() );
				_flashSeqWantsNext = false;
			}
			if ( record && flash01 > _bestFlash && flash01 > 0.002f )
			{
				_bestFlash = flash01;
				_flashSeqFrame = _frameIndex;
				_flashSeq.Clear();
				for ( var h = Math.Max( 0, _history.Count - 3 ); h < _history.Count; h++ )
					_flashSeq.Add( _history[h].ToArray() );
				_flashSeq.Add( pixels.ToArray() );
				_flashSeqWantsNext = true;
			}

			var score = changed01 + (flash01 * 4f);
			if ( record && score > _bestScore && changed > 0 )
			{
				_bestScore = score;
				_bestBefore = prev.ToArray();
				_bestDuring = pixels.ToArray();
				_bestMinX = minX; _bestMinY = minY; _bestMaxX = maxX; _bestMaxY = maxY;
			}
		}

		_history.Add( pixels );
		if ( _history.Count > History )
			_history.RemoveAt( 0 );

		if ( _sinceLog >= 1f )
		{
			_sinceLog = 0f;
			Log.Info( $"[FlashProbe] {_frames} frames | changed: max {_maxChanged * 100f:0.00}% (frame {_maxChangedFrame}) mean {(_frames > 0 ? _sumChanged / _frames * 100f : 0f):0.00}% | returned-within-{History}: max {_maxFlash * 100f:0.00}% (frame {_maxFlashFrame})" );
			_frames = 0;
			_sumChanged = 0f;
			_maxChanged = 0f;
			_maxFlash = 0f;
		}
	}

	static int MaxDiff( Color32 a, Color32 b )
	{
		var dr = Math.Abs( a.r - b.r );
		var dg = Math.Abs( a.g - b.g );
		var db = Math.Abs( a.b - b.b );
		return Math.Max( dr, Math.Max( dg, db ) );
	}

	void Finish()
	{
		if ( _bestBefore is not null && _bestDuring is not null )
		{
			WritePpm( "flashprobe_before.ppm", _bestBefore );
			WritePpm( "flashprobe_during.ppm", _bestDuring );
			Log.Info( $"[FlashProbe] done — biggest change written to data/flashprobe_before.ppm + flashprobe_during.ppm (score {_bestScore:0.000}, box x {_bestMinX}-{_bestMaxX} y {_bestMinY}-{_bestMaxY} of {Width}x{Height})" );
		}
		else
			Log.Info( "[FlashProbe] done — no change seen" );

		if ( _flashSeq.Count > 0 )
		{
			for ( var i = 0; i < _flashSeq.Count; i++ )
				WritePpm( $"flashprobe_seq{i}.ppm", _flashSeq[i] );
			Log.Info( $"[FlashProbe] vanish-and-return spike {_bestFlash * 100f:0.00}% at frame {_flashSeqFrame}: {_flashSeq.Count} frames written as data/flashprobe_seq0..{_flashSeq.Count - 1}.ppm (spike frame is seq{Math.Min( 3, _flashSeq.Count - 1 )})" );
		}

		GameObject.Destroy();
	}

	static void WritePpm( string name, Color32[] pixels )
	{
		var header = System.Text.Encoding.ASCII.GetBytes( $"P6\n{Width} {Height}\n255\n" );
		var bytes = new byte[header.Length + (pixels.Length * 3)];
		Array.Copy( header, bytes, header.Length );
		var o = header.Length;
		foreach ( var p in pixels )
		{
			bytes[o++] = p.r;
			bytes[o++] = p.g;
			bytes[o++] = p.b;
		}
		FileSystem.Data.WriteAllBytes( name, bytes );
	}

	/// <summary>Usage: <c>flash_probe [seconds] [ignoreSeconds]</c> — per-frame image-change readout of the main camera (0 stops); spikes in the first ignoreSeconds are not recorded.</summary>
	[ConCmd( "flash_probe" )]
	public static void ConCmdFlashProbe( float seconds = 20f, float ignoreSeconds = 0f )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		if ( seconds <= 0f )
		{
			foreach ( var probe in scene.GetAllComponents<TerrainFlashProbe>().ToList() )
				probe.GameObject.Destroy();
			Log.Info( "[FlashProbe] stopped" );
			return;
		}

		Start( scene, seconds, ignoreSeconds );
	}
}
