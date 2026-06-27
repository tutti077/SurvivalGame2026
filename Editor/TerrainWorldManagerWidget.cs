using System.IO;

namespace Editor;

[CustomEditor( typeof( Survival.TerrainWorldManager ) )]
public sealed class TerrainWorldManagerWidget : ComponentEditorWidget
{
	static readonly HashSet<string> VisibleInspectorProperties = new( StringComparer.Ordinal )
	{
		nameof( Survival.TerrainWorldManager.WorldName ),
		nameof( Survival.TerrainWorldManager.WorldDiameterMeters ),
		nameof( Survival.TerrainWorldManager.WorldSeed ),
		nameof( Survival.TerrainWorldManager.MaxTerrainHeightMeters ),
		nameof( Survival.TerrainWorldManager.ChunkSizeMeters ),
		nameof( Survival.TerrainWorldManager.ChunkVerticesPerSide ),
		nameof( Survival.TerrainWorldManager.StreamRadiusChunks ),
		nameof( Survival.TerrainWorldManager.UseForwardConeStreaming ),
		nameof( Survival.TerrainWorldManager.ForwardViewRadiusChunks ),
		nameof( Survival.TerrainWorldManager.ViewDistanceMeters ),
		nameof( Survival.TerrainWorldManager.ForwardViewConeDegrees ),
		nameof( Survival.TerrainWorldManager.SideViewRadiusChunks ),
		nameof( Survival.TerrainWorldManager.CollisionRangeMeters ),
		nameof( Survival.TerrainWorldManager.ChunksPerFrame ),
		nameof( Survival.TerrainWorldManager.MeshBorderPrefetch01 ),
		nameof( Survival.TerrainWorldManager.MeshedChunkCount ),
		nameof( Survival.TerrainWorldManager.BiomePreviewMetersPerPixel ),
		nameof( Survival.TerrainWorldManager.BiomePreviewMapMaxResolution ),
		nameof( Survival.TerrainWorldManager.PreviewMapRowsPerFrame ),
		nameof( Survival.TerrainWorldManager.RegeneratePreviewOnStart ),
		nameof( Survival.TerrainWorldManager.ShowWorldLoadScreen ),
		nameof( Survival.TerrainWorldManager.IsWorldLoading ),
		nameof( Survival.TerrainWorldManager.LoadedChunkCount ),
		nameof( Survival.TerrainWorldManager.IsMapGenerating ),
		nameof( Survival.TerrainWorldManager.MapGenerationProgress01 ),
		nameof( Survival.TerrainWorldManager.MapGenerationStatus ),
		nameof( Survival.TerrainWorldManager.EffectiveBiomePreviewResolution ),
		nameof( Survival.TerrainWorldManager.EffectiveMetersPerPixel ),
		nameof( Survival.TerrainWorldManager.BiomePreviewMapSeed ),
		nameof( Survival.TerrainWorldManager.IsBiomePreviewMapStale ),
		nameof( Survival.TerrainWorldManager.HasStreamPosition ),
		nameof( Survival.TerrainWorldManager.StreamWorldPosition ),
		nameof( Survival.TerrainWorldManager.StreamXMeters ),
		nameof( Survival.TerrainWorldManager.StreamYMeters ),
		nameof( Survival.TerrainWorldManager.StreamElevationMeters ),
		nameof( Survival.TerrainWorldManager.StreamChunkX ),
		nameof( Survival.TerrainWorldManager.StreamChunkY ),
		nameof( Survival.TerrainWorldManager.StreamHeadingDegrees ),
	};

	TerrainBiomeMapLivePreviewWidget _previewWidget;
	Label _previewStatus;
	Texture _lastPreviewTexture;
	int _lastDisplayedSeed = int.MinValue;
	Vector3 _lastStreamPositionForStatus;

	public TerrainWorldManagerWidget( SerializedObject obj ) : base( obj )
	{
		Layout = Layout.Column();
		Layout.Spacing = 6;
		BuildUi();
	}

	void BuildUi()
	{
		Layout.Add( new Label( "Biome Preview Map" ) );

		_previewWidget = new TerrainBiomeMapLivePreviewWidget( this )
		{
			ResolveManager = GetManager,
		};
		Layout.Add( _previewWidget );

		_previewStatus = new Label( "No biome map yet — press Regenerate Biome Map." ) { WordWrap = true };
		Layout.Add( _previewStatus );

		var regen = new Button( "Regenerate Biome Map", "refresh" );
		regen.Clicked = () => RegeneratePreview( savePng: true );
		Layout.Add( regen );

		Layout.AddSpacingCell( 8 );

		var sheet = new ControlSheet();
		sheet.AddObject( SerializedObject, prop => VisibleInspectorProperties.Contains( prop.Name ) );
		Layout.Add( sheet );

		RefreshPreview();
	}

	Survival.TerrainWorldManager GetManager()
		=> SerializedObject.Targets.FirstOrDefault() as Survival.TerrainWorldManager;

	void RegeneratePreview( bool savePng )
	{
		var manager = GetManager();
		if ( manager is null || !manager.IsValid )
			return;

		manager.RegenerateBiomePreviewMap();
		RefreshPreview();

		if ( !savePng )
			return;

		try
		{
			var settings = manager.BuildGenerationSettings();
			var job = Survival.TerrainWorldPreviewJob.Create(
				settings,
				TerrainPreviewBackendRegistry.Active,
				manager.ComputeBiomePreviewResolution() );
			while ( !job.IsComplete )
				job.Step( int.MaxValue );

			Survival.WorldSaveIO.WriteBiomeMapPng( manager.WorldName, job.FinishBitmap() );
			_previewStatus.Text =
				$"Biome map ready — {manager.EffectiveBiomePreviewResolution}px, {manager.EffectiveMetersPerPixel:0.##} m/px (saved WorldSaves/{manager.WorldName}/biome_map.png)";
		}
		catch ( Exception e )
		{
			_previewStatus.Text = $"Biome map ready — save failed: {e.Message}";
		}
	}

	void RefreshPreview()
	{
		var manager = GetManager();
		if ( manager is null || !manager.IsValid )
			return;

		_previewWidget.ResolveManager = GetManager;
		_lastPreviewTexture = manager.BiomePreviewMap;
		_lastDisplayedSeed = manager.WorldSeed;
		_lastStreamPositionForStatus = manager.StreamWorldPosition;

		var streamNote = Sandbox.Game.IsPlaying && manager.HasStreamPosition
			? $" · {manager.FormatStreamPositionMetersFromCenter()} · chunk ({manager.StreamChunkX}, {manager.StreamChunkY}) · {manager.MeshedChunkCount} meshed / {manager.LoadedChunkCount} stream"
			: "";

		if ( manager.BiomePreviewMap.IsValid() )
		{
			var mapSeed = manager.BiomePreviewMapSeed != int.MinValue ? manager.BiomePreviewMapSeed : manager.WorldSeed;
			var isStale = manager.BiomePreviewMapSeed != int.MinValue && manager.BiomePreviewMapSeed != manager.WorldSeed;
			var staleNote = isStale
				? $" — map is seed {mapSeed}; press Regenerate for seed {manager.WorldSeed}"
				: "";
			_previewStatus.Text =
				$"Biome map ready — {manager.EffectiveBiomePreviewResolution}px, {manager.EffectiveMetersPerPixel:0.##} m/px, seed {mapSeed}{staleNote}{streamNote}";
		}
		else if ( manager.IsMapGenerating )
		{
			_previewStatus.Text = $"{manager.MapGenerationStatus} ({manager.MapGenerationProgress01 * 100f:0}%){streamNote}";
		}
		else
		{
			_previewStatus.Text =
				$"No biome map for seed {manager.WorldSeed} — press Regenerate Biome Map or play with Regenerate Preview On Start.";
		}
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var manager = GetManager();
		if ( manager is null || !manager.IsValid )
			return;

		if ( manager.IsMapGenerating
			|| manager.BiomePreviewMap != _lastPreviewTexture
			|| manager.WorldSeed != _lastDisplayedSeed
			|| manager.IsBiomePreviewMapStale
			|| (Sandbox.Game.IsPlaying && manager.HasStreamPosition && manager.StreamWorldPosition != _lastStreamPositionForStatus) )
			RefreshPreview();
	}

	static string FindProjectRoot()
	{
		if ( Project.Current?.RootDirectory is { Exists: true } root )
			return root.FullName;

		var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir.FullName, "survivalgamebasics.sbproj" ) ) )
				return dir.FullName;

			dir = dir.Parent;
		}

		return Directory.GetCurrentDirectory();
	}
}
