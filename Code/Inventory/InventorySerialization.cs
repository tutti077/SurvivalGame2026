using System.Text;

namespace Game;

public static class InventorySerialization
{
	public const char SlotSeparator = '|';
	public const char IdCountSeparator = '^';

	public static string Serialize( InvSlot[] slots )
	{
		if ( slots is null || slots.Length == 0 )
			return "";

		var sb = new StringBuilder( slots.Length * 8 );
		for ( var i = 0; i < slots.Length; i++ )
		{
			if ( i > 0 )
				sb.Append( SlotSeparator );

			var s = slots[i];
			if ( s.IsEmpty )
				sb.Append( '_' );
			else
				sb.Append( s.ItemId ).Append( IdCountSeparator ).Append( s.Count );
		}

		return sb.ToString();
	}

	public static void ParseInto( string blob, InvSlot[] slots )
	{
		if ( slots is null || slots.Length == 0 )
			return;

		for ( var i = 0; i < slots.Length; i++ )
			slots[i] = InvSlot.Empty;

		if ( string.IsNullOrEmpty( blob ) )
			return;

		var parts = blob.Split( SlotSeparator );
		for ( var i = 0; i < parts.Length && i < slots.Length; i++ )
		{
			var p = parts[i];
			if ( string.IsNullOrEmpty( p ) || p == "_" )
				continue;

			var sep = p.IndexOf( IdCountSeparator );
			if ( sep <= 0 || sep >= p.Length - 1 )
				continue;

			var id = p.Substring( 0, sep );
			if ( !int.TryParse( p.AsSpan( sep + 1 ), out var c ) || c <= 0 )
				continue;

			slots[i] = InvSlot.Of( id, c );
		}
	}
}
