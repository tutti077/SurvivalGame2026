using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads equipment profiles from <c>data/equipment_profiles.json</c>.</summary>
public static class EquipmentCatalog
{
	const string EquipmentProfilesFilePath = "data/equipment_profiles.json";

	static readonly List<EquipmentProfileData> Profiles = new();
	static readonly Dictionary<string, EquipmentProfileData> ByResourceId =
		new( StringComparer.OrdinalIgnoreCase );

	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<EquipmentProfileData> All
	{
		get
		{
			EnsureLoaded();
			return Profiles;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		EnsureLoaded();
	}

	public static void EnsureLoaded()
	{
		var jsonHash = TryReadJsonHash();
		if ( _loaded && jsonHash == _loadedJsonHash )
			return;

		_loaded = true;
		_loadedJsonHash = jsonHash;
		Profiles.Clear();
		ByResourceId.Clear();

		if ( TryLoadFromFile() )
			return;

		Profiles.AddRange( CreateFallbackProfiles() );
		RebuildLookup();
		Log.Warning( "[EquipmentCatalog] Using built-in fallback equipment profiles." );
	}

	public static bool TryGet( string resourceId, out EquipmentProfileData profile )
	{
		EnsureLoaded();
		profile = null;
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		resourceId = ResourceCatalog.NormalizeResourceId( resourceId );
		return ByResourceId.TryGetValue( resourceId, out profile );
	}

	public static EquippedItemActions GetActions( string resourceId )
	{
		if ( !TryGet( resourceId, out var profile ) || profile.Actions is null )
			return EquippedItemActions.None;

		var actions = EquippedItemActions.None;
		for ( var i = 0; i < profile.Actions.Count; i++ )
		{
			if ( TryParseAction( profile.Actions[i], out var flag ) )
				actions |= flag;
		}

		return actions;
	}

	public static bool HasAction( string resourceId, EquippedItemActions action )
	{
		if ( action == EquippedItemActions.None )
			return false;

		return ( GetActions( resourceId ) & action ) == action;
	}

	public static bool TryParseSlot( string value, out EquipmentSlot slot )
	{
		slot = default;
		if ( string.IsNullOrWhiteSpace( value ) )
			return false;

		return Enum.TryParse( value, ignoreCase: true, out slot );
	}

	public static EquipmentSlot GetPrimarySlot( EquipmentProfileData profile )
	{
		if ( profile is null )
			return EquipmentSlot.MainHand;

		if ( TryParseSlot( profile.Slot, out var slot ) )
			return slot;

		return EquipmentSlot.MainHand;
	}

	public static bool IsSlotAllowed( EquipmentProfileData profile, EquipmentSlot slot )
	{
		if ( profile is null )
			return false;

		if ( profile.AllowedSlots is null || profile.AllowedSlots.Count == 0 )
			return GetPrimarySlot( profile ) == slot;

		for ( var i = 0; i < profile.AllowedSlots.Count; i++ )
		{
			if ( TryParseSlot( profile.AllowedSlots[i], out var allowed ) && allowed == slot )
				return true;
		}

		return false;
	}

	static bool TryParseAction( string value, out EquippedItemActions action )
	{
		action = EquippedItemActions.None;
		if ( string.IsNullOrWhiteSpace( value ) )
			return false;

		return Enum.TryParse( value, ignoreCase: true, out action );
	}

	static int TryReadJsonHash()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( EquipmentProfilesFilePath ) )
				return 0;

			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( EquipmentProfilesFilePath ) );
		}
		catch
		{
			return 0;
		}
	}

	static bool TryLoadFromFile()
	{
		try
		{
			if ( !FileSystem.Mounted.FileExists( EquipmentProfilesFilePath ) )
				return false;

			var json = FileSystem.Mounted.ReadAllText( EquipmentProfilesFilePath );
			var file = JsonSerializer.Deserialize<EquipmentProfilesFile>( json, JsonOptions );
			if ( file?.Profiles is null || file.Profiles.Count == 0 )
				return false;

			for ( var i = 0; i < file.Profiles.Count; i++ )
			{
				var entry = file.Profiles[i];
				if ( entry is null || string.IsNullOrWhiteSpace( entry.ResourceId ) )
					continue;

				Profiles.Add( entry );
			}

			if ( Profiles.Count == 0 )
				return false;

			RebuildLookup();
			return true;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[EquipmentCatalog] Failed to load {EquipmentProfilesFilePath}: {ex.Message}" );
			return false;
		}
	}

	static void RebuildLookup()
	{
		ByResourceId.Clear();
		for ( var i = 0; i < Profiles.Count; i++ )
		{
			var entry = Profiles[i];
			if ( entry is null || string.IsNullOrWhiteSpace( entry.ResourceId ) )
				continue;

			ByResourceId[ResourceCatalog.NormalizeResourceId( entry.ResourceId )] = entry;
		}
	}

	static IEnumerable<EquipmentProfileData> CreateFallbackProfiles()
	{
		yield return new EquipmentProfileData
		{
			ResourceId = "sword",
			DisplayName = "Sword",
			Slot = "mainHand",
			AllowedSlots = { "mainHand" },
			Actions = { "PrimaryMelee", "Block" },
			HotbarEquipable = true,
		};

		yield return new EquipmentProfileData
		{
			ResourceId = "building_hammer",
			DisplayName = "Building Hammer",
			Slot = "mainHand",
			AllowedSlots = { "mainHand" },
			Actions = { "BuildHammer" },
			HotbarEquipable = true,
			ToolPrefab = "prefabs/tools/build_hammer_tool.prefab",
		};

		yield return new EquipmentProfileData
		{
			ResourceId = "basic_hook",
			DisplayName = "Basic Hook",
			Slot = "grapple",
			AllowedSlots = { "grapple" },
			Actions = { "Grapple" },
			HotbarEquipable = false,
			GrappleMaxRangeMeters = 30f,
			GrappleRetractMetersPerSecond = 2.5f,
			GrappleDetractMetersPerSecond = 4f,
			GrappleAttachStaminaCost = 8f,
			GrappleAirborneStaminaPerSecond = 1.5f,
		};
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class EquipmentProfilesFile
	{
		public List<EquipmentProfileData> Profiles { get; set; } = new();
	}
}
