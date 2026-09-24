namespace Survival;

/// <summary>
/// Behaviour behind a <see cref="CircuitNode"/> — the lever, light, door, gate component on the
/// same object. The host asks it what the node pushes down its wires; the device reads
/// <see cref="CircuitNode.Powered"/> itself (every machine, it is synced) to drive visuals.
/// </summary>
public interface ICircuitDevice
{
	/// <summary>
	/// The node's own state given whether its circuit is live. Sources (lever, sensor) ignore
	/// <paramref name="powered"/> and return their own state — the registry asks them with
	/// <c>false</c> to find out whether they make the circuit live; sinks return the input.
	/// </summary>
	bool ComputeOutput( bool powered );
}
