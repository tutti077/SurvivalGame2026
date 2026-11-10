using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Placeable drying rack (Mark 2026-11-08): no menu. E pulls applicable items straight from the
/// player's inventory into its <see cref="SlotCount"/> slots, one item per slot, and each slot
/// finishes after the process's game-clock hours (<c>Assets/data/drying_rack.json</c>: hide → 2
/// leather in 6 h; jerky from meat lands there once a jerky item exists). E again collects whatever
/// is ready, then refills the freed slots. Host-authoritative: the owner sends intent through
/// <c>PlayerInventoryInteraction</c>, the host checks range once and runs <see cref="HostUse"/>.
/// Slot contents live only on the host; the two synced counts drive every peer's prompt.
/// Hours come from <see cref="EnvironmentDayNightCycle.TryGetGameHours"/>; a scene without a
/// cycle counts <see cref="FallbackSecondsPerHour"/> real seconds per hour. Not persisted.
/// </summary>
[Title( "Drying Rack" )]
public sealed class DryingRack : Component
{
	public const string StationId = "drying_rack";
	public const int SlotCount = 3;
	public const float FallbackSecondsPerHour = 60f;

	/// <summary>The player must stand within this distance to load or collect (host re-checks with one metre of slack).</summary>
	[Property, Group( "Drying Rack" ), Title( "Use range (m)" ), Range( 1f, 10f )]
	public float UseRangeMeters { get; set; } = 4f;

	/// <summary>Slots holding an item that is still drying.</summary>
	[Sync( SyncFlags.FromHost )] public int DryingCount { get; private set; }

	/// <summary>Slots holding a finished output waiting to be collected.</summary>
	[Sync( SyncFlags.FromHost )] public int ReadyCount { get; private set; }

	readonly string[] _inputs = new string[SlotCount];
	readonly double[] _readyAtHours = new double[SlotCount];
	readonly bool[] _ready = new bool[SlotCount];
	double _nextCheckAt;

	public bool HasHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	public bool IsEmpty => DryingCount == 0 && ReadyCount == 0;

	/// <summary>Prompt line for the HUD, from the synced counts.</summary>
	public string PromptText
	{
		get
		{
			if ( ReadyCount > 0 )
				return $"Collect from Drying Rack ({ReadyCount} ready)";
			if ( DryingCount >= SlotCount )
				return "Drying Rack (full, drying)";
			if ( DryingCount > 0 )
				return $"Load Drying Rack ({DryingCount}/{SlotCount} drying)";
			return "Load Drying Rack";
		}
	}

	protected override void OnFixedUpdate()
	{
		if ( !HasHostAuthority || Time.NowDouble < _nextCheckAt )
			return;

		_nextCheckAt = Time.NowDouble + 0.5;
		var now = NowHours();
		var changed = false;
		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( string.IsNullOrEmpty( _inputs[i] ) || _ready[i] || now < _readyAtHours[i] )
				continue;

			_ready[i] = true;
			changed = true;
		}

		if ( changed )
			RefreshCounts();
	}

	/// <summary>
	/// Host: collect every finished output into <paramref name="inventory"/> (a stack that does not fit
	/// stays on the rack), then fill each free slot with one applicable item taken from the inventory.
	/// Returns true when anything moved.
	/// </summary>
	public bool HostUse( PlayerInventory inventory )
	{
		if ( !HasHostAuthority || inventory is null || !inventory.IsValid() )
			return false;

		DryingRackCatalog.EnsureLoaded();
		var moved = false;

		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( string.IsNullOrEmpty( _inputs[i] ) || !_ready[i] )
				continue;

			if ( !DryingRackCatalog.TryGetProcess( _inputs[i], out var process ) )
			{
				ClearSlot( i );
				moved = true;
				continue;
			}

			if ( !inventory.HostTryAddResource( process.Output, process.OutputAmount ) )
				continue;

			ClearSlot( i );
			moved = true;
		}

		var now = NowHours();
		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( !string.IsNullOrEmpty( _inputs[i] ) )
				continue;

			if ( !TryTakeOneInput( inventory, out var process ) )
				break;

			_inputs[i] = process.Input;
			_ready[i] = false;
			_readyAtHours[i] = now + Math.Max( 0.01, process.Hours );
			moved = true;
		}

		if ( moved )
			RefreshCounts();

		return moved;
	}

	static bool TryTakeOneInput( PlayerInventory inventory, out DryingRackProcess process )
	{
		process = null;
		var processes = DryingRackCatalog.Processes;
		for ( var i = 0; i < processes.Count; i++ )
		{
			var candidate = processes[i];
			if ( candidate is null || inventory.CountResource( candidate.Input ) <= 0 )
				continue;

			var one = new List<CraftingIngredient> { new() { ResourceId = candidate.Input, Amount = 1 } };
			if ( !inventory.HostTryConsumeResources( one ) )
				continue;

			process = candidate;
			return true;
		}

		return false;
	}

	void ClearSlot( int i )
	{
		_inputs[i] = null;
		_ready[i] = false;
		_readyAtHours[i] = 0;
	}

	void RefreshCounts()
	{
		var drying = 0;
		var ready = 0;
		for ( var i = 0; i < SlotCount; i++ )
		{
			if ( string.IsNullOrEmpty( _inputs[i] ) )
				continue;
			if ( _ready[i] )
				ready++;
			else
				drying++;
		}

		DryingCount = drying;
		ReadyCount = ready;
	}

	/// <summary>Game-clock hours; without a day cycle in the scene, real time at <see cref="FallbackSecondsPerHour"/>.</summary>
	static double NowHours() =>
		EnvironmentDayNightCycle.TryGetGameHours( out var hours ) ? hours : Time.NowDouble / FallbackSecondsPerHour;

	/// <summary>Is <paramref name="player"/> within use range (plus <paramref name="slackMeters"/>) of this rack?</summary>
	public bool IsWithinUseRange( GameObject player, float slackMeters = 1f )
	{
		if ( player is null || !player.IsValid() )
			return false;

		var range = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseRangeMeters ) + Math.Max( 0f, slackMeters ) );
		return (GameObject.WorldPosition - player.WorldPosition).LengthSquared <= range * range;
	}

	public static bool TryFindOnHierarchy( GameObject hitObject, out DryingRack rack )
	{
		rack = null;
		if ( hitObject is null || !hitObject.IsValid() )
			return false;

		for ( var current = hitObject; current.IsValid(); current = current.Parent )
		{
			var candidate = current.Components.Get<DryingRack>();
			if ( candidate is null || !candidate.Enabled || !candidate.IsBuilt )
				continue;

			rack = candidate;
			return true;
		}

		return false;
	}

	/// <summary>Not a preview ghost or blueprint.</summary>
	public bool IsBuilt
	{
		get
		{
			if ( GameObject.Tags.Has( "buildpreview" ) )
				return false;

			var piece = Components.Get<BuildPiece>();
			return piece is null || ( !piece.IsPreviewGhost && !piece.IsBlueprint );
		}
	}
}
