using System.Text.Json;

namespace Survival;

/// <summary>
/// The underground in the live world (Mark 2026-11-18; rules and data in <see cref="Underground"/>, <c>data/underground.json</c>).
/// A voxel column starts <see cref="VoxelDepthBelowMeters"/> deep; this part lets a dug column reach the bottom of the world:
/// <list type="bullet">
/// <item><b>Depth on demand.</b> A stroke that comes within <see cref="UndergroundGrowMarginMeters"/> of a column's floor grows
/// its field two mesh chunks (32 m) down first (<see cref="VoxelField.Deepened"/>, <see cref="VoxelChunkSet.AdoptDeepened"/>:
/// nothing above is meshed again), never further than a little under the bedrock. A column that comes back from the edit log
/// is built that deep from the start - the same rule on the lowest logged stroke, so the live column and the restored one
/// stand on the same sample lattice.</item>
/// <item><b>Bedrock.</b> The field refuses any cut deeper than the last layer (<see cref="VoxelField.BedrockDepthMeters"/>).</item>
/// <item><b>Loot rooms.</b> Each 64 m chunk may hold one room per layer (<see cref="Underground.TryRoom"/>, a pure function of
/// the world seed). A room is carved into a column's field once the field reaches under its floor - always before a digger
/// can get there - and the host puts a chest on its floor (the build system's chest piece, filled from the layer's loot).
/// A chest is spawned once per session; a room a player has stood in is written to <c>underground_rooms.json</c> in the world
/// save and gets no chest in later sessions (no container contents are saved anywhere yet, so "found" is the record).</item>
/// </list>
/// </summary>
public sealed partial class TerrainWorldManager
{
	[Property, Group( "Voxel" ), Title( "Underground" ), Description( "Layers, pockets, bedrock and loot rooms under the ground (data/underground.json). Off = plain soil and stone down to Depth Below Ground." )]
	public bool UndergroundEnabled { get; set; } = true;

	/// <summary>A stroke whose reach comes this close to a column's floor grows the column first.</summary>
	const float UndergroundGrowMarginMeters = 24f;
	/// <summary>Mesh chunks (16 m each) a column grows by at a time.</summary>
	const int UndergroundGrowChunks = 2;
	/// <summary>A column's field ends this far under the bedrock line of its lowest ground.</summary>
	const float UndergroundBedrockSlackMeters = 8f;
	/// <summary>With no layers in the data (no bedrock) a column still stops growing this far under its lowest ground.</summary>
	const float UndergroundMaxDepthMeters = 512f;
	/// <summary>A player this close to a room's chest has found the room.</summary>
	const float UndergroundRoomFoundMeters = 6f;
	const string UndergroundRoomsFileName = "underground_rooms.json";

	/// <summary>Rooms found in this world (keys from <see cref="UndergroundRoom.Key"/>): no chest again.</summary>
	readonly HashSet<string> _undergroundFoundRooms = new();
	string _undergroundRoomsWorld;
	/// <summary>Host: the chests standing in rooms this session, by room key.</summary>
	readonly Dictionary<string, GameObject> _undergroundChests = new();
	float _undergroundRoomTimer;

	int? UndergroundSeedOrNull => UndergroundEnabled && _loadSettings is not null ? _loadSettings.WorldSeed : null;

	/// <summary>How deep a column may reach under its lowest ground (m).</summary>
	static float UndergroundDepthLimitMeters()
	{
		var bedrock = Underground.BedrockDepthMeters;
		return float.IsPositiveInfinity( bedrock ) ? UndergroundMaxDepthMeters : bedrock + UndergroundBedrockSlackMeters;
	}

	/// <summary>
	/// Worker-safe: the floor (m) a column's field needs so the lowest of <paramref name="edits"/> keeps the grow margin under
	/// it - <paramref name="baseOriginZ"/> lowered by whole grow steps, never past the depth limit under <paramref name="minGroundZ"/>.
	/// </summary>
	static float UndergroundOriginZ( float baseOriginZ, float minGroundZ, VoxelEdit[] edits, float cell, bool enabled )
	{
		if ( !enabled || edits is null || edits.Length == 0 )
			return baseOriginZ;
		var lowest = float.MaxValue;
		foreach ( var e in edits )
		{
			if ( !e.IsPaint )
				lowest = Math.Min( lowest, e.Z - e.ReachMeters );
		}
		var step = UndergroundGrowChunks * VoxelField.ChunkCells * cell;
		var limit = minGroundZ - UndergroundDepthLimitMeters();
		var origin = baseOriginZ;
		while ( origin > lowest - UndergroundGrowMarginMeters && origin > limit )
			origin -= step;
		return origin;
	}

	/// <summary>Before a stroke lands on a column: grow its field down while the stroke reaches into the grow margin over its floor.</summary>
	void EnsureColumnDepthFor( VoxelColumn column, VoxelEdit edit )
	{
		if ( !UndergroundEnabled || edit.IsPaint )
			return;
		var limit = column.MinGroundZ - UndergroundDepthLimitMeters();
		var grown = false;
		while ( column.Set.Field.OriginMeters.z > edit.Z - edit.ReachMeters - UndergroundGrowMarginMeters && column.Set.Field.OriginMeters.z > limit )
		{
			var deeper = column.Set.Field.Deepened( UndergroundGrowChunks );
			column.Set.AdoptDeepened( deeper, UndergroundGrowChunks );
			grown = true;
		}
		if ( !grown )
			return;
		var field = column.Set.Field;
		column.Set.MarkDirty( CarveUndergroundRooms( field, _loadSettings.WorldSeed, column.Coord, column.ChunkMinX, column.ChunkMinY, VoxelChunkSize, column.Rooms ) );
		HostSpawnRoomChests( column );
		if ( VoxelLogEdits )
			Log.Info( $"[Voxel] column {column.Coord} grown to {field.SamplesZ} samples deep (floor {field.OriginMeters.z:0.#} m, {column.Rooms.Count} rooms)" );
	}

	/// <summary>A room carved into a column's field: its spec and where its floor is in the world (m).</summary>
	readonly record struct CarvedRoom( UndergroundRoom Room, float FloorZ );

	/// <summary>
	/// Worker-safe: carve every loot room of this chunk that the field now reaches under and that <paramref name="carved"/>
	/// does not hold yet; adds them to it. A room waits until the field's floor is 2 m under its own, so it is never cut off.
	/// </summary>
	static HashSet<(int X, int Y, int Z)> CarveUndergroundRooms( VoxelField field, int seed, TerrainChunkCoord coord, float chunkMinX, float chunkMinY, float chunkSize, List<CarvedRoom> carved )
	{
		var dirty = new HashSet<(int X, int Y, int Z)>();
		var layers = Underground.Layers.Count;
		for ( var layer = 0; layer < layers; layer++ )
		{
			if ( !Underground.TryRoom( seed, coord.X, coord.Y, layer, chunkMinX, chunkMinY, chunkSize, out var room ) )
				continue;
			var already = false;
			foreach ( var c in carved )
				already |= c.Room.Key == room.Key;
			if ( already )
				continue;
			var ix = Math.Clamp( (int)MathF.Round( (room.CenterXy.x - field.OriginMeters.x) / field.CellMeters ), 0, field.SamplesX - 1 );
			var iy = Math.Clamp( (int)MathF.Round( (room.CenterXy.y - field.OriginMeters.y) / field.CellMeters ), 0, field.SamplesY - 1 );
			// On the half-metre lattice, like the walls
			var floorZ = MathF.Round( (field.GroundZ( ix, iy ) - room.FloorDepthMeters) * 2f ) / 2f;
			if ( floorZ - 2f < field.OriginMeters.z )
				continue;   // the field does not reach under it yet
			var half = new Vector3( room.SizeMeters.x * 0.5f, room.SizeMeters.y * 0.5f, 0f );
			var min = new Vector3( room.CenterXy.x, room.CenterXy.y, floorZ ) - half;
			var max = new Vector3( room.CenterXy.x, room.CenterXy.y, floorZ + room.SizeMeters.z ) + half;
			dirty.UnionWith( field.CarveBox( min, max ) );
			carved.Add( new CarvedRoom( room, floorZ ) );
		}
		return dirty;
	}

	void EnsureUndergroundRoomsLoaded()
	{
		if ( _undergroundRoomsWorld == WorldName )
			return;
		_undergroundRoomsWorld = WorldName;
		_undergroundFoundRooms.Clear();
		var path = $"{WorldSaveIO.GetWorldFolder( WorldName )}/{UndergroundRoomsFileName}";
		try
		{
			if ( FileSystem.Data.FileExists( path ) && JsonSerializer.Deserialize<UndergroundRoomsFile>( FileSystem.Data.ReadAllText( path ) ) is { Found: { } found } )
				_undergroundFoundRooms.UnionWith( found );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Underground] could not read {path}: {e.Message}" );
		}
	}

	void SaveUndergroundRooms()
	{
		var path = $"{WorldSaveIO.GetWorldFolder( WorldName )}/{UndergroundRoomsFileName}";
		try
		{
			WorldSaveIO.EnsureWorldDirectory( WorldName );
			FileSystem.Data.WriteAllText( path, JsonSerializer.Serialize( new UndergroundRoomsFile { Found = _undergroundFoundRooms.ToList() } ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[Underground] could not write {path}: {e.Message}" );
		}
	}

	sealed class UndergroundRoomsFile
	{
		public List<string> Found { get; set; } = new();
	}

	/// <summary>Host: a chest on the floor of every carved room of this column that has none this session and was not found in an earlier one.</summary>
	void HostSpawnRoomChests( VoxelColumn column )
	{
		if ( column.Rooms.Count == 0 || (GameObject.Network is { Active: true } && !Networking.IsHost) )
			return;
		EnsureUndergroundRoomsLoaded();
		foreach ( var carved in column.Rooms )
		{
			var room = carved.Room;
			if ( _undergroundFoundRooms.Contains( room.Key ) || (_undergroundChests.TryGetValue( room.Key, out var standing ) && standing.IsValid()) )
				continue;
			var rotation = Rotation.FromYaw( (room.Seed & 3) * 90f );
			var position = TerrainWorldUnits.MetersToEngine( new Vector3( room.CenterXy.x, room.CenterXy.y, carved.FloorZ ) );
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( "chest", rotation );
			if ( !BuildAuthority.HostPlacePiece( Scene, "chest", new Transform( position, rotation ), blueprint: false, out var chest ) )
			{
				Log.Warning( $"[Underground] could not place the chest of room {room.Key}" );
				continue;
			}
			chest.Name = $"underground_chest_{room.Key}";
			chest.Flags |= GameObjectFlags.NotSaved;
			_undergroundChests[room.Key] = chest;
			var container = chest.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
			if ( container is null )
				continue;
			foreach ( var (resourceId, count) in Underground.RollLoot( room ) )
				container.HostDepositStack( resourceId, count );
			if ( VoxelLogEdits )
				Log.Info( $"[Underground] room {room.Key} (layer {room.Layer}, {room.SizeMeters.x:0.#}×{room.SizeMeters.y:0.#}×{room.SizeMeters.z:0.#} m) floor {carved.FloorZ:0.#} m at {room.CenterXy.x:0.#},{room.CenterXy.y:0.#}: chest placed" );
		}
	}

	/// <summary>Host, once a second: a room whose chest a player stands beside is found - written to the world save, no chest in later sessions.</summary>
	void TickUndergroundRooms()
	{
		if ( _undergroundChests.Count == 0 )
			return;
		_undergroundRoomTimer -= Time.Delta;
		if ( _undergroundRoomTimer > 0f )
			return;
		_undergroundRoomTimer = 1f;
		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;
		var reach = TerrainWorldUnits.MetersToEngine( UndergroundRoomFoundMeters );
		List<string> gone = null;
		var changed = false;
		foreach ( var (key, chest) in _undergroundChests )
		{
			if ( !chest.IsValid() )
			{
				(gone ??= new()).Add( key );   // broken up or removed: found
				changed |= _undergroundFoundRooms.Add( key );
				continue;
			}
			if ( _undergroundFoundRooms.Contains( key ) )
				continue;
			foreach ( var pawn in Scene.GetAllComponents<PlayerTerraform>() )
			{
				if ( (pawn.WorldPosition - chest.WorldPosition).Length > reach )
					continue;
				changed |= _undergroundFoundRooms.Add( key );
				break;
			}
		}
		if ( gone is not null )
		{
			foreach ( var key in gone )
				_undergroundChests.Remove( key );
		}
		if ( changed )
			SaveUndergroundRooms();
	}

	/// <summary>The world is unloading: this session's room chests go with it.</summary>
	void DestroyUndergroundChests()
	{
		foreach ( var chest in _undergroundChests.Values )
		{
			if ( chest.IsValid() )
				chest.Destroy();
		}
		_undergroundChests.Clear();
		_undergroundRoomsWorld = null;
	}

	public UndergroundPoint UndergroundAt( Vector3 pointEngine )
	{
		if ( !VoxelReady || UndergroundSeedOrNull is not { } seed )
			return UndergroundPoint.Surface;
		var m = TerrainWorldUnits.EngineToMeters( pointEngine );
		if ( _voxelColumns.TryGetValue( VoxelCoordAt( m.x, m.y ), out var column ) )
			return column.Set.Field.UndergroundAt( m );
		// Undug heightfield: depth under the meshed ground
		return TryHeightfieldZMeters( m.x, m.y, out var groundZ ) ? Underground.Sample( seed, m.x, m.y, m.z, groundZ - m.z ) : UndergroundPoint.Surface;
	}

	/// <summary>Usage: <c>underground_info</c> — the layers, the column under the camera (depth, rooms carved) and the rooms found in this world; <c>underground_info reload</c> re-reads data/underground.json first.</summary>
	[ConCmd( "underground_info" )]
	public static void ConCmdUndergroundInfo( string arg = "" )
	{
		if ( string.Equals( arg, "reload", StringComparison.OrdinalIgnoreCase ) )
			Underground.Reload();
		Log.Info( $"[Underground] layers of {Underground.LayerThicknessMeters:0} m, bedrock from {Underground.BedrockDepthMeters:0} m down" );
		for ( var i = 0; i < Underground.Layers.Count; i++ )
		{
			var l = Underground.Layers[i];
			Log.Info( $"[Underground]   {i}: {l.Id} ({i * Underground.LayerThicknessMeters:0}–{(i + 1) * Underground.LayerThicknessMeters:0} m) pickaxe tier {l.PickaxeTier}, base {l.Base}, pockets {string.Join( ", ", l.Pockets.Select( p => $"{p.ResourceId} {p.Share * 100f:0.#}%" ) )}, room chance {l.RoomChance:0.##}" );
		}
		var world = Sandbox.Game.ActiveScene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		if ( world is null || !world.VoxelReady || !world.TryGetStreamTransform( out var streamEngine, out _ ) )
			return;
		world.EnsureUndergroundRoomsLoaded();
		var p = TerrainWorldUnits.EngineToMeters( streamEngine );
		var coord = world.VoxelCoordAt( p.x, p.y );
		var cs = world.VoxelChunkSize;
		var minX = -world._loadSettings.TotalWorldRadiusMeters + (coord.X * cs);
		var minY = -world._loadSettings.TotalWorldRadiusMeters + (coord.Y * cs);
		for ( var layer = 0; layer < Underground.Layers.Count; layer++ )
		{
			if ( Underground.TryRoom( world._loadSettings.WorldSeed, coord.X, coord.Y, layer, minX, minY, cs, out var room ) )
				Log.Info( $"[Underground] chunk {coord} layer {layer}: room {room.Key} at {room.CenterXy.x:0.#},{room.CenterXy.y:0.#}, floor {room.FloorDepthMeters:0} m under the surface, {room.SizeMeters.x:0.#}×{room.SizeMeters.y:0.#}×{room.SizeMeters.z:0.#} m{(world._undergroundFoundRooms.Contains( room.Key ) ? " (found)" : "")}" );
			else
				Log.Info( $"[Underground] chunk {coord} layer {layer}: no room" );
		}
		if ( world._voxelColumns.TryGetValue( coord, out var column ) )
			Log.Info( $"[Underground] column {coord}: floor {column.Set.Field.OriginMeters.z:0.#} m ({column.MinGroundZ - column.Set.Field.OriginMeters.z:0.#} m under its lowest ground), {column.Rooms.Count} rooms carved, {world._undergroundChests.Count} chests standing, {world._undergroundFoundRooms.Count} rooms found in this world" );
	}
}
