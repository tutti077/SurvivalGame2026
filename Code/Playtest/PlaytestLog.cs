#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Game;
using Sandbox;

namespace Survival;

/// <summary>
/// The playtest logger (Mark 2026-11-09): host-side behaviour telemetry for the friends playtests, so the
/// session can be analysed from what players DID rather than what they said. One JSON object per line
/// (<c>.jsonl</c>) under <c>FileSystem.Data/2Tgames/playtest/</c>; <c>Tools/playtest_report.py</c> turns a
/// file into the per-player / per-weapon tables.
/// <para>
/// Off by default. <c>playtest_log 1</c> on the host starts a new file named
/// <c>&lt;host steam name&gt;_&lt;yyyyMMdd_HHmmss&gt;_v&lt;build&gt;.jsonl</c>, <c>playtest_log 0</c> closes it,
/// <c>playtest_mark &lt;text&gt;</c> drops a marker line ("feedback break", "toys phase"). Every hook is one
/// static call behind <see cref="Enabled"/> on a path that already runs once per real event — never per
/// frame except the grapple speed accumulator, which only touches pawns whose rope is attached.
/// </para>
/// <para>
/// Events: <c>session</c>, <c>player_join</c>, <c>vitals</c> (2 s sample per pawn), <c>stamina_empty</c> /
/// <c>stamina_recovered</c>, <c>hit</c> (player or entity hurt an entity), <c>player_damaged</c>,
/// <c>player_death</c>, <c>kill</c> (entity died: time-to-kill, hits, damage traded, killer's pools),
/// <c>block</c>, <c>grapple_attach</c> / <c>grapple_detach</c> / <c>grapple_reject</c>, <c>craft</c>,
/// <c>build</c>, <c>gain</c>, <c>player_leave</c>, <c>client_perf</c> (each client's average and 1 % low fps, menu seconds and
/// resolution every 30 s; the <c>vitals</c> sample carries the pawn position in meters), <c>mark</c>. Hooks: <see cref="VitalsAuthority.TryApplyDeltas"/>
/// (player pools), <see cref="EntityVitals.ApplyDamage"/>, <see cref="PlayerCombat"/> (block outcome),
/// <see cref="PlayerMovement"/> (grapple attach / detach), <see cref="PlayerCrafting"/>, <see cref="BuildAuthority"/>,
/// <see cref="PlayerInventory"/>. Damage sources that are not the equipped melee weapon wrap their
/// <c>TakeDamage</c> in <see cref="KindScope"/> ("arrow", "punch", "burn", "bite" …).
/// </para>
/// </summary>
public static class PlaytestLog
{
	public const string Folder = QuestSaveStore.RootFolder + "/playtest";

	/// <summary>Seconds between <c>vitals</c> samples per pawn.</summary>
	const double SampleIntervalSeconds = 2.0;

	/// <summary>Seconds between file flushes (also flushed when the buffer passes <see cref="FlushLines"/> lines).</summary>
	const double FlushIntervalSeconds = 3.0;
	const int FlushLines = 128;

	/// <summary>Stamina at or under this counts as empty (the pool is 0..100).</summary>
	const float StaminaEmptyThreshold = 0.5f;

	/// <summary>A fight nobody touched for this long is forgotten (the entity leashed or despawned).</summary>
	const double FightIdleSeconds = 120.0;

	/// <summary>A death this long after the last damage is reported with cause "unknown" (fall / drown / starve without a source).</summary>
	const double DeathCauseWindowSeconds = 10.0;

	public static bool Enabled { get; private set; }
	public static string CurrentFilePath { get; private set; }

	static readonly StringBuilder _buffer = new();
	static int _bufferedLines;
	static double _startedAt;
	static double _nextFlushAt;
	static double _nextSampleAt;
	static double _nextPruneAt;
	static int _eventCount;

	static readonly Dictionary<Guid, PlayerState> _players = new();
	static readonly Dictionary<Guid, Fight> _fights = new();
	static readonly List<PlayerState> _attached = new();

	// Ambient tags set by a damage source around its TakeDamage call (single-threaded game loop).
	static string _ambientKind;
	static string _ambientGainSource;

	/// <summary>Seconds per client perf / menu-time report window.</summary>
	const double ClientReportSeconds = 30.0;

	// Client side (every peer, the host's own screen included): on while the host logs.
	static bool _clientReporting;
	static double _clientWindowStart;
	static double _nextLocalPawnRefreshAt;
	static readonly List<float> _frameTimes = new( 4096 );
	static float _frameTimeSum;
	static float _menuSeconds;
	static GameObject _localPawn;
	static PlayerGameMenuController _localMenu;
	static ToolBuildHammer _localHammer;
	static ToolHoe _localHoe;
	static double _nextReportingBroadcastAt;

	sealed class PlayerState
	{
		public Guid RootId;
		public string Label;
		public double AliveSince;
		public string LastDamageSource = "unknown";
		public double LastDamageAt = -1;
		public double LastDeathAt = -1;
		public bool StaminaEmpty;
		public double StaminaEmptyAt;
		public double LastSampleAt = -1;

		public bool Attached;
		public double AttachAt;
		public double LastAttachAt = -1;
		public double LastDetachAt = -1;
		public Vector3 AttachPos;
		public Vector3 LastPos;
		public float MaxSpeedUps;
		public float SumSpeedUps;
		public int SpeedSamples;
		public float DistUnits;
		public float MaxZ;
	}

	sealed class Fight
	{
		public string VictimLabel;
		public double FirstHitAny;
		public double LastActivity;
		public int HitsAny;
		public readonly Dictionary<Guid, Hitter> ByAttacker = new();
	}

	sealed class Hitter
	{
		public double FirstHit;
		public int Hits;
		public float Dealt;
		public float Taken;
	}

	// ------------------------------------------------------------------ console

	/// <summary>Usage: <c>playtest_log 1</c> starts a new file, <c>playtest_log 0</c> closes it, no argument prints the state.</summary>
	[ConCmd( "playtest_log" )]
	public static void ConCmdPlaytestLog( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( Enabled
				? $"[Playtest] logging ON → {CurrentFilePath} ({_eventCount} events)"
				: "[Playtest] logging OFF (usage: playtest_log 1|0)" );
			return;
		}

		var on = enabled.Trim() is "1" or "true" or "on";
		if ( on )
			Start();
		else
			Stop();
	}

	/// <summary>Usage: <c>playtest_mark toys phase</c> — a marker line in the log.</summary>
	[ConCmd( "playtest_mark" )]
	public static void ConCmdPlaytestMark( string text )
	{
		if ( !Enabled )
		{
			Log.Warning( "[Playtest] not logging — playtest_log 1 first." );
			return;
		}

		Emit( Ev( "mark" ).S( "text", text ?? string.Empty ) );
		Log.Info( $"[Playtest] mark: {text}" );
	}

	static bool IsHostContext => !Networking.IsActive || Networking.IsHost;

	public static void Start()
	{
		if ( !IsHostContext )
		{
			Log.Warning( "[Playtest] only the host logs — run playtest_log 1 on the host." );
			return;
		}

		if ( Enabled )
			Stop();

		_players.Clear();
		_fights.Clear();
		_attached.Clear();
		_buffer.Clear();
		_bufferedLines = 0;
		_eventCount = 0;
		_startedAt = RealTime.GlobalNow;
		_nextFlushAt = _startedAt + FlushIntervalSeconds;
		_nextSampleAt = _startedAt;
		_nextPruneAt = _startedAt + FightIdleSeconds;

		var scene = Sandbox.Game.ActiveScene;
		var sceneName = scene.IsValid() ? scene.Name : "?";
		CurrentFilePath = $"{Folder}/{HostNameForFile()}_{DateTime.Now:yyyyMMdd_HHmmss}_v{GameBuildLabel.Display}.jsonl";
		Enabled = true;

		try
		{
			FileSystem.Data.CreateDirectory( Folder );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Playtest] could not create {Folder}: {ex.Message}" );
		}

		Emit( Ev( "session" )
			.S( "scene", sceneName )
			.S( "build", GameBuildLabel.Display )
			.S( "startedAt", DateTime.Now.ToString( "o", CultureInfo.InvariantCulture ) )
			.S( "host", ConnectionIdentity.Format( Connection.Local ) )
			.B( "networked", Networking.IsActive ) );

		Log.Info( $"[Playtest] logging ON → {CurrentFilePath}" );
		_nextReportingBroadcastAt = _startedAt + ClientReportSeconds;
		RpcSetClientReporting( true );
	}

	public static void Stop()
	{
		if ( !Enabled )
			return;

		foreach ( var p in _attached.ToArray() )
			EmitGrappleDetach( p, "log_stop" );

		Emit( Ev( "session_end" ).I( "events", _eventCount ) );
		Flush();
		Enabled = false;
		RpcSetClientReporting( false );
		Log.Info( $"[Playtest] logging OFF — {_eventCount} events in {CurrentFilePath}" );
	}

	// ------------------------------------------------------------------ tick (PlaytestLogSystem)

	internal static void HostTick( Scene scene )
	{
		if ( !Enabled || !IsHostContext || !scene.IsValid() )
			return;

		var now = RealTime.GlobalNow;

		// Grapple travel accumulators: only pawns with the rope attached.
		if ( _attached.Count > 0 )
		{
			var dt = Time.Delta;
			for ( var i = _attached.Count - 1; i >= 0; i-- )
			{
				var p = _attached[i];
				var root = scene.Directory.FindByGuid( p.RootId );
				if ( root is null || !root.IsValid() )
				{
					p.Attached = false;
					_attached.RemoveAt( i );
					continue;
				}

				var pos = root.WorldPosition;
				var step = Vector3.DistanceBetween( pos, p.LastPos );
				p.LastPos = pos;
				p.DistUnits += step;
				p.MaxZ = Math.Max( p.MaxZ, pos.z );
				if ( dt > 1e-4f )
				{
					var speed = step / dt;
					p.MaxSpeedUps = Math.Max( p.MaxSpeedUps, speed );
					p.SumSpeedUps += speed;
					p.SpeedSamples++;
				}
			}
		}

		if ( now >= _nextSampleAt )
		{
			_nextSampleAt = now + SampleIntervalSeconds;
			foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
			{
				if ( vitals is null || !vitals.GameObject.IsValid() || !vitals.Enabled )
					continue;

				var state = StateFor( vitals.GameObject );
				if ( state is null )
					continue;

				state.LastSampleAt = now;
				var pos = vitals.GameObject.WorldPosition;
				Emit( Ev( "vitals" )
					.S( "player", state.Label )
					.F( "hp", vitals.CurrentHealth ).F( "hpMax", vitals.CurrentHealthMax )
					.F( "st", vitals.CurrentStamina ).F( "stMax", vitals.CurrentStaminaMax )
					.F( "x", TerrainWorldUnits.EngineToMeters( pos.x ) ).F( "y", TerrainWorldUnits.EngineToMeters( pos.y ) ).F( "z", TerrainWorldUnits.EngineToMeters( pos.z ) )
					.S( "flags", Flags( vitals.GameObject ) ) );
			}

			// A pawn that is gone from the scene left the game (or was destroyed): one player_leave, then forget it.
			List<PlayerState> gone = null;
			foreach ( var state in _players.Values )
			{
				if ( state.LastSampleAt >= now - 1e-3 )
					continue;

				var root = scene.Directory.FindByGuid( state.RootId );
				if ( root is null || !root.IsValid() )
					( gone ??= new List<PlayerState>() ).Add( state );
			}

			if ( gone is not null )
			{
				foreach ( var state in gone )
				{
					if ( state.Attached )
						EmitGrappleDetach( state, "left" );
					Emit( Ev( "player_leave" )
						.S( "player", state.Label )
						.F( "sinceDeathS", state.LastDeathAt >= 0 ? (float)( now - state.LastDeathAt ) : -1f ) );
					_players.Remove( state.RootId );
				}
			}
		}

		// Late joiners never saw the Start broadcast: repeat the "report your frame rate" flag on the report cadence.
		if ( now >= _nextReportingBroadcastAt )
		{
			_nextReportingBroadcastAt = now + ClientReportSeconds;
			if ( Networking.IsActive )
				RpcSetClientReporting( true );
		}

		if ( now >= _nextPruneAt )
		{
			_nextPruneAt = now + FightIdleSeconds;
			List<Guid> stale = null;
			foreach ( var kv in _fights )
			{
				if ( now - kv.Value.LastActivity > FightIdleSeconds )
					( stale ??= new List<Guid>() ).Add( kv.Key );
			}

			if ( stale is not null )
				foreach ( var id in stale )
					_fights.Remove( id );
		}

		if ( now >= _nextFlushAt || _bufferedLines >= FlushLines )
			Flush();
	}

	// ------------------------------------------------------------------ hooks: player pools

	/// <summary>Host (<see cref="VitalsAuthority.TryApplyDeltas"/>): health left the pool.</summary>
	internal static void HostPlayerDamaged( PlayerVitals vitals, Component source, float removed, float hpAfter, float hpMax )
	{
		if ( !Enabled || vitals is null || !vitals.GameObject.IsValid() )
			return;

		var state = StateFor( vitals.GameObject );
		if ( state is null )
			return;

		var now = RealTime.GlobalNow;
		var src = Describe( source );
		state.LastDamageSource = src.Label;
		state.LastDamageAt = now;

		var ev = Ev( "player_damaged" )
			.S( "player", state.Label )
			.S( "src", src.Label ).S( "srcKind", src.Kind )
			.S( "kind", ResolveKind( src ) )
			.F( "dmg", removed ).F( "hp", hpAfter ).F( "hpMax", hpMax );
		if ( src.Kind == "player" )
			ev.S( "weapon", WeaponOf( src.Root ) );
		Emit( ev );

		// Damage traded inside a fight this player is in with that entity.
		if ( src.Kind == "entity" && _fights.TryGetValue( src.Id, out var fight )
		     && fight.ByAttacker.TryGetValue( state.RootId, out var hitter ) )
		{
			hitter.Taken += removed;
			fight.LastActivity = now;
		}
	}

	/// <summary>Host (<see cref="VitalsAuthority.TryApplyDeltas"/>): stamina moved — only pool-empty edges are written.</summary>
	internal static void HostStaminaChanged( PlayerVitals vitals, float before, float after )
	{
		if ( !Enabled || vitals is null || !vitals.GameObject.IsValid() )
			return;

		var emptyNow = after <= StaminaEmptyThreshold;
		var emptyBefore = before <= StaminaEmptyThreshold;
		if ( emptyNow == emptyBefore )
			return;

		var state = StateFor( vitals.GameObject );
		if ( state is null )
			return;

		var now = RealTime.GlobalNow;
		if ( emptyNow && !state.StaminaEmpty )
		{
			state.StaminaEmpty = true;
			state.StaminaEmptyAt = now;
			Emit( Ev( "stamina_empty" ).S( "player", state.Label ).S( "flags", Flags( vitals.GameObject ) ) );
		}
		else if ( !emptyNow && state.StaminaEmpty )
		{
			state.StaminaEmpty = false;
			Emit( Ev( "stamina_recovered" ).S( "player", state.Label ).F( "emptyS", (float)( now - state.StaminaEmptyAt ) ) );
		}
	}

	/// <summary>Host (<see cref="VitalsAuthority.TryApplyDeltas"/>): the pool just hit zero.</summary>
	internal static void HostPlayerDied( PlayerVitals vitals )
	{
		if ( !Enabled || vitals is null || !vitals.GameObject.IsValid() )
			return;

		var state = StateFor( vitals.GameObject );
		if ( state is null )
			return;

		var now = RealTime.GlobalNow;
		var recent = state.LastDamageAt >= 0 && now - state.LastDamageAt <= DeathCauseWindowSeconds;
		Emit( Ev( "player_death" )
			.S( "player", state.Label )
			.S( "cause", recent ? state.LastDamageSource : "unknown" )
			.F( "sinceDmgS", state.LastDamageAt >= 0 ? (float)( now - state.LastDamageAt ) : -1f )
			.F( "aliveS", (float)( now - state.AliveSince ) )
			.I( "deaths", vitals.DeathCount + 1 ) );
		state.AliveSince = now;
		state.LastDeathAt = now;
		state.StaminaEmpty = false;
	}

	// ------------------------------------------------------------------ hooks: entities

	/// <summary>Host (<see cref="EntityVitals.ApplyDamage"/>): an entity lost health.</summary>
	internal static void HostEntityDamaged( EntityVitals victim, Component attacker, float dealt )
	{
		if ( !Enabled || victim is null || !victim.GameObject.IsValid() )
			return;

		var now = RealTime.GlobalNow;
		var atk = Describe( attacker );
		var victimLabel = EntityLabel( victim.GameObject );
		var victimId = victim.GameObject.Id;

		if ( !_fights.TryGetValue( victimId, out var fight ) )
		{
			fight = new Fight { VictimLabel = victimLabel, FirstHitAny = now };
			_fights[victimId] = fight;
		}

		fight.LastActivity = now;
		fight.HitsAny++;
		if ( atk.Id != Guid.Empty )
		{
			if ( !fight.ByAttacker.TryGetValue( atk.Id, out var hitter ) )
			{
				hitter = new Hitter { FirstHit = now };
				fight.ByAttacker[atk.Id] = hitter;
			}

			hitter.Hits++;
			hitter.Dealt += dealt;
		}

		var ev = Ev( "hit" )
			.S( "atk", atk.Label ).S( "atkKind", atk.Kind )
			.S( "tgt", victimLabel ).S( "tgtId", ShortId( victimId ) )
			.S( "weapon", atk.Kind == "player" ? WeaponOf( atk.Root ) : atk.Label )
			.S( "kind", ResolveKind( atk ) );
		AppendSwingType( ev, atk );
		ev.F( "dmg", dealt ).F( "hp", victim.CurrentHealth ).F( "hpMax", victim.CurrentHealthMax );
		AppendAttackerPools( ev, atk );
		Emit( ev );
	}

	/// <summary>Host (<see cref="EntityVitals.ApplyDamage"/>): the pool emptied for good.</summary>
	internal static void HostEntityKilled( EntityVitals victim, Component attacker )
	{
		if ( !Enabled || victim is null || !victim.GameObject.IsValid() )
			return;

		var now = RealTime.GlobalNow;
		var atk = Describe( attacker );
		var victimId = victim.GameObject.Id;
		_fights.TryGetValue( victimId, out var fight );
		Hitter hitter = null;
		if ( fight is not null && atk.Id != Guid.Empty )
			fight.ByAttacker.TryGetValue( atk.Id, out hitter );

		var ev = Ev( "kill" )
			.S( "killer", atk.Label ).S( "killerKind", atk.Kind )
			.S( "victim", EntityLabel( victim.GameObject ) ).S( "victimId", ShortId( victimId ) )
			.S( "weapon", atk.Kind == "player" ? WeaponOf( atk.Root ) : atk.Label )
			.S( "kind", ResolveKind( atk ) )
			.F( "ttkS", hitter is not null ? (float)( now - hitter.FirstHit ) : -1f )
			.F( "ttkAnyS", fight is not null ? (float)( now - fight.FirstHitAny ) : -1f )
			.I( "hits", hitter?.Hits ?? 0 ).I( "hitsAny", fight?.HitsAny ?? 0 )
			.F( "dealt", hitter?.Dealt ?? 0f ).F( "taken", hitter?.Taken ?? 0f )
			.I( "helpers", fight is not null ? Math.Max( 0, fight.ByAttacker.Count - 1 ) : 0 )
			.F( "hpMaxVictim", victim.CurrentHealthMax );
		AppendAttackerPools( ev, atk );
		Emit( ev );
		_fights.Remove( victimId );
	}

	// ------------------------------------------------------------------ hooks: block / grapple / economy

	/// <summary>Host (<see cref="PlayerCombat"/> block outcome): the defender's guard took a swing.</summary>
	internal static void HostBlocked( PlayerCombat defender, Component attacker, in MeleeBlockOutcome outcome )
	{
		if ( !Enabled || defender is null || !defender.GameObject.IsValid() )
			return;

		var def = Describe( defender );
		var atk = Describe( attacker );
		var ev = Ev( "block" )
			.S( "def", def.Label ).S( "defKind", def.Kind )
			.S( "atk", atk.Label ).S( "atkKind", atk.Kind )
			.S( "weapon", atk.Kind == "player" ? WeaponOf( atk.Root ) : atk.Label )
			.S( "outcome", outcome.OutcomeId ?? string.Empty )
			.B( "parry", outcome.WasPerfectParry )
			.F( "hpDmg", outcome.HealthDamage ).F( "stCost", outcome.StaminaCost );
		AppendSwingType( ev, atk );
		Emit( ev );
	}

	/// <summary>Host (<see cref="PlayerMovement"/> ServerTryAttach): the rope took hold.</summary>
	internal static void HostGrappleAttached( PlayerMovement movement, string kind, float ropeLengthEngine )
	{
		if ( !Enabled || movement is null || !movement.GameObject.IsValid() )
			return;

		var state = StateFor( movement.GameObject );
		if ( state is null )
			return;

		var now = RealTime.GlobalNow;
		var root = movement.GameObject;
		var ev = Ev( "grapple_attach" )
			.S( "player", state.Label ).S( "kind", kind )
			.F( "lenM", TerrainWorldUnits.EngineToMeters( ropeLengthEngine ) )
			.F( "sinceAttachS", state.LastAttachAt >= 0 ? (float)( now - state.LastAttachAt ) : -1f )
			.F( "sinceDetachS", state.LastDetachAt >= 0 ? (float)( now - state.LastDetachAt ) : -1f )
			.B( "airborne", !( root.Components.Get<PlayerController>()?.IsOnGround ?? true ) )
			.F( "st", root.Components.Get<PlayerVitals>()?.CurrentStamina ?? -1f );
		Emit( ev );

		state.LastAttachAt = now;
		state.AttachAt = now;
		state.AttachPos = root.WorldPosition;
		state.LastPos = state.AttachPos;
		state.MaxZ = state.AttachPos.z;
		state.MaxSpeedUps = 0f;
		state.SumSpeedUps = 0f;
		state.SpeedSamples = 0;
		state.DistUnits = 0f;
		if ( !state.Attached )
		{
			state.Attached = true;
			_attached.Add( state );
		}
	}

	/// <summary>Host (<see cref="PlayerMovement"/> ServerDetach): the rope let go.</summary>
	internal static void HostGrappleDetached( PlayerMovement movement, string reason )
	{
		if ( !Enabled || movement is null || !movement.GameObject.IsValid() )
			return;

		var state = StateFor( movement.GameObject );
		if ( state is null || !state.Attached )
			return;

		EmitGrappleDetach( state, reason );
	}

	/// <summary>Host (<see cref="PlayerMovement"/> ServerTryAttach): the host refused a shot.</summary>
	internal static void HostGrappleRejected( PlayerMovement movement, string why )
	{
		if ( !Enabled || movement is null || !movement.GameObject.IsValid() )
			return;

		var state = StateFor( movement.GameObject );
		if ( state is null )
			return;

		Emit( Ev( "grapple_reject" ).S( "player", state.Label ).S( "why", why ) );
	}

	static void EmitGrappleDetach( PlayerState state, string reason )
	{
		var now = RealTime.GlobalNow;
		var dur = (float)( now - state.AttachAt );
		var avg = state.SpeedSamples > 0 ? state.SumSpeedUps / state.SpeedSamples : 0f;
		Emit( Ev( "grapple_detach" )
			.S( "player", state.Label ).S( "reason", reason )
			.F( "durS", dur )
			.F( "maxMps", TerrainWorldUnits.EngineToMeters( state.MaxSpeedUps ) )
			.F( "avgMps", TerrainWorldUnits.EngineToMeters( avg ) )
			.F( "distM", TerrainWorldUnits.EngineToMeters( state.DistUnits ) )
			.F( "netM", TerrainWorldUnits.EngineToMeters( Vector3.DistanceBetween( state.LastPos, state.AttachPos ) ) )
			.F( "riseM", TerrainWorldUnits.EngineToMeters( state.MaxZ - state.AttachPos.z ) ) );
		state.Attached = false;
		state.LastDetachAt = now;
		_attached.Remove( state );
	}

	/// <summary>Host (<see cref="PlayerCrafting.HostTryCraft"/>): a recipe produced its output.</summary>
	internal static void HostCrafted( GameObject playerRoot, string recipeId, int amount )
	{
		if ( !Enabled )
			return;

		var state = StateFor( playerRoot );
		if ( state is null )
			return;

		Emit( Ev( "craft" ).S( "player", state.Label ).S( "recipe", recipeId ).I( "amount", amount ) );
	}

	/// <summary>Host (<see cref="BuildAuthority.TryPlacePiece"/>): a piece stood.</summary>
	internal static void HostBuilt( GameObject playerRoot, string pieceId )
	{
		if ( !Enabled )
			return;

		var state = StateFor( playerRoot );
		if ( state is null )
			return;

		Emit( Ev( "build" ).S( "player", state.Label ).S( "piece", pieceId ) );
	}

	/// <summary>Host (<see cref="PlayerInventory.HostTryAddResource"/>): items entered a player's inventory.</summary>
	internal static void HostGained( GameObject playerRoot, string resourceId, int amount )
	{
		if ( !Enabled )
			return;

		var state = StateFor( playerRoot );
		if ( state is null )
			return;

		Emit( Ev( "gain" ).S( "player", state.Label ).S( "item", resourceId ).I( "amount", amount ).S( "src", _ambientGainSource ?? "pickup" ) );
	}

	// ------------------------------------------------------------------ client telemetry (every peer)

	/// <summary>
	/// Runs on every machine from <see cref="PlaytestLogSystem"/>. While the host logs, each client (the host's own
	/// screen too) accumulates frame times and menu-open seconds and reports them every <see cref="ClientReportSeconds"/>:
	/// average fps, 1 % low fps, seconds in a menu, resolution. The host writes them as <c>client_perf</c> lines.
	/// </summary>
	internal static void ClientTick( Scene scene )
	{
		if ( !_clientReporting || !scene.IsValid() )
			return;

		var now = RealTime.GlobalNow;
		var dt = Math.Clamp( Time.Delta, 0f, 1f );
		if ( dt > 0f )
		{
			if ( _frameTimes.Count < 8192 )
				_frameTimes.Add( dt );
			_frameTimeSum += dt;
		}

		if ( now >= _nextLocalPawnRefreshAt )
		{
			_nextLocalPawnRefreshAt = now + 2.0;
			_localPawn = FindLocalPawn( scene );
			_localMenu = _localPawn?.Components.Get<PlayerGameMenuController>();
			_localHammer = _localPawn?.Components.Get<ToolBuildHammer>( FindMode.EverythingInSelfAndDescendants );
			_localHoe = _localPawn?.Components.Get<ToolHoe>( FindMode.EverythingInSelfAndDescendants );
		}

		var inMenu = InventoryScreenPointer.SoftCursorActive
		             || ( _localMenu.IsValid() && _localMenu.IsMenuOpen )
		             || ( _localHammer.IsValid() && _localHammer.IsBuildMenuOpen )
		             || ( _localHoe.IsValid() && _localHoe.IsMenuOpen );
		if ( inMenu )
			_menuSeconds += dt;

		var window = now - _clientWindowStart;
		if ( window < ClientReportSeconds || _frameTimes.Count == 0 )
			return;

		var avgFps = _frameTimeSum > 1e-5f ? _frameTimes.Count / _frameTimeSum : 0f;
		_frameTimes.Sort();
		var p99 = _frameTimes[Math.Clamp( (int)( _frameTimes.Count * 0.99 ), 0, _frameTimes.Count - 1 )];
		var lowFps = p99 > 1e-5f ? 1f / p99 : 0f;
		var size = Screen.Size;
		RpcClientPerf( avgFps, lowFps, _menuSeconds, (float)window, (int)size.x, (int)size.y );
		ResetClientWindow( now );
	}

	static void ResetClientWindow( double now )
	{
		_clientWindowStart = now;
		_frameTimes.Clear();
		_frameTimeSum = 0f;
		_menuSeconds = 0f;
	}

	static GameObject FindLocalPawn( Scene scene )
	{
		GameObject fallback = null;
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			var root = vitals.GameObject;
			if ( root is null || !root.IsValid() || root.Name.StartsWith( "CombatTest", StringComparison.Ordinal ) )
				continue;

			if ( root.Network is { Active: true } )
			{
				if ( root.Network.IsOwner )
					return root;
			}
			else
				fallback ??= root;
		}

		return fallback;
	}

	/// <summary>Host → every peer: start / stop the client-side frame-time and menu-time accumulators.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	public static void RpcSetClientReporting( bool on )
	{
		if ( on == _clientReporting )
			return;

		_clientReporting = on;
		ResetClientWindow( RealTime.GlobalNow );
		_nextLocalPawnRefreshAt = 0;
	}

	/// <summary>Client → host: one report window of frame times and menu time; the host writes a <c>client_perf</c> line for the caller's pawn.</summary>
	[Rpc.Host]
	public static void RpcClientPerf( float avgFps, float lowFps, float menuSeconds, float windowSeconds, int width, int height )
	{
		if ( !Enabled || !IsHostContext )
			return;

		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var caller = Rpc.Caller;
		GameObject root = null;
		if ( caller is not null && Networking.IsActive )
		{
			foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
			{
				var owner = vitals.GameObject.Network?.Owner;
				if ( owner is not null && ConnectionIdentity.SameClient( owner, caller ) )
				{
					root = vitals.GameObject;
					break;
				}
			}
		}

		root ??= FindLocalPawn( scene );
		var state = StateFor( root );
		Emit( Ev( "client_perf" )
			.S( "player", state?.Label ?? ( caller is not null ? ConnectionIdentity.Format( caller ) : "host" ) )
			.F( "avgFps", avgFps ).F( "lowFps", lowFps )
			.F( "menuS", menuSeconds ).F( "windowS", windowSeconds )
			.S( "res", $"{width}x{height}" ) );
	}

	// ------------------------------------------------------------------ ambient scopes

	/// <summary>
	/// Tags the damage dealt inside the scope ("arrow", "punch", "burn", "bite", "laser", "deadshot"). Without a scope a
	/// player's damage is "melee" / "heavy" from the swing in flight.
	/// </summary>
	public static KindScope Kind( string kind ) => new( kind );

	/// <summary>Tags inventory gains inside the scope ("craft"); without it a gain is a "pickup".</summary>
	public static GainScope Gain( string source ) => new( source );

	public readonly struct KindScope : IDisposable
	{
		readonly string _previous;
		public KindScope( string kind ) { _previous = _ambientKind; _ambientKind = kind; }
		public void Dispose() => _ambientKind = _previous;
	}

	public readonly struct GainScope : IDisposable
	{
		readonly string _previous;
		public GainScope( string source ) { _previous = _ambientGainSource; _ambientGainSource = source; }
		public void Dispose() => _ambientGainSource = _previous;
	}

	// ------------------------------------------------------------------ actors

	readonly struct Actor
	{
		public readonly string Label;
		public readonly string Kind; // player | entity | env
		public readonly Guid Id;
		public readonly GameObject Root;

		public Actor( string label, string kind, Guid id, GameObject root )
		{
			Label = label;
			Kind = kind;
			Id = id;
			Root = root;
		}
	}

	static readonly Actor Environment = new( "env", "env", Guid.Empty, null );

	static Actor Describe( Component c )
	{
		if ( c is null || !c.IsValid() || c.GameObject is null || !c.GameObject.IsValid() )
			return Environment;

		for ( var go = c.GameObject; go is not null && go.IsValid(); go = go.Parent )
		{
			if ( go.Components.Get<PlayerVitals>() is not null )
			{
				var state = StateFor( go );
				return new Actor( state?.Label ?? go.Name, "player", go.Id, go );
			}

			if ( go.Components.Get<EntityVitals>() is not null )
				return new Actor( EntityLabel( go ), "entity", go.Id, go );
		}

		return new Actor( c.GameObject.Name, "env", Guid.Empty, null );
	}

	static string EntityLabel( GameObject root )
	{
		if ( root.Components.Get<AnimalBrain>() is { } animal )
			return animal.Species.ToString();

		var vitals = root.Components.Get<EntityVitals>();
		if ( vitals is null )
			return root.Name;

		var name = vitals.GetDisplayName();
		return vitals.Tier > 0 && string.IsNullOrWhiteSpace( vitals.DisplayNameOverride ) ? $"{name} T{vitals.Tier}" : name;
	}

	static string WeaponOf( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return "?";

		var id = playerRoot.Components.Get<PlayerEquipment>()?.NetworkedMainHandResourceId;
		return string.IsNullOrWhiteSpace( id ) ? "fists" : id;
	}

	/// <summary>The ambient scope wins; otherwise the swing in flight says melee / heavy; otherwise "melee" for a pawn, the kind of actor for anything else.</summary>
	static string ResolveKind( in Actor atk )
	{
		if ( _ambientKind is not null )
			return _ambientKind;

		if ( atk.Root is not null && atk.Root.Components.Get<PlayerCombat>() is { } combat
		     && combat.TryGetServerSwingInfo( out var heavy, out _ ) )
			return heavy ? "heavy" : "melee";

		return atk.Kind == "env" ? "env" : "melee";
	}

	static void AppendSwingType( Event ev, in Actor atk )
	{
		if ( _ambientKind is not null || atk.Root is null )
			return;

		if ( atk.Root.Components.Get<PlayerCombat>() is { } combat && combat.TryGetServerSwingInfo( out _, out var type ) )
			ev.S( "swing", MeleeAttackTypes.Label( type ).Replace( "Attack", string.Empty ) );
	}

	static void AppendAttackerPools( Event ev, in Actor atk )
	{
		if ( atk.Kind != "player" || atk.Root is null )
			return;

		var v = atk.Root.Components.Get<PlayerVitals>();
		if ( v is null )
			return;

		ev.F( "atkHp", v.CurrentHealth ).F( "atkHpMax", v.CurrentHealthMax ).F( "atkSt", v.CurrentStamina ).F( "atkStMax", v.CurrentStaminaMax );
	}

	static string Flags( GameObject root )
	{
		var sb = new StringBuilder( 32 );
		var move = root.Components.Get<PlayerMovement>();
		if ( move is not null )
		{
			if ( move.GrappleAttached ) Add( sb, "grapple" );
			if ( move.IsWingsuitDeployed ) Add( sb, "wingsuit" );
			if ( move.IsSprintingForNoise() ) Add( sb, "sprint" );
			if ( root.Components.Get<PlayerController>() is { IsOnGround: false } ) Add( sb, "air" );
		}

		var combat = root.Components.Get<PlayerCombat>();
		if ( combat is not null )
		{
			if ( combat.IsAuthoritativeMeleeBlocking ) Add( sb, "block" );
			if ( combat.TryGetServerSwingInfo( out var heavy, out _ ) ) Add( sb, heavy ? "heavy" : "swing" );
		}

		return sb.ToString();

		static void Add( StringBuilder b, string flag )
		{
			if ( b.Length > 0 ) b.Append( ',' );
			b.Append( flag );
		}
	}

	static PlayerState StateFor( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return null;

		if ( _players.TryGetValue( playerRoot.Id, out var state ) )
			return state;

		var owner = playerRoot.Network?.Owner;
		var label = owner is not null && !string.IsNullOrWhiteSpace( owner.DisplayName ) ? owner.DisplayName : playerRoot.Name;
		// Two pawns with the same name stay distinct in the file.
		foreach ( var other in _players.Values )
		{
			if ( string.Equals( other.Label, label, StringComparison.Ordinal ) )
			{
				label = $"{label}#{ShortId( playerRoot.Id )}";
				break;
			}
		}

		state = new PlayerState { RootId = playerRoot.Id, Label = label, AliveSince = RealTime.GlobalNow };
		_players[playerRoot.Id] = state;
		Emit( Ev( "player_join" )
			.S( "player", label )
			.S( "steam", owner is not null ? owner.SteamId.ToString() : string.Empty )
			.S( "root", ShortId( playerRoot.Id ) ) );
		return state;
	}

	static string ShortId( Guid id ) => id.ToString( "N" ).Substring( 0, 8 );

	/// <summary>The host's Steam display name, reduced to filename-safe characters ("host" when there is none).</summary>
	static string HostNameForFile()
	{
		var name = Connection.Local?.DisplayName;
		if ( string.IsNullOrWhiteSpace( name ) )
			return "host";

		var sb = new StringBuilder( name.Length );
		foreach ( var ch in name.Trim() )
			sb.Append( char.IsLetterOrDigit( ch ) ? ch : '_' );

		var safe = sb.ToString().Trim( '_' );
		return string.IsNullOrEmpty( safe ) ? "host" : safe;
	}

	// ------------------------------------------------------------------ line writer

	sealed class Event
	{
		readonly StringBuilder _sb = new( 160 );

		public Event( string name, double t )
		{
			_sb.Append( "{\"t\":" ).Append( t.ToString( "0.###", CultureInfo.InvariantCulture ) )
				.Append( ",\"ev\":\"" ).Append( name ).Append( '"' );
		}

		public Event S( string key, string value )
		{
			_sb.Append( ",\"" ).Append( key ).Append( "\":\"" );
			Escape( _sb, value ?? string.Empty );
			_sb.Append( '"' );
			return this;
		}

		public Event F( string key, float value )
		{
			_sb.Append( ",\"" ).Append( key ).Append( "\":" ).Append( value.ToString( "0.##", CultureInfo.InvariantCulture ) );
			return this;
		}

		public Event I( string key, int value )
		{
			_sb.Append( ",\"" ).Append( key ).Append( "\":" ).Append( value.ToString( CultureInfo.InvariantCulture ) );
			return this;
		}

		public Event B( string key, bool value )
		{
			_sb.Append( ",\"" ).Append( key ).Append( "\":" ).Append( value ? "true" : "false" );
			return this;
		}

		public void WriteTo( StringBuilder target ) => target.Append( _sb ).Append( "}\n" );

		static void Escape( StringBuilder sb, string s )
		{
			foreach ( var ch in s )
			{
				switch ( ch )
				{
					case '"': sb.Append( "\\\"" ); break;
					case '\\': sb.Append( "\\\\" ); break;
					case '\n': sb.Append( "\\n" ); break;
					case '\r': sb.Append( "\\r" ); break;
					case '\t': sb.Append( "\\t" ); break;
					default:
						if ( ch < ' ' ) sb.Append( "\\u" ).Append( ( (int)ch ).ToString( "x4", CultureInfo.InvariantCulture ) );
						else sb.Append( ch );
						break;
				}
			}
		}
	}

	static Event Ev( string name ) => new( name, RealTime.GlobalNow - _startedAt );

	static void Emit( Event ev )
	{
		ev.WriteTo( _buffer );
		_bufferedLines++;
		_eventCount++;
	}

	static void Flush()
	{
		_nextFlushAt = RealTime.GlobalNow + FlushIntervalSeconds;
		if ( _bufferedLines == 0 || string.IsNullOrEmpty( CurrentFilePath ) )
			return;

		try
		{
			using var stream = FileSystem.Data.OpenWrite( CurrentFilePath, FileMode.Append );
			var bytes = Encoding.UTF8.GetBytes( _buffer.ToString() );
			stream.Write( bytes, 0, bytes.Length );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Playtest] write failed ({CurrentFilePath}): {ex.Message}" );
		}

		_buffer.Clear();
		_bufferedLines = 0;
	}
}

/// <summary>Per-scene tick for <see cref="PlaytestLog"/> (grapple travel accumulators, 2 s vitals samples, flushes). Does nothing while logging is off.</summary>
public sealed class PlaytestLogSystem : GameObjectSystem
{
	public PlaytestLogSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 0, Tick, "PlaytestLog" );
	}

	void Tick()
	{
		PlaytestLog.HostTick( Scene );
		PlaytestLog.ClientTick( Scene );
	}
}
