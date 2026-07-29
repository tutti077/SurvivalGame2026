using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Single owner for pawn citizen animation: hold poses, attack triggers, demo props.
/// Combat / equipment request intents; this component applies animgraph + presentation.
/// </summary>
[Title( "Player Animation" )]
public sealed class PlayerAnimation : Component
{
	public enum HoldPose : byte
	{
		None = 0,
		/// <summary>Citizen <c>holdtype=melee_weapons</c> (2H idle + attack graph).</summary>
		MeleeTwoHand = 1,
	}

	static readonly string[] DemoStickRightBoneCandidates =
	{
		"hold_R",
		"hand_R",
		"hold_r",
		"hand_r",
		"RightHand",
	};

	static readonly string[] DemoStickLeftBoneCandidates =
	{
		"hold_L",
		"hand_L",
		"hold_l",
		"hand_l",
		"LeftHand",
	};

	const string DemoStickModelPath = "models/dev/box.vmdl";

	[Property, Group( "Animation" ), Title( "Play melee swing animation" )]
	public bool PlayMeleeSwingAnimation { get; set; } = true;

	/// <summary>Playback multiplier on the body during left/right swings (&lt;1 = slower).</summary>
	[Property, Group( "Animation" ), Title( "Lateral swing playback rate" ), Range( 0.5f, 1f ), Step( 0.05f )]
	public float MeleeLateralSwingPlaybackRate { get; set; } = 0.85f;

	[Property, Group( "Animation" ), Title( "Lateral swing slow duration (s)" ), Range( 0.2f, 2f ), Step( 0.05f )]
	public float MeleeLateralSwingSlowSeconds { get; set; } = 0.9f;

	[Property, Group( "Melee demo stick" ), Title( "Show demo stick" )]
	public bool ShowMeleeDemoStick { get; set; } = true;

	[Property, Group( "Melee demo stick" ), Title( "Length (m)" ), Range( 0.4f, 2.5f ), Step( 0.01f )]
	public float MeleeDemoStickLengthMeters { get; set; } = 1.26f;

	[Property, Group( "Melee demo stick" ), Title( "Thickness (m)" ), Range( 0.02f, 0.2f ), Step( 0.005f )]
	public float MeleeDemoStickThicknessMeters { get; set; } = 0.045f;

	/// <summary>
	/// Where along the blade the hands sit: 0 = hilt (bottom / rear of long axis), 0.5 = middle.
	/// </summary>
	[Property, Group( "Melee demo stick" ), Title( "Grip along length (0=hilt)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float MeleeDemoStickGripAlongLength { get; set; } = 0f;

	/// <summary>Fine-tune after hilt snap (applied in stick local space).</summary>
	[Property, Group( "Melee demo stick" ), Title( "Local offset (m)" )]
	public Vector3 MeleeDemoStickLocalOffset { get; set; } = Vector3.Zero;

	[Property, Group( "Melee demo stick" ), Title( "Local angles" )]
	public Angles MeleeDemoStickLocalAngles { get; set; } = Angles.Zero;

	[Property, Group( "Melee demo stick" ), Title( "Tint" )]
	public Color MeleeDemoStickTint { get; set; } = new( 0.72f, 0.72f, 0.78f, 1f );

	CitizenAnimationHelper _animHelper;
	SkinnedModelRenderer _bodyRenderer;
	bool _targetsResolved;

	HoldPose _appliedHoldPose = HoldPose.None;

	bool _lateralSwingPlaybackSlowed;
	float _playbackRateSaved = 1f;
	double _playbackRateRestoreAt;

	GameObject _meleeDemoStick;
	ModelRenderer _meleeDemoStickRenderer;
	float _demoStickMeshHalfExtentX = 0.5f;

	PlayerEquippedItem _equippedItem;

	/// <summary>Last hold pose this component applied (for debugging / future callers).</summary>
	public HoldPose AppliedHoldPose => _appliedHoldPose;

	protected override void OnStart()
	{
		base.OnStart();
		_equippedItem = Components.Get<PlayerEquippedItem>();
		EnsureAnimTargets();
		ClearStuckNegativeBodyScale();
	}

	protected override void OnUpdate()
	{
		if ( !GameObject.IsValid() )
			return;

		EnsureAnimTargets();
		TickHoldPose();
		TickLateralSwingPlaybackRestore();
	}

	protected override void OnPreRender()
	{
		base.OnPreRender();
		if ( !GameObject.IsValid() )
			return;

		EnsureAnimTargets();
		TickHoldPose();
		TickMeleeDemoStickTransform();
	}

	protected override void OnDestroy()
	{
		RestoreLateralSwingPlaybackRate();
		DestroyMeleeDemoStick();
		ClearStuckNegativeBodyScale();
		base.OnDestroy();
	}

	/// <summary>Host/local: play melee swing anim; optionally broadcast for remotes.</summary>
	public void PlayMeleeSwingAttack( byte attackType, bool broadcastFromHost = false )
	{
		if ( !PlayMeleeSwingAnimation || !GameObject.IsValid() )
			return;

		ApplyMeleeSwingAttackLocal( attackType );

		if ( !broadcastFromHost )
			return;

		if ( GameObject.Network is { Active: true } && Networking.IsHost )
			RpcBroadcastMeleeSwingAttack( attackType );
	}

	void ApplyMeleeSwingAttackLocal( byte attackType )
	{
		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );
		ApplyLateralSwingPlaybackSlow( attackType );

		var body = ResolveBody();
		if ( body is null )
			return;

		// Citizen only ships Melee_Weapons_2H_Attack_01 (rightward). No engine L/R mirror —
		// a real left clip needs to come from Blender / animgraph.
		body.Set( "holdtype_attack", 0f );
		body.Set( "b_attack", true );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastMeleeSwingAttack( byte attackType )
	{
		if ( Networking.IsHost )
			return;

		ApplyMeleeSwingAttackLocal( attackType );
	}

	HoldPose ResolveDesiredHoldPose()
	{
		_equippedItem ??= Components.Get<PlayerEquippedItem>();
		if ( _equippedItem is not null && _equippedItem.IsValid()
		     && _equippedItem.HasAction( EquippedItemActions.PrimaryMelee )
		     && PlayMeleeSwingAnimation )
			return HoldPose.MeleeTwoHand;

		return HoldPose.None;
	}

	void TickHoldPose()
	{
		ApplyHoldPose( ResolveDesiredHoldPose() );
	}

	void ApplyHoldPose( HoldPose pose )
	{
		if ( pose == HoldPose.MeleeTwoHand )
		{
			ApplyMeleeTwoHandHold();
			_appliedHoldPose = HoldPose.MeleeTwoHand;
			return;
		}

		if ( _appliedHoldPose == HoldPose.MeleeTwoHand || pose == HoldPose.None )
			ClearMeleeTwoHandHold();

		_appliedHoldPose = HoldPose.None;
	}

	void ApplyMeleeTwoHandHold()
	{
		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			_animHelper.HoldType = CitizenAnimationHelper.HoldTypes.Swing;
			_animHelper.Handedness = CitizenAnimationHelper.Hand.Both;
			_animHelper.IsWeaponLowered = false;
		}

		var body = ResolveBody();
		if ( body is null )
			return;

		body.Set( "holdtype", (int)CitizenAnimationHelper.HoldTypes.Swing );
		body.Set( "holdtype_handedness", (int)CitizenAnimationHelper.Hand.Both );
		body.Set( "holdtype_pose", 0f );
		body.Set( "b_weapon_lower", false );

		EnsureMeleeDemoStick();
	}

	void ClearMeleeTwoHandHold()
	{
		RestoreLateralSwingPlaybackRate();
		DestroyMeleeDemoStick();

		if ( _animHelper is not null && _animHelper.IsValid() )
			_animHelper.HoldType = CitizenAnimationHelper.HoldTypes.None;

		var body = ResolveBody();
		if ( body is null )
			return;

		body.Set( "holdtype", (int)CitizenAnimationHelper.HoldTypes.None );
	}

	void EnsureMeleeDemoStick()
	{
		if ( !ShowMeleeDemoStick )
		{
			DestroyMeleeDemoStick();
			return;
		}

		if ( _meleeDemoStick is not null && _meleeDemoStick.IsValid() )
			return;

		_meleeDemoStick = new GameObject( true, "melee_demo_stick" );
		_meleeDemoStick.Parent = GameObject;
		_meleeDemoStick.Tags.Add( "ignore" );

		_meleeDemoStickRenderer = _meleeDemoStick.Components.Create<ModelRenderer>();
		var model = Model.Load( DemoStickModelPath );
		_meleeDemoStickRenderer.Model = model;
		_meleeDemoStickRenderer.Tint = MeleeDemoStickTint;
		_meleeDemoStickRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		_demoStickMeshHalfExtentX = ResolveMeshHalfExtentAlongX( model );
	}

	static float ResolveMeshHalfExtentAlongX( Model model )
	{
		if ( model is null )
			return 0.5f;

		var size = model.Bounds.Size;
		var half = MathF.Abs( size.x ) * 0.5f;
		if ( half < 1e-4f )
			half = 0.5f;
		return half;
	}

	void DestroyMeleeDemoStick()
	{
		if ( _meleeDemoStick is not null && _meleeDemoStick.IsValid() )
			_meleeDemoStick.Destroy();

		_meleeDemoStick = null;
		_meleeDemoStickRenderer = null;
	}

	void TickMeleeDemoStickTransform()
	{
		if ( !ShowMeleeDemoStick || ResolveDesiredHoldPose() != HoldPose.MeleeTwoHand )
		{
			DestroyMeleeDemoStick();
			return;
		}

		EnsureMeleeDemoStick();
		if ( _meleeDemoStick is null || !_meleeDemoStick.IsValid() )
			return;

		if ( !TryResolveDemoStickGrip( out var gripPos, out var tipDir ) )
			return;

		var thickness = MathF.Max( 0.01f, MeleeDemoStickThicknessMeters );
		var length = MathF.Max( 0.1f, MeleeDemoStickLengthMeters );

		// Long axis = local +X after LookAt(tip). Mesh half-extent × scale = half visual blade.
		var worldRot = Rotation.LookAt( tipDir ) * MeleeDemoStickLocalAngles.ToRotation();
		var halfBlade = _demoStickMeshHalfExtentX * length;

		// GripAlongLength 0 → hands on hilt (rear); 0.5 → geometric center (old wrong look).
		var along = Math.Clamp( MeleeDemoStickGripAlongLength, 0f, 1f );
		var center = gripPos + tipDir * (halfBlade * (1f - 2f * along));

		_meleeDemoStick.WorldRotation = worldRot;
		_meleeDemoStick.LocalScale = new Vector3( length, thickness, thickness );
		_meleeDemoStick.WorldPosition = center + worldRot * MeleeDemoStickLocalOffset;

		if ( _meleeDemoStickRenderer is not null && _meleeDemoStickRenderer.IsValid() )
			_meleeDemoStickRenderer.Tint = MeleeDemoStickTint;
	}

	bool TryResolveDemoStickGrip( out Vector3 gripPos, out Vector3 tipDir )
	{
		gripPos = default;
		tipDir = Vector3.Up;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return false;

		var hasRight = TryGetFirstBoneTransform( body, DemoStickRightBoneCandidates, out var rightTx );
		var hasLeft = TryGetFirstBoneTransform( body, DemoStickLeftBoneCandidates, out var leftTx );

		if ( hasRight && hasLeft )
		{
			// Prefer the lower hand as the hilt contact.
			gripPos = rightTx.Position.z <= leftTx.Position.z ? rightTx.Position : leftTx.Position;

			tipDir = (rightTx.Rotation.Up + leftTx.Rotation.Up);
			if ( tipDir.LengthSquared < 1e-4f )
			{
				var between = rightTx.Position - leftTx.Position;
				tipDir = between.LengthSquared > 1e-4f ? between.Normal : Vector3.Up;
			}
			else
			{
				tipDir = tipDir.Normal;
			}

			if ( tipDir.Dot( Vector3.Up ) < 0f )
				tipDir = -tipDir;

			return true;
		}

		if ( hasRight )
		{
			gripPos = rightTx.Position;
			tipDir = StabilizeTipDir( rightTx.Rotation.Up );
			return true;
		}

		if ( hasLeft )
		{
			gripPos = leftTx.Position;
			tipDir = StabilizeTipDir( leftTx.Rotation.Up );
			return true;
		}

		return false;
	}

	static Vector3 StabilizeTipDir( Vector3 up )
	{
		var tipDir = up.LengthSquared < 1e-4f ? Vector3.Up : up.Normal;
		if ( tipDir.Dot( Vector3.Up ) < 0f )
			tipDir = -tipDir;
		return tipDir;
	}

	static bool TryGetFirstBoneTransform( SkinnedModelRenderer body, string[] candidates, out Transform tx )
	{
		tx = default;
		for ( var i = 0; i < candidates.Length; i++ )
		{
			if ( !body.TryGetBoneTransform( candidates[i], out tx ) )
				continue;
			if ( tx.Position.LengthSquared <= 1e-6f )
				continue;
			return true;
		}

		return false;
	}

	void ApplyLateralSwingPlaybackSlow( byte attackType )
	{
		if ( attackType is not (MeleeAttackTypes.Left or MeleeAttackTypes.Right) )
		{
			RestoreLateralSwingPlaybackRate();
			return;
		}

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		if ( !_lateralSwingPlaybackSlowed )
		{
			_playbackRateSaved = body.PlaybackRate;
			_lateralSwingPlaybackSlowed = true;
		}

		body.PlaybackRate = Math.Clamp( MeleeLateralSwingPlaybackRate, 0.5f, 1f );
		_playbackRateRestoreAt = Time.NowDouble + Math.Max( 0.2f, MeleeLateralSwingSlowSeconds );
	}

	void TickLateralSwingPlaybackRestore()
	{
		if ( !_lateralSwingPlaybackSlowed )
			return;

		if ( Time.NowDouble < _playbackRateRestoreAt )
			return;

		RestoreLateralSwingPlaybackRate();
	}

	void RestoreLateralSwingPlaybackRate()
	{
		if ( !_lateralSwingPlaybackSlowed )
			return;

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
			body.PlaybackRate = _playbackRateSaved;

		_lateralSwingPlaybackSlowed = false;
		_playbackRateSaved = 1f;
	}

	/// <summary>Clear leftover −Y body scale from older left-mirror builds.</summary>
	void ClearStuckNegativeBodyScale()
	{
		if ( _bodyRenderer is null || !_bodyRenderer.IsValid() )
			return;

		var go = _bodyRenderer.GameObject;
		if ( go is null || !go.IsValid() )
			return;

		var scale = go.LocalScale;
		if ( scale.x >= 0f && scale.y >= 0f && scale.z >= 0f )
			return;

		go.LocalScale = new Vector3( MathF.Abs( scale.x ), MathF.Abs( scale.y ), MathF.Abs( scale.z ) );
	}

	SkinnedModelRenderer ResolveBody()
	{
		if ( _animHelper is not null && _animHelper.IsValid() && _animHelper.Target is not null )
			return _animHelper.Target;

		return _bodyRenderer is not null && _bodyRenderer.IsValid() ? _bodyRenderer : null;
	}

	void EnsureAnimTargets()
	{
		if ( _targetsResolved
		     && ( (_animHelper is not null && _animHelper.IsValid())
		          || (_bodyRenderer is not null && _bodyRenderer.IsValid()) ) )
			return;

		_targetsResolved = true;
		_bodyRenderer = null;

		foreach ( var renderer in Components.GetAll<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null || !renderer.IsValid() || !renderer.Enabled )
				continue;

			_bodyRenderer = renderer;
			break;
		}

		_bodyRenderer ??= Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );

		_animHelper = Components.Get<CitizenAnimationHelper>( FindMode.EverythingInSelfAndDescendants );
		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			if ( _animHelper.Target is null && _bodyRenderer is not null )
				_animHelper.Target = _bodyRenderer;
			return;
		}

		if ( _bodyRenderer is null || !_bodyRenderer.IsValid() )
			return;

		_animHelper = _bodyRenderer.Components.Get<CitizenAnimationHelper>();
		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			if ( _animHelper.Target is null )
				_animHelper.Target = _bodyRenderer;
			return;
		}

		_animHelper = _bodyRenderer.Components.Create<CitizenAnimationHelper>();
		if ( _animHelper is not null && _animHelper.IsValid() )
			_animHelper.Target = _bodyRenderer;
	}
}
