namespace Survival;

/// <summary>
/// Which foot traps can hold a creature. A trap catches a victim whose size sits inside the
/// trap's [<see cref="BearTrap.MinCatchSize"/>, <see cref="BearTrap.MaxCatchSize"/>] band.
/// Players count as <see cref="Small"/>. Animals get theirs from <c>data/animal_behaviors.json</c>
/// (<c>trapSize</c>), enemies from <see cref="EntityArchetype"/>.
/// </summary>
public enum TrapSize
{
	/// <summary>Never trapped — birds, fish, anything that does not put a foot down.</summary>
	None = 0,
	/// <summary>Most animals and biped creatures.</summary>
	Small = 1,
	/// <summary>Bears, the dunewyrm, the heavier bots.</summary>
	Large = 2
}
