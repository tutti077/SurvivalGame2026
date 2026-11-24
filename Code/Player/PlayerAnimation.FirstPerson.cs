using System;
using Sandbox;

namespace Survival;

/// <summary>
/// First person on the owner's screen (Mark 2026-11-16: "clean up what the animations look like when in first person?
/// Sword seems too close to face, no arms or feet", then "Make it so we just see our arms, no chest or legs"). The stock
/// controller hid the whole body in first person and left the held sword floating where its shoulder rest put it, a
/// hand's width from the eye. Now first person shows your two arms and the sword:
/// <list type="bullet">
/// <item><b>Arms only</b>: a second renderer of the body's model (<see cref="_firstPersonArms"/>, a child of the body,
/// no animation graph of its own) draws for the owner's camera with no shadow. Each frame every one of its bones is set
/// to the body's animated pose, except the pelvis, spine, neck and collarbones, which are scaled to nothing at a point
/// behind the camera; its head, legs and feet body groups are empty. Only the arms and hands are left (the chest skin
/// stretches back out of view - shrunk in place, or with the collarbones kept, it showed as a sheet between the arms when
/// looking down). The arm solves (<see cref="SolveTwoBoneArm"/>: this rest grip, the shield) write the same bones to it.
/// Bone merge was tried first: it overwrites every bone from the body, so the torso could not be shrunk. The real body and every worn piece draw
/// shadow-only, so the shadow on the ground is still the whole player. The stock controller tags the pawn's renderers
/// <c>viewer</c> every frame in first person and the pawn camera leaves that tag out, so <c>PlayerMovement</c> lets the
/// first-person camera draw <c>viewer</c> objects (the tag cannot be taken off: the controller re-tags after this runs).
/// <c>PlayerMovement</c> also turns the body with the view (First person: body turn limit) and sets the camera a little
/// ahead of the eyes (First person: camera forward).</item>
/// <item>At rest a one-handed sword is held low and out in front of the camera instead of on the shoulder: a two-bone
/// solve of the right arm (<see cref="SolveTwoBoneArm"/>, the shield's) brings the grip to a point fixed in view space,
/// blade up and leaning in. A swing, wind-up or guard leaves that grip in <see cref="FirstPersonSwordBlendOutSeconds"/>
/// for the animated one - the same clip everyone else sees - lifted in view space by
/// <see cref="FirstPersonSwingLiftUpMeters"/> / <see cref="FirstPersonSwingLiftForwardMeters"/>: unlifted, the strike
/// ran 30–35° under the view and the swing happened off the bottom of the screen.</item>
/// <item><b>Never through the camera</b> (Mark 2026-11-16: "The sword clips into the players head/camera, we should never
/// see clipping like that"): whatever the grip, the blade's tip may lean back from the view only
/// <see cref="FirstPersonTipBackLimitDegrees"/> (the wind-ups laid it back over the shoulder, through the head), and if
/// any point of the blade still comes within <see cref="FirstPersonSwordClearanceMeters"/> of the camera the grip is
/// pushed straight away from it - before the arm solve, and again on the solved grip if the arm fell short. The solve
/// stays on for every one-handed sword in first person (not only at rest), so this holds through swings and rolls too;
/// sitting and the wingsuit hand the arm back to the animation.</item>
/// </list>
/// Owner only, presentation only: remote peers and the third-person view are untouched.
/// </summary>
public sealed partial class PlayerAnimation
{
	[Property, Group( "First Person" ), Title( "Sword grip forward (m)" ), Range( 0.2f, 0.8f ), Step( 0.01f ), Description( "Rest grip ahead of the camera, along the view." )]
	public float FirstPersonSwordForwardMeters { get; set; } = 0.55f;

	[Property, Group( "First Person" ), Title( "Sword grip right (m)" ), Range( -0.2f, 0.5f ), Step( 0.01f )]
	public float FirstPersonSwordRightMeters { get; set; } = 0.3f;

	[Property, Group( "First Person" ), Title( "Sword grip down (m)" ), Range( 0f, 0.6f ), Step( 0.01f )]
	public float FirstPersonSwordDownMeters { get; set; } = 0.25f;

	[Property, Group( "First Person" ), Title( "Blade lean forward (deg)" ), Range( 0f, 80f ), Step( 1f ), Description( "Blade tilt from straight up toward the view direction." )]
	public float FirstPersonBladeForwardDegrees { get; set; } = 28f;

	[Property, Group( "First Person" ), Title( "Blade lean in (deg)" ), Range( -30f, 45f ), Step( 1f ), Description( "Blade tilt toward the middle of the screen." )]
	public float FirstPersonBladeInDegrees { get; set; } = 15f;

	[Property, Group( "First Person" ), Title( "Swing lift up (m)" ), Range( 0f, 0.5f ), Step( 0.01f ), Description( "Swings, wind-ups and the guard are the third-person clips moved up the view by this much, so the strike crosses the screen." )]
	public float FirstPersonSwingLiftUpMeters { get; set; } = 0.18f;

	[Property, Group( "First Person" ), Title( "Swing lift ceiling (m)" ), Range( 0f, 0.5f ), Step( 0.01f ), Description( "The lift never raises a grip higher than this far under eye level." )]
	public float FirstPersonSwingCeilingMeters { get; set; } = 0.2f;

	[Property, Group( "First Person" ), Title( "Swing lift forward (m)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float FirstPersonSwingLiftForwardMeters { get; set; } = 0.1f;

	[Property, Group( "First Person" ), Title( "Grip pitch limit (deg)" ), Range( 0f, 89f ), Step( 1f ), Description( "The rest grip follows the look up / down only this far, so looking down keeps the arm in front instead of folding it in against the body." )]
	public float FirstPersonGripPitchLimitDegrees { get; set; } = 40f;

	[Property, Group( "First Person" ), Title( "Blade clearance (m)" ), Range( 0.1f, 0.8f ), Step( 0.01f ), Description( "No point of the blade comes nearer the camera than this; the grip is pushed away instead. Keep it above the camera's near clip (0.25 m)." )]
	public float FirstPersonSwordClearanceMeters { get; set; } = 0.4f;

	[Property, Group( "First Person" ), Title( "Tip lean back limit (deg)" ), Range( -30f, 90f ), Step( 1f ), Description( "How far the blade tip may lean back from the view (0 = never past straight across). The third-person wind-ups lay the blade back over the shoulder, through the head." )]
	public float FirstPersonTipBackLimitDegrees { get; set; } = 15f;

	[Property, Group( "First Person" ), Title( "Sword blend in (s)" ), Range( 0.02f, 0.6f ), Step( 0.01f )]
	public float FirstPersonSwordBlendInSeconds { get; set; } = 0.18f;

	[Property, Group( "First Person" ), Title( "Sword blend out (s)" ), Range( 0.02f, 0.3f ), Step( 0.01f ), Description( "How fast the hand leaves the rest grip when a swing, wind-up or guard starts." )]
	public float FirstPersonSwordBlendOutSeconds { get; set; } = 0.06f;

	/// <summary>The local owner is looking through the pawn's eyes this frame.</summary>
	bool _firstPersonShown;
	/// <summary>Rest grip (1) versus the lifted animated grip (0).</summary>
	float _firstPersonSword01;
	/// <summary>The first-person arm solve as a whole (0 = the animation alone: pose clips, no sword, third person).</summary>
	float _firstPersonArm01;
	bool _firstPersonArmIkActive;
	/// <summary>The solved grip bone (<c>hold_R</c>) while the rest arm solve holds the sword: the blade follows it, not last frame's bone.</summary>
	Transform? _firstPersonSwordHold;
	/// <summary>The arms-only renderer the owner sees in first person (null in third person).</summary>
	SkinnedModelRenderer _firstPersonArms;
	RealTimeSince _firstPersonClothingScan;

	/// <summary>Bones scaled to nothing behind the camera on the arms renderer: the torso (head, legs and feet are empty body groups there).</summary>
	static readonly string[] FirstPersonCollapsedBones = { "pelvis", "spine_0", "spine_1", "spine_2", "neck_0" };
	/// <summary>
	/// Collarbones scaled to nothing at their shoulder joint (<c>arm_upper</c>): the chest skin they carry folds into the
	/// shoulder. Sent behind the camera the shoulder stretched across the view; folded at their own root (by the neck) the
	/// armpit skin stretched into a web whenever the arm was raised for a wind-up.
	/// </summary>
	static readonly (string Bone, string FoldInto)[] FirstPersonFoldedBones = { ("clavicle_L", "arm_upper_L"), ("clavicle_R", "arm_upper_R") };

	/// <summary>OnPreRender, after facing / look-aim and before the held sword is placed.</summary>
	void TickFirstPersonPresentation()
	{
		_firstPersonSwordHold = null;
		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		var controller = Components.Get<PlayerController>();
		var fp = !GameObject.IsProxy && controller is { IsValid: true, Enabled: true, ThirdPerson: false } && IsLocalOwnerView( controller );

		if ( fp != _firstPersonShown )
		{
			_firstPersonShown = fp;
			_firstPersonClothingScan = 999f;
			if ( !fp )
			{
				DestroyFirstPersonArms();
				SetBodyShadowOnly( body, false );
			}
		}

		if ( fp )
		{
			// Re-dressing can bring a new piece in at any time: twice a second is plenty
			if ( _firstPersonClothingScan > 0.5f )
			{
				_firstPersonClothingScan = 0f;
				SetBodyShadowOnly( body, true );
			}
			// The animated pose first: the arm solves after this write their bones over it
			TickFirstPersonArmsRenderer( body, controller );
		}

		TickFirstPersonSwordArm( body, controller, fp );
	}

	bool IsLocalOwnerView( PlayerController controller )
		=> GameObject.Network is not { Active: true } n || (n.Owner is null ? Networking.IsHost : n.IsOwner);

	/// <summary>The body and every worn piece shadow-only (first person: the arms renderer is what you see) or back to normal.</summary>
	void SetBodyShadowOnly( SkinnedModelRenderer body, bool shadowOnly )
	{
		var want = shadowOnly ? ModelRenderer.ShadowRenderType.ShadowsOnly : ModelRenderer.ShadowRenderType.On;
		foreach ( var renderer in body.GameObject.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null || !renderer.IsValid() || renderer == _firstPersonArms )
				continue;
			if ( renderer.RenderType != want )
				renderer.RenderType = want;
		}
	}

	/// <summary>Make the arms renderer if needed, then pose it as the body's animation with the torso collapsed, for this frame.</summary>
	void TickFirstPersonArmsRenderer( SkinnedModelRenderer body, PlayerController controller )
	{
		if ( _firstPersonArms is null || !_firstPersonArms.IsValid() || _firstPersonArms.Model != body.Model )
		{
			DestroyFirstPersonArms();
			var go = new GameObject( true, "FirstPersonArms" );
			go.Flags |= GameObjectFlags.NotSaved;
			go.NetworkMode = NetworkMode.Never;
			go.SetParent( body.GameObject, false );
			_firstPersonArms = go.Components.Create<SkinnedModelRenderer>();
			_firstPersonArms.Model = body.Model;
			_firstPersonArms.UseAnimGraph = false;
			_firstPersonArms.RenderType = ModelRenderer.ShadowRenderType.Off;
			_firstPersonArms.BodyGroups = body.BodyGroups;
			foreach ( var part in new[] { "Head", "Legs", "Feet" } )
				SetEmptyBodyGroup( _firstPersonArms, part );
		}

		var bones = body.Model?.Bones;
		if ( bones is null )
			return;
		var world = _firstPersonArms.Transform.World;
		// The torso shrinks to a point behind the eyes: the skin it shares with the shoulders stretches back, out of view
		var u = ResolvePawnUnitsPerMeter();
		var behind = controller.EyePosition - (Rotation.FromYaw( controller.EyeAngles.yaw ).Forward * (0.35f * u));
		foreach ( var bone in bones.AllBones )
		{
			if ( !body.TryGetBoneTransformAnimation( bone, out var tx ) )
				continue;
			var pose = Array.IndexOf( FirstPersonCollapsedBones, bone.Name ) >= 0 ? new Transform( behind, tx.Rotation, 0.001f ) : tx;
			foreach ( var (folded, into) in FirstPersonFoldedBones )
			{
				if ( bone.Name == folded && bones.GetBone( into ) is { } joint && body.TryGetBoneTransformAnimation( joint, out var jointTx ) )
					pose = new Transform( jointTx.Position, tx.Rotation, 0.001f );
			}
			_firstPersonArms.SetBoneTransform( bone, world.ToLocal( pose ) );
		}
	}

	/// <summary>
	/// An arm solve on the body, written to the arms renderer too: the three solved bones, and every bone under them (fingers,
	/// the grip bone) carried along by its animated offset from its nearest solved ancestor - every bone of the arms renderer
	/// is set outright each frame, so nothing under a solved bone follows it on its own.
	/// </summary>
	void MirrorArmSolveToFirstPerson( SkinnedModelRenderer body, string upperName, string lowerName, string handName,
		Transform upperAnim, Transform lowerAnim, Transform handAnim, Transform upper, Transform lower, Transform hand )
	{
		if ( _firstPersonArms is not { IsValid: true } arms || body.Model?.Bones is not { } bones )
			return;
		var world = arms.Transform.World;
		foreach ( var bone in bones.AllBones )
		{
			// Nearest solved ancestor (or the bone itself)
			Transform? anim = null, solved = null;
			for ( var b = bone; b is not null; b = b.Parent )
			{
				if ( b.Name == handName ) { anim = handAnim; solved = hand; break; }
				if ( b.Name == lowerName ) { anim = lowerAnim; solved = lower; break; }
				if ( b.Name == upperName ) { anim = upperAnim; solved = upper; break; }
			}
			if ( anim is null || !body.TryGetBoneTransformAnimation( bone, out var tx ) )
				continue;
			arms.SetBoneTransform( bone, world.ToLocal( solved.Value.ToWorld( anim.Value.ToLocal( tx ) ) ) );
		}
	}

	static void SetEmptyBodyGroup( SkinnedModelRenderer renderer, string partName )
	{
		var part = renderer.Model?.BodyParts.FirstOrDefault( p => string.Equals( p.Name, partName, StringComparison.OrdinalIgnoreCase ) );
		var empty = part?.Choices.ToList().FindIndex( c => string.IsNullOrEmpty( c.Name ) ) ?? -1;
		if ( empty >= 0 )
			renderer.SetBodyGroup( part.Name, empty );
	}

	void DestroyFirstPersonArms()
	{
		if ( _firstPersonArms is { IsValid: true } && _firstPersonArms.GameObject.IsValid() )
			_firstPersonArms.GameObject.Destroy();
		_firstPersonArms = null;
	}

	/// <summary>
	/// The right arm in first person: the rest grip in view space, blended (<see cref="_firstPersonSword01"/>) against the
	/// animated grip lifted into view, the whole solve faded by <see cref="_firstPersonArm01"/>.
	/// </summary>
	void TickFirstPersonSwordArm( SkinnedModelRenderer body, PlayerController controller, bool fp )
	{
		var armWanted = fp && FirstPersonSwordArmShown();
		var restWanted = armWanted && FirstPersonSwordAtRest();
		var restStep = Time.Delta / Math.Max( 0.01f, restWanted ? FirstPersonSwordBlendInSeconds : FirstPersonSwordBlendOutSeconds );
		_firstPersonSword01 = Math.Clamp( _firstPersonSword01 + (restWanted ? restStep : -restStep), 0f, 1f );
		var armStep = Time.Delta / Math.Max( 0.01f, armWanted ? FirstPersonSwordBlendInSeconds : FirstPersonSwordBlendOutSeconds );
		_firstPersonArm01 = Math.Clamp( _firstPersonArm01 + (armWanted ? armStep : -armStep), 0f, 1f );

		if ( _firstPersonArm01 <= 0f )
		{
			if ( _firstPersonArmIkActive )
			{
				_firstPersonArmIkActive = false;
				// The shield and the bow solve after this every frame, so clearing every override here costs them nothing.
				body.SceneModel?.ClearBoneOverrides();
			}
			return;
		}

		var bones = body.Model?.Bones;
		var handBone = bones?.GetBone( "hand_R" );
		var holdBone = bones?.GetBone( "hold_R" ) ?? handBone;
		if ( handBone is null || holdBone is null
		     || !body.TryGetBoneTransformAnimation( handBone, out var handAnim )
		     || !body.TryGetBoneTransformAnimation( holdBone, out var holdAnim ) )
			return;

		// The view the player sees: the eyes, the yaw-forward camera nudge PlayerMovement applies, the look rotation
		var u = ResolvePawnUnitsPerMeter();
		// The grip follows the view's pitch only so far: looking at your feet folded the arm across the belly
		var gripAngles = controller.EyeAngles;
		gripAngles.pitch = Math.Clamp( gripAngles.pitch, -FirstPersonGripPitchLimitDegrees, FirstPersonGripPitchLimitDegrees );
		var view = gripAngles.ToRotation();
		var yawForward = Rotation.FromYaw( controller.EyeAngles.yaw ).Forward;
		var nudge = Components.Get<PlayerMovement>() is { } movement ? movement.FirstPersonCameraForwardMeters : 0f;
		var eye = controller.EyePosition + (yawForward * (nudge * u));

		var gripPos = eye
		              + (view.Forward * (FirstPersonSwordForwardMeters * u))
		              + (view.Right * (FirstPersonSwordRightMeters * u))
		              + (view.Down * (FirstPersonSwordDownMeters * u));
		var lean = Rotation.FromAxis( view.Right, FirstPersonBladeForwardDegrees ) * Rotation.FromAxis( view.Forward, -FirstPersonBladeInDegrees );
		var tip = lean * view.Up;
		var gripRot = Rotation.LookAt( (view.Forward - (tip * Vector3.Dot( view.Forward, tip ))).Normal, tip );

		// Action: the clip's own grip, moved up and out along the view so the swing crosses the screen - but the lift never
		// raises a grip above the ceiling under the eyes (the left wind-up already holds the fist at chest height: lifted, it
		// sat in front of the face with the elbow under the camera), and a grip the clip already holds higher gets none
		var gripHeight = Vector3.Dot( holdAnim.Position - eye, view.Up );
		var ceiling = -FirstPersonSwingCeilingMeters * u;
		var lift = Math.Clamp( ceiling - gripHeight, 0f, FirstPersonSwingLiftUpMeters * u );
		var actionPos = holdAnim.Position + (view.Up * lift) + (view.Forward * (FirstPersonSwingLiftForwardMeters * u));
		var rest = Smooth( _firstPersonSword01 );
		var target = new Transform( Vector3.Lerp( actionPos, gripPos, rest ), Rotation.Slerp( holdAnim.Rotation, gripRot, rest ) );
		var arm = Smooth( _firstPersonArm01 );
		var hold = new Transform( Vector3.Lerp( holdAnim.Position, target.Position, arm ), Rotation.Slerp( holdAnim.Rotation, target.Rotation, arm ) );

		// Never through the camera: the tip may not lean far back from the real view, and the blade keeps its clearance
		var camera = new Transform( eye, controller.EyeAngles.ToRotation() );
		hold = hold.WithRotation( LimitTipBack( hold.Rotation, camera.Rotation.Forward ) );
		hold = hold.WithPosition( hold.Position + BladeClearancePush( hold, camera, u ) );

		// The hand target is the grip target carried back by the animated hand-to-grip offset
		var hand = hold.ToWorld( holdAnim.ToLocal( handAnim ) );
		var pole = (view.Right * 0.6f) + Vector3.Down;
		if ( SolveTwoBoneArm( body, "arm_upper_R", "arm_lower_R", "hand_R", hand.Position, pole, hand.Rotation ) is not { } solved )
			return;
		_firstPersonArmIkActive = true;
		// The grip bone follows the solved hand by its animated offset (out-of-reach targets leave the hand short of it)
		var held = solved.ToWorld( handAnim.ToLocal( holdAnim ) );
		// An arm that fell short of the pushed target can leave the blade near the camera again: the blade gives way last
		_firstPersonSwordHold = held.WithPosition( held.Position + BladeClearancePush( held, camera, u ) );
	}

	/// <summary>The grip rotation with its blade tip (grip up) leaning back from the view at most <see cref="FirstPersonTipBackLimitDegrees"/>.</summary>
	Rotation LimitTipBack( Rotation grip, Vector3 viewForward )
	{
		var tip = grip.Up;
		var minForward = -MathF.Sin( FirstPersonTipBackLimitDegrees * MathF.PI / 180f );
		var forward = Vector3.Dot( tip, viewForward );
		if ( forward >= minForward )
			return grip;
		var side = tip - (viewForward * forward);
		var sideLength = side.Length;
		var sideDir = sideLength > 1e-4f ? side / sideLength : Vector3.Up;
		var limited = ((viewForward * minForward) + (sideDir * MathF.Sqrt( 1f - (minForward * minForward) ))).Normal;
		return Rotation.FromToRotation( tip, limited ) * grip;
	}

	/// <summary>How far below the grip point the hand reaches, along the blade axis: kept clear of the camera with the blade.</summary>
	const float FistReachMeters = 0.15f;

	/// <summary>How far to move a grip so no point of its blade (or the fist on it) is nearer the camera than <see cref="FirstPersonSwordClearanceMeters"/>.</summary>
	Vector3 BladeClearancePush( Transform grip, Transform camera, float unitsPerMeter )
	{
		var tipDir = grip.Rotation.Up;
		var length = _demoStickIsHeldModel && _demoStickFit.Length > 1f ? _demoStickFit.Length : MeleeDemoStickLengthMeters * unitsPerMeter;
		var along = Math.Clamp( MeleeDemoStickGripAlongLength, 0f, 1f );
		// The fist around the grip counts as blade too (the left wind-up brought it into the camera)
		var butt = grip.Position - (tipDir * ((length * along) + (FistReachMeters * unitsPerMeter)));
		length += FistReachMeters * unitsPerMeter;
		var t = Math.Clamp( Vector3.Dot( camera.Position - butt, tipDir ) / Math.Max( 1f, length ), 0f, 1f );
		var closest = butt + (tipDir * (length * t));
		var away = closest - camera.Position;
		var distance = away.Length;
		var clearance = FirstPersonSwordClearanceMeters * unitsPerMeter;
		if ( distance >= clearance )
			return Vector3.Zero;
		var dir = distance > 1e-3f ? away / distance : camera.Rotation.Right;
		return dir * (clearance - distance);
	}

	static float Smooth( float t ) => t * t * (3f - (2f * t));

	/// <summary>A one-handed sword in hand, not seated or gliding: the first-person solve (and its camera clearance) is on.</summary>
	bool FirstPersonSwordArmShown()
	{
		if ( !MeleeOneHandedGrip || !ShowMeleeDemoStick || ResolvePresentationHoldPose() != HoldPose.MeleeTwoHand )
			return false;
		_seatMovement ??= Components.Get<PlayerMovement>();
		return !_wingsuitPoseActive && _seatMovement is not { IsSeated: true };
	}

	/// <summary>No swing, wind-up, shoulder swap, guard or pose clip: the sword sits in the rest grip.</summary>
	bool FirstPersonSwordAtRest()
		=> !_swordCocked && !_swordStrikeScheduled && !_swordStrikeStarted && !IsGuardStanceShown()
		   && !_hitReactionPoseActive && !_ledgeMantlePoseActive && !_dodgeRollPoseActive;
}
