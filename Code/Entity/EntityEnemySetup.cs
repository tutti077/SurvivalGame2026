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
		agent.UpdatePosition = false;
		agent.UpdateRotation = false;
		agent.Acceleration = 800f;
		agent.Enabled = true;
		EntityArchetype.ApplyToAgent( agent, enemyType );

		// Enemies are nav/locomotion driven — never leave Rigidbody gravity on (scav prefab defaults Gravity=true).
		var rb = root.Components.Get<Rigidbody>();
		if ( rb is not null )
		{
			rb.Gravity = false;
			rb.MotionEnabled = false;
		}

		// Prefab ships a flat foot BoxCollider + capsule; the box skates on terrain and reads as horizontal walk.
		DisableFlatFootBoxColliders( root );

		var onNav = SnapToNavOrTerrain( root, agent );

		var combat = GetOrCreate<PlayerCombat>( root );
		combat.Enabled = true;
		TryBindCombatAuthority( combat, root.Scene );

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
		var perceptionId = EntityPerceptionCatalog.BuildEntityId( enemyType, tier );
		brain.ApplyPerception( EntityPerceptionCatalog.Resolve( perceptionId ) );
		brain.SetHomePosition( root.WorldPosition );
		brain.Agent ??= agent;
		brain.Locomotion ??= root.Components.Get<EntityLocomotion>();
		brain.Enabled = true;

		// Wander needs nav — wait for bake if snap failed this frame.
		if ( onNav )
		{
			agent.UpdatePosition = true;
			brain.MarkAgentOnNav();
			brain.BeginAiNow();
		}
		else
		{
			agent.UpdatePosition = false;
			brain.WaitForNavThenStartAi();
		}

		root.Name = $"enemy_{enemyType}_t{tier}";
		root.Tags.Add( "enemy" );

		BuildNavMeshSync.EnsureBuildTraversalSettings( root.Scene );
	}

	static void DisableFlatFootBoxColliders( GameObject root )
	{
		foreach ( var box in root.Components.GetAll<BoxCollider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( box is null || !box.IsValid() )
				continue;

			// Foot box on scav is Scale ~16,16,36 Center z~18 — keep capsule only.
			box.Enabled = false;
		}
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
		{
			rb.Gravity = false;
			rb.MotionEnabled = false;
		}
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

	static void TryBindCombatAuthority( PlayerCombat combat, Scene scene )
	{
		if ( combat is null || !combat.IsValid() )
			return;

		if ( combat.HostCombatAuthority is { } existing && existing.IsValid() )
			return;

		if ( !scene.IsValid() )
			return;

		foreach ( var auth in scene.GetAllComponents<CombatAuthority>() )
		{
			if ( auth is null || !auth.IsValid() || !auth.Enabled )
				continue;

			combat.HostCombatAuthority = auth;
			return;
		}
	}

	static bool SnapToNavOrTerrain( GameObject root, NavMeshAgent agent )
	{
		if ( agent is null || !agent.IsValid() )
			return false;

		var scene = root.Scene;
		if ( !scene.IsValid() )
			return false;

		var spawnPos = root.WorldPosition;

		// Correct height onto terrain first — population height can sit inside/under the mesh.
		if ( TrySnapToPhysicsGround( scene, root, spawnPos, out var grounded ) )
			spawnPos = grounded;

		// Streamed terrain often has no tiles yet — bake a local pad; deferred chunk bake retries via OnNavBakeComplete.
		BuildNavMeshSync.EnsureNavAroundPoint( scene, spawnPos );

		if ( EntityNavMeshUtility.EnsureAgentOnNavMesh( scene, agent, spawnPos ) )
		{
			agent.UpdatePosition = true;
			return true;
		}

		agent.SetAgentPosition( root.WorldPosition );
		agent.UpdatePosition = false;
		// Expected on the spawn frame while Recast catches up; agents snap in OnNavBakeComplete.
		return false;
	}

	static bool TrySnapToPhysicsGround( Scene scene, GameObject root, Vector3 near, out Vector3 feet )
	{
		feet = near;
		if ( !scene.IsValid() )
			return false;

		// Start well above the sample height in case we spawned slightly into the mesh.
		var from = near + Vector3.Up * 512f;
		var to = near + Vector3.Down * 4096f;
		var tr = scene.Trace.Ray( from, to )
			.Radius( 8f )
			.UsePhysicsWorld()
			.IgnoreGameObjectHierarchy( root )
			.Run();

		if ( !tr.Hit )
		{
			tr = scene.Trace.Ray( from, to )
				.Radius( 8f )
				.IgnoreGameObjectHierarchy( root )
				.Run();
		}

		if ( !tr.Hit )
		{
			// Heightfield fallback when physics body isn't ready yet this frame.
			if ( TrySnapToTerrainHeight( scene, root, near, out feet ) )
				return true;
			return false;
		}

		// Reject near-vertical hits (walls); want a standable surface.
		if ( tr.Normal.z < 0.35f )
		{
			if ( TrySnapToTerrainHeight( scene, root, near, out feet ) )
				return true;
			return false;
		}

		feet = tr.HitPosition + Vector3.Up * 2f;
		root.WorldPosition = feet;
		return true;
	}

	static bool TrySnapToTerrainHeight( Scene scene, GameObject root, Vector3 near, out Vector3 feet )
	{
		feet = near;
		if ( !scene.IsValid() )
			return false;

		TerrainWorldManager manager = null;
		foreach ( var m in scene.GetAllComponents<TerrainWorldManager>() )
		{
			if ( m is not null && m.IsValid() && m.Enabled )
			{
				manager = m;
				break;
			}
		}

		if ( manager is null )
			return false;

		var meters = TerrainWorldUnits.EngineToMeters( near );
		if ( !manager.TrySampleGroundMeters( meters.x, meters.y, out var groundZMeters ) )
			return false;

		feet = new Vector3( near.x, near.y, TerrainWorldUnits.MetersToEngine( groundZMeters ) );
		root.WorldPosition = feet;
		return true;
	}
}
