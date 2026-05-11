using Sandbox;

namespace Game;

[Title( "Entity Nameplate Feature" )]
[Category( "Entity" )]
public sealed class EntityNameplateFeature : Component
{
	[Property] public Vector3 NameLocalOffset { get; set; } = new Vector3( 0f, 0f, 18f );
	[Property] public float TextScale { get; set; } = 1f;
	[Property] public bool FaceCameraYawOnly { get; set; } = true;
	[Property] public bool MirrorFacing { get; set; } = true;

	private GameObject _nameGo;
	private TextRenderer _text;

	protected override void OnStart()
	{
		EnsureNameplate();
	}

	protected override void OnUpdate()
	{
		if ( _nameGo is null || !_nameGo.IsValid() )
			EnsureNameplate();

		var cam = Scene.Camera;
		if ( cam is null || _nameGo is null || !_nameGo.IsValid() )
			return;

		var toCamera = cam.WorldPosition - _nameGo.WorldPosition;
		if ( FaceCameraYawOnly )
			toCamera = toCamera.WithZ( 0f );
		if ( toCamera.IsNearlyZero( 0.0001f ) )
			return;

		_nameGo.WorldScale = Vector3.One;
		var rot = Rotation.LookAt( toCamera.Normal, Vector3.Up );
		if ( MirrorFacing )
			rot *= Rotation.FromYaw( 180f );
		_nameGo.WorldRotation = rot;
	}

	private void EnsureNameplate()
	{
		var core = EntityCore.EnsureOn( GameObject, EntityKind.Enemy );
		var textValue = core?.DisplayName ?? GameObject.Name;
		var anchor = core?.EnsureOverheadAnchor() ?? GameObject;

		if ( _nameGo is null || !_nameGo.IsValid() )
		{
			_nameGo = new GameObject( true, "EntityNameplate" );
			_nameGo.Parent = anchor;
			_nameGo.LocalPosition = NameLocalOffset;
			_nameGo.LocalRotation = Rotation.Identity;
		}
		_nameGo.WorldScale = Vector3.One;

		if ( _text is null || !_text.IsValid() )
		{
			_text = _nameGo.Components.Create<TextRenderer>();
			_text.Text = textValue;
			_text.HorizontalAlignment = TextRenderer.HAlignment.Center;
			_text.VerticalAlignment = TextRenderer.VAlignment.Center;
			_text.Scale = TextScale;
		}
		else
		{
			_text.Text = textValue;
			_text.Scale = TextScale;
		}
	}
}
