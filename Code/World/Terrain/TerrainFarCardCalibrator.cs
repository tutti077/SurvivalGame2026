using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>far_card_calibrate [apply]</c>: measures, on screen, how the far impostor cards compare with the instanced
/// LOD3 ring they continue (Mark 2026-10-20: "see the transition??"). Renders the scene camera to a small target
/// three times a few frames apart — as is, with the ring hidden, with the impostors hidden — so the pixels that
/// change are exactly the ring's and exactly the impostors'. Prints mean colour + coverage for both, in eight
/// horizontal screen bands and overall. <c>apply tint</c> (by NIGHT, ambient only) multiplies
/// <see cref="TerrainWorldManager.FarImpostorTint"/> by the ring / impostor ratio; <c>apply direct</c> (by DAY) scales
/// <see cref="TerrainWorldManager.FarImpostorDirect"/> by the luminance ratio. Run each a few times — the tonemapper is not linear. Stand still while it runs (~1 s). Dev only; the object is never saved.
/// </summary>
public sealed class TerrainFarCardCalibrator : Component
{
	const int Width = 640, Height = 360, Bands = 8;

	Texture _target;
	TerrainWorldManager _manager;
	Color32[] _normal, _noRing, _noImpostors, _noBoth;
	int _phase, _wait;
	bool _apply;
	bool _applyDirect;
	bool _applyAmbient;
	bool _windWasOn;

	public static void Start( Scene scene, bool apply, bool direct = false, bool ambient = false )
	{
		var manager = scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( manager is null || !manager.IsValid() )
		{
			Log.Warning( "[CardCal] no terrain world in this scene" );
			return;
		}
		foreach ( var old in scene.GetAllComponents<TerrainFarCardCalibrator>() )
			old.GameObject.Destroy();

		var go = new GameObject( true, "FarCardCalibrator" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
		var probe = go.Components.Create<TerrainFarCardCalibrator>();
		probe._manager = manager;
		probe._apply = apply;
		probe._applyDirect = direct;
		probe._applyAmbient = ambient;
		probe._wait = 3;
		// Leaves sway (F_WIND) between the captures, which the four-way consistency test reads as neither ring nor card: hold the wind.
		if ( WindSystem.Current.IsValid() )
		{
			probe._windWasOn = WindSystem.Current.WindEnabled;
			WindSystem.Current.WindEnabled = false;
		}
		Log.Info( "[CardCal] measuring ring vs impostors — stand still" );
	}

	protected override void OnUpdate()
	{
		if ( _manager is null || !_manager.IsValid() )
		{
			GameObject.Destroy();
			return;
		}
		if ( --_wait > 0 )
			return;

		switch ( _phase )
		{
			case 0:
				_normal = Capture();
				_manager.FarRingHidden = true;
				_wait = 16;    // the ring list rebuilds at most every FarRebuildMinFrames (10) frames after going dirty
				break;
			case 1:
				_noRing = Capture();
				_manager.FarRingHidden = false;
				_manager.FarImpostorsEnabled = false;
				_wait = 16;    // the ring must be back (same rebuild cadence) before C is taken
				break;
			case 2:
				_noImpostors = Capture();
				_manager.FarRingHidden = true;
				_wait = 16;
				break;
			case 3:
				_noBoth = Capture();
				_manager.FarRingHidden = false;
				_manager.FarImpostorsEnabled = true;
				_wait = 2;
				break;
			default:
				Report();
				if ( _windWasOn && WindSystem.Current.IsValid() )
					WindSystem.Current.WindEnabled = true;
				GameObject.Destroy();
				return;
		}
		_phase++;
	}

	Color32[] Capture()
	{
		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return null;
		_target ??= Texture.CreateRenderTarget( "cardcal", ImageFormat.RGBA8888, new Vector2( Width, Height ) );
		cam.RenderToTexture( _target );
		var pixels = _target.GetPixels();
		return pixels is not null && pixels.Length == Width * Height ? pixels : null;
	}

	// 4 levels: dark leaves against dark ground at night differ by less than the 10 the first version used, which
	// left only the trunks attributed (grey "ring" 37 38 40) and sent the night calibration the wrong way (2026-10-20).
	static bool Differs( Color32 a, Color32 b ) =>
		Math.Abs( a.r - b.r ) > 4 || Math.Abs( a.g - b.g ) > 4 || Math.Abs( a.b - b.b ) > 4;

	void Report()
	{
		if ( _normal is null || _noRing is null || _noImpostors is null || _noBoth is null )
		{
			Log.Warning( "[CardCal] a capture failed (no scene camera?)" );
			return;
		}

		var ringSum = new double[Bands, 4];
		var impSum = new double[Bands, 4];
		var bandPixels = Width * Height / Bands;
		for ( var y = 0; y < Height; y++ )
		{
			var band = y * Bands / Height;
			for ( var x = 0; x < Width; x++ )
			{
				var i = (y * Width) + x;
				var p = _normal[i];
				// Four captures: A as is, B ring hidden, C cards hidden, D both hidden. A ring pixel changes when the
				// ring goes (A≠B, C≠D) and not when the cards go (A≈C, B≈D); a card pixel the other way round. Wind-moved
				// grass or leaves fail the "same" halves and drop out instead of polluting either side.
				var ring = Differs( p, _noRing[i] ) && Differs( _noImpostors[i], _noBoth[i] ) && !Differs( p, _noImpostors[i] ) && !Differs( _noRing[i], _noBoth[i] );
				var imp = Differs( p, _noImpostors[i] ) && Differs( _noRing[i], _noBoth[i] ) && !Differs( p, _noRing[i] ) && !Differs( _noImpostors[i], _noBoth[i] );
				if ( ring == imp )
					continue;   // both or neither: not attributable
				if ( ring )
				{ ringSum[band, 0] += p.r; ringSum[band, 1] += p.g; ringSum[band, 2] += p.b; ringSum[band, 3]++; }
				else
				{ impSum[band, 0] += p.r; impSum[band, 1] += p.g; impSum[band, 2] += p.b; impSum[band, 3]++; }
			}
		}

		// Only the bands that hold BOTH count: the ring runs 0.3-2.5 km and the cards 2.4-8 km, and atmospheric fog
		// legitimately blues the far ones more than the near ones (2026-10-20: the whole-screen ratio read 0.75 / 0.88 /
		// 0.69 by day while the boundary band read 0.96 / 0.94 / 0.98). Fall back to everything when no band has both.
		var anyShared = false;
		for ( var b = 0; b < Bands; b++ )
			anyShared |= ringSum[b, 3] >= bandPixels * 0.02 && impSum[b, 3] >= bandPixels * 0.02;

		double rr = 0, rg = 0, rb = 0, rn = 0, ir = 0, ig = 0, ib = 0, inn = 0;
		Log.Info( $"[CardCal] band (top→bottom): ring cover% mean RGB | impostor cover% mean RGB{(anyShared ? "   (* = boundary band, counted)" : "")}" );
		for ( var b = 0; b < Bands; b++ )
		{
			var rc = ringSum[b, 3];
			var ic = impSum[b, 3];
			var counted = !anyShared || (rc >= bandPixels * 0.02 && ic >= bandPixels * 0.02);
			if ( counted )
			{
				rr += ringSum[b, 0]; rg += ringSum[b, 1]; rb += ringSum[b, 2]; rn += rc;
				ir += impSum[b, 0]; ig += impSum[b, 1]; ib += impSum[b, 2]; inn += ic;
			}
			var ringText = rc > 0 ? $"{100 * rc / bandPixels,5:0.0}% {ringSum[b, 0] / rc,4:0} {ringSum[b, 1] / rc,4:0} {ringSum[b, 2] / rc,4:0}" : "   —  ";
			var impText = ic > 0 ? $"{100 * ic / bandPixels,5:0.0}% {impSum[b, 0] / ic,4:0} {impSum[b, 1] / ic,4:0} {impSum[b, 2] / ic,4:0}" : "   —  ";
			Log.Info( $"[CardCal]   {b}{(counted && anyShared ? "*" : " ")}: ring {ringText} | imp {impText}" );
		}

		if ( rn < 50 || inn < 50 )
		{
			Log.Warning( $"[CardCal] too few attributable pixels (ring {rn:0}, impostors {inn:0}) — look at a view with both" );
			return;
		}

		var ringMean = new Vector3( (float)(rr / rn), (float)(rg / rn), (float)(rb / rn) );
		var impMean = new Vector3( (float)(ir / inn), (float)(ig / inn), (float)(ib / inn) );
		var ratio = new Vector3( ringMean.x / Math.Max( 1f, impMean.x ), ringMean.y / Math.Max( 1f, impMean.y ), ringMean.z / Math.Max( 1f, impMean.z ) );
		Log.Info( $"[CardCal] overall: ring {ringMean.x:0} {ringMean.y:0} {ringMean.z:0} ({rn:0} px) | impostors {impMean.x:0} {impMean.y:0} {impMean.z:0} ({inn:0} px) | ring/imp ratio {ratio.x:0.000} {ratio.y:0.000} {ratio.z:0.000}" );

		if ( !_apply )
			return;

		if ( _applyAmbient )
		{
			var lum = (0.30f * ratio.x) + (0.59f * ratio.y) + (0.11f * ratio.z);
			var ambient = _manager.FarImpostorAmbient;
			var nextAmbient = Math.Clamp( ambient * MathF.Pow( lum, 1.5f ), 0.05f, 2f );
			_manager.FarImpostorAmbient = nextAmbient;
			Log.Info( $"[CardCal] applied: Far Impostor Ambient Light {ambient:0.000} → {nextAmbient:0.000} (run again to converge)" );
			return;
		}

		if ( _applyDirect )
		{
			// Day: scale the cards' direct-light response by the luminance ratio (the tonemapper is not linear:
			// a few runs converge). Run at night with 'apply tint' first, so the ambient half already matches.
			var lum = (0.30f * ratio.x) + (0.59f * ratio.y) + (0.11f * ratio.z);
			var direct = _manager.FarImpostorDirect;
			var nextDirect = Math.Clamp( direct * MathF.Pow( lum, 1.5f ), 0.05f, 2f );
			_manager.FarImpostorDirect = nextDirect;
			Log.Info( $"[CardCal] applied: Far Impostor Direct Light {direct:0.000} → {nextDirect:0.000} (run again to converge)" );
			return;
		}

		// Vertex paint is linearised with pow 2.2 in the shader; the tonemapper on top is not linear, so this
		// converges over a few runs rather than landing in one.
		var tint = _manager.FarImpostorTint;
		var next = new Color(
			Math.Clamp( tint.r * MathF.Pow( ratio.x, 1f / 2.2f ), 0.05f, 4f ),
			Math.Clamp( tint.g * MathF.Pow( ratio.y, 1f / 2.2f ), 0.05f, 4f ),
			Math.Clamp( tint.b * MathF.Pow( ratio.z, 1f / 2.2f ), 0.05f, 4f ), 1f );
		_manager.FarImpostorTint = next;
		Log.Info( $"[CardCal] applied: Impostor Tint {tint.r:0.000} {tint.g:0.000} {tint.b:0.000} → {next.r:0.000} {next.g:0.000} {next.b:0.000} (run again to converge)" );
	}

	/// <summary><c>far_card_calibrate</c> measures; <c>far_card_calibrate apply</c> / <c>apply tint</c> sets the tint (do it by NIGHT); <c>far_card_calibrate apply direct</c> sets the direct-light scale (do it by DAY).</summary>
	[ConCmd( "far_card_calibrate" )]
	public static void ConCmdCalibrate( string apply = "", string what = "tint" )
	{
		var doApply = apply?.Trim().ToLowerInvariant() is "apply" or "1";
		var w = what?.Trim().ToLowerInvariant();
		Start( Sandbox.Game.ActiveScene, doApply, doApply && w == "direct", doApply && w == "ambient" );
	}
}
