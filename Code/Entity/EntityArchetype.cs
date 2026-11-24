using System;

namespace Survival;

/// <summary>What an entity is made of — some augments (Medusa Eye, Hackd) only bite on machines and the ascended.</summary>
public enum EntityKind
{
	Feral = 0,
	Robot = 1,
	Ascended = 2,
}

/// <summary>Default stats per <see cref="EnemyType"/> (tier scales health modestly).</summary>
public static class EntityArchetype
{
	public readonly struct Profile
	{
		public EntityKind Kind { get; init; }
		public float MaxHealth { get; init; }
		public float ArmorFlat { get; init; }
		public float MoveSpeed { get; init; }
		/// <summary>Which foot traps hold this type (<see cref="BearTrap"/>). Boneback is the heavy one.</summary>
		public TrapSize TrapSize { get; init; }
	}

	public static Profile Get( EnemyType type ) => type switch
	{
		EnemyType.Boneback => new Profile
		{
			MaxHealth = 160f,
			ArmorFlat = 6f,
			MoveSpeed = 180f,
			TrapSize = TrapSize.Large
		},
		// Patrol bot: the scav's numbers with a robot's insides.
		EnemyType.PatrolBot => new Profile
		{
			Kind = EntityKind.Robot,
			MaxHealth = 80f,
			ArmorFlat = 0f,
			MoveSpeed = 220f,
			TrapSize = TrapSize.Small
		},
		EnemyType.Howler => new Profile
		{
			MaxHealth = 95f,
			ArmorFlat = 1f,
			MoveSpeed = 260f,
			TrapSize = TrapSize.Small
		},
		_ => new Profile
		{
			MaxHealth = 80f,
			ArmorFlat = 0f,
			MoveSpeed = 220f,
			TrapSize = TrapSize.Small
		}
	};

	public static void ApplyToVitals( EntityVitals vitals, EnemyType type, int tier )
	{
		if ( vitals is null )
			return;

		var profile = Get( type );
		var tierScale = 1f + Math.Max( 0, tier - 1 ) * 0.18f;
		vitals.EnemyType = type;
		vitals.Kind = profile.Kind;
		vitals.Tier = Math.Max( 1, tier );
		vitals.MaxHealth = profile.MaxHealth * tierScale;
		vitals.ArmorFlat = profile.ArmorFlat;
	}

	public static void ApplyToBrain( EntityBrain brain, EnemyType type )
	{
		if ( brain is null )
			return;

		var profile = Get( type );
		brain.WanderMoveSpeed = Math.Max( 72f, profile.MoveSpeed * 0.4f );
		brain.ChaseMoveSpeed = Math.Max( 160f, profile.MoveSpeed );
	}

	public static void ApplyToAgent( NavMeshAgent agent, EnemyType type )
	{
		if ( agent is null )
			return;

		agent.MaxSpeed = Get( type ).MoveSpeed;
	}
}
