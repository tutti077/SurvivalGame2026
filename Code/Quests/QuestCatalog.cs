using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>Loads quest definitions from <c>data/quests.json</c> (file order is display order).</summary>
public static class QuestCatalog
{
	const string QuestsFilePath = "data/quests.json";

	static readonly List<QuestDefinition> Quests = new();
	static bool _loaded;
	static int _loadedJsonHash;
	static int _contentVersion;

	public static IReadOnlyList<QuestDefinition> All
	{
		get
		{
			EnsureLoaded();
			return Quests;
		}
	}

	/// <summary>Bumps whenever the quest list is replaced — UI rebuilds rows when this changes.</summary>
	public static int ContentVersion
	{
		get
		{
			EnsureLoaded();
			return _contentVersion;
		}
	}

	public static void ForceReload()
	{
		_loaded = false;
		_loadedJsonHash = 0;
		ReloadFromDisk();
		QuestTracker.Invalidate();
	}

	/// <summary>
	/// Re-read <c>data/quests.json</c> if its text changed since the last load. Called when the quest
	/// menu opens (infrequent) so JSON edits and hot reloads — which keep static state — show up
	/// without a restart.
	/// </summary>
	public static void ReloadIfChanged()
	{
		if ( !_loaded )
		{
			ReloadFromDisk();
			return;
		}

		var hash = TryReadJsonHash();
		if ( hash == 0 || hash == _loadedJsonHash )
			return;

		ForceReload();
	}

	static int TryReadJsonHash()
	{
		if ( !FileSystem.Mounted.FileExists( QuestsFilePath ) )
			return 0;

		try
		{
			return StringComparer.Ordinal.GetHashCode( FileSystem.Mounted.ReadAllText( QuestsFilePath ) );
		}
		catch
		{
			return 0;
		}
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
		_loaded = true;
		_contentVersion++;
		Quests.Clear();

		if ( !FileSystem.Mounted.FileExists( QuestsFilePath ) )
		{
			Log.Warning( $"[QuestCatalog] Missing {QuestsFilePath} — no quests loaded." );
			return;
		}

		try
		{
			var json = FileSystem.Mounted.ReadAllText( QuestsFilePath );
			_loadedJsonHash = StringComparer.Ordinal.GetHashCode( json );
			var root = JsonSerializer.Deserialize<QuestFileRoot>( json, JsonOptions );
			if ( root?.Quests is null )
				return;

			var disabledIds = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

			for ( var i = 0; i < root.Quests.Count; i++ )
			{
				var quest = root.Quests[i];
				if ( quest is null || string.IsNullOrWhiteSpace( quest.Id ) )
					continue;

				if ( quest.Disabled )
				{
					disabledIds.Add( quest.Id );
					continue;
				}

				quest.Requires ??= new List<string>();
				quest.Objectives ??= new List<QuestObjective>();
				quest.Rewards ??= new List<string>();

				if ( quest.Objectives.Count == 0 )
					Log.Warning( $"[QuestCatalog] Quest '{quest.Id}' has no objectives and can never complete." );

				Quests.Add( quest );
			}

			// A disabled quest must not block the chain: drop it from every prerequisite list.
			if ( disabledIds.Count > 0 )
			{
				for ( var i = 0; i < Quests.Count; i++ )
					Quests[i].Requires.RemoveAll( disabledIds.Contains );

				Log.Info( $"[QuestCatalog] Disabled quests skipped: {string.Join( ", ", disabledIds )}." );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[QuestCatalog] Failed to parse {QuestsFilePath}: {ex.Message}" );
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
