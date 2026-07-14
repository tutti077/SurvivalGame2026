using Sandbox.UI;

namespace Survival;

/// <summary>One skill node — clicks come from soft-cursor Attack1, not OS mouse.</summary>
public sealed class SkillNodePanel : Panel
{
	public string SkillId { get; }
	public SkillsMenuSection Section { get; }

	public SkillNodePanel( string skillId, SkillsMenuSection section )
	{
		SkillId = skillId;
		Section = section;
	}

	public override bool WantsMouseInput() => false;
}
