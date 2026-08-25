using Sandbox;

namespace Survival;

/// <summary>
/// Solid size matches <see cref="BuildPieceModelCache"/> — the same extents snap corners use.
/// Preview ghosts stay fully disabled (no physics thrash while aiming).
/// </summary>
static class BuildPieceCollider
{
	public static void Ensure( GameObject instance, string pieceId, bool previewGhost )
	{
		if ( instance is null || !instance.IsValid() )
			return;

		if ( previewGhost )
		{
			DisableAll( instance );
			return;
		}

		if ( string.IsNullOrWhiteSpace( pieceId ) )
			return;

		if ( BuildPieceVisual.UsesMeshCollision( pieceId ) && TryApplyMeshCollider( instance, pieceId ) )
			return;

		// Same extents the snap corners use, turned into the frame the mesh occupies, so the solid
		// and the seams can never disagree.
		var size = BuildColliderSnap.GetColliderSizeInMeshFrame( pieceId );

		// One solid: box sized to the same bounds as snaps. Prefer this over ModelCollider so
		// empty PhysicsShapeList vmdls still block, and we never fight a second shape.
		foreach ( var col in instance.Components.GetAll<ModelCollider>( FindMode.EverythingInSelf ) )
		{
			if ( col is not null )
				col.Enabled = false;
		}

		var box = instance.Components.Get<BoxCollider>() ?? instance.Components.Create<BoxCollider>();
		// Origin-centred like the snap corners — see BuildColliderSnap.GetCornerSnapLocal.
		box.Center = Vector3.Zero;
		box.Scale = size;
		box.Static = true;
		box.IsTrigger = false;
		box.Enabled = true;
	}

	/// <summary>
	/// Solid straight from the vmdl for pieces a box cannot describe (see
	/// <see cref="BuildPieceVisual.UsesMeshCollision"/>). The pitched roof plane and the folded hip /
	/// valley corners carry their shape in the mesh, so the mesh is the only honest solid: a box on
	/// the yaw-only root stays flat under a leaning roof, and fills a corner's open underside.
	/// Returns false when the model has no usable physics geometry so the caller keeps the box.
	/// </summary>
	static bool TryApplyMeshCollider( GameObject instance, string pieceId )
	{
		// All three roof vmdls declare PhysicsMeshFromRender; a model that ever loses it falls back to the box.
		if ( !BuildPieceModelCache.TryGetModel( pieceId, out var model ) )
			return false;

		foreach ( var box in instance.Components.GetAll<BoxCollider>( FindMode.EverythingInSelf ) )
		{
			if ( box is not null )
				box.Enabled = false;
		}

		var mesh = instance.Components.Get<ModelCollider>() ?? instance.Components.Create<ModelCollider>();
		mesh.Model = model;
		mesh.Static = true;
		mesh.IsTrigger = false;
		mesh.Enabled = true;
		return true;
	}

	static void DisableAll( GameObject root )
	{
		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null )
				continue;

			col.Enabled = false;
			col.IsTrigger = true;
			col.Static = false;
		}
	}
}
