using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Deterministic per-peer world scatter identity (trees/bushes). Same WorldSeed + key ⇒ same object
/// on every peer without NetworkSpawn. Host mutations (harvest/chop) address objects by this key.
/// </summary>
[Title( "World Scatter Identity" )]
public sealed class WorldScatterIdentity : Component
{
	static readonly Dictionary<string, WorldScatterIdentity> ByKey = new( StringComparer.Ordinal );

	[Property] public string StableKey { get; set; } = string.Empty;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		Register();
	}

	protected override void OnDisabled()
	{
		Unregister();
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Unregister();
		base.OnDestroy();
	}

	public void Configure( string stableKey )
	{
		Unregister();
		StableKey = stableKey ?? string.Empty;
		Register();
	}

	void Register()
	{
		if ( string.IsNullOrWhiteSpace( StableKey ) )
			return;
		ByKey[StableKey] = this;
	}

	void Unregister()
	{
		if ( string.IsNullOrWhiteSpace( StableKey ) )
			return;
		if ( ByKey.TryGetValue( StableKey, out var existing ) && existing == this )
			ByKey.Remove( StableKey );
	}

	public static bool TryFind( string stableKey, out WorldScatterIdentity identity )
	{
		identity = null;
		if ( string.IsNullOrWhiteSpace( stableKey ) )
			return false;
		return ByKey.TryGetValue( stableKey, out identity ) && identity is not null && identity.IsValid();
	}

	public static bool TryFindHarvestNode( string stableKey, out ResourceItemDefinition node )
	{
		node = null;
		if ( !TryFind( stableKey, out var identity ) )
			return false;
		node = identity.Components.Get<ResourceItemDefinition>( FindMode.EverythingInSelfAndDescendants );
		return node is not null && node.IsValid();
	}

	public static bool TryFindChopableTree( string stableKey, out ChopableTree tree )
	{
		tree = null;
		if ( !TryFind( stableKey, out var identity ) )
			return false;
		tree = identity.Components.Get<ChopableTree>( FindMode.EverythingInSelfAndDescendants );
		return tree is not null && tree.IsValid();
	}

	/// <summary>Host→peers: hide/break the local deterministic copy matching this key.</summary>
	public static void HostBroadcastBroken( string stableKey )
	{
		if ( string.IsNullOrWhiteSpace( stableKey ) || !Networking.IsHost )
			return;

		var auth = CombatAuthority.Instance;
		if ( auth is null || !auth.IsValid() )
		{
			ApplyBrokenLocal( stableKey );
			return;
		}

		auth.HostBroadcastScatterBroken( stableKey );
	}

	public static void HostBroadcastHarvestDepleted( string stableKey )
	{
		if ( string.IsNullOrWhiteSpace( stableKey ) || !Networking.IsHost )
			return;

		var auth = CombatAuthority.Instance;
		if ( auth is null || !auth.IsValid() )
		{
			ApplyHarvestDepletedLocal( stableKey );
			return;
		}

		auth.HostBroadcastScatterHarvestDepleted( stableKey );
	}

	internal static void ApplyBrokenLocal( string stableKey )
	{
		if ( !TryFindChopableTree( stableKey, out var tree ) )
			return;

		tree.ApplyRemoteBrokenPresentation();
	}

	internal static void ApplyHarvestDepletedLocal( string stableKey )
	{
		if ( !TryFindHarvestNode( stableKey, out var node ) )
			return;

		node.ApplyRemoteDepletedPresentation();
	}
}
