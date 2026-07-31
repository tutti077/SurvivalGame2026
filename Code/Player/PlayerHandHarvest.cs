using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Local pawn: hand harvest on <see cref="ResourceItemDefinition"/> within range while looking at it.
/// Press <see cref="HandHarvestInputAction"/> (default F) while the harvest prompt is shown.
/// World drops magnetize toward the pawn in range, then pick up on contact.
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

	[Property, Group( "World Pickup" ), Title( "Magnet Attract Radius (meters)" )]
	public float MagnetAttractRadiusMeters { get; set; } = 2.5f;

	[Property, Group( "World Pickup" ), Title( "Magnet Contact Radius (meters)" )]
	public float MagnetContactRadiusMeters { get; set; } = 0.4f;

	[Property, Group( "World Pickup" ), Title( "Magnet Speed (m/s)" )]
	public float MagnetSpeedMetersPerSecond { get; set; } = 10f;

	[Property, Group( "World Pickup" ), Title( "Magnet Aim Height (meters)" )]
	public float MagnetAimHeightMeters { get; set; } = 0.85f;

	[Property, Group( "Debug" )]
	public bool LogHandHarvest { get; set; }

	public ResourceItemDefinition FocusedNode { get; private set; }

	/// <summary>Fires when <see cref="FocusedNode"/> reference changes (including to null).</summary>
	public event Action FocusedNodeChanged;

	PlayerVitals _vitals;
	double _nextFocusScanAt;
	readonly List<(string ResourceId, int Amount)> _capacityScratch = new();

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

		// Loot magnet is a background behavior — keep it running even while the menu is open.
		TickWorldDropMagnet();

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

	void TickWorldDropMagnet()
	{
		var inventory = Components.Get<PlayerInventory>();
		if ( inventory is null )
			return;

		var attractRadius = TerrainWorldUnits.MetersToEngine( Math.Max( 0.25f, MagnetAttractRadiusMeters ) );
		var contactRadius = TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, MagnetContactRadiusMeters ) );
		contactRadius = Math.Min( contactRadius, attractRadius );

		var speed = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, MagnetSpeedMetersPerSecond ) );
		var aim = GameObject.WorldPosition
		          + Vector3.Up * TerrainWorldUnits.MetersToEngine( Math.Max( 0.1f, MagnetAimHeightMeters ) );
		var maxStep = speed * Time.Delta;

		foreach ( var candidate in WorldDroppedResourceRegistry.Drops )
		{
			if ( candidate is null || !candidate.IsAvailable )
				continue;

			if ( !candidate.CanPickupInto( inventory ) )
				continue;

			var pos = candidate.GameObject.WorldPosition;
			var dist = Vector3.DistanceBetween( aim, pos );
			if ( dist > attractRadius )
				continue;

			if ( dist <= contactRadius )
			{
				RequestWorldDropPickup( candidate );
				continue;
			}

			candidate.AttractToward( aim, maxStep, attractRadius );
		}
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

	void RequestWorldDropPickup( WorldDroppedResource drop )
	{
		if ( drop is null || !drop.GameObject.IsValid() )
			return;

		var dropId = drop.GameObject.Id;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerTryWorldDropPickup( dropId );
			return;
		}

		RpcRequestWorldDropPickup( dropId );
	}

	[Rpc.Host]
	void RpcRequestWorldDropPickup( Guid dropRootId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerTryWorldDropPickup( dropRootId );
	}

	void ServerTryWorldDropPickup( Guid dropRootId )
	{
		if ( !TryResolveWorldDrop( dropRootId, out var drop ) )
		{
			if ( LogHandHarvest )
				Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: world drop {dropRootId} not found." );
			return;
		}

		var inventory = Components.Get<PlayerInventory>();
		if ( inventory is null )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: world pickup rejected — no inventory." );
			return;
		}

		if ( !drop.TryPickup( inventory ) && LogHandHarvest )
			Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: world pickup rejected — inventory full or blocked." );
	}

	static bool TryResolveWorldDrop( Guid dropRootId, out WorldDroppedResource drop )
	{
		drop = null;

		foreach ( var candidate in WorldDroppedResourceRegistry.Drops )
		{
			if ( candidate is null || !candidate.GameObject.IsValid() || candidate.GameObject.Id != dropRootId )
				continue;

			drop = candidate;
			return true;
		}

		return false;
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
		if ( node is null || !node.HasAnyPossibleLoot() )
			return false;

		var inventory = Components.Get<PlayerInventory>();
		if ( inventory is null )
			return false;

		node.CollectGuaranteedCapacityNeeds( _capacityScratch );
		if ( _capacityScratch.Count > 0 )
			return inventory.CanAcceptResourceBundle( _capacityScratch );

		return true;
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
		if ( inventory is null )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: hand harvest rejected — no inventory." );
			return;
		}

		node.CollectGuaranteedCapacityNeeds( _capacityScratch );
		if ( _capacityScratch.Count > 0 && !inventory.CanAcceptResourceBundle( _capacityScratch ) )
		{
			if ( LogHandHarvest )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: hand harvest rejected — inventory and hotbar full." );
			return;
		}

		var result = node.TryPerformHarvestTick( HarvestToolType.Hand, 0 );
		if ( result.Success )
		{
			TryDepositHarvest( result );
			EmitHarvestNoise( node );
		}

		if ( LogHandHarvest )
		{
			if ( result.Success )
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: {FormatLootLog( result.Loot )}." );
			else
				Log.Info( $"[PlayerHandHarvest] {GameObject.Name}: harvest failed — {result.FailReason}." );
		}
	}

	void EmitHarvestNoise( ResourceItemDefinition node )
	{
		if ( node is null )
			return;

		// Chop / axe nodes use the longer hear range; soft hand-only gather stays quiet for now.
		if ( node.ToolTypeRequired != HarvestToolType.Axe && !LooksLikeTree( node ) )
			return;

		EntityNoiseBus.Emit( GameObject.Scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, GameObject );
	}

	static bool LooksLikeTree( ResourceItemDefinition node )
	{
		var id = node.ResourceId ?? string.Empty;
		var name = node.DisplayName ?? string.Empty;
		return id.Contains( "tree", StringComparison.OrdinalIgnoreCase )
		       || id.Contains( "wood", StringComparison.OrdinalIgnoreCase )
		       || id.Contains( "log", StringComparison.OrdinalIgnoreCase )
		       || name.Contains( "tree", StringComparison.OrdinalIgnoreCase )
		       || name.Contains( "wood", StringComparison.OrdinalIgnoreCase );
	}

	static string FormatLootLog( HarvestLootItem[] loot )
	{
		if ( loot is null || loot.Length == 0 )
			return "no loot";

		var parts = new string[loot.Length];
		for ( var i = 0; i < loot.Length; i++ )
			parts[i] = $"+{loot[i].Amount} {loot[i].ResourceId}";

		return string.Join( ", ", parts );
	}

	void TryDepositHarvest( HarvestTickResult result )
	{
		if ( result.Loot is null || result.Loot.Length == 0 )
			return;

		var inventory = Components.Get<PlayerInventory>();
		if ( inventory is null )
		{
			if ( LogHandHarvest )
				Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: no PlayerInventory to receive harvest loot." );
			return;
		}

		if ( !inventory.HostTryAddHarvestLoot( result.Loot ) && LogHandHarvest )
			Log.Warning( $"[PlayerHandHarvest] {GameObject.Name}: inventory full — lost {FormatLootLog( result.Loot )}." );
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
