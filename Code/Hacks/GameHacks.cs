using System;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-toggled dev hacks, driven from the console. Flags are local to this client; systems
/// that need host validation to honour a flag mirror it onto the pawn (e.g.
/// <see cref="PlayerCrafting.AllCraftingHack"/>) so the host reads the owner's setting.
/// <para>
/// Console: <c>allCrafting true</c> / <c>allCrafting false</c>, <c>freeAugments true|false</c> — <c>hacks</c> lists every flag.
/// </para>
/// </summary>
public static class GameHacks
{
	/// <summary>
	/// Personal crafting menu lists every recipe (locked, workbench-only, station-gated) and crafting
	/// consumes nothing. Defaults on while the crafting content is being built out.
	/// </summary>
	public static bool AllCrafting { get; private set; } = true;

	/// <summary>
	/// The augment station charges 0 gold coins to commit augments (cores for enhancing still apply).
	/// Defaults on while the gold economy does not exist yet — <c>freeAugments false</c> re-enables the prices.
	/// </summary>
	public static bool FreeAugments { get; private set; } = true;

	/// <summary>Forces the Thermal Eye view on for the local player without the augment (<c>thermalView true|false</c>). Off by default.</summary>
	public static bool ThermalView { get; private set; }

	/// <summary>Bumps whenever any flag changes — UI that caches a hack-dependent layout rebuilds on this.</summary>
	public static int Version { get; private set; }

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
	/// Usage: <c>equip &lt;resourceId&gt;</c> — host gives one of the item into hotbar slot 1, selects
	/// that slot and equips it (<c>equip club_wood</c> shows the held sword model). Hotbar main-hand
	/// items only.
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
		if ( !EquipmentCatalog.TryGet( id, out var profile ) || !EquipmentCatalog.IsHotbarMainHandItem( profile ) )
		{
			Log.Warning( $"[Hacks] equip: '{id}' is not a hotbar main-hand item." );
			return;
		}

		// A binding ghost on slot 0 makes the pickup land there; PlayerEquipment re-syncs on the
		// hotbar change and on the active-slot change, so the item is in hand right after.
		hotbar.SetBinding( 0, id );
		if ( !inventory.HostTryAddResource( id, 1 ) )
		{
			Log.Warning( $"[Hacks] equip {id} failed (hotbar slot 1 occupied or inventory full)." );
			return;
		}

		hotbar.SetActiveSlot( 0 );
		Log.Info( $"[Hacks] equipped {id} in hotbar slot 1." );
	}

	// --- Simulated attack press (swing) -------------------------------------------------------

	static double _simulatedAttackUntilGlobal;

	/// <summary>
	/// Usage: <c>swing [holdSeconds]</c> — holds the primary attack action for <c>holdSeconds</c>
	/// (default 0.15 s = light attack) and releases it, exactly as a mouse press would, so the
	/// windup → swing can be filmed from the editor, which cannot press keys.
	/// <see cref="PlayerCombat"/> calls <see cref="ApplySimulatedInput"/> at the top of its owner
	/// input tick; <c>Input.SetAction</c> only lasts the frame it is set in.
	/// </summary>
	[ConCmd( "swing" )]
	public static void ConCmdSwing( float holdSeconds = 0.15f )
	{
		_simulatedAttackUntilGlobal = RealTime.GlobalNow + Math.Clamp( holdSeconds, 0.02f, 3f );
		Log.Info( $"[Hacks] swing: holding attack for {holdSeconds:0.##}s." );
	}

	/// <summary>Owner input tick hook: keeps the simulated attack action down until its time is up.</summary>
	public static void ApplySimulatedInput( string primaryAttackAction )
	{
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
		sb.AppendLine( "  status <id> <seconds>  apply a buff / debuff (status clear)" );
		sb.AppendLine( "  spawnPatrolBot  host: drop a robot patrol unit in front of you" );
		sb.AppendLine( "  give <resourceId> [amount]  host: put an item in your hotbar / inventory" );
		sb.AppendLine( "  equip <resourceId>  host: give one into hotbar slot 1 and hold it" );
		sb.AppendLine( "  swing [holdSeconds]  press + release the primary attack (0.15 = light)" );
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
