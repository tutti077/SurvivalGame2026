namespace Survival;

/// <summary>Maps world meters on the terrain X/Y plane to biome preview map UV (0–1).</summary>
public static class TerrainBiomeMapCoordinates
{
	public static Vector2 WorldMetersToNormalized( float worldXMeters, float worldYMeters, TerrainPreviewSettings settings )
	{
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( diameter <= 0f )
			return Vector2.Zero;

		return new Vector2(
			Math.Clamp( (worldXMeters + radius) / diameter, 0f, 1f ),
			Math.Clamp( (worldYMeters + radius) / diameter, 0f, 1f ) );
	}

	public static Rect GetAspectContainRect( Rect outer, float contentWidth, float contentHeight )
	{
		if ( contentWidth <= 0f || contentHeight <= 0f )
			return outer;

		return outer.Contain( new Vector2( contentWidth, contentHeight ) );
	}

	public static Vector2 NormalizedToLocalPoint( Rect mapRect, Vector2 normalized )
		=> new(
			mapRect.Left + (normalized.x * mapRect.Width),
			mapRect.Top + (normalized.y * mapRect.Height) );
}
