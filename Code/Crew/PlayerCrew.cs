using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Per-pawn crew view, synced host → owner/proxies. The host mirrors this player's slice of
/// <see cref="CrewRegistry"/> (their crew + their invites) into two blobs whenever the registry
/// changes; UI reads the parsed view on any machine. Lives on the player prefab so crew data
/// travels with the player, not with any feature object or scene.
/// </summary>
[Title( "Player Crew" )]
public sealed class PlayerCrew : Component
{
	/// <summary><c>crewId|name|leaderId|memberId:memberName|...</c>, empty while solo.</summary>
	[Sync( SyncFlags.FromHost )] public string MyCrewBlob { get; private set; } = string.Empty;

	/// <summary>Lines of <c>crewKey|crewName|inviterName</c> for this player's pending invites.</summary>
	[Sync( SyncFlags.FromHost )] public string MyInviteBlob { get; private set; } = string.Empty;

	/// <summary>Comma-joined player ids this player's crew has live invites out to (greys the Invite button).</summary>
	[Sync( SyncFlags.FromHost )] public string MyOutgoingInviteBlob { get; private set; } = string.Empty;

	int _lastPushedVersion = -1;
	double _nextHostPushAt;

	// Parsed caches, rebuilt when the synced blob string instance changes.
	string _parsedCrewBlob;
	string _parsedInviteBlob;
	string _parsedOutgoingBlob;
	CrewRegistry.CrewInfo _parsedCrew;
	readonly List<CrewRegistry.InviteInfo> _parsedInvites = new();
	readonly HashSet<Guid> _parsedOutgoingTargets = new();

	bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	public Guid PlayerKey => TimeTrialSession.ResolvePlayerKey( GameObject );

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !HasHostAuthority || Time.NowDouble < _nextHostPushAt )
			return;

		_nextHostPushAt = Time.NowDouble + 0.25;
		CrewRegistry.MaybePruneExpiredInvites();

		if ( CrewRegistry.Version == _lastPushedVersion )
			return;

		_lastPushedVersion = CrewRegistry.Version;
		HostPushBlobs();
	}

	void HostPushBlobs()
	{
		var key = PlayerKey;
		var crew = CrewRegistry.GetCrewOf( key );
		if ( crew is null )
		{
			MyCrewBlob = string.Empty;
		}
		else
		{
			var sb = new StringBuilder();
			sb.Append( crew.Key ).Append( '|' ).Append( crew.Name ).Append( '|' ).Append( crew.LeaderId );
			foreach ( var m in crew.Members )
				sb.Append( '|' ).Append( m.PlayerId ).Append( ':' ).Append( m.DisplayName );
			MyCrewBlob = sb.ToString();
		}

		var invites = CrewRegistry.GetInvitesFor( key );
		if ( invites.Count == 0 )
		{
			MyInviteBlob = string.Empty;
		}
		else
		{
			var sb = new StringBuilder();
			foreach ( var invite in invites )
			{
				if ( sb.Length > 0 )
					sb.Append( '\n' );
				sb.Append( invite.CrewKey ).Append( '|' )
					.Append( invite.CrewName ).Append( '|' )
					.Append( invite.InviterName );
			}

			MyInviteBlob = sb.ToString();
			if ( CrewRegistry.LogCrews )
				Log.Info( $"[Crew] Host pushed {invites.Count} pending invite(s) to {GameObject.Name}." );
		}

		var outgoing = CrewRegistry.GetOutgoingInviteTargets( crew?.Key ?? key );
		if ( outgoing.Count == 0 )
		{
			MyOutgoingInviteBlob = string.Empty;
		}
		else
		{
			var sb = new StringBuilder();
			foreach ( var target in outgoing )
			{
				if ( sb.Length > 0 )
					sb.Append( ',' );
				sb.Append( target );
			}

			MyOutgoingInviteBlob = sb.ToString();
		}
	}

	/// <summary>True while this player's crew has a live invite out to that player. Works on every machine.</summary>
	public bool HasPendingInviteTo( Guid playerId )
	{
		if ( !ReferenceEquals( _parsedOutgoingBlob, MyOutgoingInviteBlob ) )
		{
			_parsedOutgoingBlob = MyOutgoingInviteBlob;
			_parsedOutgoingTargets.Clear();
			if ( !string.IsNullOrEmpty( MyOutgoingInviteBlob ) )
			{
				foreach ( var part in MyOutgoingInviteBlob.Split( ',' ) )
				{
					if ( Guid.TryParse( part, out var id ) )
						_parsedOutgoingTargets.Add( id );
				}
			}
		}

		return _parsedOutgoingTargets.Contains( playerId );
	}

	/// <summary>This pawn's crew, or null while solo. Works on every machine.</summary>
	public CrewRegistry.CrewInfo GetMyCrew()
	{
		EnsureCrewParsed();
		return _parsedCrew;
	}

	/// <summary>This pawn's pending invites. Works on every machine.</summary>
	public IReadOnlyList<CrewRegistry.InviteInfo> GetMyInvites()
	{
		EnsureInvitesParsed();
		return _parsedInvites;
	}

	void EnsureCrewParsed()
	{
		if ( ReferenceEquals( _parsedCrewBlob, MyCrewBlob ) )
			return;

		_parsedCrewBlob = MyCrewBlob;
		_parsedCrew = null;
		if ( string.IsNullOrEmpty( MyCrewBlob ) )
			return;

		var parts = MyCrewBlob.Split( '|' );
		if ( parts.Length < 4
		     || !Guid.TryParse( parts[0], out var crewId )
		     || !Guid.TryParse( parts[2], out var leaderId ) )
			return;

		var info = new CrewRegistry.CrewInfo { Key = crewId, Name = parts[1], LeaderId = leaderId };
		for ( var i = 3; i < parts.Length; i++ )
		{
			var sep = parts[i].IndexOf( ':' );
			if ( sep <= 0 || !Guid.TryParse( parts[i][..sep], out var memberId ) )
				continue;
			info.Members.Add( new CrewRegistry.CrewMember
			{
				PlayerId = memberId,
				DisplayName = parts[i][(sep + 1)..],
			} );
		}

		_parsedCrew = info;
	}

	void EnsureInvitesParsed()
	{
		if ( ReferenceEquals( _parsedInviteBlob, MyInviteBlob ) )
			return;

		_parsedInviteBlob = MyInviteBlob;
		_parsedInvites.Clear();
		if ( string.IsNullOrEmpty( MyInviteBlob ) )
			return;

		foreach ( var line in MyInviteBlob.Split( '\n' ) )
		{
			var parts = line.Split( '|' );
			if ( parts.Length < 3 || !Guid.TryParse( parts[0], out var crewKey ) )
				continue;

			_parsedInvites.Add( new CrewRegistry.InviteInfo
			{
				CrewKey = crewKey,
				CrewName = parts[1],
				InviterName = parts[2],
			} );
		}

		if ( CrewRegistry.LogCrews && _parsedInvites.Count > 0 )
			Log.Info( $"[Crew] {GameObject.Name}: {_parsedInvites.Count} pending invite(s) arrived locally." );
	}
}
