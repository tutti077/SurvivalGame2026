using Sandbox.UI;

namespace Survival;

public sealed class SettingsMenuBackButtonPanel : Panel
{
	public GameSettingsMenuSection Section { get; init; }

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || Section is null )
			return;

		Section.NavigateToRoot();
	}
}
