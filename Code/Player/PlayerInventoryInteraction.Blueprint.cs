using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Look at an unbuilt blueprint + E finishes it — no hammer needed, any player. The owner traces
/// the real crosshair ray (the build hammer's view ray, so a wall two storeys up is what you are
/// pointing at, not what an eye-level ray happens to cross) and sends intent; the host re-checks
/// reach once and commits through <see cref="BuildAuthority.TryBuildBlueprint"/>.
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Blueprint" ), Title( "Build Action" )]
	public string BlueprintBuildAction { get; set; } = "Use";

	/// <summary>
	/// Twice the 3 m reach of the other E prompts, measured from the eye to the nearest point of the
	/// piece, so tall blueprints can be finished from the ground.
	/// </summary>
	[Property, Group( "Blueprint" ), Title( "Build Reach (m)" ), Range( 1f, 20f )]
	public float BlueprintBuildReachMeters { get; set; } = 6f;

	[Property, Group( "Blueprint" ), Title( "Focus Scan Interval (seconds)" )]
	public float BlueprintFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Unbuilt blueprint under the crosshair (drives the "E — Build …" HUD prompt).</summary>
	public BuildPiece FocusedBlueprint { get; private set; }

	public event Action FocusedBlueprintChanged;

	double _nextBlueprintFocusScanAt;

	void TickBlueprintAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;
		var pressed = !menuOpen && Input.Pressed( BlueprintBuildAction );
		TickBlueprintFocusPrompt( menuOpen, force: pressed );

		if ( !pressed )
			return;

		// While a ghost is on the hammer, E is the snap cycle.
		if ( IsBuildHammerPreviewing() || IsGrappleRetractActive() )
			return;

		if ( FocusedBlueprint is null || !FocusedBlueprint.IsValid() )
			return;

		OwnerBuildBlueprint( FocusedBlueprint );

		// Host: the flag has already flipped — drop the prompt this frame instead of a scan later.
		_nextBlueprintFocusScanAt = 0d;
		TickBlueprintFocusPrompt( menuOpen, force: true );
	}

	void TickBlueprintFocusPrompt( bool menuOpen, bool force = false )
	{
		if ( FocusedBlueprint is not null && ( !FocusedBlueprint.IsValid() || !FocusedBlueprint.IsBlueprint ) )
			SetFocusedBlueprint( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedBlueprint( null );
			return;
		}

		if ( !force && Time.NowDouble < _nextBlueprintFocusScanAt )
			return;

		_nextBlueprintFocusScanAt = Time.NowDouble + Math.Max( 0.05, BlueprintFocusScanIntervalSeconds );
		SetFocusedBlueprint( FindFocusedBlueprint() );
	}

	/// <summary>The blueprint the crosshair rests on, if it is the first thing in the way and within reach.</summary>
	BuildPiece FindFocusedBlueprint()
	{
		if ( !BuildPlacementUtility.TryGetViewRay( GameObject, out var origin, out var direction ) )
			return null;

		var scene = Scene;
		if ( scene is null || !scene.IsValid() )
			return null;

		// The camera sits behind the pawn in third person; the reach is measured from the eye, so
		// the ray gets the camera's own distance on top.
		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, BlueprintBuildReachMeters ) );
		var range = reach + Vector3.DistanceBetween( origin, GameObject.WorldPosition ) + 72f;

		if ( !BuildPlacementUtility.TryTraceBuildPiece( scene, GameObject, null, origin, direction, range, out var piece ) )
			return null;

		if ( !piece.IsBlueprint || !BuildAuthority.IsWithinBlueprintReach( GameObject, piece, BlueprintBuildReachMeters ) )
			return null;

		return piece;
	}

	void SetFocusedBlueprint( BuildPiece piece )
	{
		if ( ReferenceEquals( FocusedBlueprint, piece ) )
			return;

		FocusedBlueprint = piece;
		FocusedBlueprintChanged?.Invoke();
	}

	void OwnerBuildBlueprint( BuildPiece piece )
	{
		if ( piece is null || !piece.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			BuildAuthority.TryBuildBlueprint( GameObject, piece, BlueprintBuildReachMeters );
			return;
		}

		RpcHostBuildBlueprint( piece.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostBuildBlueprint( Guid pieceRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var piece = Scene.Directory.FindByGuid( pieceRootId )?.Components.Get<BuildPiece>();
		if ( piece is null || !piece.IsValid() )
			return;

		// Client sent intent; the host validates reach once, from its own property value.
		BuildAuthority.TryBuildBlueprint( GameObject, piece, BlueprintBuildReachMeters );
	}
}
