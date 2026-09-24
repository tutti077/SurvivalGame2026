namespace Survival;

/// <summary>
/// Behaviour behind a <see cref="CircuitNode"/> — the lever, light, door, gate component on the
/// same object. The host asks it what the node pushes down its wires; the device reads
/// <see cref="CircuitNode.Powered"/> itself (every machine, it is synced) to drive visuals.
/// </summary>
public interface ICircuitDevice
{
	/// <summary>
	/// Output for the node's outgoing wires given its wired input. Sources (lever, sensor) ignore
	/// <paramref name="powered"/> and return their own state; sinks pass it through so a chain of
	/// lights lights up from one lever.
	/// </summary>
	bool ComputeOutput( bool powered );
}
