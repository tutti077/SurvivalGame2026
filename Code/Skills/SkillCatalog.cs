using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads skill nodes from <c>data/skills.json</c> with a built-in fallback web.</summary>
public static class SkillCatalog
{
	const string SkillsFilePath = "data/skills.json";

	static readonly List<SkillDefinition> Skills = new();
	static bool _loaded;
	static int _loadedJsonHash;

	public static IReadOnlyList<SkillDefinition> All
	{
		get
		{
			EnsureLoaded();
			return Skills;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		EnsureLoaded();
	}

	public static SkillDefinition Get( string skillId )
	{
		EnsureLoaded();
		if ( string.IsNullOrWhiteSpace( skillId ) )
			return null;

		for ( var i = 0; i < Skills.Count; i++ )
		{
			if ( string.Equals( Skills[i].Id, skillId, StringComparison.OrdinalIgnoreCase ) )
				return Skills[i];
		}

		return null;
	}

	public static void EnsureLoaded()
	{
		var jsonHash = TryReadJsonHash();
		if ( _loaded && jsonHash == _loadedJsonHash )
			return;

		_loaded = true;
		_loadedJsonHash = jsonHash;
		Skills.Clear();

		if ( TryLoadFromFile() )
			return;

		LoadFallback();
	}

	static bool TryLoadFromFile()
	{
		if ( !FileSystem.Mounted.FileExists( SkillsFilePath ) )
			return false;

		try
		{
			var json = FileSystem.Mounted.ReadAllText( SkillsFilePath );
			var root = JsonSerializer.Deserialize<SkillFileRoot>( json, JsonOptions );
			if ( root?.Skills is null || root.Skills.Count == 0 )
				return false;

			for ( var i = 0; i < root.Skills.Count; i++ )
			{
				var skill = root.Skills[i];
				if ( skill is null || string.IsNullOrWhiteSpace( skill.Id ) )
					continue;

				Skills.Add( skill );
			}

			NormalizeGraphLinks();
			return Skills.Count > 0;
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[SkillCatalog] Failed to parse {SkillsFilePath}: {ex.Message}" );
			return false;
		}
	}

	static void LoadFallback()
	{
		Skills.Add( new SkillDefinition
		{
			Id = "survival_core",
			DisplayName = "Survival Core",
			Description = "Foundation of wilderness survival. Unlocks basic gathering instincts.",
			Icon = "ui/items/sample_resource.png",
			X = 0.5f,
			Y = 0.5f,
			Children = new List<string> { "forager", "stone_breaker" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "forager",
			DisplayName = "Forager",
			Description = "Hand harvesting yields slightly more resources from plants and loose nodes.",
			Icon = "ui/items/sample_bush.png",
			X = 0.32f,
			Y = 0.28f,
			Parents = new List<string> { "survival_core" },
			Children = new List<string> { "crafting_focus", "pathfinder" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "stone_breaker",
			DisplayName = "Stone Breaker",
			Description = "Improved efficiency when harvesting rock nodes.",
			Icon = "ui/items/sample_rock.png",
			X = 0.68f,
			Y = 0.28f,
			Parents = new List<string> { "survival_core" },
			Children = new List<string> { "inventory_tidy", "pathfinder" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "crafting_focus",
			DisplayName = "Crafting Focus",
			Description = "Hold-to-craft completes slightly faster (future tuning hook).",
			Icon = "ui/items/sample_stick.png",
			X = 0.22f,
			Y = 0.52f,
			Parents = new List<string> { "forager" },
			Children = new List<string> { "blade_training" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "inventory_tidy",
			DisplayName = "Inventory Tidy",
			Description = "Stacks merge more generously when moving items (future tuning hook).",
			Icon = "ui/menu/InventoryTab.png",
			X = 0.78f,
			Y = 0.52f,
			Parents = new List<string> { "stone_breaker" },
			Children = new List<string> { "guard_stance" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "blade_training",
			DisplayName = "Blade Training",
			Description = "Melee attacks cost less stamina on light swings (future tuning hook).",
			Icon = "ui/items/item_sample_sword.png",
			X = 0.38f,
			Y = 0.76f,
			Parents = new List<string> { "crafting_focus" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "guard_stance",
			DisplayName = "Guard Stance",
			Description = "Blocking recovers stamina sooner after a successful block (future tuning hook).",
			Icon = "ui/menu/tab_blank.png",
			X = 0.62f,
			Y = 0.76f,
			Parents = new List<string> { "inventory_tidy" }
		} );
		Skills.Add( new SkillDefinition
		{
			Id = "pathfinder",
			DisplayName = "Pathfinder",
			Description = "Sprint drains stamina more slowly while exploring (future tuning hook).",
			Icon = "ui/items/sample_resource.png",
			X = 0.5f,
			Y = 0.14f,
			Parents = new List<string> { "forager", "stone_breaker" }
		} );

		NormalizeGraphLinks();
	}

	/// <summary>Visits each directed link parent → child once (for drawing the web).</summary>
	public static void ForEachGraphLink( Action<string, string> visitLink )
	{
		EnsureLoaded();
		if ( visitLink is null )
			return;

		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < Skills.Count; i++ )
		{
			var skill = Skills[i];
			if ( skill is null )
				continue;

			MergeLegacyParents( skill );

			if ( skill.Parents is not null )
			{
				for ( var p = 0; p < skill.Parents.Count; p++ )
				{
					var parentId = skill.Parents[p];
					if ( string.IsNullOrWhiteSpace( parentId ) )
						continue;

					var key = $"{parentId}->{skill.Id}";
					if ( !seen.Add( key ) )
						continue;

					visitLink( parentId, skill.Id );
				}
			}

			if ( skill.Children is null )
				continue;

			for ( var c = 0; c < skill.Children.Count; c++ )
			{
				var childId = skill.Children[c];
				if ( string.IsNullOrWhiteSpace( childId ) )
					continue;

				var key = $"{skill.Id}->{childId}";
				if ( !seen.Add( key ) )
					continue;

				visitLink( skill.Id, childId );
			}
		}
	}

	static void NormalizeGraphLinks()
	{
		var byId = new Dictionary<string, SkillDefinition>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < Skills.Count; i++ )
		{
			var skill = Skills[i];
			if ( skill is null || string.IsNullOrWhiteSpace( skill.Id ) )
				continue;

			MergeLegacyParents( skill );
			byId[skill.Id] = skill;
		}

		foreach ( var skill in byId.Values )
		{
			if ( skill.Parents is not null )
			{
				for ( var i = 0; i < skill.Parents.Count; i++ )
				{
					var parentId = skill.Parents[i];
					if ( string.IsNullOrWhiteSpace( parentId ) || !byId.TryGetValue( parentId, out var parent ) )
						continue;

					TryAddLink( parent.Children, skill.Id );
				}
			}

			if ( skill.Children is null )
				continue;

			for ( var i = 0; i < skill.Children.Count; i++ )
			{
				var childId = skill.Children[i];
				if ( string.IsNullOrWhiteSpace( childId ) || !byId.TryGetValue( childId, out var child ) )
					continue;

				TryAddLink( child.Parents, skill.Id );
			}
		}
	}

	static void MergeLegacyParents( SkillDefinition skill )
	{
		if ( skill.RequiresLegacy is not { Count: > 0 } )
			return;

		for ( var i = 0; i < skill.RequiresLegacy.Count; i++ )
			TryAddLink( skill.Parents, skill.RequiresLegacy[i] );

		skill.RequiresLegacy.Clear();
	}

	static void TryAddLink( List<string> list, string id )
	{
		if ( list is null || string.IsNullOrWhiteSpace( id ) )
			return;

		for ( var i = 0; i < list.Count; i++ )
		{
			if ( string.Equals( list[i], id, StringComparison.OrdinalIgnoreCase ) )
				return;
		}

		list.Add( id );
	}

	static int TryReadJsonHash()
	{
		if ( !FileSystem.Mounted.FileExists( SkillsFilePath ) )
			return 0;

		try
		{
			return FileSystem.Mounted.ReadAllText( SkillsFilePath ).GetHashCode();
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

	sealed class SkillFileRoot
	{
		public List<SkillDefinition> Skills { get; set; } = new();
	}
}
