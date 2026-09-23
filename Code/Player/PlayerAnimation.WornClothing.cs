using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Dresses the citizen body in the clothing of the worn paperdoll pieces
/// (<see cref="PlayerEquipment.NetworkedWornClothing"/>). Presentation only: every peer applies the
/// same host string, so the owner, the host and proxies all show the same outfit. Ticks from OnUpdate
/// and OnPreRender (a proxy's OnUpdate may not run) and re-dresses only when the string changes.
/// </summary>
public sealed partial class PlayerAnimation
{
	PlayerEquipment _clothingEquipment;
	string _appliedClothingKey;
	readonly List<GameObject> _wornClothingObjects = new();
	readonly HashSet<string> _warnedClothingPaths = new( StringComparer.OrdinalIgnoreCase );

	void TickWornClothing()
	{
		if ( _clothingEquipment is null || !_clothingEquipment.IsValid() )
			_clothingEquipment = Components.Get<PlayerEquipment>();

		if ( _clothingEquipment is null )
			return;

		var key = _clothingEquipment.NetworkedWornClothing ?? string.Empty;
		if ( string.Equals( key, _appliedClothingKey, StringComparison.Ordinal ) )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		_appliedClothingKey = key;
		DressBody( body, key );
	}

	void DressBody( SkinnedModelRenderer body, string key )
	{
		for ( var i = 0; i < _wornClothingObjects.Count; i++ )
		{
			var go = _wornClothingObjects[i];
			if ( go is not null && go.IsValid() )
				go.Destroy();
		}

		_wornClothingObjects.Clear();

		var container = new ClothingContainer();
		if ( !string.IsNullOrWhiteSpace( key ) )
		{
			foreach ( var path in key.Split( '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			{
				if ( ResourceLibrary.TryGet<Clothing>( path, out var clothing ) && clothing is not null )
				{
					container.Add( clothing );
					continue;
				}

				if ( _warnedClothingPaths.Add( path ) )
					Log.Warning( $"[PlayerAnimation] {GameObject.Name}: clothing resource not found: {path}" );
			}
		}

		var bodyObject = body.GameObject;
		var before = new HashSet<GameObject>( bodyObject.Children );

		// Empty container = strip the outfit and restore the body groups it hid.
		container.Apply( body );

		// Local presentation only — a plain child of a networked pawn replicates and doubles up on joiners.
		foreach ( var child in bodyObject.Children )
		{
			if ( child is null || !child.IsValid() || before.Contains( child ) )
				continue;

			child.NetworkMode = NetworkMode.Never;
			child.Flags |= GameObjectFlags.NotSaved;
			_wornClothingObjects.Add( child );
		}
	}
}
