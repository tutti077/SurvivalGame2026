using System;
using Sandbox;
using Sandbox.UI;

namespace Survival;

/// <summary>
/// Look at a tamed animal (<see cref="AnimalBrain.IsTamed"/>): tap E to pet it (hearts), hold E to open the
/// naming box. Owner traces and sends intent; the host re-checks reach once and commits through
/// <see cref="AnimalBrain.HostPet"/> / <see cref="AnimalBrain.HostSetName"/>. Taming itself needs no key:
/// crouch with the food in hand and wait (<c>AnimalBrain.Taming.cs</c>).
/// </summary>
public sealed partial class PlayerInventoryInteraction
{
	[Property, Group( "Animal" ), Title( "Use Action" )]
	public string AnimalUseAction { get; set; } = "Use";

	[Property, Group( "Animal" ), Title( "Reach (m)" ), Range( 1f, 6f ), Step( 0.5f )]
	public float AnimalUseReachMeters { get; set; } = 3f;

	[Property, Group( "Animal" ), Title( "Hold E to name (s)" ), Range( 0.2f, 2f ), Step( 0.05f )]
	public float AnimalNameHoldSeconds { get; set; } = 0.6f;

	[Property, Group( "Animal" ), Title( "Focus Scan Interval (seconds)" )]
	public float AnimalFocusScanIntervalSeconds { get; set; } = 0.15f;

	/// <summary>Tamed animal under the crosshair (drives the "E — Pet Fox" prompt).</summary>
	public AnimalBrain FocusedAnimal { get; private set; }

	public event Action FocusedAnimalChanged;

	public bool IsPetNameEntryOpen => _petNameOpen;

	double _nextAnimalFocusScanAt;
	double _animalUseDownAt;
	bool _animalUseHeld;
	bool _animalNameOpenedThisHold;

	Panel _petNamePanel;
	Label _petNameTitle;
	TextEntry _petNameEntry;
	bool _petNameOpen;
	AnimalBrain _petNameTarget;

	void TickAnimalAccess()
	{
		var menuOpen = _menu is not null && _menu.IsMenuOpen;

		if ( _petNameOpen )
		{
			TickPetNameEntry();
			return;
		}

		TickAnimalFocusPrompt( menuOpen );
		if ( menuOpen || FocusedAnimal is null || !FocusedAnimal.IsValid() )
		{
			_animalUseHeld = false;
			return;
		}

		if ( Input.Pressed( AnimalUseAction ) )
		{
			_animalUseDownAt = Time.NowDouble;
			_animalUseHeld = true;
			_animalNameOpenedThisHold = false;
		}

		if ( _animalUseHeld && Input.Down( AnimalUseAction ) && !_animalNameOpenedThisHold
		     && Time.NowDouble - _animalUseDownAt >= Math.Max( 0.2f, AnimalNameHoldSeconds ) )
		{
			_animalNameOpenedThisHold = true;
			_animalUseHeld = false;
			OpenPetNameEntry( FocusedAnimal );
			return;
		}

		if ( _animalUseHeld && Input.Released( AnimalUseAction ) )
		{
			_animalUseHeld = false;
			if ( !_animalNameOpenedThisHold )
				OwnerPetAnimal( FocusedAnimal );
		}
	}

	void TickAnimalFocusPrompt( bool menuOpen )
	{
		if ( FocusedAnimal is not null && !FocusedAnimal.IsValid() )
			SetFocusedAnimal( null );

		if ( menuOpen || IsBuildHammerPreviewing() )
		{
			SetFocusedAnimal( null );
			return;
		}

		if ( Time.NowDouble < _nextAnimalFocusScanAt )
			return;

		_nextAnimalFocusScanAt = Time.NowDouble + Math.Max( 0.05, AnimalFocusScanIntervalSeconds );

		// Anything else under the reticule keeps its own prompt.
		if ( FocusedContainer is not null || FocusedAugmentStation is not null || FocusedWorkbench is not null
		     || FocusedCampfire is not null || FocusedDoor is not null || FocusedTrap is not null || FocusedLever is not null )
		{
			SetFocusedAnimal( null );
			return;
		}

		if ( AnimalBrain.TryFindFocused( GameObject, AnimalUseReachMeters, out var animal ) && animal.IsTamed )
			SetFocusedAnimal( animal );
		else
			SetFocusedAnimal( null );
	}

	void SetFocusedAnimal( AnimalBrain animal )
	{
		if ( ReferenceEquals( FocusedAnimal, animal ) )
			return;

		FocusedAnimal = animal;
		FocusedAnimalChanged?.Invoke();
	}

	// ------------------------------------------------------------------
	// Pet
	// ------------------------------------------------------------------

	void OwnerPetAnimal( AnimalBrain animal )
	{
		if ( animal is null || !animal.IsValid() )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			animal.HostPet( GameObject );
			return;
		}

		RpcHostPetAnimal( animal.GameObject.Id );
	}

	[Rpc.Host]
	void RpcHostPetAnimal( Guid animalRootId )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var animal = Scene.Directory.FindByGuid( animalRootId )?.Components.Get<AnimalBrain>();
		if ( animal is null || !animal.IsValid() || !animal.IsWithinUseReach( GameObject, AnimalUseReachMeters ) )
			return;

		animal.HostPet( GameObject );
	}

	// ------------------------------------------------------------------
	// Name
	// ------------------------------------------------------------------

	void OpenPetNameEntry( AnimalBrain animal )
	{
		if ( animal is null || !animal.IsValid() || _dragLayerRoot is null || !_dragLayerRoot.IsValid() )
			return;

		EnsurePetNamePanel();
		if ( _petNamePanel is null )
			return;

		_petNameTarget = animal;
		_petNameTitle.Text = $"Name your {AnimalBrain.AnimalSpeciesLabel( animal.Species )}";
		_petNameEntry.Text = animal.PetName ?? string.Empty;
		_petNamePanel.Style.Set( "display", "flex" );
		_petNameOpen = true;
		_petNameEntry.Focus();
	}

	void TickPetNameEntry()
	{
		if ( _petNameTarget is null || !_petNameTarget.IsValid() || Input.EscapePressed )
		{
			ClosePetNameEntry();
			return;
		}

		// The entry owns the keyboard while it is up; E must not pet / re-open behind it.
		if ( _petNameEntry is { IsValid: true } && !_petNameEntry.HasFocus )
			_petNameEntry.Focus();
	}

	void CommitPetName()
	{
		if ( !_petNameOpen )
			return;

		var name = AnimalBrain.SanitizePetName( _petNameEntry?.Text );
		var target = _petNameTarget;
		ClosePetNameEntry();
		if ( target is null || !target.IsValid() || name.Length == 0 )
			return;

		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			target.HostSetName( GameObject, name );
			return;
		}

		RpcHostNameAnimal( target.GameObject.Id, name );
	}

	void ClosePetNameEntry()
	{
		_petNameOpen = false;
		_petNameTarget = null;
		if ( _petNameEntry is { IsValid: true } )
			_petNameEntry.Blur();
		_petNamePanel?.Style.Set( "display", "none" );
	}

	[Rpc.Host]
	void RpcHostNameAnimal( Guid animalRootId, string name )
	{
		if ( !Networking.IsHost )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && caller.Id != owner.Id )
			return;

		var animal = Scene.Directory.FindByGuid( animalRootId )?.Components.Get<AnimalBrain>();
		if ( animal is null || !animal.IsValid() || !animal.IsWithinUseReach( GameObject, AnimalUseReachMeters * 2f ) )
			return;

		animal.HostSetName( GameObject, name );
	}

	void EnsurePetNamePanel()
	{
		if ( _petNamePanel is { IsValid: true } )
			return;

		var scale = InventoryMenuSection.Scale;
		_petNamePanel = new Panel { Parent = _dragLayerRoot };
		_petNamePanel.Style.Set( "position", "absolute" );
		_petNamePanel.Style.Set( "left", "50%" );
		_petNamePanel.Style.Set( "top", "38%" );
		_petNamePanel.Style.Set( "transform", "translateX(-50%)" );
		_petNamePanel.Style.Set( "flex-direction", "column" );
		_petNamePanel.Style.Set( "align-items", "center" );
		_petNamePanel.Style.Set( "gap", $"{8f * scale}px" );
		_petNamePanel.Style.Set( "padding", $"{14f * scale}px" );
		_petNamePanel.Style.BackgroundColor = new Color( 0.08f, 0.09f, 0.12f, 0.95f );
		_petNamePanel.Style.Set( "border-width", "1px" );
		_petNamePanel.Style.Set( "border-color", "#ffd23a" );
		_petNamePanel.Style.Set( "border-radius", "6px" );
		_petNamePanel.Style.Set( "z-index", "6000" );
		_petNamePanel.Style.Set( "pointer-events", "auto" );
		_petNamePanel.Style.Set( "display", "none" );

		_petNameTitle = new Label { Parent = _petNamePanel, Text = "Name your animal" };
		_petNameTitle.Style.FontColor = Color.White;
		_petNameTitle.Style.FontSize = Length.Pixels( 20f );

		_petNameEntry = new TextEntry { Parent = _petNamePanel };
		_petNameEntry.Style.Width = Length.Pixels( 260f );
		_petNameEntry.Style.Height = Length.Pixels( 34f );
		_petNameEntry.Style.PaddingLeft = Length.Pixels( 8f );
		_petNameEntry.Style.PaddingRight = Length.Pixels( 8f );
		_petNameEntry.Style.BackgroundColor = new Color( 0.14f, 0.15f, 0.18f, 1f );
		_petNameEntry.Style.Set( "border-radius", "4px" );
		_petNameEntry.Style.Set( "border-width", "1px" );
		_petNameEntry.Style.Set( "border-color", "#8a8f99" );
		_petNameEntry.Style.FontColor = Color.White;
		_petNameEntry.Style.FontSize = Length.Pixels( 17f );
		_petNameEntry.Placeholder = "Name";
		_petNameEntry.MaxLength = AnimalBrain.PetNameMaxLength;
		_petNameEntry.AddEventListener( "onsubmit", () => CommitPetName() );

		var hint = new Label { Parent = _petNamePanel, Text = "Enter to confirm, Esc to cancel" };
		hint.Style.FontColor = new Color( 0.75f, 0.75f, 0.78f );
		hint.Style.FontSize = Length.Pixels( 14f );
	}
}
