using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// One per fighting pawn (<see cref="PlayerCombat"/>): the white slash ribbon along the damage arc
/// of a strike (bright at the tip edge, feathering toward the heel, fading
/// out over <see cref="TrailSeconds"/>) and the short white streaks that burst off a shield when a
/// block lands, and the heavy-ready glint: a bright band that runs grip → tip along the held weapon and
/// bursts into a small star at the tip the moment a held swing becomes a heavy. Presentation only - fed by
/// the same blade samples the host sweep uses, never the other way round. Drawn with
/// <see cref="UnlitVfxGeometry"/> in world space.
/// </summary>
public sealed class MeleeSwingVfx : SceneCustomObject
{
	const int MaxSamples = 96;
	const int MaxSparks = 16;
	/// <summary>A sample this far from the last one (units) starts a new ribbon - the next swing.</summary>
	const float BreakDistance = 140f;
	/// <summary>A gap this long since the last sample starts a new ribbon (the held cocked pose dedupes to one sample, so a long windup must not break it).</summary>
	const float BreakSeconds = 0.6f;

	struct Sample
	{
		public Vector3 Tip;
		public Vector3 Heel;
		public float Time;
		public bool Break;
	}

	struct Spark
	{
		public Vector3 Origin;
		public Vector3 Dir;
		public float Born;
		public float Life;
		public float Length;
		public float Width;
	}

	readonly Sample[] _samples = new Sample[MaxSamples];
	int _sampleStart;
	int _sampleCount;

	readonly Spark[] _sparks = new Spark[MaxSparks];
	int _sparkCount;

	readonly List<Vertex> _verts = new( 2048 );
	float _now;

	public float TrailSeconds { get; set; } = 0.16f;
	public Color TrailColor { get; set; } = Color.White;
	/// <summary>Alpha at the heel edge relative to the tip edge - the inside of the crescent fades away.</summary>
	public float TrailHeelAlpha { get; set; } = 0.1f;
	public Color SparkColor { get; set; } = Color.White;

	// Heavy-ready glint: the weapon's grip / tip are re-fed every frame (it rides the cocked pose).
	float _flashBorn = -1f;
	float _flashSeconds;
	Vector3 _flashHeel;
	Vector3 _flashTip;
	bool _flashHasBlade;
	public Color FlashColor { get; set; } = Color.White;

	bool FlashActive => _flashBorn >= 0f && _now - _flashBorn <= _flashSeconds;

	/// <summary>Start the heavy-ready glint (the blade is supplied each frame with <see cref="SetFlashBlade"/>).</summary>
	public void StartFlash( float seconds, float now )
	{
		_flashBorn = now;
		_now = now;
		_flashSeconds = MathF.Max( 0.05f, seconds );
		_flashHasBlade = false;
	}

	/// <summary>Grip-side point and tip of the held weapon this frame (world).</summary>
	public void SetFlashBlade( Vector3 heel, Vector3 tip )
	{
		_flashHeel = heel;
		_flashTip = tip;
		_flashHasBlade = true;
	}

	/// <summary>True while the glint plays - the owner keeps feeding the blade until then.</summary>
	public bool WantsFlashBlade => _flashBorn >= 0f && _now - _flashBorn <= _flashSeconds;

	/// <summary>Resolved here on the main thread; the render callback runs on a worker.</summary>
	readonly Material _material;

	public MeleeSwingVfx( SceneWorld world ) : base( world )
	{
		_material = UnlitVfxGeometry.CreateMaterial();
		Flags.CastShadows = false;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		RenderingEnabled = false;
	}

	public bool IsIdle => _sampleCount == 0 && _sparkCount == 0 && !(FlashActive && _flashHasBlade);

	/// <summary>Ribbon sample (world): outer (tip) edge, inner edge, its time. <paramref name="newRibbon"/> starts the next swing's ribbon.</summary>
	public void PushSample( Vector3 tip, Vector3 heel, float now, bool newRibbon = false )
	{
		var brk = newRibbon;
		if ( !brk && _sampleCount > 0 )
		{
			ref var last = ref _samples[(_sampleStart + _sampleCount - 1) % MaxSamples];
			if ( Vector3.DistanceBetween( last.Tip, tip ) > BreakDistance || now - last.Time > BreakSeconds )
				brk = true;
			else if ( Vector3.DistanceBetween( last.Tip, tip ) < 0.5f && Vector3.DistanceBetween( last.Heel, heel ) < 0.5f )
				return; // the blade did not move: no zero-area quad
		}

		if ( _sampleCount == MaxSamples )
		{
			_sampleStart = (_sampleStart + 1) % MaxSamples;
			_sampleCount--;
		}

		_samples[(_sampleStart + _sampleCount) % MaxSamples] = new Sample { Tip = tip, Heel = heel, Time = now, Break = brk };
		_sampleCount++;
	}

	/// <summary>Streaks fanning out from <paramref name="center"/> around <paramref name="normal"/> (the shield face).</summary>
	public void AddSparks( Vector3 center, Vector3 normal, int count, float lengthUnits, float seconds, float now )
	{
		if ( normal.LengthSquared < 1e-6f )
			normal = Vector3.Up;
		normal = normal.Normal;
		var tangent = Vector3.Cross( normal, Vector3.Up );
		if ( tangent.LengthSquared < 1e-4f )
			tangent = Vector3.Cross( normal, Vector3.Forward );
		tangent = tangent.Normal;
		var bitangent = Vector3.Cross( normal, tangent ).Normal;

		var rng = Sandbox.Game.Random;
		var baseAngle = rng.Float( 0f, 360f );
		for ( var i = 0; i < count && _sparkCount < MaxSparks; i++ )
		{
			// Spread round the face, tilted a little off it so they read as flying off the shield.
			var angle = (baseAngle + i * (360f / Math.Max( 1, count )) + rng.Float( -25f, 25f )) * (MathF.PI / 180f);
			var inPlane = tangent * MathF.Cos( angle ) + bitangent * MathF.Sin( angle );
			var dir = (inPlane + normal * rng.Float( 0.25f, 0.7f )).Normal;
			_sparks[_sparkCount++] = new Spark
			{
				Origin = center + dir * (lengthUnits * 0.15f),
				Dir = dir,
				Born = now,
				Life = MathF.Max( 0.05f, seconds ) * rng.Float( 0.75f, 1.15f ),
				Length = lengthUnits * rng.Float( 0.7f, 1.3f ),
				Width = rng.Float( 0.9f, 1.6f ),
			};
		}
	}

	/// <summary>Main thread, once per frame: drop what has faded, refresh the bounds.</summary>
	public void Tick( float now )
	{
		_now = now;
		var life = MathF.Max( 0.03f, TrailSeconds );
		while ( _sampleCount > 0 && now - _samples[_sampleStart].Time > life )
		{
			_sampleStart = (_sampleStart + 1) % MaxSamples;
			_sampleCount--;
		}

		for ( var i = _sparkCount - 1; i >= 0; i-- )
		{
			if ( now - _sparks[i].Born > _sparks[i].Life )
				_sparks[i] = _sparks[--_sparkCount];
		}

		if ( IsIdle )
		{
			RenderingEnabled = false;
			return;
		}

		var box = default( BBox );
		var any = false;
		for ( var i = 0; i < _sampleCount; i++ )
		{
			ref var s = ref _samples[(_sampleStart + i) % MaxSamples];
			UnlitVfxGeometry.Grow( ref box, ref any, s.Tip );
			UnlitVfxGeometry.Grow( ref box, ref any, s.Heel );
		}

		for ( var i = 0; i < _sparkCount; i++ )
		{
			UnlitVfxGeometry.Grow( ref box, ref any, _sparks[i].Origin );
			UnlitVfxGeometry.Grow( ref box, ref any, _sparks[i].Origin + _sparks[i].Dir * _sparks[i].Length );
		}

		if ( FlashActive && _flashHasBlade )
		{
			UnlitVfxGeometry.Grow( ref box, ref any, _flashHeel );
			UnlitVfxGeometry.Grow( ref box, ref any, _flashTip );
		}

		Bounds = box.Grow( 16f );
		RenderingEnabled = true;
	}

	public override void RenderSceneObject()
	{
		_verts.Clear();
		BuildRibbon();
		BuildSparks();
		BuildFlash();
		if ( _verts.Count == 0 )
			return;

		Graphics.Draw( _verts, _verts.Count, _material, Attributes, Graphics.PrimitiveType.Triangles );
	}

	void BuildRibbon()
	{
		if ( _sampleCount < 2 )
			return;

		var life = MathF.Max( 0.03f, TrailSeconds );
		var color = TrailColor;
		var heelAlpha = Math.Clamp( TrailHeelAlpha, 0f, 1f );

		for ( var i = 1; i < _sampleCount; i++ )
		{
			ref var a = ref _samples[(_sampleStart + i - 1) % MaxSamples];
			ref var b = ref _samples[(_sampleStart + i) % MaxSamples];
			if ( b.Break )
				continue;

			var fadeA = Fade( (_now - a.Time) / life );
			var fadeB = Fade( (_now - b.Time) / life );
			var tipA = color.WithAlpha( color.a * fadeA );
			var tipB = color.WithAlpha( color.a * fadeB );
			var heelA = color.WithAlpha( color.a * fadeA * heelAlpha );
			var heelB = color.WithAlpha( color.a * fadeB * heelAlpha );

			UnlitVfxGeometry.AddQuad( _verts, a.Heel, a.Tip, b.Tip, b.Heel, heelA, tipA, tipB, heelB );

			// Thin feather past the tip so the outer edge is soft, not a hard polygon edge.
			var featherA = a.Tip + (a.Tip - a.Heel) * 0.06f;
			var featherB = b.Tip + (b.Tip - b.Heel) * 0.06f;
			UnlitVfxGeometry.AddQuad( _verts, a.Tip, featherA, featherB, b.Tip, tipA, tipA.WithAlpha( 0f ), tipB.WithAlpha( 0f ), tipB );
		}
	}

	void BuildSparks()
	{
		if ( _sparkCount == 0 )
			return;

		for ( var i = 0; i < _sparkCount; i++ )
		{
			ref var s = ref _sparks[i];
			var age01 = Math.Clamp( (_now - s.Born) / s.Life, 0f, 1f );
			// Fast out, easing to a stop; the tail trails a third of the length behind the head.
			var head = (1f - (1f - age01) * (1f - age01)) * s.Length;
			var tail = MathF.Max( 0f, head - s.Length * 0.35f );
			var alpha = (1f - age01) * SparkColor.a;
			var c0 = SparkColor.WithAlpha( alpha * 0.25f );
			var c1 = SparkColor.WithAlpha( alpha );
			UnlitVfxGeometry.AddCrossLine( _verts, s.Origin + s.Dir * tail, s.Origin + s.Dir * head, s.Width, c0, c1 );
		}
	}

	/// <summary>
	/// Heavy-ready glint. First 60 %: a bright band (a fifth of the weapon long) runs from the grip to the tip with a
	/// faint glow over the whole weapon. Then a four-point star opens at the tip and fades.
	/// </summary>
	void BuildFlash()
	{
		if ( !FlashActive || !_flashHasBlade )
			return;

		var axis = _flashTip - _flashHeel;
		var length = axis.Length;
		if ( length < 1f )
			return;

		var dir = axis / length;
		var t = Math.Clamp( (_now - _flashBorn) / _flashSeconds, 0f, 1f );
		var color = FlashColor;
		var width = MathF.Max( 2f, length * 0.1f );

		// Whole-weapon glow: up fast, out slow.
		var glow = (t < 0.15f ? t / 0.15f : 1f - (t - 0.15f) / 0.85f) * 0.35f * color.a;
		UnlitVfxGeometry.AddCrossLine( _verts, _flashHeel, _flashTip, width * 1.6f, color.WithAlpha( glow * 0.6f ), color.WithAlpha( glow ) );

		// Running band.
		var run = Math.Clamp( t / 0.6f, 0f, 1f );
		var band = 0.2f;
		var center = -band + run * (1f + band);
		var a0 = Math.Clamp( center - band, 0f, 1f );
		var a1 = Math.Clamp( center, 0f, 1f );
		var a2 = Math.Clamp( center + band, 0f, 1f );
		var bandAlpha = (t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f) * color.a;
		if ( a1 > a0 )
			UnlitVfxGeometry.AddCrossLine( _verts, _flashHeel + dir * (a0 * length), _flashHeel + dir * (a1 * length), width * 2.2f,
				color.WithAlpha( 0f ), color.WithAlpha( bandAlpha ) );
		if ( a2 > a1 )
			UnlitVfxGeometry.AddCrossLine( _verts, _flashHeel + dir * (a1 * length), _flashHeel + dir * (a2 * length), width * 2.2f,
				color.WithAlpha( bandAlpha ), color.WithAlpha( 0f ) );

		// Star at the tip once the band arrives.
		if ( t < 0.45f )
			return;

		var st = (t - 0.45f) / 0.55f;
		var size = length * (0.18f + 0.3f * MathF.Sin( MathF.Min( st, 1f ) * MathF.PI * 0.5f ));
		var starAlpha = (1f - st) * color.a;
		var side = Vector3.Cross( dir, Vector3.Up );
		if ( side.LengthSquared < 1e-4f )
			side = Vector3.Cross( dir, Vector3.Left );
		side = side.Normal;
		var up = Vector3.Cross( side, dir ).Normal;
		foreach ( var ray in new[] { dir, -dir, side, -side, up, -up } )
			UnlitVfxGeometry.AddCrossLine( _verts, _flashTip, _flashTip + ray * size, width * 0.9f,
				color.WithAlpha( starAlpha ), color.WithAlpha( 0f ) );
	}

	/// <summary>Holds near full for the first part of the life, then drops away.</summary>
	static float Fade( float age01 )
	{
		age01 = Math.Clamp( age01, 0f, 1f );
		var t = 1f - age01;
		return t * t * (3f - 2f * t);
	}
}
