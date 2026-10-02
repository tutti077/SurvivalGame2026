using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Pushes the directional-light shadow reach out. The engine fits its cascades to a fixed max
/// distance, which ends in a hard shadow ring a short way from the camera; the only handle is
/// <see cref="SceneDirectionalLight.SetCascadeDistanceScale"/> on the scene-world light, so this
/// component applies it to every directional scene light (sun and moon). Sits on the Sun object.
/// <para>
/// The scene lights are found once and cached; the scale is re-applied to the cached lights every
/// frame (a cheap setter), because the engine can reset it when the light is updated. The old
/// version re-scanned every SceneObject (all scatter trees, ~56k night stars) once a second and only
/// then re-applied, so the shadow reach snapped between stock and scaled once per second (Mark: shadows
/// "glowing and darkening once per second") and the scan itself was a once-a-second spike.
/// </para>
/// </summary>
[Title( "Sun Shadow Distance" )]
public sealed class SunShadowDistance : Component
{
	/// <summary>
	/// Multiplier on the engine's stock cascade reach (where shadows stop entirely). Bigger = shadows
	/// further out but every cascade gets coarser. The ring <i>near</i> the player is not this — it is
	/// the first cascade's edge, set by the light's <c>ShadowCascadeSplitRatio</c>: near 1 (logarithmic)
	/// the first cascade is only a few metres wide; ~0.3 stretches it to tens of metres.
	/// </summary>
	[Property, Title( "Cascade distance scale" ), Range( 0.25f, 16f ), Step( 0.25f )]
	public float CascadeDistanceScale { get; set; } = 2f;

	/// <summary>Re-scan for scene lights at most this often, and only while none of the cached ones is valid.</summary>
	const float RescanSeconds = 2f;

	readonly List<SceneDirectionalLight> _lights = new();
	RealTimeSince _sinceScan;
	bool _needsScan;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		_lights.Clear();
		Rescan();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		var scale = Math.Max( 0.05f, CascadeDistanceScale );

		for ( var i = _lights.Count - 1; i >= 0; i-- )
		{
			var light = _lights[i];
			if ( light is null || !light.IsValid() )
			{
				_lights.RemoveAt( i );
				_needsScan = true;
				continue;
			}

			light.SetCascadeDistanceScale( scale );
		}

		// A rebuilt light is a new SceneObject: find it again (rate-limited full scan), only when a
		// cached light died or none was ever found.
		if ( (_needsScan || _lights.Count == 0) && _sinceScan >= RescanSeconds )
			Rescan();
	}

	void Rescan()
	{
		_sinceScan = 0f;
		_needsScan = false;
		var world = Scene?.SceneWorld;
		if ( world is null || !world.IsValid() )
			return;

		foreach ( var sceneObject in world.SceneObjects )
		{
			if ( sceneObject is SceneDirectionalLight light && light.IsValid() && !_lights.Contains( light ) )
				_lights.Add( light );
		}
	}
}
