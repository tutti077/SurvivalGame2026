using System;
using System.Collections.Generic;
using System.Text.Json;
using Sandbox;

namespace Survival;

/// <summary>One biome's air from <c>data/biome_atmosphere.json</c> (the file documents each value).</summary>
public sealed class BiomeAtmosphereData
{
	public string Biome { get; set; } = string.Empty;
	public float MistDistanceMeters { get; set; }
	public float MistStartMeters { get; set; }
	public float MistMax { get; set; } = 1f;
	/// <summary>What the mist already covers where it begins (0-1): it comes in at this over 40 m, then builds.</summary>
	public float MistFloor { get; set; }
	public float[] MistTint { get; set; } = [1f, 1f, 1f];
	public float[] MistSunTint { get; set; } = [0f, 0f, 0f];
	public float Shade { get; set; } = 1f;
	public float CeilingMeters { get; set; }
	/// <summary>Falling snow 0-1 (the Ivory Alps: it snows there all the time).</summary>
	public float Snow { get; set; }

	internal TerrainPreviewBiomeId BiomeId;
}

/// <summary>
/// The air the camera stands in: the mist and shade of its biome, blended by the land's biome weights at the camera.
/// Built by <see cref="BiomeAtmosphereCatalog.Mix"/>, drawn by <see cref="AerialPerspective"/>.
/// </summary>
public struct BiomeAtmosphereMix
{
	/// <summary>1 / mist distance (1 / metres); 0 = no mist.</summary>
	public float InverseMistMeters;
	public float MistStartMeters;
	public float MistMax;
	public float MistFloor;
	public Vector3 MistTint;
	/// <summary>The mist's sun side: sun glow colour x this, added toward the sun.</summary>
	public Vector3 MistSunTint;
	/// <summary>Share of the sky ambient the biome lets through (see <see cref="AerialPerspective.BiomeShade"/>).</summary>
	public float Shade;
	/// <summary>Falling snow 0-1.</summary>
	public float Snow;

	public static BiomeAtmosphereMix None => new() { MistMax = 1f, MistTint = Vector3.One, Shade = 1f };

	public static BiomeAtmosphereMix Lerp( in BiomeAtmosphereMix a, in BiomeAtmosphereMix b, float t ) => new()
	{
		InverseMistMeters = MathX.Lerp( a.InverseMistMeters, b.InverseMistMeters, t ),
		MistStartMeters = MathX.Lerp( a.MistStartMeters, b.MistStartMeters, t ),
		MistMax = MathX.Lerp( a.MistMax, b.MistMax, t ),
		MistFloor = MathX.Lerp( a.MistFloor, b.MistFloor, t ),
		MistTint = Vector3.Lerp( a.MistTint, b.MistTint, t ),
		MistSunTint = Vector3.Lerp( a.MistSunTint, b.MistSunTint, t ),
		Shade = MathX.Lerp( a.Shade, b.Shade, t ),
		Snow = MathX.Lerp( a.Snow, b.Snow, t ),
	};
}

/// <summary>The per-biome air (<c>Assets/data/biome_atmosphere.json</c>).</summary>
public static class BiomeAtmosphereCatalog
{
	const string FilePath = "data/biome_atmosphere.json";
	const float CeilingFadeMeters = 30f;

	static readonly List<BiomeAtmosphereData> Rows = new();
	static bool _loaded;

	/// <summary>Seconds the air takes to follow a change of the camera's biome.</summary>
	public static float BlendSeconds { get; private set; } = 4f;

	/// <summary>How much of its own hue the ground (everything the fog pass sees) keeps in the fog, 0-1.</summary>
	public static float GroundColorHold { get; private set; } = 0.45f;
	/// <summary>The same for the far-tree ring: lower, so far forests soften before the land does.</summary>
	public static float TreeColorHold { get; private set; } = 0.25f;
	/// <summary>Sun shaft strength (0 = off).</summary>
	public static float SunShafts { get; private set; } = 0.5f;
	/// <summary>The most the world's distance fog may cover anything (0-1).</summary>
	public static float FogMax { get; private set; } = 0.8f;
	/// <summary>Colour of the world's distance fog, as a multiple of the sky colour along the ray.</summary>
	public static Vector3 FogTint { get; private set; } = new( 0.22f, 0.34f, 0.56f );

	public static void EnsureLoaded()
	{
		if ( _loaded )
			return;
		_loaded = true;
		Rows.Clear();
		try
		{
			if ( !FileSystem.Mounted.FileExists( FilePath ) )
				return;
			var file = JsonSerializer.Deserialize<AtmosphereFile>( FileSystem.Mounted.ReadAllText( FilePath ), JsonOptions );
			BlendSeconds = Math.Max( 0f, file?.BlendSeconds ?? 4f );
			GroundColorHold = Math.Clamp( file?.GroundColorHold ?? 0.45f, 0f, 1f );
			TreeColorHold = Math.Clamp( file?.TreeColorHold ?? 0.25f, 0f, 1f );
			SunShafts = Math.Clamp( file?.SunShafts ?? 0.5f, 0f, 4f );
			FogMax = Math.Clamp( file?.FogMax ?? 0.8f, 0f, 1f );
			FogTint = file?.FogTint is { Length: >= 3 } ft ? new Vector3( ft[0], ft[1], ft[2] ) : new Vector3( 0.22f, 0.34f, 0.56f );
			foreach ( var row in file?.Biomes ?? new() )
			{
				if ( row is null )
					continue;
				if ( !Enum.TryParse<TerrainPreviewBiomeId>( row.Biome?.Trim(), true, out var id ) )
				{
					Log.Warning( $"[BiomeAtmosphere] unknown biome '{row.Biome}' in {FilePath}" );
					continue;
				}
				row.BiomeId = id;
				Rows.Add( row );
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"[BiomeAtmosphere] Failed to load {FilePath}: {ex.Message}" );
		}
	}

	public static void Reload()
	{
		_loaded = false;
		EnsureLoaded();
	}

	static readonly TerrainPreviewBiomeId[] LandBiomes =
		[TerrainPreviewBiomeId.CloverHills, TerrainPreviewBiomeId.RedwoodForest, TerrainPreviewBiomeId.AmberDunes, TerrainPreviewBiomeId.Mountain];

	static BiomeAtmosphereData RowFor( TerrainPreviewBiomeId biome )
	{
		BiomeAtmosphereData fallback = null;
		foreach ( var row in Rows )
		{
			if ( row.BiomeId == biome )
				return row;
			if ( row.BiomeId == TerrainPreviewBiomeId.CloverHills )
				fallback = row;
		}
		return fallback;
	}

	/// <summary>
	/// The air for a camera standing where the land has these biome weights, <paramref name="aboveGroundMeters"/> over the
	/// ground. Mist densities add up by weight and the tint follows the density, so a thin pale mist next door does not
	/// wash out a thick dark one. A biome without a row, and anywhere that is no land biome at all (afloat on the sea,
	/// past the world's edge), takes the Clover Hills row (Mark 2026-11-23: "for the unlisted biomes just leave them the
	/// same as clover hills", "when youre floating on the water we should use same baseline as clover hills").
	/// </summary>
	public static BiomeAtmosphereMix Mix( TerrainPreviewBiomeResolver.LandBiomeWeights weights, float aboveGroundMeters )
	{
		EnsureLoaded();
		if ( Rows.Count == 0 )
			return BiomeAtmosphereMix.None;
		var total = weights.Total;
		if ( total <= 1e-4f )
		{
			weights = new TerrainPreviewBiomeResolver.LandBiomeWeights { Clover = 1f };
			total = 1f;
		}

		var inverse = 0f;
		var start = 0f;
		var max = 0f;
		var floor = 0f;
		var tint = Vector3.Zero;
		var sunTint = Vector3.Zero;
		var shade = 1f;
		var snow = 0f;
		foreach ( var biome in LandBiomes )
		{
			var row = RowFor( biome );
			if ( row is null )
				continue;
			var w = biome switch
			{
				TerrainPreviewBiomeId.CloverHills => weights.Clover,
				TerrainPreviewBiomeId.RedwoodForest => weights.Redwood,
				TerrainPreviewBiomeId.AmberDunes => weights.Amber,
				_ => weights.Mountain,
			} / total;
			if ( row.CeilingMeters > 0f )
				w *= 1f - Math.Clamp( (aboveGroundMeters - row.CeilingMeters) / CeilingFadeMeters, 0f, 1f );
			if ( w <= 1e-4f )
				continue;

			shade += w * (Math.Clamp( row.Shade, 0f, 1f ) - 1f);
			snow += w * Math.Clamp( row.Snow, 0f, 1f );
			if ( row.MistDistanceMeters <= 0f )
				continue;
			var density = w / Math.Max( 10f, row.MistDistanceMeters );
			inverse += density;
			start += density * Math.Max( 0f, row.MistStartMeters );
			max += density * Math.Clamp( row.MistMax, 0f, 1f );
			floor += density * Math.Clamp( row.MistFloor, 0f, 1f );
			var t = row.MistTint;
			tint += density * (t is { Length: >= 3 } ? new Vector3( t[0], t[1], t[2] ) : Vector3.One);
			var st = row.MistSunTint;
			sunTint += density * (st is { Length: >= 3 } ? new Vector3( st[0], st[1], st[2] ) : Vector3.Zero);
		}
		if ( inverse <= 0f )
			return BiomeAtmosphereMix.None with { Shade = shade, Snow = snow };

		return new BiomeAtmosphereMix
		{
			InverseMistMeters = inverse,
			MistStartMeters = start / inverse,
			MistMax = max / inverse,
			MistFloor = floor / inverse,
			MistTint = tint / inverse,
			MistSunTint = sunTint / inverse,
			Shade = shade,
			Snow = snow,
		};
	}

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	sealed class AtmosphereFile
	{
		public float BlendSeconds { get; set; } = 4f;
		public float GroundColorHold { get; set; } = 0.45f;
		public float TreeColorHold { get; set; } = 0.25f;
		public float SunShafts { get; set; } = 0.5f;
		public float FogMax { get; set; } = 0.8f;
		public float[] FogTint { get; set; }
		public List<BiomeAtmosphereData> Biomes { get; set; } = new();
	}
}
