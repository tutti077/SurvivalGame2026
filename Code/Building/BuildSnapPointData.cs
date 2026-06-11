namespace Survival;

public sealed class BuildSnapPointData
{
	public BuildSnapRole Role { get; set; } = BuildSnapRole.Unknown;
	public string LocalPosition { get; set; } = "0,0,0";
	public string LocalRotation { get; set; } = "0,0,0,1";
}
