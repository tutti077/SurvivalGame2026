using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Mouths cut into the trunk and the passages hung off them: dead-end rooms and loops.</summary>
public sealed partial class CaveLayout
{
	// ---- mouths ----------------------------------------------------------------------------------

	/// <summary>
	/// A hole on a drop wall of the trunk: rings <c>[floorRing - h, floorRing)</c> × a segment span around
	/// <paramref name="wantAngle"/> (frame angle). The floor is the bottom ring's highest boundary vertex. Null when the
	/// rings are not a straight drop, or the hole would overlap another mouth. Unregistered until its passage exists.
	/// </summary>
	CaveMouth MakeMouthVertical( Vector3 wantOutward, int floorRing, float throat, int passage )
	{
		var host = Trunk;
		var ring = host.RingMeters;
		var h = Math.Max( 2, (int)MathF.Ceiling( 2f * throat / ring ) );
		throat = MathF.Max( throat, h * ring * 0.5f + 0.3f );
		var k1 = floorRing;
		var k0 = k1 - h;
		if ( k0 < 2 || k1 >= host.Samples.Count - 2 )
			return null;

		for ( var k = k0; k <= k1; k++ )
		{
			var sm = host.Samples[k];
			if ( MathF.Abs( sm.Tangent.z ) < 0.75f || host.Legs[sm.Leg].Kind != CaveLegKind.Drop || !float.IsNaN( sm.FloorZ ) )
				return null;
		}

		var mid = host.Samples[(k0 + k1) / 2];
		var step = 2f * MathF.PI / host.Segments;
		var wantAngle = FrameAngle( mid, wantOutward );
		var w = Math.Clamp( (int)MathF.Ceiling( 2f * throat / (step * mid.Radius) ), 2, host.Segments / 3 );
		var m0 = (int)MathF.Floor( (wantAngle + MathF.PI) / step - w * 0.5f );
		m0 = ((m0 % host.Segments) + host.Segments) % host.Segments;

		if ( MouthOverlaps( k0, k1, m0, w ) )
			return null;

		var midPhi = SegmentAngle( host, m0 ) + w * 0.5f * step;
		var outward = (mid.U * MathF.Cos( midPhi ) + mid.V * MathF.Sin( midPhi )).WithZ( 0f ).Normal;
		if ( outward.LengthSquared < 0.5f )
			return null;
		var center = TubePoint( host, (k0 + k1) / 2, midPhi );

		var floorZ = float.MinValue;
		var start = 0f;
		for ( var m = m0; m <= m0 + w; m++ )
		{
			for ( var k = k0; k <= k1; k++ )
			{
				var pnt = TubePoint( host, k, SegmentAngle( host, m % host.Segments ) );
				start = MathF.Max( start, Vector3.Dot( pnt - center, outward ) );
				if ( k == k1 )
					floorZ = MathF.Max( floorZ, pnt.z );
			}
		}

		return new CaveMouth
		{
			Host = 0,
			Passage = passage,
			RingStart = k0,
			RingEnd = k1,
			SegStart = m0,
			SegCount = w,
			FloorZ = floorZ,
			CenterMeters = center,
			OutwardMeters = outward,
			StartMeters = start + 1f,
			ThroatRadius = throat,
			OnVerticalWall = true,
		};
	}

	/// <summary>
	/// A hole on the side wall of a walk leg of the trunk (tunnel / hall): rings <c>[ringStart, ringStart + w)</c> along
	/// the leg × the segments from just under the floor up to two throats above it, on <paramref name="side"/> (+1 = +U).
	/// </summary>
	CaveMouth MakeMouthSide( int ringStart, int side, float throat, int passage, bool isExit )
	{
		var host = Trunk;
		var ring = host.RingMeters;
		var w = Math.Max( 2, (int)MathF.Ceiling( 2f * throat / ring ) );
		var k0 = ringStart;
		var k1 = ringStart + w;
		if ( k0 < 1 || k1 >= host.Samples.Count - 1 )
			return null;

		var floorZ = float.NaN;
		var radius = 0f;
		for ( var k = k0; k <= k1; k++ )
		{
			var sm = host.Samples[k];
			if ( MathF.Abs( sm.Tangent.z ) > 0.3f || host.Legs[sm.Leg].Kind != CaveLegKind.Walk || float.IsNaN( sm.FloorZ ) )
				return null;
			if ( float.IsNaN( floorZ ) )
				floorZ = sm.FloorZ;
			else if ( MathF.Abs( floorZ - sm.FloorZ ) > 0.01f )
				return null;
			radius = MathF.Max( radius, sm.Radius );
		}

		var mid = host.Samples[(k0 + k1) / 2];
		var step = 2f * MathF.PI / host.Segments;
		// Heights above the axis: one segment under the floor (so the bottom edge is clamped floor) up to two throats above it.
		var hLo = MathF.Max( -mid.Radius * 0.98f, floorZ - mid.Position.z - step * mid.Radius * 1.05f );
		var hHi = MathF.Min( mid.Radius * 0.95f, floorZ + 2f * throat - mid.Position.z );
		var phiLo = AngleAtHeight( mid, side, hLo );
		var phiHi = AngleAtHeight( mid, side, hHi );

		// On side +1 angles grow with height, on side -1 they shrink: walk from the lower angle to the higher one.
		var first = side > 0 ? phiLo : phiHi;
		var last = side > 0 ? phiHi : phiLo;
		var m0 = SegmentIndex( host, first );
		var count = SegmentIndex( host, last ) - m0;
		count = ((count % host.Segments) + host.Segments) % host.Segments;
		count = Math.Clamp( count, 2, host.Segments / 3 );

		if ( MouthOverlaps( k0, k1, m0, count ) )
			return null;

		var midPhi = SegmentAngle( host, m0 ) + count * 0.5f * step;
		var outward = (mid.U * MathF.Cos( midPhi ) + mid.V * MathF.Sin( midPhi )).WithZ( 0f ).Normal;
		if ( outward.LengthSquared < 0.5f )
			return null;
		var center = TubePoint( host, (k0 + k1) / 2, midPhi );

		var start = 0f;
		for ( var m = m0; m <= m0 + count; m++ )
		{
			for ( var k = k0; k <= k1; k++ )
			{
				var pnt = TubePoint( host, k, SegmentAngle( host, m % host.Segments ) );
				start = MathF.Max( start, Vector3.Dot( pnt - center, outward ) );
			}
		}

		return new CaveMouth
		{
			Host = 0,
			Passage = passage,
			IsExit = isExit,
			RingStart = k0,
			RingEnd = k1,
			SegStart = m0,
			SegCount = count,
			FloorZ = floorZ,
			CenterMeters = center,
			OutwardMeters = outward,
			StartMeters = start + 1f,
			ThroatRadius = MathF.Max( throat, (phiHi - phiLo) * mid.Radius * 0.5f ),
			OnVerticalWall = false,
		};
	}

	bool MouthOverlaps( int k0, int k1, int m0, int w )
	{
		var n = Trunk.Segments;
		foreach ( var other in Mouths )
		{
			if ( k0 > other.RingEnd || k1 < other.RingStart )
				continue;
			for ( var i = 0; i < w; i++ )
			{
				var mi = (m0 + i) % n;
				for ( var j = 0; j < other.SegCount; j++ )
				{
					if ( mi == (other.SegStart + j) % n )
						return true;
				}
			}
		}
		return false;
	}

	int RegisterMouth( CaveMouth mouth )
	{
		mouth.Index = Mouths.Count;
		Mouths.Add( mouth );
		return mouth.Index;
	}

	// ---- dead ends -------------------------------------------------------------------------------

	/// <summary>A dead-end room off a mouth: a short walk out, then straight into the room, a chimney up into it, or a drop down into it; domed shut.</summary>
	CavePassage BuildDeadEnd( Random rng, CaveMouth mouth )
	{
		for ( var attempt = 0; attempt < 6; attempt++ )
		{
			var throat = mouth.ThroatRadius;
			var roomR = Range( rng, 10f, 25f );
			var l1 = Range( rng, 15f, 30f ) + attempt * 4f;
			var l2 = roomR * Range( rng, 2.5f, 4f );
			var variant = rng.NextDouble();
			var a = mouth.CenterMeters;
			var dA = mouth.OutwardMeters;
			var p1 = a + dA * l1;

			var legs = new List<LegSpec> { new( p1, CaveLegKind.Walk, throat, throat, throat * 1.15f, mouth.FloorZ, 0, false ) };
			var roomStart = p1;
			var roomDir = dA;
			if ( variant < 0.35 )
			{
				var p2 = p1 + Vector3.Up * Range( rng, 15f, 40f );
				legs.Add( new( p2, CaveLegKind.Climb, throat, throat, throat * Range( rng, 1.2f, 1.6f ), float.NaN, 1, false ) );
				roomStart = p2;
				roomDir = Turn( dA, Range( rng, -1f, 1f ) );
			}
			else if ( variant < 0.55 )
			{
				var p2 = p1 - Vector3.Up * Range( rng, 10f, 30f );
				legs.Add( new( p2, CaveLegKind.Drop, throat, throat, throat * Range( rng, 1.2f, 1.6f ), float.NaN, 1, false ) );
				roomStart = p2;
				roomDir = Turn( dA, Range( rng, -1f, 1f ) );
			}

			var roomEnd = roomStart + roomDir * l2;
			var roomFloor = legs.Count == 1 ? mouth.FloorZ : roomStart.z - throat * 0.55f;
			legs.Add( new( roomEnd, CaveLegKind.Walk, throat, MathF.Max( 1.5f, roomR * 0.2f ), roomR, roomFloor, 2, false ) );

			var passage = new CavePassage
			{
				Index = Passages.Count,
				Kind = CavePassageKind.DeadEnd,
				RoomRadius = roomR,
				NoisePhase = Range( rng, 0f, MathF.PI * 2f ),
				RingMeters = MathF.Max( 1f, Settings.PassageRingMeters ),
				Segments = Math.Clamp( Settings.PassageSegments, 12, 64 ),
			};
			Sweep( passage, a + dA * mouth.StartMeters, dA, legs );
			if ( !PassageClear( passage, throat * 2f + 8f, 0f ) )
				continue;

			mouth.Passage = passage.Index;
			passage.MouthA = RegisterMouth( mouth );
			Passages.Add( passage );
			DressRoom( rng, passage, legs.Count - 1, chestSide: false );
			return passage;
		}

		return null;
	}

	// ---- loops -----------------------------------------------------------------------------------

	/// <summary>
	/// A loop from a junction side mouth to an exit mouth on a later walk leg of the trunk: out, vertically to the
	/// exit's floor level, across through a gallery, and back in. Null when no exit fits or nothing clears.
	/// </summary>
	CavePassage BuildLoop( Random rng, CaveMouth entry, int fromLeg )
	{
		var trunk = Trunk;
		var candidates = new List<int>();
		for ( var i = fromLeg + 1; i < trunk.Legs.Count - 1 && candidates.Count < 4; i++ )
		{
			if ( trunk.Legs[i].Kind == CaveLegKind.Walk && trunk.Legs[i].End - trunk.Legs[i].Start >= 5 )
				candidates.Add( i );
		}
		if ( candidates.Count == 0 )
			return null;

		for ( var attempt = 0; attempt < 10; attempt++ )
		{
			var exitLeg = trunk.Legs[candidates[rng.Next( candidates.Count )]];
			var ringStart = rng.Next( exitLeg.Start + 1, Math.Max( exitLeg.Start + 2, exitLeg.End - 4 ) );
			var throatB = Range( rng, 4f, 5.5f );
			var passageIndex = Passages.Count;
			var exit = MakeMouthSide( ringStart, rng.NextDouble() < 0.5 ? 1 : -1, throatB, passageIndex, isExit: true );
			if ( exit is null )
				continue;

			var a = entry.CenterMeters;
			var dA = entry.OutwardMeters;
			var b = exit.CenterMeters;
			var dB = exit.OutwardMeters;
			var throatA = entry.ThroatRadius;
			var galleryR = Range( rng, 12f, 22f );
			var l1 = Range( rng, 25f, 45f ) + attempt * 3f;
			var l3 = Range( rng, 25f, 45f );

			var p1 = a + dA * l1;
			var p4 = b + dB * l3;
			var dz = p4.z - p1.z;
			var legs = new List<LegSpec> { new( p1, CaveLegKind.Walk, throatA, throatA, throatA * 1.15f, entry.FloorZ, 0, false ) };
			var group = 1;
			var cursor = p1;
			var radius = throatA;
			if ( MathF.Abs( dz ) > 8f )
			{
				var p2 = p1.WithZ( p4.z );
				var kind = dz > 0f ? CaveLegKind.Climb : CaveLegKind.Drop;
				var r1 = Range( rng, 3.5f, 5f );
				legs.Add( new( p2, kind, radius, r1, Range( rng, 6f, 10f ), float.NaN, group++, false ) );
				cursor = p2;
				radius = r1;
			}
			if ( (p4 - cursor).WithZ( 0f ).Length < 20f )
				continue;

			var galleryFloor = cursor.z - radius * 0.55f;
			legs.Add( new( p4.WithZ( cursor.z ), CaveLegKind.Walk, radius, throatB, galleryR, galleryFloor, group++, false ) );
			if ( MathF.Abs( cursor.z - p4.z ) > 0.5f )
				continue;
			legs.Add( new( b + dB * exit.StartMeters, CaveLegKind.Walk, throatB, throatB, throatB * 1.15f, exit.FloorZ, group++, false ) );

			// The gallery floor and the exit floor must agree, else the walk out of the gallery steps.
			if ( MathF.Abs( galleryFloor - exit.FloorZ ) > 3f )
			{
				legs[^2] = new( legs[^2].End, CaveLegKind.Walk, radius, throatB, galleryR, exit.FloorZ, legs[^2].Group, false );
			}

			var passage = new CavePassage
			{
				Index = passageIndex,
				Kind = CavePassageKind.Loop,
				RoomRadius = galleryR,
				NoisePhase = Range( rng, 0f, MathF.PI * 2f ),
				RingMeters = MathF.Max( 1f, Settings.PassageRingMeters ),
				Segments = Math.Clamp( Settings.PassageSegments, 12, 64 ),
			};
			Sweep( passage, a + dA * entry.StartMeters, dA, legs );
			if ( !PassageClear( passage, throatA * 2f + 8f, throatB * 2f + 8f ) )
				continue;

			passage.MouthA = RegisterMouth( entry );
			passage.MouthB = RegisterMouth( exit );
			Passages.Add( passage );
			DressRoom( rng, passage, legs.Count - 2, chestSide: true );
			return passage;
		}

		return null;
	}

	static Vector3 Turn( Vector3 dir, float radians )
	{
		var c = MathF.Cos( radians );
		var s = MathF.Sin( radians );
		return new Vector3( dir.x * c - dir.y * s, dir.x * s + dir.y * c, 0f );
	}

	/// <summary>Chest, lights and (sometimes) a pond in a passage's room leg.</summary>
	void DressRoom( Random rng, CavePassage passage, int legIndex, bool chestSide )
	{
		if ( legIndex < 0 || legIndex >= passage.Legs.Count )
			return;
		var leg = passage.Legs[legIndex];
		if ( leg.End <= leg.Start )
			return;

		var mid = passage.Samples[(leg.Start + leg.End) / 2];
		var roomDir = mid.Tangent.WithZ( 0f ).Normal;
		if ( roomDir.LengthSquared < 0.5f )
			roomDir = passage.Samples[leg.Start].Tangent.WithZ( 0f ).Normal;
		var side = new Vector3( -roomDir.y, roomDir.x, 0f );
		var floor = float.IsNaN( leg.FloorZ ) ? mid.Position.z - mid.Radius * 0.55f : leg.FloorZ;

		var pond = passage.RoomRadius >= 12f && rng.NextDouble() < Settings.RoomPondChance;
		if ( pond )
		{
			Bowls.Add( new CaveBowl
			{
				CenterMeters = mid.Position.WithZ( floor ) + roomDir * (passage.RoomRadius * 0.15f),
				RadiusAlong = MathF.Min( passage.RoomRadius * 0.4f, 6f ),
				RadiusAcross = MathF.Min( passage.RoomRadius * 0.3f, 4.5f ),
				YawRadians = MathF.Atan2( roomDir.y, roomDir.x ),
				DepthMeters = 1.6f,
				OpenDirection = -roomDir,
				ReachMeters = MathF.Min( 6f, passage.RoomRadius * 0.35f ),
			} );
			passage.Bowl = Bowls.Count - 1;
		}

		var chestAlong = passage.Kind == CavePassageKind.Loop ? 0.5f : 0.75f;
		var chestSample = passage.Samples[leg.Start + (int)((leg.End - leg.Start - 1) * chestAlong)];
		var offset = (chestSide || pond) ? side * (passage.RoomRadius * 0.45f * (rng.NextDouble() < 0.5 ? 1f : -1f)) : Vector3.Zero;
		passage.HasChest = true;
		passage.ChestMeters = chestSample.Position.WithZ( floor ) + offset;
		passage.ChestYawDegrees = MathF.Atan2( -roomDir.y, -roomDir.x ).RadianToDegree();

		passage.LightsMeters.Add( mid.Position.WithZ( floor + passage.RoomRadius * 0.6f ) );
		foreach ( var other in passage.Legs )
		{
			if ( other.Kind == CaveLegKind.Walk || other.End <= other.Start )
				continue;
			passage.LightsMeters.Add( passage.Samples[(other.Start + other.End) / 2].Position );
		}
	}

	// ---- clearance -------------------------------------------------------------------------------

	/// <summary>
	/// True when the tube stays under the platform, away from every other passage, and (outside its own mouths) out of
	/// the trunk. Rejections are logged.
	/// </summary>
	bool PassageClear( CavePassage passage, float skipStart, float skipEnd )
	{
		var samples = passage.Samples;
		if ( samples.Count < 4 )
			return Reject( passage, "too few samples" );
		var total = samples[^1].S;

		for ( var i = 0; i < samples.Count; i++ )
		{
			var sm = samples[i];
			if ( sm.Position.z + sm.Radius > -6f )
				return Reject( passage, $"s {sm.S:0} breaks the platform (z {sm.Position.z:0})" );

			var nearMouth = sm.S < skipStart || sm.S > total - skipEnd;
			foreach ( var other in Passages )
			{
				if ( other == passage )
					continue;
				if ( other.Kind == CavePassageKind.Trunk && nearMouth )
					continue;
				foreach ( var os in other.Samples )
				{
					var need = sm.Radius + os.Radius + 2f;
					var d = sm.Position - os.Position;
					if ( d.LengthSquared < need * need )
						return Reject( passage, $"s {sm.S:0} hits passage {other.Index} ({other.Kind}) at its s {os.S:0}" );
				}
			}
		}

		return true;
	}

	bool Reject( CavePassage passage, string why )
	{
		if ( DebugLog.Count < 400 )
			DebugLog.Add( $"{passage.Kind} {passage.Index}: {why}" );
		return false;
	}
}
