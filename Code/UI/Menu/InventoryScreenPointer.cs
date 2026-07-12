using Sandbox;

namespace Survival;

/// <summary>Screen pointer helpers aligned with the combat teardrop crosshair (<see cref="PlayerCombat"/> viewport).</summary>
public static class InventoryScreenPointer
{
	static bool _softCursorActive;
	static Vector2 _softCursorScreen;

	/// <summary>True while the inventory soft cursor is driving menu hit-tests.</summary>
	public static bool SoftCursorActive => _softCursorActive;

	/// <summary>Menu soft-cursor screen position (valid when <see cref="SoftCursorActive"/>).</summary>
	public static Vector2 SoftCursorScreen => _softCursorScreen;

	public static void SetSoftCursor( Vector2 screenPosition, bool active )
	{
		_softCursorActive = active;
		_softCursorScreen = screenPosition;
	}

	/// <summary>Soft cursor while the menu is open; otherwise OS <see cref="Mouse.Position"/>.</summary>
	public static Vector2 GetMenuOrMousePosition() =>
		_softCursorActive ? _softCursorScreen : Mouse.Position;

	public static Vector2 GetCrosshairScreenPosition( GameObject from )
	{
		if ( TryGetViewScreenRect( from, out var left, out var top, out var right, out var bottom ) )
			return new Vector2( (left + right) * 0.5f, (top + bottom) * 0.5f );

		var size = Screen.Size;
		return size * 0.5f;
	}

	public static void CenterMouseOnCrosshair( GameObject from )
	{
		if ( !TryGetViewScreenRect( from, out var left, out var top, out var right, out var bottom ) )
			return;

		Mouse.Position = new Vector2( (left + right) * 0.5f, (top + bottom) * 0.5f );
	}

	public static void ClampMouseToView( GameObject from )
	{
		var current = Mouse.Position;
		var clamped = ClampToView( current, from );
		// Only write when outside the view — rewriting every frame fights the OS cursor and feels like a snap-back.
		if ( ( clamped - current ).LengthSquared < 0.25f )
			return;

		Mouse.Position = clamped;
	}

	public static Vector2 ClampToView( Vector2 screenPosition, GameObject from )
	{
		if ( !TryGetViewScreenRect( from, out var left, out var top, out var right, out var bottom ) )
			return screenPosition;

		return new Vector2(
			screenPosition.x.Clamp( left, right ),
			screenPosition.y.Clamp( top, bottom ) );
	}

	public static bool TryGetViewScreenRect( GameObject from, out float left, out float top, out float right, out float bottom )
	{
		left = top = right = bottom = 0f;

		if ( TryResolveViewCamera( from, out var cam ) && cam.IsValid() )
		{
			var rect = cam.ScreenRect;
			left = rect.Left;
			top = rect.Top;
			right = rect.Right;
			bottom = rect.Bottom;
			return right > left && bottom > top;
		}

		var size = Screen.Size;
		if ( size.x <= 0f || size.y <= 0f )
			return false;

		left = 0f;
		top = 0f;
		right = size.x - 1f;
		bottom = size.y - 1f;
		return true;
	}

	static bool TryResolveViewCamera( GameObject from, out CameraComponent cam )
	{
		cam = default;
		if ( !from.IsValid() )
			return false;

		for ( var go = from; go.IsValid(); go = go.Parent )
		{
			if ( TryFindFirstCameraInHierarchy( go, out cam ) && cam.IsValid() )
				return true;

			var pc = go.Components.Get<PlayerController>();
			if ( pc is null )
				continue;

			var embedded = pc.Components.Get<CameraComponent>();
			if ( embedded.IsValid() )
			{
				cam = embedded;
				return true;
			}
		}

		var sceneCam = from.Scene?.Camera;
		if ( sceneCam is not null && sceneCam.IsValid() )
		{
			cam = sceneCam;
			return true;
		}

		return false;
	}

	static bool TryFindFirstCameraInHierarchy( GameObject go, out CameraComponent found )
	{
		found = default;
		if ( !go.IsValid() )
			return false;

		var self = go.Components.Get<CameraComponent>();
		if ( self.IsValid() )
		{
			found = self;
			return true;
		}

		foreach ( var ch in go.Children )
		{
			if ( TryFindFirstCameraInHierarchy( ch, out found ) )
				return true;
		}

		return false;
	}
}
