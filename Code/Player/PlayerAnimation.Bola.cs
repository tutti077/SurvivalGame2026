using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The bola in the hand: at rest three stones hang limp from the right hand on their cords; while the
/// owner winds up (<see cref="PlayerCombat.IsBolaCharging"/>) the whole thing swings out flat and spins
/// round the hand, faster the longer the hold. Local presentation on the right hand bone like the torch
/// (<see cref="NetworkMode.Never"/>), built from the profile's <c>heldModel</c> whose origin is the knot.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string BolaPropObjectName = "held_bola_prop";

	[Property, Group( "Bola" ), Title( "Spin at the start of the wind-up (°/s)" ), Range( 60f, 720f ), Step( 10f )]
	public float BolaSpinStartDegreesPerSecond { get; set; } = 180f;

	[Property, Group( "Bola" ), Title( "Spin at full charge (°/s)" ), Range( 360f, 2400f ), Step( 10f )]
	public float BolaSpinFullDegreesPerSecond { get; set; } = 1260f;

	[Property, Group( "Bola" ), Title( "Swing-out time (s)" ), Range( 0.05f, 1f ), Step( 0.05f )]
	public float BolaSwingOutSeconds { get; set; } = 0.35f;

	GameObject _bolaProp;
	string _bolaPropModelPath;
	float _bolaSpinAngle;
	float _bolaSwingOut01;

	EquipmentProfileData ResolveBolaProfile()
	{
		var id = ResolvePresentationMainHandId();
		if ( string.IsNullOrWhiteSpace( id ) || !EquipmentCatalog.TryGet( id, out var profile ) )
			return null;

		return EquipmentCatalog.HasAction( id, EquippedItemActions.Bola ) && !string.IsNullOrWhiteSpace( profile.HeldModel ) ? profile : null;
	}

	/// <summary>OnPreRender, after the torch: place and spin the bola on the right hand.</summary>
	void TickBolaProp()
	{
		var profile = ResolveBolaProfile();
		if ( profile is null || _hitReactionPoseActive || _ledgeMantlePoseActive )
		{
			DestroyBolaProp();
			return;
		}

		EnsureBolaProp( profile.HeldModel.Trim() );
		if ( _bolaProp is null || !_bolaProp.IsValid() )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid()
		     || !TryGetFirstBoneTransform( body, DemoStickRightBoneCandidates, out var handTx ) )
			return;

		var combat = ResolveCombat();
		var charging = combat is { IsValid: true } && combat.IsBolaCharging && IsLocalOwnerForPresentation();
		var charge01 = charging ? combat.BolaCharge01 : 0f;

		var step = Time.Delta / MathF.Max( 0.02f, BolaSwingOutSeconds );
		_bolaSwingOut01 = charging ? MathF.Min( 1f, _bolaSwingOut01 + step ) : MathF.Max( 0f, _bolaSwingOut01 - step * 1.5f );

		if ( charging || _bolaSwingOut01 > 0.001f )
		{
			var spin = MathX.Lerp( BolaSpinStartDegreesPerSecond, BolaSpinFullDegreesPerSecond, charge01 ) * _bolaSwingOut01;
			_bolaSpinAngle = (_bolaSpinAngle + spin * Time.Delta) % 360f;
		}

		// Rest: knot in the hand, cords hanging straight down (model -Z). Wind-up: the cords swing out level
		// and the whole set turns round the hand's vertical axis.
		var hang = Rotation.Identity;
		var swing = Rotation.FromYaw( _bolaSpinAngle ) * Rotation.FromPitch( -90f + 20f );
		var t = _bolaSwingOut01 * _bolaSwingOut01 * (3f - 2f * _bolaSwingOut01);
		_bolaProp.WorldRotation = Rotation.Slerp( hang, swing, t );
		_bolaProp.WorldPosition = handTx.Position;
		_bolaProp.LocalScale = Vector3.One;
	}

	bool IsLocalOwnerForPresentation()
	{
		var networked = GameObject.Network is { Active: true };
		return !networked || !GameObject.IsProxy;
	}

	void EnsureBolaProp( string modelPath )
	{
		if ( _bolaProp is { IsValid: true } && string.Equals( _bolaPropModelPath, modelPath, StringComparison.OrdinalIgnoreCase ) )
			return;

		DestroyBolaProp();
		var model = Model.Load( modelPath );
		if ( model is null || model.IsError )
			return;

		_bolaPropModelPath = modelPath;
		_bolaProp = new GameObject( true, BolaPropObjectName );
		_bolaProp.NetworkMode = NetworkMode.Never;
		_bolaProp.Parent = GameObject;
		_bolaProp.Tags.Add( "ignore" );
		var renderer = _bolaProp.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}

	void DestroyBolaProp()
	{
		if ( _bolaProp is not null && _bolaProp.IsValid() )
			_bolaProp.Destroy();

		_bolaProp = null;
		_bolaPropModelPath = null;
		_bolaSwingOut01 = 0f;
	}
}
