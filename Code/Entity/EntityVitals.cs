using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Enemy health pool — max HP, armor, and type tuning (no health regen) — and a host-side stamina bar its melee spends
/// like a player's (Mark 2026-11-14 scav sparring: "very close to fighting rumple"): the swing / block / feint / heavy costs
/// of its <see cref="PlayerCombat"/>, the player's regen rates and delays (<see cref="VitalsAuthority"/>), exhaustion at
/// empty until <see cref="ExhaustionRecoverStamina"/>, and the host's Dev tools infinite stamina.
/// </summary>
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

	[Property, Group( "Stamina" )] public float MaxStamina { get; set; } = 100f;

	/// <summary>An empty bar locks stamina-costing combat until it refills to this (the player rule).</summary>
	[Property, Group( "Stamina" ), Title( "Exhaustion recovers at" )] public float ExhaustionRecoverStamina { get; set; } = 20f;

	/// <summary>Host only (scav fighting runs there).</summary>
	public float CurrentStamina { get; private set; } = 100f;

	public bool IsStaminaExhausted => _staminaExhausted && !DevToolsSettings.InfiniteStamina;

	bool _staminaExhausted;
	double _staminaRegenAt;

	/// <summary>Its <see cref="PlayerCombat"/> (scavs fight through one) holds a raised guard.</summary>
	bool IsGuardRaised
	{
		get
		{
			if ( !_combatSearched )
			{
				_combatSearched = true;
				_combat = Components.Get<PlayerCombat>();
			}

			return _combat is { IsAuthoritativeMeleeBlocking: true };
		}
	}

	PlayerCombat _combat;
	bool _combatSearched;
	Vector3 _staminaLastPos;

	/// <summary>Host: pay <paramref name="cost"/> stamina (false when the bar cannot), regen paused for <paramref name="regenDelaySeconds"/>.</summary>
	internal bool HostTrySpendStamina( float cost, float regenDelaySeconds )
	{
		if ( cost <= 1e-4f || DevToolsSettings.InfiniteStamina )
			return true;
		if ( CurrentStamina + 1e-4f < cost )
			return false;

		CurrentStamina = Math.Max( 0f, CurrentStamina - cost );
		if ( CurrentStamina <= 0.01f )
			_staminaExhausted = true;
		_staminaRegenAt = Math.Max( _staminaRegenAt, Time.NowDouble + Math.Max( 0f, regenDelaySeconds ) );
		return true;
	}

	/// <summary>Host: a blow the guard could not pay for empties the bar.</summary>
	internal void HostEmptyStamina( float regenDelaySeconds )
	{
		if ( DevToolsSettings.InfiniteStamina )
			return;
		CurrentStamina = 0f;
		_staminaExhausted = true;
		_staminaRegenAt = Math.Max( _staminaRegenAt, Time.NowDouble + Math.Max( 0f, regenDelaySeconds ) );
	}

	protected override void OnUpdate()
	{
		if ( IsProxy || IsDead )
			return;

		var pos = WorldPosition;
		var moving = (pos - _staminaLastPos).WithZ( 0f ).Length > 20f * Math.Max( Time.Delta, 1e-4f );
		_staminaLastPos = pos;

		var max = Math.Max( 1f, MaxStamina );
		if ( DevToolsSettings.InfiniteStamina )
		{
			CurrentStamina = max;
			_staminaExhausted = false;
			return;
		}

		// A raised guard blocks regen (the player rule, Mark 2026-11-16).
		if ( CurrentStamina < max && Time.NowDouble >= _staminaRegenAt && !IsGuardRaised )
		{
			var auth = VitalsAuthority.Instance;
			var rate = moving ? auth?.StaminaRegenMovingPerSecond ?? 15f : auth?.StaminaRegenIdlePerSecond ?? 20f;
			CurrentStamina = Math.Min( max, CurrentStamina + rate * Time.Delta );
		}

		if ( _staminaExhausted && CurrentStamina >= Math.Min( ExhaustionRecoverStamina, max ) )
			_staminaExhausted = false;
	}

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

	/// <summary>The banner name: the enemy type alone (Mark 2026-11-04: "drop the T1 from the Scav"), or the override.</summary>
	public string GetDisplayName() =>
		string.IsNullOrWhiteSpace( DisplayNameOverride ) ? $"{EnemyType}" : DisplayNameOverride;

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
		CurrentStamina = Math.Max( 1f, MaxStamina );
		_staminaExhausted = false;
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
			PlaytestLog.HostEntityDamaged( this, attacker, afterArmor );
			OnDamaged?.Invoke( attacker );
			return afterArmor;
		}

		PlaytestLog.HostEntityDamaged( this, attacker, afterArmor );
		OnVitalsChanged?.Invoke();
		OnDamaged?.Invoke( attacker );

		if ( IsDead )
		{
			// Quest credit goes to the pawn that landed the killing blow (before OnDied destroys us).
			PlaytestLog.HostEntityKilled( this, attacker );
			PlayerQuests.HostReportKill( this, attacker );
			OnDied?.Invoke();
		}

		return afterArmor;
	}
}
