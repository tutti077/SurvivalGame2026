using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Build-hammer tool: menu, ghost preview, snap placement, demolish.
/// </summary>
[Title( "Tool Build Hammer" )]
public sealed class ToolBuildHammer : Component
{
	[Property, Group( "Input" )] public string OpenMenuAction { get; set; } = "Attack2";
	[Property, Group( "Input" )] public string PlaceAction { get; set; } = "Attack1";
	[Property, Group( "Input" )] public string SnapPrevAction { get; set; } = "BuildSnapPrev";
	[Property, Group( "Input" )] public string SnapNextAction { get; set; } = "BuildSnapNext";

	/// <summary>Hold Attack2 this long while aimed at a piece to demolish. Quick tap/release opens the build menu.</summary>
	[Property, Group( "Input" ), Title( "Demolish Hold (seconds)" )]
	public float DemolishHoldSeconds { get; set; } = 0.25f;
	[Property, Group( "Placement" )] public float BuildRange { get; set; } = 640f;

	/// <summary>Plain scroll rotate step.</summary>
	[Property, Group( "Placement" ), Title( "Scroll Rotate Step (deg)" )]
	public float ScrollYawStep { get; set; } = 45f;

	/// <summary>
	/// Ctrl + scroll. Fine steps only read on free / corner-held placements — a flush edge mate
	/// quantises yaw to 90°, so anything under a quarter turn is rounded away there.
	/// </summary>
	[Property, Group( "Placement" ), Title( "Ctrl Scroll Rotate Step (deg)" )]
	public float FineYawStep { get; set; } = 15f;
	[Property, Group( "Debug" )] public bool FreeBuildEnabled { get; set; } = true;
	[Property, Group( "Debug" )] public bool ShowSnapDebug { get; set; } = false;
	[Property, Group( "Debug" )] public bool ShowBuildRayDebug { get; set; } = true;
	[Property, Group( "Debug" )] public bool LogBuildMode { get; set; }

	/// <summary>
	/// Flip on to dump one snap/collider report for every catalog piece to the console, then it
	/// resets itself. Use this when a piece's markers do not sit where the mesh does — the report
	/// says whether the layout, the half extents or the pitch is the one disagreeing.
	/// </summary>
	[Property, Group( "Debug" ), Title( "Dump Snap Report" )]
	public bool DumpSnapReport { get; set; }

	public bool BlueprintModeEnabled { get; private set; } = true;
	public bool IsBuildMenuOpen { get; private set; }
	public bool IsPlacingPiece => !string.IsNullOrWhiteSpace( _selectedPieceId );
	public bool IsRepairMode => BuildPieceCatalog.IsRepairTool( _selectedPieceId );
	public bool IsPreviewingPlacePiece => IsPlacingPiece && !IsRepairMode && !IsBuildMenuOpen;
	public string SelectedPieceId => _selectedPieceId ?? string.Empty;

	public event Action BuildMenuOpenChanged;
	public event Action BlueprintModeChanged;

	GameObject _pawn;
	PlayerVitals _vitals;
	PlayerGameMenuController _menu;

	string _selectedPieceId;
	BuildPieceData _selectedData;
	GameObject _prefabTemplate;
	GameObject _previewRoot;
	float _yawDegrees;
	int _snapAnchorIndex;
	int _lastSnapAnchorCount;
	BuildSnapGroupKey? _lockedSnapGroup;
	/// <summary>Last Q/E variant per piece id — kept across places of the same type.</summary>
	readonly Dictionary<string, int> _snapVariantByPieceId = new( StringComparer.OrdinalIgnoreCase );
	bool _previewValid;
	BuildPlacementResult _lastPlacement;
	BuildSnapCandidate? _activeSnapCandidate;
	readonly List<BuildSnapPoint> _placingSnaps = new();
	bool _suppressBuildMenuToggle;
	bool _openMenuHeld;
	bool _demolishedThisHold;
	double _openMenuHoldStarted;

	public void BindPawn( GameObject pawn ) => _pawn = pawn;

	/// <summary>True while a ghost is up and mated to an existing piece (as opposed to free aim).</summary>
	public bool IsSnappedToStructure => IsPreviewingPlacePiece && _lastPlacement.SnappedToStructure;

	/// <summary>1-based Q/E position and the number of variants on the current seam, for the HUD.</summary>
	public int SnapVariantNumber => _lastPlacement.SnapCandidateCount <= 0 ? 0 : _snapAnchorIndex + 1;

	public int SnapVariantCount => Math.Max( 0, _lastPlacement.SnapCandidateCount );

	/// <summary>
	/// Which snap the ghost is hanging from: "Auto" for the flush edge mate, otherwise the held
	/// corner/end of the piece being placed.
	/// </summary>
	public string SnapVariantLabel
	{
		get
		{
			if ( !IsSnappedToStructure || _activeSnapCandidate is not { } candidate )
				return "Free";

			if ( candidate.CycleOrder == 0 )
				return "Auto";

			if ( candidate.AnchorSnapIndex >= 0 && candidate.AnchorSnapIndex < _placingSnaps.Count )
				return BuildSnapLayout.GetHoldLabel(
					_selectedPieceId,
					_placingSnaps[candidate.AnchorSnapIndex].Role );

			return "Auto";
		}
	}

	protected override void OnStart()
	{
		base.OnStart();
		ResolvePawnComponents();
		BuildPieceCatalog.EnsureLoaded();
		ApplyBuildSettings();
	}

	protected override void OnDestroy()
	{
		DestroyPreview();
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		if ( DumpSnapReport )
		{
			DumpSnapReport = false;
			BuildSnapDebug.LogPieceReport();
		}

		base.OnUpdate();
		if ( !IsLocalDriver() )
			return;

		if ( _menu is not null && _menu.IsMenuOpen )
		{
			if ( IsBuildMenuOpen )
				SetBuildMenuOpen( false );
			return;
		}

		PollBuildMenuInput();
		if ( IsBuildMenuOpen )
			return;

		PollHammerInput();

		if ( !IsPlacingPiece )
			return;

		UpdatePreview();
	}

	void ApplyBuildSettings()
	{
		// Local preview/UI (FreeBuild cost display) — every driver needs this, not just host.
		BuildSettings.FreeBuild = FreeBuildEnabled;
	}

	public void ToggleBlueprintMode()
	{
		BlueprintModeEnabled = !BlueprintModeEnabled;
		BlueprintModeChanged?.Invoke();
		UpdatePreviewVisual();
	}

	public void SetBuildMenuOpen( bool open )
	{
		if ( IsBuildMenuOpen == open )
			return;

		IsBuildMenuOpen = open;
		if ( open )
			DestroyPreview();

		BuildMenuOpenChanged?.Invoke();
	}

	public void SelectPiece( string pieceId )
	{
		pieceId = pieceId?.Trim() ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( pieceId ) || !BuildPieceCatalog.TryGet( pieceId, out var data ) )
			return;

		RememberSnapVariantForSelected();

		DestroyPreview();
		_selectedPieceId = pieceId;
		_selectedData = data;
		_prefabTemplate = BuildPrefabUtility.GetTemplate( data.Prefab );
		_yawDegrees = 0f;
		_snapAnchorIndex = _snapVariantByPieceId.TryGetValue( pieceId, out var saved ) ? saved : 0;
		_lastSnapAnchorCount = 0;
		_lockedSnapGroup = null;
		RefreshPlacingSnaps();
		RefreshSceneSnapPoints();

		if ( _prefabTemplate is null || !_prefabTemplate.IsValid() )
			Log.Warning( $"[ToolBuildHammer] Prefab '{data.Prefab}' not loaded for '{pieceId}' — using placeholder preview." );

		if ( LogBuildMode )
			Log.Info( $"[ToolBuildHammer] Selected build piece '{pieceId}'." );

		UpdatePreview();
	}

	void RememberSnapVariantForSelected()
	{
		if ( string.IsNullOrWhiteSpace( _selectedPieceId ) )
			return;

		_snapVariantByPieceId[_selectedPieceId] = _snapAnchorIndex;
	}

	public void ClearSelectedPiece()
	{
		if ( !IsPlacingPiece && _previewRoot is null )
			return;

		RememberSnapVariantForSelected();
		_selectedPieceId = null;
		_selectedData = null;
		_prefabTemplate = null;
		_placingSnaps.Clear();
		DestroyPreview();
	}

	void RefreshSceneSnapPoints()
	{
		var scene = ResolveScene();
		if ( !scene.IsValid() )
			return;

		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is not null && piece.IsValid() && !piece.IsPreviewGhost )
				piece.RefreshSnapPoints();
		}
	}

	void RefreshPlacingSnaps()
	{
		_placingSnaps.Clear();
		if ( _selectedData is null )
			return;

		BuildSnapDefaults.EnsureDefaults( _selectedData );
		if ( _selectedData.SnapPoints.Count == 0 )
			return;

		for ( var i = 0; i < _selectedData.SnapPoints.Count; i++ )
		{
			var snap = BuildSnapParse.FromData( _selectedData.SnapPoints[i] );
			if ( snap.Role == BuildSnapRole.Unknown )
				continue;

			_placingSnaps.Add( snap );
		}
	}

	void PollBuildMenuInput()
	{
		if ( string.IsNullOrWhiteSpace( OpenMenuAction ) )
			return;

		// Quick tap/release → build menu. Hold on a piece → demolish (no accidental deletes).
		if ( Input.Pressed( OpenMenuAction ) )
		{
			_openMenuHeld = true;
			_demolishedThisHold = false;
			_suppressBuildMenuToggle = false;
			_openMenuHoldStarted = Time.NowDouble;
			return;
		}

		if ( _openMenuHeld && Input.Down( OpenMenuAction ) )
		{
			if ( !_demolishedThisHold
			     && !IsBuildMenuOpen
			     && (Time.NowDouble - _openMenuHoldStarted) >= Math.Max( 0.05f, DemolishHoldSeconds )
			     && TryDeleteLookedAtBuildPiece() )
			{
				_demolishedThisHold = true;
				_suppressBuildMenuToggle = true;
			}

			return;
		}

		if ( !_openMenuHeld && !Input.Released( OpenMenuAction ) )
			return;

		_openMenuHeld = false;

		if ( _suppressBuildMenuToggle || _demolishedThisHold )
		{
			_suppressBuildMenuToggle = false;
			_demolishedThisHold = false;
			return;
		}

		SetBuildMenuOpen( !IsBuildMenuOpen );
	}

	void PollHammerInput()
	{
		if ( !IsPlacingPiece )
			return;

		if ( !IsRepairMode )
		{
			// Hardcoded Q/E so snap cycle isn't lost if Use/Menu share those keys.
			if ( Input.Pressed( SnapPrevAction ) || Input.Keyboard.Pressed( "q" ) )
				_snapAnchorIndex--;

			if ( Input.Pressed( SnapNextAction ) || Input.Keyboard.Pressed( "e" ) )
				_snapAnchorIndex++;

			if ( IsPreviewingPlacePiece )
			{
				var scroll = Input.MouseWheel.y;
				if ( Math.Abs( scroll ) > 0.01f )
				{
					// Hardcoded physical Ctrl for fine rotate — not a rebindable action. Shift used to
					// do this and collided with sprint.
					var fine = Input.Keyboard.Down( "ctrl" )
					           || Input.Keyboard.Down( "leftctrl" )
					           || Input.Keyboard.Down( "rightctrl" )
					           || Input.Keyboard.Down( "leftcontrol" )
					           || Input.Keyboard.Down( "rightcontrol" );
					var step = fine ? FineYawStep : ScrollYawStep;
					_yawDegrees += scroll > 0f ? step : -step;
				}
			}
		}

		if ( Input.Released( PlaceAction ) )
		{
			if ( IsRepairMode )
				TryRepairLookedAtBuildPiece();
			else
			{
				TryPlaceSelectedPiece();
				RememberSnapVariantForSelected();
			}
		}
	}

	void UpdatePreview()
	{
		if ( _selectedData is null || string.IsNullOrWhiteSpace( _selectedPieceId ) )
		{
			DestroyPreview();
			return;
		}

		if ( IsRepairMode )
		{
			DestroyPreview();
			_lockedSnapGroup = null;
			return;
		}

		if ( !BuildPlacementUtility.TryGetViewRay( Pawn, out var origin, out var direction ) )
			return;

		var scene = ResolveScene();
		if ( !scene.IsValid() )
			return;

		_lastPlacement = BuildPlacementUtility.ComputePlacement(
			_selectedData,
			_placingSnaps,
			scene,
			Pawn,
			_previewRoot,
			origin,
			direction,
			_yawDegrees,
			BuildRange,
			_snapAnchorIndex,
			_lockedSnapGroup );

		if ( _lastPlacement.SnappedToStructure && _lastPlacement.ActiveSnapGroup is { } activeGroup )
		{
			// Keep Q/E index across seams and places of the same piece type (1234 sticky).
			_lockedSnapGroup = activeGroup;
		}
		else if ( !_lastPlacement.SnappedToStructure )
		{
			// Do not clear lock here when sticky snap dropped for one frame — sticky in
			// ComputePlacement keeps SnappedToStructure while aim stays near the seam.
			_lockedSnapGroup = null;
		}

		var variantCount = Math.Max( 0, _lastPlacement.SnapCandidateCount );
		if ( variantCount != _lastSnapAnchorCount )
			_lastSnapAnchorCount = variantCount;

		if ( variantCount > 0 )
		{
			// Wrap Q/E instead of clamping to 0 when count flickers.
			_snapAnchorIndex %= variantCount;
			if ( _snapAnchorIndex < 0 )
				_snapAnchorIndex += variantCount;

			RememberSnapVariantForSelected();
		}
		// Keep last index while free-aiming so the next same-type snap restores 1234.

		_activeSnapCandidate = _lastPlacement.SnapCandidate;

		_previewValid = _lastPlacement.IsValid;
		EnsurePreviewObject( scene );
		BuildPrefabUtility.ApplyStandardPieceTransform(
			_previewRoot,
			_selectedPieceId,
			new Transform( _lastPlacement.Position, _lastPlacement.Rotation ) );
		UpdatePreviewVisual();

		if ( ShowBuildRayDebug )
			BuildSnapDebug.DrawPlacementRay( _lastPlacement, DrawRayLine, DrawSnapMark );

		BuildSnapDebug.LogEdgeRejects = ShowSnapDebug;
		if ( ShowSnapDebug )
			DrawSnapMarks( scene );
	}

	void DrawSnapMarks( Scene scene )
	{
		var focus = Pawn.IsValid() ? Pawn.WorldPosition : _lastPlacement.Position;
		var drawRadius = BuildRange + BuildModuleDimensions.SnapModuleHalfUnits * 2f;

		foreach ( var piece in scene.GetAllComponents<BuildPiece>() )
		{
			if ( piece is null || !piece.IsValid() || piece.IsPreviewGhost )
				continue;

			if ( _previewRoot.IsValid() && piece.GameObject == _previewRoot )
				continue;

			if ( Vector3.DistanceBetween( focus, piece.GameObject.WorldPosition ) > drawRadius )
				continue;

			BuildSnapDebug.DrawPieceSnapPoints( piece, BuildSnapDebug.DefaultColor, _activeSnapCandidate, isPreview: false, DrawSnapMark );
		}

		if ( _previewRoot is { IsValid: true } )
		{
			var previewPiece = _previewRoot.Components.Get<BuildPiece>();
			if ( previewPiece is not null )
				BuildSnapDebug.DrawPieceSnapPoints( previewPiece, BuildSnapDebug.PreviewColor, _activeSnapCandidate, isPreview: true, DrawSnapMark );
		}

		var placement = new Transform( _lastPlacement.Position, _lastPlacement.Rotation );
		BuildSnapDebug.DrawPlacingSnapPoints( _placingSnaps, placement, _selectedPieceId, DrawSnapMark );
	}

	void DrawSnapMark( Vector3 position, Color color, float radius ) =>
		DebugOverlay.Sphere( new Sphere( position, radius ), color, 0f );

	void DrawRayLine( Vector3 start, Vector3 end, Color color ) =>
		DebugOverlay.Line( start, end, color, 0f );

	void EnsurePreviewObject( Scene scene )
	{
		if ( _previewRoot is { IsValid: true } )
			return;

		_previewRoot = BuildPrefabUtility.CreatePreviewClone( scene, _selectedData, _prefabTemplate );
		if ( _previewRoot is null || !_previewRoot.IsValid() )
			return;

		var previewPiece = _previewRoot.Components.Get<BuildPiece>() ?? _previewRoot.Components.Create<BuildPiece>();
		previewPiece.Configure( _selectedPieceId, BlueprintModeEnabled, previewGhost: true );
	}

	void UpdatePreviewVisual()
	{
		if ( _previewRoot is null || !_previewRoot.IsValid() )
			return;

		var piece = _previewRoot.Components.Get<BuildPiece>();
		if ( piece is null )
			return;

		if ( piece.IsBlueprint != BlueprintModeEnabled )
			piece.Configure( _selectedPieceId, BlueprintModeEnabled, previewGhost: true );

		piece.SetPreviewValid( _previewValid );
	}

	void TryPlaceSelectedPiece()
	{
		if ( _selectedData is null )
			return;

		if ( !_previewValid || _previewRoot is null || !_previewRoot.IsValid() )
		{
			if ( LogBuildMode )
				Log.Info( "[ToolBuildHammer] Place rejected: invalid preview." );
			return;
		}

		var transform = new Transform( _lastPlacement.Position, _lastPlacement.Rotation );
		var pieceId = _selectedPieceId;
		var equipment = ResolveEquipment();
		if ( equipment is null )
		{
			if ( LogBuildMode )
				Log.Warning( "[ToolBuildHammer] Place failed — no PlayerEquipment on pawn." );
			return;
		}

		equipment.OwnerRequestPlacePiece( pieceId, transform.Position, transform.Rotation, BlueprintModeEnabled );
		if ( LogBuildMode )
			Log.Info( $"[ToolBuildHammer] Place requested '{pieceId}'." );
	}

	void TryRepairLookedAtBuildPiece()
	{
		if ( !BuildPlacementUtility.TryGetViewRay( Pawn, out var origin, out var direction ) )
			return;

		var scene = ResolveScene();
		if ( !BuildPlacementUtility.TryTraceBuildPiece( scene, Pawn, _previewRoot, origin, direction, BuildRange, out var piece ) )
			return;

		var equipment = ResolveEquipment();
		if ( equipment is null )
			return;

		equipment.OwnerRequestRepairBuildPiece( piece.GameObject.Id );
		if ( LogBuildMode )
			Log.Info( $"[ToolBuildHammer] Repair requested '{piece.PieceId}'." );
	}

	bool TryDeleteLookedAtBuildPiece()
	{
		if ( !BuildPlacementUtility.TryGetViewRay( Pawn, out var origin, out var direction ) )
			return false;

		var scene = ResolveScene();
		if ( !BuildPlacementUtility.TryTraceBuildPiece( scene, Pawn, _previewRoot, origin, direction, BuildRange, out var piece ) )
			return false;

		var equipment = ResolveEquipment();
		if ( equipment is null )
			return false;

		equipment.OwnerRequestDestroyBuildPiece( piece.GameObject.Id );
		return true;
	}

	PlayerEquipment ResolveEquipment()
	{
		var pawn = Pawn;
		return pawn.IsValid() ? pawn.Components.Get<PlayerEquipment>() : null;
	}

	Scene ResolveScene() => Pawn.Scene.IsValid() ? Pawn.Scene : Sandbox.Game.ActiveScene;

	void DestroyPreview()
	{
		if ( _previewRoot is { IsValid: true } )
			_previewRoot.Destroy();

		_previewRoot = null;
		_previewValid = false;
		_activeSnapCandidate = null;
	}

	void ResolvePawnComponents()
	{
		if ( _pawn is null || !_pawn.IsValid() )
			return;

		_vitals = _pawn.Components.Get<PlayerVitals>();
		_menu = _pawn.Components.Get<PlayerGameMenuController>();
	}

	GameObject Pawn => _pawn is { IsValid: true } ? _pawn : GameObject;

	bool IsLocalDriver()
	{
		if ( _pawn is null || !_pawn.IsValid() )
			ResolvePawnComponents();

		if ( _vitals is null && _pawn is { IsValid: true } )
			_vitals = _pawn.Components.Get<PlayerVitals>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
	}
}
