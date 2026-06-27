namespace Survival;

/// <summary>
/// World ↔ biome preview map pixel space. Physical terrain uses +X/+Y meters from center.
/// Editor live preview mirrors overlay horizontally (vertical crease) to match map art; raster keeps px mirror for GPU pixmap layout.
/// </summary>
public static class TerrainBiomeMapCoordinates
{
	/// <summary>World meters at the center of preview pixel (px, py).</summary>
	public static void RasterPixelToWorldMeters(
		int px,
		int py,
		int resolution,
		float worldRadiusMeters,
		float worldDiameterMeters,
		out float worldXMeters,
		out float worldYMeters )
	{
		var pxMirror = (resolution - 1) - px;
		worldXMeters = ((pxMirror + 0.5f) / resolution * worldDiameterMeters) - worldRadiusMeters;
		worldYMeters = ((py + 0.5f) / resolution * worldDiameterMeters) - worldRadiusMeters;
	}

	/// <summary>World meters → [0,1] map UV (west/up = 0, east/north = 1).</summary>
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

	/// <summary>Live inspector preview: mirror horizontally so overlay matches displayed map art.</summary>
	public static Vector2 WorldMetersToPreviewNormalized( float worldXMeters, float worldYMeters, TerrainPreviewSettings settings )
	{
		var uv = WorldMetersToNormalized( worldXMeters, worldYMeters, settings );
		return new Vector2( 1f - uv.x, uv.y );
	}

	public static Rect GetAspectContainRect( Rect outer, float contentWidth, float contentHeight )
	{
		if ( contentWidth <= 0f || contentHeight <= 0f )
			return outer;

		return outer.Contain( new Vector2( contentWidth, contentHeight ) );
	}

	/// <summary>Flat world forward in map-plane axes (before preview mirror).</summary>
	public static Vector2 WorldForwardToMapDirection( Vector3 worldForward )
	{
		var flat = worldForward.WithZ( 0f );
		if ( flat.LengthSquared < 1e-8f )
			return Vector2.Zero;

		flat = flat.Normal;
		return new Vector2( flat.x, flat.y );
	}

	/// <summary>Look line for live preview after the same horizontal mirror as the crosshair.</summary>
	public static Vector2 WorldForwardToPreviewMapDirection( Vector3 worldForward )
	{
		var dir = WorldForwardToMapDirection( worldForward );
		return new Vector2( -dir.x, dir.y );
	}

	public static Vector2 NormalizedToLocalPoint( Rect mapRect, Vector2 normalized )
		=> new(
			mapRect.Left + (normalized.x * mapRect.Width),
			mapRect.Top + (normalized.y * mapRect.Height) );
}
