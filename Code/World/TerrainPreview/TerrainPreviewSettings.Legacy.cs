namespace Survival;

/// <summary>Deserialize-only fields for old preview JSON bundles. No [Property] — not shown in the editor.</summary>
public sealed partial class TerrainPreviewSettings
{
	// bush.png vertex-colour tiling (replaced by the world-tiled terrain_ground.vmat texture 2026-10-12).
	public float BiomeCloverGroundTextureTileMeters { get; set; } = 12f;

	// Noise-scatter biome placement (replaced by seeded regions 2026-10-08) - still in saved recipes.
	public int BiomeScatterOctaves { get; set; } = 1;
	public float AzureCoastMinDistanceFromSpawnMeters { get; set; } = 5500f;   // replaced by AzureCoastMinDistanceFromSpawn01 (2026-10-13)
	public float BiomePickerFrequency { get; set; } = 30f;
	public bool EnableValleyAutoTune { get; set; }
	public bool RetrySeedsUntilSolved { get; set; }
	public int ValleyAutoMaxSeedAttempts { get; set; } = 100;
	public bool EnableValleyOceanAutoWeight { get; set; }
	public bool EnableValleySpawnAutoFrequency { get; set; }
	public bool EnableValleyAutoExhaustiveSearch { get; set; }
	public bool RejectSeedOnAutoFailure { get; set; }
	public float ValleyAutoSearchTimeoutSeconds { get; set; }
	public int ValleyAutoMaxIterationsPerSeed { get; set; } = 100;
	public int ValleyAutoTunePreviewResolution { get; set; } = 256;
	public float ValleyOceanWeightStep { get; set; } = 0.1f;
	public float ValleyOceanAutoMinInteriorFraction01 { get; set; } = 0.08f;
	public float ValleyOceanAutoMaxTotalFraction01 { get; set; } = 0.13f;
	public float ValleyOceanAbsoluteMaxTotalFraction01 { get; set; } = 0.25f;
	public float ValleySpawnLandRadiusMeters { get; set; } = 50f;
	public float ValleySpawnMinLandFraction01 { get; set; } = 0.5f;
	public float ValleySpawnAcceptableLandFraction01 { get; set; } = 0.5f;
	public bool SpawnRequireLandEscape { get; set; } = true;
	public float SpawnEscapeMinDistanceMeters { get; set; } = 400f;
	public float ValleyOceanMaxExteriorFraction01 { get; set; } = 0.17f;
	public float ValleyAutoFrequencyStep { get; set; } = 4f;
	public float ValleyAutoFrequencyMin { get; set; } = 16f;
	public float ValleyAutoFrequencyMax { get; set; } = 40f;
	public float ValleyNearWaterMaxDistanceMeters { get; set; } = 5000f;
	public float ValleyInnerHalfRadius01 { get; set; } = 0.5f;
	public float ValleyInnerHalfMinOceanFraction01 { get; set; } = 0.02f;

	public float InteriorWaterWeight { get; set; } = 1f;
	public float InteriorWaterAutoStep { get; set; } = 0.05f;
	public float LakeMacroWavelengthMeters { get; set; } = 2200f;
	public float LakeMediumWavelengthMeters { get; set; } = 650f;
	public float LakeMacroMin01 { get; set; } = 0.48f;
	public float LakeMacroSpan01 { get; set; } = 0.10f;
	public float LakeMediumMix01 { get; set; } = 0.58f;
	public float InteriorLakeBasinPower { get; set; } = 0.95f;
	public float LakeBreakerFrequencyScale { get; set; } = 2.4f;
	public float LakeBreakerMin01 { get; set; } = 0.40f;
	public float LakeBreakerSpan01 { get; set; } = 0.12f;
	public float LakeBreakerStrength01 { get; set; } = 0.35f;
	public float LakeMinBasinDiameterMeters { get; set; } = 80f;
	public float LakeMaxBasinDiameterMeters { get; set; } = 2800f;
	public bool LakeMaxBasinFilterEnabled { get; set; } = true;
	public float LakeShoreBlendWidth01 { get; set; } = 0.1f;
	public float InteriorLakeMountainClearCarve01 { get; set; } = 0.85f;
	public float TargetTotalOceanFraction01 { get; set; } = 0.08f;
	public float TargetInteriorOceanFraction01 { get; set; } = 0.25f;
	public float InteriorZoneRadius01 { get; set; } = 0.7f;

	public float LakeLargeFrequency { get; set; } = 0.42f;
	public float LakeSmallFrequency { get; set; } = 4.5f;
	public float LakeSmallWavelengthMeters { get; set; } = 140f;
	public float LakeSmallAmplitude01 { get; set; } = 0.1f;
	public float LakeSmallDetailGate01 { get; set; } = 0.14f;
	public float LakeSmallDetailSpan01 { get; set; } = 0.12f;
	public bool LakeOversizedBasinFilterEnabled { get; set; } = true;
	public float InteriorWaterFrequency { get; set; } = 2f;
	public float InteriorWaterCenterInfluence01 { get; set; } = 0.08f;
	public float InteriorWaterFullInfluenceRadius01 { get; set; } = 0.35f;
	public float InteriorWaterFalloffPower { get; set; } = 0.4f;
	public float InteriorWaterEdgeFade01 { get; set; } = 0.12f;
	public float InteriorLakeCarveMin01 { get; set; } = 0.038f;
	public float InteriorLakeMacroFrequencyScale { get; set; } = 0.46f;
	public float InteriorLakeMacroMix01 { get; set; } = 0.66f;
	public float InteriorLakeShoreExpand01 { get; set; } = 0.72f;
	public float SeaLevelHeight01 { get; set; } = 0.001f;

	// Deprecated mountain mask / spawn-solve fields — old preview JSON only; no runtime effect.
	public float MountainSpawnMacroFrequency { get; set; } = 0.78f;
	public float MountainSpawnMacroMin01 { get; set; } = 0.52f;
	public float MountainSpawnMacroSpan01 { get; set; } = 0.11f;
	public bool EnableMountainSpawnSolveOnGenerate { get; set; }
	public float MountainNearSpawnMinMeters { get; set; }
	public float MountainNearSpawnMaxMeters { get; set; }
	public float MountainNearSpawnTargetMeters { get; set; }
	public float MountainOffsetXMeters { get; set; }
	public float MountainOffsetYMeters { get; set; }
	public float MountainMaxOffsetMeters { get; set; }
	public bool MountainSpawnOversizedPatchFilterEnabled { get; set; }
	public float MountainSpawnMaxPatchDiameterMeters { get; set; }
	public float MountainSpawnPatchSeparation01 { get; set; }
	public float MountainSpawnCoastalBandMeters { get; set; }
	public float MountainSpawnCoastalAlign01 { get; set; }
	public float MountainSpawnCoastalStretch01 { get; set; }
	public int MountainSpawnMinNeighborCount { get; set; }
	public float MountainSpawnRegionFrequencyScale { get; set; }
	public float MountainSpawnRegionMin01 { get; set; }
	public float MountainSpawnRegionSpan01 { get; set; }
	public float LandMinPatchDiameterMeters { get; set; } = 80f;
	public float InlandWaterMinPatchDiameterMeters { get; set; } = 80f;
	public bool InlandWaterSpeckFilterEnabled { get; set; } = true;
	public float LakeShoreBlendStrength01 { get; set; } = 0.68f;
	public int AzureCoastTargetRegionCount { get; set; } = 12;
	public float AzureCoastRegionCellSizeMeters { get; set; } = 2500f;
	public float AzureCoastAlongShoreRunMeters { get; set; } = 500f;
	public float AzureCoastAlongShoreRunCutoff01 { get; set; } = 0.38f;
	public float AzureCoastAlongShoreFadeMeters { get; set; } = 120f;
	public float SeaLevelSubmergeDepthMeters { get; set; } = 4f;
	public bool EnableOrganicOceanFloor { get; set; } = true;
	public float OceanFloorFrequency { get; set; } = 1.5f;
	public float OceanFloorMaxDepthMeters { get; set; } = 35f;
	public float InlandLakeMaxDepthMeters { get; set; } = 12f;
	public float MountainHeightBlendBand01 { get; set; } = 0.14f;
	public float MountainLowlandSculptDeltaCap01 { get; set; } = 0.16f;
	public float MountainPeakExcessPower { get; set; } = 1.45f;
	public float MountainPeakMaxLift01 { get; set; } = 0.82f;
	public float MountainHeightDisplayBlendSoftness01 { get; set; } = 0.72f;
	public bool EnableMountainPeakSlopeMask { get; set; } = true;
	public float MountainPeakBaseSlopeDegrees { get; set; } = 14f;
	public float MountainPeakTypicalMaxSlopeDegrees { get; set; } = 34f;
	public float MountainPeakMaxSlopeDegrees { get; set; } = 58f;
	public float MountainCliffCoverage01 { get; set; } = 0.05f;
	public float MountainPeakSlopeRampStart01 { get; set; } = 0.25f;
	public float MountainPeakSlopeRampEnd01 { get; set; } = 0.72f;
	public float MountainPeakPlateauStart01 { get; set; } = 0.86f;
	public float MountainPeakPlateauFlatten01 { get; set; } = 0.62f;
	public float MountainPeakDetailFrequency { get; set; } = 9f;
	public float MountainPeakDetailAmplitude01 { get; set; } = 0.04f;
	public float MountainTraverseCorridorFrequency { get; set; } = 2.2f;
	public float MountainTraverseCorridorStrength01 { get; set; } = 0.55f;

	/// <summary>Old Clover gentle-roll knobs — superseded by Voronoi hill spacing / micro meters.</summary>
	public float BiomeCloverRollFrequency { get; set; } = 4.5f;
	public float BiomeCloverRollAmplitude01 { get; set; } = 0.09f;

	/// <summary>Old land-rolling macro/detail knobs — lowlands use sculpted base height capped at Lowland Height Max.</summary>
	public float LandRollingMacroFrequency { get; set; } = 0.55f;
	public int LandRollingMacroOctaves { get; set; } = 4;
	public float LandRollingDetailFrequencyScale { get; set; } = 2.5f;
	public float LandRollingDetailAmplitude01 { get; set; } = 0.12f;
	public bool NormalizeLandRollingHeights { get; set; }
}

// Biome slope smoothing pass (removed 2026-10-05: the pass had no caller) and the first-generation Redwood knobs.
public sealed partial class TerrainPreviewSettings
{
	public bool EnableBiomeSlopeSmoothing { get; set; }
	public float BiomeRedwoodSlopeSmooth01 { get; set; } = 0.38f;
	public float BiomeAmberSlopeSmooth01 { get; set; } = 0.82f;
	public float BiomeMountainSlopeSmooth01 { get; set; } = 0.12f;
	public float BiomeSlopeDetailGate01 { get; set; } = 0.12f;
	public float BiomeRedwoodHillFrequency { get; set; } = 6f;
	public float BiomeRedwoodHillAmplitude01 { get; set; } = 0.11f;
	public float BiomeRedwoodRidgeAmplitude01 { get; set; } = 0.045f;
}

// Blended post-cap gates (the pass had no caller) and the unreachable Mountain base shaper, removed 2026-10-05.
public sealed partial class TerrainPreviewSettings
{
	public float BiomeMountainMinSlopeDegrees { get; set; } = 8f;
	public float BiomeMountainMinPeakLift01 { get; set; } = 0.12f;
	public float BiomeMountainCapWeightFullAt01 { get; set; } = 0.35f;
	public float BiomeMountainMinHeight01 { get; set; } = 0.34f;
	public float BiomeHeightCapBorderSharpness { get; set; } = 1.35f;
	public bool EnableBiomeHeightBlend { get; set; } = true;
	public float BiomeMountainBaseRuggedAmplitude01 { get; set; } = 0.06f;
}

// Knobs with no read site (dead peak cap, summit flatten, boundary/weight noise, picker octaves), retired 2026-10-05.
public sealed partial class TerrainPreviewSettings
{
	public float MountainTypicalPeakMax01 { get; set; } = 0.78f;
	public float MountainAbsolutePeakMax01 { get; set; } = 0.985f;
	public float MountainSummitLocalPeakMin01 { get; set; } = 0.38f;
	public float CoastalInlandHeightMargin01 { get; set; } = 0.12f;
	public float BiomeMountainSummitFlattenStart01 { get; set; } = 0.88f;
	public float BiomeMountainSummitFlattenStrength01 { get; set; } = 0.55f;
	public float BiomeBoundaryNoiseFrequency { get; set; } = 8f;
	public float BiomeDistanceWarpMeters { get; set; } = 0f;
	public float BiomeWeightNoiseStrength01 { get; set; } = 0f;
	public int BiomePickerOctaves { get; set; } = 3;
}
