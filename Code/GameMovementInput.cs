using Sandbox;

namespace Game;

/// <summary>
/// Shared movement input helpers (Title Case vs lowercase action names from scenes vs Input.config).
/// </summary>
public static class GameMovementInput
{
	public static bool AnyMoveKeyDown()
		=> InputDownFlexible( "Forward" )
		   || InputDownFlexible( "Backward" )
		   || InputDownFlexible( "Left" )
		   || InputDownFlexible( "Right" );

	/// <summary>Gamepad / stick intent without using grapple move-mode wish (it can include non-WASD noise).</summary>
	public static bool StrongAnalogMove( float minLength = 0.42f )
	{
		var a = Input.AnalogMove;
		return new Vector2( a.x, a.y ).Length >= minLength;
	}

	public static bool InputDownFlexible( string action )
	{
		if ( string.IsNullOrEmpty( action ) )
			return false;

		if ( Input.Down( action ) )
			return true;

		if ( action.Length == 1 )
		{
			var c = action[0];
			var other = char.IsUpper( c ) ? char.ToLowerInvariant( c ) : char.ToUpperInvariant( c );
			return Input.Down( other.ToString() );
		}

		var head = action[0];
		var toggledHead = char.IsUpper( head ) ? char.ToLowerInvariant( head ) : char.ToUpperInvariant( head );
		return Input.Down( toggledHead + action.Substring( 1 ) );
	}
}
