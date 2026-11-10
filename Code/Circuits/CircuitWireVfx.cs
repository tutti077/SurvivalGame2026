using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The wires the wire stripper shows: unlit camera-facing strips in the cable colour between two node
/// spheres, drawn through <see cref="UnlitVfxGeometry"/> so they are at full brightness in any light.
/// A wire that was just connected carries a glow (<c>pulse01</c> 1 → 0): a brighter, wider core and a
/// white spark that runs from the source sphere to the target one - the "connecting" effect that then
/// leaves the coloured line behind. The overlay rebuilds the list every tick it is shown.
/// </summary>
public sealed class CircuitWireVfx : SceneCustomObject
{
	struct Wire
	{
		public Vector3 A;
		public Vector3 B;
		public Color Color;
		public float Width;
		public float Pulse01;
	}

	readonly List<Wire> _wires = new();
	readonly List<Vertex> _verts = new( 512 );

	/// <summary>Resolved here on the main thread; the render callback runs on a worker.</summary>
	readonly Material _material;

	public CircuitWireVfx( SceneWorld world ) : base( world )
	{
		_material = UnlitVfxGeometry.CreateMaterial();
		Flags.CastShadows = false;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		RenderingEnabled = false;
	}

	public void Begin() => _wires.Clear();

	public void AddWire( Vector3 a, Vector3 b, Color color, float width, float pulse01 = 0f )
	{
		_wires.Add( new Wire { A = a, B = b, Color = color, Width = width, Pulse01 = Math.Clamp( pulse01, 0f, 1f ) } );
	}

	/// <summary>After the adds: bounds + visibility for this frame.</summary>
	public void End()
	{
		if ( _wires.Count == 0 )
		{
			RenderingEnabled = false;
			return;
		}

		var box = default( BBox );
		var any = false;
		for ( var i = 0; i < _wires.Count; i++ )
		{
			UnlitVfxGeometry.Grow( ref box, ref any, _wires[i].A );
			UnlitVfxGeometry.Grow( ref box, ref any, _wires[i].B );
		}

		Bounds = box.Grow( 16f );
		RenderingEnabled = true;
	}

	public void Hide()
	{
		_wires.Clear();
		RenderingEnabled = false;
	}

	public override void RenderSceneObject()
	{
		_verts.Clear();
		for ( var i = 0; i < _wires.Count; i++ )
		{
			var w = _wires[i];
			var core = w.Color;
			var width = w.Width;
			if ( w.Pulse01 > 0f )
			{
				// Glow: brighter toward white and a little wider while the pulse lives.
				core = Color.Lerp( w.Color, Color.White, w.Pulse01 * 0.6f );
				width *= 1f + w.Pulse01 * 0.8f;
				// Halo behind the core.
				UnlitVfxGeometry.AddCrossLine( _verts, w.A, w.B, width * 3f, w.Color.WithAlpha( 0.25f * w.Pulse01 ), w.Color.WithAlpha( 0.25f * w.Pulse01 ) );
			}

			UnlitVfxGeometry.AddCrossLine( _verts, w.A, w.B, width, core, core );

			if ( w.Pulse01 > 0f )
			{
				// The spark travels source → target over the first 70 % of the pulse.
				var travel = Math.Clamp( (1f - w.Pulse01) / 0.7f, 0f, 1f );
				var head = Vector3.Lerp( w.A, w.B, travel );
				var tail = Vector3.Lerp( w.A, w.B, MathF.Max( 0f, travel - 0.18f ) );
				var alpha = travel < 1f ? 1f : Math.Clamp( w.Pulse01 / 0.3f, 0f, 1f );
				UnlitVfxGeometry.AddCrossLine( _verts, tail, head, width * 2.2f, Color.White.WithAlpha( 0f ), Color.White.WithAlpha( alpha ) );
			}
		}

		if ( _verts.Count == 0 )
			return;

		Graphics.Draw( _verts, _verts.Count, _material, Attributes, Graphics.PrimitiveType.Triangles );
	}
}
