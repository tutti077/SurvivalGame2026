using Sandbox;

namespace Survival;

/// <summary>Host-only bus: player actions emit noises; nearby <see cref="EntityBrain"/>s fill alert / face stimulus.</summary>
public static class EntityNoiseBus
{
	public static void Emit( Scene scene, Vector3 worldPosition, EntityNoiseKind kind, GameObject source = null )
	{
		if ( scene is null || !scene.IsValid() )
			return;

		if ( scene.Network?.Active == true && !Networking.IsHost )
			return;

		var heard = 0;
		foreach ( var brain in scene.GetAllComponents<EntityBrain>() )
		{
			if ( brain is null || !brain.IsValid() || !brain.Active )
				continue;

			brain.TryHearNoise( worldPosition, kind, source );
			heard++;
		}

		foreach ( var animal in scene.GetAllComponents<AnimalBrain>() )
		{
			if ( animal is null || !animal.IsValid() || !animal.Active )
				continue;

			animal.TryHearNoise( worldPosition, kind, source );
			heard++;
		}

		var srcName = source is { IsValid: true } ? source.Name : "?";
		EntityPerceptionDebug.LogNoise( $"emit {kind} from {srcName} brainsNotified={heard}" );
	}
}
