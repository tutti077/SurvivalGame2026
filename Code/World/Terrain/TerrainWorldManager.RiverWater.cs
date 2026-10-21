namespace Survival;

/// <summary>
/// River water surface per near chunk (Mark 2026-10-07: the channel is now carved as a bed below the water
/// and the water fills it, so the surface needs its own mesh). For every river segment that touches the
/// chunk, one quad along the segment at the water level — sloped along the river, flat across — in the
/// terrain's vertex-colour material painted the lake water colour. No collider yet (the swim volume and
/// flow for boats are the next stage). Built with the chunk and destroyed with it.
/// </summary>
public sealed partial class TerrainWorldManager
{
	void TryBuildRiverWater( GameObject chunkRoot, TerrainPreviewSettings settings, float chunkMinX, float chunkMinY, float chunkSize )
	{
		if ( !settings.EnableRivers )
			return;

		var net = TerrainPreviewLandDiskFields.RiverNetwork;
		if ( net is null || net.Segments.Length == 0 )
			return;

		var maxX = chunkMinX + chunkSize;
		var maxY = chunkMinY + chunkSize;
		const float overlap = 0.6f;
		var quads = new List<int>();
		for ( var i = 0; i < net.Segments.Length; i++ )
		{
			ref var s = ref net.Segments[i];
			var reach = Math.Max( s.HalfA, s.HalfB ) + overlap;
			if ( Math.Max( s.Ax, s.Bx ) + reach < chunkMinX || Math.Min( s.Ax, s.Bx ) - reach > maxX )
				continue;
			if ( Math.Max( s.Ay, s.By ) + reach < chunkMinY || Math.Min( s.Ay, s.By ) - reach > maxY )
				continue;
			quads.Add( i );
		}

		if ( quads.Count == 0 )
			return;

		var material = TerrainMeshBuilder.GetTerrainMaterial();
		var mesh = new Mesh( material, MeshPrimitiveType.Triangles );
		var vertexCount = quads.Count * 4;
		mesh.CreateVertexBuffer<Vertex>( vertexCount );
		mesh.CreateIndexBuffer( quads.Count * 6 );
		var color = TerrainPreviewBiomeColors.PaletteColor( TerrainPreviewBiomeId.Water, 1f );
		var lo = new Vector3( float.MaxValue );
		var hi = new Vector3( float.MinValue );

		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			var v = 0;
			for ( var q = 0; q < quads.Count; q++ )
			{
				ref var s = ref net.Segments[quads[q]];
				var dir = new Vector2( s.Bx - s.Ax, s.By - s.Ay );
				if ( dir.LengthSquared < 1e-6f )
					dir = Vector2.Right;
				var side = new Vector2( -dir.y, dir.x ).Normal;
				var ha = s.HalfA + overlap;
				var hb = s.HalfB + overlap;
				// Local to the chunk origin, metres → engine units; a hair above the level so it never z-fights the bed.
				var corners = new[]
				{
					new Vector3( s.Ax - (side.x * ha) - chunkMinX, s.Ay - (side.y * ha) - chunkMinY, s.LevelA + 0.08f ),
					new Vector3( s.Ax + (side.x * ha) - chunkMinX, s.Ay + (side.y * ha) - chunkMinY, s.LevelA + 0.08f ),
					new Vector3( s.Bx + (side.x * hb) - chunkMinX, s.By + (side.y * hb) - chunkMinY, s.LevelB + 0.08f ),
					new Vector3( s.Bx - (side.x * hb) - chunkMinX, s.By - (side.y * hb) - chunkMinY, s.LevelB + 0.08f ),
				};
				for ( var c = 0; c < 4; c++ )
				{
					var p = TerrainWorldUnits.MetersToEngine( corners[c] );
					lo = Vector3.Min( lo, p );
					hi = Vector3.Max( hi, p );
					vertices[v++] = new Vertex
					{
						Position = p,
						Normal = Vector3.Up,
						Tangent = new Vector4( 1f, 0f, 0f, 1f ),
						TexCoord0 = new Vector2( c == 1 || c == 2 ? 1f : 0f, c >= 2 ? 1f : 0f ),
						Color = color,
					};
				}
			}
		} );

		mesh.LockIndexBuffer( indices =>
		{
			var n = 0;
			for ( var q = 0; q < quads.Count; q++ )
			{
				// Counter-clockwise seen from above, like the chunk quads (i0, i1, i3): corner 1 is on the +side of A
				// and side = dir rotated +90 deg, so 0 -> 1 -> 2 runs clockwise and the engine culled the whole
				// strip from above (2026-10-07: the carved channel showed, the water never did).
				var b = q * 4;
				indices[n++] = b; indices[n++] = b + 2; indices[n++] = b + 1;
				indices[n++] = b; indices[n++] = b + 3; indices[n++] = b + 2;
			}
		} );

		mesh.Bounds = new BBox( lo - new Vector3( 1f ), hi + new Vector3( 1f ) );
		var model = new ModelBuilder().AddMesh( mesh ).Create();
		if ( model is null || !model.IsValid )
			return;

		var go = new GameObject( true, "RiverWater" );
		go.Flags |= GameObjectFlags.NotSaved;
		go.Parent = chunkRoot;
		go.LocalPosition = Vector3.Zero;
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.MaterialOverride = material;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}
}
