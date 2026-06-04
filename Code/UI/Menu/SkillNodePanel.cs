using Sandbox.UI;

namespace Survival;

/// <summary>One clickable node in the skills web.</summary>
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

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" )
			return;

		Section?.SelectSkill( SkillId );
	}
}
