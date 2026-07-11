using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Rope-swing grapple: aim crosshair, attach/detach, length control, and host-validated attach state.
/// Swing constraint / air push live on <see cref="PlayerMovement"/>.
/// </summary>
[Title( "Player Grapple" )]
public sealed class PlayerGrapple : Component
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

	[Property, Group( "Input" )] public string GrappleAction { get; set; } = "mouse3";
	/// <summary>Shorten rope (default E).</summary>
	[Property, Group( "Input" )] public string RetractAction { get; set; } = "GrappleRetract";
	/// <summary>Pay out / expand max rope length (default Q).</summary>
	[Property, Group( "Input" )] public string DetractAction { get; set; } = "GrappleDetract";

	[Property, Group( "Range" ), Title( "Max Range (meters)" )]
	public float MaxRangeMeters { get; set; } = 30f;

	[Property, Group( "Rope" ), Title( "Retract (m/s)" )]
	public float RetractMetersPerSecond { get; set; } = 2.5f;

	[Property, Group( "Rope" ), Title( "Detract (m/s)" )]
	public float DetractMetersPerSecond { get; set; } = 8f;

	[Property, Group( "Rope" ), Title( "Hard Max Length (meters)" )]
	public float HardMaxLengthMeters { get; set; } = 30f;

	[Property, Group( "Rope" ), Title( "Min Length (meters)" )]
	public float MinLengthMeters { get; set; } = 1f;

	[Property, Group( "Stamina" )]
	public float AttachStaminaCost { get; set; } = 8f;

	[Property, Group( "Stamina" ), Title( "Airborne Drain (stamina/s)" )]
	public float AirborneStaminaPerSecond { get; set; } = 1.5f;

	[Property, Group( "Swing" ), Title( "Attach Velocity Scale" )]
	public float AttachVelocityScale { get; set; } = 1.08f;

	/// <summary>Base WASD accel. Hold uses <see cref="HoldPushScale"/>; pumps with the arc use <see cref="PumpWithArcMult"/>.</summary>
	[Property, Group( "Swing" ), Title( "Air Push (engine u/s²)" )]
	public float AirPushAcceleration { get; set; } = 110f;

	/// <summary>Hold / start thrust as a fraction of air push (keeps parked angle near hang).</summary>
	[Property, Group( "Swing" ), Title( "Hold Push Scale" )]
	public float HoldPushScale { get; set; } = 0.45f;

	/// <summary>Multiplier when WASD aligns with current swing velocity (timed pumps build speed).</summary>
	[Property, Group( "Swing" ), Title( "Pump With Arc Mult" )]
	public float PumpWithArcMult { get; set; } = 3.2f;

	/// <summary>Min tangent speed (engine u/s) before pump bonus applies.</summary>
	[Property, Group( "Swing" ), Title( "Pump Min Speed (u/s)" )]
	public float PumpMinSpeed { get; set; } = 35f;

	/// <summary>Hold thrust fades after this angle from vertical hang (degrees).</summary>
	[Property, Group( "Swing" ), Title( "Hold Max Angle (deg)" )]
	public float HoldMaxAngleDegrees { get; set; } = 16f;

	/// <summary>Extra accel multiplier when WASD fights current swing direction (bleeds momentum).</summary>
	[Property, Group( "Swing" ), Title( "Fight Swing Brake Mult" )]
	public float FightSwingBrakeMult { get; set; } = 1.45f;

	/// <summary>Softens hold / fight push as tangential speed rises.</summary>
	[Property, Group( "Swing" ), Title( "Swing Speed Soften (u/s)" )]
	public float SwingSpeedSoften { get; set; } = 260f;

	/// <summary>Softer falloff for with-arc pumps so late pumps still add speed.</summary>
	[Property, Group( "Swing" ), Title( "Pump Speed Soften (u/s)" )]
	public float PumpSpeedSoften { get; set; } = 1100f;

	/// <summary>Light tangential damping while no WASD (settles toward hang).</summary>
	[Property, Group( "Swing" ), Title( "Coast Damping (1/s)" )]
	public float SwingCoastDamping { get; set; } = 0.18f;

	[Property, Group( "Aim" ), Title( "Crosshair Idle Hide (seconds)" )]
	public float CrosshairIdleHideSeconds { get; set; } = 10f;

	/// <summary>Soft lock: pick the best in-range tagged surface near the crosshair, not only the exact center ray.</summary>
	[Property, Group( "Aim Assist" ), Title( "Enabled" )]
	public bool AimAssistEnabled { get; set; } = true;

	/// <summary>Screen-pixel radius around the crosshair where assist may steal a target.</summary>
	[Property, Group( "Aim Assist" ), Title( "Radius (pixels)" )]
	public float AssistRadiusPixels { get; set; } = 72f;

	/// <summary>
	/// Secondary score weight (meters). Crosshair closeness wins first; this breaks ties toward nearer surfaces.
	/// </summary>
	[Property, Group( "Aim Assist" ), Title( "Distance Bias" )]
	public float AssistDistanceBias { get; set; } = 0.05f;

	[Property, Group( "Aim Assist" ), Title( "Sample Rings" )]
	public int AssistSampleRings { get; set; } = 3;

	[Property, Group( "Aim Assist" ), Title( "Samples Per Ring" )]
	public int AssistSamplesPerRing { get; set; } = 8;

	/// <summary>
	/// How many pixels closer to the crosshair a new candidate must be before we leave the sticky lock.
	/// </summary>
	[Property, Group( "Aim Assist" ), Title( "Stick Break (pixels)" )]
	public float AssistStickBreakPixels { get; set; } = 18f;

	/// <summary>Screen-space smoothing rate for the lock reticle (higher = snappier).</summary>
	[Property, Group( "Aim Assist" ), Title( "Lock Smooth" )]
	public float AssistLockSmooth { get; set; } = 14f;

	[Property, Group( "Visual" )]
	public bool DrawDebugRope { get; set; } = true;

	[Property, Group( "Debug" )]
	public bool LogGrapple { get; set; }

	/// <summary>Host-synced: rope currently attached.</summary>
	[Sync] public bool IsAttached { get; private set; }

	/// <summary>Host-synced world attach point (static for v1).</summary>
	[Sync] public Vector3 AttachWorldPoint { get; private set; }

	/// <summary>Host-synced current rope length in engine units.</summary>
	[Sync] public float RopeLengthEngine { get; private set; }

	/// <summary>Local aim UI: crosshair should draw.</summary>
	public bool IsAimHudActive { get; private set; }

	/// <summary>Local aim UI: look ray hits a tagged surface within range.</summary>
	public bool HasValidAimTarget { get; private set; }

	/// <summary>Local aim hit point when <see cref="HasValidAimTarget"/>.</summary>
	public Vector3 AimHitWorldPoint { get; private set; }

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

	PlayerVitals _vitals;
	PlayerController _controller;
	PlayerEquipment _equipment;
	double _aimHudHideAt;
	float _airborneStaminaDebt;
	bool _savedEnablePressing = true;
	bool _pressingOverrideActive;

	protected override void OnStart()
	{
		base.OnStart();
		_vitals = Components.Get<PlayerVitals>();
		_controller = Components.Get<PlayerController>();
		_equipment = Components.Get<PlayerEquipment>();
		RefreshTuningFromEquipment();

		if ( LogGrapple )
			Log.Info( $"[PlayerGrapple] {GameObject.Name}: ready (action '{GrappleAction}', range {MaxRangeMeters:0.#}m)." );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !IsLocalDriver() )
		{
			DrawRopeIfNeeded();
			return;
		}

		RefreshTuningFromEquipment();

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
			if ( IsAttached )
				RequestDetach();
			DrawRopeIfNeeded();
			return;
		}

		UpdateAimTrace();
		PollToggleInput();
		PollLengthHoldState();
		UpdateAimHudVisibility( forceHide: false );
		UpdatePressingOverride( IsAttached );
		DrawRopeIfNeeded();
	}

	protected override void OnPreRender()
	{
		base.OnPreRender();

		// After combat teardrop (OnUpdate) so the yellow grapple reticle sits on top.
		if ( !IsLocalDriver() )
			return;

		if ( !HasGrappleEquipped() )
			return;

		var menu = Components.Get<PlayerGameMenuController>();
		if ( menu is not null && menu.IsMenuOpen )
			return;

		DrawCrosshairIfNeeded();
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();

		if ( !IsLocalDriver() )
			return;

		if ( IsAttached )
		{
			ApplyLengthHoldDelta( Time.Delta );
			DrainAirborneStamina( Time.Delta );
		}
	}

	public float GetMaxRangeEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, MaxRangeMeters ) );

	public float GetMinLengthEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( 0.25f, MinLengthMeters ) );

	public float GetHardMaxLengthEngine() =>
		TerrainWorldUnits.MetersToEngine( Math.Max( MinLengthMeters, HardMaxLengthMeters ) );

	/// <summary>Called from <see cref="PlayerVitals.ApplyDamageAfterArmor"/> on the host when HP is lost.</summary>
	public void NotifyDamaged( float damageAfterArmor )
	{
		if ( damageAfterArmor <= 0f || !IsAttached )
			return;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		ServerDetach( "damage" );
	}

	bool IsLocalDriver()
	{
		if ( _vitals is null )
			_vitals = Components.Get<PlayerVitals>();

		return _vitals is not null && _vitals.IsLocalInputOwnedPawn();
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
		{
			MaxRangeMeters = profile.GrappleMaxRangeMeters;
			HardMaxLengthMeters = profile.GrappleMaxRangeMeters;
		}

		if ( profile.GrappleRetractMetersPerSecond > 0f )
			RetractMetersPerSecond = profile.GrappleRetractMetersPerSecond;

		if ( profile.GrappleDetractMetersPerSecond > 0f )
			DetractMetersPerSecond = profile.GrappleDetractMetersPerSecond;

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
			var sameObject = hitObject.IsValid() && IsSameGrappleObject( hitObject, _stickyAimObject );
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

		if ( IsAttached )
		{
			RequestDetach();
			return;
		}

		if ( !HasValidAimTarget )
		{
			if ( LogGrapple )
				LogAimRejectReason();
			return;
		}

		RequestAttach( AimHitWorldPoint );
	}

	void LogAimRejectReason()
	{
		if ( !TryGetAimRayFromPlayer( out var origin, out var direction ) )
		{
			Log.Info( $"[PlayerGrapple] {GameObject.Name}: aim reject — no look direction (view camera)." );
			return;
		}

		var maxRange = GetMaxRangeEngine();
		var tr = TraceAimRay( origin, direction, maxRange );
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() )
		{
			Log.Info( $"[PlayerGrapple] {GameObject.Name}: aim reject — ray miss (cast {TerrainWorldUnits.EngineToMeters( maxRange ):0.#}m from player)." );
			return;
		}

		var distPawn = Vector3.DistanceBetween( GameObject.WorldPosition, tr.HitPosition );
		var tagged = HasGrappleTag( tr );
		Log.Info(
			$"[PlayerGrapple] {GameObject.Name}: aim reject — hit '{tr.GameObject.Name}' " +
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

			if ( !HasGrappleTag( tr ) )
				return;

			var distPawn = Vector3.DistanceBetween( pawnPos, tr.HitPosition );
			if ( distPawn > maxRange )
				return;

			var root = ResolveGrappleRoot( tr.GameObject );
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
		     && HasGrappleTag( centerTr ) )
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

	static bool IsSameGrappleObject( GameObject a, GameObject b )
	{
		if ( !a.IsValid() || !b.IsValid() )
			return false;

		return ResolveGrappleRoot( a ) == ResolveGrappleRoot( b );
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
	/// Host check: client snap must be in range, in the assist cone, and on a tagged surface.
	/// Uses the same closest-to-ray resolve as the client (not a strict camera→point face hit).
	/// </summary>
	bool TryValidateAttachPoint( Vector3 clientHitPoint, out Vector3 validatedPoint, out float length )
	{
		validatedPoint = default;
		length = 0f;

		if ( !IsWithinGrappleRange( clientHitPoint ) )
			return false;

		if ( !TryGetAimRayFromPlayer( out var eyeOrigin, out var look ) )
			return false;

		var cam = BuildViewCamera.Resolve( GameObject );
		var rayOrigin = cam.IsValid() ? cam.WorldPosition : eyeOrigin;
		var toClient = clientHitPoint - rayOrigin;
		var rayDist = toClient.Length;
		if ( rayDist < 1e-3f )
			return false;

		var dir = toClient / rayDist;
		var maxAngleDeg = GetAssistMaxAngleDegrees() + 4f;
		if ( Vector3.GetAngle( look, dir ) > maxAngleDeg )
			return false;

		var surfaceSlack = TerrainWorldUnits.MetersToEngine( 3f );

		// Same resolve path as the aim HUD — accept if client matches host snap.
		if ( TryTraceGrappleAim( out var hostPoint, out _, out var hostLen, out _, out _, out _ ) )
		{
			if ( Vector3.DistanceBetween( hostPoint, clientHitPoint ) <= surfaceSlack && IsWithinGrappleRange( hostPoint ) )
			{
				validatedPoint = hostPoint;
				length = hostLen;
				return true;
			}
		}

		// Fallback: confirm a tagged surface near the client point, then snap with closest-to-ray.
		var tr = TraceAimRay( rayOrigin, dir, rayDist + TerrainWorldUnits.MetersToEngine( 1f ) );
		if ( !tr.Hit || tr.GameObject is null || !tr.GameObject.IsValid() || !HasGrappleTag( tr ) )
		{
			// Point may sit on a side face the center ray misses — probe colliders near the client point.
			if ( !TryFindTaggedRootNearPoint( clientHitPoint, surfaceSlack, out var nearRoot ) )
				return false;

			if ( !TryResolveAimPointOnObject( nearRoot, out var resolved, out _, out _ ) || !IsWithinGrappleRange( resolved ) )
				return false;

			if ( Vector3.DistanceBetween( resolved, clientHitPoint ) > surfaceSlack )
			{
				// Still accept the client point if it lies on that object's collider.
				if ( !TryConfirmPointOnGrappleObject( nearRoot, clientHitPoint ) )
					return false;

				validatedPoint = clientHitPoint;
				length = Vector3.DistanceBetween( GameObject.WorldPosition, clientHitPoint );
				return true;
			}

			validatedPoint = resolved;
			length = Vector3.DistanceBetween( GameObject.WorldPosition, resolved );
			return true;
		}

		var root = ResolveGrappleRoot( tr.GameObject );
		if ( TryResolveAimPointOnObject( root, out var snap, out _, out _ ) && IsWithinGrappleRange( snap ) )
		{
			if ( Vector3.DistanceBetween( snap, clientHitPoint ) <= surfaceSlack
			     || TryConfirmPointOnGrappleObject( root, clientHitPoint ) )
			{
				validatedPoint = snap;
				length = Vector3.DistanceBetween( GameObject.WorldPosition, snap );
				return true;
			}
		}

		if ( Vector3.DistanceBetween( tr.HitPosition, clientHitPoint ) <= surfaceSlack && IsWithinGrappleRange( tr.HitPosition ) )
		{
			validatedPoint = tr.HitPosition;
			length = Vector3.DistanceBetween( GameObject.WorldPosition, tr.HitPosition );
			return true;
		}

		return false;
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

		// Short probe through the point along look so we can pick up side-face snaps.
		if ( !TryGetAimRayFromPlayer( out _, out var look ) )
			look = GameObject.WorldRotation.Forward;

		var a = point - look.Normal * radius;
		var b = point + look.Normal * radius;
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
			return false;

		root = ResolveGrappleRoot( tr.GameObject );
		return root.IsValid();
	}

	float GetAssistMaxAngleDegrees()
	{
		if ( !AimAssistEnabled || AssistRadiusPixels < 1f )
			return 0.75f;

		var cam = BuildViewCamera.Resolve( GameObject );
		if ( !cam.IsValid() )
			return 8f;

		var rect = cam.ScreenRect;
		var halfH = Math.Max( 1f, rect.Height * 0.5f );
		var halfFovRad = Math.Clamp( cam.FieldOfView, 20f, 110f ) * 0.5f * ( MathF.PI / 180f );
		var angleRad = MathF.Atan( ( AssistRadiusPixels / halfH ) * MathF.Tan( halfFovRad ) );
		return angleRad * ( 180f / MathF.PI );
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

		if ( !IsAttached )
			return;

		IsRetractingRope = IsLengthActionDown( RetractAction, "GrappleRetract", "Use", "e" );
		IsDetractingRope = IsLengthActionDown( DetractAction, "GrappleDetract", "Menu", "q" );
	}

	void ApplyLengthHoldDelta( float dt )
	{
		if ( !IsAttached )
			return;

		var deltaMeters = 0f;
		if ( IsRetractingRope )
			deltaMeters -= Math.Max( 0.1f, RetractMetersPerSecond ) * dt;

		if ( IsDetractingRope )
			deltaMeters += Math.Max( 0.1f, DetractMetersPerSecond ) * dt;

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

	void UpdateAimHudVisibility( bool forceHide )
	{
		if ( forceHide )
		{
			IsAimHudActive = false;
			return;
		}

		if ( IsAttached )
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

	void UpdatePressingOverride( bool attached )
	{
		if ( _controller is null )
			_controller = Components.Get<PlayerController>();

		if ( _controller is null )
			return;

		if ( attached )
		{
			if ( !_pressingOverrideActive )
			{
				_savedEnablePressing = _controller.EnablePressing;
				_pressingOverrideActive = true;
			}

			_controller.EnablePressing = false;
			return;
		}

		if ( !_pressingOverrideActive )
			return;

		_controller.EnablePressing = _savedEnablePressing;
		_pressingOverrideActive = false;
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

	void RequestAttach( Vector3 hitPoint )
	{
		if ( AttachStaminaCost > 0f && _vitals is not null && !_vitals.CanAffordStamina( AttachStaminaCost ) )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerGrapple] {GameObject.Name}: attach failed — stamina." );
			return;
		}

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			ServerTryAttach( hitPoint );
			return;
		}

		RpcRequestAttach( hitPoint );
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
	void RpcRequestAttach( Vector3 hitPoint )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		ServerTryAttach( hitPoint );
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

	void ServerTryAttach( Vector3 clientHitPoint )
	{
		if ( IsAttached )
			return;

		if ( !HasGrappleEquipped() )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerGrapple] {GameObject.Name}: host rejected attach — no grapple equipped." );
			return;
		}

		if ( !TryValidateAttachPoint( clientHitPoint, out var validatedPoint, out var length ) )
		{
			if ( LogGrapple )
				Log.Info( $"[PlayerGrapple] {GameObject.Name}: host rejected attach." );
			return;
		}

		if ( AttachStaminaCost > 0f )
		{
			if ( _vitals is null )
				_vitals = Components.Get<PlayerVitals>();

			if ( _vitals is not null && !_vitals.TrySpendStamina( AttachStaminaCost ) )
			{
				if ( LogGrapple )
					Log.Info( $"[PlayerGrapple] {GameObject.Name}: host attach stamina reject." );
				return;
			}
		}

		IsAttached = true;
		AttachWorldPoint = validatedPoint;
		RopeLengthEngine = length;

		if ( LogGrapple )
			Log.Info( $"[PlayerGrapple] {GameObject.Name}: attached len={TerrainWorldUnits.EngineToMeters( length ):0.##}m" );
	}

	void ServerDetach( string reason )
	{
		if ( !IsAttached )
			return;

		IsAttached = false;
		AttachWorldPoint = default;
		RopeLengthEngine = 0f;
		_airborneStaminaDebt = 0f;

		if ( LogGrapple )
			Log.Info( $"[PlayerGrapple] {GameObject.Name}: detached ({reason})" );
	}

	void ServerAdjustLength( float deltaEngine )
	{
		if ( !IsAttached || MathF.Abs( deltaEngine ) < 1e-5f )
			return;

		if ( !HasGrappleEquipped() )
		{
			ServerDetach( "unequipped" );
			return;
		}

		var min = GetMinLengthEngine();
		var max = GetHardMaxLengthEngine();
		var previous = RopeLengthEngine;
		RopeLengthEngine = Math.Clamp( RopeLengthEngine + deltaEngine, min, max );

		if ( LogGrapple && MathF.Abs( RopeLengthEngine - previous ) > 1e-3f )
		{
			Log.Info(
				$"[PlayerGrapple] {GameObject.Name}: rope length " +
				$"{TerrainWorldUnits.EngineToMeters( previous ):0.##}m → {TerrainWorldUnits.EngineToMeters( RopeLengthEngine ):0.##}m " +
				$"(max {TerrainWorldUnits.EngineToMeters( max ):0.#}m)" );
		}
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

		// TagSet.Has is the project-standard check (see buildpreview / worlddrop).
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

	void DrawCrosshairIfNeeded()
	{
		if ( !IsAimHudActive )
			return;

		var cam = BuildViewCamera.Resolve( GameObject );
		if ( !cam.IsValid() )
			return;

		var rect = cam.ScreenRect;
		var center = new Vector2( rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f );
		var yellow = new Color( 1f, 0.92f, 0.2f, 0.95f );

		// Yellow outer ring stays on the true crosshair.
		const float outerRadius = 5f;
		DrawHudCircleOutline( cam, center, outerRadius, 20, 1.75f, yellow );

		if ( !HasValidAimTarget )
			return;

		// Yellow lock dot: centered in the bullseye when looking straight at a target,
		// otherwise slides to the assist snap on the object.
		var lockPos = center;
		if ( _aimHitScreenValid )
			lockPos = _aimHitScreenPoint;
		else if ( TryWorldToScreen( cam, AimHitWorldPoint, out var projected ) )
			lockPos = projected;

		// Dead-center aim → keep the lock in the bullseye (avoid 1px projection jitter).
		const float centerSnapPixels = 3.5f;
		if ( (lockPos - center).Length <= centerSnapPixels )
			lockPos = center;

		const float lockDiameter = 4f;
		cam.Overlay.DrawCircle( lockPos, new Vector2( lockDiameter, lockDiameter ), yellow );
	}

	static void DrawHudCircleOutline(
		CameraComponent cam,
		Vector2 center,
		float radius,
		int segments,
		float lineWidth,
		Color color )
	{
		if ( !cam.IsValid() )
			return;

		segments = Math.Clamp( segments, 8, 64 );
		radius = Math.Max( 1f, radius );
		var prev = center + new Vector2( radius, 0f );
		for ( var i = 1; i <= segments; i++ )
		{
			var a = i * ( MathF.PI * 2f / segments );
			var next = center + new Vector2( MathF.Cos( a ), MathF.Sin( a ) ) * radius;
			cam.Overlay.DrawLine( prev, next, lineWidth, color );
			prev = next;
		}
	}

	void DrawRopeIfNeeded()
	{
		if ( !DrawDebugRope || !IsAttached )
			return;

		var from = ResolveLeftArmWorldPoint();
		DebugOverlay.Line( from, AttachWorldPoint, Color.Black, 0f );
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
