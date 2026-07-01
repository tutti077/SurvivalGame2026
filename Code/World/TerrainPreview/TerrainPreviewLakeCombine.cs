namespace Survival;

/// <summary>
/// Applies speck-filtered open water to dry land height. All water surfaces sit at <see cref="TerrainPreviewSettings.SeaLevelMeters"/> (flat).
/// </summary>
static class TerrainPreviewLakeCombine
{
	public readonly struct Result
	{
		public float HeightMeters { get; init; }
		public bool IsLakeWater { get; init; }
	}

	public static Result Apply(
		TerrainPreviewSettings settings,
		float dryLandHeightMeters,
		bool isFilteredOpenWater )
	{
		dryLandHeightMeters = ClampDryLandAboveSea( settings, dryLandHeightMeters );

		if ( isFilteredOpenWater )
		{
			return new Result
			{
				HeightMeters = settings.SeaLevelMeters,
				IsLakeWater = true,
			};
		}

		return new Result { HeightMeters = dryLandHeightMeters, IsLakeWater = false };
	}

	static float ClampDryLandAboveSea( TerrainPreviewSettings settings, float heightMeters )
	{
		var floor = settings.SeaLevelMeters + Math.Max( 0.05f, settings.InlandDryLandSeaMarginMeters );
		return Math.Max( heightMeters, floor );
	}
}
