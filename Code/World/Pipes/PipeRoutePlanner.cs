using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>One placed piece of a pipe system: where its start joint is (metres) and which way its +X runs.</summary>
public readonly record struct PipePiece( PipePieceKind Kind, Vector3 PositionMeters, Rotation Rotation );

/// <summary>A planned pipe system. Everything in metres.</summary>
public sealed class PipeRoute
{
	public int CellX { get; init; }
	public int CellY { get; init; }
	public List<PipePiece> Pieces { get; } = new();
	/// <summary>Where the pipe meets the ground on the crushed side / on the deep side (on the pipe axis).</summary>
	public Vector3 EntryCrushed { get; set; }
	public Vector3 EntryDeep { get; set; }
	/// <summary>The pinched tip of the crushed end and the centre of the cement cube.</summary>
	public Vector3 CrushedTip { get; set; }
	public Vector3 CubeCenter { get; set; }
	/// <summary>The pipe's centre line from entry to entry, flattened: what trees and rocks keep clear of.</summary>
	public List<Vector2> Line { get; } = new();
	public float RunLengthMeters { get; set; }
	public Vector2 Min { get; set; }
	public Vector2 Max { get; set; }

	public Vector3 Middle => Line.Count > 0 ? new Vector3( Line[Line.Count / 2].x, Line[Line.Count / 2].y, (EntryCrushed.z + EntryDeep.z) * 0.5f ) : EntryCrushed;

	public bool Overlaps( float minX, float minY, float maxX, float maxY )
		=> Max.x >= minX && Min.x <= maxX && Max.y >= minY && Min.y <= maxY;

	/// <summary>Horizontal distance from a point to the above-ground centre line.</summary>
	public float DistanceToLine( float x, float y )
	{
		var best = float.MaxValue;
		var p = new Vector2( x, y );
		for ( var i = 0; i + 1 < Line.Count; i++ )
		{
			var a = Line[i];
			var ab = Line[i + 1] - a;
			var t = ab.LengthSquared > 1e-6f ? Math.Clamp( Vector2.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f ) : 0f;
			best = Math.Min( best, (a + (ab * t) - p).Length );
		}
		return best;
	}
}

/// <summary>
/// Pipe systems (Mark 2026-11-19: "scarce metal pipe systems, that look like they carry stuff from point A to point B. Both
/// of them lead into the ground and while theyre above ground they kinda follow the slope a bit ... 3-5 meters off the ground
/// ... one end pinched and destroyed ... the other should go deep underground, 20 meters, [into] a cement square").
///
/// A route is a pure function of the world seed and the terrain sampler, planned once per <c>cellMeters</c> world cell and
/// cached, so every chunk (and every peer) agrees where the pieces are. The kit's pieces chain joint to joint
/// (<c>Blender/scripts/create_pipe_kit.py</c>): start joint at the origin, pipe along local +X, a collar on every start joint
/// that hides a kink of up to ~10 degrees - which is how rigid 8 m pieces follow the slope.
///
/// Along the line: crushed end (buried, pointing back down) - a 45 degree rise through the ground entry collar - an elbow - the
/// run on supports (each piece pitches toward <c>clearanceMeters</c> over the ground ahead, at most <c>maxJointDegrees</c> off
/// the piece before it, levelling out toward both elbows) - an elbow - a 45 degree dive to <c>deepEndDepthMeters</c> - an elbow
/// rolled over to level it - the cement cube. The buried pieces are ordinary static props inside the ground: the heightfield
/// and the voxel columns never carve around them, so digging simply uncovers them.
/// </summary>
public static class PipeRoutePlanner
{
	// The kit's dimensions (create_pipe_kit.py prints them)
	public const float PipeRadiusMeters = 0.9f;
	const float SegmentMeters = 8f;
	const float ShortSegmentMeters = 4f;
	const float BendDegrees = 45f;
	/// <summary>Where the elbow ends in its own frame: forward, and toward local -Z.</summary>
	static readonly Vector3 BendEndLocal = new( 1.6971f, 0f, -0.7029f );
	const float CrushedLengthMeters = 4.85f;
	const float CrushedTipSagMeters = 0.62f;
	const float CubeStubMeters = 1f;
	const float CubeSizeMeters = 5f;
	/// <summary>A support stands this far along its piece, clear of the joint collar.</summary>
	const float SupportOffsetMeters = 1.2f;
	const int SeedSalt = 9100;

	static readonly object Gate = new();
	static readonly Dictionary<(int Seed, int X, int Y), PipeRoute> Cache = new();

	/// <summary>World (re)load, or the data changed: plan again on demand.</summary>
	public static void ClearCache()
	{
		lock ( Gate )
			Cache.Clear();
	}

	public static (int X, int Y) CellAt( float x, float y )
	{
		var cell = PipeSystemCatalog.Config.CellMeters;
		return ((int)MathF.Floor( x / cell ), (int)MathF.Floor( y / cell ));
	}

	/// <summary>The pipe system of one world cell, or null. Thread-safe; the first call per cell plans it (a few ms of terrain samples).</summary>
	public static PipeRoute RouteForCell( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, int cx, int cy )
	{
		var config = PipeSystemCatalog.Config;
		if ( !config.Enabled || settings is null || backend is null )
			return null;
		var key = (settings.WorldSeed, cx, cy);
		lock ( Gate )
		{
			if ( Cache.TryGetValue( key, out var cached ) )
				return cached;
			var route = Plan( config, settings, backend, cx, cy );
			Cache[key] = route;
			return route;
		}
	}

	/// <summary>Is this point within <paramref name="radiusMeters"/> of a pipe's above-ground centre line? (Trees, rock clusters.)</summary>
	public static bool IsNearPipe( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, float x, float y, float radiusMeters )
	{
		var config = PipeSystemCatalog.Config;
		if ( !config.Enabled )
			return false;
		// A route never leaves its own cell
		var (cx, cy) = CellAt( x, y );
		var route = RouteForCell( settings, backend, cx, cy );
		if ( route is null || !route.Overlaps( x - radiusMeters, y - radiusMeters, x + radiusMeters, y + radiusMeters ) )
			return false;
		return route.DistanceToLine( x, y ) < radiusMeters;
	}

	/// <summary>Every pipe system whose cell lies within <paramref name="radiusMeters"/> of a point (dev commands).</summary>
	public static List<PipeRoute> RoutesNear( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, float x, float y, float radiusMeters )
	{
		var result = new List<PipeRoute>();
		var (loX, loY) = CellAt( x - radiusMeters, y - radiusMeters );
		var (hiX, hiY) = CellAt( x + radiusMeters, y + radiusMeters );
		for ( var cy = loY; cy <= hiY; cy++ )
		{
			for ( var cx = loX; cx <= hiX; cx++ )
			{
				if ( RouteForCell( settings, backend, cx, cy ) is { } route )
					result.Add( route );
			}
		}
		return result;
	}

	static PipeRoute Plan( PipeSystemConfig config, TerrainPreviewSettings settings, ITerrainPreviewBackend backend, int cx, int cy )
	{
		var salt = settings.WorldSeed + SeedSalt;
		if ( TerrainPreviewNoise.Hash01( salt, cx, cy ) > Math.Clamp( config.Chance, 0f, 1f ) )
			return null;

		var cell = config.CellMeters;
		var land = settings.LandRadiusMeters;
		for ( var k = 0; k < config.CandidatesPerCell; k++ )
		{
			var s = salt + 16 + (k * 16);
			var x = (cx + 0.25f + (0.5f * TerrainPreviewNoise.Hash01( s, cx, cy ))) * cell;
			var y = (cy + 0.25f + (0.5f * TerrainPreviewNoise.Hash01( s + 1, cx, cy ))) * cell;
			var fromOrigin = MathF.Sqrt( (x * x) + (y * y) );
			if ( fromOrigin > land - 300f || fromOrigin < config.SpawnClearanceMeters + config.LengthMaxMeters )
				continue;

			var yaw = TerrainPreviewNoise.Hash01( s + 2, cx, cy ) * MathF.Tau;
			var length = config.LengthMinMeters + ((config.LengthMaxMeters - config.LengthMinMeters) * TerrainPreviewNoise.Hash01( s + 3, cx, cy ));
			var turn = ((TerrainPreviewNoise.Hash01( s + 4, cx, cy ) * 2f) - 1f) * config.MaxTurnDegrees.DegreeToRadian();
			if ( TryCandidate( config, settings, backend, cx, cy, x, y, yaw, length, turn, out var route ) )
				return route;
		}
		return null;
	}

	static Vector3 Dir( float yaw, float pitch )
		=> new( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );

	static Rotation Facing( Vector3 forward ) => Rotation.LookAt( forward, Vector3.Up );

	/// <summary>Dry land a pipe may stand on or dive under: no sea, lake, river, beach or wet band.</summary>
	static bool Ground( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, float x, float y, out float z )
	{
		var s = backend.Sample( settings, x, y );
		z = s.HeightMeters;
		return s.IsInsideWorld && s.IsOnLand && s.OceanHeight01 <= 0.5f && !s.RiverWater && s.Wet01 < 0.02f && s.Beach01 < 0.02f;
	}

	static bool TryCandidate( PipeSystemConfig config, TerrainPreviewSettings settings, ITerrainPreviewBackend backend, int cx, int cy,
		float x0, float y0, float yaw0, float length, float turn, out PipeRoute route )
	{
		route = null;
		var centre = backend.Sample( settings, x0, y0 );
		if ( !config.AllowsBiome( TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, centre, x0, y0 ).BiomeId ) )
			return false;
		if ( !Ground( settings, backend, x0, y0, out var g0 ) )
			return false;

		// ---- the run above ground
		var n = Math.Clamp( (int)MathF.Round( length / SegmentMeters ), 6, 30 );
		var nodes = new Vector3[n + 1];
		var yaws = new float[n];
		var pitches = new float[n];
		var joint = config.MaxJointDegrees.DegreeToRadian();
		var maxPitch = config.MaxPitchDegrees.DegreeToRadian();
		nodes[0] = new Vector3( x0, y0, g0 + config.ClearanceMeters );
		var prevPitch = 0f;
		for ( var k = 0; k < n; k++ )
		{
			var yaw = yaw0 + (turn * k / Math.Max( 1, n - 1 ));
			var hx = MathF.Cos( yaw );
			var hy = MathF.Sin( yaw );
			var at = nodes[k];
			if ( !Ground( settings, backend, at.x + (hx * SegmentMeters), at.y + (hy * SegmentMeters), out var g1 )
			     || !Ground( settings, backend, at.x + (hx * SegmentMeters * 2f), at.y + (hy * SegmentMeters * 2f), out var g2 ) )
				return false;
			// Aim between the next joint and the one after: following the slope, not every bump
			var slope = 0.5f * (((g1 + config.ClearanceMeters - at.z) / SegmentMeters) + ((g2 + config.ClearanceMeters - at.z) / (SegmentMeters * 2f)));
			var pitch = MathF.Asin( Math.Clamp( slope, -0.9f, 0.9f ) );
			// Level at both elbows, free in between
			var limit = Math.Min( maxPitch, joint * Math.Min( k + 1, n - k ) );
			pitch = Math.Clamp( pitch, prevPitch - joint, prevPitch + joint );
			pitch = Math.Clamp( pitch, -limit, limit );

			var dir = Dir( yaw, pitch );
			var mid = at + (dir * (SegmentMeters * 0.5f));
			var next = at + (dir * SegmentMeters);
			if ( !Ground( settings, backend, mid.x, mid.y, out var gm ) || !Ground( settings, backend, next.x, next.y, out var gn ) )
				return false;
			if ( mid.z - gm < config.ClearanceMinMeters - 0.3f || mid.z - gm > config.ClearanceMaxMeters + 0.5f )
				return false;
			if ( next.z - gn < config.ClearanceMinMeters || next.z - gn > config.ClearanceMaxMeters )
				return false;

			yaws[k] = yaw;
			pitches[k] = pitch;
			nodes[k + 1] = next;
			prevPitch = pitch;
		}

		var result = new PipeRoute { CellX = cx, CellY = cy, RunLengthMeters = n * SegmentMeters };
		var pieces = result.Pieces;

		// ---- crushed side, planned backward from the first joint: elbow, a 45 degree rise out of the ground, the crushed end
		var rise = Dir( yaws[0], pitches[0] + BendDegrees.DegreeToRadian() );   // direction of travel on the way up
		var riseRot = Facing( rise );
		var elbowStart = nodes[0] - (riseRot * BendEndLocal);
		pieces.Add( new PipePiece( PipePieceKind.Bend, elbowStart, riseRot ) );
		var j = elbowStart;
		var buried = false;
		for ( var i = 0; i < 6 && !buried; i++ )
		{
			var len = i == 0 ? SegmentMeters : ShortSegmentMeters;
			j -= rise * len;
			pieces.Add( new PipePiece( len > 6f ? PipePieceKind.Straight8 : PipePieceKind.Straight4, j, riseRot ) );
			if ( !Ground( settings, backend, j.x, j.y, out var g ) )
				return false;
			buried = g - j.z >= config.CrushedJointDepthMeters;
		}
		if ( !buried )
			return false;
		pieces.Add( new PipePiece( PipePieceKind.CrushedEnd, j, Facing( -rise ) ) );
		var tip = j - (rise * CrushedLengthMeters) + (Vector3.Down * CrushedTipSagMeters);
		if ( !Ground( settings, backend, tip.x, tip.y, out var tipGround ) || tipGround - tip.z < 1f )
			return false;
		result.CrushedTip = tip;
		if ( !TryFindEntry( settings, backend, elbowStart, -rise, out var entryCrushed ) )
			return false;
		result.EntryCrushed = entryCrushed;
		pieces.Add( new PipePiece( PipePieceKind.GroundEntry, entryCrushed, riseRot ) );

		// ---- the run and its supports
		for ( var k = 0; k < n; k++ )
		{
			var dir = Dir( yaws[k], pitches[k] );
			pieces.Add( new PipePiece( PipePieceKind.Straight8, nodes[k], Facing( dir ) ) );
			if ( k % config.SupportEverySegments != config.SupportEverySegments - 1 && k != 0 && k != n - 1 )
				continue;
			var p = nodes[k] + (dir * SupportOffsetMeters);
			if ( !Ground( settings, backend, p.x, p.y, out var g ) )
				return false;
			var upright = Rotation.FromYaw( yaws[k].RadianToDegree() );
			pieces.Add( new PipePiece( PipePieceKind.Support, p, upright ) );
			pieces.Add( new PipePiece( PipePieceKind.Footing, new Vector3( p.x, p.y, g ), upright ) );
		}

		// ---- deep side: elbow, a 45 degree dive, an elbow rolled over to level it, the cube
		var lastYaw = yaws[n - 1];
		var lastPitch = pitches[n - 1];
		var runRot = Facing( Dir( lastYaw, lastPitch ) );
		pieces.Add( new PipePiece( PipePieceKind.Bend, nodes[n], runRot ) );
		j = nodes[n] + (runRot * BendEndLocal);
		var dive = Dir( lastYaw, lastPitch - BendDegrees.DegreeToRadian() );
		var diveRot = Facing( dive );
		if ( !TryFindEntry( settings, backend, j, dive, out var entryDeep ) )
			return false;
		result.EntryDeep = entryDeep;
		pieces.Add( new PipePiece( PipePieceKind.GroundEntry, entryDeep, diveRot ) );
		for ( var i = 0; i < 12; i++ )
		{
			if ( !Ground( settings, backend, j.x, j.y, out var g ) )
				return false;
			// The levelling elbow drops the axis a little more
			var remaining = config.DeepEndDepthMeters + BendEndLocal.z - (g - j.z);
			if ( remaining <= 0.4f )
				break;
			var len = remaining > 4.2f ? SegmentMeters : ShortSegmentMeters;
			pieces.Add( new PipePiece( len > 6f ? PipePieceKind.Straight8 : PipePieceKind.Straight4, j, diveRot ) );
			j += dive * len;
		}
		var levelRot = Rotation.LookAt( dive, Vector3.Down );   // rolled over: the elbow bends back up
		pieces.Add( new PipePiece( PipePieceKind.Bend, j, levelRot ) );
		j += levelRot * BendEndLocal;
		// The cube sits level whatever the run's last pitch was; its collar hides the kink
		var cubeRot = Rotation.FromYaw( lastYaw.RadianToDegree() );
		pieces.Add( new PipePiece( PipePieceKind.CementCube, j, cubeRot ) );
		result.CubeCenter = j + (cubeRot.Forward * (CubeStubMeters + (CubeSizeMeters * 0.5f)));
		if ( !Ground( settings, backend, result.CubeCenter.x, result.CubeCenter.y, out var cubeGround ) || cubeGround - result.CubeCenter.z < CubeSizeMeters )
			return false;

		// ---- what vegetation keeps clear of, and the bounds
		result.Line.Add( new Vector2( entryCrushed.x, entryCrushed.y ) );
		foreach ( var node in nodes )
			result.Line.Add( new Vector2( node.x, node.y ) );
		result.Line.Add( new Vector2( entryDeep.x, entryDeep.y ) );
		var min = new Vector2( float.MaxValue, float.MaxValue );
		var max = new Vector2( float.MinValue, float.MinValue );
		foreach ( var piece in pieces )
		{
			min = new Vector2( Math.Min( min.x, piece.PositionMeters.x ), Math.Min( min.y, piece.PositionMeters.y ) );
			max = new Vector2( Math.Max( max.x, piece.PositionMeters.x ), Math.Max( max.y, piece.PositionMeters.y ) );
		}
		const float pad = 10f;   // a piece reaches up to 8 m from its joint
		result.Min = min - new Vector2( pad, pad );
		result.Max = max + new Vector2( pad, pad );
		route = result;
		return true;
	}

	/// <summary>March from a point above ground along a diving direction to where the pipe axis meets the ground.</summary>
	static bool TryFindEntry( TerrainPreviewSettings settings, ITerrainPreviewBackend backend, Vector3 from, Vector3 direction, out Vector3 entry )
	{
		entry = from;
		const float step = 0.5f;
		for ( var d = 0f; d <= 24f; d += step )
		{
			var p = from + (direction * d);
			if ( !Ground( settings, backend, p.x, p.y, out var g ) )
				return false;
			if ( p.z <= g )
			{
				entry = p;
				return true;
			}
		}
		return false;
	}
}
