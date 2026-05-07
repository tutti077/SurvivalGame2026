using System;
using Sandbox;
using Sandbox.Movement;

namespace Game;

public sealed class MoveModeGrapple1 : MoveMode, IGrappleStop
{
	[Property] public float Range { get; set; } = 3000f;
	[Property] public float AimAssistRadius { get; set; } = 48f;

	[Property] public float SwingControl { get; set; } = 95f;
	[Property] public float RopeTension { get; set; } = 420.0f;
	[Property] public float MaxRopeCorrection { get; set; } = 12000f;

	[Property] public float InitialRopeShorten { get; set; } = 0f;
	[Property] public float MinRopeLength { get; set; } = 8f;
	[Property] public float MaxAllowedStretch { get; set; } = 0f;

	[Property] public float StretchHoldPull { get; set; } = 1000f;

	[Property] public float MomentumChangeThreshold { get; set; } = 0.2f;
	[Property] public float LowMomentumSwingControl { get; set; } = 42f;

	[Property] public float RetractSpeed { get; set; } = 420f;
	[Property] public float RetractPullSpeed { get; set; } = 320f;

	[Property] public bool AutoRetractOnHighSpeed { get; set; } = true;
	[Property] public float AutoRetractMinSpeed { get; set; } = 500f;
	[Property] public float AutoRetractMinGrappleAge { get; set; } = 0.08f;

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

	public bool IsGrappling { get; private set; }

	/// <inheritdoc />
	public bool GrappleSwingStaminaDrainActive { get; private set; }

	private Vector3 GrapplePoint;
	private float RopeLength;
	private float GrappleAge;

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
		var currentSpeed = Controller.Velocity.Length;

		var move = Vector3.Zero;

		var manualRetract = Input.Down( RetractButton );
		var autoVelocityRetract = AutoRetractOnHighSpeed
			&& GrappleAge >= AutoRetractMinGrappleAge
			&& currentSpeed >= AutoRetractMinSpeed;
		if ( manualRetract || autoVelocityRetract )
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

				var currentVelocity = Controller.Velocity;
				var currentTangentVelocity = currentVelocity - ropeDir * currentVelocity.Dot( ropeDir );

				if ( currentTangentVelocity.Length > 40f )
				{
					var currentTangentDir = currentTangentVelocity.Normal;
					var alignment = tangent.Dot( currentTangentDir );

					if ( alignment > MomentumChangeThreshold )
						move += tangent * SwingControl;
				}
				else
				{
					move += tangent * LowMomentumSwingControl;
				}
			}
		}

		if ( distance > RopeLength + MaxAllowedStretch )
		{
			var stretch = distance - RopeLength - MaxAllowedStretch;
			var correctionSpeed = Math.Min( stretch * RopeTension, MaxRopeCorrection );

			move += ropeDir * correctionSpeed * attachAlpha;

			if ( ropeDir.z > 0f )
				move += Vector3.Up * StretchHoldPull * ropeDir.z * attachAlpha;
		}

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

		RopeLength = Math.Max( MinRopeLength, distance - InitialRopeShorten );
		GrappleAge = 0f;

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
	}
}
