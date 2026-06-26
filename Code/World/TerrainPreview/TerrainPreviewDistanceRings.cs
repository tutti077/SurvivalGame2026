namespace Survival;

/// <summary>Thin meter rings centered on world spawn for preview distance readout.</summary>
public static class TerrainPreviewDistanceRings
{
	const float DefaultIntervalMeters = 1000f;

	public static void Stamp(
		Color[] colors,
		int res,
		float worldRadiusMeters,
		float worldDiameterMeters,
		bool[] insideWorld,
		float intervalMeters = DefaultIntervalMeters )
	{
		if ( colors == null || res <= 0 || intervalMeters <= 1f )
			return;

		var metersPerPixel = worldDiameterMeters / res;
		var halfWidth = metersPerPixel * 0.55f;
		var ring = Color.Black;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( insideWorld != null && !insideWorld[idx] )
					continue;

				var wx = (px + 0.5f) / res * worldDiameterMeters - worldRadiusMeters;
				var wy = (py + 0.5f) / res * worldDiameterMeters - worldRadiusMeters;
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );

				var ringIndex = MathF.Round( dist / intervalMeters );
				if ( ringIndex <= 0f )
					continue;

				var ringDist = ringIndex * intervalMeters;
				if ( ringDist > worldRadiusMeters + halfWidth )
					continue;

				if ( MathF.Abs( dist - ringDist ) <= halfWidth )
					colors[idx] = ring;
			}
		}
	}
}
