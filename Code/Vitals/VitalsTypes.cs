using System;

namespace Survival;

/// <summary>Authoritative vitals snapshot (host <see cref="VitalsAuthority"/> → owner <see cref="PlayerVitals"/>).</summary>
public readonly record struct VitalsSnapshot(
	float Health,
	float HealthMax,
	float Stamina,
	float StaminaMax );
