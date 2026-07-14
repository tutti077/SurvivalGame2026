using Sandbox.UI;

namespace Survival;

/// <summary>Settings root action row — invoked via soft-cursor Attack1, not OS mouse.</summary>
public sealed class SettingsMenuButtonPanel : Panel
{
	public GameSettingsMenuSection Section { get; init; }
	public string ActionId { get; init; }

	public override bool WantsMouseInput() => false;
}
