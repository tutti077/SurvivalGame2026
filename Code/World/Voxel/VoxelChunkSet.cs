using System.Threading.Tasks;

namespace Survival;

/// <summary>
/// The chunk objects over one <see cref="VoxelField"/>: a child object per mesh chunk (ModelRenderer + static ModelCollider,
/// both from one ModelBuilder with a collision + trace mesh), a dirty queue, <see cref="BuildsInFlight"/> worker builds at a
/// time and <see cref="ModelsPerFrame"/> models made per frame. Shared by the standalone prototype (<see cref="VoxelTerrain"/>)
/// and the live world's voxel columns (TerrainWorldManager.Voxel.cs); the owner calls <see cref="Tick"/> every frame.
/// </summary>
public sealed class VoxelChunkSet
{
	sealed class Chunk
	{
		public (int X, int Y, int Z) Coord;
		public GameObject Object;
		public ModelRenderer Renderer;
		public ModelCollider Collider;
		public bool Dirty;
		public bool Building;
		public int Vertices;
	}

	const int BuildsInFlight = 6;
	const int ModelsPerFrame = 3;

	public VoxelField Field { get; }
	/// <summary>What the ground is made of per xy (the live world's biome splat); null = Clover everywhere.</summary>
	public VoxelSurfaceStyle Style { get; init; }
	/// <summary>The cells the meshes may cover (a world column meshes only its own square).</summary>
	public VoxelMesher.Clip Clip { get; init; } = VoxelMesher.Clip.None;

	readonly GameObject _parent;
	readonly Material _material;
	readonly string _namePrefix;
	readonly string[] _tags;
	readonly Dictionary<(int, int, int), Chunk> _chunks = new();
	readonly Queue<Chunk> _dirtyQueue = new();
	readonly List<(Chunk Chunk, Task<VoxelMesher.MeshData> Task)> _building = new();

	public float LastMainThreadMs { get; private set; }
	public float LastBuildMs { get; private set; }
	public int TotalVertices { get; private set; }
	public int QueueCount => _dirtyQueue.Count;
	public int BuildingCount => _building.Count;
	public int ChunkCount => _chunks.Count;
	public int DrawnChunks => _chunks.Values.Count( c => c.Renderer.IsValid() && c.Renderer.Enabled );
	/// <summary>No chunk waiting or building: every mesh matches the field.</summary>
	public bool Settled => _dirtyQueue.Count == 0 && _building.Count == 0;

	public VoxelChunkSet( VoxelField field, GameObject parent, Material material, string namePrefix, params string[] tags )
	{
		Field = field;
		_parent = parent;
		_material = material;
		_namePrefix = namePrefix;
		_tags = tags;
	}

	/// <summary>One object per mesh chunk of the field, all queued for their first build.</summary>
	public void CreateAllChunks()
	{
		for ( var z = 0; z < Field.ChunksZ; z++ )
		{
			for ( var y = 0; y < Field.ChunksY; y++ )
			{
				for ( var x = 0; x < Field.ChunksX; x++ )
					CreateChunk( (x, y, z) );
			}
		}
	}

	void CreateChunk( (int X, int Y, int Z) coord )
	{
		var go = new GameObject( true, $"{_namePrefix}_{coord.X}_{coord.Y}_{coord.Z}" );
		go.Flags |= GameObjectFlags.NotSaved;
		go.Parent = _parent;
		var origin = Field.OriginMeters + (new Vector3( coord.X, coord.Y, coord.Z ) * Field.ChunkMeters);
		go.WorldPosition = TerrainWorldUnits.MetersToEngine( origin );
		foreach ( var tag in _tags )
			go.Tags.Add( tag );
		var chunk = new Chunk
		{
			Coord = coord,
			Object = go,
			Renderer = go.Components.Create<ModelRenderer>(),
			Collider = go.Components.Create<ModelCollider>(),
		};
		chunk.Renderer.Enabled = false;
		chunk.Collider.Static = true;
		chunk.Collider.Enabled = false;
		_chunks[coord] = chunk;
		MarkDirty( chunk );
	}

	public void MarkDirty( IEnumerable<(int X, int Y, int Z)> coords )
	{
		foreach ( var coord in coords )
		{
			if ( _chunks.TryGetValue( coord, out var chunk ) )
				MarkDirty( chunk );
		}
	}

	public void MarkAllDirty()
	{
		foreach ( var chunk in _chunks.Values )
			MarkDirty( chunk );
	}

	void MarkDirty( Chunk chunk )
	{
		if ( chunk.Dirty )
			return;
		chunk.Dirty = true;
		_dirtyQueue.Enqueue( chunk );
	}

	/// <summary>Land finished builds as models (a few per frame) and start new ones from the dirty queue.</summary>
	public void Tick()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var made = 0;
		for ( var i = _building.Count - 1; i >= 0 && made < ModelsPerFrame; i-- )
		{
			var (chunk, task) = _building[i];
			if ( !task.IsCompleted )
				continue;
			_building.RemoveAt( i );
			chunk.Building = false;
			if ( task.IsFaulted )
			{
				Log.Warning( $"[Voxel] mesh {chunk.Coord} failed: {task.Exception?.GetBaseException().Message}" );
				continue;
			}
			ApplyMesh( chunk, task.Result );
			made++;
			// Edited again while it was building: go round once more
			if ( chunk.Dirty )
				StartBuild( chunk );
		}
		while ( _building.Count < BuildsInFlight && _dirtyQueue.Count > 0 )
		{
			var chunk = _dirtyQueue.Dequeue();
			if ( chunk.Building )
				continue;   // picked up again when it lands
			StartBuild( chunk );
		}
		LastMainThreadMs = (float)sw.Elapsed.TotalMilliseconds;
	}

	void StartBuild( Chunk chunk )
	{
		chunk.Dirty = false;
		chunk.Building = true;
		var field = Field;
		var coord = chunk.Coord;
		var style = Style;
		var clip = Clip;
		var task = GameTask.RunInThreadAsync( () =>
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var data = VoxelMesher.Build( field, coord.X, coord.Y, coord.Z, style, clip );
			LastBuildMs = (float)sw.Elapsed.TotalMilliseconds;
			return data;
		} );
		_building.Add( (chunk, task) );
	}

	void ApplyMesh( Chunk chunk, VoxelMesher.MeshData data )
	{
		if ( chunk.Object is null || !chunk.Object.IsValid() )
			return;
		TotalVertices += data.VertexCount - chunk.Vertices;
		chunk.Vertices = data.VertexCount;
		if ( data.VertexCount == 0 || data.Indices.Length == 0 )
		{
			chunk.Renderer.Enabled = false;
			chunk.Collider.Enabled = false;
			return;
		}
		var mesh = new Mesh( _material, MeshPrimitiveType.Triangles );
		mesh.CreateVertexBuffer<Vertex>( data.VertexCount );
		mesh.CreateIndexBuffer( data.Indices.Length );
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		mesh.LockVertexBuffer<Vertex>( vertices =>
		{
			for ( var i = 0; i < data.VertexCount; i++ )
			{
				var p = data.Positions[i];
				min = Vector3.Min( min, p );
				max = Vector3.Max( max, p );
				var n = data.Normals[i];
				vertices[i] = new Vertex
				{
					Position = p,
					Normal = n,
					Tangent = Tangent( n ),
					// terrain_ground.vmat (pixel_lit F_TERRAIN_SPLAT): the same vertex contract as the heightfield chunks
					TexCoord0 = data.Extras[i],
					TexCoord1 = new Vector2( 0f, 0f ),
					Color = data.Colors[i],
				};
			}
		} );
		mesh.LockIndexBuffer( indices =>
		{
			for ( var i = 0; i < data.Indices.Length; i++ )
				indices[i] = data.Indices[i];
		} );
		mesh.Bounds = new BBox( min, max );
		var positions = new List<Vector3>( data.Positions );
		var indexList = new List<int>( data.Indices );
		var model = new ModelBuilder()
			.AddMesh( mesh )
			.AddCollisionMesh( positions, indexList )
			.AddTraceMesh( positions, indexList )
			.Create();
		chunk.Renderer.Model = model;
		chunk.Renderer.Enabled = true;
		chunk.Collider.Model = model;
		chunk.Collider.Enabled = true;
	}

	static Vector4 Tangent( Vector3 normal )
	{
		var tangent = Vector3.Cross( normal, Vector3.Up );
		if ( tangent.LengthSquared < 1e-6f )
			tangent = Vector3.Cross( normal, Vector3.Forward );
		return new Vector4( tangent.Normal, 1f );
	}

	/// <summary>Destroy every chunk object. Builds still running are dropped when they land.</summary>
	public void Destroy()
	{
		foreach ( var chunk in _chunks.Values )
		{
			if ( chunk.Object is not null && chunk.Object.IsValid() )
				chunk.Object.Destroy();
		}
		_chunks.Clear();
		_dirtyQueue.Clear();
		_building.Clear();
		TotalVertices = 0;
	}
}
