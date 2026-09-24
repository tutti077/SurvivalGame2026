using Sandbox;

namespace Survival;

/// <summary>
/// What a circuit-enabled object is, for the wire stripper's sphere colour. Set on the
/// <see cref="CircuitNode"/> authored on the prefab; the catalog only says whether a piece may join a circuit.
/// </summary>
public enum CircuitNodeKind : byte
{
	/// <summary>Lever / button / pressure plate — pushes its own state down its wires.</summary>
	Switch = 0,
	/// <summary>Electric light — powered = lit.</summary>
	Light = 1,
	/// <summary>Door / draw bridge — powered = open.</summary>
	Door = 2,
	/// <summary>Traps and other hazards — powered = armed.</summary>
	Trap = 3,
	/// <summary>Logic gates, timers, relays, pulsers.</summary>
	Gate = 4,
	/// <summary>Daylight / entity sensors.</summary>
	Sensor = 5,
	/// <summary>Generator — the power source.</summary>
	Power = 6,
}

public static class CircuitNodeKindExtensions
{
	/// <summary>Sphere tint per kind: doors teal, traps red, gates lime (per Mark), the rest picked to stay apart.</summary>
	public static Color SphereColor( this CircuitNodeKind kind ) => kind switch
	{
		CircuitNodeKind.Switch => new Color( 0.98f, 0.85f, 0.15f ),
		CircuitNodeKind.Light => new Color( 0.55f, 0.75f, 1f ),
		CircuitNodeKind.Door => new Color( 0.1f, 0.8f, 0.8f ),
		CircuitNodeKind.Trap => new Color( 0.92f, 0.2f, 0.16f ),
		CircuitNodeKind.Gate => new Color( 0.55f, 0.95f, 0.2f ),
		CircuitNodeKind.Sensor => new Color( 0.7f, 0.35f, 0.95f ),
		CircuitNodeKind.Power => Color.White,
		_ => Color.White,
	};
}
