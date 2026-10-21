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
// F_VERTEX_PAINT + PixelWorldUvMeters — painted biome vertex colours, the Clover ground texture tiled in world space).
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
};

VS
{
	#include "common/vertex.hlsl"

	StaticCombo( S_WIND, F_WIND, Sys( ALL ) );

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

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vColor = i.vColor;

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
		float3 vShadePosWs = TexelCentreWs( m.WorldPosition, vUv );
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
