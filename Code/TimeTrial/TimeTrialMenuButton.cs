using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// World prop you look at + E to open the time trial menu (e.g. the "Time Trials" sign).
/// </summary>
[Title( "Time Trial Menu Button" )]
public sealed class TimeTrialMenuButton : Component
{
	[Property, Title( "Reach (m)" ), Range( 1f, 12f )]
	public float ReachMeters { get; set; } = 8f;

	[Property, Title( "Look Cone (°)" ), Range( 5f, 60f )]
	public float LookConeDegrees { get; set; } = 40f;

	public TimeTrialSession Session =>
		TimeTrialSession.Instance is { IsValid: true } s ? s : null;

	public static bool TryFindFocused( GameObject viewer, float reachMeters, out TimeTrialMenuButton button )
	{
		button = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var eye = viewer.WorldPosition + Vector3.Up * 64f;
		var pc = viewer.Components.Get<PlayerController>();
		if ( pc is not null && pc.IsValid() )
			eye = viewer.WorldPosition + Vector3.Up * Math.Max( 48f, pc.BodyHeight * 0.9f );

		var viewDir = viewer.WorldRotation.Forward.Normal;
		var cam = BuildViewCamera.Resolve( viewer );
		if ( cam.IsValid() )
			viewDir = cam.WorldRotation.Forward.Normal;
		if ( viewDir.LengthSquared < 1e-8f )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		TimeTrialMenuButton best = null;
		var bestScore = float.MaxValue;

		foreach ( var candidate in scene.GetAllComponents<TimeTrialMenuButton>() )
		{
			if ( candidate is null || !candidate.IsValid() || !candidate.Enabled )
				continue;

			var to = candidate.GameObject.WorldPosition - eye;
			var dist = to.Length;
			if ( dist > reach || dist < 1e-3f )
				continue;

			var dir = to / dist;
			var dot = Vector3.Dot( viewDir, dir );
			var cone = MathF.Cos( Math.Max( 5f, candidate.LookConeDegrees ) * (MathF.PI / 180f) );
			if ( dot < cone )
				continue;

			var score = dist * (2f - dot);
			if ( score >= bestScore )
				continue;

			bestScore = score;
			best = candidate;
		}

		// Bonus: ray hit on the button collider.
		if ( best is null )
		{
			var tr = scene.Trace.Ray( eye, eye + viewDir * reach )
				.IgnoreGameObjectHierarchy( viewer )
				.Run();
			if ( tr.Hit && tr.GameObject.IsValid() )
			{
				for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
				{
					var b = go.Components.Get<TimeTrialMenuButton>();
					if ( b is not null && b.Enabled )
					{
						best = b;
						break;
					}
				}
			}
		}

		button = best;
		return button is not null;
	}
}
