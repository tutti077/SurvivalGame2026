using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Resource identity (catalog + inventory UI) and optional world harvest behavior on the same GameObject.
/// Library-only entries leave <see cref="Harvestable"/> off; things you pick in the world turn it on.
/// </summary>
[Title( "Resource Item Definition" )]
public sealed class ResourceItemDefinition : Component
{
	[Property, Group( "Identity" )]
	public string ResourceId { get; set; } = string.Empty;

	[Property, Group( "Identity" )]
	public string DisplayName { get; set; } = "Rock";

	[Property, Group( "Identity" ), Title( "Max Stack" ), Range( 1, 9999 )]
	public int MaxStack { get; set; } = 64;

	/// <summary>Project-relative image path (e.g. <c>ui/items/rock.jpg</c> or <c>.png</c>).</summary>
	[Property, Group( "UI" ), Title( "Icon Path" )]
	public string Icon { get; set; } = "ui/items/rock.jpg";

	[Property, Group( "UI" )]
	public Color FallbackColor { get; set; } = new Color( 0.58f, 0.50f, 0.42f );

	[Property, Group( "Harvest" ), Title( "Harvestable" )]
	public bool Harvestable { get; set; }

	[Property, Group( "Harvest" ), Title( "Harvest Yields" )]
	public List<HarvestYieldEntry> HarvestYields { get; set; } = new();

	[Property, Group( "Harvest" ), Title( "Legacy Yield Low (used when Harvest Yields is empty)" ), Range( 0, 200 )]
	public int BaseYieldPerTickLow { get; set; } = 1;

	[Property, Group( "Harvest" ), Title( "Legacy Yield High (used when Harvest Yields is empty)" ), Range( 0, 200 )]
	public int BaseYieldPerTickHigh { get; set; } = 1;

	[Property, Group( "Harvest" ), Title( "Tool Type Required" )]
	public HarvestToolType ToolTypeRequired { get; set; } = HarvestToolType.Axe;

	[Property, Group( "Harvest" ), Title( "Minimum Tool Tier" ), Range( 0, 5 )]
	public int MinimumToolTier { get; set; }

	[Property, Group( "Harvest" ), Title( "Hand Harvest Range" )]
	public float HandHarvestRange { get; set; } = 80f;

	[Property, Group( "Harvest" ), Title( "Harvest Ticks Until Gone" )]
	public int HarvestTicksUntilGone { get; set; } = 10;

	[Property, Group( "Harvest" ), Title( "Respawn Rate (seconds)" )]
	public float RespawnRateSeconds { get; set; } = 60f;

	[Property, Group( "Harvest" ), Title( "Auto Ensure Trace Collider" )]
	public bool AutoEnsureTraceCollider { get; set; } = true;

	[Property, Group( "Harvest" ), Title( "Hide Model When Depleted" )]
	public bool HideModelWhenDepleted { get; set; } = true;

	[Property, Group( "Harvest" ), Title( "Log Harvest" )]
	public bool LogHarvest { get; set; }

	public bool IsDepleted => Harvestable && _isDepleted;
	public int RemainingHarvestTicks => Harvestable ? _remainingHarvestTicks : 0;

	Texture _resolvedIcon;
	string _resolvedIconPath;
	bool _isDepleted;
	int _remainingHarvestTicks;
	double _respawnAt;
	readonly Random _rng = new();

	bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnEnabled()
	{
		_resolvedIcon = null;
		_resolvedIconPath = null;
		base.OnEnabled();

		if ( !string.IsNullOrWhiteSpace( ResourceId ) )
			ResourceCatalog.Register( this );

		if ( Harvestable )
			ResourceHarvestRegistry.Register( this );
	}

	protected override void OnDisabled()
	{
		if ( Harvestable )
			ResourceHarvestRegistry.Unregister( this );

		ResourceCatalog.Unregister( this );
		base.OnDisabled();
	}

	protected override void OnStart()
	{
		base.OnStart();

		ResourceDefinitionCatalog.TryApplyIdentity( this );

		if ( !Harvestable )
			return;

		EnsureTraceCollider();
		if ( !AutoEnsureTraceCollider )
			DisableSolidColliders( GameObject );
		ResetToFull();
	}

	protected override void OnUpdate()
	{
		if ( !Harvestable || !_isDepleted || !IsHostAuthority )
			return;

		if ( RespawnRateSeconds <= 0f )
			return;

		if ( Time.NowDouble < _respawnAt )
			return;

		ResetToFull();
		if ( LogHarvest )
			Log.Info( $"[ResourceItemDefinition] {GameObject.Name}: respawned ({DisplayName}, id={ResourceId})." );

		if ( GameObject.Network is { Active: true } )
			RpcSyncHarvestState( false, _remainingHarvestTicks );
	}

	public Texture ResolveIcon()
	{
		if ( string.IsNullOrWhiteSpace( Icon ) )
		{
			_resolvedIcon = null;
			_resolvedIconPath = null;
			return null;
		}

		if ( _resolvedIcon is not null && _resolvedIcon.IsValid() && string.Equals( _resolvedIconPath, Icon, StringComparison.OrdinalIgnoreCase ) )
			return _resolvedIcon;

		_resolvedIconPath = Icon;
		_resolvedIcon = MenuUiTextures.TryLoad( Icon );
		return _resolvedIcon;
	}

	public void InvalidateIconCache()
	{
		_resolvedIcon = null;
		_resolvedIconPath = null;
	}

	/// <summary>Catalog-only entry spawned from <see cref="ResourceDefinitionCatalog"/>.</summary>
	public void ApplyCatalogData( ResourceDefinitionData data )
	{
		if ( data is null || string.IsNullOrWhiteSpace( data.Id ) )
			return;

		ResourceId = ResourceCatalog.NormalizeResourceId( data.Id );
		DisplayName = data.DisplayName;
		Icon = data.Icon;
		MaxStack = Math.Max( 1, data.MaxStack );
		FallbackColor = ResourceDefinitionCatalog.ParseFallbackColor( data.FallbackColor );
		Harvestable = false;
		InvalidateIconCache();
		ResourceCatalog.Register( this );
	}

	internal ResourceCatalog.ResourceDefinition ToCatalogEntry()
	{
		return new ResourceCatalog.ResourceDefinition( DisplayName, ResolveIcon(), FallbackColor, Math.Max( 1, MaxStack ) );
	}

	public void ResetToFull()
	{
		if ( !Harvestable )
			return;

		_isDepleted = false;
		_remainingHarvestTicks = Math.Max( 0, HarvestTicksUntilGone );
		_respawnAt = 0;
		ApplyDepletedVisual( false );
	}

	public bool CanHarvestWith( HarvestToolType toolType, int toolTier )
	{
		if ( !Harvestable || !Active || !GameObject.IsValid() || !GameObject.Enabled || _isDepleted )
			return false;

		if ( toolType != ToolTypeRequired )
			return false;

		if ( ToolTypeRequired == HarvestToolType.Hand )
			return true;

		return toolTier >= MinimumToolTier;
	}

	/// <summary>Host-only: apply one harvest tick and return yield for this tick (0 if rejected or depleted).</summary>
	public HarvestTickResult TryPerformHarvestTick( HarvestToolType toolType, int toolTier )
	{
		if ( !Harvestable )
			return HarvestTickResult.Failed( "not a harvest node" );

		if ( !IsHostAuthority )
			return HarvestTickResult.Failed( "not host" );

		if ( !CanHarvestWith( toolType, toolTier ) )
		{
			if ( _isDepleted )
				return HarvestTickResult.Failed( "depleted" );
			if ( toolType != ToolTypeRequired )
				return HarvestTickResult.Failed( "wrong tool type" );
			if ( ToolTypeRequired != HarvestToolType.Hand && toolTier < MinimumToolTier )
				return HarvestTickResult.Failed( "tool tier too low" );
			return HarvestTickResult.Failed( "unavailable" );
		}

		if ( _remainingHarvestTicks <= 0 )
		{
			MarkDepleted();
			return HarvestTickResult.Failed( "depleted" );
		}

		var loot = RollHarvestLoot();
		_remainingHarvestTicks--;

		var depletedThisTick = false;
		if ( _remainingHarvestTicks <= 0 )
		{
			depletedThisTick = true;
			MarkDepleted();
		}

		if ( LogHarvest )
		{
			var lootSummary = loot.Length > 0
				? string.Join( ", ", Array.ConvertAll( loot, l => $"+{l.Amount} {l.ResourceId}" ) )
				: "nothing";
			Log.Info(
				$"[ResourceItemDefinition] {GameObject.Name}: harvest tick {lootSummary} ({DisplayName}), remaining={_remainingHarvestTicks}, tool={toolType} tier={toolTier}." );
		}

		return new HarvestTickResult
		{
			Success = true,
			Loot = loot,
			RemainingHarvestTicks = _remainingHarvestTicks,
			DepletedThisTick = depletedThisTick,
		};
	}

	void MarkDepleted()
	{
		_isDepleted = true;
		_remainingHarvestTicks = 0;
		if ( RespawnRateSeconds > 0f )
			_respawnAt = Time.NowDouble + Math.Max( 0.05, RespawnRateSeconds );

		ApplyDepletedVisual( true );

		if ( GameObject.Network is { Active: true } )
			RpcSyncHarvestState( true, 0 );
	}

	void EnsureTraceCollider()
	{
		if ( !AutoEnsureTraceCollider || HasSolidCollider( GameObject ) || HasDisabledSolidColliderOptOut( GameObject ) )
			return;

		var renderer = Components.Get<ModelRenderer>();
		if ( renderer?.Model is not null )
		{
			var modelCol = Components.Create<ModelCollider>();
			modelCol.Model = renderer.Model;
			modelCol.Static = true;
			if ( LogHarvest )
				Log.Info( $"[ResourceItemDefinition] {GameObject.Name}: added ModelCollider for harvest traces." );
			return;
		}

		var sphere = Components.Get<SphereCollider>() ?? Components.Create<SphereCollider>();
		sphere.Radius = 16f;
		sphere.Static = true;
		if ( LogHarvest )
			Log.Info( $"[ResourceItemDefinition] {GameObject.Name}: added SphereCollider fallback for harvest traces." );
	}

	static bool HasSolidCollider( GameObject root )
	{
		if ( !root.IsValid() )
			return false;

		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.Enabled || col.IsTrigger )
				continue;
			return true;
		}

		return false;
	}

	static void DisableSolidColliders( GameObject root )
	{
		if ( !root.IsValid() )
			return;

		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || col.IsTrigger || !col.Enabled )
				continue;
			col.Enabled = false;
		}

		foreach ( var rb in root.Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( rb is null || !rb.Enabled )
				continue;
			rb.Enabled = false;
		}
	}

	/// <summary>Disabled non-trigger collider on this object = designer opted out of auto physics.</summary>
	static bool HasDisabledSolidColliderOptOut( GameObject root )
	{
		if ( !root.IsValid() )
			return false;

		foreach ( var col in root.Components.GetAll<Collider>( FindMode.EverythingInSelf ) )
		{
			if ( col is null || col.IsTrigger || col.Enabled )
				continue;
			return true;
		}

		return false;
	}

	void ApplyDepletedVisual( bool depleted )
	{
		if ( !HideModelWhenDepleted )
			return;

		foreach ( var renderer in GameObject.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null )
				continue;
			renderer.Enabled = !depleted;
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcSyncHarvestState( bool depleted, int remainingTicks )
	{
		_isDepleted = depleted;
		_remainingHarvestTicks = Math.Max( 0, remainingTicks );
		ApplyDepletedVisual( depleted );
	}

	/// <summary>Guaranteed loot lines that must fit before a harvest tick is allowed.</summary>
	public void CollectGuaranteedCapacityNeeds( List<(string ResourceId, int Amount)> needs )
	{
		needs.Clear();
		foreach ( var entry in EnumerateEffectiveYieldEntries() )
		{
			if ( string.IsNullOrWhiteSpace( entry.ResourceId ) )
				continue;

			if ( entry.ChancePercent < 100f )
				continue;

			var max = GetEntryMaxAmount( entry );
			if ( max > 0 )
				needs.Add( (entry.ResourceId, max) );
		}
	}

	/// <summary>True when at least one yield entry can produce loot this tick.</summary>
	public bool HasAnyPossibleLoot()
	{
		foreach ( var entry in EnumerateEffectiveYieldEntries() )
		{
			if ( !string.IsNullOrWhiteSpace( entry.ResourceId ) && entry.ChancePercent > 0f )
				return true;
		}

		return false;
	}

	IEnumerable<HarvestYieldEntry> EnumerateEffectiveYieldEntries()
	{
		if ( HarvestYields is { Count: > 0 } )
		{
			foreach ( var entry in HarvestYields )
			{
				if ( entry is not null )
					yield return entry;
			}

			yield break;
		}

		yield return new HarvestYieldEntry
		{
			ResourceId = ResourceId,
			AmountLow = BaseYieldPerTickLow,
			AmountHigh = BaseYieldPerTickHigh,
			ChancePercent = 100f,
		};
	}

	HarvestLootItem[] RollHarvestLoot()
	{
		var rolled = new List<HarvestLootItem>();

		foreach ( var entry in EnumerateEffectiveYieldEntries() )
		{
			if ( string.IsNullOrWhiteSpace( entry.ResourceId ) )
				continue;

			if ( !RollChance( entry.ChancePercent ) )
				continue;

			var amount = RollEntryAmount( entry );
			if ( amount <= 0 )
				continue;

			var display = ResourceCatalog.Resolve( entry.ResourceId ).DisplayName;
			var resourceId = ResourceCatalog.NormalizeResourceId( entry.ResourceId );
			rolled.Add( new HarvestLootItem( resourceId, amount, display ) );
		}

		return rolled.ToArray();
	}

	static int GetEntryMaxAmount( HarvestYieldEntry entry )
	{
		var low = Math.Max( 0, entry.AmountLow );
		var high = Math.Max( low, entry.AmountHigh );
		return high;
	}

	bool RollChance( float chancePercent )
	{
		if ( chancePercent >= 100f )
			return true;

		if ( chancePercent <= 0f )
			return false;

		return _rng.NextDouble() * 100.0 < chancePercent;
	}

	int RollEntryAmount( HarvestYieldEntry entry )
	{
		var low = Math.Max( 0, entry.AmountLow );
		var high = Math.Max( low, entry.AmountHigh );
		if ( low == high )
			return low;

		return _rng.Next( low, high + 1 );
	}
}
