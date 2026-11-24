using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Points of interest from <c>data/pois.json</c> (Mark 2026-11-19: "POIs have a few different ways to spawn. Discoverable
/// POIs, generated at world generation: abandoned buildings, hidden caves etc. Distant + visible POIs: a trail of smoke
/// that trails into the air"). Two kinds today:
/// <list type="bullet">
/// <item><see cref="PoiStructureData"/> - discoverable: a grid of world cells, each stamping one of its
/// <see cref="PoiLayoutData"/> (build pieces) on open, flat, dry ground (<see cref="PoiSites"/>, <see cref="PoiSpawner"/>).</item>
/// <item><see cref="PoiLandmarkData"/> - distant + visible: a prefab the terrain scatter plants with the large trees so it
/// (and the smoke over it) exists far out (<c>TerrainWorldManager.Pois.cs</c>).</item>
/// </list>
/// </summary>
public sealed class PoiStructureData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>A <see cref="TerrainPreviewBiomeId"/> name ("CloverHills").</summary>
	public string Biome { get; set; } = "CloverHills";
	/// <summary>Side of one world cell in metres: at most one structure per cell.</summary>
	public float SpacingMeters { get; set; } = 640f;
	/// <summary>Chance (0-1) a cell holds one, before the site rules.</summary>
	public float Chance { get; set; } = 0.7f;
	/// <summary>Added to the world seed for every hash of this row, so two rows (and the scav camps) never roll the same cells and points.</summary>
	public int SeedSalt { get; set; }
	/// <summary>The <see cref="PoiLayoutData"/> ids a cell picks from, by hash.</summary>
	public List<string> Layouts { get; set; } = new();

	public TerrainPreviewBiomeId ResolveBiome() =>
		!string.IsNullOrWhiteSpace( Biome ) && Enum.TryParse( Biome.Trim(), ignoreCase: true, out TerrainPreviewBiomeId parsed )
			? parsed
			: TerrainPreviewBiomeId.None;
}

/// <summary>One structure: build pieces as offsets from its origin, listed bottom-up so each stands on what is already there.</summary>
public sealed class PoiLayoutData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public List<PoiPieceData> Pieces { get; set; } = new();
}

/// <summary>A build piece in a POI layout. Offsets are layout-local metres in the build kit's space (<see cref="PoiSpawner.ToWorldOffset"/>).</summary>
public sealed class PoiPieceData
{
	public string PieceId { get; set; } = string.Empty;
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	/// <summary>Yaw relative to the structure's facing.</summary>
	public float YawDegrees { get; set; }
	/// <summary>A container piece (the chest): the <see cref="PoiLootTable"/> id it is filled from when the structure is stamped. Empty = left empty.</summary>
	public string Loot { get; set; }
}

/// <summary>A scattered prefab POI (the crashed stasis pod).</summary>
public sealed class PoiLandmarkData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	public string Biome { get; set; } = "CloverHills";
	public string Prefab { get; set; } = string.Empty;
	/// <summary>Chance (0-1) one 64 m terrain chunk of the biome holds it, before the clearing rule.</summary>
	public float ChancePerChunk { get; set; } = 0.02f;
	public int SeedSalt { get; set; }
	/// <summary>&gt; 0 = only where no large tree stands within this many metres.</summary>
	public float ClearingRadiusMeters { get; set; }
	/// <summary>&gt; 0 = the ground this far round it is bare soil with no grass (burnt ground under the wreck).</summary>
	public float BareGroundRadiusMeters { get; set; }

	public TerrainPreviewBiomeId ResolveBiome() =>
		!string.IsNullOrWhiteSpace( Biome ) && Enum.TryParse( Biome.Trim(), ignoreCase: true, out TerrainPreviewBiomeId parsed )
			? parsed
			: TerrainPreviewBiomeId.None;
}

/// <summary>What a POI chest can hold: <see cref="Stacks"/> different items rolled from <see cref="Items"/>.</summary>
public sealed class PoiLootTable
{
	public string Id { get; set; } = string.Empty;
	/// <summary>[min, max] stacks per chest.</summary>
	public int[] Stacks { get; set; } = { 2, 4 };
	public List<PoiLootItem> Items { get; set; } = new();

	/// <summary>The stacks of one chest. Seeded by the site, so a world's chest holds the same thing every session; no item twice.</summary>
	public List<(string ResourceId, int Count)> Roll( int seed )
	{
		var result = new List<(string, int)>();
		if ( Items is not { Count: > 0 } )
			return result;
		var rng = new Random( seed );
		var lo = Stacks is { Length: >= 1 } ? Math.Max( 1, Stacks[0] ) : 2;
		var hi = Stacks is { Length: >= 2 } ? Math.Max( lo, Stacks[1] ) : lo;
		var want = Math.Min( rng.Next( lo, hi + 1 ), Items.Count );
		var pool = new List<PoiLootItem>( Items );
		while ( result.Count < want && pool.Count > 0 )
		{
			var total = 0f;
			foreach ( var item in pool )
				total += Math.Max( 0.01f, item.Weight );
			var roll = (float)rng.NextDouble() * total;
			var pick = pool[^1];
			foreach ( var item in pool )
			{
				roll -= Math.Max( 0.01f, item.Weight );
				if ( roll > 0f )
					continue;
				pick = item;
				break;
			}
			pool.Remove( pick );
			if ( string.IsNullOrWhiteSpace( pick.ResourceId ) )
				continue;
			result.Add( (pick.ResourceId.Trim(), rng.Next( Math.Min( pick.Min, pick.Max ), Math.Max( pick.Min, pick.Max ) + 1 )) );
		}
		return result;
	}
}

/// <summary>One line of a POI loot table: an item, how many, and how often it is picked against the other lines.</summary>
public sealed class PoiLootItem
{
	public string ResourceId { get; set; } = string.Empty;
	public int Min { get; set; } = 1;
	public int Max { get; set; } = 1;
	public float Weight { get; set; } = 1f;
}
