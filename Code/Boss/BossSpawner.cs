using Sandbox;

namespace Survival;

/// <summary>
/// Host: spawns a boss from <c>data/bosses.json</c> — clones its prefab, tunes it through the
/// shared <see cref="EntityEnemySetup"/> path, then binds the <see cref="BossEntity"/> row.
/// The debug key and (later) biome / quest triggers both end here.
/// </summary>
public static class BossSpawner
{
	public static bool TrySpawn( Scene scene, string bossId, Vector3 origin, float yawDegrees )
	{
		if ( !scene.IsValid() )
			return false;

		if ( !BossCatalog.TryGet( bossId, out var boss ) )
		{
			Log.Warning( $"[BossSpawner] Unknown boss '{bossId}'." );
			return false;
		}

		BuildNavMeshSync.EnsureBuildTraversalSettings( scene );

		var instance = BuildPrefabUtility.GetTemplate( boss.Prefab )?.Clone();
		if ( instance is null || !instance.IsValid() )
		{
			Log.Warning( $"[BossSpawner] Failed to clone prefab '{boss.Prefab}' for boss '{boss.Id}'." );
			return false;
		}

		instance.Parent = scene;
		instance.WorldPosition = origin;
		instance.WorldRotation = Rotation.FromYaw( yawDegrees );

		var bossEntity = instance.Components.Get<BossEntity>();
		if ( bossEntity is null )
		{
			Log.Warning( $"[BossSpawner] Prefab '{boss.Prefab}' has no BossEntity — add it to the boss prefab; spawn skipped." );
			instance.Destroy();
			return false;
		}

		EntityEnemySetup.Configure( instance, boss.ResolveEnemyType(), boss.Tier, boss.Health );
		bossEntity.HostConfigure( boss, instance.WorldPosition );
		instance.Name = $"boss_{boss.Id}";

		// Clone alone is host-local — remotes never see the boss without NetworkSpawn.
		if ( Networking.IsActive && !HostNetworkSpawn.TrySpawn( instance ) )
		{
			Log.Warning( $"[BossSpawner] NetworkSpawn failed for '{boss.Prefab}' — destroying local clone." );
			instance.Destroy();
			return false;
		}

		var secondForm = boss.HasSecondForm ? $" + form 2 {boss.SecondFormHealth:0} HP" : string.Empty;
		Log.Info( $"[BossSpawner] Boss '{boss.Id}' ({boss.DisplayName}) at {instance.WorldPosition}: {boss.Health:0} HP{secondForm}." );
		return true;
	}
}
