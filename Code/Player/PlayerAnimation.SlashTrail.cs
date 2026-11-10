using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The held weapon as placed this frame, for presentation that rides it: the heavy-ready glint. The white slash
/// ribbon does not follow the visible blade (its wrist roll and entry keys made a messy fan) - it traces the damage
/// arc, <see cref="PlayerCombat.FeedSwingVfxFromDamageArc"/> (Mark 2026-11-05).
/// </summary>
public sealed partial class PlayerAnimation
{
	/// <summary>After the sword transform is placed: the heavy-ready glint rides the held weapon (sword or club) while it plays.</summary>
	void TickHeavyFlashFromHeldBlade()
	{
		if ( ResolveCombat() is { WantsHeavyFlashBlade: true } flashCombat && TryGetHeldBladeWorld( out var flashHeel, out var flashTip ) )
			flashCombat.FeedHeavyFlashBlade( flashHeel, flashTip );
	}

	/// <summary>Grip-side point and tip of the held sword (world) as placed this frame. False without a sword in hand.</summary>
	public bool TryGetHeldBladeWorld( out Vector3 heel, out Vector3 tip )
	{
		heel = default;
		tip = default;
		if ( _meleeDemoStick is null || !_meleeDemoStick.IsValid() || !_meleeDemoStick.Enabled )
			return false;

		var rot = _meleeDemoStick.WorldRotation;
		var pos = _meleeDemoStick.WorldPosition;

		if ( _demoStickIsHeldModel )
		{
			// Real weapon model: the fit knows the butt, the head direction and the length in model space.
			var butt = pos + rot * _demoStickFit.ButtPoint;
			var head = rot * _demoStickFit.HeadDir;
			// Ribbon inner edge just above the grip, not at the pommel.
			heel = butt + head * (_demoStickFit.Length * 0.3f);
			tip = butt + head * _demoStickFit.Length;
			return true;
		}

		// Box stick: local +X is the long axis, mesh half extent x scale is half the blade.
		var halfBlade = _demoStickMeshHalfExtentX * MathF.Max( 0.01f, _meleeDemoStick.LocalScale.x );
		heel = pos - rot.Forward * (halfBlade * 0.4f);
		tip = pos + rot.Forward * halfBlade;
		return true;
	}
}
