using Sandbox.UI;

namespace Survival;

public sealed class QuestRowPanel : Panel
{
	public QuestMenuSection Section { get; init; }
	public string QuestId { get; init; }

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || Section is null )
			return;

		Section.SelectQuest( QuestId );
	}
}
