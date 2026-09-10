using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hoe tool (spawned from <c>prefabs/tools/hoe_tool.prefab</c> while a <c>Till</c> item is in the
/// active hotbar slot). Aim at dirt → a translucent square shows the grid cell that Attack1 will
/// turn into tilled soil; red when the aim point cannot be tilled (not dirt, too steep, already
/// tilled, out of reach). Aim at a plant → Attack1 uproots it for a seed. The host re-validates in
/// <see cref="FarmingAuthority"/>; the HUD prompt for uprooting comes from <see cref="PlayerFarming"/>.
/// </summary>
[Title( "Tool Hoe" )]
public sealed class ToolHoe : Component
{
	[Property, Group( "Input" )] public string TillAction { get; set; } = "Attack1";

	[Property, Group( "Tilling" ), Title( "Reach (m)" ), Range( 1f, 10f ), Step( 0.5f )]
	public float ReachMeters { get; set; } = 4f;

	[Property, Group( "Debug" )] public bool LogTilling { get; set; }

	/// <summary>True while the square preview is up (other E/click systems yield to the hoe).</summary>
	public bool IsPreviewing => _preview is { IsValid: true } && _preview.Enabled;

	/// <summary>Plant under the aim this frame — Attack1 uproots it instead of tilling.</summary>
	public FarmPlant FocusedPlant { get; private set; }

	GameObject _pawn;
	PlayerVitals _vitals;
	PlayerGameMenuController _menu;
	GameObject _preview;
	ModelRenderer _previewRenderer;
	bool _previewValid;
	Vector3 _previewCenter;

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

		if ( _menu is not null && _menu.IsMenuOpen )
		{
			HidePreview();
			FocusedPlant = null;
			return;
		}

		UpdatePreview();

		if ( !Input.Pressed( TillAction ) )
			return;

		if ( FocusedPlant is not null && FocusedPlant.IsValid() )
			RequestUproot();
		else if ( _previewValid )
			RequestTill();
	}

	void UpdatePreview()
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
