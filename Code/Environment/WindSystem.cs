using Sandbox;

namespace Survival;

/// <summary>
/// Scene-wide wind. The host owns the wind <b>direction</b> and base strength (picked at random
/// on start, then slowly drifting, synced to clients); every client simulates its own gusts on
/// top, because gusts never need to line up between players. Once per frame the result is
/// published to every shader in the scene through <see cref="Scene.RenderAttributes"/> — grass,
/// trees and anything else that sways read the same three values, so there is no per-object cost.
///
/// Shader attributes: <c>WindDirection</c> (unit XY vector), <c>WindStrength</c> (host base, 0–1),
/// <c>WindGust</c> (local gust contribution, 0–1). Shaders add the two strengths.
/// </summary>
[Title( "Wind System" )]
[Icon( "air" )]
public sealed class WindSystem : Component
{
	public static WindSystem Current { get; private set; }

	const string AttrDirection = "WindDirection";
	const string AttrStrength = "WindStrength";
	const string AttrGust = "WindGust";

	[Property, Group( "Direction" ), Title( "Random Heading On Start" ), Description( "Host picks a random heading each session instead of the authored yaw." )]
	public bool RandomHeadingOnStart { get; set; } = true;

	[Property, Group( "Direction" ), Title( "Yaw (degrees)" ), Range( 0f, 360f ), Step( 1f ), Description( "Authored heading, used when Random Heading On Start is off. 0 = +X." )]
	public float YawDegrees { get; set; } = 35f;

	[Property, Group( "Direction" ), Title( "Wander (degrees)" ), Range( 0f, 90f ), Step( 1f ), Description( "How far the host lets the heading drift either side of the session heading." )]
	public float WanderDegrees { get; set; } = 10f;

	[Property, Group( "Direction" ), Title( "Wander Period (s)" ), Range( 5f, 600f ), Step( 5f ), Description( "Roughly how long one full swing of the heading takes." )]
	public float WanderPeriodSeconds { get; set; } = 120f;

	[Property, Group( "Materials" ), Title( "Wind Materials" ), Description( "Materials that receive WindDirection / WindStrength / WindGust directly every frame (Clutter batches do not pick up Scene.RenderAttributes). Add any swaying material here." )]
	public List<Material> WindMaterials { get; set; } = new();

	[Property, Group( "Strength" ), Title( "Base Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Steady breeze the grass always leans into, host owned. 0 = dead calm." )]
	public float BaseStrength01 { get; set; } = 0.25f;

	[Property, Group( "Gusts" ), Title( "Gust Strength (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "Strongest gust added on top of the base. Individual gusts vary between weak and this peak. Simulated locally on every client." )]
	public float GustStrength01 { get; set; } = 0.8f;

	[Property, Group( "Gusts" ), Title( "Gust Period (s)" ), Range( 1f, 60f ), Step( 0.5f ), Description( "Average time between gusts." )]
	public float GustPeriodSeconds { get; set; } = 7f;

	[Property, Group( "Gusts" ), Title( "Gust Variation Period (s)" ), Range( 5f, 300f ), Step( 5f ), Description( "Slow envelope that makes some stretches of gusts weak and others strong." )]
	public float GustVariationPeriodSeconds { get; set; } = 45f;

	/// <summary>Host → clients: the heading everyone bends toward.</summary>
	[Sync( SyncFlags.FromHost )]
	public float CurrentYawDegrees { get; set; }

	/// <summary>Host → clients: steady strength (0–1).</summary>
	[Sync( SyncFlags.FromHost )]
	public float CurrentBaseStrength01 { get; set; }

	/// <summary>World-space unit vector (XY) the wind blows toward. Read this from gameplay code.</summary>
	public Vector3 Direction { get; private set; } = Vector3.Forward;

	/// <summary>Local gust contribution this frame (0–1).</summary>
	public float Gust01 { get; private set; }

	/// <summary>Base + gust, clamped 0–1.</summary>
	public float Strength01 => Math.Clamp( CurrentBaseStrength01 + Gust01, 0f, 1f );

	float _sessionYaw;
	float _gustSeed;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Current = this;
		_gustSeed = Random.Shared.Float( 0f, 1000f );
		if ( IsAuthority )
		{
			_sessionYaw = RandomHeadingOnStart ? Random.Shared.Float( 0f, 360f ) : YawDegrees;
			CurrentYawDegrees = _sessionYaw;
			CurrentBaseStrength01 = Math.Clamp( BaseStrength01, 0f, 1f );
			Log.Info( $"[Wind] heading {_sessionYaw:0}° (blows toward {Rotation.FromYaw( _sessionYaw ).Forward.WithZ( 0f ).Normal}), base {CurrentBaseStrength01:0.00}, gusts up to {GustStrength01:0.00}." );
		}
	}

	protected override void OnDisabled()
	{
		if ( Current == this )
			Current = null;
		base.OnDisabled();
	}

	/// <summary>Single player or listen-server host; clients only read the synced values.</summary>
	bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	protected override void OnUpdate()
	{
		var now = Time.Now;

		if ( IsAuthority )
		{
			// Slow, smooth heading wander: two incommensurate sines so it never visibly loops.
			var period = Math.Max( 5f, WanderPeriodSeconds );
			var w = (2f * MathF.PI) / period;
			var wander = (MathF.Sin( now * w ) * 0.7f) + (MathF.Sin( (now * w * 0.37f) + 1.9f ) * 0.3f);
			CurrentYawDegrees = _sessionYaw + (wander * WanderDegrees);
			CurrentBaseStrength01 = Math.Clamp( BaseStrength01, 0f, 1f );
		}

		Direction = Rotation.FromYaw( CurrentYawDegrees ).Forward.WithZ( 0f ).Normal;
		Gust01 = SampleGust( now + _gustSeed ) * Math.Clamp( GustStrength01, 0f, 1f );

		var attrs = Scene.RenderAttributes;
		attrs.Set( AttrDirection, Direction );
		attrs.Set( AttrStrength, CurrentBaseStrength01 );
		attrs.Set( AttrGust, Gust01 );

		if ( WindMaterials is null )
			return;

		foreach ( var material in WindMaterials )
		{
			if ( material is null || !material.IsValid )
				continue;

			material.Set( AttrDirection, Direction );
			material.Set( AttrStrength, CurrentBaseStrength01 );
			material.Set( AttrGust, Gust01 );
		}
	}

	/// <summary>
	/// 0–1 gust envelope: mostly calm with rounded peaks that come and go every ~Gust Period,
	/// multiplied by a slow envelope so some gusts are weak and some hit the full peak.
	/// </summary>
	float SampleGust( float t )
	{
		var period = Math.Max( 1f, GustPeriodSeconds );
		var w = (2f * MathF.PI) / period;
		var raw = (MathF.Sin( t * w ) * 0.5f)
			+ (MathF.Sin( (t * w * 1.73f) + 0.8f ) * 0.3f)
			+ (MathF.Sin( (t * w * 0.41f) + 2.6f ) * 0.2f);
		var gust = Math.Clamp( (raw + 1f) * 0.5f, 0f, 1f );
		gust *= gust; // bias toward calm, sharper peaks

		var slowW = (2f * MathF.PI) / Math.Max( 5f, GustVariationPeriodSeconds );
		var envelope = 0.35f + (0.65f * (0.5f + (0.5f * MathF.Sin( (t * slowW) + 0.4f ))));
		return gust * envelope;
	}
}
