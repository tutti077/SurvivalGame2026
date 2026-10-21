namespace Survival;

/// <summary>
/// Far impostors (Mark 2026-10-20: "I should be able to see SOMETHING for these trees" past the 2.5 km ring).
/// Beyond the instanced LOD3 ring the forest is drawn as crossed TEXTURED cards — two crown quads, a cap and two
/// trunk quads, 10 triangles, a ragged round crown with a sphere normal map (far_tree_card_*.png) — out to the far tier's edge (<see cref="FarViewDistanceMeters"/>). They are
/// planted on the far tier's worker at the REAL tree positions (the scatter's own planner, one card per tree), and BAKED into the far chunk's
/// own model as a second mesh with <c>materials/environment/cloverHills/trees/far_tree_card.vmat</c>: no instance list, no
/// per-frame upload — the 32 k-instance command list this replaced cost 1.45 ms of main thread every frame
/// (perf_bisect, 2026-10-20). The cards fade in with their chunk through the renderer's <c>FarCardFade</c>
/// attribute and take <see cref="FarImpostorTint"/> through <c>FarCardTint</c> (pixel_lit, materials with
/// <c>PixelFarCard 1</c> only, so the ground mesh in the same model is untouched). Card normals arch like the
/// top of a crown (outward + up) so they take the sun the way the LOD3 crowns do; <c>far_card_calibrate apply</c>
/// then measures both on screen: <c>apply direct</c> by day sets <see cref="FarImpostorDirect"/> (the sun response),
/// <c>apply tint</c> by night sets the tint (the ambient response), so the boundary holds through the day cycle.
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Far Terrain" ), Title( "Far Impostors" ), Description( "Crossed-card trees baked into the far tier chunks beyond the instanced ring, out to Far View Distance (chunks built while this is off get no cards)." )]
	public bool FarImpostorsEnabled
	{
		get => _farImpostorsEnabled;
		set
		{
			if ( _farImpostorsEnabled == value )
				return;
			_farImpostorsEnabled = value;
			foreach ( var chunk in _far.Values )
			{
				if ( chunk.Renderer is null || !chunk.Renderer.IsValid() )
					continue;
				chunk.CardFade = value ? chunk.CardFade : 0f;
				chunk.Renderer.Attributes.Set( "FarCardFade", chunk.CardFade );
			}
		}
	}
	bool _farImpostorsEnabled = true;

	[Property, Group( "Far Terrain" ), Title( "Far Impostor Height (m)" ), Range( 8f, 40f ), Step( 1f ), Description( "Card height for a scale-1 tree (the elm is ~22 m)." )]
	public float FarImpostorHeightMeters { get; set; } = 22f;

	[Property, Group( "Far Terrain" ), Title( "Far Impostor Tint" ), Description( "Multiplies the card colours (gamma) so the cards land on the LOD3 ring's on-screen colour. far_card_calibrate apply measures and sets it; copy the value here to keep it." )]
	public Color FarImpostorTint
	{
		get => _farImpostorTint;
		set
		{
			_farImpostorTint = value;
			foreach ( var chunk in _far.Values )
			{
				if ( chunk.Renderer is not null && chunk.Renderer.IsValid() )
					chunk.Renderer.Attributes.Set( "FarCardTint", new Vector3( value.r, value.g, value.b ) );
			}
		}
	}
	/// <summary>far_card_calibrate apply, converged 2026-10-20 (terrainTest noon sun, 260 m up, ring / card ratio 0.98): the cards land on the LOD3 ring's on-screen green.</summary>
	/// <summary>far_card_calibrate apply tint at midnight (ambient only), 2026-10-20: ~1, i.e. the texture colours were right.</summary>
	Color _farImpostorTint = Color.White;

	[Property, Group( "Far Terrain" ), Title( "Far Impostor Direct Light" ), Range( 0.05f, 2f ), Step( 0.01f ), Description( "Scales only the sun / moon light on the cards (ambient stays 1:1 with the ring). far_card_calibrate apply direct measures and sets it by day; apply tint sets the tint by night." )]
	public float FarImpostorDirect
	{
		get => _farImpostorDirect;
		set
		{
			_farImpostorDirect = value;
			foreach ( var chunk in _far.Values )
			{
				if ( chunk.Renderer is not null && chunk.Renderer.IsValid() )
					chunk.Renderer.Attributes.Set( "FarCardDirect", value );
			}
		}
	}
	/// <summary>Boundary-band calibration 2026-10-20 under terrainTest's FIXED scene sun (pitch 58): 0.15 reads ~0.92 at the ring / card boundary; the day-cycle's noon (lower, tilted sun) wanted 0.5 — the sun-angle residual.</summary>
	float _farImpostorDirect = 0.15f;

	[Property, Group( "Far Terrain" ), Title( "Far Impostor Ambient Light" ), Range( 0.05f, 2f ), Step( 0.01f ), Description( "Scales only the sky ambient on the cards. far_card_calibrate apply ambient measures and sets it by night." )]
	public float FarImpostorAmbient
	{
		get => _farImpostorAmbient;
		set
		{
			_farImpostorAmbient = value;
			foreach ( var chunk in _far.Values )
			{
				if ( chunk.Renderer is not null && chunk.Renderer.IsValid() )
					chunk.Renderer.Attributes.Set( "FarCardAmbient", value );
			}
		}
	}
	float _farImpostorAmbient = 0.78f;

	static Material _farCardMaterial;

	static Material GetFarCardMaterial()
	{
		if ( _farCardMaterial is not null && _farCardMaterial.IsValid )
			return _farCardMaterial;
		_farCardMaterial = Material.Load( "materials/environment/cloverHills/trees/far_tree_card.vmat" );
		if ( _farCardMaterial is null || !_farCardMaterial.IsValid )
			_farCardMaterial = TerrainMeshBuilder.GetTerrainMaterial();
		return _farCardMaterial;
	}

	/// <summary>One card in local units (1 m = 40 u, origin at the ground): 20 vertices, 30 indices.</summary>
	static readonly Vertex[] FarCardTemplate = BuildFarCardTemplate();
	static readonly int[] FarCardTemplateIndices = BuildFarCardTemplateIndices();

	static Vertex[] BuildFarCardTemplate()
	{
		var verts = new List<Vertex>( 20 );
		var u = TerrainWorldUnits.MetersToEngine( 1f );
		float cw = 7.5f * u, c0 = 6f * u, c1 = 21f * u, tw = 0.7f * u, t1 = 8f * u, cz = 15f * u;

		// The card is textured (Blender/scripts/create_far_card_textures.py → far_tree_card_{color,mask,normal}.png):
		// rows 0-191 (v 0 .. 0.75) a ragged round crown with a SPHERE normal map, rows 205-255 (v 0.8 .. 1) a bark strip
		// with a cylinder normal map. Vertex colour is white / alpha 1 (pure texture); each quad's tangent runs along its
		// own U axis and its normal is the face normal, so the normal map — not the plate — decides how the sun hits it
		// (Mark 2026-10-20: solid quads with one normal "VERY square ... clearly not a part of the other trees").
		var white = new Color( 1f, 1f, 1f, 1f );

		void Quad( Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector3 tangentU, float v0, float v1 )
		{
			var tangent = new Vector4( tangentU.x, tangentU.y, tangentU.z, 1f );
			verts.Add( new Vertex { Position = a, Normal = normal, Tangent = tangent, TexCoord0 = new Vector2( 0f, v0 ), Color = white } );
			verts.Add( new Vertex { Position = b, Normal = normal, Tangent = tangent, TexCoord0 = new Vector2( 1f, v0 ), Color = white } );
			verts.Add( new Vertex { Position = c, Normal = normal, Tangent = tangent, TexCoord0 = new Vector2( 1f, v1 ), Color = white } );
			verts.Add( new Vertex { Position = d, Normal = normal, Tangent = tangent, TexCoord0 = new Vector2( 0f, v1 ), Color = white } );
		}

		const float crownBottom = 0.75f, crownTop = 0f, trunkBottom = 1f, trunkTop = 0.8f;
		// Crown: two crossed quads from 6 m to 21 m, 15 m wide (the elm's crown spread); a, b = bottom edge.
		Quad( new Vector3( -cw, 0f, c0 ), new Vector3( cw, 0f, c0 ), new Vector3( cw, 0f, c1 ), new Vector3( -cw, 0f, c1 ), Vector3.Forward.Cross( Vector3.Up ).Normal, Vector3.Forward, crownBottom, crownTop );
		Quad( new Vector3( 0f, -cw, c0 ), new Vector3( 0f, cw, c0 ), new Vector3( 0f, cw, c1 ), new Vector3( 0f, -cw, c1 ), Vector3.Forward, Vector3.Left, crownBottom, crownTop );
		// Cap: a flat quad through the crown so the tree reads round from above (the same round blob, seen as a disc).
		Quad( new Vector3( -cw, -cw, cz ), new Vector3( cw, -cw, cz ), new Vector3( cw, cw, cz ), new Vector3( -cw, cw, cz ), Vector3.Up, Vector3.Forward, crownBottom, crownTop );
		// Trunk: two crossed quads from the ground into the crown.
		Quad( new Vector3( -tw, 0f, 0f ), new Vector3( tw, 0f, 0f ), new Vector3( tw, 0f, t1 ), new Vector3( -tw, 0f, t1 ), Vector3.Forward.Cross( Vector3.Up ).Normal, Vector3.Forward, trunkBottom, trunkTop );
		Quad( new Vector3( 0f, -tw, 0f ), new Vector3( 0f, tw, 0f ), new Vector3( 0f, tw, t1 ), new Vector3( 0f, -tw, t1 ), Vector3.Forward, Vector3.Left, trunkBottom, trunkTop );
		return verts.ToArray();
	}

	static int[] BuildFarCardTemplateIndices()
	{
		var indices = new int[30];
		for ( var q = 0; q < 5; q++ )
		{
			var i0 = q * 4;
			var o = q * 6;
			indices[o] = i0; indices[o + 1] = i0 + 1; indices[o + 2] = i0 + 2;
			indices[o + 3] = i0; indices[o + 4] = i0 + 2; indices[o + 5] = i0 + 3;
		}
		return indices;
	}

	/// <summary>
	/// Worker: one card per REAL tree. The same planner the scatter and the LOD3 ring use
	/// (<see cref="TerrainVegetationScatter.EnumerateTrees"/>) runs for every near chunk inside this far chunk, so a
	/// card stands exactly where the tree will stand when you fly to it (Mark 2026-10-20: "pick out a tree from 3 km
	/// away and fly toward it"). Chunk-local engine units; the card is the elm's 22 m at scale 1, times the tree's own scale.
	/// </summary>
	static Transform[] PlanFarCards( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, TerrainChunkCoord coord, float size, float nearChunkSize, TerrainVegetationScatter.Options options, float heightMeters )
	{
		var perSide = Math.Max( 1, (int)MathF.Round( size / nearChunkSize ) );
		var worldRadius = settings.TotalWorldRadiusMeters;
		var minX = -worldRadius + (coord.X * size);
		var minY = -worldRadius + (coord.Y * size);
		var scaleUnits = heightMeters / 22f;
		var scratch = new List<TerrainVegetationScatter.FarTree>();
		var result = new List<Transform>( 64 );
		for ( var iy = 0; iy < perSide; iy++ )
		{
			for ( var ix = 0; ix < perSide; ix++ )
			{
				var near = new TerrainChunkCoord( (coord.X * perSide) + ix, (coord.Y * perSide) + iy );
				scratch.Clear();
				TerrainVegetationScatter.EnumerateTrees( settings, backend, near, nearChunkSize, options, scratch );
				foreach ( var tree in scratch )
				{
					var pos = TerrainWorldUnits.MetersToEngine( new Vector3( tree.X - minX, tree.Y - minY, tree.HeightMeters ) );
					result.Add( new Transform( pos, Rotation.FromYaw( tree.YawDegrees ), tree.ScaleMul * scaleUnits ) );
				}
			}
		}
		return result.ToArray();
	}

	/// <summary>Worker: the planted cards as one vertex / index array pair in chunk-local units (the far chunk's second mesh).</summary>
	static void BuildFarCardGeometry( Transform[] cards, out Vertex[] vertices, out int[] indices )
	{
		if ( cards is null || cards.Length == 0 )
		{
			vertices = null;
			indices = null;
			return;
		}

		var template = FarCardTemplate;
		var templateIndices = FarCardTemplateIndices;
		vertices = new Vertex[cards.Length * template.Length];
		indices = new int[cards.Length * templateIndices.Length];
		for ( var c = 0; c < cards.Length; c++ )
		{
			var t = cards[c];
			var vBase = c * template.Length;
			for ( var v = 0; v < template.Length; v++ )
			{
				var src = template[v];
				vertices[vBase + v] = new Vertex
				{
					Position = t.Position + (t.Rotation * (src.Position * t.Scale)),
					Normal = t.Rotation * src.Normal,
					Tangent = src.Tangent,
					TexCoord0 = src.TexCoord0,
					Color = src.Color,
				};
			}
			var iBase = c * templateIndices.Length;
			for ( var i = 0; i < templateIndices.Length; i++ )
				indices[iBase + i] = vBase + templateIndices[i];
		}
	}

	/// <summary>Main thread: the card mesh for a far chunk (null without cards), bounds = the chunk column plus a tree.</summary>
	static Mesh BuildFarCardMesh( Vertex[] vertices, int[] indices, float chunkSizeMeters, float maxTerrainHeightMeters )
	{
		if ( vertices is null || indices is null || vertices.Length == 0 )
			return null;

		var mesh = new Mesh( GetFarCardMaterial(), MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( vertices.Length );
		mesh.CreateIndexBuffer( indices.Length );
		mesh.LockVertexBuffer<Vertex>( buffer =>
		{
			for ( var i = 0; i < vertices.Length; i++ )
				buffer[i] = vertices[i];
		} );
		mesh.LockIndexBuffer( buffer =>
		{
			for ( var i = 0; i < indices.Length; i++ )
				buffer[i] = indices[i];
		} );
		mesh.Bounds = new BBox(
			Vector3.Zero,
			TerrainWorldUnits.MetersToEngine( new Vector3( chunkSizeMeters, chunkSizeMeters, maxTerrainHeightMeters + 60f ) ) );
		return mesh;
	}

	/// <summary>A far chunk just built: its cards start invisible (they fade in from TickFarCardFades); tint applied at once.</summary>
	void BeginFarCardFade( FarChunk chunk )
	{
		if ( chunk.Renderer is null || !chunk.Renderer.IsValid() )
			return;
		chunk.Renderer.Attributes.Set( "FarCardTint", new Vector3( _farImpostorTint.r, _farImpostorTint.g, _farImpostorTint.b ) );
		chunk.Renderer.Attributes.Set( "FarCardDirect", _farImpostorDirect );
		chunk.Renderer.Attributes.Set( "FarCardAmbient", _farImpostorAmbient );
		chunk.CardFade = 0f;
		chunk.Renderer.Attributes.Set( "FarCardFade", 0f );
	}

	/// <summary>
	/// Every frame: every far chunk's cards fade toward shown when the chunk's centre is past the LOD3 ring's inner
	/// edge (Far Trees Range − half a far chunk, the ring draws the trees inside it) and toward hidden inside it —
	/// a far chunk overlaps the near zone too, and without this its baked cards stood among the real trees.
	/// Over Far Trees — Fade (s) either way; the attribute is written only while a fade moves.
	/// </summary>
	void TickFarCardFades( Vector3 camMeters )
	{
		if ( _far.Count == 0 || _loadSettings is null )
			return;
		var size = Math.Max( 256f, FarChunkSizeMeters );
		var worldRadius = _loadSettings.TotalWorldRadiusMeters;
		var inner = Math.Max( 600f, VegetationFarCardRangeMeters ) - (size * 0.5f);
		var innerSq = inner * inner;
		var step = Time.Delta / Math.Max( 0.001f, VegetationFarFadeSeconds );
		var slot = 0;
		foreach ( var pair in _far )
		{
			var chunk = pair.Value;
			slot++;
			if ( chunk.CardCount == 0 || chunk.Renderer is null || !chunk.Renderer.IsValid() )
				continue;
			var cx = -worldRadius + ((pair.Key.X + 0.5f) * size) - camMeters.x;
			var cy = -worldRadius + ((pair.Key.Y + 0.5f) * size) - camMeters.y;
			var target = _farImpostorsEnabled && (cx * cx) + (cy * cy) > innerSq ? 1f : 0f;
			// Settled chunks re-assert their attribute every 30 frames (a hotload or a re-created scene object can drop it).
			if ( chunk.CardFade == target && (_frameIndex + slot) % 30 != 0 )
				continue;
			chunk.CardFade = target > chunk.CardFade ? Math.Min( target, chunk.CardFade + step ) : Math.Max( target, chunk.CardFade - step );
			chunk.Renderer.Attributes.Set( "FarCardFade", chunk.CardFade );
		}
	}
}
