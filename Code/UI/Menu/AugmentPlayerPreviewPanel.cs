using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Little standing citizen in the middle of the augment station: its own render scene with a
/// camera, a sun and a dressed body (same pattern as the engine menu's avatar card). Presentation
/// only — nothing here touches the pawn.
/// </summary>
public sealed class AugmentPlayerPreviewPanel : ScenePanel
{
	const string HumanModelPath = "models/citizen_human/citizen_human_male.vmdl";

	const float DefaultYaw = -12f;

	SkinnedModelRenderer _body;
	GameObject _bodyObject;
	float _yaw = DefaultYaw;
	string _appliedClothingKey;
	bool _built;

	public AugmentPlayerPreviewPanel()
	{
		Style.Set( "pointer-events", "none" );
	}

	public override bool WantsMouseInput() => false;

	protected override void OnAfterTreeRender( bool firstTime )
	{
		base.OnAfterTreeRender( firstTime );
		if ( _built )
			return;

		_built = true;
		BuildScene();
	}

	void BuildScene()
	{
		var scene = RenderScene;
		if ( scene is null )
			return;

		using ( scene.Push() )
		{
			var cameraObject = new GameObject( true, "Camera" );
			var camera = cameraObject.AddComponent<CameraComponent>();
			camera.BackgroundColor = Color.Transparent;
			// Portrait frame: a narrow field and a close camera fill the box with the body.
			camera.FieldOfView = 26;
			camera.ZNear = 1;
			camera.ZFar = 512;
			cameraObject.WorldPosition = new Vector3( 105, 0, 37 );
			cameraObject.WorldRotation = Rotation.FromYaw( 180 );

			var sunObject = new GameObject( true, "Sun" );
			sunObject.WorldRotation = Rotation.From( 50, 150, 0 );
			var sun = sunObject.AddComponent<DirectionalLight>();
			sun.LightColor = new Color( 1.0f, 0.96f, 0.9f ) * 1.6f;
			sun.SkyColor = new Color( 0.35f, 0.38f, 0.45f );

			var bodyObject = new GameObject( true, "Body" );
			bodyObject.WorldRotation = Rotation.FromYaw( _yaw );
			_bodyObject = bodyObject;
			_body = bodyObject.AddComponent<SkinnedModelRenderer>();
			_body.Model = Model.Load( HumanModelPath );
		}

		if ( !string.IsNullOrWhiteSpace( _appliedClothingKey ) )
			Dress( _appliedClothingKey );
	}

	/// <summary>Spin the body about its feet (click-drag on the frame). Degrees, positive = clockwise from above.</summary>
	public void RotateBy( float degrees )
	{
		if ( MathF.Abs( degrees ) < 1e-4f )
			return;

		_yaw = (_yaw + degrees) % 360f;
		if ( _bodyObject is not null && _bodyObject.IsValid() )
			_bodyObject.WorldRotation = Rotation.FromYaw( _yaw );
	}

	/// <summary>Mirror the pawn's worn outfit (<see cref="PlayerEquipment.NetworkedWornClothing"/> key). Re-dresses only on change.</summary>
	public void SetClothing( string key )
	{
		key ??= string.Empty;
		if ( string.Equals( key, _appliedClothingKey, StringComparison.Ordinal ) )
			return;

		_appliedClothingKey = key;
		if ( _body is not null && _body.IsValid() )
			Dress( key );
	}

	void Dress( string key )
	{
		var scene = RenderScene;
		if ( scene is null || _body is null || !_body.IsValid() )
			return;

		var container = new ClothingContainer();
		foreach ( var path in key.Split( '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			if ( ResourceLibrary.TryGet<Clothing>( path, out var clothing ) && clothing is not null )
				container.Add( clothing );
		}

		using ( scene.Push() )
		{
			container.Apply( _body );
		}
	}
}
