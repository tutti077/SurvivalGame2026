using System;

namespace Survival;

/// <summary>Default stats per <see cref="EnemyType"/> (tier scales health modestly).</summary>
public static class EntityArchetype
{
	public readonly struct Profile
	{
		public float MaxHealth { get; init; }
		public float ArmorFlat { get; init; }
		public float MoveSpeed { get; init; }
		public float TelegraphSeconds { get; init; }
		public float RecoverySeconds { get; init; }
		public float AttackHoldSeconds { get; init; }
	}

	public static Profile Get( EnemyType type ) => type switch
	{
		EnemyType.Boneback => new Profile
		{
			MaxHealth = 160f,
			ArmorFlat = 6f,
			MoveSpeed = 180f,
			TelegraphSeconds = 1.0f,
			RecoverySeconds = 1f,
			AttackHoldSeconds = 0.14f
		},
		EnemyType.Howler => new Profile
		{
			MaxHealth = 95f,
			ArmorFlat = 1f,
			MoveSpeed = 260f,
			TelegraphSeconds = 0.7f,
			RecoverySeconds = 0.75f,
			AttackHoldSeconds = 0.1f
		},
		_ => new Profile
		{
			MaxHealth = 80f,
			ArmorFlat = 0f,
			MoveSpeed = 220f,
			TelegraphSeconds = 0.85f,
			RecoverySeconds = 1f,
			AttackHoldSeconds = 0.12f
		}
	};

	public static void ApplyToVitals( EntityVitals vitals, EnemyType type, int tier )
	{
		if ( vitals is null )
			return;

		var profile = Get( type );
		var tierScale = 1f + Math.Max( 0, tier - 1 ) * 0.18f;
		vitals.EnemyType = type;
		vitals.Tier = Math.Max( 1, tier );
		vitals.MaxHealth = profile.MaxHealth * tierScale;
		vitals.ArmorFlat = profile.ArmorFlat;
	}

	public static void ApplyToCombat( EntityCombat combat, EnemyType type )
	{
		if ( combat is null )
			return;

		var profile = Get( type );
		combat.TelegraphSeconds = profile.TelegraphSeconds;
		combat.RecoverySeconds = profile.RecoverySeconds;
		combat.HoldSeconds = profile.AttackHoldSeconds;
	}

	public static void ApplyToBrain( EntityBrain brain, EnemyType type )
	{
		if ( brain is null )
			return;

		var profile = Get( type );
		brain.AttackRange = 110f;
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
