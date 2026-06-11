using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

static class BuildSnapParse
{
	public static Vector3 ParseVector3( string value, Vector3 fallback = default )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return fallback;

		var parts = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 3 )
			return fallback;

		if ( !float.TryParse( parts[0], out var x ) || !float.TryParse( parts[1], out var y ) || !float.TryParse( parts[2], out var z ) )
			return fallback;

		return new Vector3( x, y, z );
	}

	public static Rotation ParseRotation( string value, Rotation fallback = default )
	{
		if ( string.IsNullOrWhiteSpace( value ) )
			return fallback;

		var parts = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length < 4 )
			return fallback;

		if ( !float.TryParse( parts[0], out var x ) || !float.TryParse( parts[1], out var y )
		     || !float.TryParse( parts[2], out var z ) || !float.TryParse( parts[3], out var w ) )
			return fallback;

		return new Rotation( x, y, z, w );
	}

	public static BuildSnapPoint FromData( BuildSnapPointData data )
	{
		if ( data is null )
			return default;

		return new BuildSnapPoint(
			data.Role,
			BuildSnapParse.ParseVector3( data.LocalPosition ),
			BuildSnapParse.ParseRotation( data.LocalRotation ) );
	}
}
