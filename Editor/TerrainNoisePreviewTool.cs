/// <summary>
/// Scene-view editor tool: live procedural terrain noise preview (PNG + texture) without spawning chunks.
/// </summary>
[EditorTool]
[Title( "Terrain Noise Preview" )]
[Icon( "map" )]
[Alias( "tools.terrain-noise-preview" )]
[Group( "Survival" )]
[Order( 100 )]
public sealed class TerrainNoisePreviewTool : EditorTool
{
	TerrainNoisePreviewWindow _window;

	public override void OnEnabled()
	{
		_window = new TerrainNoisePreviewWindow();
		AddOverlay( _window, TextFlag.LeftTop, 12 );
	}

	public override void OnDisabled()
	{
		_window?.Destroy();
		_window = null;
	}
}

/// <summary>Draggable scene overlay for terrain preview controls and live texture.</summary>
sealed class TerrainNoisePreviewWindow : WidgetWindow
{
	readonly TerrainPreviewSettings _settings = new();
	SerializedObject _serializedSettings;
	TextureWidget _previewWidget;
	Label _statusLabel;
	Label _statsLabel;
	Button _generateButton;
	Button _cancelButton;

	Texture _liveTexture;
	bool _isGenerating;
	bool _livePreviewEnabled = true;
	int _livePreviewResolution = 512;
	int _settingsFingerprint;
	RealTimeSince _liveDebounce;
	bool _liveRegenScheduled;
	bool _uiAlive = true;
	TerrainPreviewWaterCoverageStats _lastWaterCoverage;
	TerrainPreviewGenerationMetrics _lastMetrics;
	TerrainPreviewCategoryTabs _tabs;

	public TerrainNoisePreviewWindow() : base( null, "Terrain Noise Preview" )
	{
		MinimumWidth = 1080;
		MaximumWidth = 1400;
		Layout = Layout.Row();
		Layout.Margin = 8;
		Layout.Spacing = 8;

		BuildUi();
	}

	public override void OnDestroyed()
	{
		_uiAlive = false;
		base.OnDestroyed();
	}

	void SetStatusText( string text )
	{
		if ( !_uiAlive || _statusLabel is null )
			return;

		_statusLabel.Text = text;
	}

	void SetStatsText( string text )
	{
		if ( !_uiAlive || _statsLabel is null )
			return;

		_statsLabel.Text = text;
	}

	void BuildUi()
	{
		var controlsPanel = new Widget( this );
		controlsPanel.Layout = Layout.Column();
		controlsPanel.Layout.Spacing = 6;
		controlsPanel.MinimumWidth = 360;
		controlsPanel.MaximumWidth = 420;

		_serializedSettings = EditorTypeLibrary.GetSerializedObject( _settings );

		var tabs = new TerrainPreviewCategoryTabs( controlsPanel )
		{
			StateCookie = "terrain-noise-preview",
		};
		_tabs = tabs;

		tabs.AddPage( "World", "public", CreateSettingsTab( TerrainPreviewControlTabs.World ) );
		tabs.AddPage( "Continental", "language", CreateSettingsTab( TerrainPreviewControlTabs.Continental ) );
		tabs.AddPage( "Hills", "landscape", CreateSettingsTab( TerrainPreviewControlTabs.Hills ) );
		tabs.AddPage( "Valleys", "south", CreateSettingsTab( TerrainPreviewControlTabs.Valleys ) );
		tabs.AddPage( "Height Curve", "show_chart", CreateSettingsTab( TerrainPreviewControlTabs.HeightCurve ) );
		tabs.AddPage( "Water", "water", CreateSettingsTab( TerrainPreviewControlTabs.Water ) );
		tabs.AddPage( "Lakes", "waves", CreateSettingsTab( TerrainPreviewControlTabs.Lakes ) );
		tabs.AddPage( "Biomes", "park", CreateSettingsTab( TerrainPreviewControlTabs.Biomes ) );
		tabs.AddPage( "Biome Shape", "brush", CreateSettingsTab( TerrainPreviewControlTabs.BiomeTerrain ) );
		tabs.AddPage( "Biome Weights", "blur_linear", CreateSettingsTab( TerrainPreviewControlTabs.BiomeWeights ) );
		tabs.AddPage( "Slope", "trending_up", CreateSettingsTab( TerrainPreviewControlTabs.Slope ) );
		tabs.AddPage( "Biome Transition", "swap_horiz", CreateSettingsTab( TerrainPreviewControlTabs.BiomeTransition ) );
		tabs.AddPage( "Mountain Mask", "terrain", CreateSettingsTab( TerrainPreviewControlTabs.MountainMask ) );
		tabs.AddPage( "Mountains", "landscape", CreateSettingsTab( TerrainPreviewControlTabs.Mountains ) );
		tabs.AddPage( "Mountain Falloff", "donut_large", CreateSettingsTab( TerrainPreviewControlTabs.MountainFalloff ) );
		tabs.FinishSetup();

		controlsPanel.Layout.Add( tabs );

		_generateButton = new Button( "Generate Preview", "refresh" )
		{
			ToolTip = "Re-run preview for the selected preview layer and update PNG + live texture",
		};
		_generateButton.Clicked = () => _ = GeneratePreviewAsync( liveOnly: false );

		_cancelButton = new Button( "Cancel", "close" )
		{
			ToolTip = "Stop auto-tune / seed search and finish preview with current settings",
			Enabled = false,
		};
		_cancelButton.Clicked = () =>
		{
			if ( !_isGenerating )
				return;

			TerrainPreviewMapIterationTracker.RequestUserAbort();
			SetStatusText( "Cancelling…" );
		};

		var actionRow = new Widget( this );
		actionRow.Layout = Layout.Row();
		actionRow.Layout.Spacing = 6;
		actionRow.Layout.Add( _generateButton );
		actionRow.Layout.Add( _cancelButton );
		controlsPanel.Layout.Add( actionRow );

		var liveRow = new Widget( this );
		liveRow.Layout = Layout.Row();
		liveRow.Layout.Spacing = 6;
		var liveCheck = new Checkbox( "Live preview on slider change" );
		liveCheck.Value = _livePreviewEnabled;
		liveCheck.Toggled = () => _livePreviewEnabled = liveCheck.Value;
		liveRow.Layout.Add( liveCheck );
		controlsPanel.Layout.Add( liveRow );

		_statusLabel = new Label( "Press Generate Preview to sample the current settings." );
		_statusLabel.WordWrap = true;
		controlsPanel.Layout.Add( _statusLabel );

		Layout.Add( controlsPanel );

		var previewPanel = new Widget( this );
		previewPanel.Layout = Layout.Column();
		previewPanel.Layout.Spacing = 4;
		previewPanel.MinimumWidth = 520;

		previewPanel.Layout.Add( new Label( "Live Preview" ) { ToolTip = "Updates on Generate, or automatically ~350ms after slider changes when Live preview is on" } );

		_previewWidget = new TextureWidget
		{
			FixedSize = 512,
			RetainAspectRatio = true,
			Padding = 2,
		};
		previewPanel.Layout.Add( _previewWidget );

		Layout.Add( previewPanel );

		var statsPanel = new Widget( this );
		statsPanel.Layout = Layout.Column();
		statsPanel.Layout.Spacing = 4;
		statsPanel.MinimumWidth = 240;
		statsPanel.MaximumWidth = 300;

		statsPanel.Layout.Add( new Label( "Generate Stats" ) { ToolTip = "Coverage, targets, limits, and tune steps" } );
		_statsLabel = new Label( "—" ) { WordWrap = true };
		statsPanel.Layout.Add( _statsLabel );

		Layout.Add( statsPanel );
	}

	Widget CreateSettingsTab( HashSet<string> propertyNames )
	{
		var page = new Widget( null );
		page.Layout = Layout.Column();
		page.Layout.Margin = 0;

		var scroll = new ScrollArea( page );
		scroll.FocusMode = FocusMode.None;
		scroll.Canvas = new Widget( scroll );
		scroll.Canvas.Layout = Layout.Column();
		scroll.Canvas.Layout.Margin = new Sandbox.UI.Margin( 4, 0, 4, 0 );

		var sheet = new ControlSheet();
		sheet.AddObject( _serializedSettings, prop => propertyNames.Contains( prop.Name ) );
		scroll.Canvas.Layout.Add( sheet );

		page.Layout.Add( scroll );
		page.FixedHeight = 380;

		return page;
	}

	[EditorEvent.Frame]
	void OnLivePreviewFrame()
	{
		if ( !_livePreviewEnabled || _isGenerating )
			return;

		var fingerprint = ComputeSettingsFingerprint();
		if ( fingerprint != _settingsFingerprint )
		{
			_settingsFingerprint = fingerprint;
			_liveDebounce = 0;
			_liveRegenScheduled = true;
		}

		if ( _liveRegenScheduled && _liveDebounce > 0.35f )
		{
			_liveRegenScheduled = false;
			_ = GeneratePreviewAsync( liveOnly: true );
		}
	}

	int ComputeSettingsFingerprint()
	{
		if ( _serializedSettings is null )
			return 0;

		var hash = new HashCode();
		foreach ( var prop in _serializedSettings )
		{
			hash.Add( prop.Name, StringComparer.Ordinal );
			hash.Add( prop.GetValue<object>() );
		}

		return hash.ToHashCode();
	}

	async Task GeneratePreviewAsync( bool liveOnly )
	{
		if ( _isGenerating )
			return;

		_isGenerating = true;
		_generateButton.Enabled = false;
		_cancelButton.Enabled = !liveOnly;
		TerrainPreviewGenerateProgress.Reset();
		SetStatusText( liveOnly ? "Live preview…" : "Generating… starting" );
		if ( !liveOnly )
			TerrainPreviewMapIterationTracker.ClearUserAbort();

		Bitmap bitmap = null;
		TerrainPreviewLakeSpawnSolver.RunResult lakeSpawn = default;
		TerrainPreviewSettings settings = null;
		try
		{
			settings = _settings.CloneForGenerate( !liveOnly && _settings.RandomizeSeedOnGenerate );
			if ( liveOnly )
				settings.PreviewResolution = Math.Clamp( _livePreviewResolution, 64, settings.PreviewResolution );
			else if ( _settings.RandomizeSeedOnGenerate )
				_settings.WorldSeed = settings.WorldSeed;

			var work = Task.Run( () =>
			{
				if ( TerrainPreviewMapIterationTracker.IsAbortRequested )
					return;

				if ( !liveOnly && settings.EnableLakeSpawnSolveOnGenerate )
					lakeSpawn = TerrainPreviewLakeSpawnSolver.Run( settings );

				if ( TerrainPreviewMapIterationTracker.IsAbortRequested || lakeSpawn.Cancelled )
					return;

				var full = TerrainPreviewGenerator.Generate( settings );
				if ( TerrainPreviewMapIterationTracker.IsAbortRequested || full.Colors is null || full.Colors.Length == 0 )
					return;

				bitmap = new Bitmap( settings.ClampedResolution, settings.ClampedResolution );
				bitmap.SetPixels( full.Colors );
				_lastWaterCoverage = full.WaterCoverage;
				_lastMetrics = full.Metrics;
			} );

			while ( !work.IsCompleted )
			{
				if ( !_uiAlive )
					return;

				if ( liveOnly )
				{
					await Task.Delay( 33 );
					continue;
				}

				SetStatusText( TerrainPreviewGenerateProgress.FormatStatusLine( settings.WorldSeed ) );
				await Task.Delay( 33 );
			}

			await work;
			if ( TerrainPreviewMapIterationTracker.UserAbortRequested
				|| ( !liveOnly && settings.EnableLakeSpawnSolveOnGenerate && lakeSpawn.Cancelled ) )
			{
				SetStatusText( "Cancelled." );
				return;
			}

			if ( work.IsFaulted )
				throw work.Exception?.GetBaseException() ?? work.Exception;

			if ( settings.WorldSeed != _settings.WorldSeed )
				_settings.WorldSeed = settings.WorldSeed;
			if ( Math.Abs( settings.LakeOffsetXMeters - _settings.LakeOffsetXMeters ) > 0.01f )
				_settings.LakeOffsetXMeters = settings.LakeOffsetXMeters;
			if ( Math.Abs( settings.LakeOffsetYMeters - _settings.LakeOffsetYMeters ) > 0.01f )
				_settings.LakeOffsetYMeters = settings.LakeOffsetYMeters;

			if ( !_uiAlive )
				return;

			_liveTexture = bitmap.ToTexture( false );
			_previewWidget.Texture = _liveTexture;
			_settingsFingerprint = ComputeSettingsFingerprint();

			if ( liveOnly )
			{
				SetStatusText( $"Live · {TerrainPreviewGenerator.ModeDisplayName( settings.PreviewMode )} · {settings.ClampedResolution}px · seed {settings.WorldSeed}" );
				return;
			}

			var lakeLine = TerrainPreviewLakeSpawnSolver.FormatStatus( lakeSpawn, settings );
			var waterOnLand = _lastWaterCoverage.LandDiskLakeFraction01 * 100f;
			var target = settings.TargetLakeCoverageOnLand01 * 100f;
			SetStatsText(
				$"{lakeLine}\nWater on land: {waterOnLand:0.#}% (target {target:0.#}%)\n{_lastMetrics.FormatStatsBlock()}\nSaved metrics → generation_metrics.json" );

			var resultNote = lakeSpawn.Solved || !settings.EnableLakeSpawnSolveOnGenerate
				? "Generated"
				: "Spawn solve failed — PNG saved anyway";

			string pngPath = null;
			string bundleName = null;
			await Task.Run( () => pngPath = TerrainPreviewAssetExporter.ExportBitmap(
				bitmap, settings, out bundleName, _lastWaterCoverage, _lastMetrics ) );
			SetStatusText( $"{resultNote} · {TerrainPreviewGenerator.ModeDisplayName( settings.PreviewMode )} · seed {settings.WorldSeed}\nSaved {TerrainPreviewGenerator.ModeFileStem( settings.PreviewMode )}.png → Assets/terrain/preview/{bundleName}/" );
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Terrain preview generation failed: {ex}" );
			SetStatusText( $"Preview failed: {ex.Message}" );
		}
		finally
		{
			bitmap?.Dispose();
			_isGenerating = false;
			if ( _uiAlive )
			{
				_generateButton.Enabled = true;
				_cancelButton.Enabled = false;
			}
			TerrainPreviewMapIterationTracker.ClearUserAbort();
		}
	}
}

/// <summary>Maps preview layers to settings property names for tabbed ControlSheets.</summary>
static class TerrainPreviewControlTabs
{
	public static readonly HashSet<string> World = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.WorldDiameterMeters ),
		nameof( TerrainPreviewSettings.PreviewResolution ),
		nameof( TerrainPreviewSettings.WorldSeed ),
		nameof( TerrainPreviewSettings.RandomizeSeedOnGenerate ),
		nameof( TerrainPreviewSettings.EnableLakeSpawnSolveOnGenerate ),
		nameof( TerrainPreviewSettings.RetryLakeSeedsUntilSpawn ),
		nameof( TerrainPreviewSettings.LakeMaxSeedAttempts ),
		nameof( TerrainPreviewSettings.PreviewMode ),
		nameof( TerrainPreviewSettings.ShowPreviewDistanceRings ),
		nameof( TerrainPreviewSettings.PreviewDistanceRingIntervalMeters ),
		nameof( TerrainPreviewSettings.EnableContinentalLayer ),
		nameof( TerrainPreviewSettings.EnableHillLayer ),
		nameof( TerrainPreviewSettings.EnableValleyLayer ),
		nameof( TerrainPreviewSettings.EnableHeightCurveLayer ),
		nameof( TerrainPreviewSettings.EnableMountainLayer ),
	};

	public static readonly HashSet<string> Continental = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.ContinentalFrequency ),
		nameof( TerrainPreviewSettings.ContinentalWeight ),
	};

	public static readonly HashSet<string> Hills = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.HillFrequency ),
		nameof( TerrainPreviewSettings.HillWeight ),
	};

	public static readonly HashSet<string> Valleys = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.ValleyFrequency ),
		nameof( TerrainPreviewSettings.ValleyWeight ),
	};

	public static readonly HashSet<string> HeightCurve = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.HeightCurvePower ),
	};

	public static readonly HashSet<string> Water = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.EnableInteriorWaterLayer ),
		nameof( TerrainPreviewSettings.InlandDryLandSeaMarginMeters ),
		nameof( TerrainPreviewSettings.SeaLevelMeters ),
		nameof( TerrainPreviewSettings.SpeckMinPatchDiameterMeters ),
		nameof( TerrainPreviewSettings.LandSpeckFilterEnabled ),
	};

	public static readonly HashSet<string> Lakes = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.TargetLakeCoverageOnLand01 ),
		nameof( TerrainPreviewSettings.LakeAutoThreshold ),
		nameof( TerrainPreviewSettings.LakeMaskThreshold01 ),
		nameof( TerrainPreviewSettings.LakeMacroFrequency ),
		nameof( TerrainPreviewSettings.LakeMediumFrequency ),
		nameof( TerrainPreviewSettings.LakeMacroOctaves ),
		nameof( TerrainPreviewSettings.LakeShoreDetail01 ),
		nameof( TerrainPreviewSettings.LakeOffsetXMeters ),
		nameof( TerrainPreviewSettings.LakeOffsetYMeters ),
		nameof( TerrainPreviewSettings.LakeMaxOffsetMeters ),
		nameof( TerrainPreviewSettings.LakeSpawnCheckRadiusMeters ),
		nameof( TerrainPreviewSettings.LakeSpawnShowcaseWaterRadiusMeters ),
	};

	public static readonly HashSet<string> Biomes = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.BiomeOverlayStrength01 ),
		nameof( TerrainPreviewSettings.BiomeNoiseFrequency ),
		nameof( TerrainPreviewSettings.BiomeBoundaryNoiseFrequency ),
		nameof( TerrainPreviewSettings.BiomeDistanceWarpMeters ),
		nameof( TerrainPreviewSettings.BiomeWeightNoiseStrength01 ),
		nameof( TerrainPreviewSettings.BiomeScatterOctaves ),
		nameof( TerrainPreviewSettings.BiomePickerOctaves ),
		nameof( TerrainPreviewSettings.BiomePickerFrequency ),
		nameof( TerrainPreviewSettings.BiomeDistanceInfluenceScale01 ),
		nameof( TerrainPreviewSettings.BiomeSpeckFilterEnabled ),
		nameof( TerrainPreviewSettings.BiomeMinPatchDiameterMeters ),
		nameof( TerrainPreviewSettings.BiomeMountainMinHeight01 ),
		nameof( TerrainPreviewSettings.BiomeAppearInnerRampPower ),
		nameof( TerrainPreviewSettings.BiomeCloverGuaranteeSpawn ),
		nameof( TerrainPreviewSettings.BiomeSpawnBlendEndMeters ),
		nameof( TerrainPreviewSettings.BiomeSpawnCloverBlendBoost01 ),
		nameof( TerrainPreviewSettings.BiomeCloverRampFullDistanceMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverAppearEndMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverPriorityStartMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverPriorityEndMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverWeight ),
		nameof( TerrainPreviewSettings.BiomeCloverDistanceInfluenceStartMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverDistanceInfluenceEndMeters ),
		nameof( TerrainPreviewSettings.BiomeCloverPriorityWeight ),
		nameof( TerrainPreviewSettings.BiomeRedwoodHardMinDistanceMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodRampFullDistanceMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodAppearEndMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodPriorityStartMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodPriorityEndMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodWeight ),
		nameof( TerrainPreviewSettings.BiomeRedwoodPriorityWeight ),
		nameof( TerrainPreviewSettings.BiomeAmberHardMinDistanceMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberRampFullDistanceMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberAppearEndMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberPriorityStartMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberPriorityEndMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberWeight ),
		nameof( TerrainPreviewSettings.BiomeAmberPriorityWeight ),
	};

	public static readonly HashSet<string> BiomeTerrain = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.EnableBiomeHeightBlend ),
		nameof( TerrainPreviewSettings.BiomeCloverMaxHeightMeters ),
		nameof( TerrainPreviewSettings.BiomeRedwoodMaxHeightMeters ),
		nameof( TerrainPreviewSettings.BiomeAmberMaxHeightMeters ),
		nameof( TerrainPreviewSettings.BiomeMountainMaxHeightMeters ),
		nameof( TerrainPreviewSettings.BiomeMountainPlacementStrength01 ),
		nameof( TerrainPreviewSettings.BiomeCloverRollFrequency ),
		nameof( TerrainPreviewSettings.BiomeCloverRollAmplitude01 ),
		nameof( TerrainPreviewSettings.BiomeCloverShapeBlend01 ),
		nameof( TerrainPreviewSettings.BiomeCloverSlopeSmooth01 ),
		nameof( TerrainPreviewSettings.BiomeRedwoodHillFrequency ),
		nameof( TerrainPreviewSettings.BiomeRedwoodHillAmplitude01 ),
		nameof( TerrainPreviewSettings.BiomeRedwoodRidgeAmplitude01 ),
		nameof( TerrainPreviewSettings.BiomeRedwoodSlopeSmooth01 ),
		nameof( TerrainPreviewSettings.BiomeAmberDuneFrequency ),
		nameof( TerrainPreviewSettings.BiomeAmberDuneFloor01 ),
		nameof( TerrainPreviewSettings.BiomeAmberDuneAmplitude01 ),
		nameof( TerrainPreviewSettings.BiomeAmberDuneReshapeBlend01 ),
		nameof( TerrainPreviewSettings.BiomeAmberSlopeSmooth01 ),
		nameof( TerrainPreviewSettings.BiomeMountainBaseRuggedAmplitude01 ),
		nameof( TerrainPreviewSettings.BiomeMountainSlopeSmooth01 ),
		nameof( TerrainPreviewSettings.BiomeMountainSummitFlattenStart01 ),
		nameof( TerrainPreviewSettings.BiomeMountainSummitFlattenStrength01 ),
		nameof( TerrainPreviewSettings.BiomeSlopeDetailGate01 ),
	};

	public static readonly HashSet<string> BiomeWeights = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.PreviewMode ),
	};

	public static readonly HashSet<string> Slope = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.PreviewMode ),
		nameof( TerrainPreviewSettings.MountainSlopeSampleStepMeters ),
		nameof( TerrainPreviewSettings.BiomeMountainMinSlopeDegrees ),
	};

	public static readonly HashSet<string> BiomeTransition = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.PreviewMode ),
		nameof( TerrainPreviewSettings.BiomeHeightCapBorderSharpness ),
	};

	public static readonly HashSet<string> MountainMask = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.EnableMountainLayer ),
		nameof( TerrainPreviewSettings.PreviewMode ),
		nameof( TerrainPreviewSettings.BiomeMinMountainMask01 ),
		nameof( TerrainPreviewSettings.BiomeMountainPlacementStrength01 ),
		nameof( TerrainPreviewSettings.MountainSpawnMacroWavelengthMeters ),
		nameof( TerrainPreviewSettings.MountainSpawnMediumWavelengthMeters ),
		nameof( TerrainPreviewSettings.MountainSpawnRidgeSharpness ),
		nameof( TerrainPreviewSettings.MountainSpawnFieldFloor01 ),
		nameof( TerrainPreviewSettings.MountainSpawnMacroOctaves ),
		nameof( TerrainPreviewSettings.MountainSpawnMediumOctaves ),
		nameof( TerrainPreviewSettings.MountainSpawnMediumFrequencyScale ),
		nameof( TerrainPreviewSettings.MountainSpawnMediumMix01 ),
		nameof( TerrainPreviewSettings.MountainSpawnBreakerFrequencyScale ),
		nameof( TerrainPreviewSettings.MountainSpawnBreakerMin01 ),
		nameof( TerrainPreviewSettings.MountainSpawnBreakerSpan01 ),
		nameof( TerrainPreviewSettings.MountainSpawnBreakerStrength01 ),
		nameof( TerrainPreviewSettings.MountainSpawnWarpStrength01 ),
		nameof( TerrainPreviewSettings.MountainSpawnRangeStretch01 ),
		nameof( TerrainPreviewSettings.MountainSpawnRangePower01 ),
		nameof( TerrainPreviewSettings.MountainSpawnSpeckFilterEnabled ),
		nameof( TerrainPreviewSettings.MountainSpawnMinPatchDiameterMeters ),
		nameof( TerrainPreviewSettings.MountainSpawnMinPatchSupport01 ),
		nameof( TerrainPreviewSettings.MountainSpawnMinPatchGridSteps ),
	};

	public static readonly HashSet<string> Mountains = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.MountainThreshold ),
		nameof( TerrainPreviewSettings.MountainFrequency ),
		nameof( TerrainPreviewSettings.MountainPeakBoost ),
		nameof( TerrainPreviewSettings.MountainMinPeakHeight01 ),
		nameof( TerrainPreviewSettings.MountainPeakVariationFrequency ),
		nameof( TerrainPreviewSettings.MountainPeakRarityPower ),
		nameof( TerrainPreviewSettings.MountainTypicalPeakMax01 ),
		nameof( TerrainPreviewSettings.MountainAbsolutePeakMax01 ),
		nameof( TerrainPreviewSettings.MountainSummitMacroFrequency ),
		nameof( TerrainPreviewSettings.MountainSummitMacroThreshold01 ),
		nameof( TerrainPreviewSettings.MountainSummitLocalPeakMin01 ),
		nameof( TerrainPreviewSettings.MountainPeakBandWidth01 ),
		nameof( TerrainPreviewSettings.MountainPeakSharpnessPower ),
		nameof( TerrainPreviewSettings.MountainSummitExtraLift01 ),
		nameof( TerrainPreviewSettings.MountainSlopeSampleStepMeters ),
		nameof( TerrainPreviewSettings.MountainFoothillSpread ),
		nameof( TerrainPreviewSettings.MountainFoothillBoost ),
	};

	public static readonly HashSet<string> MountainFalloff = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.MountainInnerRadius01 ),
		nameof( TerrainPreviewSettings.MountainOuterRadius01 ),
		nameof( TerrainPreviewSettings.MountainBandFade01 ),
		nameof( TerrainPreviewSettings.MountainFalloffRimPower ),
		nameof( TerrainPreviewSettings.MountainMidMapEmphasis01 ),
		nameof( TerrainPreviewSettings.MountainMidMapRadialPeak01 ),
		nameof( TerrainPreviewSettings.MountainMidMapRadialSpread01 ),
		nameof( TerrainPreviewSettings.MountainMidMapRadialFloor01 ),
	};
}
