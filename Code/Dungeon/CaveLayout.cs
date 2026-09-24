using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Per-seed personality of a cave: how its chain of drops, tunnels, chimneys and caverns is strung together.</summary>
public enum CaveArchetype
{
	/// <summary>Mostly one long drifting, pinching shaft with few junctions — the closest to a single well.</summary>
	Shaft,
	/// <summary>Short drops stepping sideways through tunnels, like a staircase of wells.</summary>
	Stairs,
	/// <summary>Drops that lean hard, tunnels that turn back on themselves: a snake going down.</summary>
	Serpent,
	/// <summary>Frequent chimneys the player has to climb before the next drop.</summary>
	Chimneys,
	/// <summary>Big caverns strung between short drops.</summary>
	Caverns,
}

public enum CaveLedgeKind
{
	Slab,
	Jut,
	Shelf,
	Spur,
	Boulder,
	/// <summary>The wide doorstep slab under a side-room mouth.</summary>
	Doorstep,
}

public enum CavePassageKind
{
	/// <summary>The cave itself: the chain every other passage hangs off.</summary>
	Trunk,
	/// <summary>A side room (possibly after a short climb or drop) that ends in a dome.</summary>
	DeadEnd,
	/// <summary>Leaves a junction and rejoins the trunk further down the chain.</summary>
	Loop,
}

public enum CaveLegKind
{
	/// <summary>Tunnel or hall: horizontal, flat floor.</summary>
	Walk,
	/// <summary>Chimney: the route climbs it.</summary>
	Climb,
	/// <summary>Shaft: the route descends it.</summary>
	Drop,
}

/// <summary>Designer inputs for <see cref="CaveLayout.Generate"/>, all in meters.</summary>
public sealed class CaveLayoutSettings
{
	/// <summary>Net vertical drop from the rim to the chamber floor.</summary>
	public float DepthMeters = 3000f;
	public float TopRadiusMeters = 25f;
	public float MinRadiusMeters = 3f;
	public float MaxRadiusMeters = 45f;
	public float ChamberRadiusMeters = 40f;
	public float WallNoiseMeters = 1.2f;
	public float PlatformHalfMeters = 45f;
	/// <summary>-1 = pick an archetype from the seed; otherwise a <see cref="CaveArchetype"/> index.</summary>
	public int Archetype = -1;

	public float TrunkRingMeters = 4f;
	public int TrunkSegments = 48;
	public float PassageRingMeters = 2.5f;
	public int PassageSegments = 28;

	/// <summary>Dead-end rooms hung off the drops (on the route, with a doorstep). Few: the cave is mainly one path.</summary>
	public int SideRoomCount = 2;
	/// <summary>Drops whose bottom is a junction hall where decoys and a loop fork off.</summary>
	public int JunctionCount = 1;
	/// <summary>Decoy dead-end mouths in a junction hall besides the way on.</summary>
	public int JunctionDecoysMin = 1;
	public int JunctionDecoysMax = 2;
	/// <summary>Loops that leave a junction and rejoin the trunk further on.</summary>
	public int LoopCount = 1;
	public float RoomPondChance = 0.5f;
	public float JunctionPondChance = 0.4f;

	public float JumpGapMinMeters = 3f;
	public float JumpGapMaxMeters = 5f;
	public float JumpDropMinMeters = 3f;
	public float JumpDropMaxMeters = 7f;
	public float GrappleGapMinMeters = 8f;
	public float GrappleGapMaxMeters = 15f;
	public float GrappleDropMinMeters = 8f;
	public float GrappleDropMaxMeters = 18f;
	public float GrappleHopChance = 0.35f;
	public float ClimbRiseMinMeters = 5f;
	public float ClimbRiseMaxMeters = 10f;
	public float ClimbGapMinMeters = 2f;
	public float ClimbGapMaxMeters = 7f;
	public float FillerFraction = 0.4f;
	public int LightEveryLedges = 6;
}

/// <summary>A ledge on a tube wall, in generator-local meters.</summary>
public sealed class CaveLedge
{
	public int Index;
	/// <summary>Position on the main route, or -1 for a filler / branch ledge.</summary>
	public int RouteIndex = -1;
	public CaveLedgeKind Kind;
	/// <summary>Point on the rock surface the ledge grows out of.</summary>
	public Vector3 AnchorMeters;
	/// <summary>Horizontal unit vector from the wall into the open air.</summary>
	public Vector3 IntoVoid;
	/// <summary>Z of the walkable top surface.</summary>
	public float ZMeters;
	public float WidthMeters;
	public float ProtrudeMeters;
	public float ThicknessMeters;
	public float TiltDegrees;
	public float RollDegrees;
	public bool GrappleHop;
	public bool Ascending;
	public bool HasLight;
	/// <summary>Passage this ledge sits in (0 = the trunk).</summary>
	public int Passage;
	/// <summary>Mouth this ledge is the doorstep of, or -1.</summary>
	public int Mouth = -1;
	/// <summary>Middle of the walkable top surface.</summary>
	public Vector3 LandingMeters;

	public bool IsRoute => RouteIndex >= 0;
}

/// <summary>An elliptical depression pressed into a floor (the pond of an oasis), generator-local meters.</summary>
public sealed class CaveBowl
{
	public Vector3 CenterMeters;
	public float RadiusAlong;
	public float RadiusAcross;
	public float YawRadians;
	public float DepthMeters;
	public Vector3 OpenDirection;
	public float ReachMeters = 9f;

	public float DepthAt( float x, float y )
	{
		var dx = x - CenterMeters.x;
		var dy = y - CenterMeters.y;
		var c = MathF.Cos( YawRadians );
		var s = MathF.Sin( YawRadians );
		var along = dx * c + dy * s;
		var across = -dx * s + dy * c;
		var q = (along * along) / (RadiusAlong * RadiusAlong) + (across * across) / (RadiusAcross * RadiusAcross);
		return q >= 1f ? 0f : DepthMeters * (1f - q);
	}
}

/// <summary>A hole patch in a host tube's ring / segment grid where another passage joins it.</summary>
public sealed class CaveMouth
{
	public int Index;
	/// <summary>Passage whose wall the hole is cut into (always the trunk today).</summary>
	public int Host;
	public int Passage;
	public bool IsExit;
	/// <summary>Hole patch: quads between rings <c>[RingStart, RingEnd)</c> (host sample indices) × segments <c>[SegStart, SegStart + SegCount)</c> (mod host segments).</summary>
	public int RingStart;
	public int RingEnd;
	public int SegStart;
	public int SegCount;
	/// <summary>Z of the floor you walk through the mouth on.</summary>
	public float FloorZ;
	/// <summary>Wall point at the patch centre.</summary>
	public Vector3 CenterMeters;
	/// <summary>Horizontal unit vector out of the host through the mouth.</summary>
	public Vector3 OutwardMeters;
	/// <summary>Distance along the outward direction past every hole-boundary vertex (where the tube can start).</summary>
	public float StartMeters;
	public float ThroatRadius;
	/// <summary>True when the hole sits on a vertical (drop) wall — angle-based; false on a tunnel side wall.</summary>
	public bool OnVerticalWall;
	/// <summary>Doorstep ledge index under this mouth, or -1 when the mouth opens onto a floor.</summary>
	public int Doorstep = -1;
}

/// <summary>One sample of a swept spine.</summary>
public sealed class CaveSpineSample
{
	public Vector3 Position;
	public Vector3 Tangent;
	public Vector3 U;
	public Vector3 V;
	public float Radius;
	public float S;
	/// <summary>Flat floor this ring is clamped to, or NaN.</summary>
	public float FloorZ = float.NaN;
	public int Leg;
}

/// <summary>A straight leg of a swept passage: sample range, kind, floor, and which run of consecutive legs it belongs to.</summary>
public sealed class CaveLeg
{
	public int Start;
	public int End;
	public CaveLegKind Kind;
	public float FloorZ = float.NaN;
	/// <summary>Consecutive drop / climb sub-legs share a group; the route treats a group as one tube.</summary>
	public int Group;
	/// <summary>Peak radius of a hall bulge (0 for plain legs).</summary>
	public float HallRadius;
	/// <summary>True for the junction hall at the bottom of a drop, where decoys and loops fork off.</summary>
	public bool IsJunction;
	public readonly List<int> BranchPassages = new();
}

/// <summary>A tube swept along a polyline: the trunk, a dead-end room or a loop.</summary>
public sealed class CavePassage
{
	public int Index;
	public CavePassageKind Kind;
	public int MouthA = -1;
	public int MouthB = -1;
	public readonly List<CaveSpineSample> Samples = new();
	public readonly List<CaveLeg> Legs = new();
	public float RoomRadius;
	public int Bowl = -1;
	public bool HasChest;
	public Vector3 ChestMeters;
	public float ChestYawDegrees;
	public readonly List<Vector3> LightsMeters = new();
	public float NoisePhase;
	public float RingMeters = 2.5f;
	public int Segments = 28;

	public bool HasPond => Bowl >= 0;
	public float Length => Samples.Count > 0 ? Samples[^1].S : 0f;
}

/// <summary>One point of the main route: a ledge landing, or a walk point on a floor.</summary>
public readonly record struct CaveRoutePoint( Vector3 Position, int Ledge, int Passage );

/// <summary>
/// Pure seeded layout of a cave: a <b>chain</b> (the trunk, <c>Passages[0]</c>) of drops, junction halls, tunnels,
/// chimneys and caverns swept as one tube that wanders in 3D — no central axis — with dead-end rooms and loops forking
/// off the junctions and the drops, ponds as floor bowls, a bottom chamber with an oasis, and the main route as ledge
/// hops (down the drops, up the chimneys) plus floor walks. Everything is in meters and generator-local space (rim at
/// the origin, Z negative = deeper). No engine calls, so every peer produces the same layout from the same seed and a
/// plain .NET harness can check it.
/// </summary>
public sealed partial class CaveLayout
{
	public int Seed;
	public CaveLayoutSettings Settings;
	public CaveArchetype Archetype;

	public float DepthMeters;
	public float TopRadiusMeters;
	public float ChamberRadiusMeters;
	public float PlatformHalfMeters;
	public float EntranceAngle = MathF.PI;
	/// <summary>Trunk leg index of the bottom chamber.</summary>
	public int ChamberLeg = -1;
	public int ChamberBowl = -1;
	public Vector3 ChamberChestMeters;
	public float ChamberChestYawDegrees;
	public Vector3 WaterfallCrackMeters;
	public Vector3 WaterfallOutward;
	public float WaterfallHeightMeters;

	public readonly List<CaveLedge> Ledges = new();
	public readonly List<CaveMouth> Mouths = new();
	public readonly List<CavePassage> Passages = new();
	public readonly List<CaveBowl> Bowls = new();
	public readonly List<CaveRoutePoint> Route = new();
	/// <summary>Rejection reasons for the harness / cave_info.</summary>
	public readonly List<string> DebugLog = new();
	public int RouteLedgeCount;
	public float RoutePathMeters;
	public float RouteClimbMeters;
	public float RouteDescentMeters;
	/// <summary>Deepest point of the trunk axis (meters, negative).</summary>
	public float BottomZ;

	public CavePassage Trunk => Passages[0];

	// ---- generation ------------------------------------------------------------------------------

	public static CaveLayout Generate( CaveLayoutSettings s, int seed )
	{
		var rng = new Random( seed );
		var layout = new CaveLayout { Seed = seed, Settings = s };

		layout.Archetype = s.Archetype is >= 0 and < 5 ? (CaveArchetype)s.Archetype : (CaveArchetype)rng.Next( 5 );
		layout.DepthMeters = MathF.Max( 300f, s.DepthMeters );
		layout.TopRadiusMeters = Math.Clamp( s.TopRadiusMeters * Range( rng, 0.8f, 1.2f ), s.MinRadiusMeters + 4f, s.MaxRadiusMeters );
		layout.ChamberRadiusMeters = MathF.Max( 20f, s.ChamberRadiusMeters * Range( rng, 0.85f, 1.15f ) );
		layout.PlatformHalfMeters = MathF.Max( s.PlatformHalfMeters, layout.TopRadiusMeters + s.WallNoiseMeters * 1.5f + 8f );

		layout.BuildTrunk( rng );
		layout.BuildRoute( rng );
		layout.BuildFillers( rng );
		layout.MeasureRoute();

		return layout;
	}

	void MeasureRoute()
	{
		RoutePathMeters = 0f;
		RouteClimbMeters = 0f;
		RouteDescentMeters = 0f;
		for ( var i = 1; i < Route.Count; i++ )
		{
			var d = Route[i].Position - Route[i - 1].Position;
			RoutePathMeters += d.Length;
			if ( d.z > 0f ) RouteClimbMeters += d.z;
			else RouteDescentMeters -= d.z;
		}
	}

	// ---- tube surface (meters) -------------------------------------------------------------------

	/// <summary>Rock noise on a tube wall, keyed by the passage so two tubes never share a pattern.</summary>
	public float TubeNoise( CavePassage p, float angle, float s )
	{
		var amp = Settings.WallNoiseMeters * (p.Kind == CavePassageKind.Trunk ? 1f : 0.8f);
		return amp * (0.5f * MathF.Sin( 3f * angle + 0.25f * s + p.NoisePhase )
			+ 0.3f * MathF.Sin( 7f * angle - 0.4f * s + p.NoisePhase * 1.7f )
			+ 0.2f * MathF.Sin( 0.6f * s + p.NoisePhase * 0.4f ));
	}

	/// <summary>Rock surface radius of a sample ring at polar angle <paramref name="phi"/> (in the sample's U / V frame).</summary>
	public float TubeRadius( CavePassage p, CaveSpineSample sm, float phi ) =>
		MathF.Max( 1f, sm.Radius * MathF.Min( 1f, 1f ) + TubeNoise( p, phi, sm.S ) * MathF.Min( 1f, sm.Radius / 8f ) );

	/// <summary>Point on a tube's rock surface: ring <paramref name="r"/>, polar angle <paramref name="phi"/>, floor clamp and pond bowls applied.</summary>
	public Vector3 TubePoint( CavePassage p, int r, float phi )
	{
		var sm = p.Samples[r];
		var radial = sm.U * MathF.Cos( phi ) + sm.V * MathF.Sin( phi );
		var pos = sm.Position + radial * TubeRadius( p, sm, phi );
		return ClampFloor( sm, pos );
	}

	public Vector3 ClampFloor( CaveSpineSample sm, Vector3 pos )
	{
		if ( float.IsNaN( sm.FloorZ ) )
			return pos;
		var floor = sm.FloorZ - BowlDepthAt( pos.x, pos.y );
		if ( pos.z < floor )
			pos.z = floor;
		return pos;
	}

	/// <summary>Total pond depression at a floor point from every bowl (they never overlap in practice).</summary>
	public float BowlDepthAt( float x, float y )
	{
		var depth = 0f;
		foreach ( var bowl in Bowls )
			depth = MathF.Max( depth, bowl.DepthAt( x, y ) );
		return depth;
	}

	/// <summary>Polar angle in a sample's frame of a horizontal direction.</summary>
	public static float FrameAngle( CaveSpineSample sm, Vector3 dir ) =>
		MathF.Atan2( Vector3.Dot( dir, sm.V ), Vector3.Dot( dir, sm.U ) );

	public static float SegmentAngle( CavePassage p, int m ) => -MathF.PI + m * 2f * MathF.PI / p.Segments;

	public static int SegmentIndex( CavePassage p, float phi )
	{
		var m = (int)MathF.Floor( (phi + MathF.PI) / (2f * MathF.PI) * p.Segments );
		return ((m % p.Segments) + p.Segments) % p.Segments;
	}

	/// <summary>Frame angle of straight up in a sample's ring (only meaningful for non-vertical tangents).</summary>
	public static float UpAngle( CaveSpineSample sm ) => MathF.Atan2( sm.V.z, sm.U.z );

	/// <summary>+1 when a horizontal direction lies on the ring's "right" (angles below UpAngle), -1 on its "left".</summary>
	public static int SideOf( CaveSpineSample sm, Vector3 horizontal )
	{
		var phi = FrameAngle( sm, horizontal );
		return Wrap( phi - UpAngle( sm ) ) < 0f ? 1 : -1;
	}

	/// <summary>Frame angle on one side of a ring where the wall sits <paramref name="height"/> above the axis (clamped to the tube).</summary>
	public static float AngleAtHeight( CaveSpineSample sm, int side, float height )
	{
		var c = Math.Clamp( height / MathF.Max( 0.01f, sm.Radius ), -1f, 1f );
		return Wrap( UpAngle( sm ) - side * MathF.Acos( c ) );
	}

	static float Range( Random rng, float min, float max ) => min + (float)rng.NextDouble() * (max - min);

	static float Wrap( float a )
	{
		while ( a > MathF.PI ) a -= 2f * MathF.PI;
		while ( a < -MathF.PI ) a += 2f * MathF.PI;
		return a;
	}

	static float Smooth( float t )
	{
		t = Math.Clamp( t, 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}
