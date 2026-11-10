using System;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-toggled dev hacks, driven from the console. Flags are local to this client; systems
/// that need host validation to honour a flag mirror it onto the pawn (e.g.
/// <see cref="PlayerCrafting.AllCraftingHack"/>) so the host reads the owner's setting.
/// <para>
/// Console: <c>allCrafting true</c> / <c>allCrafting false</c>, <c>freeAugments true|false</c>, <c>devSupply true|false</c> — <c>hacks</c> lists every flag.
/// </para>
/// </summary>
public static class GameHacks
{
	/// <summary>
	/// Personal crafting menu lists every recipe (locked, workbench-only, station-gated) and crafting
	/// consumes nothing; the hoe's Raise costs no dirt and its Cobblestone no stone either (<see cref="PlayerTerraform.MaterialsAreFree"/>). Defaults on
	/// while the crafting content is being built out.
	/// </summary>
	public static bool AllCrafting { get; private set; } = true;

	/// <summary>
	/// The augment station charges 0 gold coins to commit augments (cores for enhancing still apply).
	/// Defaults on while the gold economy does not exist yet — <c>freeAugments false</c> re-enables the prices.
	/// </summary>
	public static bool FreeAugments { get; private set; } = true;

	/// <summary>Forces the Thermal Eye view on for the local player without the augment (<c>thermalView true|false</c>). Off by default.</summary>
	public static bool ThermalView { get; private set; }

	/// <summary>
	/// Unlocks the dev supply: a take-only grid with a full stack of every resource and crafting
	/// ingredient that refills after every take (<c>devSupply true</c>, then key X or <c>devSupplyOpen</c>).
	/// Off by default.
	/// </summary>
	public static bool DevSupply { get; private set; }

	/// <summary>
	/// Players arrive in a crash-landing <see cref="StasisPod"/> (<see cref="StasisPodSpawner"/>) on their first
	/// spawn of the session. On by default (Mark 2026-11-09); <c>pod_arrivals 0</c> for plain dev spawns.
	/// </summary>
	public static bool PodArrivals { get; private set; } = true;

	/// <summary>Bumps whenever any flag changes — UI that caches a hack-dependent layout rebuilds on this.</summary>
	public static int Version { get; private set; }

	/// <summary>Usage: <c>pod_arrivals 1</c> / <c>pod_arrivals 0</c>. No argument prints the state.</summary>
	[ConCmd( "pod_arrivals" )]
	public static void ConCmdPodArrivals( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( $"[Hacks] pod_arrivals is {(PodArrivals ? "1" : "0")} (usage: pod_arrivals 0|1)" );
			return;
		}

		if ( !TryParseBool( enabled, out var value ) )
		{
			Log.Warning( $"[Hacks] pod_arrivals: '{enabled}' is not 0/1." );
			return;
		}

		if ( value == PodArrivals )
			return;

		PodArrivals = value;
		Version++;
		Log.Info( $"[Hacks] pod_arrivals = {(value ? "1" : "0")}" );
	}

	/// <summary>Usage: <c>thermalView true</c> / <c>thermalView false</c>.</summary>
	[ConCmd( "thermalView" )]
	public static void ConCmdThermalView( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( $"[Hacks] thermalView is {(ThermalView ? "true" : "false")} (usage: thermalView true|false)" );
			return;
		}

		if ( !TryParseBool( enabled, out var value ) )
		{
			Log.Warning( $"[Hacks] thermalView: '{enabled}' is not true/false." );
			return;
		}

		if ( value == ThermalView )
			return;

		ThermalView = value;
		Version++;
		Log.Info( $"[Hacks] thermalView = {(value ? "true" : "false")}" );
	}

	/// <summary>Usage: <c>devSupply true</c> / <c>devSupply false</c>. No argument prints the current state.</summary>
	[ConCmd( "devSupply" )]
	public static void ConCmdDevSupply( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( $"[Hacks] devSupply is {(DevSupply ? "true" : "false")} (usage: devSupply true|false)" );
			return;
		}

		if ( !TryParseBool( enabled, out var value ) )
		{
			Log.Warning( $"[Hacks] devSupply: '{enabled}' is not true/false." );
			return;
		}

		if ( value == DevSupply )
			return;

		DevSupply = value;
		Version++;
		Log.Info( $"[Hacks] devSupply = {(value ? "true" : "false")}{(value ? " — press X or run devSupplyOpen" : string.Empty)}" );
	}

	/// <summary>Usage: <c>devSupplyOpen</c> — opens (or closes) the dev supply next to your bag. Needs <c>devSupply true</c>.</summary>
	[ConCmd( "devSupplyOpen" )]
	public static void ConCmdDevSupplyOpen()
	{
		if ( !DevSupply )
		{
			Log.Warning( "[Hacks] devSupplyOpen: run devSupply true first." );
			return;
		}

		var interaction = FindLocalPawnVitals()?.Components.Get<PlayerInventoryInteraction>( FindMode.EverythingInSelfAndDescendants );
		if ( interaction is null )
		{
			Log.Warning( "[Hacks] devSupplyOpen: no local player pawn." );
			return;
		}

		interaction.ToggleDevSupply();
	}

	public static void SetFreeAugments( bool enabled )
	{
		if ( FreeAugments == enabled )
			return;

		FreeAugments = enabled;
		Version++;
		Log.Info( $"[Hacks] freeAugments = {(enabled ? "true" : "false")}" );
	}

	/// <summary>Usage: <c>freeAugments true</c> / <c>freeAugments false</c>. No argument prints the current state.</summary>
	[ConCmd( "freeAugments" )]
	public static void ConCmdFreeAugments( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( $"[Hacks] freeAugments is {(FreeAugments ? "true" : "false")} (usage: freeAugments true|false)" );
			return;
		}

		if ( !TryParseBool( enabled, out var value ) )
		{
			Log.Warning( $"[Hacks] freeAugments: '{enabled}' is not true/false." );
			return;
		}

		SetFreeAugments( value );
	}

	public static void SetAllCrafting( bool enabled )
	{
		if ( AllCrafting == enabled )
			return;

		AllCrafting = enabled;
		Version++;
		Log.Info( $"[Hacks] allCrafting = {(enabled ? "true" : "false")}" );
	}

	/// <summary>Usage: <c>allCrafting true</c> / <c>allCrafting false</c>. No argument prints the current state.</summary>
	[ConCmd( "allCrafting" )]
	public static void ConCmdAllCrafting( string enabled )
	{
		if ( string.IsNullOrWhiteSpace( enabled ) )
		{
			Log.Info( $"[Hacks] allCrafting is {(AllCrafting ? "true" : "false")} (usage: allCrafting true|false)" );
			return;
		}

		if ( !TryParseBool( enabled, out var value ) )
		{
			Log.Warning( $"[Hacks] allCrafting: '{enabled}' is not true/false." );
			return;
		}

		if ( value == AllCrafting )
		{
			Log.Info( $"[Hacks] allCrafting already {(value ? "true" : "false")}" );
			return;
		}

		SetAllCrafting( value );
	}

	/// <summary>
	/// Usage: <c>status &lt;id&gt; &lt;seconds&gt;</c> applies a buff / debuff from
	/// <c>data/status_effects.json</c> to your pawn (e.g. <c>status poisoned 10</c>);
	/// <c>status &lt;id&gt; 0</c> removes it; <c>status clear</c> drops them all.
	/// </summary>
	[ConCmd( "status" )]
	public static void ConCmdStatus( string effectId, float seconds )
	{
		if ( string.IsNullOrWhiteSpace( effectId ) )
		{
			Log.Info( "[Hacks] usage: status <id> <seconds> | status <id> 0 | status clear" );
			return;
		}

		var vitals = FindLocalPawnVitals();
		if ( vitals is null )
		{
			Log.Warning( "[Hacks] status: no local player pawn." );
			return;
		}

		vitals.OwnerRequestDebugStatusEffect( effectId.Trim(), seconds );
	}

	/// <summary>
	/// Usage: <c>spawnPatrolBot</c> — host drops a patrol bot (scav stats, robot kind) 3 m in front of
	/// your pawn. For Medusa Eye / Hackd testing.
	/// </summary>
	[ConCmd( "spawnPatrolBot" )]
	public static void ConCmdSpawnPatrolBot()
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Hacks] spawnPatrolBot: host only." );
			return;
		}

		var vitals = FindLocalPawnVitals();
		if ( vitals is null )
		{
			Log.Warning( "[Hacks] spawnPatrolBot: no local player pawn." );
			return;
		}

		var pawn = vitals.GameObject;
		var forward = pawn.WorldRotation.Forward.WithZ( 0f );
		if ( forward.LengthSquared < 1e-6f )
			forward = Vector3.Forward;

		var position = pawn.WorldPosition + forward.Normal * TerrainWorldUnits.MetersToEngine( 3f );
		var facing = Rotation.LookAt( -forward.Normal, Vector3.Up );
		var bot = EnemySpawnButton.HostSpawn( pawn.Scene, "prefabs/entity/scavT1.prefab", EnemyType.PatrolBot, 1, position, facing );
		Log.Info( bot is not null ? "[Hacks] spawned a patrol bot." : "[Hacks] spawnPatrolBot failed." );
	}

	/// <summary>
	/// Usage: <c>give &lt;resourceId&gt; [amount]</c> — host puts an item straight into your pawn's
	/// hotbar / inventory (<c>give club_wood</c>, <c>give resource_woodBasic 20</c>). For checking
	/// imported held models and recipes without walking to a kit chest.
	/// </summary>
	[ConCmd( "give" )]
	public static void ConCmdGive( string resourceId, int amount = 1 )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Hacks] give: host only." );
			return;
		}

		var vitals = FindLocalPawnVitals();
		var inventory = vitals?.Components.Get<PlayerInventory>();
		if ( inventory is null )
		{
			Log.Warning( "[Hacks] give: no local player pawn." );
			return;
		}

		var id = ResourceCatalog.NormalizeResourceId( resourceId ?? string.Empty );
		if ( string.IsNullOrWhiteSpace( id ) )
		{
			Log.Warning( "[Hacks] give: usage give <resourceId> [amount]" );
			return;
		}

		var added = inventory.HostTryAddResource( id, Math.Max( 1, amount ) );
		Log.Info( added ? $"[Hacks] gave {Math.Max( 1, amount )} x {id}." : $"[Hacks] give {id} failed (unknown id or inventory full)." );
	}

	/// <summary>
	/// Usage: <c>equip &lt;resourceId&gt;</c> — host gives one of the item into hotbar slot 1 (slot 2 for a
	/// shield, so both hands can be filled), selects that slot and equips it (<c>equip club_wood</c> shows the
	/// held sword model, <c>equip shield_wood</c> the shield on the left forearm). Hotbar hand items only.
	/// </summary>
	[ConCmd( "equip" )]
	public static void ConCmdEquip( string resourceId )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Hacks] equip: host only." );
			return;
		}

		var vitals = FindLocalPawnVitals();
		var inventory = vitals?.Components.Get<PlayerInventory>();
		var hotbar = vitals?.Components.Get<PlayerHotbar>();
		if ( inventory is null || hotbar is null )
		{
			Log.Warning( "[Hacks] equip: no local player pawn." );
			return;
		}

		var id = ResourceCatalog.NormalizeResourceId( resourceId ?? string.Empty );
		if ( !EquipmentCatalog.TryGet( id, out var profile ) || !EquipmentCatalog.IsHotbarHandItem( profile ) )
		{
			Log.Warning( $"[Hacks] equip: '{id}' is not a hotbar hand item (weapon, tool or shield)." );
			return;
		}

		// A binding ghost on the slot makes the pickup land there; PlayerEquipment re-syncs on the
		// hotbar change and on the active-slot change, so the item is in hand right after.
		var slotIndex = hotbar.ActiveBank * PlayerHotbar.BankSize + (EquipmentCatalog.IsHotbarOffHandItem( profile ) ? 1 : 0);
		hotbar.SetBinding( slotIndex, id );
		if ( !inventory.HostTryAddResource( id, 1 ) )
		{
			Log.Warning( $"[Hacks] equip {id} failed (hotbar slot {PlayerHotbar.IndexInBank( slotIndex ) + 1} occupied or inventory full)." );
			return;
		}

		hotbar.SetActiveSlot( slotIndex );
		Log.Info( $"[Hacks] equipped {id} in hotbar slot {PlayerHotbar.IndexInBank( slotIndex ) + 1}." );
	}

	// --- Simulated attack press (swing) -------------------------------------------------------

	static double _simulatedAttackUntilGlobal;
	static double _simulatedBlockUntilGlobal;

	/// <summary>Usage: <c>block [holdSeconds]</c> — holds the block action (Attack2) like a mouse would, for shield / guard checks.</summary>
	[ConCmd( "block" )]
	public static void ConCmdBlock( float holdSeconds = 1f )
	{
		_simulatedBlockUntilGlobal = RealTime.GlobalNow + Math.Clamp( holdSeconds, 0.02f, 10f );
		Log.Info( $"[Hacks] block: holding guard for {holdSeconds:0.##}s." );
	}

	/// <summary>
	/// Usage: <c>swing [holdSeconds]</c> — clicks the primary attack (the swing commits on the press) and
	/// holds the button for <c>holdSeconds</c>, so the windup → swing can be filmed from the editor, which
	/// cannot press keys. Held past the class windup (e.g. <c>swing 0.7</c>) the swing turns heavy. A second <c>swing</c>
	/// during a swing is a queued follow-up click.
	/// <see cref="PlayerCombat"/> calls <see cref="ApplySimulatedInput"/> at the top of its owner
	/// input tick; <c>Input.SetAction</c> only lasts the frame it is set in.
	/// </summary>
	[ConCmd( "swing" )]
	public static void ConCmdSwing( float holdSeconds = 0.15f )
	{
		_simulatedAttackUntilGlobal = RealTime.GlobalNow + Math.Clamp( holdSeconds, 0.02f, 3f );
		Log.Info( $"[Hacks] swing: holding attack for {holdSeconds:0.##}s." );
	}

	/// <summary>Usage: <c>special_attack</c> — press the melee special (Q) for one frame, as the key would (static combo mode, on the ground).</summary>
	[ConCmd( "special_attack" )]
	public static void ConCmdSpecialAttack()
	{
		_simulatedSpecialPending = true;
		Log.Info( "[Hacks] special_attack: pressing the special once." );
	}

	static bool _simulatedSpecialPending;

	/// <summary>
	/// Usage: <c>swing_dir L|R|U|D</c> — sets the local pawn's attack side as mouse movement would (Left / Right swing or the
	/// Up overhead), so a filmed or scripted <c>swing</c> can pick its direction from the editor. Mouse movement overrides it.
	/// </summary>
	/// <summary>Usage: <c>wingsuit_test [meters]</c> — lift the local pawn and open the wingsuit (no gear needed).</summary>
	[ConCmd( "wingsuit_test" )]
	public static void ConCmdWingsuitTest( float meters = 80f )
	{
		var movement = FindLocalPawnVitals()?.Components.Get<PlayerMovement>();
		if ( movement is null )
		{
			Log.Warning( "[Hacks] wingsuit_test: no local pawn." );
			return;
		}

		if ( !movement.TestLiftForWingsuit( meters ) )
		{
			Log.Warning( "[Hacks] wingsuit_test: not the movement driver." );
			return;
		}

		_ = DeployWingsuitWhenAirborne( movement, meters );
	}

	static async System.Threading.Tasks.Task DeployWingsuitWhenAirborne( PlayerMovement movement, float meters )
	{
		// The controller keeps its old ground contact for a tick after the lift - the first glide tick would stow.
		await GameTask.DelaySeconds( 0.2f );
		if ( !movement.IsValid() )
			return;

		Log.Info( movement.TestDeployWingsuit()
			? $"[Hacks] wingsuit_test: gliding from {meters:0} m up."
			: "[Hacks] wingsuit_test: the suit did not open." );
	}

	[ConCmd( "swing_dir" )]
	public static void ConCmdSwingDir( string side = "R" )
	{
		var want = (side ?? "R").Trim().ToUpperInvariant() switch
		{
			"L" => MeleeAttackTypes.Left,
			"U" => MeleeAttackTypes.Forward,
			"D" => MeleeAttackTypes.Stab,
			_ => MeleeAttackTypes.Right,
		};
		var combat = FindLocalPawnVitals()?.Components.Get<PlayerCombat>();
		if ( combat is null )
		{
			Log.Warning( "[Hacks] swing_dir: no local pawn." );
			return;
		}

		foreach ( var dir in new[] { SwingDirs.Left, SwingDirs.Right, SwingDirs.Up, SwingDirs.Down } )
		{
			if ( combat.ResolveAttackTypeFromCursorDir( dir ) != want )
				continue;
			combat.TestSetLiveSwingDir( dir );
			Log.Info( $"[Hacks] swing_dir: next swing {side} (cursor {SwingDirs.Letter( dir )})." );
			return;
		}

		Log.Warning( $"[Hacks] swing_dir: no cursor side gives a {side} attack with this weapon." );
	}

	static double _simulatedSneakUntilGlobal;

	/// <summary>Usage: <c>sneak [holdSeconds]</c> — holds the crouch / sneak action as a key would (taming offers, sneak footsteps).</summary>
	[ConCmd( "sneak" )]
	public static void ConCmdSneak( float holdSeconds = 5f )
	{
		_simulatedSneakUntilGlobal = RealTime.GlobalNow + Math.Clamp( holdSeconds, 0.1f, 120f );
		Log.Info( $"[Hacks] sneak: holding crouch for {holdSeconds:0.##}s." );
	}

	/// <summary>True while a <c>sneak</c> hack hold is running (PlayerMovement reads it beside the real key).</summary>
	public static bool SimulatedSneakHeld => RealTime.GlobalNow < _simulatedSneakUntilGlobal;

	/// <summary>Owner input tick hook: keeps the simulated attack action down until its time is up.</summary>
	public static void ApplySimulatedInput( string primaryAttackAction, string blockAction = null, string specialAction = null )
	{
		if ( _simulatedSpecialPending && !string.IsNullOrWhiteSpace( specialAction ) )
		{
			_simulatedSpecialPending = false;
			Input.SetAction( specialAction, true );
		}

		if ( RealTime.GlobalNow < _simulatedBlockUntilGlobal && !string.IsNullOrWhiteSpace( blockAction ) )
			Input.SetAction( blockAction, true );

		if ( RealTime.GlobalNow >= _simulatedAttackUntilGlobal || string.IsNullOrWhiteSpace( primaryAttackAction ) )
			return;

		Input.SetAction( primaryAttackAction, true );
	}

	static PlayerVitals FindLocalPawnVitals()
	{
		var scene = Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return null;

		foreach ( var vitals in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( vitals is not null && vitals.IsLocalInputOwnedPawn() )
				return vitals;
		}

		return null;
	}

	/// <summary>Prints every hack flag and its current value.</summary>
	[ConCmd( "hacks" )]
	public static void ConCmdList()
	{
		var sb = new StringBuilder();
		sb.AppendLine( "[Hacks]" );
		sb.Append( "  allCrafting  " ).AppendLine( AllCrafting ? "true" : "false" );
		sb.Append( "  freeAugments " ).AppendLine( FreeAugments ? "true" : "false" );
		sb.Append( "  thermalView  " ).AppendLine( ThermalView ? "true" : "false" );
		sb.Append( "  devSupply    " ).AppendLine( DevSupply ? "true" : "false" );
		sb.AppendLine( "  devSupplyOpen  open / close the dev supply (key X; needs devSupply true)" );
		sb.AppendLine( "  status <id> <seconds>  apply a buff / debuff (status clear)" );
		sb.AppendLine( "  spawnPatrolBot  host: drop a robot patrol unit in front of you" );
		sb.AppendLine( "  give <resourceId> [amount]  host: put an item in your hotbar / inventory" );
		sb.AppendLine( "  equip <resourceId>  host: give one into hotbar slot 1 and hold it" );
		sb.AppendLine( "  swing [holdSeconds]  click the primary attack; held past the windup (0.7) = heavy" );
		Log.Info( sb.ToString() );
	}

	static bool TryParseBool( string text, out bool value )
	{
		value = false;
		if ( string.IsNullOrWhiteSpace( text ) )
			return false;

		switch ( text.Trim().ToLowerInvariant() )
		{
			case "1":
			case "true":
			case "on":
			case "yes":
				value = true;
				return true;
			case "0":
			case "false":
			case "off":
			case "no":
				value = false;
				return true;
			default:
				return false;
		}
	}
}
