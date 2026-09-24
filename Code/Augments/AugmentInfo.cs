using System.Collections.Generic;
using System.Text;

namespace Survival;

/// <summary>Kind of a line in the shared augment info block (drives colour in the station box and the tooltip).</summary>
public enum AugmentInfoLineKind
{
	Description = 0,
	Slot = 1,
	Cost = 2,
	Stat = 3,
	/// <summary>The gold-coin price the Augment button charges — the station renders it as its own coin row.</summary>
	InstallCost = 4,
	/// <summary>How it is activated (passive / key bind / F wheel, one-shot or toggle, cooldown, battery).</summary>
	Activation = 5,
	/// <summary>Effect not built yet.</summary>
	Warning = 6,
}

/// <summary>
/// The one "info about augment" block: description, tree, which sockets it fits, tier, how it is
/// activated, install cost and craft cost. The station detail box and the item hover tooltip both
/// render exactly these lines, so the player reads the same facts wherever an augment shows up.
/// </summary>
public static class AugmentInfo
{
	public static List<(string Text, AugmentInfoLineKind Kind)> BuildLines( AugmentDefinition def )
	{
		var lines = new List<(string, AugmentInfoLineKind)>();
		if ( def is null )
			return lines;

		if ( !string.IsNullOrWhiteSpace( def.Description ) )
			lines.Add( (def.Description, AugmentInfoLineKind.Description) );

		if ( !def.Implemented )
			lines.Add( ("Effect not yet implemented — crafts and installs, does nothing yet.", AugmentInfoLineKind.Warning) );

		var tree = DescribeTree( def );
		if ( !string.IsNullOrWhiteSpace( tree ) )
			lines.Add( (tree, AugmentInfoLineKind.Slot) );

		lines.Add( ($"Fits: {DescribeSlots( def )}", AugmentInfoLineKind.Slot) );
		lines.Add( (DescribeActivation( def ), AugmentInfoLineKind.Activation) );
		lines.Add( ($"Augment cost: {DescribeInstallCost( def )}", AugmentInfoLineKind.InstallCost) );
		lines.Add( ($"Craft cost: {DescribeCraftCost( def )}", AugmentInfoLineKind.Cost) );

		if ( def.Stats is not null )
		{
			for ( var i = 0; i < def.Stats.Count; i++ )
			{
				var stat = def.Stats[i];
				if ( stat is null || string.IsNullOrWhiteSpace( stat.Label ) )
					continue;

				lines.Add( ($"{stat.Label}: {stat.Value}", AugmentInfoLineKind.Stat) );
			}
		}

		return lines;
	}

	/// <summary>"Cyber · Combat", "Movement" …</summary>
	public static string DescribeTree( AugmentDefinition def )
	{
		var school = def?.School?.Trim() ?? string.Empty;
		var category = def?.Category?.Trim() ?? string.Empty;
		if ( string.IsNullOrWhiteSpace( school ) )
			return category;
		if ( string.IsNullOrWhiteSpace( category ) )
			return school;
		return $"{school} · {category}";
	}

	/// <summary>"Trigger — key bind 1–6 · one-shot · 10 s cooldown", "Wheel — hold C · toggle · 20 s battery", "Passive".</summary>
	public static string DescribeActivation( AugmentDefinition def )
	{
		if ( def is null )
			return string.Empty;

		var sb = new StringBuilder();
		switch ( def.ResolvedActivation )
		{
			case AugmentActivation.Trigger:
				sb.Append( "Trigger — assign to a key 1–6 on the Augments page" );
				break;
			case AugmentActivation.Wheel:
				sb.Append( "Wheel — hold C and pick it" );
				break;
			default:
				sb.Append( "Passive" );
				if ( def.CooldownSeconds > 0f && def.CooldownSeconds != AugmentDefinition.DefaultCooldownSeconds )
					sb.Append( $" · once every {def.CooldownSeconds:0.#} s" );
				return sb.ToString();
		}

		sb.Append( def.ResolvedMode == AugmentMode.Toggle ? " · toggle" : " · one-shot" );
		if ( def.HasBattery )
			sb.Append( $" · {def.BatterySeconds:0.#} s battery" );
		if ( def.ResolvedCooldownSeconds > 0f )
			sb.Append( $" · {def.ResolvedCooldownSeconds:0.#} s cooldown" );

		return sb.ToString();
	}

	public static string DescribeSlots( AugmentDefinition def )
	{
		var allowed = def?.AllowedSlots;
		if ( allowed is null || allowed.Count == 0 )
			return "no socket";

		var sb = new StringBuilder();
		for ( var i = 0; i < allowed.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( ", " );
			sb.Append( AugmentSlots.Label( allowed[i] ) );
		}

		return sb.ToString();
	}

	/// <summary>"150 gold" — or one figure per socket when the augment fits several (the price belongs to the socket).</summary>
	public static string DescribeInstallCost( AugmentDefinition def )
	{
		// freeAugments hack: the Augment button charges nothing, so say so everywhere the price shows.
		if ( GameHacks.FreeAugments )
			return "free (freeAugments hack)";

		var allowed = def?.AllowedSlots;
		if ( allowed is null || allowed.Count == 0 )
			return "—";

		var sb = new StringBuilder();
		for ( var i = 0; i < allowed.Count; i++ )
		{
			if ( sb.Length > 0 )
				sb.Append( " · " );

			var cost = AugmentBodyParts.InstallGoldCost( allowed[i] );
			sb.Append( allowed.Count > 1
				? $"{AugmentSlots.Label( allowed[i] )} {cost} gold"
				: $"{cost} gold" );
		}

		return sb.ToString();
	}

	public static string DescribeCraftCost( AugmentDefinition def )
	{
		if ( GameHacks.AllCrafting )
			return "free (allCrafting hack)";

		if ( def?.Ingredients is null || def.Ingredients.Count == 0 )
			return "free";

		var sb = new StringBuilder();
		for ( var i = 0; i < def.Ingredients.Count; i++ )
		{
			var ing = def.Ingredients[i];
			if ( ing is null || string.IsNullOrWhiteSpace( ing.ResourceId ) )
				continue;

			if ( sb.Length > 0 )
				sb.Append( ", " );
			sb.Append( ResourceCatalog.Resolve( ing.ResourceId ).DisplayName ).Append( " ×" ).Append( ing.Amount );
		}

		return sb.Length > 0 ? sb.ToString() : "free";
	}
}
