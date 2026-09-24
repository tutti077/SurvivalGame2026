using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// World-space "Ping: &lt;name&gt;" sign dropped at a map ping (own or a crew mate's), so the spot
/// can be found from the ground and not only on the map. A transient local object: a
/// <see cref="Sandbox.WorldPanel"/> that draws over geometry, destroyed after
/// <see cref="MapPingFeed.LifetimeSeconds"/>. Nothing is networked — every machine that shows the
/// map ping builds its own sign from the same broadcast. Faces the camera the same way the enemy
/// health bar does (camera rotation + 180° yaw — a WorldPanel's drawn side is its back, so
/// <c>LookAtCamera</c> alone shows the culled face).
/// </summary>
public sealed class MapPingBillboardPanel : PanelComponent
{
	/// <summary>
	/// Same recipe as the enemy health bar (640×128 panel, 36 px text, root sized by PanelBounds
	/// only). Wider so a 32-character name still fits; the root is transparent, only the pill shows,
	/// and the pill sizes itself to the name.
	/// </summary>
	public static readonly Vector2 PanelPixelSize = new( 1280f, 128f );
	/// <summary>Health-bar scale is 2.5; a touch bigger so the sign reads from across a clearing without dwarfing the pawn.</summary>
	public const float WorldScale = 3f;
	/// <summary>Plate centre this far above the ground hit (≈2.75 m) so its bottom edge clears the terrain — the overlay is still depth-tested.</summary>
	public const float RaiseUnits = 110f;
	/// <summary>Same trick as <see cref="EnemyHealthBar"/>: the panel's front is its -forward.</summary>
	const float PanelFaceYawCorrection = 180f;

	public string Text { get; set; } = "Ping";

	double _diesAt;
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

		if ( _diesAt <= 0 )
			_diesAt = MapPingFeed.Now + MapPingFeed.LifetimeSeconds;

		if ( MapPingFeed.Now >= _diesAt && GameObject.IsValid() )
		{
			GameObject.Destroy();
			return;
		}

		FaceCamera();
		if ( _label is not null && _label.IsValid() && _label.Text != Text )
			_label.Text = Text;
	}

	/// <summary>Screen-aligned billboard: copy the view camera's rotation, flipped so the drawn side faces it.</summary>
	void FaceCamera()
	{
		var cam = Scene?.Camera;
		if ( cam is null || !cam.IsValid() )
			return;

		GameObject.WorldRotation = cam.WorldRotation * Rotation.FromYaw( PanelFaceYawCorrection );
	}

	void EnsureBuilt()
	{
		if ( _built || Panel is null )
			return;

		_built = true;
		// Never pixel-size the root: the WorldPanel lays it out from PanelBounds, and explicit px
		// sizes made it larger than the render area (top-left clipped box).
		if ( Panel is RootPanel root )
			root.PanelBounds = new Rect( 0, 0, PanelPixelSize.x, PanelPixelSize.y );
		Panel.Style.Width = Length.Percent( 100 );
		Panel.Style.Height = Length.Percent( 100 );
		Panel.Style.Set( "flex-direction", "row" );
		Panel.Style.Set( "align-items", "center" );
		Panel.Style.Set( "justify-content", "center" );
		Panel.Style.Set( "overflow", "visible" );
		Panel.Style.Set( "pointer-events", "none" );

		// The pill hugs its label, so a long name simply makes a wider box.
		var pill = new Panel { Parent = Panel };
		pill.Style.Set( "flex-shrink", "0" );
		pill.Style.PaddingLeft = Length.Pixels( 24f );
		pill.Style.PaddingRight = Length.Pixels( 24f );
		pill.Style.PaddingTop = Length.Pixels( 6f );
		pill.Style.PaddingBottom = Length.Pixels( 6f );
		pill.Style.BackgroundColor = new Color( 0.05f, 0.05f, 0.07f, 0.85f );
		pill.Style.Set( "border-radius", "14px" );
		pill.Style.Set( "border-width", "2px" );
		pill.Style.Set( "border-color", "#ffe14d" );
		pill.Style.Set( "align-items", "center" );
		pill.Style.Set( "justify-content", "center" );

		_label = new Label { Parent = pill, Text = Text };
		_label.Style.FontColor = new Color( 1f, 0.92f, 0.35f );
		_label.Style.FontSize = Length.Pixels( 36f );
		_label.Style.Set( "font-weight", "bold" );
		_label.Style.Set( "white-space", "nowrap" );
		// Crisp 1px outline like the health bar — blurred shadows look muddy on world panels.
		_label.Style.Set( "text-shadow", "1px 1px 0px #000, -1px -1px 0px #000, 1px -1px 0px #000, -1px 1px 0px #000" );
	}
}

public static class MapPingBillboard
{
	/// <summary>Drop a sign at <paramref name="worldMeters"/> (x/y from world center), on the ground under that spot.</summary>
	public static void Spawn( Scene scene, Vector2 worldMeters, string senderName )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		var flat = new Vector3( TerrainWorldUnits.MetersToEngine( worldMeters.x ), TerrainWorldUnits.MetersToEngine( worldMeters.y ), 0f );
		var position = FindGround( scene, flat ) + Vector3.Up * MapPingBillboardPanel.RaiseUnits;

		var go = new GameObject( true, "MapPingBillboard" );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.WorldPosition = position;
		go.WorldScale = Vector3.One * MapPingBillboardPanel.WorldScale;

		var worldPanel = go.Components.Create<Sandbox.WorldPanel>();
		worldPanel.PanelSize = MapPingBillboardPanel.PanelPixelSize;
		worldPanel.LookAtCamera = false;
		worldPanel.RenderOptions.Game = false;
		worldPanel.RenderOptions.Overlay = true;

		var who = string.IsNullOrWhiteSpace( senderName ) ? "You" : senderName.Trim();
		var panel = go.Components.Create<MapPingBillboardPanel>( startEnabled: false );
		panel.Text = $"Ping: {who}";
		panel.Enabled = true;

		Log.Info( $"[MapPing] sign for {who} at {position} (ping {worldMeters.x:0}, {worldMeters.y:0} m)" );
	}

	/// <summary>Ground under the ping: a downward trace from well above the terrain; the flat point at z=0 when nothing is hit.</summary>
	static Vector3 FindGround( Scene scene, Vector3 flat )
	{
		var camera = scene.Camera;
		var top = camera is { IsValid: true } ? MathF.Max( camera.WorldPosition.z, 0f ) + 4096f : 8192f;
		var from = flat.WithZ( top );
		var to = flat.WithZ( top - 16384f );

		var tr = scene.Trace.Ray( from, to )
			.UsePhysicsWorld()
			.WithoutTags( "player", "enemy", "buildpreview" )
			.Run();

		if ( !tr.Hit )
		{
			tr = scene.Trace.Ray( from, to )
				.WithoutTags( "player", "enemy", "buildpreview" )
				.Run();
		}

		return tr.Hit ? tr.HitPosition : flat;
	}
}
