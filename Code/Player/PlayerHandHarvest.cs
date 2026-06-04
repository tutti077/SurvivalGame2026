using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Local pawn: hand harvest on <see cref="ResourceItemDefinition"/> within range while looking at it.
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

	[Property, Group( "Interaction" ), Title( "Focus Scan Interval (seconds)" )]
	public float FocusScanIntervalSeconds { get; set; } = 0.2f;

	[Property, Group( "Debug" )]
	public bool LogHandHarvest { get; set; }

	public ResourceItemDefinition FocusedNode { get; private set; }

	/// <summary>Fires when <see cref="FocusedNode"/> reference changes (including to null).</summary>
	public event Action FocusedNodeChanged;

	PlayerVitals _vitals;
	double _nextFocusScanAt;

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
			SetFocusedNode( null );
			return;
		}

		var menu = Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
		{
			SetFocusedNode( null );
			return;
		}

		if ( Time.NowDouble >= _nextFocusScanAt )
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
		var interval = Math.Max( 0.05, FocusScanIntervalSeconds );
		_nextFocusScanAt = Time.NowDouble + interval;

		var cam = ResolveViewCamera();
		if ( !cam.IsValid() )
		{
			SetFocusedNode( null );
			return;
		}

		var viewPos = cam.WorldPosition;
		var look = cam.WorldRotation.Forward.Normal;
		if ( look.LengthSquared < 1e-8f )
		{
			SetFocusedNode( null );
			return;
		}

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			SetFocusedNode( null );
			return;
		}

		if ( TryFindFocusedNodeFromRay( scene, viewPos, look, out var rayNode ) )
		{
			SetFocusedNode( rayNode );
			return;
		}

		ResourceItemDefinition best = null;
		var bestDist = float.MaxValue;

		foreach ( var candidate in ResourceHarvestRegistry.Nodes )
		{
			if ( candidate is null || !candidate.GameObject.IsValid() )
				continue;

			if ( !HandHarvestTargeting.PassesQuickFocusCheck( candidate, GameObject, viewPos, look, LookConeDegrees ) )
				continue;

			if ( !HandHarvestTargeting.TryValidateFocus( candidate, GameObject, viewPos, look, LookConeDegrees, PawnEyeHeight, out _ ) )
				continue;

			if ( !CanReceiveHarvestYield( candidate ) )
				continue;

			var dist = Vector3.DistanceBetween( GameObject.WorldPosition, candidate.GameObject.WorldPosition );
			if ( dist >= bestDist )
				continue;

			best = candidate;
			bestDist = dist;
		}

		SetFocusedNode( best );
	}

	void SetFocusedNode( ResourceItemDefinition node )
	{
		if ( FocusedNode == node )
			return;

		FocusedNode = node;
		FocusedNodeChanged?.Invoke();
	}

	bool TryFindFocusedNodeFromRay( Scene scene, Vector3 eye, Vector3 dir, out ResourceItemDefinition node )
	{
		node = null;

		var end = eye + dir * 320f;
		var tr = scene.Trace.Ray( eye, end ).IgnoreGameObjectHierarchy( GameObject ).Run();
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
		{
			tr = scene.Trace.Ray( eye, end ).IgnoreGameObjectHierarchy( GameObject ).UseHitboxes().Run();
			if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
				return false;
		}

		if ( !ResourceHarvestTrace.TryFindOnHierarchy( tr.GameObject, out node ) )
			return false;

		if ( !HandHarvestTargeting.TryValidateFocus( node, GameObject, eye, dir, LookConeDegrees, PawnEyeHeight, out _ ) )
			return false;

		return CanReceiveHarvestYield( node );
	}

	bool CanReceiveHarvestYield( ResourceItemDefinition node )
	{
		if ( node is null || string.IsNullOrWhiteSpace( node.ResourceId ) )
			return false;

		var yield = node.GetMaxYieldPerTick();
		if ( yield <= 0 )
			return false;

		var inventory = Components.Get<PlayerInventory>();
		return inventory is not null && inventory.CanAcceptResource( node.ResourceId, yield );
	}

	void RequestHandHarvest( ResourceItemDefinition node )
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

		var inventory = Components.Get<PlayerInventory>();
		var maxYield = node.GetMaxYieldPerTick();
		if ( inventory is null || maxYield <= 0 || !inventory.CanAcceptResource( node.ResourceId, maxYield ) )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: hand harvest rejected — inventory and hotbar full." );
			return;
		}

		var result = node.TryPerformHarvestTick( HarvestToolType.Hand, 0 );
		if ( result.Success )
			TryDepositHarvest( result );

		if ( LogHandHarvest )
		{
			if ( result.Success )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: +{result.YieldAmount} {result.ResourceId} ({result.DisplayName})." );
			else
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: harvest failed — {result.FailReason}." );
		}
	}

	void TryDepositHarvest( HarvestTickResult result )
	{
		if ( result.YieldAmount <= 0 || string.IsNullOrWhiteSpace( result.ResourceId ) )
			return;

		var inventory = Components.Get<PlayerInventory>();
		if ( inventory is null )
		{
			if ( LogHandHarvest )
				Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: no PlayerInventory to receive {result.YieldAmount} {result.ResourceId}." );
			return;
		}

		if ( !inventory.HostTryAddResource( result.ResourceId, result.YieldAmount ) && LogHandHarvest )
			Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: inventory full — lost {result.YieldAmount} {result.ResourceId}." );
	}

	bool TryResolveHarvestNode( Guid nodeRootId, out ResourceItemDefinition node )
	{
		node = null;

		foreach ( var n in ResourceHarvestRegistry.Nodes )
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
