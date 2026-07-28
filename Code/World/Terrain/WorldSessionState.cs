namespace Survival;

/// <summary>Handoff from main menu → game scene for world name, seed, and settings source.</summary>
public static class WorldSessionState
{
	public const string GameScenePath = GameSceneIdentity.GameScenePath;

	static bool _hasPendingWorld;
	static string _worldName = "TestWorld";
	static int _worldSeed = 1337;
	static TerrainWorldSettingsSource _settingsSource = TerrainWorldSettingsSource.WorldRecipeFirst;

	public static bool HasPendingWorld => _hasPendingWorld;

	/// <summary>Last world chosen from the menu (survives after <see cref="ApplyTo"/>).</summary>
	public static string ActiveWorldName
		=> string.IsNullOrWhiteSpace( _worldName ) ? "TestWorld" : _worldName;

	public static void BeginNewWorld( string worldName, int worldSeed )
	{
		_worldName = worldName;
		_worldSeed = worldSeed;
		_settingsSource = TerrainWorldSettingsSource.WorldRecipeFirst;
		_hasPendingWorld = true;
		WorldSaveIO.WriteNewWorld( worldName, worldSeed );
	}

	public static void BeginLoadWorld( string worldName )
	{
		var entry = WorldSaveIO.TryReadEntry( worldName );
		_worldName = entry?.WorldName ?? worldName;
		_worldSeed = entry?.WorldSeed ?? 1337;
		_settingsSource = TerrainWorldSettingsSource.WorldRecipeFirst;
		_hasPendingWorld = true;
		WorldSaveIO.TouchLastLoaded( _worldName );
	}

	public static void ApplyTo( TerrainWorldManager manager )
	{
		if ( manager is null || !_hasPendingWorld )
			return;

		manager.WorldName = _worldName;
		manager.WorldSeed = _worldSeed;
		manager.SettingsSource = _settingsSource;
		manager.OverrideWorldScalarsFromComponent = false;
		_hasPendingWorld = false;
	}

	public static void Clear()
		=> _hasPendingWorld = false;
}
