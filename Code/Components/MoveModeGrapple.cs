using System;
using Sandbox;
using Sandbox.Movement;

namespace Game;

/// <summary>Last strong mouse direction used for attack-aim hint (read from <see cref="MoveModeGrapple.AttackAimCardinal"/>).</summary>
public enum GrappleAttackAimCardinal
{
	None,
	Left,
	Right,
	Up,
	Down
}

public sealed class MoveModeGrapple : MoveMode, IGrappleStop
{
	[Property] public float Range { get; set; } = 3000f;
	[Property] public float AimAssistRadius { get; set; } = 48f;

	[Property] public float SwingControl { get; set; } = 95f;
	[Property] public float RopeTension { get; set; } = 420.0f;
	[Property] public float MaxRopeCorrection { get; set; } = 12000f;
	[Property] public float TautInputDeadzone { get; set; } = 8f;
	[Property] public float TautReleaseDistance { get; set; } = 6f;
	[Property] public float OutwardSuppressionSpeed { get; set; } = 120f;
	[Property] public float MinTautInputScale { get; set; } = 0.2f;
	[Property] public float TautRadialDamping { get; set; } = 5f;

	[Property] public float InitialRopeShorten { get; set; } = 0f;
	[Property] public float MinRopeLength { get; set; } = 8f;
	[Property] public float MaxAllowedStretch { get; set; } = 0f;

	[Property] public float StretchHoldPull { get; set; } = 1000f;

	[Property] public float MomentumChangeThreshold { get; set; } = 0.2f;
	[Property] public float LowMomentumSwingControl { get; set; } = 42f;
	[Property] public float SpeedBoostStart { get; set; } = 450f;
	[Property] public float SpeedBoostEnd { get; set; } = 1800f;
	[Property] public float MaxSwingSpeedBoost { get; set; } = 2.2f;
	[Property] public float MinAlignmentAtHighSpeed { get; set; } = 0.05f;
	[Property] public float MomentumCarryStartSpeed { get; set; } = 500f;
	[Property] public float MomentumCarryEndSpeed { get; set; } = 1600f;
	[Property] public float MaxMomentumCarryAccel { get; set; } = 170f;

	/// <summary>Magnitude of world gravity along -Z used for tangent “deep swing” assist.</summary>
	[Property] public float SwingGravityAccel { get; set; } = 800f;

	/// <summary>Peak extra tangential accel at rope mid-swing toward bottom (combined with tangent gravity).</summary>
	[Property] public float SwingDepthBoostMaxAccel { get; set; } = 115f;

	/// <summary>Exponent on swing depth shaping (higher = more boost deeper in the arc).</summary>
	[Property] public float SwingDepthBoostExponent { get; set; } = 2.2f;

	/// <summary>
	/// While holding retract, tangential swing assists are scaled by this (0 = off, 1 = same as free swing).
	/// Logs showed retract held the whole sample — boosts were intentionally skipped before.
	/// </summary>
	[Property] public float RetractSwingAssistScale { get; set; } = 0.75f;

	[Property] public float RetractSpeed { get; set; } = 520f;
	[Property] public float RetractPullSpeed { get; set; } = 650f;

	/// <summary>When enabled, rope shortens automatically once speed reaches <see cref="AutoRetractMinSpeed"/> (same pull as manual retract).</summary>
	[Property] public bool AutoRetractOnHighSpeed { get; set; } = true;

	/// <summary>World velocity length at or above which automatic retract runs.</summary>
	[Property] public float AutoRetractMinSpeed { get; set; } = 500f;

	/// <summary>Seconds after attach before auto retract can begin (avoids launch impulse triggering immediately).</summary>
	[Property] public float AutoRetractMinGrappleAge { get; set; } = 0.08f;

	[Property] public float AttachRampTime { get; set; } = 0.001f;

	[Property] public float CrosshairSurfaceOffset { get; set; } = 8f;

	/// <summary>Fixed radius for yellow (valid target) and green (grappling) world highlights — always the same size.</summary>
	[Property] public float CrosshairWorldMarkerRadius { get; set; } = 8f;

	/// <summary>HUD reticle sits this far along view from the camera (reads as screen-center, not glued to world props).</summary>
	[Property] public float HudCrosshairForward { get; set; } = 22f;

	[Property] public float HudTeardropCoreRadius { get; set; } = 0.42f;
	[Property] public float HudTeardropTipLength { get; set; } = 0.55f;
	[Property] public float HudTeardropBaseHalfWidth { get; set; } = 0.28f;

	/// <summary>Start grapple traces slightly in front of the body so 3rd-person camera rays do not self-hit the player.</summary>
	[Property] public float AimTraceStartForward { get; set; } = 14f;

	[Property] public Color CrosshairHudColor { get; set; } = Color.White;
	[Property] public Color CrosshairActiveColor { get; set; } = Color.Green;
	[Property] public bool UseActiveCrosshairColor { get; set; } = true;

	[Property] public Color CrosshairHoverColor { get; set; } = Color.Yellow;

	/// <summary>
	/// After this many seconds without grappling (and no grapple button press), the yellow hover marker is hidden until you press grapple again.
	/// </summary>
	[Property] public float CrosshairHoverIdleHideSeconds { get; set; } = 5f;

	[Property] public float AttackAimMouseDeadzone { get; set; } = 0.75f;
	[Property] public float AttackAimAxisDominance { get; set; } = 1.12f;

	/// <summary>
	/// Low-pass time constant on mouse delta (seconds). Higher = less flicker, slightly slower to respond.
	/// </summary>
	[Property] public float AttackAimSmoothSeconds { get; set; } = 0.12f;

	/// <summary>
	/// While already on a horizontal cardinal, vertical must exceed this × |horizontal| to switch axis (and vice versa).
	/// </summary>
	[Property] public float AttackAimAxisStickiness { get; set; } = 1.42f;

	[Property] public float AttackAimDecaySeconds { get; set; } = 0.35f;

	[Property] public bool DrawGrappleCrosshair { get; set; } = true;
	[Property] public bool CrosshairIgnoreDepth { get; set; } = true;

	[Property] public string GrappleButton { get; set; } = "attack2";
	[Property] public string RetractButton { get; set; } = "jump";
	[Property] public string GrappleTag { get; set; } = "grappleable";

	[Property] public bool DrawRope { get; set; } = true;
	[Property] public bool DebugGrappleVelocity { get; set; } = true;
	[Property] public float DebugLogInterval { get; set; } = 0.15f;

	public bool IsGrappling { get; private set; }

	/// <inheritdoc />
	public bool GrappleSwingStaminaDrainActive { get; private set; }

	private bool IsRopeTaut;
	private Vector3 GrapplePoint;
	private float RopeLength;
	private float GrappleAge;
	private float DebugLogTimer;

	private bool HasTarget;
	private Vector3 TargetPoint;
	private Vector3 TargetNormal;

	private float _secondsSinceStrongAttackMouse;
	private Vector2 _lastAttackMouseDelta;
	private Vector2 _attackAimSmoothed;
	private float _secondsSinceGrappleInterest;

	/// <summary>Latest cardinal from mouse movement (for future melee / attack code).</summary>
	public GrappleAttackAimCardinal AttackAimCardinal { get; private set; }

	/// <summary>Last mouse delta that updated <see cref="AttackAimCardinal"/>.</summary>
	public Vector2 LastAttackAimMouseDelta => _lastAttackMouseDelta;

	public override int Score( PlayerController controller )
	{
		return IsGrappling ? 100000 : 0;
	}

	protected override void OnUpdate()
	{
		if ( !IsGrappling )
			UpdateGrappleTarget();

		if ( Input.Pressed( GrappleButton ) )
			TryStartGrapple();

		if ( Input.Released( GrappleButton ) )
			StopGrapple();

		if ( IsGrappling || Input.Pressed( GrappleButton ) )
			_secondsSinceGrappleInterest = 0f;
		else
			_secondsSinceGrappleInterest += Time.Delta;

		if ( IsGrappling )
			GrappleAge += Time.Delta;

		UpdateAttackAimFromMouse();

		if ( IsGrappling && DrawRope )
		{
			Gizmo.Draw.Color = Color.Black;
			Gizmo.Draw.LineThickness = 5f;
			Gizmo.Draw.Line( WorldPosition, GrapplePoint );
		}

		if ( DrawGrappleCrosshair )
			DrawTargetCrosshair();
	}

	public override Vector3 UpdateMove( Rotation eyes, Vector3 input )
	{
		if ( !IsGrappling )
		{
			GrappleSwingStaminaDrainActive = false;
			return Vector3.Zero;
		}

		var toPoint = GrapplePoint - WorldPosition;
		var distance = toPoint.Length;

		if ( distance <= 1f )
		{
			GrappleSwingStaminaDrainActive = false;
			return Vector3.Zero;
		}

		var stamina = Controller is not null ? PlayerStamina.FindForPlayerRoot( Controller.GameObject ) : null;
		var allowGrappleAirControl = stamina is null || stamina.HasStaminaForActions;

		var ropeDir = toPoint.Normal;
		var attachAlpha = Math.Clamp( GrappleAge / AttachRampTime, 0f, 1f );
		var maxRopeDistance = RopeLength + MaxAllowedStretch;
		var currentRadialSpeed = Controller.Velocity.Dot( ropeDir );
		var tautReleaseDistance = Math.Max( TautReleaseDistance, 0f );

		if ( distance >= maxRopeDistance )
			IsRopeTaut = true;
		else if ( distance <= maxRopeDistance - tautReleaseDistance )
			IsRopeTaut = false;

		var tautStart = maxRopeDistance - Math.Max( TautInputDeadzone, 0.01f );
		var tautAlpha = IsRopeTaut ? 1f : Math.Clamp( (distance - tautStart) / Math.Max( TautInputDeadzone, 0.01f ), 0f, 1f );
		var outwardAlpha = Math.Clamp( -currentRadialSpeed / Math.Max( OutwardSuppressionSpeed, 1f ), 0f, 1f );
		var tautInputScale = 1f - (tautAlpha * outwardAlpha * (1f - MinTautInputScale));
		var currentVelocity = Controller.Velocity;
		var currentTangentVelocity = currentVelocity - ropeDir * currentRadialSpeed;
		var currentSpeed = currentVelocity.Length;
		var tangentSpeed = currentTangentVelocity.Length;

		Vector3 tangentMotionDir;

		if ( tangentSpeed > 1f )
			tangentMotionDir = currentTangentVelocity.Normal;
		else
			tangentMotionDir = Vector3.Zero;

		var gravDir = Vector3.Down;
		var ropeDownDot = gravDir.Dot( ropeDir ); // ±1 apex, 0 at horizontal “deep” portions
		var swingDepthFactor = MathF.Pow( 1f - MathF.Abs( ropeDownDot ), SwingDepthBoostExponent );
		var gravityAlongMotion = tangentSpeed > 1f ? gravDir.Dot( tangentMotionDir ) : 0f;

		var naturalTangentAssist =
			tangentSpeed > 1f && gravityAlongMotion > 0f
				? gravityAlongMotion * SwingGravityAccel * swingDepthFactor
				: 0f;

		var depthBoostAccel =
			tangentSpeed > 1f && gravityAlongMotion > 0f ? SwingDepthBoostMaxAccel * swingDepthFactor * gravityAlongMotion : 0f;

		var move = Vector3.Zero;

		var manualRetract = Input.Down( RetractButton );
		var autoVelocityRetract = AutoRetractOnHighSpeed
			&& GrappleAge >= AutoRetractMinGrappleAge
			&& currentSpeed >= AutoRetractMinSpeed;
		var isRetractingRope = manualRetract || autoVelocityRetract;
		var swingAssistScale = isRetractingRope ? Math.Clamp( RetractSwingAssistScale, 0f, 1f ) : 1f;
		if ( isRetractingRope )
		{
			RopeLength -= RetractSpeed * Time.Delta;
			RopeLength = Math.Max( MinRopeLength, RopeLength );

			move += ropeDir * RetractPullSpeed;
		}

		var wishDir =
			eyes.Forward * input.x -
			eyes.Right * input.y;

		wishDir = wishDir.WithZ( 0 );

		if ( allowGrappleAirControl && !wishDir.IsNearZeroLength )
		{
			var tangent = wishDir.Normal - ropeDir * wishDir.Normal.Dot( ropeDir );

			if ( !tangent.IsNearZeroLength )
			{
				tangent = tangent.Normal;

				if ( currentTangentVelocity.Length > 40f )
				{
					var speedRange = Math.Max( SpeedBoostEnd - SpeedBoostStart, 1f );
					var speedAlpha = Math.Clamp( (tangentSpeed - SpeedBoostStart) / speedRange, 0f, 1f );
					var dynamicAlignmentThreshold = MomentumChangeThreshold + (MinAlignmentAtHighSpeed - MomentumChangeThreshold) * speedAlpha;
					var speedBoost = 1f + (MaxSwingSpeedBoost - 1f) * speedAlpha;

					var currentTangentDir = currentTangentVelocity.Normal;
					var alignment = tangent.Dot( currentTangentDir );

					if ( alignment > dynamicAlignmentThreshold )
					{
						var swingAccel = SwingControl * speedBoost * tautInputScale;
						move += tangent * swingAccel;
					}
				}
				else
				{
					var lowSpeedAccel = LowMomentumSwingControl * tautInputScale;
					move += tangent * lowSpeedAccel;
				}
			}
		}

		// Preserve high-speed swing energy through the bottom of the arc.
		// This offsets losses from rope correction / integration without adding hard pulls.
		if ( IsRopeTaut && tangentSpeed > 1f && swingAssistScale > 0f )
		{
			var carryRange = Math.Max( MomentumCarryEndSpeed - MomentumCarryStartSpeed, 1f );
			var carryAlpha = Math.Clamp( (tangentSpeed - MomentumCarryStartSpeed) / carryRange, 0f, 1f );
			if ( carryAlpha > 0f )
			{
				var carryAccel = MaxMomentumCarryAccel * carryAlpha * swingAssistScale;
				move += tangentMotionDir * carryAccel * attachAlpha;
			}
		}

		// Deeper into the arc (rope closer to horizontal), bias tangential motion slightly faster.
		if ( IsRopeTaut && tangentSpeed > 1f && swingAssistScale > 0f )
		{
			move += tangentMotionDir * (naturalTangentAssist + depthBoostAccel) * swingAssistScale * attachAlpha;
		}

		if ( IsRopeTaut )
		{
			// Radial speed along rope (+ toward anchor, - away from anchor)
			// We only correct outward extension; do not add extra pull inward.
			var outwardSpeed = Math.Max( 0f, -currentRadialSpeed );

			if ( outwardSpeed > 0f )
			{
				// Cancel outward radial velocity over this frame.
				var cancelOutward = Math.Min( outwardSpeed / Time.Delta, MaxRopeCorrection );
				move += ropeDir * cancelOutward * attachAlpha;
			}
		}

		// Near taut rope, damp radial speed to kill oscillation.
		// Skip during retract so reel-in remains strong.
		if ( tautAlpha > 0f && !isRetractingRope )
			move -= ropeDir * currentRadialSpeed * TautRadialDamping * tautAlpha * attachAlpha;

		if ( DebugGrappleVelocity )
		{
			DebugLogTimer += Time.Delta;

			if ( DebugLogTimer >= Math.Max( DebugLogInterval, 0.01f ) )
			{
				DebugLogTimer = 0f;
				var stretch = distance - maxRopeDistance;
				Log.Warning(
					$"[Grapple] v:{currentSpeed:0.0} tan:{tangentSpeed:0.0} rad:{currentRadialSpeed:0.0} dist:{distance:0.0} rope:{maxRopeDistance:0.0} stretch:{stretch:0.00} taut:{IsRopeTaut} inScale:{tautInputScale:0.00} retract:{isRetractingRope} autoVelRetract:{autoVelocityRetract} assist:{swingAssistScale:0.00} depth:{swingDepthFactor:0.00} gMot:{gravityAlongMotion:0.00}" );
			}
		}

		// WASD swing does not drain stamina; PlayerStamina.GrappleSwingStaminaDrainPerSecond applies only while auto high-speed retract is active.
		GrappleSwingStaminaDrainActive = autoVelocityRetract;

		return move;
	}

	private void UpdateGrappleTarget()
	{
		HasTarget = false;

		if ( !TryGetGrappleAimOriginAndForward( out var aimOrigin, out var aimForward ) )
			return;

		var trace = Scene.Trace
			.Sphere( AimAssistRadius, aimOrigin, aimOrigin + aimForward * Range )
			.IgnoreGameObjectHierarchy( Controller?.GameObject ?? GameObject );

		var held = FindPlayerPickup()?.HeldRoot;
		if ( held is not null && held.IsValid() )
			trace = trace.IgnoreGameObjectHierarchy( held );

		var tr = trace
			.UseHitPosition( true )
			.Run();

		if ( !tr.Hit )
			return;

		if ( !HasGrappleTag( tr.GameObject ) )
			return;

		HasTarget = true;
		TargetPoint = tr.HitPosition;
		TargetNormal = tr.Normal;
	}

	private void DrawTargetCrosshair()
	{
		if ( !DrawGrappleCrosshair )
			return;

		var prevIgnore = Gizmo.Draw.IgnoreDepth;
		if ( CrosshairIgnoreDepth )
			Gizmo.Draw.IgnoreDepth = true;

		var prevThickness = Gizmo.Draw.LineThickness;
		Gizmo.Draw.LineThickness = 1f;

		// World highlights (same radius, depth-ignored so they read as overlays on geometry).
		if ( IsGrappling )
		{
			Gizmo.Draw.Color = CrosshairActiveColor;
			Gizmo.Draw.SolidSphere( GrapplePoint, CrosshairWorldMarkerRadius );
		}
		else if ( HasTarget && _secondsSinceGrappleInterest < Math.Max( CrosshairHoverIdleHideSeconds, 0.01f ) )
		{
			Gizmo.Draw.Color = CrosshairHoverColor;
			Gizmo.Draw.SolidSphere( TargetPoint + TargetNormal * CrosshairSurfaceOffset, CrosshairWorldMarkerRadius );
		}

		// HUD: always a small white teardrop locked to view center (camera-forward stub, not placed on props).
		GetHudViewPoint( out var hudCenter, out var viewRot );
		DrawHudTeardropReticle( hudCenter, viewRot );

		Gizmo.Draw.LineThickness = prevThickness;
		Gizmo.Draw.IgnoreDepth = prevIgnore;
	}

	private void DrawHudTeardropReticle( Vector3 hudCenter, Rotation viewRot )
	{
		var forward = viewRot.Forward;

		Gizmo.Draw.Color = CrosshairHudColor;
		Gizmo.Draw.SolidSphere( hudCenter, HudTeardropCoreRadius );

		if ( AttackAimCardinal == GrappleAttackAimCardinal.None
			|| !TryGetAttackAimPlanarUnit( viewRot, AttackAimCardinal, out var dir ) )
			return;

		var r = HudTeardropCoreRadius;
		var tip = hudCenter + dir * (r + HudTeardropTipLength);
		var baseMid = hudCenter + dir * (r * 0.2f);
		var perp = Vector3.Cross( forward, dir );
		if ( perp.Length < 0.0001f )
			perp = viewRot.Right;
		else
			perp = perp.Normal;

		var b0 = baseMid + perp * HudTeardropBaseHalfWidth;
		var b1 = baseMid - perp * HudTeardropBaseHalfWidth;
		Gizmo.Draw.Color = CrosshairHudColor;
		Gizmo.Draw.SolidTriangle( tip, b0, b1 );
	}

	private void GetHudViewPoint( out Vector3 hudCenter, out Rotation viewRot )
	{
		if ( !TryGetCrosshairViewRotation( out viewRot ) )
		{
			if ( Controller is not null )
				viewRot = Rotation.From( Controller.EyeAngles );
			else
				viewRot = GameObject.WorldRotation;
		}

		var fwd = viewRot.Forward;
		var cam = Scene.Camera;

		if ( cam is not null )
			hudCenter = cam.WorldPosition + fwd * HudCrosshairForward;
		else if ( TryGetGrappleAimOriginAndForward( out var aimOrigin, out var aimForward ) )
			hudCenter = aimOrigin + aimForward * HudCrosshairForward;
		else if ( Controller?.Body is not null )
		{
			var body = Controller.Body.GameObject.WorldPosition;
			hudCenter = body + Vector3.Up * (Controller.CurrentHeight * 0.5f) + fwd * HudCrosshairForward;
		}
		else
			hudCenter = GameObject.WorldPosition + fwd * HudCrosshairForward;
	}

	/// <summary>World-space unit direction on the view plane for the current <see cref="AttackAimCardinal"/>.</summary>
	public bool TryGetAttackAimWorldDirection( out Vector3 worldUnit, out Rotation viewRotation )
	{
		worldUnit = default;
		viewRotation = default;

		if ( !TryGetCrosshairViewRotation( out viewRotation ) )
			return false;

		if ( AttackAimCardinal == GrappleAttackAimCardinal.None )
			return false;

		return TryGetAttackAimPlanarUnit( viewRotation, AttackAimCardinal, out worldUnit );
	}

	private void UpdateAttackAimFromMouse()
	{
		var d = Input.MouseDelta;
		var dead = Math.Max( AttackAimMouseDeadzone, 0.01f );
		var dom = Math.Max( AttackAimAxisDominance, 1.01f );
		var stick = Math.Max( AttackAimAxisStickiness, 1f );

		var smoothTau = Math.Max( AttackAimSmoothSeconds, 0.001f );
		var smoothK = 1f - MathF.Exp( -Time.Delta / smoothTau );

		// Weak / idle frames: decay smoothed vector so we don't oscillate on sub-pixel noise.
		if ( d.Length < dead * 0.35f )
		{
			var decayTau = Math.Max( AttackAimDecaySeconds * 0.55f, 0.06f );
			var decayK = 1f - MathF.Exp( -Time.Delta / decayTau );
			_attackAimSmoothed = Vector2.Lerp( _attackAimSmoothed, Vector2.Zero, decayK );
		}
		else
		{
			_attackAimSmoothed = Vector2.Lerp( _attackAimSmoothed, d, smoothK );
		}

		var s = _attackAimSmoothed;
		var clearDead = dead * 0.55f;

		if ( s.Length < clearDead )
		{
			_secondsSinceStrongAttackMouse += Time.Delta;
			if ( _secondsSinceStrongAttackMouse >= AttackAimDecaySeconds )
			{
				AttackAimCardinal = GrappleAttackAimCardinal.None;
				_attackAimSmoothed = Vector2.Zero;
			}

			return;
		}

		_secondsSinceStrongAttackMouse = 0f;
		_lastAttackMouseDelta = d;

		var ax = MathF.Abs( s.x );
		var ay = MathF.Abs( s.y );

		var cur = AttackAimCardinal;
		var curHoriz = cur is GrappleAttackAimCardinal.Left or GrappleAttackAimCardinal.Right;
		var curVert = cur is GrappleAttackAimCardinal.Up or GrappleAttackAimCardinal.Down;

		bool pickHorizontal;
		if ( cur == GrappleAttackAimCardinal.None )
			pickHorizontal = ax >= dom * ay;
		else if ( curHoriz )
			pickHorizontal = ay <= stick * ax;
		else if ( curVert )
			pickHorizontal = ax >= stick * ay;
		else
			pickHorizontal = ax >= dom * ay;

		if ( pickHorizontal )
			AttackAimCardinal = s.x >= 0f ? GrappleAttackAimCardinal.Right : GrappleAttackAimCardinal.Left;
		else
			AttackAimCardinal = s.y >= 0f ? GrappleAttackAimCardinal.Down : GrappleAttackAimCardinal.Up;
	}

	private static bool TryGetAttackAimPlanarUnit( Rotation viewRot, GrappleAttackAimCardinal c, out Vector3 unit )
	{
		unit = c switch
		{
			GrappleAttackAimCardinal.Left => -viewRot.Right,
			GrappleAttackAimCardinal.Right => viewRot.Right,
			GrappleAttackAimCardinal.Up => viewRot.Up,
			GrappleAttackAimCardinal.Down => -viewRot.Up,
			_ => default
		};

		return c != GrappleAttackAimCardinal.None && unit.Length > 0.0001f;
	}

	private bool TryGetCrosshairViewRotation( out Rotation viewRot )
	{
		var cam = Scene.Camera;
		var useThirdPersonAim = Controller is not null && Controller.ThirdPerson;

		if ( !useThirdPersonAim && cam is not null )
		{
			viewRot = cam.WorldRotation;
			return true;
		}

		if ( Controller is not null )
		{
			viewRot = Rotation.From( Controller.EyeAngles );
			return true;
		}

		if ( cam is not null )
		{
			viewRot = cam.WorldRotation;
			return true;
		}

		viewRot = default;
		return false;
	}

	private void TryStartGrapple()
	{
		if ( !HasTarget )
			return;

		if ( Controller is not null && !PlayerStamina.HasStaminaToStartGrapple( Controller.GameObject ) )
			return;

		GrapplePoint = TargetPoint;
		IsGrappling = true;

		var distance = (GrapplePoint - WorldPosition).Length;

		RopeLength = Math.Max( MinRopeLength, distance - InitialRopeShorten );
		GrappleAge = 0f;
		IsRopeTaut = false;
		DebugLogTimer = 0f;

		if ( DebugGrappleVelocity )
			Log.Warning( "[Grapple] Debug enabled. Logging velocity samples..." );

		if ( Controller is not null )
			PlayerStamina.ApplyGrappleAttachCost( Controller.GameObject );
	}

	private bool HasGrappleTag( GameObject obj )
	{
		while ( obj is not null )
		{
			if ( obj.Tags.Has( GrappleTag ) )
				return true;

			obj = obj.Parent;
		}

		return false;
	}

	public void StopGrapple()
	{
		IsGrappling = false;
		GrappleSwingStaminaDrainActive = false;
		IsRopeTaut = false;
		DebugLogTimer = 0f;
	}

	private PlayerItemPickup FindPlayerPickup()
	{
		var root = Controller?.GameObject;
		if ( root is null )
			return null;

		var direct = root.Components.Get<PlayerItemPickup>();
		if ( direct is not null )
			return direct;

		return FindPickupInDescendants( root );
	}

	private static PlayerItemPickup FindPickupInDescendants( GameObject go )
	{
		foreach ( var child in go.Children )
		{
			var p = child.Components.Get<PlayerItemPickup>();
			if ( p is not null )
				return p;

			var nested = FindPickupInDescendants( child );
			if ( nested is not null )
				return nested;
		}

		return null;
	}

	/// <summary>First person: scene camera. Third person (or no camera): controller eye direction from the body.</summary>
	private bool TryGetGrappleAimOriginAndForward( out Vector3 origin, out Vector3 forward )
	{
		var cam = Scene.Camera;
		var useThirdPersonAim = Controller is not null && Controller.ThirdPerson;

		if ( !useThirdPersonAim && cam is not null )
		{
			origin = cam.WorldPosition;
			forward = cam.WorldRotation.Forward;
			return true;
		}

		if ( Controller is null || Controller.Body is null )
		{
			if ( cam is not null )
			{
				origin = cam.WorldPosition;
				forward = cam.WorldRotation.Forward;
				return true;
			}

			origin = default;
			forward = default;
			return false;
		}

		var eyeRot = Rotation.From( Controller.EyeAngles );
		forward = eyeRot.Forward;
		var bodyPos = Controller.Body.GameObject.WorldPosition;
		var pad = Math.Max( AimTraceStartForward, 0f ) + Math.Max( Controller.BodyRadius, 1f );
		origin = bodyPos + Vector3.Up * (Controller.CurrentHeight * 0.45f) + forward * pad;
		return true;
	}
}
