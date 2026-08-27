namespace Editor;

/// <summary>Biome map texture with a live stream-camera marker drawn on top.</summary>
sealed class TerrainBiomeMapLivePreviewWidget : Widget
{
	const float PreviewSize = 320f;
	const float MarkerArmPixels = 7f;
	const float HeadingLinePixels = 22f;

	public Func<TerrainWorldManager> ResolveManager { get; set; }

	Texture _lastMapTexture;
	Pixmap _mapPixmap;
	Texture _cachedMapPixmapSource;
	Vector3 _lastStreamPosition;
	Vector2 _lastStreamLookDirection;
	int _lastLoadedChunkCount;
	bool _lastHasStream;

	public TerrainBiomeMapLivePreviewWidget( Widget parent ) : base( parent )
	{
		FixedSize = PreviewSize;
		MinimumSize = new Vector2( PreviewSize, PreviewSize );
	}

	[EditorEvent.Frame]
	void OnEditorFrame()
	{
		var manager = ResolveManager?.Invoke();
		if ( manager is null || !manager.IsValid )
			return;

		var map = manager.GetHudBiomeMapTexture();
		var streamMoved = manager.HasStreamPosition && manager.StreamWorldPosition != _lastStreamPosition;
		var headingChanged = (manager.StreamLookDirectionMap - _lastStreamLookDirection).LengthSquared > 0.0001f;
		var chunksChanged = manager.LoadedChunkCount != _lastLoadedChunkCount;
		var mapChanged = map != _lastMapTexture;

		if ( mapChanged )
			InvalidateMapPixmap( map );

		if ( !mapChanged && !streamMoved && !headingChanged && !chunksChanged && manager.HasStreamPosition == _lastHasStream )
			return;

		_lastMapTexture = map;
		_lastStreamPosition = manager.StreamWorldPosition;
		_lastStreamLookDirection = manager.StreamLookDirectionMap;
		_lastLoadedChunkCount = manager.LoadedChunkCount;
		_lastHasStream = manager.HasStreamPosition;
		Update();
	}

	protected override void OnPaint()
	{
		var manager = ResolveManager?.Invoke();
		if ( manager is null || !manager.IsValid )
			return;

		var map = manager.GetHudBiomeMapTexture();
		if ( !map.IsValid() )
			return;

		EnsureMapPixmap( map );
		if ( _mapPixmap is null )
			return;

		Paint.Antialiasing = true;
		Paint.ClearPen();

		var mapRect = TerrainBiomeMapCoordinates.GetAspectContainRect(
			LocalRect,
			_mapPixmap.Width,
			_mapPixmap.Height );
		Paint.Draw( mapRect, _mapPixmap );

		if ( !Sandbox.Game.IsPlaying || !manager.HasStreamPosition )
			return;

		var settings = manager.BuildGenerationSettings();
		var normalized = TerrainBiomeMapCoordinates.WorldMetersToPreviewNormalized(
			manager.StreamXMeters,
			manager.StreamYMeters,
			settings );
		var marker = TerrainBiomeMapCoordinates.NormalizedToLocalPoint( mapRect, normalized );

		DrawStreamMarker( marker, manager.StreamLookDirectionMap );
	}

	static void DrawStreamMarker( Vector2 center, Vector2 mapDirection )
	{
		if ( mapDirection.LengthSquared < 1e-8f )
			return;

		Paint.ClearPen();
		Paint.SetPen( Color.White.WithAlpha( 0.95f ) );
		Paint.DrawLine( center - new Vector2( MarkerArmPixels, 0f ), center + new Vector2( MarkerArmPixels, 0f ) );
		Paint.DrawLine( center - new Vector2( 0f, MarkerArmPixels ), center + new Vector2( 0f, MarkerArmPixels ) );

		var headingEnd = center + (mapDirection.Normal * HeadingLinePixels);
		Paint.SetPen( Color.Cyan.WithAlpha( 0.95f ) );
		Paint.DrawLine( center, headingEnd );
	}

	void InvalidateMapPixmap( Texture map )
	{
		_cachedMapPixmapSource = map;
		_mapPixmap = map.IsValid() ? Pixmap.FromTexture( map ) : null;
	}

	void EnsureMapPixmap( Texture map )
	{
		if ( map == _cachedMapPixmapSource )
			return;

		InvalidateMapPixmap( map );
	}
}
