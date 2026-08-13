using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Host: if this object's <see cref="ContainerInventory"/> is empty, deposit starter gear once.
/// Put on an authored scene chest — does not spawn anything at runtime.
/// </summary>
[Title( "Starter Kit Chest Fill" )]
public sealed class StarterKitChestFill : Component
{
	static readonly string[] KitResourceIds =
	{
		"basic_hook",
		"basic_wingsuit",
		"build_hammer",
		"basic_sword",
	};

	[Property, Title( "Sets of each item" ), Range( 1, 8 ), Step( 1 )]
	public int SetsOfEach { get; set; } = 4;

	[Property, Title( "Display name when filled" )]
	public string KitDisplayName { get; set; } = "Starter Kit";

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

		if ( !container.IsEmpty )
			return;

		EquipmentCatalog.EnsureLoaded();

		if ( !string.IsNullOrWhiteSpace( KitDisplayName ) )
			container.DisplayName = KitDisplayName;

		var sets = Math.Clamp( SetsOfEach, 1, 8 );
		for ( var s = 0; s < sets; s++ )
		{
			for ( var i = 0; i < KitResourceIds.Length; i++ )
				container.HostDepositStack( KitResourceIds[i], 1 );
		}

		Log.Info( $"[StarterKitChestFill] Filled '{GameObject.Name}' with {sets} set(s)." );
	}
}
