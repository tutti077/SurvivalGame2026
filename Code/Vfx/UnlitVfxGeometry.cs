using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Shared materials and vertex helpers for the small unlit effects drawn straight from a
/// <see cref="SceneCustomObject"/> (sword slash trail, block sparks, circuit wires). Everything is
/// world-space triangles in <c>shaders/vfx_unlit.shader</c> (built like the engine's gizmo line shader:
/// the common/*.hlsl vertex path drops the COLOR0 input): vertex colour + alpha, unlit, so the effect is
/// always at its own brightness whatever the scene light.
/// </summary>
public static class UnlitVfxGeometry
{
	public const string ShaderPath = "shaders/vfx_unlit.shader";
	public const string TintShaderPath = "shaders/vfx_unlit_tint.shader";

	static Material _tintMaterial;

	/// <summary>
	/// Immediate-draw material (vertex colour + alpha, unlit). Call on the main thread only - each effect resolves it
	/// once in its constructor and draws with that copy: <c>RenderSceneObject</c> runs on render worker threads, where
	/// <c>Material.FromShader</c> throws "Create must be called on the main thread!". Not cached in a static: one held
	/// a stale material across a hotload.
	/// </summary>
	public static Material CreateMaterial() => Sandbox.Material.FromShader( ShaderPath );

	/// <summary>ModelRenderer override material (renderer Tint, unlit) - the circuit spheres.</summary>
	public static Material TintMaterial => _tintMaterial ??= Sandbox.Material.FromShader( TintShaderPath );

	/// <summary>Two triangles a-b-c, a-c-d (winding does not matter: the shader culls nothing).</summary>
	public static void AddQuad( List<Vertex> verts, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd )
	{
		verts.Add( new Vertex( a, ca ) );
		verts.Add( new Vertex( b, cb ) );
		verts.Add( new Vertex( c, cc ) );
		verts.Add( new Vertex( a, ca ) );
		verts.Add( new Vertex( c, cc ) );
		verts.Add( new Vertex( d, cd ) );
	}

	/// <summary>
	/// A line segment as two strips crossed at right angles (<paramref name="width"/> units across), so it reads
	/// from any direction without knowing the camera - the render callback has no camera to ask.
	/// </summary>
	public static void AddCrossLine( List<Vertex> verts, Vector3 p0, Vector3 p1, float width, Color c0, Color c1 )
	{
		var dir = p1 - p0;
		if ( dir.LengthSquared < 1e-6f )
			return;

		var axis = dir.Normal;
		var sideA = Vector3.Cross( axis, Vector3.Up );
		if ( sideA.LengthSquared < 1e-4f )
			sideA = Vector3.Cross( axis, Vector3.Left );
		sideA = sideA.Normal;
		var sideB = Vector3.Cross( axis, sideA ).Normal;
		var half = MathF.Max( 0.05f, width ) * 0.5f;
		sideA *= half;
		sideB *= half;

		AddQuad( verts, p0 - sideA, p0 + sideA, p1 + sideA, p1 - sideA, c0, c0, c1, c1 );
		AddQuad( verts, p0 - sideB, p0 + sideB, p1 + sideB, p1 - sideB, c0, c0, c1, c1 );
	}

	/// <summary>Grows <paramref name="box"/> to hold <paramref name="p"/> (the first point starts the box).</summary>
	public static void Grow( ref BBox box, ref bool any, Vector3 p )
	{
		if ( !any )
		{
			box = new BBox( p, p );
			any = true;
			return;
		}

		box = box.AddPoint( p );
	}
}
