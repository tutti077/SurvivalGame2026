using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Dev options behind Settings → Dev tools in the in-game menu (Mark 2026-11-06: "We can add more stuff there later on").
/// The flags are <b>host-wide</b> (<see cref="InfiniteStamina"/>, <see cref="DayLocked"/>, <see cref="ScavDifficulty"/> — only the host changes them;
/// the per-player static combo and WASD body facing were removed 2026-11-14 for the directional combat). Host-wide values reach clients
/// by static RPC: a client asks the host once it is connected, the host answers that client, and every host toggle is
/// broadcast. Until the answer lands a client uses its own saved values (Mark 2026-11-12: the old scene-synced fields
/// left a joined client on its own defaults — infinite stamina on, clock running — whatever the host chose).
/// Saved to <c>dev_tools_settings.json</c> in <c>FileSystem.Data</c> and written through on change (the host-wide ones
/// are the host's last choice). Add a flag here, a row on the Dev tools page (<see cref="GameSettingsMenuSection"/>)
/// and, when it helps testing, a console toggle.
/// </summary>
public static class DevToolsSettings
{
	const string FileName = "dev_tools_settings.json";

	static SettingsFile _cached;
	static bool _loaded;

	public static event Action Changed;

	/// <summary>Host-wide settings may be changed on this machine: the host, or offline.</summary>
	public static bool CanChangeHostSettings => !Networking.IsActive || Networking.IsHost;

	/// <summary>
	/// Host-wide: stamina costs nothing and every bar stays full (Mark 2026-11-06; on by default, as the basicplayer
	/// prefab's old per-pawn debug flag was). Read by <see cref="PlayerVitals.InfiniteStamina"/> on every peer.
	/// </summary>
	public static bool InfiniteStamina
	{
		get
		{
			if ( CanChangeHostSettings )
			{
				ForgetHostAnswer();
				return SavedInfiniteStamina;
			}
			RequestFromHostIfNeeded();
			return _hostInfiniteStamina ?? SavedInfiniteStamina;
		}
	}

	/// <summary>Host-wide: the clock jumps to noon and holds there on every peer (<see cref="EnvironmentDayNightCycle"/>).</summary>
	public static bool DayLocked
	{
		get
		{
			if ( CanChangeHostSettings )
			{
				ForgetHostAnswer();
				return SavedDayLocked;
			}
			RequestFromHostIfNeeded();
			return _hostDayLocked ?? SavedDayLocked;
		}
	}

	/// <summary>
	/// Host-wide: the scav difficulty level, 1 (the scav's real difficulty, the dumbest fighter) to
	/// <see cref="ScavCombatStyleCatalog.LevelCount"/> (God, a stand-in for a human opponent) — Mark 2026-11-15: "make the
	/// Scav ai a sliding scale". Scav AI runs on the host only; clients read it for the Dev tools label.
	/// </summary>
	public static int ScavDifficulty
	{
		get
		{
			if ( CanChangeHostSettings )
			{
				ForgetHostAnswer();
				return SavedScavDifficulty;
			}
			RequestFromHostIfNeeded();
			return _hostScavDifficulty ?? SavedScavDifficulty;
		}
	}

	/// <summary>Client: the host's host-wide values, once its answer arrived (null until then).</summary>
	static bool? _hostInfiniteStamina, _hostDayLocked;
	static int? _hostScavDifficulty;
	static RealTimeSince _sinceHostRequest = 999f;

	/// <summary>Offline / hosting: a later join (rejoin, another host) must ask again.</summary>
	static void ForgetHostAnswer()
	{
		_hostInfiniteStamina = _hostDayLocked = null;
		_hostScavDifficulty = null;
	}

	/// <summary>
	/// Client: ask the host for its host-wide values until an answer lands (retried every 2 s, so a request sent while
	/// the join was still settling is not lost). Runs from the getters, which vitals and the day cycle read every frame.
	/// </summary>
	static void RequestFromHostIfNeeded()
	{
		if ( _hostInfiniteStamina.HasValue || _sinceHostRequest < 2f )
			return;

		_sinceHostRequest = 0f;
		RpcRequestHostSettings();
	}

	[Rpc.Host( NetFlags.Reliable )]
	static void RpcRequestHostSettings()
	{
		if ( !Networking.IsHost || Rpc.Caller is not { } caller )
			return;

		using ( Rpc.FilterInclude( caller ) )
			RpcHostSettings( SavedInfiniteStamina, SavedDayLocked, SavedScavDifficulty );
	}

	/// <summary>Host → clients: the host's host-wide values (answer to a join request, or a toggle).</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	static void RpcHostSettings( bool infiniteStamina, bool dayLocked, int scavDifficulty )
	{
		if ( Networking.IsHost )
			return;

		_hostInfiniteStamina = infiniteStamina;
		_hostDayLocked = dayLocked;
		_hostScavDifficulty = scavDifficulty;
		Changed?.Invoke();
	}

	/// <summary>Host: push the current host-wide values to every client.</summary>
	static void BroadcastHostSettings()
	{
		if ( Networking.IsActive && Networking.IsHost )
			RpcHostSettings( SavedInfiniteStamina, SavedDayLocked, SavedScavDifficulty );
	}

	/// <summary>This machine's saved choice: the live value on the host / offline, the fallback on a client.</summary>
	internal static bool SavedInfiniteStamina
	{
		get { EnsureLoaded(); return _cached.InfiniteStamina; }
	}

	internal static bool SavedDayLocked
	{
		get { EnsureLoaded(); return _cached.DayLocked; }
	}

	internal static int SavedScavDifficulty
	{
		get { EnsureLoaded(); return Math.Clamp( _cached.ScavDifficulty, 1, ScavCombatStyleCatalog.LevelCount ); }
	}

	/// <summary>Host / offline: set infinite stamina for everyone. False on a client.</summary>
	public static bool HostSetInfiniteStamina( bool value )
	{
		if ( !CanChangeHostSettings )
			return false;

		EnsureLoaded();
		if ( _cached.InfiniteStamina != value )
		{
			_cached.InfiniteStamina = value;
			Save();
		}

		BroadcastHostSettings();
		Changed?.Invoke();
		return true;
	}

	/// <summary>Host / offline: lock (or release) the clock at noon for everyone. False on a client.</summary>
	public static bool HostSetDayLocked( bool value )
	{
		if ( !CanChangeHostSettings )
			return false;

		EnsureLoaded();
		if ( _cached.DayLocked != value )
		{
			_cached.DayLocked = value;
			Save();
		}

		BroadcastHostSettings();
		Changed?.Invoke();
		return true;
	}

	/// <summary>Host / offline: set the scav difficulty level (clamped to the ladder). False on a client.</summary>
	public static bool HostSetScavDifficulty( int level )
	{
		if ( !CanChangeHostSettings )
			return false;

		EnsureLoaded();
		level = Math.Clamp( level, 1, ScavCombatStyleCatalog.LevelCount );
		if ( _cached.ScavDifficulty != level )
		{
			_cached.ScavDifficulty = level;
			Save();
		}

		BroadcastHostSettings();
		Changed?.Invoke();
		return true;
	}

	/// <summary>
	/// Resync (Mark 2026-11-16: "we constantly get desynced when you make changes, maybe there's a way to add manual
	/// resync button to dev tools"): the host sends every networked object's full snapshot to the clients again
	/// (<c>GameObject.Network.Refresh</c>) and repeats the host-wide dev settings. Anyone may ask; the host does it.
	/// </summary>
	public static void RequestResync()
	{
		if ( !Networking.IsActive )
		{
			Log.Info( "[DevTools] resync: not in a networked session." );
			return;
		}

		RpcRequestResync();
	}

	[Rpc.Host( NetFlags.Reliable )]
	static void RpcRequestResync()
	{
		if ( !Networking.IsHost || Sandbox.Game.ActiveScene is not { } scene )
			return;

		var count = 0;
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( !go.IsValid() || go.NetworkMode != NetworkMode.Object || !go.Network.Active )
				continue;

			try
			{
				go.Network.Refresh();
				count++;
			}
			catch ( Exception ex )
			{
				Log.Warning( $"[DevTools] resync: {go.Name} failed: {ex.Message}" );
			}
		}

		BroadcastHostSettings();
		Log.Info( $"[DevTools] resync: {count} networked objects sent again (asked by {Rpc.Caller?.DisplayName ?? "host"})." );
	}

	[ConCmd( "net_resync" )]
	public static void ConCmdResync() => RequestResync();

	[ConCmd( "scav_difficulty" )]
	public static void ConCmdScavDifficulty( string value = null )
	{
		if ( !string.IsNullOrWhiteSpace( value ) )
		{
			if ( !int.TryParse( value.Trim(), out var level ) )
				Log.Warning( $"[DevTools] scav_difficulty: a level 1-{ScavCombatStyleCatalog.LevelCount}." );
			else if ( !HostSetScavDifficulty( level ) )
				Log.Warning( "[DevTools] scav_difficulty: host only." );
		}
		Log.Info( $"[DevTools] scav difficulty: {ScavDifficulty} {ScavCombatStyleCatalog.Current.Name} (of {ScavCombatStyleCatalog.LevelCount})" );
	}

	[ConCmd( "stamina_infinite" )]
	public static void ConCmdInfiniteStamina( string value = null )
	{
		if ( !string.IsNullOrWhiteSpace( value ) && !HostSetInfiniteStamina( value.Trim() is "1" or "true" or "on" ) )
			Log.Warning( "[DevTools] stamina_infinite: host only." );
		Log.Info( $"[DevTools] infinite stamina (everyone): {(InfiniteStamina ? "on" : "off")}" );
	}

	[ConCmd( "day_lock" )]
	public static void ConCmdDayLock( string value = null )
	{
		if ( !string.IsNullOrWhiteSpace( value ) && !HostSetDayLocked( value.Trim() is "1" or "true" or "on" ) )
			Log.Warning( "[DevTools] day_lock: host only." );
		Log.Info( $"[DevTools] daytime lock (everyone): {(DayLocked ? "on" : "off")}" );
	}

	static void EnsureLoaded()
	{
		if ( _loaded )
			return;

		_loaded = true;
		_cached = new SettingsFile();

		try
		{
			if ( !FileSystem.Data.FileExists( FileName ) )
				return;

			_cached = JsonSerializer.Deserialize<SettingsFile>( FileSystem.Data.ReadAllText( FileName ) ) ?? new SettingsFile();
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[DevTools] Failed to load {FileName}: {ex.Message}" );
		}
	}

	static void Save()
	{
		try
		{
			FileSystem.Data.WriteAllText( FileName, JsonSerializer.Serialize( _cached, JsonOptions ) );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[DevTools] Failed to save {FileName}: {ex.Message}" );
		}
	}

	static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	sealed class SettingsFile
	{
		public bool InfiniteStamina { get; set; } = true;
		public bool DayLocked { get; set; }
		/// <summary>1 = easiest (the scav's real difficulty).</summary>
		public int ScavDifficulty { get; set; } = 1;
	}
}
