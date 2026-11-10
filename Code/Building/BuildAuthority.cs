using System;
using Sandbox;

namespace Survival;

public static class BuildAuthority
{
	public static bool TryPlacePiece(
		GameObject placer,
		string pieceId,
		Transform transform,
		bool blueprint,
		out GameObject spawned )
	{
		spawned = null;
		if ( !placer.IsValid() || string.IsNullOrWhiteSpace( pieceId ) )
			return false;

		// Broken build hammer cannot place — repair it at a workbench first.
		if ( ToolDurability.IsActiveToolBroken( placer ) )
			return false;

		if ( !BuildPieceCatalog.TryGet( pieceId, out var data ) || string.IsNullOrWhiteSpace( data.Prefab ) )
			return false;

		var scene = placer.Scene.IsValid() ? placer.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		if ( !HostPlacePiece( scene, pieceId, transform, blueprint, out spawned ) )
			return false;

		if ( spawned is null || !spawned.IsValid() )
		{
			// Nothing was actually built — the swing is free (durability only ticks on real effect).
			spawned = null;
			return true;
		}

		// Build hammer durability: 1 tick per placement that actually stood.
		ToolDurability.HostAddWearToActiveTool( placer );

		if ( !blueprint )
		{
			placer.Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.PieceBuilt, pieceId );
			PlaytestLog.HostBuilt( placer, pieceId );
		}

		return true;
	}

	/// <summary>
	/// Host: spawn a catalog piece into the world with every bit of bookkeeping a placement needs —
	/// configure, NetworkSpawn, snap cache, nav rebake and the structural solve. No pawn, no cost,
	/// no durability: this is the shared tail of hammer placement and world-authored structures
	/// (enemy camps). Returns false when the piece could not be spawned at all; returns true with an
	/// invalid <paramref name="spawned"/> when the structural solver collapsed it immediately.
	/// </summary>
	public static bool HostPlacePiece( Scene scene, string pieceId, Transform transform, bool blueprint, out GameObject spawned )
	{
		spawned = null;
		if ( !scene.IsValid() || string.IsNullOrWhiteSpace( pieceId ) )
			return false;

		if ( !BuildPieceCatalog.TryGet( pieceId, out var data ) || string.IsNullOrWhiteSpace( data.Prefab ) )
			return false;

		spawned = BuildPrefabUtility.SpawnPiece( scene, data.Prefab, pieceId, transform );
		if ( spawned is null || !spawned.IsValid() )
		{
			Log.Warning( $"[BuildAuthority] Missing prefab '{data.Prefab}' for piece '{pieceId}'." );
			spawned = null;
			return false;
		}

		if ( data.Vehicle )
		{
			// A vehicle is a physics object the hammer merely drops into the world: no BuildPiece, no
			// static collider, no nav rebake, no structural solve. Host-owned until a driver sits down.
			spawned.Name = pieceId;
			HostNetworkSpawn.TrySpawn( spawned );
			return true;
		}

		spawned.Name = $"build_{pieceId}";

		// A piece standing on a buildable deck (the hover platform) is parented under that vehicle before
		// it is configured: its colliders then join the hull's body instead of being static, the structural
		// solver treats it as grounded, and it rides along. Decided once here, on the host, per placement.
		var deck = Vehicle.FindBuildHostUnder( scene, spawned, pieceId );
		if ( deck is not null )
			spawned.SetParent( deck.GameObject, true );

		var piece = spawned.Components.Get<BuildPiece>() ?? spawned.Components.Create<BuildPiece>();
		piece.Configure( pieceId, blueprint, previewGhost: false );

		// Host NetworkSpawn so remotes see placed walls/chests (prefabs are already Object mode).
		HostNetworkSpawn.TrySpawn( spawned );

		BuildSnapPlacement.InvalidatePieceCache();
		// Nothing on a moving deck is nav-walkable for entities; the world bake leaves vehicles alone.
		// A blueprint is not an obstacle yet either — its bake comes when it is built.
		if ( deck is null && !blueprint )
			BuildNavMeshSync.OnBuildPieceChanged( scene, spawned );

		// Valheim-style: placement always succeeds, then the solver may collapse the piece
		// (and anything that only stood because of intermediate state) immediately.
		BuildStructuralIntegrity.HostOnPlaced( piece );
		return true;
	}

	/// <summary>Hammer repair tool: click a blueprint to finish it. Costs one hammer durability tick.</summary>
	public static bool TryRepairBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || ToolDurability.IsActiveToolBroken( placer ) )
			return false;

		if ( !HostFinishBlueprint( placer, target ) )
			return false;

		// Build hammer durability: 1 tick per successful structure repair.
		ToolDurability.HostAddWearToActiveTool( placer );
		return true;
	}

	/// <summary>
	/// "E — Build" on a blueprint: any player, no hammer needed. The client already checked reach
	/// for its prompt; the host checks it once more here (with a metre of slack for the walk that
	/// happened while the RPC was in flight) so nobody finishes blueprints across the map.
	/// </summary>
	public static bool TryBuildBlueprint( GameObject builder, BuildPiece target, float reachMeters )
	{
		if ( !builder.IsValid() || target is null || !target.IsValid() )
			return false;

		if ( !IsWithinBlueprintReach( builder, target, reachMeters + 1f ) )
			return false;

		return HostFinishBlueprint( builder, target );
	}

	/// <summary>
	/// Reach from the pawn's eye to the nearest point of the piece — the whole piece counts, so a
	/// tall wall is buildable from the ground as long as any part of it is within reach. The same
	/// measure drives the prompt (owner) and the validation (host).
	/// </summary>
	public static bool IsWithinBlueprintReach( GameObject pawn, BuildPiece target, float reachMeters )
	{
		if ( !pawn.IsValid() || target is null || !target.IsValid() )
			return false;

		var controller = pawn.Components.Get<PlayerController>();
		var eyeHeight = controller is not null && controller.IsValid()
			? Math.Max( 8f, controller.BodyHeight - controller.EyeDistanceFromTop )
			: 64f;
		var eye = pawn.WorldPosition + Vector3.Up * eyeHeight;

		var bounds = target.GameObject.GetBounds();
		var nearest = bounds.Size.LengthSquared < 1f ? target.GameObject.WorldPosition : bounds.ClosestPoint( eye );
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		return Vector3.DistanceBetween( eye, nearest ) <= reach;
	}

	/// <summary>
	/// Host: blueprint to built. The shared tail of the hammer repair click and the E prompt: flips
	/// the synced flag (every client's ghost turns solid-coloured through the change callback),
	/// rebakes nav for the now-real obstacle and credits the builder exactly like a direct placement.
	/// </summary>
	static bool HostFinishBlueprint( GameObject builder, BuildPiece target )
	{
		if ( target is null || !target.IsValid() || target.IsPreviewGhost || !target.IsBlueprint )
			return false;

		// Placing a blueprint is free; this is the moment the materials leave the bag.
		if ( !HostChargeMaterials( builder, target.PieceId ) )
			return false;

		target.Configure( target.PieceId, blueprint: false, previewGhost: false );
		if ( !Vehicle.IsDeckMounted( target.GameObject ) )
			BuildNavMeshSync.OnBuildPieceChanged( target.Scene, target.GameObject );

		if ( builder.IsValid() )
		{
			builder.Components.Get<PlayerQuests>()?.HostReport( QuestEventIds.PieceBuilt, target.PieceId );
			PlaytestLog.HostBuilt( builder, target.PieceId );
		}

		return true;
	}

	/// <summary>
	/// What the builder's bag is short of for this piece, as "3 Wood, 1 Stone" — empty when it is
	/// affordable, costs nothing, or Free build is on. Drives the E prompt; the host charges through
	/// <see cref="HostChargeMaterials"/> on the same catalog costs.
	/// </summary>
	public static string DescribeMissingMaterials( GameObject builder, string pieceId )
	{
		if ( BuildSettings.FreeBuild || !BuildPieceCatalog.TryGet( pieceId, out var data ) || data.Costs is null || data.Costs.Count == 0 )
			return string.Empty;

		var inventory = builder.IsValid() ? builder.Components.Get<PlayerInventory>() : null;
		if ( inventory is null )
			return string.Empty;

		System.Text.StringBuilder missing = null;
		for ( var i = 0; i < data.Costs.Count; i++ )
		{
			var cost = data.Costs[i];
			if ( cost is null || string.IsNullOrWhiteSpace( cost.ResourceId ) || cost.Amount <= 0 )
				continue;

			var short_ = cost.Amount - inventory.CountResource( cost.ResourceId );
			if ( short_ <= 0 )
				continue;

			missing ??= new System.Text.StringBuilder();
			if ( missing.Length > 0 )
				missing.Append( ", " );
			missing.Append( short_ ).Append( ' ' ).Append( ResourceCatalog.Resolve( cost.ResourceId ).DisplayName );
		}

		return missing?.ToString() ?? string.Empty;
	}

	/// <summary>Host: take the piece's catalog costs out of the builder's bag + hotbar, all or nothing.</summary>
	static bool HostChargeMaterials( GameObject builder, string pieceId )
	{
		if ( BuildSettings.FreeBuild || !BuildPieceCatalog.TryGet( pieceId, out var data ) || data.Costs is null || data.Costs.Count == 0 )
			return true;

		var inventory = builder.IsValid() ? builder.Components.Get<PlayerInventory>() : null;
		if ( inventory is null )
			return false;

		var ingredients = new System.Collections.Generic.List<CraftingIngredient>( data.Costs.Count );
		for ( var i = 0; i < data.Costs.Count; i++ )
		{
			var cost = data.Costs[i];
			if ( cost is null || string.IsNullOrWhiteSpace( cost.ResourceId ) || cost.Amount <= 0 )
				continue;

			ingredients.Add( new CraftingIngredient { ResourceId = cost.ResourceId, Amount = cost.Amount } );
		}

		return ingredients.Count == 0 || inventory.HostTryConsumeResources( ingredients );
	}

	public static bool TryDestroyBuildPiece( GameObject placer, BuildPiece target )
	{
		if ( !placer.IsValid() || target is null || !target.IsValid() || target.IsPreviewGhost )
			return false;

		HostRemovePiece( target );
		return true;
	}

	/// <summary>
	/// Host: take a placed piece out of the world with every bit of bookkeeping a removal needs —
	/// snap cache, nav rebake (which also tells entities the structure changed) and the structural
	/// re-solve that collapses whatever the piece solely held up. Hammer demolish and entity
	/// damage (<see cref="BuildPiece.HostApplyDamage"/>) both end here.
	/// </summary>
	public static void HostRemovePiece( BuildPiece target )
	{
		if ( target is null || !target.IsValid() || target.IsPreviewGhost )
			return;

		var scene = target.Scene;
		var removedRoot = target.GameObject;
		var bounds = target.GameObject.GetBounds();
		if ( bounds.Size.LengthSquared < 1f )
			bounds = BBox.FromPositionAndSize( target.GameObject.WorldPosition, 120f );

		var deckMounted = Vehicle.IsDeckMounted( removedRoot );
		target.GameObject.Destroy();
		BuildSnapPlacement.InvalidatePieceCache();
		if ( scene.IsValid() && !deckMounted )
			BuildNavMeshSync.OnBuildPieceBoundsChanged( scene, bounds );

		// Re-solve what the piece used to touch — collapses anything it solely held up. The root is
		// passed so the deferred-destroyed piece can't keep supporting the structure this frame.
		BuildStructuralIntegrity.HostOnRemoved( scene, bounds, removedRoot );
	}
}
