namespace Survival;

/// <summary>Player-made noises that can alert entities (ranges are per-entity in perception JSON).</summary>
public enum EntityNoiseKind : byte
{
	Run = 0,
	ChopTree = 1,
	Swing = 2,
	Footstep = 3,
	SneakFootstep = 4,
}
