//
// pixel_lit — standard lit surface that samples its colour texture with POINT filtering.
//
// The engine's complex.shader samples bilinearly no matter what the vmat asks for, so a true
// 64x64 texture turned into a smooth blur in game and the generated textures were written as
// 512x512 nearest-neighbour block images instead. This shader keeps the real 64x64 (or 128x128)
// files and shows hard texels at any distance: colour + cutout from one point-sampled texture,
// flat material parameters, PBR-style lighting.
//
// Per-texel lighting (PixelTexelLighting, default on — the Valheim sconce look): direct light is
// evaluated once per TEXEL, not per screen pixel. The shading point and every shadow lookup are
// moved to the centre of the texel under the pixel (from the screen-space derivatives of position
// and uv), and the normal is already per texel (flat facet + point-sampled normal map), so a texel
// is lit as one block — it glows or it does not — while falloff between texels stays smooth.
// PixelLightSteps posterises local lights into brightness bands on top of that (default 6, so the
// fall-off from a torch goes 0 % / 17 % / 33 % … in texel-edged steps, not a smooth glow);
// PixelLightCutoff and the sun's own PixelSunSteps default 0 = off. Indirect light (sky, cubemaps, AO) stays the
// engine's own, through ShadingModelStandard's combiner.
//
// Used by the generated elm bark / end grain / leaf-card materials (Blender/scripts/create_elm_tree.py),
// the build-kit wood, weapons, the buggy atlas and the streamed terrain (materials/terrain/terrain_ground.vmat:
// F_TERRAIN_SPLAT + PixelWorldUvMeters — the per-pixel biome blend, every ground layer tiled in world space).
//
// F_TERRAIN_SPLAT (Mark 2026-10-24: biome borders were stair-stepped or smeared — the colour lived on the vertices):
// the vertex carries WHAT the ground is — COLOR0 = Clover / Redwood / Amber land shares + the Mountain weight (from
// TerrainPreviewBiomeResolver), TEXCOORD0 = sand (beaches, beds) + wet, TEXCOORD1 = far-forest canopy darkening + the
// tier (0 near, 1 far, 2 horizon) — and the pixel decides what it LOOKS like: five point-sampled ground layers, the
// biome picked per pixel exactly as the resolver picks it (Mountain from 0.5, else the largest land share) with a
// noise-broken edge a few metres wide, sand and wet on top. The same rule on every tier, so near, far and horizon
// ground agree, and a border is an organic line at any vertex spacing — never the grid's stairs nor a 100 m smear.
//
// F_WIND ("Wind" feature, off by default) adds the tree sway in the vertex stage — trunk lean + limb
// motion from the object-space position, driven by WindSystem like grass_blade.shader. Only the standing
// elm's bark / leaf materials turn it on (Mark 2026-10-13: Rumple's clover tree stays untouched).
//
// Notes for anyone editing: F_RENDER_BACKFACES is defined by the engine for every shader (do not
// redeclare it). The cutout combo is named S_ALPHA_TEST on purpose: that name is what makes the engine run
// the pixel shader in the shadow / depth passes, so leaf cards cast cut-out shadows instead of squares
// (Mark 2026-10-13); the clip() itself is ours, and the depth pass returns DepthNormals with the card's
// alpha before any of the lighting code (which the shading model's alpha path never sees). Parameter names carry a
// Pixel prefix: a plain 'Roughness' collided with the engine's struct of that name. Derivatives
// (ddx / ddy, ComputeShadowReceiverNormal) must run before the light loop, in uniform control flow.
//
HEADER
{
	Description = "Lit surface, point-sampled pixel-art texture, per-texel lighting, optional alpha cutout";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
	// Tree wind: vertex sway driven by WindSystem (see the VS block). Off for everything but swaying trees.
	Feature( F_WIND, 0..1, "Wind" );
	// Vertex paint (terrain): vertex colour is the painted ground colour, vertex ALPHA is how much the texture
	// shows instead of it (1 = pure texture, 0 = pure vertex colour). Off = texture only, vertex colour ignored.
	Feature( F_VERTEX_PAINT, 0..1, "Rendering" );
	// Far terrain tier: sunk under the near chunks only (TerrainWorldManager publishes their square), true height
	// everywhere else — so there is no step where the near square ends (Mark 2026-10-22: "square chunks taken out").
	Feature( F_FAR_SINK, 0..1, "Terrain" );
	// The per-pixel biome blend (terrain ground). Supersedes F_VERTEX_PAINT on the terrain materials.
	Feature( F_TERRAIN_SPLAT, 0..1, "Terrain" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"

	float4 vColor : COLOR0 < Semantic( Color ); >;
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	float4 vColor : COLOR0;
	// F_TERRAIN_SPLAT: x = sand, y = wet, z = canopy darkening, w = tier
	float4 vSplat : TEXCOORD12;
};

VS
{
	#include "common/vertex.hlsl"

	StaticCombo( S_WIND, F_WIND, Sys( ALL ) );
	// Without this declaration "#if ( S_FAR_SINK )" below compiled to nothing: the far-tier sink and the horizon burial
	// never ran (2026-10-24 — the unburied horizon covered rivers and drew sand halos and straight edges round lakes)
	StaticCombo( S_FAR_SINK, F_FAR_SINK, Sys( ALL ) );

	#if ( S_WIND )
		// --- Tree wind (F_WIND) ---
		// Same wind inputs as grass_blade.shader, but a tree is a stiff body: the trunk leans a few
		// centimetres and rocks slowly (inertia), while the limbs — the parts far from the trunk axis —
		// bob up and down and flutter along the wind because they catch it. No vertex colours are
		// needed: the weights come from the OBJECT-space position, so artist meshes (Tripo, Rumple)
		// sway as-is. Every weight is a continuous function of position — a faceted mesh with split
		// normals shares no vertices between faces, so anything per-vertex-random would tear it open.
		//   trunk weight = (z / Tree Height)^Bend Power            → root pinned, top carries the lean
		//   limb weight  = (xy radius / Crown Radius)^Bend Power   → the trunk column stays put, tips move
		// Distances are written in metres and converted once here (40 engine units per metre, the
		// environment factor — TerrainWorldUnits.MetersToEngine).

		// Set once per frame by Survival.WindSystem on Scene.RenderAttributes (and on any material listed in its Wind Materials).
		// Strength and gust default to ZERO and every displacement term is proportional to the summed strength: with no
		// WindSystem publishing — the editor viewport, or a scene without the component — the trees stand perfectly still
		// (Mark 2026-10-12: no tree wind unless the game is running; a constant "calm-air stir" read as jitter in the editor).
		float3 WindDirection < Attribute( "WindDirection" ); Default3( 1.0, 0.0, 0.0 ); >;
		float WindStrength < Attribute( "WindStrength" ); Default( 0.0 ); >;
		float WindGust < Attribute( "WindGust" ); Default( 0.0 ); >;

		// Height of the model in object space; z at this height is the full trunk lean.
		float WindTreeHeightMeters < Default( 6.0 ); Range( 0.5, 60.0 ); UiGroup( "Wind,30/10" ); >;
		// Limb reach from the trunk axis; a vertex this far out gets the full limb motion.
		float WindCrownRadiusMeters < Default( 3.0 ); Range( 0.25, 30.0 ); UiGroup( "Wind,30/20" ); >;
		// Downwind lean of the tree top at full wind strength (the grass tip leans 0.6 m — trees far less).
		float WindTrunkSwayMeters < Default( 0.15 ); Range( 0.0, 2.0 ); UiGroup( "Wind,30/30" ); >;
		// Swing rate of the whole tree, cycles per second: lean downwind, overshoot back past upright, again.
		float WindTrunkRockHz < Default( 0.45 ); Range( 0.0, 3.0 ); UiGroup( "Wind,30/40" ); >;
		// Dip of a downwind limb tip (and lift of an upwind one) at full bend — a rotation about the trunk, so the
		// limb compresses toward the wind axis instead of stretching.
		float WindLimbBobMeters < Default( 0.10 ); Range( 0.0, 2.0 ); UiGroup( "Wind,30/50" ); >;
		// Rate of the small lag the limbs keep behind the trunk, cycles per second.
		float WindLimbHz < Default( 1.1 ); Range( 0.0, 6.0 ); UiGroup( "Wind,30/70" ); >;
		// 1 = root-to-tip ramp is linear, 2 = quadratic (base nearly still), higher = only the very tips move.
		float WindBendPower < Default( 2.0 ); Range( 1.0, 4.0 ); UiGroup( "Wind,30/110" ); >;
		// Leaf cards only (0 on bark): every card flaps on its own hinge, rate and phase (from its face normal), on top of the limb motion.
		float WindLeafFlutterMeters < Default( 0.0 ); Range( 0.0, 0.5 ); UiGroup( "Wind,30/120" ); >;
		float WindLeafHz < Default( 2.5 ); Range( 0.0, 10.0 ); UiGroup( "Wind,30/130" ); >;
		// Leaf cards only: downwind streaming of the cards inside a gust (on top of riding the limbs), and the rustle
		// amplitude triples at full gust so the crown visibly boils when the wind hits.
		float WindLeafGustMeters < Default( 0.0 ); Range( 0.0, 2.0 ); UiGroup( "Wind,30/135" ); >;
		// Distance fade: full sway up to Fade Start, none past Fade End (camera to the tree's root). Past the end the
		// vertex stage skips the wind math entirely — far trees are plain static meshes.
		float WindFadeStartMeters < Default( 60.0 ); Range( 0.0, 1000.0 ); UiGroup( "Wind,30/140" ); >;
		float WindFadeEndMeters < Default( 100.0 ); Range( 0.0, 2000.0 ); UiGroup( "Wind,30/150" ); >;

		static const float WIND_UNITS_PER_METER = 40.0;
		static const float WIND_TWO_PI = 6.2831853;
		static const float WIND_MAX_BEND_RADIANS = 1.3; // ~75°: a hurricane lays a tree over, it never folds it through the ground

		// Rodrigues rotation of v about unit axis a by angle r. Rotations keep every vertex at its distance from the
		// pivot, so a bent trunk or limb never grows longer (Mark 2026-10-13: the logs stretched).
		float3 RotateAboutAxis( float3 v, float3 a, float r )
		{
			float s = sin( r );
			float c = cos( r );
			return v * c + cross( a, v ) * s + a * ( dot( a, v ) * ( 1.0 - c ) );
		}
	#endif

	#if ( S_FAR_SINK )
		// TerrainWorldManager.SeaWater: SeaClock.y = far sink (m); SeaNearRect = the near chunks' square (engine units)
		float4 g_vFarSinkClock < Attribute( "SeaClock" ); Default4( 0.0, 2.5, 0.0, 0.0 ); >;
		float4 g_vFarSinkNearRect < Attribute( "SeaNearRect" ); Default4( 0.0, 0.0, -1.0, -1.0 ); >;
		// The horizon tier (TerrainWorldManager.Horizon, on Scene.RenderAttributes): xy = the far tier's centre, z = Far
		// View Distance (engine units); z <= 0 = no horizon. Only vertices marked tier 2 (TEXCOORD1.y) are buried — a
		// per-renderer attribute never reached this stage, and the unburied horizon bridged over every river channel near
		// the camera (Mark 2026-10-24: a river with no water and dark rectangles on the ground).
		float4 g_vHorizonSinkCircle < Attribute( "HorizonSinkCircle" ); Default4( 0.0, 0.0, -1.0, 0.0 ); >;
	#endif

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vColor = i.vColor;
		o.vSplat = float4( i.vTexCoord.xy, i.vTexCoord2.xy );

		#if ( S_FAR_SINK )
		{
			// Inside the near square the detailed chunks are the ground: the coarse far surface drops under them,
			// easing in over the first 4 m so the two meet at true height on the square's edge
			float2 xy = o.vPositionWs.xy;
			float2 inside = min( xy - g_vFarSinkNearRect.xy, g_vFarSinkNearRect.zw - xy );
			float flInside = min( inside.x, inside.y );
			float flSink = g_vFarSinkClock.y * 40.0 * saturate( flInside / (4.0 * 40.0) );
			// Inside the far tier the horizon tier is buried: 300 m down until ~770 m short of Far View Distance (well
			// within the far chunks everywhere), then 15 m across the ring where the far chunks may or may not reach,
			// back to its true height 400 m past it
			if ( g_vHorizonSinkCircle.z > 0.0 && i.vTexCoord2.y > 1.5 )
			{
				float d = length( xy - g_vHorizonSinkCircle.xy );
				float R = g_vHorizonSinkCircle.z;
				float s1 = saturate( (d - (R - 768.0 * 40.0)) / (406.0 * 40.0) );
				float s2 = saturate( (d - (R - 362.0 * 40.0)) / (762.0 * 40.0) );
				float flCircle = d < R - 362.0 * 40.0 ? lerp( 300.0, 15.0, s1 ) : 15.0 * (1.0 - s2);
				flSink = max( flSink, flCircle * 40.0 );
			}
			if ( flSink > 0.0 )
			{
				o.vPositionWs.z -= flSink;
				o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );
			}
		}
		#endif

		#if ( S_WIND )
			float3x4 matObjectToWorld = GetTransformMatrix( i.nInstanceTransformID );
			float3 vRootWs = mul( matObjectToWorld, float4( 0.0, 0.0, 0.0, 1.0 ) );

			// Distance fade (camera → root): past Fade End nothing below runs, the tree is a static mesh.
			// The shadow / depth passes MUST sway exactly like the main pass: a cheaper shadow sway was tried and the
			// displaced crowns fell inside their own un-swayed shadow (blue, self-shadowed leaves) — 2026-10-13.
			float flFadeStart = WindFadeStartMeters * WIND_UNITS_PER_METER;
			float flFadeEnd = max( WindFadeEndMeters, WindFadeStartMeters + 1.0 ) * WIND_UNITS_PER_METER;
			float flDistance = distance( vRootWs, g_vCameraPositionWs );
			float flFade = 1.0 - smoothstep( flFadeStart, flFadeEnd, flDistance );

			// WindGust is ONE value for the whole scene this frame, so every tree in view bends with the same gust at
			// the same moment, all of them downwind (Mark 2026-10-12: a gust must read as one event across the forest).
			// The base breeze breathes a little per tree so a calm forest is not frozen in lockstep.
			float2 vWind = normalize( WindDirection.xy + float2( 1e-4, 0.0 ) );
			float t = g_flTime;
			float flRootPhase = ( vRootWs.x * 0.013 + vRootWs.y * 0.007 );
			float flBreath = 0.85 + 0.15 * sin( t * 0.35 * WIND_TWO_PI + flRootPhase );
			float flBase = WindStrength * flBreath * flFade;
			float flGust = max( WindGust, 0.0 ) * flFade; // NOT capped: windtest hurricanes push past 1 and every term scales
			// Zero wind (editor, no WindSystem, dead calm) = zero motion: every term below is proportional to these.
			float flStrength = flBase + flGust;

			[branch]
			if ( flStrength > 0.0 )
			{
				float3 vPositionWs = mul( matObjectToWorld, float4( i.vPositionOs.xyz, 1.0 ) );
				// Instance scale: a tree placed at 3x sways 3x the centimetres, so it reads as the same tree, bigger.
				float flInstanceScale = length( mul( matObjectToWorld, float4( 1.0, 0.0, 0.0, 0.0 ) ) );

				// Weights from the object-space position (instance scale does not change them).
				float flHeight01 = saturate( i.vPositionOs.z / max( WindTreeHeightMeters * WIND_UNITS_PER_METER, 1.0 ) );
				float flRadius01 = saturate( length( i.vPositionOs.xy ) / max( WindCrownRadiusMeters * WIND_UNITS_PER_METER, 1.0 ) );
				float flTrunkWeight = pow( flHeight01, WindBendPower );
				// Limbs: far from the axis, and never the root flare (fade in over the bottom fifth of the tree).
				float flLimbWeight = pow( flRadius01, WindBendPower ) * smoothstep( 0.0, 0.2, flHeight01 );

				// ONE bend per tree, like a grass blade: the base breeze rocks it slowly, a gust leans the whole tree
				// downwind and lets it back (small per-tree wobble so a forest is not in lockstep). Trunk and limbs share
				// this value — nothing on the tree oscillates on its own clock, which is what read as jelly.
				// Swing: the whole lean (breeze and gust) rides one oscillation that overshoots back past upright
				// (-0.2) and far downwind (1.4) — big leans one way, then the other (Mark 2026-10-13).
				float flSwing = 0.6 + 0.8 * sin( t * WindTrunkRockHz * WIND_TWO_PI + flRootPhase * 3.0 );
				float flGustWobble = 0.9 + 0.1 * sin( t * WindTrunkRockHz * 1.7 * WIND_TWO_PI + flRootPhase );
				float flBend = ( flBase + flGust * flGustWobble ) * flSwing;

				// The bend is a ROTATION about the root, around the horizontal axis perpendicular to the wind, with the
				// angle growing up the trunk (Bend Power): the top travels Trunk Sway sideways at full bend and comes
				// down the arc as it goes, so the trunk and every limb keep their length — no stretch, slight compression
				// on the inside of the bend, exactly what wood does.
				float3 vRel = vPositionWs - vRootWs;                   // includes the instance scale
				float3 vAxis = float3( -vWind.y, vWind.x, 0.0 );
				float flTopAngle = atan2( WindTrunkSwayMeters, max( WindTreeHeightMeters, 0.1 ) );
				float flTrunkAngle = clamp( flTopAngle * flBend * flTrunkWeight, -WIND_MAX_BEND_RADIANS, WIND_MAX_BEND_RADIANS );
				float3 vBent = RotateAboutAxis( vRel, vAxis, flTrunkAngle );

				// Limbs: the same bend rotates each limb about the trunk axis at its own height, with a slight lag by
				// limb direction so the crown follows the trunk rather than snapping with it. Downwind limbs dip and
				// upwind limbs lift (Limb Bob at the crown edge), and both shorten toward the axis along the wind.
				float flLimbDirAngle = atan2( i.vPositionOs.y, i.vPositionOs.x );
				float flLimbLag = 0.85 + 0.15 * sin( t * WindLimbHz * WIND_TWO_PI + sin( flLimbDirAngle ) * 1.5 + flRootPhase );
				float flLimbTopAngle = atan2( WindLimbBobMeters, max( WindCrownRadiusMeters, 0.1 ) );
				float flLimbAngle = clamp( flLimbTopAngle * flBend * flLimbLag * flLimbWeight, -WIND_MAX_BEND_RADIANS, WIND_MAX_BEND_RADIANS );
				float3 vFromAxis = float3( vRel.xy, 0.0 );
				float3 vLimb = RotateAboutAxis( vFromAxis, vAxis, flLimbAngle ) - vFromAxis;

				// Leaves (leaf materials only — bark has Leaf Flutter 0 and skips all of this): every card gets its OWN
				// rate, phase and flap axis from its face normal (constant across the
				// card's vertices, different for every card, so nothing tears and no two cards jiggle alike). The card
				// flaps along its normal — its own hinge — plus a smaller sideways component.
				float3 vLeaf = 0.0;
				[branch]
				if ( WindLeafFlutterMeters > 0.0 || WindLeafGustMeters > 0.0 )
				{
				float3 vCardN = normalize( o.vNormalWs.xyz );
				float flCardRand = frac( sin( dot( vCardN, float3( 12.9898, 78.233, 37.719 ) ) ) * 43758.5453 );
				float flCardRand2 = frac( flCardRand * 7.31 + 0.17 );
				float flCardRand3 = frac( flCardRand * 3.77 + 0.61 );
				float flCardRand4 = frac( flCardRand2 * 5.13 + 0.29 );
				// Each card wiggles in its OWN random direction (Valheim-style): no two cards share an axis, a rate or
				// a phase, so a crown never settles into a repeating pattern however long you stare at it.
				float3 vCardWiggle = normalize( float3( flCardRand3 - 0.5, flCardRand4 - 0.5, ( flCardRand - 0.5 ) * 0.6 ) + 1e-4 );
				float flLeafMask = smoothstep( 0.0, 0.2, flHeight01 );
				// Rustle: breeze level plus up to three times that inside a gust (capped — a hurricane streams, it does not blur).
				float flLeafAmp = WindLeafFlutterMeters * WIND_UNITS_PER_METER * ( flBase + 3.0 * saturate( flGust ) ) * flLeafMask;
				float flLeafT = t * WindLeafHz * ( 0.6 + 0.8 * flCardRand ) * WIND_TWO_PI + flCardRand2 * WIND_TWO_PI + flRootPhase;
				vLeaf = ( vCardWiggle * sin( flLeafT ) + vCardN * ( 0.5 * sin( flLeafT * 1.3 + flCardRand3 * WIND_TWO_PI ) ) ) * flLeafAmp;
				// Gust: the cards stream downwind as one (per-card jitter keeps it from reading as a slide).
				// Streaming is capped at 1.5x — past that the cards flew off the limbs (the limbs themselves keep bending).
				vLeaf.xy += vWind * ( WindLeafGustMeters * WIND_UNITS_PER_METER * min( flGust, 1.5 ) * ( 0.8 + 0.2 * sin( flLeafT * 0.5 ) ) * flLeafMask );
				}

				float3 vOffset = ( vBent - vRel ) + vLimb + vLeaf * flInstanceScale;

				o.vPositionWs.xyz = vPositionWs + vOffset;
				o.vPositionPs.xyzw = Position3WsToPs( o.vPositionWs.xyz );
			}
		#endif

		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/classes/Light.hlsl"

	StaticCombo( S_ALPHA_TEST, F_ALPHA_TEST, Sys( ALL ) );
	StaticCombo( S_VERTEX_PAINT, F_VERTEX_PAINT, Sys( ALL ) );
	StaticCombo( S_TERRAIN_SPLAT, F_TERRAIN_SPLAT, Sys( ALL ) );

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureTranslucency, Linear, 8, "", "_trans", "Material,10/20", Default( 1.0 ) );
	// Optional tangent-space normal map, point-sampled like the colour: every texel on a flat facet gets
	// its own tilt, so the sun catches texels differently inside one face (Valheim-style per-pixel light).
	// The default flat normal leaves materials without a map untouched.
	CreateInputTexture2D( TextureNormal, Linear, 8, "NormalizeNormals", "_normal", "Material,10/70", Default3( 0.5, 0.5, 1.0 ) );

	// RGB = colour, A = cutout mask. Uncompressed: a 64x64 pixel-art tile is tiny anyway and
	// block compression would smear its hard edges.
	Texture2D g_tColor < Channel( RGB, Box( TextureColor ), Srgb ); Channel( A, Box( TextureTranslucency ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;

	// The whole point of this shader: nearest texel, no blending between texels.
	Texture2D g_tNormal < Channel( RGB, Box( TextureNormal ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	// Optional per-texel roughness (multiplied with PixelRoughness; default 1 = flat roughness).
	CreateInputTexture2D( TextureRoughness, Linear, 8, "", "_rough", "Material,10/35", Default( 1.0 ) );
	Texture2D g_tRough < Channel( R, Box( TextureRoughness ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	SamplerState g_sPointSampler < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;

	// --- Terrain splat (F_TERRAIN_SPLAT): TextureColor is the Clover grass; the other four ground layers tile the same
	// way (PixelWorldUvMeters, same size), the noise breaks the biome / sand edges up (tiled every Splat Noise metres).
	CreateInputTexture2D( TextureSplatRedwood, Srgb, 8, "", "_color", "Splat,40/10", Default3( 0.35, 0.16, 0.13 ) );
	CreateInputTexture2D( TextureSplatAmber, Srgb, 8, "", "_color", "Splat,40/20", Default3( 0.73, 0.50, 0.25 ) );
	CreateInputTexture2D( TextureSplatMountain, Srgb, 8, "", "_color", "Splat,40/30", Default3( 0.50, 0.49, 0.47 ) );
	CreateInputTexture2D( TextureSplatSand, Srgb, 8, "", "_color", "Splat,40/40", Default3( 0.62, 0.57, 0.45 ) );
	CreateInputTexture2D( TextureSplatNoise, Linear, 8, "", "", "Splat,40/50", Default3( 0.5, 0.5, 0.5 ) );
	Texture2D g_tSplatRedwood < Channel( RGB, Box( TextureSplatRedwood ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	Texture2D g_tSplatAmber < Channel( RGB, Box( TextureSplatAmber ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	Texture2D g_tSplatMountain < Channel( RGB, Box( TextureSplatMountain ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	Texture2D g_tSplatSand < Channel( RGB, Box( TextureSplatSand ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	Texture2D g_tSplatNoise < Channel( RGBA, Box( TextureSplatNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	// Past a texel per pixel the point-sampled layers would shimmer: blend to their filtered (mipped) colour
	SamplerState g_sSplatSmooth < Filter( ANISOTROPIC ); MaxAniso( 8 ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSplatNoiseSampler < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;
	// How far the noise pushes a border about (as a share of the biome weights: the borders' own ramps are ~100 m wide,
	// so 0.35 wanders a border by roughly ±15 m) and how soft the final edge is (higher = crisper)
	float SplatEdgeWander < Default( 0.35 ); Range( 0.0, 1.0 ); UiGroup( "Splat,40/60" ); >;
	float SplatEdgeSharpness < Default( 30.0 ); Range( 2.0, 200.0 ); UiGroup( "Splat,40/70" ); >;
	// Size of the edge noise (m per repeat of the noise texture)
	float SplatNoiseMeters < Default( 40.0 ); Range( 2.0, 400.0 ); UiGroup( "Splat,40/80" ); >;
	// How much darker wet ground is (beds, the band at the waterline)
	float SplatWetDarken < Default( 0.3 ); Range( 0.0, 1.0 ); UiGroup( "Splat,40/90" ); >;
	// Far-forest canopy colour (TerrainWorldManager.FarTier) and the large-scale brightness variation that keeps the
	// tiles from reading as tiles at a distance
	float3 SplatCanopyColor < UiType( Color ); Default3( 0.09, 0.22, 0.07 ); UiGroup( "Splat,40/100" ); >;
	float SplatMacroVariation < Default( 0.22 ); Range( 0.0, 1.0 ); UiGroup( "Splat,40/110" ); >;

	// One ground layer at this pixel: point-sampled up close, its filtered colour once a texel is under a pixel. No
	// visible repeat: a second copy of the tile, turned and shifted, takes over in noise patches (pick 0..1) — a hard
	// switch up close, so the texels stay crisp, a blend in the distance.
	float3 SplatLayerOnce( Texture2D tex, float2 uv, float2 dUvX, float2 dUvY, float smooth )
	{
		float3 crisp = tex.SampleLevel( g_sPointSampler, uv, 0 ).rgb;
		float3 soft = tex.SampleGrad( g_sSplatSmooth, uv, dUvX, dUvY ).rgb;
		return lerp( crisp, soft, smooth );
	}
	float3 SplatLayer( Texture2D tex, float2 uv, float2 dUvX, float2 dUvY, float smooth, float pick )
	{
		static const float2x2 turn = float2x2( 0.766, -0.643, 0.643, 0.766 );   // 40°
		float3 a = SplatLayerOnce( tex, uv, dUvX, dUvY, smooth );
		float3 b = SplatLayerOnce( tex, mul( turn, uv ) + float2( 0.37, 0.61 ), mul( turn, dUvX ), mul( turn, dUvY ), smooth );
		return lerp( a, b, pick );
	}

	// The ground's colour from the splat inputs (see the header). Uniform control flow: call before any branch.
	// The sea's wet-sand memory (TerrainWorldManager.SeaFoamMemory): G = how recently water stood here. Read within its rect
	// (engine units around the camera) and only on ground within ~1.5 m of sea level — the memory knows the laps, not the
	// ground height, so the height gate keeps the wet off cliffs and dunes behind the beach.
	Texture2D g_tSeaFoamMemory < Attribute( "SeaFoamMemory" ); SrgbRead( false ); >;
	SamplerState g_sSeaFoamMemorySampler < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	float4 g_vSeaFoamRect < Attribute( "SeaFoamRect" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	// x = sea level (engine units)
	float4 g_vSeaFoamInfo < Attribute( "SeaFoamInfo" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	float SeaWetMemory( float3 vPositionWs )
	{
		if ( g_vSeaFoamRect.w < 0.5 )
			return 0.0;
		float2 fuv = (vPositionWs.xy - g_vSeaFoamRect.xy) / g_vSeaFoamRect.z;
		if ( any( fuv < 0.0 ) || any( fuv > 1.0 ) )
			return 0.0;
		float above = (vPositionWs.z - g_vSeaFoamInfo.x) / 40.0;
		float2 edge = min( fuv, 1.0 - fuv ) * g_vSeaFoamRect.z / 40.0;
		float inside = smoothstep( 0.0, 40.0, min( edge.x, edge.y ) );
		return g_tSeaFoamMemory.SampleLevel( g_sSeaFoamMemorySampler, fuv, 0 ).g * (1.0 - smoothstep( 0.8, 1.6, above )) * inside;
	}

	// The five ground tiles at one projection, added in with weight w. Every derivative is taken by the caller, outside
	// any branch, so a projection that carries no weight can be skipped without undefined ddx / ddy.
	void SplatProjection( float2 uv, float2 dUvX, float2 dUvY, float pick, float w,
		inout float3 grass, inout float3 redwood, inout float3 amber, inout float3 rock, inout float3 sand )
	{
		float2 vSize;
		g_tColor.GetDimensions( vSize.x, vSize.y );
		float footprint = max( length( dUvX ), length( dUvY ) ) * vSize.x;
		float smooth = smoothstep( 0.6, 2.0, footprint );
		float p = smoothstep( 0.5 - 0.02 - 0.3 * smooth, 0.5 + 0.02 + 0.3 * smooth, pick );
		grass += SplatLayer( g_tColor, uv, dUvX, dUvY, smooth, p ) * w;
		redwood += SplatLayer( g_tSplatRedwood, uv, dUvX, dUvY, smooth, p ) * w;
		amber += SplatLayer( g_tSplatAmber, uv, dUvX, dUvY, smooth, p ) * w;
		rock += SplatLayer( g_tSplatMountain, uv, dUvX, dUvY, smooth, p ) * w;
		sand += SplatLayer( g_tSplatSand, uv, dUvX, dUvY, smooth, p ) * w;
	}

	// Triplanar (Mark 2026-10-29, voxel pit walls: "the rendering issue with the vertical lines" — the tiles were projected
	// from above only, so a steep dug wall stretched one texel row into vertical stripes). The tiles are projected along
	// the face's dominant axes, top (xy) plus the x and y sides (yz / xz), weighted by the surface normal to the 8th
	// power so level ground stays the single top projection it always was and only slopes past ~35° start mixing in a
	// side. A side with no weight is skipped (uniform-safe: derivatives are taken first).
	// The weights come from the INTERPOLATED vertex normal, never the triangle's own plane (ddx / ddy): on a steep smooth
	// face the two triangles of every grid quad lean a little differently, so a per-triangle weight flipped the mix
	// between the top and the side tile from one triangle to the next — horizontal sawtooth bands up every mountain
	// face (Mark 2026-10-30, "when the terrain gets very sloped it gets all zigzaggy"). The vertex normal is continuous
	// across the diagonal, so the blend is too.
	float3 TerrainSplatAlbedo( float3 vPositionWs, float3 vNormalWs, float2 vUv, float4 vWeights, float4 vSplat )
	{
		float flTile = max( PixelWorldUvMeters, 0.01 ) * 40.0;
		float2 vUvX = vPositionWs.yz / flTile;
		float2 vUvY = vPositionWs.xz / flTile;
		float2 dUvX = ddx( vUv );
		float2 dUvY = ddy( vUv );
		float2 dUvXx = ddx( vUvX );
		float2 dUvXy = ddy( vUvX );
		float2 dUvYx = ddx( vUvY );
		float2 dUvYy = ddy( vUvY );
		float3 vGeoN = abs( normalize( vNormalWs ) );
		float3 vTri = pow( vGeoN, 8.0 );
		vTri /= max( vTri.x + vTri.y + vTri.z, 1e-5 );

		// Edge noise: three independent channels per biome, plus a slow one for the macro variation
		float2 m = vPositionWs.xy / 40.0;
		float2 nUv = m / SplatNoiseMeters;
		float4 n1 = g_tSplatNoise.SampleLevel( g_sSplatNoiseSampler, nUv, 0 );
		float4 n2 = g_tSplatNoise.SampleLevel( g_sSplatNoiseSampler, nUv * 0.37 + float2( 0.31, 0.67 ), 0 );
		float4 n3 = g_tSplatNoise.SampleLevel( g_sSplatNoiseSampler, nUv * 0.11 + float2( 0.73, 0.19 ), 0 );
		// (the noise texture carries two FBM channels, r and g; b / a are flat)
		float3 jitter = float3( n1.r * 0.6 + n2.g * 0.4, n1.g * 0.6 + n2.r * 0.4, n1.r * 0.3 + n2.r * 0.3 + n1.g * 0.4 ) - 0.5;

		// Land biome per pixel: the largest Clover / Redwood / Amber share, softly (a softmax), shares pushed about by the noise
		float3 land = vWeights.rgb;
		float landSum = land.r + land.g + land.b;
		land /= max( landSum, 1e-4 );
		float3 l = land + jitter * SplatEdgeWander;
		float3 e = exp( (l - max( l.r, max( l.g, l.b ) )) * SplatEdgeSharpness );
		e /= (e.r + e.g + e.b);

		// Which copy of the tile shows here: patches a few metres across, hard-edged up close
		float pickN = g_tSplatNoise.SampleLevel( g_sSplatNoiseSampler, m / 9.0 + float2( 0.11, 0.83 ), 0 ).g;

		float3 grass = 0, redwood = 0, amber = 0, rock = 0, sand = 0;
		if ( vTri.z > 0.002 )
			SplatProjection( vUv, dUvX, dUvY, pickN, vTri.z, grass, redwood, amber, rock, sand );
		if ( vTri.x > 0.002 )
			SplatProjection( vUvX, dUvXx, dUvXy, pickN, vTri.x, grass, redwood, amber, rock, sand );
		if ( vTri.y > 0.002 )
			SplatProjection( vUvY, dUvYx, dUvYy, pickN, vTri.y, grass, redwood, amber, rock, sand );

		float3 albedo = grass * e.r + redwood * e.g + amber * e.b;
		// Mountain from 0.5 (TerrainPreviewBiomeResolver), the edge wandering like the others
		float mountain = smoothstep( 0.47, 0.53, vWeights.a + (n2.r * 0.5 + n1.g * 0.5 - 0.5) * SplatEdgeWander * 0.6 );
		albedo = lerp( albedo, rock, mountain );
		// Beaches and beds: sand, the band's edge broken into patches
		float sandAmt = smoothstep( 0.3, 0.7, vSplat.x + (n2.g * 0.6 + n1.r * 0.4 - 0.5) * 0.5 );
		albedo = lerp( albedo, sand, sandAmt );
		// Blackwater (no land weight at all): near-black
		if ( landSum + vWeights.a < 0.02 && vSplat.x < 0.02 )
			albedo = 0.012;
		// Wet, far canopy, macro variation
		albedo *= 1.0 - SplatWetDarken * max( saturate( vSplat.y ), SeaWetMemory( vPositionWs ) );
		albedo = lerp( albedo, pow( SplatCanopyColor, 2.2 ), saturate( vSplat.z ) );
		albedo *= 1.0 + (n3.r - 0.5) * SplatMacroVariation;
		return albedo;
	}

	// Screen-door fade (Mark 2026-10-20: trees must fade in / out and cross-fade between LODs, never hard-pop).
	// Set per renderer / per draw through RenderAttributes; the default leaves every other material untouched.
	// TreeFade 1 = fully drawn, 0 = gone (the far ring's bands; rocks / sticks / saplings fading in and out).
	float TreeFade < Attribute( "TreeFade" ); Default( 1.0 ); >;
	// Far impostor cards baked into the far terrain chunks (materials with PixelFarCard 1 only): their own fade and a
	// gamma tint (far_card_calibrate), both per renderer, so the ground mesh sharing that renderer is untouched.
	float FarCardFade < Attribute( "FarCardFade" ); Default( 1.0 ); >;
	float3 FarCardTint < Attribute( "FarCardTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	// Scales only the card's DIRECT (sun / moon) diffuse. A flat random-leaf card takes about 4x the sun a sparse LOD3
	// crown does per unit of albedo, while both take ambient 1:1 — one albedo tint could match the day or the night,
	// never both (cards 2x too dark at night when the tint matched noon, 2026-10-20). far_card_calibrate apply direct
	// sets this by day, apply tint sets the tint by night.
	float FarCardDirect < Attribute( "FarCardDirect" ); Default( 1.0 ); >;
	// Scales only the card's ambient (indirect diffuse). A card's face normal is horizontal; a crown's visible leaves
	// point every way, down included, and take less sky ambient per unit of albedo. far_card_calibrate apply ambient
	// sets this by night (ambient-led), apply direct by day.
	float FarCardAmbient < Attribute( "FarCardAmbient" ); Default( 1.0 ); >;
	float PixelFarCard < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/90" ); >;

	float PixelRoughness < Default( 0.9 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/30" ); >;
	// 0 = no specular at all: no highlight from any light and no reflected sky / probe (the surface is pure diffuse —
	// the terrain, Mark 2026-10-20: "remove the light bouncing off the terrain material 100%"). 1 = as before.
	float PixelSpecular < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/32" ); >;
	float PixelAlphaCutoff < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/40" ); >;
	// Leaf cards: blend the card normal toward world up so a crown shades as a soft mass, not flickering planes.
	float PixelNormalUp < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/50" ); >;
	// 1 = ignore the smooth vertex normals and light every triangle flat (faceted, low-poly look); 0 = smooth.
	float PixelFlatShading < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/60" ); >;
	// Scales the normal-map tilt (0 = ignore the map, 1 = as authored).
	float PixelNormalStrength < Default( 1.0 ); Range( 0.0, 3.0 ); UiGroup( "Material,10/80" ); >;
	// Caps N·L for direct light (sun and local lights). Foliage: a leaf card facing the sun square-on took the full
	// 3.2x sun and tonemapped to white seen from above (Mark 2026-10-20: "a lot of white in them when I'm up high");
	// real leaves are thin, translucent and shade each other, so the elm leaf material caps at 0.55. 1 = untouched.
	float PixelDirectMax < Default( 1.0 ); Range( 0.1, 1.0 ); UiGroup( "Lighting,20/40" ); >;
	// Tiling: texture repeats per UV unit (dev meshes map 0..1 across the whole face).
	float2 PixelTexCoordScale < Default2( 1.0, 1.0 ); Range2( 0.01, 0.01, 500.0, 500.0 ); UiGroup( "Material,10/90" ); >;
	// > 0 = ignore the mesh UVs and tile the texture on the world XY plane, one repeat per this many metres
	// (terrain: chunk UVs are per chunk, a world tile is seamless across chunks). 0 = mesh UVs.
	float PixelWorldUvMeters < Default( 0.0 ); Range( 0.0, 100.0 ); UiGroup( "Material,10/95" ); >;

	// --- Per-texel lighting ("Lighting" group) ---
	// 1 = direct light is evaluated at the texel centre so every texel is one lit block; 0 = the engine's per-pixel standard model.
	float PixelTexelLighting < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Lighting,20/10" ); >;
	// Local lights (torch, sconce, campfire): N brightness levels (6 = 0 %, 17 %, 33 % … bands whose edges
	// sit on texel boundaries — the boxy Valheim fall-off); 0 = smooth falloff between texels.
	float PixelLightSteps < Default( 6.0 ); Range( 0.0, 16.0 ); UiGroup( "Lighting,20/20" ); >;
	// Local lights: a texel receiving less than this fraction of the light shows none of it.
	float PixelLightCutoff < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Lighting,20/30" ); >;
	// Sun / moon: 0 = smooth (default). The banding is for fires, torches and lanterns only (Mark 2026-10-13,
	// after trying 5 steps on the sun and rejecting it); N = N levels if a material ever wants it.
	float PixelSunSteps < Default( 0.0 ); Range( 0.0, 16.0 ); UiGroup( "Lighting,20/40" ); >;

	float QuantizeLight( float f, float steps, float cutoff )
	{
		if ( cutoff > 0.0 && f < cutoff )
			return 0.0;
		if ( steps >= 1.0 )
			f = floor( f * steps + 0.5 ) / steps;
		return f;
	}

	// World position of the centre of the texel under this pixel, on the surface plane: solve the
	// 2x2 screen-derivative system for the pixel offset that lands on the texel centre, then walk
	// the position derivatives by that offset. Call in uniform control flow (uses ddx / ddy).
	float3 TexelCentreWs( float3 vPositionWs, float2 vUv )
	{
		float2 vTexSize;
		g_tColor.GetDimensions( vTexSize.x, vTexSize.y );
		vTexSize = max( vTexSize, 1.0 );

		float2 vUvCentre = ( floor( vUv * vTexSize ) + 0.5 ) / vTexSize;
		float2 dUv = vUvCentre - vUv;

		float2 dUvDx = ddx( vUv );
		float2 dUvDy = ddy( vUv );
		float3 dPDx = ddx( vPositionWs );
		float3 dPDy = ddy( vPositionWs );

		float det = dUvDx.x * dUvDy.y - dUvDy.x * dUvDx.y;
		if ( abs( det ) < 1e-14 )
			return vPositionWs;

		float a = ( dUv.x * dUvDy.y - dUvDy.x * dUv.y ) / det;
		float b = ( dUvDx.x * dUv.y - dUv.x * dUvDx.y ) / det;
		// A texel seen edge-on can be hundreds of pixels long; cap the walk so a degenerate quad never flies off.
		a = clamp( a, -256.0, 256.0 );
		b = clamp( b, -256.0, 256.0 );
		return vPositionWs + dPDx * a + dPDy * b;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 vPositionWs = i.vPositionWithOffsetWs + g_vCameraPositionWs;
		float2 vUv = PixelWorldUvMeters > 0.0
			? vPositionWs.xy / ( PixelWorldUvMeters * 40.0 )
			: i.vTextureCoords.xy * PixelTexCoordScale;
		float4 tex = g_tColor.Sample( g_sPointSampler, vUv );
		#if ( S_TERRAIN_SPLAT )
			float3 vSplatAlbedo = TerrainSplatAlbedo( vPositionWs, i.vNormalWs, vUv, i.vColor, i.vSplat );
		#endif

		#if ( S_ALPHA_TEST )
			clip( tex.a - PixelAlphaCutoff );
		#endif

		// Screen-door fade: a 4x4 Bayer threshold per screen pixel, applied in every pass (shadows and the
		// depth prepass fade with the colour), no blending and no sorting.
		float flFade = PixelFarCard > 0.5 ? FarCardFade : TreeFade;
		if ( flFade < 0.999 )
		{
			static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
			uint2 p = uint2( i.vPositionSs.xy ) & 3;
			float threshold = ( bayer[ p.y * 4 + p.x ] + 0.5 ) / 16.0;
			clip( flFade - threshold );
		}

		// Shadow maps / depth prepass: nothing below matters, only the cut-out shape (and the normal for the prepass).
		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( normalize( i.vNormalWs ), PixelRoughness, tex.a );

		Material m = Material::Init( i );

		// Double-sided cards: face the normal toward the camera so the back of a card is lit
		// like its front (the usual SV_IsFrontFace input is owned by the common pixel input).
		float3 vNormal = normalize( i.vNormalWs );

		// Faceted: the plane normal of the triangle itself from screen-space derivatives, signed like the vertex normal.
		float3 vFlatNormal = normalize( cross( ddy( vPositionWs ), ddx( vPositionWs ) ) );
		if ( dot( vFlatNormal, vNormal ) < 0.0 )
			vFlatNormal = -vFlatNormal;
		vNormal = normalize( lerp( vNormal, vFlatNormal, saturate( PixelFlatShading ) ) );

		float3 vToCamera = g_vCameraPositionWs - vPositionWs;
		if ( dot( vNormal, vToCamera ) < 0.0 )
			vNormal = -vNormal;

		// Per-texel tilt from the normal map, in the tangent frame of the (possibly flattened) face normal.
		float3 vNormalTs = DecodeNormal( g_tNormal.Sample( g_sPointSampler, vUv ).rgb );
		vNormalTs.xy *= PixelNormalStrength;
		vNormal = TransformNormal( normalize( vNormalTs ), vNormal, i.vTangentUWs, i.vTangentVWs );

		m.Normal = normalize( lerp( vNormal, float3( 0.0, 0.0, 1.0 ), PixelNormalUp ) );

		m.Albedo = tex.rgb;
		#if ( S_VERTEX_PAINT )
			// Painted vertex colour arrives in gamma (Color32 bytes); linearise like vertex_color.shader did, then blend
			// toward the texture by vertex alpha.
			float3 vPaint = pow( max( i.vColor.rgb, 0.0 ), 2.2 );
			m.Albedo = lerp( vPaint, tex.rgb, saturate( i.vColor.a ) );
		#endif
		#if ( S_TERRAIN_SPLAT )
			m.Albedo = vSplatAlbedo;
		#endif
		if ( PixelFarCard > 0.5 )
			m.Albedo *= pow( max( FarCardTint, 0.0 ), 2.2 );   // far impostor cards: gamma tint from far_card_calibrate
		m.Metalness = 0.0;
		m.Roughness = saturate( PixelRoughness * g_tRough.Sample( g_sPointSampler, vUv ).r );
		if ( PixelSpecular <= 0.0 )
			m.Roughness = 1.0;   // the engine's indirect specular (sky / probe reflection) is flattest at full roughness
		m.AmbientOcclusion = 1.0;
		m.Opacity = 1.0;

		// Legacy per-pixel path: the engine's own standard model, untouched.
		if ( PixelTexelLighting < 0.5 )
			return ShadingModelStandard::Shade( i, m );

		// ---- Per-texel direct lighting ----
		// Everything derivative-based happens here, before any divergent branch.
		// The texel centre is found by walking the top-projection texel; on a steep dug wall that texel is edge-on and the
		// walk flies hundreds of pixels along the wall, so the shadow was read from the wrong spot — one stripe per texel
		// column, speckled (Mark 2026-10-29, "the vertical lines"). With the splat triplanar, shade at the texel of the
		// projection that faces the surface instead — picked by the vertex normal, like the triplanar weights, so the
		// pick does not flip per triangle on a steep face (2026-10-30).
		float2 vUvShade = vUv;
		#if ( S_TERRAIN_SPLAT )
			float3 vGeoA = abs( normalize( i.vNormalWs ) );
			float flTileShade = max( PixelWorldUvMeters, 0.01 ) * 40.0;
			vUvShade = vGeoA.z >= max( vGeoA.x, vGeoA.y ) ? vUv : ( vGeoA.x >= vGeoA.y ? vPositionWs.yz / flTileShade : vPositionWs.xz / flTileShade );
		#endif
		float3 vShadePosWs = TexelCentreWs( m.WorldPosition, vUvShade );
		float3 vReceiverNormalWs = ComputeShadowReceiverNormal( m.WorldPosition );

		Decals::Apply( m.WorldPosition, m );

		float3 N = m.Normal;
		float3 V = normalize( g_vCameraPositionWs - m.WorldPosition );
		float flRough = saturate( m.Roughness );
		// Modest Blinn-Phong highlight — bark and planks are rough, so this mostly matters on wet-looking props.
		float flGloss = lerp( 96.0, 4.0, flRough );
		float flSpecWeight = ( 1.0 - flRough ) * 0.12 * saturate( PixelSpecular );

		float3 vDirectDiffuse = 0.0;
		float3 vDirectSpecular = 0.0;

		// Sun / moon through the directional constants (the engine's fast path).
		bool bSunDone = false;
		if ( g_DirectionalLightEnabled )
		{
			bSunDone = true;
			float3 L = normalize( -g_DirectionalLightDirection.xyz );
			float flNdotL = min( saturate( dot( N, L ) ), PixelDirectMax );
			float flVis = 1.0;
			if ( g_DirectionalLightCascadeCount > 0 )
				flVis = DirectionalLightShadow::GetVisibility( vShadePosWs, vReceiverNormalWs, i.vPositionSs );

			float f = QuantizeLight( flNdotL * flVis, PixelSunSteps, 0.0 );
			vDirectDiffuse += g_DirectionalLightColor.rgb * f;
			float3 H = normalize( L + V );
			vDirectSpecular += g_DirectionalLightColor.rgb * ( pow( saturate( dot( N, H ) ), flGloss ) * flSpecWeight * f );
		}

		// Local lights from the cluster list (plus baked ones when a probe / lightmap is bound).
		uint nLightCount = Light::Count( i.vPositionSs );
		[loop]
		for ( uint n = 0; n < nLightCount; n++ )
		{
			Light light = Light::From( vShadePosWs, i.vPositionSs, n, i.vLightmapUV, vReceiverNormalWs );
			bool bDirectional = light.LightData.Type == LightType::LightTypeDirectional;
			if ( bDirectional && bSunDone )
				continue; // already lit through the constants above

			if ( !bDirectional )
			{
				float3 vToLight = light.Position - vShadePosWs;
				if ( dot( vToLight, vToLight ) > light.LightData.RadiusSquared )
					continue;
			}

			float flNdotL = min( saturate( dot( N, light.Direction ) ), PixelDirectMax );
			float f = flNdotL * light.Attenuation * light.Visibility;
			f = bDirectional
				? QuantizeLight( f, PixelSunSteps, 0.0 )
				: QuantizeLight( f, PixelLightSteps, PixelLightCutoff );
			if ( f <= 0.0 )
				continue;

			vDirectDiffuse += light.Color * f;
			float3 H = normalize( light.Direction + V );
			vDirectSpecular += light.Color * ( pow( saturate( dot( N, H ) ), flGloss ) * flSpecWeight * f );
		}

		// ---- Indirect light from the engine (sky ambient, cubemaps, AO) ----
		CombinerInput ci = ShadingModelStandard::MaterialToCombinerInput( m );
		LightingTerms_t lt = InitLightingTerms();
		CalculateIndirectLighting( lt, ci );

		float3 vDiffuseAO = CalculateDiffuseAmbientOcclusion( ci, lt );
		float3 vSpecularAO = CalculateSpecularAmbientOcclusion( ci, lt );
		// Far impostor cards: no screen-space AO. The LOD3 ring they continue is drawn after the AO pass (command list,
		// never in the depth prepass) and so is never occluded; cards baked into the far chunks ARE in the prepass and the
		// dense little geometry occluded itself 1.4-1.8x darker than the ring whenever ambient light led (night / dusk, 2026-10-20).
		if ( PixelFarCard > 0.5 )
		{
			vDiffuseAO = float3( 1.0, 1.0, 1.0 );
			vSpecularAO = float3( 1.0, 1.0, 1.0 );
		}
		lt.vIndirectDiffuse.rgb *= vDiffuseAO;
		lt.vIndirectSpecular.rgb *= vSpecularAO;
		vDirectDiffuse *= lerp( float3( 1.0, 1.0, 1.0 ), vDiffuseAO, ci.flAmbientOcclusionDirectDiffuse );

		if ( PixelFarCard > 0.5 )
		{
			vDirectDiffuse *= FarCardDirect;
			lt.vIndirectDiffuse.rgb *= FarCardAmbient;
		}
		float3 vColor = ( vDirectDiffuse + lt.vIndirectDiffuse.rgb ) * ci.vDiffuseColor.rgb
		              + vDirectSpecular + lt.vIndirectSpecular.rgb + ci.vEmissive.rgb;


		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, float4( vColor, 1.0 ) );
	}
}
