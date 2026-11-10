using Sandbox;

namespace Survival;

/// <summary>
/// Combat's view of the victim "I was just hit" state. The window, the broadcast, and the pose all
/// live on <see cref="PlayerAnimation"/> (see PlayerAnimation.HitReaction.cs), which is not
/// equipment-dependent. Combat only needs to know "is it running" (to lock actions) and to drop its
/// own in-flight state.
/// </summary>
public partial class PlayerCombat
{
	/// <summary>Locks attack / block / shove for the reaction window. See <see cref="IsCombatActionLocked"/>.</summary>
	public bool IsHitReactionActive =>
		Components.Get<PlayerAnimation>() is { IsHitReactionActive: true };

	/// <summary>
	/// Host entry for combat's own self-inflicted reactions (blocked heavy, shove-vs-block). External
	/// victims are resolved as <see cref="PlayerAnimation"/> instead — a weaponless victim reacts the
	/// same way and may not carry combat state at all (enemies).
	/// </summary>
	internal void ServerBeginHitReaction( float durationSeconds ) =>
		Components.Get<PlayerAnimation>()?.ServerBeginHitReaction( durationSeconds );

	/// <summary>
	/// Called by <see cref="PlayerAnimation"/> when a reaction starts. A hit never breaks an attack (Mark 2026-11-05):
	/// a swing in its windup, held as a heavy or striking plays on, and a queued click still fires once the reaction
	/// lock ends — only a leftover recovery pose gives way to the flail.
	/// </summary>
	internal void OnHitReactionBegan() => ClearCombatRecoveryPresentation( pushOwnerClear: false );
}
