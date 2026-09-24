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
}

/// <summary>
/// The one "info about augment" block: description, which sockets it fits, tier, install cost and
/// craft cost. The station detail box and the item hover tooltip both render exactly these lines,
/// so the player reads the same facts wherever an augment shows up.
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

		lines.Add( ($"Fits: {DescribeSlots( def )}", AugmentInfoLineKind.Slot) );
		lines.Add( ($"Tier {def.ResolvedTier} augment", AugmentInfoLineKind.Slot) );
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

	/// <summary>"150 gold" — or one figure per body part when the augment fits parts with different rates.</summary>
	public static string DescribeInstallCost( AugmentDefinition def )
	{
		var allowed = def?.AllowedSlots;
		if ( allowed is null || allowed.Count == 0 )
			return "—";

		var seenParts = new List<AugmentBodyPart>();
		var sb = new StringBuilder();
		for ( var i = 0; i < allowed.Count; i++ )
		{
			var part = AugmentSlots.PartOf( allowed[i] );
			if ( seenParts.Contains( part ) )
				continue;

			seenParts.Add( part );
			if ( sb.Length > 0 )
				sb.Append( " · " );

			var cost = def.InstallGoldCost( allowed[i] );
			sb.Append( seenParts.Count > 1 || allowed.Count > 1
				? $"{AugmentBodyParts.Label( part )} {cost} gold"
				: $"{cost} gold" );
		}

		return sb.ToString();
	}

	public static string DescribeCraftCost( AugmentDefinition def )
	{
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
