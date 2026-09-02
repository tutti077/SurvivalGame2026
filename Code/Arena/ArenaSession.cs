using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

public enum ArenaPhase
{
	Idle,
	Countdown,
	Fighting,
	Finished,
}

/// <summary>
/// Host-authoritative arena battles, living on the Arena menu button object.
/// Crew leaders queue a mode (1v1 … 4v4v4); undersized crews are merged with other queued
/// crews/solos to fill teams. When every team fills, all participants teleport to the arena,
/// fight until one crew is left alive (dead players ghost-spectate), then everyone returns to
/// where they stood with full pools. Arena deaths drop no loot.
/// </summary>
[Title( "Arena Session" )]
public sealed class ArenaSession : Component
{
	public static ArenaSession Instance { get; private set; }

	/// <summary>Center-screen arena text (countdown beats, winner banner). Empty string clears.</summary>
	public static event Action<string> LocalArenaTextChanged;

	[Property, Group( "Arena" ), Title( "Arena Center (optional, defaults to this)" )]
	public GameObject ArenaCenter { get; set; }

	[Property, Group( "Arena" ), Title( "Arena Radius (m)" ), Range( 5f, 200f )]
	public float ArenaRadiusMeters { get; set; } = 30f;

	[Property, Group( "Arena" ), Title( "Team Spawn Points (optional, one per team)" )]
	public List<GameObject> TeamSpawnPoints { get; set; } = new();

	[Property, Group( "Arena" ), Title( "Return Point (optional, else pre-match spots)" )]
	public GameObject ReturnPoint { get; set; }

	[Property, Group( "Timing" ), Title( "Countdown Seconds Per Beat" ), Range( 0.5f, 2f )]
	public float CountdownBeatSeconds { get; set; } = 1f;

	[Property, Group( "Timing" ), Title( "Finished Hold Seconds" ), Range( 1f, 20f )]
	public float FinishedHoldSeconds { get; set; } = 6f;

	[Property, Group( "Debug" )] public bool LogArena { get; set; }

	[Sync( SyncFlags.FromHost )] public ArenaPhase Phase { get; private set; } = ArenaPhase.Idle;

	/// <summary>Lines of <c>crewKey|modeInt|playerCount</c> for every waiting queue entry (host → all).</summary>
	[Sync( SyncFlags.FromHost )] public string QueueBlob { get; private set; } = string.Empty;

	sealed class QueueEntry
	{
		public Guid CrewKey; // crew id, or player id for a solo queue
		public ArenaMode Mode;
	}

	sealed class Participant
	{
		public Guid PlayerId;
		public string Name = "";
		public GameObject Root;
		public int TeamIndex;
		public bool Dead;
		public Vector3 ReturnPos;
		public Rotation ReturnRot;
	}

	readonly List<QueueEntry> _queue = new();
	readonly List<Participant> _participants = new();
	readonly List<string> _teamNames = new();
	ArenaMode _activeMode = ArenaMode.OneVOne;
	double _countdownBeatAt;
	int _countdownValue;
	double _finishedUntil;
	double _nextFightTickAt;

	bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	Vector3 CenterWorldPosition =>
		ArenaCenter is { IsValid: true } c ? c.WorldPosition : GameObject.WorldPosition;

	float RadiusUnits => TerrainWorldUnits.MetersToEngine( Math.Max( 5f, ArenaRadiusMeters ) );

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
		CrewRegistry.MembersChanged += HostOnCrewMembersChanged;
	}

	protected override void OnDisabled()
	{
		CrewRegistry.MembersChanged -= HostOnCrewMembersChanged;
		if ( Instance == this )
			Instance = null;
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		if ( Instance == this )
			Instance = null;
		base.OnDestroy();
	}

	protected override void OnStart()
	{
		base.OnStart();
		// Object-mode so [Sync] queue state + broadcast RPCs reach joining clients.
		if ( Networking.IsHost )
			HostNetworkSpawn.TrySpawn( GameObject );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !HasHostAuthority )
			return;

		switch ( Phase )
		{
			case ArenaPhase.Countdown:
				TickCountdown();
				break;
			case ArenaPhase.Fighting:
				if ( Time.NowDouble >= _nextFightTickAt )
				{
					_nextFightTickAt = Time.NowDouble + 0.25;
					TickFighting();
				}

				break;
			case ArenaPhase.Finished:
				if ( Time.NowDouble >= _finishedUntil )
					HostFinishAndRestore();
				break;
		}
	}

	// ── Queue ───────────────────────────────────────────────────────────────────

	/// <summary>Leader (or solo player) queues their crew for a mode.</summary>
	public void HostTryQueue( GameObject playerRoot, ArenaMode mode )
	{
		if ( !HasHostAuthority || playerRoot is null || !playerRoot.IsValid() )
			return;

		var playerId = TimeTrialSession.ResolvePlayerKey( playerRoot );
		var crew = CrewRegistry.GetCrewOf( playerId );

		if ( crew is not null && crew.LeaderId != playerId )
		{
			if ( LogArena )
				Log.Info( "[Arena] Queue rejected — only the crew leader can queue." );
			return;
		}

		var crewSize = crew?.Members.Count ?? 1;
		if ( crewSize > ArenaModeInfo.TeamSize( mode ) )
		{
			if ( LogArena )
				Log.Info( $"[Arena] Queue rejected — crew of {crewSize} does not fit {ArenaModeInfo.Display( mode )}." );
			return;
		}

		var crewKey = crew?.Key ?? playerId;
		RemoveQueueEntries( crewKey );
		_queue.Add( new QueueEntry { CrewKey = crewKey, Mode = mode } );
		PushQueueBlob();
		if ( LogArena )
			Log.Info( $"[Arena] {CrewRegistry.ResolvePawnDisplayName( playerRoot )} queued {(crew?.Name ?? "solo")} for {ArenaModeInfo.Display( mode )}." );

		TryMatchmake();
	}

	/// <summary>Leader cancels their crew's waiting queue entry.</summary>
	public void HostTryCancelQueue( GameObject playerRoot )
	{
		if ( !HasHostAuthority || playerRoot is null || !playerRoot.IsValid() )
			return;

		var playerId = TimeTrialSession.ResolvePlayerKey( playerRoot );
		var crew = CrewRegistry.GetCrewOf( playerId );
		if ( crew is not null && crew.LeaderId != playerId )
			return;

		if ( RemoveQueueEntries( crew?.Key ?? playerId ) )
		{
			PushQueueBlob();
			if ( LogArena )
				Log.Info( $"[Arena] {CrewRegistry.ResolvePawnDisplayName( playerRoot )} cancelled the arena queue." );
		}
	}

	/// <summary>Crew membership changed (join/leave/disband) — its waiting queue entry is void.</summary>
	void HostOnCrewMembersChanged( Guid crewKey )
	{
		if ( !HasHostAuthority )
			return;

		if ( RemoveQueueEntries( crewKey ) )
		{
			PushQueueBlob();
			if ( LogArena )
				Log.Info( "[Arena] Queue entry cancelled — crew members changed while waiting." );
		}
	}

	bool RemoveQueueEntries( Guid crewKey )
	{
		var removed = false;
		for ( var i = _queue.Count - 1; i >= 0; i-- )
		{
			if ( _queue[i].CrewKey != crewKey )
				continue;
			_queue.RemoveAt( i );
			removed = true;
		}

		return removed;
	}

	void PushQueueBlob()
	{
		var sb = new StringBuilder();
		foreach ( var entry in _queue )
		{
			if ( sb.Length > 0 )
				sb.Append( '\n' );
			sb.Append( entry.CrewKey ).Append( '|' ).Append( (int)entry.Mode )
				.Append( '|' ).Append( HostEntrySize( entry ) );
		}

		QueueBlob = sb.ToString();
	}

	/// <summary>Players already queued for this mode (waiting count, e.g. "1/2"). Works on host and clients.</summary>
	public int GetQueuedPlayerCount( ArenaMode mode )
	{
		if ( HasHostAuthority )
		{
			var total = 0;
			foreach ( var entry in _queue )
			{
				if ( entry.Mode == mode )
					total += HostEntrySize( entry );
			}

			return total;
		}

		if ( string.IsNullOrEmpty( QueueBlob ) )
			return 0;

		var sum = 0;
		foreach ( var line in QueueBlob.Split( '\n' ) )
		{
			var parts = line.Split( '|' );
			if ( parts.Length < 3
			     || !int.TryParse( parts[1], out var modeInt ) || (ArenaMode)modeInt != mode
			     || !int.TryParse( parts[2], out var size ) )
				continue;
			sum += size;
		}

		return sum;
	}

	/// <summary>Works on host and clients (parses the synced blob on clients).</summary>
	public bool TryGetQueuedMode( Guid crewKey, out ArenaMode mode )
	{
		mode = ArenaMode.OneVOne;
		if ( HasHostAuthority )
		{
			foreach ( var entry in _queue )
			{
				if ( entry.CrewKey != crewKey )
					continue;
				mode = entry.Mode;
				return true;
			}

			return false;
		}

		if ( string.IsNullOrEmpty( QueueBlob ) )
			return false;

		foreach ( var line in QueueBlob.Split( '\n' ) )
		{
			var parts = line.Split( '|' );
			if ( parts.Length < 2 || !Guid.TryParse( parts[0], out var key ) || key != crewKey )
				continue;
			if ( int.TryParse( parts[1], out var modeInt ) )
			{
				mode = (ArenaMode)modeInt;
				return true;
			}
		}

		return false;
	}

	// ── Matchmaking ─────────────────────────────────────────────────────────────

	int HostEntrySize( QueueEntry entry )
	{
		var members = CrewRegistry.GetCrewMembers( entry.CrewKey );
		return members?.Count ?? 1;
	}

	void TryMatchmake()
	{
		if ( Phase != ArenaPhase.Idle )
			return;

		foreach ( var mode in ArenaModeInfo.All )
		{
			var teamSize = ArenaModeInfo.TeamSize( mode );
			var teamCount = ArenaModeInfo.TeamCount( mode );
			var remaining = new int[teamCount];
			for ( var t = 0; t < teamCount; t++ )
				remaining[t] = teamSize;

			var picked = new List<(QueueEntry Entry, int Team)>();
			var filled = 0;
			foreach ( var entry in _queue )
			{
				if ( entry.Mode != mode )
					continue;

				var size = HostEntrySize( entry );
				for ( var t = 0; t < teamCount; t++ )
				{
					if ( size > remaining[t] )
						continue;
					remaining[t] -= size;
					filled += size;
					picked.Add( (entry, t) );
					break;
				}

				if ( filled >= teamSize * teamCount )
					break;
			}

			if ( filled < teamSize * teamCount )
				continue;

			HostStartMatch( mode, picked );
			return;
		}
	}

	void HostStartMatch( ArenaMode mode, List<(QueueEntry Entry, int Team)> picked )
	{
		var teamCount = ArenaModeInfo.TeamCount( mode );
		_participants.Clear();
		_teamNames.Clear();
		for ( var t = 0; t < teamCount; t++ )
			_teamNames.Add( "" );

		foreach ( var (entry, team) in picked )
		{
			var members = CrewRegistry.GetCrewMembers( entry.CrewKey );
			if ( members is not null )
			{
				var crewName = CrewRegistry.GetCrewOf( members[0].PlayerId )?.Name ?? "Crew";
				AppendTeamName( team, crewName );
				foreach ( var m in members )
					TryAddParticipant( m.PlayerId, m.DisplayName, team );
			}
			else
			{
				// Solo entry — crew key is the player id.
				var root = FindPawnByPlayerId( entry.CrewKey );
				var name = root is not null ? CrewRegistry.ResolvePawnDisplayName( root ) : "Player";
				AppendTeamName( team, name );
				TryAddParticipant( entry.CrewKey, name, team );
			}
		}

		// Every team needs at least one live pawn or the match is void.
		for ( var t = 0; t < teamCount; t++ )
		{
			if ( CountAliveOnTeam( t ) != 0 )
				continue;

			Log.Warning( "[Arena] Match aborted — a team has no resolvable players." );
			_participants.Clear();
			return;
		}

		foreach ( var (entry, _) in picked )
			_queue.Remove( entry );
		PushQueueBlob();

		_activeMode = mode;
		foreach ( var p in _participants )
		{
			p.ReturnPos = p.Root.WorldPosition;
			p.ReturnRot = p.Root.WorldRotation;
			var movement = p.Root.Components.Get<PlayerMovement>();
			movement?.HostApplyEventSpawn( ResolveMemberSpawn( p ), Rotation.LookAt(
				(CenterWorldPosition - ResolveTeamSpawn( p.TeamIndex )).WithZ( 0f ).Normal, Vector3.Up ) );
			movement?.HostSetEventFrozen( true );
		}

		Phase = ArenaPhase.Countdown;
		_countdownValue = 3;
		_countdownBeatAt = Time.NowDouble;
		RpcBroadcastArenaText( "3" );
		if ( LogArena )
			Log.Info( $"[Arena] {ArenaModeInfo.Display( mode )} starting — {_participants.Count} players." );
	}

	void AppendTeamName( int team, string crewName )
	{
		if ( string.IsNullOrEmpty( _teamNames[team] ) )
			_teamNames[team] = crewName;
		else
			_teamNames[team] += " + " + crewName;
	}

	void TryAddParticipant( Guid playerId, string name, int team )
	{
		var root = FindPawnByPlayerId( playerId );
		if ( root is null )
		{
			Log.Warning( $"[Arena] No pawn found for {name} — they miss the match." );
			return;
		}

		_participants.Add( new Participant
		{
			PlayerId = playerId,
			Name = name,
			Root = root,
			TeamIndex = team,
		} );
	}

	// ── Spawning / confinement ──────────────────────────────────────────────────

	Vector3 ResolveTeamSpawn( int teamIndex )
	{
		if ( TeamSpawnPoints is not null
		     && teamIndex < TeamSpawnPoints.Count
		     && TeamSpawnPoints[teamIndex] is { IsValid: true } point )
			return point.WorldPosition;

		var teamCount = ArenaModeInfo.TeamCount( _activeMode );
		var angle = teamIndex * (360f / Math.Max( 1, teamCount ));
		var dir = Rotation.FromYaw( angle ).Forward;
		return CenterWorldPosition + dir * (RadiusUnits * 0.5f) + Vector3.Up * 40f;
	}

	Vector3 ResolveMemberSpawn( Participant p )
	{
		// One spawn per crew; stack members on a tiny ring so physics doesn't pop them apart.
		var indexInTeam = 0;
		foreach ( var other in _participants )
		{
			if ( other == p )
				break;
			if ( other.TeamIndex == p.TeamIndex )
				indexInTeam++;
		}

		var offset = Rotation.FromYaw( indexInTeam * 90f ).Forward * (indexInTeam > 0 ? 30f : 0f);
		return ResolveTeamSpawn( p.TeamIndex ) + offset;
	}

	void TickCountdown()
	{
		if ( Time.NowDouble < _countdownBeatAt + Math.Max( 0.2f, CountdownBeatSeconds ) )
			return;

		_countdownBeatAt = Time.NowDouble;
		_countdownValue--;

		if ( _countdownValue >= 1 )
		{
			RpcBroadcastArenaText( _countdownValue.ToString() );
			return;
		}

		RpcBroadcastArenaText( "FIGHT!" );
		foreach ( var p in _participants )
		{
			if ( p.Root is { IsValid: true } )
				p.Root.Components.Get<PlayerMovement>()?.HostSetEventFrozen( false );
		}

		Phase = ArenaPhase.Fighting;
		_nextFightTickAt = Time.NowDouble + 1.0;
	}

	void TickFighting()
	{
		var center = CenterWorldPosition;
		var radius = RadiusUnits;

		foreach ( var p in _participants )
		{
			if ( p.Dead )
				continue;

			if ( p.Root is not { IsValid: true } )
			{
				p.Dead = true;
				continue;
			}

			// Cannot leave the arena until one crew is left: yank runaways back to their spawn.
			var flat = (p.Root.WorldPosition - center).WithZ( 0f );
			var fellOut = p.Root.WorldPosition.z < center.z - 400f;
			if ( flat.Length > radius || fellOut )
			{
				p.Root.Components.Get<PlayerMovement>()?.HostApplyEventSpawn(
					ResolveTeamSpawn( p.TeamIndex ),
					Rotation.LookAt( (center - p.Root.WorldPosition).WithZ( 0f ).Normal, Vector3.Up ) );
			}
		}

		var aliveTeams = 0;
		var lastAliveTeam = -1;
		for ( var t = 0; t < _teamNames.Count; t++ )
		{
			if ( CountAliveOnTeam( t ) <= 0 )
				continue;
			aliveTeams++;
			lastAliveTeam = t;
		}

		if ( aliveTeams > 1 )
			return;

		Phase = ArenaPhase.Finished;
		_finishedUntil = Time.NowDouble + Math.Max( 1f, FinishedHoldSeconds );
		var banner = lastAliveTeam >= 0
			? $"{_teamNames[lastAliveTeam]} wins the arena!"
			: "Arena battle over!";
		RpcBroadcastArenaText( banner );
		if ( LogArena )
			Log.Info( $"[Arena] {banner}" );
	}

	int CountAliveOnTeam( int teamIndex )
	{
		var alive = 0;
		foreach ( var p in _participants )
		{
			if ( p.TeamIndex == teamIndex && !p.Dead && p.Root is { IsValid: true } )
				alive++;
		}

		return alive;
	}

	// ── Death / restore ─────────────────────────────────────────────────────────

	/// <summary>
	/// Called from <see cref="PlayerVitals"/> on the host death path. Returns true when the pawn
	/// is an arena participant: no loot bag, no normal respawn — they ghost-spectate instead.
	/// </summary>
	public static bool HostTryInterceptDeath( PlayerVitals vitals ) =>
		Instance is { IsValid: true } session && session.HostInterceptDeath( vitals );

	bool HostInterceptDeath( PlayerVitals vitals )
	{
		if ( Phase is not (ArenaPhase.Countdown or ArenaPhase.Fighting or ArenaPhase.Finished) )
			return false;

		var root = vitals?.GameObject;
		if ( root is not { IsValid: true } )
			return false;

		foreach ( var p in _participants )
		{
			if ( p.Root != root )
				continue;

			if ( !p.Dead )
			{
				p.Dead = true;
				root.Components.Get<PlayerMovement>()?.HostSetArenaSpectate(
					true, CenterWorldPosition, RadiusUnits * 1.5f );
				if ( LogArena )
					Log.Info( $"[Arena] {p.Name} is down — spectating." );
			}

			// Already-dead participants stay suppressed until the match restores them.
			return true;
		}

		return false;
	}

	void HostFinishAndRestore()
	{
		RpcBroadcastArenaText( "" );
		var returnIndex = 0;
		foreach ( var p in _participants )
		{
			if ( p.Root is not { IsValid: true } )
				continue;

			var movement = p.Root.Components.Get<PlayerMovement>();
			movement?.HostSetArenaSpectate( false, Vector3.Zero, 0f );
			movement?.HostSetEventFrozen( false );

			// Everyone exits to the return point (the arena stand) when one is wired;
			// otherwise they go back to wherever they stood before the match.
			Vector3 pos;
			Rotation rot;
			if ( ReturnPoint is { IsValid: true } rp )
			{
				var ring = Rotation.FromYaw( returnIndex * (360f / Math.Max( 1, _participants.Count )) ).Forward * 60f;
				pos = rp.WorldPosition + ring + Vector3.Up * 40f;
				rot = rp.WorldRotation;
			}
			else
			{
				pos = p.ReturnPos;
				rot = p.ReturnRot;
			}

			p.Root.Components.Get<PlayerVitals>()?.HostArenaRestoreAndTeleport( pos, rot );
			returnIndex++;
		}

		_participants.Clear();
		_teamNames.Clear();
		Phase = ArenaPhase.Idle;
		if ( LogArena )
			Log.Info( "[Arena] Everyone restored — arena idle." );

		TryMatchmake();
	}

	[Rpc.Broadcast]
	void RpcBroadcastArenaText( string text )
	{
		LocalArenaTextChanged?.Invoke( text ?? string.Empty );
	}

	GameObject FindPawnByPlayerId( Guid playerId )
	{
		var scene = Scene.IsValid() ? Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals?.GameObject is { IsValid: true } root
			     && TimeTrialSession.ResolvePlayerKey( root ) == playerId )
				return root;
		}

		return null;
	}
}
