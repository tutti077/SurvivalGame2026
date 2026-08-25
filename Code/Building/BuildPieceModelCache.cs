using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// One-time model bounds per piece id. Snap corners and solid size come from the authored
/// <c>.vmdl</c>, not from a hardcoded meter×50 guess or a stale prefab box.
/// </summary>
static class BuildPieceModelCache
{
	struct Entry
	{
		public Vector3 HalfExtents;
		public Vector3 Center;
		public Vector3 Size;
		public bool HasModel;
	}

	static readonly Dictionary<string, Entry> Cache = new( StringComparer.OrdinalIgnoreCase );

	public static void Invalidate() => Cache.Clear();

	public static Vector3 GetHalfExtents( string pieceId )
	{
		var entry = Resolve( pieceId );
		return entry.HalfExtents;
	}

	public static Vector3 GetCenter( string pieceId )
	{
		var entry = Resolve( pieceId );
		return entry.Center;
	}

	public static Vector3 GetSize( string pieceId )
	{
		var entry = Resolve( pieceId );
		return entry.Size;
	}

	public static bool TryGetModel( string pieceId, out Model model )
	{
		model = null;
		if ( !BuildPieceVisual.TryGetModelPath( pieceId, out var path ) )
			return false;

		model = Model.Load( path );
		return model is not null && model.IsValid();
	}

	static Entry Resolve( string pieceId )
	{
		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return Fallback( pieceId );

		if ( Cache.TryGetValue( pieceId, out var cached ) )
			return cached;

		var entry = BuildEntry( pieceId );
		Cache[pieceId] = entry;
		return entry;
	}

	static Entry BuildEntry( string pieceId )
	{
		if ( TryGetModel( pieceId, out var model ) )
		{
			var bounds = model.Bounds;
			var size = bounds.Size;
			// Degenerate / unloaded mesh — fall back to the size table.
			if ( size.x > 0.01f && size.y > 0.01f && size.z > 0.01f )
			{
				return new Entry
				{
					Size = size,
					HalfExtents = size * 0.5f,
					Center = bounds.Center,
					HasModel = true,
				};
			}
		}

		return Fallback( pieceId );
	}

	static Entry Fallback( string pieceId )
	{
		var size = BuildModuleDimensions.GetColliderScale( pieceId );
		return new Entry
		{
			Size = size,
			HalfExtents = size * 0.5f,
			Center = Vector3.Zero,
			HasModel = false,
		};
	}
}
