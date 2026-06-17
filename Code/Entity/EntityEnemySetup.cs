using Sandbox;
using Sandbox.Movement;

namespace Survival;

/// <summary>Configures a scav (or player-clone) into a host-simulated enemy.</summary>
public static class EntityEnemySetup
{
	public static void Configure( GameObject root, EnemyType enemyType, int tier, float healthOverride = 0f )
	{
		if ( root is null || !root.IsValid() )
			return;

		root.NetworkMode = NetworkMode.Never;
		root.Enabled = true;

		if ( root.Components.Get<PlayerController>() is not null )
			StripPlayerSystems( root );

		var vitals = GetOrCreate<EntityVitals>( root );
		EntityArchetype.ApplyToVitals( vitals, enemyType, tier );
		if ( healthOverride > 0f )
			vitals.MaxHealth = healthOverride;
		vitals.ResetToFull();

		var agent = GetOrCreate<NavMeshAgent>( root );
		agent.UpdatePosition = true;
		agent.UpdateRotation = false;
		agent.Acceleration = 800f;
		agent.Enabled = true;
		EntityArchetype.ApplyToAgent( agent, enemyType );

		SnapToNavMesh( root, agent );

		var combat = GetOrCreate<PlayerCombat>( root );
		combat.Enabled = true;

		var entityCombat = GetOrCreate<EntityCombat>( root );
		entityCombat.Combat = combat;
		EntityArchetype.ApplyToCombat( entityCombat, enemyType );

		GetOrCreate<EntityLocomotion>( root );
		var locomotion = root.Components.Get<EntityLocomotion>();
		if ( locomotion is not null )
			locomotion.Agent = agent;
		var healthBar = GetOrCreate<EnemyHealthBar>( root );
		healthBar.RefreshBinding();

		var brain = GetOrCreate<EntityBrain>( root );
		EntityArchetype.ApplyToBrain( brain, enemyType );
		brain.SetHomePosition( root.WorldPosition );
		brain.Agent ??= agent;
		brain.Locomotion ??= root.Components.Get<EntityLocomotion>();
		brain.Enabled = true;

		// Run after all snaps so the first path query uses a valid on-mesh origin.
		brain.BeginAiNow();

		root.Name = $"enemy_{enemyType}_t{tier}";
		root.Tags.Add( "enemy" );

		BuildNavMeshSync.EnsureBuildTraversalSettings( root.Scene );
	}

	static void StripPlayerSystems( GameObject root )
	{
		Destroy<PlayerEquipment>( root );
		Destroy<PlayerInventoryInteraction>( root );
		Destroy<PlayerInventory>( root );
		Destroy<PlayerHotbarController>( root );
		Destroy<PlayerHotbar>( root );
		Destroy<PlayerGameMenuController>( root );
		Destroy<PlayerCrafting>( root );
		Destroy<PlayerHandHarvest>( root );
		Destroy<PlayerEquippedItem>( root );
		Destroy<PlayerScreenHud>( root );
		Destroy<ScreenPanel>( root );
		Destroy<PlayerMovement>( root );
		Destroy<PlayerVitals>( root );
		Destroy<TrainingDummyAttackTelegraph>( root );
		Destroy<MoveModeWalk>( root );
		Destroy<MoveModeSwim>( root );
		Destroy<MoveModeLadder>( root );
		Destroy<PlayerController>( root );

		var rb = root.Components.Get<Rigidbody>();
		if ( rb is not null )
			rb.MotionEnabled = false;
	}

	static void Destroy<T>( GameObject root ) where T : Component
	{
		var component = root.Components.Get<T>();
		component?.Destroy();
	}

	static T GetOrCreate<T>( GameObject root ) where T : Component, new()
	{
		var existing = root.Components.Get<T>();
		if ( existing is not null )
			return existing;

		return root.Components.Create<T>();
	}

	static void SnapToGround( GameObject root )
	{
		var scene = root.Scene;
		if ( !scene.IsValid() )
			return;

		var origin = root.WorldPosition + Vector3.Up * 48f;
		var trace = scene.Trace.Ray( origin, origin + Vector3.Down * 512f )
			.IgnoreGameObjectHierarchy( root )
			.Run();

		if ( !trace.Hit )
			return;

		var grounded = trace.HitPosition;
		root.WorldPosition = grounded;

		var agent = root.Components.Get<NavMeshAgent>();
		agent?.SetAgentPosition( grounded );
	}

	static void SnapToNavMesh( GameObject root, NavMeshAgent agent )
	{
		if ( agent is null || !agent.IsValid() )
			return;

		var scene = root.Scene;
		if ( !scene.IsValid() )
			return;

		var spawnPos = root.WorldPosition;

		if ( EntityNavMeshUtility.EnsureAgentOnNavMesh( scene, agent, spawnPos ) )
			return;

		Log.Warning(
			$"[EntityEnemySetup] No nav mesh near spawn {spawnPos} after tile generate — enemy left at spawn height; brain will retry." );
	}
}
