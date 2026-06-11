using Sandbox;

namespace Survival;

/// <summary>Runtime snap point on a build piece (catalog, prefab child, or default).</summary>
public readonly struct BuildSnapPoint
{
	public BuildSnapRole Role { get; init; }
	/// <summary>Offset on the 1 m dev box (±0.5 wide, 0 on thin-center plane) before piece scale.</summary>
	public Vector3 LocalPosition { get; init; }
	public Rotation LocalRotation { get; init; }

	public BuildSnapPoint( BuildSnapRole role, Vector3 localPosition, Rotation localRotation )
	{
		Role = role;
		LocalPosition = localPosition;
		LocalRotation = localRotation;
	}
}
