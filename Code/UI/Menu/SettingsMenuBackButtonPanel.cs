using Sandbox.UI;

namespace Survival;

/// <summary>Settings sub-page back control — invoked via soft-cursor Attack1, not OS mouse.</summary>
public sealed class SettingsMenuBackButtonPanel : Panel
{
	public GameSettingsMenuSection Section { get; init; }

	public override bool WantsMouseInput() => false;
}
