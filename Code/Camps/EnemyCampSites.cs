using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Generated enemy camps across the land (Mark 2026-11-02: "scav camps should be placed around the world with the procedural
/// generation. I dont see any."). A biome population row with <c>"camp": "&lt;enemy_camps.json id&gt;"</c> is a camp row: its
/// world grid of <c>spacingMeters</c> cells (the same grid and <c>spawnWeight</c> roll as every population row) gives each
/// cell up to <see cref="CandidatesPerCell"/> seeded candidate sites, and the first one that is open, flat, dry and far enough
/// from spawn is that cell's camp (<see cref="TryPlanSite"/> — pure, so every chunk agrees which chunk owns it). The chunk
/// holding the site stamps it once per session through <see cref="EnemyCampSpawner"/> when its population runs (host, inside
/// Collision Range, like every scav); each guard is its own population slot <c>{entityId}:c:{cx}:{cy}:{i}</c>, so the guards
/// respawn after <c>respawnDelaySeconds</c>, sleep into <see cref="EntityChunkMemory"/> with their camp leash and come back
/// through the existing systems. A chunk reload re-spawns only the guards whose slot is free; the pieces stay for the session.
///
/// Site rules (metres): inside the land disc, at least <see cref="SpawnClearanceMeters"/> from the world spawn (the origin);
/// the centre on land, not a river, beach or wet ground; 8 points at <see cref="FlatRadiusMeters"/> within
/// <see cref="FlatToleranceMeters"/> of the centre height (≈ 7°) and dry; 8 points at <see cref="DryRadiusMeters"/> on land, no
/// river or wet band (water stays ~35 m away); in a biome with forest clearings (Clover Hills) the centre and a
/// <see cref="ClearingRadiusMeters"/> ring must be clearing, so no trunk stands in the camp.
/// </summary>
public static class EnemyCampSites
{
	public const int CandidatesPerCell = 4;
	public const float SpawnClearanceMeters = 300f;
	public const float FlatRadiusMeters = 12f;
	public const float FlatToleranceMeters = 1.5f;
	public const float DryRadiusMeters = 35f;
	public const float ClearingRadiusMeters = 14f;

	/// <summary>World camps stamped this session (host): slot prefix → camp root. Cleared with the world (<see cref="ClearWorldCamps"/>).</summary>
	static readonly Dictionary<string, EnemyCamp> Stamped = new( StringComparer.Ordinal );

	/// <summary>A world camp's site: metres, ground height from the sampler, facing.</summary>
	public readonly struct Site
	{
		public float X { get; init; }
		public float Y { get; init; }
		public float HeightMeters { get; init; }
		public float YawDegrees { get; init; }
		public int CellX { get; init; }
		public int CellY { get; init; }
	}

	/// <summary>The camp row's cells whose site lands in this chunk: stamp the camp the first time, re-fill free guard slots after.</summary>
	public static int ScatterCampEntry(
		GameObject chunkRoot,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int worldSeed,
		TerrainPreviewBiomeId biome,
		BiomePopulationEntry entry,
		TerrainVegetationScatter.ForestPatchField forest )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() || !EnemyCampCatalog.TryGet( entry.CampId, out var camp ) )
			return 0;

		var spacing = Math.Clamp( entry.SpacingMeters, 64f, 4000f );
		var cellMinX = (int)MathF.Floor( chunkMinX / spacing );
		var cellMaxX = (int)MathF.Floor( (chunkMinX + chunkSize - 1e-3f) / spacing );
		var cellMinY = (int)MathF.Floor( chunkMinY / spacing );
		var cellMaxY = (int)MathF.Floor( (chunkMinY + chunkSize - 1e-3f) / spacing );
		var stamped = 0;

		for ( var cy = cellMinY; cy <= cellMaxY; cy++ )
		{
			for ( var cx = cellMinX; cx <= cellMaxX; cx++ )
			{
				if ( !TryPlanSite( settings, backend, worldSeed, entry, biome, forest, cx, cy, out var site ) )
					continue;
				if ( site.X < chunkMinX || site.X >= chunkMinX + chunkSize || site.Y < chunkMinY || site.Y >= chunkMinY + chunkSize )
					continue;   // another chunk owns it

				var scene = chunkRoot.Scene;
				var prefix = SlotPrefix( entry, cx, cy );
				var origin = TerrainWorldUnits.MetersToEngine( new Vector3( site.X, site.Y, site.HeightMeters ) );
				if ( Stamped.TryGetValue( prefix, out var root ) && root.IsValid() )
				{
					EnemyCampSpawner.SpawnGuards( scene, camp, root.WorldPosition, site.YawDegrees, prefix, entry );
					continue;
				}

				var spawned = EnemyCampSpawner.TrySpawn( scene, camp.Id, origin, site.YawDegrees, prefix, entry );
				if ( spawned is null )
					continue;
				Stamped[prefix] = spawned;
				stamped++;
			}
		}

		return stamped;
	}

	static string SlotPrefix( in BiomePopulationEntry entry, int cx, int cy ) => $"{entry.EntityId}:c:{cx}:{cy}";

	/// <summary>
	/// The camp of one world cell, or false: the cell's <c>spawnWeight</c> roll, then up to <see cref="CandidatesPerCell"/>
	/// seeded points inside the cell tried in order against the site rules (class summary). Pure and deterministic (sampler +
	/// hashes only), so the chunk that owns the result and its neighbours all agree.
	/// </summary>
	public static bool TryPlanSite(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int worldSeed,
		in BiomePopulationEntry entry,
		TerrainPreviewBiomeId biome,
		in TerrainVegetationScatter.ForestPatchField forest,
		int cx,
		int cy,
		out Site site )
	{
		site = default;
		var spacing = Math.Clamp( entry.SpacingMeters, 64f, 4000f );
		var salt = worldSeed + 4700;
		if ( TerrainPreviewNoise.Hash01( salt, cx, cy ) > Math.Clamp( entry.SpawnWeight, 0f, 1f ) )
			return false;

		var landRadius = settings.LandRadiusMeters;
		var spawnSq = SpawnClearanceMeters * SpawnClearanceMeters;
		var useClearing = forest.Valid && forest.BiomeId == biome;
		for ( var k = 0; k < CandidatesPerCell; k++ )
		{
			var x = (cx + 0.15f + (0.7f * TerrainPreviewNoise.Hash01( salt + 11 + (k * 2), cx, cy ))) * spacing;
			var y = (cy + 0.15f + (0.7f * TerrainPreviewNoise.Hash01( salt + 12 + (k * 2), cx, cy ))) * spacing;
			if ( (x * x) + (y * y) > landRadius * landRadius || (x * x) + (y * y) < spawnSq )
				continue;
			if ( useClearing && !forest.IsClearingDisc( x, y, ClearingRadiusMeters ) )
				continue;

			var center = backend.Sample( settings, x, y );
			if ( !IsDryLand( center ) )
				continue;
			if ( TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, center, x, y ).BiomeId != biome )
				continue;
			if ( !RingOk( settings, backend, x, y, center.HeightMeters ) )
				continue;

			site = new Site
			{
				X = x,
				Y = y,
				HeightMeters = center.HeightMeters,
				YawDegrees = TerrainPreviewNoise.Hash01( salt + 30, cx, cy ) * 360f,
				CellX = cx,
				CellY = cy,
			};
			return true;
		}

		return false;
	}

	static bool IsDryLand( in TerrainPreviewSample s )
		=> s.IsInsideWorld && s.IsOnLand && s.OceanHeight01 <= 0.5f && !s.RiverWater && s.Wet01 < 0.02f && s.Beach01 < 0.02f;

	/// <summary>Flat and dry inside <see cref="FlatRadiusMeters"/>, still land with no river or wet band out to <see cref="DryRadiusMeters"/>.</summary>
	static bool RingOk( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, float x, float y, float h0 )
	{
		for ( var k = 0; k < 8; k++ )
		{
			var a = k * (MathF.Tau / 8f);
			var dx = MathF.Cos( a );
			var dy = MathF.Sin( a );
			var near = backend.Sample( settings, x + (dx * FlatRadiusMeters), y + (dy * FlatRadiusMeters) );
			if ( !IsDryLand( near ) || MathF.Abs( near.HeightMeters - h0 ) > FlatToleranceMeters )
				return false;
			var far = backend.Sample( settings, x + (dx * DryRadiusMeters), y + (dy * DryRadiusMeters) );
			if ( !far.IsInsideWorld || !far.IsOnLand || far.OceanHeight01 > 0.5f || far.RiverWater || far.Wet01 > 0.05f )
				return false;
		}
		return true;
	}

	/// <summary>World reset (host): every generated camp's root and pieces go; the next populate stamps them again.</summary>
	public static void ClearWorldCamps()
	{
		foreach ( var root in Stamped.Values )
		{
			if ( root is null || !root.IsValid() )
				continue;
			foreach ( var piece in root.Pieces )
			{
				if ( piece is not null && piece.IsValid() )
					piece.Destroy();
			}
			root.GameObject.Destroy();
		}
		Stamped.Clear();
	}

	/// <summary>Dev: every planned camp site within <paramref name="radiusMeters"/> of a point (metres), nearest first.</summary>
	public static List<Site> PlanSitesNear( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, int worldSeed, TerrainVegetationScatter.ForestPatchField forest, float xMeters, float yMeters, float radiusMeters )
	{
		var result = new List<Site>();
		foreach ( TerrainPreviewBiomeId biome in Enum.GetValues<TerrainPreviewBiomeId>() )
		{
			if ( biome is TerrainPreviewBiomeId.None or TerrainPreviewBiomeId.Water )
				continue;
			foreach ( var entry in BiomePopulationCatalog.GetEntries( biome ) )
			{
				if ( entry.CampId is null )
					continue;
				var spacing = Math.Clamp( entry.SpacingMeters, 64f, 4000f );
				var lo = (int)MathF.Floor( (xMeters - radiusMeters) / spacing );
				var hi = (int)MathF.Floor( (xMeters + radiusMeters) / spacing );
				var loY = (int)MathF.Floor( (yMeters - radiusMeters) / spacing );
				var hiY = (int)MathF.Floor( (yMeters + radiusMeters) / spacing );
				for ( var cy = loY; cy <= hiY; cy++ )
				{
					for ( var cx = lo; cx <= hi; cx++ )
					{
						if ( !TryPlanSite( settings, backend, worldSeed, entry, biome, forest, cx, cy, out var site ) )
							continue;
						var dx = site.X - xMeters;
						var dy = site.Y - yMeters;
						if ( (dx * dx) + (dy * dy) <= radiusMeters * radiusMeters )
							result.Add( site );
					}
				}
			}
		}
		result.Sort( ( a, b ) => ((a.X - xMeters) * (a.X - xMeters) + (a.Y - yMeters) * (a.Y - yMeters)).CompareTo( (b.X - xMeters) * (b.X - xMeters) + (b.Y - yMeters) * (b.Y - yMeters) ) );
		return result;
	}
}
