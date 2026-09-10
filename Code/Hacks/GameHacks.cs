using System;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>
/// Player-toggled dev hacks, driven from the console. Flags are local to this client; systems
/// that need host validation to honour a flag mirror it onto the pawn (e.g.
/// <see cref="PlayerCrafting.AllCraftingHack"/>) so the host reads the owner's setting.
/// <para>
/// Console: <c>allCrafting true</c> / <c>allCrafting false</c> — <c>hacks</c> lists every flag.
/// </para>
/// </summary>
public static class GameHacks
{
	/// <summary>
	/// Personal crafting menu lists every recipe (locked, workbench-only, station-gated) and crafting
	/// consumes nothing. Defaults on while the crafting content is being built out.
	/// </summary>
	public static bool AllCrafting { get; private set; } = true;

	/// <summary>Bumps whenever any flag changes — UI that caches a hack-dependent layout rebuilds on this.</summary>
	public static int Version { get; private set; }

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
		sb.AppendLine( "  status <id> <seconds>  apply a buff / debuff (status clear)" );
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
