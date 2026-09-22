using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// One enemy camp layout from <c>data/enemy_camps.json</c>: the build pieces that make up the
/// camp and the entities that guard it, both as camp-local offsets.
/// </summary>
public sealed class EnemyCampData
{
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	/// <summary>Inner ring, centered on the camp origin: guards wander within this distance of it (camp-local meters).</summary>
	public float WanderDistanceMeters { get; set; } = 24f;
	/// <summary>Outer ring, centered on the camp origin: an alerted guard pulled past it drops its target and runs back (camp-local meters).</summary>
	public float MaxTravelDistanceMeters { get; set; } = 80f;
	public List<EnemyCampPieceData> Pieces { get; set; } = new();
	public List<EnemyCampEntityData> Entities { get; set; } = new();
}

/// <summary>A build piece in a camp layout. Offsets are camp-local meters (see <see cref="EnemyCampLayout"/>).</summary>
public sealed class EnemyCampPieceData
{
	public string PieceId { get; set; } = string.Empty;
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	/// <summary>Yaw relative to the camp facing.</summary>
	public float YawDegrees { get; set; }
	/// <summary>True = placed as an unbuilt blueprint (ghost) instead of a finished piece.</summary>
	public bool Blueprint { get; set; }
}

/// <summary>An entity in a camp layout. Offsets are camp-local meters (see <see cref="EnemyCampLayout"/>).</summary>
public sealed class EnemyCampEntityData
{
	/// <summary>Archetype name from <see cref="EnemyType"/> ("Scav", "Boneback", "Howler").</summary>
	public string Archetype { get; set; } = "Scav";
	public int Tier { get; set; } = 1;
	/// <summary>Entity prefab to clone (the archetype only tunes components, it does not pick the model).</summary>
	public string Prefab { get; set; } = "prefabs/entity/scavT1.prefab";
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	/// <summary>Yaw relative to the camp facing.</summary>
	public float YawDegrees { get; set; }
	/// <summary>Override max HP (0 = archetype default).</summary>
	public float Health { get; set; }
	/// <summary>Per-guard override of the camp wander distance, still measured from the camp origin (camp-local meters, 0 = camp default).</summary>
	public float WanderDistanceMeters { get; set; }
	/// <summary>Per-guard override of the camp max travel distance, still measured from the camp origin (camp-local meters, 0 = camp default).</summary>
	public float MaxTravelDistanceMeters { get; set; }

	public EnemyType ResolveEnemyType() =>
		!string.IsNullOrWhiteSpace( Archetype ) && Enum.TryParse( Archetype.Trim(), ignoreCase: true, out EnemyType parsed )
			? parsed
			: EnemyType.Scav;
}

/// <summary>
/// Camp-local → world conversion. Camp layouts are written in the build kit's meters (50 u/m,
/// <see cref="BuildColliderSnap.PrefabColliderSize"/>) so entity offsets line up with piece
/// footprints — a 2 m floor is exactly one tile. Convert once here; never pre-scale in JSON.
/// </summary>
public static class EnemyCampLayout
{
	public static float UnitsPerMeter => BuildColliderSnap.PrefabColliderSize.x;

	/// <summary>Camp-local meters → world offset (units) rotated into the camp facing.</summary>
	public static Vector3 ToWorldOffset( float xMeters, float yMeters, float zMeters, Rotation campRotation ) =>
		campRotation * new Vector3( xMeters * UnitsPerMeter, yMeters * UnitsPerMeter, zMeters * UnitsPerMeter );
}
