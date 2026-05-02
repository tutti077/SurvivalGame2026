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
	[Property] public float MinTautInputScale { get; set; } = 0.05f;
	[Property] public float TautRadialDamping { get; set; } = 8f;

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

		if ( IsGrappling && DebugGrappleVelocity )
			EmitDebugLog();

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

		var move = Vector3.Zero;

		var isRetracting = Input.Down( RetractButton );
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
				Log.Warning( $"[Grapple] v:{currentSpeed:0.0} tan:{tangentSpeed:0.0} rad:{currentRadialSpeed:0.0} dist:{distance:0.0} rope:{maxRopeDistance:0.0} stretch:{stretch:0.00} taut:{IsRopeTaut} inScale:{tautInputScale:0.00} retract:{isRetracting}" );
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

	private void EmitDebugLog()
	{
		DebugLogTimer += Time.Delta;
		if ( DebugLogTimer < Math.Max( DebugLogInterval, 0.01f ) )
			return;

		DebugLogTimer = 0f;

		var toPoint = GrapplePoint - WorldPosition;
		var distance = toPoint.Length;
		if ( distance <= 0.001f )
			return;

		var ropeDir = toPoint / distance;
		var velocity = Controller.Velocity;
		var radialSpeed = velocity.Dot( ropeDir );
		var tangentVelocity = velocity - ropeDir * radialSpeed;
		var maxRopeDistance = RopeLength + MaxAllowedStretch;
		var stretch = distance - maxRopeDistance;

		Log.Warning( $"[Grapple] v:{velocity.Length:0.0} tan:{tangentVelocity.Length:0.0} rad:{radialSpeed:0.0} dist:{distance:0.0} rope:{maxRopeDistance:0.0} stretch:{stretch:0.00} taut:{IsRopeTaut}" );
	}
}
