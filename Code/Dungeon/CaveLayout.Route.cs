using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Ledges, the descend / climb steppers that lay them, and the main route along the trunk with its branches.</summary>
public sealed partial class CaveLayout
{
	/// <summary>A near-vertical run of a passage the steppers can hang ledges on, indexed by z.</summary>
	sealed class LegTube
	{
		readonly CaveLayout _l;
		readonly CavePassage _p;
		readonly List<CaveSpineSample> _s = new(); // sorted by z ascending
		readonly Vector3 _u;
		readonly Vector3 _v;
		public float ZMin;
		public float ZMax;
		public bool Valid => _s.Count >= 2;

		public LegTube( CaveLayout l, CavePassage p, int sampleStart, int sampleEnd )
		{
			_l = l;
			_p = p;
			for ( var i = sampleStart; i < sampleEnd; i++ )
			{
				var sm = p.Samples[i];
				if ( MathF.Abs( sm.Tangent.z ) > 0.6f )
					_s.Add( sm );
			}
			_s.Sort( ( a, b ) => a.Position.z.CompareTo( b.Position.z ) );
			if ( _s.Count == 0 )
				return;

			ZMin = _s[0].Position.z;
			ZMax = _s[^1].Position.z;
			var mid = _s[_s.Count / 2];
			_u = mid.U.WithZ( 0f ).Normal;
			if ( _u.LengthSquared < 0.5f ) _u = Vector3.Forward;
			_v = new Vector3( -_u.y, _u.x, 0f );
		}

		public Vector3 Dir( float angle ) => _u * MathF.Cos( angle ) + _v * MathF.Sin( angle );

		public Vector3 Axis( float z )
		{
			var (a, b, t) = Find( z );
			return a.Position + (b.Position - a.Position) * t;
		}

		public float Radius( float angle, float z )
		{
			var (a, b, t) = Find( z );
			var r = a.Radius + (b.Radius - a.Radius) * t;
			var s = a.S + (b.S - a.S) * t;
			return MathF.Max( 1.5f, r + _l.TubeNoise( _p, angle, s ) * MathF.Min( 1f, r / 8f ) );
		}

		public float Profile( float z )
		{
			var (a, b, t) = Find( z );
			return a.Radius + (b.Radius - a.Radius) * t;
		}

		/// <summary>Sample index nearest to a depth (for cutting mouths).</summary>
		public int RingAt( float z )
		{
			var best = _s[0];
			foreach ( var sm in _s )
			{
				if ( MathF.Abs( sm.Position.z - z ) < MathF.Abs( best.Position.z - z ) )
					best = sm;
			}
			return _p.Samples.IndexOf( best );
		}

		(CaveSpineSample a, CaveSpineSample b, float t) Find( float z )
		{
			if ( z <= ZMin ) return (_s[0], _s[0], 0f);
			if ( z >= ZMax ) return (_s[^1], _s[^1], 0f);
			for ( var i = 0; i < _s.Count - 1; i++ )
			{
				var z0 = _s[i].Position.z;
				var z1 = _s[i + 1].Position.z;
				if ( z >= z0 && z <= z1 )
					return (_s[i], _s[i + 1], z1 - z0 > 1e-4f ? (z - z0) / (z1 - z0) : 0f);
			}
			return (_s[^1], _s[^1], 0f);
		}
	}

	// ---- ledge factories -------------------------------------------------------------------------

	CaveLedge MakeLedge( Random rng, Vector3 anchor, Vector3 intoVoid, float z, float localRadius, bool grapple, bool ascending, int passage )
	{
		var roll = rng.NextDouble();
		var kind = roll < 0.34 ? CaveLedgeKind.Slab
			: roll < 0.54 ? CaveLedgeKind.Jut
			: roll < 0.70 ? CaveLedgeKind.Shelf
			: roll < 0.82 ? CaveLedgeKind.Spur
			: CaveLedgeKind.Boulder;

		var ledge = new CaveLedge
		{
			Kind = kind,
			AnchorMeters = anchor.WithZ( z ),
			IntoVoid = intoVoid,
			ZMeters = z,
			GrappleHop = grapple,
			Ascending = ascending,
			Passage = passage,
		};

		switch ( kind )
		{
			case CaveLedgeKind.Slab:
				ledge.WidthMeters = Range( rng, 3f, 6f );
				ledge.ProtrudeMeters = Range( rng, 2f, 4f );
				ledge.ThicknessMeters = Range( rng, 0.6f, 1.2f );
				break;
			case CaveLedgeKind.Jut:
				ledge.WidthMeters = Range( rng, 4f, 8f );
				ledge.ProtrudeMeters = Range( rng, 4f, 7f );
				ledge.ThicknessMeters = Range( rng, 2f, 4f );
				ledge.TiltDegrees = Range( rng, -8f, 8f );
				ledge.RollDegrees = Range( rng, -6f, 6f );
				break;
			case CaveLedgeKind.Shelf:
				ledge.WidthMeters = Range( rng, 7f, 12f );
				ledge.ProtrudeMeters = Range( rng, 1.5f, 2.5f );
				ledge.ThicknessMeters = Range( rng, 0.5f, 0.9f );
				break;
			case CaveLedgeKind.Spur:
				ledge.WidthMeters = Range( rng, 1.6f, 2.6f );
				ledge.ProtrudeMeters = Range( rng, 5f, 9f );
				ledge.ThicknessMeters = Range( rng, 0.8f, 1.4f );
				ledge.TiltDegrees = Range( rng, -5f, 5f );
				break;
			default:
				ledge.WidthMeters = Range( rng, 3f, 5f );
				ledge.ProtrudeMeters = Range( rng, 3f, 5f );
				ledge.ThicknessMeters = Range( rng, 3f, 5f );
				ledge.TiltDegrees = Range( rng, -15f, 15f );
				ledge.RollDegrees = Range( rng, -15f, 15f );
				break;
		}

		ledge.ProtrudeMeters = MathF.Min( ledge.ProtrudeMeters, MathF.Max( 1.2f, localRadius * 0.45f ) );
		ledge.LandingMeters = ledge.AnchorMeters + intoVoid * (ledge.ProtrudeMeters * 0.5f);
		return ledge;
	}

	CaveLedge MakeDoorstep( CaveMouth mouth )
	{
		var host = Passages[mouth.Host];
		var step = 2f * MathF.PI / host.Segments;
		var midPhi = SegmentAngle( host, mouth.SegStart ) + mouth.SegCount * 0.5f * step;
		var floorPoint = TubePoint( host, mouth.RingEnd, midPhi );
		var wallR = host.Samples[mouth.RingEnd].Radius;
		var ledge = new CaveLedge
		{
			Kind = CaveLedgeKind.Doorstep,
			AnchorMeters = floorPoint.WithZ( mouth.FloorZ + 0.03f ),
			IntoVoid = -mouth.OutwardMeters,
			ZMeters = mouth.FloorZ + 0.03f,
			WidthMeters = Math.Clamp( mouth.SegCount * step * wallR + 1f, 4f, 10f ),
			ProtrudeMeters = MathF.Min( 3f, wallR * 0.4f ),
			ThicknessMeters = 0.8f,
			Mouth = mouth.Index,
			Passage = mouth.Host,
		};
		ledge.LandingMeters = ledge.AnchorMeters + ledge.IntoVoid * (ledge.ProtrudeMeters * 0.5f);
		return ledge;
	}

	int AddLedge( CaveLedge ledge, bool onRoute, int passage )
	{
		ledge.Index = Ledges.Count;
		Ledges.Add( ledge );
		if ( onRoute )
		{
			ledge.RouteIndex = RouteLedgeCount++;
			Route.Add( new CaveRoutePoint( ledge.LandingMeters, ledge.Index, passage ) );
		}
		return ledge.Index;
	}

	static float HorizontalDistance( Vector3 a, Vector3 b )
	{
		var dx = a.x - b.x;
		var dy = a.y - b.y;
		return MathF.Sqrt( dx * dx + dy * dy );
	}

	static float WorldAngle( Vector3 dir ) => MathF.Atan2( dir.y, dir.x );

	/// <summary>True when a trunk-drop ledge at (world outward angle, z) would sit inside a vertical mouth's hole.</summary>
	bool InsideMouth( Vector3 outward, float z, float halfWidth )
	{
		var angle = WorldAngle( outward );
		foreach ( var m in Mouths )
		{
			if ( !m.OnVerticalWall )
				continue;
			var top = m.FloorZ + (m.RingEnd - m.RingStart) * Trunk.RingMeters;
			if ( z < m.FloorZ - 1.5f || z > top + 1.5f )
				continue;
			var r = MathF.Max( 2f, Trunk.Samples[(m.RingStart + m.RingEnd) / 2].Radius );
			var span = m.SegCount * (2f * MathF.PI / Trunk.Segments) * 0.5f + halfWidth / r + 0.15f;
			if ( MathF.Abs( Wrap( angle - WorldAngle( m.OutwardMeters ) ) ) < span )
				return true;
		}
		return false;
	}

	/// <summary>True when no trunk ledge laid so far would end up inside this (unregistered) vertical mouth.</summary>
	bool HoleClearOfLedges( CaveMouth m )
	{
		var top = m.FloorZ + (m.RingEnd - m.RingStart) * Trunk.RingMeters;
		var r = MathF.Max( 2f, Trunk.Samples[(m.RingStart + m.RingEnd) / 2].Radius );
		var span = m.SegCount * (2f * MathF.PI / Trunk.Segments) * 0.5f + 0.45f;
		foreach ( var ledge in Ledges )
		{
			if ( ledge.Passage != 0 || ledge.ZMeters < m.FloorZ - 1.5f || ledge.ZMeters > top + 1.5f )
				continue;
			var half = ledge.WidthMeters * 0.5f / r;
			if ( MathF.Abs( Wrap( WorldAngle( -ledge.IntoVoid ) - WorldAngle( m.OutwardMeters ) ) ) < span + half )
				return false;
		}
		return true;
	}

	// ---- steppers --------------------------------------------------------------------------------

	/// <summary>
	/// Lay a descending ledge route down a tube from the current (angle, z) until <paramref name="zEnd"/>: jump hops and
	/// grapple hops, tangential step solved against the real horizontal distance, cross-shaft swings in pinches, and
	/// (trunk only) hops onto the doorstep of a planned side room, with mouths avoided.
	/// </summary>
	void DescendTube( Random rng, LegTube tube, ref float angle, ref float z, float zEnd, int passage, bool onRoute, List<float> roomDepths )
	{
		var s = Settings;
		var dir = rng.NextDouble() < 0.5 ? 1f : -1f;
		var runLeft = rng.Next( 3, 9 );
		var isTrunk = passage == 0;
		var guard = 0;
		var prevLanding = Route.Count > 0 && onRoute ? Route[^1].Position : tube.Axis( z ) + tube.Dir( angle ) * (tube.Radius( angle, z ) - 1.5f);
		var forceGrapple = false;

		while ( z > zEnd + 0.5f && guard++ < 3000 )
		{
			var grapple = forceGrapple || rng.NextDouble() < s.GrappleHopChance;
			var drop = grapple ? Range( rng, s.GrappleDropMinMeters, s.GrappleDropMaxMeters ) : Range( rng, s.JumpDropMinMeters, s.JumpDropMaxMeters );
			var gap = grapple ? Range( rng, s.GrappleGapMinMeters, s.GrappleGapMaxMeters ) : Range( rng, s.JumpGapMinMeters, s.JumpGapMaxMeters );

			var roomHop = isTrunk && roomDepths is { Count: > 0 } && z - drop <= roomDepths[0];
			if ( roomHop )
			{
				grapple = false;
				drop = Range( rng, s.JumpDropMinMeters, s.JumpDropMaxMeters );
				gap = Range( rng, s.JumpGapMinMeters, s.JumpGapMaxMeters );
			}

			// Retries never walk the start below the grapple drop limit: past that, go straight down the same wall.
			var straightDown = false;
			if ( z < prevLanding.z - s.GrappleDropMaxMeters + 1f )
			{
				z = prevLanding.z - s.GrappleDropMaxMeters + 1f;
				straightDown = true;
				grapple = true;
			}

			var nextZ = MathF.Max( zEnd, z - drop );
			nextZ = MathF.Max( nextZ, MathF.Min( z - 1f, prevLanding.z - s.GrappleDropMaxMeters ) );
			var estRadius = tube.Profile( nextZ );
			// How far the tube itself leans between the two heights: that much sideways is free.
			var lean = HorizontalDistance( tube.Axis( nextZ ), tube.Axis( prevLanding.z ) );

			var nextAngle = angle;
			var cross = false;
			if ( straightDown )
			{
				// keep the angle
			}
			else if ( !roomHop && grapple && estRadius * 2f <= s.GrappleGapMaxMeters && lean < 2f && rng.NextDouble() < 0.25 )
			{
				nextAngle = Wrap( angle + MathF.PI );
				cross = true;
			}
			else
			{
				var delta = gap / MathF.Max( 2f, estRadius ) * dir;
				nextAngle = Wrap( angle + delta );
				for ( var iter = 0; iter < 3; iter++ )
				{
					var probe = tube.Axis( nextZ ) + tube.Dir( nextAngle ) * (tube.Radius( nextAngle, nextZ ) - 1.5f);
					var d = HorizontalDistance( probe, prevLanding );
					if ( d <= gap * 1.1f || d < 0.5f )
						break;
					delta *= gap / d;
					nextAngle = Wrap( angle + delta );
				}
			}

			CaveLedge candidate = null;
			CaveMouth roomMouth = null;
			if ( roomHop )
			{
				roomMouth = TrySideRoom( rng, tube, tube.Dir( nextAngle ), nextZ, z );
				roomDepths.RemoveAt( 0 );
				if ( roomMouth is not null )
				{
					candidate = MakeDoorstep( roomMouth );
					nextAngle = FrameAngleOf( tube, roomMouth.OutwardMeters );
					nextZ = candidate.ZMeters;
				}
			}

			if ( candidate is null )
			{
				if ( isTrunk )
				{
					for ( var tries = 0; tries < 6 && InsideMouth( tube.Dir( nextAngle ), nextZ, 3f ); tries++ )
						nextAngle = Wrap( nextAngle + dir * 0.2f );
					if ( InsideMouth( tube.Dir( nextAngle ), nextZ, 3f ) )
					{
						z -= 1f;
						forceGrapple = true;
						continue;
					}
				}

				var radius = tube.Radius( nextAngle, nextZ );
				var anchor = tube.Axis( nextZ ) + tube.Dir( nextAngle ) * radius;
				candidate = MakeLedge( rng, anchor, -tube.Dir( nextAngle ), nextZ, radius, grapple, ascending: false, passage );
			}

			var dist = HorizontalDistance( candidate.LandingMeters, prevLanding );
			if ( !grapple && dist > s.JumpGapMaxMeters + 0.5f )
			{
				candidate.GrappleHop = true;
				grapple = true;
			}
			if ( grapple && dist > s.GrappleGapMaxMeters + 1.5f + lean && roomMouth is null && !straightDown )
			{
				z -= 1f;
				forceGrapple = true;
				continue;
			}
			if ( !grapple && prevLanding.z - candidate.ZMeters > s.JumpDropMaxMeters + 1f )
			{
				candidate.GrappleHop = true;
				grapple = true;
			}

			AddLedge( candidate, onRoute, passage );
			forceGrapple = false;
			if ( roomMouth is not null )
				roomMouth.Doorstep = candidate.Index;

			prevLanding = candidate.LandingMeters;
			angle = nextAngle;
			z = nextZ;

			if ( --runLeft <= 0 )
			{
				if ( rng.NextDouble() < 0.6 )
					dir = -dir;
				runLeft = rng.Next( 3, 9 );
			}
		}
	}

	static float FrameAngleOf( LegTube tube, Vector3 dir )
	{
		var x = Vector3.Dot( dir, tube.Dir( 0f ) );
		var y = Vector3.Dot( dir, tube.Dir( MathF.PI * 0.5f ) );
		return MathF.Atan2( y, x );
	}

	/// <summary>
	/// Lay a climbing ledge route up a chimney from the floor at <paramref name="z"/> to just under <paramref name="zTop"/>:
	/// every hop is a grapple hop upward, ending within 1–2 m of the top so the last move is a mantle onto the floor above.
	/// </summary>
	void AscendTube( Random rng, LegTube tube, ref float angle, ref float z, float zTop, int passage, bool onRoute )
	{
		var s = Settings;
		var dir = rng.NextDouble() < 0.5 ? 1f : -1f;
		var runLeft = rng.Next( 3, 8 );
		var guard = 0;
		var prevLanding = Route.Count > 0 && onRoute ? Route[^1].Position : tube.Axis( z );
		var lastZ = MathF.Min( zTop - 1.2f, tube.ZMax + 6f );

		while ( z < lastZ - 0.5f && guard++ < 2000 )
		{
			var rise = Range( rng, s.ClimbRiseMinMeters, s.ClimbRiseMaxMeters );
			var nextZ = MathF.Min( lastZ, z + rise );
			if ( lastZ - nextZ < s.ClimbRiseMinMeters * 0.6f && lastZ - z <= s.ClimbRiseMaxMeters )
				nextZ = lastZ;
			var gap = Range( rng, s.ClimbGapMinMeters, s.ClimbGapMaxMeters );
			var estRadius = tube.Profile( nextZ );

			var delta = gap / MathF.Max( 2f, estRadius ) * dir;
			var nextAngle = Wrap( angle + delta );
			for ( var iter = 0; iter < 3; iter++ )
			{
				var probe = tube.Axis( nextZ ) + tube.Dir( nextAngle ) * (tube.Radius( nextAngle, nextZ ) - 1.5f);
				var d = HorizontalDistance( probe, prevLanding );
				if ( d <= gap * 1.1f || d < 0.5f )
					break;
				delta *= gap / d;
				nextAngle = Wrap( angle + delta );
			}

			var radius = tube.Radius( nextAngle, nextZ );
			var anchor = tube.Axis( nextZ ) + tube.Dir( nextAngle ) * radius;
			var ledge = MakeLedge( rng, anchor, -tube.Dir( nextAngle ), nextZ, radius, grapple: true, ascending: true, passage );
			AddLedge( ledge, onRoute, passage );

			prevLanding = ledge.LandingMeters;
			angle = nextAngle;
			z = nextZ;

			if ( --runLeft <= 0 )
			{
				if ( rng.NextDouble() < 0.6 )
					dir = -dir;
				runLeft = rng.Next( 3, 8 );
			}
		}
	}

	// ---- route -----------------------------------------------------------------------------------

	/// <summary>Consecutive trunk legs of one group as (first leg, last leg) index pairs.</summary>
	List<(int first, int last)> TrunkGroups()
	{
		var groups = new List<(int, int)>();
		var legs = Trunk.Legs;
		var i = 0;
		while ( i < legs.Count )
		{
			var j = i;
			while ( j + 1 < legs.Count && legs[j + 1].Group == legs[i].Group && legs[j + 1].Kind == legs[i].Kind )
				j++;
			groups.Add( (i, j) );
			i = j + 1;
		}
		return groups;
	}

	void BuildRoute( Random rng )
	{
		var trunk = Trunk;
		var groups = TrunkGroups();
		var roomDepths = PlanSideRooms( rng, groups );
		var loopsLeft = Math.Clamp( Settings.LoopCount, 0, 8 );

		var rim = TubePoint( trunk, 0, EntranceAngle );
		var rimOut = (rim - trunk.Samples[0].Position).WithZ( 0f ).Normal;
		Route.Add( new CaveRoutePoint( rim.WithZ( 0f ) + rimOut * 2f, -1, 0 ) );

		var angle = EntranceAngle;
		var z = 0f;

		for ( var g = 0; g < groups.Count; g++ )
		{
			var (first, last) = groups[g];
			var kind = trunk.Legs[first].Kind;
			var sampleStart = trunk.Legs[first].Start;
			var sampleEnd = trunk.Legs[last].End;
			var nextFloor = last + 1 < trunk.Legs.Count ? trunk.Legs[last + 1].FloorZ : trunk.Legs[last].FloorZ;

			if ( kind == CaveLegKind.Drop )
			{
				var tube = new LegTube( this, trunk, sampleStart, sampleEnd );
				if ( !tube.Valid )
					continue;
				var floor = float.IsNaN( nextFloor ) ? tube.ZMin : nextFloor;
				var depthsHere = roomDepths.FindAll( d => d < z - 20f && d > floor + 25f );
				roomDepths.RemoveAll( d => d < z - 20f && d > floor + 25f );
				if ( g == 0 )
				{
					// The rim point is on the -X wall; carry that direction into the tube's own frame.
					angle = FrameAngleOf( tube, (Route[^1].Position - tube.Axis( 0f )).WithZ( 0f ).Normal );
				}
				else
				{
					// From a floor: pick a wall, walk to its edge, then start hopping.
					angle = Range( rng, -MathF.PI, MathF.PI );
					var topZ = MathF.Min( z, tube.ZMax );
					var edge = tube.Axis( topZ ) + tube.Dir( angle ) * MathF.Max( 1f, tube.Radius( angle, topZ ) - 1.5f );
					Route.Add( new CaveRoutePoint( edge.WithZ( z ), -1, 0 ) );
				}
				DescendTube( rng, tube, ref angle, ref z, floor + 3f, 0, onRoute: true, depthsHere );
				Route.Add( new CaveRoutePoint( Route[^1].Position.WithZ( floor ), -1, 0 ) );
				z = floor;
			}
			else if ( kind == CaveLegKind.Climb )
			{
				var tube = new LegTube( this, trunk, sampleStart, sampleEnd );
				if ( !tube.Valid )
					continue;
				var top = float.IsNaN( nextFloor ) ? tube.ZMax : nextFloor;
				angle = Range( rng, -MathF.PI, MathF.PI );
				AscendTube( rng, tube, ref angle, ref z, top, 0, onRoute: true );
				Route.Add( new CaveRoutePoint( trunk.Samples[Math.Min( trunk.Samples.Count - 1, sampleEnd )].Position.WithZ( top ), -1, 0 ) );
				z = top;
			}
			else
			{
				for ( var li = first; li <= last; li++ )
				{
					var leg = trunk.Legs[li];
					var floor = leg.FloorZ;
					if ( li == ChamberLeg )
					{
						Route.Add( new CaveRoutePoint( trunk.Samples[(leg.Start + leg.End) / 2].Position.WithZ( floor ), -1, 0 ) );
						z = floor;
						continue;
					}

					Route.Add( new CaveRoutePoint( trunk.Samples[leg.Start].Position.WithZ( floor ), -1, 0 ) );
					if ( leg.IsJunction )
						BuildJunctionBranches( rng, li, ref loopsLeft );
					Route.Add( new CaveRoutePoint( trunk.Samples[leg.End - 1].Position.WithZ( floor ), -1, 0 ) );
					z = floor;
				}
			}
		}

		if ( Settings.LightEveryLedges > 0 )
		{
			var n = 0;
			foreach ( var ledge in Ledges )
			{
				if ( !ledge.IsRoute || ledge.Kind == CaveLedgeKind.Doorstep )
					continue;
				if ( ++n % Settings.LightEveryLedges == 0 )
					ledge.HasLight = true;
			}
		}
	}

	/// <summary>Decoy dead ends and (while any are left) a loop, forking off a junction hall's side walls.</summary>
	void BuildJunctionBranches( Random rng, int legIndex, ref int loopsLeft )
	{
		var leg = Trunk.Legs[legIndex];
		var usable = leg.End - leg.Start - 2;
		if ( usable < 3 )
			return;

		var wanted = rng.Next( Settings.JunctionDecoysMin, Settings.JunctionDecoysMax + 1 );
		var placed = 0;
		for ( var attempt = 0; attempt < wanted * 6 && placed < wanted; attempt++ )
		{
			var ringStart = leg.Start + 1 + rng.Next( Math.Max( 1, usable - 2 ) );
			var mouth = MakeMouthSide( ringStart, rng.NextDouble() < 0.5 ? 1 : -1, Range( rng, 4f, 5.5f ), Passages.Count, isExit: false );
			if ( mouth is null )
				continue;
			var passage = BuildDeadEnd( rng, mouth );
			if ( passage is null )
				continue;
			leg.BranchPassages.Add( passage.Index );
			BranchLedges( rng, passage, mouth );
			placed++;
		}

		if ( loopsLeft <= 0 )
			return;
		for ( var attempt = 0; attempt < 4; attempt++ )
		{
			var ringStart = leg.Start + 1 + rng.Next( Math.Max( 1, usable - 2 ) );
			var mouth = MakeMouthSide( ringStart, rng.NextDouble() < 0.5 ? 1 : -1, Range( rng, 4f, 5.5f ), Passages.Count, isExit: false );
			if ( mouth is null )
				continue;
			var loop = BuildLoop( rng, mouth, legIndex );
			if ( loop is null )
				continue;
			leg.BranchPassages.Add( loop.Index );
			BranchLedges( rng, loop, mouth );
			loopsLeft--;
			break;
		}

		if ( rng.NextDouble() < Settings.JunctionPondChance && leg.HallRadius >= 12f )
		{
			var mid = Trunk.Samples[(leg.Start + leg.End) / 2];
			var dir = mid.Tangent.WithZ( 0f ).Normal;
			Bowls.Add( new CaveBowl
			{
				CenterMeters = mid.Position.WithZ( leg.FloorZ ),
				RadiusAlong = MathF.Min( leg.HallRadius * 0.3f, 6f ),
				RadiusAcross = MathF.Min( leg.HallRadius * 0.22f, 4f ),
				YawRadians = MathF.Atan2( dir.y, dir.x ),
				DepthMeters = 1.6f,
				OpenDirection = -dir,
				ReachMeters = MathF.Min( 6f, leg.HallRadius * 0.35f ),
			} );
		}
	}

	/// <summary>Depths for the dead-end rooms hung off drops: inside drop groups, apart from each other and from the groups' ends.</summary>
	List<float> PlanSideRooms( Random rng, List<(int first, int last)> groups )
	{
		var depths = new List<float>();
		var wanted = Math.Clamp( Settings.SideRoomCount, 0, 16 );
		var spans = new List<(float top, float bottom)>();
		foreach ( var (first, last) in groups )
		{
			if ( Trunk.Legs[first].Kind != CaveLegKind.Drop )
				continue;
			var top = Trunk.Samples[Trunk.Legs[first].Start].Position.z - 25f;
			var bottom = Trunk.Samples[Trunk.Legs[last].End - 1].Position.z + 30f;
			if ( top - bottom > 30f )
				spans.Add( (top, bottom) );
		}
		if ( spans.Count == 0 )
			return depths;

		var attempts = 0;
		while ( depths.Count < wanted && attempts++ < 300 )
		{
			var span = spans[rng.Next( spans.Count )];
			var z = Range( rng, span.bottom, span.top );
			var ok = true;
			foreach ( var d in depths )
			{
				if ( MathF.Abs( d - z ) < 40f ) { ok = false; break; }
			}
			if ( ok )
				depths.Add( z );
		}
		depths.Sort( ( a, b ) => b.CompareTo( a ) );
		return depths;
	}

	/// <summary>A dead-end room off a drop at roughly (angle, z): mouth + passage + branch ledges, or null.</summary>
	CaveMouth TrySideRoom( Random rng, LegTube tube, Vector3 wantOutward, float wantZ, float prevZ )
	{
		var throat = Range( rng, 4f, 5.5f );
		var holeRings = Math.Max( 2, (int)MathF.Ceiling( 2f * throat / Trunk.RingMeters ) );
		var k1 = tube.RingAt( wantZ );
		while ( k1 < Trunk.Samples.Count - 1 && Trunk.Samples[Math.Max( 0, k1 - holeRings )].Position.z > prevZ - 1.5f )
			k1++;

		var r = MathF.Max( 3f, tube.Profile( wantZ ) );
		var step = 4f / r;
		var wantAngle = FrameAngleOf( tube, wantOutward );
		var prevLanding = Route.Count > 0 ? Route[^1].Position : tube.Axis( prevZ );
		CaveMouth mouth = null;
		foreach ( var offset in new[] { 0f, step, -step, step * 2f, -step * 2f } )
		{
			mouth = MakeMouthVertical( tube.Dir( Wrap( wantAngle + offset ) ), k1, throat, Passages.Count );
			if ( mouth is not null && HoleClearOfLedges( mouth ) )
			{
				// The doorstep must be a real hop from the previous ledge: lower, and inside grapple reach.
				var step0 = MakeDoorstep( mouth );
				var drop = prevLanding.z - step0.ZMeters;
				var gap = HorizontalDistance( step0.LandingMeters, prevLanding );
				if ( drop >= 1.5f && drop <= Settings.GrappleDropMaxMeters && gap <= Settings.GrappleGapMaxMeters )
					break;
			}
			mouth = null;
		}
		if ( mouth is null )
			return null;

		var passage = BuildDeadEnd( rng, mouth );
		if ( passage is null )
			return null;

		BranchLedges( rng, passage, mouth );
		return mouth;
	}

	/// <summary>Ledges inside a passage's vertical legs (off the main route).</summary>
	void BranchLedges( Random rng, CavePassage passage, CaveMouth mouth )
	{
		for ( var i = 0; i < passage.Legs.Count; i++ )
		{
			var leg = passage.Legs[i];
			if ( leg.Kind == CaveLegKind.Walk )
				continue;

			var tube = new LegTube( this, passage, leg.Start, leg.End );
			if ( !tube.Valid )
				continue;

			var angle = Range( rng, -MathF.PI, MathF.PI );
			var prevFloor = i > 0 ? passage.Legs[i - 1].FloorZ : mouth.FloorZ;
			var nextFloor = i + 1 < passage.Legs.Count ? passage.Legs[i + 1].FloorZ : float.NaN;
			if ( leg.Kind == CaveLegKind.Climb )
			{
				var z = float.IsNaN( prevFloor ) ? tube.ZMin : prevFloor;
				var top = float.IsNaN( nextFloor ) ? tube.ZMax : nextFloor;
				AscendTube( rng, tube, ref angle, ref z, top, passage.Index, onRoute: false );
			}
			else
			{
				var z = float.IsNaN( prevFloor ) ? tube.ZMax : MathF.Min( prevFloor, tube.ZMax );
				var bottom = float.IsNaN( nextFloor ) ? tube.ZMin : MathF.Max( tube.ZMin, nextFloor + 2f );
				DescendTube( rng, tube, ref angle, ref z, bottom, passage.Index, onRoute: false, null );
			}
		}
	}

	// ---- fillers ---------------------------------------------------------------------------------

	/// <summary>Off-route ledges on the trunk's drops: options and decoys, clear of route ledges and mouths.</summary>
	void BuildFillers( Random rng )
	{
		var wanted = (int)MathF.Round( RouteLedgeCount * Math.Clamp( Settings.FillerFraction, 0f, 2f ) );
		var groups = TrunkGroups();
		var drops = groups.FindAll( g => Trunk.Legs[g.first].Kind == CaveLegKind.Drop );
		if ( drops.Count == 0 )
			return;

		var landings = new List<Vector3>();
		foreach ( var l in Ledges )
			landings.Add( l.LandingMeters );

		var attempts = 0;
		var placed = 0;
		while ( placed < wanted && attempts++ < wanted * 30 )
		{
			var (first, last) = drops[rng.Next( drops.Count )];
			var tube = new LegTube( this, Trunk, Trunk.Legs[first].Start, Trunk.Legs[last].End );
			if ( !tube.Valid || tube.ZMax - tube.ZMin < 20f )
				continue;
			var z = Range( rng, tube.ZMin + 6f, tube.ZMax - 6f );
			var angle = Range( rng, -MathF.PI, MathF.PI );
			if ( InsideMouth( tube.Dir( angle ), z, 3f ) )
				continue;

			var radius = tube.Radius( angle, z );
			var ledge = MakeLedge( rng, tube.Axis( z ) + tube.Dir( angle ) * radius, -tube.Dir( angle ), z, radius, grapple: false, ascending: false, 0 );
			ledge.WidthMeters *= 0.8f;

			var tooClose = false;
			foreach ( var other in landings )
			{
				if ( Vector3.DistanceBetween( other, ledge.LandingMeters ) < 6f ) { tooClose = true; break; }
			}
			if ( tooClose )
				continue;

			AddLedge( ledge, onRoute: false, 0 );
			landings.Add( ledge.LandingMeters );
			placed++;
		}
	}
}
