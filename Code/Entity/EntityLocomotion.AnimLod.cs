using System;
using Sandbox;
using Sandbox.Citizen;

namespace Survival;

/// <summary>
/// Animation level of detail for citizen-bodied entities (2026-11-10 fps routine, Mark: 240 fps in the terrainTest fly
/// camera). The full human / sword animgraph costs ~0.1 ms of Animation per body per frame on the host — four scavs
/// 170-230 m away were 0.5 ms of a 5.7 ms frame. Beyond <see cref="AnimLodFarMeters"/> from the camera AND every
/// player pawn a body stops evaluating the graph and plays one looping clip picked by speed (idle / walk / run);
/// back inside (minus <see cref="AnimLodHysteresisMeters"/>) the graph and the <see cref="CitizenAnimationHelper"/>
/// take over again. At that range the clip reads the same; the graph's held-weapon arm pose and upper-body layers
/// are what drops. Measured: clip instead of graph = 0.09 ms saved per body.
///
/// The graph cannot be skipped frame-by-frame instead: with <c>UseAnimGraph</c> off a body snaps to its reference
/// pose (probed 2026-11-10), so the LOD is a clip, not a lower update rate. Attacks, hit reactions and falls happen
/// inside the near band (melee range, or a pawn nearby), and a falling body always keeps the graph. Host / owner
/// only, like the rest of the locomotion step (proxies return before it).
/// </summary>
public sealed partial class EntityLocomotion
{
	[Property, Group( "Animation LOD" ), Title( "Clip Beyond (m)" ), Range( 15f, 300f ), Step( 5f ), Description( "Beyond this from the camera and every pawn the body plays one looping clip instead of the full animgraph. 0 = always the graph." )]
	public float AnimLodFarMeters { get; set; } = 50f;

	/// <summary>Back to the graph this much closer than <see cref="AnimLodFarMeters"/>, so a body on the edge does not flip.</summary>
	const float AnimLodHysteresisMeters = 8f;
	/// <summary>How often the distance test runs (staggered per entity).</summary>
	const float AnimLodRecheckSeconds = 0.25f;
	/// <summary>Planar speed (u/s) below which the clip is idle, and from which it is the run.</summary>
	const float AnimLodWalkFromUnits = 15f;
	const float AnimLodRunFromUnits = 180f;

	const string AnimLodIdleClip = "IdlePose_Default";
	const string AnimLodWalkClip = "WalkFast_N";
	const string AnimLodRunClip = "Run_N_m";

	bool _animLod;
	float _animLodCheckAt;
	string _animLodClip;

	/// <summary>True while the body plays the far clip instead of the animgraph.</summary>
	public bool AnimLodActive => _animLod;

	void TickAnimLod( Vector3 moveVelocity )
	{
		if ( Body is null || !Body.IsValid() )
			return;

		if ( RealTime.Now >= _animLodCheckAt )
		{
			_animLodCheckAt = RealTime.Now + AnimLodRecheckSeconds * (0.75f + 0.5f * Random.Shared.Float());
			var want = WantsAnimLod();
			if ( want != _animLod )
				SetAnimLod( want );
		}

		if ( !_animLod )
			return;

		var speed = moveVelocity.WithZ( 0f ).Length;
		var clip = speed < AnimLodWalkFromUnits ? AnimLodIdleClip
			: speed < AnimLodRunFromUnits ? AnimLodWalkClip
			: AnimLodRunClip;
		if ( clip == _animLodClip )
			return;

		Body.Sequence.Name = clip;
		Body.Sequence.Looping = true;
		_animLodClip = clip;
	}

	bool WantsAnimLod()
	{
		if ( AnimLodFarMeters <= 0f || _isFalling )
			return false;

		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return false;   // no view (dedicated host): nobody to judge distance by — keep the graph

		// Entering the LOD needs the full distance; leaving it only the hysteresis band less.
		var meters = _animLod ? Math.Max( 1f, AnimLodFarMeters - AnimLodHysteresisMeters ) : AnimLodFarMeters;
		var here = GameObject.WorldPosition;
		var range = TerrainWorldUnits.MetersToEngine( meters );
		if ( (cam.WorldPosition - here).LengthSquared <= range * range )
			return false;

		return !EntityChunkMemory.AnyViewerWithin( Scene, here, meters );
	}

	void SetAnimLod( bool on )
	{
		_animLod = on;
		_animLodClip = null;
		if ( on )
		{
			if ( AnimHelper is not null && AnimHelper.IsValid() )
				AnimHelper.Enabled = false;
			Body.UseAnimGraph = false;
			return;
		}

		// Clear the clip before the graph comes back (PlayerAnimation does the same) or the graph resumes under it.
		Body.Sequence.Name = null;
		Body.Sequence.Time = 0f;
		Body.UseAnimGraph = true;
		if ( AnimHelper is not null && AnimHelper.IsValid() )
			AnimHelper.Enabled = true;
	}
}
