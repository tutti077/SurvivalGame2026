namespace Survival;

/// <summary>
/// Cached land-disk rasters. Biome placement is independent of lake mask offset/threshold;
/// water is applied on top, then biome patch merge runs on dry land only.
/// </summary>
public static class TerrainPreviewLandDiskFields
{
	public static int ResolveFieldResolution( TerrainPreviewSettings settings )
	{
		var preview = Math.Clamp( settings.ClampedResolution, 64, 4096 );
		return Math.Clamp( preview / 2, 256, 1024 );
	}

	static int _fieldResolution = 256;
	static int _biomePlacementFingerprint = int.MinValue;
	static int _waterFingerprint = int.MinValue;
	static int _finalizeFingerprint = int.MinValue;
	static bool _isBuilding;
	static float _openWaterThreshold01 = 0.5f;
	static float _lakeCoverageOnLand01;
	static bool[] _landDisk = Array.Empty<bool>();
	static bool[] _openWater = Array.Empty<bool>();
	static TerrainPreviewBiomeId[] _rawLandBiomes = Array.Empty<TerrainPreviewBiomeId>();
	static TerrainPreviewBiomeResolver.LandBiomeWeights[] _placementWeights = Array.Empty<TerrainPreviewBiomeResolver.LandBiomeWeights>();
	static bool[] _azureCoast = Array.Empty<bool>();
	static int _azureCoastFingerprint = int.MinValue;
	static bool[] _blackwater = Array.Empty<bool>();
	static int _blackwaterFingerprint = int.MinValue;
	static TerrainPreviewBlackwater.Spot[] _blackwaterSpots = Array.Empty<TerrainPreviewBlackwater.Spot>();
	static readonly object _buildLock = new();

	static int RasterCellCount( int res ) => Math.Max( 0, res ) * Math.Max( 0, res );

	static bool RasterArraysMatch( int res )
	{
		var count = RasterCellCount( res );
		return count > 0
			&& _landDisk.Length == count
			&& _rawLandBiomes.Length == count;
	}

	static bool WaterRasterReady( int res )
		=> RasterArraysMatch( res ) && _openWater.Length == RasterCellCount( res );

	public static void InvalidateCache()
	{
		_biomePlacementFingerprint = int.MinValue;
		_waterFingerprint = int.MinValue;
		_finalizeFingerprint = int.MinValue;
		_azureCoastFingerprint = int.MinValue;
		_blackwaterFingerprint = int.MinValue;
		_isBuilding = false;
		_landDisk = Array.Empty<bool>();
		_openWater = Array.Empty<bool>();
		_azureCoast = Array.Empty<bool>();
		_blackwater = Array.Empty<bool>();
		_blackwaterSpots = Array.Empty<TerrainPreviewBlackwater.Spot>();
		_rawLandBiomes = Array.Empty<TerrainPreviewBiomeId>();
		_placementWeights = Array.Empty<TerrainPreviewBiomeResolver.LandBiomeWeights>();
	}

	/// <summary>Drop lake mask only — biome placement on land is unchanged.</summary>
	public static void InvalidateWaterCache()
	{
		_waterFingerprint = int.MinValue;
		_finalizeFingerprint = int.MinValue;
		_azureCoastFingerprint = int.MinValue;
		_blackwaterFingerprint = int.MinValue;
		_openWater = Array.Empty<bool>();
		_azureCoast = Array.Empty<bool>();
		_blackwater = Array.Empty<bool>();
		_blackwaterSpots = Array.Empty<TerrainPreviewBlackwater.Spot>();
	}

	/// <summary>Drop biome placement — e.g. when World Seed changes during spawn solve.</summary>
	public static void InvalidateBiomePlacementCache()
	{
		_biomePlacementFingerprint = int.MinValue;
		_finalizeFingerprint = int.MinValue;
		_rawLandBiomes = Array.Empty<TerrainPreviewBiomeId>();
		_placementWeights = Array.Empty<TerrainPreviewBiomeResolver.LandBiomeWeights>();
		_blackwaterFingerprint = int.MinValue;
		_blackwater = Array.Empty<bool>();
		_blackwaterSpots = Array.Empty<TerrainPreviewBlackwater.Spot>();
		_landDisk = Array.Empty<bool>();
	}

	public static void EnsureReady( TerrainPreviewSettings settings ) => EnsureBuilt( settings );

	public static void EnsureLandBiomesBuilt( TerrainPreviewSettings settings )
	{
		var desiredRes = ResolveFieldResolution( settings );
		if ( _isBuilding )
			return;

		_fieldResolution = desiredRes;
		if ( !RasterArraysMatch( _fieldResolution ) )
			_biomePlacementFingerprint = int.MinValue;

		EnsureBiomePlacement( settings );
	}

	public static bool TryGetLandDiskForSolve(
		TerrainPreviewSettings settings,
		out bool[] landDisk,
		out int res,
		out float radius,
		out float diameter )
	{
		EnsureLandBiomesBuilt( settings );
		landDisk = _landDisk;
		res = _fieldResolution;
		radius = settings.WorldRadiusMeters;
		diameter = settings.WorldDiameterMeters;
		return RasterArraysMatch( res );
	}

	public static float GetLakeCoverageOnLand01( TerrainPreviewSettings settings )
	{
		EnsureBuilt( settings );
		return _lakeCoverageOnLand01;
	}

	public static TerrainPreviewGenerationMetrics MeasureGenerationMetrics( TerrainPreviewSettings settings )
	{
		EnsureBuilt( settings );
		var res = _fieldResolution;
		var metersPerPixel = settings.WorldDiameterMeters / Math.Max( 1, res );
		var lakePatches = TerrainPreviewPatchMetrics.Measure( _openWater, res, res, metersPerPixel );

		var mountainLand = 0;
		var landCount = 0;
		for ( var i = 0; i < _openWater.Length; i++ )
		{
			if ( _landDisk.Length <= i || !_landDisk[i] || _openWater[i] )
				continue;

			landCount++;
			if ( _placementWeights.Length > i && _placementWeights[i].Mountain >= 0.5f )
				mountainLand++;
		}

		var coverage = _lakeCoverageOnLand01;
		var archipelago = lakePatches.PatchCount / MathF.Max( 0.01f, coverage * 100f );

		return new TerrainPreviewGenerationMetrics
		{
			LakePatchCount = lakePatches.PatchCount,
			MedianLakeDiameterMeters = lakePatches.MedianDiameterMeters,
			MeanLakeDiameterMeters = lakePatches.MeanDiameterMeters,
			LakeArchipelagoScore = archipelago,
			MountainLandFraction01 = landCount > 0 ? mountainLand / (float)landCount : 0f,
			WaterOnLandFraction01 = coverage,
		};
	}

	public static float GetOpenWaterThreshold01( TerrainPreviewSettings settings )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return 2f;

		if ( _isBuilding )
			return _openWaterThreshold01;

		EnsureBuilt( settings );
		return _openWaterThreshold01;
	}

	public static bool IsOpenWater( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return false;

		if ( _isBuilding && !WaterRasterReady( _fieldResolution ) )
			return false;

		EnsureBuilt( settings );
		if ( !TryWorldToIndex( settings, worldXMeters, worldYMeters, out var idx ) )
			return false;

		return idx < _openWater.Length && _openWater[idx];
	}

	public static bool IsOnLand( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		EnsureBuilt( settings );
		if ( !TryWorldToIndex( settings, worldXMeters, worldYMeters, out var idx ) )
			return false;

		return idx < _landDisk.Length && _landDisk[idx];
	}

	/// <summary>Raster scan — accurate nearest open lake on the land disk from spawn.</summary>
	public static float MeasureNearestOpenWaterMeters( TerrainPreviewSettings settings, float searchRadiusMeters )
	{
		if ( !settings.EnableInteriorWaterLayer )
			return -1f;

		EnsureBuilt( settings );
		searchRadiusMeters = Math.Max( 10f, searchRadiusMeters );
		var res = _fieldResolution;
		if ( !WaterRasterReady( res ) )
			return -1f;

		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var nearest = float.MaxValue;

		for ( var py = 0; py < res; py++ )
		{
			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( idx >= _landDisk.Length || idx >= _openWater.Length || !_landDisk[idx] || !_openWater[idx] )
					continue;

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var dist = MathF.Sqrt( (wx * wx) + (wy * wy) );
				if ( dist < 1f || dist > searchRadiusMeters )
					continue;

				nearest = Math.Min( nearest, dist );
			}
		}

		return nearest < float.MaxValue ? nearest : -1f;
	}

	public static bool IsAzureCoast( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( !settings.EnableAzureCoastBiome )
			return false;

		EnsureBuilt( settings );
		if ( !TryWorldToIndex( settings, worldXMeters, worldYMeters, out var idx ) )
			return false;

		return _azureCoast.Length > idx && _azureCoast[idx];
	}

	public static bool IsBlackwater( TerrainPreviewSettings settings, float worldXMeters, float worldYMeters )
	{
		if ( !settings.EnableBlackwaterBiome )
			return false;

		if ( _blackwaterSpots.Length == 0 && !_isBuilding )
			EnsureBuilt( settings );

		return ContainsBlackwaterAtWorld( settings, worldXMeters, worldYMeters );
	}

	static bool ContainsBlackwaterAtWorld(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		if ( _blackwaterSpots.Length == 0 )
			return false;

		var insideSpot = false;
		for ( var i = 0; i < _blackwaterSpots.Length; i++ )
		{
			var spot = _blackwaterSpots[i];
			var dx = worldXMeters - spot.CenterXMeters;
			var dy = worldYMeters - spot.CenterYMeters;
			var radiusSqLimit = spot.RadiusMeters * spot.RadiusMeters;
			if ( (dx * dx) + (dy * dy ) <= radiusSqLimit )
			{
				insideSpot = true;
				break;
			}
		}

		if ( !insideSpot )
			return false;

		var spawnDist = MathF.Sqrt( (worldXMeters * worldXMeters) + (worldYMeters * worldYMeters ) );
		if ( spawnDist > settings.LandRadiusMeters )
			return false;

		if ( settings.EnableInteriorWaterLayer
			&& TryWorldToIndex( settings, worldXMeters, worldYMeters, out var idx )
			&& _openWater.Length > idx
			&& _openWater[idx] )
			return false;

		return true;
	}

	public static TerrainPreviewBiomeResolver.LandBiomeWeights GetFilteredPlacementWeights(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters )
	{
		EnsureBuilt( settings );
		if ( !TryWorldToIndex( settings, worldXMeters, worldYMeters, out var idx ) )
			return default;

		if ( idx >= _openWater.Length || _openWater[idx] )
			return default;

		if ( idx >= _placementWeights.Length )
			return default;

		return _placementWeights[idx];
	}

	static void EnsureBuilt( TerrainPreviewSettings settings )
	{
		lock ( _buildLock )
		{
			if ( _isBuilding )
				return;

			_isBuilding = true;
			try
			{
				_fieldResolution = ResolveFieldResolution( settings );
				EnsureBiomePlacement( settings );
				if ( TerrainPreviewGenerateProgress.ShouldAbort() )
					return;

				EnsureWaterMask( settings );
				if ( TerrainPreviewGenerateProgress.ShouldAbort() )
					return;

				EnsureAzureCoastMask( settings );
				if ( TerrainPreviewGenerateProgress.ShouldAbort() )
					return;

				FinalizeDryLandBiomes( settings );
				if ( TerrainPreviewGenerateProgress.ShouldAbort() )
					return;

				EnsureBlackwaterMask( settings );
			}
			finally
			{
				_isBuilding = false;
			}
		}
	}

	static void EnsureBiomePlacement( TerrainPreviewSettings settings )
	{
		var fingerprint = ComputeBiomePlacementFingerprint( settings );
		if ( fingerprint == _biomePlacementFingerprint && RasterArraysMatch( _fieldResolution ) )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Land biomes" );
		BuildLandDisk( settings, out _landDisk );
		BuildRawLandBiomeMap( settings, _landDisk, out _rawLandBiomes );
		_biomePlacementFingerprint = fingerprint;
		_finalizeFingerprint = int.MinValue;
	}

	static void EnsureWaterMask( TerrainPreviewSettings settings )
	{
		var fingerprint = ComputeWaterFingerprint( settings );
		if ( fingerprint == _waterFingerprint && WaterRasterReady( _fieldResolution ) )
			return;

		var res = _fieldResolution;
		var metersPerPixel = settings.WorldDiameterMeters / Math.Max( 1, res );

		TerrainPreviewGenerateProgress.SetStage( "Lake mask — sample" );
		var lakeMaskGrid = TerrainPreviewLakeThresholdSolver.SampleLakeMaskGrid( settings, _landDisk, res );
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Lake mask — threshold" );
		_openWaterThreshold01 = TerrainPreviewLakeThresholdSolver.ResolveThreshold01(
			settings, _landDisk, res, metersPerPixel, lakeMaskGrid );
		if ( TerrainPreviewGenerateProgress.ShouldAbort() )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Lake mask" );
		TerrainPreviewLakeThresholdSolver.ApplyThresholdWithSpeck(
			settings,
			_landDisk,
			res,
			metersPerPixel,
			lakeMaskGrid,
			_openWaterThreshold01,
			out _openWater,
			out _lakeCoverageOnLand01 );

		_waterFingerprint = fingerprint;
		_finalizeFingerprint = int.MinValue;
		_azureCoastFingerprint = int.MinValue;
		_blackwaterFingerprint = int.MinValue;
	}

	static void EnsureAzureCoastMask( TerrainPreviewSettings settings )
	{
		var fingerprint = ComputeAzureCoastFingerprint( settings );
		if ( fingerprint == _azureCoastFingerprint && _azureCoast.Length > 0 )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Azure coast" );
		var res = _fieldResolution;
		TerrainPreviewAzureCoast.BuildMask(
			settings,
			_landDisk,
			_openWater,
			res,
			settings.WorldRadiusMeters,
			settings.WorldDiameterMeters,
			out _azureCoast );
		_azureCoastFingerprint = fingerprint;
		_finalizeFingerprint = int.MinValue;
		_blackwaterFingerprint = int.MinValue;
	}

	static void EnsureBlackwaterMask( TerrainPreviewSettings settings )
	{
		var fingerprint = ComputeBlackwaterFingerprint( settings );
		if ( fingerprint == _blackwaterFingerprint && _blackwater.Length > 0 )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Blackwater" );
		var res = _fieldResolution;
		TerrainPreviewBlackwater.BuildMask(
			settings,
			_landDisk,
			_openWater,
			res,
			settings.WorldRadiusMeters,
			settings.WorldDiameterMeters,
			out _blackwater,
			out _blackwaterSpots );
		_blackwaterFingerprint = fingerprint;
	}

	static int ComputeBlackwaterFingerprint( TerrainPreviewSettings settings )
	{
		var hash = new HashCode();
		hash.Add( _waterFingerprint );
		hash.Add( settings.WorldSeed );
		hash.Add( settings.EnableBlackwaterBiome );
		TerrainPreviewMountainSpawnMask.AddSettingsFingerprint( hash, settings );
		hash.Add( settings.BlackwaterSpotCount );
		hash.Add( settings.BlackwaterMinDiameterMeters );
		hash.Add( settings.BlackwaterMaxDiameterMeters );
		hash.Add( settings.BlackwaterMinDistanceFromSpawnMeters );
		hash.Add( settings.BlackwaterMaxDistanceFromSpawnMeters );
		hash.Add( settings.BlackwaterMountainClearanceMeters );
		hash.Add( settings.BlackwaterMinDistanceFromOtherMeters );
		return hash.ToHashCode();
	}

	static int ComputeAzureCoastFingerprint( TerrainPreviewSettings settings )
		=> HashCode.Combine(
			_waterFingerprint,
			settings.EnableAzureCoastBiome,
			settings.AzureCoastIncludeRimOcean,
			settings.AzureCoastWidthMeters,
			settings.AzureCoastMinDistanceFromSpawnMeters,
			settings.AzureCoastTargetRegionCount,
			settings.AzureCoastAlongShoreRunMeters,
			settings.AzureCoastAlongShoreRunCutoff01 );

	static void FinalizeDryLandBiomes( TerrainPreviewSettings settings )
	{
		var fingerprint = HashCode.Combine(
			_biomePlacementFingerprint,
			_waterFingerprint,
			settings.BiomeSpeckFilterEnabled,
			settings.BiomeMinPatchDiameterMeters );

		if ( fingerprint == _finalizeFingerprint && _placementWeights.Length > 0 )
			return;

		TerrainPreviewGenerateProgress.SetStage( "Biome patch merge" );
		var res = _fieldResolution;
		if ( !WaterRasterReady( res ) || _rawLandBiomes.Length != RasterCellCount( res ) )
		{
			_waterFingerprint = int.MinValue;
			EnsureWaterMask( settings );
		}

		if ( !WaterRasterReady( res ) )
			return;

		var metersPerPixel = settings.WorldDiameterMeters / Math.Max( 1, res );
		var dryBiomeMap = new TerrainPreviewBiomeId[RasterCellCount( res )];
		_placementWeights = new TerrainPreviewBiomeResolver.LandBiomeWeights[dryBiomeMap.Length];

		for ( var i = 0; i < dryBiomeMap.Length; i++ )
		{
			if ( i >= _landDisk.Length || i >= _openWater.Length || i >= _rawLandBiomes.Length
				|| !_landDisk[i] || _openWater[i] )
			{
				dryBiomeMap[i] = TerrainPreviewBiomeId.None;
				continue;
			}

			dryBiomeMap[i] = _rawLandBiomes[i];
		}

		if ( settings.BiomeSpeckFilterEnabled )
		{
			TerrainPreviewBiomeSpeckFilter.MergeSmallPatches(
				dryBiomeMap, res, res, metersPerPixel, settings.BiomeMinPatchDiameterMeters, maxPasses: 1 );
		}

		for ( var i = 0; i < dryBiomeMap.Length; i++ )
		{
			if ( i >= _landDisk.Length || i >= _openWater.Length || !_landDisk[i] || _openWater[i] )
				continue;

			_placementWeights[i] = TerrainPreviewBiomeResolver.WeightsFromDominantBiome( dryBiomeMap[i] );
		}

		_finalizeFingerprint = fingerprint;
	}

	static void BuildLandDisk( TerrainPreviewSettings settings, out bool[] landDisk )
	{
		var res = _fieldResolution;
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		landDisk = new bool[res * res];

		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return;

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );

				var dist = MathF.Sqrt( wx * wx + wy * wy );
				landDisk[idx] = dist <= settings.LandRadiusMeters;
			}
		}
	}

	static void BuildRawLandBiomeMap(
		TerrainPreviewSettings settings,
		bool[] landDisk,
		out TerrainPreviewBiomeId[] biomeMap )
	{
		var res = _fieldResolution;
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		var seed = settings.WorldSeed;
		biomeMap = new TerrainPreviewBiomeId[res * res];

		for ( var py = 0; py < res; py++ )
		{
			if ( TerrainPreviewGenerateProgress.ShouldAbort() )
				return;

			for ( var px = 0; px < res; px++ )
			{
				var idx = (py * res) + px;
				if ( !landDisk[idx] )
				{
					biomeMap[idx] = TerrainPreviewBiomeId.None;
					continue;
				}

				TerrainBiomeMapCoordinates.RasterPixelToWorldMeters(
					px, py, res, radius, diameter, out var wx, out var wy );
				var nx = (wx + radius) / diameter;
				var ny = (wy + radius) / diameter;
				var heightAfterCurve = TerrainPreviewBaseHeight.SampleAfterCurve01( settings, nx, ny, seed, out _ );
				var weights = TerrainPreviewBiomeResolver.SamplePlacementWeights(
					settings, wx, wy, heightAfterCurve );
				biomeMap[idx] = TerrainPreviewBiomeResolver.PickDominantPlacementBiome( weights );
			}
		}
	}

	public static bool TryWorldToIndex(
		TerrainPreviewSettings settings,
		float worldXMeters,
		float worldYMeters,
		out int index )
	{
		index = 0;
		var res = _fieldResolution;
		var radius = settings.WorldRadiusMeters;
		var diameter = settings.WorldDiameterMeters;
		if ( diameter <= 0f )
			return false;

		var dist = MathF.Sqrt( worldXMeters * worldXMeters + worldYMeters * worldYMeters );
		if ( dist > settings.LandRadiusMeters )
			return false;

		var py = (int)MathF.Floor( ((worldYMeters + radius) / diameter) * res );
		var pxMirror = (int)MathF.Floor( ((worldXMeters + radius) / diameter) * res );
		var px = (res - 1) - pxMirror;
		if ( px < 0 || py < 0 || px >= res || py >= res )
			return false;

		index = (py * res) + px;
		return true;
	}

	static int ComputeBiomePlacementFingerprint( TerrainPreviewSettings settings )
	{
		var hash = new HashCode();
		hash.Add( settings.WorldSeed );
		hash.Add( settings.ClampedResolution );
		hash.Add( settings.WorldDiameterMeters );
		hash.Add( settings.EnableContinentalLayer );
		hash.Add( settings.ContinentalFrequency );
		hash.Add( settings.ContinentalWeight );
		hash.Add( settings.EnableHillLayer );
		hash.Add( settings.HillFrequency );
		hash.Add( settings.HillWeight );
		hash.Add( settings.EnableValleyLayer );
		hash.Add( settings.ValleyFrequency );
		hash.Add( settings.ValleyWeight );
		hash.Add( settings.EnableHeightCurveLayer );
		hash.Add( settings.HeightCurvePower );
		hash.Add( settings.BiomePickerFrequency );
		hash.Add( settings.BiomeCloverGuaranteeSpawn );
		hash.Add( settings.BiomeMinPatchDiameterMeters );
		hash.Add( settings.BiomeAmberWeight );
		hash.Add( settings.BiomeCloverWeight );
		hash.Add( settings.BiomeRedwoodWeight );
		TerrainPreviewMountainSpawnMask.AddSettingsFingerprint( hash, settings );
		return hash.ToHashCode();
	}

	static int ComputeWaterFingerprint( TerrainPreviewSettings settings )
	{
		var hash = new HashCode();
		hash.Add( settings.ClampedResolution );
		hash.Add( settings.WorldDiameterMeters );
		hash.Add( settings.WorldSeed );
		hash.Add( settings.LakeOffsetXMeters );
		hash.Add( settings.LakeOffsetYMeters );
		hash.Add( settings.EnableInteriorWaterLayer );
		hash.Add( settings.LakeAutoThreshold );
		hash.Add( settings.LakeMaskThreshold01 );
		hash.Add( settings.TargetLakeCoverageOnLand01 );
		hash.Add( settings.LakeMacroFrequency );
		hash.Add( settings.LakeMediumFrequency );
		hash.Add( settings.LakeMacroOctaves );
		hash.Add( settings.LakeShoreDetail01 );
		hash.Add( settings.SpeckMinPatchDiameterMeters );
		return hash.ToHashCode();
	}
}
