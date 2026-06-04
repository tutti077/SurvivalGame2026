using System;
using Sandbox;

namespace Survival;

/// <summary>
/// World harvest node: designer-tuned yield, tool requirements, depletion, and respawn.
/// Host applies harvest ticks via <see cref="TryPerformHarvestTick"/>; inventory / interaction hooks in later.
/// </summary>
[Title( "Resource Harvest Node" )]
public sealed class ResourceHarvestNode : Component
{
	[Property, Group( "Resource" ), Title( "Resource Display Name" )]
	public string DisplayName { get; set; } = "Resource";

	[Property, Group( "Resource" ), Title( "Resource ID" )]
	public string ResourceId { get; set; } = "resource";

	[Property, Group( "Yield" )]
	public int BaseYieldPerTickLow { get; set; } = 1;

	[Property, Group( "Yield" )]
	public int BaseYieldPerTickHigh { get; set; } = 1;

	[Property, Group( "Requirements" ), Title( "Tool Type Required" )]
	public HarvestToolType ToolTypeRequired { get; set; } = HarvestToolType.Axe;

	[Property, Group( "Requirements" ), Title( "Minimum Tool Tier" ), Range( 0, 5 )]
	public int MinimumToolTier { get; set; }

	[Property, Group( "Requirements" ), Title( "Hand Harvest Range" )]
	public float HandHarvestRange { get; set; } = 80f;

	[Property, Group( "Depletion" ), Title( "Harvest Ticks Until Gone" )]
	public int HarvestTicksUntilGone { get; set; } = 10;

	[Property, Group( "Depletion" ), Title( "Respawn Rate (seconds)" )]
	public float RespawnRateSeconds { get; set; } = 60f;

	[Property, Group( "Setup" ), Title( "Auto Ensure Trace Collider" )]
	public bool AutoEnsureTraceCollider { get; set; } = true;

	[Property, Group( "Setup" ), Title( "Hide Model When Depleted" )]
	public bool HideModelWhenDepleted { get; set; } = true;

	[Property, Group( "Debug" )]
	public bool LogHarvest { get; set; }

	public bool IsDepleted => _isDepleted;
	public int RemainingHarvestTicks => _remainingHarvestTicks;

	bool _isDepleted;
	int _remainingHarvestTicks;
	double _respawnAt;
	readonly Random _rng = new();

	bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		EnsureTraceCollider();
		ResetToFull();
	}

	protected override void OnUpdate()
	{
		if ( !_isDepleted || !IsHostAuthority )
			return;

		if ( RespawnRateSeconds <= 0f )
			return;

		if ( Time.NowDouble < _respawnAt )
			return;

		ResetToFull();
		if ( LogHarvest )
			Log.Info( $"[ResourceHarvestNode] {GameObject.Name}: respawned ({DisplayName}, id={ResourceId})." );

		if ( GameObject.Network is { Active: true } )
			RpcSyncHarvestState( false, _remainingHarvestTicks );
	}

	public void ResetToFull()
	{
		_isDepleted = false;
		_remainingHarvestTicks = Math.Max( 0, HarvestTicksUntilGone );
		_respawnAt = 0;
		ApplyDepletedVisual( false );
	}

	public bool CanHarvestWith( HarvestToolType toolType, int toolTier )
	{
		if ( !Active || !GameObject.IsValid() || !GameObject.Enabled || _isDepleted )
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

		var yield = RollYieldAmount();
		_remainingHarvestTicks--;

		var depletedThisTick = false;
		if ( _remainingHarvestTicks <= 0 )
		{
			depletedThisTick = true;
			MarkDepleted();
		}

		if ( LogHarvest )
		{
			Log.Info(
				$"[ResourceHarvestNode] {GameObject.Name}: harvest tick +{yield} {ResourceId} ({DisplayName}), remaining={_remainingHarvestTicks}, tool={toolType} tier={toolTier}." );
		}

		return new HarvestTickResult
		{
			Success = true,
			YieldAmount = yield,
			ResourceId = ResourceId,
			DisplayName = DisplayName,
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
		if ( !AutoEnsureTraceCollider || HasSolidCollider( GameObject ) )
			return;

		var renderer = Components.Get<ModelRenderer>();
		if ( renderer?.Model is not null )
		{
			var modelCol = Components.Get<ModelCollider>() ?? Components.Create<ModelCollider>();
			modelCol.Model = renderer.Model;
			modelCol.Static = true;
			if ( LogHarvest )
				Log.Info( $"[ResourceHarvestNode] {GameObject.Name}: added ModelCollider for harvest traces." );
			return;
		}

		var sphere = Components.Get<SphereCollider>() ?? Components.Create<SphereCollider>();
		sphere.Radius = 16f;
		sphere.Static = true;
		if ( LogHarvest )
			Log.Info( $"[ResourceHarvestNode] {GameObject.Name}: added SphereCollider fallback for harvest traces." );
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

	int RollYieldAmount()
	{
		var low = Math.Max( 0, BaseYieldPerTickLow );
		var high = Math.Max( low, BaseYieldPerTickHigh );
		if ( low == high )
			return low;
		return _rng.Next( low, high + 1 );
	}
}
