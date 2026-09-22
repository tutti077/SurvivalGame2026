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

	[Property, Title( "Tilt With Ground" ), Description( "Off = clumps stand straight up (reads best for flat cards). On = lean with the surface normal." )]
	public bool TiltWithGround { get; set; }

	protected override List<ClutterInstance> Generate( BBox bounds, ClutterDefinition clutter, Scene scene )
	{
		var result = new List<ClutterInstance>();
		if ( clutter is null || clutter.IsEmpty )
			return result;

		var density = Math.Max( 0.01f, ClumpsPerSquareMeter );
		var spacingUnits = TerrainWorldUnits.MetersToEngine( 1f / MathF.Sqrt( density ) );
		var cellsX = Math.Max( 1, (int)MathF.Floor( bounds.Size.x / spacingUnits ) );
		var cellsY = Math.Max( 1, (int)MathF.Floor( bounds.Size.y / spacingUnits ) );
		var jitter = Math.Clamp( Jitter01, 0f, 1f ) * spacingUnits * 0.5f;
		var minNormalZ = MathF.Cos( Math.Clamp( MaxSlopeDegrees, 0f, 90f ).DegreeToRadian() );
		var scaleMin = Math.Min( ScaleMin, ScaleMax );
		var scaleMax = Math.Max( ScaleMin, ScaleMax );

		for ( var iy = 0; iy < cellsY; iy++ )
		{
			for ( var ix = 0; ix < cellsX; ix++ )
			{
				var x = bounds.Mins.x + ((ix + 0.5f) * spacingUnits) + Random.Float( -jitter, jitter );
				var y = bounds.Mins.y + ((iy + 0.5f) * spacingUnits) + Random.Float( -jitter, jitter );
				var probe = new Vector3( x, y, bounds.Maxs.z );

				var trace = TraceGround( scene, probe, bounds );
				if ( !trace.Hit || trace.Normal.z < minNormalZ )
					continue;

				var yaw = Random.Float( 0f, 360f );
				var rotation = TiltWithGround
					? GetAlignedRotation( trace.Normal, yaw )
					: Rotation.FromYaw( yaw );

				// Uniform pick with our own Random — every clump model gets an equal share.
				var entry = clutter.Entries[Random.Next( clutter.Entries.Count )];
				if ( entry is null || !entry.HasAsset )
					continue;

				result.Add( new ClutterInstance
				{
					Transform = new Transform( trace.EndPosition, rotation, Random.Float( scaleMin, scaleMax ) ),
					Entry = entry,
				} );
			}
		}

		return result;
	}
}
