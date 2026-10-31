using System.Threading.Tasks;

namespace Survival;

/// <summary>
/// The horizon tier (Mark 2026-10-22: "a weird fov glitch when looking at the shores from high enough up"): the far
/// tier stops at Far View Distance (8 km), so from any height the land between there and the coast was simply not
/// drawn and the rim ocean, 20 km out, showed through the gap as a flat sheet of water cutting the land off. Four
/// render-only quadrants cover the whole world disc (<see cref="HorizonVerticesPerSide"/> per side ≈ 350 m steps),
/// sampled once per load on workers like the far chunks. Inside the far tier they sink out of sight on the GPU
/// (pixel_lit F_FAR_SINK, the scene-wide HorizonSinkCircle on tier-2 vertices: 300 m down well inside Far View Distance, easing to the true
/// height just past it), so the two never fight and the horizon carries on where the far tier ends.
/// </summary>
public sealed partial class TerrainWorldManager
{
	const int HorizonVerticesPerSide = 65;

	readonly Task<FarSample>[] _horizonPending = new Task<FarSample>[4];
	readonly ModelRenderer[] _horizonRenderers = new ModelRenderer[4];
	bool _horizonStarted;

	void TickHorizonTerrain( TerrainPreviewSettings settings, Vector3 streamPosEngine )
	{
		var worldRadius = settings.TotalWorldRadiusMeters;
		var size = worldRadius;   // 2 × 2 quadrants: coords 0..1 from -radius
		if ( !_horizonStarted )
		{
			_horizonStarted = true;
			var backend = _backend ?? TerrainPreviewBackendRegistry.Active;
			var options = BuildVegetationOptions();
			var tint = FarCanopyTint01;
			for ( var k = 0; k < 4; k++ )
			{
				var coord = new TerrainChunkCoord( k % 2, k / 2 );
				_horizonPending[k] = GameTask.RunInThreadAsync( () => SampleFarChunk( coord, settings, size, backend, options, tint, HorizonVerticesPerSide, false, 0f, Math.Max( 32f, ChunkSizeMeters ) ) );
			}
		}

		for ( var k = 0; k < 4; k++ )
		{
			var task = _horizonPending[k];
			if ( task is null || !task.IsCompleted )
				continue;
			_horizonPending[k] = null;
			var sampled = task.IsCompletedSuccessfully ? task.Result : null;
			if ( sampled?.Geometry is null )
			{
				Log.Warning( $"[TerrainWorldManager] horizon quadrant {k} failed: {(task.IsFaulted ? task.Exception?.GetBaseException().Message : "no geometry")}" );
				continue;
			}
			var built = TerrainMeshBuilder.BuildModelFromGeometry( sampled.Geometry, size, MaxTerrainHeightMeters, withCollision: false, sinkMeters: 0f, farTier: true, horizonTier: true );
			if ( built.Model is null || !built.Model.IsValid )
				continue;
			var coord = new TerrainChunkCoord( k % 2, k / 2 );
			var go = new GameObject( true, $"TerrainHorizon {coord}" );
			go.Flags |= GameObjectFlags.NotSaved;
			go.Parent = GameObject;
			go.WorldPosition = TerrainWorldUnits.MetersToEngine( new Vector3( -worldRadius + (coord.X * size), -worldRadius + (coord.Y * size), 0f ) );
			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = built.Model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_horizonRenderers[k] = renderer;
			var g = sampled.Geometry;
			Log.Info( $"[TerrainWorldManager] horizon quadrant {coord}: {g.VerticesPerSide}² vertices from {g.ChunkMinX:0},{g.ChunkMinY:0} m, step {g.Step:0} m, heights {g.Heights.Min():0}..{g.Heights.Max():0} m" );
		}

		// Buried under the far tier: x, y = the far tier's centre, z = Far View Distance (engine units). Scene-wide —
		// pixel_lit applies it to tier-2 vertices only (a per-renderer attribute never reached the vertex stage)
		Scene.RenderAttributes.Set( "HorizonSinkCircle", new Vector4( streamPosEngine.x, streamPosEngine.y, TerrainWorldUnits.MetersToEngine( Math.Max( 1000f, FarViewDistanceMeters ) ), 0f ) );
	}

	/// <summary>Usage: <c>terrain_horizon 0|1</c> — hide / show the horizon tier (A/B against the far tier).</summary>
	[ConCmd( "terrain_horizon" )]
	public static void ConCmdHorizon( int on = 1 )
	{
		var scene = global::Sandbox.Game.ActiveScene;
		var manager = scene.IsValid() ? scene.GetAllComponents<TerrainWorldManager>().FirstOrDefault() : null;
		if ( manager is null )
			return;
		foreach ( var renderer in manager._horizonRenderers )
		{
			if ( renderer is not null && renderer.IsValid() )
				renderer.Enabled = on != 0;
		}
		Log.Info( $"[TerrainWorldManager] horizon tier {(on != 0 ? "shown" : "hidden")}" );
	}

	void UnloadHorizonTerrain()
	{
		for ( var k = 0; k < 4; k++ )
		{
			_horizonPending[k] = null;
			var renderer = _horizonRenderers[k];
			if ( renderer is not null && renderer.IsValid() )
				renderer.GameObject.Destroy();
			_horizonRenderers[k] = null;
		}
		_horizonStarted = false;
	}
}
