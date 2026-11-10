using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Wingsuit glide presentation (Mark 2026-11-05: "laying flat with their arms out like they're a bird"). While
/// <see cref="PlayerMovement.IsWingsuitDeployed"/> (synced, so every peer sees it) the body plays the looping
/// <see cref="WingsuitGlideSequence"/> clip - arms spread, chest arched, head back, feet pointed, a slow wing flex -
/// as a <c>UseAnimGraph=false</c> sequence like the dodge roll, and the BODY RENDERER is laid flat: pitched 90 degrees
/// about the hips so the head points along the glide and the belly faces the ground. The movement owner already turns
/// the root along the glide path (pitch / roll), so the flat body follows dives and banks. Presentation only - the
/// root, collider and camera are untouched. The hit-reaction flail wins: the body stands back up for it and lies
/// down again after.
/// </summary>
public sealed partial class PlayerAnimation
{
	/// <summary>Looping clip authored in Blender/scripts/create_sword_anims.py (Wingsuit_Glide), registered by make_human_sword_assets.py.</summary>
	public const string WingsuitGlideSequence = "Wingsuit_Glide";

	/// <summary>Height above the feet the body pivots about when it lies flat (the hips), so the pawn does not swing out of its collider.</summary>
	[Property, Group( "Animation" ), Title( "Wingsuit lay pivot height (m)" ), Range( 0.5f, 1.4f ), Step( 0.05f )]
	public float WingsuitLayPivotMeters { get; set; } = 0.95f;

	bool _wingsuitPoseActive;
	bool _wingsuitBodyLaid;
	Vector3 _wingsuitBodyRestPos;
	Rotation _wingsuitBodyRestRot;

	PlayerMovement _wingsuitMovement;

	/// <summary>Ticked from OnUpdate and OnPreRender after the facing / look ticks, so the laid-flat body lands last.</summary>
	void TickWingsuitPose()
	{
		_wingsuitMovement ??= Components.Get<PlayerMovement>();
		var gliding = _wingsuitMovement is { IsWingsuitDeployed: true };

		if ( gliding && !IsHitReactionActive )
		{
			var starting = !_wingsuitPoseActive;
			_wingsuitPoseActive = true;
			PlayCombatSequencePose( WingsuitGlideSequence, keepMeleeSwordVisible: false, forceRestart: starting );

			var body = ResolveBody();
			if ( body is not null && body.IsValid() )
			{
				body.Sequence.Looping = true;
				LayWingsuitBody( body );
			}

			// No floating weapon over the spread hands - TickHoldPose re-creates it after.
			DestroyMeleeDemoStick();
			return;
		}

		StandWingsuitBody();
		if ( !_wingsuitPoseActive )
			return;

		_wingsuitPoseActive = false;
		// The flail owns the body now (its own clear restores the graph), or the glide ended: same hard restore as the roll.
		if ( !IsHitReactionActive )
			ClearHitReactionPose();
	}

	void LayWingsuitBody( SkinnedModelRenderer body )
	{
		var go = body.GameObject;
		if ( go == GameObject )
			return;

		if ( !_wingsuitBodyLaid )
		{
			_wingsuitBodyRestPos = go.LocalPosition;
			_wingsuitBodyRestRot = go.LocalRotation;
			_wingsuitBodyLaid = true;
		}

		// Pitch 90 (nose down) turns the model's up onto the glide direction and its front to the ground; offsetting
		// by pivot - rot * pivot keeps the hips where they stand.
		var lay = Rotation.FromPitch( 90f );
		var pivot = Vector3.Up * (WingsuitLayPivotMeters * ResolvePawnUnitsPerMeter());
		go.LocalRotation = lay;
		go.LocalPosition = _wingsuitBodyRestPos + pivot - lay * pivot;
	}

	void StandWingsuitBody()
	{
		if ( !_wingsuitBodyLaid )
			return;

		_wingsuitBodyLaid = false;
		var body = ResolveBody();
		if ( body is null || !body.IsValid() || body.GameObject == GameObject )
			return;

		body.GameObject.LocalPosition = _wingsuitBodyRestPos;
		body.GameObject.LocalRotation = _wingsuitBodyRestRot;
	}
}
