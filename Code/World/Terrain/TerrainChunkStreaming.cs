namespace Survival;

/// <summary>Square neighborhood + optional forward-cone chunk selection around the stream camera.</summary>
public static class TerrainChunkStreaming
{
	/// <summary>Loads a square of chunks centered on the stream position (radius 1 = 3×3).</summary>
	public static void CollectSquareChunks(
		Vector3 streamPos,
		TerrainPreviewSettings settings,
		float chunkSizeMeters,
		int radiusChunks,
		HashSet<TerrainChunkCoord> needed )
	{
		needed.Clear();

		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		radiusChunks = Math.Clamp( radiusChunks, 0, 32 );

		var worldRadius = settings.WorldRadiusMeters;
		var center = WorldToChunkCoord( streamPos.x, streamPos.y, worldRadius, chunkSizeMeters );

		for ( var dz = -radiusChunks; dz <= radiusChunks; dz++ )
		{
			for ( var dx = -radiusChunks; dx <= radiusChunks; dx++ )
			{
				var coord = new TerrainChunkCoord( center.X + dx, center.Y + dz );
				if ( IsChunkInsideWorld( coord, settings, chunkSizeMeters ) )
					needed.Add( coord );
			}
		}
	}

	public static void CollectNeededChunks(
		Vector3 streamPos,
		Rotation viewRotation,
		TerrainPreviewSettings settings,
		float chunkSizeMeters,
		float forwardViewDistanceMeters,
		float forwardViewConeDegrees,
		int sideViewRadiusChunks,
		HashSet<TerrainChunkCoord> needed )
	{
		needed.Clear();

		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		forwardViewDistanceMeters = Math.Max( chunkSizeMeters, forwardViewDistanceMeters );
		sideViewRadiusChunks = Math.Clamp( sideViewRadiusChunks, 0, 32 );
		forwardViewConeDegrees = Math.Clamp( forwardViewConeDegrees, 10f, 360f );

		var worldRadius = settings.WorldRadiusMeters;
		var forward = viewRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = Vector3.Forward;
		else
			forward = forward.Normal;

		var forwardRadiusChunks = (int)MathF.Ceiling( forwardViewDistanceMeters / chunkSizeMeters );
		var searchRadiusChunks = Math.Max( forwardRadiusChunks, sideViewRadiusChunks );
		var center = WorldToChunkCoord( streamPos.x, streamPos.y, worldRadius, chunkSizeMeters );
		var halfCone = forwardViewConeDegrees * 0.5f;
		var sideDistanceMeters = sideViewRadiusChunks * chunkSizeMeters;

		for ( var dz = -searchRadiusChunks; dz <= searchRadiusChunks; dz++ )
		{
			for ( var dx = -searchRadiusChunks; dx <= searchRadiusChunks; dx++ )
			{
				var coord = new TerrainChunkCoord( center.X + dx, center.Y + dz );
				if ( !IsChunkInsideWorld( coord, settings, chunkSizeMeters ) )
					continue;

				if ( IsChunkNeeded(
					coord,
					streamPos,
					worldRadius,
					forward,
					chunkSizeMeters,
					forwardViewDistanceMeters,
					sideDistanceMeters,
					halfCone ) )
				{
					needed.Add( coord );
				}
			}
		}
	}

	public static Vector3 GetChunkCenterWorld( TerrainChunkCoord coord, float worldRadius, float chunkSizeMeters )
	{
		var minX = -worldRadius + (coord.X * chunkSizeMeters);
		var minY = -worldRadius + (coord.Y * chunkSizeMeters);
		return new Vector3( minX + (chunkSizeMeters * 0.5f), minY + (chunkSizeMeters * 0.5f), 0f );
	}

	static bool IsChunkNeeded(
		TerrainChunkCoord coord,
		Vector3 streamPos,
		float worldRadius,
		Vector3 forward,
		float chunkSizeMeters,
		float forwardViewDistanceMeters,
		float sideDistanceMeters,
		float halfConeDegrees )
	{
		var chunkCenter = GetChunkCenterWorld( coord, worldRadius, chunkSizeMeters );
		var toChunk = new Vector3( chunkCenter.x - streamPos.x, chunkCenter.y - streamPos.y, 0f );
		var distance = toChunk.Length;

		if ( distance <= sideDistanceMeters + (chunkSizeMeters * 0.5f) )
			return true;

		if ( distance > forwardViewDistanceMeters + chunkSizeMeters )
			return false;

		if ( toChunk.LengthSquared < 1e-6f )
			return true;

		var dir = toChunk / distance;
		var dot = Math.Clamp( Vector3.Dot( forward, dir ), -1f, 1f );
		var angle = MathF.Acos( dot ) * (180f / MathF.PI );
		return angle <= halfConeDegrees;
	}

	/// <summary>Mesh only the chunk under the stream position plus neighbors when near a chunk edge.</summary>
	public static void CollectMeshChunks(
		Vector3 streamPos,
		TerrainPreviewSettings settings,
		float chunkSizeMeters,
		float borderPrefetch01,
		HashSet<TerrainChunkCoord> meshNeeded )
	{
		meshNeeded.Clear();

		chunkSizeMeters = Math.Max( 16f, chunkSizeMeters );
		borderPrefetch01 = Math.Clamp( borderPrefetch01, 0.05f, 0.5f );

		var worldRadius = settings.WorldRadiusMeters;
		var center = WorldToChunkCoord( streamPos.x, streamPos.y, worldRadius, chunkSizeMeters );
		if ( !IsChunkInsideWorld( center, settings, chunkSizeMeters ) )
			return;

		meshNeeded.Add( center );

		var chunkMinX = -worldRadius + (center.X * chunkSizeMeters);
		var chunkMinY = -worldRadius + (center.Y * chunkSizeMeters);
		var localX = streamPos.x - chunkMinX;
		var localY = streamPos.y - chunkMinY;
		var borderDistance = chunkSizeMeters * borderPrefetch01;

		TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, -1, 0, localX < borderDistance );
		TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, 1, 0, localX > chunkSizeMeters - borderDistance );
		TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, 0, -1, localY < borderDistance );
		TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, 0, 1, localY > chunkSizeMeters - borderDistance );

		var nearWest = localX < borderDistance;
		var nearEast = localX > chunkSizeMeters - borderDistance;
		var nearSouth = localY < borderDistance;
		var nearNorth = localY > chunkSizeMeters - borderDistance;
		if ( nearWest && nearSouth )
			TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, -1, -1, true );
		if ( nearEast && nearSouth )
			TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, 1, -1, true );
		if ( nearWest && nearNorth )
			TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, -1, 1, true );
		if ( nearEast && nearNorth )
			TryAddNeighborMeshChunk( meshNeeded, settings, chunkSizeMeters, center, 1, 1, true );
	}

	static void TryAddNeighborMeshChunk(
		HashSet<TerrainChunkCoord> meshNeeded,
		TerrainPreviewSettings settings,
		float chunkSizeMeters,
		TerrainChunkCoord center,
		int deltaX,
		int deltaY,
		bool shouldAdd )
	{
		if ( !shouldAdd )
			return;

		var coord = new TerrainChunkCoord( center.X + deltaX, center.Y + deltaY );
		if ( IsChunkInsideWorld( coord, settings, chunkSizeMeters ) )
			meshNeeded.Add( coord );
	}

	public static TerrainChunkCoord WorldToChunkCoord( float worldX, float worldY, float worldRadius, float chunkSize )
	{
		var chunkX = (int)MathF.Floor( (worldX + worldRadius) / chunkSize );
		var chunkY = (int)MathF.Floor( (worldY + worldRadius) / chunkSize );
		return new TerrainChunkCoord( chunkX, chunkY );
	}

	public static bool IsChunkInsideWorld( TerrainChunkCoord coord, TerrainPreviewSettings settings, float chunkSize )
	{
		var radius = settings.WorldRadiusMeters;
		var minX = -radius + (coord.X * chunkSize);
		var minY = -radius + (coord.Y * chunkSize);
		var maxX = minX + chunkSize;
		var maxY = minY + chunkSize;

		if ( maxX < -radius || minX > radius || maxY < -radius || minY > radius )
			return false;

		var centerX = (minX + maxX) * 0.5f;
		var centerY = (minY + maxY) * 0.5f;
		return MathF.Sqrt( (centerX * centerX) + (centerY * centerY) ) <= radius + chunkSize;
	}
}
