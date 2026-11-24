using System;
using System.Collections.Generic;

namespace Survival;

/// <summary>
/// Host-only helper: deadline instant (same clock as <see cref="RealTime.GlobalNow"/> at <see cref="VitalsAuthority"/>) before a pool may receive positive regen ticks.
/// Clients never touch this; they mirror numbers via <see cref="PlayerVitals"/> RPC sync.
/// </summary>
internal abstract class HostPoolRegenDelayGateBase
{
	protected readonly Dictionary<Guid, double> NotBeforeUtc = new();

	public void Clear( Guid id ) => NotBeforeUtc.Remove( id );

	/// <summary>Regen must not run until <paramref name="nowSeconds"/> + delay (restarts the window on each drain / hit).</summary>
	public void ArmNoRegenBefore( Guid id, double nowSeconds, float delaySeconds )
	{
		var d = Math.Max( 0.0, delaySeconds );
		NotBeforeUtc[id] = nowSeconds + d;
	}

	/// <summary>
	/// When no deadline exists and <paramref name="armFullDelayIfMissing"/>, arms from <paramref name="nowSeconds"/> so regen waits a full delay (synced / seeded pool below max).
	/// </summary>
	public bool MayRegenAfterDelay( Guid id, double nowSeconds, float delaySeconds, bool armFullDelayIfMissing )
	{
		var d = Math.Max( 0.0, delaySeconds );

		if ( !NotBeforeUtc.TryGetValue( id, out var notBefore ) )
		{
			if ( !armFullDelayIfMissing )
				return false;

			NotBeforeUtc[id] = nowSeconds + d;
			return false;
		}

		return nowSeconds + 1e-4 >= notBefore;
	}
}

internal sealed class StaminaRegenDelayGate : HostPoolRegenDelayGateBase
{
}

internal sealed class HealthRegenDelayGate : HostPoolRegenDelayGateBase
{
}
