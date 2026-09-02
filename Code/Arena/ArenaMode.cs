namespace Survival;

/// <summary>Arena battle formats: N players per team, 2 or 3 teams.</summary>
public enum ArenaMode
{
	OneVOne,
	TwoVTwo,
	ThreeVThree,
	FourVFour,
	TwoVTwoVTwo,
	ThreeVThreeVThree,
	FourVFourVFour,
}

public static class ArenaModeInfo
{
	public static readonly ArenaMode[] All =
	{
		ArenaMode.OneVOne,
		ArenaMode.TwoVTwo,
		ArenaMode.ThreeVThree,
		ArenaMode.FourVFour,
		ArenaMode.TwoVTwoVTwo,
		ArenaMode.ThreeVThreeVThree,
		ArenaMode.FourVFourVFour,
	};

	public static int TeamSize( ArenaMode mode ) => mode switch
	{
		ArenaMode.OneVOne => 1,
		ArenaMode.TwoVTwo or ArenaMode.TwoVTwoVTwo => 2,
		ArenaMode.ThreeVThree or ArenaMode.ThreeVThreeVThree => 3,
		_ => 4,
	};

	public static int TeamCount( ArenaMode mode ) => mode switch
	{
		ArenaMode.TwoVTwoVTwo or ArenaMode.ThreeVThreeVThree or ArenaMode.FourVFourVFour => 3,
		_ => 2,
	};

	public static string Display( ArenaMode mode ) => mode switch
	{
		ArenaMode.OneVOne => "1v1",
		ArenaMode.TwoVTwo => "2v2",
		ArenaMode.ThreeVThree => "3v3",
		ArenaMode.FourVFour => "4v4",
		ArenaMode.TwoVTwoVTwo => "2v2v2",
		ArenaMode.ThreeVThreeVThree => "3v3v3",
		_ => "4v4v4",
	};
}
