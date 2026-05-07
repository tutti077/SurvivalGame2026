using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox;

namespace Game;

/// <summary>
/// Host-authoritative grid + hotbar. Local owner gets UI; mutations from owner go through <see cref="RpcInvMove"/> / <see cref="RpcInvDropWorld"/>.
/// Wire <see cref="Catalog"/> to a scene <see cref="ItemCatalog"/>. Hotbar is the first <see cref="HotbarSlotCount"/> indices.
/// </summary>
public sealed partial class PlayerInventory : Component
{
	[Property] public ItemCatalog Catalog { get; set; }

	[Property] public int HotbarSlotCount { get; set; } = 10;

	[Property] public int BackpackSlotCount { get; set; } = 24;

	[Property] public string InventoryToggleButton { get; set; } = "score";

	[Property] public float DropForwardDistance { get; set; } = 72f;

	/// <summary>Editor: log inventory drop/move and icon load diagnostics to the game console.</summary>
	[Property] public bool DebugInventory { get; set; }

	[Sync( SyncFlags.FromHost ), Change( nameof( OnSlotBlobChanged ) )]
	public string SlotBlob { get; set; } = "";

	/// <summary>Raised locally when synced slots change (including from host).</summary>
	public event Action OnInventoryChanged;

	private GameObject _uiRoot;
	private bool _ownsDedicatedInventoryScreenRoot;
	private InvSlot[] _cacheSlots;
	private string _cacheKey = "";
	private int _hotbarSelected;

	public int TotalSlotCount => Math.Max( 1, HotbarSlotCount + Math.Max( 0, BackpackSlotCount ) );

	public int HotbarSelectedIndex => _hotbarSelected;

	public bool IsInventoryOpen { get; private set; }

	public void SetInventoryOpen( bool open )
	{
		IsInventoryOpen = open;
		OnInventoryChanged?.Invoke();
	}

	public void ToggleInventory()
	{
		SetInventoryOpen( !IsInventoryOpen );
	}

	public InvSlot GetSlot( int index )
	{
		EnsureCache();
		if ( index < 0 || index >= _cacheSlots.Length )
			return InvSlot.Empty;

		return _cacheSlots[index];
	}

	/// <summary>First empty hotbar index, then first empty backpack slot; <c>-1</c> if the grid is full.</summary>
	public int FindFirstEmptySlotIndex()
	{
		EnsureCache();
		var n = TotalSlotCount;
		var hotEnd = Math.Clamp( HotbarSlotCount, 0, n );
		for ( var i = 0; i < hotEnd; i++ )
		{
			if ( GetSlot( i ).IsEmpty )
				return i;
		}

		for ( var i = hotEnd; i < n; i++ )
		{
			if ( GetSlot( i ).IsEmpty )
				return i;
		}

		return -1;
	}

	public bool TryGetDefinition( string itemId, out InventoryItemDefinitionEntry def )
	{
		if ( Catalog is not null && Catalog.IsValid() && Catalog.TryGet( itemId, out def ) )
			return true;

		def = null;
		return false;
	}

	protected override void OnStart()
	{
		_hotbarSelected = 0;
		if ( IsLocalOwnerForUi() )
			_ = CreateLocalUiDeferredAsync();
	}

	private async Task CreateLocalUiDeferredAsync()
	{
		await GameTask.Yield();
		await GameTask.Yield();

		if ( !GameObject.IsValid() || !IsLocalOwnerForUi() )
			return;

		CreateUi();
		EnsureHotbarHold();
	}

	private void EnsureHotbarHold()
	{
		var hold = Components.Get<PlayerInventoryHotbarHold>();
		if ( hold is null )
			hold = Components.Create<PlayerInventoryHotbarHold>();

		hold.Inventory = this;
		hold.ItemPickup = FindPlayerItemPickup();
	}

	private PlayerItemPickup FindPlayerItemPickup()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var p = go.Components.Get<PlayerItemPickup>();
			if ( p is not null )
				return p;
		}

		return GameObject.Components.Get<PlayerItemPickup>();
	}

	private void InvDbg( string message )
	{
		if ( !DebugInventory )
			return;

		Log.Info( $"[PlayerInventory] {message}" );
	}

	protected override void OnDestroy()
	{
		if ( _ownsDedicatedInventoryScreenRoot && _uiRoot is not null && _uiRoot.IsValid() )
			_uiRoot.Destroy();

		_uiRoot = null;
		_ownsDedicatedInventoryScreenRoot = false;
	}

	protected override void OnUpdate()
	{
		if ( !IsLocalOwnerForUi() )
			return;

		var hotbarHold = Components.Get<PlayerInventoryHotbarHold>();
		if ( hotbarHold is not null && hotbarHold.ItemPickup is null )
			EnsureHotbarHold();

		if ( !string.IsNullOrEmpty( InventoryToggleButton ) && Input.Pressed( InventoryToggleButton ) )
			ToggleInventory();

		// Scroll wheel: forward (positive y) = next slot, backward = previous; wraps at ends.
		var wheel = Input.MouseWheel.y;
		if ( MathF.Abs( wheel ) > 0.01f && HotbarSlotCount > 0 )
		{
			var delta = wheel > 0f ? 1 : -1;
			_hotbarSelected = ((_hotbarSelected + delta) % HotbarSlotCount + HotbarSlotCount) % HotbarSlotCount;
			OnInventoryChanged?.Invoke();
		}

		// Digit keys: Slot1..Slot9 → slots 0..8; Slot0 (keyboard 0) → slot 9 — see ProjectSettings/Input.config.
		var digitKeyHandled = false;
		for ( var digit = 1; digit <= 9; digit++ )
		{
			if ( !GameMovementInput.InputPressedFlexible( $"Slot{digit}" ) )
				continue;

			digitKeyHandled = true;
			var idx = digit - 1;
			if ( idx < HotbarSlotCount )
			{
				_hotbarSelected = idx;
				OnInventoryChanged?.Invoke();
			}

			break;
		}

		if ( !digitKeyHandled && GameMovementInput.InputPressedFlexible( "Slot0" ) && HotbarSlotCount > 9 )
		{
			_hotbarSelected = 9;
			OnInventoryChanged?.Invoke();
		}
	}

	public void RequestMoveSlot( int fromIndex, int toIndex, bool splitHalf )
	{
		if ( !IsLocalOwnerForUi() )
			return;

		if ( IsInventoryAuthority() )
			HostMoveSlot( fromIndex, toIndex, splitHalf );
		else
			RpcInvMove( fromIndex, toIndex, splitHalf );
	}

	public void RequestDropSlotToWorld( int slotIndex, bool dropAll )
	{
		if ( !IsLocalOwnerForUi() )
			return;

		InvDbg( $"RequestDropSlotToWorld slot={slotIndex} dropAll={dropAll} authority={IsInventoryAuthority()} netActive={Networking.IsActive} isHost={Networking.IsHost}" );

		if ( IsInventoryAuthority() )
			HostDropToWorld( slotIndex, dropAll );
		else
			RpcInvDropWorld( slotIndex, dropAll );
	}

	/// <summary>Host / offline: add items from a destroyed world pickup.</summary>
	/// <summary>True when world pickups can be resolved into inventory on this process (listen server host or offline).</summary>
	public static bool CanAuthoritativePickup()
		=> !Networking.IsActive || Networking.IsHost;

	public bool HostTryAddFromWorld( string itemId, int count )
	{
		if ( !IsInventoryAuthority() || count <= 0 || string.IsNullOrWhiteSpace( itemId ) )
			return false;

		if ( !TryGetDefinition( itemId, out var def ) )
			return false;

		var scratch = ReadSlots();
		var remaining = count;
		TryMergeIntoExistingStacks( scratch, HotbarSlotCount, itemId, ref remaining, def );

		while ( remaining > 0 )
		{
			var empty = FindFirstEmptyPreferHotbar( scratch, HotbarSlotCount );
			if ( empty < 0 )
				return false;

			var chunk = def.Stackable ? Math.Min( remaining, Math.Max( 1, def.MaxStackSize ) ) : 1;
			scratch[empty] = InvSlot.Of( itemId, chunk );
			remaining -= chunk;
		}

		CommitSlots( scratch );
		return true;
	}

	private void OnSlotBlobChanged( string oldValue, string newValue )
	{
		_cacheKey = "";
		OnInventoryChanged?.Invoke();
	}

	private void EnsureCache()
	{
		var len = TotalSlotCount;
		if ( _cacheSlots is null || _cacheSlots.Length != len )
			_cacheSlots = new InvSlot[len];

		if ( string.Equals( _cacheKey, SlotBlob, StringComparison.Ordinal ) )
			return;

		InventorySerialization.ParseInto( SlotBlob, _cacheSlots );
		_cacheKey = SlotBlob ?? "";
	}

	private InvSlot[] ReadSlots()
	{
		var slots = new InvSlot[TotalSlotCount];
		InventorySerialization.ParseInto( SlotBlob, slots );
		return slots;
	}

	private void CommitSlots( InvSlot[] slots )
	{
		SlotBlob = InventorySerialization.Serialize( slots );
		_cacheKey = "";
		OnInventoryChanged?.Invoke();
	}

	private static bool IsInventoryAuthority()
		=> !Networking.IsActive || Networking.IsHost;

	private bool IsLocalOwnerForUi()
	{
		var n = GameObject.Network;
		if ( n is null || !n.Active )
			return true;

		return n.IsOwner;
	}

	private PlayerHealth FindPlayerHealth()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var h = go.Components.Get<PlayerHealth>();
			if ( h is not null )
				return h;
		}

		return GameObject.Components.Get<PlayerHealth>();
	}

	private void CreateUi()
	{
		_ownsDedicatedInventoryScreenRoot = false;

		var health = FindPlayerHealth();
		if ( health is not null )
		{
			_uiRoot = health.EnsureLocalScreenUiHost();
			if ( _uiRoot is not null && _uiRoot.IsValid() )
			{
				var hud = _uiRoot.Components.Create<PlayerInventoryHud>();
				hud.Inventory = this;
				return;
			}
		}

		_uiRoot = new GameObject( true, "PlayerInventoryUi" );
		_uiRoot.Parent = GameObject;
		_ = _uiRoot.Components.Create<ScreenPanel>();
		_ownsDedicatedInventoryScreenRoot = true;
		var soloHud = _uiRoot.Components.Create<PlayerInventoryHud>();
		soloHud.Inventory = this;
	}

	private static void TryMergeIntoExistingStacks( InvSlot[] slots, int hotbarSlotCount, string itemId, ref int remaining, InventoryItemDefinitionEntry def )
	{
		if ( !def.Stackable )
			return;

		var max = Math.Max( 1, def.MaxStackSize );
		var hotEnd = Math.Clamp( hotbarSlotCount, 0, slots.Length );

		TryMergeSpanIntoExistingStacks( slots, 0, hotEnd, itemId, ref remaining, max );
		TryMergeSpanIntoExistingStacks( slots, hotEnd, slots.Length, itemId, ref remaining, max );
	}

	private static void TryMergeSpanIntoExistingStacks( InvSlot[] slots, int start, int endEx, string itemId, ref int remaining, int max )
	{
		for ( var i = start; i < endEx && remaining > 0; i++ )
		{
			ref var s = ref slots[i];
			if ( s.IsEmpty || !string.Equals( s.ItemId, itemId, StringComparison.OrdinalIgnoreCase ) )
				continue;

			var room = max - s.Count;
			if ( room <= 0 )
				continue;

			var add = Math.Min( room, remaining );
			s.Count += add;
			remaining -= add;
		}
	}

	/// <summary>Empty hotbar slots (left to right), then first empty backpack slot.</summary>
	private static int FindFirstEmptyPreferHotbar( InvSlot[] slots, int hotbarSlotCount )
	{
		var hotEnd = Math.Clamp( hotbarSlotCount, 0, slots.Length );
		for ( var i = 0; i < hotEnd; i++ )
		{
			if ( slots[i].IsEmpty )
				return i;
		}

		for ( var i = hotEnd; i < slots.Length; i++ )
		{
			if ( slots[i].IsEmpty )
				return i;
		}

		return -1;
	}

	private void HostMoveSlot( int from, int to, bool splitHalf )
	{
		if ( !IsInventoryAuthority() || from == to )
			return;

		var n = TotalSlotCount;
		if ( from < 0 || from >= n || to < 0 || to >= n )
			return;

		var slots = ReadSlots();
		ref var a = ref slots[from];
		if ( a.IsEmpty )
			return;

		if ( !TryGetDefinition( a.ItemId, out var defFrom ) )
			return;

		var take = splitHalf && a.Count > 1 ? a.Count / 2 : a.Count;
		if ( take <= 0 )
			return;

		take = Math.Min( take, a.Count );
		ref var b = ref slots[to];

		if ( !b.IsEmpty && !string.Equals( a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase ) && take < a.Count )
			return;

		if ( !b.IsEmpty && string.Equals( a.ItemId, b.ItemId, StringComparison.OrdinalIgnoreCase ) && defFrom.Stackable )
		{
			var max = Math.Max( 1, defFrom.MaxStackSize );
			var room = max - b.Count;
			if ( room <= 0 )
				return;

			var mv = Math.Min( room, take );
			b.Count += mv;
			a.Count -= mv;
			if ( a.Count <= 0 )
				a = InvSlot.Empty;

			CommitSlots( slots );
			return;
		}

		if ( b.IsEmpty )
		{
			var maxFirst = defFrom.Stackable ? Math.Max( 1, defFrom.MaxStackSize ) : 1;
			var mv = Math.Min( take, maxFirst );
			b = InvSlot.Of( a.ItemId, mv );
			a.Count -= mv;
			if ( a.Count <= 0 )
				a = InvSlot.Empty;

			CommitSlots( slots );
			return;
		}

		if ( !TryGetDefinition( b.ItemId, out _ ) )
			return;

		var tmpA = a;
		var tmpB = b;
		a = tmpB;
		b = tmpA;
		CommitSlots( slots );
	}

	private void HostDropToWorld( int slotIndex, bool dropAll )
	{
		if ( !IsInventoryAuthority() )
		{
			InvDbg( "HostDropToWorld aborted: not inventory authority" );
			return;
		}

		var n = TotalSlotCount;
		if ( slotIndex < 0 || slotIndex >= n )
		{
			InvDbg( $"HostDropToWorld aborted: bad slot index {slotIndex} (total {n})" );
			return;
		}

		var slots = ReadSlots();
		ref var s = ref slots[slotIndex];
		if ( s.IsEmpty )
		{
			InvDbg( $"HostDropToWorld aborted: slot {slotIndex} empty" );
			return;
		}

		if ( Catalog is null || !Catalog.IsValid() )
			Log.Warning( "[PlayerInventory] HostDropToWorld: Catalog is not set on this PlayerInventory — drops and icons need the ItemCatalog reference." );

		if ( !TryGetDefinition( s.ItemId, out var def ) )
		{
			InvDbg( $"HostDropToWorld aborted: no definition for itemId '{s.ItemId}' (assign ItemCatalog on player)" );
			return;
		}

		if ( !TryGetDropTransform( out var pos, out var rot ) )
		{
			InvDbg( "HostDropToWorld aborted: TryGetDropTransform failed (no PlayerController / camera?)" );
			return;
		}

		RefineDroppedItemWorldPlacement( ref pos );

		var dropCount = dropAll ? s.Count : 1;
		dropCount = Math.Clamp( dropCount, 1, s.Count );
		var droppedId = s.ItemId;

		GameObject inst = null;
		var editorPrefab = ItemCatalog.ResolveEditorDropPrefab( def );
		if ( editorPrefab is not null && editorPrefab.IsValid() )
			inst = editorPrefab.Clone();
		else if ( ItemCatalog.TryLoadPrefabFile( def.WorldDropPrefabPath ) is { } pf )
			inst = GameObject.Clone( pf );

		if ( inst is null || !inst.IsValid() )
		{
			InvDbg( $"HostDropToWorld aborted: could not spawn prefab for '{s.ItemId}' (set WorldDroppedPrefab on catalog row or valid worldDropPrefabPath JSON, got path '{def.WorldDropPrefabPath}')" );
			return;
		}
		if ( inst is not null && inst.IsValid() )
		{
			inst.WorldPosition = pos;
			inst.WorldRotation = rot;

			var hadPickable = false;
			foreach ( var p in EnumeratePickables( inst ) )
			{
				hadPickable = true;
				p.InventoryItemId = s.ItemId;
				p.WorldPickupCount = dropCount;
				p.BeginInventoryDropSleepUntilCollision();
			}

			if ( !hadPickable )
			{
				foreach ( var rb in EnumerateRigidbodies( inst ) )
				{
					rb.CollisionEventsEnabled = true;
					rb.Gravity = true;
					rb.MotionEnabled = true;
				}
			}
		}

		s.Count -= dropCount;
		if ( s.Count <= 0 )
			s = InvSlot.Empty;

		CommitSlots( slots );
		InvDbg( $"HostDropToWorld OK: dropped '{droppedId}' x{dropCount} at {pos}" );
	}

	/// <summary>Trace downward from rough drop position onto geometry; clamps Z near the feet if missed.</summary>
	private void RefineDroppedItemWorldPlacement( ref Vector3 pos )
	{
		var pc = FindPlayerController();
		var bodyGo = pc?.Body?.GameObject;
		if ( bodyGo is null || !bodyGo.IsValid() )
			return;

		var probeStart = pos + Vector3.Up * 256f;
		var probeEnd = pos - Vector3.Up * 4096f;

		var trace = Scene.Trace
			.Sphere( 16f, probeStart, probeEnd )
			.IgnoreGameObjectHierarchy( GameObject )
			.IgnoreGameObjectHierarchy( bodyGo );

		var tr = trace.UseHitPosition( true ).Run();
		if ( tr.Hit )
		{
			pos = tr.HitPosition + tr.Normal * 4f + Vector3.Up * 8f;
			return;
		}

		var feetZ = bodyGo.WorldPosition.z;
		if ( pos.z < feetZ + 16f )
			pos = pos.WithZ( feetZ + 32f );
	}

	private bool TryGetDropTransform( out Vector3 pos, out Rotation rot )
	{
		pos = default;
		rot = Rotation.Identity;

		var pc = FindPlayerController();
		if ( pc is null )
			return false;

		if ( Scene.Camera is not null && !pc.ThirdPerson )
		{
			var cr = Scene.Camera.WorldRotation;
			var start = Scene.Camera.WorldPosition;
			var forward = cr.Forward;
			pos = start + forward * DropForwardDistance;
			rot = Rotation.LookAt( forward );
			return true;
		}

		if ( TryGetEyeForward( pc, out var origin, out var fwd ) )
		{
			pos = origin + fwd * DropForwardDistance;
			rot = Rotation.LookAt( fwd );
			return true;
		}

		// Menus / edge cases: camera or eye path can fail while inventory is open — still drop in front of the body.
		if ( pc.Body is not null && pc.Body.IsValid() )
		{
			var body = pc.Body.GameObject;
			var flatFwd = body.WorldRotation.Forward.WithZ( 0f );
			if ( flatFwd.Length < 0.001f )
				flatFwd = Rotation.From( pc.EyeAngles ).Forward.WithZ( 0f );
			if ( flatFwd.Length < 0.001f )
				flatFwd = Vector3.Forward;

			var dropFwd = flatFwd.Normal;
			pos = body.WorldPosition + Vector3.Up * (pc.CurrentHeight * 0.35f) + dropFwd * DropForwardDistance;
			rot = Rotation.LookAt( dropFwd );
			return true;
		}

		return false;
	}

	private static bool TryGetEyeForward( PlayerController pc, out Vector3 origin, out Vector3 forward )
	{
		origin = default;
		forward = default;
		if ( pc?.Body is null )
			return false;

		var eyeRot = Rotation.From( pc.EyeAngles );
		forward = eyeRot.Forward;
		var bodyPos = pc.Body.GameObject.WorldPosition;
		origin = bodyPos + Vector3.Up * (pc.CurrentHeight * 0.45f) + forward * 12f;
		return true;
	}

	[Rpc.Host]
	public void RpcInvMove( int fromIndex, int toIndex, bool splitHalf )
	{
		HostMoveSlot( fromIndex, toIndex, splitHalf );
	}

	[Rpc.Host]
	public void RpcInvDropWorld( int slotIndex, bool dropAll )
	{
		InvDbg( $"RpcInvDropWorld received slot={slotIndex} dropAll={dropAll}" );
		HostDropToWorld( slotIndex, dropAll );
	}

	private PlayerController FindPlayerController()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return pc;
		}

		return GameObject.Components.Get<PlayerController>();
	}

	private static IEnumerable<PickableItem> EnumeratePickables( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		var self = root.Components.Get<PickableItem>();
		if ( self is not null )
			yield return self;

		foreach ( var child in root.Children )
		{
			foreach ( var p in EnumeratePickables( child ) )
				yield return p;
		}
	}

	private static IEnumerable<Rigidbody> EnumerateRigidbodies( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		var self = root.Components.Get<Rigidbody>();
		if ( self is not null )
			yield return self;

		foreach ( var child in root.Children )
		{
			foreach ( var rb in EnumerateRigidbodies( child ) )
				yield return rb;
		}
	}
}
