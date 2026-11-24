using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// <c>inventory_test</c>: automated regression suite for item authority. Run it in play mode in any scene.
/// Two real <c>basicplayer</c> pawns (input, camera and HUD off) and a scratch chest are driven through the
/// <c>HostCursor*</c> methods - the bodies of the RPCs a remote client's drag-and-drop arrives on - far above
/// the scene. The rule under test: an item only reaches a slot if the host saw it leave another one
/// (<c>PlayerInventory.HostCursor.cs</c>), so nothing a client sends can create, duplicate or overwrite items.
/// Stack sizes come from the live catalog, so retuning never fails it; broken behaviour does.
/// Console: one <c>[InventoryTest] PASS|FAIL</c> line per case, then <c>[InventoryTest] RESULT ...</c>.
/// </summary>
public sealed class InventoryTest : Component
{
	const string Tag = "[InventoryTest]";
	const string PawnPrefab = "prefabs/player/basicplayer.prefab";

	/// <summary>Two plain stackable resources.</summary>
	const string StackA = "resource_stone";
	const string StackB = "resource_dirt";

	/// <summary>A real hotbar weapon (the one the combat suite fights with).</summary>
	const string WeaponId = "club_wood";

	/// <summary>Far above any scene content.</summary>
	static readonly Vector3 TestOrigin = new( 4000f, 0f, 30000f );

	readonly List<string> _failures = new();
	int _passed;
	GameObject _pawnGo;
	GameObject _otherGo;
	GameObject _chestGo;

	PlayerInventory _bag;
	PlayerHotbar _hotbar;
	PlayerEquipment _equipment;
	PlayerAugments _augments;
	PlayerInventory _otherBag;
	ContainerInventory _chest;

	static InventoryTest _running;

	[ConCmd( "inventory_test" )]
	public static void ConCmdInventoryTest()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( !scene.IsValid() || !Sandbox.Game.IsPlaying )
		{
			Log.Warning( $"{Tag} enter play mode first (any scene)." );
			return;
		}

		if ( _running.IsValid() )
		{
			Log.Warning( $"{Tag} already running." );
			return;
		}

		var go = scene.CreateObject();
		go.Name = "InventoryTest";
		go.NetworkMode = NetworkMode.Never;
		_running = go.Components.Create<InventoryTest>();
	}

	protected override void OnStart() => _ = RunAsync();

	protected override void OnDestroy()
	{
		_pawnGo?.Destroy();
		_otherGo?.Destroy();
		_chestGo?.Destroy();
	}

	async System.Threading.Tasks.Task RunAsync()
	{
		Log.Info( $"{Tag} starting" );
		try
		{
			if ( !Spawn() )
				Fail( "spawn", $"could not clone {PawnPrefab} with bag, hotbar, equipment and augments" );
			else
			{
				// Components wire up in OnStart, which can land a few frames after the clone.
				for ( var i = 0; i < 5; i++ )
					await Task.Frame();

				ClearPawn();
				RunCases();
			}
		}
		catch ( Exception e )
		{
			Fail( "exception", e.ToString() );
		}

		var total = _passed + _failures.Count;
		var verdict = _failures.Count == 0 ? "ALL PASS" : $"{_failures.Count} FAILED: {string.Join( ", ", _failures )}";
		Log.Info( $"{Tag} RESULT {_passed}/{total} passed - {verdict}" );
		GameObject.Destroy();
	}

	// ── Setup ───────────────────────────────────────────────────────────────────────────────────────────

	bool Spawn()
	{
		_pawnGo = SpawnPawn( "InventoryTest_Pawn", TestOrigin );
		_otherGo = SpawnPawn( "InventoryTest_Other", TestOrigin + new Vector3( 200f, 0f, 0f ) );
		if ( _pawnGo is null || _otherGo is null )
			return false;

		_bag = _pawnGo.Components.Get<PlayerInventory>();
		_hotbar = _pawnGo.Components.Get<PlayerHotbar>();
		_equipment = _pawnGo.Components.Get<PlayerEquipment>();
		_augments = _pawnGo.Components.Get<PlayerAugments>();
		_otherBag = _otherGo.Components.Get<PlayerInventory>();

		_chestGo = Scene.CreateObject();
		_chestGo.Name = "InventoryTest_Chest";
		_chestGo.NetworkMode = NetworkMode.Never;
		_chestGo.WorldPosition = TestOrigin + new Vector3( 0f, 200f, 0f );
		_chest = _chestGo.Components.Create<ContainerInventory>();

		return _bag is not null && _hotbar is not null && _equipment is not null && _augments is not null && _otherBag is not null;
	}

	static GameObject SpawnPawn( string name, Vector3 position )
	{
		var template = BuildPrefabUtility.GetTemplate( PawnPrefab );
		if ( template is null )
			return null;

		var go = template.Clone( new Transform( position ), name: name );
		go.NetworkMode = NetworkMode.Never;

		// Only the item components matter: everything that serves a local player is switched off.
		if ( go.Components.Get<PlayerController>() is { } controller )
		{
			controller.UseInputControls = false;
			controller.UseCameraControls = false;
			controller.UseLookControls = false;
			controller.Enabled = false;
		}

		if ( go.Components.Get<Rigidbody>() is { } body )
			body.MotionEnabled = false;

		// A scripted pawn must not load or overwrite the player's saved hotbar bindings.
		if ( go.Components.Get<PlayerHotbar>() is { } hotbar )
			hotbar.UsesSavedBindings = false;

		DisableAll<ScreenPanel>( go );
		DisableAll<PlayerScreenHud>( go );
		DisableAll<PlayerCrosshair>( go );
		DisableAll<PlayerGameMenuController>( go );
		DisableAll<PlayerHotbarController>( go );
		DisableAll<PlayerInventoryInteraction>( go );
		DisableAll<PlayerSpawnSeparation>( go );
		DisableAll<PlayerMovement>( go );
		return go;
	}

	static void DisableAll<T>( GameObject go ) where T : Component
	{
		foreach ( var c in go.Components.GetAll<T>( FindMode.EverythingInSelfAndDescendants ) )
			c.Enabled = false;
	}

	/// <summary>Whatever the prefab or a spawn kit put on the pawn is thrown away so every slot index below is known.</summary>
	void ClearPawn()
	{
		for ( var i = 0; i < _bag.SlotCount; i++ )
			_bag.HostTryPickupAll( i, out _ );
		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
			_hotbar.HostTryPickupAll( i, out _ );
		_bag.HostCursor.Clear();
		_otherBag.HostCursor.Clear();
	}

	// ── Cases ───────────────────────────────────────────────────────────────────────────────────────────

	void RunCases()
	{
		var maxA = ResourceCatalog.GetMaxStack( StackA );
		var maxB = ResourceCatalog.GetMaxStack( StackB );
		if ( maxA < 20 || maxB < 6 )
		{
			Fail( "catalog", $"{StackA} / {StackB} no longer stack to 20 / 6 (max {maxA} / {maxB}): pick other test resources" );
			return;
		}

		ref var cursor = ref _bag.HostCursor;

		// 1. Nothing on the host cursor: no grid accepts a place, whatever the client asks for.
		_bag.HostCursorPlaceHeld( 5 );
		_bag.HostCursorFinishDragDrop( 0, 5 );
		_bag.HostCursorDropOne( 5 );
		_bag.HostCursorPlaceHalf( 5 );
		_hotbar.HostCursorFinishDragDrop( -1, 0 );
		_hotbar.HostCursorDropOne( 0 );
		_chest.HostCursorPlaceHeld( _bag, 0 );
		_chest.HostCursorDropOne( _bag, 0 );
		_equipment.HostCursorPlaceIntoSlot( EquipmentSlot.Head );
		_augments.HostCursorPlaceHeldBank( 0 );
		_augments.HostCursorPlaceIntoInstalled( AugmentSlot.HeadEye );
		Check( "an empty cursor places nothing in any grid",
			_bag.GetSlot( 5 ).IsEmpty && _hotbar.GetSlot( 0 ).IsEmpty && _chest.GetSlot( 0 ).IsEmpty
			&& _equipment.GetSlot( EquipmentSlot.Head ).IsEmpty && _augments.GetBankSlot( 0 ).IsEmpty
			&& _augments.GetInstalled( AugmentSlot.HeadEye ).IsEmpty && cursor.IsEmpty,
			"a slot filled or the cursor gained a stack with nothing taken first" );

		// Seed 20 of A in bag slot 0 (the one legit source: the host's own resource add).
		_bag.HostTryAddResource( StackA, 20 );
		var seeded = FindBagSlot( StackA );
		Check( "host add lands 20 in the bag", seeded >= 0 && Total( StackA ) == 20, $"bag holds {Total( StackA )} {StackA}" );
		if ( seeded < 0 )
			return;

		// 2. Pickup: the slot empties, the host cursor holds the stack.
		_bag.HostCursorPickupAll( seeded );
		Check( "pickup moves the stack onto the host cursor",
			_bag.GetSlot( seeded ).IsEmpty && cursor.CanStack( StackA ) && cursor.Count == 20,
			$"slot {Describe( _bag.GetSlot( seeded ) )}, cursor {Describe( cursor )}" );

		// 3. Place: the cursor's stack lands, the cursor empties.
		_bag.HostCursorPlaceHeld( 7 );
		Check( "place lands the cursor's stack",
			_bag.GetSlot( 7 ).Count == 20 && cursor.IsEmpty,
			$"slot 7 {Describe( _bag.GetSlot( 7 ) )}, cursor {Describe( cursor )}" );

		// 4. Splits: half off, half of that down, one down, one back up. Never more or fewer than 20.
		_bag.HostCursorTakeHalf( 7 );
		var afterTakeHalf = cursor.Count == 10 && _bag.GetSlot( 7 ).Count == 10;
		_bag.HostCursorPlaceHalf( 8 );
		var afterPlaceHalf = cursor.Count == 5 && _bag.GetSlot( 8 ).Count == 5;
		_bag.HostCursorDropOne( 9 );
		var afterDropOne = cursor.Count == 4 && _bag.GetSlot( 9 ).Count == 1;
		_bag.HostCursorTakeOne( 8 );
		var afterTakeOne = cursor.Count == 5 && _bag.GetSlot( 8 ).Count == 4;
		Check( "take half / place half / drop one / take one move exact counts",
			afterTakeHalf && afterPlaceHalf && afterDropOne && afterTakeOne && Total( StackA ) == 20,
			$"steps {afterTakeHalf}/{afterPlaceHalf}/{afterDropOne}/{afterTakeOne}, total {Total( StackA )}" );

		// 5. Cross-grid: the same cursor lands on the hotbar.
		_hotbar.HostCursorFinishDragDrop( -1, 2 );
		Check( "the cursor crosses from the bag to the hotbar",
			_hotbar.GetSlot( 2 ).Count == 5 && cursor.IsEmpty && Total( StackA ) == 20,
			$"hotbar 2 {Describe( _hotbar.GetSlot( 2 ) )}, cursor {Describe( cursor )}, total {Total( StackA )}" );

		// 6. Chest: a take credits the taker's cursor only.
		_chest.HostDepositStack( StackB, 6 );
		var chestSlot = FindChestSlot( StackB );
		_chest.HostCursorPickupAll( _bag, chestSlot );
		Check( "a chest take credits only the taker's cursor",
			chestSlot >= 0 && _chest.GetSlot( chestSlot ).IsEmpty && cursor.CanStack( StackB ) && cursor.Count == 6 && _otherBag.HostCursor.IsEmpty,
			$"chest {Describe( _chest.GetSlot( Math.Max( 0, chestSlot ) ) )}, taker {Describe( cursor )}, other {Describe( _otherBag.HostCursor )}" );

		// 7. Chest: a place spends the placer's cursor; another player with an empty cursor adds nothing.
		_chest.HostCursorPlaceHeld( _otherBag, 4 );
		var otherAddedNothing = _chest.GetSlot( 4 ).IsEmpty;
		_chest.HostCursorPlaceHeld( _bag, 3 );
		Check( "a chest place spends the placer's cursor",
			otherAddedNothing && _chest.GetSlot( 3 ).Count == 6 && cursor.IsEmpty && Total( StackB ) == 6,
			$"other added {!otherAddedNothing}, chest 3 {Describe( _chest.GetSlot( 3 ) )}, cursor {Describe( cursor )}" );

		// 8. A stack the client no longer shows goes back to the bag before a different take lands.
		_bag.HostCursorPickupAll( 7 );
		_chest.HostCursorPickupAll( _bag, 3 );
		Check( "a stale cursor returns to the bag before a different take",
			cursor.CanStack( StackB ) && cursor.Count == 6 && _bag.CountResource( StackA ) == 20,
			$"cursor {Describe( cursor )}, {StackA} in bag + hotbar {_bag.CountResource( StackA )}" );

		// 9. Settle (menu closed): the cursor goes back to the bag.
		_bag.HostSettleCursor();
		Check( "settle returns the cursor to the bag",
			cursor.IsEmpty && _bag.CountResource( StackB ) == 6 && Total( StackA ) == 20,
			$"cursor {Describe( cursor )}, {StackB} {_bag.CountResource( StackB )}, {StackA} {Total( StackA )}" );

		// 10. A drag swap puts the displaced stack on the source slot: refused when that slot is not empty.
		var slotA = FindBagSlot( StackA );
		var slotB = FindBagSlot( StackB );
		var beforeA = _bag.GetSlot( slotA ).Count;
		_bag.HostCursorPickupAll( slotA );
		_bag.HostCursorDropOne( slotA );
		_bag.HostCursorFinishDragDrop( slotA, slotB );
		var swapRefused = _bag.GetSlot( slotB ).Count == 6 && _bag.GetSlot( slotA ).Count == 1 && cursor.Count == beforeA - 1;
		_bag.HostSettleCursor();
		Check( "a drag swap never overwrites an occupied source slot",
			swapRefused && Total( StackA ) == 20 && Total( StackB ) == 6,
			$"refused {swapRefused}, {StackA} {Total( StackA )}, {StackB} {Total( StackB )}" );

		RunPaperdollCases();
		RunHandCases();
		RunAugmentCases();
		RunStorageToBoundHotbarCase();

		Check( "totals unchanged after every case",
			Total( StackA ) == 20 && Total( StackB ) == 6 && cursor.IsEmpty && _otherBag.HostCursor.IsEmpty,
			$"{StackA} {Total( StackA )}, {StackB} {Total( StackB )}, cursor {Describe( cursor )}" );
	}

	/// <summary>
	/// Mark 2026-11-17: a stack shift-clicked out of a chest goes to the hotbar slot bound to it, not the bag's first slot.
	/// The shift-click (PlayerInventoryInteraction) takes the chest stack and places it on
	/// <see cref="PlayerHotbar.SlotsTakingFromStorage"/> first; this drives the same host steps.
	/// </summary>
	void RunStorageToBoundHotbarCase()
	{
		const int boundSlot = 6;
		ref var cursor = ref _bag.HostCursor;
		var bagSlot = FindBagSlot( StackB );
		if ( bagSlot < 0 )
		{
			Fail( "storage to bound hotbar", $"no {StackB} in the bag to stash" );
			return;
		}

		// Stash the bag's B in the chest, then bind hotbar slot 6 to B.
		_bag.HostCursorPickupAll( bagSlot );
		_chest.HostCursorPlaceHeld( _bag, 5 );
		_hotbar.SetBinding( boundSlot, StackB );
		var targets = _hotbar.SlotsTakingFromStorage( StackB );

		var chestSlot = FindChestSlot( StackB );
		_chest.HostCursorPickupAll( _bag, chestSlot );
		if ( targets is not null )
		{
			foreach ( var slot in targets )
			{
				if ( !cursor.IsEmpty )
					_hotbar.HostCursorFinishDragDrop( -1, slot );
			}
		}

		var landed = _hotbar.GetSlot( boundSlot );
		Check( "a chest stack goes to the hotbar slot bound to it",
			targets is { Count: 1 } && targets[0] == boundSlot && landed.Count == 6 && ResourceCatalog.ResourceIdsMatch( landed.ResourceId, StackB )
			&& cursor.IsEmpty && Total( StackB ) == 6,
			$"targets [{(targets is null ? "" : string.Join( ",", targets ))}], hotbar {boundSlot} {Describe( landed )}, cursor {Describe( cursor )}" );

		// Back to the bag, binding cleared, so the totals check below sees the pawn as before.
		_hotbar.HostCursorPickupAll( boundSlot );
		_bag.HostSettleCursor();
		_hotbar.ClearBinding( boundSlot );
	}

	void RunPaperdollCases()
	{
		if ( !TryFindArmor( out var armorId, out var armorSlot, out var wrongSlot ) )
		{
			Fail( "paperdoll", "the equipment catalog has no armor piece to test with" );
			return;
		}

		ref var cursor = ref _bag.HostCursor;
		_bag.HostTryAddResource( armorId, 1 );
		_bag.HostCursorPickupAll( FindBagSlot( armorId ) );

		_equipment.HostCursorPlaceIntoSlot( wrongSlot );
		var wrongRefused = _equipment.GetSlot( wrongSlot ).IsEmpty && cursor.CanStack( armorId ) && cursor.Count == 1;

		_equipment.HostCursorPlaceIntoSlot( armorSlot );
		var worn = ResourceCatalog.ResourceIdsMatch( _equipment.GetSlotResourceId( armorSlot ), armorId ) && cursor.IsEmpty;
		Check( "armor is worn from the cursor, only in a slot it fits",
			wrongRefused && worn,
			$"wrong slot refused {wrongRefused}, worn {worn}, cursor {Describe( cursor )}" );

		_equipment.HostCursorPickupFromSlot( armorSlot );
		var offAgain = _equipment.GetSlot( armorSlot ).IsEmpty && cursor.CanStack( armorId ) && cursor.Count == 1;
		_equipment.HostCursorPickupFromSlot( armorSlot );
		var noSecondCopy = cursor.Count == 1;
		_bag.HostSettleCursor();
		Check( "taking armor off returns exactly the worn piece",
			offAgain && noSecondCopy && _bag.CountResource( armorId ) == 1,
			$"off {offAgain}, single {noSecondCopy}, bag holds {_bag.CountResource( armorId )}" );
	}

	void RunHandCases()
	{
		var refused = !_equipment.HostSetHandFromHotbar( EquipmentSlot.MainHand, WeaponId )
		              && _equipment.GetSlot( EquipmentSlot.MainHand ).IsEmpty;
		Check( "a hand refuses a weapon the hotbar does not hold", refused,
			$"main hand {Describe( _equipment.GetSlot( EquipmentSlot.MainHand ) )}" );

		ref var cursor = ref _bag.HostCursor;
		_bag.HostTryAddResource( WeaponId, 1 );
		var weaponSlot = FindBagSlot( WeaponId );
		if ( weaponSlot < 0 )
		{
			// A saved binding steered it straight onto the hotbar: equally fine for the check below.
			weaponSlot = FindHotbarSlot( WeaponId );
			if ( weaponSlot < 0 )
			{
				Fail( "hand", $"host add of {WeaponId} landed nowhere" );
				return;
			}
		}
		else
		{
			_bag.HostCursorPickupAll( weaponSlot );
			_hotbar.HostCursorFinishDragDrop( -1, 0 );
		}

		var accepted = _equipment.HostSetHandFromHotbar( EquipmentSlot.MainHand, WeaponId )
		               && ResourceCatalog.ResourceIdsMatch( _equipment.GetSlotResourceId( EquipmentSlot.MainHand ), WeaponId );
		Check( "a hand equips the hotbar's own copy of the weapon", accepted && cursor.IsEmpty,
			$"main hand {Describe( _equipment.GetSlot( EquipmentSlot.MainHand ) )}, cursor {Describe( cursor )}" );

		_equipment.HostCursorPickupFromSlot( EquipmentSlot.MainHand );
		Check( "a hand slot is display only, never a source of items",
			cursor.IsEmpty && _bag.CountResource( WeaponId ) == 1,
			$"cursor {Describe( cursor )}, {WeaponId} carried {_bag.CountResource( WeaponId )}" );
	}

	void RunAugmentCases()
	{
		AugmentCatalog.EnsureLoaded();
		if ( AugmentCatalog.All.Count == 0 )
		{
			Fail( "augments", "the augment catalog is empty" );
			return;
		}

		var augmentId = AugmentCatalog.All[0].Id;
		ref var cursor = ref _bag.HostCursor;

		// The bank refuses anything that is not an augment.
		_bag.HostCursorPickupAll( FindBagSlot( StackB ) );
		_augments.HostCursorPlaceHeldBank( 1 );
		var nonAugmentRefused = _augments.GetBankSlot( 1 ).IsEmpty && cursor.CanStack( StackB ) && cursor.Count == 6;
		_bag.HostSettleCursor();

		_bag.HostTryAddResource( augmentId, 1 );
		_bag.HostCursorPickupAll( FindBagSlot( augmentId ) );
		_augments.HostCursorPlaceHeldBank( 0 );
		var banked = ResourceCatalog.ResourceIdsMatch( _augments.GetBankSlot( 0 ).ResourceId, augmentId )
		             && _augments.GetBankSlot( 0 ).Count == 1 && cursor.IsEmpty;
		Check( "the augment bank takes augments from the cursor and nothing else",
			nonAugmentRefused && banked,
			$"non-augment refused {nonAugmentRefused}, banked {banked}, cursor {Describe( cursor )}" );

		// A locked socket refuses the augment and the cursor keeps it.
		var locked = FirstLockedSocket();
		_augments.HostCursorPickupBank( 0 );
		var pickedUp = _augments.GetBankSlot( 0 ).IsEmpty && cursor.CanStack( augmentId ) && cursor.Count == 1;
		var lockedRefused = true;
		if ( locked is { } socket )
		{
			_augments.HostCursorPlaceIntoInstalled( socket );
			lockedRefused = _augments.GetInstalled( socket ).IsEmpty && cursor.Count == 1;
		}

		_augments.HostCursorPlaceHeldBank( 0 );
		Check( "a bank pickup and a locked socket never copy or lose the augment",
			pickedUp && lockedRefused && _augments.GetBankSlot( 0 ).Count == 1 && cursor.IsEmpty,
			$"picked {pickedUp}, locked refused {lockedRefused}, bank {Describe( _augments.GetBankSlot( 0 ) )}, cursor {Describe( cursor )}" );
	}

	// ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Everything the pawn and the chest hold of one resource, wherever it sits.</summary>
	int Total( string resourceId )
	{
		var total = _bag.CountResource( resourceId );
		if ( _bag.HostCursor.CanStack( resourceId ) )
			total += _bag.HostCursor.Count;

		for ( var i = 0; i < _chest.SlotCount; i++ )
		{
			var slot = _chest.GetSlot( i );
			if ( !slot.IsEmpty && ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				total += slot.Count;
		}

		return total;
	}

	int FindBagSlot( string resourceId )
	{
		for ( var i = 0; i < _bag.SlotCount; i++ )
		{
			var slot = _bag.GetSlot( i );
			if ( !slot.IsEmpty && ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				return i;
		}

		return -1;
	}

	int FindHotbarSlot( string resourceId )
	{
		for ( var i = 0; i < PlayerHotbar.SlotCount; i++ )
		{
			var slot = _hotbar.GetSlot( i );
			if ( !slot.IsEmpty && ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				return i;
		}

		return -1;
	}

	int FindChestSlot( string resourceId )
	{
		for ( var i = 0; i < _chest.SlotCount; i++ )
		{
			var slot = _chest.GetSlot( i );
			if ( !slot.IsEmpty && ResourceCatalog.ResourceIdsMatch( slot.ResourceId, resourceId ) )
				return i;
		}

		return -1;
	}

	/// <summary>First catalog piece worn in one of the six armor slots, plus an armor slot it does not fit.</summary>
	static bool TryFindArmor( out string armorId, out EquipmentSlot armorSlot, out EquipmentSlot wrongSlot )
	{
		armorId = null;
		armorSlot = default;
		wrongSlot = default;

		EquipmentCatalog.EnsureLoaded();
		var profiles = EquipmentCatalog.All;
		for ( var i = 0; i < profiles.Count; i++ )
		{
			var profile = profiles[i];
			if ( profile is null || string.IsNullOrWhiteSpace( profile.ResourceId ) || EquipmentCatalog.IsHotbarHandItem( profile ) )
				continue;

			var primary = EquipmentCatalog.GetPrimarySlot( profile );
			if ( primary > EquipmentSlot.Feet || !EquipmentCatalog.IsSlotAllowed( profile, primary ) )
				continue;

			for ( var s = EquipmentSlot.Head; s <= EquipmentSlot.Feet; s++ )
			{
				if ( EquipmentCatalog.IsSlotAllowed( profile, s ) )
					continue;

				armorId = profile.ResourceId;
				armorSlot = primary;
				wrongSlot = s;
				return true;
			}
		}

		return false;
	}

	AugmentSlot? FirstLockedSocket()
	{
		for ( var i = 0; i < AugmentSlots.Count; i++ )
		{
			if ( !_augments.IsSlotUnlocked( (AugmentSlot)i ) )
				return (AugmentSlot)i;
		}

		return null;
	}

	static string Describe( in InventorySlot slot ) => slot.IsEmpty ? "empty" : $"{slot.Count}x {slot.ResourceId}";

	static string Describe( in InventoryCursorStack stack ) => stack.IsEmpty ? "empty" : $"{stack.Count}x {stack.ResourceId}";

	void Check( string name, bool ok, string detail )
	{
		if ( ok )
		{
			_passed++;
			Log.Info( $"{Tag} PASS {name}" );
		}
		else
			Fail( name, detail );
	}

	void Fail( string name, string detail )
	{
		_failures.Add( name );
		Log.Warning( $"{Tag} FAIL {name} - {detail}" );
	}
}
