using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Hammer-placed foot trap (<c>trap_small</c> / <c>trap_large</c> in <c>data/build_pieces.json</c>).
/// Armed, it watches its trigger volume on the host at a low poll rate; the first creature whose
/// <see cref="TrapSize"/> fits the [<see cref="MinCatchSize"/>, <see cref="MaxCatchSize"/>] band
/// is held in place — players for <see cref="PlayerHoldSeconds"/>, animals and enemies for
/// <see cref="EntityHoldSeconds"/>. A trap is built disarmed and left disarmed after every
/// release; someone has to look at it and press E to arm it. Whoever arms it is ignored until they step off.
/// <para>
/// The hold itself lives on the victim's movement owner: <see cref="PlayerMovement.HostSetTrapped"/>
/// for pawns, <see cref="EntityLocomotion.HostSetTrapped"/> for anything the brains drive.
/// </para>
/// </summary>
[Title( "Bear Trap" )]
public sealed class BearTrap : Component
{
	public const string TrappedStatusId = "trapped";

	/// <summary>Polls of the trigger volume per second while armed — a trap is not a hot path.</summary>
	const float ArmedPollHz = 10f;

	/// <summary>A held victim this far (m) from the trap has been teleported / respawned — let go.</summary>
	const float VictimLostDistanceMeters = 2.5f;

	static readonly List<BearTrap> Active = new();

	[Property, Group( "Trap" ), Title( "Smallest creature it holds" )]
	public TrapSize MinCatchSize { get; set; } = TrapSize.Small;

	[Property, Group( "Trap" ), Title( "Largest creature it holds" )]
	public TrapSize MaxCatchSize { get; set; } = TrapSize.Small;

	[Property, Group( "Trap" ), Title( "Player hold (seconds)" ), Range( 0.5f, 30f )]
	public float PlayerHoldSeconds { get; set; } = 5f;

	[Property, Group( "Trap" ), Title( "Animal / enemy hold (seconds)" ), Range( 0.5f, 60f )]
	public float EntityHoldSeconds { get; set; } = 10f;

	/// <summary>Bite on the way in. 0 = the trap only holds.</summary>
	[Property, Group( "Trap" ), Title( "Damage on catch" ), Range( 0f, 100f )]
	public float CatchDamage { get; set; }

	[Property, Group( "Trap" ), Title( "Use reach (m)" ), Range( 1f, 8f )]
	public float UseReachMeters { get; set; } = 3f;

	[Property, Group( "Trap" ), Title( "Armed color" )]
	public Color ArmedColor { get; set; } = new( 0.35f, 0.36f, 0.4f );

	[Property, Group( "Trap" ), Title( "Sprung (holding) color" )]
	public Color HoldingColor { get; set; } = new( 0.75f, 0.2f, 0.16f );

	[Property, Group( "Trap" ), Title( "Disarmed color" )]
	public Color DisarmedColor { get; set; } = new( 0.55f, 0.5f, 0.42f );

	[Property, Group( "Debug" ), Title( "Log trap" )]
	public bool LogTrap { get; set; }

	/// <summary>Host → everyone: waiting for a foot.</summary>
	[Sync] public bool IsArmed { get; private set; }

	/// <summary>Host → everyone: jaws closed on a victim.</summary>
	[Sync] public bool IsHolding { get; private set; }

	public static IReadOnlyList<BearTrap> All => Active;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	/// <summary>"Arm Trap" / "Disarm Trap" / "Open Trap" for the HUD prompt.</summary>
	public string PromptText =>
		IsHolding ? "Open Trap" : IsArmed ? "Disarm Trap" : "Arm Trap";

	Collider _trigger;
	ModelRenderer _renderer;
	bool _visualArmed;
	bool _visualHolding;
	bool _visualApplied;

	// Host state
	double _nextPollAt;
	double _releaseAt;
	GameObject _victimRoot;
	PlayerMovement _victimPlayer;
	PlayerVitals _victimVitals;
	EntityLocomotion _victimEntity;
	readonly HashSet<Guid> _ignoreWhileTouching = new();
	readonly List<Guid> _scratchIds = new();
	/// <summary>First poll after arming only records who is already on the plate — a fresh trigger's Touching list is empty on the arm frame.</summary>
	bool _seedIgnoreOnNextPoll;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Active.Remove( this );
		if ( HasHostAuthority && IsHolding )
			HostRelease( "trap destroyed" );
		base.OnDestroy();
	}

	protected override void OnStart()
	{
		base.OnStart();
		_renderer = Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		_trigger = FindTrigger();

		if ( Components.Get<BuildPiece>() is { IsPreviewGhost: true } )
			return;

		if ( HasHostAuthority )
		{
			if ( _trigger is null )
				Log.Warning( $"[BearTrap] {GameObject.Name}: no trigger collider under this object — author a BoxCollider child with Is Trigger on the trap prefab." );

			// A freshly built trap sits open: the builder arms it with E once they have stepped off.
		}

		ApplyVisual( force: true );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		ApplyVisual( force: false );

		if ( !HasHostAuthority || Components.Get<BuildPiece>() is { IsPreviewGhost: true } )
			return;

		if ( IsHolding )
		{
			TickHold();
			return;
		}

		if ( !IsArmed || Time.NowDouble < _nextPollAt )
			return;

		_nextPollAt = Time.NowDouble + 1.0 / ArmedPollHz;
		PollForVictim();
	}

	// ------------------------------------------------------------------
	// Arm / disarm (host)
	// ------------------------------------------------------------------

	/// <summary>Host: arm or disarm. Ignored while holding — use <see cref="HostOpen"/> for that.</summary>
	public bool HostSetArmed( bool armed, GameObject by )
	{
		if ( !HasHostAuthority || IsHolding )
			return false;

		if ( IsArmed == armed )
			return false;

		IsArmed = armed;
		_ignoreWhileTouching.Clear();

		if ( armed )
		{
			// Whoever is standing on the plate right now (usually the one arming it) is not a catch
			// until they step off and back on. The plate is read on the first poll, not here — a
			// just-placed trigger has nothing in Touching yet.
			if ( by.IsValid() )
				_ignoreWhileTouching.Add( by.Id );

			_seedIgnoreOnNextPoll = true;
			_nextPollAt = Time.NowDouble + 1.0 / ArmedPollHz;
		}

		if ( LogTrap )
			Log.Info( $"[BearTrap] {GameObject.Name}: {(armed ? "armed" : "disarmed")}{(by.IsValid() ? $" by {by.Name}" : string.Empty)}" );

		return true;
	}

	/// <summary>Host: someone pried the jaws open — free the victim early, trap stays sprung.</summary>
	public bool HostOpen( GameObject by )
	{
		if ( !HasHostAuthority || !IsHolding )
			return false;

		// The victim cannot free themselves; that is what the hold is for.
		if ( by.IsValid() && _victimRoot.IsValid() && by == _victimRoot )
			return false;

		HostRelease( by.IsValid() ? $"opened by {by.Name}" : "opened" );
		return true;
	}

	/// <summary>Host: E on the trap — arm when idle, disarm when armed, open when holding.</summary>
	public void HostUse( GameObject user )
	{
		if ( !HasHostAuthority )
			return;

		if ( IsHolding )
		{
			HostOpen( user );
			return;
		}

		HostSetArmed( !IsArmed, user );
	}

	public bool IsWithinUseReach( GameObject user )
	{
		if ( user is null || !user.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseReachMeters ) ) + 48f;
		return (user.WorldPosition - GameObject.WorldPosition).LengthSquared <= reach * reach;
	}

	// ------------------------------------------------------------------
	// Catch / hold / release (host)
	// ------------------------------------------------------------------

	void PollForVictim()
	{
		_trigger ??= FindTrigger();
		if ( _trigger is null || !_trigger.IsValid() || !_trigger.Enabled )
			return;

		_scratchIds.Clear();
		GameObject catchRoot = null;
		var catchIsPlayer = false;
		TrapSize catchSize = TrapSize.None;

		foreach ( var other in _trigger.Touching )
		{
			if ( other is null || !other.GameObject.IsValid() )
				continue;

			if ( !TryResolveVictimRoot( other.GameObject, out var root, out var isPlayer, out var size ) )
				continue;

			_scratchIds.Add( root.Id );
			if ( catchRoot is not null || _ignoreWhileTouching.Contains( root.Id ) )
				continue;

			if ( !FitsBand( size ) )
				continue;

			catchRoot = root;
			catchIsPlayer = isPlayer;
			catchSize = size;
		}

		if ( _seedIgnoreOnNextPoll )
		{
			_seedIgnoreOnNextPoll = false;
			foreach ( var id in _scratchIds )
				_ignoreWhileTouching.Add( id );
			return;
		}

		// Anyone the trap was ignoring who has stepped off is fair game again.
		if ( _ignoreWhileTouching.Count > 0 )
			_ignoreWhileTouching.RemoveWhere( id => !_scratchIds.Contains( id ) );

		if ( catchRoot is not null )
			HostCatch( catchRoot, catchIsPlayer, catchSize );
	}

	bool FitsBand( TrapSize size ) =>
		size != TrapSize.None && size >= MinCatchSize && size <= MaxCatchSize;

	void HostCatch( GameObject root, bool isPlayer, TrapSize size )
	{
		var hold = isPlayer ? PlayerHoldSeconds : EntityHoldSeconds;
		hold = Math.Max( 0.1f, hold );

		// Heat Breaker augment: red-hot calves halve the hold.
		if ( isPlayer && root.Components.Get<PlayerAugments>() is { } augments && augments.IsAbilityOn( AugmentAbility.HeatBreaker ) )
			hold *= 0.5f;

		_victimRoot = root;
		_victimPlayer = null;
		_victimVitals = null;
		_victimEntity = null;

		if ( isPlayer )
		{
			_victimPlayer = root.Components.Get<PlayerMovement>();
			_victimVitals = root.Components.Get<PlayerVitals>();
			_victimPlayer?.HostSetTrapped( true, GameObject.WorldPosition );
			_victimVitals?.HostSetStatusEffectExpiry( TrappedStatusId, Time.NowDouble + hold, extendOnly: false );
		}
		else
		{
			_victimEntity = root.Components.Get<EntityLocomotion>();
			_victimEntity?.HostSetTrapped( true, GameObject.WorldPosition );
		}

		IsHolding = true;
		IsArmed = false;
		_releaseAt = Time.NowDouble + hold;
		_ignoreWhileTouching.Clear();

		if ( LogTrap )
			Log.Info( $"[BearTrap] {GameObject.Name}: caught {root.Name} ({size}) for {hold:0.#}s" );

		if ( CatchDamage > 0f )
			ApplyCatchDamage( root, isPlayer );
	}

	void TickHold()
	{
		if ( !_victimRoot.IsValid() )
		{
			HostRelease( "victim gone" );
			return;
		}

		if ( Time.NowDouble >= _releaseAt )
		{
			HostRelease( "hold elapsed" );
			return;
		}

		if ( _victimVitals is not null && _victimVitals.CurrentHealth <= 0.001f )
		{
			HostRelease( "victim died" );
			return;
		}

		if ( _victimEntity is not null )
		{
			var entityVitals = _victimRoot.Components.Get<EntityVitals>();
			if ( entityVitals is not null && entityVitals.IsDead )
			{
				HostRelease( "victim died" );
				return;
			}
		}

		var lost = TerrainWorldUnits.MetersToEngine( VictimLostDistanceMeters );
		if ( (_victimRoot.WorldPosition - GameObject.WorldPosition).WithZ( 0f ).LengthSquared > lost * lost )
			HostRelease( "victim left" );
	}

	void HostRelease( string reason )
	{
		if ( _victimPlayer is not null && _victimPlayer.IsValid() )
			_victimPlayer.HostSetTrapped( false );
		if ( _victimVitals is not null && _victimVitals.IsValid() )
			_victimVitals.HostRemoveStatusEffect( TrappedStatusId );
		if ( _victimEntity is not null && _victimEntity.IsValid() )
			_victimEntity.HostSetTrapped( false );

		if ( LogTrap && _victimRoot.IsValid() )
			Log.Info( $"[BearTrap] {GameObject.Name}: released {_victimRoot.Name} ({reason})" );

		_victimRoot = null;
		_victimPlayer = null;
		_victimVitals = null;
		_victimEntity = null;
		IsHolding = false;
		IsArmed = false;
	}

	void ApplyCatchDamage( GameObject root, bool isPlayer )
	{
		var amount = Math.Abs( CatchDamage );
		if ( isPlayer )
		{
			var vitals = root.Components.Get<PlayerVitals>();
			if ( vitals is null )
				return;

			var auth = VitalsAuthority.Instance;
			if ( auth is not null )
				auth.TryApplyDeltas( root, -amount, 0f, vitals, damageSource: this );
			else if ( !root.IsProxy )
				vitals.RequestVitalsDelta( -amount, 0f );
			return;
		}

		root.Components.Get<EntityVitals>()?.ApplyDamage( amount, this );
	}

	// ------------------------------------------------------------------
	// Victim resolution
	// ------------------------------------------------------------------

	/// <summary>Walk up from a touching collider to the pawn / entity root and read its trap size.</summary>
	static bool TryResolveVictimRoot( GameObject start, out GameObject root, out bool isPlayer, out TrapSize size )
	{
		root = null;
		isPlayer = false;
		size = TrapSize.None;

		for ( var go = start; go.IsValid(); go = go.Parent )
		{
			if ( go.Components.Get<EntityLocomotion>() is not null )
			{
				root = go;
				size = ResolveEntityTrapSize( go );
				return true;
			}

			if ( go.Components.Get<PlayerMovement>() is not null )
			{
				root = go;
				isPlayer = true;
				size = TrapSize.Small;
				return true;
			}
		}

		return false;
	}

	static TrapSize ResolveEntityTrapSize( GameObject root )
	{
		var animal = root.Components.Get<AnimalBrain>();
		if ( animal is not null )
			return animal.TrapSize;

		var vitals = root.Components.Get<EntityVitals>();
		if ( vitals is not null )
			return EntityArchetype.Get( vitals.EnemyType ).TrapSize;

		return TrapSize.None;
	}

	Collider FindTrigger()
	{
		foreach ( var collider in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( collider is not null && collider.IsTrigger )
				return collider;
		}

		return null;
	}

	// ------------------------------------------------------------------
	// Visual (every machine)
	// ------------------------------------------------------------------

	void ApplyVisual( bool force )
	{
		if ( !force && _visualApplied && _visualArmed == IsArmed && _visualHolding == IsHolding )
			return;

		_visualApplied = true;
		_visualArmed = IsArmed;
		_visualHolding = IsHolding;

		_renderer ??= Components.Get<ModelRenderer>( FindMode.EverythingInSelfAndDescendants );
		if ( _renderer is null || !_renderer.IsValid() )
			return;

		if ( Components.Get<BuildPiece>() is { IsPreviewGhost: true } )
			return;

		_renderer.Tint = IsHolding ? HoldingColor : IsArmed ? ArmedColor : DisarmedColor;
	}

	// ------------------------------------------------------------------
	// Focus (owner)
	// ------------------------------------------------------------------

	public static bool TryFindFocusedTrap( GameObject viewer, float reachMeters, out BearTrap trap )
	{
		trap = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var direction ) )
			return false;

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = scene.Trace.Ray( origin, origin + direction * reach )
			.IgnoreGameObjectHierarchy( viewer )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
		{
			var t = go.Components.Get<BearTrap>();
			if ( t is not null && t.Enabled && go.Components.Get<BuildPiece>() is not { IsPreviewGhost: true } )
			{
				trap = t;
				return true;
			}
		}

		return false;
	}
}
