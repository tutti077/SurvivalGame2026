using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Smoke columns seen from far off (Mark 2026-11-23: "make an LOD of the smoke poi so that we can see it from 1000m away",
/// then "make the LOD smoke stack move even from far away. When it comes in range dont make it just pop in, make it fade in").
/// <para>A far-ring landmark whose prefab carries a <see cref="SmokeColumn"/> (the crashed pod) is planned by the far-tree
/// ring like a spire, but it draws no model there: while its chunk is inside Far Trees - Range and its real object does not
/// stand yet, a bare object with only the column stands at the site - the prefab's own column, copied property for
/// property, with Fade In on. The column is a function of its position and the scene clock, so when the real pod arrives
/// (Tree Objects Range) its column is already the same column and the stand-in is simply removed.</para>
/// <para>Sites come with the chunk's far plan (<see cref="BuildFarBatches"/>); the stand-ins follow the ring's own rebuild
/// (<see cref="BuildFarCommandList"/>), so nothing here runs per frame. Local on every machine, never saved or networked.</para>
/// </summary>
public sealed partial class TerrainWorldManager
{
	/// <summary>One smoke site of a far chunk: the landmark's prefab and where it stands (engine units).</summary>
	readonly record struct FarSmokeSite( string PrefabPath, Transform Transform );

	readonly Dictionary<TerrainChunkCoord, List<FarSmokeSite>> _farSmokeSites = new();
	readonly Dictionary<TerrainChunkCoord, List<GameObject>> _farSmokeObjects = new();
	readonly HashSet<TerrainChunkCoord> _farSmokeWanted = new();
	readonly List<TerrainChunkCoord> _farSmokeScratch = new();
	/// <summary>Prefab path → its column template, or null when the prefab has none. Resolved once per prefab.</summary>
	readonly Dictionary<string, SmokeColumn> _farSmokeTemplates = new( StringComparer.OrdinalIgnoreCase );

	SmokeColumn FarSmokeTemplate( string prefabPath )
	{
		if ( string.IsNullOrEmpty( prefabPath ) )
			return null;
		if ( _farSmokeTemplates.TryGetValue( prefabPath, out var cached ) )
			return cached;
		var template = GameObject.GetPrefab( prefabPath );
		var column = template is not null && template.IsValid
			? template.Components.Get<SmokeColumn>( FindMode.EverythingInSelfAndDescendants )
			: null;
		_farSmokeTemplates[prefabPath] = column;
		return column;
	}

	bool IsFarSmokePrefab( string prefabPath ) => FarSmokeTemplate( prefabPath ) is not null;

	/// <summary>A chunk's far plan landed (or left): remember its smoke sites.</summary>
	void SetFarSmokeSites( TerrainChunkCoord coord, List<FarSmokeSite> sites )
	{
		if ( sites is { Count: > 0 } )
		{
			_farSmokeSites[coord] = sites;
			return;
		}
		if ( _farSmokeSites.Remove( coord ) )
			DestroyFarSmoke( coord );
	}

	/// <summary>The ring's rebuild found this chunk in range without its real objects: its columns should stand.</summary>
	void WantFarSmoke( TerrainChunkCoord coord )
	{
		if ( _farSmokeSites.ContainsKey( coord ) )
			_farSmokeWanted.Add( coord );
	}

	/// <summary>End of the ring's rebuild: stand the wanted columns, remove the rest.</summary>
	void SyncFarSmoke()
	{
		if ( _farSmokeObjects.Count > 0 )
		{
			_farSmokeScratch.Clear();
			foreach ( var coord in _farSmokeObjects.Keys )
			{
				if ( !_farSmokeWanted.Contains( coord ) )
					_farSmokeScratch.Add( coord );
			}
			foreach ( var coord in _farSmokeScratch )
				DestroyFarSmoke( coord );
		}

		foreach ( var coord in _farSmokeWanted )
		{
			if ( _farSmokeObjects.ContainsKey( coord ) || !_farSmokeSites.TryGetValue( coord, out var sites ) )
				continue;
			var objects = new List<GameObject>( sites.Count );
			foreach ( var site in sites )
			{
				var source = FarSmokeTemplate( site.PrefabPath );
				if ( source is null )
					continue;
				var go = new GameObject( true, "far_smoke" );
				go.NetworkMode = NetworkMode.Never;
				go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
				go.WorldPosition = site.Transform.Position;
				go.WorldRotation = site.Transform.Rotation;
				var column = go.Components.Create<SmokeColumn>( false );
				column.OriginMeters = source.OriginMeters;
				column.HeightMeters = source.HeightMeters;
				column.RiseSeconds = source.RiseSeconds;
				column.PuffCount = source.PuffCount;
				column.BaseDiameterMeters = source.BaseDiameterMeters;
				column.TopDiameterMeters = source.TopDiameterMeters;
				column.LeanMeters = source.LeanMeters;
				column.WanderMeters = source.WanderMeters;
				column.PuffModel = source.PuffModel;
				column.PuffMaterial = source.PuffMaterial;
				column.FootTint = source.FootTint;
				column.TopTint = source.TopTint;
				column.StopWhenMinedOut = false;
				column.FadeIn = true;
				column.Enabled = true;
				objects.Add( go );
			}
			_farSmokeObjects[coord] = objects;
		}
		_farSmokeWanted.Clear();
	}

	void DestroyFarSmoke( TerrainChunkCoord coord )
	{
		if ( !_farSmokeObjects.Remove( coord, out var objects ) )
			return;
		foreach ( var go in objects )
		{
			if ( go.IsValid() )
				go.Destroy();
		}
	}

	void ClearFarSmoke()
	{
		foreach ( var objects in _farSmokeObjects.Values )
		{
			foreach ( var go in objects )
			{
				if ( go.IsValid() )
					go.Destroy();
			}
		}
		_farSmokeObjects.Clear();
		_farSmokeSites.Clear();
		_farSmokeWanted.Clear();
	}
}
