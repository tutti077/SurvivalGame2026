using Sandbox.UI;

namespace Survival;

/// <summary>Quest list row — selection is via soft-cursor Attack1, not OS mouse.</summary>
public sealed class QuestRowPanel : Panel
{
	public QuestMenuSection Section { get; init; }
	public string QuestId { get; init; }

	public override bool WantsMouseInput() => false;
}
