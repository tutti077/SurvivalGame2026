namespace Survival;

/// <summary>Raster summary of land vs ocean. <see cref="LandDiskLakeFraction01"/> matches Target Lake Coverage.</summary>
public readonly struct TerrainPreviewWaterCoverageStats
{
	public int InsideWorldPixels { get; init; }
	public int LandPixels { get; init; }
	public int OceanPixels { get; init; }
	public int InteriorOceanPixels { get; init; }
	public int ExteriorOceanPixels { get; init; }

	/// <summary>Open lake water on the land circle only (0–1). Same metric as Target Lake Coverage.</summary>
	public float LandDiskLakeFraction01 { get; init; }

	public float LandFraction01 => InsideWorldPixels > 0 ? LandPixels / (float)InsideWorldPixels : 0f;

	/// <summary>All water inside the world square (rim ocean + lakes on land).</summary>
	public float OceanFraction01 => InsideWorldPixels > 0 ? OceanPixels / (float)InsideWorldPixels : 0f;

	public float InteriorOceanFraction01 => InsideWorldPixels > 0 ? InteriorOceanPixels / (float)InsideWorldPixels : 0f;

	public float ExteriorOceanFraction01 => InsideWorldPixels > 0 ? ExteriorOceanPixels / (float)InsideWorldPixels : 0f;

	public float InteriorOceanOfOceanFraction01 => OceanPixels > 0 ? InteriorOceanPixels / (float)OceanPixels : 0f;

	public bool IsBelowInteriorOceanTarget( float targetFraction01 )
		=> InteriorOceanFraction01 + 0.0001f < Math.Clamp( targetFraction01, 0f, 1f );

	public bool IsBelowTotalOceanTarget( float targetFraction01 )
		=> OceanFraction01 + 0.0001f < Math.Clamp( targetFraction01, 0f, 1f );

	public bool IsAtOrAboveTotalOceanCap( float capFraction01 )
		=> OceanFraction01 + 0.0001f >= Math.Clamp( capFraction01, 0f, 1f );

	public bool IsAtOrAboveExteriorOceanCap( float capFraction01 )
		=> ExteriorOceanFraction01 + 0.0001f >= Math.Clamp( capFraction01, 0f, 1f );

	public string FormatSummary( float targetTotalOceanFraction01, float targetInteriorOceanFraction01, float interiorZoneRadius01 )
	{
		var land = LandFraction01 * 100f;
		var ocean = OceanFraction01 * 100f;
		var interior = InteriorOceanFraction01 * 100f;
		var exterior = ExteriorOceanFraction01 * 100f;
		var totalTarget = Math.Clamp( targetTotalOceanFraction01, 0f, 1f ) * 100f;
		var interiorTarget = Math.Clamp( targetInteriorOceanFraction01, 0f, 1f ) * 100f;
		var zone = Math.Clamp( interiorZoneRadius01, 0.1f, 0.95f ) * 100f;
		var totalFlag = IsBelowTotalOceanTarget( targetTotalOceanFraction01 ) ? " · below total target" : "";
		var interiorFlag = IsBelowInteriorOceanTarget( targetInteriorOceanFraction01 ) ? " · below interior target" : "";
		return $"Land {land:0.#}% · Ocean {ocean:0.#}% (interior {interior:0.#}%, rim {exterior:0.#}% @ {zone:0.#}% radius){totalFlag}{interiorFlag} · Target total {totalTarget:0.#}% · interior {interiorTarget:0.#}%";
	}
}
