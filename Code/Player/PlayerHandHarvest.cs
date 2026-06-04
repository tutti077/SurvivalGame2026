using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Local pawn: hand harvest on <see cref="ResourceHarvestNode"/> within range while looking at it.
/// Press <see cref="HandHarvestInputAction"/> (default F) while the prompt is shown.
/// </summary>
[Title( "Player Hand Harvest" )]
public sealed class PlayerHandHarvest : Component
{
	[Property, Group( "Input" )]
	public string HandHarvestInputAction { get; set; } = "HandHarvest";

	[Property, Group( "Interaction" ), Title( "Look Cone (degrees)" )]
	public float LookConeDegrees { get; set; } = 18f;

	[Property, Group( "Interaction" ), Title( "Pawn Eye Height" )]
	public float PawnEyeHeight { get; set; } = 64f;

	[Property, Group( "Debug" )]
	public bool LogHandHarvest { get; set; } = true;

	public ResourceHarvestNode FocusedNode { get; private set; }

	PlayerVitals _vitals;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();

		if ( _vitals is null || !_vitals.IsLocalInputOwnedPawn() )
		{
			FocusedNode = null;
			return;
		}

		UpdateFocusedNode();

		if ( !Input.Pressed( HandHarvestInputAction ) )
			return;

		if ( FocusedNode is null )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: {HandHarvestInputAction} pressed with no harvest target in view/range." );
			return;
		}

		RequestHandHarvest( FocusedNode );
	}

	void UpdateFocusedNode()
	{
		FocusedNode = null;

		var cam = ResolveViewCamera();
		if ( !cam.IsValid() )
			return;

		var viewPos = cam.WorldPosition;
		var look = cam.WorldRotation.Forward.Normal;
		if ( look.LengthSquared < 1e-8f )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		ResourceHarvestNode best = null;
		var bestDist = float.MaxValue;

		foreach ( var candidate in scene.GetAllComponents<ResourceHarvestNode>() )
		{
			if ( candidate is null || !candidate.GameObject.IsValid() )
				continue;

			if ( !HandHarvestTargeting.TryValidateFocus( candidate, GameObject, viewPos, look, LookConeDegrees, PawnEyeHeight, out _ ) )
				continue;

			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, candidate.GameObject.WorldPosition );
			if ( dist >= bestDist )
				continue;

			best = candidate;
			bestDist = dist;
		}

		FocusedNode = best;
	}

	void RequestHandHarvest( ResourceHarvestNode node )
	{
		if ( node is null || !node.GameObject.IsValid() )
			return;

		var cam = ResolveViewCamera();
		if ( !cam.IsValid() )
			return;

		var viewPos = cam.WorldPosition;
		var look = cam.WorldRotation.Forward.Normal;
		var nodeId = node.GameObject.Id;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerTryHandHarvest( nodeId, viewPos, look );
			return;
		}

		RpcRequestHandHarvest( nodeId, viewPos, look );
	}

	[Rpc.Host]
	void RpcRequestHandHarvest( Guid nodeRootId, Vector3 clientViewPos, Vector3 clientLookDir )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerTryHandHarvest( nodeRootId, clientViewPos, clientLookDir );
	}

	void ServerTryHandHarvest( Guid nodeRootId, Vector3 viewPos, Vector3 lookDir )
	{
		if ( !TryResolveHarvestNode( nodeRootId, out var node ) )
		{
			if ( LogHandHarvest )
				Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: harvest node {nodeRootId} not found." );
			return;
		}

		if ( !HandHarvestTargeting.TryValidateFocus( node, GameObject, viewPos, lookDir, LookConeDegrees, PawnEyeHeight, out var failReason ) )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: hand harvest rejected — {failReason}." );
			return;
		}

		var result = node.TryPerformHarvestTick( HarvestToolType.Hand, 0 );
		if ( LogHandHarvest )
		{
			if ( result.Success )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: +{result.YieldAmount} {result.ResourceId} ({result.DisplayName})." );
			else
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: harvest failed — {result.FailReason}." );
		}
	}

	static bool TryResolveHarvestNode( Guid nodeRootId, out ResourceHarvestNode node )
	{
		node = null;
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		foreach ( var n in scene.GetAllComponents<ResourceHarvestNode>() )
		{
			if ( n is null || !n.GameObject.IsValid() || n.GameObject.Id != nodeRootId )
				continue;

			node = n;
			return true;
		}

		return false;
	}

	CameraComponent ResolveViewCamera()
	{
		for ( var go = GameObject; go.IsValid(); go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is null )
				continue;

			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
				return embedded;
		}

		if ( TryFindFirstCameraInHierarchy( GameObject, out var cam ) && cam.IsValid() )
			return cam;

		var sceneCam = Scene?.Camera;
		return sceneCam.IsValid() ? sceneCam : default;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent cam )
	{
		cam = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			cam = self;
			return true;
		}

		foreach ( var ch in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( ch, out cam ) )
				return true;
		}

		return false;
	}
}
