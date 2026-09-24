using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>The trunk: the archetype's chain of drops, chutes, rare landings, chimneys and (rarer) tunnels, swept as one tube.</summary>
public sealed partial class CaveLayout
{
	/// <summary>Leg description before sweeping: a straight run to <c>End</c> with a radius profile and an optional flat floor.</summary>
	readonly record struct LegSpec( Vector3 End, CaveLegKind Kind, float R0, float R1, float RPeak, float FloorZ, int Group, bool IsJunction );

	readonly record struct TrunkPersonality(
		float DropMin, float DropMax, float DriftMin, float DriftMax,
		float ChuteChance, float ChuteDriftMin, float ChuteDriftMax,
		float RadiusStep, float PinchChance, float PinchMul,
		float BulgeChance, float BulgeMin, float BulgeMax,
		float ClimbChance, float ClimbMin, float ClimbMax,
		float TunnelChance, float TunnelMin, float TunnelMax,
		float TurnMax );

	static TrunkPersonality PersonalityOf( CaveArchetype a ) => a switch
	{
		CaveArchetype.Shaft => new( 400f, 800f, 2f, 8f, 0.05f, 20f, 28f, 0.30f, 0.20f, 0.35f, 0.10f, 18f, 26f, 0.12f, 40f, 80f, 0.04f, 30f, 60f, 1.0f ),
		CaveArchetype.Stairs => new( 100f, 220f, 3f, 10f, 0.25f, 22f, 34f, 0.25f, 0.15f, 0.45f, 0.15f, 16f, 24f, 0.20f, 40f, 90f, 0.10f, 40f, 90f, 2.0f ),
		CaveArchetype.Serpent => new( 150f, 300f, 12f, 22f, 0.35f, 25f, 36f, 0.25f, 0.20f, 0.40f, 0.15f, 16f, 24f, 0.12f, 40f, 80f, 0.06f, 40f, 80f, 2.6f ),
		CaveArchetype.Chimneys => new( 150f, 300f, 4f, 10f, 0.15f, 20f, 30f, 0.30f, 0.25f, 0.35f, 0.10f, 16f, 24f, 0.65f, 60f, 150f, 0.05f, 40f, 80f, 1.6f ),
		_ => new( 120f, 250f, 4f, 10f, 0.15f, 20f, 30f, 0.20f, 0.10f, 0.50f, 0.45f, 22f, 36f, 0.15f, 40f, 80f, 0.10f, 60f, 120f, 2.0f ),
	};

	const float SubLegMeters = 45f;
	const float FloorCap = 10f;

	void BuildTrunk( Random rng )
	{
		var trunk = new CavePassage
		{
			Index = 0,
			Kind = CavePassageKind.Trunk,
			RingMeters = MathF.Max( 1f, Settings.TrunkRingMeters ),
			Segments = Math.Clamp( Settings.TrunkSegments, 16, 128 ),
			NoisePhase = Range( rng, 0f, MathF.PI * 2f ),
			RoomRadius = ChamberRadiusMeters,
		};
		Passages.Add( trunk );

		var legs = PlanTrunk( rng );
		Sweep( trunk, Vector3.Zero, Vector3.Down, legs );

		ChamberLeg = trunk.Legs.Count - 1;
		BottomZ = 0f;
		foreach ( var sm in trunk.Samples )
			BottomZ = MathF.Min( BottomZ, sm.Position.z );

		BuildChamber( rng );
	}

	/// <summary>
	/// The chain as a polyline of legs. It is one long descent: runs of 45 m sub-legs that drift, walk their radius
	/// (pinches, bulges) and now and then turn into a <b>chute</b> — a steep leaning run that carries the shaft sideways
	/// and turns its heading. Flat floors only appear where they are needed: a short landing under a chimney and on
	/// its apex, a rare tunnel, the few junction halls that host branches, and the chamber. Each straight run is
	/// checked against the polyline so far so the chain never crosses itself.
	/// </summary>
	List<LegSpec> PlanTrunk( Random rng )
	{
		var a = PersonalityOf( Archetype );
		var legs = new List<LegSpec>();
		var pts = new List<(Vector3 s, Vector3 e, float r)>();
		var pos = Vector3.Zero;
		var heading = Range( rng, -MathF.PI, MathF.PI );
		var radius = TopRadiusMeters;
		var group = 0;
		var floorTarget = -DepthMeters;
		var guard = 0;

		// Which drops (by index) end in a junction hall: spread through the middle of the cave.
		var junctionsWanted = Math.Clamp( Settings.JunctionCount, 0, 6 );
		var estimatedDrops = Math.Max( 2, (int)(DepthMeters / ((a.DropMin + a.DropMax) * 0.5f)) );
		var junctionDrops = new HashSet<int>();
		for ( var j = 0; j < junctionsWanted; j++ )
		{
			var slot = (int)MathF.Round( (j + 1) * estimatedDrops / (float)(junctionsWanted + 1) ) - 1;
			junctionDrops.Add( Math.Clamp( slot, 0, estimatedDrops - 2 ) );
		}

		void AddLeg( Vector3 end, CaveLegKind kind, float r0, float r1, float rPeak, float floorZ, bool junction )
		{
			legs.Add( new LegSpec( end, kind, r0, r1, rPeak, floorZ, group, junction ) );
			pts.Add( (pos, end, MathF.Max( r0, MathF.Max( r1, rPeak ) )) );
			pos = end;
		}

		bool Clear( Vector3 from, Vector3 to, float r, int skip = 1 )
		{
			// Every earlier run except the last `skip` (the one this run continues from shares a corner with it).
			var len = (to - from).Length;
			var steps = Math.Max( 1, (int)(len / 5f) );
			for ( var i = 0; i <= steps; i++ )
			{
				var p = from + (to - from) * (i / (float)steps);
				for ( var k = 0; k < pts.Count - skip; k++ )
				{
					var (s, e, rr) = pts[k];
					if ( SegmentDistance( p, s, e ) < r + rr + 8f )
						return false;
				}
			}
			return true;
		}

		Vector3 Horizontal( float h ) => new( MathF.Cos( h ), MathF.Sin( h ), 0f );

		var first = true;
		var dropIndex = 0;
		var chuteLeft = 0;
		var chuteHeading = heading;
		var bulgeLeft = 0;
		var bulgeTarget = 0f;
		var slimLeft = 0;      // sub-legs kept narrow right after a chimney apex
		var skipNext = 1;      // trailing legs the next sub-leg's self-check ignores
		const float MaxRadiusRate = 0.4f; // meters of radius per meter of height: keeps the wall within hop reach

		while ( pos.z > floorTarget + 25f && guard++ < 300 )
		{
			// ---- drop (with chutes) ----
			var remaining = pos.z - floorTarget;
			var dropLen = MathF.Min( Range( rng, a.DropMin, a.DropMax ), remaining );
			if ( remaining - dropLen < 60f )
				dropLen = remaining;
			var subCount = Math.Max( 1, (int)MathF.Round( dropLen / SubLegMeters ) );
			var subLen = dropLen / subCount;
			var driftHeading = heading + Range( rng, -1f, 1f );
			var baseRadius = first ? TopRadiusMeters : Range( rng, 8f, 25f );

			for ( var i = 0; i < subCount; i++ )
			{
				float drift;
				if ( chuteLeft > 0 )
				{
					chuteLeft--;
					chuteHeading += Range( rng, -0.25f, 0.25f );
					driftHeading = chuteHeading;
					drift = Range( rng, a.ChuteDriftMin, a.ChuteDriftMax ) * (subLen / SubLegMeters);
				}
				else
				{
					if ( !first && rng.NextDouble() < a.ChuteChance )
					{
						chuteLeft = rng.Next( 2, 5 );
						chuteHeading = heading + (rng.NextDouble() < 0.5 ? 1f : -1f) * Range( rng, 0.3f, a.TurnMax );
						heading = chuteHeading;
					}
					driftHeading += Range( rng, -0.8f, 0.8f );
					drift = first && i == 0 ? 0f : Range( rng, a.DriftMin, a.DriftMax ) * (subLen / SubLegMeters);
				}

				float r1;
				if ( bulgeLeft > 0 )
				{
					// A cavern: head for the target radius, then back to the base, a few sub-legs each way.
					bulgeLeft--;
					r1 = bulgeLeft >= 2 ? bulgeTarget : baseRadius;
				}
				else
				{
					r1 = MathF.Exp( MathF.Log( radius ) + 0.5f * (MathF.Log( baseRadius ) - MathF.Log( radius )) + Range( rng, -a.RadiusStep, a.RadiusStep ) );
					if ( rng.NextDouble() < a.PinchChance )
						r1 *= rng.NextDouble() < 0.5 ? a.PinchMul : 1.5f;
					if ( !first && rng.NextDouble() < a.BulgeChance )
					{
						bulgeLeft = 4;
						bulgeTarget = Range( rng, a.BulgeMin, a.BulgeMax );
						r1 = bulgeTarget;
					}
				}
				r1 = Math.Clamp( r1, radius - MaxRadiusRate * subLen, radius + MaxRadiusRate * subLen );
				r1 = Math.Clamp( r1, Settings.MinRadiusMeters, Settings.MaxRadiusMeters );
				if ( slimLeft > 0 )
				{
					slimLeft--;
					r1 = MathF.Min( r1, 10f );
					bulgeLeft = 0;
				}
				var peak = MathF.Max( radius, r1 ) * Range( rng, 1f, 1.08f );

				var skip = skipNext;
				skipNext = 1;
				var end = pos + Horizontal( driftHeading ) * drift + Vector3.Down * subLen;
				for ( var tries = 0; tries < 6 && !Clear( pos, end, MathF.Max( peak, r1 ), skip ); tries++ )
				{
					driftHeading += 1.3f;
					chuteHeading = driftHeading;
					end = pos + Horizontal( driftHeading ) * drift + Vector3.Down * subLen;
				}
				if ( !Clear( pos, end, MathF.Max( peak, r1 ), skip ) )
				{
					end = pos + Vector3.Down * subLen;
					chuteLeft = 0;
				}
				AddLeg( end, CaveLegKind.Drop, radius, r1, peak, float.NaN, false );
				radius = r1;
			}
			first = false;

			if ( pos.z <= floorTarget + 25f )
				break;

			// ---- connector ----
			var junction = junctionDrops.Contains( dropIndex );
			dropIndex++;
			var wantClimb = rng.NextDouble() < a.ClimbChance && pos.z - floorTarget > 200f;
			var wantTunnel = !wantClimb && rng.NextDouble() < a.TunnelChance;

			if ( !junction && !wantClimb && !wantTunnel )
			{
				// Straight on: the next drop continues the same tube (same group), kicked into a chute so the heading turns.
				chuteLeft = rng.Next( 2, 4 );
				chuteHeading = heading + (rng.NextDouble() < 0.5 ? 1f : -1f) * Range( rng, 0.4f, a.TurnMax );
				heading = chuteHeading;
				continue;
			}

			group++;
			heading += (rng.NextDouble() < 0.5 ? 1f : -1f) * Range( rng, 0.3f, a.TurnMax );

			// A flat floor: a junction hall (branches fork here) or a short landing under a chimney / before a tunnel.
			var landingR = Range( rng, 5f, 9f );
			var landingLen = junction ? Range( rng, 25f, 40f ) : wantClimb ? MathF.Max( Range( rng, 18f, 28f ), radius + 18f ) : Range( rng, 10f, 16f );
			var hallR = junction ? Range( rng, 12f, 20f ) : 0f;
			var floor = pos.z - MathF.Min( FloorCap, 0.55f * radius );
			var landingEnd = (pos + Horizontal( heading ) * landingLen).WithZ( floor + 0.55f * landingR );
			for ( var tries = 0; tries < 8 && !Clear( pos, landingEnd, MathF.Max( landingR, hallR ) ); tries++ )
			{
				heading += 0.8f;
				landingEnd = (pos + Horizontal( heading ) * landingLen).WithZ( floor + 0.55f * landingR );
			}
			AddLeg( landingEnd, CaveLegKind.Walk, radius, landingR, MathF.Max( radius, MathF.Max( landingR, hallR ) ), floor, junction );
			radius = landingR;
			group++;

			if ( wantClimb )
			{
				var climb = Range( rng, a.ClimbMin, a.ClimbMax );
				var topR = Range( rng, 3.5f, 5f );
				var climbEnd = pos + Vector3.Up * climb + Horizontal( heading ) * MathF.Min( climb * 0.5f, Range( rng, 20f, 40f ) );
				for ( var tries = 0; tries < 6 && !Clear( pos, climbEnd, radius * 1.3f, 2 ); tries++ )
				{
					heading += 0.9f;
					climbEnd = pos + Vector3.Up * climb + Horizontal( heading ) * MathF.Min( climb * 0.5f, Range( rng, 20f, 40f ) );
				}
				if ( Clear( pos, climbEnd, radius * 1.3f, 2 ) )
				{
					AddLeg( climbEnd, CaveLegKind.Climb, radius, topR, Range( rng, 6f, 10f ), float.NaN, false );
					radius = topR;
					group++;

					// Apex landing: a few meters of floor at the top, continuing away from the chimney's base.
					var leanHeading = heading;
					heading += Range( rng, -0.6f, 0.6f );
					var apexLen = Range( rng, 10f, 14f );
					var apexFloor = pos.z - 0.55f * radius;
					var apexEnd = pos + Horizontal( heading ) * apexLen;
					for ( var tries = 0; tries < 8 && !Clear( pos, apexEnd, radius ); tries++ )
					{
						heading += 0.8f;
						apexEnd = pos + Horizontal( heading ) * apexLen;
					}
					AddLeg( apexEnd, CaveLegKind.Walk, radius, radius, radius * 1.1f, apexFloor, false );
					group++;

					// The drop that follows must get clear of the chimney: chute away from its base, slim, and ignore the
					// apex + chimney in its first self-check (they are adjacent by construction).
					chuteLeft = 3;
					chuteHeading = leanHeading;
					heading = leanHeading;
					slimLeft = 3;
					skipNext = 2;
					continue;
				}
			}

			if ( wantTunnel )
			{
				var tLen = Range( rng, a.TunnelMin, a.TunnelMax );
				var tR = Range( rng, 5f, 10f );
				var cavern = rng.NextDouble() < 0.5 ? Range( rng, a.BulgeMin, a.BulgeMax ) : 0f;
				var tFloor = floor;
				var tEnd = (pos + Horizontal( heading ) * tLen).WithZ( tFloor + 0.55f * tR );
				for ( var tries = 0; tries < 8 && !Clear( pos, tEnd, MathF.Max( tR, cavern ) ); tries++ )
				{
					heading += 0.8f;
					tEnd = (pos + Horizontal( heading ) * tLen).WithZ( tFloor + 0.55f * tR );
				}
				AddLeg( tEnd, CaveLegKind.Walk, radius, tR, MathF.Max( radius, MathF.Max( tR, cavern ) ), tFloor, false );
				radius = tR;
				group++;
			}
		}

		// ---- chamber ----
		{
			heading += Range( rng, -0.6f, 0.6f );
			var len = ChamberRadiusMeters * 2.4f;
			var end = pos + Horizontal( heading ) * len;
			for ( var tries = 0; tries < 8 && !Clear( pos, end, ChamberRadiusMeters ); tries++ )
			{
				heading += 0.8f;
				end = pos + Horizontal( heading ) * len;
			}
			var floor = pos.z - MathF.Min( FloorCap, 0.55f * radius );
			AddLeg( end, CaveLegKind.Walk, radius, ChamberRadiusMeters * 0.3f, ChamberRadiusMeters, floor, false );
		}

		return legs;
	}

	static float SegmentDistance( Vector3 p, Vector3 a, Vector3 b )
	{
		var ab = b - a;
		var len2 = ab.LengthSquared;
		if ( len2 < 1e-6f )
			return (p - a).Length;
		var t = Math.Clamp( Vector3.Dot( p - a, ab ) / len2, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}

	/// <summary>The oasis at the bottom of the chamber: pond against one wall, waterfall crack above it, chest on the bank.</summary>
	void BuildChamber( Random rng )
	{
		var trunk = Trunk;
		var leg = trunk.Legs[ChamberLeg];
		var mid = trunk.Samples[(leg.Start + leg.End) / 2];
		var floor = leg.FloorZ;
		var u = mid.U.WithZ( 0f ).Normal;
		if ( u.LengthSquared < 0.5f )
			u = Vector3.Cross( Vector3.Up, mid.Tangent ).WithZ( 0f ).Normal;
		if ( rng.NextDouble() < 0.5 )
			u = -u;
		var r = mid.Radius;
		var along = Range( rng, 6f, 8f );
		var across = Range( rng, 9f, 12f );

		var bowl = new CaveBowl
		{
			CenterMeters = mid.Position.WithZ( floor ) + u * (r - along - 3f),
			RadiusAlong = along,
			RadiusAcross = across,
			YawRadians = MathF.Atan2( u.y, u.x ),
			DepthMeters = 2.2f,
			OpenDirection = -u,
			ReachMeters = 10f,
		};
		ChamberBowl = Bowls.Count;
		Bowls.Add( bowl );

		WaterfallHeightMeters = Range( rng, 16f, 24f );
		var side = SideOf( mid, u );
		var phi = AngleAtHeight( mid, side, floor + WaterfallHeightMeters - mid.Position.z );
		WaterfallCrackMeters = mid.Position + (mid.U * MathF.Cos( phi ) + mid.V * MathF.Sin( phi )) * r;
		WaterfallOutward = u;

		ChamberChestMeters = mid.Position.WithZ( floor ) + u * (r - 3f - along * 2f - 7f);
		ChamberChestYawDegrees = MathF.Atan2( u.y, u.x ).RadianToDegree();
	}

	// ---- sweep -----------------------------------------------------------------------------------

	/// <summary>
	/// Sample a tube spine: straight legs with the corners rounded (quadratic curves sized to the tube), constant ring
	/// spacing, parallel-transported frames (continuous, so rings never twist; consumers that need "up" or "sideways"
	/// in a ring ask UpAngle / AngleAtHeight), a per-leg radius profile (<c>R0 → R1</c> with an <c>RPeak</c> bulge in
	/// the middle), and a flat floor in walk legs that also reaches 1.6 radii into the neighbouring drop / chimney so
	/// shaft bottoms are flat.
	/// </summary>
	void Sweep( CavePassage passage, Vector3 start, Vector3 startDir, List<LegSpec> legs )
	{
		var spacing = passage.RingMeters;

		var pts = new List<Vector3> { start };
		foreach ( var leg in legs )
			pts.Add( leg.End );

		var path = new List<(Vector3 p, int leg)>();
		for ( var i = 0; i < legs.Count; i++ )
		{
			var p0 = pts[i];
			var p1 = pts[i + 1];
			var legLen = (p1 - p0).Length;
			var dirIn = (p1 - p0).Normal;

			var cutStart = i > 0 ? CornerCut( pts[i - 1], p0, p1, MathF.Max( legs[i - 1].R1, legs[i].R0 ) ) : 0f;
			var cutEnd = i < legs.Count - 1 ? CornerCut( p0, p1, pts[i + 2], MathF.Max( legs[i].R1, legs[i + 1].R0 ) ) : 0f;
			if ( cutStart + cutEnd > legLen * 0.9f )
			{
				var k = legLen * 0.9f / (cutStart + cutEnd);
				cutStart *= k;
				cutEnd *= k;
			}

			var s0 = p0 + dirIn * cutStart;
			var s1 = p1 - dirIn * cutEnd;
			var straight = (s1 - s0).Length;
			var steps = Math.Max( 1, (int)MathF.Ceiling( straight / spacing ) );
			for ( var k = 0; k <= steps; k++ )
			{
				if ( k == 0 && path.Count > 0 )
					continue;
				path.Add( (s0 + (s1 - s0) * (k / (float)steps), i) );
			}

			if ( i < legs.Count - 1 )
			{
				var dirOut = (pts[i + 2] - p1).Normal;
				var nextCut = CornerCut( p0, p1, pts[i + 2], MathF.Max( legs[i].R1, legs[i + 1].R0 ) );
				var nextLen = (pts[i + 2] - p1).Length;
				nextCut = MathF.Min( nextCut, nextLen * 0.45f );
				var e = p1 + dirOut * nextCut;
				var arc = (e - s1).Length;
				var arcSteps = Math.Max( 4, (int)MathF.Ceiling( arc / spacing * 1.2f ) );
				for ( var k = 1; k <= arcSteps; k++ )
				{
					var t = k / (float)arcSteps;
					var q = s1 * ((1f - t) * (1f - t)) + p1 * (2f * (1f - t) * t) + e * (t * t);
					path.Add( (q, t < 0.5f ? i : i + 1) );
				}
			}
		}

		var cum = new List<float> { 0f };
		for ( var i = 1; i < path.Count; i++ )
			cum.Add( cum[i - 1] + (path[i].p - path[i - 1].p).Length );
		var total = cum[^1];
		var count = Math.Max( 2, (int)MathF.Floor( total / spacing ) + 1 );

		var legFirstS = new float[legs.Count];
		var legLastS = new float[legs.Count];
		for ( var i = 0; i < legs.Count; i++ ) { legFirstS[i] = -1f; legLastS[i] = -1f; }
		for ( var i = 0; i < path.Count; i++ )
		{
			var l = path[i].leg;
			if ( legFirstS[l] < 0f ) legFirstS[l] = cum[i];
			legLastS[l] = cum[i];
		}

		var legStarts = new int[legs.Count];
		var legEnds = new int[legs.Count];
		for ( var i = 0; i < legs.Count; i++ ) { legStarts[i] = -1; legEnds[i] = -1; }

		var cursor = 0;
		for ( var n = 0; n < count; n++ )
		{
			var s = MathF.Min( total, n * spacing );
			while ( cursor < path.Count - 2 && cum[cursor + 1] < s )
				cursor++;
			var seg = MathF.Max( 1e-4f, cum[cursor + 1] - cum[cursor] );
			var t = Math.Clamp( (s - cum[cursor]) / seg, 0f, 1f );
			var pos = path[cursor].p + (path[cursor + 1].p - path[cursor].p) * t;
			var leg = t < 0.5f ? path[cursor].leg : path[cursor + 1].leg;
			var spec = legs[leg];
			var legLen = legLastS[leg] - legFirstS[leg];
			var lt = legLen > 0.01f ? Math.Clamp( (s - legFirstS[leg]) / legLen, 0f, 1f ) : 0f;
			var baseR = spec.R0 + (spec.R1 - spec.R0) * lt;
			var bulge = MathF.Pow( MathF.Max( 0f, MathF.Sin( lt * MathF.PI ) ), 0.8f );
			var radius = baseR + MathF.Max( 0f, spec.RPeak - baseR ) * bulge;

			var floor = float.NaN;
			if ( spec.Kind == CaveLegKind.Walk )
				floor = spec.FloorZ;
			else
			{
				if ( leg + 1 < legs.Count && legs[leg + 1].Kind == CaveLegKind.Walk && legLastS[leg] - s < 1.6f * spec.R1 )
					floor = legs[leg + 1].FloorZ;
				if ( leg > 0 && legs[leg - 1].Kind == CaveLegKind.Walk && s - legFirstS[leg] < 1.6f * spec.R0 )
					floor = legs[leg - 1].FloorZ;
			}

			passage.Samples.Add( new CaveSpineSample { Position = pos, Radius = radius, S = s, Leg = leg, FloorZ = floor } );
			if ( legStarts[leg] < 0 ) legStarts[leg] = n;
			legEnds[leg] = n + 1;
		}

		var samples = passage.Samples;
		for ( var i = 0; i < samples.Count; i++ )
		{
			var prev = samples[Math.Max( 0, i - 1 )].Position;
			var next = samples[Math.Min( samples.Count - 1, i + 1 )].Position;
			var tangent = (next - prev).Normal;
			if ( tangent.LengthSquared < 1e-6f )
				tangent = startDir;
			samples[i].Tangent = tangent;
		}

		var uPrev = Vector3.Cross( Vector3.Up, samples[0].Tangent );
		if ( uPrev.LengthSquared < 1e-4f )
			uPrev = Vector3.Forward;
		uPrev = uPrev.Normal;
		for ( var i = 0; i < samples.Count; i++ )
		{
			var t = samples[i].Tangent;
			var u = uPrev - t * Vector3.Dot( uPrev, t );
			if ( u.LengthSquared < 1e-6f )
				u = Vector3.Cross( Vector3.Up, t );
			if ( u.LengthSquared < 1e-6f )
				u = Vector3.Forward;
			u = u.Normal;
			var v = Vector3.Cross( t, u ).Normal;
			samples[i].U = u;
			samples[i].V = v;
			uPrev = u;
		}

		for ( var i = 0; i < legs.Count; i++ )
		{
			if ( legStarts[i] < 0 )
				continue;
			passage.Legs.Add( new CaveLeg
			{
				Start = legStarts[i],
				End = legEnds[i],
				Kind = legs[i].Kind,
				FloorZ = legs[i].FloorZ,
				Group = legs[i].Group,
				HallRadius = legs[i].RPeak > MathF.Max( legs[i].R0, legs[i].R1 ) + 2f ? legs[i].RPeak : 0f,
				IsJunction = legs[i].IsJunction,
			} );
		}
	}

	/// <summary>How far back from a corner the straight run stops so a curve of radius ≈ 1.4 × tube radius fits.</summary>
	static float CornerCut( Vector3 a, Vector3 b, Vector3 c, float tubeRadius )
	{
		var din = (b - a).Normal;
		var dout = (c - b).Normal;
		var cos = Math.Clamp( Vector3.Dot( din, dout ), -1f, 1f );
		var theta = MathF.Acos( cos );
		if ( theta < 0.05f )
			return 0f;
		var bend = MathF.Max( 6f, tubeRadius * 1.4f );
		var cut = bend * MathF.Tan( MathF.Min( theta, 2.6f ) * 0.5f );
		var shortest = MathF.Min( (b - a).Length, (c - b).Length );
		return MathF.Min( cut, shortest * 0.45f );
	}
}
