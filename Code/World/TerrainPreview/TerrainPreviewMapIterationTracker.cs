namespace Survival;

/// <summary>
/// User abort flag for an editor generate run. The former session / iteration / seed-attempt counters had
/// no caller (auto-tune was never started), so they were removed 2026-10-05.
/// </summary>
public static class TerrainPreviewMapIterationTracker
{
	static bool _userAbortRequested;

	public static bool UserAbortRequested => _userAbortRequested;
	public static bool IsAbortRequested => _userAbortRequested;

	public static void RequestUserAbort() => _userAbortRequested = true;
	public static void ClearUserAbort() => _userAbortRequested = false;
}
