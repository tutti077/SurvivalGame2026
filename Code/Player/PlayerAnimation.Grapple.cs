using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Grapple presentation (Mark 2026-11-19: "it should be connected to the players left arm ... some model that wraps
/// around the players arm ... as the anchor point moves around the player, the players arm should move to follow it").
/// <list type="bullet">
/// <item><b>Gauntlet</b>: the equipped grapple's <c>heldModel</c> (equipment_profiles.json) strapped to the LEFT forearm
/// whenever a grapple is equipped (<see cref="PlayerMovement.GrappleGearResourceId"/>, host-synced so every peer sees
/// it). Model convention (Blender/scripts/create_grapple_gear.py): +X up the forearm toward the elbow, +Z the outer
/// side, origin on the forearm centre line - no per-item angles.</item>
/// <item><b>Arm</b>: while the rope is attached the left arm is solved straight at the anchor every frame
/// (<see cref="SolveTwoBoneArm"/>, the shield's solver), so the arm sticks out on the shot and tracks the anchor
/// through a swing or a tow. Clamped to what a left arm can reach.</item>
/// <item><b>Rope + hook</b>: a chain of stretched rope-segment models from the gauntlet's muzzle to the hook head at
/// the anchor. The hook flies out on attach and reels back on release; a slack rope sags.</item>
/// </list>
/// Every peer builds its own local props (<see cref="NetworkMode.Never"/>, like the shield) from synced state. Runs
/// last in <c>OnPreRender</c>, after the shield and the bow, so its arm solve wins the left arm while a rope is out;
/// the gauntlet then follows whichever solve moved the forearm this frame.
/// </summary>
public sealed partial class PlayerAnimation
{
	const string GrappleGauntletObjectName = "grapple_gauntlet";
	const string GrappleHookObjectName = "grapple_hook";
	const string GrappleRopeObjectName = "grapple_rope";
	const string GrappleHookModelPath = "models/tools/grapple_gear/grapple_hook.vmdl";
	const string GrappleRopeModelPath = "models/tools/grapple_gear/grapple_rope_segment.vmdl";

	// The numbers below mirror Blender/scripts/create_grapple_gear.py (MUZZLE, HOOK_LENGTH, ROPE_RADIUS).
	/// <summary>Where the rope leaves the gauntlet: this far toward the hand from its origin (m).</summary>
	const float GrappleMuzzleTowardHandMeters = 0.128f;
	/// <summary>... and this far out from the forearm centre line (m).</summary>
	const float GrappleMuzzleOutMeters = 0.076f;
	const float GrappleHookLengthMeters = 0.15f;
	const float GrappleRopeModelRadiusMeters = 0.02f;
	const int GrappleRopeMaxSegments = 24;

	[Property, Group( "Grapple Gear" ), Title( "Show grapple gauntlet" )]
	public bool ShowGrappleGear { get; set; } = true;

	/// <summary>Where along the left forearm the gauntlet's centre sits: 0 = at the hand, 1 = at the elbow.</summary>
	[Property, Group( "Grapple Gear" ), Title( "Along forearm (0=hand, 1=elbow)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float GrappleGauntletAlongForearm { get; set; } = 0.5f;

	/// <summary>Gauntlet size on the arm. The model is life size; a little over reads better at camera distance.</summary>
	[Property, Group( "Grapple Gear" ), Title( "Gauntlet scale" ), Range( 0.5f, 2f ), Step( 0.05f )]
	public float GrappleGauntletScale { get; set; } = 1.15f;

	[Property, Group( "Grapple Gear" ), Title( "Rope thickness (m)" ), Range( 0.01f, 0.12f ), Step( 0.005f )]
	public float GrappleRopeThicknessMeters { get; set; } = 0.025f;

	/// <summary>Hook head size. Bigger than life so the end of the rope reads from 30 m.</summary>
	[Property, Group( "Grapple Gear" ), Title( "Hook scale" ), Range( 0.5f, 4f ), Step( 0.1f )]
	public float GrappleHookScale { get; set; } = 1.6f;

	/// <summary>How fast the hook leaves the gauntlet on attach (the rope is already holding; this is only what you see).</summary>
	[Property, Group( "Grapple Gear" ), Title( "Shot speed (m/s)" ), Range( 20f, 600f ), Step( 10f )]
	public float GrappleShotSpeedMeters { get; set; } = 220f;

	[Property, Group( "Grapple Gear" ), Title( "Reel-in time (s)" ), Range( 0.02f, 0.6f ), Step( 0.01f )]
	public float GrappleReelInSeconds { get; set; } = 0.14f;

	[Property, Group( "Grapple Gear" ), Title( "Arm points at the anchor" )]
	public bool GrappleArmFollows { get; set; } = true;

	/// <summary>Seconds for the arm to come up onto the rope line (and half as fast to drop back).</summary>
	[Property, Group( "Grapple Gear" ), Title( "Arm raise time (s)" ), Range( 0.02f, 0.5f ), Step( 0.01f )]
	public float GrappleArmRaiseSeconds { get; set; } = 0.08f;

	/// <summary>How far round from "forward and to the left" the left arm may point before it stops following (degrees).</summary>
	[Property, Group( "Grapple Gear" ), Title( "Arm reach cone (deg)" ), Range( 60f, 180f ), Step( 5f )]
	public float GrappleArmReachDegrees { get; set; } = 125f;

	GameObject _grappleGauntlet;
	string _grappleGauntletModelPath = string.Empty;
	string _grappleGearCachedId;
	string _grappleGearCachedModel = string.Empty;
	GameObject _grappleHook;
	readonly List<GameObject> _grappleRopeSegments = new();
	/// <summary>0 = arm on its animation, 1 = pointing down the rope.</summary>
	float _grappleAim01;
	bool _grappleIkActive;
	/// <summary>0 = hook in the gauntlet, 1 = hook at the anchor.</summary>
	float _grappleRopeOut01;
	Vector3 _grappleAnchor;
	bool _grappleAnchorValid;
	float _grappleSag;
	Vector3 _grappleMuzzleWorld;
	bool _grappleMuzzleValid;

	/// <summary>Left forearm as an arm solve left it this frame: where the elbow went and how the forearm turned.</summary>
	double _leftArmSolveTime = -1d;
	Vector3 _leftArmSolveAnimElbow;
	Vector3 _leftArmSolveElbow;
	Rotation _leftArmSolveDelta = Rotation.Identity;

	/// <summary>Where the rope leaves the gauntlet (world), as placed last frame. False without a gauntlet on the arm.</summary>
	public bool TryGetGrappleMuzzleWorld( out Vector3 muzzle )
	{
		muzzle = _grappleMuzzleWorld;
		return _grappleMuzzleValid && _grappleGauntlet is { IsValid: true };
	}

	/// <summary>The equipped grapple's held model, looked up once per id change.</summary>
	string ResolveGrappleGearModel( PlayerMovement move )
	{
		var id = move.GrappleGearResourceId ?? string.Empty;
		if ( string.Equals( id, _grappleGearCachedId, StringComparison.Ordinal ) )
			return _grappleGearCachedModel;

		_grappleGearCachedId = id;
		_grappleGearCachedModel = id.Length > 0 && EquipmentCatalog.TryGet( id, out var profile ) && !string.IsNullOrWhiteSpace( profile.HeldModel )
			? profile.HeldModel.Trim()
			: string.Empty;
		return _grappleGearCachedModel;
	}

	/// <summary>
	/// Before the first-person / shield / bow solves: once the arm has fully dropped off the rope line, take the
	/// grapple's bone overrides off. Clearing here costs the other solvers nothing - they write again right after.
	/// </summary>
	void TickGrappleArmRelease()
	{
		if ( !_grappleIkActive || _grappleAim01 > 1e-3f )
			return;

		_grappleIkActive = false;
		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
			body.SceneModel?.ClearBoneOverrides();
	}

	/// <summary>OnPreRender, last: gauntlet on the forearm, arm down the rope, rope and hook.</summary>
	void TickGrappleGear()
	{
		_seatMovement ??= Components.Get<PlayerMovement>();
		var move = _seatMovement;
		var body = ResolveBody();
		var modelPath = move is not null && move.IsValid() ? ResolveGrappleGearModel( move ) : string.Empty;
		if ( modelPath.Length == 0 || !ShowGrappleGear || body is null || !body.IsValid() || move.IsWingsuitDeployed )
		{
			_grappleAim01 = 0f;
			DestroyGrappleGear();
			return;
		}

		EnsureGrappleGear( modelPath );
		if ( _grappleGauntlet is null || !_grappleGauntlet.IsValid() )
			return;

		var attached = move.GrappleAttached && !move.IsLedgePulling;
		if ( attached )
		{
			_grappleAnchor = move.GrappleRopeAnchorWorld;
			_grappleAnchorValid = true;
		}

		// Arm: up onto the rope line while attached, back to the animation after.
		var aimWanted = attached && GrappleArmFollows && !_hitReactionPoseActive && !_ledgeMantlePoseActive && !move.IsSeated;
		var raise = MathF.Max( 0.02f, GrappleArmRaiseSeconds );
		_grappleAim01 = Math.Clamp( _grappleAim01 + (aimWanted ? Time.Delta / raise : -Time.Delta / (raise * 2f)), 0f, 1f );
		if ( _grappleAim01 > 1e-3f && _grappleAnchorValid )
			SolveGrappleArm( body, _grappleAnchor, _grappleAim01 * _grappleAim01 * (3f - 2f * _grappleAim01) );

		if ( !PlaceGrappleGauntlet( body, out var muzzle, out var muzzleDir ) )
			return;

		_grappleMuzzleWorld = muzzle;
		_grappleMuzzleValid = true;
		PlaceGrappleRope( move, attached, muzzle, muzzleDir );
	}

	/// <summary>Left arm straight at the anchor, blended in by <paramref name="weight"/> from the animated hand.</summary>
	void SolveGrappleArm( SkinnedModelRenderer body, Vector3 anchor, float weight )
	{
		var bones = body.Model?.Bones;
		if ( bones is null )
			return;

		var upperBone = bones.GetBone( "arm_upper_L" );
		var lowerBone = bones.GetBone( "arm_lower_L" );
		var handBone = bones.GetBone( "hand_L" );
		if ( upperBone is null || lowerBone is null || handBone is null
		     || !body.TryGetBoneTransformAnimation( upperBone, out var upperTx )
		     || !body.TryGetBoneTransformAnimation( lowerBone, out var lowerTx )
		     || !body.TryGetBoneTransformAnimation( handBone, out var handTx ) )
			return;

		var shoulder = upperTx.Position;
		var toAnchor = anchor - shoulder;
		if ( toAnchor.LengthSquared < 1f )
			return;

		// What a left arm can do: anywhere within the cone around "forward and out to the left". Past that the arm
		// stops at the edge of the cone instead of folding through the back.
		var bodyRot = body.GameObject.WorldRotation;
		var comfortable = (bodyRot.Forward * 0.6f + bodyRot.Left * 0.75f + bodyRot.Up * 0.15f).Normal;
		var dir = toAnchor.Normal;
		var maxAngle = Math.Clamp( GrappleArmReachDegrees, 60f, 180f );
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( dir, comfortable ), -1f, 1f ) ) * (180f / MathF.PI);
		if ( angle > maxAngle )
		{
			var axis = Vector3.Cross( comfortable, dir );
			axis = axis.LengthSquared < 1e-6f ? bodyRot.Up : axis.Normal;
			dir = Rotation.FromAxis( axis, maxAngle ) * comfortable;
		}

		var reach = ((lowerTx.Position - shoulder).Length + (handTx.Position - lowerTx.Position).Length) * 0.97f;
		var target = Vector3.Lerp( handTx.Position, shoulder + dir * reach, Math.Clamp( weight, 0f, 1f ) );
		// Elbow out to the left and down, as a braced arm holds a line.
		_ = SolveTwoBoneArm( body, "arm_upper_L", "arm_lower_L", "hand_L", target, bodyRot.Left * 0.5f + Vector3.Down * 0.85f, null );
		_grappleIkActive = true;
	}

	/// <summary>
	/// Gauntlet onto the forearm: rest frame from the animation pose (as the shield reads it), then carried by the
	/// forearm's solve of this frame if there was one - the grapple aim, the raised shield or the bow stance.
	/// </summary>
	bool PlaceGrappleGauntlet( SkinnedModelRenderer body, out Vector3 muzzle, out Vector3 muzzleDir )
	{
		muzzle = default;
		muzzleDir = default;
		if ( !TryGetFirstAnimationBoneTransform( body, DemoStickLeftBoneCandidates, out var handTx ) )
			return false;

		var unitsPerMeter = ResolvePawnUnitsPerMeter();
		var bodyRot = body.GameObject.WorldRotation;
		var hand = handTx.Position;
		var elbow = TryGetFirstAnimationBoneTransform( body, LeftForearmBoneCandidates, out var elbowTx )
			? elbowTx.Position
			: hand + bodyRot.Up * (0.26f * unitsPerMeter);
		var forearmUp = elbow - hand;
		forearmUp = forearmUp.LengthSquared < 1e-4f ? bodyRot.Up : forearmUp.Normal;
		var outward = bodyRot.Left - forearmUp * Vector3.Dot( bodyRot.Left, forearmUp );
		outward = outward.LengthSquared < 1e-4f ? bodyRot.Forward : outward.Normal;

		var pos = Vector3.Lerp( hand, elbow, Math.Clamp( GrappleGauntletAlongForearm, 0f, 1f ) );
		// Model +X (forward) = up the forearm, model +Z (up) = the outer side.
		var rot = Rotation.LookAt( forearmUp, outward );

		if ( _leftArmSolveTime == Time.NowDouble )
		{
			pos = _leftArmSolveElbow + _leftArmSolveDelta * (pos - _leftArmSolveAnimElbow);
			rot = _leftArmSolveDelta * rot;
		}

		var scale = Math.Clamp( GrappleGauntletScale, 0.5f, 2f );
		_grappleGauntlet.WorldPosition = pos;
		_grappleGauntlet.WorldRotation = rot;
		_grappleGauntlet.LocalScale = Vector3.One * scale;

		muzzleDir = -rot.Forward;
		muzzle = pos + muzzleDir * (GrappleMuzzleTowardHandMeters * unitsPerMeter * scale)
		         + rot.Up * (GrappleMuzzleOutMeters * unitsPerMeter * scale);
		return true;
	}

	/// <summary>Hook head and rope for this frame: stowed in the barrel, flying out, holding, or reeling back.</summary>
	void PlaceGrappleRope( PlayerMovement move, bool attached, Vector3 muzzle, Vector3 muzzleDir )
	{
		var unitsPerMeter = ResolvePawnUnitsPerMeter();
		var hookScale = Math.Clamp( GrappleHookScale, 0.5f, 4f );
		var hookLength = GrappleHookLengthMeters * unitsPerMeter * hookScale;

		if ( attached && _grappleAnchorValid )
		{
			var flight = MathF.Max( 0.03f, Vector3.DistanceBetween( muzzle, _grappleAnchor ) / (MathF.Max( 20f, GrappleShotSpeedMeters ) * unitsPerMeter) );
			_grappleRopeOut01 = MathF.Min( 1f, _grappleRopeOut01 + Time.Delta / flight );
		}
		else
			_grappleRopeOut01 = MathF.Max( 0f, _grappleRopeOut01 - Time.Delta / MathF.Max( 0.02f, GrappleReelInSeconds ) );

		if ( _grappleRopeOut01 <= 0f || !_grappleAnchorValid )
		{
			// Stowed: the shaft sits in the barrel, prongs just proud of the muzzle.
			_grappleAnchorValid = attached;
			_grappleSag = 0f;
			_grappleHook.WorldRotation = Rotation.LookAt( muzzleDir, Vector3.Up );
			_grappleHook.WorldPosition = muzzle - muzzleDir * (hookLength * 0.55f);
			_grappleHook.LocalScale = Vector3.One * (hookScale * 0.6f);
			SetGrappleRopeSegments( 0 );
			return;
		}

		// The hook's point sits on the anchor; its rope eye is one hook length back down the rope.
		var toAnchor = _grappleAnchor - muzzle;
		var ropeDir = toAnchor.LengthSquared < 1e-4f ? muzzleDir : toAnchor.Normal;
		var eyeAtAnchor = _grappleAnchor - ropeDir * hookLength;
		var eye = Vector3.Lerp( muzzle, eyeAtAnchor, _grappleRopeOut01 );
		_grappleHook.WorldRotation = Rotation.LookAt( ropeDir, Vector3.Up );
		_grappleHook.WorldPosition = eye;
		_grappleHook.LocalScale = Vector3.One * (hookScale * (0.6f + 0.4f * _grappleRopeOut01));

		// Slack: rope paid out past the straight distance hangs in a shallow curve (parabola of that arc length).
		var span = Vector3.DistanceBetween( muzzle, eye );
		var slack = attached && _grappleRopeOut01 >= 1f
			? MathF.Max( 0f, move.GrappleRopeLengthEngine - Vector3.DistanceBetween( GameObject.WorldPosition, _grappleAnchor ) )
			: 0f;
		var wantSag = slack > 2f ? MathF.Min( MathF.Sqrt( 0.375f * span * slack ), span * 0.3f ) : 0f;
		_grappleSag += (wantSag - _grappleSag) * Math.Clamp( Time.Delta * 10f, 0f, 1f );

		var thickness = Math.Clamp( GrappleRopeThicknessMeters, 0.01f, 0.12f ) * unitsPerMeter;
		var count = Math.Clamp( (int)MathF.Ceiling( span / (1.5f * unitsPerMeter) ), _grappleSag > 1f ? 10 : 1, GrappleRopeMaxSegments );
		SetGrappleRopeSegments( count );
		var from = muzzle;
		for ( var i = 0; i < count; i++ )
		{
			var t = (i + 1f) / count;
			var to = Vector3.Lerp( muzzle, eye, t ) + Vector3.Down * (_grappleSag * 4f * t * (1f - t));
			PlaceGrappleRopeSegment( _grappleRopeSegments[i], from, to, thickness );
			from = to;
		}
	}

	/// <summary>One rope segment model (1 m along +X from its origin) stretched from <paramref name="a"/> to <paramref name="b"/>.</summary>
	void PlaceGrappleRopeSegment( GameObject segment, Vector3 a, Vector3 b, float thickness )
	{
		var dir = b - a;
		var length = MathF.Max( 0.01f, dir.Length );
		var unitsPerMeter = ResolvePawnUnitsPerMeter();
		var across = thickness / (GrappleRopeModelRadiusMeters * 2f * unitsPerMeter);
		segment.WorldPosition = a;
		segment.WorldRotation = Rotation.LookAt( dir / length, MathF.Abs( dir.z ) > length * 0.99f ? Vector3.Forward : Vector3.Up );
		segment.LocalScale = new Vector3( length / unitsPerMeter, across, across );
	}

	/// <summary>Show the first <paramref name="count"/> rope segments, building them as needed; hide the rest.</summary>
	void SetGrappleRopeSegments( int count )
	{
		while ( _grappleRopeSegments.Count < count )
		{
			var model = Model.Load( GrappleRopeModelPath );
			var segment = new GameObject( true, GrappleRopeObjectName );
			segment.NetworkMode = NetworkMode.Never;
			segment.Parent = GameObject;
			segment.Tags.Add( "ignore" );
			var renderer = segment.Components.Create<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_grappleRopeSegments.Add( segment );
		}

		for ( var i = 0; i < _grappleRopeSegments.Count; i++ )
		{
			var segment = _grappleRopeSegments[i];
			if ( segment.IsValid() && segment.Enabled != i < count )
				segment.Enabled = i < count;
		}
	}

	void EnsureGrappleGear( string modelPath )
	{
		if ( _grappleGauntlet is { IsValid: true } )
		{
			if ( string.Equals( _grappleGauntletModelPath, modelPath, StringComparison.OrdinalIgnoreCase ) )
				return;

			DestroyGrappleGear();
		}

		_grappleGauntletModelPath = modelPath;
		var model = Model.Load( modelPath );
		if ( model is null )
		{
			Log.Warning( $"[PlayerAnimation] grapple heldModel '{modelPath}' failed to load." );
			return;
		}

		// Stale copies from a hotload.
		foreach ( var child in GameObject.Children )
		{
			if ( child is { IsValid: true } && (child.Name == GrappleGauntletObjectName || child.Name == GrappleHookObjectName || child.Name == GrappleRopeObjectName) )
				child.Destroy();
		}

		_grappleGauntlet = CreateGrappleProp( GrappleGauntletObjectName, model );
		_grappleHook = CreateGrappleProp( GrappleHookObjectName, Model.Load( GrappleHookModelPath ) );
	}

	/// <summary>Local presentation only - a plain child of a networked pawn replicates (see the shield).</summary>
	GameObject CreateGrappleProp( string name, Model model )
	{
		var go = new GameObject( true, name );
		go.NetworkMode = NetworkMode.Never;
		go.Parent = GameObject;
		go.Tags.Add( "ignore" );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.Tint = Color.White;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		return go;
	}

	void DestroyGrappleGear()
	{
		if ( _grappleGauntlet is { IsValid: true } )
			_grappleGauntlet.Destroy();
		if ( _grappleHook is { IsValid: true } )
			_grappleHook.Destroy();
		for ( var i = 0; i < _grappleRopeSegments.Count; i++ )
		{
			if ( _grappleRopeSegments[i].IsValid() )
				_grappleRopeSegments[i].Destroy();
		}

		_grappleRopeSegments.Clear();
		_grappleGauntlet = null;
		_grappleHook = null;
		_grappleGauntletModelPath = string.Empty;
		_grappleMuzzleValid = false;
		_grappleRopeOut01 = 0f;
		_grappleAnchorValid = false;
	}
}
