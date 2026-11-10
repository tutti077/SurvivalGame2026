using System;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>
/// Dev options behind Settings → Dev tools in the in-game menu (Mark 2026-11-06: "We can add more stuff there later on").
/// Two kinds: <b>per player</b> (<see cref="StaticMeleeCombo"/> — every client sets its own) and <b>host-wide</b>
/// (<see cref="InfiniteStamina"/>, <see cref="DayLocked"/> — only the host changes them; they reach every peer through
/// <see cref="WorldNetworkSession"/>'s host-synced fields, and a scene without that component runs them locally).
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

	/// <summary>
	/// Melee is a static three-hit combo (Mark 2026-11-06): quick clicks go right, left, overhead whatever the mouse
	/// does, a long hold is a heavy that ends the combo, and Q on the ground is the weapon's special (sword / club
	/// stab). Off = the free-aim mouse-direction attacks. Rules: <see cref="PlayerCombat"/> (PlayerCombat.StaticCombo.cs).
	/// </summary>
	public static bool StaticMeleeCombo
	{
		get
		{
			EnsureLoaded();
			return _cached.StaticMeleeCombo;
		}
		set
		{
			EnsureLoaded();
			if ( _cached.StaticMeleeCombo == value )
				return;

			_cached.StaticMeleeCombo = value;
			Save();
			Changed?.Invoke();
		}
	}

	/// <summary>
	/// Per player: Valheim walk-facing (Mark 2026-11-10: "the players orientation changes with WASD"). While moving with
	/// no attack / block held in third person, the Body renderer turns toward the WASD travel direction - S walks toward
	/// the camera instead of back-pedalling. Off = the stock controller: the body faces the camera and strafes /
	/// back-pedals. Guard, swings and the bow stance face the camera either way (<see cref="PlayerCombat.TryGetCombatFacingYaw"/>
	/// wins). Source: <see cref="PlayerMovement.TryGetWasdFacingTargetYaw"/>; presentation: <see cref="PlayerAnimation"/>.
	/// </summary>
	public static bool WasdBodyFacing
	{
		get
		{
			EnsureLoaded();
			return _cached.WasdBodyFacing;
		}
		set
		{
			EnsureLoaded();
			if ( _cached.WasdBodyFacing == value )
				return;

			_cached.WasdBodyFacing = value;
			Save();
			Changed?.Invoke();
		}
	}

	/// <summary>Host-wide settings may be changed on this machine: the host, or offline.</summary>
	public static bool CanChangeHostSettings => !Networking.IsActive || Networking.IsHost;

	/// <summary>
	/// Host-wide: stamina costs nothing and every bar stays full (Mark 2026-11-06; on by default, as the basicplayer
	/// prefab's old per-pawn debug flag was). Read by <see cref="PlayerVitals.InfiniteStamina"/> on every peer.
	/// </summary>
	public static bool InfiniteStamina =>
		WorldNetworkSession.Instance is { IsValid: true } session ? session.DevInfiniteStamina : SavedInfiniteStamina;

	/// <summary>Host-wide: the clock jumps to noon and holds there on every peer (<see cref="EnvironmentDayNightCycle"/>).</summary>
	public static bool DayLocked =>
		WorldNetworkSession.Instance is { IsValid: true } session ? session.DevDayLocked : SavedDayLocked;

	/// <summary>The host's last choice (what a new session publishes).</summary>
	internal static bool SavedInfiniteStamina
	{
		get { EnsureLoaded(); return _cached.InfiniteStamina; }
	}

	internal static bool SavedDayLocked
	{
		get { EnsureLoaded(); return _cached.DayLocked; }
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

		WorldNetworkSession.Instance?.HostPublishDevSettings();
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

		WorldNetworkSession.Instance?.HostPublishDevSettings();
		Changed?.Invoke();
		return true;
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

	[ConCmd( "melee_combo_static" )]
	public static void ConCmdStaticMeleeCombo( string value = null )
	{
		if ( !string.IsNullOrWhiteSpace( value ) )
			StaticMeleeCombo = value.Trim() is "1" or "true" or "on";
		Log.Info( $"[DevTools] static 3-hit melee combo: {(StaticMeleeCombo ? "on" : "off")}" );
	}

	[ConCmd( "wasd_facing" )]
	public static void ConCmdWasdBodyFacing( string value = null )
	{
		if ( !string.IsNullOrWhiteSpace( value ) )
			WasdBodyFacing = value.Trim() is "1" or "true" or "on";
		Log.Info( $"[DevTools] body turns with WASD: {(WasdBodyFacing ? "on" : "off")}" );
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
		public bool StaticMeleeCombo { get; set; }
		public bool WasdBodyFacing { get; set; } = true;
		public bool InfiniteStamina { get; set; } = true;
		public bool DayLocked { get; set; }
	}
}
