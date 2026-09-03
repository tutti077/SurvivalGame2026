using Sandbox.UI;

namespace Survival;

/// <summary>Quest list row — selection is via soft-cursor Attack1, not OS mouse.</summary>
public sealed class QuestRowPanel : Panel
{
	public QuestMenuSection Section { get; init; }
	public string QuestId { get; init; }

	/// <summary>Title label (dimmed while locked).</summary>
	public Label NameLabel { get; set; }

	/// <summary>Right-aligned Locked / Active / Done tag.</summary>
	public Label StatusLabel { get; set; }

	public override bool WantsMouseInput() => false;
}
