using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Client-local quest progress for the player at this machine. Loads once per player key
/// (Steam id) from <see cref="QuestSaveStore"/>, applies reported events, and writes through on
/// every change so nothing depends on a clean shutdown.
/// <para>
/// Progress is per <b>player</b>, not per world — it follows you between saves/scenes. Host-side
/// actions reach here through <see cref="PlayerQuests"/>, which forwards to the owning client.
/// </para>
/// </summary>
public static class QuestTracker
{
	/// <summary>Raised after any progress, completion or unlock change (UI refresh hook).</summary>
	public static event Action Changed;

	sealed class QuestRuntime
	{
		public QuestDefinition Definition;
		public QuestState State;
		public int[] Progress;
		public string CompletedUtc;
	}

	static readonly Dictionary<string, QuestRuntime> Runtime = new( StringComparer.OrdinalIgnoreCase );
	static string _loadedKey;
	static bool _loaded;

	public static string PlayerKey => _loadedKey ?? ResolvePlayerKey();

	public static void EnsureLoaded()
	{
		var key = ResolvePlayerKey();
		if ( _loaded && string.Equals( _loadedKey, key, StringComparison.Ordinal ) )
			return;

		LoadFor( key );
	}

	/// <summary>Drop cached state so the next access re-reads the file (catalog reload, debug).</summary>
	public static void Invalidate()
	{
		_loaded = false;
		_loadedKey = null;
		Runtime.Clear();
	}

	public static QuestState GetState( string questId )
	{
		EnsureLoaded();
		return Runtime.TryGetValue( questId ?? string.Empty, out var rt ) ? rt.State : QuestState.Locked;
	}

	public static int GetProgress( string questId, int objectiveIndex )
	{
		EnsureLoaded();
		if ( !Runtime.TryGetValue( questId ?? string.Empty, out var rt ) )
			return 0;

		return objectiveIndex >= 0 && objectiveIndex < rt.Progress.Length ? rt.Progress[objectiveIndex] : 0;
	}

	public static bool IsObjectiveComplete( string questId, int objectiveIndex )
	{
		var def = QuestCatalog.Get( questId );
		if ( def is null || objectiveIndex < 0 || objectiveIndex >= def.Objectives.Count )
			return false;

		return GetProgress( questId, objectiveIndex ) >= def.Objectives[objectiveIndex].RequiredCount;
	}

	public static int CountByState( QuestState state )
	{
		EnsureLoaded();
		var n = 0;
		foreach ( var rt in Runtime.Values )
		{
			if ( rt.State == state )
				n++;
		}

		return n;
	}

	/// <summary>
	/// Apply one gameplay event to every active quest. <paramref name="match"/> is compared to the
	/// objective's <see cref="QuestObjective.Match"/> (empty objective match accepts anything).
	/// </summary>
	public static void Report( string eventId, string match = null, int amount = 1 )
	{
		if ( string.IsNullOrWhiteSpace( eventId ) || amount <= 0 )
			return;

		EnsureLoaded();

		var changed = false;
		var completedAny = false;

		foreach ( var quest in QuestCatalog.All )
		{
			if ( !Runtime.TryGetValue( quest.Id, out var rt ) || rt.State != QuestState.Active )
				continue;

			for ( var i = 0; i < quest.Objectives.Count; i++ )
			{
				var objective = quest.Objectives[i];
				if ( !string.Equals( objective.Event, eventId, StringComparison.OrdinalIgnoreCase ) )
					continue;

				if ( !string.IsNullOrWhiteSpace( objective.Match )
				     && !string.Equals( objective.Match, match ?? string.Empty, StringComparison.OrdinalIgnoreCase ) )
					continue;

				var required = objective.RequiredCount;
				if ( rt.Progress[i] >= required )
					continue;

				rt.Progress[i] = Math.Min( required, rt.Progress[i] + amount );
				changed = true;
			}

			if ( AllObjectivesDone( rt ) )
			{
				rt.State = QuestState.Completed;
				rt.CompletedUtc = DateTime.UtcNow.ToString( "o" );
				completedAny = true;
				Log.Info( $"[Quests] Completed '{quest.DisplayName}'." );
			}
		}

		if ( completedAny )
			RecomputeLocks();

		if ( !changed )
			return;

		Save();
		Changed?.Invoke();
	}

	/// <summary>Wipe this player's progress (console: <c>quests_reset</c>).</summary>
	public static void ResetAll()
	{
		var key = ResolvePlayerKey();
		QuestSaveStore.Delete( key );
		// Also re-read the JSON: hot reload keeps static state, so a reset is the manual refresh path.
		QuestCatalog.ForceReload();
		EnsureLoaded();
		Changed?.Invoke();
	}

	static void LoadFor( string key )
	{
		Runtime.Clear();
		_loaded = true;
		_loadedKey = key;

		var file = QuestSaveStore.Load( key );

		foreach ( var quest in QuestCatalog.All )
		{
			var rt = new QuestRuntime
			{
				Definition = quest,
				State = QuestState.Locked,
				Progress = new int[quest.Objectives.Count],
			};

			if ( file?.Quests is not null && file.Quests.TryGetValue( quest.Id, out var saved ) && saved is not null )
			{
				var n = Math.Min( rt.Progress.Length, saved.Progress?.Length ?? 0 );
				for ( var i = 0; i < n; i++ )
					rt.Progress[i] = Math.Max( 0, saved.Progress[i] );

				if ( saved.Completed || AllObjectivesDone( rt ) )
				{
					rt.State = QuestState.Completed;
					rt.CompletedUtc = saved.CompletedUtc;
				}
			}

			Runtime[quest.Id] = rt;
		}

		RecomputeLocks();

		var completed = CountByState( QuestState.Completed );
		var active = CountByState( QuestState.Active );
		Log.Info( $"[Quests] Loaded player '{key}': {completed} completed, {active} active, {Runtime.Count - completed - active} locked ({QuestSaveStore.GetFilePath( key )})." );
	}

	static bool AllObjectivesDone( QuestRuntime rt )
	{
		var objectives = rt.Definition.Objectives;
		if ( objectives.Count == 0 )
			return false;

		for ( var i = 0; i < objectives.Count; i++ )
		{
			if ( rt.Progress[i] < objectives[i].RequiredCount )
				return false;
		}

		return true;
	}

	/// <summary>Locked ↔ Active from prerequisites; completed quests never move.</summary>
	static void RecomputeLocks()
	{
		foreach ( var rt in Runtime.Values )
		{
			if ( rt.State == QuestState.Completed )
				continue;

			rt.State = PrerequisitesMet( rt.Definition ) ? QuestState.Active : QuestState.Locked;
		}
	}

	static bool PrerequisitesMet( QuestDefinition def )
	{
		var requires = def.Requires;
		if ( requires is null || requires.Count == 0 )
			return true;

		for ( var i = 0; i < requires.Count; i++ )
		{
			var id = requires[i];
			if ( string.IsNullOrWhiteSpace( id ) )
				continue;

			if ( !Runtime.TryGetValue( id, out var req ) || req.State != QuestState.Completed )
				return false;
		}

		return true;
	}

	static void Save()
	{
		var file = new QuestSaveFile { PlayerKey = _loadedKey };

		foreach ( var rt in Runtime.Values )
		{
			var touched = rt.State == QuestState.Completed;
			if ( !touched )
			{
				for ( var i = 0; i < rt.Progress.Length; i++ )
				{
					if ( rt.Progress[i] > 0 )
					{
						touched = true;
						break;
					}
				}
			}

			// Untouched quests are omitted so a fresh file stays tiny and new quests need no migration.
			if ( !touched )
				continue;

			file.Quests[rt.Definition.Id] = new QuestSaveEntry
			{
				Completed = rt.State == QuestState.Completed,
				CompletedUtc = rt.CompletedUtc ?? string.Empty,
				Progress = CopyProgress( rt.Progress ),
			};
		}

		QuestSaveStore.Save( _loadedKey, file );
	}

	/// <summary><c>Array.Clone</c> is not on the s&amp;box whitelist — copy by hand.</summary>
	static int[] CopyProgress( int[] source )
	{
		var copy = new int[source.Length];
		for ( var i = 0; i < source.Length; i++ )
			copy[i] = source[i];
		return copy;
	}

	static string ResolvePlayerKey()
	{
		var steam = Connection.Local?.SteamId.ValueUnsigned ?? 0UL;
		return steam != 0UL ? steam.ToString() : "local";
	}

	[ConCmd( "quests_reset" )]
	public static void ConCmdReset()
	{
		ResetAll();
		Log.Info( $"[Quests] Progress reset for player '{PlayerKey}'." );
	}

	[ConCmd( "quests_list" )]
	public static void ConCmdList()
	{
		EnsureLoaded();
		var sb = new StringBuilder();
		sb.AppendLine( $"[Quests] Player '{PlayerKey}' — {QuestSaveStore.GetFilePath( PlayerKey )}" );

		foreach ( var quest in QuestCatalog.All )
		{
			if ( !Runtime.TryGetValue( quest.Id, out var rt ) )
				continue;

			sb.Append( "  " ).Append( rt.State.ToString().PadRight( 9 ) ).Append( ' ' ).Append( quest.Id );
			for ( var i = 0; i < quest.Objectives.Count; i++ )
				sb.Append( $"  [{rt.Progress[i]}/{quest.Objectives[i].RequiredCount}]" );
			sb.AppendLine();
		}

		Log.Info( sb.ToString() );
	}

	/// <summary>Usage: <c>quests_report entity_killed fox 5</c> (pass "" for match when unused).</summary>
	[ConCmd( "quests_report" )]
	public static void ConCmdReport( string eventId, string match, int amount )
	{
		Report( eventId, match, amount );
		Log.Info( $"[Quests] Reported '{eventId}' match='{match}' x{amount}." );
	}
}
