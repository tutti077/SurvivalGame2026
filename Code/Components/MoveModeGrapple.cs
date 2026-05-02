using System;
using Sandbox;
using Sandbox.Movement;

public sealed class MoveModeGrapple : MoveMode
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

	[Property] public float AttachRampTime { get; set; } = 0.001f;

	[Property] public float CrosshairSize { get; set; } = 12f;
	[Property] public float CrosshairPulseAmount { get; set; } = 1.5f;
	[Property] public float CrosshairPulseSpeed { get; set; } = 3f;
	[Property] public float CrosshairSurfaceOffset { get; set; } = 8f;
	[Property] public Color CrosshairColor { get; set; } = Color.White;
	[Property] public Color CrosshairActiveColor { get; set; } = Color.Green;
	[Property] public bool UseActiveCrosshairColor { get; set; } = true;
	[Property] public bool DrawGrappleCrosshair { get; set; } = true;

	[Property] public string GrappleButton { get; set; } = "attack2";
	[Property] public string RetractButton { get; set; } = "jump";
	[Property] public string GrappleTag { get; set; } = "grappleable";

	[Property] public bool DrawRope { get; set; } = true;
	[Property] public bool DebugGrappleVelocity { get; set; } = true;
	[Property] public float DebugLogInterval { get; set; } = 0.15f;

	private bool IsGrappling;
	private bool IsRopeTaut;
	private Vector3 GrapplePoint;
	private float RopeLength;
	private float GrappleAge;
	private float DebugLogTimer;

	private bool HasTarget;
	private Vector3 TargetPoint;
	private Vector3 TargetNormal;

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

		if ( IsGrappling )
			GrappleAge += Time.Delta;

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
			return Vector3.Zero;

		var toPoint = GrapplePoint - WorldPosition;
		var distance = toPoint.Length;

		if ( distance <= 1f )
			return Vector3.Zero;

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

		var isRetracting = Input.Down( RetractButton );
		var swingAssistScale = isRetracting ? Math.Clamp( RetractSwingAssistScale, 0f, 1f ) : 1f;
		if ( isRetracting )
		{
			RopeLength -= RetractSpeed * Time.Delta;
			RopeLength = Math.Max( MinRopeLength, RopeLength );

			move += ropeDir * RetractPullSpeed;
		}

		var wishDir =
			eyes.Forward * input.x -
			eyes.Right * input.y;

		wishDir = wishDir.WithZ( 0 );

		if ( !wishDir.IsNearZeroLength )
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
		if ( tautAlpha > 0f && !isRetracting )
			move -= ropeDir * currentRadialSpeed * TautRadialDamping * tautAlpha * attachAlpha;

		if ( DebugGrappleVelocity )
		{
			DebugLogTimer += Time.Delta;

			if ( DebugLogTimer >= Math.Max( DebugLogInterval, 0.01f ) )
			{
				DebugLogTimer = 0f;
				var stretch = distance - maxRopeDistance;
				Log.Warning(
					$"[Grapple] v:{currentSpeed:0.0} tan:{tangentSpeed:0.0} rad:{currentRadialSpeed:0.0} dist:{distance:0.0} rope:{maxRopeDistance:0.0} stretch:{stretch:0.00} taut:{IsRopeTaut} inScale:{tautInputScale:0.00} retract:{isRetracting} assist:{swingAssistScale:0.00} depth:{swingDepthFactor:0.00} gMot:{gravityAlongMotion:0.00}" );
			}
		}

		return move;
	}

	private void UpdateGrappleTarget()
	{
		HasTarget = false;

		var cam = Scene.Camera;
		if ( cam is null )
			return;

		var tr = Scene.Trace
			.Sphere( AimAssistRadius, cam.WorldPosition, cam.WorldPosition + cam.WorldRotation.Forward * Range )
			.IgnoreGameObjectHierarchy( GameObject )
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
		Vector3 drawPoint;
		float size;

		if ( IsGrappling )
		{
			drawPoint = GrapplePoint;
			size = CrosshairSize;
			Gizmo.Draw.Color = UseActiveCrosshairColor ? CrosshairActiveColor : CrosshairColor;
		}
		else if ( HasTarget )
		{
			var pulse = MathF.Sin( Time.Now * CrosshairPulseSpeed ) * CrosshairPulseAmount;
			size = CrosshairSize + pulse;
			drawPoint = TargetPoint + TargetNormal * CrosshairSurfaceOffset;
			Gizmo.Draw.Color = CrosshairColor;
		}
		else
		{
			return;
		}

		Gizmo.Draw.SolidSphere( drawPoint, size );
	}

	private void TryStartGrapple()
	{
		if ( !HasTarget )
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

	private void StopGrapple()
	{
		IsGrappling = false;
		IsRopeTaut = false;
		DebugLogTimer = 0f;
	}
}
