using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host dev controls over the player pawns (Mark 2026-11-17: "cycle through players ingame and press bring them to
/// me or bring me to them ... and button to respawn"). Behind Settings → Dev tools → Players
/// (<see cref="GameSettingsMenuSection"/>) and the <c>player_*</c> console commands. Host or offline only, like the
/// other host-wide dev rows: everything here runs on the host, the pawn's owner is placed by
/// <see cref="PlayerMovement.HostDevTeleport"/> / <see cref="PlayerVitals.HostDevRespawn"/>.
/// </summary>
public static class DevPlayerControls
{
	/// <summary>A moved pawn lands this far from the other one, and this far above its feet height.</summary>
	const float ArrivalGapMeters = 1.5f;
	const float ArrivalLiftMeters = 0.25f;

	public static bool CanUse => DevToolsSettings.CanChangeHostSettings;

	/// <summary>Every player pawn in the scene, in a stable order (name, then id) so a cycler does not shuffle.</summary>
	public static void CollectPlayers( List<PlayerVitals> into )
	{
		into.Clear();
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsValid() && vitals.GameObject.IsValid() )
				into.Add( vitals );
		}

		var local = FindLocal();
		into.Sort( ( a, b ) =>
		{
			var byName = string.Compare( NameOf( a, local ), NameOf( b, local ), StringComparison.OrdinalIgnoreCase );
			return byName != 0 ? byName : a.GameObject.Id.CompareTo( b.GameObject.Id );
		} );
	}

	/// <summary>This machine's own pawn (an owned pawn wins over an ownerless test pawn the host also drives).</summary>
	public static PlayerVitals FindLocal()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		PlayerVitals fallback = null;
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() || !vitals.IsLocalInputOwnedPawn() )
				continue;

			if ( vitals.GameObject.Network is not { Active: true } net || net.Owner is not null )
				return vitals;

			fallback ??= vitals;
		}

		return fallback;
	}

	/// <summary>The owner's name; an ownerless pawn (offline, or a scripted test pawn) shows as the local player or its object name.</summary>
	public static string NameOf( PlayerVitals player, PlayerVitals local )
	{
		if ( player is null || !player.IsValid() )
			return "nobody";

		string name;
		if ( player.GameObject.Network is { Active: true, Owner: { } owner } )
			name = owner.DisplayName;
		else if ( ReferenceEquals( player, local ) )
			name = Connection.Local?.DisplayName;
		else
			name = player.GameObject.Name;

		return string.IsNullOrWhiteSpace( name ) ? "Player" : name;
	}

	/// <summary>Host: put <paramref name="target"/> just in front of where this machine's player is looking.</summary>
	public static string BringToMe( PlayerVitals target )
	{
		if ( !TryResolvePair( target, out var me, out var failure ) )
			return failure;

		var yaw = me.Components.Get<PlayerController>()?.EyeAngles.yaw ?? me.WorldRotation.Yaw();
		var arrival = ArrivalBeside( me.WorldPosition, Rotation.FromYaw( yaw ).Forward );
		var name = NameOf( target, me );
		return Teleport( target, arrival ) ? $"Brought {name} to you." : $"{name} cannot be moved right now.";
	}

	/// <summary>Host: put this machine's player next to <paramref name="target"/>, on the side it came from.</summary>
	public static string GoTo( PlayerVitals target )
	{
		if ( !TryResolvePair( target, out var me, out var failure ) )
			return failure;

		var arrival = ArrivalBeside( target.WorldPosition, me.WorldPosition - target.WorldPosition );
		var name = NameOf( target, me );
		return Teleport( me, arrival ) ? $"Moved you to {name}." : "You cannot be moved right now.";
	}

	/// <summary>Host: send <paramref name="target"/> (this machine's player included) back to its bed / spawn with full pools.</summary>
	public static string Respawn( PlayerVitals target )
	{
		if ( !CanUse )
			return "Host only.";
		if ( target is null || !target.IsValid() )
			return "No player selected.";

		var name = NameOf( target, FindLocal() );
		return target.HostDevRespawn() ? $"Respawned {name}." : $"{name}: no spawn point to respawn at.";
	}

	static bool TryResolvePair( PlayerVitals target, out PlayerVitals me, out string failure )
	{
		me = null;
		failure = null;
		if ( !CanUse )
			failure = "Host only.";
		else if ( target is null || !target.IsValid() )
			failure = "No player selected.";
		else if ( ( me = FindLocal() ) is null )
			failure = "You have no pawn in the world.";
		else if ( ReferenceEquals( me, target ) )
			failure = "That is you.";

		return failure is null;
	}

	static Vector3 ArrivalBeside( Vector3 anchor, Vector3 towards )
	{
		var dir = towards.WithZ( 0f );
		dir = dir.LengthSquared < 1f ? Vector3.Forward : dir.Normal;
		return anchor
			+ dir * TerrainWorldUnits.MetersToEngine( ArrivalGapMeters )
			+ Vector3.Up * TerrainWorldUnits.MetersToEngine( ArrivalLiftMeters );
	}

	static bool Teleport( PlayerVitals player, Vector3 worldPos ) =>
		player.Components.Get<PlayerMovement>() is { } movement && movement.HostDevTeleport( worldPos );

	// ---- console

	/// <summary>A 1-based number from <c>player_list</c>, or part of a name.</summary>
	static PlayerVitals ResolveArgument( string who, List<PlayerVitals> players, PlayerVitals local )
	{
		if ( string.IsNullOrWhiteSpace( who ) )
			return null;

		who = who.Trim();
		if ( int.TryParse( who, out var number ) )
			return number >= 1 && number <= players.Count ? players[number - 1] : null;

		foreach ( var player in players )
		{
			if ( NameOf( player, local ).Contains( who, StringComparison.OrdinalIgnoreCase ) )
				return player;
		}

		return null;
	}

	static PlayerVitals ResolveForCommand( string command, string who )
	{
		var players = new List<PlayerVitals>();
		CollectPlayers( players );
		var target = ResolveArgument( who, players, FindLocal() );
		if ( target is null )
			Log.Warning( $"[DevPlayers] {command}: no player '{who}' — a number or part of a name from player_list." );
		return target;
	}

	[ConCmd( "player_list" )]
	public static void ConCmdList()
	{
		var players = new List<PlayerVitals>();
		CollectPlayers( players );
		var local = FindLocal();
		Log.Info( $"[DevPlayers] {players.Count} player(s):" );
		for ( var i = 0; i < players.Count; i++ )
		{
			var player = players[i];
			var you = ReferenceEquals( player, local ) ? " (you)" : "";
			var meters = TerrainWorldUnits.EngineToMeters( player.WorldPosition );
			Log.Info( $"[DevPlayers]   {i + 1}  {NameOf( player, local )}{you}  at {meters.x:0} {meters.y:0} {meters.z:0} m  HP {player.CurrentHealth:0}/{player.CurrentHealthMax:0}" );
		}
	}

	[ConCmd( "player_bring" )]
	public static void ConCmdBring( string who = null )
	{
		if ( ResolveForCommand( "player_bring", who ) is { } target )
			Log.Info( $"[DevPlayers] {BringToMe( target )}" );
	}

	[ConCmd( "player_goto" )]
	public static void ConCmdGoTo( string who = null )
	{
		if ( ResolveForCommand( "player_goto", who ) is { } target )
			Log.Info( $"[DevPlayers] {GoTo( target )}" );
	}

	/// <summary>No argument respawns this machine's own player.</summary>
	[ConCmd( "player_respawn" )]
	public static void ConCmdRespawn( string who = null )
	{
		var target = string.IsNullOrWhiteSpace( who ) ? FindLocal() : ResolveForCommand( "player_respawn", who );
		if ( target is not null )
			Log.Info( $"[DevPlayers] {Respawn( target )}" );
		else if ( string.IsNullOrWhiteSpace( who ) )
			Log.Warning( "[DevPlayers] player_respawn: you have no pawn in the world." );
	}
}
