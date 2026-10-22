using Sandbox;
using Sandbox.Clutter;

namespace Survival;

/// <summary>
/// Clutter scatterer for grass clumps, tuned in project meters (40 u/m) instead of the
/// engine's <see cref="SimpleScatterer"/> units. Jittered grid → one ground trace per cell →
/// random yaw and scale. Runs once per patch / tile, never per frame.
/// </summary>
[Title( "Grass Scatterer" )]
public sealed class GrassScatterer : Scatterer
{
	[Property, Title( "Clumps Per m²" ), Range( 0.1f, 12f ), Step( 0.1f )]
	public float ClumpsPerSquareMeter { get; set; } = 3f;

	[Property, Title( "Scale Min" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float ScaleMin { get; set; } = 0.8f;

	[Property, Title( "Scale Max" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float ScaleMax { get; set; } = 1.25f;

	[Property, Title( "Jitter (0–1)" ), Range( 0f, 1f ), Step( 0.05f ), Description( "How far a clump may leave its grid cell centre. 0 = visible grid, 1 = fully random." )]
	public float Jitter01 { get; set; } = 0.9f;

	[Property, Title( "Max Slope (degrees)" ), Range( 0f, 90f ), Step( 1f ), Description( "Ground steeper than this gets no grass." )]
	public float MaxSlopeDegrees { get; set; } = 50f;

	[Property, Title( "Ground Tag" ), Description( "Only colliders carrying this tag count as ground. Empty = anything solid. Ignored when a height field is set." )]
	public string GroundTag { get; set; } = "";

	Func<Vector3, bool> _worldFilter;
	Func<float, float, (float Z, Vector3 Normal)?> _heightField;

	/// <summary>Runtime-only: skip probes this returns false for (the terrain's biome mask). Not serialized.</summary>
	public void SetWorldFilter( Func<Vector3, bool> filter ) => _worldFilter = filter;

	/// <summary>
	/// Runtime-only: ground from a height grid (world xy → ground z + normal) instead of a physics trace — the
	/// streamed terrain plants on its own mesh heights, so no collider is needed and nothing else can be hit.
	/// </summary>
	public void SetHeightField( Func<float, float, (float Z, Vector3 Normal)?> field ) => _heightField = field;

	static bool _loggedFirstGenerate;

	protected override List<ClutterInstance> Generate( BBox bounds, ClutterDefinition clutter, Scene scene )
	{
		var result = new List<ClutterInstance>();
		if ( clutter is null || clutter.IsEmpty )
			return result;

		int probes = 0, filtered = 0, misses = 0, steep = 0;
		string error = null;

		var density = Math.Max( 0.01f, ClumpsPerSquareMeter );
		var wantedSpacing = TerrainWorldUnits.MetersToEngine( 1f / MathF.Sqrt( density ) );
		// Round UP and stretch the grid to the bounds: the engine hands this method one 512-unit tile at a
		// time, and flooring left an unplanted strip at the end of every tile — a visible grid of bare lines.
		var cellsX = Math.Max( 1, (int)MathF.Ceiling( bounds.Size.x / wantedSpacing ) );
		var cellsY = Math.Max( 1, (int)MathF.Ceiling( bounds.Size.y / wantedSpacing ) );
		var spacingX = bounds.Size.x / cellsX;
		var spacingY = bounds.Size.y / cellsY;
		var jitterX = Math.Clamp( Jitter01, 0f, 1f ) * spacingX * 0.5f;
		var jitterY = Math.Clamp( Jitter01, 0f, 1f ) * spacingY * 0.5f;
		var minNormalZ = MathF.Cos( Math.Clamp( MaxSlopeDegrees, 0f, 90f ).DegreeToRadian() );
		var scaleMin = Math.Min( ScaleMin, ScaleMax );
		var scaleMax = Math.Max( ScaleMin, ScaleMax );

		for ( var iy = 0; iy < cellsY; iy++ )
		{
			for ( var ix = 0; ix < cellsX; ix++ )
			{
				var x = bounds.Mins.x + ((ix + 0.5f) * spacingX) + Random.Float( -jitterX, jitterX );
				var y = bounds.Mins.y + ((iy + 0.5f) * spacingY) + Random.Float( -jitterY, jitterY );
				var probe = new Vector3( x, y, bounds.Maxs.z );
				probes++;
				if ( _worldFilter is not null && !_worldFilter( probe ) )
				{
					filtered++;
					continue;
				}

				Vector3 groundPos;
				Vector3 groundNormal;
				if ( _heightField is not null )
				{
					var ground = _heightField( x, y );
					if ( ground is null )
					{
						misses++;
						continue;
					}
					groundPos = new Vector3( x, y, ground.Value.Z );
					groundNormal = ground.Value.Normal;
				}
				else
				{
					SceneTraceResult trace;
					try
					{
						trace = string.IsNullOrEmpty( GroundTag )
							? TraceGround( scene, probe, bounds )
							: scene.Trace.Ray( probe, probe.WithZ( bounds.Mins.z ) ).WithTag( GroundTag ).Run();
					}
					catch ( Exception e )
					{
						error ??= e.GetType().Name + ": " + e.Message;
						break;
					}

					if ( !trace.Hit )
					{
						misses++;
						continue;
					}
					groundPos = trace.EndPosition;
					groundNormal = trace.Normal;
				}

				if ( groundNormal.z < minNormalZ )
				{
					steep++;
					continue;
				}

				var yaw = Random.Float( 0f, 360f );
				// The instance is tilted onto the ground normal only to carry that normal to grass_blade.shader, which
				// stands the blades back up and lights them with it — the same normal the terrain under them is lit with
				// (Mark 2026-10-21: the grass must take the terrain's lighting so it does not stand out).
				var rotation = GetAlignedRotation( groundNormal, yaw );

				// Uniform pick with our own Random — every clump model gets an equal share.
				var entry = clutter.Entries[Random.Next( clutter.Entries.Count )];
				if ( entry is null || !entry.HasAsset )
					continue;

				result.Add( new ClutterInstance
				{
					Transform = new Transform( groundPos, rotation, Random.Float( scaleMin, scaleMax ) ),
					Entry = entry,
				} );
			}
		}

		if ( !_loggedFirstGenerate )
		{
			_loggedFirstGenerate = true;
			Log.Info( $"[GrassScatterer] first generate: bounds {bounds.Mins} … {bounds.Maxs}, {cellsX}×{cellsY} cells, {probes} probes, {filtered} filtered, {misses} missed, {steep} too steep, {result.Count} clumps, {(_heightField is null ? "traced" : "height field")}, main thread {ThreadSafe.IsMainThread}{(error is null ? "" : ", ERROR " + error)}" );
		}

		return result;
	}
}
