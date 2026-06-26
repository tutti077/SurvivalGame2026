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
	float _lastHeadingDegrees;
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

		var map = manager.BiomePreviewMap;
		var streamMoved = manager.HasStreamPosition && manager.StreamWorldPosition != _lastStreamPosition;
		var headingChanged = MathF.Abs( manager.StreamHeadingDegrees - _lastHeadingDegrees ) > 0.25f;
		var chunksChanged = manager.LoadedChunkCount != _lastLoadedChunkCount;
		var mapChanged = map != _lastMapTexture;

		if ( mapChanged )
			InvalidateMapPixmap( map );

		if ( !mapChanged && !streamMoved && !headingChanged && !chunksChanged && manager.HasStreamPosition == _lastHasStream )
			return;

		_lastMapTexture = map;
		_lastStreamPosition = manager.StreamWorldPosition;
		_lastHeadingDegrees = manager.StreamHeadingDegrees;
		_lastLoadedChunkCount = manager.LoadedChunkCount;
		_lastHasStream = manager.HasStreamPosition;
		Update();
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		Paint.Antialiasing = true;
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( LocalRect, Theme.ControlRadius );

		var manager = ResolveManager?.Invoke();
		if ( manager is null || !manager.IsValid )
			return;

		var map = manager.BiomePreviewMap;
		if ( !map.IsValid() )
			return;

		EnsureMapPixmap( map );
		if ( _mapPixmap is null )
			return;

		var mapRect = TerrainBiomeMapCoordinates.GetAspectContainRect(
			LocalRect.Shrink( 2f ),
			_mapPixmap.Width,
			_mapPixmap.Height );
		Paint.Draw( mapRect, _mapPixmap );

		if ( !Sandbox.Game.IsPlaying || !manager.HasStreamPosition )
			return;

		var settings = manager.BuildGenerationSettings();
		var normalized = TerrainBiomeMapCoordinates.WorldMetersToNormalized(
			manager.StreamWorldPosition.x,
			manager.StreamWorldPosition.y,
			settings );
		var marker = TerrainBiomeMapCoordinates.NormalizedToLocalPoint( mapRect, normalized );

		DrawStreamMarker( marker, manager.StreamHeadingDegrees );
	}

	static void DrawStreamMarker( Vector2 center, float headingDegrees )
	{
		Paint.SetPen( Color.White.WithAlpha( 0.95f ) );
		Paint.DrawLine( center - new Vector2( MarkerArmPixels, 0f ), center + new Vector2( MarkerArmPixels, 0f ) );
		Paint.DrawLine( center - new Vector2( 0f, MarkerArmPixels ), center + new Vector2( 0f, MarkerArmPixels ) );

		var radians = headingDegrees * (MathF.PI / 180f);
		var headingDir = new Vector2( MathF.Cos( radians ), MathF.Sin( radians ) );
		var headingEnd = center + (headingDir * HeadingLinePixels);
		Paint.SetPen( Color.Cyan.WithAlpha( 0.95f ) );
		Paint.DrawLine( center, headingEnd );

		Paint.SetPen( Color.Black.WithAlpha( 0.85f ) );
		var dotRect = new Rect( center - new Vector2( 3f, 3f ), center + new Vector2( 3f, 3f ) );
		Paint.DrawRect( dotRect, 3f );
		Paint.SetPen( Color.White );
		Paint.DrawRect( dotRect.Shrink( 1f ), 2f );
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
