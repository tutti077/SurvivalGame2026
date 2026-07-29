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
		ReloadFromDisk();
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		ReloadFromDisk();
	}

	static void ReloadFromDisk()
	{
		var jsonHash = TryReadJsonHash();
		_loaded = true;
		_loadedJsonHash = jsonHash;
		Profiles.Clear();
		ByResourceId.Clear();

		if ( TryLoadFromFile() )
			return;

		Profiles.AddRange( CreateFallbackProfiles() );
		RebuildLookup();
		EnsureRequiredMainHandProfiles();
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

		value = value.Trim();
		if ( Enum.TryParse( value, ignoreCase: true, out slot ) )
			return true;

		// equipment_profiles.json uses camelCase ("mainHand") — keep an explicit map so tools
		// are never dropped as "not a MainHand item" if Enum.TryParse quirks show up.
		switch ( value.Replace( "_", string.Empty ).ToLowerInvariant() )
		{
			case "mainhand":
			case "main":
				slot = EquipmentSlot.MainHand;
				return true;
			case "offhand":
			case "off":
				slot = EquipmentSlot.OffHand;
				return true;
			case "head":
				slot = EquipmentSlot.Head;
				return true;
			case "chest":
				slot = EquipmentSlot.Chest;
				return true;
			case "arms":
				slot = EquipmentSlot.Arms;
				return true;
			case "hands":
				slot = EquipmentSlot.Hands;
				return true;
			case "legs":
				slot = EquipmentSlot.Legs;
				return true;
			case "feet":
				slot = EquipmentSlot.Feet;
				return true;
			case "backpack":
			case "pack":
				slot = EquipmentSlot.Backpack;
				return true;
			case "grapple":
			case "hook":
				slot = EquipmentSlot.Grapple;
				return true;
			case "wingsuit":
			case "wing":
				slot = EquipmentSlot.Wingsuit;
				return true;
			default:
				return false;
		}
	}

	/// <summary>Weapons/tools that live on the hotbar and mirror into MainHand — not paperdoll storage.</summary>
	public static bool IsHotbarMainHandItem( EquipmentProfileData profile ) =>
		profile is not null && IsSlotAllowed( profile, EquipmentSlot.MainHand );

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
			EnsureRequiredMainHandProfiles();
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

			entry.ResourceId = ResourceCatalog.NormalizeResourceId( entry.ResourceId );
			ByResourceId[entry.ResourceId] = entry;

			// MainHand tools must stay selectable from the hotbar even if JSON omitted the flag.
			if ( IsSlotAllowed( entry, EquipmentSlot.MainHand ) )
				entry.HotbarEquipable = true;
			else if ( GetPrimarySlot( entry ) == EquipmentSlot.Grapple )
				entry.HotbarEquipable = false;
		}
	}

	/// <summary>Upsert built-in MainHand tools so craft ids like build_hammer always resolve.</summary>
	static void EnsureRequiredMainHandProfiles()
	{
		foreach ( var fallback in CreateFallbackProfiles() )
		{
			if ( fallback is null || string.IsNullOrWhiteSpace( fallback.ResourceId ) )
				continue;

			// Never rewrite Grapple/armor profiles here — that was forcing the hook HotbarEquipable
			// and blocking paperdoll equip.
			if ( !IsSlotAllowed( fallback, EquipmentSlot.MainHand )
			     && GetPrimarySlot( fallback ) != EquipmentSlot.MainHand )
				continue;

			var id = ResourceCatalog.NormalizeResourceId( fallback.ResourceId );
			fallback.ResourceId = id;

			if ( ByResourceId.TryGetValue( id, out var existing ) && existing is not null )
			{
				existing.HotbarEquipable = true;
				if ( string.IsNullOrWhiteSpace( existing.Slot ) )
					existing.Slot = fallback.Slot;
				if ( existing.AllowedSlots is null || existing.AllowedSlots.Count == 0 )
					existing.AllowedSlots = fallback.AllowedSlots;
				if ( existing.Actions is null || existing.Actions.Count == 0 )
					existing.Actions = fallback.Actions;
				if ( string.IsNullOrWhiteSpace( existing.ToolPrefab ) )
					existing.ToolPrefab = fallback.ToolPrefab;
				continue;
			}

			Profiles.Add( fallback );
			ByResourceId[id] = fallback;
		}
	}

	static IEnumerable<EquipmentProfileData> CreateFallbackProfiles()
	{
		yield return new EquipmentProfileData
		{
			ResourceId = "basic_sword",
			DisplayName = "Sword",
			Slot = "mainHand",
			AllowedSlots = { "mainHand" },
			Actions = { "PrimaryMelee", "Block" },
			HotbarEquipable = true,
		};

		yield return new EquipmentProfileData
		{
			ResourceId = "build_hammer",
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
			GrappleSlackRetractMetersPerSecond = 7f,
			GrappleTautSlackMeters = 0.75f,
			GrappleSwingLoadSlackGraceMeters = 2.5f,
			GrappleSwingLoadCentripetalGravityFraction = 0.35f,
			GrappleDetractMetersPerSecond = 4f,
			GrappleAttachStaminaCost = 8f,
			GrappleAirborneStaminaPerSecond = 1.5f,
		};

		yield return new EquipmentProfileData
		{
			ResourceId = "axe_stone",
			DisplayName = "Stone Axe",
			Slot = "mainHand",
			AllowedSlots = { "mainHand" },
			Actions = { "PrimaryMelee" },
			HotbarEquipable = true,
			HarvestToolType = "Axe",
			HarvestToolTier = 0,
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
