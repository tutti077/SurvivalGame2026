using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Host: stamps a <see cref="PoiLayoutData"/> into the world - every build piece through
/// <see cref="BuildAuthority.HostPlacePiece"/> (the shared tail of hammer placement: NetworkSpawn, snap cache, nav rebake,
/// structural solve; no pawn, no cost), then the loot of its containers. The generated world calls it from
/// <see cref="PoiSites"/>; <c>poi_spawn</c> (dev) drops one in front of the camera.
/// </summary>
public static class PoiSpawner
{
	/// <summary>Layout-local metres -> world offset (units) rotated into the structure's facing. Layouts are written at the
	/// build kit's scale (<see cref="BuildModuleDimensions.UnitsPerMeter"/>, 40 u/m): a 2 m floor is exactly one tile.</summary>
	public static Vector3 ToWorldOffset( float xMeters, float yMeters, float zMeters, Rotation facing )
	{
		var unit = BuildModuleDimensions.UnitsPerMeter;
		return facing * new Vector3( xMeters * unit, yMeters * unit, zMeters * unit );
	}

	/// <summary>
	/// Spawn <paramref name="layoutId"/> with its origin dropped onto the ground under <paramref name="origin"/>. Returns the
	/// pieces that stand (empty = nothing was spawned). <paramref name="seedKey"/> seeds the chest loot: the same key gives
	/// the same contents.
	/// </summary>
	public static List<GameObject> TrySpawn( Scene scene, string layoutId, Vector3 origin, float yawDegrees, string seedKey )
	{
		var pieces = new List<GameObject>();
		if ( !scene.IsValid() )
			return pieces;

		if ( !PoiCatalog.TryGetLayout( layoutId, out var layout ) )
		{
			Log.Warning( $"[PoiSpawner] Unknown layout '{layoutId}'." );
			return pieces;
		}

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var ground = FindGround( scene, origin );
		var facing = Rotation.FromYaw( yawDegrees );
		for ( var i = 0; i < layout.Pieces.Count; i++ )
		{
			var piece = layout.Pieces[i];
			if ( piece is null || string.IsNullOrWhiteSpace( piece.PieceId ) )
				continue;

			var rotation = Rotation.FromYaw( yawDegrees + piece.YawDegrees );
			var position = ground + ToWorldOffset( piece.X, piece.Y, piece.Z, facing );
			// z is where the piece's underside sits: lift its centre the same way a hammer-placed piece sits on terrain
			position += Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( piece.PieceId, rotation );

			if ( !BuildAuthority.HostPlacePiece( scene, piece.PieceId, new Transform( position, rotation ), blueprint: false, out var spawned ) )
			{
				Log.Warning( $"[PoiSpawner] Layout '{layout.Id}': failed to place piece '{piece.PieceId}'." );
				continue;
			}
			if ( !spawned.IsValid() )
			{
				Log.Warning( $"[PoiSpawner] Layout '{layout.Id}': piece {i} '{piece.PieceId}' collapsed (nothing under it?)." );
				continue;
			}

			pieces.Add( spawned );
			// drawn out to the far version's distance (PoiFarView), which hides once these stand
			if ( spawned.Components.Get<BuildPiece>() is { } buildPiece )
				buildPiece.WorldPoiPiece = true;
			if ( !string.IsNullOrWhiteSpace( piece.Loot ) )
				FillLoot( spawned, layout, piece.Loot, StableHash( $"{seedKey ?? layout.Id}:{i}" ) );
		}

		Log.Info( $"[PoiSpawner] '{layout.Id}' at {ground}: {pieces.Count}/{layout.Pieces.Count} pieces." );
		return pieces;
	}

	/// <summary>
	/// Every machine: the far version of a layout (<see cref="PoiFarView"/>) - each piece's models where <see cref="TrySpawn"/>
	/// would put the piece, render only: no colliders, no build pieces, no lights, not networked or saved. The origin is
	/// already on the ground (the planner's height), so there is no trace.
	/// </summary>
	public static GameObject BuildFarProxy( Scene scene, string layoutId, Vector3 origin, float yawDegrees )
	{
		if ( scene is null || !scene.IsValid() || !PoiCatalog.TryGetLayout( layoutId, out var layout ) )
			return null;

		var root = new GameObject( true, $"poi_far_{layout.Id}" );
		root.NetworkMode = NetworkMode.Never;
		root.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		root.WorldPosition = origin;

		var facing = Rotation.FromYaw( yawDegrees );
		foreach ( var piece in layout.Pieces )
		{
			if ( piece is null || string.IsNullOrWhiteSpace( piece.PieceId ) || !BuildPieceCatalog.TryGet( piece.PieceId, out var data ) )
				continue;

			var placement = Rotation.FromYaw( yawDegrees + piece.YawDegrees );
			var position = origin + ToWorldOffset( piece.X, piece.Y, piece.Z, facing )
				+ (Vector3.Up * BuildModuleDimensions.GetGroundSitHalfExtent( piece.PieceId, placement ));
			// the same root rotation BuildPrefabUtility.ApplyStandardPieceTransform gives the real piece
			var pitch = BuildPieceVisual.UsesBakedMeshRotation( piece.PieceId ) ? Rotation.Identity : BuildModuleDimensions.GetPrefabLocalRotation( piece.PieceId );
			var pieceXf = new Transform( position, Rotation.FromYaw( placement.Angles().yaw ) * pitch );

			if ( BuildPieceVisual.TryGetModelPath( piece.PieceId, out var modelPath ) )
			{
				// a kit piece: one model on the root, exactly as BuildPieceVisual wires the real one
				var renderer = AddProxyRenderer( root, pieceXf, Model.Load( modelPath ) );
				if ( renderer is not null )
					BuildPieceVisual.ApplyCatalogTint( renderer, piece.PieceId );
				continue;
			}

			// furniture: every enabled model of its prefab, at its place under the prefab root
			var template = BuildPrefabUtility.GetTemplate( data.Prefab );
			if ( template is null )
				continue;
			var templateXf = template.WorldTransform;
			foreach ( var source in template.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			{
				if ( source is null || !source.Enabled || source.Model is null )
					continue;
				var sourceXf = source.GameObject.WorldTransform;
				var local = new Transform( templateXf.PointToLocal( sourceXf.Position ), templateXf.Rotation.Inverse * sourceXf.Rotation, sourceXf.Scale );
				var world = new Transform( pieceXf.PointToWorld( local.Position ), pieceXf.Rotation * local.Rotation, local.Scale );
				var renderer = AddProxyRenderer( root, world, source.Model );
				if ( renderer is null )
					continue;
				renderer.Tint = source.Tint;
				if ( source.MaterialOverride is not null )
					renderer.MaterialOverride = source.MaterialOverride;
			}
		}

		return root;
	}

	static ModelRenderer AddProxyRenderer( GameObject root, Transform world, Model model )
	{
		if ( model is null || model.IsError )
			return null;
		var go = new GameObject( true, "far_piece" );
		go.NetworkMode = NetworkMode.Never;
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = root;
		go.WorldTransform = world;
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.On;
		return renderer;
	}

	/// <summary>Host: a POI chest takes its stacks from <paramref name="tableId"/> (<c>pois.json</c>, <c>lootTables</c>), once, when the structure is stamped.</summary>
	static void FillLoot( GameObject piece, PoiLayoutData layout, string tableId, int seed )
	{
		var container = piece.Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( container is null || !PoiCatalog.TryGetLootTable( tableId, out var table ) )
		{
			Log.Warning( $"[PoiSpawner] Layout '{layout.Id}': piece or loot table '{tableId}' cannot take loot." );
			return;
		}
		foreach ( var (resourceId, count) in table.Roll( seed ) )
			container.HostDepositStack( resourceId, count );
	}

	/// <summary>The same number for the same text in every session (string.GetHashCode is not).</summary>
	static int StableHash( string text )
	{
		var hash = 17;
		foreach ( var c in text )
			hash = unchecked( (hash * 31) + c );
		return hash;
	}

	/// <summary>Standable ground under <paramref name="near"/>; the point itself when nothing is below.</summary>
	static Vector3 FindGround( Scene scene, Vector3 near )
	{
		var from = near + Vector3.Up * 512f;
		var to = near + Vector3.Down * 4096f;
		var tr = scene.Trace.Ray( from, to ).Radius( 8f ).UsePhysicsWorld().WithoutTags( "player", "enemy", "buildpreview" ).Run();
		if ( !tr.Hit )
			tr = scene.Trace.Ray( from, to ).Radius( 8f ).WithoutTags( "player", "enemy", "buildpreview" ).Run();
		return !tr.Hit || tr.Normal.z < 0.35f ? near : tr.HitPosition;
	}
}
