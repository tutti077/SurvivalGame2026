#nullable disable
using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Pawn vitals: health + stamina pool display plus authority/RPC bridge. Authoritative numbers live on <see cref="VitalsAuthority"/>; other systems
/// mutate pools through <see cref="RequestVitalsDelta"/> / <see cref="TrySpendStamina"/> or combat via <see cref="ApplyDamageAfterArmor"/> on the host.
/// Movement input rules (jump/sprint actions and stamina gating choices) are owned by <see cref="PlayerMovement"/>, which calls this component to spend/check pool values.
/// </summary>
[Title( "Player Vitals" )]
public sealed class PlayerVitals : Component
{
	[Property] public float MaxHealth { get; set; } = 100f;

	[Property] public float MaxStamina { get; set; } = 100f;

	/// <summary>
	/// Optional per-player stamina regen delay override in seconds. Use a value >= 0 to override
	/// <see cref="VitalsAuthority.StaminaRegenDelaySeconds"/> for this pawn; negative values use the authority/default source.
	/// </summary>
	[Property, Group( "Stamina" )] public float StaminaRegenDelayOverrideSeconds { get; set; } = -1f;

	/// <summary>Subtracted from incoming damage before health (client hint; host still applies via authority).</summary>
	[Property] public float ArmorFlat { get; set; }

	[Property, Group( "Debug" )] public bool LogVitalsNetworking { get; set; } = true;

	[Property, Group( "Debug" )] public bool LogWhenStaminaReachesFull { get; set; } = true;

	/// <summary>Debug: ignore stamina costs and keep the bar full. Remove before shipping.</summary>
	[Property, Group( "Debug" )] public bool InfiniteStaminaDebug { get; set; }

	/// <summary>Optional spawn root; if unset, the first enabled <see cref="SpawnPoint"/> in this scene is used.</summary>
	[Property, Group( "Death / Respawn" )] public GameObject RespawnPointOverride { get; set; }

	public float CurrentHealth { get; private set; }
	public float CurrentHealthMax { get; private set; }
	public float CurrentStamina { get; private set; }
	public float CurrentStaminaMax { get; private set; }
	public double LastStaminaDrainArmedAtRealtime { get; private set; }
	public float LastStaminaRegenDelayResolvedSeconds { get; private set; }

	/// <summary>Last melee stagger amount applied by the host (hook for future hit reactions).</summary>
	public float LastMeleeStaggerApplied { get; private set; }

	public void ApplyMeleeStagger( float amount )
	{
		if ( amount <= 1e-4f )
			return;
		LastMeleeStaggerApplied = amount;
	}
	public float LastStaminaDrainAmount { get; private set; }

	/// <summary>Raised when any displayed vital changes (for HUD).</summary>
	public event Action OnVitalsChanged;

	bool _pendingDeathRespawnHost;

	/// <summary>Jump stamina charged for the current airborne cycle; cleared in <see cref="OnControllerLandedForJumpStaminaFromMovement"/> so duplicate jump events do not stack spends.</summary>
	bool _jumpStaminaChargedThisAirborne;

	/// <summary>When <see cref="VitalsAuthority.Instance"/> was not ready in <see cref="OnStart"/>, retry registration so regen ticks see this pawn.</summary>
	bool _pendingAuthorityRegistration;

	int _staminaFullLogCount;

	/// <summary>
	/// Short process/network role for vitals-related logs: <c>offline</c>, <c>host</c>, <c>client</c>, <c>proxy</c>, or <c>non-owner</c>.
	/// </summary>
	public static string GetVitalsProcessRoleTag( GameObject pawnRoot )
	{
		if ( pawnRoot is null || !pawnRoot.IsValid() )
			return "unknown";

		if ( pawnRoot.Network is not { Active: true } )
			return "offline";

		if ( pawnRoot.IsProxy )
			return "proxy";

		if ( Networking.IsHost )
			return "host";

		if ( pawnRoot.Network is { IsOwner: true } )
			return "client";

		return "non-owner";
	}

	string VitalsLogPrefix() => $"[PlayerVitals|{GetVitalsProcessRoleTag( GameObject )}]";

	bool IsHostOrOffline =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>
	/// <see cref="CombatAuthority.TryFindDamageable"/> requires a <see cref="DamageReceiver"/> on the hit hierarchy; keep one on the pawn root next to this component.
	/// </summary>
	void EnsureDamageReceiverForMelee()
	{
		if ( GameObject.IsProxy )
			return;

		if ( Components.Get<DamageReceiver>() is not null )
			return;

		GameObject.Components.Create<DamageReceiver>();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _pendingAuthorityRegistration )
		{
			if ( GameObject.Network is { Active: true } && !Networking.IsHost )
				_pendingAuthorityRegistration = false;
			else if ( VitalsAuthority.Instance is { } auth )
			{
				var snap = auth.RegisterAndGetSnapshot( GameObject, MaxHealth, MaxStamina, false );
				if ( snap is { } s )
				{
					ApplyFromAuthorityAndSync( s );
					_pendingAuthorityRegistration = false;
				}
			}
		}

		if ( _pendingDeathRespawnHost )
			TryRunHostDeathRespawn();

		MaintainInfiniteStaminaDebugDisplay();
	}

	void MaintainInfiniteStaminaDebugDisplay()
	{
		if ( !InfiniteStaminaDebug )
			return;

		var max = Math.Max( 0f, CurrentStaminaMax );
		if ( MathF.Abs( CurrentStamina - max ) <= 0.01f )
			return;

		CurrentStamina = max;
		OnVitalsChanged?.Invoke();
	}

	protected override void OnStart()
	{
		base.OnStart();
		_jumpStaminaChargedThisAirborne = false;

		EnsureDamageReceiverForMelee();

		if ( GameObject.IsProxy && !Networking.IsHost )
			return;

		if ( GameObject.Network is not { Active: true } )
		{
			TryRegisterWithAuthorityOrLocalDefaults();
			return;
		}

		if ( Networking.IsHost )
			TryRegisterWithAuthorityOrLocalDefaults();
		else
			RpcRegisterPlayerDefaults( MaxHealth, MaxStamina );
	}

	/// <summary>Host / offline: enter <see cref="VitalsAuthority"/> bookkeeping when possible (required for server-side regen). Defers if authority is not enabled yet.</summary>
	void TryRegisterWithAuthorityOrLocalDefaults()
	{
		var auth = VitalsAuthority.Instance;
		if ( auth is not null )
		{
			var snap = auth.RegisterAndGetSnapshot( GameObject, MaxHealth, MaxStamina, false );
			if ( snap is { } s )
			{
				ApplyFromAuthorityAndSync( s );
				_pendingAuthorityRegistration = false;
				return;
			}
		}

		ApplyLocalSnapshot( new VitalsSnapshot( MaxHealth, MaxHealth, MaxStamina, MaxStamina ) );
		_pendingAuthorityRegistration = auth is null;
	}

	/// <summary>
	/// Movement-owned jump rule calls into vitals to spend stamina once per airborne cycle.
	/// Returns true when movement should apply exhausted jump velocity scaling.
	/// </summary>
	public bool OnControllerJumpedForStaminaFromMovement( float jumpStaminaCost, float exhaustedJumpHeightFraction )
	{
		if ( !IsLocalInputOwnedPawn() )
			return false;

		if ( jumpStaminaCost <= 0f )
			return false;

		if ( _jumpStaminaChargedThisAirborne )
			return false;

		if ( !CanAffordStamina( jumpStaminaCost ) )
		{
			if ( exhaustedJumpHeightFraction <= 0f )
				return false;

			_jumpStaminaChargedThisAirborne = true;
			return true;
		}

		_jumpStaminaChargedThisAirborne = true;
		if ( !TrySpendStamina( jumpStaminaCost ) )
			_jumpStaminaChargedThisAirborne = false;
		return false;
	}

	public void OnControllerLandedForJumpStaminaFromMovement( float distance, Vector3 impactVelocity )
	{
		if ( !IsLocalInputOwnedPawn() )
			return;

		_jumpStaminaChargedThisAirborne = false;
	}

	/// <summary>True if current stamina can pay <paramref name="cost"/> (cost ≤ 0 always passes).</summary>
	public bool HasStaminaFor( float cost )
	{
		if ( InfiniteStaminaDebug )
			return true;

		if ( cost <= 0f )
			return true;

		return CurrentStamina + 1e-4f >= cost;
	}

	/// <inheritdoc cref="HasStaminaFor"/>
	public bool CanAffordStamina( float staminaCost ) => HasStaminaFor( staminaCost );

	/// <summary>Local owning client / host (called from <see cref="PlayerMovement"/>).</summary>
	public bool IsLocalInputOwnedPawn()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } n && !n.IsOwner )
			return false;

		return true;
	}

	/// <summary>
	/// True when this process may request vitals mutations for this pawn: not a proxy; and either the host, or the owning client.
	/// </summary>
	public bool MayIssueVitalsDelta()
	{
		if ( GameObject.IsProxy )
			return false;

		if ( GameObject.Network is { Active: true } n && !Networking.IsHost && !n.IsOwner )
			return false;

		return true;
	}

	/// <summary>
	/// Owner / host path: spends stamina through <see cref="RequestVitalsDelta"/> if affordable. Fails closed when the host authority rejects the spend.
	/// </summary>
	/// <returns>False if not allowed on this machine, unaffordable, or authority rejected the drain.</returns>
	public bool TrySpendStamina( float staminaCost )
	{
		if ( staminaCost <= 0f )
			return true;

		if ( InfiniteStaminaDebug )
			return true;

		if ( !CanAffordStamina( staminaCost ) )
			return false;

		if ( !MayIssueVitalsDelta() )
			return false;

		float? authorityStaminaBeforeSpend = null;
		if ( GameObject.Network is { Active: true } && !GameObject.IsProxy )
		{
			var sprintDebt = Components.Get<PlayerMovement>()?.PeekPendingSprintStaminaDebt() ?? 0f;
			authorityStaminaBeforeSpend = CurrentStamina + sprintDebt;
		}

		return RequestVitalsDelta( 0f, -staminaCost, mergePendingSprintDebt: true, clientAuthorityStaminaBeforeSpend: authorityStaminaBeforeSpend );
	}

	/// <summary>
	/// Owner-only: reduces displayed stamina during sprint without contacting the host. Next <see cref="ApplyLocalSnapshot"/> / authority sync replaces this with truth.
	/// </summary>
	public void ApplyLocalStaminaSprintPreviewSpend( float decrease )
	{
		if ( InfiniteStaminaDebug || decrease <= 0f || !IsLocalInputOwnedPawn() )
			return;

		CurrentStamina = Math.Max( 0f, CurrentStamina - decrease );
		OnVitalsChanged?.Invoke();
	}

	/// <summary>Owner-only: undo preview if the host rejected the sprint reconciliation delta.</summary>
	public void RestoreLocalStaminaAfterFailedSprintSpend( float restoreAmount )
	{
		if ( restoreAmount <= 0f || !IsLocalInputOwnedPawn() )
			return;

		CurrentStamina = Math.Min( CurrentStaminaMax, CurrentStamina + restoreAmount );
		OnVitalsChanged?.Invoke();
	}

	public static void ClearJumpInputIfPressed( string jumpInputAction )
	{
		var configured = jumpInputAction?.Trim();
		if ( !string.IsNullOrWhiteSpace( configured ) )
			ClearSpecificInputAction( configured );

		// Keep jump gating resilient to action-name case drift in scene/prefab data.
		ClearSpecificInputAction( "jump" );
		ClearSpecificInputAction( "Jump" );
	}

	static bool ClearSpecificInputAction( string action )
	{
		if ( string.IsNullOrWhiteSpace( action ) )
			return false;

		if ( !Input.Pressed( action ) && !Input.Down( action ) )
			return false;

		Input.SetAction( action, false );
		Input.ReleaseAction( action );
		return true;
	}

	/// <summary>True when current stamina is at or below the provided exhausted threshold.</summary>
	public bool IsStaminaExhausted( float exhaustedStaminaEpsilon ) =>
		!InfiniteStaminaDebug && CurrentStamina <= Math.Max( 0f, exhaustedStaminaEpsilon );

	/// <summary>
	/// Host authority asks vitals for per-pawn stamina regen delay so lookup stays tied to this owning vitals root.
	/// Negative vitals and movement overrides mean "use authority default".
	/// </summary>
	public float ResolveStaminaRegenDelayForAuthority( float authorityDefaultSeconds )
	{
		if ( StaminaRegenDelayOverrideSeconds >= 0f )
			return StaminaRegenDelayOverrideSeconds;

		var movement = Components.Get<PlayerMovement>();
		if ( movement is not null && movement.StaminaRegenDelayOverrideSeconds >= 0f )
			return movement.StaminaRegenDelayOverrideSeconds;
		return authorityDefaultSeconds;
	}

	/// <summary>
	/// Host-only tracking of the last stamina-consuming event that armed regen delay.
	/// </summary>
	public void RecordStaminaDrainForAuthority( float drainAmount, float resolvedDelaySeconds )
	{
		LastStaminaDrainAmount = Math.Max( 0f, drainAmount );
		LastStaminaRegenDelayResolvedSeconds = Math.Max( 0f, resolvedDelaySeconds );
		LastStaminaDrainArmedAtRealtime = RealTime.GlobalNow;
	}

	/// <summary>Host / offline: apply damage after flat armor; networked clients should not hit this for real hits.</summary>
	/// <returns>Damage removed from health (after armor).</returns>
	public float ApplyDamageAfterArmor( float incoming, Component attacker )
	{
		var afterArmor = Math.Max( 0f, incoming - ArmorFlat );

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return 0f;

		var auth = VitalsAuthority.Instance;
		if ( auth is not null )
		{
			auth.TryApplyDeltas( GameObject, -afterArmor, 0f, this );
			var attackerName = attacker is not null ? attacker.GameObject.Name : "—";
			Log.Info( $"{VitalsLogPrefix()} {GameObject.Name} −{afterArmor:0.#} HP (pre-armor {incoming:0.#}, flat {ArmorFlat:0.#}) from {attackerName} → {CurrentHealth:0.#}/{CurrentHealthMax:0.#} HP" );
			return afterArmor;
		}

		var newHealth = Math.Max( 0f, CurrentHealth - afterArmor );
		ApplyLocalSnapshot( new VitalsSnapshot( newHealth, CurrentHealthMax, CurrentStamina, CurrentStaminaMax ) );
		Log.Info( $"{VitalsLogPrefix()} {GameObject.Name} −{afterArmor:0.#} HP (local, no VitalsAuthority) → {CurrentHealth:0.#} HP" );
		return afterArmor;
	}

	/// <summary>Owner / host: ask host to change health / stamina by delta (e.g. stamina drain). Combat damage uses <see cref="ApplyDamageAfterArmor"/> on the host.</summary>
	/// <param name="mergePendingSprintDebt">When true and <paramref name="staminaDelta"/> is negative, unsynced sprint preview debt from <see cref="PlayerMovement"/> is folded into this spend once (host via <see cref="VitalsAuthority.TryApplyDeltas"/>, owner client before Rpc). Use false for sprint flush deltas that already carry the full debt.</param>
	/// <returns>False when this process may not issue the request, or the host authority rejected the mutation.</returns>
	public bool RequestVitalsDelta( float healthDelta, float staminaDelta, bool mergePendingSprintDebt = true, float? clientAuthorityStaminaBeforeSpend = null )
	{
		if ( !MayIssueVitalsDelta() )
			return false;

		if ( InfiniteStaminaDebug && staminaDelta < 0f )
			return true;

		if ( mergePendingSprintDebt && staminaDelta < 0f && !Networking.IsHost && GameObject.Network is { Active: true }
		     && Components.Get<PlayerMovement>() is { } pmPre )
		{
			var extraDebt = pmPre.TakePendingSprintStaminaDebt();
			if ( extraDebt > 1e-6f )
			{
				staminaDelta -= extraDebt;
				LogVitalsNetwork( $"client merged sprint preview debt −{extraDebt:0.###} before Rpc (Δst={staminaDelta:0.###})" );
			}
		}

		if ( GameObject.Network is not { Active: true } )
		{
			if ( VitalsAuthority.Instance is not null && VitalsAuthority.Instance.TryApplyDeltas( GameObject, healthDelta, staminaDelta, this, mergePendingSprintDebtForNegativeStamina: mergePendingSprintDebt, clientAuthorityStaminaBeforeSpend: clientAuthorityStaminaBeforeSpend ) )
			{
				LogVitalsNetwork( $"offline authority TryApplyDeltas Δhp={healthDelta:0.####} Δst={staminaDelta:0.####} → HP={CurrentHealth:0.#}/{CurrentHealthMax:0.#} ST={CurrentStamina:0.#}/{CurrentStaminaMax:0.#}" );
				return true;
			}

			var hMax = Math.Max( 1f, CurrentHealthMax );
			var sMax = Math.Max( 0f, CurrentStaminaMax );
			ApplyLocalSnapshot( new VitalsSnapshot(
				Math.Clamp( CurrentHealth + healthDelta, 0f, hMax ),
				hMax,
				Math.Clamp( CurrentStamina + staminaDelta, 0f, sMax ),
				sMax ) );
			return true;
		}

		if ( Networking.IsHost )
		{
			if ( VitalsAuthority.Instance is not null )
			{
				if ( VitalsAuthority.Instance.TryApplyDeltas( GameObject, healthDelta, staminaDelta, this, mergePendingSprintDebtForNegativeStamina: mergePendingSprintDebt, clientAuthorityStaminaBeforeSpend: clientAuthorityStaminaBeforeSpend ) )
				{
					LogVitalsNetwork( $"host TryApplyDeltas Δhp={healthDelta:0.####} Δst={staminaDelta:0.####} → HP={CurrentHealth:0.#}/{CurrentHealthMax:0.#} ST={CurrentStamina:0.#}/{CurrentStaminaMax:0.#}" );
					return true;
				}

				Log.Warning( $"{VitalsLogPrefix()} {GameObject.Name}: host TryApplyDeltas rejected (see VitalsAuthority)." );
				return false;
			}

			var hMax = Math.Max( 1f, CurrentHealthMax );
			var sMax = Math.Max( 0f, CurrentStaminaMax );
			ApplyLocalSnapshot( new VitalsSnapshot(
				Math.Clamp( CurrentHealth + healthDelta, 0f, hMax ),
				hMax,
				Math.Clamp( CurrentStamina + staminaDelta, 0f, sMax ),
				sMax ) );
			LogVitalsNetwork( $"host local fallback (no VitalsAuthority) Δhp={healthDelta:0.####} → HP={CurrentHealth:0.#}/{CurrentHealthMax:0.#}" );
			if ( GameObject.Network is { Active: true } net0 && net0.Owner is { } own
			     && !ConnectionIdentity.SameClient( own, Connection.Local ) && CurrentHealth > 0.001f )
				RpcVitalsSync( CurrentHealth, CurrentHealthMax, CurrentStamina, CurrentStaminaMax );
			return true;
		}

		LogVitalsNetwork( $"RpcRequestVitalsDelta → host Δhp={healthDelta:0.####} Δst={staminaDelta:0.####} authoritySt={( clientAuthorityStaminaBeforeSpend is { } ca ? $"{ca:0.####}" : "—" )}" );
		RpcRequestVitalsDelta( healthDelta, staminaDelta, clientAuthorityStaminaBeforeSpend ?? float.NaN );
		return true;
	}

	/// <summary>Called by <see cref="VitalsAuthority"/> on the host after mutating server state.</summary>
	public void ApplyFromAuthorityAndSync( VitalsSnapshot snap )
	{
		// Respawn is handled explicitly after lethal authority deltas — never push 0 HP to the owner first.
		ApplyLocalSnapshot( snap, allowDeathRespawn: false );
		if ( GameObject.Network is { Active: true } net1 && Networking.IsHost && net1.Owner is { } own
		     && !ConnectionIdentity.SameClient( own, Connection.Local ) && CurrentHealth > 0.001f )
			RpcVitalsSync( CurrentHealth, CurrentHealthMax, CurrentStamina, CurrentStaminaMax );
	}

	void ApplyLocalSnapshot( VitalsSnapshot s, bool allowDeathRespawn = true )
	{
		var wasAlive = CurrentHealth > 0.001f;
		var previousStamina = CurrentStamina;
		var previousStaminaMax = CurrentStaminaMax;
		CurrentHealth = s.Health;
		CurrentHealthMax = s.HealthMax;
		CurrentStamina = s.Stamina;
		CurrentStaminaMax = s.StaminaMax;
		LogStaminaFullTransition( previousStamina, previousStaminaMax );
		OnVitalsChanged?.Invoke();

		if ( IsHostOrOffline && ( GameObject.Network is not { Active: true } || Networking.IsHost ) )
			VitalsAuthority.Instance?.EnsureRecordFromVitalsIfMissing( GameObject, this );

		if ( allowDeathRespawn && wasAlive && CurrentHealth <= 0.001f && IsHostOrOffline )
			HostExecuteDeathRespawnIfDead();
	}

	/// <summary>Host-only: teleport + refill pools; replicated to every machine.</summary>
	public void HostExecuteDeathRespawnIfDead()
	{
		if ( !IsHostOrOffline || CurrentHealth > 0.001f )
			return;

		_pendingDeathRespawnHost = false;

		var hasSpawn = TryResolveSpawnTransform( out var spawnPos, out var spawnRot );
		if ( !hasSpawn )
			Log.Warning( $"{VitalsLogPrefix()} {GameObject.Name}: no SpawnPoint or RespawnPointOverride — respawn position unchanged." );

		// Client-owned pawns are simulated by the owner — host proxy transforms do not stick.
		var hostSimulatesTransform = GameObject.Network is not { Active: true } || !GameObject.IsProxy;
		if ( hasSpawn && hostSimulatesTransform )
			ApplyRespawnTransform( spawnPos, spawnRot );

		var auth = VitalsAuthority.Instance;
		VitalsSnapshot snap;
		if ( auth is not null )
		{
			var restored = auth.RegisterAndGetSnapshot( GameObject, MaxHealth, MaxStamina, forceFullPoolsAndResetRegenClocks: true );
			snap = restored ?? new VitalsSnapshot( MaxHealth, MaxHealth, MaxStamina, MaxStamina );
		}
		else
			snap = new VitalsSnapshot( MaxHealth, MaxHealth, MaxStamina, MaxStamina );

		ApplyLocalSnapshot( snap, allowDeathRespawn: false );

		if ( GameObject.Network is { Active: true } net )
		{
			var pos = hasSpawn ? spawnPos : GameObject.WorldPosition;
			var rot = hasSpawn ? spawnRot : GameObject.WorldRotation;
			RpcBroadcastDeathRespawn( pos, rot, snap.Health, snap.HealthMax, snap.Stamina, snap.StaminaMax );

			if ( net.Owner is { } owner && !ConnectionIdentity.SameClient( owner, Connection.Local ) )
				RpcOwnerDeathRespawnTransform( pos, rot );
		}

		_jumpStaminaChargedThisAirborne = false;

		var logPos = hasSpawn ? spawnPos : GameObject.WorldPosition;
		Log.Info( $"{VitalsLogPrefix()} Death → respawn for {GameObject.Name} at {logPos} (HP/ST restored)." );
	}

	void TryRunHostDeathRespawn()
	{
		if ( !_pendingDeathRespawnHost )
			return;

		_pendingDeathRespawnHost = false;
		HostExecuteDeathRespawnIfDead();
	}

	GameObject ResolveRespawnRoot()
	{
		if ( RespawnPointOverride.IsValid() )
			return RespawnPointOverride;

		if ( GameObject.Scene.IsValid() )
		{
			foreach ( var sp in GameObject.Scene.GetAllComponents<SpawnPoint>() )
			{
				if ( sp is null || !sp.GameObject.IsValid() )
					continue;
				return sp.GameObject;
			}
		}

		return null;
	}

	/// <summary>Spawn transform from scene — not from the pawn (host cannot move client-owned proxies).</summary>
	bool TryResolveSpawnTransform( out Vector3 position, out Rotation rotation )
	{
		position = GameObject.WorldPosition;
		rotation = GameObject.WorldRotation;

		var spawnGo = ResolveRespawnRoot();
		if ( spawnGo is null || !spawnGo.IsValid() )
			return false;

		position = spawnGo.WorldPosition;
		rotation = spawnGo.WorldRotation;
		return true;
	}

	void ApplyRespawnTransform( Vector3 worldPosition, Rotation worldRotation )
	{
		if ( !GameObject.IsValid() )
			return;

		GameObject.WorldPosition = worldPosition;
		GameObject.WorldRotation = worldRotation;
		Transform.ClearInterpolation();
		if ( GameObject.Network is { Active: true } )
			Network.ClearInterpolation();

		var rb = GameObject.Components.Get<Rigidbody>();
		if ( rb is not null )
		{
			rb.Velocity = Vector3.Zero;
			rb.AngularVelocity = Vector3.Zero;
		}
	}

	/// <summary>Owner client: authoritative transform snap (position is owner-simulated).</summary>
	[Rpc.Owner]
	public void RpcOwnerDeathRespawnTransform( Vector3 worldPosition, Rotation worldRotation )
	{
		ApplyRespawnTransform( worldPosition, worldRotation );
		LogVitalsNetwork( $"Rpc.Owner death respawn transform @ {worldPosition}" );
	}

	/// <summary>Host → all machines (including owner): teleport + full pools after death.</summary>
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public void RpcBroadcastDeathRespawn( Vector3 worldPosition, Rotation worldRotation, float health, float healthMax, float stamina, float staminaMax )
	{
		ApplyDeathRespawnLocal( worldPosition, worldRotation, health, healthMax, stamina, staminaMax );
	}

	void ApplyDeathRespawnLocal( Vector3 worldPosition, Rotation worldRotation, float health, float healthMax, float stamina, float staminaMax )
	{
		if ( !GameObject.IsValid() )
			return;

		ApplyRespawnTransform( worldPosition, worldRotation );
		ApplyLocalSnapshot( new VitalsSnapshot( health, healthMax, stamina, staminaMax ), allowDeathRespawn: false );
		LogVitalsNetwork( $"death respawn applied @ {worldPosition} HP={health:0.#}/{healthMax:0.#}" );
	}

	[Rpc.Host]
	public void RpcRegisterPlayerDefaults( float maxHealth, float maxStamina )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true } netRpcReg && netRpcReg.Owner is { } owner && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		var auth = VitalsAuthority.Instance;
		var snap = auth?.RegisterAndGetSnapshot( GameObject, maxHealth, maxStamina, false );
		if ( snap is { } s )
			ApplyFromAuthorityAndSync( s );
	}

	[Rpc.Host]
	public void RpcRequestVitalsDelta( float healthDelta, float staminaDelta, float clientAuthorityStaminaBeforeSpend )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		var baselineHint = float.IsNaN( clientAuthorityStaminaBeforeSpend ) ? null : (float?)clientAuthorityStaminaBeforeSpend;

		if ( VitalsAuthority.Instance is not null )
		{
			if ( VitalsAuthority.Instance.TryApplyDeltas( GameObject, healthDelta, staminaDelta, this, mergePendingSprintDebtForNegativeStamina: false, clientAuthorityStaminaBeforeSpend: baselineHint ) )
				LogVitalsNetwork( $"Rpc.Host applied Δhp={healthDelta:0.####} Δst={staminaDelta:0.####} → HP={CurrentHealth:0.#}/{CurrentHealthMax:0.#} ST={CurrentStamina:0.#}/{CurrentStaminaMax:0.#}" );
			else
				Log.Warning( $"{VitalsLogPrefix()} {GameObject.Name}: Rpc.Host TryApplyDeltas rejected." );
			return;
		}

		var hMaxRpc = Math.Max( 1f, CurrentHealthMax );
		var sMaxRpc = Math.Max( 0f, CurrentStaminaMax );
		ApplyLocalSnapshot( new VitalsSnapshot(
			Math.Clamp( CurrentHealth + healthDelta, 0f, hMaxRpc ),
			hMaxRpc,
			Math.Clamp( CurrentStamina + staminaDelta, 0f, sMaxRpc ),
			sMaxRpc ) );
		LogVitalsNetwork( $"Rpc.Host local fallback (no VitalsAuthority) Δhp={healthDelta:0.####} → HP={CurrentHealth:0.#}/{CurrentHealthMax:0.#}" );
		if ( GameObject.Network is { Active: true } n && n.Owner is { } own
		     && !ConnectionIdentity.SameClient( own, Connection.Local ) && CurrentHealth > 0.001f )
			RpcVitalsSync( CurrentHealth, CurrentHealthMax, CurrentStamina, CurrentStaminaMax );
	}

	[Rpc.Owner]
	public void RpcVitalsSync( float health, float healthMax, float stamina, float staminaMax )
	{
		LogVitalsNetwork( $"Rpc.Owner sync HP={health:0.#}/{healthMax:0.#} ST={stamina:0.#}/{staminaMax:0.#}" );
		ApplyLocalSnapshot( new VitalsSnapshot( health, healthMax, stamina, staminaMax ), allowDeathRespawn: false );
	}

	void LogVitalsNetwork( string message )
	{
		if ( !LogVitalsNetworking )
			return;

		var net = GameObject.Network is { Active: true } na ? $"net owner={ConnectionIdentity.Format( na.Owner )}" : "offline";
		Log.Info( $"{VitalsLogPrefix()} {GameObject.Name} ({net}): {message}" );
	}

	void LogStaminaFullTransition( float previousStamina, float previousStaminaMax )
	{
		if ( !LogWhenStaminaReachesFull )
			return;

		var previousMax = Math.Max( 0f, previousStaminaMax );
		var currentMax = Math.Max( 0f, CurrentStaminaMax );
		if ( previousMax <= 1e-4f || currentMax <= 1e-4f )
			return;

		if ( previousStamina >= previousMax - 1e-4f )
			return;

		if ( CurrentStamina < currentMax - 1e-4f )
			return;

		_staminaFullLogCount++;
		Log.Info( $"[PlayerVitals|{GetVitalsProcessRoleTag( GameObject )}|StaminaFull] {GameObject.Name} fill#{_staminaFullLogCount} time={Time.NowDouble:0.###}s ST={CurrentStamina:0.###}/{CurrentStaminaMax:0.###} prev={previousStamina:0.###}/{previousStaminaMax:0.###}" );
	}
}
