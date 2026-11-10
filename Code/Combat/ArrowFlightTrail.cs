using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The arrow's flight and landing, as every peer sees it (Mark 2026-11-06: "the bow flying should just be like a semi
/// transparent white line to show air movement. When it hits something … a similar effect to when the sword is charged
/// up, little white star thing pops"). One local object per flying <see cref="ArrowProjectile"/>: a faint white streak
/// that trails the arrow as a short tail, fading along its length, and where the arrow lands a small white star pops open and fades,
/// drawn like the sword's heavy-ready glint star (<see cref="MeleeSwingVfx"/>). Both keep about the same size on screen
/// at any range. Lives apart from the arrow so it outlasts it; presentation only.
/// </summary>
[Title( "Arrow Flight Trail" )]
public sealed class ArrowFlightTrail : Component
{
	/// <summary>
	/// How long a stretch of the streak stays behind the arrow before it has faded out: a short comet tail that fades
	/// along its length as the arrow flies (Mark 2026-11-06: "should sorta fade out as it flies") — about 14 m at full
	/// draw, 5 m at the weakest — and after the landing it drains into the impact point.
	/// </summary>
	public const float StreakSeconds = 0.15f;
	/// <summary>The landing star, open to faded.</summary>
	public const float StarSeconds = 0.4f;
	/// <summary>A new streak point every this many metres of flight.</summary>
	const float SampleSpacingMeters = 0.3f;

	ArrowTrailVfx _vfx;
	double _endedAt = -1d;

	/// <summary>Local object holding the trail for one arrow.</summary>
	public static ArrowFlightTrail Create( Scene scene )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		var go = new GameObject( true, "arrow_trail" );
		go.NetworkMode = NetworkMode.Never;
		go.Parent = scene;
		go.Tags.Add( "ignore" );
		return go.Components.Create<ArrowFlightTrail>();
	}

	/// <summary>The local player shot it (their crosshair gets the hit tick).</summary>
	public bool IsOwn { get; set; }

	public bool Ended => _endedAt >= 0d;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		_vfx ??= new ArrowTrailVfx( Scene.SceneWorld );
	}

	protected override void OnDestroy()
	{
		_vfx?.Delete();
		_vfx = null;
		base.OnDestroy();
	}

	/// <summary>The arrow is here now (world).</summary>
	public void Push( Vector3 position )
	{
		if ( Ended || _vfx is null )
			return;

		_vfx.Push( position, Time.Now, SampleSpacingMeters * TerrainWorldUnits.MetersToEngine( 1f ) );
	}

	/// <summary>The flight is over. <paramref name="impact"/> null = it never landed (lost in flight): no star.</summary>
	public void End( Vector3? impact )
	{
		if ( Ended || _vfx is null )
			return;

		if ( impact is { } at )
		{
			_vfx.Push( at, Time.Now, 0f );
			_vfx.SetImpact( at, Time.Now );
		}

		_endedAt = Time.NowDouble;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _vfx is null )
			return;

		var cam = Scene.Camera;
		_vfx.Tick( Time.Now, cam.IsValid() ? cam.WorldPosition : Vector3.Zero, cam.IsValid() ? cam.FieldOfView : 80f );

		if ( Ended && Time.NowDouble - _endedAt > MathF.Max( StreakSeconds, StarSeconds ) )
			GameObject.Destroy();
	}
}

/// <summary>The trail's geometry (<see cref="UnlitVfxGeometry"/> cross-lines, world space).</summary>
sealed class ArrowTrailVfx : SceneCustomObject
{
	const int MaxPoints = 128;
	/// <summary>Streak alpha right behind the arrow; it falls to 0 over <see cref="ArrowFlightTrail.StreakSeconds"/>.</summary>
	const float StreakAlpha = 0.4f;
	/// <summary>Streak width on screen (px at 1080p) and the landing star's full ray length (px), whatever the range.</summary>
	const float StreakPixels = 1.6f;
	const float StarPixels = 26f;

	struct Point
	{
		public Vector3 Position;
		public float Time;
		public float Width;
	}

	readonly List<Point> _points = new( 64 );
	readonly List<Vertex> _verts = new( 2048 );
	readonly Material _material;

	Vector3 _impact;
	bool _hasImpact;
	float _impactTime;
	float _now;
	float _starSize;

	public ArrowTrailVfx( SceneWorld world ) : base( world )
	{
		_material = UnlitVfxGeometry.CreateMaterial();
		Flags.CastShadows = false;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		RenderingEnabled = false;
	}

	public void Push( Vector3 position, float now, float minSpacing )
	{
		if ( _points.Count > 0 && (position - _points[^1].Position).Length < minSpacing )
			return;

		if ( _points.Count >= MaxPoints )
			_points.RemoveAt( 0 );

		_points.Add( new Point { Position = position, Time = now, Width = 1f } );
	}

	public void SetImpact( Vector3 at, float now )
	{
		_impact = at;
		_hasImpact = true;
		_impactTime = now;
	}

	/// <summary>Main thread: drop faded streak points, sizes from the camera distance, bounds.</summary>
	public void Tick( float now, Vector3 camera, float fov )
	{
		_now = now;

		// Keep one faded point past the cutoff so the streak's tail end fades instead of jumping.
		var cutoff = now - ArrowFlightTrail.StreakSeconds;
		var drop = 0;
		while ( drop < _points.Count - 1 && _points[drop + 1].Time < cutoff )
			drop++;
		if ( drop > 0 )
			_points.RemoveRange( 0, drop );

		var perUnit = 2f * MathF.Tan( Math.Clamp( fov, 20f, 120f ) * 0.5f * (MathF.PI / 180f) ) / 1080f;
		var box = default( BBox );
		var any = false;
		for ( var i = 0; i < _points.Count; i++ )
		{
			var p = _points[i];
			p.Width = MathF.Max( 0.8f, (p.Position - camera).Length * perUnit * StreakPixels );
			_points[i] = p;
			UnlitVfxGeometry.Grow( ref box, ref any, p.Position );
		}

		var starLive = _hasImpact && now - _impactTime <= ArrowFlightTrail.StarSeconds;
		if ( starLive )
		{
			_starSize = MathF.Max( 8f, (_impact - camera).Length * perUnit * StarPixels );
			UnlitVfxGeometry.Grow( ref box, ref any, _impact );
		}

		var streakLive = _points.Count >= 2 && now - _points[^1].Time < ArrowFlightTrail.StreakSeconds;
		Bounds = box.Grow( 16f + _starSize );
		RenderingEnabled = any && (streakLive || starLive);
	}

	public override void RenderSceneObject()
	{
		_verts.Clear();
		BuildStreak();
		BuildStar();
		if ( _verts.Count > 0 )
			Graphics.Draw( _verts, _verts.Count, _material, Attributes, Graphics.PrimitiveType.Triangles );
	}

	/// <summary>Semi-transparent white line along the flight, strongest at the arrow, gone after the streak time.</summary>
	void BuildStreak()
	{
		for ( var i = 1; i < _points.Count; i++ )
		{
			var a = _points[i - 1];
			var b = _points[i];
			var ca = Color.White.WithAlpha( StreakAlpha * Fade( a.Time ) );
			var cb = Color.White.WithAlpha( StreakAlpha * Fade( b.Time ) );
			if ( ca.a <= 0.002f && cb.a <= 0.002f )
				continue;
			UnlitVfxGeometry.AddCrossLine( _verts, a.Position, b.Position, (a.Width + b.Width) * 0.5f, ca, cb );
		}
	}

	float Fade( float time ) => Math.Clamp( 1f - (_now - time) / ArrowFlightTrail.StreakSeconds, 0f, 1f );

	/// <summary>
	/// The landing pop: the sword glint's star (<see cref="MeleeSwingVfx"/> BuildFlash) — six rays open out from the
	/// point, easing to full length, and fade as they go.
	/// </summary>
	void BuildStar()
	{
		if ( !_hasImpact )
			return;

		var st = (_now - _impactTime) / ArrowFlightTrail.StarSeconds;
		if ( st < 0f || st > 1f )
			return;

		var size = _starSize * (0.35f + 0.65f * MathF.Sin( st * MathF.PI * 0.5f ));
		var alpha = 1f - st;
		var width = MathF.Max( 1f, _starSize * 0.09f );
		var c0 = Color.White.WithAlpha( alpha );
		var c1 = Color.White.WithAlpha( 0f );
		foreach ( var ray in Rays )
			UnlitVfxGeometry.AddCrossLine( _verts, _impact, _impact + ray * size, width, c0, c1 );
	}

	static readonly Vector3[] Rays =
	{
		new Vector3( 1f, 0f, 0f ), new Vector3( -1f, 0f, 0f ),
		new Vector3( 0f, 1f, 0f ), new Vector3( 0f, -1f, 0f ),
		new Vector3( 0f, 0f, 1f ), new Vector3( 0f, 0f, -1f ),
	};
}
