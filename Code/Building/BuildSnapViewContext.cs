using Sandbox;

namespace Survival;

/// <summary>Camera view ray passed into snap collection.</summary>
public readonly struct BuildSnapViewContext
{
	public Vector3 RayOrigin { get; init; }
	public Vector3 RayDirection { get; init; }
	public float MaxRange { get; init; }
	public Vector3 CrosshairPoint { get; init; }
	public bool HasCrosshairFocus { get; init; }
}
