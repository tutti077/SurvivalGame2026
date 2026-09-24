using System;
using System.Collections.Generic;
using System.Text;

namespace Survival;

/// <summary>One wire between this node and <see cref="TargetId"/> (two-way), drawn in <see cref="Cable"/>.</summary>
public readonly struct CircuitLink
{
	public readonly Guid TargetId;
	public readonly CircuitCable Cable;

	public CircuitLink( Guid targetId, CircuitCable cable )
	{
		TargetId = targetId;
		Cable = cable;
	}

	/// <summary>
	/// Links travel as one <c>[Sync]</c> string on the source node (<c>guid:cable;guid:cable</c>) —
	/// a plain string syncs on every build without leaning on a networked collection type.
	/// </summary>
	public static string Encode( IReadOnlyList<CircuitLink> links )
	{
		if ( links is null || links.Count == 0 )
			return string.Empty;

		var sb = new StringBuilder( links.Count * 36 );
		for ( var i = 0; i < links.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( ';' );
			sb.Append( links[i].TargetId.ToString( "N" ) ).Append( ':' ).Append( (int)links[i].Cable );
		}

		return sb.ToString();
	}

	public static void Decode( string encoded, List<CircuitLink> into )
	{
		into.Clear();
		if ( string.IsNullOrWhiteSpace( encoded ) )
			return;

		foreach ( var part in encoded.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var colon = part.IndexOf( ':' );
			if ( colon <= 0 )
				continue;

			if ( !Guid.TryParseExact( part.Substring( 0, colon ), "N", out var id ) )
				continue;

			var cable = int.TryParse( part.Substring( colon + 1 ), out var c )
				? (CircuitCable)Math.Clamp( c, 0, CircuitCableExtensions.Count - 1 )
				: CircuitCable.Red;
			into.Add( new CircuitLink( id, cable ) );
		}
	}
}
