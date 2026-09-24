using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Static world-space text plate (the "ENTRANCE" sign over the dungeon doorway). Same recipe as
/// <see cref="MapPingBillboardPanel"/> — root sized by PanelBounds only, one pill hugging the label —
/// but it never turns to face the camera and never expires. <see cref="Spawn"/> builds two plates
/// back to back so the text reads from either side of the wall.
/// </summary>
public sealed class DungeonSignPanel : PanelComponent
{
	public static readonly Vector2 PanelPixelSize = new( 1280f, 256f );

	public string Text { get; set; } = "ENTRANCE";

	bool _built;
	Label _label;

	protected override void OnTreeFirstBuilt()
	{
		base.OnTreeFirstBuilt();
		EnsureBuilt();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		EnsureBuilt();

		if ( _label is not null && _label.IsValid() && _label.Text != Text )
			_label.Text = Text;
	}

	void EnsureBuilt()
	{
		if ( _built || Panel is null )
			return;

		_built = true;
		if ( Panel is RootPanel root )
			root.PanelBounds = new Rect( 0, 0, PanelPixelSize.x, PanelPixelSize.y );
		Panel.Style.Width = Length.Percent( 100 );
		Panel.Style.Height = Length.Percent( 100 );
		Panel.Style.Set( "flex-direction", "row" );
		Panel.Style.Set( "align-items", "center" );
		Panel.Style.Set( "justify-content", "center" );
		Panel.Style.Set( "overflow", "visible" );
		Panel.Style.Set( "pointer-events", "none" );

		var plate = new Panel { Parent = Panel };
		plate.Style.Set( "flex-shrink", "0" );
		plate.Style.PaddingLeft = Length.Pixels( 48f );
		plate.Style.PaddingRight = Length.Pixels( 48f );
		plate.Style.PaddingTop = Length.Pixels( 12f );
		plate.Style.PaddingBottom = Length.Pixels( 12f );
		plate.Style.BackgroundColor = new Color( 0.08f, 0.06f, 0.04f, 0.95f );
		plate.Style.Set( "border-radius", "12px" );
		plate.Style.Set( "border-width", "6px" );
		plate.Style.Set( "border-color", "#ffe14d" );
		plate.Style.Set( "align-items", "center" );
		plate.Style.Set( "justify-content", "center" );

		_label = new Label { Parent = plate, Text = Text };
		_label.Style.FontColor = new Color( 1f, 0.92f, 0.35f );
		_label.Style.FontSize = Length.Pixels( 140f );
		_label.Style.Set( "font-weight", "bold" );
		_label.Style.Set( "letter-spacing", "12px" );
		_label.Style.Set( "white-space", "nowrap" );
		_label.Style.Set( "text-shadow", "2px 2px 0px #000, -2px -2px 0px #000, 2px -2px 0px #000, -2px 2px 0px #000" );
	}

	/// <summary>
	/// Two plates back to back at <paramref name="worldPosition"/>, one facing <paramref name="facing"/> and one
	/// the other way, so whichever side the WorldPanel draws on, the sign is readable from outside and inside.
	/// </summary>
	public static GameObject Spawn( GameObject parent, string name, Vector3 worldPosition, Vector3 facing, string text, float worldScale )
	{
		var holder = new GameObject( true, name );
		holder.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		holder.Parent = parent;
		holder.WorldPosition = worldPosition;
		holder.WorldRotation = Rotation.LookAt( facing.Normal, Vector3.Up );
		holder.WorldScale = Vector3.One;

		CreatePlate( holder, Rotation.Identity, text, worldScale );
		CreatePlate( holder, Rotation.FromYaw( 180f ), text, worldScale );
		return holder;
	}

	static void CreatePlate( GameObject holder, Rotation localRotation, string text, float worldScale )
	{
		var go = new GameObject( true, "plate" );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = holder;
		go.LocalPosition = Vector3.Zero;
		go.LocalRotation = localRotation;
		go.LocalScale = Vector3.One * worldScale;

		var worldPanel = go.Components.Create<Sandbox.WorldPanel>();
		worldPanel.PanelSize = PanelPixelSize;
		worldPanel.LookAtCamera = false;
		worldPanel.RenderOptions.Game = false;
		worldPanel.RenderOptions.Overlay = true;

		var panel = go.Components.Create<DungeonSignPanel>( startEnabled: false );
		panel.Text = text;
		panel.Enabled = true;
	}
}
