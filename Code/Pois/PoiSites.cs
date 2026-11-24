using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Where the generated world puts its structure POIs (Mark 2026-11-19: "discoverable POIs in the form of abandoned
/// seemingly player-built structures. When the player enters the structure they can find a chest, a campfire thats not
/// lit, a roasting spit, and a bed with no assignment"). Every <see cref="PoiStructureData"/> row of <c>pois.json</c> is a
/// grid of <c>spacingMeters</c> world cells; a cell rolls the row's <c>chance</c>, then the shared cell planner
/// (<see cref="EnemyCampSites.TryPlanCell"/>: up to four seeded points against the open / flat / dry / clearing site
/// rules) names its site, and <see cref="LayoutFor"/> picks which of the row's layouts stands there. Pure and seeded, so
/// every chunk agrees which chunk owns a site; that chunk's population pass (host, inside Collision Range, collider up)
/// stamps it once per session through <see cref="PoiSpawner"/>. The pieces stay for the session and go with the world
/// (<see cref="ClearWorldPois"/>). Before that, from <see cref="PoiFarView.DrawMeters"/> in, <see cref="PoiFarView"/> draws a
/// render-only far version of the same layout at the same site.
/// </summary>
public static class PoiSites
{
	/// <summary>Base of every POI hash (plus the row's <c>seedSalt</c>); the scav camps use 4700.</summary>
	const int SeedBase = 5300;

	/// <summary>Structures stamped this session (host): site key -> its pieces. Cleared with the world.</summary>
	static readonly Dictionary<string, List<GameObject>> Stamped = new( StringComparer.Ordinal );

	/// <summary>A planned structure: where, facing, and which layout.</summary>
	public readonly struct Site
	{
		public float X { get; init; }
		public float Y { get; init; }
		public float HeightMeters { get; init; }
		public float YawDegrees { get; init; }
		public string StructureId { get; init; }
		public string LayoutId { get; init; }
		public string Key { get; init; }
	}

	/// <summary>Host, per chunk population pass: stamp every structure whose site lies in this chunk and is not standing yet.</summary>
	public static int ScatterChunk(
		GameObject chunkRoot,
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		float chunkSize,
		float chunkMinX,
		float chunkMinY,
		int worldSeed,
		TerrainVegetationScatter.ForestPatchField forest )
	{
		if ( chunkRoot is null || !chunkRoot.IsValid() )
			return 0;

		var stamped = 0;
		foreach ( var row in PoiCatalog.Structures )
		{
			var spacing = Math.Clamp( row.SpacingMeters, 64f, 4000f );
			var cellMinX = (int)MathF.Floor( chunkMinX / spacing );
			var cellMaxX = (int)MathF.Floor( (chunkMinX + chunkSize - 1e-3f) / spacing );
			var cellMinY = (int)MathF.Floor( chunkMinY / spacing );
			var cellMaxY = (int)MathF.Floor( (chunkMinY + chunkSize - 1e-3f) / spacing );
			for ( var cy = cellMinY; cy <= cellMaxY; cy++ )
			{
				for ( var cx = cellMinX; cx <= cellMaxX; cx++ )
				{
					if ( !TryPlanSite( settings, backend, worldSeed, row, forest, cx, cy, out var site ) )
						continue;
					if ( site.X < chunkMinX || site.X >= chunkMinX + chunkSize || site.Y < chunkMinY || site.Y >= chunkMinY + chunkSize )
						continue;   // another chunk owns it
					if ( Stamped.ContainsKey( site.Key ) )
						continue;

					var origin = TerrainWorldUnits.MetersToEngine( new Vector3( site.X, site.Y, site.HeightMeters ) );
					var pieces = PoiSpawner.TrySpawn( chunkRoot.Scene, site.LayoutId, origin, site.YawDegrees, site.Key );
					if ( pieces.Count == 0 )
						continue;
					Stamped[site.Key] = pieces;
					stamped++;
				}
			}
		}

		return stamped;
	}

	/// <summary>The structure of one world cell of a row, or false. Pure and deterministic (sampler + hashes only).</summary>
	public static bool TryPlanSite(
		TerrainPreviewSettings settings,
		ITerrainPreviewBackend backend,
		int worldSeed,
		PoiStructureData row,
		in TerrainVegetationScatter.ForestPatchField forest,
		int cx,
		int cy,
		out Site site )
	{
		site = default;
		var biome = row.ResolveBiome();
		if ( biome is TerrainPreviewBiomeId.None or TerrainPreviewBiomeId.Water || row.Layouts is not { Count: > 0 } )
			return false;

		var salt = worldSeed + SeedBase + row.SeedSalt;
		if ( !EnemyCampSites.TryPlanCell( settings, backend, salt, row.SpacingMeters, row.Chance, biome, forest, cx, cy, out var cell ) )
			return false;

		site = new Site
		{
			X = cell.X,
			Y = cell.Y,
			HeightMeters = cell.HeightMeters,
			YawDegrees = cell.YawDegrees,
			StructureId = row.Id,
			LayoutId = LayoutFor( row, salt, cx, cy ),
			Key = $"{row.Id}:{cx}:{cy}",
		};
		return true;
	}

	/// <summary>Which of the row's layouts a cell gets: by hash, so the same world always builds the same one.</summary>
	static string LayoutFor( PoiStructureData row, int salt, int cx, int cy )
	{
		var pick = (int)(TerrainPreviewNoise.Hash01( salt + 40, cx, cy ) * row.Layouts.Count);
		return row.Layouts[Math.Clamp( pick, 0, row.Layouts.Count - 1 )];
	}

	/// <summary>World reset (host): every stamped structure's pieces go; the next populate stamps them again.</summary>
	public static void ClearWorldPois()
	{
		foreach ( var pieces in Stamped.Values )
		{
			foreach ( var piece in pieces )
			{
				if ( piece is not null && piece.IsValid() )
					piece.Destroy();
			}
		}
		Stamped.Clear();
	}

	/// <summary>Dev: every planned structure site within <paramref name="radiusMeters"/> of a point (metres).</summary>
	public static List<Site> PlanSitesNear( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, int worldSeed, TerrainVegetationScatter.ForestPatchField forest, float xMeters, float yMeters, float radiusMeters )
	{
		var result = new List<Site>();
		foreach ( var row in PoiCatalog.Structures )
		{
			var spacing = Math.Clamp( row.SpacingMeters, 64f, 4000f );
			var loX = (int)MathF.Floor( (xMeters - radiusMeters) / spacing );
			var hiX = (int)MathF.Floor( (xMeters + radiusMeters) / spacing );
			var loY = (int)MathF.Floor( (yMeters - radiusMeters) / spacing );
			var hiY = (int)MathF.Floor( (yMeters + radiusMeters) / spacing );
			for ( var cy = loY; cy <= hiY; cy++ )
			{
				for ( var cx = loX; cx <= hiX; cx++ )
				{
					if ( !TryPlanSite( settings, backend, worldSeed, row, forest, cx, cy, out var site ) )
						continue;
					var dx = site.X - xMeters;
					var dy = site.Y - yMeters;
					if ( (dx * dx) + (dy * dy) <= radiusMeters * radiusMeters )
						result.Add( site );
				}
			}
		}
		return result;
	}
}
