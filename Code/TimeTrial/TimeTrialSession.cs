using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-authoritative time trial living on the Time Trials menu button object: menu-driven
/// solo / 1v1 lobby, variation routes, countdown, race, per-variation leaderboard.
/// Spawn offsets are local to this button object.
/// </summary>
[Title( "Time Trial Session" )]
public sealed class TimeTrialSession : Component
{
	public static TimeTrialSession Instance { get; private set; }

	public static event Action<string> LocalCountdownTextChanged;
	public static event Action<float> LocalRaceElapsedChanged;
	public static event Action<string> LocalFinishBannerChanged;
	public static event Action<TimeTrialFinishResults> LocalFinishResultsChanged;

	[Property, Group( "Spawns" ), Title( "Solo Start Local Offset" )]
	public Vector3 SoloStartLocalOffset { get; set; } = new( 0f, 0f, 40f );

	[Property, Group( "Spawns" ), Title( "2P Left Local Offset (from this button)" )]
	public Vector3 TwoPlayerLeftLocalOffset { get; set; } = new( 0f, -60f, 0f );

	[Property, Group( "Spawns" ), Title( "2P Right Local Offset (from this button)" )]
	public Vector3 TwoPlayerRightLocalOffset { get; set; } = new( 0f, 60f, 0f );

	[Property, Group( "Spawns" ), Title( "Face Target (optional)" )]
	public GameObject FaceTarget { get; set; }

	[Property, Group( "Timing" ), Title( "Countdown Seconds Per Beat" ), Range( 0.5f, 2f )]
	public float CountdownBeatSeconds { get; set; } = 1f;

	[Property, Group( "Timing" ), Title( "Finished Hold Seconds" ), Range( 1f, 20f )]
	public float FinishedHoldSeconds { get; set; } = 10f;

	[Property, Group( "Audio" ), Title( "Countdown Beep (optional)" )]
	public SoundEvent CountdownBeep { get; set; }

	[Property, Group( "Audio" ), Title( "Go Beep (optional)" )]
	public SoundEvent GoBeep { get; set; }

	[Property, Group( "Debug" )] public bool LogTimeTrial { get; set; }

	[Sync( SyncFlags.FromHost )] public TimeTrialPhase Phase { get; private set; } = TimeTrialPhase.Idle;
	[Sync( SyncFlags.FromHost )] public int ReadyCount { get; private set; }
	[Sync( SyncFlags.FromHost )] public int NextCheckpointOrder { get; private set; }
	[Sync( SyncFlags.FromHost )] public string CountdownText { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public string ActiveVariationId { get; private set; } = string.Empty;
	[Sync( SyncFlags.FromHost )] public TimeTrialMode ActiveMode { get; private set; } = TimeTrialMode.Solo;
	[Sync( SyncFlags.FromHost )] public Guid QueueLeaderId { get; private set; }
	[Sync( SyncFlags.FromHost )] public Guid QueuedPlayerA { get; private set; }
	[Sync( SyncFlags.FromHost )] public Guid QueuedPlayerB { get; private set; }

	readonly List<RacerState> _racers = new();
	readonly List<int> _activeRouteOrders = new();
	double _countdownBeatAt;
	int _countdownValue;
	double _finishedUntil;
	string _lastLocalCountdown = "\0";
	float _lastLocalElapsed = float.MinValue;
	double _localTimingStartedAt = -1;
	bool _localTimingActive;

	bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	struct RacerState
	{
		public Guid PlayerId;
		public GameObject Root;
		public int RouteIndex;
		public int NextOrder;
		public bool TimingStarted;
		public double TimingStartedAt;
		public bool Finished;
		public float FinishSeconds;
		public string DisplayName;
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Instance = this;
	}

	protected override void OnDisabled()
	{
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
		TimeTrialVariationCatalog.EnsureLoaded();

		// Object-mode session so [Sync] lobby state + Broadcast RPCs reach joining clients.
		if ( Networking.IsHost )
			HostNetworkSpawn.TrySpawn( GameObject );

		if ( HasHostAuthority )
		{
			TimeTrialLeaderboardStore.DiscardLegacyBoards();
			TimeTrialCheckpoint.RefreshHighlights( -1 );
		}
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		PushLocalHud();

		if ( !HasHostAuthority )
			return;

		switch ( Phase )
		{
			case TimeTrialPhase.Countdown:
				TickCountdown();
				break;
			case TimeTrialPhase.Racing:
				TickRacing();
				break;
			case TimeTrialPhase.Finished:
				if ( Time.NowDouble >= _finishedUntil )
					HostResetToIdle();
				break;
		}
	}

	public bool IsPlayerInQueue( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return false;
		var id = ResolvePlayerKey( playerRoot );
		return id != default && (id == QueuedPlayerA || id == QueuedPlayerB);
	}

	void SyncQueueMembersFromRacers()
	{
		QueuedPlayerA = _racers.Count > 0 ? _racers[0].PlayerId : default;
		QueuedPlayerB = _racers.Count > 1 ? _racers[1].PlayerId : default;
		ReadyCount = _racers.Count;
	}

	public bool CanOpenMenu =>
		Phase is TimeTrialPhase.Idle or TimeTrialPhase.WaitingForPlayers;

	public bool CanSelectVariation( GameObject playerRoot )
	{
		if ( Phase == TimeTrialPhase.Idle )
			return true;
		if ( Phase == TimeTrialPhase.WaitingForPlayers )
			return playerRoot is { IsValid: true } && ResolvePlayerKey( playerRoot ) == QueueLeaderId;
		return false;
	}

	/// <summary>Network-stable player key (connection id when networked, else GameObject id).</summary>
	public static Guid ResolvePlayerKey( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			return default;

		if ( root.Network is { Active: true, Owner: { } owner } )
			return owner.Id;

		return root.Id;
	}

	/// <summary>Solo start, or first 1v1 player creating the lobby (picks variation).</summary>
	public void HostTryStart( GameObject playerRoot, TimeTrialMode mode, string variationId )
	{
		if ( !HasHostAuthority || playerRoot is null || !playerRoot.IsValid() )
			return;

		if ( Phase is TimeTrialPhase.Countdown or TimeTrialPhase.Racing or TimeTrialPhase.Finished )
		{
			if ( LogTimeTrial )
				Log.Info( $"[TimeTrial] Start rejected — race in progress ({Phase})." );
			return;
		}

		var variation = TimeTrialVariationCatalog.GetOrDefault( variationId );
		if ( variation is null || !TimeTrialCheckpoint.TryGetRoute( variation.CheckpointOrders, out _ ) )
		{
			Log.Warning( $"[TimeTrial] Start rejected — invalid variation '{variationId}'." );
			return;
		}

		var name = ResolveDisplayName( playerRoot );

		if ( mode == TimeTrialMode.Solo )
		{
			if ( Phase != TimeTrialPhase.Idle )
				return;

			_racers.Clear();
			_racers.Add( CreateRacer( playerRoot, name ) );
			ActiveVariationId = variation.Id;
			ActiveMode = TimeTrialMode.Solo;
			QueueLeaderId = ResolvePlayerKey( playerRoot );
			SyncQueueMembersFromRacers();
			HostBeginCountdown();
			return;
		}

		// 1v1 — first player opens lobby
		if ( Phase == TimeTrialPhase.Idle )
		{
			_racers.Clear();
			_racers.Add( CreateRacer( playerRoot, name ) );
			ActiveVariationId = variation.Id;
			ActiveMode = TimeTrialMode.TwoPlayer;
			QueueLeaderId = ResolvePlayerKey( playerRoot );
			SyncQueueMembersFromRacers();
			Phase = TimeTrialPhase.WaitingForPlayers;
			if ( LogTimeTrial )
				Log.Info( $"[TimeTrial] {name} opened 1v1 lobby — {variation.DisplayName} (1/2)." );
			return;
		}

		if ( LogTimeTrial )
			Log.Info( $"[TimeTrial] Start rejected — lobby already open; second player must Join." );
	}

	/// <summary>Second 1v1 player joins the existing lobby (variation already locked).</summary>
	public void HostTryJoin( GameObject playerRoot )
	{
		if ( !HasHostAuthority || playerRoot is null || !playerRoot.IsValid() )
			return;

		if ( Phase != TimeTrialPhase.WaitingForPlayers || ActiveMode != TimeTrialMode.TwoPlayer )
			return;

		if ( ReadyCount >= 2 )
			return;

		var key = ResolvePlayerKey( playerRoot );
		for ( var i = 0; i < _racers.Count; i++ )
		{
			if ( _racers[i].PlayerId == key )
				return;
		}

		var name = ResolveDisplayName( playerRoot );
		_racers.Add( CreateRacer( playerRoot, name ) );
		SyncQueueMembersFromRacers();
		if ( LogTimeTrial )
			Log.Info( $"[TimeTrial] {name} joined 1v1 ({ReadyCount}/2)." );

		if ( ReadyCount >= 2 )
			HostBeginCountdown();
	}

	/// <summary>Leave queue before countdown starts.</summary>
	public void HostTryLeave( GameObject playerRoot )
	{
		if ( !HasHostAuthority || playerRoot is null || !playerRoot.IsValid() )
			return;

		if ( Phase != TimeTrialPhase.WaitingForPlayers )
			return;

		var key = ResolvePlayerKey( playerRoot );
		var removed = false;
		for ( var i = _racers.Count - 1; i >= 0; i-- )
		{
			if ( _racers[i].PlayerId != key )
				continue;
			_racers.RemoveAt( i );
			removed = true;
		}

		if ( !removed )
			return;

		SyncQueueMembersFromRacers();
		if ( LogTimeTrial )
			Log.Info( $"[TimeTrial] {ResolveDisplayName( playerRoot )} left queue ({ReadyCount}/2)." );

		if ( _racers.Count == 0 )
		{
			HostResetToIdle();
			return;
		}

		// Leader left — remaining player becomes leader (still waiting).
		if ( QueueLeaderId == key )
			QueueLeaderId = _racers[0].PlayerId;
	}

	static RacerState CreateRacer( GameObject root, string name ) =>
		new()
		{
			PlayerId = ResolvePlayerKey( root ),
			Root = root,
			RouteIndex = 0,
			NextOrder = 0,
			TimingStarted = false,
			Finished = false,
			DisplayName = name,
		};

	static string ResolveDisplayName( GameObject pawn )
	{
		var name = pawn.Network is { Active: true, Owner: { } owner }
			? owner.DisplayName
			: Connection.Local?.DisplayName;
		return string.IsNullOrWhiteSpace( name ) ? "Player" : name;
	}

	void HostBeginCountdown()
	{
		var variation = TimeTrialVariationCatalog.GetOrDefault( ActiveVariationId );
		if ( variation is null || !TimeTrialCheckpoint.TryGetRoute( variation.CheckpointOrders, out var route ) )
		{
			Log.Warning( "[TimeTrial] Need a valid variation route — aborting." );
			HostResetToIdle();
			return;
		}

		_activeRouteOrders.Clear();
		for ( var i = 0; i < variation.CheckpointOrders.Count; i++ )
			_activeRouteOrders.Add( variation.CheckpointOrders[i] );

		Phase = TimeTrialPhase.Countdown;
		_countdownValue = 3;
		_countdownBeatAt = Time.NowDouble;
		CountdownText = "3";
		NextCheckpointOrder = _activeRouteOrders[0];
		TimeTrialCheckpoint.RefreshHighlights( NextCheckpointOrder );

		for ( var i = 0; i < _racers.Count; i++ )
		{
			var r = _racers[i];
			r.RouteIndex = 0;
			r.NextOrder = _activeRouteOrders[0];
			r.TimingStarted = false;
			r.Finished = false;
			r.FinishSeconds = 0f;
			_racers[i] = r;
			HostPlaceRacer( i );
			HostSetRacerFrozen( r.Root, true );
		}

		BroadcastCountdown( "3", playGo: false );
		if ( LogTimeTrial )
			Log.Info( $"[TimeTrial] Countdown — {variation.DisplayName}." );
	}

	void TickCountdown()
	{
		if ( Time.NowDouble < _countdownBeatAt + Math.Max( 0.2f, CountdownBeatSeconds ) )
			return;

		_countdownBeatAt = Time.NowDouble;
		_countdownValue--;

		if ( _countdownValue >= 1 )
		{
			CountdownText = _countdownValue.ToString();
			BroadcastCountdown( CountdownText, playGo: false );
			return;
		}

		if ( _countdownValue == 0 )
		{
			CountdownText = "GO!";
			BroadcastCountdown( "GO!", playGo: true );
			for ( var i = 0; i < _racers.Count; i++ )
				HostSetRacerFrozen( _racers[i].Root, false );

			Phase = TimeTrialPhase.Racing;
			_countdownValue = -1;
			_countdownBeatAt = Time.NowDouble + 0.8;
			if ( LogTimeTrial )
				Log.Info( "[TimeTrial] GO — racing." );
		}
	}

	void TickRacing()
	{
		if ( !string.IsNullOrEmpty( CountdownText ) && Time.NowDouble >= _countdownBeatAt )
		{
			CountdownText = string.Empty;
			BroadcastCountdown( string.Empty, playGo: false );
		}

		if ( _activeRouteOrders.Count < 2 )
			return;

		var lastOrder = _activeRouteOrders[^1];
		var anyActive = false;

		for ( var i = 0; i < _racers.Count; i++ )
		{
			var r = _racers[i];
			if ( r.Finished || r.Root is null || !r.Root.IsValid() )
				continue;

			anyActive = true;
			TimeTrialCheckpoint hit = null;
			foreach ( var cp in TimeTrialCheckpoint.All )
			{
				if ( cp is null || !cp.IsValid() || cp.Order != r.NextOrder )
					continue;
				if ( cp.HostIsPlayerInside( r.Root ) )
				{
					hit = cp;
					break;
				}
			}

			if ( hit is null )
				continue;

			if ( !r.TimingStarted )
			{
				r.TimingStarted = true;
				r.TimingStartedAt = Time.NowDouble;
				RpcBroadcastTiming( r.PlayerId, started: true, finished: false, 0f );
				if ( LogTimeTrial )
					Log.Info( $"[TimeTrial] {r.DisplayName} timing started at order {hit.Order}." );
			}

			if ( hit.Order == lastOrder && r.TimingStarted && r.RouteIndex >= _activeRouteOrders.Count - 1 )
			{
				r.Finished = true;
				r.FinishSeconds = (float)(Time.NowDouble - r.TimingStartedAt);
				_racers[i] = r;
				HostOnRacerFinished( r );
				continue;
			}

			var nextIndex = r.RouteIndex + 1;
			if ( nextIndex >= _activeRouteOrders.Count )
			{
				_racers[i] = r;
				continue;
			}

			r.RouteIndex = nextIndex;
			r.NextOrder = _activeRouteOrders[nextIndex];
			_racers[i] = r;
			if ( LogTimeTrial )
				Log.Info( $"[TimeTrial] {r.DisplayName} → next order {r.NextOrder}." );
		}

		UpdateSharedNextCheckpointHighlight();

		if ( !anyActive || AllRacersFinished() )
		{
			Phase = TimeTrialPhase.Finished;
			_finishedUntil = Time.NowDouble + Math.Max( 1f, FinishedHoldSeconds );
			CountdownText = string.Empty;
			BroadcastCountdown( string.Empty, playGo: false );
			// Board refresh only — keep the per-finisher Winner banner already shown.
			HostBroadcastLeaderboardBoard( banner: "" );
		}
	}

	void UpdateSharedNextCheckpointHighlight()
	{
		var best = int.MaxValue;
		for ( var i = 0; i < _racers.Count; i++ )
		{
			if ( _racers[i].Finished )
				continue;
			best = Math.Min( best, _racers[i].NextOrder );
		}

		if ( best == int.MaxValue )
			best = -1;

		if ( NextCheckpointOrder != best )
		{
			NextCheckpointOrder = best;
			TimeTrialCheckpoint.RefreshHighlights( best );
		}
	}

	bool AllRacersFinished()
	{
		if ( _racers.Count == 0 )
			return true;

		for ( var i = 0; i < _racers.Count; i++ )
		{
			if ( !_racers[i].Finished && _racers[i].Root is { IsValid: true } )
				return false;
		}

		return true;
	}

	void HostOnRacerFinished( RacerState r )
	{
		var winner = true;
		for ( var i = 0; i < _racers.Count; i++ )
		{
			if ( !_racers[i].Finished || _racers[i].PlayerId == r.PlayerId )
				continue;

			var otherAbs = _racers[i].TimingStartedAt + _racers[i].FinishSeconds;
			var myAbs = r.TimingStartedAt + r.FinishSeconds;
			if ( otherAbs < myAbs )
			{
				winner = false;
				break;
			}
		}

		TimeTrialLeaderboardStore.TryRecord( ActiveVariationId, r.DisplayName, r.FinishSeconds, out var rank );
		var banner = winner
			? $"Winner! {r.DisplayName} — {FormatTime( r.FinishSeconds )}"
			: $"{r.DisplayName} — {FormatTime( r.FinishSeconds )}";
		if ( rank > 0 )
			banner += $" (#{rank})";

		RpcBroadcastTiming( r.PlayerId, started: false, finished: true, r.FinishSeconds );
		// Show finish banner + leaderboard to every client (not only the finisher).
		HostBroadcastLeaderboardBoard( banner, focusPlayerId: default, r.FinishSeconds, rank );
		if ( LogTimeTrial )
			Log.Info( $"[TimeTrial] Finish: {banner}" );
	}

	void HostBroadcastLeaderboardBoard( string banner, Guid focusPlayerId = default, float yourTime = 0f, int yourRank = -1 )
	{
		var variation = TimeTrialVariationCatalog.GetOrDefault( ActiveVariationId );
		var top = TimeTrialLeaderboardStore.Load( ActiveVariationId );
		var sb = new StringBuilder();
		for ( var i = 0; i < top.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( '\n' );
			sb.Append( i + 1 ).Append( '\t' )
				.Append( top[i].DisplayName ).Append( '\t' )
				.Append( top[i].TimeSeconds.ToString( "0.00" ) );
		}

		RpcBroadcastFinishResults(
			focusPlayerId,
			banner ?? "",
			variation?.DisplayName ?? ActiveVariationId,
			ActiveVariationId ?? "",
			yourTime,
			yourRank,
			sb.ToString() );
	}

	public static string FormatTime( float seconds ) =>
		seconds >= 60f
			? $"{(int)(seconds / 60f)}:{seconds % 60f:00.00}"
			: $"{seconds:0.00}s";

	void HostPlaceRacer( int index )
	{
		if ( index < 0 || index >= _racers.Count )
			return;

		var r = _racers[index];
		if ( r.Root is null || !r.Root.IsValid() )
			return;

		var local = _racers.Count == 1
			? SoloStartLocalOffset
			: index == 0 ? TwoPlayerLeftLocalOffset : TwoPlayerRightLocalOffset;
		var worldPos = GameObject.WorldPosition + GameObject.WorldRotation * local;

		var facePos = ResolveFaceWorldPosition();
		var flat = (facePos - worldPos).WithZ( 0f );
		var yaw = flat.Length > 1e-3f
			? Rotation.LookAt( flat.Normal, Vector3.Up )
			: GameObject.WorldRotation;

		var movement = r.Root.Components.Get<PlayerMovement>();
		if ( movement is not null )
			movement.HostApplyEventSpawn( worldPos, yaw );
		else
		{
			r.Root.WorldPosition = worldPos;
			r.Root.WorldRotation = yaw;
		}
	}

	Vector3 ResolveFaceWorldPosition()
	{
		if ( FaceTarget is { IsValid: true } )
			return FaceTarget.WorldPosition;

		if ( _activeRouteOrders.Count > 0 )
		{
			foreach ( var cp in TimeTrialCheckpoint.All )
			{
				if ( cp is not null && cp.IsValid() && cp.Order == _activeRouteOrders[0] )
					return cp.GameObject.WorldPosition;
			}
		}

		return GameObject.WorldPosition + GameObject.WorldRotation.Forward * 200f;
	}

	void HostSetRacerFrozen( GameObject root, bool frozen )
	{
		if ( root is null || !root.IsValid() )
			return;

		var movement = root.Components.Get<PlayerMovement>();
		movement?.HostSetEventFrozen( frozen );
	}

	void HostResetToIdle()
	{
		for ( var i = 0; i < _racers.Count; i++ )
			HostSetRacerFrozen( _racers[i].Root, false );

		_racers.Clear();
		_activeRouteOrders.Clear();
		ReadyCount = 0;
		QueuedPlayerA = default;
		QueuedPlayerB = default;
		Phase = TimeTrialPhase.Idle;
		CountdownText = string.Empty;
		NextCheckpointOrder = -1;
		ActiveVariationId = string.Empty;
		QueueLeaderId = default;
		TimeTrialCheckpoint.RefreshHighlights( -1 );
		BroadcastCountdown( string.Empty, playGo: false );
		RpcBroadcastTiming( default, started: false, finished: true, 0f );
		if ( LogTimeTrial )
			Log.Info( "[TimeTrial] Idle." );
	}

	void BroadcastCountdown( string text, bool playGo )
	{
		CountdownText = text ?? string.Empty;
		RpcBroadcastCountdown( CountdownText, playGo );
	}

	[Rpc.Broadcast]
	void RpcBroadcastTiming( Guid playerId, bool started, bool finished, float finishSeconds )
	{
		if ( playerId == default )
		{
			_localTimingActive = false;
			_localTimingStartedAt = -1;
			LocalRaceElapsedChanged?.Invoke( -1f );
			return;
		}

		var local = FindLocalPlayerRoot();
		if ( local is null || ResolvePlayerKey( local ) != playerId )
			return;

		if ( started )
		{
			_localTimingActive = true;
			_localTimingStartedAt = Time.NowDouble;
		}

		if ( finished )
		{
			_localTimingActive = false;
			_localTimingStartedAt = -1;
			LocalRaceElapsedChanged?.Invoke( finishSeconds > 0f ? finishSeconds : -1f );
		}
	}

	[Rpc.Broadcast]
	void RpcBroadcastCountdown( string text, bool playGo )
	{
		LocalCountdownTextChanged?.Invoke( text ?? string.Empty );
		if ( string.IsNullOrEmpty( text ) )
			return;

		var sound = playGo ? GoBeep : CountdownBeep;
		if ( sound is not null )
			Sound.Play( sound );
	}

	[Rpc.Broadcast]
	void RpcBroadcastFinishResults(
		Guid focusPlayerId,
		string banner,
		string variationName,
		string variationId,
		float yourTime,
		int yourRank,
		string topTsv )
	{
		var local = FindLocalPlayerRoot();
		var isFocus = focusPlayerId == default
		              || (local is not null && ResolvePlayerKey( local ) == focusPlayerId);

		// Always show the race banner when the host sends one (winner time for everyone).
		if ( !string.IsNullOrWhiteSpace( banner ) )
			LocalFinishBannerChanged?.Invoke( banner );

		var results = new TimeTrialFinishResults
		{
			Banner = banner ?? "",
			VariationDisplayName = variationName ?? "",
			VariationId = variationId ?? "",
			YourTimeSeconds = isFocus ? yourTime : 0f,
			YourRank = isFocus ? yourRank : -1,
			Entries = ParseTopTsv( topTsv ),
		};
		LocalFinishResultsChanged?.Invoke( results );
	}

	static List<TimeTrialLeaderboardStore.TimeTrialLeaderboardEntry> ParseTopTsv( string tsv )
	{
		var list = new List<TimeTrialLeaderboardStore.TimeTrialLeaderboardEntry>();
		if ( string.IsNullOrWhiteSpace( tsv ) )
			return list;

		var lines = tsv.Split( '\n' );
		for ( var i = 0; i < lines.Length; i++ )
		{
			var parts = lines[i].Split( '\t' );
			if ( parts.Length < 3 )
				continue;
			if ( !float.TryParse( parts[2], out var time ) )
				continue;
			list.Add( new TimeTrialLeaderboardStore.TimeTrialLeaderboardEntry
			{
				DisplayName = parts[1],
				TimeSeconds = time,
			} );
		}

		return list;
	}

	void PushLocalHud()
	{
		var text = CountdownText ?? string.Empty;
		if ( text != _lastLocalCountdown )
		{
			_lastLocalCountdown = text;
			LocalCountdownTextChanged?.Invoke( text );
		}

		var elapsed = -1f;
		if ( _localTimingActive && _localTimingStartedAt > 0 )
			elapsed = (float)(Time.NowDouble - _localTimingStartedAt);

		if ( Math.Abs( elapsed - _lastLocalElapsed ) > 0.05f || (elapsed < 0f) != (_lastLocalElapsed < 0f) )
		{
			_lastLocalElapsed = elapsed;
			LocalRaceElapsedChanged?.Invoke( elapsed );
		}

		TimeTrialCheckpoint.RefreshHighlights( NextCheckpointOrder );
	}

	static GameObject FindLocalPlayerRoot()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals.GameObject;
		}

		return null;
	}
}

public sealed class TimeTrialFinishResults
{
	public string Banner { get; set; } = "";
	public string VariationDisplayName { get; set; } = "";
	public string VariationId { get; set; } = "";
	public float YourTimeSeconds { get; set; }
	public int YourRank { get; set; } = -1;
	public List<TimeTrialLeaderboardStore.TimeTrialLeaderboardEntry> Entries { get; set; } = new();
}
