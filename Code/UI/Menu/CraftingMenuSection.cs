using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Left-side crafting panel: recipe preview, list, and craft action.</summary>
public sealed class CraftingMenuSection : IPlayerMenuSection
{
	public const float WidthScale = 1.5f;
	public const float LengthScale = 2f;
	public const float TextScale = 1.4f;
	public const float LayoutScale = 1.25f;

	public const float RowIconSize = 48f * WidthScale;
	public const float DetailIconSize = 80f * WidthScale;
	public const float MinSectionHeight = 420f * LengthScale;
	public const float RecipeListMaxHeight = 220f * LengthScale;

	public const float CraftingTitleFontSize = 24f * TextScale;
	public const float ItemNameFontSize = 20f * TextScale;
	public const float SectionHeaderFontSize = 17f * TextScale;
	public const float SectionEntryFontSize = 14f * TextScale;
	public const float CraftHoldSeconds = 1f;

	public string SectionId => "crafting";

	readonly PlayerInventory _inventory;
	readonly PlayerCrafting _crafting;
	readonly List<RowUi> _rows = new();

	Panel _sectionRoot;
	Panel _craftOutline;
	Panel _craftProgressFill;
	Panel _craftButton;
	Label _craftButtonLabel;
	Panel _detailIcon;
	Label _detailName;
	Panel _requirementsEntries;
	Panel _statsEntries;
	Panel _recipeList;

	string _selectedRecipeId;
	bool _menuOpen;
	bool _craftHoldActive;
	bool _craftHoldCompleted;
	float _craftHoldElapsed;
	bool _craftButtonPressedVisual;

	static readonly Color CraftButtonColor = new( 0.22f, 0.45f, 0.28f, 0.95f );
	static readonly Color CraftButtonPressedColor = new( 0.14f, 0.32f, 0.18f, 0.98f );

	public CraftingMenuSection( PlayerInventory inventory, PlayerCrafting crafting )
	{
		_inventory = inventory;
		_crafting = crafting;
	}

	public void Build( Panel menuColumn )
	{
		CraftingRecipeCatalog.EnsureLoaded();

		_sectionRoot = new Panel { Parent = menuColumn };
		_sectionRoot.Style.Set( "position", "relative" );
		_sectionRoot.Style.Set( "z-index", "1" );
		_sectionRoot.Style.Set( "pointer-events", "auto" );
		_sectionRoot.Style.Set( "flex-direction", "column" );
		_sectionRoot.Style.Set( "align-items", "stretch" );
		_sectionRoot.Style.Set( "gap", $"{10f * LayoutScale}px" );
		_sectionRoot.Style.Width = Length.Percent( 100 );
		_sectionRoot.Style.MinHeight = Length.Pixels( MinSectionHeight );

		var header = new Panel { Parent = _sectionRoot };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "gap", $"{8f * LayoutScale}px" );

		var title = new Label { Parent = header, Text = "Crafting" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( CraftingTitleFontSize );

		var craftWrap = new Panel { Parent = header };
		craftWrap.Style.Set( "position", "relative" );
		craftWrap.Style.Set( "flex-shrink", "0" );

		_craftOutline = new Panel { Parent = craftWrap };
		_craftOutline.Style.Set( "position", "absolute" );
		_craftOutline.Style.Set( "left", "-3px" );
		_craftOutline.Style.Set( "top", "-3px" );
		_craftOutline.Style.Set( "right", "-3px" );
		_craftOutline.Style.Set( "bottom", "-3px" );
		_craftOutline.Style.Set( "border-radius", "6px" );
		_craftOutline.Style.Set( "border-width", "2px" );
		_craftOutline.Style.Set( "border-color", "#9fd6a6" );
		_craftOutline.Style.Set( "pointer-events", "none" );
		_craftOutline.Style.Set( "opacity", "0" );

		_craftButton = new CraftButtonPanel( this ) { Parent = craftWrap };
		_craftButton.Style.Set( "position", "relative" );
		_craftButton.Style.Set( "overflow", "hidden" );

		_craftProgressFill = new Panel { Parent = _craftButton };
		_craftProgressFill.Style.Set( "position", "absolute" );
		_craftProgressFill.Style.Set( "left", "0" );
		_craftProgressFill.Style.Set( "top", "0" );
		_craftProgressFill.Style.Set( "bottom", "0" );
		_craftProgressFill.Style.Width = Length.Percent( 0 );
		_craftProgressFill.Style.Set( "z-index", "0" );
		_craftProgressFill.Style.Set( "pointer-events", "none" );
		_craftProgressFill.Style.BackgroundColor = new Color( 0.45f, 0.88f, 0.52f, 0.55f );
		_craftProgressFill.Style.Set( "display", "none" );
		_craftButton.Style.Set( "padding-left", $"{12f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-right", $"{12f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-top", $"{6f * LayoutScale}px" );
		_craftButton.Style.Set( "padding-bottom", $"{6f * LayoutScale}px" );
		_craftButton.Style.BackgroundColor = CraftButtonColor;
		_craftButton.Style.Set( "border-radius", "4px" );
		_craftButton.Style.Set( "border-width", "1px" );
		_craftButton.Style.Set( "border-color", "#5a8f62" );
		_craftButton.Style.Set( "pointer-events", "auto" );
		_craftButton.Style.Set( "z-index", "4" );
		_craftButton.Style.Set( "cursor", "pointer" );

		_craftButtonLabel = new Label { Parent = _craftButton, Text = "Craft" };
		_craftButtonLabel.Style.FontColor = Color.White;
		_craftButtonLabel.Style.FontSize = Length.Pixels( 15f * TextScale );
		_craftButtonLabel.Style.Set( "pointer-events", "none" );
		_craftButtonLabel.Style.Set( "position", "relative" );
		_craftButtonLabel.Style.Set( "z-index", "2" );

		var detail = new Panel { Parent = _sectionRoot };
		detail.Style.Set( "flex-direction", "column" );
		detail.Style.Set( "gap", $"{10f * LayoutScale}px" );
		detail.Style.Set( "width", "100%" );
		detail.Style.PaddingTop = Length.Pixels( 4f * LayoutScale );
		detail.Style.PaddingBottom = Length.Pixels( 8f * LayoutScale );
		detail.Style.Set( "border-bottom-width", "1px" );
		detail.Style.Set( "border-bottom-color", "#383d47" );

		var detailRow = new Panel { Parent = detail };
		detailRow.Style.Set( "flex-direction", "row" );
		detailRow.Style.Set( "gap", $"{10f * LayoutScale}px" );
		detailRow.Style.Set( "align-items", "flex-start" );
		detailRow.Style.Set( "width", "100%" );

		_detailIcon = new Panel { Parent = detailRow };
		_detailIcon.Style.Width = Length.Pixels( DetailIconSize );
		_detailIcon.Style.Height = Length.Pixels( DetailIconSize );
		_detailIcon.Style.Set( "flex-shrink", "0" );
		_detailIcon.Style.BackgroundColor = new Color( 0.12f, 0.13f, 0.15f, 0.92f );
		_detailIcon.Style.Set( "border-width", "1px" );
		_detailIcon.Style.Set( "border-color", "#474d57" );
		_detailIcon.Style.Set( "border-radius", "4px" );
		_detailIcon.Style.Set( "background-size", "contain" );
		_detailIcon.Style.Set( "background-repeat", "no-repeat" );
		_detailIcon.Style.Set( "background-position", "center" );

		_detailName = new Label { Parent = detailRow, Text = "Select a recipe" };
		_detailName.Style.Set( "flex-grow", "1" );
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( ItemNameFontSize );
		_detailName.Style.Set( "white-space", "normal" );

		var requirementsBlock = CreateDetailListBlock( detail, "Requirements", out _requirementsEntries );
		_ = requirementsBlock;

		var statsBlock = CreateDetailListBlock( detail, "Stats", out _statsEntries );
		_ = statsBlock;

		_recipeList = new Panel { Parent = _sectionRoot };
		_recipeList.Style.Set( "flex-direction", "column" );
		_recipeList.Style.Set( "gap", $"{4f * LayoutScale}px" );
		_recipeList.Style.Set( "width", "100%" );
		_recipeList.Style.Set( "overflow-y", "scroll" );
		_recipeList.Style.Set( "max-height", $"{RecipeListMaxHeight}px" );

		_rows.Clear();
		foreach ( var recipe in CraftingRecipeCatalog.All )
		{
			if ( recipe is null || string.IsNullOrWhiteSpace( recipe.Id ) )
				continue;

			if ( !recipe.IsUnlockedByDefault )
				continue;

			var row = new CraftingRecipeRowPanel
			{
				Parent = _recipeList,
				Section = this,
				RecipeId = recipe.Id
			};
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "align-items", "center" );
			row.Style.Set( "gap", $"{8f * LayoutScale}px" );
			row.Style.Set( "width", "100%" );
			row.Style.PaddingTop = Length.Pixels( 4f * LayoutScale );
			row.Style.PaddingBottom = Length.Pixels( 4f * LayoutScale );
			row.Style.PaddingLeft = Length.Pixels( 6f * LayoutScale );
			row.Style.PaddingRight = Length.Pixels( 6f * LayoutScale );
			row.Style.BackgroundColor = new Color( 0.10f, 0.11f, 0.13f, 0.9f );
			row.Style.Set( "border-width", "1px" );
			row.Style.Set( "border-color", "#383d47" );
			row.Style.Set( "border-radius", "4px" );
			row.Style.Set( "pointer-events", "auto" );

			var icon = new Panel { Parent = row };
			icon.Style.Width = Length.Pixels( RowIconSize );
			icon.Style.Height = Length.Pixels( RowIconSize );
			icon.Style.Set( "flex-shrink", "0" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			MenuUiTextures.ApplyBackground( icon, CraftingRecipeCatalog.ResolveIconPath( recipe ) );

			var name = new Label { Parent = row, Text = recipe.DisplayName };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( 14f * TextScale );
			name.Style.Set( "pointer-events", "none" );

			_rows.Add( new RowUi( row, icon, recipe.Id ) );
		}

		if ( CraftingRecipeCatalog.All.Count > 0 )
			SelectRecipe( CraftingRecipeCatalog.All[0].Id );

		Refresh();
	}

	public void SelectRecipe( string recipeId )
	{
		_selectedRecipeId = recipeId;
		Refresh();
	}

	public void Refresh()
	{
		CraftingRecipeCatalog.EnsureLoaded();

		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is null )
		{
			_detailName.Text = "Select a recipe";
			PopulateEntryList( _requirementsEntries, Array.Empty<string>() );
			PopulateEntryList( _statsEntries, Array.Empty<string>() );
			MenuUiTextures.ApplyBackground( _detailIcon, null );
			UpdateCraftButton( false );
			UpdateRowHighlights();
			return;
		}

		_detailName.Text = recipe.DisplayName;
		PopulateEntryList( _requirementsEntries, BuildRequirementLines( recipe ) );
		PopulateEntryList( _statsEntries, BuildStatLines( recipe ) );
		MenuUiTextures.ApplyBackground( _detailIcon, CraftingRecipeCatalog.ResolveIconPath( recipe ) );

		var canCraft = CanCraftRecipe( recipe );
		UpdateCraftButton( canCraft );
		UpdateRowHighlights();
		RefreshRowIcons();
	}

	void RefreshRowIcons()
	{
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i];
			var recipe = CraftingRecipeCatalog.Get( row.RecipeId );
			MenuUiTextures.ApplyBackground( row.IconPanel, CraftingRecipeCatalog.ResolveIconPath( recipe ) );
		}
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			CraftingRecipeCatalog.ForceReload();
			Refresh();
		}
		else
		{
			CancelCraftHold();
			SetButtonPressedVisual( false );
		}

		UpdateVisibility();
	}

	public void SetPanelVisible( bool visible )
	{
		UpdateVisibility();
	}

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen ? "flex" : "none" );
	}

	public void TryCraftSelected()
	{
		if ( string.IsNullOrWhiteSpace( _selectedRecipeId ) || _inventory is null )
			return;

		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is null || !CanCraftRecipe( recipe ) )
			return;

		var applied = _inventory.OwnerTryCraftRecipe( _selectedRecipeId );
		if ( _crafting is not null && _crafting.LogCrafting )
			Log.Info( applied
				? $"[CraftingMenu] Craft request sent/applied for '{_selectedRecipeId}'."
				: $"[CraftingMenu] Craft request failed for '{_selectedRecipeId}' (host rejected or not authoritative)." );

		Refresh();
	}

	public void BeginCraftHold()
	{
		if ( !CanCraftSelectedRecipe() )
			return;

		_craftHoldActive = true;
		_craftHoldCompleted = false;
		_craftHoldElapsed = 0f;
		SetCraftHoldVisual( 0f );
	}

	public void CancelCraftHold()
	{
		_craftHoldActive = false;
		_craftHoldCompleted = false;
		SetCraftHoldVisual( 0f );
	}

	/// <summary>Called from <see cref="CraftButtonPanel.Tick"/> each frame while LMB hold is active.</summary>
	public void AdvanceCraftHoldWhileHeld()
	{
		if ( !_craftHoldActive )
			return;

		AdvanceCraftHold();
	}

	void AdvanceCraftHold()
	{
		if ( !_craftHoldActive || _craftHoldCompleted )
			return;

		if ( !CanCraftSelectedRecipe() )
		{
			CancelCraftHold();
			return;
		}

		_craftHoldElapsed += Time.Delta;
		var duration = Math.Max( 0.05f, CraftHoldSeconds );
		var t = Math.Clamp( _craftHoldElapsed / duration, 0f, 1f );
		SetCraftHoldVisual( t );

		if ( t < 1f )
			return;

		_craftHoldCompleted = true;
		TryCraftSelected();
		CancelCraftHold();
	}

	public void EndCraftHoldFromButtonRelease()
	{
		if ( !_craftHoldCompleted )
			CancelCraftHold();

		SetButtonPressedVisual( false );
	}

	public void TickMenu( bool menuOpen )
	{
	}

	public void OnMenuGlobalMouseUp()
	{
	}

	void SetCraftHoldVisual( float progress )
	{
		if ( _craftProgressFill is not null && _craftProgressFill.IsValid() )
		{
			if ( progress <= 0f )
			{
				_craftProgressFill.Style.Width = Length.Percent( 0 );
				_craftProgressFill.Style.Set( "display", "none" );
			}
			else
			{
				_craftProgressFill.Style.Set( "display", "flex" );
				_craftProgressFill.Style.Width = Length.Percent( progress * 100f );
			}
		}

		if ( _craftOutline is null || !_craftOutline.IsValid() )
			return;

		if ( progress <= 0f )
		{
			_craftOutline.Style.Set( "opacity", "0" );
			return;
		}

		_craftOutline.Style.Set( "opacity", progress >= 1f ? "1" : "0.85" );
		_craftOutline.Style.Set( "border-width", "2px" );
		_craftOutline.Style.Set( "border-color", "#9fd6a6" );
	}

	public bool CanCraftSelectedRecipe()
	{
		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		return recipe is not null && CanCraftRecipe( recipe );
	}

	bool CanCraftRecipe( CraftingRecipe recipe )
	{
		if ( recipe is null || _inventory is null )
			return false;

		if ( !HasScaledResources( recipe ) )
			return false;

		return _inventory.CanFitResource( recipe.OutputResourceId, recipe.TotalOutputAmount );
	}

	bool HasScaledResources( CraftingRecipe recipe )
	{
		if ( _inventory is null || recipe.Ingredients is null )
			return false;

		var batch = recipe.CraftBatchCount;
		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			if ( _inventory.CountResource( ing.ResourceId ) < ing.Amount * batch )
				return false;
		}

		return true;
	}

	void UpdateCraftButton( bool canCraft )
	{
		if ( _craftButton is null || _craftButtonLabel is null )
			return;

		_craftButton.Style.Set( "opacity", canCraft ? "1" : "0.45" );
		_craftButtonLabel.Text = canCraft ? "Hold to craft (LMB)" : "Need materials / space";
		if ( !_craftButtonPressedVisual )
			_craftButton.Style.BackgroundColor = CraftButtonColor;
	}

	void SetButtonPressedVisual( bool pressed )
	{
		_craftButtonPressedVisual = pressed;
		if ( _craftButton is null || !_craftButton.IsValid() )
			return;

		_craftButton.Style.BackgroundColor = pressed ? CraftButtonPressedColor : CraftButtonColor;
		_craftButton.Style.Set( "transform", pressed ? "scale(0.96)" : "scale(1)" );
	}

	static bool IsMouseOverPanel( Panel panel )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		if ( panel.IsInside( Mouse.Position ) )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		var m = Mouse.Position;
		return m.x >= rect.Left && m.x <= rect.Right && m.y >= rect.Top && m.y <= rect.Bottom;
	}

	void UpdateRowHighlights()
	{
		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i].Root;
			if ( row is null || !row.IsValid() )
				continue;

			var selected = string.Equals( _rows[i].RecipeId, _selectedRecipeId, StringComparison.OrdinalIgnoreCase );
			row.Style.Set( "border-color", selected ? "#8ab4f8" : "#383d47" );
		}
	}

	static Panel CreateDetailListBlock( Panel parent, string headingText, out Panel entriesHost )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "gap", $"{4f * LayoutScale}px" );
		block.Style.Set( "width", "100%" );

		var heading = new Label { Parent = block, Text = headingText };
		heading.Style.FontColor = Color.White;
		heading.Style.FontSize = Length.Pixels( SectionHeaderFontSize );

		entriesHost = new Panel { Parent = block };
		entriesHost.Style.Set( "flex-direction", "column" );
		entriesHost.Style.Set( "gap", $"{3f * LayoutScale}px" );
		entriesHost.Style.Set( "width", "100%" );
		entriesHost.Style.PaddingLeft = Length.Pixels( 6f * LayoutScale );

		return block;
	}

	static void PopulateEntryList( Panel host, IReadOnlyList<string> lines )
	{
		if ( host is null )
			return;

		host.DeleteChildren();

		if ( lines is null || lines.Count == 0 )
		{
			AddEntryLabel( host, "—" );
			return;
		}

		for ( var i = 0; i < lines.Count; i++ )
			AddEntryLabel( host, lines[i] );
	}

	static void AddEntryLabel( Panel host, string text )
	{
		var label = new Label { Parent = host, Text = text };
		label.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		label.Style.FontSize = Length.Pixels( SectionEntryFontSize );
		label.Style.Set( "white-space", "normal" );
	}

	List<string> BuildRequirementLines( CraftingRecipe recipe )
	{
		var lines = new List<string>();
		if ( recipe.Ingredients is null || recipe.Ingredients.Count == 0 )
			return lines;

		var batch = recipe.CraftBatchCount;
		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			var def = ResourceCatalog.Resolve( ing.ResourceId );
			var have = _inventory?.CountResource( ing.ResourceId ) ?? 0;
			var need = ing.Amount * batch;
			lines.Add( $"{have}/{need} {def.DisplayName}" );
		}

		return lines;
	}

	static List<string> BuildStatLines( CraftingRecipe recipe )
	{
		var lines = new List<string>();
		if ( recipe.Stats is null || recipe.Stats.Count == 0 )
			return lines;

		for ( var i = 0; i < recipe.Stats.Count; i++ )
		{
			var line = recipe.Stats[i];
			if ( line is null )
				continue;

			lines.Add( $"{line.Label}: {line.Value}" );
		}

		return lines;
	}

	sealed class CraftButtonPanel : Panel
	{
		readonly CraftingMenuSection _section;

		public CraftButtonPanel( CraftingMenuSection section ) => _section = section;

		public override bool WantsMouseInput() => true;

		public override void Tick()
		{
			base.Tick();
			_section.AdvanceCraftHoldWhileHeld();
		}

		protected override void OnMouseDown( MousePanelEvent e )
		{
			base.OnMouseDown( e );
			if ( e.Button != "mouseleft" )
				return;

			_section.SetButtonPressedVisual( true );
			_section.BeginCraftHold();
		}

		protected override void OnMouseUp( MousePanelEvent e )
		{
			base.OnMouseUp( e );
			if ( e.Button != "mouseleft" )
				return;

			_section.EndCraftHoldFromButtonRelease();
		}
	}

	sealed class RowUi
	{
		public Panel Root { get; }
		public Panel IconPanel { get; }
		public string RecipeId { get; }

		public RowUi( Panel root, Panel iconPanel, string recipeId )
		{
			Root = root;
			IconPanel = iconPanel;
			RecipeId = recipeId;
		}
	}
}
