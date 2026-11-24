using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at a placed <see cref="BuildBed"/>: the prompt says whose bed it is; E on an unclaimed (or abandoned)
/// bed claims it as your respawn point and you climb in, E on your own bed climbs in, someone else's bed does
/// nothing. Owner traces and sends intent; the host re-checks reach once and commits through
/// <see cref="BuildBed.HostUse"/>. Lying in the bed and getting up are <c>PlayerMovement.Bed.cs</c>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Bed" ), Title( "Use Action" )]
	public string BedUseAction { get; set; } = "Use";

	[Property, Group( "Bed" ), Title( "Focus Scan Interval (seconds)" )]
	public float BedFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Bed under the crosshair (drives the "Sleep in Your Bed" / "Mark's Bed" / "E — Claim Bed and Sleep" HUD prompt).</summary>
	public BuildBed FocusedBed { get; private set; }

	public event Action FocusedBedChanged;

	double _nextBedFocusScanAt;
	PlayerMovement _bedMovement;
	double _lastLyingInBedAt = -1000;

	/// <summary>The local pawn lies in a bed (chests, doors, other beds are not reachable from it).</summary>
	public bool IsLyingInBed
	{
		get
		{
			_bedMovement ??= Components.Get<PlayerMovement>();
			return _bedMovement is { IsInBed: true };
		}
	}

	void TickBedAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( BedUseAction );
		TickBedFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		// The E that got the pawn up must not put it straight back in.
		if ( RealTime.GlobalNow - _lastLyingInBedAt < 0.4 )
			return;

		if ( FocusedBed is null || !FocusedBed.IsValid() || !FocusedBed.CanUse( GameObject ) )
			return;

		OwnerUseBed( FocusedBed );
	}

	void TickBedFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedBed is not null && !FocusedBed.IsValid() )
			SetFocusedBed( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedBed( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextBedFocusScanAt )
			return;

		_nextBedFocusScanAt = Time.NowDouble + Math.Max( 0.05, BedFocusScanIntervalSeconds );

		// Chest / station / workbench / campfire / door / trap under the reticule keep their prompt.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null
		     || FocusedCampfire is not null || FocusedDoor is not null || FocusedBlueprint is not null || FocusedTrap is not null )
		{
			SetFocusedBed( null );
			return;
		}

		var reach = FocusedBed is { IsValid: true } ? FocusedBed.UseReachMeters : 3f;
		if ( BuildBed.TryFindFocusedBed( GameObject, reach, out var bed ) )
			SetFocusedBed( bed );
		else
			SetFocusedBed( null );
	}

	void SetFocusedBed( BuildBed bed )
	{
		if ( ReferenceEquals( FocusedBed, bed ) )
			return;

		FocusedBed = bed;
		FocusedBedChanged?.Invoke();
	}

	/// <summary>Owner: E on <paramref name="bed"/> (also the <c>bed_use</c> console command).</summary>
	public void OwnerUseBed( BuildBed bed )
	{
		if ( bed is null || !bed.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			if ( bed.IsWithinUseReach( GameObject ) )
				bed.HostUse( GameObject );
			return;
		}

		RpcHostUseBed( bed.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostUseBed( Guid bedRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var bed = Scene.Directory.FindByGuid( bedRootId )?.Components.Get<BuildBed>();
		if ( bed is null || !bed.IsValid() )
			return;

		// Client sent intent; the host validates reach once — no claiming or climbing into beds across the map.
		if ( !bed.IsWithinUseReach( GameObject ) )
			return;

		bed.HostUse( GameObject );
	}
}
