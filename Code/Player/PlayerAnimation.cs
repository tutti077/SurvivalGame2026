using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Single owner for pawn citizen animation: hold poses, attack triggers, demo props.
/// Combat / equipment request intents; this component applies animgraph + presentation.
/// </summary>
[Title( "Player Animation" )]
public sealed partial class PlayerAnimation : Component
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

	/// <summary>
	/// Owner's screen only: past this look-up pitch the local model + held props fade out (and back in
	/// when the camera drops below the ridge again), so a steep upward camera never stares up the model.
	/// Other peers see the pawn unchanged (Tint/RenderType are not networked).
	/// </summary>
	[Property, Group( "Animation" ), Title( "Hide body on steep look-up" )]
	public bool HideBodyOnSteepLookUp { get; set; } = true;

	/// <summary>Steeper (more negative) = the camera must be lower / closer before the fade starts.</summary>
	[Property, Group( "Animation" ), Title( "Look-up hide pitch (°, negative = up)" ), Range( -89f, 0f ), Step( 1f )]
	public float HideBodyLookUpPitchDegrees { get; set; } = -35f;

	[Property, Group( "Animation" ), Title( "Look-up fade duration (s)" ), Range( 0.1f, 3f ), Step( 0.1f )]
	public float HideBodyLookUpFadeSeconds { get; set; } = 1f;

	/// <summary>0 = fully visible, 1 = fully hidden; eased toward the look-up state each frame.</summary>
	float _lookUpHideFade01;
	bool _lookUpHideApplied;

	/// <summary>Playback multiplier on the body during left/right swings (&lt;1 = slower).</summary>
	[Property, Group( "Animation" ), Title( "Lateral swing playback rate" ), Range( 0.5f, 1f ), Step( 0.05f )]
	public float MeleeLateralSwingPlaybackRate { get; set; } = 0.85f;

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

	/// <summary>
	/// Host→all peers presentation of hold pose. Inventory/equipment stays owner-private;
	/// remotes drive stick + animgraph from this instead of empty MainHand slots.
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public byte NetworkedHoldPose { get; set; }

	/// <summary>
	/// Host increments on each accepted melee swing; non-host peers play the anim when this changes.
	/// More reliable than Broadcast for host-owned pawns (presentation, not authority).
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public int NetworkedSwingCounter { get; set; }

	[Sync( SyncFlags.FromHost )]
	public byte NetworkedSwingAttackType { get; set; }

	int _lastAppliedSwingCounter;
	byte _deferredSwingAnimType;
	bool _deferSwingAnimBroadcast;

	bool _lateralSwingPlaybackSlowed;
	float _playbackRateSaved = 1f;
	double _playbackRateRestoreAt;
	/// <summary>Sandbox time until the melee attack clip presentation should be treated as finished.</summary>
	double _meleeAttackAnimBusyUntilSandbox;

	const string DemoStickObjectName = "melee_demo_stick";

	GameObject _meleeDemoStick;
	ModelRenderer _meleeDemoStickRenderer;
	float _demoStickMeshHalfExtentX = 0.5f;

	PlayerEquippedItem _equippedItem;
	PlayerCombat _combat;

	protected override void OnStart()
	{
		base.OnStart();
		_equippedItem = Components.Get<PlayerEquippedItem>();
		EnsureAnimTargets();
		ClearStuckNegativeBodyScale();
		// Don't replay a pre-join swing when Sync arrives with a non-zero counter.
		_lastAppliedSwingCounter = NetworkedSwingCounter;
	}

	protected override void OnUpdate()
	{
		if ( !GameObject.IsValid() )
			return;

		EnsureAnimTargets();
		TickSyncedSwingPresentation();
		TickHitReactionPose();
		TickLedgeMantlePose();
		TickDodgeRollPose();
		TickHoldPose();
		TickLateralSwingPlaybackRestore();
		TickMeleeSwingPresentationExpiry();
		TickLookUpBodyHide();
		TickCombatFacingPresentation( advance: true );
	}

	bool _combatFacingApplied;
	float _combatFacingYaw;

	/// <summary>How fast the model turns back to locomotion facing after combat releases it (higher = quicker hand-back).</summary>
	[Property, Group( "Animation" ), Title( "Combat facing release turn rate (°/s)" ), Range( 90f, 1440f ), Step( 30f )]
	public float CombatFacingReleaseDegreesPerSecond { get; set; } = 540f;

	/// <summary>
	/// Presentation-only combat facing: rotates the RENDERER child (never the physics root — the
	/// controller and rigidbody interpolation fight root writes, which showed as facing spazz).
	/// Runs from OnUpdate (advancing) and OnPreRender (re-apply) so it lands last before render.
	/// When combat releases the facing, the model LINGERS on the last combat yaw while standing —
	/// resetting instantly popped the model a second time at recovery — and only eases back to the
	/// root's facing once locomotion actually moves the pawn (or the root is already aligned).
	/// </summary>
	/// <summary>Instantly drop the combat-facing override/linger — a movement mode (wingsuit) owns the root now.</summary>
	public void ReleaseCombatFacingOverride()
	{
		if ( !_combatFacingApplied )
			return;

		_combatFacingApplied = false;
		var body = ResolveBody();
		if ( body is not null && body.IsValid() && body.GameObject != GameObject )
			body.GameObject.LocalRotation = Rotation.Identity;
	}

	void TickCombatFacingPresentation( bool advance )
	{
		var body = ResolveBody();
		if ( body is null || !body.IsValid() || body.GameObject == GameObject )
			return;

		// Wingsuit flight rotates the whole root (pitch/roll included) — an upright yaw override on
		// the child would fight it and pin the model sideways mid-glide.
		if ( Components.Get<PlayerMovement>() is { IsWingsuitDeployed: true } )
		{
			ReleaseCombatFacingOverride();
			return;
		}

		var combat = ResolveCombat();
		if ( combat is not null && combat.Enabled && combat.TryGetCombatFacingYaw( out var activeYaw ) )
		{
			_combatFacingYaw = activeYaw;
			_combatFacingApplied = true;
			body.GameObject.WorldRotation = new Angles( 0f, _combatFacingYaw, 0f ).ToRotation();
			return;
		}

		if ( !_combatFacingApplied )
			return;

		var rootYaw = GameObject.WorldRotation.Angles().yaw;
		var delta = NormalizeYawDeltaDegrees( rootYaw - _combatFacingYaw );

		if ( advance )
		{
			var controller = GameObject.Components.Get<PlayerController>();
			var rigidbody = Components.Get<Rigidbody>();
			var moving = ( controller is not null && controller.IsValid()
			               && controller.Velocity.WithZ( 0f ).Length > 20f )
			             || ( rigidbody is not null && rigidbody.IsValid()
			                  && rigidbody.Velocity.WithZ( 0f ).Length > 20f );

			if ( moving || MathF.Abs( delta ) < 3f )
			{
				var step = Math.Max( 30f, CombatFacingReleaseDegreesPerSecond ) * Time.Delta;
				if ( MathF.Abs( delta ) <= step )
				{
					_combatFacingApplied = false;
					body.GameObject.LocalRotation = Rotation.Identity;
					return;
				}

				_combatFacingYaw += MathF.Sign( delta ) * step;
			}
		}

		body.GameObject.WorldRotation = new Angles( 0f, _combatFacingYaw, 0f ).ToRotation();
	}

	static float NormalizeYawDeltaDegrees( float delta )
	{
		while ( delta > 180f )
			delta -= 360f;
		while ( delta < -180f )
			delta += 360f;
		return delta;
	}

	/// <summary>Actual world yaw of the Body renderer child (facing diagnostics) — null when the renderer sits on the root.</summary>
	internal float? GetBodyVisualYawDegrees()
	{
		var body = ResolveBody();
		if ( body is null || !body.IsValid() || body.GameObject == GameObject )
			return null;

		return body.GameObject.WorldRotation.Angles().yaw;
	}

	/// <summary>Owner-only, third person: fade the local model out while the camera pitches steeply upward, back in below the threshold.</summary>
	void TickLookUpBodyHide()
	{
		if ( GameObject.IsProxy )
			return;

		var controller = GameObject.Components.Get<PlayerController>();
		var isLocalOwner = controller is not null && controller.Enabled
		                   && ( GameObject.Network is not { Active: true } n
		                        || (n.Owner is null ? Networking.IsHost : n.IsOwner) );

		// Fade only when the pawn is standing on the ground AND the camera boom is actually being
		// squeezed against the ground — a steep look-up in open air (jumping, gliding, high orbit
		// distance with clearance) keeps the model visible.
		var movement = Components.Get<PlayerMovement>();
		var wantHide = HideBodyOnSteepLookUp
		               && isLocalOwner
		               && controller is { ThirdPerson: true, IsOnGround: true }
		               && movement is { CameraBumpingGround: true }
		               && controller.EyeAngles.pitch <= HideBodyLookUpPitchDegrees;

		var fadeStep = Time.Delta / Math.Clamp( HideBodyLookUpFadeSeconds, 0.05f, 10f );
		_lookUpHideFade01 = Math.Clamp( _lookUpHideFade01 + (wantHide ? fadeStep : -fadeStep), 0f, 1f );

		// Fully visible steady state: one restore pass, then idle.
		if ( _lookUpHideFade01 <= 0f )
		{
			if ( !_lookUpHideApplied )
				return;

			ApplyLookUpHideToRenderers( 1f, ModelRenderer.ShadowRenderType.On );
			_lookUpHideApplied = false;
			return;
		}

		// Mid-fade: alpha tint; fully faded: shadow-only so the mesh stops rendering entirely
		// (shadow stays). Re-asserted every frame so a held prop spawned mid-fade is caught too.
		var alpha = 1f - _lookUpHideFade01;
		var renderType = _lookUpHideFade01 >= 1f
			? ModelRenderer.ShadowRenderType.ShadowsOnly
			: ModelRenderer.ShadowRenderType.On;
		ApplyLookUpHideToRenderers( alpha, renderType );
		_lookUpHideApplied = true;
	}

	void ApplyLookUpHideToRenderers( float alpha, ModelRenderer.ShadowRenderType renderType )
	{
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null || !renderer.IsValid() )
				continue;

			if ( renderer.RenderType != renderType )
				renderer.RenderType = renderType;

			if ( MathF.Abs( renderer.Tint.a - alpha ) > 0.003f )
				renderer.Tint = renderer.Tint.WithAlpha( alpha );
		}
	}

	PlayerCombat ResolveCombat()
	{
		if ( _combat is null || !_combat.IsValid() )
			_combat = Components.Get<PlayerCombat>();

		return _combat;
	}

	protected override void OnPreRender()
	{
		base.OnPreRender();
		if ( !GameObject.IsValid() )
			return;

		EnsureAnimTargets();
		TickSyncedSwingPresentation();
		TickHitReactionPose();
		TickLedgeMantlePose();
		TickDodgeRollPose();
		TickHoldPose();
		// Facing before the stick transform so the held prop follows the rotated body this frame.
		TickCombatFacingPresentation( advance: false );
		TickMeleeDemoStickTransform();
	}

	protected override void OnDestroy()
	{
		RestoreLateralSwingPlaybackRate();
		DestroyMeleeDemoStick();
		ClearStuckNegativeBodyScale();
		base.OnDestroy();
	}

	/// <summary>
	/// Host: flush deferred swing-anim broadcasts after Rpc.Host stacks unwind.
	/// Routes through <see cref="CombatAuthority"/> (scene NetworkManager) so delivery isn't
	/// tied to the attacker object's ownership.
	/// </summary>
	public static void FlushDeferredSwingAnimBroadcasts( Scene scene )
	{
		if ( !Networking.IsHost || scene is null || !scene.IsValid() )
			return;

		var authority = CombatAuthority.Instance;
		if ( authority is null || !authority.GameObject.IsValid() )
			return;

		foreach ( var anim in scene.GetAllComponents<PlayerAnimation>() )
		{
			if ( anim is null || !anim.GameObject.IsValid() || !anim._deferSwingAnimBroadcast )
				continue;

			anim._deferSwingAnimBroadcast = false;
			authority.HostBroadcastMeleeSwingAnim( anim.GameObject.Id, anim._deferredSwingAnimType );
		}
	}

	/// <summary>Called on non-host peers from CombatAuthority Broadcast (low-latency path).</summary>
	internal void ApplyRemoteMeleeSwingAttack( byte attackType )
	{
		// Sync may have already applied this swing; don't double-fire.
		if ( NetworkedSwingCounter > 0 && NetworkedSwingCounter == _lastAppliedSwingCounter )
			return;

		if ( NetworkedSwingCounter > _lastAppliedSwingCounter )
			_lastAppliedSwingCounter = NetworkedSwingCounter;

		if ( ShouldSkipRemoteSwingRestart() )
			return;

		ApplyMeleeSwingAttackLocal( attackType, isHeavy: false );
	}

	void TickSyncedSwingPresentation()
	{
		if ( Networking.IsHost || GameObject.Network is not { Active: true } )
			return;

		if ( NetworkedSwingCounter == _lastAppliedSwingCounter )
			return;

		_lastAppliedSwingCounter = NetworkedSwingCounter;
		if ( _lastAppliedSwingCounter <= 0 )
			return;

		if ( ShouldSkipRemoteSwingRestart() )
			return;

		ApplyMeleeSwingAttackLocal( NetworkedSwingAttackType, isHeavy: false );
	}

	/// <summary>
	/// Owner already started the clip on press/release — don't re-pulse b_attack from host Sync/Broadcast
	/// (that stuttered the attack start as a second begin).
	/// </summary>
	bool ShouldSkipRemoteSwingRestart()
	{
		// Getting hit outranks a swing — never turn the graph back on mid-flail.
		if ( _hitReactionPoseActive )
			return true;

		if ( _ownerSkipNextRemoteSwingApply )
		{
			_ownerSkipNextRemoteSwingApply = false;
			return true;
		}

		// Owning client already playing this swing from windup hold / press clip.
		if ( !GameObject.IsProxy
		     && (_windupHoldActive || _meleeSwingClipFromPress || GetMeleeAttackAnimBusyRemainingSeconds() > 0.05f) )
			return true;

		return false;
	}

	void ApplyMeleeSwingAttackLocal( byte attackType, bool isHeavy = false )
	{
		EnsureAnimTargets();
		ApplyHoldPose( HoldPose.MeleeTwoHand );

		var body = ResolveBody();
		if ( body is null )
			return;

		// Citizen only ships Melee_Weapons_2H_Attack_01 (rightward). No engine L/R mirror —
		// a real left clip needs to come from Blender / animgraph.
		body.UseAnimGraph = true;
		body.Set( "holdtype_attack", 0f );
		body.Set( "b_attack", true );
		ApplySwingPlaybackRate( body, attackType, isHeavy );
		_meleeSwingClipFromPress = false;
		Components.Get<PlayerCombat>()?.LogMeleeAnimStart(
			"animgraph b_attack (Melee_Weapons_2H_Attack_01)",
			$"type={MeleeAttackTypes.Label( attackType )} heavy={isHeavy} busyUntil+={GetMeleeAttackAnimBusyRemainingSeconds():0.###}s" );
	}

	/// <summary>Seconds left in the melee attack clip window (from swing start). Used to gate chain-ready.</summary>
	public float GetMeleeAttackAnimBusyRemainingSeconds() =>
		Math.Max( 0f, (float)( _meleeAttackAnimBusyUntilSandbox - Time.NowDouble ) );

	HoldPose ResolveDesiredHoldPose()
	{
		_equippedItem ??= Components.Get<PlayerEquippedItem>();
		if ( _equippedItem is not null && _equippedItem.IsValid()
		     && _equippedItem.HasAction( EquippedItemActions.PrimaryMelee )
		     && PlayMeleeSwingAnimation )
			return HoldPose.MeleeTwoHand;

		return HoldPose.None;
	}

	/// <summary>Hold pose used for local presentation this frame (synced on remotes).</summary>
	HoldPose ResolvePresentationHoldPose()
	{
		if ( !PlayMeleeSwingAnimation )
			return HoldPose.None;

		var networked = GameObject.Network is { Active: true };

		// Host: authoritative equipment → Sync for remotes.
		if ( !networked || Networking.IsHost )
		{
			var desired = ResolveDesiredHoldPose();
			NetworkedHoldPose = (byte)desired;
			return desired;
		}

		// Owning client: local equipment (owner RPCs keep MainHand filled).
		if ( !GameObject.IsProxy )
			return ResolveDesiredHoldPose();

		// Other peers: inventory is owner-private — use host Sync.
		return (HoldPose)NetworkedHoldPose;
	}

	void TickHoldPose()
	{
		// Hit reaction / ledge mantle: the sequence owns the body and the sword is gone for the window.
		if ( _hitReactionPoseActive || _ledgeMantlePoseActive )
		{
			DestroyMeleeDemoStick();
			return;
		}

		// Combat recovery / shove sequences own the graph; still refresh melee hold for sword visibility.
		if ( _combatSequenceActive )
		{
			if ( ResolvePresentationHoldPose() == HoldPose.MeleeTwoHand )
				ApplyMeleeTwoHandHold();
			return;
		}

		ApplyHoldPose( ResolvePresentationHoldPose() );
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
		ApplyMeleeTwoHandHold( includeDemoStick: true );
	}

	/// <param name="includeDemoStick">
	/// False during combat recovery sequences so we don't spawn a second box-sword on top of real equipment.
	/// </param>
	void ApplyMeleeTwoHandHold( bool includeDemoStick )
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

		if ( includeDemoStick )
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

	bool _combatSequenceActive;
	string _activeCombatSequenceName;
	string _lastClearedCombatSequenceName;
	double _combatSequenceClearedAtSandbox;

	public bool IsPlayingCombatSequence( string sequenceName ) =>
		_combatSequenceActive
		&& !string.IsNullOrEmpty( sequenceName )
		&& string.Equals( _activeCombatSequenceName, sequenceName, StringComparison.OrdinalIgnoreCase );

	/// <summary>True when the active combat sequence has reached its end (non-looping).</summary>
	public bool IsCombatSequenceFinished()
	{
		if ( !_combatSequenceActive )
			return true;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return true;

		if ( body.Sequence.IsFinished )
			return true;

		var duration = body.Sequence.Duration;
		return duration > 1e-4f && body.Sequence.Time >= duration - 1e-3f;
	}

	/// <summary>Length of the active combat sequence clip, or 0 if none.</summary>
	public float GetActiveCombatSequenceDurationSeconds()
	{
		if ( !_combatSequenceActive )
			return 0f;

		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return 0f;

		return Math.Max( 0f, body.Sequence.Duration );
	}

	/// <summary>
	/// Play a named citizen sequence (recovery / shove). Keeps melee hold + demo stick so the sword stays visible.
	/// Aborts any in-flight animgraph attack clip first so it cannot keep playing "in the background"
	/// under the sequence or resume mid-swing when the graph returns.
	/// </summary>
	/// <param name="forceRestart">True for a new shove/recovery — bypass post-clear suppress and restart Time=0.</param>
	public void PlayCombatSequencePose( string sequenceName, bool keepMeleeSwordVisible, bool forceRestart = false )
	{
		if ( string.IsNullOrWhiteSpace( sequenceName ) || !GameObject.IsValid() )
			return;

		// Clear→Apply / Sync race at recovery end used to restart the same clip (second punch).
		// Intentional new shove must forceRestart — otherwise spam F lunges with no anim for ~0.35s.
		if ( !forceRestart
		     && !_combatSequenceActive
		     && Time.NowDouble - _combatSequenceClearedAtSandbox < 0.35f
		     && string.Equals( _lastClearedCombatSequenceName, sequenceName, StringComparison.OrdinalIgnoreCase ) )
		{
			Components.Get<PlayerCombat>()?.LogShoveAnimIfPunch( sequenceName,
				$"SUPPRESS Play after clear ({Time.NowDouble - _combatSequenceClearedAtSandbox:0.000}s)" );
			return;
		}

		if ( forceRestart )
		{
			_lastClearedCombatSequenceName = null;
			_combatSequenceClearedAtSandbox = 0;
		}

		EnsureAnimTargets();
		var body = ResolveBody();
		if ( body is null )
			return;

		if ( !_combatSequenceActive )
		{
			// Kill Melee_Weapons_2H_Attack_01 while the graph is still active, then freeze to a sequence.
			AbortMeleeAttackAnimClip( "enter sequence" );
			_combatSequenceActive = true;
		}

		body.UseAnimGraph = false;
		body.Sequence.Looping = false;
		var sameName = string.Equals( _activeCombatSequenceName, sequenceName, StringComparison.OrdinalIgnoreCase );
		if ( forceRestart || !sameName )
		{
			_activeCombatSequenceName = sequenceName;
			body.Sequence.Name = sequenceName;
			body.Sequence.Time = 0f;
			body.Sequence.Looping = false;
			Components.Get<PlayerCombat>()?.LogMeleeAnimStart( $"sequence {sequenceName}",
				$"UseAnimGraph=false Looping=false force={forceRestart} dur={body.Sequence.Duration:0.###}s" );
			Components.Get<PlayerCombat>()?.LogShoveAnimIfPunch( sequenceName,
				$"ACTIVATE Sequence.Time=0 force={forceRestart}" );
		}

		if ( keepMeleeSwordVisible )
			ApplyMeleeTwoHandHold( includeDemoStick: false );
	}

	/// <summary>Keep an already-playing recovery clip alive without restarting it from time 0.</summary>
	public void MaintainCombatSequencePose( string sequenceName, bool keepMeleeSwordVisible )
	{
		if ( !IsPlayingCombatSequence( sequenceName ) )
		{
			PlayCombatSequencePose( sequenceName, keepMeleeSwordVisible );
			return;
		}

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			body.UseAnimGraph = false;
			body.Sequence.Looping = false;
		}

		if ( keepMeleeSwordVisible )
			ApplyMeleeTwoHandHold( includeDemoStick: false );
	}

	public void ClearCombatSequencePose()
	{
		var hadSequence = _combatSequenceActive || HasBodyCombatSequencePose();
		if ( !hadSequence )
		{
			ForceRestoreLocomotionGraph();
			return;
		}

		var stopped = _activeCombatSequenceName;
		_combatSequenceActive = false;
		_activeCombatSequenceName = null;
		_lastClearedCombatSequenceName = stopped;
		_combatSequenceClearedAtSandbox = Time.NowDouble;

		if ( !string.IsNullOrEmpty( stopped ) )
			Components.Get<PlayerCombat>()?.LogMeleeAnimStopIfAny( $"cleared sequence {stopped}" );

		// If a swing clip is presenting (press windup, release, or a remote-applied swing), only drop the
		// sequence — do NOT kill b_attack. Wiping it left the sweep drawing arcs with no animation.
		// Entering a sequence still aborts the swing explicitly (see PlayCombatSequencePose).
		var preserveSwing = HasActiveMeleeSwingPresentation;

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			body.UseAnimGraph = false;
			if ( !string.IsNullOrEmpty( body.Sequence.Name ) )
			{
				body.Sequence.Name = null;
				body.Sequence.Time = 0f;
			}

			body.UseAnimGraph = true;

			if ( !preserveSwing )
			{
				body.Set( "b_attack", false );
				body.Set( "holdtype_attack", 0f );
				body.Set( "b_weapon_lower", false );
				Components.Get<PlayerCombat>()?.LogMeleeAnimStart( "animgraph melee idle hold (post-recovery reset)",
					"b_attack=false seqClearedBeforeGraphOn UseAnimGraph=true" );
			}
			else
			{
				Components.Get<PlayerCombat>()?.LogMeleeAnimStart( "animgraph keep swing after sequence clear",
					"preserved press/windup clip UseAnimGraph=true" );
			}
		}

		if ( !preserveSwing )
		{
			RestoreLateralSwingPlaybackRate();
			_meleeAttackAnimBusyUntilSandbox = 0;
			ClearMeleeSwingClipFromPressFlag();
		}
	}

	/// <summary>Hard reset to citizen locomotion — call when a pose ends even if flags desynced.</summary>
	public void ForceRestoreLocomotionGraph()
	{
		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		if ( !string.IsNullOrEmpty( body.Sequence.Name ) )
		{
			body.UseAnimGraph = false;
			body.Sequence.Name = null;
			body.Sequence.Time = 0f;
		}

		body.UseAnimGraph = true;
		_combatSequenceActive = false;
		_activeCombatSequenceName = null;
	}

	/// <summary>
	/// Exit a UseAnimGraph=false combat sequence into standing locomotion.
	/// Clearing the sequence alone leaves the last flail bone pose frozen until the graph is nudged.
	/// </summary>
	public void ExitCombatSequenceToLocomotion()
	{
		// A swing clip started on press/release owns b_attack and the playback rate. Clearing a recovery
		// sequence must never reset it to idle: that killed the animation while the sweep kept drawing arcs.
		var preserveSwing = HasActiveMeleeSwingPresentation;
		if ( preserveSwing && !_combatSequenceActive && !HasBodyCombatSequencePose() )
		{
			var swingBody = ResolveBody();
			if ( swingBody is not null && swingBody.IsValid() )
				swingBody.UseAnimGraph = true;
			return;
		}

		ClearCombatSequencePose();
		ForceRestoreLocomotionGraph();

		EnsureAnimTargets();
		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			// Some Sequence.Name clears ignore null — force empty then graph on.
			body.UseAnimGraph = false;
			body.Sequence.Name = string.Empty;
			body.Sequence.Time = 0f;
			body.Sequence.Looping = false;
			body.UseAnimGraph = true;
			if ( !preserveSwing )
			{
				body.PlaybackRate = 1f;
				ResetMeleeAttackAnimGraphToIdle( body );
			}

			body.Set( "b_grounded", true );
			body.Set( "b_swim", false );
			body.Set( "b_climbing", false );
			body.Set( "b_noclip", false );
			body.Set( "duck", 0f );
		}

		if ( _animHelper is not null && _animHelper.IsValid() )
		{
			var controller = Components.Get<PlayerController>();
			_animHelper.IsGrounded = controller is null || !controller.IsValid() || controller.IsOnGround;
			_animHelper.IsSwimming = false;
			_animHelper.IsClimbing = false;
			_animHelper.IsNoclipping = false;
			_animHelper.DuckLevel = 0f;
			// One-shot idle nudge — PlayerController resumes real velocity next frame.
			_animHelper.WithVelocity( Vector3.Zero );
			_animHelper.WithWishVelocity( Vector3.Zero );
		}

		if ( !preserveSwing )
		{
			_lateralSwingPlaybackSlowed = false;
			_playbackRateSaved = 1f;
			_windupHoldFrozen = false;
		}

		if ( ResolvePresentationHoldPose() == HoldPose.MeleeTwoHand )
			ApplyMeleeTwoHandHold();
		else
			ClearMeleeTwoHandHold();
	}

	bool HasBodyCombatSequencePose()
	{
		var body = ResolveBody();
		return body is not null && body.IsValid()
		       && (!body.UseAnimGraph || !string.IsNullOrEmpty( body.Sequence.Name ));
	}

	/// <summary>
	/// Stop the citizen melee attack clip and clear the busy window so recovery/idle can take over cleanly.
	/// </summary>
	public void AbortMeleeAttackAnimClip( string reason )
	{
		ClearMeleeSwingClipFromPressFlag();
		_meleeAttackAnimBusyUntilSandbox = 0;
		RestoreLateralSwingPlaybackRate();

		var body = ResolveBody();
		if ( body is not null && body.IsValid() )
		{
			// Prefer killing the attack while the graph still drives the pose.
			var graphWasOn = body.UseAnimGraph;
			if ( !graphWasOn )
				body.UseAnimGraph = true;

			body.Set( "b_attack", false );
			body.Set( "holdtype_attack", 0f );
			body.Set( "b_weapon_lower", false );

			if ( !graphWasOn )
				body.UseAnimGraph = false;
		}

		Components.Get<PlayerCombat>()?.LogMeleeAnimStopIfAny( $"abort attack clip ({reason})" );
	}

	/// <summary>
	/// After a UseAnimGraph=false recovery sequence, force melee idle — never resume a frozen attack clip.
	/// </summary>
	void ResetMeleeAttackAnimGraphToIdle( SkinnedModelRenderer body )
	{
		if ( body is null || !body.IsValid() )
			return;

		body.Set( "b_attack", false );
		body.Set( "holdtype_attack", 0f );
		body.Set( "b_weapon_lower", false );

		if ( !string.IsNullOrEmpty( body.Sequence.Name ) )
		{
			body.Sequence.Name = null;
			body.Sequence.Time = 0f;
		}

		Components.Get<PlayerCombat>()?.LogMeleeAnimStart( "animgraph melee idle hold (post-recovery reset)",
			"b_attack=false busyCleared" );
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

		_meleeDemoStick = new GameObject( true, DemoStickObjectName );
		// Local presentation only. As a plain child of a networked pawn this replicated to clients,
		// so a client saw the host's copy on top of the one it built itself — two swords.
		_meleeDemoStick.NetworkMode = NetworkMode.Never;
		_meleeDemoStick.Parent = GameObject;
		_meleeDemoStick.Tags.Add( "ignore" );

		_meleeDemoStickRenderer = _meleeDemoStick.Components.Create<ModelRenderer>();
		var model = Model.Load( DemoStickModelPath );
		_meleeDemoStickRenderer.Model = model;
		_meleeDemoStickRenderer.Tint = MeleeDemoStickTint;
		_meleeDemoStickRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;

		_demoStickMeshHalfExtentX = ResolveMeshHalfExtentAlongX( model );
		DestroyStrayDemoSticks();
	}

	/// <summary>
	/// Drop any other box-sword under this pawn (a replicated copy from a peer, or a leftover whose
	/// field reference we lost). Only runs when we build a stick — not per frame.
	/// </summary>
	void DestroyStrayDemoSticks()
	{
		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() || child == _meleeDemoStick )
				continue;

			if ( string.Equals( child.Name, DemoStickObjectName, StringComparison.OrdinalIgnoreCase ) )
				child.Destroy();
		}
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
		// Sword is dropped for the whole hit reaction — don't let PreRender re-create it.
		if ( _hitReactionPoseActive || !ShowMeleeDemoStick || ResolvePresentationHoldPose() != HoldPose.MeleeTwoHand )
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

	void TickLateralSwingPlaybackRestore()
	{
		if ( !_lateralSwingPlaybackSlowed )
			return;

		if ( _windupHoldFrozen || _windupHoldActive )
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

		// Authored on the Body object in basicplayer.prefab with Target bound (Commandment #5).
		_animHelper = Components.Get<CitizenAnimationHelper>( FindMode.EverythingInSelfAndDescendants );
		if ( _animHelper is null || !_animHelper.IsValid() )
		{
			Log.Warning( $"[PlayerAnimation] {GameObject.Name}: no CitizenAnimationHelper on the pawn — add one to the body renderer object on the prefab." );
			return;
		}

		if ( _animHelper.Target is null && _bodyRenderer is not null && _bodyRenderer.IsValid() )
			_animHelper.Target = _bodyRenderer;
	}
}
