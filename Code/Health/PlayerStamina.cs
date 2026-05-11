using Sandbox;

namespace Game;

/// <summary>
/// Compatibility wrapper. New logic lives in <see cref="EntityStaminaFeature"/>.
/// </summary>
[Title( "Player Stamina (Legacy Alias)" )]
[Category( "Health" )]
public sealed partial class PlayerStamina : EntityStaminaFeature { }

public interface IGrappleStop
{
	bool IsGrappling { get; }

	void StopGrapple();

	bool GrappleSwingStaminaDrainActive { get; }
}
