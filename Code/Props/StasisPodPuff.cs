using System;
using Sandbox;

namespace Survival;

/// <summary>
/// One faceted smoke blob (<c>stasis_pod_puff.vmdl</c>): grows from <see cref="StartScale"/> to <see cref="EndScale"/>,
/// drifts along <see cref="Velocity"/> with drag, and destroys itself after <see cref="LifeSeconds"/>. Purely local -
/// the pod spawns these on every peer from the same timeline. Used for the crash dust ring and the door's air release.
/// </summary>
[Title( "Stasis Pod Puff" )]
public sealed class StasisPodPuff : Component
{
	/// <summary>Diameter in metres at birth.</summary>
	public float StartScale { get; set; } = 0.3f;

	/// <summary>Diameter in metres at the end of its life.</summary>
	public float EndScale { get; set; } = 1.2f;

	/// <summary>World units per second at birth; halves roughly every 0.4 s.</summary>
	public Vector3 Velocity { get; set; }

	public float LifeSeconds { get; set; } = 1f;

	float _age;

	public static StasisPodPuff Spawn( Scene scene, Vector3 at, Vector3 velocity, float startMeters, float endMeters, float lifeSeconds, Material material )
	{
		if ( scene is null || !scene.IsValid() )
			return null;

		var go = new GameObject( true, "stasis_pod_puff" );
		go.NetworkMode = NetworkMode.Never;
		go.Flags |= GameObjectFlags.NotSaved;
		go.WorldPosition = at;
		go.WorldRotation = Rotation.FromYaw( Random.Shared.Float( 0f, 360f ) ) * Rotation.FromPitch( Random.Shared.Float( -40f, 40f ) );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( StasisPod.PuffModelPath );
		if ( material is not null )
			renderer.MaterialOverride = material;
		renderer.RenderOptions.Game = true;

		var puff = go.Components.Create<StasisPodPuff>();
		puff.StartScale = startMeters;
		puff.EndScale = endMeters;
		puff.Velocity = velocity;
		puff.LifeSeconds = MathF.Max( 0.1f, lifeSeconds );
		puff.ApplyScale( 0f );
		return puff;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		_age += Time.Delta;
		var k = _age / LifeSeconds;
		if ( k >= 1f )
		{
			GameObject.Destroy();
			return;
		}

		WorldPosition += Velocity * Time.Delta;
		Velocity *= MathF.Pow( 0.5f, Time.Delta / 0.4f );
		ApplyScale( k );
	}

	void ApplyScale( float k01 )
	{
		// Fast swell, then a shrink into nothing over the last third - reads as smoke thinning out.
		var swell = 1f - MathF.Pow( 1f - MathF.Min( 1f, k01 / 0.7f ), 2f );
		var fade = k01 < 0.66f ? 1f : 1f - (k01 - 0.66f) / 0.34f;
		var meters = (StartScale + (EndScale - StartScale) * swell) * MathF.Max( 0.01f, fade );
		// The model is a unit blob (radius 1 m at scale 1), so the diameter in metres is the scale times 2 m, in 40 u/m.
		WorldScale = Vector3.One * (meters * 0.5f * TerrainWorldUnits.MetersToEngine( 1f ) / 40f);
	}
}
