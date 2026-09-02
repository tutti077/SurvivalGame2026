using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host-authoritative crew registry (max 4 players per crew) — player data, owned by no scene
/// object, so crews survive scene loads (create a crew in a hub, keep it back in your world).
/// Every player without a record is an implicit solo crew of themselves. Real records exist only
/// for crews of 2+; the member list is join-ordered and the leader is always the longest-tenured
/// member (index 0). Per-pawn <see cref="PlayerCrew"/> components sync each player's view to
/// their clients; consumers like the arena read the registry directly on the host.
/// All mutating calls must come from host/offline paths (the intent RPCs gate this).
/// </summary>
public static class CrewRegistry
{
	public const int MaxCrewSize = 4;
	const double InviteLifetimeSeconds = 60.0;

	public static bool LogCrews { get; set; } = true;

	/// <summary>Bumped on every change — <see cref="PlayerCrew"/> uses it to re-push sync blobs.</summary>
	public static int Version { get; private set; } = 1;

	/// <summary>Host-side: fired with the crew key whose membership changed (join/leave/disband), so e.g. the arena can void stale queue entries.</summary>
	public static event Action<Guid> MembersChanged;

	sealed class CrewRecord
	{
		public Guid Id;
		public string Name = "";
		public readonly List<CrewMember> Members = new();
	}

	public struct CrewMember
	{
		public Guid PlayerId;
		public string DisplayName;
	}

	sealed class InviteRecord
	{
		public Guid InviteePlayerId;
		public Guid CrewKey; // crew id, or inviter player id while the inviter is still solo
		public string CrewName = "";
		public string InviterName = "";
		public double ExpiresAt;
	}

	public sealed class CrewInfo
	{
		public Guid Key;
		public string Name = "";
		public Guid LeaderId;
		public readonly List<CrewMember> Members = new();
	}

	public sealed class InviteInfo
	{
		public Guid CrewKey;
		public string CrewName = "";
		public string InviterName = "";
	}

	static readonly List<CrewRecord> Crews = new();
	static readonly List<InviteRecord> Invites = new();
	static double _nextPruneAt;

	static readonly string[] NameAdjectives =
	{
		"Magic", "Rusty", "Soggy", "Turbo", "Feral", "Cosmic", "Sneaky", "Mighty",
		"Golden", "Crimson", "Howling", "Slippery",
	};

	static readonly string[] NameNouns =
	{
		"CoolBus", "Badgers", "Wolves", "Pioneers", "Marauders", "Otters", "Vikings",
		"Nomads", "Falcons", "Goblins", "Raiders", "Wombats",
	};

	// ── Host intents ────────────────────────────────────────────────────────────

	public static void TryInvite( GameObject actingPawn, Guid targetPlayerId )
	{
		if ( actingPawn is null || !actingPawn.IsValid() )
			return;

		var actingId = TimeTrialSession.ResolvePlayerKey( actingPawn );
		if ( actingId == default || targetPlayerId == default || actingId == targetPlayerId )
			return;

		var actingCrew = FindCrewOf( actingId );
		if ( actingCrew is not null && actingCrew.Members.Count >= MaxCrewSize )
		{
			if ( LogCrews )
				Log.Info( $"[Crew] Invite rejected — {actingCrew.Name} is full." );
			return;
		}

		// Target already in the same crew — nothing to invite.
		if ( actingCrew is not null && IndexOfMember( actingCrew, targetPlayerId ) >= 0 )
			return;

		var crewKey = actingCrew?.Id ?? actingId;
		foreach ( var invite in Invites )
		{
			if ( invite.InviteePlayerId != targetPlayerId || invite.CrewKey != crewKey )
				continue;
			invite.ExpiresAt = Time.NowDouble + InviteLifetimeSeconds;
			Version++;
			return;
		}

		var inviterName = ResolvePawnDisplayName( actingPawn );
		Invites.Add( new InviteRecord
		{
			InviteePlayerId = targetPlayerId,
			CrewKey = crewKey,
			CrewName = actingCrew?.Name ?? inviterName,
			InviterName = inviterName,
			ExpiresAt = Time.NowDouble + InviteLifetimeSeconds,
		} );
		Version++;

		if ( LogCrews )
			Log.Info( $"[Crew] {inviterName} invited {targetPlayerId} to {(actingCrew?.Name ?? "a new crew")}." );
	}

	public static void TryAcceptInvite( GameObject actingPawn, Guid crewKey )
	{
		if ( actingPawn is null || !actingPawn.IsValid() )
			return;

		var actingId = TimeTrialSession.ResolvePlayerKey( actingPawn );
		var invite = FindInvite( actingId, crewKey );
		if ( invite is null || Time.NowDouble >= invite.ExpiresAt )
			return;

		// Resolve destination crew: an existing record, or a fresh crew with the solo inviter.
		CrewRecord destination;
		if ( FindCrewById( invite.CrewKey ) is { } existing )
		{
			destination = existing;
		}
		else
		{
			// Key is the solo inviter's player id. If they crewed up meanwhile, route there.
			destination = FindCrewOf( invite.CrewKey );
			if ( destination is null )
			{
				destination = new CrewRecord { Id = Guid.NewGuid(), Name = GenerateCrewName() };
				destination.Members.Add( new CrewMember
				{
					PlayerId = invite.CrewKey,
					DisplayName = invite.InviterName,
				} );
				Crews.Add( destination );
			}
		}

		if ( destination.Members.Count >= MaxCrewSize || IndexOfMember( destination, actingId ) >= 0 )
		{
			Invites.Remove( invite );
			Version++;
			return;
		}

		// Leaving the current crew (if any) hands leadership to the longest-tenured member.
		RemoveFromCrew( actingId );

		destination.Members.Add( new CrewMember
		{
			PlayerId = actingId,
			DisplayName = ResolvePawnDisplayName( actingPawn ),
		} );
		Invites.Remove( invite );
		Version++;
		MembersChanged?.Invoke( destination.Id );
		// A solo queue entry keyed by the joiner is void too.
		MembersChanged?.Invoke( actingId );

		if ( LogCrews )
			Log.Info( $"[Crew] {ResolvePawnDisplayName( actingPawn )} joined {destination.Name} ({destination.Members.Count}/{MaxCrewSize})." );
	}

	public static void TryDeclineInvite( GameObject actingPawn, Guid crewKey )
	{
		if ( actingPawn is null || !actingPawn.IsValid() )
			return;

		var invite = FindInvite( TimeTrialSession.ResolvePlayerKey( actingPawn ), crewKey );
		if ( invite is null )
			return;

		Invites.Remove( invite );
		Version++;
	}

	/// <summary>Leader-only crew rename. Blob delimiters are stripped; length clamped to 20.</summary>
	public static void TryRename( GameObject actingPawn, string newName )
	{
		if ( actingPawn is null || !actingPawn.IsValid() )
			return;

		var actingId = TimeTrialSession.ResolvePlayerKey( actingPawn );
		var crew = FindCrewOf( actingId );
		if ( crew is null || crew.Members.Count == 0 || crew.Members[0].PlayerId != actingId )
		{
			if ( LogCrews )
				Log.Info( "[Crew] Rename rejected — only the crew leader can rename." );
			return;
		}

		var sanitized = (newName ?? "")
			.Replace( "|", "" ).Replace( "\n", "" ).Replace( "\r", "" )
			.Trim();
		if ( sanitized.Length > 20 )
			sanitized = sanitized[..20];
		if ( sanitized.Length == 0 || string.Equals( sanitized, crew.Name, StringComparison.Ordinal ) )
			return;

		var oldName = crew.Name;
		crew.Name = sanitized;

		// Pending invites carry the crew name — keep them current.
		foreach ( var invite in Invites )
		{
			if ( invite.CrewKey == crew.Id )
				invite.CrewName = sanitized;
		}

		Version++;
		if ( LogCrews )
			Log.Info( $"[Crew] {oldName} renamed to {sanitized}." );
	}

	public static void TryLeaveCrew( GameObject actingPawn )
	{
		if ( actingPawn is null || !actingPawn.IsValid() )
			return;

		if ( RemoveFromCrew( TimeTrialSession.ResolvePlayerKey( actingPawn ) ) && LogCrews )
			Log.Info( $"[Crew] {ResolvePawnDisplayName( actingPawn )} left their crew." );
	}

	/// <summary>Remove a player from whatever crew they are in. Disbands 1-member leftovers.</summary>
	static bool RemoveFromCrew( Guid playerId )
	{
		var crew = FindCrewOf( playerId );
		if ( crew is null )
			return false;

		var index = IndexOfMember( crew, playerId );
		if ( index < 0 )
			return false;

		crew.Members.RemoveAt( index );

		// Members[0] is the longest-tenured survivor — leadership follows automatically.
		if ( crew.Members.Count <= 1 )
		{
			Crews.Remove( crew );

			// Pending invites to a disbanded crew would resurrect its id as a ghost member.
			for ( var i = Invites.Count - 1; i >= 0; i-- )
			{
				if ( Invites[i].CrewKey == crew.Id )
					Invites.RemoveAt( i );
			}
		}

		Version++;
		MembersChanged?.Invoke( crew.Id );
		return true;
	}

	/// <summary>Throttled expiry sweep — driven by <see cref="PlayerCrew"/> host ticks.</summary>
	public static void MaybePruneExpiredInvites()
	{
		if ( Time.NowDouble < _nextPruneAt )
			return;

		_nextPruneAt = Time.NowDouble + 5.0;
		var removed = 0;
		for ( var i = Invites.Count - 1; i >= 0; i-- )
		{
			if ( Time.NowDouble < Invites[i].ExpiresAt )
				continue;
			Invites.RemoveAt( i );
			removed++;
		}

		if ( removed > 0 )
			Version++;
	}

	// ── Host queries ────────────────────────────────────────────────────────────

	/// <summary>Crew containing the player, or null when solo (host-side records).</summary>
	public static CrewInfo GetCrewOf( Guid playerId )
	{
		var crew = FindCrewOf( playerId );
		return crew is null ? null : ToInfo( crew );
	}

	public static List<InviteInfo> GetInvitesFor( Guid playerId )
	{
		var list = new List<InviteInfo>();
		foreach ( var invite in Invites )
		{
			if ( invite.InviteePlayerId != playerId || Time.NowDouble >= invite.ExpiresAt )
				continue;
			list.Add( new InviteInfo
			{
				CrewKey = invite.CrewKey,
				CrewName = invite.CrewName,
				InviterName = invite.InviterName,
			} );
		}

		return list;
	}

	/// <summary>Player ids with a live invite *from* this crew key (crew id, or a solo inviter's player id) — for the inviter-side "Invited" button state.</summary>
	public static List<Guid> GetOutgoingInviteTargets( Guid crewKey )
	{
		var list = new List<Guid>();
		foreach ( var invite in Invites )
		{
			if ( invite.CrewKey != crewKey || Time.NowDouble >= invite.ExpiresAt )
				continue;
			list.Add( invite.InviteePlayerId );
		}

		return list;
	}

	/// <summary>Join-ordered member list for arena team building. Null when the key is no crew.</summary>
	public static IReadOnlyList<CrewMember> GetCrewMembers( Guid crewId ) =>
		FindCrewById( crewId )?.Members;

	static CrewInfo ToInfo( CrewRecord crew )
	{
		var info = new CrewInfo { Key = crew.Id, Name = crew.Name };
		info.Members.AddRange( crew.Members );
		info.LeaderId = crew.Members.Count > 0 ? crew.Members[0].PlayerId : default;
		return info;
	}

	// ── Helpers ─────────────────────────────────────────────────────────────────

	static CrewRecord FindCrewOf( Guid playerId )
	{
		foreach ( var crew in Crews )
		{
			if ( IndexOfMember( crew, playerId ) >= 0 )
				return crew;
		}

		return null;
	}

	static CrewRecord FindCrewById( Guid crewId )
	{
		foreach ( var crew in Crews )
		{
			if ( crew.Id == crewId )
				return crew;
		}

		return null;
	}

	static int IndexOfMember( CrewRecord crew, Guid playerId )
	{
		for ( var i = 0; i < crew.Members.Count; i++ )
		{
			if ( crew.Members[i].PlayerId == playerId )
				return i;
		}

		return -1;
	}

	static InviteRecord FindInvite( Guid inviteeId, Guid crewKey )
	{
		foreach ( var invite in Invites )
		{
			if ( invite.InviteePlayerId == inviteeId && invite.CrewKey == crewKey )
				return invite;
		}

		return null;
	}

	static string GenerateCrewName()
	{
		for ( var attempt = 0; attempt < 8; attempt++ )
		{
			var name = NameAdjectives[Random.Shared.Next( 0, NameAdjectives.Length )]
			           + NameNouns[Random.Shared.Next( 0, NameNouns.Length )];
			if ( !CrewNameExists( name ) )
				return name;
		}

		return $"Crew{Random.Shared.Next( 100, 1000 )}";
	}

	static bool CrewNameExists( string name )
	{
		foreach ( var crew in Crews )
		{
			if ( string.Equals( crew.Name, name, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	public static string ResolvePawnDisplayName( GameObject pawn )
	{
		var name = pawn?.Network is { Active: true, Owner: { } owner }
			? owner.DisplayName
			: Connection.Local?.DisplayName;
		return string.IsNullOrWhiteSpace( name ) ? "Player" : name;
	}
}
