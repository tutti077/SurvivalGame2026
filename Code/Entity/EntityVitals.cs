using System;
using Sandbox;

namespace Survival;

/// <summary>Enemy health pool — max HP, armor, and type tuning (no regen).</summary>
[Title( "Entity Vitals" )]
public sealed class EntityVitals : Component
{
	[Property] public EnemyType EnemyType { get; set; } = EnemyType.Scav;

	[Property] public int Tier { get; set; } = 1;

	[Property] public float MaxHealth { get; set; } = 80f;

	[Property] public float ArmorFlat { get; set; }

	public float CurrentHealth { get; private set; }
	public float CurrentHealthMax { get; private set; }

	public float HealthFraction =>
		CurrentHealthMax <= 1e-4f ? 0f : Math.Clamp( CurrentHealth / CurrentHealthMax, 0f, 1f );

	public bool IsDead => CurrentHealth <= 0.001f;

	public string GetDisplayName() => $"{EnemyType} T{Math.Max( 1, Tier )}";

	public string GetHealthLabel()
	{
		var current = MathF.Ceiling( CurrentHealth );
		var max = MathF.Ceiling( CurrentHealthMax );
		return $"{GetDisplayName()} - {current}/{max}";
	}

	public event Action OnVitalsChanged;
	public event Action OnDied;
	public event Action<Component> OnDamaged;

	protected override void OnStart()
	{
		if ( GameObject.IsProxy )
			return;

		ResetToFull();
	}

	public void ResetToFull()
	{
		CurrentHealthMax = Math.Max( 1f, MaxHealth );
		CurrentHealth = CurrentHealthMax;
		OnVitalsChanged?.Invoke();
	}

	public float ApplyDamage( float amount, Component attacker )
	{
		if ( IsDead || amount <= 0f )
			return 0f;

		var afterArmor = Math.Max( 0f, amount - ArmorFlat );
		if ( afterArmor <= 0f )
			return 0f;

		CurrentHealth = Math.Max( 0f, CurrentHealth - afterArmor );
		OnVitalsChanged?.Invoke();
		OnDamaged?.Invoke( attacker );

		if ( IsDead )
			OnDied?.Invoke();

		return afterArmor;
	}
}
