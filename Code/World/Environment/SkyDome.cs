using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Camera-centred sky sphere drawn with <c>shaders/environment/sky_dome.shader</c>: gradient,
/// sun glow, a procedural star layer and two cloud layers that scroll with <see cref="WindSystem"/>.
/// It owns no colours — <see cref="EnvironmentDayNightCycle"/> hands it the blended preset look and
/// the star fade each frame via <see cref="Apply"/>. Sits behind the sun / moon disks.
/// </summary>
[Title( "Sky Dome" )]
public sealed class SkyDome : Component
{
	public const string DefaultMaterialPath = "materials/environment/sky_dome.vmat";

	[Property, Title( "Material" )]
	public Material DomeMaterial { get; set; }

	[Property, Title( "Radius (m)" ), Description( "Must sit beyond the sun / moon disk orbit and inside the camera's Z Far. Default 9000 m." )]
	public float RadiusMeters { get; set; } = 9000f;

	[Property, Title( "Cloud drift (uv/s at full wind)" ), Range( 0f, 0.05f ), Step( 0.001f ), Description( "How fast the cloud ceiling scrolls at wind strength 1, before the preset's Cloud Speed." )]
	public float CloudDriftPerSecond { get; set; } = 0.006f;

	ModelRenderer _renderer;
	float _builtRadius = -1f;
	Vector2 _cloudOffset;
	float _cloudScale = 0.35f;
	float _cloudSpeed = 1f;

	protected override void OnStart()
	{
		Rebuild();
	}

	protected override void OnUpdate()
	{
		if ( MathF.Abs( _builtRadius - RadiusMeters ) > 0.5f )
			Rebuild();

		if ( _renderer is null || !_renderer.IsValid() )
			return;

		// Scroll with the wind; integrate so a heading change never jumps the clouds.
		var wind = WindSystem.Current;
		var heading = wind is not null && wind.IsValid() ? new Vector2( wind.Direction.x, wind.Direction.y ) : new Vector2( 1f, 0.3f );
		if ( heading.LengthSquared < 1e-6f )
			heading = new Vector2( 1f, 0f );
		var strength = wind is not null && wind.IsValid() ? wind.Strength01 : 0.3f;
		var speed = CloudDriftPerSecond * _cloudSpeed * (0.25f + strength);
		_cloudOffset += heading.Normal * (speed * Time.Delta);
		_cloudOffset = new Vector2( _cloudOffset.x - MathF.Floor( _cloudOffset.x ), _cloudOffset.y - MathF.Floor( _cloudOffset.y ) );

		var attrs = _renderer.Attributes;
		attrs.Set( "CloudOffset", _cloudOffset );
		attrs.Set( "CloudScale", _cloudScale );
	}

	protected override void OnPreRender()
	{
		// Follow the camera (position only) so the sky has no parallax.
		var cam = Scene.Camera;
		if ( cam is not null && cam.IsValid() )
			WorldPosition = cam.WorldPosition;
		WorldRotation = Rotation.Identity;
	}

	/// <summary>Push the blended look for this frame. Called by <see cref="EnvironmentDayNightCycle"/>. <paramref name="starVisibility"/> 0 = day, 1 = deep night.</summary>
	public void Apply( in SkyLook look, Vector3 sunDirection, float cloudScale, float cloudSpeed, float starVisibility )
	{
		_cloudScale = cloudScale;
		_cloudSpeed = cloudSpeed;

		if ( _renderer is null || !_renderer.IsValid() )
			return;

		var attrs = _renderer.Attributes;
		attrs.Set( "SkyZenith", Rgb( look.Zenith ) );
		attrs.Set( "SkyHorizon", Rgb( look.Horizon ) );
		attrs.Set( "SkySunGlow", new Vector4( look.SunGlow.r, look.SunGlow.g, look.SunGlow.b, look.SunGlow.a ) );
		attrs.Set( "CloudLit", Rgb( look.CloudLit ) );
		attrs.Set( "CloudShade", Rgb( look.CloudShade ) );
		attrs.Set( "CloudCover", look.CloudCover );
		attrs.Set( "SunDirection", sunDirection.Normal );
		attrs.Set( "StarVisibility", Math.Clamp( starVisibility, 0f, 1f ) );
	}

	static Vector3 Rgb( Color c ) => new( c.r, c.g, c.b );

	void Rebuild()
	{
		DomeMaterial ??= Material.Load( DefaultMaterialPath );
		var radius = TerrainWorldUnits.MetersToEngine( Math.Max( 100f, RadiusMeters ) );

		_renderer ??= Components.Get<ModelRenderer>();
		if ( _renderer is null )
		{
			Log.Warning( $"[SkyDome] '{GameObject.Name}' needs a ModelRenderer on the same object — add it in the scene." );
			_builtRadius = RadiusMeters;
			return;
		}

		_renderer.Model = BuildSphere( DomeMaterial, radius );
		_renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		_builtRadius = RadiusMeters;
	}

	/// <summary>Low-poly UV sphere; the shader derives the view direction per pixel, so 32×16 is plenty.</summary>
	static Model BuildSphere( Material material, float radius )
	{
		const int Segments = 32;
		const int Rings = 16;
		var vertexCount = (Segments + 1) * (Rings + 1);
		var indexCount = Segments * Rings * 6;

		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertexCount );
		mesh.CreateIndexBuffer( indexCount );

		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			var v = 0;
			for ( var r = 0; r <= Rings; r++ )
			{
				var phi = MathF.PI * r / Rings; // 0 = top
				for ( var s = 0; s <= Segments; s++ )
				{
					var theta = 2f * MathF.PI * s / Segments;
					var dir = new Vector3( MathF.Sin( phi ) * MathF.Cos( theta ), MathF.Sin( phi ) * MathF.Sin( theta ), MathF.Cos( phi ) );
					vertices[v++] = new Vertex
					{
						Position = dir * radius,
						Normal = -dir,
						Tangent = new Vector4( 1f, 0f, 0f, 1f ),
						TexCoord0 = new Vector2( s / (float)Segments, r / (float)Rings ),
						Color = Color.White,
					};
				}
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			var n = 0;
			for ( var r = 0; r < Rings; r++ )
			{
				for ( var s = 0; s < Segments; s++ )
				{
					var i0 = r * (Segments + 1) + s;
					var i1 = i0 + 1;
					var i2 = i0 + Segments + 1;
					var i3 = i2 + 1;
					indices[n++] = i0;
					indices[n++] = i2;
					indices[n++] = i1;
					indices[n++] = i1;
					indices[n++] = i2;
					indices[n++] = i3;
				}
			}
		} );

		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, radius * 2f );
		return new ModelBuilder().AddMesh( mesh ).Create();
	}
}
