using System;
using Sandbox;

namespace Survival;

/// <summary>Shared hand-harvest focus rules for client prompt + host validation.</summary>
public static class HandHarvestTargeting
{
	/// <summary>
	/// Validates hand harvest: pawn within range, view direction toward node, clear line from pawn eye to node.
	/// Uses pawn position for range/LOS (works in third person); uses view position + look for aim cone.
	/// </summary>
	public static bool TryValidateFocus(
		ResourceHarvestNode node,
		GameObject harvesterRoot,
		Vector3 viewPosition,
		Vector3 viewLookDirection,
		float lookConeDegrees,
		float pawnEyeHeight,
		out string failReason )
	{
		failReason = null;

		if ( node is null || !node.GameObject.IsValid() || harvesterRoot is null || !harvesterRoot.IsValid() )
		{
			failReason = "invalid";
			return false;
		}

		if ( node.ToolTypeRequired != HarvestToolType.Hand || node.IsDepleted )
		{
			failReason = "not hand harvestable";
			return false;
		}

		if ( !node.CanHarvestWith( HarvestToolType.Hand, 0 ) )
		{
			failReason = "cannot harvest";
			return false;
		}

		var nodePos = node.GameObject.WorldPosition;
		var pawnPos = harvesterRoot.WorldPosition;
		var range = Math.Max( 1f, node.HandHarvestRange );
		var pawnDist = Vector3.DistanceBetween( pawnPos, nodePos );
		if ( pawnDist > range + 4f )
		{
			failReason = "out of range";
			return false;
		}

		viewLookDirection = viewLookDirection.Normal;
		if ( viewLookDirection.LengthSquared < 1e-8f )
		{
			failReason = "invalid look";
			return false;
		}

		var toNodeFromView = nodePos - viewPosition;
		if ( toNodeFromView.LengthSquared > 1e-8f )
		{
			var cone = Math.Clamp( lookConeDegrees, 1f, 89f );
			var minDot = MathF.Cos( cone * MathF.PI / 180f );
			if ( Vector3.Dot( viewLookDirection, toNodeFromView.Normal ) < minDot )
			{
				failReason = "not facing node";
				return false;
			}
		}

		var scene = node.GameObject.Scene.IsValid() ? node.GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			failReason = "no scene";
			return false;
		}

		var eyeHeight = Math.Max( 0f, pawnEyeHeight );
		var losStart = pawnPos + Vector3.Up * eyeHeight;
		var losEnd = nodePos;

		if ( !HasClearLineToNode( scene, losStart, losEnd, harvesterRoot, node, out failReason ) )
			return false;

		return true;
	}

	static bool HasClearLineToNode( Scene scene, Vector3 from, Vector3 to, GameObject harvesterRoot, ResourceHarvestNode node, out string failReason )
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
