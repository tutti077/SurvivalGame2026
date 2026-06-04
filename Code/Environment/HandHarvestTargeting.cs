using System;
using Sandbox;

namespace Survival;

/// <summary>Shared hand-harvest focus rules for client prompt + host validation.</summary>
public static class HandHarvestTargeting
{
	/// <summary>Fast range + aim cone check without a scene trace.</summary>
	public static bool PassesQuickFocusCheck(
		ResourceItemDefinition node,
		GameObject harvesterRoot,
		Vector3 viewPosition,
		Vector3 viewLookDirection,
		float lookConeDegrees )
	{
		if ( node is null || !node.GameObject.IsValid() || harvesterRoot is null || !harvesterRoot.IsValid() )
			return false;

		if ( node.ToolTypeRequired != HarvestToolType.Hand || node.IsDepleted )
			return false;

		if ( !node.CanHarvestWith( HarvestToolType.Hand, 0 ) )
			return false;

		var nodePos = node.GameObject.WorldPosition;
		var pawnPos = harvesterRoot.WorldPosition;
		var range = Math.Max( 1f, node.HandHarvestRange );
		if ( Vector3.DistanceBetween( pawnPos, nodePos ) > range + 4f )
			return false;

		viewLookDirection = viewLookDirection.Normal;
		if ( viewLookDirection.LengthSquared < 1e-8f )
			return false;

		var toNodeFromView = nodePos - viewPosition;
		if ( toNodeFromView.LengthSquared <= 1e-8f )
			return true;

		var cone = Math.Clamp( lookConeDegrees, 1f, 89f );
		var minDot = MathF.Cos( cone * MathF.PI / 180f );
		return Vector3.Dot( viewLookDirection, toNodeFromView.Normal ) >= minDot;
	}

	/// <summary>
	/// Validates hand harvest: pawn within range, view direction toward node, clear line from pawn eye to node.
	/// Uses pawn position for range/LOS (works in third person); uses view position + look for aim cone.
	/// </summary>
	public static bool TryValidateFocus(
		ResourceItemDefinition node,
		GameObject harvesterRoot,
		Vector3 viewPosition,
		Vector3 viewLookDirection,
		float lookConeDegrees,
		float pawnEyeHeight,
		out string failReason )
	{
		failReason = null;

		if ( !PassesQuickFocusCheck( node, harvesterRoot, viewPosition, viewLookDirection, lookConeDegrees ) )
		{
			failReason = "not in range or facing";
			return false;
		}

		var scene = node.GameObject.Scene.IsValid() ? node.GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			failReason = "no scene";
			return false;
		}

		var pawnPos = harvesterRoot.WorldPosition;
		var nodePos = node.GameObject.WorldPosition;
		var eyeHeight = Math.Max( 0f, pawnEyeHeight );
		var losStart = pawnPos + Vector3.Up * eyeHeight;

		if ( !HasClearLineToNode( scene, losStart, nodePos, harvesterRoot, node, out failReason ) )
			return false;

		return true;
	}

	static bool HasClearLineToNode( Scene scene, Vector3 from, Vector3 to, GameObject harvesterRoot, ResourceItemDefinition node, out string failReason )
	{
		failReason = null;

		var delta = to - from;
		if ( delta.LengthSquared <= 1e-6f )
			return true;

		var tr = scene.Trace.Ray( from, to ).IgnoreGameObjectHierarchy( harvesterRoot ).Run();
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
			return true;

		if ( ResourceHarvestTrace.TryFindOnHierarchy( tr.GameObject, out var hitNode ) && hitNode == node )
			return true;

		if ( Vector3.DistanceBetween( tr.HitPosition, to ) <= 20f
		     && ResourceHarvestTrace.TryFindOnHierarchy( tr.GameObject, out hitNode )
		     && hitNode == node )
			return true;

		failReason = "blocked line of sight";
		return false;
	}
}
