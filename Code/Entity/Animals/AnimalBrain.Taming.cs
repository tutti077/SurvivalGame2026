using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Taming by hand (Mark 2026-11-02, type a): a calm animal that sees a crouching player holding its
/// favourite food (<c>tameFoodResourceId</c> in <c>animal_behaviors.json</c>, raw whitetail meat for
/// the fox) inside <c>tameOfferRangeMeters</c> walks up slowly, eats from the hand for
/// <c>tameEatSeconds</c>, hearts rise, and it is tamed: it never treats players as threats again, the
/// food leaves the player's hand, and the nameplate shows its name once given. Host owns every
/// decision; the synced <see cref="IsTamed"/> / <see cref="PetName"/> / <see cref="HeartSerial"/> drive
/// the nameplate and hearts on every peer. Pet = E, name = hold E
/// (<c>PlayerInventoryInteraction.Animal.cs</c>). Trap-based taming comes later on the same hooks.
/// </summary>
public sealed partial class AnimalBrain
{
	public const int PetNameMaxLength = 16;

	[Sync] public bool IsTamed { get; private set; }
	[Sync] public string PetName { get; private set; } = string.Empty;
	[Sync] public Guid OwnerPawnId { get; private set; }
	/// <summary>Host → everyone: bumps on taming and on every pet, each peer spawns hearts.</summary>
	[Sync] public int HeartSerial { get; private set; }

	GameObject _tameTarget;
	double _nextTameScanAt;
	const float TameScanInterval = 0.3f;

	// Presentation
	HeartsVfx _hearts;
	int _heartsSeen;
	bool _heartsInit;
	string _plateSeen;
	bool _plateTamedSeen;
	bool _plateInit;

	/// <summary>The species has a hand-feed food configured.</summary>
	public bool CanBeTamedByHand => !string.IsNullOrEmpty( _behavior.TameFood );

	/// <summary>Name over the head: the pet's name, else "Fox (tame)" / "Fox".</summary>
	public string DisplayNameForPlate
	{
		get
		{
			if ( !string.IsNullOrWhiteSpace( PetName ) )
				return PetName;
			var kind = AnimalSpeciesLabel( Species );
			return IsTamed ? kind + " (tame)" : kind;
		}
	}

	public static string AnimalSpeciesLabel( AnimalSpecies species ) => species switch
	{
		AnimalSpecies.Whitetail => "Whitetail Deer",
		AnimalSpecies.Jay => "Blue Jay",
		AnimalSpecies.BlackBear => "Black Bear",
		AnimalSpecies.SnowHare => "Snow Hare",
		AnimalSpecies.WildBoar => "Wild Boar",
		_ => species.ToString()
	};

	// ------------------------------------------------------------------
	// Host: offer detection, the walk-up and the meal
	// ------------------------------------------------------------------

	/// <summary>Calm, untamed and hand-feedable: look for a crouching player holding the food within range.</summary>
	void TickTamingOffer()
	{
		if ( IsTamed || !CanBeTamedByHand )
			return;

		// Calm animals look round for an offer; a harasser that is already sizing the player up (alerted /
		// stalking) takes the meal instead of the bite. Attacking and fleeing never notice food.
		if ( _state is not (AnimalAiState.Idle or AnimalAiState.Wander or AnimalAiState.Graze or AnimalAiState.Alerted or AnimalAiState.Tracking) )
			return;

		if ( Time.NowDouble < _nextTameScanAt )
			return;

		_nextTameScanAt = Time.NowDouble + TameScanInterval;
		foreach ( var pv in Scene.GetAllComponents<PlayerVitals>() )
		{
			var go = pv.GameObject;
			if ( !IsOfferingFood( go ) )
				continue;

			if ( Vector3.DistanceBetween( GameObject.WorldPosition, go.WorldPosition ) > _behavior.TameOfferRange )
				continue;

			_tameTarget = go;
			EnterState( AnimalAiState.Approaching );
			return;
		}
	}

	/// <summary>Crouched, alive, and the active hotbar slot holds the species' food.</summary>
	bool IsOfferingFood( GameObject pawn )
	{
		if ( pawn is null || !pawn.IsValid() )
			return false;

		var vitals = pawn.Components.Get<PlayerVitals>();
		if ( vitals is null || vitals.CurrentHealth <= 0.001f )
			return false;

		var movement = pawn.Components.Get<PlayerMovement>();
		if ( movement is null || !movement.IsCrouchHeldForTaming() )
			return false;

		var hotbar = pawn.Components.Get<PlayerHotbar>();
		if ( hotbar is null )
			return false;

		var slot = hotbar.GetSlot( hotbar.ActiveSlotIndex );
		if ( slot.IsEmpty )
			return false;

		return string.Equals( ResourceCatalog.NormalizeResourceId( slot.ResourceId ), _behavior.TameFood, StringComparison.OrdinalIgnoreCase );
	}

	void TickApproaching()
	{
		if ( !IsOfferingFood( _tameTarget )
		     || Vector3.DistanceBetween( GameObject.WorldPosition, _tameTarget.WorldPosition ) > _behavior.TameOfferRange * 1.4f )
		{
			// Stood up, put the food away or backed off: the offer is withdrawn.
			_tameTarget = null;
			EnterState( AnimalAiState.Wander );
			return;
		}

		var dist = Vector3.DistanceBetween( GameObject.WorldPosition, _tameTarget.WorldPosition );
		if ( dist <= _behavior.TameEatRange )
		{
			EnterState( AnimalAiState.Eating );
			return;
		}

		Locomotion?.SetTravelHint( _tameTarget.WorldPosition );
		// A careful walk, not a trot - the slow approach is the tell that it is coming to eat.
		MoveTowardTarget( _tameTarget.WorldPosition, EntityLocomotion.GetNavChasePoint( Scene, _tameTarget.WorldPosition ),
			_behavior.WalkSpeed * 0.6f, 0.5f, _behavior.TameEatRange * 0.8f );
	}

	void TickEating()
	{
		if ( !IsOfferingFood( _tameTarget ) )
		{
			_tameTarget = null;
			EnterState( AnimalAiState.Wander );
			return;
		}

		Agent?.Stop();
		Locomotion?.SmoothFaceTowardWorld( _tameTarget.WorldPosition, TurnDegreesPerSecond );
		if ( Time.NowDouble < _stateEndsAt )
			return;

		HostFinishTaming();
	}

	void HostFinishTaming()
	{
		var hotbar = _tameTarget?.Components.Get<PlayerHotbar>();
		if ( hotbar is not null )
			hotbar.HostTryTakeOne( hotbar.ActiveSlotIndex );

		IsTamed = true;
		OwnerPawnId = _tameTarget?.Id ?? Guid.Empty;
		HeartSerial++;
		_threat = null;
		if ( Vitals is not null )
			Vitals.DisplayNameOverride = DisplayNameForPlate;

		if ( LogStateDebug )
			Log.Info( $"[AnimalBrain] {GameObject.Name} tamed by {_tameTarget?.Name}" );

		_tameTarget = null;
		EnterState( AnimalAiState.Graze );
	}

	/// <summary>Host: E on a tamed animal - hearts.</summary>
	public void HostPet( GameObject by )
	{
		if ( !CanRunHostLogic() || !IsTamed || IsDead() )
			return;

		HeartSerial++;
	}

	/// <summary>Host: hold-E naming. Trimmed, printable, at most <see cref="PetNameMaxLength"/> characters.</summary>
	public void HostSetName( GameObject by, string name )
	{
		if ( !CanRunHostLogic() || !IsTamed )
			return;

		PetName = SanitizePetName( name );
		if ( Vitals is not null )
			Vitals.DisplayNameOverride = DisplayNameForPlate;
	}

	public static string SanitizePetName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return string.Empty;

		var sb = new System.Text.StringBuilder( PetNameMaxLength );
		foreach ( var c in name.Trim() )
		{
			if ( char.IsControl( c ) )
				continue;
			sb.Append( c );
			if ( sb.Length >= PetNameMaxLength )
				break;
		}

		return sb.ToString().Trim();
	}

	public bool IsWithinUseReach( GameObject pawn, float reachMeters ) =>
		pawn.IsValid() && Vector3.DistanceBetween( GameObject.WorldPosition, pawn.WorldPosition ) <= TerrainWorldUnits.MetersToEngine( reachMeters ) + 48f;

	/// <summary>The animal under <paramref name="viewer"/>'s crosshair within reach (any animal; the caller decides what it can do with it).</summary>
	public static bool TryFindFocused( GameObject viewer, float reachMeters, out AnimalBrain animal )
	{
		animal = null;
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
			var brain = go.Components.Get<AnimalBrain>();
			if ( brain is not null && brain.IsValid() && !brain.IsDead() )
			{
				animal = brain;
				return true;
			}
		}

		return false;
	}

	// ------------------------------------------------------------------
	// Every peer: hearts and the nameplate
	// ------------------------------------------------------------------

	void TickTamingPresentation()
	{
		if ( !_heartsInit )
		{
			_heartsInit = true;
			_heartsSeen = HeartSerial;
		}
		else if ( HeartSerial != _heartsSeen )
		{
			_heartsSeen = HeartSerial;
			SpawnHearts();
		}

		if ( !_plateInit || !string.Equals( _plateSeen, PetName, StringComparison.Ordinal ) || _plateTamedSeen != IsTamed )
		{
			_plateInit = true;
			_plateSeen = PetName;
			_plateTamedSeen = IsTamed;
			Vitals ??= Components.Get<EntityVitals>();
			if ( Vitals is not null )
				Vitals.DisplayNameOverride = DisplayNameForPlate;
			Components.Get<EnemyHealthBar>()?.RefreshLabel();
		}

		if ( _hearts is not null )
		{
			var cam = Scene.IsValid() ? Scene.Camera : null;
			_hearts.Tick( Time.Now, cam is { IsValid: true } ? cam.WorldRotation : Rotation.Identity );
		}
	}

	void SpawnHearts()
	{
		if ( _hearts is null )
		{
			var world = Scene.IsValid() ? Scene.SceneWorld : null;
			if ( world is null )
				return;
			_hearts = new HeartsVfx( world );
		}

		var top = GameObject.WorldPosition + Vector3.Up * (_behavior.EyeHeight + 10f);
		_hearts.Spawn( top, 4, Time.Now );
	}

	void DestroyTamingPresentation()
	{
		_hearts?.Delete();
		_hearts = null;
	}
}
