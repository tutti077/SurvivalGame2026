using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Offhand shield presentation. The OffHand paperdoll item's <c>heldModel</c> (equipment_profiles.json,
/// slot <c>offHand</c>) is strapped to the LEFT forearm while nothing hides it, and while the guard is up
/// (right-click, <see cref="PlayerCombat.IsGuardShown"/>) it glides to the centre of the body, square to the
/// block basis yaw — the "shield raise" that goes with the block effect. Every peer builds its own local
/// prop (<see cref="NetworkMode.Never"/>, like the sword and the torch) from the offhand id it can see:
/// local slots on host / owner, <see cref="PlayerEquipment.NetworkedOffHandResourceId"/> on other peers.
/// Shield model convention (Blender/scripts/create_shield_wood.py): +Z = outward face normal, +X = up the
/// forearm toward the elbow, origin on the forearm centre line — so the prop's origin goes ON the arm and
/// no per-item angles exist. Hidden during the hit reaction, ledge mantle, seated, wingsuit, and while a
/// two-handed main hand (bow) needs both hands.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string ShieldObjectName = "offhand_shield";

	static readonly string[] LeftForearmBoneCandidates =
	{
		"arm_lower_L",
		"arm_lower_l",
		"LeftForeArm",
	};

	[Property, Group( "Shield" ), Title( "Show offhand shield" )]
	public bool ShowOffhandShield { get; set; } = true;

	/// <summary>Where along the left forearm the shield's centre sits at rest: 0 = at the hand, 1 = at the elbow.</summary>
	[Property, Group( "Shield" ), Title( "Rest: along forearm (0=hand, 1=elbow)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float ShieldRestAlongForearm { get; set; } = 0.5f;

	/// <summary>Trim along the outward normal on top of the model's own forearm origin (+ = further off the arm).</summary>
	[Property, Group( "Shield" ), Title( "Rest: arm offset (m)" ), Range( -0.1f, 0.1f ), Step( 0.005f )]
	public float ShieldRestArmOffsetMeters { get; set; } = 0f;

	/// <summary>Height of the raised shield's centre as a fraction of body height (0.62 ≈ the chest).</summary>
	[Property, Group( "Shield" ), Title( "Block: height (fraction of body)" ), Range( 0.3f, 0.9f ), Step( 0.01f )]
	public float ShieldBlockHeightFraction { get; set; } = 0.645f;

	[Property, Group( "Shield" ), Title( "Block: forward (m)" ), Range( 0.1f, 0.8f ), Step( 0.01f )]
	public float ShieldBlockForwardMeters { get; set; } = 0.28f;

	/// <summary>Raised shield centre this far toward the shield arm (+ = left). A few centimetres keeps the hand within the arm's reach while the shield still reads as centred.</summary>
	[Property, Group( "Shield" ), Title( "Block: left offset (m)" ), Range( -0.3f, 0.3f ), Step( 0.01f )]
	public float ShieldBlockLeftMeters { get; set; } = 0.08f;

	/// <summary>Top of the raised shield leans back toward the player by this much (the face looks a little up).</summary>
	[Property, Group( "Shield" ), Title( "Block: tilt back (°)" ), Range( -30f, 45f ), Step( 1f )]
	public float ShieldBlockTiltDegrees { get; set; } = 8f;

	/// <summary>Seconds for the shield to travel arm → centre when the guard goes up (and back when it drops) — matches <see cref="PlayerCombat.BlockRaiseSeconds"/> (Mark 2026-11-04: the block shows and blocks in 50 ms).</summary>
	/// <summary>Shield bash (F with a shield out, Mark 2026-11-05): the whole thrust-and-return, seconds.</summary>
	[Property, Group( "Shield" ), Title( "Bash: time (s)" ), Range( 0.15f, 1f ), Step( 0.01f )]
	public float ShieldBashSeconds { get; set; } = 0.35f;

	/// <summary>How far past the block position the shield drives at the peak of a bash.</summary>
	[Property, Group( "Shield" ), Title( "Bash: forward (m)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float ShieldBashForwardMeters { get; set; } = 0.35f;

	[Property, Group( "Shield" ), Title( "Raise / lower time (s)" ), Range( 0.02f, 0.6f ), Step( 0.01f )]
	public float ShieldRaiseSeconds { get; set; } = 0.05f;

	/// <summary>
	/// While the shield is raised, the left arm is solved onto it (Mark 2026-11-02: "make the arm follow
	/// the shield when the player blocks"): the hand keeps the placement it has against the strap at rest,
	/// a two-bone IK (<see cref="SolveShieldArm"/>) swings <c>arm_upper_L</c> / <c>arm_lower_L</c> to reach
	/// it and the fingers ride along. The sword graph declares <c>ik.hand_left.*</c> parameters but has no IK
	/// node reading them, so this is done with bone overrides after the animation.
	/// </summary>
	[Property, Group( "Shield" ), Title( "Arm follows the raised shield" )]
	public bool ShieldArmFollows { get; set; } = true;

	/// <summary>Where the elbow goes when the arm bends onto the raised shield: out to the left and down, as a strapped arm does.</summary>
	[Property, Group( "Shield" ), Title( "Elbow pole (left, down)" )]
	public Vector2 ShieldElbowPole { get; set; } = new( 0.7f, 0.7f );

	GameObject _shieldProp;

	/// <summary>Where the offhand shield is right now and which way its face points (block sparks). False without a shield.</summary>
	public bool TryGetShieldWorld( out Vector3 position, out Vector3 faceNormal )
	{
		if ( _shieldProp is null || !_shieldProp.IsValid() || !_shieldProp.Enabled )
		{
			position = default;
			faceNormal = default;
			return false;
		}

		position = _shieldProp.WorldPosition;
		// Model +Z is the outward face (create_shield_wood.py).
		faceNormal = _shieldProp.WorldRotation.Up;
		return true;
	}
	ModelRenderer _shieldRenderer;
	string _shieldBuiltModelPath = string.Empty;
	/// <summary>0 = resting on the forearm, 1 = raised to the body centre. Blended per frame toward the guard state.</summary>
	float _shieldRaise01;
	PlayerEquipment _shieldEquipment;
	/// <summary>Left hand's placement in the shield's frame, sampled while the shield rests on the forearm; carried along when it raises.</summary>
	Vector3 _shieldHandOffsetLocal;
	Rotation _shieldHandRotLocal = Rotation.Identity;
	bool _shieldHandOffsetValid;
	bool _shieldIkActive;
	/// <summary>Sandbox time the current shield bash started; negative = none.</summary>
	double _shieldBashStartedAt = -1d;

	/// <summary>The shield is on this pawn as this peer sees it — F is a shield bash instead of a punch.</summary>
	internal bool HasShieldShown => _shieldProp is not null && _shieldProp.IsValid();

	/// <summary>Play the shield bash (every peer, from the same host cue as the punch).</summary>
	internal void PlayShieldBash() => _shieldBashStartedAt = Time.NowDouble;

	/// <summary>
	/// Bash envelope 0..1: up to the block pose and past it in the first 30 %, held to 45 %, back to rest by the end.
	/// </summary>
	float ShieldBashEnvelope()
	{
		if ( _shieldBashStartedAt < 0d )
			return 0f;

		var t = (float)((Time.NowDouble - _shieldBashStartedAt) / MathF.Max( 0.15f, ShieldBashSeconds ));
		if ( t >= 1f )
		{
			_shieldBashStartedAt = -1d;
			return 0f;
		}

		if ( t < 0.3f )
		{
			var u = t / 0.3f;
			return u * u * (3f - 2f * u);
		}

		if ( t < 0.45f )
			return 1f;

		var d = 1f - (t - 0.45f) / 0.55f;
		return d * d * (3f - 2f * d);
	}

	/// <summary>Offhand id as this peer sees it: local slots on host / owner, host Sync on other peers.</summary>
	string ResolvePresentationOffHandId()
	{
		_shieldEquipment ??= Components.Get<PlayerEquipment>();
		if ( _shieldEquipment is null || !_shieldEquipment.IsValid() )
			return string.Empty;

		var networked = GameObject.Network is { Active: true };
		if ( networked && GameObject.IsProxy )
			return _shieldEquipment.NetworkedOffHandResourceId ?? string.Empty;

		return _shieldEquipment.GetSlotResourceId( EquipmentSlot.OffHand );
	}

	/// <summary>The offhand profile when it carries a held model (a shield), else null.</summary>
	EquipmentProfileData ResolveOffhandShieldProfile()
	{
		var id = ResolvePresentationOffHandId();
		if ( string.IsNullOrWhiteSpace( id ) )
			return null;

		if ( !EquipmentCatalog.TryGet( id, out var profile ) || string.IsNullOrWhiteSpace( profile.HeldModel ) )
			return null;

		return profile;
	}

	/// <summary>OnPreRender, after the sword and the torch: rest the shield on the left forearm, raise it while the guard is up.</summary>
	void TickOffhandShield()
	{
		var profile = ResolveOffhandShieldProfile();
		_seatMovement ??= Components.Get<PlayerMovement>();
		if ( profile is null || !ShowOffhandShield || _hitReactionPoseActive || _ledgeMantlePoseActive
		     || _seatMovement is { IsSeated: true } || _seatMovement is { IsWingsuitDeployed: true }
		     || ResolvePresentationMainHandProfile() is { TwoHanded: true } )
		{
			DestroyOffhandShield();
			return;
		}

		EnsureOffhandShield( profile.HeldModel.Trim() );
		if ( _shieldProp is null || !_shieldProp.IsValid() )
			return;

		var body = ResolveBody();
		if ( body is null || !body.IsValid()
		     || !TryGetFirstAnimationBoneTransform( body, DemoStickLeftBoneCandidates, out var handTx ) )
			return;

		var unitsPerMeter = ResolvePawnUnitsPerMeter();
		var bodyRot = body.GameObject.WorldRotation;

		// Rest frame: strapped to the forearm. Up runs hand → elbow, the face points away from the body
		// (body-left made orthogonal to the forearm), the origin (model forearm line) sits on the arm.
		// Bones are read from the ANIMATION pose (before this frame's arm override), so the raised arm
		// can never feed back into where the shield would rest.
		var hand = handTx.Position;
		var elbow = TryGetFirstAnimationBoneTransform( body, LeftForearmBoneCandidates, out var elbowTx )
			? elbowTx.Position
			: hand + bodyRot.Up * (0.26f * unitsPerMeter);
		var forearmUp = elbow - hand;
		forearmUp = forearmUp.LengthSquared < 1e-4f ? bodyRot.Up : forearmUp.Normal;
		var outward = bodyRot.Left - forearmUp * Vector3.Dot( bodyRot.Left, forearmUp );
		outward = outward.LengthSquared < 1e-4f ? bodyRot.Forward : outward.Normal;

		var restPos = Vector3.Lerp( hand, elbow, Math.Clamp( ShieldRestAlongForearm, 0f, 1f ) )
		              + outward * (ShieldRestArmOffsetMeters * unitsPerMeter);
		// Model +X (forward) = up the forearm, model +Z (up) = outward face.
		var restRot = Rotation.LookAt( forearmUp, outward );

		// Block frame: centred on the body, square to the block basis yaw, tilted back a touch.
		var combat = ResolveCombat();
		var guardUp = combat is { IsValid: true, Enabled: true } && combat.IsGuardShown;
		var basis = combat is { IsValid: true } ? combat.GetBlockCombatBasisRotation() : bodyRot;
		var tilt = new Angles( -Math.Clamp( ShieldBlockTiltDegrees, -30f, 45f ), 0f, 0f ).ToRotation();
		var blockNormal = basis * tilt.Forward;
		// Raised, the forearm lies ACROSS the body behind the shield (elbow out left, hand toward the middle),
		// so the shield's forearm axis goes horizontal: "up" = the basis left, turned with the tilt.
		var blockUp = basis * (tilt * Vector3.Left);
		var bodyHeight = unitsPerMeter * 1.8f;
		var blockPos = GameObject.WorldPosition
		               + Vector3.Up * (bodyHeight * Math.Clamp( ShieldBlockHeightFraction, 0.3f, 0.9f ))
		               + basis.Forward * (MathF.Max( 0.1f, ShieldBlockForwardMeters ) * unitsPerMeter)
		               + basis.Left * (ShieldBlockLeftMeters * unitsPerMeter);
		var blockRot = Rotation.LookAt( blockUp, blockNormal );

		var step = Time.Delta / MathF.Max( 0.02f, ShieldRaiseSeconds );
		_shieldRaise01 = guardUp
			? MathF.Min( 1f, _shieldRaise01 + step )
			: MathF.Max( 0f, _shieldRaise01 - step );
		var t = _shieldRaise01 * _shieldRaise01 * (3f - 2f * _shieldRaise01);

		// Shield bash: drive into the block pose and on past it along the facing, then back.
		var bash = ShieldBashEnvelope();
		if ( bash > 0f )
		{
			t = MathF.Max( t, bash );
			blockPos += basis.Forward * (bash * ShieldBashForwardMeters * unitsPerMeter);
		}

		// At full rest the arm places the shield, so this is where the hand's grip on it is read.
		if ( _shieldRaise01 <= 1e-3f && bash <= 0f )
		{
			_shieldHandOffsetLocal = restRot.Inverse * (hand - restPos);
			_shieldHandRotLocal = restRot.Inverse * handTx.Rotation;
			_shieldHandOffsetValid = true;
		}

		var pos = Vector3.Lerp( restPos, blockPos, t );
		var rot = Rotation.Slerp( restRot, blockRot, t );
		_shieldProp.WorldPosition = pos;
		_shieldProp.WorldRotation = rot;
		_shieldProp.LocalScale = Vector3.One;

		// Raised: the shield now places the arm — the hand goes where it sat on the strap at rest.
		if ( ShieldArmFollows && _shieldHandOffsetValid && t > 0.01f )
		{
			SolveShieldArm( body, basis, pos + rot * _shieldHandOffsetLocal, rot * _shieldHandRotLocal );
			_shieldIkActive = true;
		}
		else
			ClearShieldArmIk( body );
	}

	/// <summary>
	/// Two-bone IK for the left arm, written as bone overrides: the upper arm and forearm swing (minimal
	/// rotation from their animated direction, so their roll is kept) until the hand reaches the target and
	/// the hand takes the target rotation; the fingers follow their overridden parent on their own. The chain
	/// is read from the ANIMATION pose (<c>TryGetBoneTransformAnimation</c>, world space) — the plain bone
	/// getters return last frame's override, which compounds frame over frame — and <c>SetBoneTransform</c>
	/// takes RENDERER-LOCAL transforms (measured 2026-11-02: a world-space write lands root-offset away).
	/// Lengths come from the live pose, the elbow goes toward <see cref="ShieldElbowPole"/>. Out-of-reach
	/// targets straighten the arm toward them.
	/// </summary>
	void SolveShieldArm( SkinnedModelRenderer body, Rotation basis, Vector3 handTarget, Rotation handRotTarget ) =>
		_ = SolveTwoBoneArm( body, "arm_upper_L", "arm_lower_L", "hand_L", handTarget,
			basis.Left * ShieldElbowPole.x + Vector3.Down * ShieldElbowPole.y, handRotTarget );

	/// <summary>
	/// The two-bone arm solve shared by the shield and the bow stance (see <see cref="SolveShieldArm"/>).
	/// <paramref name="handRotTarget"/> null = the hand keeps its animated rotation relative to the forearm.
	/// </summary>
	/// <returns>The solved hand bone (world), or null when the chain is missing.</returns>
	Transform? SolveTwoBoneArm( SkinnedModelRenderer body, string upperName, string lowerName, string handName,
		Vector3 handTarget, Vector3 pole, Rotation? handRotTarget )
	{
		var bones = body.Model?.Bones;
		if ( bones is null )
			return null;

		var upperBone = bones.GetBone( upperName );
		var lowerBone = bones.GetBone( lowerName );
		var handBone = bones.GetBone( handName );
		if ( upperBone is null || lowerBone is null || handBone is null )
			return null;

		if ( !body.TryGetBoneTransformAnimation( upperBone, out var upperTx )
		     || !body.TryGetBoneTransformAnimation( lowerBone, out var lowerTx )
		     || !body.TryGetBoneTransformAnimation( handBone, out var handTx ) )
			return null;

		var shoulder = upperTx.Position;
		var elbow = lowerTx.Position;
		var hand = handTx.Position;
		var l1 = (elbow - shoulder).Length;
		var l2 = (hand - elbow).Length;
		if ( l1 < 1e-3f || l2 < 1e-3f )
			return null;

		var toTarget = handTarget - shoulder;
		var d = Math.Clamp( toTarget.Length, MathF.Abs( l1 - l2 ) + 0.05f, l1 + l2 - 0.05f );
		if ( toTarget.LengthSquared < 1e-6f )
			return null;

		var dir = toTarget.Normal;
		var poleSide = pole - dir * Vector3.Dot( pole, dir );
		if ( poleSide.LengthSquared < 1e-4f )
			poleSide = Vector3.Down - dir * Vector3.Dot( Vector3.Down, dir );
		poleSide = poleSide.Normal;

		// Law of cosines: distance of the elbow along the shoulder→target line, and its height off it.
		var a = (l1 * l1 - l2 * l2 + d * d) / (2f * d);
		var h = MathF.Sqrt( MathF.Max( 0f, l1 * l1 - a * a ) );
		var newElbow = shoulder + dir * a + poleSide * h;
		var newHand = shoulder + dir * d;

		var newUpperRot = Rotation.FromToRotation( (elbow - shoulder).Normal, (newElbow - shoulder).Normal ) * upperTx.Rotation;
		var newLowerRot = Rotation.FromToRotation( (hand - elbow).Normal, (newHand - newElbow).Normal ) * lowerTx.Rotation;

		var newHandRot = handRotTarget ?? newLowerRot * lowerTx.Rotation.Inverse * handTx.Rotation;

		var world = body.Transform.World;
		body.SetBoneTransform( upperBone, world.ToLocal( new Transform( shoulder, newUpperRot ) ) );
		body.SetBoneTransform( lowerBone, world.ToLocal( new Transform( newElbow, newLowerRot ) ) );
		body.SetBoneTransform( handBone, world.ToLocal( new Transform( newHand, newHandRot ) ) );
		return new Transform( newHand, newHandRot );
	}

	/// <summary>First bone of the candidates, read from the animation pose (world space, before any override this frame).</summary>
	static bool TryGetFirstAnimationBoneTransform( SkinnedModelRenderer body, string[] candidates, out Transform tx )
	{
		tx = default;
		var bones = body.Model?.Bones;
		if ( bones is null )
			return false;

		for ( var i = 0; i < candidates.Length; i++ )
		{
			var bone = bones.GetBone( candidates[i] );
			if ( bone is null || !body.TryGetBoneTransformAnimation( bone, out tx ) )
				continue;
			if ( tx.Position.LengthSquared <= 1e-6f )
				continue;
			return true;
		}

		return false;
	}

	void ClearShieldArmIk( SkinnedModelRenderer body )
	{
		if ( !_shieldIkActive )
			return;

		_shieldIkActive = false;
		// No per-bone clear exists; nothing else on the pawn writes bone overrides (grep SetBoneTransform / SetBoneOverride).
		if ( body is not null && body.IsValid() )
			body.SceneModel?.ClearBoneOverrides();
	}

	void EnsureOffhandShield( string modelPath )
	{
		if ( _shieldProp is { IsValid: true } )
		{
			if ( string.Equals( _shieldBuiltModelPath, modelPath, StringComparison.OrdinalIgnoreCase ) )
				return;

			DestroyOffhandShield();
		}

		var model = Model.Load( modelPath );
		if ( model is null )
		{
			Log.Warning( $"[PlayerAnimation] shield heldModel '{modelPath}' failed to load." );
			_shieldBuiltModelPath = modelPath;
			return;
		}

		// Local presentation only — a plain child of a networked pawn replicates (see the demo stick).
		_shieldProp = new GameObject( true, ShieldObjectName );
		_shieldProp.NetworkMode = NetworkMode.Never;
		_shieldProp.Parent = GameObject;
		_shieldProp.Tags.Add( "ignore" );

		_shieldRenderer = _shieldProp.Components.Create<ModelRenderer>();
		_shieldRenderer.Model = model;
		_shieldRenderer.Tint = Color.White;
		_shieldRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		_shieldBuiltModelPath = modelPath;
		_shieldRaise01 = 0f;

		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() || child == _shieldProp )
				continue;

			if ( string.Equals( child.Name, ShieldObjectName, StringComparison.OrdinalIgnoreCase ) )
				child.Destroy();
		}
	}

	void DestroyOffhandShield()
	{
		ClearShieldArmIk( ResolveBody() );
		_shieldHandOffsetValid = false;

		if ( _shieldProp is not null && _shieldProp.IsValid() )
			_shieldProp.Destroy();

		_shieldProp = null;
		_shieldRenderer = null;
		_shieldBuiltModelPath = string.Empty;
		_shieldRaise01 = 0f;
	}
}
