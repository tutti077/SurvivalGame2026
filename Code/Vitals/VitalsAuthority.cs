using System;
using System.Collections.Generic;
using System.Linq;

namespace Survival;

/// <summary>
/// Regen delays use <see cref="RealTime.GlobalNow"/> so "wait N seconds" matches wall clock (unlike scaled <see cref="Time.NowDouble"/>).
/// Host-only source of truth for player health / stamina. Place on your network manager; <see cref="PlayerVitals"/> forwards changes here.
/// After each stamina <b>drain</b> (negative delta), regen is blocked until either <see cref="PlayerVitals.StaminaRegenDelayOverrideSeconds"/> (when &gt;= 0), <see cref="PlayerMovement.StaminaRegenDelayOverrideSeconds"/> (when &gt;= 0), or <see cref="StaminaRegenDelaySeconds"/> real seconds (<see cref="RealTime.GlobalNow"/>) have passed; then the rate ramps linearly from <see cref="StaminaRegenMinPerSecond"/> to <see cref="StaminaRegenMaxPerSecond"/> over <see cref="StaminaRegenRampSeconds"/>. Positive deltas do not move the deadline.
/// After each damaging health delta, regen is blocked until <see cref="HealthRegenDelayAfterDamageSeconds"/> real seconds (<see cref="RealTime.GlobalNow"/>) have passed; then HP refills at <see cref="HealthRegenPerSecond"/> up to max.
/// </summary>
[Title( "Vitals Authority" )]
public sealed class VitalsAuthority : Component
{
	public static VitalsAuthority Instance { get; private set; }

	/// <summary>Default seconds after the last time stamina was <b>reduced</b> (negative stamina delta in <see cref="TryApplyDeltas"/>) before regen begins, in real time (<see cref="RealTime.GlobalNow"/>). Non-negative per-pawn overrides take precedence.</summary>
	[Property, Group( "Stamina regen" )] public float StaminaRegenDelaySeconds { get; set; } = 4f;

	/// <summary>Stamina restored per second at the start of the regen ramp (right after <see cref="StaminaRegenDelaySeconds"/>). When equal to <see cref="StaminaRegenMaxPerSecond"/>, regen rate stays flat.</summary>
	[Property, Group( "Stamina regen" )] public float StaminaRegenMinPerSecond { get; set; } = 20f;

	/// <summary>Stamina restored per second once the ramp has been active for <see cref="StaminaRegenRampSeconds"/> (clamped &gt;= min).</summary>
	[Property, Group( "Stamina regen" )] public float StaminaRegenMaxPerSecond { get; set; } = 20f;

	/// <summary>Seconds for the regen rate to lerp from <see cref="StaminaRegenMinPerSecond"/> up to <see cref="StaminaRegenMaxPerSecond"/> after regen becomes eligible (no effect when min equals max).</summary>
	[Property, Group( "Stamina regen" )] public float StaminaRegenRampSeconds { get; set; } = 5f;

	[Property, Group( "Stamina regen" )] public bool LogStaminaRegenDebug { get; set; }

	[Property, Group( "Health regen" )] public bool HealthRegenEnabled { get; set; } = true;

	/// <summary>Hit points restored per second while regen is active (flat, not ramped).</summary>
	[Property, Group( "Health regen" )] public float HealthRegenPerSecond { get; set; } = 2f;

	/// <summary>Seconds after the last damaging health delta (<see cref="TryApplyDeltas"/> with negative health) before regen runs, in real time (<see cref="RealTime.GlobalNow"/>).</summary>
	[Property, Group( "Health regen" )] public float HealthRegenDelayAfterDamageSeconds { get; set; } = 10f;

	readonly Dictionary<Guid, VitalsRecord> _players = new();
	readonly Dictionary<Guid, PlayerVitals> _vitalsByPlayerId = new();
	readonly StaminaRegenDelayGate _staminaRegenGate = new();
	readonly HealthRegenDelayGate _healthRegenGate = new();
	readonly Dictionary<Guid, double> _lastStaminaRegenDebugLogTime = new();

	struct VitalsRecord
	{
		public float Health;
		public float HealthMax;
		public float Stamina;
		public float StaminaMax;
	}

	protected override void OnEnabled()
	{
		if ( Instance is not null && Instance != this )
			Log.Warning( $"{AuthorityLogPrefix()} Multiple enabled VitalsAuthority — Instance points at the last enabled." );
		Instance = this;
	}

	protected override void OnDisabled()
	{
		if ( Instance == this )
			Instance = null;
	}

	bool IsHostContext() =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>Host-side authority runs in <c>offline</c> or networked <c>host</c> contexts.</summary>
	string AuthorityLogRoleTag() =>
		GameObject.Network is not { Active: true } ? "offline" : ( Networking.IsHost ? "host" : "non-host" );

	string AuthorityLogPrefix() => $"[VitalsAuthority|{AuthorityLogRoleTag()}]";

	float ResolveStaminaRegenDelaySeconds( PlayerVitals vitals )
	{
		if ( vitals is not null )
			return vitals.ResolveStaminaRegenDelayForAuthority( StaminaRegenDelaySeconds );

		return StaminaRegenDelaySeconds;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsHostContext() )
			return;

		var dt = Time.Delta;
		if ( dt <= 0f )
			return;

		var now = RealTime.GlobalNow;
		var minRate = Math.Max( 0f, StaminaRegenMinPerSecond );
		var maxRate = Math.Max( minRate, StaminaRegenMaxPerSecond );
		var rampSeconds = Math.Max( 1e-3f, StaminaRegenRampSeconds );

		foreach ( var id in _players.Keys.ToArray() )
		{
			if ( !_vitalsByPlayerId.TryGetValue( id, out var vitals ) || vitals is null || !vitals.GameObject.IsValid() )
			{
				_vitalsByPlayerId.Remove( id );
				_staminaRegenGate.Clear( id );
				_healthRegenGate.Clear( id );
				_lastStaminaRegenDebugLogTime.Remove( id );
				continue;
			}

			if ( !_players.TryGetValue( id, out var r ) )
				continue;

			if ( r.Stamina < r.StaminaMax - 1e-4f )
			{
				var staminaDelaySeconds = Math.Max( 0f, ResolveStaminaRegenDelaySeconds( vitals ) );
				var blockStaminaRegen = vitals.GameObject.Components.Get<PlayerMovement>() is { } moveBlock
				                        && moveBlock.ShouldBlockStaminaRegenForAuthority();

				if ( !blockStaminaRegen
				     && _staminaRegenGate.MayRegenAfterDelay( id, now, staminaDelaySeconds, armFullDelayIfMissing: true, out var rampOriginUtc ) )
				{
					var rampT = (float)Math.Clamp( ( now - rampOriginUtc ) / rampSeconds, 0.0, 1.0 );
					var stRegenPerSec = minRate + ( maxRate - minRate ) * rampT;
					var addSt = MathF.Min( stRegenPerSec * dt, r.StaminaMax - r.Stamina );
					if ( addSt > 1e-5f )
					{
						TryApplyDeltas( vitals.GameObject, 0f, addSt, vitals );

						if ( LogStaminaRegenDebug
						     && ( !_lastStaminaRegenDebugLogTime.TryGetValue( id, out var lastLog ) || now - lastLog >= 1.0 ) )
						{
							_lastStaminaRegenDebugLogTime[id] = now;
							Log.Info( $"[VitalsAuthority|{AuthorityLogRoleTag()}|StaminaRegen] {vitals.GameObject.Name} ramp={rampT * 100f:0}% stRegen/s={stRegenPerSec:0.##}/s (min={minRate:0.#} max={maxRate:0.#} over {rampSeconds:0.##}s, tSinceEligible={(now - rampOriginUtc):0.###}s) +st={addSt:0.###} → {vitals.CurrentStamina:0.#}/{vitals.CurrentStaminaMax:0.#}" );
						}
					}
				}
			}
			else
			{
				_staminaRegenGate.Clear( id );
			}

			if ( !_players.TryGetValue( id, out r ) )
				continue;

			if ( !HealthRegenEnabled || HealthRegenPerSecond <= 0f )
				continue;

			if ( r.Health >= r.HealthMax - 1e-4f )
				continue;

			var hpDelay = Math.Max( 0f, HealthRegenDelayAfterDamageSeconds );
			if ( !_healthRegenGate.MayRegenAfterDelay( id, now, hpDelay, armFullDelayIfMissing: true, out _ ) )
				continue;

			var healthRegenRate = HealthRegenPerSecond;
			var addHp = Math.Min( healthRegenRate * dt, r.HealthMax - r.Health );
			if ( addHp <= 1e-5f )
				continue;

			TryApplyDeltas( vitals.GameObject, addHp, 0f, vitals );
		}
	}

	/// <summary>
	/// Host: if <paramref name="playerRoot"/> has no authority row yet, seed it from <paramref name="vitals"/> (current pools).
	/// Fixes regen / deltas when registration order was missed — does not overwrite an existing row.
	/// </summary>
	public void EnsureRecordFromVitalsIfMissing( GameObject playerRoot, PlayerVitals vitals )
	{
		if ( !IsHostContext() || playerRoot is null || !playerRoot.IsValid() || vitals is null )
			return;

		var id = playerRoot.Id;
		if ( _players.ContainsKey( id ) )
			return;

		var hMax = Math.Max( 1f, vitals.CurrentHealthMax );
		var sMax = Math.Max( 0f, vitals.CurrentStaminaMax );
		var r = new VitalsRecord
		{
			Health = Math.Clamp( vitals.CurrentHealth, 0f, hMax ),
			HealthMax = hMax,
			Stamina = Math.Clamp( vitals.CurrentStamina, 0f, sMax ),
			StaminaMax = sMax
		};
		_players[id] = r;
		_vitalsByPlayerId[id] = vitals;
		var nowSeed = RealTime.GlobalNow;
		if ( r.Stamina < r.StaminaMax - 1e-4f )
			_staminaRegenGate.ArmNoRegenBefore( id, nowSeed, ResolveStaminaRegenDelaySeconds( vitals ) );
		if ( r.Health < r.HealthMax - 1e-4f )
			_healthRegenGate.ArmNoRegenBefore( id, nowSeed, HealthRegenDelayAfterDamageSeconds );
	}

	/// <summary>Host: reset or create vitals for a pawn and return the snapshot.</summary>
	/// <param name="forceFullPoolsAndResetRegenClocks">
	/// When the pawn is <b>already</b> registered: if true, refill pools and restart stamina regen clocks (e.g. respawn). If false, only refresh max caps and clamp — does <b>not</b> reset the host’s last-stamina-drain time, so the post-use regen delay is not restarted by duplicate <c>RpcRegisterPlayerDefaults</c> / registration retries.
	/// </param>
	public VitalsSnapshot? RegisterAndGetSnapshot( GameObject playerRoot, float healthMax, float staminaMax, bool forceFullPoolsAndResetRegenClocks = false )
	{
		if ( !IsHostContext() )
			return null;

		var id = playerRoot.Id;
		var hMax = Math.Max( 1f, healthMax );
		var sMax = Math.Max( 0f, staminaMax );

		if ( _players.TryGetValue( id, out var existing ) )
		{
			if ( forceFullPoolsAndResetRegenClocks )
			{
				existing = new VitalsRecord
				{
					Health = hMax,
					HealthMax = hMax,
					Stamina = sMax,
					StaminaMax = sMax
				};
				_players[id] = existing;
				_staminaRegenGate.Clear( id );
				_healthRegenGate.Clear( id );
			}
			else
			{
				existing.HealthMax = hMax;
				existing.StaminaMax = sMax;
				existing.Health = Math.Clamp( existing.Health, 0f, hMax );
				existing.Stamina = Math.Clamp( existing.Stamina, 0f, sMax );
				_players[id] = existing;
			}

			var vitalsExisting = playerRoot.Components.Get<PlayerVitals>();
			if ( vitalsExisting is not null )
				_vitalsByPlayerId[id] = vitalsExisting;

			return ToSnapshot( _players[id] );
		}

		var r = new VitalsRecord
		{
			Health = hMax,
			HealthMax = hMax,
			Stamina = sMax,
			StaminaMax = sMax
		};
		_players[id] = r;
		_staminaRegenGate.Clear( id );
		_healthRegenGate.Clear( id );

		var vitals = playerRoot.Components.Get<PlayerVitals>();
		if ( vitals is not null )
			_vitalsByPlayerId[id] = vitals;

		return ToSnapshot( r );
	}

	/// <summary>Host: apply deltas (negative = damage / stamina cost). Updates <paramref name="vitals"/> and syncs to owner.</summary>
	/// <param name="mergePendingSprintDebtForNegativeStamina">When true and stamina delta is negative, pull <see cref="PlayerMovement.TakePendingSprintStaminaDebt"/> into the spend. Set false when the caller already merged (e.g. client Rpc payload).</param>
	/// <param name="clientAuthorityStaminaBeforeSpend">Optional owner baseline (≈ displayed stamina + unsynced sprint debt) for negative stamina deltas — rejects cheats/large desync vs <see cref="VitalsRecord.Stamina"/>.</param>
	public bool TryApplyDeltas( GameObject playerRoot, float healthDelta, float staminaDelta, PlayerVitals vitals, bool mergePendingSprintDebtForNegativeStamina = true, float? clientAuthorityStaminaBeforeSpend = null )
	{
		if ( !IsHostContext() || vitals is null || !vitals.GameObject.IsValid() )
			return false;

		var nowT = RealTime.GlobalNow;

		// Always key authority rows by the GameObject that owns this vitals component (callers sometimes passed a parent root).
		var root = vitals.GameObject;
		if ( playerRoot.IsValid() && playerRoot.Id != root.Id )
			Log.Warning( $"{AuthorityLogPrefix()} TryApplyDeltas: playerRoot={playerRoot.Name} id≠ vitals root={root.Name} — using vitals.GameObject as key." );

		var id = root.Id;

		if ( mergePendingSprintDebtForNegativeStamina && staminaDelta < 0f && root.Components.Get<PlayerMovement>() is { } pm )
		{
			var extraDebt = pm.TakePendingSprintStaminaDebt();
			if ( extraDebt > 1e-6f )
			{
				staminaDelta -= extraDebt;
				if ( vitals.LogVitalsNetworking )
					Log.Info( $"{AuthorityLogPrefix()} {root.Name}: merged sprint preview debt −{extraDebt:0.###} into spend (total Δst={staminaDelta:0.###})" );
			}
		}
		if ( !_players.TryGetValue( id, out var r ) )
		{
			var hMax = Math.Max( 1f, vitals.MaxHealth );
			var sMax = Math.Max( 0f, vitals.MaxStamina );
			r = new VitalsRecord { Health = hMax, HealthMax = hMax, Stamina = sMax, StaminaMax = sMax };
			_players[id] = r;
		}

		r.Health = Math.Clamp( r.Health + healthDelta, 0f, r.HealthMax );

		if ( healthDelta < 0f )
			_healthRegenGate.ArmNoRegenBefore( id, nowT, HealthRegenDelayAfterDamageSeconds );

		if ( staminaDelta < 0f )
		{
			if ( vitals.InfiniteStaminaDebug )
			{
				r.Stamina = r.StaminaMax;
				_players[id] = r;
				_vitalsByPlayerId[id] = vitals;
				vitals.ApplyFromAuthorityAndSync( ToSnapshot( r ) );
				return true;
			}

			var drain = -staminaDelta;
			if ( clientAuthorityStaminaBeforeSpend is { } clientSt
			     && Networking.IsHost
			     && vitals.MayIssueVitalsDelta() )
			{
				var tol = MathF.Max( 0.25f, 0.002f * r.StaminaMax );
				if ( MathF.Abs( r.Stamina - clientSt ) > tol )
				{
					if ( vitals.LogVitalsNetworking )
						Log.Warning( $"{AuthorityLogPrefix()} {root.Name}: stamina baseline mismatch — host={r.Stamina:0.####} client≈{clientSt:0.####} (tol={tol:0.####}) Δst={staminaDelta:0.####} — rejecting" );
					vitals.ApplyFromAuthorityAndSync( ToSnapshot( r ) );
					return false;
				}
			}

			if ( r.Stamina + 1e-4f < drain )
			{
				vitals.ApplyFromAuthorityAndSync( ToSnapshot( r ) );
				return false;
			}

			r.Stamina = Math.Clamp( r.Stamina + staminaDelta, 0f, r.StaminaMax );
			var resolvedDelay = Math.Max( 0f, ResolveStaminaRegenDelaySeconds( vitals ) );
			_staminaRegenGate.ArmNoRegenBefore( id, nowT, resolvedDelay );
			vitals.RecordStaminaDrainForAuthority( drain, resolvedDelay );
			if ( LogStaminaRegenDebug )
				Log.Info( $"[VitalsAuthority|{AuthorityLogRoleTag()}|StaminaRegen] {root.Name}: armed drain delay={resolvedDelay:0.###}s drain={drain:0.###}" );
		}
		else
			r.Stamina = Math.Clamp( r.Stamina + staminaDelta, 0f, r.StaminaMax );

		_players[id] = r;

		_vitalsByPlayerId[id] = vitals;
		var snap = ToSnapshot( r );
		vitals.ApplyFromAuthorityAndSync( snap );
		return true;
	}

	static VitalsSnapshot ToSnapshot( VitalsRecord r ) =>
		new( r.Health, r.HealthMax, r.Stamina, r.StaminaMax );
}
