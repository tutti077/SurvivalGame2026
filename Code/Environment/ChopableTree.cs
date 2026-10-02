using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Hidden HP on a tree: melee hits (via <see cref="DamageReceiver"/>) chop it; at 0 HP the tree
/// hides and drops wood as world pickups. Requires an equipped Axe harvest tool.
/// <para>Felling (optional, <see cref="StumpModel"/> set): the first break swaps the tree to its stump
/// (choppable again for <see cref="StumpWoodMin"/>..<see cref="StumpWoodMax"/> wood) and spawns
/// <see cref="FelledLogPrefab"/> balanced on it, tipped away from the chopper. Splitting (optional,
/// <see cref="SplitPiecePrefab"/> set, used by the log): a break replaces this object with two
/// pieces offset along its long axis. Plain objects (the log halves) drop wood.</para>
/// <para>Crown fall (Mark's "type 2" tree): the break swaps the tree to its stump and releases the
/// authored crown — a child tagged <see cref="CrownTag"/>, one rigidbody whose colliders are its
/// piece children tagged <see cref="PieceTag"/> — which tips over away from the chopper
/// (<see cref="ChopFallingCrown"/>). When it lands it splits: the pieces come apart as short-lived
/// local debris (<see cref="ChopDebris"/>) and the host drops the wood there. Nothing to chop
/// afterwards but the stump. Crown and pieces are cosmetic on every peer; only the wood is networked.</para>
/// </summary>
[Title( "Chopable Tree" )]
public sealed class ChopableTree : Component
{
	[Property, Group( "Chop" ), Title( "Max Health" ), Range( 1f, 500f )]
	public float MaxHealth { get; set; } = 40f;

	[Property, Group( "Chop" ), Title( "Current Health" )]
	public float CurrentHealth { get; private set; }

	[Property, Group( "Loot" ), Title( "Wood Resource Id" )]
	public string WoodResourceId { get; set; } = "resource_woodBasic";

	[Property, Group( "Loot" ), Title( "Wood Drop Min" ), Range( 0, 50 )]
	public int WoodDropMin { get; set; } = 10;

	[Property, Group( "Loot" ), Title( "Wood Drop Max" ), Range( 1, 50 )]
	public int WoodDropMax { get; set; } = 10;

	[Property, Group( "Chop" ), Title( "Require Axe" )]
	public bool RequireAxe { get; set; } = true;

	[Property, Group( "Felling" ), Title( "Stump Model" )]
	public Model StumpModel { get; set; }

	[Property, Group( "Felling" ), Title( "Stump Top (m)" ), Range( 0f, 3f )]
	public float StumpTopMeters { get; set; } = 0.8f;

	[Property, Group( "Felling" ), Title( "Felled Log Prefab" )]
	public string FelledLogPrefab { get; set; }

	[Property, Group( "Felling" ), Title( "Stump Health" ), Range( 1f, 500f )]
	public float StumpHealth { get; set; } = 12f;

	[Property, Group( "Felling" ), Title( "Stump Wood Min" ), Range( 0, 50 )]
	public int StumpWoodMin { get; set; } = 3;

	[Property, Group( "Felling" ), Title( "Stump Wood Max" ), Range( 0, 50 )]
	public int StumpWoodMax { get; set; } = 4;

	/// <summary>Speed given to the log's top as it is felled; gravity does the rest. Low = a heavy log that hangs a moment before it goes (Mark: 1.5 tipped like it was weightless).</summary>
	[Property, Group( "Felling" ), Title( "Tip Speed (m/s)" ), Range( 0f, 5f )]
	public float FellTipSpeedMeters { get; set; } = 0.3f;

	[Property, Group( "Split" ), Title( "Split Piece Prefab" )]
	public string SplitPiecePrefab { get; set; }

	[Property, Group( "Split" ), Title( "Split Piece Offset (m)" ), Range( 0f, 10f )]
	public float SplitPieceOffsetMeters { get; set; } = 2f;

	/// <summary>Where the split happens, measured from this object's origin along its up axis: a log whose origin is its bottom point splits at half its length, so the pieces land exactly where the log lay (Mark: the halves used to teleport 2 m).</summary>
	[Property, Group( "Split" ), Title( "Split Centre (m from origin)" ), Range( 0f, 20f )]
	public float SplitCenterOffsetMeters { get; set; }

	/// <summary>The child carrying this tag is the crown: authored disabled, with its renderer and rigidbody.</summary>
	public const string CrownTag = "chop_crown";

	/// <summary>Crown children carrying this tag are the pieces: their colliders are the crown's while it falls; their renderer is authored off.</summary>
	public const string PieceTag = "chop_piece";

	/// <summary>Where the felling push lands on the crown, above its origin. Higher = less speed needed, slower lean.</summary>
	[Property, Group( "Crown" ), Title( "Crown Push Height (m)" ), Range( 0.5f, 20f )]
	public float CrownPushHeightMeters { get; set; } = 3f;

	[Property, Group( "Crown" ), Title( "Crown Split Lean (°)" ), Range( 10f, 90f )]
	public float CrownSplitTiltDegrees { get; set; } = 60f;

	/// <summary>Extra push the pieces get when the crown splits, on top of the crown's own motion. Small: they come apart, not fly.</summary>
	[Property, Group( "Crown" ), Title( "Piece Scatter (m/s)" ), Range( 0f, 10f )]
	public float PieceScatterMeters { get; set; } = 1f;

	/// <summary>How long a split piece stays before it is removed — the pieces leave, the wood stays.</summary>
	[Property, Group( "Crown" ), Title( "Piece Lifetime (s)" ), Range( 0.2f, 20f )]
	public float PieceLifetimeSeconds { get; set; } = 3f;

	[Property, Group( "Debug" )]
	public bool LogChop { get; set; }

	/// <summary>Presentation stages broadcast to peers: standing tree, stump, gone.</summary>
	public const int StageStump = 1;
	public const int StageGone = 2;

	bool _broken;
	bool _isStump;
	bool _crownReleased;
	int _crownWood;
	GameObject _crown;
	readonly List<GameObject> _pieces = new();

	bool CanFell => StumpModel is not null && !_isStump;

	bool CanFellCrown => _crown is { IsValid: true } && !_crownReleased && !_isStump;

	public bool IsBroken => _broken;

	bool IsHostAuthority =>
		!Networking.IsActive || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		if ( CurrentHealth <= 0f || CurrentHealth > MaxHealth )
			CurrentHealth = Math.Max( 1f, MaxHealth );

		// Authored once on the prefab; cached once here.
		foreach ( var child in GameObject.Children )
		{
			if ( !child.IsValid() || !child.Tags.Has( CrownTag ) )
				continue;

			_crown = child;
			foreach ( var piece in child.Children )
			{
				if ( piece.IsValid() && piece.Tags.Has( PieceTag ) )
					_pieces.Add( piece );
			}

			break;
		}
	}

	/// <summary>Host: apply chop damage from a melee hit. Returns HP removed.</summary>
	public float ApplyChopDamage( float amount, Component attacker )
	{
		if ( !IsHostAuthority || _broken || amount <= 0f )
			return 0f;

		// Physics / impact also hits <see cref="IDamageable"/> — only melee may chop.
		if ( attacker is not PlayerCombat )
			return 0f;

		if ( RequireAxe && !AttackerHasAxe( attacker ) )
		{
			if ( LogChop )
				Log.Info( $"[ChopableTree] {GameObject.Name}: hit ignored — axe required." );
			return 0f;
		}

		var before = CurrentHealth;
		CurrentHealth = Math.Max( 0f, CurrentHealth - amount );
		var dealt = before - CurrentHealth;

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: -{dealt:0.#} HP ({CurrentHealth:0.#}/{MaxHealth:0.#})." );

		if ( CurrentHealth <= 1e-3f )
		{
			if ( CanFellCrown )
				FellCrown( attacker );
			else if ( CanFell )
				FellToStump( attacker );
			else if ( !string.IsNullOrWhiteSpace( SplitPiecePrefab ) )
				SplitIntoPieces( attacker );
			else
				BreakAndDrop( attacker );
		}

		return dealt;
	}

	/// <summary>Host: the tree becomes its stump and a physics log is stood on the stump top.</summary>
	void FellToStump( Component attacker )
	{
		BecomeStump();

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var log = string.IsNullOrWhiteSpace( FelledLogPrefab ) ? null : BuildPrefabUtility.GetTemplate( FelledLogPrefab )?.Clone();
		if ( log is not null && log.IsValid() )
		{
			log.NetworkMode = NetworkMode.Object;
			log.Parent = scene;
			log.WorldPosition = WorldPosition + WorldRotation.Up * TerrainWorldUnits.MetersToEngine( StumpTopMeters );
			log.WorldRotation = Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) );
			log.Enabled = true;
			HostNetworkSpawn.TrySpawn( log );

			// The log stands on its point: a push at the top, away from the chopper, tips it over.
			var body = log.Components.Get<Rigidbody>();
			if ( body is not null )
			{
				var from = attacker is not null && attacker.IsValid() ? attacker.WorldPosition : WorldPosition;
				var away = (WorldPosition - from).WithZ( 0f );
				away = away.Length > 1f ? away.Normal : Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
				var top = log.WorldPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( 6f );
				body.ApplyImpulseAt( top, away * body.Mass * TerrainWorldUnits.MetersToEngine( FellTipSpeedMeters ) );
			}
		}
		else
			Log.Warning( $"[ChopableTree] {GameObject.Name}: felled log prefab '{FelledLogPrefab}' not found." );

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: felled, stump + log." );

		PlayerQuests.FindOnAttacker( attacker )?.HostReport( QuestEventIds.TreeChopped );
		BroadcastStage( StageStump );
		EntityNoiseBus.Emit( scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, NoiseIgnore( attacker ) );
	}

	/// <summary>Host: the tree becomes its stump and the crown tips over; the wood lands when the crown does.</summary>
	void FellCrown( Component attacker )
	{
		_crownWood = RollWoodCount();
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var from = attacker is not null && attacker.IsValid() ? attacker.WorldPosition : WorldPosition;
		ReleaseCrown( (WorldPosition - from).WithZ( 0f ) );

		if ( StumpModel is not null )
			BecomeStump();
		else
		{
			_broken = true;
			CurrentHealth = 0f;
			ApplyBrokenVisual();
		}

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: crown released, {_crownWood}x wood on landing." );

		PlayerQuests.FindOnAttacker( attacker )?.HostReport( QuestEventIds.TreeChopped );
		BroadcastStage( StumpModel is not null ? StageStump : StageGone );
		EntityNoiseBus.Emit( scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, NoiseIgnore( attacker ) );
	}

	/// <summary>Every peer: detach the authored crown, enable it and tip it along <paramref name="away"/> (any direction if zero).</summary>
	void ReleaseCrown( Vector3 away )
	{
		if ( _crownReleased || _crown is not { IsValid: true } )
			return;

		_crownReleased = true;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var direction = away.Length > 1f ? away.Normal : Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;

		var transform = _crown.WorldTransform;
		_crown.Parent = scene;
		_crown.WorldTransform = transform;
		_crown.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_crown.Enabled = true;

		var fall = _crown.Components.Create<ChopFallingCrown>();
		fall.PushDirection = direction;
		fall.PushSpeedUnits = TerrainWorldUnits.MetersToEngine( FellTipSpeedMeters );
		fall.PushHeightUnits = TerrainWorldUnits.MetersToEngine( CrownPushHeightMeters );
		fall.SplitTiltDegrees = CrownSplitTiltDegrees;
		fall.OnLanded = SplitCrown;
	}

	/// <summary>Every peer: the landed crown comes apart into its pieces, which keep its motion and then vanish; the host drops the wood here.</summary>
	void SplitCrown( ChopFallingCrown crown )
	{
		if ( crown is null || !crown.IsValid() || crown.GameObject != _crown )
			return;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var crownBody = _crown.Components.Get<Rigidbody>();
		var linear = crownBody?.PhysicsBody is not null ? crownBody.Velocity : Vector3.Zero;
		var angular = crownBody?.PhysicsBody is not null ? crownBody.AngularVelocity : Vector3.Zero;
		var scatter = TerrainWorldUnits.MetersToEngine( PieceScatterMeters );
		var mass = crownBody is not null ? Math.Max( 1f, crownBody.MassOverride / Math.Max( 1, _pieces.Count ) ) : 100f;
		var landing = _crown.WorldPosition;

		foreach ( var piece in _pieces )
		{
			if ( piece is null || !piece.IsValid() )
				continue;

			var transform = piece.WorldTransform;
			piece.Parent = scene;
			piece.WorldTransform = transform;
			piece.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			foreach ( var renderer in piece.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelf ) )
				renderer.Enabled = true;

			// The piece already owns its colliders; the body is the transient physics for its flight.
			var body = piece.Components.GetOrCreate<Rigidbody>();
			body.Gravity = true;
			body.MassOverride = mass;

			var outward = (piece.WorldPosition - landing).WithZ( 0f );
			outward = outward.Length > 1f ? outward.Normal : Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
			var debris = piece.Components.Create<ChopDebris>();
			debris.Velocity = linear + outward * scatter + Vector3.Up * (scatter * 0.5f);
			debris.AngularVelocity = angular + Vector3.Random * 1f;
			debris.LifetimeSeconds = PieceLifetimeSeconds;
		}

		_pieces.Clear();
		_crown.Destroy();
		_crown = null;

		if ( IsHostAuthority )
		{
			DropWoodAt( landing, _crownWood, null );
			_crownWood = 0;
		}
	}

	/// <summary>Host: replace this (networked) object with two pieces along its long axis.</summary>
	void SplitIntoPieces( Component attacker )
	{
		_broken = true;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var offset = TerrainWorldUnits.MetersToEngine( SplitPieceOffsetMeters );
		var centre = WorldPosition + WorldRotation.Up * TerrainWorldUnits.MetersToEngine( SplitCenterOffsetMeters );
		var body = Components.Get<Rigidbody>();
		foreach ( var side in new[] { -1f, 1f } )
		{
			var piece = BuildPrefabUtility.GetTemplate( SplitPiecePrefab )?.Clone();
			if ( piece is null || !piece.IsValid() )
			{
				Log.Warning( $"[ChopableTree] {GameObject.Name}: split piece prefab '{SplitPiecePrefab}' not found." );
				break;
			}

			piece.NetworkMode = NetworkMode.Object;
			piece.Parent = scene;
			piece.WorldPosition = centre + WorldRotation.Up * offset * side;
			piece.WorldRotation = WorldRotation;
			piece.Enabled = true;
			HostNetworkSpawn.TrySpawn( piece );
			var pieceBody = piece.Components.Get<Rigidbody>();
			if ( pieceBody is not null && body is not null )
				pieceBody.Velocity = body.Velocity;
		}

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: split in two." );

		EntityNoiseBus.Emit( scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, NoiseIgnore( attacker ) );
		GameObject.Destroy();
	}

	GameObject NoiseIgnore( Component attacker ) =>
		attacker is not null && attacker.GameObject.IsValid() ? attacker.GameObject : GameObject;

	void BecomeStump()
	{
		if ( _isStump || StumpModel is null )
			return;

		_isStump = true;
		_broken = false;
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
			renderer.Model = StumpModel;
		foreach ( var col in Components.GetAll<ModelCollider>( FindMode.EverythingInSelfAndDescendants ) )
			col.Model = StumpModel;

		MaxHealth = Math.Max( 1f, StumpHealth );
		CurrentHealth = MaxHealth;
		WoodDropMin = StumpWoodMin;
		WoodDropMax = StumpWoodMax;
	}

	void BroadcastStage( int stage )
	{
		var identity = Components.Get<WorldScatterIdentity>( FindMode.EverythingInSelfAndAncestors );
		if ( identity is not null && !string.IsNullOrWhiteSpace( identity.StableKey ) )
			WorldScatterIdentity.HostBroadcastBroken( identity.StableKey, stage );
		else if ( GameObject.Network is { Active: true } )
			RpcBroadcastStage( stage );
	}

	void BreakAndDrop( Component attacker )
	{
		if ( _broken )
			return;

		_broken = true;
		CurrentHealth = 0f;
		ApplyBrokenVisual();

		var count = RollWoodCount();
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var ignore = NoiseIgnore( attacker );
		DropWood( count, attacker );

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: broken — dropped {count}x wood." );

		// tree-chopped fires once per tree: on felling, not again for its stump
		if ( !_isStump )
			PlayerQuests.FindOnAttacker( attacker )?.HostReport( QuestEventIds.TreeChopped );

		BroadcastStage( StageGone );

		EntityNoiseBus.Emit( scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, ignore );
	}

	int RollWoodCount()
	{
		var min = Math.Max( 0, Math.Min( WoodDropMin, WoodDropMax ) );
		var max = Math.Max( min, WoodDropMax );
		if ( max <= min )
			return min;
		return min + (int)MathF.Floor( Sandbox.Game.Random.Float( 0f, max - min + 0.999f ) );
	}

	/// <summary>Host: scatter <paramref name="count"/> wood pickups around the trunk.</summary>
	void DropWood( int count, Component attacker ) => DropWoodAt( GameObject.WorldPosition, count, attacker );

	/// <summary>Host: scatter <paramref name="count"/> wood pickups around <paramref name="origin"/>.</summary>
	void DropWoodAt( Vector3 origin, int count, Component attacker )
	{
		var resourceId = string.IsNullOrWhiteSpace( WoodResourceId ) ? "resource_woodBasic" : WoodResourceId;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var ignore = NoiseIgnore( attacker );

		for ( var i = 0; i < count; i++ )
		{
			var yaw = Sandbox.Game.Random.Float( 0f, 360f );
			var dist = Sandbox.Game.Random.Float( 18f, 55f );
			var outward = Rotation.FromYaw( yaw ).Forward;
			var offset = outward * dist + Vector3.Up * Sandbox.Game.Random.Float( 12f, 28f );
			var instance = HeldStackWorldDrop.TrySpawnWorldDrop(
				scene,
				resourceId,
				1,
				origin + offset,
				ignore,
				applyDropperSelfPickupDelay: false );
			if ( instance is not null && instance.IsValid() )
				HeldStackWorldDrop.ApplyScatterBurst( instance, outward );
		}
	}

	/// <summary>Presentation for a stage the host reached (peers, and the host's own broadcast
	/// echo): idempotent, so applying the same stage twice changes nothing.</summary>
	public void ApplyStagePresentation( int stage )
	{
		// A crown tree leaves standing only by felling its crown; the host already did, so this is the peers.
		if ( stage >= StageStump && CanFellCrown && !_broken )
			ReleaseCrown( Vector3.Zero );

		if ( stage >= StageStump && CanFell && !_broken )
			BecomeStump();
		if ( stage >= StageGone || (stage >= StageStump && StumpModel is null) )
		{
			_broken = true;
			CurrentHealth = 0f;
			ApplyBrokenVisual();
		}
	}

	void ApplyBrokenVisual()
	{
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null )
				continue;
			renderer.Enabled = false;
		}

		foreach ( var col in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || col.IsTrigger )
				continue;
			col.Enabled = false;
		}

		foreach ( var prop in Components.GetAll<Prop>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( prop is not null )
				prop.Enabled = false;
		}

		foreach ( var body in Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null )
				body.Enabled = false;
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcBroadcastStage( int stage ) => ApplyStagePresentation( stage );

	static bool AttackerHasAxe( Component attacker )
	{
		if ( attacker is null || !attacker.GameObject.IsValid() )
			return false;

		PlayerEquippedItem equipped = null;
		for ( var p = attacker.GameObject; p.IsValid(); p = p.Parent )
		{
			equipped = p.Components.Get<PlayerEquippedItem>();
			if ( equipped is not null )
				break;
		}

		equipped ??= attacker.GameObject.Components.Get<PlayerEquippedItem>( FindMode.EverythingInSelfAndDescendants );
		if ( equipped is null )
			return false;

		var id = equipped.EquippedResourceId;
		if ( string.IsNullOrWhiteSpace( id ) )
			id = equipped.ActiveHotbarResourceId;

		if ( string.IsNullOrWhiteSpace( id ) )
			return false;

		if ( EquipmentCatalog.TryGet( id, out var profile )
		     && string.Equals( profile.HarvestToolType, "Axe", StringComparison.OrdinalIgnoreCase ) )
			return true;

		return id.Contains( "axe", StringComparison.OrdinalIgnoreCase );
	}
}
