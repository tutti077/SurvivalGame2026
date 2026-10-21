namespace Survival;

/// <summary>
/// Draws the river network onto a biome / water map raster. Rivers are 8–28 m wide and a 40 km map is
/// 40–80 m per pixel, so per-pixel water sampling only ever hit them by luck and the maps showed no rivers
/// at all (Mark 2026-10-08). Every segment is stamped as a line at least ~1.5 px wide, in the water colour,
/// in the same mirrored pixel frame as <see cref="TerrainBiomeMapCoordinates.RasterPixelToWorldMeters"/>.
/// </summary>
public static class TerrainRiverMapOverlay
{
	const float MinLinePixels = 1.5f;

	public static void Stamp( TerrainPreviewSettings settings, Color[] colors, int resolution, bool[] insideWorld )
	{
		if ( settings is null || !settings.EnableRivers || colors is null || resolution <= 1 )
			return;

		TerrainPreviewLandDiskFields.EnsureReady( settings );
		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null || net.Segments.Length == 0 )
			return;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( diameter <= 0f )
			return;

		var pixelsPerMeter = resolution / diameter;
		var color = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );

		for ( var i = 0; i < net.Segments.Length; i++ )
		{
			ref var s = ref net.Segments[i];
			WorldToPixel( s.Ax, s.Ay, radius, diameter, resolution, out var ax, out var ay );
			WorldToPixel( s.Bx, s.By, radius, diameter, resolution, out var bx, out var by );
			var widthA = Math.Max( MinLinePixels, s.HalfA * 2f * pixelsPerMeter );
			var widthB = Math.Max( MinLinePixels, s.HalfB * 2f * pixelsPerMeter );

			var dx = bx - ax;
			var dy = by - ay;
			var length = MathF.Sqrt( (dx * dx) + (dy * dy) );
			var steps = Math.Max( 1, (int)MathF.Ceiling( length / 0.5f ) );
			for ( var k = 0; k <= steps; k++ )
			{
				var t = steps == 0 ? 0f : k / (float)steps;
				var cx = ax + (dx * t);
				var cy = ay + (dy * t);
				var halfWidth = (widthA + ((widthB - widthA) * t)) * 0.5f;
				StampDisc( colors, resolution, insideWorld, cx, cy, halfWidth, color );
			}
		}
	}

	static void WorldToPixel( float wx, float wy, float radius, float diameter, int resolution, out float px, out float py )
	{
		// Inverse of RasterPixelToWorldMeters: X is mirrored in the raster.
		var pxMirror = ((wx + radius) / diameter * resolution) - 0.5f;
		px = (resolution - 1) - pxMirror;
		py = ((wy + radius) / diameter * resolution) - 0.5f;
	}

	static void StampDisc( Color[] colors, int res, bool[] insideWorld, float cx, float cy, float halfWidth, Color color )
	{
		var r = Math.Max( 0.6f, halfWidth );
		var minX = Math.Max( 0, (int)MathF.Floor( cx - r ) );
		var maxX = Math.Min( res - 1, (int)MathF.Ceiling( cx + r ) );
		var minY = Math.Max( 0, (int)MathF.Floor( cy - r ) );
		var maxY = Math.Min( res - 1, (int)MathF.Ceiling( cy + r ) );
		var r2 = r * r;
		for ( var y = minY; y <= maxY; y++ )
		{
			for ( var x = minX; x <= maxX; x++ )
			{
				var ddx = x - cx;
				var ddy = y - cy;
				if ( (ddx * ddx) + (ddy * ddy) > r2 )
					continue;
				var idx = (y * res) + x;
				if ( insideWorld is not null && idx < insideWorld.Length && !insideWorld[idx] )
					continue;
				colors[idx] = color;
			}
		}
	}
}
