//
// aerial_perspective: the fog between the camera and everything it sees (Code/World/Environment/AerialPerspective.cs).
//
// Mark 2026-11-10: "make it like real fog and not just ruin the color scheme". One full-screen pass before
// post-processing, so every shader (ground, grass, trees, far cards, water, entities, the caldera ring) sits in the same
// fog. Exponential height fog: optical depth = distance × air density averaged between the camera's altitude and the
// point's (sea level = 0, 1/e per scale height), fog = 1 − exp(−optical / fog distance), no cap, no desaturation —
// a thing keeps its own colour and simply disappears into the fog with distance.
// The fog colour is the sky dome's colour along the same view ray (sky_dome.shader's gradient, sun-side band and halo,
// without clouds or stars), so a fully fogged object IS the sky behind it: no pale band where rock meets sky.
// The sky itself (depth past the dome) is left alone. Depth is the engine's scene depth (Depth::GetLinear, the prepass
// chain): a grabbed copy of the live depth buffer came back banded and stale in the live viewport (2026-11-19, the
// "shaders partially visible through the fog" bug). Things that are not in the prepass (the FarTrees ring) cannot be
// fogged here: they are drawn AFTER this pass and fog themselves with the same maths (pixel_lit.shader RingFog) - keep
// the two in step.
//
//   ColorBuffer       the frame so far (GrabFrameTexture)
//   FogShape          x = 1 / fog distance, y = 1 / scale height (both 1/engine units), z = strength (0 = off), w = sky cut-off (engine units)
//   FogStart          clear distance (engine units): no fog at all closer than this, and the fog builds up from there
//                     (Mark 2026-11-19: "the fog is 0% inside 100m and slowly increases up from there")
//   FogZenith / FogHorizon   the preset sky gradient (from the cycle)
//   FogSunGlow        rgb = sun glow colour, a = glow strength (the preset's), toward FogSunDirection
//   FogMistTint / FogMistInverse / FogMistStart / FogMistMax   the air of the camera's biome
//                     (data/biome_atmosphere.json), each its own attribute: tint = multiplies the sky fog colour,
//                     inverse = 1 / mist distance (1 / engine units, 0 = no mist), start = mist clear distance (engine units),
//                     max = the most the mist may cover
//   FogMistSunTint    the mist's sun side: sun glow colour x this, added toward the sun
//   FogColorHold      how much of its own hue a fogged thing keeps (ground; the ring has its own, lower)
//   FogShafts / FogSunU / FogSunV   sun shafts: strength and the sun's place on screen
//   FogMax / FogTint  the most the world fog may cover anything; its colour as a multiple of the sky colour
//   FogSnow / FogSnowWind   falling snow 0-1 and its wind drift (engine units / s)
//   (the biome shade is not here: it scales the sky ambient in EnvironmentDayNightCycle, nothing is packed in a float4)
//   SeaFogPlane       x = sea level, w = sea drawn (scene attribute from TerrainWorldManager.SeaWater): the translucent sea
//                     is not in the depth, so the fog clamps a downward ray at sea level
//
HEADER
{
	DevShader = true;
	Description = "Distance fog: exponential height fog toward the sky dome's colour along each view ray";
}

MODES
{
	Default();
	Forward();
}

FEATURES
{
}

COMMON
{
	#include "postprocess/shared.hlsl"
}

struct VertexInput
{
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PixelInput
{
	float2 uv : TEXCOORD0;

	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif

	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.uv = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"
	#include "common/classes/Depth.hlsl"

	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( false ); >;

	float4 g_vFogShape < Attribute( "FogShape" ); Default4( 0.0000056, 0.0000208, 1.0, 3000000.0 ); >;
	float g_flFogStart < Attribute( "FogStart" ); Default( 4000.0 ); >;
	float3 g_vFogZenith < Attribute( "FogZenith" ); Default3( 0.25, 0.45, 0.85 ); >;
	float3 g_vFogHorizon < Attribute( "FogHorizon" ); Default3( 0.72, 0.76, 0.84 ); >;
	float4 g_vFogSunGlow < Attribute( "FogSunGlow" ); Default4( 1.0, 0.9, 0.7, 0.0 ); >;
	float3 g_vFogSunDirection < Attribute( "FogSunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;
	// Scalars, not packed float4s: the density first rode in a float4's alpha and never arrived (0 here, so no mist at all; the cap on the ring), 2026-11-23
	float3 g_vFogMistTint < Attribute( "FogMistTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	float g_flFogMistInverse < Attribute( "FogMistInverse" ); Default( 0.0 ); >;
	float g_flFogMistStart < Attribute( "FogMistStart" ); Default( 0.0 ); >;
	float g_flFogMistMax < Attribute( "FogMistMax" ); Default( 1.0 ); >;
	// What the mist already covers where it begins (0 = builds from nothing)
	float g_flFogMistFloor < Attribute( "FogMistFloor" ); Default( 0.0 ); >;
	// Sun side of the mist: added toward the sun (sun glow colour x this), so the air glows warm sunward and stays the
	// tint's colour away from it (the Valheim Black Forest look, Mark 2026-11-23)
	float3 g_vFogMistSunTint < Attribute( "FogMistSunTint" ); Default3( 0.0, 0.0, 0.0 ); >;
	// How much of a thing's own hue its fog keeps (0 = fades to the plain sky colour, the old white-out)
	float g_flFogColorHold < Attribute( "FogColorHold" ); Default( 0.0 ); >;
	// The most the world fog may cover anything (data/biome_atmosphere.json fogMax)
	float g_flFogMax < Attribute( "FogMax" ); Default( 1.0 ); >;
	// The world fog's colour as a multiple of the sky colour along the ray (fogTint): a deep blue haze, not the bright sky
	float3 g_vFogTint < Attribute( "FogTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	// Falling snow: amount 0-1 (the camera's biome) and the wind's drift (engine units / s)
	float g_flFogSnow < Attribute( "FogSnow" ); Default( 0.0 ); >;
	float3 g_vFogSnowWind < Attribute( "FogSnowWind" ); Default3( 0.0, 0.0, 0.0 ); >;
	// Sun shafts: strength (0 = off) and the sun's place on screen (0-1, top-left origin)
	float g_flFogShafts < Attribute( "FogShafts" ); Default( 0.0 ); >;
	float g_flFogSunU < Attribute( "FogSunU" ); Default( 0.5 ); >;
	float g_flFogSunV < Attribute( "FogSunV" ); Default( 0.5 ); >;
	// x = sea level (engine units), w = 1 while the sea is drawn (TerrainWorldManager.SeaWater, scene attribute)
	float4 g_vSeaFogPlane < Attribute( "SeaFogPlane" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// World-space view ray through a screen pixel (g_matProjectionToWorld is camera-relative)
	float3 ViewRay( float2 screenPos )
	{
		float3 ndc = float3( screenPos * g_vInvViewportSize, 0.5 );
		ndc.y = 1.0 - ndc.y;
		ndc.xy = 2.0 * ndc.xy - 1.0;
		float4 p = mul( g_matProjectionToWorld, float4( ndc, 1.0 ) );
		return normalize( p.xyz / p.w );
	}

	// The dome's sky along a direction (sky_dome.shader: gradient, sun-side horizon band, halo). Below the horizon the
	// fog is the horizon colour: looking down through haze you see the air lit like the horizon, not the dome's dark floor.
	float3 FogColour( float3 dir )
	{
		float h = saturate( dir.z );
		float3 sky = lerp( g_vFogHorizon, g_vFogZenith, pow( h, 0.5 ) );
		float3 sunDir = normalize( g_vFogSunDirection );
		float2 flatDir = normalize( dir.xy + 1e-4 );
		float2 flatSun = normalize( sunDir.xy + 1e-4 );
		float sunSide = pow( saturate( dot( flatDir, flatSun ) * 0.5 + 0.5 ), 4.0 );
		float band = 1.0 - saturate( h * 4.0 );
		sky = lerp( sky, g_vFogSunGlow.rgb, saturate( g_vFogSunGlow.a * sunSide * band ) );
		float sunDot = saturate( dot( normalize( float3( dir.xy, max( dir.z, 0.0 ) ) + float3( 0, 0, 1e-4 ) ), sunDir ) );
		sky += g_vFogSunGlow.rgb * g_vFogSunGlow.a * ( pow( sunDot, 48.0 ) * 0.8 + pow( sunDot, 6.0 ) * 0.15 );
		return sky;
	}

	// Distance along the view ray (engine units) to what the scene depth holds at a pixel; rayScale = 1 / cos(ray, forward)
	float SceneDistance( int2 p, float rayScale )
	{
		return Depth::GetLinear( float2( p ) + 0.5 ) * rayScale;
	}

	// The biome mist's colour along a ray: the sky fog colour times the tint, turning to the sun's colour toward the sun.
	float3 MistColour( float3 ray, float3 skyFog )
	{
		float3 sunDir = normalize( g_vFogSunDirection );
		float sunUp = saturate( sunDir.z * 4.0 + 0.2 );
		float phase = pow( saturate( dot( ray, sunDir ) * 0.5 + 0.5 ), 4.0 ) * sunUp;
		// The sun's colour REPLACES the tint toward the sun: added on top, warm over blue came out a grey-mauve wash
		return lerp( skyFog * g_vFogMistTint, g_vFogSunGlow.rgb * g_vFogMistSunTint, phase * step( 1e-4, dot( g_vFogMistSunTint, float3( 1.0, 1.0, 1.0 ) ) ) );
	}

	// Fog that keeps part of the fogged thing's hue: a far green hill fades to a blue-green, not to white.
	float3 HoldColour( float3 fogColour, float3 scene )
	{
		float luma = dot( scene, float3( 0.299, 0.587, 0.114 ) );
		float3 chroma = clamp( scene / max( luma, 1e-3 ), 0.35, 1.9 );
		return fogColour * lerp( float3( 1.0, 1.0, 1.0 ), chroma, g_flFogColorHold );
	}

	// Sun shafts (god rays): from each pixel, walk toward the sun's place on screen and count how much open sky the walk
	// crosses - light pours through the gaps between crowns and streaks away from the sun. One pass, 64 depth reads, only
	// for pixels facing the sun. 64, with a soft sky test, so the walk never skips a gap: at 24 hard-tested reads neighbouring
	// pixels caught different leaf-card gaps and the shafts came out as a speckled texture, not beams (Mark 2026-11-23).
	float SunShafts( float2 ss, float3 ray )
	{
		if ( g_flFogShafts <= 0.0 )
			return 0.0;
		float facing = saturate( dot( ray, normalize( g_vFogSunDirection ) ) );
		float w = pow( facing, 5.0 );
		if ( w <= 0.004 )
			return 0.0;
		float2 size = 1.0 / g_vInvViewportSize;
		float2 sunPx = float2( g_flFogSunU, g_flFogSunV ) * size;
		float2 stride = ( sunPx - ss ) * ( 0.9 / 64.0 );
		float2 p = ss;
		float acc = 0.0;
		float decay = 1.0;
		for ( int n = 0; n < 64; n++ )
		{
			p += stride;
			float2 q = clamp( p, float2( 0.5, 0.5 ), size - 0.5 );
			// Soft sky test over the last third of the way to the dome, read between pixel centres (bilinear position)
			acc += smoothstep( g_vFogShape.w * 0.4, g_vFogShape.w * 0.6, Depth::GetLinear( q ) ) * decay;
			decay *= 0.983;
		}
		return acc * ( 1.0 / 64.0 ) * w * g_flFogShafts;
	}

	float3 Fogged( float2 ss, float3 scene, float3 ray, float strength )
	{
		float rayScale = 1.0 / max( dot( ray, g_vCameraDirWs ), 0.05 );
		float dist = SceneDistance( int2( ss ), rayScale );

		// The sea writes no depth (translucent), so behind it the scene depth holds the sea floor, or nothing at all: a ray
		// grazing the open sea measured a long slant down to a deep floor and was fogged like the horizon, while where no
		// floor was streamed it read as sky and was left clear - a pale film with a hard edge over the sea from ~200 m out to
		// the caldera (Mark 2026-11-20). A ray heading down crosses sea level first: fog the water at that crossing.
		if ( g_vSeaFogPlane.w > 0.5 && ray.z < -1e-4 && g_vCameraPositionWs.z > g_vSeaFogPlane.x )
			dist = min( dist, ( g_vSeaFogPlane.x - g_vCameraPositionWs.z ) / ray.z );

		// The sky (and anything drawn at the dome) keeps its colour: the dome already is the far colour. Tested on the
		// distance along the ray, not the view depth: off-centre the dome's view depth is shorter than its radius, and the
		// view-depth test fogged the whole sky outside a cone round the screen centre (clouds gone at the edges).
		if ( dist >= g_vFogShape.w )
		{
			// A silhouette pixel's colour can hold geometry its depth sample missed (anti-aliased edge): left unfogged, the
			// heavily fogged caldera crest drew a dotted dark line against the sky. Next to geometry, fog at its distance.
			int2 p = int2( ss );
			float edge = min( min( SceneDistance( p + int2( 1, 0 ), rayScale ), SceneDistance( p - int2( 1, 0 ), rayScale ) ),
				min( SceneDistance( p + int2( 0, 1 ), rayScale ), SceneDistance( p - int2( 0, 1 ), rayScale ) ) );
			if ( edge >= g_vFogShape.w )
				return scene;
			dist = edge;
		}

		// The camera's biome air first, nearest the eye: the biome's own mist (Mark 2026-11-23: a light fogginess in Clover
		// Hills, dark and mysterious under the Redwood trees). The dark itself is real light - the biome's shade scales the
		// sky ambient in EnvironmentDayNightCycle, so sunlit patches stay bright. Keep in step with pixel_lit RingFog.
		float3 skyFog = FogColour( ray );
		float3 mistColour = MistColour( ray, skyFog );   // the mist is the air's colour alone: silhouettes fade to it
		// The mist does not creep up from nothing: it comes in at its floor over the 40 m past the clear distance and
		// builds from there (Mark 2026-11-23: "i can see trees 300m away totally fine, try adding like 10% to the fogs
		// strength when it first starts").
		float mistFloor = g_flFogMistFloor * smoothstep( g_flFogMistStart, g_flFogMistStart + 1600.0, dist );
		float mist = min( mistFloor + ( 1.0 - mistFloor ) * ( 1.0 - exp( -max( dist - g_flFogMistStart, 0.0 ) * g_flFogMistInverse ) ), g_flFogMistMax );
		mist *= step( 1e-9, g_flFogMistInverse );
		float3 original = scene;
		scene = lerp( scene, lerp( scene, mistColour, mist ), strength );

		// No fog inside the clear distance: the fog starts at 0 there and builds up with the air beyond it.
		float fogDist = dist - g_flFogStart;
		if ( fogDist <= 0.0 )
			return scene;

		float3 hit = g_vCameraPositionWs + ray * dist;
		float3 from = g_vCameraPositionWs + ray * g_flFogStart;

		// Air density averaged along the fogged stretch of the ray through an exponential atmosphere (sea level = 0)
		float k = g_vFogShape.y;
		float h0 = max( from.z, 0.0 ) * k;
		float h1 = max( hit.z, 0.0 ) * k;
		float dh = h1 - h0;
		float density = abs( dh ) > 1e-3 ? ( exp( -h0 ) - exp( -h1 ) ) / dh : exp( -h0 );
		float fog = min( 1.0 - exp( -fogDist * density * g_vFogShape.x ), g_flFogMax );

		// Behind the biome's mist the far world fades to the same mist, not to the pale sky: the mist's colour covers the
		// world fog by as much as the mist covers the scene.
		float3 worldFog = lerp( HoldColour( skyFog * g_vFogTint, original ), mistColour, mist );
		return lerp( scene, lerp( scene, worldFog, fog ), strength );
	}

	float3 SnowHash( float3 p )
	{
		p = frac( p * float3( 0.1031, 0.1030, 0.0973 ) );
		p += dot( p, p.yxz + 33.33 );
		return frac( ( p.xxy + p.yxx ) * p.zyx );
	}

	// Falling snow (Mark 2026-11-23, Ivory Alps: "snow that falls in this biome constantly"). Flakes live in the WORLD: six
	// shells round the camera from 3 m to 110 m; on each, the point the view ray meets is looked up in a lattice that
	// falls and drifts with the wind, and a cell's flake is drawn where the ray passes it. So flakes hold their place as
	// the camera turns and moves, near ones are large and fast, far ones a fine haze, and anything nearer than a shell
	// hides its flakes. No objects, no particles: a handful of hashes per pixel, and nothing at all where it does not snow.
	float Snow( float3 ray, float sceneDist )
	{
		if ( g_flFogSnow <= 0.001 )
			return 0.0;
		// the lattice moves up and upwind = flakes fall downwind; 1.1 m/s down (44 units), the wind only a slow slant
		float3 drift = float3( -g_vFogSnowWind.xy, 44.0 ) * g_flTime;
		float cover = 0.0;
		float shell = 120.0;
		for ( int k = 0; k < 6; k++ )
		{
			if ( shell > sceneDist )
				break;
			float cell = shell * 0.055;
			float3 q = ( g_vCameraPositionWs + ray * shell + drift * ( 1.0 + 0.07 * k ) ) / cell;
			float3 id = floor( q );
			float3 h = SnowHash( id + 19.0 * k );
			if ( h.z < g_flFogSnow * 0.75 )
			{
				float3 toFlake = ( 0.2 + 0.6 * SnowHash( id.zxy + 7.0 ) - ( q - id ) ) * cell;
				toFlake -= ray * dot( toFlake, ray );
				float radius = shell * ( 0.0024 + 0.0026 * h.x );
				cover += smoothstep( radius, radius * 0.35, length( toFlake ) ) * ( 0.95 - 0.1 * k );
			}
			shell *= 2.0;
		}
		return saturate( cover );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 ss = i.vPositionSs.xy;
		float3 scene = g_tColorBuffer.SampleLevel( g_sBilinearClamp, i.uv, 0 ).rgb;
		float strength = g_vFogShape.z;
		if ( strength <= 0.0 )
			return float4( scene, 1.0 );

		float3 ray = ViewRay( ss );
		float3 colour = Fogged( ss, scene, ray, strength );
		// Shafts light what stands in the air, never the open sky itself: on sky pixels they were a white sheet over half
		// the frame whenever the camera faced the sun in open country (Mark 2026-11-23: "a blanket of fog").
		// ... and only what has air in front of it: the light is scattered by the air between the eye and the thing, so
		// nothing within 2 m takes any and it builds to full by 35 m. Unscaled, the shafts drew straight over the player's
		// own body in front of the camera (Mark 2026-11-23: "the ambient lighting appears through the player").
		float viewDist = Depth::GetLinear( ss ) / max( dot( ray, g_vCameraDirWs ), 0.05 );
		if ( viewDist < g_vFogShape.w * 0.6 )
			colour += g_vFogSunGlow.rgb * ( SunShafts( ss, ray ) * strength * smoothstep( 80.0, 1400.0, viewDist ) );
		// Snow last, over everything: lit by the sky (dark at night, never glowing)
		float snow = Snow( ray, viewDist );
		float snowLight = dot( max( g_vFogHorizon, g_vFogZenith ), float3( 0.3, 0.5, 0.2 ) ) * 1.6 + 0.04;
		colour = lerp( colour, float3( 0.96, 0.98, 1.0 ) * snowLight, snow * strength );
		return float4( colour, 1.0 );
	}
}
