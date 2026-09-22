using Sandbox;
using Sandbox.Clutter;

namespace Survival;

/// <summary>
/// A rectangle of grass around this object, drawn by the engine Clutter system (GPU instanced,
/// frustum culled, LOD'd — zero per-frame C# cost). Owns the <see cref="ClutterComponent"/>
/// authored on the same GameObject: builds its <see cref="ClutterDefinition"/> from the
/// meter-based knobs below and kicks one generation on start. Test-bed for the world-wide
/// grass that will later stream with terrain chunks.
/// </summary>
[Title( "Grass Patch" )]
[Icon( "grass" )]
public sealed class GrassPatch : Component
{
	[Property, Group( "Patch" ), Title( "Size X (m)" ), Range( 1f, 200f ), Step( 1f )]
	public float SizeXMeters { get; set; } = 16f;

	[Property, Group( "Patch" ), Title( "Size Y (m)" ), Range( 1f, 200f ), Step( 1f )]
	public float SizeYMeters { get; set; } = 16f;

	[Property, Group( "Patch" ), Title( "Ground Search (m)" ), Range( 1f, 50f ), Step( 1f ), Description( "Ground is traced this far above and below the object." )]
	public float GroundSearchMeters { get; set; } = 5f;

	[Property, Group( "Patch" ), Title( "Seed" )]
	public int Seed { get; set; } = 1;

	[Property, Group( "Clumps" ), Title( "Clump Models" ), Description( "Picked at random per clump. grass_clump1–4 (7/5/9/3 blades) are the single-plane blade fans from Blender/scripts/create_grass_clump.py." )]
	public List<Model> ClumpModels { get; set; } = new();

	[Property, Group( "Clumps" ), Title( "Clumps Per m²" ), Range( 0.1f, 12f ), Step( 0.1f )]
	public float ClumpsPerSquareMeter { get; set; } = 3f;

	[Property, Group( "Clumps" ), Title( "Scale Min" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float ScaleMin { get; set; } = 0.8f;

	[Property, Group( "Clumps" ), Title( "Scale Max" ), Range( 0.25f, 3f ), Step( 0.05f )]
	public float ScaleMax { get; set; } = 1.25f;

	[Property, Group( "Clumps" ), Title( "Max Slope (degrees)" ), Range( 0f, 90f ), Step( 1f )]
	public float MaxSlopeDegrees { get; set; } = 50f;

	[Property, Group( "Clumps" ), Title( "Cast Shadows" ), Description( "Off by default — thousands of tiny cards in the shadow pass is the first thing to cost frames." )]
	public bool CastShadows { get; set; }

	ClutterComponent _clutter;
	bool _reported;

	protected override void OnStart()
	{
		base.OnStart();

		_clutter = Components.Get<ClutterComponent>();
		if ( _clutter is null || !_clutter.IsValid() )
		{
			Log.Warning( $"[GrassPatch] '{GameObject.Name}' needs a ClutterComponent on the same GameObject — add it in the scene." );
			return;
		}

		if ( ClumpModels is null || ClumpModels.Count == 0 )
		{
			Log.Warning( $"[GrassPatch] '{GameObject.Name}' has no clump models assigned." );
			return;
		}

		var definition = new ClutterDefinition
		{
			Entries = new List<ClutterEntry>(),
			Scatterer = new GrassScatterer
			{
				ClumpsPerSquareMeter = ClumpsPerSquareMeter,
				ScaleMin = ScaleMin,
				ScaleMax = ScaleMax,
				MaxSlopeDegrees = MaxSlopeDegrees,
			},
		};

		foreach ( var model in ClumpModels )
		{
			if ( model is null || !model.IsValid )
				continue;

			definition.Entries.Add( new ClutterEntry
			{
				Model = model,
				Weight = 1f,
				LocalScale = 1f,
				CastShadows = CastShadows,
				EnablePhysics = false,
			} );
		}

		if ( definition.Entries.Count == 0 )
		{
			Log.Warning( $"[GrassPatch] '{GameObject.Name}' clump models failed to load." );
			return;
		}

		var half = TerrainWorldUnits.MetersToEngine( new Vector3( SizeXMeters * 0.5f, SizeYMeters * 0.5f, GroundSearchMeters ) );
		var bounds = new BBox( -half, half );

		_clutter.Clutter = definition;
		_clutter.Mode = ClutterComponent.ClutterMode.Volume;
		_clutter.Seed = Seed;
		_clutter.Bounds = bounds;

		// Keep an editor-baked result if one exists; otherwise scatter now.
		var stored = _clutter.Storage?.TotalCount ?? 0;
		if ( stored > 0 )
		{
			Log.Info( $"[GrassPatch] '{GameObject.Name}' using {stored} stored clumps." );
			_reported = true;
			return;
		}

		_clutter.Generate();
		Log.Info( $"[GrassPatch] '{GameObject.Name}' generating {SizeXMeters:0.#}×{SizeYMeters:0.#} m at {WorldPosition} (expected world span {WorldPosition + bounds.Mins} … {WorldPosition + bounds.Maxs})." );
	}

	protected override void OnUpdate()
	{
		// One-off report so the first test tells us where the engine actually put the clumps.
		if ( _reported || _clutter is null || !_clutter.IsValid() )
			return;

		var storage = _clutter.Storage;
		if ( storage is null || storage.TotalCount == 0 )
			return;

		_reported = true;
		var counts = new List<string>();
		Vector3? first = null;
		foreach ( var (path, list) in storage.GetAllInstances() )
		{
			if ( list is null || list.Count == 0 )
				continue;

			counts.Add( $"{System.IO.Path.GetFileNameWithoutExtension( path )}×{list.Count}" );
			first ??= list[0].Position;
		}

		Log.Info( $"[GrassPatch] '{GameObject.Name}' spawned {storage.TotalCount} clumps [{string.Join( ", ", counts )}]; first at stored position {first} (object at {WorldPosition})." );
	}
}
