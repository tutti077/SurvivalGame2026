using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Dense camera-centered star field (milky-way band + sparse field).
/// Opacity fades via Tint alpha — colors stay put; never RGB-dim to black.
/// </summary>
[Title( "Procedural Star Field" )]
public sealed class ProceduralStarField : Component
{
	public const string DefaultStarMaterialPath = "materials/environment/star_unlit.vmat";
	public const string StarModelPath = "models/dev/sphere.vmdl";

	/// <summary>Behind sun/moon disks so stars never draw in front of them.</summary>
	public const float DefaultShellRadiusMeters = 7500f;

	public const float DefaultStarScale = 3.5f;
	public const float DefaultBrightStarScale = 11f;
	public const int DefaultStarCount = 56000;

	[Property, Group( "Stars" ), Title( "Star count" ), Range( 200, 80000 ), Step( 500 )]
	public int StarCount { get; set; } = DefaultStarCount;

	[Property, Group( "Stars" ), Title( "Shell radius (m)" )]
	public float ShellRadiusMeters { get; set; } = DefaultShellRadiusMeters;

	[Property, Group( "Stars" ), Title( "Shell depth jitter" ), Range( 0f, 0.25f ), Step( 0.01f ), Description( "Fractional radius variation so stars aren’t on one shell." )]
	public float ShellDepthJitter { get; set; } = 0.1f;

	[Property, Group( "Stars" ), Title( "Base star scale" )]
	public float StarScale { get; set; } = DefaultStarScale;

	[Property, Group( "Stars" ), Title( "Bright star scale" )]
	public float BrightStarScale { get; set; } = DefaultBrightStarScale;

	[Property, Group( "Stars" ), Title( "Bright star chance" ), Range( 0f, 0.2f ), Step( 0.01f )]
	public float BrightStarChance { get; set; } = 0.06f;

	[Property, Group( "Stars" ), Title( "Milky Way fraction" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Share of stars packed into the galactic band." )]
	public float MilkyWayFraction { get; set; } = 0.72f;

	[Property, Group( "Stars" ), Title( "Milky Way thickness" ), Range( 0.02f, 0.35f ), Step( 0.01f )]
	public float MilkyWayThickness { get; set; } = 0.12f;

	[Property, Group( "Stars" ), Title( "Milky Way tilt (°)" )]
	public float MilkyWayTiltDegrees { get; set; } = 58f;

	[Property, Group( "Stars" ), Title( "Seed" )]
	public int Seed { get; set; } = 2026;

	[Property, Group( "Stars" )]
	public Material StarMaterial { get; set; }

	[Property, Group( "Runtime" ), Title( "Opacity 0-1" ), Range( 0f, 1f ), Step( 0.01f ), Description( "Night fade — multiplies Tint alpha only." )]
	public float Visibility { get; set; }

	GameObject _root;
	ModelRenderer[] _renderers;
	Vector3[] _dirs;
	float[] _radiusFactors;
	float[] _scales;
	Color[] _colors;
	bool _built;
	float _builtShellMeters = -1f;
	float _builtStarScale = -1f;
	float _builtBrightScale = -1f;
	int _builtCount = -1;
	float _lastOpacityQuant = -1f;

	protected override void OnStart()
	{
		Rebuild();
	}

	protected override void OnUpdate()
	{
		if ( !_built || _root is null || !_root.IsValid()
		     || _builtCount != Math.Clamp( StarCount, 200, 80000 )
		     || MathF.Abs( _builtShellMeters - ShellRadiusMeters ) > 0.5f
		     || MathF.Abs( _builtStarScale - StarScale ) > 0.01f
		     || MathF.Abs( _builtBrightScale - BrightStarScale ) > 0.01f )
			Rebuild();

		if ( _root is null || !_root.IsValid() )
			return;

		var opacity = Math.Clamp( Visibility, 0f, 1f );
		var visible = opacity > 0.02f;
		_root.Enabled = visible;
		if ( !visible )
			return;

		var cam = ResolveCameraPosition( Scene );
		var baseRadius = Math.Max( 1000f, TerrainWorldUnits.MetersToEngine( ShellRadiusMeters ) );

		_root.WorldPosition = cam;
		_root.WorldRotation = Rotation.Identity;
		_root.WorldScale = 1f;

		if ( _renderers is null || _dirs is null || _colors is null )
			return;

		// Performance: updating ~56k instance tints every frame can hitch badly.
		// Quantize opacity so we only touch renderer tint when the visible step changes.
		var opacityQuant = MathF.Round( opacity / 0.1f ) * 0.1f;
		opacityQuant = Math.Clamp( opacityQuant, 0f, 1f );
		if ( MathF.Abs( opacityQuant - _lastOpacityQuant ) > 0.001f )
		{
			for ( var i = 0; i < _renderers.Length; i++ )
			{
				var r = _renderers[i];
				if ( r is null || !r.IsValid() )
					continue;

				var go = r.GameObject;
				if ( go is not null && go.IsValid() )
				{
					var rf = _radiusFactors is not null ? _radiusFactors[i] : 1f;
					go.LocalPosition = _dirs[i] * (baseRadius * rf);
					if ( _scales is not null )
						go.LocalScale = _scales[i];
				}

				// Keep star color; only fade opacity (never RGB→black).
				var c = _colors[i];
				r.Tint = new Color( c.r, c.g, c.b, c.a * opacityQuant );
			}
			_lastOpacityQuant = opacityQuant;
		}
	}

	protected override void OnDestroy()
	{
		DestroyRoot();
	}

	void Rebuild()
	{
		DestroyRoot();

		StarMaterial ??= Material.Load( DefaultStarMaterialPath );
		var count = Math.Clamp( StarCount, 200, 80000 );
		var rng = new Random( Seed );
		var baseRadius = Math.Max( 1000f, TerrainWorldUnits.MetersToEngine( ShellRadiusMeters ) );
		var starModel = Model.Load( StarModelPath ); // Cache once (56k loads would hitch hard).

		var tilt = MilkyWayTiltDegrees * (MathF.PI / 180f);
		var galaxyUp = (Vector3.Up * MathF.Cos( tilt ) + Vector3.Forward * MathF.Sin( tilt )).Normal;
		var galaxyEast = Vector3.Cross( galaxyUp, Vector3.Right );
		if ( galaxyEast.LengthSquared < 1e-6f )
			galaxyEast = Vector3.Right;
		else
			galaxyEast = galaxyEast.Normal;
		var galaxyNorth = Vector3.Cross( galaxyEast, galaxyUp ).Normal;

		_root = new GameObject( true, "StarFieldRoot" );
		_root.Parent = GameObject;
		_root.LocalPosition = 0f;
		_root.LocalRotation = Rotation.Identity;
		_root.LocalScale = 1f;

		_renderers = new ModelRenderer[count];
		_dirs = new Vector3[count];
		_radiusFactors = new float[count];
		_scales = new float[count];
		_colors = new Color[count];

		var jitter = Math.Clamp( ShellDepthJitter, 0f, 0.25f );
		var mwFrac = Math.Clamp( MilkyWayFraction, 0f, 1f );
		var mwThick = Math.Clamp( MilkyWayThickness, 0.02f, 0.4f );

		for ( var i = 0; i < count; i++ )
		{
			Vector3 dir;
			if ( rng.NextSingle() < mwFrac )
				dir = SampleMilkyWayDirection( rng, galaxyEast, galaxyNorth, galaxyUp, mwThick );
			else
				dir = RandomUnitVector( rng );

			// Keep most stars above the ground plane.
			if ( dir.z < -0.2f )
				dir = (dir + Vector3.Up * 0.7f).Normal;

			var bright = rng.NextSingle() < BrightStarChance;
			var scale = bright ? BrightStarScale : StarScale;
			// Wide size spread — many tiny, some medium, few large.
			var sizeRoll = rng.NextSingle();
			if ( sizeRoll < 0.55f )
				scale *= 0.35f + rng.NextSingle() * 0.45f;
			else if ( sizeRoll < 0.9f )
				scale *= 0.7f + rng.NextSingle() * 0.7f;
			else
				scale *= 1.1f + rng.NextSingle() * 1.4f;

			var radiusFactor = 1f + (rng.NextSingle() * 2f - 1f) * jitter;
			var color = PickStarColor( rng, bright );

			_dirs[i] = dir;
			_radiusFactors[i] = radiusFactor;
			_scales[i] = scale;
			_colors[i] = color;

			var star = new GameObject( true, $"Star_{i}" );
			star.Parent = _root;
			star.LocalPosition = dir * (baseRadius * radiusFactor);
			star.LocalRotation = Rotation.Identity;
			star.LocalScale = scale;

			var mr = star.Components.Create<ModelRenderer>();
			mr.Model = starModel;
			if ( StarMaterial is not null )
				mr.MaterialOverride = StarMaterial;
			mr.Tint = color;
			mr.RenderType = ModelRenderer.ShadowRenderType.Off;
			_renderers[i] = mr;
		}

		_built = true;
		_builtCount = count;
		_builtShellMeters = ShellRadiusMeters;
		_builtStarScale = StarScale;
		_builtBrightScale = BrightStarScale;
		_root.Enabled = false;
		_lastOpacityQuant = -1f;
	}

	void DestroyRoot()
	{
		if ( _root is not null && _root.IsValid() )
			_root.Destroy();
		_root = null;
		_renderers = null;
		_dirs = null;
		_radiusFactors = null;
		_scales = null;
		_colors = null;
		_built = false;
		_builtCount = -1;
		_builtShellMeters = -1f;
		_builtStarScale = -1f;
		_builtBrightScale = -1f;
	}

	static Color PickStarColor( Random rng, bool bright )
	{
		// Distribution:
		//  - 75% white
		//  - 15% blue
		//  - 10% yellow
		var t = rng.NextSingle();
		Color baseColor;
		if ( t < 0.10f )
			baseColor = new Color( 1.25f, 1.15f, 0.7f, 1f ); // yellow
		else if ( t < 0.25f )
			baseColor = new Color( 0.7f, 0.86f, 1.25f, 1f ); // blue
		else
			baseColor = new Color( 1.12f, 1.12f, 1.12f, 1f ); // white

		// Bright stars are brighter; otherwise keep mostly-matte points.
		var lum = bright ? (0.85f + rng.NextSingle() * 0.5f) : (0.45f + rng.NextSingle() * 0.45f);
		return new Color( baseColor.r * lum, baseColor.g * lum, baseColor.b * lum, 1f );
	}

	static Vector3 SampleMilkyWayDirection( Random rng, Vector3 east, Vector3 north, Vector3 up, float thickness )
	{
		// Dense along the galactic equator, gaussian falloff off-plane.
		var along = rng.NextSingle() * MathF.PI * 2f;
		var off = SampleGaussian( rng ) * thickness;
		off = Math.Clamp( off, -0.55f, 0.55f );
		var inPlane = (east * MathF.Cos( along ) + north * MathF.Sin( along )).Normal;
		return (inPlane + up * off).Normal;
	}

	static float SampleGaussian( Random rng )
	{
		// Box-Muller
		var u1 = Math.Max( 1e-6f, rng.NextSingle() );
		var u2 = rng.NextSingle();
		return MathF.Sqrt( -2f * MathF.Log( u1 ) ) * MathF.Cos( MathF.PI * 2f * u2 );
	}

	static Vector3 ResolveCameraPosition( Scene scene )
	{
		if ( scene is null )
			return Vector3.Zero;

		foreach ( var fly in scene.GetAllComponents<TerrainTestFlyCamera>() )
		{
			if ( fly is not null && fly.IsValid() )
				return fly.WorldPosition;
		}

		var cam = scene.Camera;
		if ( cam is not null && cam.IsValid() )
			return cam.WorldPosition;

		return Vector3.Zero;
	}

	static Vector3 RandomUnitVector( Random rng )
	{
		float x, y, s;
		do
		{
			x = rng.NextSingle() * 2f - 1f;
			y = rng.NextSingle() * 2f - 1f;
			s = x * x + y * y;
		} while ( s >= 1f || s < 1e-6f );

		var z = 1f - 2f * s;
		var f = 2f * MathF.Sqrt( 1f - s );
		return new Vector3( x * f, y * f, z ).Normal;
	}
}
