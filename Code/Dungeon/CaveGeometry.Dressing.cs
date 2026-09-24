using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>Dev-box dressing on top of the rock model: ledges (the only grapple surfaces), lights, chests, oases, water, the entrance.</summary>
public static partial class CaveGeometry
{
	static readonly Color SlabColor = new( 0.55f, 0.50f, 0.44f );
	static readonly Color JutColor = new( 0.42f, 0.38f, 0.34f );
	static readonly Color ShelfColor = new( 0.50f, 0.47f, 0.42f );
	static readonly Color SpurColor = new( 0.47f, 0.42f, 0.36f );
	static readonly Color BoulderColor = new( 0.38f, 0.35f, 0.32f );
	static readonly Color DoorstepColor = new( 0.60f, 0.56f, 0.50f );
	static readonly Color FillerColor = new( 0.40f, 0.40f, 0.40f );
	static readonly Color JumpHopColor = new( 0.80f, 0.68f, 0.40f );
	static readonly Color GrappleHopColor = new( 0.40f, 0.62f, 0.85f );
	static readonly Color ClimbHopColor = new( 0.45f, 0.80f, 0.45f );
	static readonly Color SlateColor = new( 0.30f, 0.34f, 0.38f );
	static readonly Color RockColor = new( 0.32f, 0.30f, 0.28f );
	static readonly Color ReedColor = new( 0.25f, 0.50f, 0.22f );
	static readonly Color MossColor = new( 0.28f, 0.42f, 0.20f );
	static readonly Color WaterTint = new( 0.55f, 0.80f, 0.95f, 0.55f );

	// ---- platform lip ----------------------------------------------------------------------------

	/// <summary>A low wall around the square platform so the test pawn does not wander off into the void.</summary>
	static void BuildPlatformLip( BuildContext ctx )
	{
		var half = BuildContext.E( ctx.Layout.PlatformHalfMeters );
		var thick = BuildContext.E( 0.5f );
		var height = BuildContext.E( 1.2f );
		var color = PlatformColor * 0.8f;
		var len = half * 2f + thick;

		AddBox( ctx, "platform_lip", new Vector3( half + thick * 0.5f, 0f, height * 0.5f ), new Vector3( thick, len, height ), Rotation.Identity, color, grapple: false );
		AddBox( ctx, "platform_lip", new Vector3( -half - thick * 0.5f, 0f, height * 0.5f ), new Vector3( thick, len, height ), Rotation.Identity, color, grapple: false );
		AddBox( ctx, "platform_lip", new Vector3( 0f, half + thick * 0.5f, height * 0.5f ), new Vector3( len, thick, height ), Rotation.Identity, color, grapple: false );
		AddBox( ctx, "platform_lip", new Vector3( 0f, -half - thick * 0.5f, height * 0.5f ), new Vector3( len, thick, height ), Rotation.Identity, color, grapple: false );
	}

	// ---- ledges ----------------------------------------------------------------------------------

	/// <summary>Every ledge as a dev box buried into the rock behind its anchor, tagged <c>grapple</c>; route ledges tinted by hop kind while testing.</summary>
	static void BuildLedges( BuildContext ctx )
	{
		var layout = ctx.Layout;
		var p = ctx.P;
		var buried = 3f + layout.Settings.WallNoiseMeters;

		var routeLandings = new Vector3[layout.RouteLedgeCount];
		foreach ( var ledge in layout.Ledges )
		{
			var length = buried + ledge.ProtrudeMeters;
			var centerM = ledge.AnchorMeters + ledge.IntoVoid * ((ledge.ProtrudeMeters - buried) * 0.5f);
			centerM.z = ledge.ZMeters - ledge.ThicknessMeters * 0.5f;

			var yaw = MathF.Atan2( ledge.IntoVoid.y, ledge.IntoVoid.x ).RadianToDegree();
			var rotation = Rotation.FromYaw( yaw ) * Rotation.FromPitch( ledge.TiltDegrees ) * Rotation.FromRoll( ledge.RollDegrees );
			var size = BuildContext.E( new Vector3( length, ledge.WidthMeters, ledge.ThicknessMeters ) );

			var color = ledge.Kind switch
			{
				CaveLedgeKind.Jut => JutColor,
				CaveLedgeKind.Shelf => ShelfColor,
				CaveLedgeKind.Spur => SpurColor,
				CaveLedgeKind.Boulder => BoulderColor,
				CaveLedgeKind.Doorstep => DoorstepColor,
				_ => SlabColor,
			};
			if ( !ledge.IsRoute )
				color = ledge.Passage != 0 ? Color.Lerp( color, ClimbHopColor, ledge.Ascending ? 0.35f : 0f ) : FillerColor;
			else if ( p.ColorByHop && ledge.Kind != CaveLedgeKind.Doorstep )
				color = Color.Lerp( color, ledge.Ascending ? ClimbHopColor : ledge.GrappleHop ? GrappleHopColor : JumpHopColor, 0.55f );

			var name = ledge.IsRoute ? $"ledge_r{ledge.RouteIndex}" : ledge.Passage != 0 ? $"ledge_p{ledge.Passage}_{ledge.Index}" : $"ledge_f{ledge.Index}";
			AddBox( ctx, name, BuildContext.E( centerM ), size, rotation, color, grapple: true );

			var landing = BuildContext.E( ledge.LandingMeters );
			if ( ledge.IsRoute && ledge.RouteIndex < routeLandings.Length )
				routeLandings[ledge.RouteIndex] = landing;

			if ( ledge.HasLight )
				AddLight( ctx, $"light_r{ledge.RouteIndex}", landing + Vector3.Up * BuildContext.E( 2.2f ), new Color( 1f, 0.82f, 0.55f ), p.LightRadiusMeters, marker: true );
		}

		ctx.Result.RouteLandingsLocal.AddRange( routeLandings );
	}

	// ---- chamber ---------------------------------------------------------------------------------

	static void BuildChamber( BuildContext ctx )
	{
		var layout = ctx.Layout;
		var trunk = layout.Trunk;
		var leg = trunk.Legs[layout.ChamberLeg];
		var mid = trunk.Samples[(leg.Start + leg.End) / 2];
		var floorCenter = mid.Position.WithZ( leg.FloorZ );
		var lightRadius = MathF.Max( ctx.P.LightRadiusMeters, layout.ChamberRadiusMeters * 0.9f );

		var dir = mid.Tangent.WithZ( 0f ).Normal;
		var side = new Vector3( -dir.y, dir.x, 0f );
		var spread = layout.ChamberRadiusMeters * 0.45f;
		AddLight( ctx, "chamber_light_0", BuildContext.E( floorCenter + dir * spread + Vector3.Up * 12f ), new Color( 0.6f, 0.75f, 1f ), lightRadius, marker: false );
		AddLight( ctx, "chamber_light_1", BuildContext.E( floorCenter - dir * spread + Vector3.Up * 12f ), new Color( 0.6f, 0.75f, 1f ), lightRadius, marker: false );
		AddLight( ctx, "chamber_light_2", BuildContext.E( floorCenter + side * spread + Vector3.Up * 12f ), new Color( 0.6f, 0.75f, 1f ), lightRadius, marker: false );

		if ( ctx.P.BuildWaterfall && layout.ChamberBowl >= 0 )
			BuildWaterfall( ctx, leg.FloorZ );

		ctx.Result.ChestSlots.Add( new DungeonChestSlot( -1, BuildContext.E( layout.ChamberChestMeters ), layout.ChamberChestYawDegrees ) );
	}

	/// <summary>Crack high on the chamber wall with a shelf, and a translucent sheet falling into the pond (placeholder until there is a particle / shader waterfall).</summary>
	static void BuildWaterfall( BuildContext ctx, float floorZ )
	{
		var layout = ctx.Layout;
		var dir = layout.WaterfallOutward;
		var rot = Rotation.FromYaw( MathF.Atan2( dir.y, dir.x ).RadianToDegree() );
		var crack = layout.WaterfallCrackMeters;

		AddBox( ctx, "waterfall_crack", BuildContext.E( crack + dir * 0.3f + Vector3.Up * 1.5f ), BuildContext.E( new Vector3( 3f, 1.6f, 3.2f ) ), rot, new Color( 0.05f, 0.05f, 0.06f ), grapple: false );
		AddBox( ctx, "waterfall_shelf", BuildContext.E( crack - dir * 0.6f - Vector3.Up * 0.4f ), BuildContext.E( new Vector3( 3f, 2.6f, 0.8f ) ), rot, RockChamber * 0.9f, grapple: false );

		var waterTop = floorZ - 0.35f;
		var sheetTop = crack.z - 0.8f;
		var sheetM = (crack - dir * 1.9f).WithZ( (sheetTop + waterTop) * 0.5f );
		AddBox( ctx, "waterfall_sheet", BuildContext.E( sheetM ), BuildContext.E( new Vector3( 0.5f, 2.6f, sheetTop - waterTop ) ), rot, WaterTint, grapple: false, solid: false );
		AddLight( ctx, "waterfall_light", BuildContext.E( sheetM + Vector3.Up * 2f - dir * 3f ), new Color( 0.7f, 0.9f, 1f ), 18f, marker: false );
	}

	// ---- passages + junctions --------------------------------------------------------------------

	/// <summary>Lights and chests in every passage, a light over every junction hall and cavern, and an oasis on every pond bowl.</summary>
	static void BuildPassageDressing( BuildContext ctx )
	{
		var layout = ctx.Layout;
		var trunk = layout.Trunk;

		for ( var i = 0; i < trunk.Legs.Count; i++ )
		{
			var leg = trunk.Legs[i];
			if ( i == layout.ChamberLeg || leg.Kind != CaveLegKind.Walk || (!leg.IsJunction && leg.HallRadius <= 0f) )
				continue;
			var mid = trunk.Samples[(leg.Start + leg.End) / 2];
			var r = MathF.Max( 6f, leg.HallRadius > 0f ? leg.HallRadius : mid.Radius );
			AddLight( ctx, $"hall_light_{i}", BuildContext.E( mid.Position.WithZ( leg.FloorZ + MathF.Min( 10f, r * 0.7f ) ) ), new Color( 0.95f, 0.85f, 0.7f ), MathF.Max( 14f, r * 1.5f ), marker: false );
		}

		foreach ( var passage in layout.Passages )
		{
			if ( passage.Kind == CavePassageKind.Trunk )
				continue;
			var warm = passage.Kind == CavePassageKind.Loop ? new Color( 0.75f, 0.85f, 1f ) : new Color( 1f, 0.85f, 0.6f );
			for ( var i = 0; i < passage.LightsMeters.Count; i++ )
				AddLight( ctx, $"passage_light_{passage.Index}_{i}", BuildContext.E( passage.LightsMeters[i] ), warm, MathF.Max( 14f, passage.RoomRadius * 1.4f ), marker: false );

			if ( passage.HasChest )
				ctx.Result.ChestSlots.Add( new DungeonChestSlot( passage.Index, BuildContext.E( passage.ChestMeters ), passage.ChestYawDegrees ) );
		}

		for ( var i = 0; i < layout.Bowls.Count; i++ )
			BuildOasis( ctx, layout.Bowls[i], layout.Seed ^ (0x0A515 + 97 * (i + 1)) );
	}

	// ---- oasis -----------------------------------------------------------------------------------

	/// <summary>
	/// An underground oasis around a pond bowl: the water rig in the bowl, slate slabs stepping down into the water
	/// from the open side, boulders and moss on the bank, reeds at the edge, and a soft teal light over it.
	/// </summary>
	static void BuildOasis( BuildContext ctx, CaveBowl bowl, int seed )
	{
		var rng = new Random( seed );
		var yawRad = bowl.YawRadians;
		var open = bowl.OpenDirection;
		var openAngle = MathF.Atan2( open.y, open.x );
		var floorZ = bowl.CenterMeters.z;
		var center = bowl.CenterMeters;
		var waterZ = floorZ - 0.35f;

		BuildWater( ctx, "pond", center.WithZ( waterZ ), bowl.RadiusAlong * 2f, bowl.RadiusAcross * 2f, bowl.DepthMeters, yawRad.RadianToDegree() );
		AddLight( ctx, "pond_light", BuildContext.E( center + Vector3.Up * 3f ), new Color( 0.55f, 0.85f, 0.85f ), 14f, marker: false );

		float EdgeRadius( float a )
		{
			var ca = MathF.Cos( a - yawRad );
			var sa = MathF.Sin( a - yawRad );
			return 1f / MathF.Sqrt( (ca * ca) / (bowl.RadiusAlong * bowl.RadiusAlong) + (sa * sa) / (bowl.RadiusAcross * bowl.RadiusAcross) );
		}

		var reach = MathF.Max( 3f, bowl.ReachMeters );
		var paths = reach > 6f ? 2 : 1;
		for ( var path = 0; path < paths; path++ )
		{
			var pathAngle = openAngle + (paths == 1 ? 0f : (path == 0 ? -0.6f : 0.6f)) + Range( rng, -0.2f, 0.2f );
			var pdir = new Vector3( MathF.Cos( pathAngle ), MathF.Sin( pathAngle ), 0f );
			var side = new Vector3( -pdir.y, pdir.x, 0f );
			var edge = EdgeRadius( pathAngle );
			var count = Math.Clamp( (int)(reach / 2.2f) + 1, 2, 6 );
			for ( var i = 0; i < count; i++ )
			{
				var t = i / (float)Math.Max( 1, count - 1 );
				var dist = edge - 0.6f + (reach - 0.6f) * t;
				var size = new Vector3( Range( rng, 2.2f, 3.4f ), Range( rng, 1.6f, 2.4f ), 0.22f );
				var lift = 0.11f + t * 0.5f;
				var pos = center + pdir * dist + side * Range( rng, -0.5f, 0.5f ) + Vector3.Up * lift;
				var rot = Rotation.FromYaw( pathAngle.RadianToDegree() + Range( rng, -14f, 14f ) ) * Rotation.FromPitch( Range( rng, 4f, 10f ) );
				AddBox( ctx, $"slate_{path}_{i}", BuildContext.E( pos ), BuildContext.E( size ), rot, SlateColor * Range( rng, 0.9f, 1.12f ), grapple: false );
			}
		}

		var boulders = rng.Next( 3, 6 );
		for ( var i = 0; i < boulders; i++ )
		{
			var a = openAngle + Range( rng, -1.9f, 1.9f );
			var d = EdgeRadius( a ) + Range( rng, 1f, MathF.Max( 1.5f, reach * 0.6f ) );
			var size = Range( rng, 0.8f, 2.2f );
			var pos = center + new Vector3( MathF.Cos( a ), MathF.Sin( a ), 0f ) * d + Vector3.Up * (size * 0.35f);
			var rot = Rotation.FromYaw( Range( rng, 0f, 360f ) ) * Rotation.FromPitch( Range( rng, -25f, 25f ) ) * Rotation.FromRoll( Range( rng, -25f, 25f ) );
			var extents = new Vector3( size, size * Range( rng, 0.7f, 1.3f ), size * Range( rng, 0.6f, 1f ) );
			AddBox( ctx, $"boulder_{i}", BuildContext.E( pos ), BuildContext.E( extents ), rot, RockColor * Range( rng, 0.85f, 1.1f ), grapple: false );
		}

		var moss = rng.Next( 4, 7 );
		for ( var i = 0; i < moss; i++ )
		{
			var a = openAngle + Range( rng, -2.4f, 2.4f );
			var d = EdgeRadius( a ) + Range( rng, 0.3f, MathF.Max( 1f, reach * 0.5f ) );
			var pos = center + new Vector3( MathF.Cos( a ), MathF.Sin( a ), 0f ) * d + Vector3.Up * 0.03f;
			var size = new Vector3( Range( rng, 1.5f, 2.6f ), Range( rng, 1.5f, 2.6f ), 0.06f );
			AddBox( ctx, $"moss_{i}", BuildContext.E( pos ), BuildContext.E( size ), Rotation.FromYaw( Range( rng, 0f, 360f ) ), MossColor * Range( rng, 0.85f, 1.15f ), grapple: false, solid: false );
		}

		var reeds = rng.Next( 10, 17 );
		for ( var i = 0; i < reeds; i++ )
		{
			var a = Range( rng, -MathF.PI, MathF.PI );
			var d = EdgeRadius( a ) * Range( rng, 0.98f, 1.12f );
			var height = Range( rng, 1.1f, 1.9f );
			var pos = center + new Vector3( MathF.Cos( a ), MathF.Sin( a ), 0f ) * d + Vector3.Up * (height * 0.5f - 0.2f);
			var rot = Rotation.FromYaw( Range( rng, 0f, 360f ) ) * Rotation.FromPitch( Range( rng, -8f, 8f ) );
			AddBox( ctx, $"reed_{i}", BuildContext.E( pos ), BuildContext.E( new Vector3( 0.12f, 0.12f, height ) ), rot, ReedColor * Range( rng, 0.8f, 1.2f ), grapple: false, solid: false );
		}
	}

	/// <summary>The project water rig: a "water"-tagged trigger + WaterVolume (so the pawn swims), a translucent body and a tempwater surface plane.</summary>
	static void BuildWater( BuildContext ctx, string name, Vector3 surfaceCenterM, float sizeAlongM, float sizeAcrossM, float depthM, float yawDegrees )
	{
		var rot = Rotation.FromYaw( yawDegrees );
		var pool = new GameObject( true, name );
		pool.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		pool.Parent = ctx.Root;
		pool.LocalPosition = BuildContext.E( surfaceCenterM - Vector3.Up * (depthM * 0.5f) );
		pool.LocalRotation = rot;
		pool.LocalScale = Vector3.One;
		pool.Tags.Add( "water" );

		var extent = BuildContext.E( new Vector3( sizeAlongM, sizeAcrossM, depthM ) );
		var trigger = pool.Components.Create<BoxCollider>();
		trigger.IsTrigger = true;
		trigger.Scale = extent;
		pool.Components.Create<WaterVolume>();

		var body = new GameObject( true, "body" );
		body.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		body.Parent = pool;
		body.LocalPosition = Vector3.Zero;
		body.LocalRotation = Rotation.Identity;
		body.LocalScale = extent / BoxModelSize;
		var bodyRenderer = body.Components.Create<ModelRenderer>();
		bodyRenderer.Model = ctx.Box;
		bodyRenderer.MaterialOverride = ctx.BoxMaterial;
		bodyRenderer.Tint = new Color( 0.10f, 0.35f, 0.45f, 0.45f );

		var surface = new GameObject( true, "surface" );
		surface.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		surface.Parent = pool;
		surface.LocalPosition = Vector3.Up * (extent.z * 0.5f + 0.5f);
		surface.LocalRotation = Rotation.Identity;
		surface.LocalScale = new Vector3( extent.x / PlaneModelSize, extent.y / PlaneModelSize, 1f );
		var surfaceRenderer = surface.Components.Create<ModelRenderer>();
		surfaceRenderer.Model = Model.Load( PlaneModelPath );
		var water = Material.Load( WaterMaterialPath );
		if ( water is { IsValid: true } )
			surfaceRenderer.MaterialOverride = water;
		else
			surfaceRenderer.Tint = new Color( 0.3f, 0.6f, 0.8f, 0.6f );
	}

	// ---- entrance --------------------------------------------------------------------------------

	static void ResolveEntrance( BuildContext ctx )
	{
		var layout = ctx.Layout;
		var trunk = layout.Trunk;
		var rim = layout.TubePoint( trunk, 0, layout.EntranceAngle ).WithZ( 0f );
		var dir = (rim - trunk.Samples[0].Position).WithZ( 0f ).Normal;
		if ( dir.LengthSquared < 0.5f )
			dir = new Vector3( -1f, 0f, 0f );

		ctx.Result.EntranceSignLocal = BuildContext.E( rim + dir * 2.5f + Vector3.Up * 2.6f );
		ctx.Result.EntranceFacingLocal = -dir;
		ctx.Result.EntranceSpawnLocal = BuildContext.E( rim + dir * 6f + Vector3.Up * 0.2f );
	}

	// ---- primitives ------------------------------------------------------------------------------

	static float Range( Random rng, float min, float max ) => min + (float)rng.NextDouble() * (max - min);

	static GameObject AddBox( BuildContext ctx, string name, Vector3 localCenter, Vector3 size, Rotation rotation, Color color, bool grapple, bool solid = true )
	{
		if ( size.x < 0.5f || size.y < 0.5f || size.z < 0.5f )
			return null;

		var go = new GameObject( true, name );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = ctx.Root;
		go.LocalRotation = rotation;
		go.LocalPosition = localCenter;
		go.LocalScale = size / BoxModelSize;
		go.Tags.Add( "dungeon" );
		if ( solid )
			go.Tags.Add( "solid" );
		if ( grapple )
			go.Tags.Add( PlayerMovement.GrappleSurfaceTag );

		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = ctx.Box;
		renderer.MaterialOverride = ctx.BoxMaterial;
		renderer.Tint = color;

		if ( solid )
		{
			var collider = go.Components.Create<BoxCollider>();
			collider.Scale = new Vector3( BoxModelSize, BoxModelSize, BoxModelSize );
			collider.Static = true;
		}

		ctx.Result.BoxCount++;
		return go;
	}

	/// <summary>Point light under the generator root (never under a scaled box, which would scale the radius).</summary>
	static void AddLight( BuildContext ctx, string name, Vector3 localPosition, Color color, float radiusMeters, bool marker )
	{
		var go = new GameObject( true, name );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = ctx.Root;
		go.LocalPosition = localPosition;
		go.LocalRotation = Rotation.Identity;
		go.LocalScale = Vector3.One;

		var light = go.Components.Create<PointLight>();
		light.LightColor = color;
		light.Radius = BuildContext.E( radiusMeters );

		if ( marker )
		{
			var cube = new GameObject( true, "marker" );
			cube.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			cube.Parent = go;
			cube.LocalPosition = Vector3.Zero;
			cube.LocalScale = Vector3.One * (BuildContext.E( 0.35f ) / BoxModelSize);
			var r = cube.Components.Create<ModelRenderer>();
			r.Model = ctx.Box;
			r.MaterialOverride = ctx.BoxMaterial;
			r.Tint = color;
		}
	}
}
