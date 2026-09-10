using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// One square of tilled soil (host-spawned from <c>prefabs/farming/tilled_soil.prefab</c> by
/// <see cref="FarmingAuthority.TryTill"/>). Tiles sit on a world grid of
/// <see cref="FarmingRules.TileSizeMeters"/> so neighbouring tills read as one field. Seeds may
/// only be sown while the aim point is inside a tile.
/// </summary>
[Title( "Tilled Soil" )]
public sealed class TilledSoil : Component
{
	public const string Tag = "tilledsoil";

	static readonly List<TilledSoil> Active = new();

	public static IReadOnlyList<TilledSoil> All => Active;

	/// <summary>Tile edge length in engine units (converted once from <see cref="FarmingRules.TileSizeMeters"/>).</summary>
	public static float TileSizeUnits => TerrainWorldUnits.MetersToEngine( FarmingRules.TileSizeMeters );

	protected override void OnEnabled()
	{
		base.OnEnabled();
		GameObject.Tags.Add( Tag );
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Active.Remove( this );
		base.OnDestroy();
	}

	/// <summary>Top surface height — plants and ghosts sit here.</summary>
	public float SurfaceZ
	{
		get
		{
			var collider = Components.Get<BoxCollider>( FindMode.EverythingInSelf );
			if ( collider is null )
				return WorldPosition.z;

			return WorldPosition.z + collider.Center.z + collider.Scale.z * 0.5f;
		}
	}

	/// <summary>Horizontal containment test against this tile's square.</summary>
	public bool ContainsXY( Vector3 worldPoint )
	{
		var half = TileSizeUnits * 0.5f;
		var d = worldPoint - WorldPosition;
		return Math.Abs( d.x ) <= half && Math.Abs( d.y ) <= half;
	}

	/// <summary>Snap any world point to the centre of the grid cell it falls in (z untouched).</summary>
	public static Vector3 SnapToGrid( Vector3 worldPoint )
	{
		var size = TileSizeUnits;
		var x = (MathF.Floor( worldPoint.x / size ) + 0.5f) * size;
		var y = (MathF.Floor( worldPoint.y / size ) + 0.5f) * size;
		return new Vector3( x, y, worldPoint.z );
	}

	/// <summary>Tile whose grid cell holds <paramref name="worldPoint"/>, if one has been tilled there.</summary>
	public static bool TryFindAt( Vector3 worldPoint, out TilledSoil tile )
	{
		tile = null;
		for ( var i = 0; i < Active.Count; i++ )
		{
			var candidate = Active[i];
			if ( candidate is null || !candidate.IsValid() || !candidate.GameObject.IsValid() )
				continue;

			if ( !candidate.ContainsXY( worldPoint ) )
				continue;

			tile = candidate;
			return true;
		}

		return false;
	}

	/// <summary>Walk up from a trace hit to the owning tile.</summary>
	public static bool TryFindOnHierarchy( GameObject hit, out TilledSoil tile )
	{
		tile = null;
		for ( var go = hit; go.IsValid(); go = go.Parent )
		{
			var c = go.Components.Get<TilledSoil>();
			if ( c is null || !c.Enabled )
				continue;

			tile = c;
			return true;
		}

		return false;
	}
}
