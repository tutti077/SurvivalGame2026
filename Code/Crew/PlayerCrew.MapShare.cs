using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Coop map sharing (see <see cref="CrewMapShare"/>): the owner mirrors its "show my location"
/// setting and its pins onto two <c>[Sync]</c> properties every half second when they changed, so
/// crew mates can draw this player and their pins on their own maps.
/// </summary>
public sealed partial class PlayerCrew
{
	/// <summary>Owner → everyone: crew mates may draw this pawn on their maps.</summary>
	[Sync] public bool ShareLocation { get; set; } = true;

	/// <summary>Owner → everyone: this player's pins (<see cref="CrewMapShare.EncodePins"/>).</summary>
	[Sync] public string SharedPinsBlob { get; set; } = string.Empty;

	double _nextSharePushAt;
	int _pushedMarkupVersion = -1;
	string _decodedPinsFrom;
	readonly List<CrewMapShare.RemotePin> _decodedPins = new();

	/// <summary>This player's shared pins, decoded once per synced change.</summary>
	public IReadOnlyList<CrewMapShare.RemotePin> SharedPins
	{
		get
		{
			var blob = SharedPinsBlob ?? string.Empty;
			if ( !ReferenceEquals( _decodedPinsFrom, blob ) && !string.Equals( _decodedPinsFrom, blob, StringComparison.Ordinal ) )
			{
				CrewMapShare.DecodePins( blob, _decodedPins );
				_decodedPinsFrom = blob;
			}

			return _decodedPins;
		}
	}

	/// <summary>Owner side, throttled: push the local map settings + pins when they changed.</summary>
	void TickOwnerMapShare()
	{
		if ( Time.NowDouble < _nextSharePushAt )
			return;

		_nextSharePushAt = Time.NowDouble + 0.5;
		if ( GameObject.IsProxy )
			return;

		var vitals = Components.Get<PlayerVitals>();
		if ( vitals is null || !vitals.IsLocalInputOwnedPawn() )
			return;

		LocalMapMarkup.EnsureLoaded();
		var share = LocalMapMarkup.ShareLocation;
		if ( ShareLocation != share )
			ShareLocation = share;

		if ( _pushedMarkupVersion == LocalMapMarkup.Version )
			return;

		_pushedMarkupVersion = LocalMapMarkup.Version;
		var blob = CrewMapShare.EncodePins( LocalMapMarkup.Pins );
		if ( !string.Equals( blob, SharedPinsBlob, StringComparison.Ordinal ) )
			SharedPinsBlob = blob;
	}
}
