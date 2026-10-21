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
	/// <summary>
	/// Keys whose tree was felled (stage) or whose node was harvested, remembered for the session: a deterministic
	/// scatter object that is destroyed and re-planted (chunk unload / reload, the tree object range) comes back in
	/// that state instead of regrowing (2026-10-20). Cleared with the world.
	/// </summary>
	static readonly Dictionary<string, int> BrokenStages = new( StringComparer.Ordinal );
	static readonly HashSet<string> DepletedKeys = new( StringComparer.Ordinal );

	public static void ClearRemembered()
	{
		BrokenStages.Clear();
		DepletedKeys.Clear();
	}

	/// <summary>Re-apply a remembered felled / harvested state to a freshly planted object (called right after Configure).</summary>
	public static void ReapplyRemembered( WorldScatterIdentity identity )
	{
		if ( identity is null || !identity.IsValid() || string.IsNullOrWhiteSpace( identity.StableKey ) )
			return;

		if ( BrokenStages.TryGetValue( identity.StableKey, out var stage ) )
		{
			var tree = identity.Components.Get<ChopableTree>( FindMode.EverythingInSelfAndDescendants );
			if ( tree is not null && tree.IsValid() )
				tree.ApplyStagePresentation( stage );
		}

		if ( DepletedKeys.Contains( identity.StableKey ) && TryFindHarvestNode( identity.StableKey, out var node ) )
			node.ApplyRemoteDepletedPresentation();
	}

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

	/// <summary>Host→peers: move the local deterministic tree matching this key to <paramref name="stage"/>
	/// (<see cref="ChopableTree.StageStump"/> or <see cref="ChopableTree.StageGone"/>).</summary>
	public static void HostBroadcastBroken( string stableKey, int stage )
	{
		if ( string.IsNullOrWhiteSpace( stableKey ) || !Networking.IsHost )
			return;

		var auth = CombatAuthority.Instance;
		if ( auth is null || !auth.IsValid() )
		{
			ApplyBrokenLocal( stableKey, stage );
			return;
		}

		auth.HostBroadcastScatterBroken( stableKey, stage );
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

	internal static void ApplyBrokenLocal( string stableKey, int stage )
	{
		if ( !string.IsNullOrWhiteSpace( stableKey ) )
			BrokenStages[stableKey] = stage;
		if ( !TryFindChopableTree( stableKey, out var tree ) )
			return;

		tree.ApplyStagePresentation( stage );
	}

	internal static void ApplyHarvestDepletedLocal( string stableKey )
	{
		if ( !string.IsNullOrWhiteSpace( stableKey ) )
			DepletedKeys.Add( stableKey );
		if ( !TryFindHarvestNode( stableKey, out var node ) )
			return;

		node.ApplyRemoteDepletedPresentation();
	}
}
