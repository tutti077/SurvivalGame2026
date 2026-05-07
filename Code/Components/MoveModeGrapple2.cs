using System;
using Sandbox;
using Sandbox.Movement;

namespace Game;

public sealed class MoveModeGrapple2 : MoveMode, IGrappleStop
{
	[Property] public float Range { get; set; } = 3000f;
	[Property] public float AimAssistRadius { get; set; } = 48f;

	[Property] public float SwingControl { get; set; } = 220f;
	[Property] public float RopeTension { get; set; } = 420.0f;
	[Property] public float MaxRopeCorrection { get; set; } = 450f;
	[Property] public float RopeCorrectionStrength { get; set; } = 5f;

	[Property] public float InitialRopeShorten { get; set; } = 0f;
	[Property] public float InitialRopeSlack { get; set; } = 250f;
	[Property] public float MinRopeLength { get; set; } = 8f;
	[Property] public float MaxAllowedStretch { get; set; } = 450f;

	[Property] public float OutwardVelocityCancelStrength { get; set; } = 0.15f;
	[Property] public float RadialVelocityDamping { get; set; } = 0.35f;
	[Property] public float LowSpeedTangentDamping { get; set; } = 0f;
	[Property] public float MaxGrappleMove { get; set; } = 999999f;

	[Property] public float AirControl { get; set; } = 65f;
	[Property] public float MomentumChangeThreshold { get; set; } = 0.05f;
	[Property] public float LowMomentumSwingControl { get; set; } = 35f;

	[Property] public float SwingBuildRate { get; set; } = 4.0f;
	[Property] public float SwingDecayRate { get; set; } = 0.6f;
	[Property] public float MinSwingMultiplier { get; set; } = 0.12f;

	[Property] public float SwingQualitySpeedBoost { get; set; } = 22000f;
	[Property] public float SwingQualityBoostRamp { get; set; } = 0.75f;
	[Property] public float MaxSwingSpeed { get; set; } = 100000f;
	[Property] public float MinSpeedForQualityBoost { get; set; } = 20f;

	[Property] public float RetractSpeed { get; set; } = 120f;
	[Property] public float RetractPullSpeed { get; set; } = 25f;
	[Property] public float RetractStartDelay { get; set; } = 0.15f;

	[Property] public bool AutoRetractOnHighSpeed { get; set; } = true;
	[Property] public float AutoRetractMinSpeed { get; set; } = 500f;
	[Property] public float AutoRetractMinGrappleAge { get; set; } = 0.08f;

	[Property] public float AttachRampTime { get; set; } = 0.45f;

	[Property] public float CrosshairSize { get; set; } = 12f;
	[Property] public float CrosshairPulseAmount { get; set; } = 1.5f;
	[Property] public float CrosshairPulseSpeed { get; set; } = 3f;
	[Property] public float CrosshairSurfaceOffset { get; set; } = 8f;
	[Property] public Color CrosshairColor { get; set; } = Color.White;
	[Property] public Color CrosshairActiveColor { get; set; } = Color.Green;
	[Property] public bool UseActiveCrosshairColor { get; set; } = true;
	[Property] public bool DrawGrappleCrosshair { get; set; } = true;

	[Property] public string GrappleButton { get; set; } = "attack2";
	[Property] public string RetractButton { get; set; } = "";
	[Property] public string GrappleTag { get; set; } = "grappleable";

	[Property] public bool DrawRope { get; set; } = true;

	public bool IsGrappling { get; private set; }

	/// <inheritdoc />
	public bool GrappleSwingStaminaDrainActive { get; private set; }

	private Vector3 GrapplePoint;
	private float RopeLength;
	private float GrappleAge;
	private float SwingMomentum;

	private float DebugTimer;

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

		if ( IsGrappling )
		{
			DebugTimer += Time.Delta;

			if ( DebugTimer > 0.1f )
			{
				DebugTimer = 0f;
				var dist = (GrapplePoint - WorldPosition).Length;
				Log.Info( $"RopeLength: {RopeLength:0.00} | Dist: {dist:0.00} | Stretch: {(dist - RopeLength):0.00} | Vel: {Controller.Velocity.Length:0.00} | SwingMomentum: {SwingMomentum:0.00}" );
			}
		}

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
		var move = Vector3.Zero;

		var currentVelocity = Controller.Velocity;
		var radialSpeed = currentVelocity.Dot( ropeDir );
		var currentTangentVelocity = currentVelocity - ropeDir * radialSpeed;
		var currentTangentSpeed = currentTangentVelocity.Length;

		move -= ropeDir * radialSpeed * RadialVelocityDamping * attachAlpha;

		var manualRetract = !string.IsNullOrWhiteSpace( RetractButton ) && GrappleAge >= RetractStartDelay && Input.Down( RetractButton );
		var autoVelocityRetract = AutoRetractOnHighSpeed
			&& GrappleAge >= AutoRetractMinGrappleAge
			&& currentVelocity.Length >= AutoRetractMinSpeed;

		if ( manualRetract || autoVelocityRetract )
		{
			RopeLength -= RetractSpeed * Time.Delta;
			RopeLength = Math.Max( MinRopeLength, RopeLength );

			move += ropeDir * RetractPullSpeed * attachAlpha;
		}

		var forwardDir = eyes.Forward.WithZ( 0 );
		var rightDir = eyes.Right.WithZ( 0 );

		if ( !forwardDir.IsNearZeroLength )
			forwardDir = forwardDir.Normal;

		if ( !rightDir.IsNearZeroLength )
			rightDir = rightDir.Normal;

		var wishPlanarRaw = forwardDir * input.x - rightDir * input.y;

		if ( allowGrappleAirControl )
		{
			var wishDir = wishPlanarRaw;

			if ( !wishDir.IsNearZeroLength )
			{
				wishDir = wishDir.Normal;

				var airControlDir = wishDir - ropeDir * wishDir.Dot( ropeDir );

				if ( !airControlDir.IsNearZeroLength )
				{
					airControlDir = airControlDir.Normal;
					move += airControlDir * AirControl;
				}
			}

			if ( currentTangentSpeed > MinSpeedForQualityBoost && input.x > 0f && !forwardDir.IsNearZeroLength )
			{
				var currentTangentDir = currentTangentVelocity.Normal;
				var forwardTangent = forwardDir - ropeDir * forwardDir.Dot( ropeDir );

				if ( !forwardTangent.IsNearZeroLength )
				{
					forwardTangent = forwardTangent.Normal;

					var forwardAlignment = forwardTangent.Dot( currentTangentDir );
					var ropeAngleQuality = 1f - MathF.Abs( currentVelocity.Normal.Dot( ropeDir ) );
					var swingQuality = Math.Clamp( forwardAlignment, 0f, 1f ) * Math.Clamp( ropeAngleQuality, 0f, 1f );

					if ( swingQuality > MomentumChangeThreshold )
					{
						var speedRoom = Math.Clamp( 1f - currentTangentSpeed / MaxSwingSpeed, 0f, 1f );

						SwingMomentum = Math.Clamp(
							SwingMomentum + swingQuality * SwingBuildRate * Time.Delta,
							0f,
							1f
						);

						var swingMultiplier = MathF.Pow( SwingMomentum, SwingQualityBoostRamp );
						var qualityBoost = SwingQualitySpeedBoost * swingQuality * swingMultiplier * speedRoom;

						move += currentTangentDir * qualityBoost;
						move += forwardTangent * SwingControl * swingMultiplier;
					}
					else
					{
						SwingMomentum = Math.Max( 0f, SwingMomentum - SwingDecayRate * Time.Delta );
					}
				}
			}
			else if ( !wishPlanarRaw.IsNearZeroLength )
			{
				SwingMomentum = Math.Clamp( SwingMomentum + SwingBuildRate * 0.2f * Time.Delta, 0f, 1f );
				move += wishPlanarRaw.Normal * LowMomentumSwingControl * MinSwingMultiplier;
			}
			else
			{
				SwingMomentum = Math.Max( 0f, SwingMomentum - SwingDecayRate * Time.Delta );
			}
		}
		else
		{
			SwingMomentum = Math.Max( 0f, SwingMomentum - SwingDecayRate * Time.Delta );
		}

		var maxDistance = RopeLength + MaxAllowedStretch;

		if ( distance > maxDistance )
		{
			var excess = distance - maxDistance;
			var correction = excess * RopeCorrectionStrength;

			move += ropeDir * Math.Min( correction, MaxRopeCorrection ) * attachAlpha;

			var outwardSpeed = -radialSpeed;

			if ( outwardSpeed > 0f )
				move += ropeDir * outwardSpeed * OutwardVelocityCancelStrength * attachAlpha;
		}

		if ( move.Length > MaxGrappleMove )
			move = move.Normal * MaxGrappleMove;

		GrappleSwingStaminaDrainActive = autoVelocityRetract;

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

		if ( Controller is not null && !PlayerStamina.HasStaminaToStartGrapple( Controller.GameObject ) )
			return;

		GrapplePoint = TargetPoint;
		IsGrappling = true;

		var distance = (GrapplePoint - WorldPosition).Length;

		RopeLength = Math.Max( MinRopeLength, distance + InitialRopeSlack - InitialRopeShorten );
		GrappleAge = 0f;
		SwingMomentum = 0f;
		DebugTimer = 0f;

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
		SwingMomentum = 0f;
	}
}
