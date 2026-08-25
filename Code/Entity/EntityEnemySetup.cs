using Sandbox;

namespace Survival;

/// <summary>Configures an entity prefab (scav and friends) into a host-simulated enemy.</summary>
public static class EntityEnemySetup
{
	public static void Configure( GameObject root, EnemyType enemyType, int tier, float healthOverride = 0f )
	{
		if ( root is null || !root.IsValid() )
			return;

		root.NetworkMode = NetworkMode.Object;
		root.Enabled = true;

		// Every component below is authored on the entity prefab (Commandment #5) — this only tunes them.
		var vitals = Require<EntityVitals>( root );
		var agent = Require<NavMeshAgent>( root );
		var combat = Require<PlayerCombat>( root );
		var entityCombat = Require<EntityCombat>( root );
		var locomotion = Require<EntityLocomotion>( root );
		var healthBar = Require<EnemyHealthBar>( root );
		var brain = Require<EntityBrain>( root );

		if ( vitals is null || agent is null || combat is null || entityCombat is null
		     || locomotion is null || healthBar is null || brain is null )
			return;

		EntityArchetype.ApplyToVitals( vitals, enemyType, tier );
		if ( healthOverride > 0f )
			vitals.MaxHealth = healthOverride;
		vitals.ResetToFull();

		agent.UpdatePosition = false;
		agent.UpdateRotation = false;
		agent.Acceleration = 800f;
		agent.Enabled = true;
		EntityArchetype.ApplyToAgent( agent, enemyType );

		var onNav = SnapToNavOrTerrain( root, agent );

		combat.Enabled = true;
		TryBindCombatAuthority( combat, root.Scene );

		entityCombat.Combat = combat;
		EntityArchetype.ApplyToCombat( entityCombat, enemyType );

		locomotion.Agent = agent;
		healthBar.RefreshBinding();

		EntityArchetype.ApplyToBrain( brain, enemyType );
		var perceptionId = EntityPerceptionCatalog.BuildEntityId( enemyType, tier );
		brain.ApplyPerception( EntityPerceptionCatalog.Resolve( perceptionId ) );
		brain.SetHomePosition( root.WorldPosition );
		brain.Agent ??= agent;
		brain.Locomotion ??= locomotion;
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


	/// <summary>Reads a component the entity prefab must already carry. Missing means the asset is wrong — say so, don't patch it at runtime.</summary>
	static T Require<T>( GameObject root ) where T : Component
	{
		var existing = root.Components.Get<T>();
		if ( existing is not null )
			return existing;

		Log.Warning( $"[EntityEnemySetup] '{root.Name}' has no {typeof( T ).Name} — add it to the entity prefab; this spawn is skipped." );
		return null;
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
