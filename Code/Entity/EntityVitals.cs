using System;
using Sandbox;

namespace Survival;

/// <summary>Enemy health pool — max HP, armor, and type tuning (no regen).</summary>
[Title( "Entity Vitals" )]
public sealed class EntityVitals : Component
{
	[Property] public EnemyType EnemyType { get; set; } = EnemyType.Scav;

	[Property] public int Tier { get; set; } = 1;

	/// <summary>Feral / Robot / Ascended — set from the archetype at spawn.</summary>
	[Property] public EntityKind Kind { get; set; } = EntityKind.Feral;

	public bool IsMachineOrAscended => Kind is EntityKind.Robot or EntityKind.Ascended;

	[Property] public float MaxHealth { get; set; } = 80f;

	[Property] public float ArmorFlat { get; set; }

	public float CurrentHealth { get; private set; }
	public float CurrentHealthMax { get; private set; }

	public float HealthFraction =>
		CurrentHealthMax <= 1e-4f ? 0f : Math.Clamp( CurrentHealth / CurrentHealthMax, 0f, 1f );

	public bool IsDead => CurrentHealth <= 0.001f;

	/// <summary>Name shown on health bars instead of "Type Tn" (bosses set this from bosses.json).</summary>
	public string DisplayNameOverride { get; set; }

	/// <summary>
	/// Set by a form owner (<see cref="BossEntity"/>): a hit that would empty the pool is offered
	/// here before it counts as a death. Return true after refilling the pool to consume the hit —
	/// no kill credit, no <see cref="OnDied"/>. Null for every ordinary entity.
	/// </summary>
	public Func<Component, bool> LethalHitInterceptor { get; set; }

	public string GetDisplayName() =>
		string.IsNullOrWhiteSpace( DisplayNameOverride ) ? $"{EnemyType} T{Math.Max( 1, Tier )}" : DisplayNameOverride;

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
		_started = true;
		if ( GameObject.IsProxy )
			return;

		ResetToFull();
	}

	public void ResetToFull()
	{
		CurrentHealthMax = Math.Max( 1f, MaxHealth );
		CurrentHealth = _restoreHealth > 0f ? Math.Min( _restoreHealth, CurrentHealthMax ) : CurrentHealthMax;
		_restoreHealth = -1f;
		OnVitalsChanged?.Invoke();
	}

	bool _started;
	float _restoreHealth = -1f;

	/// <summary>
	/// Host: put the pool at <paramref name="current"/> (an entity coming back from <see cref="EntityChunkMemory"/>). Also
	/// survives a <see cref="ResetToFull"/> that has not run yet — a freshly cloned object's OnStart may land a frame later.
	/// </summary>
	public void RestoreHealth( float current )
	{
		var clamped = Math.Clamp( current, 0.001f, Math.Max( 1f, MaxHealth ) );
		if ( CurrentHealthMax > 1e-4f )
		{
			CurrentHealth = Math.Min( clamped, CurrentHealthMax );
			OnVitalsChanged?.Invoke();
		}

		// Only a ResetToFull still to come (OnStart not run yet) may re-apply it; never a later one.
		_restoreHealth = _started ? -1f : clamped;
	}

	public float ApplyDamage( float amount, Component attacker )
	{
		if ( IsDead || amount <= 0f )
			return 0f;

		var afterArmor = Math.Max( 0f, amount - ArmorFlat );
		if ( afterArmor <= 0f )
			return 0f;

		CurrentHealth = Math.Max( 0f, CurrentHealth - afterArmor );

		if ( IsDead && LethalHitInterceptor is { } interceptor && interceptor( attacker ) )
		{
			// The interceptor refilled the pool (its ResetToFull raised OnVitalsChanged) — this hit landed but nobody died.
			OnDamaged?.Invoke( attacker );
			return afterArmor;
		}

		OnVitalsChanged?.Invoke();
		OnDamaged?.Invoke( attacker );

		if ( IsDead )
		{
			// Quest credit goes to the pawn that landed the killing blow (before OnDied destroys us).
			PlayerQuests.HostReportKill( this, attacker );
			OnDied?.Invoke();
		}

		return afterArmor;
	}
}
