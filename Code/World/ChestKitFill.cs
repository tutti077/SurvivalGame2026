using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>One stack a <see cref="ChestKitFill"/> deposits.</summary>
public sealed class ChestKitEntry
{
	[Property, Title( "Resource Id" )]
	public string ResourceId { get; set; } = string.Empty;

	[Property, Title( "Count" ), Range( 1, 999 )]
	public int Count { get; set; } = 1;
}

/// <summary>
/// Host: if this object's <see cref="ContainerInventory"/> is empty on start, deposit the authored
/// kit once. Put on an authored scene chest (testscene1 starter / farming / bow / fish / meat
/// chests) — it never spawns anything at runtime. Stacks above the item's max stack split across
/// slots through <see cref="ContainerInventory.HostDepositStack"/>.
/// </summary>
[Title( "Chest Kit Fill" )]
public sealed class ChestKitFill : Component
{
	[Property, Title( "Items" )]
	public List<ChestKitEntry> Items { get; set; } = new();

	[Property, Title( "Display name when filled" )]
	public string KitDisplayName { get; set; } = string.Empty;

	bool _done;

	protected override void OnStart()
	{
		base.OnStart();
		TryHostFill();
	}

	void TryHostFill()
	{
		if ( _done )
			return;

		_done = true;

		if ( GameObject.Network is { Active: true } && !Networking.IsHost )
			return;

		var container = Components.Get<ContainerInventory>( FindMode.EverythingInSelfAndDescendants );
		if ( container is null || !container.HasHostAuthority )
			return;

		if ( !container.IsEmpty || Items is null || Items.Count == 0 )
			return;

		EquipmentCatalog.EnsureLoaded();
		ResourceDefinitionCatalog.EnsureLoaded();

		if ( !string.IsNullOrWhiteSpace( KitDisplayName ) )
			container.DisplayName = KitDisplayName;

		var deposited = 0;
		for ( var i = 0; i < Items.Count; i++ )
		{
			var entry = Items[i];
			if ( entry is null || string.IsNullOrWhiteSpace( entry.ResourceId ) || entry.Count <= 0 )
				continue;

			deposited += container.HostDepositStack( ResourceCatalog.NormalizeResourceId( entry.ResourceId ), Math.Max( 1, entry.Count ) );
		}

		Log.Info( $"[ChestKitFill] Filled '{GameObject.Name}' with {deposited} item(s) across {Items.Count} kit line(s)." );
	}
}
