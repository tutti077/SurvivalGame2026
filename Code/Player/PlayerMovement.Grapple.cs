using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Rope-swing grapple: aim crosshair, attach/detach, length control, and host-validated attach state.
/// Owned by <see cref="PlayerMovement"/> (Commandment #1) — same umbrella as the wingsuit.
/// Swing constraint / air push live in <c>PlayerMovement.cs</c>.
/// </summary>
partial class PlayerMovement
{
	public const string GrappleSurfaceTag = "grapple";

	static readonly string[] LeftArmBoneCandidates =
	{
		"hand_L",
		"hold_L",
		"arm_lower_L",
		"lower_arm_L",
		"LeftHand",
		"hand_left",
	};

	[Property, Group( "Grapple Input" )] public string GrappleAction { get; set; } = "Grapple";
	/// <summary>Shorten rope (default E).</summary>
	[Property, Group( "Grapple Input" )] public string RetractAction { get; set; } = "GrappleRetract";
	/// <summary>Pay out / expand max rope length (default Q).</summary>
	[Property, Group( "Grapple Input" )] public string DetractAction { get; set; } = "GrappleDetract";

	[Property, Group( "Grapple Range" ), Title( "Max Range (meters)" )]
	public float MaxRangeMeters { get; set; } = 60f;

	[Property, Group( "Grapple Rope" ), Title( "Winch (m/s)" )]
	public float RetractMetersPerSecond { get; set; } = 8f;

	/// <summary>
	/// Longest the rope may be paid out with Q. Its own value rather than a copy of
	/// <see cref="MaxRangeMeters"/>, so a short hook shot can still be let out into a long, slow swing;
	/// by default the two match, and the rope can reach as far as the hook can.
	/// </summary>
	[Property, Group( "Grapple Rope" ), Title( "Hard Max Length (meters)" )]
	public float HardMaxLengthMeters { get; set; } = 60f;

	[Property, Group( "Grapple Rope" ), Title( "Min Length (meters)" )]
	public float MinLengthMeters { get; set; } = 1f;

	[Property, Group( "Grapple Stamina" )]
	public float AttachStaminaCost { get; set; }

	[Property, Group( "Grapple Stamina" ), Title( "Airborne Drain (stamina/s)" )]
	public float AirborneStaminaPerSecond { get; set; } = 3f;

	[Property, Group( "Grapple Swing" ), Title( "Attach Velocity Scale" )]
	public float AttachVelocityScale { get; set; } = 1f;

	/// <summary>
	/// Kids-swing pump: WASD is a <b>constant force</b> (this × gravity) in the camera-relative
	/// direction you hold, exactly like leaning your weight on a swing. It is <b>not</b> thrust along
	/// your motion, so holding one key cannot climb forever — it just moves the pendulum's resting
	/// point to <c>atan(this)</c> off vertical and you settle there.
	/// <para>
	/// Height comes from flipping direction at each apex (W…S…W…S, or A…D…A…D — any axis works).
	/// This also fixes the size of the <b>first</b> pump: one W from a dead hang peaks at
	/// <c>2 × atan(this)</c> and no further, so 0.15 ≈ 17°. Every later pump grows the arc by about
	/// that same amount, which is why ~3 pumps reaches 45° and ~5-6 reaches anchor height. Keep
	/// <see cref="SwingCoastDamping"/> low or drag eats most of each pump on a long rope.
	/// Pressing against your motion brakes for the same reason.
	/// </para>
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Pump Force (× gravity)" ), Range( 0.05f, 1f ), Step( 0.01f )]
	public float SwingPumpGravityFraction { get; set; } = 0.18f;

	/// <summary>
	/// Pump authority retained once you are <b>above anchor height</b>. 0 means flat W…S…W…S tops out
	/// level with the anchor (grab the ledge) without needing drag to do it — so every pump below that
	/// keeps full strength and the ramp stays crisp.
	/// <para>
	/// Circling (W…A…S…D) still loops: a taut orbit always sits below the anchor, so it keeps full
	/// authority and can build the speed to coast over the top. Raise this only if you want flat
	/// pumping alone to be able to wrap.
	/// </para>
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Pump Above Anchor" ), Range( 0f, 1f ), Step( 0.05f )]
	public float SwingPumpAboveAnchorScale { get; set; }

	/// <summary>
	/// Retract thrust along existing motion. Requires both an existing swing <b>and</b> rope actually
	/// coming in, so at min length holding E adds nothing. Winch rate is unaffected by this.
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Retract Pump (× gravity)" ), Range( 0f, 1f ), Step( 0.01f )]
	public float SwingRetractPumpGravityFraction { get; set; } = 0.18f;

	/// <summary>Tangent speed below which retract adds nothing (keeps E from creating a swing from rest).</summary>
	[Property, Group( "Grapple Swing" ), Title( "Retract Pump Min Speed (u/s)" ), Range( 10f, 200f ), Step( 5f )]
	public float SwingRetractPumpMinSpeed { get; set; } = 60f;

	/// <summary>
	/// How much of ideal angular-momentum conservation the winch applies when rope length changes
	/// (<c>v ∝ 1/r</c>). 1 = full ice-skater spin-up on E and full slow-down on Q; lower is gentler.
	/// This is the main reason reeling in mid-swing gains speed.
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Winch Momentum Transfer" ), Range( 0f, 1f ), Step( 0.05f )]
	public float SwingWinchMomentumTransfer { get; set; } = 0.4f;

	/// <summary>
	/// Soft ceiling for WASD / retract pump add (engine units). Gravity can still carry you past this.
	/// Deliberately set well above what a loop needs (<c>sqrt(5·g·rope)</c> ≈ 3100 on a fully paid-out
	/// 60 m rope) so the cap is never the thing that stops a loop — drag and technique are.
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Max Speed" ), Range( 50f, 8000f ), Step( 50f )]
	public float SwingMaxSpeed { get; set; } = 6000f;

	/// <summary>
	/// Air drag, always applied. Costs a slice of every pump that scales with the swing period, so on a
	/// long slow rope it is expensive: 0.04 was eating enough per pump to turn a 5-pump ramp into 7.
	/// Keep it small — <see cref="SwingPumpAboveAnchorScale"/> owns the ceiling now, not drag.
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Air Drag (1/s)" ), Range( 0f, 0.4f ), Step( 0.01f )]
	public float SwingCoastDamping { get; set; } = 0.01f;

	/// <summary>
	/// Gravity scale after release until you land. Heavier = faster fall-off, less sky whip. No height cap.
	/// </summary>
	[Property, Group( "Grapple Swing" ), Title( "Air Gravity Scale" ), Range( 1f, 3f ), Step( 0.05f )]
	public float GrappleAirGravityScale { get; set; } = 1.24f;

	[Property, Group( "Grapple Aim" ), Title( "Crosshair Idle Hide (seconds)" )]
	public float CrosshairIdleHideSeconds { get; set; } = 10f;

	/// <summary>Soft lock: pick the best in-range tagged surface near the crosshair, not only the exact center ray.</summary>
	[Property, Group( "Grapple Aim Assist" ), Title( "Enabled" )]
	public bool AimAssistEnabled { get; set; } = true;

	/// <summary>Screen-pixel radius around the crosshair where assist may steal a target.</summary>
	[Property, Group( "Grapple Aim Assist" ), Title( "Radius (pixels)" )]
	public float AssistRadiusPixels { get; set; } = 72f;

	/// <summary>
	/// Secondary score weight (meters). Crosshair closeness wins first; this breaks ties toward nearer surfaces.
	/// </summary>
	[Property, Group( "Grapple Aim Assist" ), Title( "Distance Bias" )]
	public float AssistDistanceBias { get; set; } = 0.05f;

	[Property, Group( "Grapple Aim Assist" ), Title( "Sample Rings" )]
	public int AssistSampleRings { get; set; } = 3;

	[Property, Group( "Grapple Aim Assist" ), Title( "Samples Per Ring" )]
	public int AssistSamplesPerRing { get; set; } = 8;

	/// <summary>
	/// How many pixels closer to the crosshair a new candidate must be before we leave the sticky lock.
	/// </summary>
	[Property, Group( "Grapple Aim Assist" ), Title( "Stick Break (pixels)" )]
	public float AssistStickBreakPixels { get; set; } = 18f;

	/// <summary>Screen-space smoothing rate for the lock reticle (higher = snappier).</summary>
	[Property, Group( "Grapple Aim Assist" ), Title( "Lock Smooth" )]
	public float AssistLockSmooth { get; set; } = 14f;

	[Property, Group( "Grapple Visual" )]
	public bool DrawDebugRope { get; set; } = true;

	/// <summary>On-screen swing speed / velocity while attached (local driver).</summary>
	[Property, Group( "Grapple Debug" ), Title( "Show Speed Overlay" )]
	public bool ShowSpeedDebug { get; set; }

	[Property, Group( "Grapple Debug" )]
	public bool LogGrapple { get; set; }

	/// <summary>Host-synced: rope currently attached. <see cref="SyncFlags.FromHost"/> — default Sync is owner-authored and host writes on client pawns never reach the owner.</summary>
	[Sync( SyncFlags.FromHost )] public bool GrappleAttached { get; private set; }

	/// <summary>Host-synced world attach point (static for v1).</summary>
	[Sync( SyncFlags.FromHost )] public Vector3 GrappleAttachWorldPoint { get; private set; }

	/// <summary>Host-synced current rope length in engine units.</summary>
	[Sync( SyncFlags.FromHost )] public float GrappleRopeLengthEngine { get; private set; }

	/// <summary>Local aim UI: crosshair should draw.</summary>
	public bool IsAimHudActive { get; private set; }

	/// <summary>Local aim UI: look ray hits a tagged surface within range.</summary>
	public bool HasValidAimTarget { get; private set; }

	/// <summary>Local aim hit point when <see cref="HasValidAimTarget"/>.</summary>
	public Vector3 AimHitWorldPoint { get; private set; }

	/// <summary>
	/// Smoothed screen point for the grapple lock indicator (unified crosshair inner ring):
	/// slides with aim assist to the actual attach point on the object.
	/// </summary>
	public bool TryGetAimLockScreenPoint( out Vector2 screenPoint )
	{
		screenPoint = default;
		if ( !HasValidAimTarget )
			return false;

		if ( _aimHitScreenValid )
		{
			screenPoint = _aimHitScreenPoint;
			return true;
		}

		var cam = BuildViewCamera.Resolve( GameObject );
		return cam.IsValid() && TryWorldToScreen( cam, AimHitWorldPoint, out screenPoint );
	}

	/// <summary>Screen position of <see cref="AimHitWorldPoint"/> for the lock reticle (camera space).</summary>
	Vector2 _aimHitScreenPoint;
	bool _aimHitScreenValid;

	GameObject _stickyAimObject;
	Vector3 _stickyAimPoint;
	bool _hasStickyAim;
	Vector2 _displayAimScreen;
	bool _displayAimScreenInit;

	/// <summary>Owner is holding shorten (E) this frame.</summary>
	public bool IsRetractingRope { get; private set; }

	/// <summary>Owner is holding pay-out (Q) this frame.</summary>
	public bool IsDetractingRope { get; private set; }

	PlayerEquipment _equipment;
	double _aimHudHideAt;
	float _airborneStaminaDebt;
	bool _savedEnablePressing = true;
	bool _pressingOverrideActive;
	bool _grappleReleaseAir;
	bool _grappleGravityOverrideActive;
	float _savedGrappleGravityScale = 1f;
	bool _grappleWasEquippedForControlPrompt;
	bool _grappleControllerOverrideActive;
	float _savedAirFriction;
	float _savedBrakePower;
	bool _grappleDampingOverrideActive;
	float _savedLinearDamping;

	void InitializeGrapple()
	{
		_equipment = Components.Get<PlayerEquipment>();
		RefreshTuningFromEquipment();

		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: ready (action '{GrappleAction}', range {MaxRangeMeters:0.#}m)." );
	}

	void TickGrappleUpdate()
	{
		if ( !IsLocalMovementDriver() )
		{
			DrawRopeIfNeeded();
			return;
		}

		RefreshTuningFromEquipment();
		PollGrappleControlSchemePrompt();

		var menu = Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
		{
			HasValidAimTarget = false;
			IsRetractingRope = false;
			IsDetractingRope = false;
			ClearStickyAim();
			UpdateAimHudVisibility( forceHide: true );
			UpdatePressingOverride( false );
			return;
		}

		if ( !HasGrappleEquipped() )
		{
			HasValidAimTarget = false;
			IsRetractingRope = false;
			IsDetractingRope = false;
			ClearStickyAim();
			UpdateAimHudVisibility( forceHide: true );
			UpdatePressingOverride( false );
			if ( GrappleAttached )
				RequestDetach();
			DrawRopeIfNeeded();
			return;
		}

		if ( GrappleControlSchemeStore.NeedsChoice )
		{
			IsRetractingRope = false;
			IsDetractingRope = false;
			HasValidAimTarget = false;
			UpdateAimHudVisibility( forceHide: true );
			UpdatePressingOverride( false );
			DrawRopeIfNeeded();
			return;
		}

		UpdateAimTrace();
		PollToggleInput();
		PollLengthHoldState();
		UpdateAimHudVisibility( forceHide: false );
		DrawRopeIfNeeded();
		DrawSpeedDebugIfNeeded();
	}

	void PollGrappleControlSchemePrompt()
	{
		var equipped = HasGrappleEquipped();
		if ( equipped && !_grappleWasEquippedForControlPrompt )
			GrappleControlSchemeStore.RequestChoice();

		_grappleWasEquippedForControlPrompt = equipped;
	}

	void TickGrappleFixedUpdate()
	{
		TickGrappleReleaseWeight();
		TickGrappleControllerOverride();
		TickGrappledByPlayerPull();

		// Mantle broke the rope this instant — no winch / stamina while the host detach lands.
		if ( !GrappleAttached || IsGrappleLedgePulling )
			return;

		PollLengthHoldState();
		ApplyLengthHoldDelta( Time.Delta );
		DrainAirborneStamina( Time.Delta );
	}

	void TickGrappleReleaseWeight()
	{
		if ( !IsLocalMovementDriver() )
			return;

		_controller ??= Components.Get<PlayerController>();
		var grounded = _controller is not null && _controller.IsOnGround;
		if ( grounded && !GrappleAttached )
			_grappleReleaseAir = false;

		var body = ResolveGrappleBody();
		if ( body is null || !body.IsValid() )
			return;

		var heavy = !WingsuitDeployed && !grounded && !GrappleAttached && _grappleReleaseAir;
		var wantScale = heavy ? Math.Max( 1f, GrappleAirGravityScale ) : 1f;
		if ( heavy )
		{
			if ( !_grappleGravityOverrideActive )
			{
				_savedGrappleGravityScale = body.GravityScale > 0.01f ? body.GravityScale : 1f;
				_grappleGravityOverrideActive = true;
			}

			body.GravityScale = wantScale;
		}
		else if ( _grappleGravityOverrideActive )
		{
			body.GravityScale = _savedGrappleGravityScale;
			_grappleGravityOverrideActive = false;
		}
	}

	public float GetMaxRangeEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, MaxRangeMeters ) );

	public float GetMinLengthEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( 0.25f, MinLengthMeters ) );

	public float GetHardMaxLengthEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( MinLengthMeters, HardMaxLengthMeters ) );

	/// <summary>Called from <see cref="PlayerVitals.ApplyDamageAfterArmor"/> on the host when HP is lost.</summary>
	public void NotifyGrappleDamaged( float damageAfterArmor )
	{
		if ( damageAfterArmor <= 0f || !GrappleAttached )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ServerDetach( "damage" );
	}

	/// <summary>
	/// Hit reaction: drop the rope. Runs on whichever machine saw the reaction start — the host
	/// detaches authoritatively, the owner asks the host, and observers just stop drawing it.
	/// </summary>
	void DetachGrappleForHitReaction()
	{
		if ( !GrappleAttached )
			return;

		IsRetractingRope = false;
		IsDetractingRope = false;
		ClearStickyAim();

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerDetach( "hit reaction" );
			return;
		}

		if ( !GameObject.IsProxy )
			RpcRequestDetach();
	}

	/// <summary>Debug / world teleports: drop the rope so the pawn isn't yanked back to the attach point.</summary>
	public void DetachGrappleForWorldTeleport()
	{
		if ( !GrappleAttached )
			return;

		RequestDetach();
	}

	void RefreshTuningFromEquipment()
	{
		if ( _equipment is null )
			_equipment = Components.Get<PlayerEquipment>();

		if ( _equipment is null )
			return;

		if ( !TryGetEquippedGrappleProfile( out var profile ) || profile is null )
			return;

		if ( profile.GrappleMaxRangeMeters > 0f )
			MaxRangeMeters = profile.GrappleMaxRangeMeters;

		if ( profile.GrappleHardMaxLengthMeters > 0f )
			HardMaxLengthMeters = profile.GrappleHardMaxLengthMeters;
		else if ( profile.GrappleMaxRangeMeters > 0f )
			HardMaxLengthMeters = profile.GrappleMaxRangeMeters;

		if ( profile.GrappleRetractMetersPerSecond > 0f )
			RetractMetersPerSecond = profile.GrappleRetractMetersPerSecond;

		if ( profile.GrappleAttachStaminaCost >= 0f )
			AttachStaminaCost = profile.GrappleAttachStaminaCost;

		if ( profile.GrappleAirborneStaminaPerSecond >= 0f )
			AirborneStaminaPerSecond = profile.GrappleAirborneStaminaPerSecond;
	}

	/// <summary>True when the Grapple paperdoll slot holds a grapple tool profile.</summary>
	public bool HasGrappleEquipped() => TryGetEquippedGrappleProfile( out _ );

	bool TryGetEquippedGrappleProfile( out EquipmentProfileData profile )
	{
		profile = null;
		if ( _equipment is null )
			_equipment = Components.Get<PlayerEquipment>();

		if ( _equipment is null )
			return false;

		var resourceId = _equipment.GetSlotResourceId( EquipmentSlot.Grapple );
		if ( string.IsNullOrWhiteSpace( resourceId ) )
			return false;

		if ( !EquipmentCatalog.TryGet( resourceId, out profile ) || profile is null )
			return false;

		return IsGrappleEquipmentProfile( profile );
	}

	static bool IsGrappleEquipmentProfile( EquipmentProfileData profile )
	{
		if ( profile is null )
			return false;

		if ( string.Equals( profile.Slot, "grapple", StringComparison.OrdinalIgnoreCase ) )
			return true;

		if ( profile.AllowedSlots is not null )
		{
			for ( var i = 0; i < profile.AllowedSlots.Count; i++ )
			{
				if ( string.Equals( profile.AllowedSlots[i], "grapple", StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		if ( profile.Actions is not null )
		{
			for ( var i = 0; i < profile.Actions.Count; i++ )
			{
				if ( string.Equals( profile.Actions[i], "Grapple", StringComparison.OrdinalIgnoreCase ) )
					return true;
			}
		}

		return false;
	}

	void UpdateAimTrace()
	{
		HasValidAimTarget = false;
		AimHitWorldPoint = default;
		_aimHitScreenValid = false;

		if ( !TryTraceGrappleAim( out var hitPoint, out var hitObject, out _, out var hitScreen, out var hasScreen, out var selectScreenDist ) )
		{
			ClearStickyAim();
			return;
		}

		var cam = BuildViewCamera.Resolve( GameObject );
		var stickBreak = Math.Max( 4f, AssistStickBreakPixels );
		var adoptNew = true;

		// Sticky locks the *object*; the attach point always re-snaps to closest-to-cursor on that object.
		if ( _hasStickyAim && _stickyAimObject.IsValid()
		     && TryResolveAimPointOnObject( _stickyAimObject, out var stickyPoint, out var stickyScreen, out var stickySelectDist )
		     && IsWithinGrappleRange( stickyPoint ) )
		{
			var sameObject = hitObject.IsValid() && IsSameGrappleTarget( hitObject, _stickyAimObject );
			if ( sameObject || selectScreenDist + 0.01f >= stickySelectDist - stickBreak )
			{
				adoptNew = false;
				_stickyAimPoint = stickyPoint;
				hitScreen = stickyScreen;
				hasScreen = true;
			}
		}

		if ( adoptNew )
		{
			_stickyAimObject = hitObject;
			_stickyAimPoint = hitPoint;
			_hasStickyAim = true;
		}

		// Red lock only when the snap point is actually grapple-able.
		if ( !IsWithinGrappleRange( _stickyAimPoint ) )
		{
			ClearStickyAim();
			return;
		}

		AimHitWorldPoint = _stickyAimPoint;
		HasValidAimTarget = true;

		var targetScreen = hitScreen;
		if ( cam.IsValid() && TryWorldToScreen( cam, _stickyAimPoint, out var projectedSticky ) )
			targetScreen = projectedSticky;

		var smooth = Math.Max( 1f, AssistLockSmooth );
		if ( !_displayAimScreenInit || !hasScreen )
		{
			_displayAimScreen = targetScreen;
			_displayAimScreenInit = true;
		}
		else
		{
			var t = 1f - MathF.Exp( -smooth * Math.Max( 1e-4f, Time.Delta ) );
			_displayAimScreen = Vector2.Lerp( _displayAimScreen, targetScreen, t );
		}

		_aimHitScreenPoint = _displayAimScreen;
		_aimHitScreenValid = true;
	}

	bool IsWithinGrappleRange( Vector3 worldPoint )
	{
		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, worldPoint );
		return dist >= GetMinLengthEngine() * 0.5f && dist <= GetMaxRangeEngine();
	}

	void ClearStickyAim()
	{
		_hasStickyAim = false;
		_stickyAimObject = null;
		_stickyAimPoint = default;
		_displayAimScreenInit = false;
	}

	void PollToggleInput()
	{
		if ( !WasGrapplePressed() )
			return;

		BumpAimHud();

		if ( GrappleAttached )
		{
			RequestDetach();
			return;
		}

		// No re-attaching out of a hit reaction — the rope is gone for that whole window.
		if ( IsHitReactionActive() )
			return;

		// Mid ledge pull: no new rope until the vault finishes.
		if ( IsGrappleLedgePulling )
			return;

		if ( IsGrappleVictimCooldownActive() || IsHeldByPlayerGrapple )
			return;

		if ( !HasValidAimTarget )
		{
			if ( LogGrapple )
				LogAimRejectReason();
			return;
		}

		// Tell the host WHO we aimed at, not just where — a player attach must never be inferred
		// from proximity, or a bystander near the hit point steals the hook.
		var intendedPlayerId = Guid.Empty;
		if ( _hasStickyAim && _stickyAimObject.IsValid() && _stickyAimObject != GameObject
		     && _stickyAimObject.Components.Get<PlayerMovement>() is not null )
			intendedPlayerId = _stickyAimObject.Id;

		RequestAttach( AimHitWorldPoint, intendedPlayerId );
	}

	void LogAimRejectReason()
	{
		if ( !TryGetAimRayFromPlayer( out var origin, out var direction ) )
		{
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: aim reject — no look direction (view camera)." );
			return;
		}

		var maxRange = GetMaxRangeEngine();
		var tr = TraceAimRay( origin, direction, maxRange );
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
		{
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: aim reject — ray miss (cast {TerrainWorldUnits.EngineToMeters( maxRange ):0.#}m from player)." );
			return;
		}

		var distPawn = Vector3.DistanceBetween( GameObject.WorldPosition, tr.HitPosition );
		var tagged = HasGrappleTag( tr );
		Log.Info(
			$"[PlayerMovement.Grapple] {GameObject.Name}: aim reject — hit '{tr.GameObject.Name}' " +
			$"pawnDist={TerrainWorldUnits.EngineToMeters( distPawn ):0.##}m " +
			$"(max {TerrainWorldUnits.EngineToMeters( maxRange ):0.#}m) tagged={tagged}." );
	}

	bool TryTraceGrappleAim(
		out Vector3 hitPoint,
		out GameObject hitObject,
		out float distanceFromPawn,
		out Vector2 hitScreen,
		out bool hasHitScreen,
		out float selectScreenDist )
	{
		hitPoint = default;
		hitObject = null;
		distanceFromPawn = 0f;
		hitScreen = default;
		hasHitScreen = false;
		selectScreenDist = float.MaxValue;

		if ( !TryGetAimRayFromPlayer( out var eyeOrigin, out var lookDir ) )
			return false;

		var cam = BuildViewCamera.Resolve( GameObject );
		var rayOrigin = cam.IsValid() ? cam.WorldPosition : eyeOrigin;
		var maxRange = GetMaxRangeEngine();
		var castDist = maxRange;
		if ( cam.IsValid() )
			castDist += Vector3.DistanceBetween( cam.WorldPosition, eyeOrigin ) + TerrainWorldUnits.MetersToEngine( 2f );

		var pawnPos = GameObject.WorldPosition;
		var center = Vector2.Zero;
		var hasCenter = false;
		if ( cam.IsValid() )
		{
			var rect = cam.ScreenRect;
			if ( rect.Width >= 1f && rect.Height >= 1f )
			{
				center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );
				hasCenter = true;
			}
		}

		var radiusPx = Math.Max( 1f, AssistRadiusPixels );
		var assistOn = AimAssistEnabled && radiusPx >= 1f && hasCenter;

		if ( hasCenter && cam.IsValid() && TryGetAimDirectionFromScreen( cam, center, out var centerDir ) )
			lookDir = centerDir;

		// Phase 1: pick which grapple object (crosshair closeness first, distance second).
		GameObject bestObject = null;
		var bestObjectScore = float.MaxValue;
		var bestObjectScreenDist = float.MaxValue;

		void ConsiderObject( SceneTraceResult tr, Vector2 rayScreen, bool useRayScreen )
		{
			if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
				return;

			if ( !IsGrappleTarget( tr ) )
				return;

			var distPawn = Vector3.DistanceBetween( pawnPos, tr.HitPosition );
			if ( distPawn > maxRange )
				return;

			var root = ResolveGrappleTargetRoot( tr.GameObject );
			var projected = rayScreen;
			var projectedOk = useRayScreen;
			if ( hasCenter && cam.IsValid() && TryWorldToScreen( cam, tr.HitPosition, out var engineScreen ) )
			{
				projected = engineScreen;
				projectedOk = true;
			}

			var screenDist = 0f;
			if ( hasCenter && projectedOk )
				screenDist = (projected - center).Length;

			if ( assistOn && screenDist > radiusPx + 0.75f )
				return;

			if ( !assistOn && screenDist > 2.5f )
				return;

			var score = screenDist
			            + TerrainWorldUnits.EngineToMeters( distPawn ) * Math.Max( 0f, AssistDistanceBias );
			if ( score >= bestObjectScore )
				return;

			bestObjectScore = score;
			bestObjectScreenDist = screenDist;
			bestObject = root;
		}

		ConsiderObject( TraceAimRay( rayOrigin, lookDir, castDist ), center, hasCenter );

		if ( assistOn )
		{
			var rings = Math.Clamp( AssistSampleRings, 1, 6 );
			var perRing = Math.Clamp( AssistSamplesPerRing, 4, 16 );

			for ( var ring = 1; ring <= rings; ring++ )
			{
				var t = ring / (float)rings;
				var ringRadius = radiusPx * t;
				var azimuthOffset = ring * 0.35f;

				for ( var i = 0; i < perRing; i++ )
				{
					var az = azimuthOffset + i * ( MathF.PI * 2f / perRing );
					var sampleScreen = center + new Vector2( MathF.Cos( az ), MathF.Sin( az ) ) * ringRadius;
					if ( !TryGetAimDirectionFromScreen( cam, sampleScreen, out var sampleDir ) )
						continue;

					ConsiderObject( TraceAimRay( rayOrigin, sampleDir, castDist ), sampleScreen, true );
				}
			}
		}

		if ( bestObject is null || !bestObject.IsValid() )
			return false;

		// Phase 2: snap to the point on that object closest to the crosshair ray (not a random sample hit).
		if ( !TryResolveAimPointOnObject( bestObject, out hitPoint, out hitScreen, out selectScreenDist ) )
			return false;

		// Prefer the object-selection screen distance for sticky comparisons.
		selectScreenDist = bestObjectScreenDist;
		hitObject = bestObject;
		distanceFromPawn = Vector3.DistanceBetween( pawnPos, hitPoint );
		hasHitScreen = true;
		return IsWithinGrappleRange( hitPoint );
	}

	/// <summary>
	/// Closest surface point on a grapple object to the current crosshair ray.
	/// </summary>
	bool TryResolveAimPointOnObject(
		GameObject root,
		out Vector3 hitPoint,
		out Vector2 hitScreen,
		out float screenDistToCrosshair )
	{
		hitPoint = default;
		hitScreen = default;
		screenDistToCrosshair = float.MaxValue;

		if ( root is null || !root.IsValid() )
			return false;

		if ( !TryGetAimRayFromPlayer( out var eyeOrigin, out var lookDir ) )
			return false;

		var cam = BuildViewCamera.Resolve( GameObject );
		var rayOrigin = cam.IsValid() ? cam.WorldPosition : eyeOrigin;
		var maxRange = GetMaxRangeEngine();
		var castDist = maxRange;
		if ( cam.IsValid() )
			castDist += Vector3.DistanceBetween( cam.WorldPosition, eyeOrigin ) + TerrainWorldUnits.MetersToEngine( 2f );

		var center = Vector2.Zero;
		var hasCenter = false;
		if ( cam.IsValid() )
		{
			var rect = cam.ScreenRect;
			if ( rect.Width >= 1f && rect.Height >= 1f )
			{
				center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );
				hasCenter = true;
				if ( TryGetAimDirectionFromScreen( cam, center, out var centerDir ) )
					lookDir = centerDir;
			}
		}

		// Direct crosshair ray onto this object wins — that's the true cursor contact.
		var centerTr = TraceAimRay( rayOrigin, lookDir, castDist );
		if ( centerTr.Hit && centerTr.GameObject.IsValid()
		     && IsUnderGrappleRoot( centerTr.GameObject, root )
		     && IsGrappleTarget( centerTr ) )
		{
			var distPawn = Vector3.DistanceBetween( GameObject.WorldPosition, centerTr.HitPosition );
			if ( IsWithinGrappleRange( centerTr.HitPosition ) )
			{
				hitPoint = centerTr.HitPosition;
				if ( cam.IsValid() && TryWorldToScreen( cam, hitPoint, out hitScreen ) && hasCenter )
					screenDistToCrosshair = (hitScreen - center).Length;
				else
				{
					hitScreen = center;
					screenDistToCrosshair = 0f;
				}

				return true;
			}
		}

		// Only search for closest-to-ray points within grapple range of the pawn.
		var searchAlong = castDist;
		if ( !TryClosestPointOnObjectToRay( root, rayOrigin, lookDir, searchAlong, out hitPoint ) )
			return false;

		if ( !IsWithinGrappleRange( hitPoint ) )
			return false;

		if ( cam.IsValid() && TryWorldToScreen( cam, hitPoint, out hitScreen ) )
		{
			if ( hasCenter )
			{
				screenDistToCrosshair = (hitScreen - center).Length;
				if ( AimAssistEnabled && screenDistToCrosshair > Math.Max( 1f, AssistRadiusPixels ) + 10f )
					return false;
			}
			else
			{
				screenDistToCrosshair = 0f;
			}

			return true;
		}

		hitScreen = center;
		screenDistToCrosshair = 0f;
		return true;
	}

	bool TryClosestPointOnObjectToRay(
		GameObject root,
		Vector3 rayOrigin,
		Vector3 rayDir,
		float maxAlong,
		out Vector3 closest )
	{
		closest = default;
		if ( root is null || !root.IsValid() )
			return false;

		rayDir = rayDir.Normal;
		maxAlong = Math.Max( 1f, maxAlong );
		var maxRange = GetMaxRangeEngine();
		var pawnPos = GameObject.WorldPosition;

		var colliders = root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants );
		var bestPerp = float.MaxValue;
		var found = false;

		const int steps = 28;
		for ( var i = 0; i <= steps; i++ )
		{
			var along = maxAlong * ( i / (float)steps );
			var probe = rayOrigin + rayDir * along;

			foreach ( var col in colliders )
			{
				if ( col is null || !col.IsValid() )
					continue;

				var p = col.FindClosestPoint( probe );
				if ( Vector3.DistanceBetween( pawnPos, p ) > maxRange )
					continue;

				var alongP = Vector3.Dot( p - rayOrigin, rayDir );
				if ( alongP < 0f || alongP > maxAlong )
					continue;

				var onRay = rayOrigin + rayDir * alongP;
				var perp = (p - onRay).Length;
				if ( perp >= bestPerp )
					continue;

				bestPerp = perp;
				closest = p;
				found = true;
			}
		}

		return found;
	}

	static GameObject ResolveGrappleRoot( GameObject go )
	{
		GameObject tagged = go;
		for ( var cur = go; cur.IsValid(); cur = cur.Parent )
		{
			if ( ObjectHasGrappleTag( cur ) )
				tagged = cur;
		}

		return tagged;
	}

	static bool IsUnderGrappleRoot( GameObject go, GameObject root )
	{
		if ( !go.IsValid() || !root.IsValid() )
			return false;

		for ( var cur = go; cur.IsValid(); cur = cur.Parent )
		{
			if ( cur == root )
				return true;
		}

		return false;
	}

	SceneTraceResult TraceAimRay( Vector3 origin, Vector3 direction, float castDistance )
	{
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return default;

		var end = origin + direction * Math.Max( 1f, castDistance );
		var tr = scene.Trace.Ray( origin, end )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		if ( tr.Hit && tr.GameObject.IsValid() )
			return tr;

		return scene.Trace.Ray( origin, end )
			.IgnoreGameObjectHierarchy( GameObject )
			.UseHitboxes()
			.Run();
	}

	/// <summary>
	/// Host check: client snap must be in range on a tagged surface, or on the exact player the
	/// client aimed at. Avoids host-camera aim (scene.Camera is the host view for remote pawns).
	/// </summary>
	bool TryValidateAttachPoint( Vector3 clientHitPoint, Guid intendedPlayerId, out Vector3 validatedPoint, out float length, out Guid playerTargetId )
	{
		validatedPoint = default;
		length = 0f;
		playerTargetId = Guid.Empty;

		if ( !IsWithinGrappleRange( clientHitPoint ) )
			return false;

		var surfaceSlack = TerrainWorldUnits.MetersToEngine( 3f );

		// Player attach only when the client named a target — never inferred from proximity.
		if ( intendedPlayerId != Guid.Empty )
		{
			if ( !TryValidatePlayerAttach( intendedPlayerId, clientHitPoint, out var playerRoot, out var playerAttach ) )
				return false;

			validatedPoint = playerAttach;
			length = Vector3.DistanceBetween( GameObject.WorldPosition, playerAttach );
			playerTargetId = playerRoot.Id;
			return true;
		}

		if ( TryFindTaggedRootNearPoint( clientHitPoint, surfaceSlack, out var nearRoot ) )
		{
			if ( TryConfirmPointOnGrappleObject( nearRoot, clientHitPoint ) )
			{
				validatedPoint = clientHitPoint;
				length = Vector3.DistanceBetween( GameObject.WorldPosition, clientHitPoint );
				return true;
			}

			if ( TryResolveClosestPointOnObject( nearRoot, clientHitPoint, out var resolved )
			     && IsWithinGrappleRange( resolved )
			     && Vector3.DistanceBetween( resolved, clientHitPoint ) <= surfaceSlack )
			{
				validatedPoint = resolved;
				length = Vector3.DistanceBetween( GameObject.WorldPosition, resolved );
				return true;
			}
		}

		// Local/host play: still allow the richer aim-trace path when this pawn owns the view.
		// Never let it resolve onto a player — without an intended target id that would attach a
		// static rope at a pawn's position instead of a real player hook.
		if ( IsLocalMovementDriver() && TryTraceGrappleAim( out var hostPoint, out var hostObject, out var hostLen, out _, out _, out _ ) )
		{
			var hitPlayer = hostObject.IsValid() && hostObject.Components.Get<PlayerMovement>() is not null;
			if ( !hitPlayer && Vector3.DistanceBetween( hostPoint, clientHitPoint ) <= surfaceSlack && IsWithinGrappleRange( hostPoint ) )
			{
				validatedPoint = hostPoint;
				length = hostLen;
				return true;
			}
		}

		return false;
	}

	bool TryResolveClosestPointOnObject( GameObject root, Vector3 point, out Vector3 closest )
	{
		closest = default;
		if ( root is null || !root.IsValid() )
			return false;

		var bestDist = float.MaxValue;
		var found = false;
		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.IsValid() )
				continue;

			var p = col.FindClosestPoint( point );
			var d = Vector3.DistanceBetween( p, point );
			if ( d >= bestDist )
				continue;

			bestDist = d;
			closest = p;
			found = true;
		}

		return found;
	}

	bool TryConfirmPointOnGrappleObject( GameObject root, Vector3 point )
	{
		if ( root is null || !root.IsValid() )
			return false;

		var slack = TerrainWorldUnits.MetersToEngine( 0.85f );
		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.IsValid() )
				continue;

			if ( Vector3.DistanceBetween( col.FindClosestPoint( point ), point ) <= slack )
				return true;
		}

		return false;
	}

	bool TryFindTaggedRootNearPoint( Vector3 point, float radius, out GameObject root )
	{
		root = null;
		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() )
			return false;

		// Omnidirectional probes — do not depend on host look/camera.
		Vector3[] axes =
		{
			Vector3.Forward, Vector3.Backward,
			Vector3.Left, Vector3.Right,
			Vector3.Up, Vector3.Down,
		};

		foreach ( var axis in axes )
		{
			var a = point - axis * radius;
			var b = point + axis * radius;
			var tr = scene.Trace.Ray( a, b )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();

			if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() || !HasGrappleTag( tr ) )
			{
				tr = scene.Trace.Ray( a, b )
					.IgnoreGameObjectHierarchy( GameObject )
					.UseHitboxes()
					.Run();
			}

			if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() || !HasGrappleTag( tr ) )
				continue;

			root = ResolveGrappleRoot( tr.GameObject );
			if ( root.IsValid() )
				return true;
		}

		return false;
	}

	static bool TryGetAimDirectionFromScreen( CameraComponent cam, Vector2 screenPos, out Vector3 direction )
	{
		direction = default;
		if ( !cam.IsValid() )
			return false;

		// Engine-accurate: screen pixel → near-plane world point → look direction.
		var nearPoint = cam.ScreenToWorld( screenPos );
		direction = (nearPoint - cam.WorldPosition).Normal;
		return direction.LengthSquared > 1e-8f;
	}

	static bool TryWorldToScreen( CameraComponent cam, Vector3 worldPos, out Vector2 screenPos )
	{
		screenPos = default;
		if ( !cam.IsValid() )
			return false;

		// Engine-accurate projection (our manual FOV math was drifting the lock into empty space).
		var px = cam.BBoxToScreenPixels( BBox.FromPositionAndSize( worldPos, 4f ), out var onScreen );
		if ( px.Width < 0.5f && px.Height < 0.5f && !onScreen )
			return false;

		screenPos = new Vector2( px.Left + px.Width * 0.5f, px.Top + px.Height * 0.5f );
		return true;
	}

	bool WasGrapplePressed()
	{
		if ( !string.IsNullOrWhiteSpace( GrappleAction ) && Input.Pressed( GrappleAction ) )
			return true;

		// Fallback: built-in middle mouse action (always present even if custom Grapple binding isn't loaded).
		if ( !string.Equals( GrappleAction, "mouse3", StringComparison.OrdinalIgnoreCase )
		     && Input.Pressed( "mouse3" ) )
			return true;

		return false;
	}

	void PollLengthHoldState()
	{
		IsRetractingRope = false;
		IsDetractingRope = false;

		if ( !GrappleAttached )
			return;

		IsRetractingRope = IsRetractKeyDown();
		IsDetractingRope = IsDetractKeyDown();
	}

	void ApplyLengthHoldDelta( float dt )
	{
		if ( !GrappleAttached )
			return;

		var deltaMeters = 0f;
		var rate = Math.Max( 0.1f, RetractMetersPerSecond );
		if ( IsRetractingRope )
			deltaMeters -= rate * dt;

		if ( IsDetractingRope )
			deltaMeters += rate * dt;

		if ( MathF.Abs( deltaMeters ) < 1e-5f )
			return;

		RequestAdjustLength( TerrainWorldUnits.MetersToEngine( deltaMeters ) );
	}

	static bool IsLengthActionDown( string primary, string altA, string altB, string altC )
	{
		if ( !string.IsNullOrWhiteSpace( primary ) && Input.Down( primary ) )
			return true;
		if ( !string.IsNullOrWhiteSpace( altA ) && !string.Equals( primary, altA, StringComparison.OrdinalIgnoreCase ) && Input.Down( altA ) )
			return true;
		if ( !string.IsNullOrWhiteSpace( altB ) && !string.Equals( primary, altB, StringComparison.OrdinalIgnoreCase ) && Input.Down( altB ) )
			return true;
		if ( !string.IsNullOrWhiteSpace( altC ) && !string.Equals( primary, altC, StringComparison.OrdinalIgnoreCase ) && Input.Down( altC ) )
			return true;
		return false;
	}

	/// <summary>Pro: E retract. Training Wheels: Space (keyboard hold — Jump action is cleared so you don't hop).</summary>
	bool IsRetractKeyDown()
	{
		if ( GrappleControlSchemeStore.NeedsChoice )
			return false;

		if ( GrappleControlSchemeStore.IsTrainingWheels )
			return IsTrainingRetractHeld();

		if ( IsLengthActionDown( RetractAction, "GrappleRetract", "HandHarvest", "Use" ) )
			return true;
		return Input.Down( "BuildSnapNext" );
	}

	/// <summary>Pro: Q detract. Training Wheels: Ctrl via the Duck action (raw Keyboard has no Ctrl key).</summary>
	bool IsDetractKeyDown()
	{
		if ( GrappleControlSchemeStore.NeedsChoice )
			return false;

		if ( GrappleControlSchemeStore.IsTrainingWheels )
			return IsTrainingDetractHeld();

		if ( IsLengthActionDown( DetractAction, "GrappleDetract", "BuildSnapPrev", "Menu" ) )
			return true;
		return false;
	}

	/// <summary>Space is on the raw keyboard list; hold survives Jump being released.</summary>
	static bool IsTrainingRetractHeld() =>
		Input.Keyboard.Down( "space" ) || Input.Keyboard.Down( "SPACE" );

	/// <summary>
	/// Ctrl is not a raw <see cref="Input.Keyboard"/> name in s&box — Duck is the Ctrl bind.
	/// Do not <c>ReleaseAction("Duck")</c> while training or hold never stays true.
	/// </summary>
	bool IsTrainingDetractHeld() =>
		( !string.IsNullOrWhiteSpace( SneakInputAction ) && Input.Down( SneakInputAction ) )
		|| Input.Down( "Duck" );

	void UpdateAimHudVisibility( bool forceHide )
	{
		if ( forceHide )
		{
			IsAimHudActive = false;
			return;
		}

		if ( GrappleAttached )
		{
			IsAimHudActive = true;
			_aimHudHideAt = Time.NowDouble + Math.Max( 0.5, CrosshairIdleHideSeconds );
			return;
		}

		IsAimHudActive = Time.NowDouble < _aimHudHideAt;
	}

	void BumpAimHud()
	{
		_aimHudHideAt = Time.NowDouble + Math.Max( 0.5, CrosshairIdleHideSeconds );
		IsAimHudActive = true;
	}

	/// <summary>
	/// Rope hang used to force <see cref="PlayerController.EnablePressing"/> off, which also
	/// killed WASD air-push until the controller saw move input. We no longer disable it;
	/// this just restores pressing if an older attach left it off.
	/// </summary>
	void UpdatePressingOverride( bool attached )
	{
		if ( attached )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		if ( !_pressingOverrideActive )
		{
			_controller.EnablePressing = true;
			return;
		}

		_controller.EnablePressing = _savedEnablePressing;
		_pressingOverrideActive = false;
	}

	/// <summary>
	/// Walk/run air-control clamps speed to ~110–320, which tops a pendulum out around 45°.
	/// While dangling, kill air-friction / brake and body damping so pumped speed can carry over the top.
	/// Look + PreInput stay on (do not toggle UseInputControls — that would stop WASD sampling).
	/// </summary>
	void TickGrappleControllerOverride()
	{
		_controller ??= Components.Get<PlayerController>();
		// Player attach is not a swing — the attacker keeps normal air friction and damping.
		var airborne = GrappleAttached && !IsPlayerGrappleAttach
		               && _controller is not null && _controller.IsValid() && !_controller.IsOnGround;
		ApplyGrappleControllerOverride( airborne );
	}

	void ApplyGrappleControllerOverride( bool airborneGrapple )
	{
		_controller ??= Components.Get<PlayerController>();
		var body = ResolveGrappleBody();

		if ( airborneGrapple )
		{
			if ( _controller is not null && _controller.IsValid() )
			{
				if ( !_grappleControllerOverrideActive )
				{
					_savedAirFriction = _controller.AirFriction;
					_savedBrakePower = _controller.BrakePower;
					_grappleControllerOverrideActive = true;
				}

				_controller.AirFriction = 0f;
				_controller.BrakePower = 0f;
			}

			if ( body is not null && body.IsValid() )
			{
				if ( !_grappleDampingOverrideActive )
				{
					_savedLinearDamping = body.LinearDamping;
					_grappleDampingOverrideActive = true;
				}

				body.LinearDamping = 0f;
			}

			return;
		}

		if ( _grappleControllerOverrideActive && _controller is not null && _controller.IsValid() )
		{
			_controller.AirFriction = _savedAirFriction;
			_controller.BrakePower = _savedBrakePower;
			_grappleControllerOverrideActive = false;
		}

		if ( _grappleDampingOverrideActive && body is not null && body.IsValid() )
		{
			body.LinearDamping = _savedLinearDamping;
			_grappleDampingOverrideActive = false;
		}
	}

	void DrainAirborneStamina( float dt )
	{
		if ( AirborneStaminaPerSecond <= 0f || _vitals is null )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is not null && _controller.IsOnGround )
		{
			FlushAirborneStaminaDebt();
			return;
		}

		_airborneStaminaDebt += AirborneStaminaPerSecond * dt;
		if ( _airborneStaminaDebt < 0.5f )
			return;

		FlushAirborneStaminaDebt();
	}

	void FlushAirborneStaminaDebt()
	{
		if ( _airborneStaminaDebt <= 1e-4f || _vitals is null )
		{
			_airborneStaminaDebt = 0f;
			return;
		}

		var debt = _airborneStaminaDebt;
		_airborneStaminaDebt = 0f;
		_vitals.RequestVitalsDelta( 0f, -debt );
	}

	void RequestAttach( Vector3 hitPoint, Guid intendedPlayerId )
	{
		if ( AttachStaminaCost > 0f && _vitals is not null && !_vitals.CanAffordStamina( AttachStaminaCost ) )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: attach failed — stamina." );
			return;
		}

		var grappleId = _equipment?.GetSlotResourceId( EquipmentSlot.Grapple ) ?? string.Empty;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerTryAttach( hitPoint, grappleId, intendedPlayerId );
			return;
		}

		RpcRequestAttach( hitPoint, grappleId, intendedPlayerId );
	}

	void RequestDetach()
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerDetach( "toggle" );
			return;
		}

		RpcRequestDetach();
	}

	void RequestAdjustLength( float deltaEngine )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerAdjustLength( deltaEngine );
			return;
		}

		RpcRequestAdjustLength( deltaEngine );
	}

	[Rpc.Host]
	void RpcRequestAttach( Vector3 hitPoint, string clientGrappleResourceId, Guid intendedPlayerId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerTryAttach( hitPoint, clientGrappleResourceId, intendedPlayerId );
	}

	[Rpc.Host]
	void RpcRequestDetach()
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerDetach( "toggle" );
	}

	[Rpc.Host]
	void RpcRequestAdjustLength( float deltaEngine )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerAdjustLength( deltaEngine );
	}

	void ServerTryAttach( Vector3 clientHitPoint, string clientGrappleResourceId = null, Guid intendedPlayerId = default )
	{
		if ( GrappleAttached )
			return;

		if ( IsGrappleVictimCooldownActive() || IsHeldByPlayerGrapple )
			return;

		if ( !HasGrappleEquipped() )
		{
			// Paperdoll RPC can lag behind the client's local equip — accept a valid client-reported hook.
			if ( _equipment is null )
				_equipment = Components.Get<PlayerEquipment>();

			if ( _equipment is null || !_equipment.HostAcceptClientGrappleEquip( clientGrappleResourceId ) )
			{
				if ( LogGrapple )
					Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: host rejected attach — no grapple equipped." );
				return;
			}

			if ( LogGrapple )
				Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: host mirrored client grapple '{clientGrappleResourceId}'." );
		}

		if ( !TryValidateAttachPoint( clientHitPoint, intendedPlayerId, out var validatedPoint, out var length, out var playerTargetId ) )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: host rejected attach." );
			return;
		}

		if ( !HostTrySpendAttachStamina() )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: host attach stamina reject." );
			return;
		}

		GrappleAttached = true;
		GrappleAttachWorldPoint = validatedPoint;
		GrappleRopeLengthEngine = length;
		ClearGrapplePlayerAttachState();

		if ( playerTargetId != Guid.Empty && TryResolveGrapplePlayerTarget( playerTargetId, out var target ) )
		{
			GrappleAttachPlayerId = playerTargetId;
			GrappleAttachLocalOffset = target.WorldTransform.PointToLocal( validatedPoint );
			target.Components.Get<PlayerMovement>()?.HostNotifyGrappledByPlayer( GameObject.Id );
		}

		if ( LogGrapple )
		{
			var kind = playerTargetId != Guid.Empty ? "player" : "surface";
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: attached ({kind}) len={TerrainWorldUnits.EngineToMeters( length ):0.##}m" );
		}
	}

	/// <summary>
	/// Host attach cost. Must not use <see cref="PlayerVitals.TrySpendStamina"/> — that refuses
	/// <see cref="GameObject.IsProxy"/> pawns (joining clients), so attach never set <see cref="GrappleAttached"/>.
	/// Match combat: spend through <see cref="VitalsAuthority.TryApplyDeltas"/>.
	/// </summary>
	bool HostTrySpendAttachStamina()
	{
		if ( AttachStaminaCost <= 0f )
			return true;

		_vitals ??= Components.Get<PlayerVitals>();
		if ( _vitals is null )
			return true;

		if ( _vitals.InfiniteStaminaDebug )
			return true;

		if ( !_vitals.HasStaminaFor( AttachStaminaCost ) )
			return false;

		if ( VitalsAuthority.Instance is { } auth )
			return auth.TryApplyDeltas( GameObject, 0f, -AttachStaminaCost, _vitals );

		// Offline / no authority: TrySpendStamina only works for non-proxy (host's own pawn).
		return _vitals.TrySpendStamina( AttachStaminaCost );
	}

	void ServerDetach( string reason )
	{
		if ( !GrappleAttached )
			return;

		// Rope held a player — release them before the attach state is wiped.
		if ( GrappleAttachPlayerId != Guid.Empty
		     && TryResolveGrapplePlayerTarget( GrappleAttachPlayerId, out var heldPlayer ) )
			heldPlayer.Components.Get<PlayerMovement>()?.HostClearGrappledBy( GameObject.Id );

		GrappleAttached = false;
		GrappleAttachWorldPoint = default;
		GrappleRopeLengthEngine = 0f;
		ClearGrapplePlayerAttachState();
		_airborneStaminaDebt = 0f;
		_grappleReleaseAir = true;

		if ( LogGrapple )
			Log.Info( $"[PlayerMovement.Grapple] {GameObject.Name}: detached ({reason})" );
	}

	void ServerAdjustLength( float deltaEngine )
	{
		if ( !GrappleAttached || MathF.Abs( deltaEngine ) < 1e-5f )
			return;

		if ( !HasGrappleEquipped() )
		{
			ServerDetach( "unequipped" );
			return;
		}

		var min = GetMinLengthEngine();
		var max = GetHardMaxLengthEngine();
		GrappleRopeLengthEngine = Math.Clamp( GrappleRopeLengthEngine + deltaEngine, min, max );
	}

	Vector3 ResolveEyePosition()
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is not null )
		{
			// Pawn eye height — not camera (third-person camera is offset behind the body).
			return GameObject.WorldPosition + Vector3.Up * Math.Max( 8f, _controller.BodyHeight - _controller.EyeDistanceFromTop );
		}

		return GameObject.WorldPosition + Vector3.Up * 64f;
	}

	static bool HasGrappleTag( SceneTraceResult tr )
	{
		if ( tr.HasTag( GrappleSurfaceTag ) )
			return true;

		for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
		{
			if ( ObjectHasGrappleTag( go ) )
				return true;
		}

		return false;
	}

	static bool ObjectHasGrappleTag( GameObject go )
	{
		if ( !go.IsValid() )
			return false;

		// Prefab Tags only (e.g. "grapple" on temp_tree_2 / temp_tree_3).
		return go.Tags.Has( GrappleSurfaceTag );
	}

	/// <summary>
	/// Ray starts at the pawn eyes; direction follows the active view camera (crosshair look).
	/// </summary>
	bool TryGetAimRayFromPlayer( out Vector3 origin, out Vector3 direction )
	{
		origin = ResolveEyePosition();
		direction = default;

		var cam = BuildViewCamera.Resolve( GameObject );
		if ( cam.IsValid() )
		{
			direction = cam.WorldRotation.Forward.Normal;
			return direction.LengthSquared > 1e-8f;
		}

		// Last resort: pawn facing if no scene/view camera is available yet.
		direction = GameObject.WorldRotation.Forward.WithZ( 0f );
		if ( direction.LengthSquared < 1e-8f )
			direction = GameObject.WorldRotation.Forward;

		direction = direction.Normal;
		return direction.LengthSquared > 1e-8f;
	}

	void DrawRopeIfNeeded()
	{
		// Ledge mantle broke the rope this instant — don't draw it while the host detach lands.
		if ( !DrawDebugRope || !GrappleAttached || IsGrappleLedgePulling )
			return;

		var from = ResolveLeftArmWorldPoint();
		DebugOverlay.Line( from, ResolveGrappleAttachWorldPoint(), Color.Black, 0f );
	}

	void DrawSpeedDebugIfNeeded()
	{
		if ( !ShowSpeedDebug || !GrappleAttached )
			return;

		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		var body = _controller?.Body;
		if ( body is null || !body.IsValid() )
			body = Components.Get<Rigidbody>();

		if ( body is null || !body.IsValid() )
			return;

		var vel = body.Velocity;
		var speed = vel.Length;
		var horizontal = vel.WithZ( 0f );
		var hSpeed = horizontal.Length;

		var attach = ResolveGrappleAttachWorldPoint();
		var toPlayer = GameObject.WorldPosition - attach;
		var dist = toPlayer.Length;
		var tanSpeed = 0f;
		if ( dist > 1e-4f )
		{
			var radial = toPlayer / dist;
			var vRad = Vector3.Dot( vel, radial );
			tanSpeed = ( vel - radial * vRad ).Length;
		}

		var speedMs = TerrainWorldUnits.EngineToMeters( speed );
		var hMs = TerrainWorldUnits.EngineToMeters( hSpeed );
		var tanMs = TerrainWorldUnits.EngineToMeters( tanSpeed );

		var x = 24f;
		var y = 220f;
		DebugOverlay.ScreenText( new Vector2( x, y ), "[ Grapple ]", size: 12f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"speed  {speed:0} u/s  ({speedMs:0.00} m/s)", size: 14f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"horiz  {hSpeed:0} u/s  ({hMs:0.00} m/s)", size: 14f );
		y += 16f;
		DebugOverlay.ScreenText( new Vector2( x, y ), $"tangent {tanSpeed:0} u/s  ({tanMs:0.00} m/s)", size: 14f );
		y += 16f;
		DebugOverlay.ScreenText(
			new Vector2( x, y ),
			$"vel ({vel.x:0}, {vel.y:0}, {vel.z:0})",
			size: 12f );
	}

	Vector3 ResolveLeftArmWorldPoint()
	{
		var renderer = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( renderer is not null && renderer.IsValid() )
		{
			for ( var i = 0; i < LeftArmBoneCandidates.Length; i++ )
			{
				if ( !renderer.TryGetBoneTransform( LeftArmBoneCandidates[i], out var tx ) )
					continue;

				if ( tx.Position.LengthSquared > 1e-6f )
					return tx.Position;
			}
		}

		// Fallback: left-of-chest offset in pawn space.
		return GameObject.WorldPosition
		       + GameObject.WorldRotation.Left * TerrainWorldUnits.MetersToEngine( 0.35f )
		       + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.2f );
	}
}
