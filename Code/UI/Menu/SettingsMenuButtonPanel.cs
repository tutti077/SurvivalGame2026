using Sandbox.UI;

namespace Survival;

public sealed class SettingsMenuButtonPanel : Panel
{
	public GameSettingsMenuSection Section { get; init; }
	public string ActionId { get; init; }

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button != "mouseleft" || Section is null )
			return;

		Section.InvokeAction( ActionId );
	}
}
