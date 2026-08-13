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
	/// <summary>Recipe list row size vs the original oversized rows.</summary>
	public const float RecipeListItemScale = 0.5f;

	public const float RowIconSize = 48f * WidthScale * RecipeListItemScale;
	public const float DetailIconSize = 80f * WidthScale;
	public const float MinSectionHeight = 420f * LengthScale;
	public const float RecipeListMaxHeight = 220f * LengthScale;
	public const float DetailAreaMaxHeight = MinSectionHeight - RecipeListMaxHeight - 80f * LayoutScale;
	public const float RecipeRowGap = 3f;
	/// <summary>
	/// Exact Style.Height for each recipe row (border-box). Scroll range uses this — keep in sync.
	/// </summary>
	public const float RecipeRowHeight = 44f;
	public const float RecipeRowBorder = 1f;
	/// <summary>Readable list names (not scaled as hard as the 50% row shrink).</summary>
	public const float RecipeListRowFontSize = 14f * TextScale * 0.85f;
	/// <summary>One physical mouse-wheel notch reveals this many recipe rows.</summary>
	public const float WheelItemsPerNotch = 4.5f;

	public const float CraftingTitleFontSize = 24f * TextScale;
	public const float ItemNameFontSize = 20f * TextScale;
	public const float ItemDescriptionFontSize = 12f * TextScale;
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
	Label _detailDescription;
	Panel _requirementsEntries;
	Panel _statsEntries;
	string _detailIconPathApplied;
	Panel _recipeList;
	CraftingRecipeListPanel _recipeListPanel;

	string _selectedRecipeId;
	bool _menuOpen;
	bool _panelVisible = true;
	bool _craftHoldActive;
	bool _craftHoldCompleted;
	float _craftHoldElapsed;
	bool _craftButtonPressedVisual;
	int _builtRecipeContentVersion = -1;
	bool _builtNearCampfire;

	static readonly Color CraftButtonColor = new( 0.22f, 0.45f, 0.28f, 0.95f );
	static readonly Color CraftButtonPressedColor = new( 0.14f, 0.32f, 0.18f, 0.98f );

	/// <summary>True when the crafting page is open and this panel is shown — recipe list owns mouse wheel.</summary>
	public bool IsScrollTargetActive => _menuOpen && _panelVisible;

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
		_sectionRoot.Style.MaxHeight = Length.Pixels( MinSectionHeight );
		_sectionRoot.Style.Set( "overflow", "hidden" );
		_sectionRoot.Style.Set( "flex-shrink", "0" );

		var header = new Panel { Parent = _sectionRoot };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "justify-content", "space-between" );
		header.Style.Set( "width", "100%" );
		header.Style.Set( "gap", $"{8f * LayoutScale}px" );

		var title = new Label { Parent = header, Text = "Crafting" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( CraftingTitleFontSize );
		title.Style.Set( "flex-shrink", "0" );
		title.Style.Set( "white-space", "nowrap" );

		var craftWrap = new Panel { Parent = header };
		craftWrap.Style.Set( "position", "relative" );
		craftWrap.Style.Set( "flex-shrink", "1" );
		craftWrap.Style.Set( "min-width", "0" );

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
		_craftButtonLabel.Style.Set( "white-space", "nowrap" );

		var detail = new Panel { Parent = _sectionRoot };
		detail.Style.Set( "flex-direction", "column" );
		detail.Style.Set( "gap", $"{10f * LayoutScale}px" );
		detail.Style.Set( "width", "100%" );
		detail.Style.PaddingTop = Length.Pixels( 4f * LayoutScale );
		detail.Style.PaddingBottom = Length.Pixels( 8f * LayoutScale );
		detail.Style.Set( "border-bottom-width", "1px" );
		detail.Style.Set( "border-bottom-color", "#383d47" );
		detail.Style.Set( "flex-shrink", "1" );
		detail.Style.Set( "min-height", "0" );
		detail.Style.Set( "max-height", $"{DetailAreaMaxHeight}px" );
		detail.Style.Set( "overflow-y", "scroll" );

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

		var detailText = new Panel { Parent = detailRow };
		detailText.Style.Set( "flex-direction", "column" );
		detailText.Style.Set( "align-items", "stretch" );
		detailText.Style.Set( "gap", $"{4f * LayoutScale}px" );
		detailText.Style.Set( "flex-grow", "1" );
		detailText.Style.Set( "min-width", "0" );

		_detailName = new Label { Parent = detailText, Text = "Select a recipe" };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( ItemNameFontSize );
		_detailName.Style.Set( "white-space", "normal" );

		_detailDescription = new Label { Parent = detailText, Text = "" };
		_detailDescription.Style.FontColor = new Color( 0.72f, 0.74f, 0.78f );
		_detailDescription.Style.FontSize = Length.Pixels( ItemDescriptionFontSize );
		_detailDescription.Style.Set( "white-space", "normal" );
		_detailDescription.Style.Set( "display", "none" );

		var requirementsBlock = CreateDetailListBlock( detail, "Requirements", out _requirementsEntries );
		_ = requirementsBlock;

		var statsBlock = CreateDetailListBlock( detail, "Stats", out _statsEntries );
		_ = statsBlock;

		var listPanel = new CraftingRecipeListPanel { Parent = _sectionRoot };
		listPanel.Bind( this );
		listPanel.BuildChrome();
		_recipeListPanel = listPanel;
		_recipeList = listPanel;
		_recipeList.Style.Set( "width", "100%" );
		_recipeList.Style.Height = Length.Pixels( RecipeListMaxHeight );
		_recipeList.Style.Set( "flex-shrink", "0" );
		_recipeList.Style.Set( "flex-grow", "0" );
		_recipeList.Style.Set( "pointer-events", "auto" );

		var rowParent = listPanel.Content;
		PopulateRecipeRows( rowParent );
	}

	void PopulateRecipeRows( Panel rowParent )
	{
		if ( rowParent is null || !rowParent.IsValid() )
			return;

		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();

		rowParent.DeleteChildren();
		_rows.Clear();
		var nearCampfire = IsNearCampfire();
		_builtNearCampfire = nearCampfire;

		foreach ( var recipe in CraftingRecipeCatalog.All )
		{
			if ( recipe is null || string.IsNullOrWhiteSpace( recipe.Id ) )
				continue;

			if ( !recipe.IsUnlockedByDefault )
				continue;

			if ( recipe.RequiresStation && !HasRequiredStation( recipe ) )
				continue;

			var row = new CraftingRecipeRowPanel
			{
				Parent = rowParent,
				Section = this,
				RecipeId = recipe.Id
			};
			row.Style.Set( "flex-direction", "row" );
			row.Style.Set( "align-items", "center" );
			row.Style.Set( "justify-content", "flex-start" );
			row.Style.Set( "gap", $"{8f * LayoutScale * RecipeListItemScale}px" );
			row.Style.Set( "width", "100%" );
			row.Style.Height = Length.Pixels( RecipeRowHeight );
			row.Style.MinHeight = Length.Pixels( RecipeRowHeight );
			row.Style.MaxHeight = Length.Pixels( RecipeRowHeight );
			row.Style.Set( "flex-shrink", "0" );
			row.Style.Set( "flex-grow", "0" );
			row.Style.PaddingTop = Length.Pixels( 0f );
			row.Style.PaddingBottom = Length.Pixels( 0f );
			row.Style.PaddingLeft = Length.Pixels( 8f );
			row.Style.PaddingRight = Length.Pixels( 8f );
			row.Style.MarginTop = Length.Pixels( 0f );
			row.Style.MarginBottom = Length.Pixels( RecipeRowGap );
			row.Style.BackgroundColor = new Color( 0.10f, 0.11f, 0.13f, 0.9f );
			row.Style.Set( "border-width", $"{RecipeRowBorder}px" );
			row.Style.Set( "border-color", "#383d47" );
			row.Style.Set( "border-radius", "3px" );
			row.Style.Set( "pointer-events", "all" );
			row.Style.Set( "overflow", "hidden" );
			row.Style.Set( "box-sizing", "border-box" );
			row.ButtonInput = PanelInputType.UI;

			var icon = new Panel { Parent = row };
			icon.Style.Width = Length.Pixels( RowIconSize );
			icon.Style.Height = Length.Pixels( RowIconSize );
			icon.Style.Set( "flex-shrink", "0" );
			icon.Style.Set( "pointer-events", "none" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "background-position", "center" );
			MenuUiTextures.ApplyBackground( icon, CraftingRecipeCatalog.ResolveIconPath( recipe ) );

			var name = new Label { Parent = row, Text = recipe.DisplayName };
			name.Style.FontColor = Color.White;
			name.Style.FontSize = Length.Pixels( RecipeListRowFontSize );
			name.Style.Set( "pointer-events", "none" );
			name.Style.Set( "white-space", "nowrap" );

			_rows.Add( new RowUi( row, icon, recipe.Id ) );
		}

		if ( _rows.Count > 0 )
			_rows[^1].Root.Style.MarginBottom = Length.Pixels( 0f );

		_recipeListPanel?.SetRowCount( _rows.Count );
		_builtRecipeContentVersion = BuildRecipeListVersion( nearCampfire );

		var keepSelection = !string.IsNullOrWhiteSpace( _selectedRecipeId )
			&& CraftingRecipeCatalog.Get( _selectedRecipeId ) is not null;

		if ( keepSelection )
			SelectRecipe( _selectedRecipeId );
		else if ( _rows.Count > 0 )
			SelectRecipe( _rows[0].RecipeId );
		else
			RefreshSelectedDetail();
	}

	/// <summary>Rebuild list when host catalog sync or fallback recovery changes recipe content.</summary>
	void RebuildRecipeRowsIfNeeded()
	{
		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();

		var nearCampfire = IsNearCampfire();
		var version = BuildRecipeListVersion( nearCampfire );
		if ( version == _builtRecipeContentVersion && nearCampfire == _builtNearCampfire )
			return;

		var content = _recipeListPanel?.Content;
		if ( content is null || !content.IsValid() )
			return;

		PopulateRecipeRows( content );
	}

	static int BuildRecipeListVersion( bool nearCampfire ) =>
		CraftingRecipeCatalog.ContentVersion
		^ (ResourceDefinitionCatalog.ContentVersion << 16)
		^ (nearCampfire ? 1 << 30 : 0);

	public void ApplyRecipeListWheel( Vector2 wheel )
	{
		if ( !IsScrollTargetActive )
			return;

		_recipeListPanel?.ApplyWheel( wheel );
	}

	public bool TryHandleScrollbarPointer( Vector2 screenPos, bool pressed )
	{
		if ( !IsScrollTargetActive && !( _recipeListPanel?.IsDraggingThumb ?? false ) )
			return false;

		return _recipeListPanel?.TryHandlePointer( screenPos, pressed ) ?? false;
	}

	public bool TrySelectRecipeAtScreen( Vector2 screenPos )
	{
		if ( !IsScrollTargetActive )
			return false;

		if ( _recipeListPanel is not null
		     && _recipeListPanel.TryPickRowIndexAtScreen( screenPos, out var index )
		     && index >= 0 && index < _rows.Count )
		{
			SelectRecipe( _rows[index].RecipeId );
			return true;
		}

		for ( var i = 0; i < _rows.Count; i++ )
		{
			var row = _rows[i].Root;
			if ( row is null || !row.IsValid() )
				continue;

			if ( !IsScreenPosInsidePanel( row, screenPos ) )
				continue;

			SelectRecipe( _rows[i].RecipeId );
			return true;
		}

		return false;
	}

	public bool TryCraftPointerAtScreen( Vector2 screenPos, bool pressed )
	{
		if ( !IsScrollTargetActive )
			return false;

		var over = _craftButton is not null && _craftButton.IsValid()
		           && IsScreenPosInsidePanel( _craftButton, screenPos );

		if ( pressed )
		{
			if ( !over )
				return false;

			SetButtonPressedVisual( true );
			BeginCraftHold();
			return true;
		}

		if ( _craftHoldActive || _craftButtonPressedVisual )
		{
			EndCraftHoldFromButtonRelease();
			return true;
		}

		return false;
	}

	static bool IsScreenPosInsidePanel( Panel panel, Vector2 screenPos )
	{
		if ( panel is null || !panel.IsValid() )
			return false;

		if ( panel.IsInside( screenPos ) )
			return true;

		var rect = panel.Box.Rect;
		if ( rect.Width <= 0f || rect.Height <= 0f )
			return false;

		return screenPos.x >= rect.Left && screenPos.x <= rect.Right
		       && screenPos.y >= rect.Top && screenPos.y <= rect.Bottom;
	}

	public void SelectRecipe( string recipeId )
	{
		if ( string.IsNullOrWhiteSpace( recipeId ) )
			return;

		_selectedRecipeId = recipeId;
		// Highlight first — full Refresh can hitch on icon loads and looked like a delayed select.
		UpdateRowHighlights();
		RefreshSelectedDetail();
	}

	public void Refresh()
	{
		CraftingRecipeCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();
		RefreshSelectedDetail();
		UpdateRowHighlights();
	}

	void RefreshSelectedDetail()
	{
		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is null )
		{
			_detailName.Text = "Select a recipe";
			SetDetailDescription( null );
			PopulateEntryList( _requirementsEntries, Array.Empty<string>() );
			PopulateEntryList( _statsEntries, Array.Empty<string>() );
			ApplyDetailIcon( null );
			UpdateCraftButton( false );
			return;
		}

		_detailName.Text = recipe.DisplayName;
		SetDetailDescription( ExtractDescription( recipe ) );
		PopulateEntryList( _requirementsEntries, BuildRequirementLines( recipe ) );
		PopulateEntryList( _statsEntries, BuildStatLines( recipe ) );
		ApplyDetailIcon( CraftingRecipeCatalog.ResolveIconPath( recipe ) );
		UpdateCraftButton( CanCraftRecipe( recipe ) );
	}

	void ApplyDetailIcon( string iconPath )
	{
		if ( string.Equals( _detailIconPathApplied, iconPath ?? string.Empty, StringComparison.OrdinalIgnoreCase ) )
			return;

		_detailIconPathApplied = iconPath ?? string.Empty;
		MenuUiTextures.ApplyBackground( _detailIcon, iconPath );
	}

	void SetDetailDescription( string description )
	{
		if ( _detailDescription is null )
			return;

		var hasText = !string.IsNullOrWhiteSpace( description );
		_detailDescription.Text = hasText ? description : "";
		_detailDescription.Style.Set( "display", hasText ? "flex" : "none" );
	}

	public void SetMenuOpen( bool isOpen )
	{
		_menuOpen = isOpen;
		if ( isOpen )
		{
			CraftingRecipeCatalog.EnsureLoaded();
			ResourceDefinitionCatalog.EnsureLoaded();
			RebuildRecipeRowsIfNeeded();
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
		_panelVisible = visible;
		UpdateVisibility();
	}

	void UpdateVisibility()
	{
		if ( _sectionRoot is null )
			return;

		_sectionRoot.Style.Set( "display", _menuOpen && _panelVisible ? "flex" : "none" );
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
		var duration = Math.Max( 0.05f, GetSelectedCraftHoldSeconds() );
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
		if ( !menuOpen || !IsScrollTargetActive )
			return;

		RebuildRecipeRowsIfNeeded();
		_recipeListPanel?.PollWheelWhileOpen();
	}

	public void OnMenuGlobalMouseUp()
	{
		_recipeListPanel?.EndThumbDrag();
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

		if ( recipe.RequiresStation && !HasRequiredStation( recipe ) )
			return false;

		if ( !HasScaledResources( recipe ) )
			return false;

		return _inventory.CanFitResource( recipe.Id, recipe.TotalOutputAmount );
	}

	float GetSelectedCraftHoldSeconds()
	{
		var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
		if ( recipe is not null && recipe.ResolvedCraftSeconds > 0f )
			return recipe.ResolvedCraftSeconds;

		return CraftHoldSeconds;
	}

	bool IsNearCampfire() =>
		_inventory is not null
		&& Campfire.IsPlayerNearLitOrFueledStation( _inventory.GameObject, Campfire.StationId );

	bool HasRequiredStation( CraftingRecipe recipe )
	{
		if ( recipe is null || !recipe.RequiresStation || _inventory is null )
			return false;

		return Campfire.IsPlayerNearLitOrFueledStation( _inventory.GameObject, recipe.RequiredStation );
	}

	bool HasScaledResources( CraftingRecipe recipe )
	{
		if ( _inventory is null || recipe.Ingredients is null )
			return false;

		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			if ( _inventory.CountResource( ing.ResourceId ) < Math.Max( 1, ing.Amount ) )
				return false;
		}

		return true;
	}

	void UpdateCraftButton( bool canCraft )
	{
		if ( _craftButton is null || _craftButtonLabel is null )
			return;

		_craftButton.Style.Set( "opacity", canCraft ? "1" : "0.45" );
		if ( canCraft )
		{
			_craftButtonLabel.Text = "Hold to craft (LMB)";
		}
		else
		{
			var recipe = CraftingRecipeCatalog.Get( _selectedRecipeId );
			if ( recipe is not null && recipe.RequiresStation && !HasRequiredStation( recipe ) )
				_craftButtonLabel.Text = $"Need {recipe.RequiredStation} nearby";
			else
				_craftButtonLabel.Text = "Need materials / space";
		}

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
			row.Style.BackgroundColor = selected
				? new Color( 0.16f, 0.22f, 0.32f, 0.95f )
				: new Color( 0.10f, 0.11f, 0.13f, 0.9f );
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

		for ( var i = 0; i < recipe.Ingredients.Count; i++ )
		{
			var ing = recipe.Ingredients[i];
			if ( ing is null )
				continue;

			var def = ResourceCatalog.Resolve( ing.ResourceId );
			var have = _inventory?.CountResource( ing.ResourceId ) ?? 0;
			var need = Math.Max( 1, ing.Amount );
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

			// Description lives under the display name beside the icon — not in Stats.
			if ( string.Equals( line.Label, "Description", StringComparison.OrdinalIgnoreCase ) )
				continue;

			// Type is a tag — show the value alone (not "Type: …").
			if ( string.Equals( line.Label, "Type", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( !string.IsNullOrWhiteSpace( line.Value ) )
					lines.Add( line.Value );
				continue;
			}

			lines.Add( $"{line.Label}: {line.Value}" );
		}

		return lines;
	}

	static string ExtractDescription( CraftingRecipe recipe )
	{
		if ( recipe?.Stats is null )
			return null;

		for ( var i = 0; i < recipe.Stats.Count; i++ )
		{
			var line = recipe.Stats[i];
			if ( line is null )
				continue;

			if ( !string.Equals( line.Label, "Description", StringComparison.OrdinalIgnoreCase ) )
				continue;

			return string.IsNullOrWhiteSpace( line.Value ) ? null : line.Value.Trim();
		}

		return null;
	}

	sealed class CraftButtonPanel : Panel
	{
		readonly CraftingMenuSection _section;

		public CraftButtonPanel( CraftingMenuSection section ) => _section = section;

		public override bool WantsMouseInput() => false;

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
