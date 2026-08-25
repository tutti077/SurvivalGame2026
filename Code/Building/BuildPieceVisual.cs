using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Wires kit vmdls onto build instances at authored scale. Door also gets a static leaf child.
/// </summary>
static class BuildPieceVisual
{
	public const string VisualChildName = "Visual";
	public const string DoorLeafChildName = "DoorLeaf";
	public const string DoorLeafModelPath = "models/building/build_wood_door_leaf.vmdl";

	public static void Ensure( GameObject instance, string pieceId )
	{
		if ( instance is null || !instance.IsValid() || string.IsNullOrWhiteSpace( pieceId ) )
			return;

		if ( !TryGetModelPath( pieceId, out var modelPath ) )
			return;

		var model = Model.Load( modelPath );
		if ( model is null || !model.IsValid() )
			return;

		RemoveDevBoxRenderer( instance );

		var rootRenderer = instance.Components.Get<ModelRenderer>();
		if ( rootRenderer is not null )
			rootRenderer.Destroy();

		var visual = FindOrCreateChild( instance, VisualChildName );
		visual.LocalPosition = Vector3.Zero;
		visual.LocalScale = Vector3.One;
		visual.LocalRotation = Rotation.Identity;

		var renderer = visual.Components.Get<ModelRenderer>() ?? visual.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.On;
		ApplyCatalogTint( renderer, pieceId );

		if ( BuildPieceFamily.IsDoor( pieceId ) )
			EnsureStaticDoorLeaf( instance, pieceId );
	}

	public static bool TryGetModelPath( string pieceId, out string path )
	{
		path = string.Empty;
		if ( string.IsNullOrWhiteSpace( pieceId ) || !pieceId.StartsWith( "build_wood_", StringComparison.OrdinalIgnoreCase ) )
			return false;

		path = $"models/building/{pieceId.ToLowerInvariant()}.vmdl";
		return true;
	}

	/// <summary>
	/// Pieces whose solid is not an axis-aligned box in the root frame, so a <see cref="BoxCollider"/>
	/// cannot describe them:
	/// <list type="bullet">
	/// <item>the pitched plane roof — the mesh is pre-pitched and the root is yaw-only, so a box
	/// stays flat while the roof leans;</item>
	/// <item>the hip / valley corners — folded shells that a full module cube fills in solid;</item>
	/// <item>stairs — a box is a ramp-shaped hole filled in, so you collide with the flight as one
	/// block and cannot walk up the steps. The treads are in the mesh, so the mesh is the solid;</item>
	/// <item>triangles — the gable walls and the triangle floor. A box fills in the half the
	/// hypotenuse cuts away, so a 45° gable tucked under a roof stands proud of the slope and stops
	/// you walking off it.</item>
	/// </list>
	/// All of these vmdls declare PhysicsMeshFromRender, which is what gets used.
	/// </summary>
	public static bool UsesMeshCollision( string pieceId ) =>
		BuildPieceFamily.IsRoof( pieceId )
		|| BuildPieceFamily.IsStairs( pieceId )
		|| BuildSnapLayout.GetKind( pieceId ) == BuildSnapLayoutKind.TriangleCorners;

	/// <summary>Pitch is baked into the FBX — root stays yaw-only; snap math adds prefab pitch.</summary>
	public static bool UsesBakedMeshRotation( string pieceId ) =>
		( BuildPieceFamily.IsRoof( pieceId ) && !BuildPieceFamily.IsCorner( pieceId ) )
		|| ( BuildPieceFamily.IsBeam( pieceId ) && pieceId.Contains( "45", StringComparison.OrdinalIgnoreCase ) );

	static void EnsureStaticDoorLeaf( GameObject instance, string pieceId )
	{
		var leafModel = Model.Load( DoorLeafModelPath );
		if ( leafModel is null || !leafModel.IsValid() )
			return;

		// Sit the leaf at the frame opening centre from the frame model's bounds.
		var frameHalf = BuildPieceModelCache.GetHalfExtents( pieceId );
		var frameCenter = BuildPieceModelCache.GetCenter( pieceId );
		var leafHalf = leafModel.Bounds.Size * 0.5f;

		var leafGo = FindOrCreateChild( instance, DoorLeafChildName );
		leafGo.LocalPosition = frameCenter + new Vector3( 0f, 0f, -frameHalf.z + leafHalf.z );
		leafGo.LocalRotation = Rotation.Identity;
		leafGo.LocalScale = Vector3.One;

		var renderer = leafGo.Components.Get<ModelRenderer>() ?? leafGo.Components.Create<ModelRenderer>();
		renderer.Model = leafModel;
		renderer.RenderType = ModelRenderer.ShadowRenderType.On;
		ApplyCatalogTint( renderer, pieceId );
	}

	static void RemoveDevBoxRenderer( GameObject instance )
	{
		var rootRenderer = instance.Components.Get<ModelRenderer>();
		if ( rootRenderer is null || !rootRenderer.IsValid() )
			return;

		var resourcePath = rootRenderer.Model?.ResourcePath ?? string.Empty;
		if ( resourcePath.Contains( "dev/box", StringComparison.OrdinalIgnoreCase ) )
			rootRenderer.Destroy();
	}

	static GameObject FindOrCreateChild( GameObject parent, string name )
	{
		foreach ( var child in parent.Children )
		{
			if ( child.IsValid() && child.Name == name )
				return child;
		}

		var go = new GameObject( true, name );
		go.Parent = parent;
		return go;
	}

	static void ApplyCatalogTint( ModelRenderer renderer, string pieceId )
	{
		if ( !BuildPieceCatalog.TryGet( pieceId, out var data ) )
			return;

		renderer.Tint = BuildPieceCatalog.ParseFallbackColor( data.FallbackColor );
	}
}
