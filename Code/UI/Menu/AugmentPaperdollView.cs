using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// The augment paper doll: six body parts in two columns (Head / Arms / Legs left, Torso / Hands /
/// Feet right) around a standing player preview, each part with its three sockets. The station
/// builds it <b>interactive</b> (Enhance buttons + core prices, draggable sockets); the Augments menu
/// page builds the identical doll <b>read-only</b> (no Enhance row, sockets only hover for tooltips).
/// </summary>
public sealed class AugmentPaperdollView
{
	public const float SlotSize = 56f;
	public const float SlotGap = 4f;
	public const float BodyFont = 17f;
	public const float SmallFont = 13f;
	public const float CostIconSize = 20f;
	public const float PreviewWidth = 300f;

	public static readonly Color BoxBg = new( 0.10f, 0.11f, 0.13f, 0.95f );
	public static readonly Color TitleColor = Color.White;
	public static readonly Color MutedColor = new( 0.72f, 0.74f, 0.78f );
	public static readonly Color LabelColor = new( 0.78f, 0.8f, 0.84f );
	public static readonly Color CostColor = new( 0.93f, 0.8f, 0.4f );
	public static readonly Color ButtonOff = new( 0.2f, 0.21f, 0.24f, 0.95f );
	static readonly Color EnhanceOn = new( 0.25f, 0.4f, 0.6f, 0.95f );
	static readonly Color EnhanceDone = new( 0.16f, 0.18f, 0.22f, 0.95f );
	public const string BorderIdle = "#474d57";
	const string BorderLocked = "#2a2d33";
	const string BorderPending = "#e0b84a";
	const string BorderActive = "#5ec46a";

	// Sketch order: left column Head / Arms / Legs, right column Torso / Hands / Feet.
	static readonly AugmentBodyPart[] LeftParts = { AugmentBodyPart.Head, AugmentBodyPart.Arms, AugmentBodyPart.Legs };
	static readonly AugmentBodyPart[] RightParts = { AugmentBodyPart.Torso, AugmentBodyPart.Hands, AugmentBodyPart.Feet };

	readonly PlayerAugments _augments;
	readonly IInventoryGridHost _gridHost;
	readonly PlayerInventoryInteraction _interaction;
	readonly bool _interactive;

	/// <summary>Degrees of body spin per pixel of horizontal drag.</summary>
	const float DragDegreesPerPixel = 0.5f;

	readonly SocketUi[] _socketUi = new SocketUi[AugmentSlots.Count];
	readonly PartUi[] _partUi = new PartUi[AugmentBodyParts.Count];
	AugmentPlayerPreviewPanel _preview;
	Panel _previewFrame;
	Panel _body;
	bool _previewDragging;
	bool _pointerWasHeld;
	float _previewDragLastX;

	// ── Fit-to-space scaling ────────────────────────────────────────────────────────────────
	// The doll is authored at 1× (56 px sockets, 300 px preview). Every menu tick it measures the
	// row it lives in and scales every sized element so the parts and the preview fill that row
	// with little slack — fullscreen gets a big doll, a small window keeps the 1× layout.
	const float NominalBlockHeight = 26f + 4f + 56f + 2f + 16f;
	const float NominalBlockGap = 12f;
	const float NominalRowWidth = 2f * 226f + 300f + 24f;
	const float MaxScale = 2.6f;

	readonly List<(Panel Panel, float Width, float Height)> _scaledBoxes = new();
	readonly List<(Label Label, float Font)> _scaledLabels = new();
	readonly List<Panel> _scaledBlocks = new();
	float _appliedScale = 1f;

	/// <summary>Nominal (1×) height of the doll row: three blocks and two gaps.</summary>
	public const float NominalHeight = 3f * NominalBlockHeight + 2f * NominalBlockGap;

	/// <summary>Fired with the new scale whenever the fit changes — the owning page scales its own extras (bank, binds, buttons) to match.</summary>
	public event Action<float> ScaleApplied;
	public float AppliedScale => _appliedScale;

	/// <param name="interactive">True at the station (Enhance buttons shown); false on the Augments page (view only).</param>
	public AugmentPaperdollView( PlayerAugments augments, IInventoryGridHost gridHost, PlayerInventoryInteraction interaction, bool interactive )
	{
		_augments = augments;
		_gridHost = gridHost;
		_interaction = interaction;
		_interactive = interactive;
	}

	/// <summary>Builds the doll into <paramref name="parent"/> (a column); the doll row stretches to fill it.</summary>
	public void Build( Panel parent )
	{
		var body = new Panel { Parent = parent };
		_body = body;
		body.Style.Set( "flex-direction", "row" );
		body.Style.Set( "width", "100%" );
		body.Style.Set( "flex-grow", "1" );
		body.Style.Set( "flex-shrink", "1" );
		body.Style.Set( "min-height", "0" );
		body.Style.Set( "gap", "12px" );
		body.Style.Set( "align-items", "stretch" );

		var left = new Panel { Parent = body };
		left.Style.Set( "flex-direction", "column" );
		left.Style.Set( "justify-content", "space-between" );
		left.Style.Set( "flex-grow", "1" );
		left.Style.Set( "align-items", "flex-end" );
		for ( var i = 0; i < LeftParts.Length; i++ )
			BuildPart( left, LeftParts[i] );

		var previewFrame = new Panel { Parent = body };
		_previewFrame = previewFrame;
		previewFrame.Style.Set( "flex-direction", "column" );
		previewFrame.Style.Width = Length.Pixels( PreviewWidth );
		previewFrame.Style.Set( "flex-shrink", "0" );
		_scaledBoxes.Add( (previewFrame, PreviewWidth, 0f) );
		previewFrame.Style.BackgroundColor = BoxBg;
		previewFrame.Style.Set( "border-radius", "4px" );
		previewFrame.Style.Set( "border-width", "1px" );
		previewFrame.Style.Set( "border-color", BorderIdle );
		previewFrame.Style.Set( "overflow", "hidden" );
		previewFrame.Style.Set( "pointer-events", "none" );

		_preview = new AugmentPlayerPreviewPanel { Parent = previewFrame };
		_preview.Style.Set( "width", "100%" );
		_preview.Style.Set( "height", "100%" );

		var right = new Panel { Parent = body };
		right.Style.Set( "flex-direction", "column" );
		right.Style.Set( "justify-content", "space-between" );
		right.Style.Set( "flex-grow", "1" );
		right.Style.Set( "align-items", "flex-start" );
		for ( var i = 0; i < RightParts.Length; i++ )
			BuildPart( right, RightParts[i] );
	}

	void BuildPart( Panel parent, AugmentBodyPart part )
	{
		var block = new Panel { Parent = parent };
		block.Style.Set( "flex-direction", "column" );
		block.Style.Set( "gap", "4px" );
		block.Style.Set( "flex-shrink", "0" );
		_scaledBlocks.Add( block );

		var header = new Panel { Parent = block };
		header.Style.Set( "flex-direction", "row" );
		header.Style.Set( "align-items", "center" );
		header.Style.Set( "gap", "8px" );
		header.Style.Height = Length.Pixels( 26f );
		_scaledBoxes.Add( (header, 0f, 26f) );

		var name = new Label { Parent = header, Text = AugmentBodyParts.Label( part ) };
		name.Style.FontColor = TitleColor;
		name.Style.FontSize = Length.Pixels( BodyFont );
		name.Style.Width = Length.Pixels( 60f );
		name.Style.Set( "pointer-events", "none" );
		_scaledLabels.Add( (name, BodyFont) );
		_scaledBoxes.Add( (name, 60f, 0f) );

		Panel enhance = null;
		Label enhanceLabel = null;
		Label cost = null;
		Panel coreIcon = null;
		if ( _interactive )
		{
			enhance = new Panel { Parent = header };
			enhance.Style.Width = Length.Pixels( 96f );
			enhance.Style.Height = Length.Pixels( 28f );
			_scaledBoxes.Add( (enhance, 96f, 28f) );
			enhance.Style.BackgroundColor = EnhanceOn;
			enhance.Style.Set( "border-radius", "4px" );
			enhance.Style.Set( "justify-content", "center" );
			enhance.Style.Set( "align-items", "center" );
			enhance.Style.Set( "pointer-events", "all" );

			enhanceLabel = new Label { Parent = enhance, Text = "Enhance" };
			enhanceLabel.Style.FontColor = Color.White;
			enhanceLabel.Style.FontSize = Length.Pixels( SmallFont + 1f );
			enhanceLabel.Style.Set( "pointer-events", "none" );
			_scaledLabels.Add( (enhanceLabel, SmallFont + 1f) );

			cost = new Label { Parent = header, Text = "" };
			cost.Style.FontColor = CostColor;
			cost.Style.FontSize = Length.Pixels( BodyFont );
			cost.Style.Set( "pointer-events", "none" );
			_scaledLabels.Add( (cost, BodyFont) );

			coreIcon = MakeCostIcon( header, $"ui/items/{AugmentCurrency.CoreResourceId}.png" );
			_scaledBoxes.Add( (coreIcon, CostIconSize, CostIconSize) );
		}
		else
		{
			// Same header height as the station so the doll lines up identically; open-socket count instead of a button.
			cost = new Label { Parent = header, Text = "" };
			cost.Style.FontColor = MutedColor;
			cost.Style.FontSize = Length.Pixels( SmallFont );
			cost.Style.Set( "pointer-events", "none" );
			_scaledLabels.Add( (cost, SmallFont) );
		}

		var socketsRow = new Panel { Parent = block };
		socketsRow.Style.Set( "flex-direction", "row" );
		socketsRow.Style.Set( "gap", $"{SlotGap}px" );

		var slots = AugmentBodyParts.SlotsOf( part );
		for ( var i = 0; i < slots.Length; i++ )
			BuildSocket( socketsRow, slots[i] );

		_partUi[(int)part] = new PartUi( enhance, enhanceLabel, cost, coreIcon );
	}

	void BuildSocket( Panel row, AugmentSlot slot )
	{
		var host = new Panel { Parent = row };
		host.Style.Set( "flex-direction", "column" );
		host.Style.Set( "align-items", "center" );
		host.Style.Set( "gap", "2px" );
		host.Style.Width = Length.Pixels( SlotSize + 8f );
		_scaledBoxes.Add( (host, SlotSize + 8f, 0f) );

		var slotPanel = new InventorySlotPanel( (int)slot, _gridHost, _interaction ) { Parent = host };
		StyleSlot( slotPanel );
		_interaction?.RegisterSlot( slotPanel );
		_scaledBoxes.Add( (slotPanel, SlotSize, SlotSize) );
		var ui = CreateSlotUi( slotPanel );

		var lockOverlay = new Panel { Parent = slotPanel };
		lockOverlay.Style.Set( "position", "absolute" );
		lockOverlay.Style.Set( "left", "0" );
		lockOverlay.Style.Set( "top", "0" );
		lockOverlay.Style.Set( "right", "0" );
		lockOverlay.Style.Set( "bottom", "0" );
		lockOverlay.Style.BackgroundColor = new Color( 0f, 0f, 0f, 0.62f );
		lockOverlay.Style.Set( "justify-content", "center" );
		lockOverlay.Style.Set( "align-items", "center" );
		lockOverlay.Style.Set( "pointer-events", "none" );
		var lockText = new Label { Parent = lockOverlay, Text = "locked" };
		lockText.Style.FontColor = new Color( 0.55f, 0.57f, 0.62f );
		lockText.Style.FontSize = Length.Pixels( 11f );
		lockText.Style.Set( "pointer-events", "none" );
		_scaledLabels.Add( (lockText, 11f) );

		var label = new Label { Parent = host, Text = AugmentSlots.VariationLabel( slot ) };
		label.Style.FontColor = LabelColor;
		label.Style.FontSize = Length.Pixels( SmallFont - 1f );
		label.Style.Set( "pointer-events", "none" );
		_scaledLabels.Add( (label, SmallFont - 1f) );

		_socketUi[(int)slot] = new SocketUi( slotPanel, ui, lockOverlay );
	}

	/// <summary>
	/// Every menu tick: measure the page column and scale the doll (and, through
	/// <see cref="ScaleApplied"/>, the page's extras) so the whole stack fills it. The column is
	/// measured rather than the doll row so extras that grow with the scale cannot feed back into
	/// the fit. Cheap — one rect read and an early-out unless the fit moved a few percent.
	/// </summary>
	/// <param name="column">The page column the doll sits in.</param>
	/// <param name="extraScalableNominal">1× height of everything else in the column that scales with the doll.</param>
	/// <param name="fixedHeight">Height of everything that does not scale (titles, gaps, padding).</param>
	public void TickLayout( Panel column, float extraScalableNominal, float fixedHeight )
	{
		if ( column is null || !column.IsValid() )
			return;

		var scale = column.ScaleToScreen;
		if ( scale < 0.001f )
			return;

		var rect = column.Box.Rect;
		var height = rect.Height / scale - fixedHeight;
		var width = rect.Width / scale - 20f;
		if ( height < 10f || width < 10f )
			return;

		var byHeight = height / (NominalHeight + MathF.Max( 0f, extraScalableNominal ));
		var byWidth = width / NominalRowWidth;
		var fit = Math.Clamp( MathF.Min( byHeight, byWidth ), 1f, MaxScale );
		if ( MathF.Abs( fit - _appliedScale ) < 0.03f )
			return;

		ApplyScale( fit );
		ScaleApplied?.Invoke( fit );
	}

	void ApplyScale( float s )
	{
		_appliedScale = s;

		for ( var i = 0; i < _scaledBoxes.Count; i++ )
		{
			var (panel, w, h) = _scaledBoxes[i];
			if ( panel is null || !panel.IsValid() )
				continue;

			if ( w > 0f )
				panel.Style.Width = Length.Pixels( w * s );
			if ( h > 0f )
				panel.Style.Height = Length.Pixels( h * s );
		}

		for ( var i = 0; i < _scaledLabels.Count; i++ )
		{
			var (label, font) = _scaledLabels[i];
			if ( label is not null && label.IsValid() )
				label.Style.FontSize = Length.Pixels( font * s );
		}

		for ( var i = 0; i < _scaledBlocks.Count; i++ )
		{
			var block = _scaledBlocks[i];
			if ( block is not null && block.IsValid() )
				block.Style.Set( "gap", $"{4f * s:0.#}px" );
		}
	}

	/// <summary>
	/// Every frame while the page is open: soft-cursor position + Attack1 held. A press that starts on
	/// the preview frame becomes a drag that spins the body; releasing ends it.
	/// </summary>
	public void TickPointerDrag( Vector2 screenPos, bool held )
	{
		var pressedThisFrame = held && !_pointerWasHeld;
		_pointerWasHeld = held;

		if ( !held )
		{
			_previewDragging = false;
			return;
		}

		if ( _previewDragging )
		{
			_preview?.RotateBy( (screenPos.x - _previewDragLastX) * DragDegreesPerPixel );
			_previewDragLastX = screenPos.x;
			return;
		}

		// Only a press that lands on the box starts a spin — a button held elsewhere and dragged
		// across the preview (an item drag, a scrollbar drag) never grabs the character.
		if ( !pressedThisFrame )
			return;

		if ( _previewFrame is null || !_previewFrame.IsValid() || !_previewFrame.IsInside( screenPos ) )
			return;

		_previewDragging = true;
		_previewDragLastX = screenPos.x;
	}

	/// <summary>Soft-cursor press on an Enhance button (interactive doll only).</summary>
	public bool TryPressEnhanceAtScreen( Vector2 screenPos )
	{
		if ( !_interactive || _augments is null )
			return false;

		for ( var p = 0; p < _partUi.Length; p++ )
		{
			var button = _partUi[p]?.EnhanceButton;
			if ( button is null || !button.IsValid() || !button.IsInside( screenPos ) )
				continue;

			_augments.OwnerTryEnhance( (AugmentBodyPart)p );
			return true;
		}

		return false;
	}

	public void Refresh()
	{
		RefreshSockets();
		RefreshParts();
		RefreshPreviewOutfit();
	}

	void RefreshSockets()
	{
		for ( var i = 0; i < _socketUi.Length; i++ )
		{
			var ui = _socketUi[i];
			if ( ui is null )
				continue;

			var slot = (AugmentSlot)i;
			var stack = _augments?.GetInstalled( slot ) ?? InventorySlot.Empty;
			ResourceCatalog.ApplyStackVisual( ui.Slot.IconPanel, ui.Slot.CountLabel, stack );

			var unlocked = _augments?.IsSlotUnlocked( slot ) ?? false;
			ui.LockOverlay.Style.Set( "display", unlocked ? "none" : "flex" );

			var border = !unlocked ? BorderLocked
				: _augments.IsSlotActive( slot ) ? BorderActive
				: _augments.IsSlotPending( slot ) ? BorderPending
				: BorderIdle;
			ui.Root.Style.Set( "border-color", border );
		}
	}

	void RefreshParts()
	{
		for ( var p = 0; p < _partUi.Length; p++ )
		{
			var ui = _partUi[p];
			if ( ui is null )
				continue;

			var part = (AugmentBodyPart)p;
			if ( !_interactive )
			{
				var open = _augments?.GetUnlockedCount( part ) ?? 0;
				ui.CostLabel.Text = $"{open}/{AugmentBodyParts.SlotsPerPart} open";
				continue;
			}

			var cost = _augments?.GetNextEnhanceCoreCost( part ) ?? 0;
			if ( cost <= 0 )
			{
				ui.CostLabel.Text = "full";
				ui.CoreIcon.Style.Set( "display", "none" );
				ui.EnhanceButton.Style.BackgroundColor = EnhanceDone;
				ui.EnhanceLabel.Style.FontColor = MutedColor;
				continue;
			}

			ui.CostLabel.Text = cost.ToString();
			ui.CoreIcon.Style.Set( "display", "flex" );
			var can = _augments?.CanEnhance( part ) ?? false;
			ui.EnhanceButton.Style.BackgroundColor = can ? EnhanceOn : ButtonOff;
			ui.EnhanceLabel.Style.FontColor = can ? Color.White : MutedColor;
		}
	}

	void RefreshPreviewOutfit()
	{
		if ( _preview is null || _augments is null )
			return;

		var equipment = _augments.Components.Get<PlayerEquipment>();
		_preview.SetClothing( equipment?.NetworkedWornClothing ?? string.Empty );
	}

	// ── Shared slot / icon widgets (station bank + bag use these too) ───────────────────────

	public static Panel MakeCostIcon( Panel parent, string iconPath )
	{
		var icon = new Panel { Parent = parent };
		icon.Style.Width = Length.Pixels( CostIconSize );
		icon.Style.Height = Length.Pixels( CostIconSize );
		icon.Style.Set( "flex-shrink", "0" );
		icon.Style.Set( "pointer-events", "none" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );
		MenuUiTextures.ApplyBackground( icon, iconPath );
		return icon;
	}

	public static void StyleSlot( Panel slotPanel )
	{
		slotPanel.Style.Width = Length.Pixels( SlotSize );
		slotPanel.Style.Height = Length.Pixels( SlotSize );
		slotPanel.Style.Set( "flex-shrink", "0" );
		slotPanel.Style.Set( "position", "relative" );
		slotPanel.Style.Set( "box-sizing", "border-box" );
		slotPanel.Style.BackgroundColor = new Color( 0.1f, 0.11f, 0.13f, 0.95f );
		slotPanel.Style.Set( "border-width", "2px" );
		slotPanel.Style.Set( "border-color", BorderIdle );
		slotPanel.Style.Set( "border-radius", "4px" );
		slotPanel.Style.Set( "overflow", "hidden" );
		slotPanel.Style.Set( "pointer-events", "auto" );
	}

	public static SlotUi CreateSlotUi( InventorySlotPanel slotPanel )
	{
		var icon = new Panel { Parent = slotPanel };
		icon.Style.Set( "position", "absolute" );
		icon.Style.Set( "left", "4px" );
		icon.Style.Set( "right", "4px" );
		icon.Style.Set( "top", "4px" );
		icon.Style.Set( "bottom", "4px" );
		icon.Style.Set( "pointer-events", "none" );
		icon.Style.Set( "background-size", "contain" );
		icon.Style.Set( "background-repeat", "no-repeat" );
		icon.Style.Set( "background-position", "center" );

		var count = new Label { Parent = slotPanel, Text = "" };
		count.Style.Set( "position", "absolute" );
		count.Style.Set( "right", "3px" );
		count.Style.Set( "bottom", "1px" );
		count.Style.FontColor = Color.White;
		count.Style.FontSize = Length.Pixels( 12f );
		count.Style.Set( "pointer-events", "none" );

		return new SlotUi( icon, count );
	}

	public readonly struct SlotUi
	{
		public Panel IconPanel { get; }
		public Label CountLabel { get; }
		public SlotUi( Panel icon, Label count )
		{
			IconPanel = icon;
			CountLabel = count;
		}
	}

	sealed class SocketUi
	{
		public Panel Root { get; }
		public SlotUi Slot { get; }
		public Panel LockOverlay { get; }
		public SocketUi( Panel root, SlotUi slot, Panel lockOverlay )
		{
			Root = root;
			Slot = slot;
			LockOverlay = lockOverlay;
		}
	}

	sealed class PartUi
	{
		public Panel EnhanceButton { get; }
		public Label EnhanceLabel { get; }
		public Label CostLabel { get; }
		public Panel CoreIcon { get; }
		public PartUi( Panel enhanceButton, Label enhanceLabel, Label costLabel, Panel coreIcon )
		{
			EnhanceButton = enhanceButton;
			EnhanceLabel = enhanceLabel;
			CostLabel = costLabel;
			CoreIcon = coreIcon;
		}
	}
}
