#nullable disable
using Sandbox;

namespace Survival;

/// <summary>
/// RPC / logging helpers: <see cref="Connection"/> reference equality can miss the same logical client; <see cref="SameClient"/> uses <see cref="Connection.Id"/> only.
/// Do <b>not</b> match on <see cref="Connection.SteamId"/> for auth — two connections (two clients / tabs) can share one Steam account and must stay distinct.
/// </summary>
public static class ConnectionIdentity
{
	static bool HasSteam( SteamId id ) => id.ValueUnsigned != 0UL;

	public static bool SameClient( Connection a, Connection b )
	{
		if ( a is null || b is null )
			return false;

		if ( ReferenceEquals( a, b ) )
			return true;

		return a.Id == b.Id;
	}

	public static string Format( Connection c )
	{
		if ( c is null )
			return "—";

		try
		{
			var label = string.IsNullOrWhiteSpace( c.DisplayName ) ? c.Name : c.DisplayName;
			if ( HasSteam( c.SteamId ) )
				return $"{label} ({c.SteamId})";

			return string.IsNullOrWhiteSpace( label ) ? "—" : label;
		}
		catch
		{
			return "—";
		}
	}
}
