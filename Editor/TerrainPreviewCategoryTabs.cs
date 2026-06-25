namespace Editor;

/// <summary>
/// Full-width category tabs for the terrain preview tool (avoids SegmentedControl label clipping).
/// </summary>
sealed class TerrainPreviewCategoryTabs : Widget
{
	readonly Dictionary<string, Widget> _pages = new();
	readonly Dictionary<string, Button> _buttons = new();
	readonly Widget _tabButtons;
	string _selectedName;

	public TerrainPreviewCategoryTabs( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Spacing = 6;

		_tabButtons = new Widget( this );
		_tabButtons.Layout = Layout.Column();
		_tabButtons.Layout.Spacing = 2;
		Layout.Add( _tabButtons );
	}

	public string StateCookie { get; set; }

	public void AddPage( string name, string icon, Widget page )
	{
		page ??= new Widget( null );
		page.Visible = false;
		_pages[name] = page;

		var button = new Button( name, icon )
		{
			IsToggle = true,
			FixedHeight = Theme.RowHeight,
			ToolTip = name,
		};
		button.Clicked = () => SelectPage( name );
		_buttons[name] = button;

		_tabButtons.Layout.Add( button );
		Layout.Add( page );

		if ( _pages.Count == 1 )
			SelectPage( name );
	}

	public void FinishSetup() => Restore();

	public string SelectedPageName => _selectedName;

	void SelectPage( string name )
	{
		if ( !_pages.TryGetValue( name, out var page ) )
			return;

		foreach ( var entry in _pages )
			entry.Value.Visible = entry.Key == name;

		foreach ( var entry in _buttons )
			entry.Value.IsChecked = entry.Key == name;

		_selectedName = name;
		Update();
		Save();
	}

	void Save()
	{
		if ( string.IsNullOrEmpty( StateCookie ) || string.IsNullOrEmpty( _selectedName ) )
			return;

		EditorCookie.Set( $"terrain-preview-tab.{StateCookie}", _selectedName );
	}

	void Restore()
	{
		if ( string.IsNullOrEmpty( StateCookie ) )
			return;

		var name = EditorCookie.Get<string>( $"terrain-preview-tab.{StateCookie}", null );
		if ( string.IsNullOrWhiteSpace( name ) || !_pages.ContainsKey( name ) )
			return;

		SelectPage( name );
	}
}
