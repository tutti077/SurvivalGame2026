using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Little pink hearts that drift up off a tamed or petted animal (Mark 2026-11-02: "emit some hearts").
/// Flat heart polygons in the local camera's plane, drawn unlit through <see cref="UnlitVfxGeometry"/>;
/// the camera rotation is handed in from the main thread each tick. Local presentation only.
/// </summary>
public sealed class HeartsVfx : SceneCustomObject
{
	const int MaxHearts = 12;
	const float LifeSeconds = 1.4f;
	const float RiseUnits = 34f;
	const float SizeUnits = 5f;
	static readonly Color HeartColor = new( 1f, 0.36f, 0.5f );

	struct Heart
	{
		public Vector3 Origin;
		public float Born;
		public float Sway;
	}

	readonly Heart[] _hearts = new Heart[MaxHearts];
	int _count;
	readonly List<Vertex> _verts = new( 512 );
	Rotation _cameraRot = Rotation.Identity;
	float _now;

	/// <summary>Heart outline (x right, y up), unit size.</summary>
	static readonly Vector2[] Outline = BuildOutline();

	/// <summary>Resolved here on the main thread; the render callback runs on a worker.</summary>
	readonly Material _material;

	public HeartsVfx( SceneWorld world ) : base( world )
	{
		_material = UnlitVfxGeometry.CreateMaterial();
		Flags.CastShadows = false;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		RenderingEnabled = false;
	}

	public bool IsIdle => _count == 0;

	public void Spawn( Vector3 origin, int count, float now )
	{
		var rng = Sandbox.Game.Random;
		for ( var i = 0; i < count && _count < MaxHearts; i++ )
		{
			_hearts[_count++] = new Heart
			{
				Origin = origin + new Vector3( rng.Float( -8f, 8f ), rng.Float( -8f, 8f ), rng.Float( 0f, 6f ) ),
				Born = now + i * 0.12f,
				Sway = rng.Float( 0f, MathF.Tau ),
			};
		}
	}

	public void Tick( float now, Rotation cameraRotation )
	{
		_now = now;
		_cameraRot = cameraRotation;
		for ( var i = _count - 1; i >= 0; i-- )
		{
			if ( now - _hearts[i].Born > LifeSeconds )
				_hearts[i] = _hearts[--_count];
		}

		if ( _count == 0 )
		{
			RenderingEnabled = false;
			return;
		}

		var box = default( BBox );
		var any = false;
		for ( var i = 0; i < _count; i++ )
		{
			UnlitVfxGeometry.Grow( ref box, ref any, _hearts[i].Origin );
			UnlitVfxGeometry.Grow( ref box, ref any, _hearts[i].Origin + Vector3.Up * RiseUnits );
		}

		Bounds = box.Grow( SizeUnits * 2f );
		RenderingEnabled = true;
	}

	public override void RenderSceneObject()
	{
		_verts.Clear();
		var right = _cameraRot.Right;
		var up = _cameraRot.Up;
		for ( var i = 0; i < _count; i++ )
		{
			ref var h = ref _hearts[i];
			var t = Math.Clamp( (_now - h.Born) / LifeSeconds, 0f, 1f );
			if ( t <= 0f )
				continue;

			var alpha = t < 0.15f ? t / 0.15f : 1f - (t - 0.15f) / 0.85f;
			var size = SizeUnits * (0.7f + 0.3f * MathF.Min( 1f, t * 4f ));
			var center = h.Origin + Vector3.Up * (RiseUnits * t) + right * (MathF.Sin( h.Sway + t * 5f ) * 3f);
			var color = HeartColor.WithAlpha( alpha );

			for ( var k = 0; k < Outline.Length; k++ )
			{
				var a = Outline[k];
				var b = Outline[(k + 1) % Outline.Length];
				_verts.Add( new Vertex( center, color ) );
				_verts.Add( new Vertex( center + (right * a.x + up * a.y) * size, color ) );
				_verts.Add( new Vertex( center + (right * b.x + up * b.y) * size, color ) );
			}
		}

		if ( _verts.Count == 0 )
			return;

		Graphics.Draw( _verts, _verts.Count, _material, Attributes, Graphics.PrimitiveType.Triangles );
	}

	static Vector2[] BuildOutline()
	{
		// The classic parametric heart, normalised to about ±0.5.
		const int n = 20;
		var pts = new Vector2[n];
		for ( var i = 0; i < n; i++ )
		{
			var t = i / (float)n * MathF.Tau;
			var x = 16f * MathF.Pow( MathF.Sin( t ), 3f );
			var y = 13f * MathF.Cos( t ) - 5f * MathF.Cos( 2f * t ) - 2f * MathF.Cos( 3f * t ) - MathF.Cos( 4f * t );
			pts[i] = new Vector2( x / 32f, y / 32f );
		}

		return pts;
	}
}
