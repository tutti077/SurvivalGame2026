using System;
using Sandbox;

namespace Survival;

/// <summary>
/// The pickaxe on a <see cref="MineableRock"/> (breakable rocks, sectioned boulders, stone spires). The tool aims on the owner
/// and sends the rock (its scatter key, or its object id for a rock placed in a scene), the section and the hit point; the
/// host checks once per swing - a Mine tool in hand and not broken, the point within reach and on that section, stamina -
/// then <see cref="MineableRock.HostApplyHit"/> counts the hit. Wear and stamina are charged only when the hit landed.
/// The stone comes out as world drops when a section breaks, not into the bag.
/// </summary>
public sealed partial class PlayerTerraform
{
	/// <summary>Owner: the pickaxe hit section <paramref name="section"/> of <paramref name="rock"/> at <paramref name="hitEngine"/>.</summary>
	public void OwnerRequestMine( MineableRock rock, int section, Vector3 hitEngine, float reachMeters )
	{
		if ( rock is null || !rock.IsValid() )
			return;
		var identity = rock.Components.Get<WorldScatterIdentity>( FindMode.EverythingInSelfAndAncestors );
		var key = identity is not null && !string.IsNullOrWhiteSpace( identity.StableKey ) ? identity.StableKey : string.Empty;
		var id = rock.GameObject.Id;
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostMine( key, id, section, hitEngine, reachMeters );
			return;
		}
		RpcHostMine( key, id, section, hitEngine, reachMeters );
	}

	[Rpc.Host]
	void RpcHostMine( string scatterKey, Guid rockId, int section, Vector3 hitEngine, float reachMeters )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;
		HostMine( scatterKey, rockId, section, hitEngine, reachMeters );
	}

	void HostMine( string scatterKey, Guid rockId, int section, Vector3 hitEngine, float reachMeters )
	{
		var rock = ResolveRock( scatterKey, rockId );
		if ( rock is null || section < 0 || section >= rock.Sections.Count || rock.IsSectionBroken( section ) )
		{
			if ( LogTerraform )
				Log.Info( $"[PlayerTerraform] mine rejected: no such rock / section ({scatterKey} {section})" );
			return;
		}
		if ( rock.RequireMineTool && !HoldsMineTool() )
		{
			if ( LogTerraform )
				Log.Info( "[PlayerTerraform] mine rejected: no pickaxe in hand" );
			return;
		}
		if ( ToolDurability.IsActiveToolBroken( GameObject ) )
		{
			if ( LogTerraform )
				Log.Info( "[PlayerTerraform] mine rejected: tool broken" );
			return;
		}
		var reach = TerrainWorldUnits.MetersToEngine( Math.Clamp( reachMeters, 0.5f, 10f ) + ReachSlackMeters );
		var eye = WorldPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.6f );
		if ( (hitEngine - eye).Length > reach || !rock.IsPointOnSection( section, hitEngine, TerrainWorldUnits.MetersToEngine( ReachSlackMeters ) ) )
		{
			if ( LogTerraform )
				Log.Info( "[PlayerTerraform] mine rejected: out of reach" );
			return;
		}
		var vitals = Components.Get<PlayerVitals>();
		var stamina = vitals?.PickaxeSwingStaminaCost ?? 0f;
		if ( vitals is not null && !vitals.CanAffordStamina( stamina ) )
		{
			if ( LogTerraform )
				Log.Info( "[PlayerTerraform] mine rejected: too tired" );
			return;
		}
		if ( !rock.HostApplyHit( section, GameObject ) )
			return;
		ToolDurability.HostAddWearToActiveTool( GameObject );
		vitals?.TrySpendStamina( stamina );
	}

	MineableRock ResolveRock( string scatterKey, Guid rockId )
	{
		if ( !string.IsNullOrWhiteSpace( scatterKey ) && WorldScatterIdentity.TryFind( scatterKey, out var identity ) )
			return identity.Components.Get<MineableRock>( FindMode.EverythingInSelfAndDescendants );
		var go = Scene.Directory.FindByGuid( rockId );
		return go?.Components.Get<MineableRock>();
	}

	bool HoldsMineTool()
	{
		var hotbar = Components.Get<PlayerHotbar>();
		if ( hotbar is null )
			return false;
		var slot = hotbar.GetSlot( hotbar.ActiveSlotIndex );
		return !slot.IsEmpty && EquipmentCatalog.HasAction( slot.ResourceId, EquippedItemActions.Mine );
	}
}
