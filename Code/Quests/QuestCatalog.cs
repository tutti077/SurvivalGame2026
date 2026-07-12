using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads quests from <c>data/quests.json</c> with built-in fallbacks.</summary>
public static class QuestCatalog
{
	const string QuestsFilePath = "data/quests.json";

	static readonly List<QuestDefinition> Quests = new();
	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<QuestDefinition> All
	{
		get
		{
			EnsureLoaded();
			return Quests;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		ReloadFromDisk();
	}

	public static QuestDefinition Get( string questId )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( questId ) )
			return null;

		for ( var i = 0; i < Quests.Count; i++ )
		{
			if ( string.Equals( Quests[i].Id, questId, StringComparison.OrdinalIgnoreCase ) )
				return Quests[i];
		}

		return null;
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
		Quests.Clear();

		if ( TryLoadFromFile() )
			return;

		LoadFallback();
	}

	static bool TryLoadFromFile()
	{
		if ( !FileSystem.Mounted.FileExists( QuestsFilePath ) )
			return false;

		try
		{
			var json = FileSystem.Mounted.ReadAllText( QuestsFilePath );
			var root = JsonSerializer.Deserialize<QuestFileRoot>( json, JsonOptions );
			if ( root?.Quests is null || root.Quests.Count == 0 )
				return false;

			for ( var i = 0; i < root.Quests.Count; i++ )
			{
				var quest = root.Quests[i];
				if ( quest is null || string.IsNullOrWhiteSpace( quest.Id ) )
					continue;

				Quests.Add( quest );
			}

			return Quests.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[QuestCatalog] Failed to parse {QuestsFilePath}: {ex.Message}" );
			return false;
		}
	}

	static void LoadFallback()
	{
		Quests.Add( new QuestDefinition
		{
			Id = "welcome",
			DisplayName = "Welcome to the Wild",
			Description = "Learn the basics of gathering and crafting to survive your first night.",
			Task = "Hand-harvest 3 sticks and 2 rocks from resource nodes.",
			Rewards = new List<string> { "25 XP", "Plant Fiber x1" }
		} );
		Quests.Add( new QuestDefinition
		{
			Id = "craft_blade",
			DisplayName = "A Sharp Edge",
			Description = "A simple sword will help you defend yourself and test combat systems.",
			Task = "Craft a sword at the crafting station.",
			Rewards = new List<string> { "50 XP", "Wood x3" }
		} );
		Quests.Add( new QuestDefinition
		{
			Id = "stock_up",
			DisplayName = "Stock the Pack",
			Description = "Fill your inventory with mixed materials for future recipes.",
			Task = "Hold at least 5 different resource stacks in your inventory at once.",
			Rewards = new List<string> { "75 XP", "Rock x5" }
		} );
	}

	static int TryReadJsonHash()
	{
		if ( !FileSystem.Mounted.FileExists( QuestsFilePath ) )
			return 0;

		try
		{
			return FileSystem.Mounted.ReadAllText( QuestsFilePath ).GetHashCode();
		}
		catch
		{
			return 0;
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	sealed class QuestFileRoot
	{
		public List<QuestDefinition> Quests { get; set; } = new();
	}
}
