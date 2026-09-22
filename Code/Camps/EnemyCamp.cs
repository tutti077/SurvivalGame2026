using Sandbox;

namespace Survival;

/// <summary>
/// Runtime root of one stamped enemy camp: where it is and the two leash rings its guards share.
/// Created by <see cref="EnemyCampSpawner"/>. Draws the rings every frame while
/// <c>campRings</c> is on so the leash stays visible for as long as the camp exists.
/// </summary>
[Title( "Enemy Camp" )]
public sealed class EnemyCamp : Component
{
	/// <summary>Console-toggled debug rings (`campRings true|false`). On while camps are being built out.</summary>
	public static bool DebugRings { get; private set; } = true;

	[Property] public string CampId { get; set; } = string.Empty;
	[Property, Title( "Wander distance (units)" )] public float WanderDistance { get; set; }
	[Property, Title( "Max travel distance (units)" )] public float MaxTravelDistance { get; set; }

	const int RingSegments = 64;
	/// <summary>Ring line height above the sampled ground.</summary>
	const float RingLift = 12f;
	/// <summary>Vertical fence posts every Nth ring point, so the boundary reads as a wall, not a floor line.</summary>
	const int PostEvery = 4;
	const float PostHeight = 320f;
	/// <summary>Ground probe reach above / below the camp origin — covers a hillside camp.</summary>
	const float ProbeUp = 2048f;
	const float ProbeDown = 4096f;

	// The leash is flat (XY only) — these are the ring points dropped onto the terrain so a ring on a
	// slope hugs the ground instead of cutting into it. Terrain does not move, so sampled once.
	Vector3[] _wanderRing;
	Vector3[] _travelRing;

	[ConCmd( "campRings" )]
	public static void ConCmdRings( string enabled = "" )
	{
		DebugRings = string.IsNullOrWhiteSpace( enabled )
			? !DebugRings
			: enabled.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
		Log.Info( $"[EnemyCamp] Leash rings {(DebugRings ? "on" : "off")}." );
	}

	/// <summary>Re-sample the ring points (call after changing the distances at runtime).</summary>
	public void RebuildRings()
	{
		_wanderRing = BuildGroundRing( WanderDistance );
		_travelRing = BuildGroundRing( MaxTravelDistance );
	}

	protected override void OnUpdate()
	{
		if ( !DebugRings )
			return;

		if ( _wanderRing is null || _travelRing is null )
			RebuildRings();

		DrawRing( _wanderRing, Color.Green );
		DrawRing( _travelRing, Color.Red );
	}

	Vector3[] BuildGroundRing( float radius )
	{
		if ( radius <= 0f )
			return System.Array.Empty<Vector3>();

		var center = WorldPosition;
		var points = new Vector3[RingSegments];
		for ( var i = 0; i < RingSegments; i++ )
		{
			var angle = i * (360f / RingSegments);
			var flat = center + Rotation.FromYaw( angle ) * new Vector3( radius, 0f, 0f );
			points[i] = SampleGround( flat ) + Vector3.Up * RingLift;
		}

		return points;
	}

	/// <summary>Ground under a flat ring point; falls back to the camp origin height when nothing is there.</summary>
	Vector3 SampleGround( Vector3 flat )
	{
		var from = flat.WithZ( WorldPosition.z + ProbeUp );
		var to = flat.WithZ( WorldPosition.z - ProbeDown );
		var tr = Scene.Trace.Ray( from, to )
			.UsePhysicsWorld()
			.WithoutTags( "player", "enemy", "buildpreview" )
			.Run();

		if ( !tr.Hit )
		{
			tr = Scene.Trace.Ray( from, to )
				.WithoutTags( "player", "enemy", "buildpreview" )
				.Run();
		}

		return tr.Hit ? tr.HitPosition : flat.WithZ( WorldPosition.z );
	}

	void DrawRing( Vector3[] points, Color color )
	{
		if ( points is null || points.Length < 2 )
			return;

		var postColor = color.WithAlpha( 0.45f );
		for ( var i = 0; i < points.Length; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % points.Length];
			DebugOverlay.Line( a, b, color, 0f );

			if ( i % PostEvery == 0 )
				DebugOverlay.Line( a, a + Vector3.Up * PostHeight, postColor, 0f );
		}
	}
}
