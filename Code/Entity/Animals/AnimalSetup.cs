using Sandbox;

namespace Survival;

/// <summary>Configures an animal prefab into a host-simulated animal (behavior tuning from <see cref="AnimalBehaviorCatalog"/>).</summary>
public static class AnimalSetup
{
	public static void Configure( GameObject root, AnimalSpecies species, float healthOverride = 0f, bool logStateDebug = false )
	{
		if ( root is null || !root.IsValid() )
			return;

		root.NetworkMode = NetworkMode.Object;
		root.Enabled = true;

		// Every component below is authored on the animal prefab (Commandment #5) — this only tunes them.
		var vitals = Require<EntityVitals>( root );
		var agent = Require<NavMeshAgent>( root );
		var locomotion = Require<EntityLocomotion>( root );
		var receiver = Require<DamageReceiver>( root );
		var brain = Require<AnimalBrain>( root );

		if ( vitals is null || agent is null || locomotion is null || receiver is null || brain is null )
			return;

		var behavior = AnimalBehaviorCatalog.Resolve( species );

		vitals.MaxHealth = healthOverride > 0f ? healthOverride : behavior.MaxHealth;
		vitals.ArmorFlat = 0f;
		vitals.ResetToFull();

		agent.UpdatePosition = false;
		agent.UpdateRotation = false;
		agent.Acceleration = 800f;
		agent.MaxSpeed = behavior.RunSpeed;
		agent.Enabled = true;

		var onNav = EntityEnemySetup.SnapToNavOrTerrain( root, agent );

		locomotion.Agent = agent;

		brain.Species = species;
		brain.LogStateDebug |= logStateDebug;
		brain.ApplyBehavior( behavior );
		brain.SetHomePosition( root.WorldPosition );
		brain.Agent ??= agent;
		brain.Locomotion ??= locomotion;
		brain.Enabled = true;

		// Nav ready → agent drives now; otherwise the brain walks the heightfield and
		// TickNavRecovery keeps trying to place the agent in the background.
		if ( onNav )
			brain.MarkAgentOnNav();
		brain.BeginAiNow();

		root.Name = $"animal_{species}";
		root.Tags.Add( "animal" );
	}

	/// <summary>Reads a component the animal prefab must already carry. Missing means the asset is wrong — say so, don't patch it at runtime.</summary>
	static T Require<T>( GameObject root ) where T : Component
	{
		var existing = root.Components.Get<T>();
		if ( existing is not null )
			return existing;

		Log.Warning( $"[AnimalSetup] '{root.Name}' has no {typeof( T ).Name} — add it to the animal prefab; this spawn is skipped." );
		return null;
	}
}
