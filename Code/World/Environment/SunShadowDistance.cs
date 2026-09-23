using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Pushes the directional-light shadow reach out. The engine fits its cascades to a fixed max
/// distance, which ends in a hard shadow ring a short way from the camera; the only handle is
/// <see cref="SceneDirectionalLight.SetCascadeDistanceScale"/> on the scene-world light, so this
/// component applies it to every directional scene light (sun and moon) and re-applies once a
/// second in case the engine rebuilt the light. Sits on the Sun object.
/// </summary>
[Title( "Sun Shadow Distance" )]
public sealed class SunShadowDistance : Component
{
	/// <summary>Multiplier on the engine's stock cascade reach. 1 = stock ring; 4 pushes it well past the play area.</summary>
	[Property, Title( "Cascade distance scale" ), Range( 0.25f, 16f ), Step( 0.25f )]
	public float CascadeDistanceScale { get; set; } = 4f;

	float _appliedScale = -1f;
	RealTimeSince _sinceApply;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		_appliedScale = -1f;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		var scale = Math.Max( 0.05f, CascadeDistanceScale );
		if ( MathF.Abs( scale - _appliedScale ) < 1e-4f && _sinceApply < 1f )
			return;

		Apply( scale );
	}

	void Apply( float scale )
	{
		_sinceApply = 0f;
		var world = Scene?.SceneWorld;
		if ( world is null || !world.IsValid() )
			return;

		var found = false;
		foreach ( var sceneObject in world.SceneObjects )
		{
			if ( sceneObject is not SceneDirectionalLight light || !light.IsValid() )
				continue;

			light.SetCascadeDistanceScale( scale );
			found = true;
		}

		if ( found )
			_appliedScale = scale;
	}
}
