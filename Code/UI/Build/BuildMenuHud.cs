using System;
using System.Collections.Generic;
using System.Text;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Center-screen build piece picker (not a tab on the main game menu).</summary>
public sealed class BuildMenuHud
{
	public const float Scale = InventoryMenuSection.Scale;
	public const float SlotSize = InventoryMenuSection.SlotSize;
	public const float SlotGap = InventoryMenuSection.SlotGap;
	public const float TitleFontSize = CraftingMenuSection.CraftingTitleFontSize;
	public const float BodyFontSize = CraftingMenuSection.SectionEntryFontSize;
	public const float DetailNameFontSize = CraftingMenuSection.ItemNameFontSize;
	public const int Columns = 8;

	public const float DetailPanelHeight = 72f * Scale;
	public const float PanelWidth = Columns * SlotSize + ( Columns - 1 ) * SlotGap + 32f * Scale;

	readonly PlayerEquipment _equipment;
	readonly List<PieceSlotUi> _slots = new();

	BuildMenuInputOverlay _overlay;
	Panel _panelRoot;
	Panel _grid;
	Panel _detailRoot;
	Label _detailName;
	Label _detailCosts;
	Panel _blueprintToggleRow;
	Panel _blueprintToggleBox;
	Label _blueprintToggleCheck;
	Label _blueprintToggleLabel;
	ToolBuildHammer _boundHammer;
	string _hoveredPieceId;
	int _builtPieceContentVersion = -1;

	public BuildMenuHud( PlayerEquipment equipment ) => _equipment = equipment;

	public void Tick()
	{
		EnsureSlotsMatchCatalog();
		PollHoveredSlot();
	}

	/// <summary>
	/// Hover is polled rather than event-driven. Mouse-out bubbles from a slot up to the grid, so
	/// the grid's own handler cleared the hover again the moment the pointer crossed between two
	/// slots — the name panel and highlight never settled on anything.
	/// </summary>
	void PollHoveredSlot()
	{
		if ( _grid is null )
			return;

		var hammer = ResolveBuildHammer();
		if ( hammer is null || !hammer.IsBuildMenuOpen )
			return;

		PieceSlotUi hovered = null;
		for ( var i = 0; i < _slots.Count; i++ )
		{
			var slot = _slots[i];
			if ( slot?.Panel is null || !slot.Panel.HasHovered )
				continue;

			hovered = slot;
			break;
		}

		SetHoveredSlot( hovered );
	}

	public void Build( Panel hudRoot )
	{
		if ( _equipment is null || hudRoot is null )
			return;

		_overlay = new BuildMenuInputOverlay { Parent = hudRoot };
		_overlay.Bind( _equipment );
		_overlay.ButtonInput = PanelInputType.UI;
		_overlay.Style.Set( "position", "absolute" );
		_overlay.Style.Set( "left", "0" );
		_overlay.Style.Set( "top", "0" );
		_overlay.Style.Set( "width", "100%" );
		_overlay.Style.Set( "height", "100%" );
		_overlay.Style.Set( "z-index", "3500" );
		_overlay.Style.Set( "display", "none" );
		_overlay.Style.Set( "pointer-events", "none" );
		_overlay.SetOpen( false );

		_panelRoot = new Panel { Parent = _overlay };
		_panelRoot.Style.Set( "position", "absolute" );
		_panelRoot.Style.Set( "left", "50%" );
		_panelRoot.Style.Set( "top", "50%" );
		_panelRoot.Style.Set( "transform", "translate(-50%, -50%)" );
		_panelRoot.Style.Set( "flex-direction", "column" );
		_panelRoot.Style.Set( "align-items", "stretch" );
		_panelRoot.Style.Set( "gap", $"{8f * Scale}px" );
		_panelRoot.Style.Set( "width", $"{PanelWidth}px" );
		_panelRoot.Style.PaddingLeft = Length.Pixels( 16f * Scale );
		_panelRoot.Style.PaddingRight = Length.Pixels( 16f * Scale );
		_panelRoot.Style.PaddingTop = Length.Pixels( 14f * Scale );
		_panelRoot.Style.PaddingBottom = Length.Pixels( 14f * Scale );
		_panelRoot.Style.BackgroundColor = new Color( 0.06f, 0.06f, 0.07f, 0.92f );
		_panelRoot.Style.Set( "border-radius", "8px" );
		_panelRoot.Style.Set( "pointer-events", "auto" );

		var title = new Label { Parent = _panelRoot, Text = "Build" };
		title.Style.FontColor = Color.White;
		title.Style.FontSize = Length.Pixels( TitleFontSize );

		_blueprintToggleRow = new Panel { Parent = _panelRoot };
		_blueprintToggleRow.Style.Set( "flex-direction", "row" );
		_blueprintToggleRow.Style.Set( "align-items", "center" );
		_blueprintToggleRow.Style.Set( "gap", $"{6f * Scale}px" );
		_blueprintToggleRow.AddEventListener( "onclick", () => ResolveBuildHammer()?.ToggleBlueprintMode() );

		_blueprintToggleBox = new Panel { Parent = _blueprintToggleRow };
		_blueprintToggleBox.Style.Width = Length.Pixels( 18f * Scale );
		_blueprintToggleBox.Style.Height = Length.Pixels( 18f * Scale );
		_blueprintToggleBox.Style.BackgroundColor = new Color( 0.18f, 0.18f, 0.2f );
		_blueprintToggleBox.Style.Set( "border-radius", "3px" );
		_blueprintToggleBox.Style.Set( "align-items", "center" );
		_blueprintToggleBox.Style.Set( "justify-content", "center" );
		_blueprintToggleBox.Style.Set( "border-width", "2px" );
		_blueprintToggleBox.Style.Set( "border-color", "#595961" );

		_blueprintToggleCheck = new Label { Parent = _blueprintToggleBox, Text = "✓" };
		_blueprintToggleCheck.Style.FontColor = new Color( 0.22f, 0.48f, 0.95f );
		_blueprintToggleCheck.Style.FontSize = Length.Pixels( 14f * Scale );
		_blueprintToggleCheck.Style.Set( "display", "none" );

		_blueprintToggleLabel = new Label { Parent = _blueprintToggleRow, Text = "Blueprint mode: ON" };
		_blueprintToggleLabel.Style.FontColor = Color.White;
		_blueprintToggleLabel.Style.FontSize = Length.Pixels( BodyFontSize );

		_grid = new Panel { Parent = _panelRoot };
		_grid.Style.Set( "flex-direction", "row" );
		_grid.Style.Set( "flex-wrap", "wrap" );
		_grid.Style.Set( "justify-content", "center" );
		_grid.Style.Set( "gap", $"{SlotGap}px" );
		_grid.Style.Set( "max-width", $"{Columns * SlotSize + ( Columns - 1 ) * SlotGap}px" );

		_detailRoot = new Panel { Parent = _panelRoot };
		_detailRoot.Style.Set( "flex-direction", "column" );
		_detailRoot.Style.Set( "align-items", "center" );
		_detailRoot.Style.Set( "justify-content", "center" );
		_detailRoot.Style.Set( "gap", $"{4f * Scale}px" );
		_detailRoot.Style.Set( "width", "100%" );
		_detailRoot.Style.Set( "height", $"{DetailPanelHeight}px" );
		_detailRoot.Style.Set( "min-height", $"{DetailPanelHeight}px" );
		_detailRoot.Style.Set( "flex-shrink", "0" );
		_detailRoot.Style.Set( "overflow", "hidden" );

		_detailName = new Label { Parent = _detailRoot };
		_detailName.Style.FontColor = Color.White;
		_detailName.Style.FontSize = Length.Pixels( DetailNameFontSize );
		_detailName.Style.Set( "text-align", "center" );

		_detailCosts = new Label { Parent = _detailRoot };
		_detailCosts.Style.FontColor = new Color( 0.82f, 0.84f, 0.88f );
		_detailCosts.Style.FontSize = Length.Pixels( BodyFontSize );
		_detailCosts.Style.Set( "text-align", "center" );
		_detailCosts.Style.Set( "white-space", "pre-line" );

		UpdateDetailPanel( null );
		RebuildSlots();

		_equipment.EquipmentChanged += OnEquipmentChanged;
		RebindHammerEvents();
		OnBuildMenuOpenChanged();
		OnBlueprintModeChanged();
	}

	void EnsureSlotsMatchCatalog()
	{
		BuildPieceCatalog.EnsureLoaded();
		if ( _builtPieceContentVersion == BuildPieceCatalog.ContentVersion )
			return;

		RebuildSlots();
	}

	void RebuildSlots()
	{
		if ( _grid is null )
			return;

		_hoveredPieceId = null;
		_grid.DeleteChildren();
		_slots.Clear();

		BuildPieceCatalog.EnsureLoaded();
		foreach ( var piece in BuildPieceCatalog.All )
		{
			if ( piece is null || string.IsNullOrWhiteSpace( piece.Id ) )
				continue;

			var slotPanel = new Panel { Parent = _grid };
			slotPanel.Style.Width = Length.Pixels( SlotSize );
			slotPanel.Style.Height = Length.Pixels( SlotSize );
			slotPanel.Style.BackgroundColor = new Color( 0.12f, 0.13f, 0.15f, 0.92f );
			slotPanel.Style.Set( "border-radius", "4px" );
			slotPanel.Style.Set( "border-width", "1px" );
			slotPanel.Style.Set( "border-color", "#474d57" );
			slotPanel.Style.Set( "align-items", "center" );
			slotPanel.Style.Set( "justify-content", "center" );
			slotPanel.Style.Set( "cursor", "pointer" );
			slotPanel.Style.Set( "position", "relative" );

			var iconInset = 4f * Scale;
			var icon = new Panel { Parent = slotPanel };
			icon.Style.Set( "position", "absolute" );
			icon.Style.Set( "left", $"{iconInset}px" );
			icon.Style.Set( "top", $"{iconInset}px" );
			icon.Style.Set( "right", $"{iconInset}px" );
			icon.Style.Set( "bottom", $"{iconInset}px" );
			icon.Style.Set( "background-size", "contain" );
			icon.Style.Set( "background-position", "center" );
			icon.Style.Set( "background-repeat", "no-repeat" );
			icon.Style.Set( "pointer-events", "none" );

			var slotUi = new PieceSlotUi { Panel = slotPanel, Icon = icon, PieceId = piece.Id, Data = piece };

			var iconPath = piece.Icon;
			if ( !MenuUiTextures.ApplyBackground( icon, iconPath ) )
			{
				icon.Style.BackgroundColor = BuildPieceCatalog.ParseFallbackColor( piece.FallbackColor ).WithAlpha( 0.95f );
			}

			var pieceId = piece.Id;
			slotPanel.AddEventListener( "onclick", () =>
			{
				var hammer = ResolveBuildHammer();
				if ( hammer is null )
					return;

				hammer.SelectPiece( pieceId );
				hammer.SetBuildMenuOpen( false );
			} );

			_slots.Add( slotUi );
		}

		_builtPieceContentVersion = BuildPieceCatalog.ContentVersion;
		UpdateDetailPanel( null );
		ApplyBlueprintModeUi();
	}

	void SetHoveredSlot( PieceSlotUi hovered )
	{
		var hoveredId = hovered?.PieceId;
		if ( string.Equals( _hoveredPieceId, hoveredId, StringComparison.OrdinalIgnoreCase ) )
			return;

		_hoveredPieceId = hoveredId;
		UpdateSlotHighlights( hovered );
		UpdateDetailPanel( hovered?.Data );
		ApplyBlueprintModeUi();
	}

	ToolBuildHammer ResolveBuildHammer() => _equipment?.GetActiveTool<ToolBuildHammer>();

	void RebindHammerEvents()
	{
		if ( _boundHammer is not null )
		{
			_boundHammer.BuildMenuOpenChanged -= OnBuildMenuOpenChanged;
			_boundHammer.BlueprintModeChanged -= OnBlueprintModeChanged;
		}

		_boundHammer = ResolveBuildHammer();
		if ( _boundHammer is null )
			return;

		_boundHammer.BuildMenuOpenChanged += OnBuildMenuOpenChanged;
		_boundHammer.BlueprintModeChanged += OnBlueprintModeChanged;
		ApplyBlueprintModeUi();
	}

	void OnEquipmentChanged()
	{
		RebindHammerEvents();
		OnBuildMenuOpenChanged();
	}

	void OnBuildMenuOpenChanged()
	{
		var hammer = ResolveBuildHammer();
		var visible = hammer is not null && hammer.IsBuildMenuOpen;

		if ( visible )
		{
			EnsureSlotsMatchCatalog();
			RebindHammerEvents();
		}

		_overlay?.SetOpen( visible );
		if ( _panelRoot is not null )
			_panelRoot.Style.Set( "display", visible ? "flex" : "none" );

		if ( visible )
			ApplyBlueprintModeUi();
		else
		{
			_hoveredPieceId = null;
			UpdateSlotHighlights( null );
			UpdateDetailPanel( null );
		}
	}

	void OnBlueprintModeChanged() => ApplyBlueprintModeUi();

	void ApplyBlueprintModeUi()
	{
		var hammer = ResolveBuildHammer();
		var enabled = hammer?.BlueprintModeEnabled ?? true;
		var hideBlueprintToggle = hammer is not null && hammer.IsRepairMode
		                          || BuildPieceCatalog.TryGet( _hoveredPieceId, out var hovered )
		                          && hovered.IsRepairTool;

		if ( _blueprintToggleRow is not null )
			_blueprintToggleRow.Style.Set( "display", hideBlueprintToggle ? "none" : "flex" );

		if ( _blueprintToggleLabel is not null )
			_blueprintToggleLabel.Text = enabled ? "Blueprint mode: ON" : "Blueprint mode: OFF";

		if ( _blueprintToggleBox is not null )
		{
			_blueprintToggleBox.Style.BackgroundColor = enabled
				? new Color( 0.14f, 0.24f, 0.42f, 0.95f )
				: new Color( 0.18f, 0.18f, 0.2f );
			_blueprintToggleBox.Style.Set( "border-color", enabled ? "#387af2" : "#595961" );
		}

		if ( _blueprintToggleCheck is not null )
			_blueprintToggleCheck.Style.Set( "display", enabled ? "flex" : "none" );
	}

	void UpdateSlotHighlights( PieceSlotUi hovered )
	{
		for ( var i = 0; i < _slots.Count; i++ )
		{
			var slot = _slots[i];
			if ( slot?.Panel is null )
				continue;

			// A 1px→2px border recolour was too subtle to read at slot size — lift the whole tile.
			var isHovered = hovered is not null && slot == hovered;
			slot.Panel.Style.Set( "border-color", isHovered ? "#8ab4f8" : "#474d57" );
			slot.Panel.Style.Set( "border-width", isHovered ? "3px" : "1px" );
			slot.Panel.Style.BackgroundColor = isHovered
				? new Color( 0.20f, 0.28f, 0.42f, 0.98f )
				: new Color( 0.12f, 0.13f, 0.15f, 0.92f );
			slot.Panel.Style.Set( "transform", isHovered ? "scale(1.08)" : "scale(1)" );
		}
	}

	void UpdateDetailPanel( BuildPieceData piece )
	{
		if ( _detailRoot is null || _detailName is null || _detailCosts is null )
			return;

		if ( piece is null )
		{
			_detailName.Text = "Hover a piece";
			_detailCosts.Text = "Material costs appear here.";
			return;
		}

		_detailName.Text = piece.DisplayName ?? piece.Id;
		if ( piece.IsRepairTool )
		{
			_detailCosts.Text = BuildSettings.FreeBuild
				? "Click blueprint pieces to finish them.\nMaterials: Free build"
				: $"Click blueprint pieces to finish them.\n{FormatMaterialLines( piece )}";
			return;
		}

		_detailCosts.Text = FormatMaterialLines( piece );
	}

	static string FormatMaterialLines( BuildPieceData piece )
	{
		if ( BuildSettings.FreeBuild )
			return "Materials: Free build";

		if ( piece?.Costs is null || piece.Costs.Count == 0 )
			return "Materials: None";

		var sb = new StringBuilder();
		sb.AppendLine( "Materials:" );
		for ( var i = 0; i < piece.Costs.Count; i++ )
		{
			var cost = piece.Costs[i];
			if ( cost is null || string.IsNullOrWhiteSpace( cost.ResourceId ) || cost.Amount <= 0 )
				continue;

			var resource = ResourceCatalog.Resolve( cost.ResourceId );
			sb.AppendLine( $"  {resource.DisplayName} x{cost.Amount}" );
		}

		return sb.ToString().TrimEnd();
	}

	sealed class PieceSlotUi
	{
		public Panel Panel;
		public Panel Icon;
		public string PieceId;
		public BuildPieceData Data;
	}
}
