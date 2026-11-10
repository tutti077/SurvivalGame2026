using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Human look limits for the aim layers. The engine <see cref="PlayerController"/> writes the raw look
/// direction (body-local) into <c>aim_eyes</c>, <c>aim_head</c> and <c>aim_body</c>; with free-look orbit the
/// body never turns, so looking behind wound the hips and upper body round toward the camera and snapped to
/// the other side as the look crossed 180 degrees. The controller's animation pass runs after ours in a frame,
/// so overwriting its values is not enough: PlayerAnimation takes the pass over (turns the controller's
/// <c>UseAnimatorControls</c> off once and calls <see cref="PlayerController.UpdateAnimation"/> itself, except
/// while seated, where the seat pose owns the graph), then rewrites the three channels straight after, each
/// with its own human range: the chest turns a little, the head most of the rest, the eyes a bit more.
/// Past <see cref="AimLookBehindFadeDegrees"/> the character stops trying and eases back to facing forward
/// (a person would turn their feet), reaching exactly forward at 180 degrees, so +180 and -180 give the same
/// pose and nothing can flip. Results are smoothed so the head never snaps.
/// </summary>
public sealed partial class PlayerAnimation
{
	[Property, Group( "Look limits" ), Title( "Chest turn (deg)" ), Description( "Most the upper body twists toward the look direction." ), Range( 0f, 90f ), Step( 1f )]
	public float AimBodyMaxYawDegrees { get; set; } = 40f;

	[Property, Group( "Look limits" ), Title( "Head turn (deg)" ), Description( "Most the head turns from the hips' facing (chest + neck)." ), Range( 0f, 120f ), Step( 1f )]
	public float AimHeadMaxYawDegrees { get; set; } = 85f;

	[Property, Group( "Look limits" ), Title( "Eye turn (deg)" ), Range( 0f, 140f ), Step( 1f )]
	public float AimEyesMaxYawDegrees { get; set; } = 105f;

	[Property, Group( "Look limits" ), Title( "Chest pitch (deg)" ), Range( 0f, 90f ), Step( 1f )]
	public float AimBodyMaxPitchDegrees { get; set; } = 25f;

	[Property, Group( "Look limits" ), Title( "Head pitch (deg)" ), Range( 0f, 90f ), Step( 1f )]
	public float AimHeadMaxPitchDegrees { get; set; } = 60f;

	[Property, Group( "Look limits" ), Title( "Look-behind fade start (deg)" ), Description( "Beyond this look angle the character eases back to facing forward; exactly behind = forward." ), Range( 90f, 175f ), Step( 1f )]
	public float AimLookBehindFadeDegrees { get; set; } = 120f;

	[Property, Group( "Look limits" ), Title( "Look smoothing (s)" ), Range( 0f, 0.5f ), Step( 0.01f )]
	public float AimSmoothingSeconds { get; set; } = 0.08f;

	Angles _aimBodySmoothed;
	Angles _aimHeadSmoothed;
	Angles _aimEyesSmoothed;
	bool _aimSmoothedValid;

	/// <summary>
	/// Shape one look angle into a channel's human range: 1:1 up to 70% of the limit, then an exponential
	/// knee toward the limit; past the look-behind fade the result eases linearly to 0 at 180 degrees.
	/// </summary>
	static float ShapeLookAngle( float rel, float max, float fadeStart )
	{
		var a = MathF.Abs( rel );
		if ( max <= 0.01f )
			return 0f;

		float Knee( float x )
		{
			var k = max * 0.7f;
			if ( x <= k )
				return x;
			var span = max - k;
			return k + span * (1f - MathF.Exp( -(x - k) / span ));
		}

		float v;
		if ( a <= fadeStart )
			v = Knee( a );
		else
			v = Knee( fadeStart ) * Math.Clamp( (180f - a) / MathF.Max( 1f, 180f - fadeStart ), 0f, 1f );

		return MathF.Sign( rel ) * v;
	}

	/// <summary>True once this component runs the controller's animation pass itself (see the class summary).</summary>
	bool _ownsControllerAnimation;

	/// <summary>
	/// OnUpdate: run the controller's animation pass (unless seated) and then the look limits, in that order.
	/// OnPreRender: re-assert the limited aim values only.
	/// </summary>
	void TickLookAimLimits( bool advance )
	{
		var body = ResolveBody();
		if ( body is null || !body.IsValid() )
			return;

		var controller = Components.Get<PlayerController>();
		if ( controller is null || !controller.Enabled )
		{
			_aimSmoothedValid = false;
			return;
		}

		if ( advance )
		{
			// Take the pass over unconditionally: a joining peer receives an existing pawn from the
			// network snapshot with UseAnimatorControls already false (the host turned it off), so
			// "only own it if I flipped the flag" left that pawn with nobody driving its legs.
			if ( controller.UseAnimatorControls )
				controller.UseAnimatorControls = false;
			_ownsControllerAnimation = true;

			if ( Components.Get<PlayerMovement>() is { IsSeated: true } )
			{
				_aimSmoothedValid = false;
				return;
			}

			if ( body.UseAnimGraph )
				controller.UpdateAnimation( body );
		}
		else if ( !_ownsControllerAnimation || Components.Get<PlayerMovement>() is { IsSeated: true } )
		{
			return;
		}

		if ( !body.UseAnimGraph )
		{
			_aimSmoothedValid = false;
			return;
		}

		// Look relative to the body renderer's facing — the same frame the controller writes in.
		var eye = controller.EyeAngles;
		var rel = new Angles( eye.pitch, Angles.NormalizeAngle( eye.yaw - body.WorldRotation.Yaw() ), 0f );
		var fade = AimLookBehindFadeDegrees;

		// Pitch fades with the yaw too: looking up while facing away should not tilt the chest back.
		var behind = Math.Clamp( (MathF.Abs( rel.yaw ) - fade) / MathF.Max( 1f, 180f - fade ), 0f, 1f );
		var pitchScale = 1f - behind;

		// Bow stance: the chest stays square to the side-on body (the arms carry the aim), only head and eyes turn.
		var chestScale = 1f - BowStanceWeight;
		var bodyT = new Angles( Math.Clamp( rel.pitch, -AimBodyMaxPitchDegrees, AimBodyMaxPitchDegrees ) * pitchScale * chestScale,
			ShapeLookAngle( rel.yaw, AimBodyMaxYawDegrees, fade ) * chestScale, 0f );
		var headT = new Angles( Math.Clamp( rel.pitch, -AimHeadMaxPitchDegrees, AimHeadMaxPitchDegrees ) * pitchScale,
			ShapeLookAngle( rel.yaw, AimHeadMaxYawDegrees, fade ), 0f );
		var eyesT = new Angles( Math.Clamp( rel.pitch, -80f, 80f ) * pitchScale,
			ShapeLookAngle( rel.yaw, AimEyesMaxYawDegrees, fade ), 0f );

		if ( advance || !_aimSmoothedValid )
		{
			if ( !_aimSmoothedValid || AimSmoothingSeconds <= 1e-3f )
			{
				_aimBodySmoothed = bodyT;
				_aimHeadSmoothed = headT;
				_aimEyesSmoothed = eyesT;
				_aimSmoothedValid = true;
			}
			else
			{
				var k = 1f - MathF.Exp( -Time.Delta / AimSmoothingSeconds );
				_aimBodySmoothed = LerpAngles( _aimBodySmoothed, bodyT, k );
				_aimHeadSmoothed = LerpAngles( _aimHeadSmoothed, headT, k );
				_aimEyesSmoothed = LerpAngles( _aimEyesSmoothed, eyesT, k );
			}
		}

		body.Set( "aim_body", _aimBodySmoothed.Forward );
		body.Set( "aim_head", _aimHeadSmoothed.Forward );
		body.Set( "aim_eyes", _aimEyesSmoothed.Forward );
	}

	/// <summary>Shaped angles never wrap (they stay inside ±180 and reach 0 at the back), so a plain lerp is safe.</summary>
	static Angles LerpAngles( Angles a, Angles b, float t ) =>
		new( a.pitch + (b.pitch - a.pitch) * t, a.yaw + (b.yaw - a.yaw) * t, 0f );
}
