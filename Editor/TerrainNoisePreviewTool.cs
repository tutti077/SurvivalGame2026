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

	Texture _liveTexture;
	bool _isGenerating;
	TerrainPreviewWaterCoverageStats _lastWaterCoverage;
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
		tabs.AddPage( "Mountain Mask", "terrain", CreateSettingsTab( TerrainPreviewControlTabs.MountainMask ) );
		tabs.AddPage( "Mountain Falloff", "donut_large", CreateSettingsTab( TerrainPreviewControlTabs.MountainFalloff ) );
		tabs.FinishSetup();

		controlsPanel.Layout.Add( tabs );

		_generateButton = new Button( "Generate Preview", "refresh" )
		{
			ToolTip = "Re-run preview for the selected preview layer and update PNG + live texture",
		};
		_generateButton.Clicked = () => _ = GeneratePreviewAsync();
		controlsPanel.Layout.Add( _generateButton );

		_statusLabel = new Label( "Press Generate Preview to sample the current settings." );
		_statusLabel.WordWrap = true;
		controlsPanel.Layout.Add( _statusLabel );

		Layout.Add( controlsPanel );

		var previewPanel = new Widget( this );
		previewPanel.Layout = Layout.Column();
		previewPanel.Layout.Spacing = 4;
		previewPanel.MinimumWidth = 520;

		previewPanel.Layout.Add( new Label( "Live Preview" ) { ToolTip = "Updates when you press Generate Preview" } );

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

	async Task GeneratePreviewAsync()
	{
		if ( _isGenerating )
			return;

		_isGenerating = true;
		_generateButton.Enabled = false;
		_statusLabel.Text = "Generating… 0";

		Bitmap bitmap = null;
		TerrainPreviewValleyAutoPipeline.RunResult valleyAuto = default;
		TerrainPreviewSettings settings = null;
		try
		{
			if ( TerrainPreviewValleyAutoEvaluate.AutoActive( _settings ) )
				TerrainPreviewValleyDefaults.ResetAutoBaselines( _settings );

			settings = CloneSettings( _settings );
			if ( _settings.RandomizeSeedOnGenerate )
				_settings.WorldSeed = settings.WorldSeed;

			var work = Task.Run( () =>
			{
				valleyAuto = TerrainPreviewValleyAutoPipeline.Run( settings );
				var result = TerrainPreviewGenerator.Generate( settings );
				bitmap = new Bitmap( settings.ClampedResolution, settings.ClampedResolution );
				bitmap.SetPixels( result.Colors );
				_lastWaterCoverage = result.WaterCoverage;
			} );

			while ( !work.IsCompleted )
			{
				var count = TerrainPreviewMapIterationTracker.Count;
				var maxIter = Math.Max( 1, settings.ValleyAutoMaxIterationsPerSeed );
				var seedAttempt = Math.Max( 1, TerrainPreviewMapIterationTracker.CurrentSeedAttempt );
				var maxSeeds = Math.Max( 1, settings.ValleyAutoMaxSeedAttempts );
				_statusLabel.Text = $"Generating… seed {seedAttempt}/{maxSeeds} · iter {count}/{maxIter} · #{settings.WorldSeed}";
				await Task.Delay( 33 );
			}

			await work;

			if ( settings.WorldSeed != _settings.WorldSeed )
				_settings.WorldSeed = settings.WorldSeed;

			_liveTexture = bitmap.ToTexture( false );
			_previewWidget.Texture = _liveTexture;

			var statsColumn = TerrainPreviewValleyAutoRunStats.Build( valleyAuto, settings, _lastWaterCoverage );
			_statsLabel.Text = TerrainPreviewValleyAutoRunStats.FormatColumnText( statsColumn );

			if ( valleyAuto.SeedRejected )
			{
				_statusLabel.Text = $"Seed rejected — PNG not saved (seed {settings.WorldSeed}).";
			}
			else
			{
				string pngPath = null;
				string bundleName = null;
				await Task.Run( () => pngPath = TerrainPreviewAssetExporter.ExportBitmap( bitmap, settings, out bundleName, _lastWaterCoverage ) );
				var resultNote = statsColumn.Result switch
				{
					"SOLVED" => "Solved",
					"LAND ONLY" => "Land-only fallback (interior tune failed)",
					_ => "Generated with unmet targets",
				};
				_statusLabel.Text = $"{resultNote} · {TerrainPreviewGenerator.ModeDisplayName( settings.PreviewMode )} · seed {settings.WorldSeed}\nSaved {TerrainPreviewGenerator.ModeFileStem( settings.PreviewMode )}.png → Assets/terrain/preview/{bundleName}/";
			}
		}
		catch ( Exception ex )
		{
			Log.Warning( $"Terrain preview generation failed: {ex}" );
			_statusLabel.Text = $"Preview failed: {ex.Message}";
		}
		finally
		{
			bitmap?.Dispose();
			_isGenerating = false;
			_generateButton.Enabled = true;
		}
	}

	static TerrainPreviewSettings CloneSettings( TerrainPreviewSettings source ) => new()
	{
		WorldDiameterMeters = source.WorldDiameterMeters,
		PreviewResolution = source.PreviewResolution,
		WorldSeed = source.RandomizeSeedOnGenerate
			? Random.Shared.Next( 1, int.MaxValue )
			: source.WorldSeed,
		RandomizeSeedOnGenerate = source.RandomizeSeedOnGenerate,
		RetrySeedsUntilSolved = source.RetrySeedsUntilSolved,
		ValleyAutoMaxSeedAttempts = source.ValleyAutoMaxSeedAttempts,
		EnableContinentalLayer = source.EnableContinentalLayer,
		EnableHillLayer = source.EnableHillLayer,
		EnableValleyLayer = source.EnableValleyLayer,
		EnableHeightCurveLayer = source.EnableHeightCurveLayer,
		EnableMountainLayer = source.EnableMountainLayer,
		EnableValleyOceanAutoWeight = source.EnableValleyOceanAutoWeight,
		EnableValleySpawnAutoFrequency = source.EnableValleySpawnAutoFrequency,
		EnableValleyAutoExhaustiveSearch = source.EnableValleyAutoExhaustiveSearch,
		RejectSeedOnAutoFailure = source.RejectSeedOnAutoFailure,
		ValleyAutoSearchTimeoutSeconds = source.ValleyAutoSearchTimeoutSeconds,
		ValleyAutoMaxIterationsPerSeed = source.ValleyAutoMaxIterationsPerSeed,
		ValleyAutoTunePreviewResolution = source.ValleyAutoTunePreviewResolution,
		ContinentalFrequency = source.ContinentalFrequency,
		ContinentalWeight = source.ContinentalWeight,
		HillFrequency = source.HillFrequency,
		HillWeight = source.HillWeight,
		ValleyFrequency = source.ValleyFrequency,
		ValleyWeight = source.ValleyWeight,
		ValleyOceanWeightStep = source.ValleyOceanWeightStep,
		ValleyOceanAutoMinInteriorFraction01 = source.ValleyOceanAutoMinInteriorFraction01,
		ValleyOceanAutoMaxTotalFraction01 = source.ValleyOceanAutoMaxTotalFraction01,
		ValleyOceanAbsoluteMaxTotalFraction01 = source.ValleyOceanAbsoluteMaxTotalFraction01,
		ValleyOceanMaxExteriorFraction01 = source.ValleyOceanMaxExteriorFraction01,
		ValleySpawnLandRadiusMeters = source.ValleySpawnLandRadiusMeters,
		ValleySpawnMinLandFraction01 = source.ValleySpawnMinLandFraction01,
		ValleySpawnAcceptableLandFraction01 = source.ValleySpawnAcceptableLandFraction01,
		ValleyAutoFrequencyStep = source.ValleyAutoFrequencyStep,
		ValleyAutoFrequencyMin = source.ValleyAutoFrequencyMin,
		ValleyAutoFrequencyMax = source.ValleyAutoFrequencyMax,
		ValleyNearWaterMaxDistanceMeters = source.ValleyNearWaterMaxDistanceMeters,
		ValleyInnerHalfRadius01 = source.ValleyInnerHalfRadius01,
		ValleyInnerHalfMinOceanFraction01 = source.ValleyInnerHalfMinOceanFraction01,
		MountainThreshold = source.MountainThreshold,
		MountainFrequency = source.MountainFrequency,
		MountainInnerRadius01 = source.MountainInnerRadius01,
		MountainOuterRadius01 = source.MountainOuterRadius01,
		MountainBandFade01 = source.MountainBandFade01,
		MountainFalloffRimPower = source.MountainFalloffRimPower,
		MountainPeakBoost = source.MountainPeakBoost,
		MountainMinPeakHeight01 = source.MountainMinPeakHeight01,
		MountainPeakVariationFrequency = source.MountainPeakVariationFrequency,
		MountainFoothillSpread = source.MountainFoothillSpread,
		MountainFoothillBoost = source.MountainFoothillBoost,
		PreviewMode = source.PreviewMode,
		HeightCurvePower = source.HeightCurvePower,
		EnableInteriorWaterLayer = source.EnableInteriorWaterLayer,
		InteriorWaterFrequency = source.InteriorWaterFrequency,
		InteriorWaterWeight = source.InteriorWaterWeight,
		InteriorWaterAutoStep = source.InteriorWaterAutoStep,
		InteriorWaterCenterInfluence01 = source.InteriorWaterCenterInfluence01,
		InteriorWaterFullInfluenceRadius01 = source.InteriorWaterFullInfluenceRadius01,
		InteriorWaterFalloffPower = source.InteriorWaterFalloffPower,
		InteriorWaterEdgeFade01 = source.InteriorWaterEdgeFade01,
		SeaLevelHeight01 = source.SeaLevelHeight01,
		TargetTotalOceanFraction01 = source.TargetTotalOceanFraction01,
		TargetInteriorOceanFraction01 = source.TargetInteriorOceanFraction01,
		InteriorZoneRadius01 = source.InteriorZoneRadius01,
	};
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
		nameof( TerrainPreviewSettings.RetrySeedsUntilSolved ),
		nameof( TerrainPreviewSettings.ValleyAutoMaxSeedAttempts ),
		nameof( TerrainPreviewSettings.PreviewMode ),
		nameof( TerrainPreviewSettings.EnableContinentalLayer ),
		nameof( TerrainPreviewSettings.EnableHillLayer ),
		nameof( TerrainPreviewSettings.EnableValleyLayer ),
		nameof( TerrainPreviewSettings.EnableHeightCurveLayer ),
		nameof( TerrainPreviewSettings.EnableMountainLayer ),
		nameof( TerrainPreviewSettings.EnableValleyOceanAutoWeight ),
		nameof( TerrainPreviewSettings.EnableValleySpawnAutoFrequency ),
		nameof( TerrainPreviewSettings.EnableValleyAutoExhaustiveSearch ),
		nameof( TerrainPreviewSettings.RejectSeedOnAutoFailure ),
		nameof( TerrainPreviewSettings.ValleyAutoSearchTimeoutSeconds ),
		nameof( TerrainPreviewSettings.ValleyAutoMaxIterationsPerSeed ),
		nameof( TerrainPreviewSettings.ValleyAutoTunePreviewResolution ),
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
		nameof( TerrainPreviewSettings.ValleyOceanWeightStep ),
		nameof( TerrainPreviewSettings.ValleyOceanAutoMinInteriorFraction01 ),
		nameof( TerrainPreviewSettings.ValleyOceanAutoMaxTotalFraction01 ),
		nameof( TerrainPreviewSettings.ValleyOceanAbsoluteMaxTotalFraction01 ),
		nameof( TerrainPreviewSettings.ValleyOceanMaxExteriorFraction01 ),
		nameof( TerrainPreviewSettings.ValleySpawnLandRadiusMeters ),
		nameof( TerrainPreviewSettings.ValleySpawnMinLandFraction01 ),
		nameof( TerrainPreviewSettings.ValleySpawnAcceptableLandFraction01 ),
		nameof( TerrainPreviewSettings.ValleyAutoFrequencyStep ),
		nameof( TerrainPreviewSettings.ValleyAutoFrequencyMin ),
		nameof( TerrainPreviewSettings.ValleyAutoFrequencyMax ),
		nameof( TerrainPreviewSettings.ValleyNearWaterMaxDistanceMeters ),
		nameof( TerrainPreviewSettings.ValleyInnerHalfRadius01 ),
		nameof( TerrainPreviewSettings.ValleyInnerHalfMinOceanFraction01 ),
	};

	public static readonly HashSet<string> HeightCurve = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.HeightCurvePower ),
	};

	public static readonly HashSet<string> Water = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.EnableInteriorWaterLayer ),
		nameof( TerrainPreviewSettings.InteriorWaterFrequency ),
		nameof( TerrainPreviewSettings.InteriorWaterWeight ),
		nameof( TerrainPreviewSettings.InteriorWaterAutoStep ),
		nameof( TerrainPreviewSettings.InteriorWaterCenterInfluence01 ),
		nameof( TerrainPreviewSettings.InteriorWaterFullInfluenceRadius01 ),
		nameof( TerrainPreviewSettings.InteriorWaterFalloffPower ),
		nameof( TerrainPreviewSettings.InteriorWaterEdgeFade01 ),
		nameof( TerrainPreviewSettings.SeaLevelHeight01 ),
		nameof( TerrainPreviewSettings.TargetTotalOceanFraction01 ),
		nameof( TerrainPreviewSettings.TargetInteriorOceanFraction01 ),
		nameof( TerrainPreviewSettings.InteriorZoneRadius01 ),
	};

	public static readonly HashSet<string> MountainMask = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.MountainThreshold ),
		nameof( TerrainPreviewSettings.MountainFrequency ),
		nameof( TerrainPreviewSettings.MountainPeakBoost ),
		nameof( TerrainPreviewSettings.MountainMinPeakHeight01 ),
		nameof( TerrainPreviewSettings.MountainPeakVariationFrequency ),
		nameof( TerrainPreviewSettings.MountainFoothillSpread ),
		nameof( TerrainPreviewSettings.MountainFoothillBoost ),
	};

	public static readonly HashSet<string> MountainFalloff = new( StringComparer.Ordinal )
	{
		nameof( TerrainPreviewSettings.MountainInnerRadius01 ),
		nameof( TerrainPreviewSettings.MountainOuterRadius01 ),
		nameof( TerrainPreviewSettings.MountainBandFade01 ),
		nameof( TerrainPreviewSettings.MountainFalloffRimPower ),
	};
}
