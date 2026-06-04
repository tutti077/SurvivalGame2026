using Sandbox.UI;

namespace Survival;

/// <summary>
/// Hotbar row container. Does not participate in UI mouse mode — only child slots do.
/// </summary>
public sealed class HotbarHostPanel : Panel
{
	public override bool WantsMouseInput() => false;
}
