using System;
using Sandbox;

namespace Survival;

/// <summary>The host's view of one pawn's swing in flight (<see cref="PlayerCombat.TryGetHostSwing"/>).</summary>
public readonly struct HostSwingView
{
	public ushort Sequence { get; init; }
	public MeleeSwingPhase Phase { get; init; }
	/// <summary><see cref="MeleeAttackTypes"/> of the swing.</summary>
	public byte AttackType { get; init; }
	public bool IsHeavy { get; init; }
	/// <summary>Heavy, still held: the strike waits for the release (never by itself; an empty stamina bar lets go).</summary>
	public bool IsHeavyHolding { get; init; }
	public double StartedAt { get; init; }
	/// <summary>Windup from the swing start to its release as scheduled (a held heavy grows it; a scav's draw uses the start value).</summary>
	public float WindupSeconds { get; init; }
	/// <summary>Host time the release starts; NaN while a heavy is held or once the release is over.</summary>
	public double ReleaseStartAt { get; init; }
	/// <summary>Host time the recovery ends; NaN until the release has ended.</summary>
	public double RecoveryEndAt { get; init; }
	public int ComboStep { get; init; }
}

/// <summary>
/// Host hooks for a fighter that is not driven by a player's input — a scav (<see cref="EntityCombat"/>) presses attack
/// and guard through the same host intake a remote player's click and guard report use
/// (<see cref="HostSubmitPlayerSwing"/>, <see cref="HostAcceptBlockState"/>), and reads its own and its opponent's swing here.
/// </summary>
public partial class PlayerCombat
{
	/// <summary>Host: the swing in flight on this pawn; false between swings.</summary>
	internal bool TryGetHostSwing( out HostSwingView view )
	{
		if ( _serverMeleeAttack is not { } runtime )
		{
			view = default;
			return false;
		}

		view = new HostSwingView
		{
			Sequence = runtime.IntentSequence,
			Phase = runtime.Phase,
			AttackType = runtime.AttackType,
			IsHeavy = runtime.IsHeavy,
			IsHeavyHolding = runtime.IsHeavyHolding,
			StartedAt = runtime.StartedAtSandbox,
			WindupSeconds = runtime.WindupRemainingSeconds,
			ReleaseStartAt = runtime.ReleaseStartSandbox ?? double.NaN,
			RecoveryEndAt = runtime.RecoveryEndSandbox,
			ComboStep = runtime.ComboStep
		};
		return true;
	}

	/// <summary>A scav's vitals (its stamina bar stands in for <see cref="PlayerVitals"/>); null on a player pawn.</summary>
	EntityVitals HostEntityVitals
	{
		get
		{
			if ( !_entityVitalsSearched )
			{
				_entityVitalsSearched = true;
				_entityVitals = Components.Get<EntityVitals>();
			}
			return _entityVitals;
		}
	}

	EntityVitals _entityVitals;
	bool _entityVitalsSearched;

	/// <summary>The combat stamina regen delay (after an attack / after a block), from <see cref="VitalsAuthority"/>.</summary>
	static float CombatRegenDelaySeconds( bool afterBlock ) =>
		VitalsAuthority.Instance is { } a
			? (afterBlock ? a.StaminaRegenDelayAfterBlockSeconds : a.StaminaRegenDelayAfterAttackSeconds)
			: (afterBlock ? 0.3f : 0.6f);

	/// <summary>Host: a click is stored to fire later (a chain waiting for the recovery end, or a buffered press).</summary>
	internal bool HostHasPendingSwing => _hostHasPending;

	/// <summary>Host: blocked a light a moment ago — the next swing winds up in the counter windup.</summary>
	internal bool HostCounterWindowOpen => Time.NowDouble < _counterWindowUntilSandbox;

	/// <summary>Host: guard up / down for a fighter without input (the same intake a remote owner's guard report takes;
	/// up during the early windup of its own swing is a feint).</summary>
	internal void HostSubmitGuard( bool active, byte direction, float basisYaw ) =>
		HostAcceptBlockState( active, direction, basisYaw );
}
