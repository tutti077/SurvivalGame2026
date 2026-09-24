using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Geometry inputs the component resolves once (debug toggles); sizes come from the layout in meters.</summary>
public sealed class CaveGeometryParams
{
	public bool ColorByHop = true;
	public float LightRadiusMeters = 22f;
	public bool BuildWaterfall = true;
}

/// <summary>Everything the component needs after a build, in generator-local engine units.</summary>
public sealed class CaveGeometryResult
{
	public Vector3 EntranceSignLocal;
	public Vector3 EntranceFacingLocal;
	public Vector3 EntranceSpawnLocal;
	public Vector3 BottomLocal;
	/// <summary>Chamber chest first (RoomIndex -1), then one per passage that has one (RoomIndex = passage index).</summary>
	public readonly List<DungeonChestSlot> ChestSlots = new();
	/// <summary>Route ledge landings in route order.</summary>
	public readonly List<Vector3> RouteLandingsLocal = new();
	/// <summary>A standing point on every passage's room floor, by passage index (the trunk's is the entrance).</summary>
	public readonly List<Vector3> PassageFloorsLocal = new();
	/// <summary>A standing point in every junction hall, in trunk order.</summary>
	public readonly List<Vector3> JunctionFloorsLocal = new();
	public int TriangleCount;
	public int BoxCount;
}

/// <summary>
/// Turns a <see cref="CaveLayout"/> into scene geometry: one runtime-built rock model — every passage (the trunk chain
/// first) as a tube of rings around its swept spine with the mouth holes cut out of the trunk, each branch's end
/// rings zipped onto their hole loops or domed shut, the entrance platform collar on the trunk's first ring, and
/// floors with pond bowls pressed in — all inward-facing, with mesh collision and <b>no grapple tag</b>. The dressing
/// (ledges, lights, chests, oases, waterfall) lives in the partial next door.
/// </summary>
public static partial class CaveGeometry
{
	const string BoxModelPath = "models/dev/box.vmdl";
	const string PlaneModelPath = "models/dev/plane.vmdl";
	const string BoxMaterialPath = "materials/default.vmat";
	const string WaterMaterialPath = "materials/water/tempwater.vmat";
	const float BoxModelSize = 50f;
	const float PlaneModelSize = 100f;

	static readonly Color RockTop = new( 0.46f, 0.41f, 0.35f );
	static readonly Color RockBottom = new( 0.24f, 0.22f, 0.21f );
	static readonly Color RockTunnel = new( 0.34f, 0.31f, 0.28f );
	static readonly Color RockJunction = new( 0.38f, 0.33f, 0.28f );
	static readonly Color RockChimney = new( 0.30f, 0.32f, 0.30f );
	static readonly Color RockChamber = new( 0.26f, 0.27f, 0.30f );
	static readonly Color RockPassage = new( 0.36f, 0.31f, 0.27f );
	static readonly Color RockLoop = new( 0.31f, 0.30f, 0.32f );
	static readonly Color PlatformColor = new( 0.38f, 0.38f, 0.36f );
	static readonly Color FloorColor = new( 0.30f, 0.29f, 0.27f );
	static readonly Color WetFloorColor = new( 0.16f, 0.22f, 0.24f );

	public static CaveGeometryResult Build( GameObject root, CaveLayout layout, CaveGeometryParams p )
	{
		var result = new CaveGeometryResult();
		if ( root is null || !root.IsValid() || layout is null || layout.Passages.Count == 0 )
			return result;

		var ctx = new BuildContext( root, layout, p, result );

		BuildRockModel( ctx );
		BuildPlatformLip( ctx );
		BuildLedges( ctx );
		BuildChamber( ctx );
		BuildPassageDressing( ctx );
		ResolveEntrance( ctx );

		return result;
	}

	sealed class BuildContext
	{
		public readonly GameObject Root;
		public readonly CaveLayout Layout;
		public readonly CaveGeometryParams P;
		public readonly CaveGeometryResult Result;
		public readonly Model Box;
		public readonly Material BoxMaterial;

		public BuildContext( GameObject root, CaveLayout layout, CaveGeometryParams p, CaveGeometryResult result )
		{
			Root = root;
			Layout = layout;
			P = p;
			Result = result;
			Box = Model.Load( BoxModelPath );
			BoxMaterial = Material.Load( BoxMaterialPath );
		}

		public static Vector3 E( Vector3 meters ) => TerrainWorldUnits.MetersToEngine( meters );
		public static float E( float meters ) => TerrainWorldUnits.MetersToEngine( meters );
	}

	/// <summary>Vertex / index accumulator for the one rock model (positions in engine units).</summary>
	sealed class MeshData
	{
		public readonly List<Vector3> Positions = new();
		public readonly List<Vector3> Normals = new();
		public readonly List<Color> Colors = new();
		public readonly List<int> Indices = new();

		public int Add( Vector3 positionEngine, Vector3 normal, Color color )
		{
			Positions.Add( positionEngine );
			Normals.Add( normal );
			Colors.Add( color );
			return Positions.Count - 1;
		}

		/// <summary>Triangle wound so its face points along <paramref name="want"/> (the side we look at).</summary>
		public void Tri( int a, int b, int c, Vector3 want )
		{
			var face = Vector3.Cross( Positions[b] - Positions[a], Positions[c] - Positions[a] );
			if ( face.LengthSquared < 1e-4f )
				return;
			if ( Vector3.Dot( face, want ) < 0f )
				(b, c) = (c, b);
			Indices.Add( a );
			Indices.Add( b );
			Indices.Add( c );
		}

		public void Quad( int a, int b, int c, int d, Vector3 want )
		{
			Tri( a, c, b, want );
			Tri( b, c, d, want );
		}
	}

	// ---- rock model ------------------------------------------------------------------------------

	static void BuildRockModel( BuildContext ctx )
	{
		var layout = ctx.Layout;
		var mesh = new MeshData();

		// Mouth holes on the trunk: quads to skip.
		var holes = new HashSet<(int r, int m)>();
		foreach ( var mouth in layout.Mouths )
		{
			if ( mouth.Host != 0 )
				continue;
			for ( var r = mouth.RingStart; r < mouth.RingEnd; r++ )
			{
				for ( var j = 0; j < mouth.SegCount; j++ )
					holes.Add( (r, (mouth.SegStart + j) % layout.Trunk.Segments) );
			}
		}

		var rings = new List<int[][]>();
		for ( var i = 0; i < layout.Passages.Count; i++ )
			rings.Add( BuildTube( ctx, mesh, layout.Passages[i], i == 0 ? holes : null ) );

		// Trunk: platform collar on the first ring, dome on the last (the chamber's far wall).
		var trunk = layout.Trunk;
		var trunkRings = rings[0];
		if ( trunkRings.Length > 0 )
		{
			BuildPlatform( ctx, mesh, trunk, trunkRings[0] );
			CapRing( mesh, trunk, trunkRings[^1], trunk.Samples[^1], RockChamber * 0.9f );
		}

		// Branches: zip onto their mouths, dome the dead ends.
		for ( var i = 1; i < layout.Passages.Count; i++ )
		{
			var passage = layout.Passages[i];
			var own = rings[i];
			if ( own.Length == 0 )
				continue;

			if ( passage.MouthA >= 0 )
			{
				var mouthA = layout.Mouths[passage.MouthA];
				var loopA = MouthLoop( layout, mouthA, rings[mouthA.Host] );
				Zip( mesh, loopA, MouthAngles( mesh, loopA, mouthA ), own[0], MouthAngles( mesh, own[0], mouthA ) );
			}

			if ( passage.MouthB >= 0 )
			{
				var mouthB = layout.Mouths[passage.MouthB];
				var loopB = MouthLoop( layout, mouthB, rings[mouthB.Host] );
				Zip( mesh, loopB, MouthAngles( mesh, loopB, mouthB ), own[^1], MouthAngles( mesh, own[^1], mouthB ) );
			}
			else
			{
				CapRing( mesh, passage, own[^1], passage.Samples[^1], (passage.Kind == CavePassageKind.Loop ? RockLoop : RockPassage) * 0.9f );
			}
		}

		CreateModel( ctx, mesh );

		var chamber = trunk.Legs[layout.ChamberLeg];
		ctx.Result.BottomLocal = BuildContext.E( trunk.Samples[(chamber.Start + chamber.End) / 2].Position.WithZ( chamber.FloorZ ) );
		foreach ( var leg in trunk.Legs )
		{
			if ( leg.IsJunction )
				ctx.Result.JunctionFloorsLocal.Add( BuildContext.E( trunk.Samples[(leg.Start + leg.End) / 2].Position.WithZ( leg.FloorZ ) ) );
		}
	}

	static Color TrunkColor( CaveLayout layout, CaveSpineSample sm )
	{
		var leg = layout.Trunk.Legs[Math.Clamp( sm.Leg, 0, layout.Trunk.Legs.Count - 1 )];
		if ( sm.Leg == layout.ChamberLeg )
			return RockChamber;
		return leg.Kind switch
		{
			CaveLegKind.Climb => RockChimney,
			CaveLegKind.Walk => leg.IsJunction ? RockJunction : RockTunnel,
			_ => Color.Lerp( RockTop, RockBottom, Math.Clamp( -sm.Position.z / layout.DepthMeters, 0f, 1f ) ),
		};
	}

	/// <summary>
	/// One passage as rings of vertices (radius + rock noise, floor-clamped with bowls) joined by quads, skipping any
	/// hole quads. Returns the vertex indices per ring so mouths can be zipped onto them.
	/// </summary>
	static int[][] BuildTube( BuildContext ctx, MeshData mesh, CavePassage passage, HashSet<(int r, int m)> holes )
	{
		var layout = ctx.Layout;
		var n = passage.Segments;
		var samples = passage.Samples;
		if ( samples.Count < 2 )
			return Array.Empty<int[]>();

		var isTrunk = passage.Kind == CavePassageKind.Trunk;
		var baseColor = passage.Kind == CavePassageKind.Loop ? RockLoop : RockPassage;
		var ringsIdx = new int[samples.Count][];
		var ringsM = new Vector3[samples.Count][];

		for ( var r = 0; r < samples.Count; r++ )
		{
			var sm = samples[r];
			var ringM = new Vector3[n];
			for ( var m = 0; m < n; m++ )
				ringM[m] = layout.TubePoint( passage, r, CaveLayout.SegmentAngle( passage, m ) );

			var ringIdx = new int[n];
			var prevRing = r > 0 ? ringsM[r - 1] : null;
			var color = isTrunk ? TrunkColor( layout, sm ) : baseColor;
			var band = layout.TubeNoise( passage, 0f, sm.S ) / MathF.Max( 0.1f, layout.Settings.WallNoiseMeters );
			var shade = 1f + Math.Clamp( band, -1f, 1f ) * 0.08f;
			color = new Color( color.r * shade, color.g * shade, color.b * shade, 1f );

			for ( var m = 0; m < n; m++ )
			{
				var around = ringM[(m + 1) % n] - ringM[(m - 1 + n) % n];
				var along = prevRing is not null ? ringM[m] - prevRing[m] : sm.Tangent;
				var normal = Vector3.Cross( around, along ).Normal;
				var toAxis = sm.Position - ringM[m];
				if ( Vector3.Dot( normal, toAxis ) < 0f )
					normal = -normal;
				if ( normal.LengthSquared < 1e-6f )
					normal = toAxis.Normal;

				var onFloor = !float.IsNaN( sm.FloorZ ) && ringM[m].z <= sm.FloorZ + 0.01f;
				var wet = onFloor && layout.BowlDepthAt( ringM[m].x, ringM[m].y ) > 0.01f;
				var vertexColor = onFloor ? (wet ? WetFloorColor : FloorColor) : color;
				ringIdx[m] = mesh.Add( BuildContext.E( ringM[m] ), onFloor && !wet ? Vector3.Up : normal, vertexColor );
			}

			ringsIdx[r] = ringIdx;
			ringsM[r] = ringM;
		}

		for ( var r = 0; r < samples.Count - 1; r++ )
		{
			var a = ringsIdx[r];
			var b = ringsIdx[r + 1];
			for ( var m = 0; m < n; m++ )
			{
				if ( holes is not null && holes.Contains( (r, m) ) )
					continue;
				var m1 = (m + 1) % n;
				var want = mesh.Normals[b[m]] + mesh.Normals[b[m1]];
				mesh.Quad( a[m], a[m1], b[m], b[m1], want );
			}
		}

		ctx.Result.PassageFloorsLocal.Add( BuildContext.E( passage.HasChest ? passage.ChestMeters : samples[0].Position ) );
		return ringsIdx;
	}

	/// <summary>Fan a ring shut around a centre point just past the last sample.</summary>
	static void CapRing( MeshData mesh, CavePassage passage, int[] ring, CaveSpineSample last, Color color )
	{
		var capM = last.Position + last.Tangent * 0.5f;
		if ( !float.IsNaN( last.FloorZ ) )
			capM.z = MathF.Max( capM.z, last.FloorZ + 0.3f );
		var cap = mesh.Add( BuildContext.E( capM ), -last.Tangent, color );
		var n = ring.Length;
		for ( var m = 0; m < n; m++ )
			mesh.Tri( ring[m], ring[(m + 1) % n], cap, -last.Tangent );
	}

	/// <summary>The square entrance platform: the trunk's first ring (duplicated with up normals) out to a square, flat at z = 0.</summary>
	static void BuildPlatform( BuildContext ctx, MeshData mesh, CavePassage trunk, int[] rimRing )
	{
		var layout = ctx.Layout;
		var n = rimRing.Length;
		var half = BuildContext.E( layout.PlatformHalfMeters );

		var rimBase = mesh.Positions.Count;
		for ( var m = 0; m < n; m++ )
			mesh.Add( mesh.Positions[rimRing[m]].WithZ( 0f ), Vector3.Up, PlatformColor );

		var squareBase = mesh.Positions.Count;
		for ( var m = 0; m < n; m++ )
		{
			var p = mesh.Positions[rimRing[m]];
			var angle = MathF.Atan2( p.y, p.x );
			var cx = MathF.Cos( angle );
			var sy = MathF.Sin( angle );
			var scale = half / MathF.Max( MathF.Abs( cx ), MathF.Abs( sy ) );
			mesh.Add( new Vector3( cx * scale, sy * scale, 0f ), Vector3.Up, PlatformColor * 0.92f );
		}

		for ( var m = 0; m < n; m++ )
			mesh.Quad( rimBase + m, rimBase + (m + 1) % n, squareBase + m, squareBase + (m + 1) % n, Vector3.Up );
	}

	// ---- mouths ----------------------------------------------------------------------------------

	/// <summary>Boundary loop of a mouth's hole patch as host vertex indices, once around, no repeated corners.</summary>
	static int[] MouthLoop( CaveLayout layout, CaveMouth mouth, int[][] hostRings )
	{
		var host = layout.Passages[mouth.Host];
		var n = host.Segments;
		var loop = new List<int>();
		var m0 = mouth.SegStart;
		var w = mouth.SegCount;
		var r0 = mouth.RingStart;
		var r1 = mouth.RingEnd;
		for ( var j = 0; j < w; j++ ) loop.Add( hostRings[r0][(m0 + j) % n] );
		for ( var r = r0; r < r1; r++ ) loop.Add( hostRings[r][(m0 + w) % n] );
		for ( var j = w; j > 0; j-- ) loop.Add( hostRings[r1][(m0 + j) % n] );
		for ( var r = r1; r > r0; r-- ) loop.Add( hostRings[r][m0 % n] );
		return loop.ToArray();
	}

	/// <summary>Polar angle of each vertex around a mouth, in the mouth's (tangent, up) frame.</summary>
	static float[] MouthAngles( MeshData mesh, int[] indices, CaveMouth mouth )
	{
		var c = BuildContext.E( mouth.CenterMeters );
		var d = mouth.OutwardMeters;
		var u = new Vector3( -d.y, d.x, 0f );
		var phi = new float[indices.Length];
		for ( var m = 0; m < indices.Length; m++ )
		{
			var rel = mesh.Positions[indices[m]] - c;
			phi[m] = MathF.Atan2( rel.z, Vector3.Dot( rel, u ) );
		}
		return phi;
	}

	/// <summary>
	/// Bridge two closed rings of different vertex counts with triangles, walking both by polar angle (each array is
	/// sorted ascending by its angle). Faces point along the tube ring's normals.
	/// </summary>
	static void Zip( MeshData mesh, int[] a, float[] phiA, int[] b, float[] phiB )
	{
		var na = a.Length;
		var nb = b.Length;
		if ( na < 3 || nb < 3 )
			return;

		var orderA = SortByAngle( a, phiA );
		var orderB = SortByAngle( b, phiB );
		var angA = new float[na];
		var angB = new float[nb];
		for ( var i = 0; i < na; i++ ) angA[i] = phiA[orderA[i]];
		for ( var i = 0; i < nb; i++ ) angB[i] = phiB[orderB[i]];

		float AngleA( int k ) => angA[k % na] + 2f * MathF.PI * (k / na);
		float AngleB( int k ) => angB[k % nb] + 2f * MathF.PI * (k / nb);

		var i0 = 0;
		var j0 = 0;
		var ia = 0;
		var jb = 0;
		while ( ia < na || jb < nb )
		{
			var va = a[orderA[i0 % na]];
			var vb = b[orderB[j0 % nb]];
			var nextA = ia < na ? AngleA( i0 + 1 ) : float.MaxValue;
			var nextB = jb < nb ? AngleB( j0 + 1 ) : float.MaxValue;
			if ( nextA <= nextB )
			{
				var va1 = a[orderA[(i0 + 1) % na]];
				mesh.Tri( va, vb, va1, mesh.Normals[vb] );
				i0++;
				ia++;
			}
			else
			{
				var vb1 = b[orderB[(j0 + 1) % nb]];
				mesh.Tri( va, vb, vb1, mesh.Normals[vb] + mesh.Normals[vb1] );
				j0++;
				jb++;
			}
		}
	}

	static int[] SortByAngle( int[] indices, float[] phi )
	{
		var order = new int[indices.Length];
		for ( var i = 0; i < order.Length; i++ )
			order[i] = i;
		Array.Sort( order, ( x, y ) => phi[x].CompareTo( phi[y] ) );
		return order;
	}

	// ---- model -----------------------------------------------------------------------------------

	static void CreateModel( BuildContext ctx, MeshData mesh )
	{
		var positions = mesh.Positions;
		var normals = mesh.Normals;
		var colors = mesh.Colors;
		var indices = mesh.Indices;
		if ( positions.Count == 0 || indices.Count == 0 )
			return;

		var min = positions[0];
		var max = positions[0];
		foreach ( var v in positions )
		{
			min = Vector3.Min( min, v );
			max = Vector3.Max( max, v );
		}

		var material = TerrainMeshBuilder.GetTerrainMaterial();
		var renderMesh = new Mesh( material, MeshPrimitiveType.Triangles );
		renderMesh.CreateVertexBuffer<Vertex>( positions.Count );
		renderMesh.CreateIndexBuffer( indices.Count );
		renderMesh.LockVertexBuffer<Vertex>( vertices =>
		{
			for ( var i = 0; i < positions.Count; i++ )
			{
				var n = normals[i];
				var tangent = Vector3.Cross( n, Vector3.Up );
				if ( tangent.LengthSquared < 1e-6f )
					tangent = Vector3.Cross( n, Vector3.Forward );
				vertices[i] = new Vertex
				{
					Position = positions[i],
					Normal = n,
					Tangent = new Vector4( tangent.Normal, 1f ),
					TexCoord0 = new Vector2( positions[i].x / 512f, positions[i].z / 512f ),
					Color = colors[i],
				};
			}
		} );
		renderMesh.LockIndexBuffer( buffer =>
		{
			for ( var i = 0; i < indices.Count; i++ )
				buffer[i] = indices[i];
		} );
		renderMesh.Bounds = new BBox( min, max );

		var model = new ModelBuilder()
			.AddMesh( renderMesh )
			.AddCollisionMesh( positions, indices )
			.AddTraceMesh( positions, indices )
			.Create();

		var go = new GameObject( true, "cave_rock" );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = ctx.Root;
		go.LocalPosition = Vector3.Zero;
		go.LocalRotation = Rotation.Identity;
		go.LocalScale = Vector3.One;
		go.Tags.Add( "solid" );
		go.Tags.Add( "dungeon" );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		renderer.MaterialOverride = material;

		var collider = go.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;

		ctx.Result.TriangleCount = indices.Count / 3;
	}
}
