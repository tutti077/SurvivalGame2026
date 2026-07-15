using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>Minimap +/− control — soft-cursor Attack1 and OS mouse when available.</summary>
public sealed class TerrainMinimapZoomButton : Panel
{
	public bool ZoomIn { get; init; }
	public Action OnActivated { get; init; }

	public TerrainMinimapZoomButton()
	{
		ButtonInput = PanelInputType.UI;
	}

	public override bool WantsMouseInput() => true;

	protected override void OnMouseDown( MousePanelEvent e )
	{
		base.OnMouseDown( e );
		if ( e.Button is "mouseleft" or "mouse1" or "Attack1" )
			Activate();
	}

	public void Activate() => OnActivated?.Invoke();
}
