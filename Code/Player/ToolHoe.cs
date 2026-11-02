using System;
using Sandbox;

namespace Survival;

/// <summary>What the hoe does to the ground on Attack1 - picked from its menu (Attack2), like a build piece.</summary>
public enum HoeMode
{
	/// <summary>Till the ground: on the world's voxel ground a painted circle / square of dark tilled soil (sowing needs it, no grass); on a dirt-tagged plane the old 1 m tile. Aim at a plant to uproot it for a seed.</summary>
	Till = 0,
	/// <summary>Paint the circle as the biome's grass: the biome ground, no sand or rock, grass clutter where the biome has it.</summary>
	Grass = 1,
	/// <summary>Paint the circle bare: the soil tile, no grass clutter.</summary>
	Bare = 2,
	/// <summary>The grading tool (the rake): level the circle to the surrounding ground - flat, or on the surrounding slope (F swaps).</summary>
	Grade = 3,
	/// <summary>Raise the ground inside the circle by one thin layer of dirt, paid in dirt (Mark 2026-11-02: "a way to build upward a little bit").</summary>
	Raise = 4,
	/// <summary>Pave the circle with cobblestone (the rock tile, no grass), one stone per stroke (Mark 2026-11-02).</summary>
	Cobble = 5,
}

/// <summary>
/// Hoe tool (spawned from <c>prefabs/tools/hoe_tool.prefab</c> while a <c>Till</c> item is in the active hotbar slot).
/// Attack2 opens its menu (<see cref="HoeMenuHud"/>, the build menu's pattern) with four ground modes (<see cref="HoeMode"/>):
/// <b>Tilled soil</b> - aim at ground: a translucent square shows the grid cell Attack1 turns into tilled soil, red when it
/// cannot be tilled (not ground, too steep, already tilled, out of reach); aim at a plant: Attack1 uproots it for a seed
/// (host: <see cref="FarmingAuthority"/>). <b>Grass</b> / <b>Bare ground</b> - a ring on the ground shows the circle Attack1
/// paints (hold to keep painting; F swaps a round patch for a square one snapped to the voxel grid). <b>Grade</b> - the rake from the voxel scenes: a ring on the plane it will lay (blue flat,
/// orange slope, red = nothing to level), Attack1 lays it, F swaps flat / slope. The ground strokes go through
/// <see cref="PlayerTerraform"/> to the scene's voxel ground; the HUD prompt for uprooting comes from <see cref="PlayerFarming"/>.
/// </summary>
[Title( "Tool Hoe" )]
public sealed class ToolHoe : Component
{
	[Property, Group( "Input" ), Title( "Use Action" )] public string TillAction { get; set; } = "Attack1";
	[Property, Group( "Input" ), Title( "Open Menu Action" )] public string OpenMenuAction { get; set; } = "Attack2";
	[Property, Group( "Input" ), Title( "Swap Action" ), Description( "Grade mode: swaps the rake between flat and slope. Grass / Bare ground: swaps the patch between a circle and a square." )]
	public string GradeModeAction { get; set; } = "Flashlight";

	[Property, Group( "Tilling" ), Title( "Reach (m)" ), Range( 1f, 10f ), Step( 0.5f )]
	public float ReachMeters { get; set; } = 4f;

	[Property, Group( "Ground" ), Title( "Paint Radius (m)" ), Range( 0.5f, 4f ), Step( 0.25f ), Description( "The circle one grass / bare stroke paints." )]
	public float PaintRadiusMeters { get; set; } = 1.5f;

	[Property, Group( "Ground" ), Title( "Paint Interval (s)" ), Range( 0.05f, 1f ), Step( 0.05f ), Description( "Holding the button paints again this often." )]
	public float PaintSeconds { get; set; } = 0.25f;

	[Property, Group( "Ground" ), Title( "Grade Radius (m)" ), Range( 0.5f, 6f ), Step( 0.25f ), Description( "The circle one grade stroke levels." )]
	public float GradeRadiusMeters { get; set; } = 2.5f;

	[Property, Group( "Ground" ), Title( "Raise Layer (m)" ), Range( 0.05f, 1f ), Step( 0.05f ), Description( "How thick the layer of dirt one Raise stroke adds inside the paint circle." )]
	public float RaiseLayerMeters { get; set; } = 0.25f;

	[Property, Group( "Debug" )] public bool LogTilling { get; set; }

	/// <summary>The mode picked in the menu; Till until the player picks another.</summary>
	public HoeMode Mode { get; private set; } = HoeMode.Till;

	/// <summary>Grade mode: lay the surrounding slope (true) or a horizontal plane (false).</summary>
	public bool GradeSlope { get; private set; }

	/// <summary>Grass / Bare ground: paint an axis-aligned square patch (side 2 x Paint Radius, snapped to the 0.5 m voxel grid) instead of a circle (Mark 2026-11-02: "a perfect square patch").</summary>
	public bool SquarePatch { get; private set; }

	public bool IsMenuOpen { get; private set; }

	public event Action MenuOpenChanged;
	public event Action ModeChanged;

	/// <summary>True while the till square preview is up (other E/click systems yield to the hoe).</summary>
	public bool IsPreviewing => _preview is { IsValid: true } && _preview.Enabled;

	/// <summary>Plant under the aim this frame (Till mode) — Attack1 uproots it instead of tilling.</summary>
	public FarmPlant FocusedPlant { get; private set; }

	static readonly Color GrassRingColor = new( 0.35f, 0.85f, 0.3f );
	static readonly Color BareRingColor = new( 0.62f, 0.45f, 0.28f );
	static readonly Color TilledRingColor = new( 0.40f, 0.26f, 0.14f );
	static readonly Color RaiseRingColor = new( 0.90f, 0.70f, 0.32f );
	static readonly Color CobbleRingColor = new( 0.62f, 0.62f, 0.64f );

	GameObject _pawn;
	PlayerVitals _vitals;
	PlayerGameMenuController _menu;
	PlayerTerraform _terraform;
	GameObject _preview;
	ModelRenderer _previewRenderer;
	bool _previewValid;
	Vector3 _previewCenter;
	double _nextPaintAt;

	public void BindPawn( GameObject pawn ) => _pawn = pawn;

	protected override void OnDestroy()
	{
		DestroyPreview();
		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsLocalDriver() )
			return;

		// Dev: console `swing` holds the attack action for a few frames (GameHacks) - the tool may update before PlayerCombat does
		GameHacks.ApplySimulatedInput( TillAction );

		if ( _menu is not null && _menu.IsMenuOpen )
		{
			HidePreview();
			FocusedPlant = null;
			if ( IsMenuOpen )
				SetMenuOpen( false );
			return;
		}

		// Tap Attack2: open the picker; tap again: close it (the build hammer's tap, without its demolish hold)
		if ( !string.IsNullOrWhiteSpace( OpenMenuAction ) && Input.Pressed( OpenMenuAction ) )
			SetMenuOpen( !IsMenuOpen );

		if ( IsMenuOpen )
		{
			HidePreview();
			FocusedPlant = null;
			return;
		}

		// Till on the world's ground paints tilled soil like bare ground does; on a dirt plane (testscene1) it is still the 1 m tile
		_terraform ??= Pawn.Components.Get<PlayerTerraform>();
		if ( Mode == HoeMode.Till && !(_terraform?.Target is { IsReady: true } && TryTraceGround( out _ )) )
		{
			UpdateTillPreview();
			if ( !Input.Pressed( TillAction ) )
				return;
			if ( FocusedPlant is not null && FocusedPlant.IsValid() )
				RequestUproot();
			else if ( _previewValid )
				RequestTill();
			return;
		}

		HidePreview();
		FocusedPlant = null;
		TickGroundMode();
	}

	// ---- menu

	public void SetMenuOpen( bool open )
	{
		if ( IsMenuOpen == open )
			return;
		IsMenuOpen = open;
		if ( open )
			HidePreview();
		MenuOpenChanged?.Invoke();
	}

	/// <summary>The menu's pick: switch mode and close the picker.</summary>
	public void SelectMode( HoeMode mode )
	{
		if ( Mode != mode )
		{
			Mode = mode;
			ModeChanged?.Invoke();
		}
		SetMenuOpen( false );
	}

	public void ToggleGradeSlope()
	{
		GradeSlope = !GradeSlope;
		ModeChanged?.Invoke();
	}

	public void ToggleSquarePatch()
	{
		SquarePatch = !SquarePatch;
		ModeChanged?.Invoke();
	}

	/// <summary>One line for the menu / HUD: the mode and, for Grade, flat or slope.</summary>
	public static string ModeTitle( HoeMode mode ) => mode switch
	{
		HoeMode.Grass => "Grass",
		HoeMode.Bare => "Bare ground",
		HoeMode.Grade => "Grade",
		HoeMode.Raise => "Raise ground",
		HoeMode.Cobble => "Cobblestone",
		_ => "Tilled soil",
	};

	public static string ModeDescription( HoeMode mode ) => mode switch
	{
		HoeMode.Grass => "Paint the ground as this biome's grass.\nHold LMB to keep painting; F swaps circle / square.",
		HoeMode.Bare => "Strip the ground bare: soil, no grass.\nHold LMB to keep painting; F swaps circle / square.",
		HoeMode.Grade => "Level the circle to the surrounding ground.\nLMB lays it; F swaps flat / slope.",
		HoeMode.Raise => "Add one thin layer of dirt inside the circle (costs dirt).\nLMB per layer; F swaps circle / square.",
		HoeMode.Cobble => "Pave the circle with cobblestone (costs 1 stone).\nLMB per patch; F swaps circle / square.",
		_ => "Till the ground: dark soil for sowing seeds, no grass.\nHold LMB; F swaps circle / square. Aim at a plant to uproot it (+1 seed).",
	};

	// ---- dev console

	/// <summary>Usage: <c>hoe_mode &lt;till|grass|bare|grade|raise|cobble&gt;</c> - set the local pawn's hoe mode without the menu (dev / tests).</summary>
	[ConCmd( "hoe_mode" )]
	public static void ConCmdHoeMode( string mode = "till" )
	{
		var hoe = FindLocalHoe();
		if ( hoe is null )
			return;
		var picked = (mode ?? string.Empty).Trim().ToLowerInvariant() switch
		{
			"grass" => HoeMode.Grass,
			"bare" => HoeMode.Bare,
			"grade" => HoeMode.Grade,
			"raise" => HoeMode.Raise,
			"cobble" => HoeMode.Cobble,
			_ => HoeMode.Till,
		};
		hoe.SelectMode( picked );
		Log.Info( $"[ToolHoe] mode {ModeTitle( hoe.Mode )}" );
	}

	/// <summary>Usage: <c>hoe_menu</c> - open / close the local pawn's hoe menu (dev: screenshots).</summary>
	[ConCmd( "hoe_menu" )]
	public static void ConCmdHoeMenu()
	{
		var hoe = FindLocalHoe();
		if ( hoe is null )
			return;
		hoe.SetMenuOpen( !hoe.IsMenuOpen );
		Log.Info( $"[ToolHoe] menu {(hoe.IsMenuOpen ? "open" : "closed")}" );
	}

	static ToolHoe FindLocalHoe()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;
		foreach ( var hoe in scene.GetAllComponents<ToolHoe>() )
		{
			if ( hoe is not null && hoe.IsValid() && hoe.IsLocalDriver() )
				return hoe;
		}
		Log.Warning( "[ToolHoe] no hoe in the local pawn's hand (equip tool_hoe)." );
		return null;
	}

	// ---- ground modes: grass / bare paint, grade

	void TickGroundMode()
	{
		_terraform ??= Pawn.Components.Get<PlayerTerraform>();
		var target = _terraform?.Target;
		if ( target is null || !target.IsReady )
			return;

		if ( !string.IsNullOrWhiteSpace( GradeModeAction ) && Input.Pressed( GradeModeAction ) )
		{
			if ( Mode == HoeMode.Grade )
				ToggleGradeSlope();
			else
				ToggleSquarePatch();
		}

		if ( !TryTraceGround( out var tr ) )
		{
			if ( LogTilling && Input.Down( TillAction ) )
				Log.Info( $"[ToolHoe] {Mode}: no world ground under the aim within reach (a tilled tile, prop or piece does not take a ground stroke)" );
			return;
		}
		if ( LogTilling && Input.Down( TillAction ) )
			Log.Info( $"[ToolHoe] {Mode}: aim on {tr.GameObject?.Name} at {tr.Distance:0} u, pressed {Input.Pressed( TillAction )}" );

		switch ( Mode )
		{
			case HoeMode.Grass:
			case HoeMode.Bare:
			case HoeMode.Till:
			{
				var (paint, color) = Mode switch
				{
					HoeMode.Grass => (VoxelBrushMode.PaintGrass, GrassRingColor),
					HoeMode.Bare => (VoxelBrushMode.PaintBare, BareRingColor),
					_ => (VoxelBrushMode.PaintTilled, TilledRingColor),
				};
				var affordable = _terraform.CanAffordStroke( paint, out _ );
				DrawPatch( tr.HitPosition, affordable ? color : VoxelBrushRings.BlockedColor );
				var pressed = Input.Pressed( TillAction );
				var held = Input.Down( TillAction ) && Time.NowDouble >= _nextPaintAt;
				if ( (!pressed && !held) || !affordable )
					return;
				_nextPaintAt = Time.NowDouble + Math.Max( 0.05f, PaintSeconds );
				RequestGround( tr.HitPosition, PaintRadiusMeters, paint, SquarePatch );
				return;
			}
			case HoeMode.Raise:
			{
				// One layer per press - the dirt it costs should be a deliberate spend, not a held stream
				var affordable = _terraform.CanAffordStroke( VoxelBrushMode.Raise, out _ );
				DrawPatch( tr.HitPosition, affordable ? RaiseRingColor : VoxelBrushRings.BlockedColor );
				if ( !Input.Pressed( TillAction ) || !affordable )
					return;
				RequestGround( tr.HitPosition, PaintRadiusMeters, VoxelBrushMode.Raise, SquarePatch, RaiseLayerMeters );
				return;
			}
			case HoeMode.Cobble:
			{
				// One patch per press, like Raise: every stroke costs a stone
				var affordable = _terraform.CanAffordStroke( VoxelBrushMode.PaintCobble, out _ );
				DrawPatch( tr.HitPosition, affordable ? CobbleRingColor : VoxelBrushRings.BlockedColor );
				if ( !Input.Pressed( TillAction ) || !affordable )
					return;
				RequestGround( tr.HitPosition, PaintRadiusMeters, VoxelBrushMode.PaintCobble, SquarePatch );
				return;
			}
			case HoeMode.Grade:
			{
				var affordable = _terraform.CanAffordStroke( VoxelBrushMode.Flatten, out _ );
				var planned = VoxelBrushRings.DrawRakeRing( Pawn.Scene, target, tr.HitPosition, GradeRadiusMeters, GradeSlope ) && affordable;
				if ( !Input.Pressed( TillAction ) || !planned )
					return;
				RequestGround( tr.HitPosition, GradeRadiusMeters, GradeSlope ? VoxelBrushMode.Slope : VoxelBrushMode.Flatten );
				return;
			}
		}
	}

	/// <summary>The paint / raise footprint on the ground: a circle, or the grid-snapped square.</summary>
	void DrawPatch( Vector3 hitEngine, Color color )
	{
		if ( SquarePatch )
			VoxelBrushRings.DrawSurfaceSquare( Pawn.Scene, hitEngine, PaintRadiusMeters, color );
		else
			VoxelBrushRings.DrawSurfaceRing( Pawn.Scene, hitEngine, Vector3.Up, PaintRadiusMeters, color );
	}

	bool TryTraceGround( out SceneTraceResult tr )
	{
		tr = default;
		if ( !BuildViewCamera.TryGetViewRay( Pawn, out var origin, out var direction ) )
			return false;
		var scene = Pawn.Scene.IsValid() ? Pawn.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ReachMeters ) );
		tr = FarmingRules.TraceView( scene, Pawn, origin, direction, reach );
		return tr.Hit && ToolPickaxe.IsGround( tr.GameObject );
	}

	void RequestGround( Vector3 hitEngine, float radiusMeters, VoxelBrushMode mode, bool square = false, float depth = 0f )
	{
		if ( ToolDurability.IsActiveToolBroken( Pawn ) )
		{
			if ( LogTilling )
				Log.Info( $"[ToolHoe] {mode} rejected: hoe broken — repair at a workbench." );
			return;
		}
		if ( _terraform is null )
		{
			Log.Warning( "[ToolHoe] Pawn has no PlayerTerraform component — add it to the player prefab." );
			return;
		}
		_terraform.OwnerRequestBrush( hitEngine, radiusMeters, mode, ReachMeters, square, depth );
		if ( LogTilling )
			Log.Info( $"[ToolHoe] {mode} r {radiusMeters:0.00} m at {TerrainWorldUnits.EngineToMeters( hitEngine )} m." );
	}

	// ---- till mode

	void UpdateTillPreview()
	{
		FocusedPlant = null;
		if ( !BuildViewCamera.TryGetViewRay( Pawn, out var origin, out var direction ) )
		{
			HidePreview();
			return;
		}

		var scene = Pawn.Scene.IsValid() ? Pawn.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
		{
			HidePreview();
			return;
		}

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, ReachMeters ) );
		var tr = FarmingRules.TraceView( scene, Pawn, origin, direction, reach );
		if ( !tr.Hit )
		{
			HidePreview();
			return;
		}

		if ( FarmPlant.TryFindOnHierarchy( tr.GameObject, out var plant ) )
		{
			FocusedPlant = plant;
			HidePreview();
			return;
		}

		_previewValid = FarmingRules.CanTillAt( tr, out var center, out _ );
		if ( !_previewValid )
			center = TilledSoil.SnapToGrid( tr.HitPosition );

		_previewCenter = new Vector3( center.x, center.y, tr.HitPosition.z );
		EnsurePreview( scene );
		_preview.Enabled = true;
		_preview.WorldPosition = _previewCenter + Vector3.Up * 1f;
		_previewRenderer.Tint = _previewValid ? FarmingRules.ValidGhostTint : FarmingRules.InvalidGhostTint;
	}

	void EnsurePreview( Scene scene )
	{
		if ( _preview is { IsValid: true } )
			return;

		// Dev box is 50 u across; the square is one tile wide and 2 u tall.
		var size = TilledSoil.TileSizeUnits;
		_preview = new GameObject( true, "hoe_preview" );
		_preview.Parent = scene;
		_preview.Tags.Add( "farmpreview" );
		_preview.LocalScale = new Vector3( size / 50f, size / 50f, 2f / 50f );
		_previewRenderer = _preview.Components.Create<ModelRenderer>();
		_previewRenderer.Model = Model.Load( "models/dev/box.vmdl" );
		_previewRenderer.Tint = FarmingRules.ValidGhostTint;
	}

	void HidePreview()
	{
		_previewValid = false;
		if ( _preview is { IsValid: true } )
			_preview.Enabled = false;
	}

	void DestroyPreview()
	{
		if ( _preview is { IsValid: true } )
			_preview.Destroy();
		_preview = null;
		_previewRenderer = null;
		_previewValid = false;
	}

	void RequestTill()
	{
		if ( ToolDurability.IsActiveToolBroken( Pawn ) )
		{
			if ( LogTilling )
				Log.Info( "[ToolHoe] Till rejected: hoe broken — repair at a workbench." );
			return;
		}

		var farming = ResolveFarming();
		if ( farming is null )
			return;

		farming.OwnerRequestTill( _previewCenter, ReachMeters );
		if ( LogTilling )
			Log.Info( $"[ToolHoe] Till requested at {_previewCenter}." );
	}

	void RequestUproot()
	{
		if ( ToolDurability.IsActiveToolBroken( Pawn ) )
		{
			if ( LogTilling )
				Log.Info( "[ToolHoe] Uproot rejected: hoe broken — repair at a workbench." );
			return;
		}

		var farming = ResolveFarming();
		if ( farming is null )
			return;

		farming.OwnerRequestUproot( FocusedPlant, ReachMeters );
		if ( LogTilling )
			Log.Info( $"[ToolHoe] Uproot requested on {FocusedPlant.PlantName}." );
	}

	PlayerFarming ResolveFarming()
	{
		var farming = Pawn.Components.Get<PlayerFarming>();
		if ( farming is null )
			Log.Warning( "[ToolHoe] Pawn has no PlayerFarming component — add it to the player prefab." );
		return farming;
	}

	GameObject Pawn => _pawn is { IsValid: true } ? _pawn : GameObject;

	bool IsLocalDriver()
	{
		if ( _pawn is null || !_pawn.IsValid() )
			return false;

		_vitals ??= _pawn.Components.Get<PlayerVitals>();
		_menu ??= _pawn.Components.Get<PlayerGameMenuController>();
		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
	}
}
