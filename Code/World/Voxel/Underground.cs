using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>How a dug face shows what it is made of (the terrain splat has grass, soil, rock and a darkening channel - no new material).</summary>
public enum UndergroundLook
{
	/// <summary>The surface rule: soil, then rock from 4–8 m down.</summary>
	None,
	Rock,
	Sand,
	/// <summary>Dark rust-brown (the splat's tilled-earth colour over the soil tile).</summary>
	Ore,
}

public sealed class UndergroundPocketData
{
	public string ResourceId { get; set; } = string.Empty;
	/// <summary>About how much of the layer's volume it fills (0.01 = 1 %).</summary>
	public float Share { get; set; } = 0.05f;
	public float SizeMeters { get; set; } = 4f;
	public string Look { get; set; } = "rock";
	public float MinDepthMeters { get; set; } = 3f;

	internal float Threshold;
	internal UndergroundLook LookKind;
}

public sealed class UndergroundLootData
{
	public string ResourceId { get; set; } = string.Empty;
	public int Min { get; set; } = 1;
	public int Max { get; set; } = 1;
}

public sealed class UndergroundLayerData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>The lowest pickaxe tier that chips this layer.</summary>
	public int PickaxeTier { get; set; } = 1;
	/// <summary>"soil" = the surface rule (dirt, then stone 4–8 m down); "rock" = stone all through.</summary>
	public string Base { get; set; } = "soil";
	public List<UndergroundPocketData> Pockets { get; set; } = new();
	/// <summary>Chance that one 64 m square of this layer holds a loot room.</summary>
	public float RoomChance { get; set; }
	public int[] LootStacks { get; set; } = { 2, 4 };
	public List<UndergroundLootData> Loot { get; set; } = new();

	internal bool RockBase;
}

public sealed class UndergroundRoomData
{
	public string Id { get; set; } = string.Empty;
	/// <summary>[x, y, height] metres.</summary>
	public float[] SizeMeters { get; set; } = { 4f, 4f, 3f };
}

/// <summary>What the ground is at one point under the surface (<see cref="Underground.Sample"/>).</summary>
public readonly record struct UndergroundPoint( int Layer, bool Bedrock, int PickaxeTier, string ResourceId, UndergroundLook Look, bool RockBase )
{
	/// <summary>The surface: any pickaxe, the surface yield rule.</summary>
	public static readonly UndergroundPoint Surface = new( 0, false, 1, null, UndergroundLook.None, false );
}

/// <summary>One generated loot room: an empty box under the ground with a chest in the middle of its floor.</summary>
public readonly record struct UndergroundRoom( string Key, int Layer, Vector2 CenterXy, float FloorDepthMeters, Vector3 SizeMeters, int Seed );

/// <summary>
/// What is under the ground (Mark 2026-11-18: "when players dig underground I want them to be able to find a variety of
/// things ... some rock scattered around, very rarely some scrap ... the rock type change to something that requires a
/// different level pickaxe ... each layer can be 100 meters and the final layer is to the bottom of the world which can just
/// be unbreakable ... every layer should have varying kinds of loot rooms: just a box of empty space with a chest in the
/// middle"). Data in <c>Assets/data/underground.json</c>.
/// <para>
/// Everything here is a pure function of (world seed, position, depth under the original surface), so the host, every peer
/// and a replay after a reload agree without anything being saved: <see cref="Sample"/> says what one point is made of (its
/// layer, the pickaxe tier it needs, a pocket's resource and look, or bedrock under the last layer); <see cref="TryRoom"/>
/// says whether a 64 m chunk holds a loot room in a layer and where. Pockets are 3-D value noise thresholded to the share
/// of the layer they should fill. The voxel field carves the rooms and locks the bedrock (<see cref="VoxelField"/>), the
/// mesher paints the pockets (<see cref="VoxelMesher"/>), <see cref="PlayerTerraform"/> gates the pickaxe and hands over
/// the yield, and <c>TerrainWorldManager.Underground.cs</c> grows a dug column's field downward and puts the chests in.
/// </para>
/// </summary>
public static class Underground
{
	const string FilePath = "data/underground.json";

	static bool _loaded;
	static readonly List<UndergroundLayerData> LayerList = new();
	static readonly List<UndergroundRoomData> RoomList = new();
	static readonly Dictionary<string, int> PickaxeTiers = new( StringComparer.OrdinalIgnoreCase );

	public static float LayerThicknessMeters { get; private set; } = 100f;

	public static IReadOnlyList<UndergroundLayerData> Layers
	{
		get
		{
			EnsureLoaded();
			return LayerList;
		}
	}

	/// <summary>Depth under the surface where bedrock starts (m): the bottom of the last layer. +∞ with no layers (nothing is locked).</summary>
	public static float BedrockDepthMeters
	{
		get
		{
			EnsureLoaded();
			return LayerList.Count == 0 ? float.PositiveInfinity : LayerList.Count * LayerThicknessMeters;
		}
	}

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		_loaded = true;
		LayerList.Clear();
		RoomList.Clear();
		PickaxeTiers.Clear();
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
			{
				Log.Warning( "[Underground] No underground.json — the ground is plain soil and stone, no bedrock." );
				return;
			}
			var file = JsonSerializer.Deserialize<UndergroundFile>( FileSystem.Mounted.ReadAllText( FilePath ), JsonOptions );
			if ( file is null )
				return;
			LayerThicknessMeters = Math.Max( 8f, file.LayerThicknessMeters );
			foreach ( var layer in file.Layers ?? new() )
			{
				if ( layer is null )
					continue;
				layer.RockBase = string.Equals( layer.Base, "rock", StringComparison.OrdinalIgnoreCase );
				layer.Pockets ??= new();
				layer.Loot ??= new();
				foreach ( var pocket in layer.Pockets )
				{
					pocket.Threshold = ThresholdForShare( pocket.Share );
					pocket.LookKind = pocket.Look?.Trim().ToLowerInvariant() switch
					{
						"sand" => UndergroundLook.Sand,
						"ore" => UndergroundLook.Ore,
						_ => UndergroundLook.Rock,
					};
				}
				LayerList.Add( layer );
			}
			foreach ( var room in file.Rooms ?? new() )
			{
				if ( room?.SizeMeters is { Length: >= 3 } )
					RoomList.Add( room );
			}
			foreach ( var (id, tier) in file.PickaxeTiers ?? new() )
				PickaxeTiers[id.Trim()] = Math.Max( 1, tier );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[Underground] Failed to load {FilePath}: {ex.Message}" );
		}
	}

	public static void Reload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	/// <summary>A pickaxe item's tier (1 when it is not listed).</summary>
	public static int PickaxeTier( string itemId )
	{
		EnsureLoaded();
		return !string.IsNullOrWhiteSpace( itemId ) && PickaxeTiers.TryGetValue( itemId.Trim(), out var tier ) ? tier : 1;
	}

	/// <summary>The cheapest pickaxe that has at least this tier, for the "needs a …" message (the item id; null when none is listed).</summary>
	public static string PickaxeForTier( int tier )
	{
		EnsureLoaded();
		string best = null;
		var bestTier = int.MaxValue;
		foreach ( var (id, t) in PickaxeTiers )
		{
			if ( t >= tier && t < bestTier )
			{
				best = id;
				bestTier = t;
			}
		}
		return best;
	}

	/// <summary>
	/// What the ground is at a world point (m) that lies <paramref name="depthMeters"/> under the original surface. Worker-safe
	/// once <see cref="EnsureLoaded"/> has run on the main thread.
	/// </summary>
	public static UndergroundPoint Sample( int seed, float x, float y, float z, float depthMeters )
	{
		if ( LayerList.Count == 0 || depthMeters <= 0f )
			return UndergroundPoint.Surface;
		var index = (int)(depthMeters / LayerThicknessMeters);
		if ( index >= LayerList.Count )
			return new UndergroundPoint( LayerList.Count, true, int.MaxValue, null, UndergroundLook.Rock, true );
		var layer = LayerList[index];
		for ( var k = 0; k < layer.Pockets.Count; k++ )
		{
			var pocket = layer.Pockets[k];
			if ( depthMeters < pocket.MinDepthMeters )
				continue;
			var size = Math.Max( 0.5f, pocket.SizeMeters );
			// Two octaves: the second roughens the pocket's outline so it is not a smooth blob
			var n = (ValueNoise( seed + (index * 1013) + (k * 7919), x / size, y / size, z / size ) * 0.8f)
				+ (ValueNoise( seed + (index * 1013) + (k * 7919) + 31, x / (size * 0.37f), y / (size * 0.37f), z / (size * 0.37f) ) * 0.2f);
			if ( n > pocket.Threshold )
				return new UndergroundPoint( index, false, layer.PickaxeTier, pocket.ResourceId, pocket.LookKind, layer.RockBase );
		}
		return new UndergroundPoint( index, false, layer.PickaxeTier, null, layer.RockBase ? UndergroundLook.Rock : UndergroundLook.None, layer.RockBase );
	}

	/// <summary>
	/// The loot room of one 64 m chunk in one layer, if it has one: its key, where its centre is, how far under the surface
	/// its floor lies, its size and a seed for its chest. A room keeps <see cref="RoomMarginMeters"/> from the chunk's edges
	/// (it never crosses into a neighbour's voxel column) and 12 m from the layer's top and bottom.
	/// </summary>
	public static bool TryRoom( int seed, int chunkX, int chunkY, int layer, float chunkMinX, float chunkMinY, float chunkSize, out UndergroundRoom room )
	{
		room = default;
		if ( layer < 0 || layer >= LayerList.Count || RoomList.Count == 0 )
			return false;
		var data = LayerList[layer];
		var h = Hash( seed ^ 0x51ed270b, chunkX, chunkY, layer );
		if ( Unit( h ) >= data.RoomChance )
			return false;
		var kind = RoomList[(int)(Unit( Hash( (int)h, 1, 2, 3 ) ) * RoomList.Count) % RoomList.Count];
		var size = new Vector3( Math.Max( 2f, kind.SizeMeters[0] ), Math.Max( 2f, kind.SizeMeters[1] ), Math.Max( 2f, kind.SizeMeters[2] ) );
		var spanX = Math.Max( 0f, chunkSize - (2f * RoomMarginMeters) - size.x );
		var spanY = Math.Max( 0f, chunkSize - (2f * RoomMarginMeters) - size.y );
		// Snapped to half metres: the room's walls land on the voxel grid's sample planes
		var cx = MathF.Round( (chunkMinX + RoomMarginMeters + (size.x * 0.5f) + (Unit( Hash( (int)h, 4, 5, 6 ) ) * spanX)) * 2f ) / 2f;
		var cy = MathF.Round( (chunkMinY + RoomMarginMeters + (size.y * 0.5f) + (Unit( Hash( (int)h, 7, 8, 9 ) ) * spanY)) * 2f ) / 2f;
		var top = (layer * LayerThicknessMeters) + 12f + size.z;
		var bottom = ((layer + 1) * LayerThicknessMeters) - 12f;
		var floorDepth = MathF.Round( top + (Unit( Hash( (int)h, 10, 11, 12 ) ) * Math.Max( 0f, bottom - top )) );
		room = new UndergroundRoom( $"{chunkX}_{chunkY}_{layer}", layer, new Vector2( cx, cy ), floorDepth, size, (int)Hash( (int)h, 13, 14, 15 ) );
		return true;
	}

	/// <summary>A room stays this far inside its chunk.</summary>
	public const float RoomMarginMeters = 8f;

	/// <summary>The stacks a room's chest holds: rolled from its layer's loot list with the room's seed.</summary>
	public static List<(string ResourceId, int Count)> RollLoot( UndergroundRoom room )
	{
		var result = new List<(string, int)>();
		if ( room.Layer < 0 || room.Layer >= LayerList.Count )
			return result;
		var layer = LayerList[room.Layer];
		if ( layer.Loot.Count == 0 )
			return result;
		var rng = new Random( room.Seed );
		var lo = layer.LootStacks is { Length: >= 1 } ? Math.Max( 1, layer.LootStacks[0] ) : 2;
		var hi = layer.LootStacks is { Length: >= 2 } ? Math.Max( lo, layer.LootStacks[1] ) : lo;
		var stacks = rng.Next( lo, hi + 1 );
		for ( var i = 0; i < stacks; i++ )
		{
			// The first stack is always the first entry (gold coins in the shipped file): a chest is never a disappointment
			var entry = i == 0 ? layer.Loot[0] : layer.Loot[rng.Next( layer.Loot.Count )];
			if ( string.IsNullOrWhiteSpace( entry.ResourceId ) )
				continue;
			result.Add( (entry.ResourceId, rng.Next( Math.Min( entry.Min, entry.Max ), Math.Max( entry.Min, entry.Max ) + 1 )) );
		}
		return result;
	}

	// ---- noise

	/// <summary>
	/// The noise value above which a pocket fills about <paramref name="share"/> of space. The two-octave value noise is
	/// close to a normal distribution (mean 0.5, deviation ~0.155), so the threshold is that distribution's upper quantile.
	/// </summary>
	static float ThresholdForShare( float share )
	{
		var p = Math.Clamp( share, 0.0005f, 0.5f );
		// Upper-tail normal quantile (Abramowitz–Stegun 26.2.23)
		var t = MathF.Sqrt( -2f * MathF.Log( p ) );
		var z = t - ((2.515517f + (0.802853f * t) + (0.010328f * t * t)) / (1f + (1.432788f * t) + (0.189269f * t * t) + (0.001308f * t * t * t)));
		return 0.5f + (0.155f * z);
	}

	static uint Hash( int seed, int x, int y, int z )
	{
		unchecked
		{
			var h = (uint)seed * 0x9E3779B1u;
			h ^= (uint)x * 0x85EBCA6Bu;
			h = (h << 13) | (h >> 19);
			h ^= (uint)y * 0xC2B2AE35u;
			h = (h << 17) | (h >> 15);
			h ^= (uint)z * 0x27D4EB2Fu;
			h ^= h >> 15;
			h *= 0x2C1B3C6Du;
			h ^= h >> 12;
			h *= 0x297A2D39u;
			h ^= h >> 15;
			return h;
		}
	}

	static float Unit( uint h ) => (h & 0xFFFFFF) / 16777216f;

	/// <summary>Trilinear value noise 0..1 on the unit lattice, smoothstep-eased.</summary>
	static float ValueNoise( int seed, float x, float y, float z )
	{
		var fx = MathF.Floor( x );
		var fy = MathF.Floor( y );
		var fz = MathF.Floor( z );
		var ix = (int)fx;
		var iy = (int)fy;
		var iz = (int)fz;
		var tx = x - fx;
		var ty = y - fy;
		var tz = z - fz;
		tx = tx * tx * (3f - (2f * tx));
		ty = ty * ty * (3f - (2f * ty));
		tz = tz * tz * (3f - (2f * tz));
		float C( int dx, int dy, int dz ) => Unit( Hash( seed, ix + dx, iy + dy, iz + dz ) );
		var x00 = C( 0, 0, 0 ) + ((C( 1, 0, 0 ) - C( 0, 0, 0 )) * tx);
		var x10 = C( 0, 1, 0 ) + ((C( 1, 1, 0 ) - C( 0, 1, 0 )) * tx);
		var x01 = C( 0, 0, 1 ) + ((C( 1, 0, 1 ) - C( 0, 0, 1 )) * tx);
		var x11 = C( 0, 1, 1 ) + ((C( 1, 1, 1 ) - C( 0, 1, 1 )) * tx);
		var y0 = x00 + ((x10 - x00) * ty);
		var y1 = x01 + ((x11 - x01) * ty);
		return y0 + ((y1 - y0) * tz);
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class UndergroundFile
	{
		public float LayerThicknessMeters { get; set; } = 100f;
		public List<UndergroundLayerData> Layers { get; set; } = new();
		public List<UndergroundRoomData> Rooms { get; set; } = new();
		public Dictionary<string, int> PickaxeTiers { get; set; } = new();
	}
}
