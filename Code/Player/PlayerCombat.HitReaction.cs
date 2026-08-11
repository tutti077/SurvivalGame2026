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

	/// <summary>Called by <see cref="PlayerAnimation"/> when a reaction starts: the hit beats whatever combat was doing.</summary>
	internal void OnHitReactionBegan()
	{
		ClearCombatRecoveryPresentation( pushOwnerClear: false );

		if ( !_primarySwingPhaseActive )
			return;

		_primarySwingPhaseActive = false;
		_primaryPostReleaseDragAccum = default;
	}
}
