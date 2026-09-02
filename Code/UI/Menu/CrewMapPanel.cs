using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Left column of the map page: your crew (count, name, members), a boxed scrollable list of
/// nearby players with "Invite to Crew", and incoming invites with join/decline (join asks to
/// leave the current crew first when you're already in one).
/// The game menu hides the OS cursor and dispatches soft-cursor clicks by screen position, so
/// every button here is a registered hit-rect (<see cref="TryClickAtScreen"/>), not a mouse-event
/// panel; the nearby list scrolls by window index (▲/▼ buttons + menu mouse wheel).
/// </summary>
public sealed class CrewMapPanel
{
	public const float NearbyRangeMeters = 20f;
	const int NearbyVisibleRows = 6;
	const float NearbyRowHeight = 26f;

	readonly PlayerInventoryInteraction _interaction;
	Panel _root;
	Label _crewCount;
	Label _crewName;
	Panel _renameButton;
	Panel _renameRow;
	TextEntry _renameEntry;
	Panel _renameOkButton;
	Panel _renameCancelButton;
	bool _renaming;
	Panel _memberList;
	Panel _leaveRow;
	Panel _nearbyRows;
	Panel _nearbyUpButton;
	Panel _nearbyDownButton;
	Panel _nearbyTrack;
	Panel _nearbyThumb;
	Panel _inviteList;
	double _nextRefreshAt;
	Guid _confirmLeaveForCrewKey;
	int _nearbyScrollIndex;
	int _nearbyCount;

	/// <summary>Soft-cursor click rects, rebuilt on every refresh.</summary>
	readonly List<(Panel Panel, Action Action)> _clickTargets = new();

	/// <summary>Optimistic "Invited" greying until the synced outgoing-invite blob catches up.</summary>
	readonly Dictionary<Guid, double> _recentlyInvitedUntil = new();

	public CrewMapPanel( PlayerInventoryInteraction interaction )
	{
		_interaction = interaction;
	}

	public void Build( Panel parent )
	{
		_root = new Panel { Parent = parent };
		_root.Style.Set( "flex-direction", "column" );
		_root.Style.Set( "flex-shrink", "0" );
		_root.Style.Width = Length.Pixels( 260f );
		_root.Style.Height = Length.Percent( 100 );
		_root.Style.PaddingRight = Length.Pixels( 10f );
		_root.Style.Set( "pointer-events", "none" );

		_crewCount = AddLabel( _root, "Crew 1/4", 18f, Color.White );

		var nameRow = new Panel { Parent = _root };
		nameRow.Style.Set( "flex-direction", "row" );
		nameRow.Style.Set( "align-items", "center" );
		nameRow.Style.Set( "justify-content", "space-between" );

		_crewName = AddLabel( nameRow, "Solo", 15f, new Color( 0.65f, 0.8f, 1f ) );
		_renameButton = MakePersistentButton( nameRow, "Rename", new Color( 0.2f, 0.22f, 0.26f ) );
		_renameButton.Style.Set( "display", "none" );

		// Inline rename editor (leader only). Persistent panels so the TextEntry keeps its
		// typed text across the 0.5s refreshes.
		_renameRow = new Panel { Parent = _root };
		_renameRow.Style.Set( "flex-direction", "row" );
		_renameRow.Style.Set( "align-items", "center" );
		_renameRow.Style.Set( "gap", "6px" );
		_renameRow.Style.MarginTop = Length.Pixels( 4f );
		_renameRow.Style.Set( "display", "none" );

		_renameEntry = new TextEntry { Parent = _renameRow };
		_renameEntry.Style.Set( "flex-grow", "1" );
		_renameEntry.Style.Height = Length.Pixels( 24f );
		_renameEntry.Style.PaddingLeft = Length.Pixels( 6f );
		_renameEntry.Style.PaddingRight = Length.Pixels( 6f );
		_renameEntry.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.14f );
		_renameEntry.Style.Set( "border-radius", "4px" );
		_renameEntry.Style.Set( "border-width", "1px" );
		_renameEntry.Style.Set( "border-color", "#3a4250" );
		_renameEntry.Style.FontColor = Color.White;
		_renameEntry.Style.FontSize = Length.Pixels( 13f );

		_renameOkButton = MakePersistentButton( _renameRow, "OK", new Color( 0.25f, 0.55f, 0.3f ) );
		_renameCancelButton = MakePersistentButton( _renameRow, "Cancel", new Color( 0.3f, 0.32f, 0.36f ) );

		_memberList = new Panel { Parent = _root };
		_memberList.Style.Set( "flex-direction", "column" );
		_memberList.Style.MarginTop = Length.Pixels( 4f );

		_leaveRow = new Panel { Parent = _root };
		_leaveRow.Style.Set( "flex-direction", "row" );
		_leaveRow.Style.MarginTop = Length.Pixels( 6f );

		var nearbyHeader = AddLabel( _root, "Nearby Players", 15f, new Color( 0.8f, 0.82f, 0.86f ) );
		nearbyHeader.Style.MarginTop = Length.Pixels( 14f );

		// Boxed, fixed-height list — rows are windowed by _nearbyScrollIndex; the right edge is
		// a slim scrollbar (▲ / track+thumb / ▼) that works with the menu's soft cursor.
		var nearbyBox = new Panel { Parent = _root };
		nearbyBox.Style.Set( "flex-direction", "row" );
		nearbyBox.Style.Set( "flex-shrink", "0" );
		nearbyBox.Style.MarginTop = Length.Pixels( 4f );
		nearbyBox.Style.Height = Length.Pixels( NearbyVisibleRows * NearbyRowHeight + 12f );
		nearbyBox.Style.BackgroundColor = new Color( 0.06f, 0.07f, 0.09f, 0.9f );
		nearbyBox.Style.Set( "border-radius", "6px" );
		nearbyBox.Style.Set( "border-width", "1px" );
		nearbyBox.Style.Set( "border-color", "#3a4250" );
		nearbyBox.Style.PaddingLeft = Length.Pixels( 6f );
		nearbyBox.Style.PaddingRight = Length.Pixels( 4f );
		nearbyBox.Style.PaddingTop = Length.Pixels( 6f );
		nearbyBox.Style.PaddingBottom = Length.Pixels( 6f );

		_nearbyRows = new Panel { Parent = nearbyBox };
		_nearbyRows.Style.Set( "flex-direction", "column" );
		_nearbyRows.Style.Set( "flex-grow", "1" );
		_nearbyRows.Style.Set( "overflow", "hidden" );

		var scrollColumn = new Panel { Parent = nearbyBox };
		scrollColumn.Style.Set( "flex-direction", "column" );
		scrollColumn.Style.Set( "flex-shrink", "0" );
		scrollColumn.Style.Width = Length.Pixels( 18f );
		scrollColumn.Style.MarginLeft = Length.Pixels( 4f );

		_nearbyUpButton = MakeScrollButton( scrollColumn, "▲" );
		_nearbyTrack = new Panel { Parent = scrollColumn };
		_nearbyTrack.Style.Set( "position", "relative" );
		_nearbyTrack.Style.Set( "flex-grow", "1" );
		_nearbyTrack.Style.Width = Length.Percent( 100 );
		_nearbyTrack.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.14f );
		_nearbyTrack.Style.Set( "border-radius", "4px" );
		_nearbyTrack.Style.MarginTop = Length.Pixels( 2f );
		_nearbyTrack.Style.MarginBottom = Length.Pixels( 2f );

		_nearbyThumb = new Panel { Parent = _nearbyTrack };
		_nearbyThumb.Style.Set( "position", "absolute" );
		_nearbyThumb.Style.Set( "left", "2px" );
		_nearbyThumb.Style.Set( "right", "2px" );
		_nearbyThumb.Style.BackgroundColor = new Color( 0.4f, 0.44f, 0.52f );
		_nearbyThumb.Style.Set( "border-radius", "4px" );

		_nearbyDownButton = MakeScrollButton( scrollColumn, "▼" );

		var inviteHeader = AddLabel( _root, "Crew Invites", 15f, new Color( 0.8f, 0.82f, 0.86f ) );
		inviteHeader.Style.MarginTop = Length.Pixels( 14f );

		_inviteList = new Panel { Parent = _root };
		_inviteList.Style.Set( "flex-direction", "column" );
		_inviteList.Style.MarginTop = Length.Pixels( 4f );
	}

	public void Tick()
	{
		if ( _root is null || Time.NowDouble < _nextRefreshAt )
			return;

		_nextRefreshAt = Time.NowDouble + 0.5;
		Refresh();
	}

	/// <summary>Soft-cursor Attack1 while the map page is open — OS mouse is Hidden in the menu.</summary>
	public bool TryClickAtScreen( Vector2 screenPos )
	{
		foreach ( var (panel, action) in _clickTargets )
		{
			if ( panel is null || !panel.IsValid() )
				continue;
			if ( !InventoryScreenPointer.PanelBoxContainsScreen( panel, screenPos ) )
				continue;

			action?.Invoke();
			_nextRefreshAt = 0;
			return true;
		}

		return false;
	}

	/// <summary>Menu mouse wheel while the map page is open — scrolls the nearby list window.</summary>
	public void ApplyNearbyWheel( Vector2 wheel )
	{
		// Menu sink already converts to panel direction: positive Y = scroll down.
		var delta = MathF.Abs( wheel.y ) >= MathF.Abs( wheel.x ) ? wheel.y : wheel.x;
		if ( MathF.Abs( delta ) < 0.01f )
			return;

		ScrollNearby( delta > 0f ? 1 : -1 );
	}

	void ScrollNearby( int step )
	{
		var maxIndex = Math.Max( 0, _nearbyCount - NearbyVisibleRows );
		var next = Math.Clamp( _nearbyScrollIndex + step, 0, maxIndex );
		if ( next == _nearbyScrollIndex )
			return;

		_nearbyScrollIndex = next;
		_nextRefreshAt = 0;
	}

	void Refresh()
	{
		var localRoot = _interaction?.GameObject;
		if ( localRoot is null || !localRoot.IsValid() )
			return;

		_clickTargets.Clear();
		_clickTargets.Add( (_nearbyUpButton, () => ScrollNearby( -1 )) );
		_clickTargets.Add( (_nearbyDownButton, () => ScrollNearby( 1 )) );

		var playerCrew = _interaction.Components.Get<PlayerCrew>();
		var localKey = TimeTrialSession.ResolvePlayerKey( localRoot );
		var crew = playerCrew?.GetMyCrew();
		var crewSize = crew?.Members.Count ?? 1;
		var isLeader = crew is not null && crew.LeaderId == localKey;

		_crewCount.Text = $"Crew {crewSize}/{CrewRegistry.MaxCrewSize}";
		_crewName.Text = crew?.Name ?? "Solo";

		if ( crew is null || !isLeader )
			_renaming = false;

		_renameButton.Style.Set( "display", isLeader && !_renaming ? "flex" : "none" );
		_renameRow.Style.Set( "display", _renaming ? "flex" : "none" );

		if ( isLeader )
		{
			if ( _renaming )
			{
				_clickTargets.Add( (_renameOkButton, () =>
				{
					_renaming = false;
					_interaction?.OwnerCrewRename( _renameEntry?.Text ?? "" );
				}) );
				_clickTargets.Add( (_renameCancelButton, () => _renaming = false) );
			}
			else
			{
				var currentName = crew.Name;
				_clickTargets.Add( (_renameButton, () =>
				{
					_renaming = true;
					if ( _renameEntry is not null )
					{
						_renameEntry.Text = currentName;
						_renameEntry.Focus();
					}
				}) );
			}
		}

		_memberList.DeleteChildren( true );
		if ( crew is not null )
		{
			foreach ( var member in crew.Members )
			{
				var isMemberLeader = member.PlayerId == crew.LeaderId;
				var row = AddLabel( _memberList, (isMemberLeader ? "★ " : "") + member.DisplayName, 13f,
					member.PlayerId == localKey ? new Color( 0.95f, 0.9f, 0.6f ) : new Color( 0.85f, 0.87f, 0.9f ) );
				row.Style.MarginBottom = Length.Pixels( 2f );
			}
		}
		else
		{
			AddLabel( _memberList, CrewRegistry.ResolvePawnDisplayName( localRoot ), 13f, new Color( 0.95f, 0.9f, 0.6f ) );
		}

		_leaveRow.DeleteChildren( true );
		if ( crew is not null )
		{
			MakeButton( _leaveRow, "Leave Crew", new Color( 0.55f, 0.25f, 0.22f ),
				() => _interaction?.OwnerCrewLeave() );
		}

		RefreshNearby( playerCrew, localRoot, localKey, crew, crewSize );
		RefreshInvites( playerCrew, crewSize );
	}

	void RefreshNearby( PlayerCrew playerCrew, GameObject localRoot, Guid localKey, CrewRegistry.CrewInfo crew, int crewSize )
	{
		_nearbyRows.DeleteChildren( true );
		var scene = localRoot.Scene.IsValid() ? localRoot.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return;

		var rangeUnits = TerrainWorldUnits.MetersToEngine( NearbyRangeMeters );
		var crewFull = crewSize >= CrewRegistry.MaxCrewSize;

		var candidates = new List<(GameObject Root, Guid Key, float Dist)>();
		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals?.GameObject is not { IsValid: true } root || root == localRoot )
				continue;

			var key = TimeTrialSession.ResolvePlayerKey( root );
			if ( key == default || key == localKey )
				continue;

			var dist = root.WorldPosition.Distance( localRoot.WorldPosition );
			if ( dist > rangeUnits )
				continue;

			candidates.Add( (root, key, dist) );
		}

		candidates.Sort( ( a, b ) => a.Dist.CompareTo( b.Dist ) );
		_nearbyCount = candidates.Count;
		_nearbyScrollIndex = Math.Clamp( _nearbyScrollIndex, 0, Math.Max( 0, _nearbyCount - NearbyVisibleRows ) );

		UpdateNearbyScrollbar();

		if ( candidates.Count == 0 )
		{
			AddLabel( _nearbyRows, "No players nearby.", 12f, new Color( 0.5f, 0.53f, 0.58f ) );
			return;
		}

		var last = Math.Min( candidates.Count, _nearbyScrollIndex + NearbyVisibleRows );
		for ( var i = _nearbyScrollIndex; i < last; i++ )
		{
			var (root, key, dist) = candidates[i];
			var inMyCrew = crew is not null && crew.Members.Exists( m => m.PlayerId == key );
			var meters = TerrainWorldUnits.EngineToMeters( dist );

			var row = new Panel { Parent = _nearbyRows };
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "align-items", "center" );
			row.Style.Set( "justify-content", "space-between" );
			row.Style.Set( "flex-shrink", "0" );
			row.Style.Height = Length.Pixels( NearbyRowHeight );

			AddLabel( row, $"{CrewRegistry.ResolvePawnDisplayName( root )} - {meters:0}m", 13f, new Color( 0.85f, 0.87f, 0.9f ) );

			if ( inMyCrew )
			{
				AddLabel( row, "In crew", 12f, new Color( 0.5f, 0.7f, 0.5f ) );
				continue;
			}

			var alreadyInvited = (playerCrew?.HasPendingInviteTo( key ) ?? false)
			                     || (_recentlyInvitedUntil.TryGetValue( key, out var until )
			                         && Time.NowDouble < until);
			if ( alreadyInvited )
			{
				var invited = MakeButton( row, "Invited", new Color( 0.16f, 0.17f, 0.2f ), null );
				invited.Style.Opacity = 0.5f;
			}
			else
			{
				var targetKey = key;
				var btn = MakeButton( row, "Invite to Crew",
					crewFull ? new Color( 0.16f, 0.17f, 0.2f ) : new Color( 0.2f, 0.45f, 0.85f ),
					crewFull
						? null
						: () =>
						{
							// Grey immediately; the synced outgoing blob takes over within a beat.
							_recentlyInvitedUntil[targetKey] = Time.NowDouble + 4.0;
							_interaction?.OwnerCrewInvite( targetKey );
						} );
				if ( crewFull )
					btn.Style.Opacity = 0.4f;
			}
		}
	}

	void UpdateNearbyScrollbar()
	{
		if ( _nearbyThumb is null )
			return;

		var total = Math.Max( 1, _nearbyCount );
		var visibleFrac = Math.Clamp( NearbyVisibleRows / (float)total, 0.15f, 1f );
		var maxIndex = Math.Max( 1, total - NearbyVisibleRows );
		var topFrac = _nearbyCount > NearbyVisibleRows
			? (_nearbyScrollIndex / (float)maxIndex) * (1f - visibleFrac)
			: 0f;

		_nearbyThumb.Style.Top = Length.Percent( topFrac * 100f );
		_nearbyThumb.Style.Height = Length.Percent( visibleFrac * 100f );

		var canScroll = _nearbyCount > NearbyVisibleRows;
		_nearbyUpButton.Style.Opacity = canScroll && _nearbyScrollIndex > 0 ? 1f : 0.35f;
		_nearbyDownButton.Style.Opacity = canScroll && _nearbyScrollIndex < _nearbyCount - NearbyVisibleRows ? 1f : 0.35f;
	}

	void RefreshInvites( PlayerCrew playerCrew, int crewSize )
	{
		_inviteList.DeleteChildren( true );
		var invites = playerCrew?.GetMyInvites();
		if ( invites is null || invites.Count == 0 )
		{
			AddLabel( _inviteList, "No pending invites.", 12f, new Color( 0.5f, 0.53f, 0.58f ) );
			_confirmLeaveForCrewKey = default;
			return;
		}

		foreach ( var invite in invites )
		{
			AddLabel( _inviteList, $"{invite.InviterName} - join {invite.CrewName}", 13f, new Color( 0.85f, 0.87f, 0.9f ) );

			var row = new Panel { Parent = _inviteList };
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "gap", "6px" );
			row.Style.MarginBottom = Length.Pixels( 6f );

			var key = invite.CrewKey;
			if ( _confirmLeaveForCrewKey == key )
			{
				AddLabel( row, "Leave current crew?", 12f, new Color( 0.95f, 0.8f, 0.5f ) );
				MakeButton( row, "Yes", new Color( 0.25f, 0.55f, 0.3f ), () =>
				{
					_confirmLeaveForCrewKey = default;
					_interaction?.OwnerCrewAcceptInvite( key );
				} );
				MakeButton( row, "No", new Color( 0.55f, 0.25f, 0.22f ), () => _confirmLeaveForCrewKey = default );
			}
			else
			{
				MakeButton( row, "Join", new Color( 0.25f, 0.55f, 0.3f ), () =>
				{
					// Already crewed up: confirm the implicit leave first.
					if ( crewSize >= 2 )
					{
						_confirmLeaveForCrewKey = key;
						return;
					}

					_interaction?.OwnerCrewAcceptInvite( key );
				} );
				MakeButton( row, "Decline", new Color( 0.3f, 0.32f, 0.36f ), () => _interaction?.OwnerCrewDeclineInvite( key ) );
			}
		}
	}

	static Label AddLabel( Panel parent, string text, float size, Color color )
	{
		var label = new Label { Parent = parent, Text = text };
		label.Style.FontColor = color;
		label.Style.FontSize = Length.Pixels( size );
		label.Style.Set( "pointer-events", "none" );
		return label;
	}

	Panel MakeScrollButton( Panel parent, string glyph )
	{
		var btn = new Panel { Parent = parent };
		btn.Style.Set( "flex-shrink", "0" );
		btn.Style.Height = Length.Pixels( 18f );
		btn.Style.Width = Length.Percent( 100 );
		btn.Style.Set( "align-items", "center" );
		btn.Style.Set( "justify-content", "center" );
		btn.Style.BackgroundColor = new Color( 0.2f, 0.22f, 0.26f );
		btn.Style.Set( "border-radius", "4px" );
		btn.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = btn, Text = glyph };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( 10f );
		label.Style.Set( "pointer-events", "none" );
		return btn;
	}

	/// <summary>Built-once button panel; callers register its click target per refresh.</summary>
	static Panel MakePersistentButton( Panel parent, string text, Color background )
	{
		var btn = new Panel { Parent = parent };
		btn.Style.Set( "flex-shrink", "0" );
		btn.Style.PaddingLeft = Length.Pixels( 8f );
		btn.Style.PaddingRight = Length.Pixels( 8f );
		btn.Style.PaddingTop = Length.Pixels( 3f );
		btn.Style.PaddingBottom = Length.Pixels( 3f );
		btn.Style.BackgroundColor = background;
		btn.Style.Set( "border-radius", "4px" );
		btn.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = btn, Text = text };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( 12f );
		label.Style.Set( "pointer-events", "none" );
		return btn;
	}

	/// <summary>Plain panel styled as a button; clicks arrive via <see cref="TryClickAtScreen"/> hit rects.</summary>
	Panel MakeButton( Panel parent, string text, Color background, Action onClick )
	{
		var btn = new Panel { Parent = parent };
		btn.Style.PaddingLeft = Length.Pixels( 8f );
		btn.Style.PaddingRight = Length.Pixels( 8f );
		btn.Style.PaddingTop = Length.Pixels( 4f );
		btn.Style.PaddingBottom = Length.Pixels( 4f );
		btn.Style.BackgroundColor = background;
		btn.Style.Set( "border-radius", "4px" );
		btn.Style.Set( "pointer-events", "none" );

		var label = new Label { Parent = btn, Text = text };
		label.Style.FontColor = Color.White;
		label.Style.FontSize = Length.Pixels( 12f );
		label.Style.Set( "pointer-events", "none" );

		if ( onClick is not null )
			_clickTargets.Add( (btn, onClick) );

		return btn;
	}
}
