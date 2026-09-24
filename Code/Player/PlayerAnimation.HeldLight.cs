using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Held light sources (torch, lantern): the prop in the hand and the point light on it. Driven by
/// the main-hand profile's <c>heldLight</c> block (<see cref="HeldLightData"/>), so every peer builds
/// the same prop from the synced main-hand id. Torch = a vertical stick held in the right hand
/// (Citizen <c>holdtype=HoldItem</c>, right-handed — the <c>HoldItem_RH_Pose_Standing</c> /
/// <c>HoldItem_RH_Hand_Basic</c> clips) with the light at its tip; lantern = a box hanging below
/// the relaxed right hand at hip height with the light inside it. Props are local presentation
/// (<see cref="NetworkMode.Never"/>) exactly like the melee demo stick.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string HeldLightObjectName = "held_light_prop";
	const string HeldLightGlowObjectName = "held_light_glow";
	const string HeldLightLampObjectName = "held_light_lamp";

	[Property, Group( "Held light" ), Title( "Show held light props" )]
	public bool ShowHeldLightProps { get; set; } = true;

	[Property, Group( "Held light" ), Title( "Torch length (m)" ), Range( 0.2f, 1.5f ), Step( 0.01f )]
	public float TorchLengthMeters { get; set; } = 0.6f;

	[Property, Group( "Held light" ), Title( "Torch thickness (m)" ), Range( 0.01f, 0.15f ), Step( 0.005f )]
	public float TorchThicknessMeters { get; set; } = 0.04f;

	/// <summary>Where along the torch the hand sits: 0 = bottom end, 0.5 = middle.</summary>
	[Property, Group( "Held light" ), Title( "Torch grip along length (0=bottom)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float TorchGripAlongLength { get; set; } = 0.25f;

	[Property, Group( "Held light" ), Title( "Torch flame size (m)" ), Range( 0.02f, 0.3f ), Step( 0.01f )]
	public float TorchFlameSizeMeters { get; set; } = 0.1f;

	[Property, Group( "Held light" ), Title( "Torch wood tint" )]
	public Color TorchWoodTint { get; set; } = new( 0.45f, 0.3f, 0.16f, 1f );

	[Property, Group( "Held light" ), Title( "Torch flame tint" )]
	public Color TorchFlameTint { get; set; } = new( 1f, 0.6f, 0.15f, 1f );

	/// <summary>How far below the hand the lantern body hangs.</summary>
	[Property, Group( "Held light" ), Title( "Lantern hang (m)" ), Range( 0f, 0.8f ), Step( 0.01f )]
	public float LanternHangMeters { get; set; } = 0.28f;

	[Property, Group( "Held light" ), Title( "Lantern size (m)" )]
	public Vector3 LanternSizeMeters { get; set; } = new( 0.16f, 0.16f, 0.22f );

	[Property, Group( "Held light" ), Title( "Lantern tint" )]
	public Color LanternTint { get; set; } = new( 0.3f, 0.3f, 0.33f, 1f );

	[Property, Group( "Held light" ), Title( "Lantern glass tint" )]
	public Color LanternGlassTint { get; set; } = new( 1f, 0.85f, 0.5f, 1f );

	GameObject _heldLightProp;
	ModelRenderer _heldLightRenderer;
	ModelRenderer _heldLightGlowRenderer;
	PointLight _heldLight;
	HeldLightAnchor _heldLightBuiltAnchor;
	float _heldLightMeshHalfExtent = 0.5f;

	/// <summary>Held-light block of whatever this peer knows is in the main hand, or null.</summary>
	HeldLightData ResolveHeldLightProfile()
	{
		var id = ResolvePresentationMainHandId();
		if ( string.IsNullOrWhiteSpace( id ) )
			return null;

		return EquipmentCatalog.TryGet( id, out var profile ) ? profile.HeldLight : null;
	}

	/// <summary>Main-hand id as this peer sees it: local slots on host / owner, host Sync on other peers.</summary>
	string ResolvePresentationMainHandId()
	{
		var networked = GameObject.Network is { Active: true };
		if ( networked && GameObject.IsProxy )
			return Components.Get<PlayerEquipment>()?.NetworkedMainHandResourceId ?? string.Empty;

		_equippedItem ??= Components.Get<PlayerEquippedItem>();
		return _equippedItem is { IsValid: true } ? _equippedItem.EquippedResourceId : string.Empty;
	}

	/// <summary>Citizen right-handed item hold (torch). The sword is never up at the same time.</summary>
	void ApplyHoldItemRightHold()
	{
		DestroyMeleeDemoStick();

		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			_animHelper.HoldType = CitizenAnimationHelper.HoldTypes.HoldItem;
			_animHelper.Handedness = CitizenAnimationHelper.Hand.Right;
			_animHelper.IsWeaponLowered = false;
		}

		var body = ResolveBody();
		if ( body is null )
			return;

		body.Set( "holdtype", (int)CitizenAnimationHelper.HoldTypes.HoldItem );
		body.Set( "holdtype_handedness", (int)CitizenAnimationHelper.Hand.Right );
		body.Set( "holdtype_pose", 0f );
		body.Set( "b_weapon_lower", false );
	}

	/// <summary>OnPreRender, after the sword: place the torch / lantern on the right hand bone.</summary>
	void TickHeldLightProp()
	{
		var data = ResolveHeldLightProfile();
		if ( data is null || !ShowHeldLightProps || _hitReactionPoseActive || _ledgeMantlePoseActive
		     || _appliedHoldPose == HoldPose.MeleeTwoHand )
		{
			DestroyHeldLightProp();
			return;
		}

		var anchor = data.ResolveAnchor();
		EnsureHeldLightProp( data, anchor );
		if ( _heldLightProp is null || !_heldLightProp.IsValid() )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid()
		     || !TryGetFirstBoneTransform( body, DemoStickRightBoneCandidates, out var handTx ) )
			return;

		var unitsPerMeter = ResolvePawnUnitsPerMeter();

		if ( anchor == HeldLightAnchor.LanternHang )
		{
			// Body hangs straight down from the hand; the lamp sits inside it.
			var size = new Vector3(
				MathF.Max( 0.02f, LanternSizeMeters.x ),
				MathF.Max( 0.02f, LanternSizeMeters.y ),
				MathF.Max( 0.02f, LanternSizeMeters.z ) ) * unitsPerMeter;
			var center = handTx.Position + Vector3.Down * (LanternHangMeters * unitsPerMeter + size.z * 0.5f);

			_heldLightProp.WorldRotation = Rotation.Identity;
			_heldLightProp.WorldPosition = center;
			_heldLightProp.LocalScale = size / (_heldLightMeshHalfExtent * 2f);

			if ( _heldLightGlowRenderer is { IsValid: true } )
			{
				// Glass: a slightly smaller box inside the frame, lit tint.
				_heldLightGlowRenderer.GameObject.WorldPosition = center;
				_heldLightGlowRenderer.GameObject.WorldRotation = Rotation.Identity;
				_heldLightGlowRenderer.GameObject.WorldScale = (size * 0.7f) / (_heldLightMeshHalfExtent * 2f);
			}

			if ( _heldLight is { IsValid: true } )
				_heldLight.GameObject.WorldPosition = center;

			return;
		}

		// Torch: vertical stick, hand at TorchGripAlongLength from the bottom, flame + light on the tip.
		var length = MathF.Max( 0.1f, TorchLengthMeters ) * unitsPerMeter;
		var thickness = MathF.Max( 0.01f, TorchThicknessMeters ) * unitsPerMeter;
		var bottom = handTx.Position + Vector3.Down * (length * Math.Clamp( TorchGripAlongLength, 0f, 1f ));
		var tip = bottom + Vector3.Up * length;
		var stickCenter = (bottom + tip) * 0.5f;

		_heldLightProp.WorldRotation = Rotation.Identity;
		_heldLightProp.WorldPosition = stickCenter;
		_heldLightProp.LocalScale = new Vector3( thickness, thickness, length ) / (_heldLightMeshHalfExtent * 2f);

		var flame = MathF.Max( 0.02f, TorchFlameSizeMeters ) * unitsPerMeter;
		if ( _heldLightGlowRenderer is { IsValid: true } )
		{
			_heldLightGlowRenderer.GameObject.WorldPosition = tip + Vector3.Up * (flame * 0.4f);
			_heldLightGlowRenderer.GameObject.WorldRotation = Rotation.Identity;
			_heldLightGlowRenderer.GameObject.WorldScale = new Vector3( flame, flame, flame * 1.4f ) / (_heldLightMeshHalfExtent * 2f);
		}

		if ( _heldLight is { IsValid: true } )
			_heldLight.GameObject.WorldPosition = tip + Vector3.Up * (flame * 0.5f);
	}

	/// <summary>Pawn distances: Citizen BodyHeight 72 ≈ 1.8 m (commandment #4).</summary>
	float ResolvePawnUnitsPerMeter()
	{
		var controller = Components.Get<PlayerController>();
		var bodyHeight = controller is { IsValid: true } ? controller.BodyHeight : 72f;
		return MathF.Max( 1f, bodyHeight ) / 1.8f;
	}

	void EnsureHeldLightProp( HeldLightData data, HeldLightAnchor anchor )
	{
		if ( _heldLightProp is { IsValid: true } && _heldLightBuiltAnchor == anchor )
		{
			if ( _heldLight is { IsValid: true } )
			{
				_heldLight.LightColor = data.ResolveColor();
				_heldLight.Radius = TerrainWorldUnits.MetersToEngine( MathF.Max( 0.5f, data.RadiusMeters ) );
			}

			return;
		}

		DestroyHeldLightProp();
		_heldLightBuiltAnchor = anchor;

		// Local presentation only — a plain child of a networked pawn replicates (see the demo stick).
		_heldLightProp = new GameObject( true, HeldLightObjectName );
		_heldLightProp.NetworkMode = NetworkMode.Never;
		_heldLightProp.Parent = GameObject;
		_heldLightProp.Tags.Add( "ignore" );

		var model = Model.Load( DemoStickModelPath );
		_heldLightMeshHalfExtent = ResolveMeshHalfExtentAlongX( model );

		_heldLightRenderer = _heldLightProp.Components.Create<ModelRenderer>();
		_heldLightRenderer.Model = model;
		_heldLightRenderer.Tint = anchor == HeldLightAnchor.LanternHang ? LanternTint : TorchWoodTint;
		_heldLightRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		var glow = new GameObject( true, HeldLightGlowObjectName );
		glow.NetworkMode = NetworkMode.Never;
		glow.Parent = _heldLightProp;
		glow.Tags.Add( "ignore" );
		_heldLightGlowRenderer = glow.Components.Create<ModelRenderer>();
		_heldLightGlowRenderer.Model = model;
		_heldLightGlowRenderer.Tint = anchor == HeldLightAnchor.LanternHang ? LanternGlassTint : TorchFlameTint;
		_heldLightGlowRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		var lamp = new GameObject( true, HeldLightLampObjectName );
		lamp.NetworkMode = NetworkMode.Never;
		lamp.Parent = _heldLightProp;
		_heldLight = lamp.Components.Create<PointLight>();
		_heldLight.LightColor = data.ResolveColor();
		_heldLight.Radius = TerrainWorldUnits.MetersToEngine( MathF.Max( 0.5f, data.RadiusMeters ) );

		DestroyStrayHeldLightProps();
	}

	void DestroyStrayHeldLightProps()
	{
		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() || child == _heldLightProp )
				continue;

			if ( string.Equals( child.Name, HeldLightObjectName, StringComparison.OrdinalIgnoreCase ) )
				child.Destroy();
		}
	}

	void DestroyHeldLightProp()
	{
		if ( _heldLightProp is not null && _heldLightProp.IsValid() )
			_heldLightProp.Destroy();

		_heldLightProp = null;
		_heldLightRenderer = null;
		_heldLightGlowRenderer = null;
		_heldLight = null;
	}
}
