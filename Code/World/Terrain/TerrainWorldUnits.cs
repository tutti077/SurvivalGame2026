namespace Survival;

/// <summary>
/// Terrain design values are in meters; s&amp;box world space uses <see cref="BuildModuleDimensions.UnitsPerMeter"/>.
/// Noise/biome sampling stays in meters — convert only when writing meshes, transforms, or colliders.
/// </summary>
public static class TerrainWorldUnits
{
	public static float UnitsPerMeter => BuildModuleDimensions.UnitsPerMeter;

	public static float MetersToEngine( float meters ) => meters * UnitsPerMeter;

	public static float EngineToMeters( float units ) => units / UnitsPerMeter;

	public static Vector3 MetersToEngine( Vector3 meters )
		=> new( MetersToEngine( meters.x ), MetersToEngine( meters.y ), MetersToEngine( meters.z ) );

	public static Vector2 MetersToEngine( Vector2 meters )
		=> new( MetersToEngine( meters.x ), MetersToEngine( meters.y ) );

	public static Vector3 EngineToMeters( Vector3 units )
		=> new( EngineToMeters( units.x ), EngineToMeters( units.y ), EngineToMeters( units.z ) );
}
