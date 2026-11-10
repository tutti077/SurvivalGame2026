using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The bow in the hand and the archer stance (Mark 2026-11-06: "standing sideways with its torso facing to the
/// right when the bow is being pulled back"). The main hand's <c>heldModel</c> (a <see cref="EquippedItemActions.PrimaryRanged"/>
/// profile) rides the LEFT hand's grip bone at rest. While the bow is drawn — <see cref="PlayerCombat.PresentationBowDraw01"/>,
/// the live charge on the driver and the synced draw on every other peer — the stance blends in:
/// <list type="bullet">
/// <item>the Body renderer child turns <see cref="BowStanceYawDegrees"/> to the right of the aim (never the root,
/// the camera or EyeAngles), the chest stays square to it and the head and eyes turn back to the target;</item>
/// <item>the left arm pushes the bow out along the aim (pitch included), the bow upright with a small cant;</item>
/// <item>the right hand takes the string from the bow back to the anchor at the cheek as the draw fills, and
/// after a loose (<see cref="PlayerCombat.BowShotCounter"/>) flicks back past it for the follow-through;</item>
/// <item>a nocked arrow lies from the string hand through the bow.</item>
/// </list>
/// The held bow model has no string (<c>simple_bow_held</c>, Tools/item_models): the string is drawn here as two thin
/// segments from the tips to the nock, straight tip to tip at rest.
/// Both arms are bone overrides from the shared two-bone solve (<see cref="SolveTwoBoneArm"/>) after the animation.
/// Bow model axes in the ENGINE (the item generator's OBJ comes in with its +X along engine +Y — measured
/// 2026-11-06, simple_bow bounds 0..50 on Y): +Y tip to tip, +Z the belly (toward the target), the string at -Z;
/// the grip is the middle of the belly. Hidden in the hit reaction, ledge mantle, dodge roll,
/// seated and wingsuit poses.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string BowObjectName = "held_bow";
	const string NockedArrowObjectName = "held_bow_arrow";

	static readonly string[] HeadBoneCandidates = { "head", "Head" };

	[Property, Group( "Bow" ), Title( "Show held bow" )]
	public bool ShowHeldBow { get; set; } = true;

	/// <summary>How far the body turns to the right of the aim while drawing — 90 = side-on, left shoulder to the target.</summary>
	[Property, Group( "Bow" ), Title( "Stance: turn right of aim (°)" ), Range( 0f, 120f ), Step( 1f )]
	public float BowStanceYawDegrees { get; set; } = 90f;

	/// <summary>Seconds for the stance (turn + arms) to come in when the draw starts and to go after the shot.</summary>
	[Property, Group( "Bow" ), Title( "Stance: blend (s)" ), Range( 0.03f, 0.6f ), Step( 0.01f )]
	public float BowStanceBlendSeconds { get; set; } = 0.12f;

	/// <summary>Time constant the body's yaw follows the aim with while in the stance.</summary>
	[Property, Group( "Bow" ), Title( "Stance: turn smoothing (s)" ), Range( 0f, 0.3f ), Step( 0.01f )]
	public float BowStanceTurnSmoothingSeconds { get; set; } = 0.06f;

	/// <summary>String anchor from the head bone, in aim space (m): x toward the target, y toward the chest side, z up.</summary>
	[Property, Group( "Bow" ), Title( "Draw: anchor from head (fwd, right, up m)" )]
	public Vector3 BowAnchorFromHeadMeters { get; set; } = new( 0.07f, 0.07f, 0.02f );

	/// <summary>Anchor → bow grip along the aim at full draw (m) — the draw length.</summary>
	[Property, Group( "Bow" ), Title( "Draw: length (m)" ), Range( 0.4f, 0.9f ), Step( 0.01f )]
	public float BowDrawLengthMeters { get; set; } = 0.64f;

	/// <summary>Top of the bow leans this far off upright, away from the string hand.</summary>
	[Property, Group( "Bow" ), Title( "Draw: bow cant (°)" ), Range( -30f, 30f ), Step( 1f )]
	public float BowCantDegrees { get; set; } = 8f;

	/// <summary>How long the string hand holds its flick back after a loose before the stance goes.</summary>
	[Property, Group( "Bow" ), Title( "Release: follow-through (s)" ), Range( 0f, 0.8f ), Step( 0.01f )]
	public float BowFollowThroughSeconds { get; set; } = 0.28f;

	/// <summary>Bow hand: how far each finger joint closes round the grip (°, three joints per finger).</summary>
	[Property, Group( "Bow" ), Title( "Hands: bow grip curl (°/joint)" ), Range( -90f, 90f ), Step( 1f )]
	public float BowGripCurlDegrees { get; set; } = 55f;

	/// <summary>String hand: the hook of the three draw fingers on the string (°, per joint).</summary>
	[Property, Group( "Bow" ), Title( "Hands: string hook curl (°/joint)" ), Range( -90f, 90f ), Step( 1f )]
	public float BowStringCurlDegrees { get; set; } = 35f;

	/// <summary>How far past the anchor the string hand flies back on the loose (m).</summary>
	[Property, Group( "Bow" ), Title( "Release: hand flies back (m)" ), Range( 0f, 0.3f ), Step( 0.01f )]
	public float BowFollowThroughBackMeters { get; set; } = 0.1f;

	GameObject _bowProp;
	string _bowBuiltModelPath = string.Empty;
	/// <summary>Grip point in the bow model's space (middle of the belly).</summary>
	Vector3 _bowGripLocal;
	GameObject _nockedArrow;
	string _nockedAmmoId = string.Empty;
	GameObject _bowStringTop;
	GameObject _bowStringBottom;
	/// <summary>String ends in the bow model's space (the tips, on the string side).</summary>
	Vector3 _bowStringTopLocal;
	Vector3 _bowStringBottomLocal;
	const float BowStringThicknessMeters = 0.006f;
	static readonly Color BowStringTint = new( 0.78f, 0.72f, 0.58f );

	/// <summary>0 = no stance, 1 = full stance (linear; eased where it is used).</summary>
	float _bowStance01;
	bool _bowStanceWanted;
	float _bowFacingYaw;
	bool _bowFacingApplied;
	int _bowLastShotCounter;
	bool _bowShotCounterSeen;
	double _bowReleasedAt = -1d;
	bool _bowIkActive;
	Vector3 _bowNockWorld;
	bool _bowNockValid;

	/// <summary>Stance weight 0..1, eased — the look limits keep the chest square to the body by this much.</summary>
	float BowStanceWeight => _bowStance01 * _bowStance01 * (3f - 2f * _bowStance01);

	/// <summary>Where the arrow sits on the bow right now (world): the shot leaves from here. False without a drawn stance.</summary>
	public bool TryGetBowNockWorld( out Vector3 position )
	{
		position = _bowNockWorld;
		return _bowNockValid;
	}

	/// <summary>The main hand's profile when it is a bow with a held model, as this peer sees it; else null.</summary>
	EquipmentProfileData ResolvePresentationBowProfile()
	{
		var id = ResolvePresentationMainHandId();
		if ( string.IsNullOrWhiteSpace( id ) || !EquipmentCatalog.TryGet( id, out var profile ) )
			return null;

		if ( !EquipmentCatalog.HasAction( id, EquippedItemActions.PrimaryRanged ) || string.IsNullOrWhiteSpace( profile.HeldModel ) )
			return null;

		return profile;
	}

	bool BowPoseBlocked()
	{
		_seatMovement ??= Components.Get<PlayerMovement>();
		return _hitReactionPoseActive || _ledgeMantlePoseActive || _dodgeRollPoseActive
		       || _seatMovement is { IsSeated: true } || _seatMovement is { IsWingsuitDeployed: true };
	}

	/// <summary>OnUpdate: is the stance wanted (drawing, or the follow-through of a loose), and blend toward it.</summary>
	void TickBowStanceState()
	{
		var combat = ResolveCombat();
		var draw = combat is { IsValid: true, Enabled: true } ? combat.PresentationBowDraw01 : 0f;

		if ( combat is { IsValid: true } )
		{
			var shots = combat.BowShotCounter;
			if ( _bowShotCounterSeen && shots != _bowLastShotCounter )
				_bowReleasedAt = Time.NowDouble;
			_bowLastShotCounter = shots;
			_bowShotCounterSeen = true;
		}

		var following = _bowReleasedAt >= 0d && Time.NowDouble - _bowReleasedAt < BowFollowThroughSeconds;
		_bowStanceWanted = ShowHeldBow && !BowPoseBlocked() && ResolvePresentationBowProfile() is not null
		                   && (draw > 0f || following);

		var step = Time.Delta / MathF.Max( 0.03f, BowStanceBlendSeconds );
		_bowStance01 = _bowStanceWanted ? MathF.Min( 1f, _bowStance01 + step ) : MathF.Max( 0f, _bowStance01 - step );
	}

	/// <summary>
	/// The stance's body yaw (presentation only, the Body child). In: the yaw eases from wherever the body faced to
	/// <see cref="BowStanceYawDegrees"/> right of the aim. Out: back to the aim, and the controller has it again.
	/// </summary>
	bool TryGetBowFacingYaw( bool advance, out float yaw )
	{
		yaw = _bowFacingYaw;
		if ( _bowStance01 <= 0f )
		{
			_bowFacingApplied = false;
			return false;
		}

		if ( !advance )
			return _bowFacingApplied;

		var controller = Components.Get<PlayerController>();
		if ( controller is null || !controller.IsValid() )
			return false;

		var body = ResolveBody();
		if ( !_bowFacingApplied )
			_bowFacingYaw = body is { IsValid: true } ? body.WorldRotation.Yaw() : controller.EyeAngles.yaw;

		var desired = controller.EyeAngles.yaw - (_bowStanceWanted ? BowStanceYawDegrees : 0f);
		var k = BowStanceTurnSmoothingSeconds <= 1e-3f ? 1f : 1f - MathF.Exp( -Time.Delta / BowStanceTurnSmoothingSeconds );
		_bowFacingYaw += Angles.NormalizeAngle( desired - _bowFacingYaw ) * k;
		_bowFacingApplied = true;
		yaw = _bowFacingYaw;
		return true;
	}

	/// <summary>OnPreRender, after the shield: place the bow, and in the stance solve both arms onto it.</summary>
	void TickHeldBow()
	{
		_bowNockValid = false;
		var profile = ResolvePresentationBowProfile();
		var body = ResolveBody();
		if ( profile is null || !ShowHeldBow || BowPoseBlocked() || body is null || !body.IsValid() )
		{
			DestroyHeldBow();
			return;
		}

		EnsureHeldBow( profile.HeldModel.Trim() );
		if ( _bowProp is null || !_bowProp.IsValid() )
			return;

		if ( !TryGetFirstAnimationBoneTransform( body, DemoStickLeftBoneCandidates, out var holdL )
		     || !TryGetFirstAnimationBoneTransform( body, new[] { "hand_L" }, out var handL ) )
			return;

		// Rest: carried upright in the hanging left hand, the top leaning a little forward, belly to the front.
		var bodyRot = body.WorldRotation;
		var restRot = BowRotation( Vector3.Up * 0.94f + bodyRot.Forward * 0.34f, bodyRot.Forward );
		var w = BowStanceWeight;
		var controller = Components.Get<PlayerController>();
		if ( w <= 1e-3f || controller is null || !controller.IsValid()
		     || !TryGetFirstAnimationBoneTransform( body, HeadBoneCandidates, out var head )
		     || !TryGetFirstAnimationBoneTransform( body, DemoStickRightBoneCandidates, out var holdR )
		     || !TryGetFirstAnimationBoneTransform( body, new[] { "hand_R" }, out var handR ) )
		{
			ClearBowArmIk( body );
			PlaceBow( holdL.Position, restRot );
			HideNockedArrow();
			PlaceBowString( null );
			return;
		}

		var u = ResolvePawnUnitsPerMeter();
		var aim = controller.EyeAngles.WithRoll( 0f ).ToRotation();
		var f = aim.Forward;
		var anchor = head.Position
		             + f * (BowAnchorFromHeadMeters.x * u)
		             + aim.Right * (BowAnchorFromHeadMeters.y * u)
		             + aim.Up * (BowAnchorFromHeadMeters.z * u);
		var grip = anchor + f * (BowDrawLengthMeters * u);

		// Bow arm: the left grip bone onto the bow grip, the fingers closed round it.
		var bowGripWorld = SolveArmForGrip( body, "L", handL, holdL, Vector3.Lerp( holdL.Position, grip, w ), Vector3.Down, out var bowHand );
		if ( bowHand is { } bh )
			CurlFingers( body, "L", handL, bh, BowGripCurlDegrees * w );

		// String hand: from the bow to the anchor as the draw fills; on the loose it flies back past the cheek.
		var combat = ResolveCombat();
		var draw = combat is { IsValid: true } ? combat.PresentationBowDraw01 : 0f;
		var drawT = draw * draw * (3f - 2f * draw);
		var stringAtBow = grip - f * (0.08f * u);
		var following = draw <= 0f && _bowReleasedAt >= 0d
		                && Time.NowDouble - _bowReleasedAt < BowFollowThroughSeconds + BowStanceBlendSeconds;
		var stringPoint = following
			? anchor - f * (BowFollowThroughBackMeters * u) + aim.Right * (0.03f * u)
			: Vector3.Lerp( stringAtBow, anchor, drawT );
		_ = SolveArmForGrip( body, "R", handR, holdR, Vector3.Lerp( holdR.Position, stringPoint, w ), -f + Vector3.Up * 0.3f, out var drawHand );
		// Hooked on the string while drawing; open on the loose.
		if ( drawHand is { } dh )
			CurlFingers( body, "R", handR, dh, BowStringCurlDegrees * w * (following ? 0.2f : 1f) );
		_bowIkActive = true;

		// The bow sits in the solved fist, upright across the aim and canted a little.
		var up = Rotation.FromAxis( f, -BowCantDegrees ) * aim.Up;
		var stanceRot = BowRotation( up, f );
		PlaceBow( bowGripWorld, Rotation.Slerp( restRot, stanceRot, w ) );

		if ( w > 0.5f && !following )
		{
			var nock = Vector3.Lerp( stringAtBow, anchor, drawT );
			_bowNockWorld = bowGripWorld + f * (0.02f * u);
			_bowNockValid = true;
			PlaceNockedArrow( nock, f, u );
			PlaceBowString( nock );
		}
		else
		{
			HideNockedArrow();
			PlaceBowString( null );
		}
	}

	/// <summary>The string: tip → <paramref name="nock"/> → tip, or straight tip to tip when nothing is drawn.</summary>
	void PlaceBowString( Vector3? nock )
	{
		if ( _bowProp is null || !_bowProp.IsValid() )
			return;

		var top = _bowProp.WorldTransform.PointToWorld( _bowStringTopLocal );
		var bottom = _bowProp.WorldTransform.PointToWorld( _bowStringBottomLocal );
		var middle = nock ?? (top + bottom) * 0.5f;
		var thickness = BowStringThicknessMeters * ResolvePawnUnitsPerMeter();
		_bowStringTop = PlaceStringSegment( _bowStringTop, top, middle, thickness );
		_bowStringBottom = PlaceStringSegment( _bowStringBottom, bottom, middle, thickness );
	}

	GameObject PlaceStringSegment( GameObject segment, Vector3 a, Vector3 b, float thickness )
	{
		if ( segment is null || !segment.IsValid() )
		{
			segment = new GameObject( true, "held_bow_string" );
			segment.NetworkMode = NetworkMode.Never;
			segment.Parent = GameObject;
			segment.Tags.Add( "ignore" );
			var renderer = segment.Components.Create<ModelRenderer>();
			renderer.Model = Model.Load( "models/cylinder.vmdl" );
			renderer.Tint = BowStringTint;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		var model = segment.Components.Get<ModelRenderer>()?.Model;
		var size = model?.Bounds.Size ?? Vector3.One;
		var center = model?.Bounds.Center ?? Vector3.Zero;
		var dir = b - a;
		var length = MathF.Max( 0.01f, dir.Length );
		var rot = Rotation.LookAt( dir.LengthSquared > 1e-6f ? dir.Normal : Vector3.Up, Vector3.Up );
		var scale = new Vector3( length / MathF.Max( 0.01f, size.x ), thickness / MathF.Max( 0.01f, size.y ), thickness / MathF.Max( 0.01f, size.z ) );
		segment.WorldRotation = rot;
		segment.LocalScale = scale;
		segment.WorldPosition = (a + b) * 0.5f - rot * (center * scale);
		return segment;
	}

	/// <summary>Bow rotation from where its length (model +Y) and its belly (model +Z) should point.</summary>
	static Rotation BowRotation( Vector3 length, Vector3 belly )
	{
		length = length.Normal;
		belly = (belly - length * Vector3.Dot( belly, length )).Normal;
		// Model +X = +Y × +Z.
		return Rotation.LookAt( Vector3.Cross( length, belly ), belly );
	}

	/// <summary>
	/// Solve one arm so its GRIP bone (hold_*) lands on <paramref name="gripTarget"/>. The grip sits a hand's length off
	/// the hand bone in a direction that turns with the hand, so the first solve is measured and the target moved by the
	/// miss for a second one. Returns where the grip bone ended up.
	/// </summary>
	Vector3 SolveArmForGrip( SkinnedModelRenderer body, string side, Transform handAnim, Transform holdAnim, Vector3 gripTarget,
		Vector3 pole, out Transform? solvedHand )
	{
		solvedHand = null;
		var gripInHand = handAnim.PointToLocal( holdAnim.Position );
		var target = gripTarget + (handAnim.Position - holdAnim.Position);
		var grip = holdAnim.Position;
		for ( var pass = 0; pass < 2; pass++ )
		{
			if ( SolveTwoBoneArm( body, $"arm_upper_{side}", $"arm_lower_{side}", $"hand_{side}", target, pole, null ) is not { } hand )
				return grip;

			solvedHand = hand;
			grip = hand.PointToWorld( gripInHand );
			target += gripTarget - grip;
		}

		return grip;
	}

	static readonly string[] CurledFingers = { "index", "middle", "ring", "pinky" };

	/// <summary>
	/// Close the four fingers of one hand by <paramref name="degreesPerJoint"/> at each of their three joints, as bone
	/// overrides. Overriding the hand moves the fingers with it, but an overridden finger needs its full transform, so
	/// each finger's animated chain is carried into the solved hand's frame first, then bent about the knuckle line.
	/// </summary>
	void CurlFingers( SkinnedModelRenderer body, string side, Transform handAnim, Transform handSolved, float degreesPerJoint )
	{
		if ( MathF.Abs( degreesPerJoint ) < 0.5f )
			return;

		var bones = body.Model?.Bones;
		if ( bones is null )
			return;

		var toSolved = handSolved.Rotation * handAnim.Rotation.Inverse;
		Vector3 Carry( Vector3 p ) => handSolved.PointToWorld( handAnim.PointToLocal( p ) );

		// Knuckle line, index → pinky, in the solved hand.
		if ( !TryGetFirstAnimationBoneTransform( body, new[] { $"finger_index_0_{side}" }, out var indexBase )
		     || !TryGetFirstAnimationBoneTransform( body, new[] { $"finger_pinky_0_{side}" }, out var pinkyBase ) )
			return;
		var axis = Carry( indexBase.Position ) - Carry( pinkyBase.Position );
		if ( axis.LengthSquared < 1e-4f )
			return;
		axis = axis.Normal;
		// The left hand is the mirror: the same curl turns the other way round the index→pinky line.
		var sign = side == "L" ? -1f : 1f;

		var world = body.Transform.World;
		foreach ( var finger in CurledFingers )
		{
			var prevAnimPos = Vector3.Zero;
			var prevNewPos = Vector3.Zero;
			for ( var j = 0; j < 3; j++ )
			{
				var bone = bones.GetBone( $"finger_{finger}_{j}_{side}" );
				if ( bone is null || !body.TryGetBoneTransformAnimation( bone, out var tx ) )
					break;

				var animPos = Carry( tx.Position );
				var bend = Rotation.FromAxis( axis, sign * degreesPerJoint * (j + 1) );
				var parentBend = Rotation.FromAxis( axis, sign * degreesPerJoint * j );
				var newPos = j == 0 ? animPos : prevNewPos + parentBend * (animPos - prevAnimPos);
				body.SetBoneTransform( bone, world.ToLocal( new Transform( newPos, bend * toSolved * tx.Rotation ) ) );
				prevAnimPos = animPos;
				prevNewPos = newPos;
			}
		}
	}

	void PlaceBow( Vector3 gripWorld, Rotation rot )
	{
		_bowProp.WorldRotation = rot;
		_bowProp.LocalScale = Vector3.One;
		_bowProp.WorldPosition = gripWorld - rot * _bowGripLocal;
	}

	/// <summary>The nocked arrow: the ammo's own model, its nock on the string, the shaft along the aim through the bow.</summary>
	void PlaceNockedArrow( Vector3 nock, Vector3 dir, float unitsPerMeter )
	{
		var ammoId = Components.Get<PlayerAmmoPreference>()?.PreferredAmmoResourceId;
		if ( string.IsNullOrWhiteSpace( ammoId ) )
			ammoId = "arrow_wood";
		EnsureNockedArrow( ammoId );
		if ( _nockedArrow is null || !_nockedArrow.IsValid() )
			return;

		var length = ArrowProjectile.ShaftLengthMeters * unitsPerMeter;
		_nockedArrow.Enabled = true;
		_nockedArrow.WorldRotation = Rotation.LookAt( dir, Vector3.Up );
		_nockedArrow.WorldPosition = nock + dir * (length * 0.5f);
	}

	void EnsureNockedArrow( string ammoId )
	{
		if ( _nockedArrow is { IsValid: true } )
		{
			if ( string.Equals( _nockedAmmoId, ammoId, StringComparison.OrdinalIgnoreCase ) )
				return;
			_nockedArrow.Destroy();
		}

		var u = ResolvePawnUnitsPerMeter();
		_nockedArrow = new GameObject( true, NockedArrowObjectName );
		_nockedArrow.NetworkMode = NetworkMode.Never;
		_nockedArrow.Parent = GameObject;
		_nockedArrow.Tags.Add( "ignore" );
		_nockedAmmoId = ammoId;
		var renderer = ArrowProjectile.AttachArrowVisual( _nockedArrow, ammoId,
			ArrowProjectile.ShaftLengthMeters * u, ArrowProjectile.ShaftThicknessMeters * u );
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}

	void HideNockedArrow()
	{
		if ( _nockedArrow is { IsValid: true } && _nockedArrow.Enabled )
			_nockedArrow.Enabled = false;
	}

	void EnsureHeldBow( string modelPath )
	{
		if ( _bowProp is { IsValid: true } )
		{
			if ( string.Equals( _bowBuiltModelPath, modelPath, StringComparison.OrdinalIgnoreCase ) )
				return;

			DestroyHeldBow();
		}

		var model = Model.Load( modelPath );
		_bowBuiltModelPath = modelPath;
		if ( model is null || model.IsError )
		{
			Log.Warning( $"[PlayerAnimation] bow heldModel '{modelPath}' failed to load." );
			return;
		}

		// Local presentation only — a plain child of a networked pawn replicates (see the demo stick).
		_bowProp = new GameObject( true, BowObjectName );
		_bowProp.NetworkMode = NetworkMode.Never;
		_bowProp.Parent = GameObject;
		_bowProp.Tags.Add( "ignore" );
		var renderer = _bowProp.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = Color.White;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		// Grip = the middle of the bow's length (Y), on the belly (+Z) where the wrap sits, a hand's half-thickness in.
		// String ends: 2 cm in from each tip, 1.2 cm up from the string-side face (simple_bow's string in items_held.py).
		var b = model.Bounds;
		var u = ResolvePawnUnitsPerMeter();
		_bowGripLocal = new Vector3( b.Center.x, b.Center.y, b.Maxs.z - 0.02f * u );
		_bowStringTopLocal = new Vector3( b.Center.x, b.Maxs.y - 0.02f * u, b.Mins.z + 0.012f * u );
		_bowStringBottomLocal = new Vector3( b.Center.x, b.Mins.y + 0.02f * u, b.Mins.z + 0.012f * u );

		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() || child == _bowProp )
				continue;

			if ( string.Equals( child.Name, BowObjectName, StringComparison.OrdinalIgnoreCase ) )
				child.Destroy();
		}
	}

	void DestroyHeldBow()
	{
		ClearBowArmIk( ResolveBody() );
		_bowNockValid = false;

		if ( _bowProp is { IsValid: true } )
			_bowProp.Destroy();
		_bowProp = null;
		_bowBuiltModelPath = string.Empty;

		if ( _nockedArrow is { IsValid: true } )
			_nockedArrow.Destroy();
		_nockedArrow = null;

		if ( _bowStringTop is { IsValid: true } )
			_bowStringTop.Destroy();
		if ( _bowStringBottom is { IsValid: true } )
			_bowStringBottom.Destroy();
		_bowStringTop = null;
		_bowStringBottom = null;
	}

	void ClearBowArmIk( SkinnedModelRenderer body )
	{
		if ( !_bowIkActive )
			return;

		_bowIkActive = false;
		// No per-bone clear exists; the shield (the only other override writer) is hidden while a two-handed bow is out.
		if ( body is not null && body.IsValid() )
			body.SceneModel?.ClearBoneOverrides();
	}
}
