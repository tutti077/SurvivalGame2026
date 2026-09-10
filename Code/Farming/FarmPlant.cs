using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// A sown plant (host-spawned from <c>prefabs/farming/farm_plant.prefab</c> by
/// <see cref="FarmingAuthority.TrySow"/>). The host advances <see cref="Stage"/> on a timer from the
/// seed row's <c>matureSeconds</c>; every peer rebuilds the visual when the synced stage changes.
/// Single plants are removed by their one harvest (or bolt when left too long); bushes regrow after
/// each harvest. Visuals are placeholders (tinted dev shapes) until the seed row names <c>stageModels</c>.
/// </summary>
[Title( "Farm Plant" )]
public sealed class FarmPlant : Component
{
	static readonly List<FarmPlant> Active = new();

	public static IReadOnlyList<FarmPlant> All => Active;

	/// <summary>Seed resource id this plant grew from — resolves the <see cref="FarmPlantData"/> row.</summary>
	[Sync( SyncFlags.FromHost )]
	public string SeedId { get; set; } = string.Empty;

	[Sync( SyncFlags.FromHost )]
	public int StageIndex { get; set; }

	public FarmPlantStage Stage => (FarmPlantStage)Math.Clamp( StageIndex, 0, FarmPlantData.StageCount - 1 );

	public bool IsMatured => Stage == FarmPlantStage.Matured;

	public bool IsBolting => Stage == FarmPlantStage.Bolting;

	/// <summary>Matured or bolted — E does something here.</summary>
	public bool CanHarvest => IsMatured || IsBolting;

	public FarmPlantData Data =>
		ResourceDefinitionCatalog.TryGetSeed( SeedId, out var data ) ? data : null;

	public string PlantName
	{
		get
		{
			var data = Data;
			if ( data is not null && !string.IsNullOrWhiteSpace( data.PlantName ) )
				return data.PlantName;

			var seedName = ResourceCatalog.Resolve( SeedId ).DisplayName;
			return string.IsNullOrWhiteSpace( seedName ) ? "Plant" : seedName;
		}
	}

	double _growthStart;
	int _appliedStage = -1;
	string _appliedSeedId;
	GameObject _visual;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Active.Remove( this );
		DestroyVisual();
		base.OnDestroy();
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( _growthStart <= 0 )
			_growthStart = Time.NowDouble;
		ApplyVisualIfChanged();
	}

	/// <summary>Host: stamp the seed before NetworkSpawn so joiners get it with the object.</summary>
	public void HostConfigure( string seedId )
	{
		SeedId = ResourceCatalog.NormalizeResourceId( seedId ) ?? string.Empty;
		StageIndex = (int)FarmPlantStage.FreshlySown;
		_growthStart = Time.NowDouble;
	}

	/// <summary>
	/// Host: a bush was picked — wind growth back so it is matured again after <c>regrowSeconds</c>.
	/// Single plants never call this; their harvest destroys the object.
	/// </summary>
	public void HostOnBushHarvested( FarmPlantData data )
	{
		if ( !HasHostAuthority || data is null )
			return;

		var elapsedAfterReset = Math.Max( 0f, Math.Max( 0.1f, data.MatureSeconds ) - data.EffectiveRegrowSeconds );
		_growthStart = Time.NowDouble - elapsedAfterReset;
		StageIndex = (int)data.StageAt( elapsedAfterReset );
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( !HasHostAuthority || IsBolting )
			return;

		var data = Data;
		if ( data is null )
			return;

		if ( IsMatured && !data.CanBolt )
			return;

		var stage = data.StageAt( Time.NowDouble - _growthStart );
		if ( (int)stage != StageIndex )
			StageIndex = (int)stage;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		ApplyVisualIfChanged();
	}

	void ApplyVisualIfChanged()
	{
		if ( _appliedStage == StageIndex && string.Equals( _appliedSeedId, SeedId, StringComparison.OrdinalIgnoreCase ) && _visual is { IsValid: true } )
			return;

		_appliedStage = StageIndex;
		_appliedSeedId = SeedId;
		DestroyVisual();
		_visual = BuildStageVisual( GameObject, Data, Stage, tintOverride: null, previewGhost: false );
	}

	void DestroyVisual()
	{
		if ( _visual is { IsValid: true } )
			_visual.Destroy();
		_visual = null;
	}

	/// <summary>Any plant centre within <paramref name="radiusUnits"/> of <paramref name="point"/> (horizontal).</summary>
	public static bool AnyWithin( Vector3 point, float radiusUnits )
	{
		var r2 = radiusUnits * radiusUnits;
		for ( var i = 0; i < Active.Count; i++ )
		{
			var plant = Active[i];
			if ( plant is null || !plant.IsValid() || !plant.GameObject.IsValid() )
				continue;

			var d = plant.GameObject.WorldPosition - point;
			d.z = 0f;
			if ( d.LengthSquared <= r2 )
				return true;
		}

		return false;
	}

	/// <summary>Walk up from a trace hit to the owning plant.</summary>
	public static bool TryFindOnHierarchy( GameObject hit, out FarmPlant plant )
	{
		plant = null;
		for ( var go = hit; go.IsValid(); go = go.Parent )
		{
			var c = go.Components.Get<FarmPlant>();
			if ( c is null || !c.Enabled )
				continue;

			plant = c;
			return true;
		}

		return false;
	}

	/// <summary>Resolve a plant from the GameObject id a client sent in an RPC.</summary>
	public static bool TryResolve( Guid gameObjectId, out FarmPlant plant )
	{
		plant = null;
		for ( var i = 0; i < Active.Count; i++ )
		{
			var candidate = Active[i];
			if ( candidate is null || !candidate.IsValid() || !candidate.GameObject.IsValid() )
				continue;

			if ( candidate.GameObject.Id != gameObjectId )
				continue;

			plant = candidate;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Builds the visual child for one growth stage under <paramref name="parent"/>. Shared by the
	/// placed plant and the sow ghost (<see cref="PlayerFarming"/>) so the preview matches the result.
	/// </summary>
	public static GameObject BuildStageVisual( GameObject parent, FarmPlantData data, FarmPlantStage stage, Color? tintOverride, bool previewGhost )
	{
		if ( parent is null || !parent.IsValid() )
			return null;

		var visual = new GameObject( true, previewGhost ? "farm_preview" : "plant_visual" );
		visual.Parent = parent;
		visual.LocalPosition = Vector3.Zero;
		visual.LocalRotation = Rotation.Identity;
		if ( previewGhost )
			visual.Tags.Add( "farmpreview" );

		var plantColor = FarmingRules.ParseColor( data?.Color, new Color( 0.35f, 0.65f, 0.25f ) );
		var renderer = visual.Components.Create<ModelRenderer>();

		var customModel = data?.GetStageModel( stage );
		if ( !string.IsNullOrWhiteSpace( customModel ) )
		{
			var model = Model.Load( customModel );
			if ( model is not null && model.IsValid() )
			{
				renderer.Model = model;
				renderer.Tint = tintOverride ?? Color.White;
				return visual;
			}
		}

		// Placeholder shapes — dev box and sphere are 50 u across.
		const float devModelUnits = 50f;
		var matureHeight = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, data?.MatureHeightMeters ?? 0.3f ) );
		Vector3 size;
		Color tint;
		string modelPath;
		switch ( stage )
		{
			case FarmPlantStage.FreshlySown:
				modelPath = "models/dev/box.vmdl";
				size = new Vector3( TerrainWorldUnits.MetersToEngine( 0.15f ), TerrainWorldUnits.MetersToEngine( 0.15f ), TerrainWorldUnits.MetersToEngine( 0.03f ) );
				tint = new Color( 0.38f, 0.26f, 0.15f );
				break;
			case FarmPlantStage.Seedling:
				modelPath = "models/dev/sphere.vmdl";
				size = new Vector3( TerrainWorldUnits.MetersToEngine( 0.08f ) );
				tint = new Color( 0.45f, 0.75f, 0.3f );
				break;
			case FarmPlantStage.Juvenile:
				modelPath = "models/dev/box.vmdl";
				size = new Vector3( TerrainWorldUnits.MetersToEngine( 0.12f ), TerrainWorldUnits.MetersToEngine( 0.12f ), matureHeight * 0.5f );
				tint = new Color( 0.35f, 0.65f, 0.25f );
				break;
			case FarmPlantStage.Bolting:
				// Overgrown: taller, thinner, gone to seed (straw yellow).
				modelPath = "models/dev/box.vmdl";
				size = new Vector3( TerrainWorldUnits.MetersToEngine( 0.1f ), TerrainWorldUnits.MetersToEngine( 0.1f ), matureHeight * 1.6f );
				tint = new Color( 0.78f, 0.72f, 0.35f );
				break;
			default:
				modelPath = "models/dev/box.vmdl";
				size = new Vector3( TerrainWorldUnits.MetersToEngine( 0.2f ), TerrainWorldUnits.MetersToEngine( 0.2f ), matureHeight );
				tint = plantColor;
				break;
		}

		renderer.Model = Model.Load( modelPath );
		renderer.Tint = tintOverride ?? tint;
		visual.LocalScale = size / devModelUnits;
		// Dev shapes are centred — lift so the base sits on the soil.
		visual.LocalPosition = new Vector3( 0f, 0f, size.z * 0.5f );
		return visual;
	}
}
