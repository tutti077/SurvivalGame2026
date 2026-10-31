//
// underwater — the whole view while the camera is below a water surface (Mark 2026-10-22: "a blue hue … the same hue
// as the water itself … bluer and bluer the deeper you go, like in real life"). Blitted by TerrainWorldManager.Underwater
// before post-processing:
//   - light reaching a point: sunlight loses red first, then green (Beer-Lambert on the water's absorption) over the
//     water above THAT point, so the bed gets bluer and dimmer the deeper it lies, and so does everything round a deep
//     camera;
//   - caustics: the same net the surface shaders draw on the bed from above, on upward-facing ground near the camera;
//   - the surface from below: the water mesh is not drawn from underneath, so the pass draws it — Snell's window (the
//     sky inside ~48° of straight up, wobbling with the ripples), total internal reflection of the water outside it;
//   - looking through the water: everything fades toward the water's scattered colour with the distance the view
//     travels through it — brighter looking up, darker looking down — so the distance is a blue wall, never black.
//
HEADER
{
	DevShader = true;
	Description = "Underwater view: depth-dependent blue light, caustics, the surface from below and distance fog";
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
	Texture2D g_tUnderwaterCaustics < Attribute( "UnderwaterCaustics" ); SrgbRead( false ); >;
	SamplerState g_sUnderwaterWrap < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	// x = metres of water above the camera, y = fade-in (0..1 at the surface), z = clear distance (m), w = daylight 0..1
	float4 g_vUnderwater < Attribute( "Underwater" ); Default4( 0.0, 0.0, 10.0, 1.0 ); >;
	// The water's absorption colour (what light keeps after Clear Distance), as the water surface shaders use
	float3 g_vUnderwaterTint < Attribute( "UnderwaterTint" ); Default3( 0.35, 0.55, 0.62 ); >;
	// How far daylight reaches down before it keeps only the absorption colour (m) — far more than the distance one
	// can see through the water: light from above is absorbed, a view sideways is also scattered away
	float g_flUnderwaterLightClear < Attribute( "UnderwaterLightClear" ); Default( 25.0 ); >;
	// x = surface height (engine units), y = time (s), z = caustic net size (m), w = caustic strength
	float4 g_vUnderwaterSurface < Attribute( "UnderwaterSurface" ); Default4( 0.0, 0.0, 4.5, 0.8 ); >;

	static const float UNITS_PER_METER = 40.0;

	// World-space view ray through a screen pixel (g_matProjectionToWorld is camera-relative)
	float3 ViewRay( float2 screenPos )
	{
		float3 ndc = float3( screenPos * g_vInvViewportSize, 0.5 );
		ndc.y = 1.0 - ndc.y;
		ndc.xy = 2.0 * ndc.xy - 1.0;
		float4 p = mul( g_matProjectionToWorld, float4( ndc, 1.0 ) );
		return normalize( p.xyz / p.w );
	}

	// Surface slope from a handful of crossing ripple trains (smooth everywhere; hashed noise blocks up at world-scale
	// coordinates on the GPU)
	float2 RippleSlope( float2 m, float t )
	{
		float2 n = 0.0;
		n += float2( 0.80, 0.60 ) * cos( dot( m, float2( 0.80, 0.60 ) * 1.3 ) + t * 1.1 ) * 0.50;
		n += float2( -0.45, 0.89 ) * cos( dot( m, float2( -0.45, 0.89 ) * 2.1 ) + t * 1.5 + 1.7 ) * 0.35;
		n += float2( 0.97, -0.24 ) * cos( dot( m, float2( 0.97, -0.24 ) * 3.4 ) + t * 1.9 + 4.1 ) * 0.25;
		n += float2( -0.70, -0.71 ) * cos( dot( m, float2( -0.70, -0.71 ) * 5.3 ) + t * 2.4 + 2.3 ) * 0.18;
		n += float2( 0.28, -0.96 ) * cos( dot( m, float2( 0.28, -0.96 ) * 7.9 ) + t * 2.9 + 5.2 ) * 0.12;
		return n;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 ss = i.vPositionSs.xy;
		float3 scene = g_tColorBuffer.SampleLevel( g_sBilinearClamp, i.uv, 0 ).rgb;
		float camDepthM = g_vUnderwater.x;
		float amount = g_vUnderwater.y;
		float clearM = max( g_vUnderwater.z, 0.5 );
		float daylight = g_vUnderwater.w;
		float3 tint = max( g_vUnderwaterTint, 0.001 );
		float surfaceZ = g_vUnderwaterSurface.x;
		float t = g_vUnderwaterSurface.y;

		float3 ray = ViewRay( ss );
		float3 cam = g_vCameraPositionWs;

		// Distance to what the frame shows here (sky: far away)
		float linear = Depth::GetLinear( ss );
		bool sky = linear > 50000.0;   // past 1250 m the water hides everything anyway
		float sceneDist = sky ? 1e9 : linear / max( dot( ray, g_vCameraDirWs ), 0.05 );

		// The water's own colour at the camera: its scattered light, red absorbed first — teal near the surface, deep
		// blue below. Brighter looking up (toward the light), darker looking down.
		float lightClearM = max( g_flUnderwaterLightClear, clearM );
		float3 lightHere = pow( tint, (camDepthM + 1.0) / lightClearM );
		float3 scatter = tint * tint;
		scatter /= max( max( scatter.r, scatter.g ), max( scatter.b, 1e-3 ) );
		float upness = saturate( ray.z * 0.5 + 0.5 );
		float3 waterColor = scatter * lightHere * (0.30 * daylight) * lerp( 0.45, 1.5, upness * upness );

		// The surface from below, when the view reaches it before the scene
		float surfDist = ray.z > 0.001 ? (surfaceZ - cam.z) / ray.z : 1e9;
		float3 color;
		float pathM;
		if ( surfDist > 0.0 && surfDist < sceneDist )
		{
			float2 hitM = (cam.xy + ray.xy * surfDist) / UNITS_PER_METER;
			// Ripples tilt the surface: the window's edge wobbles and the sky through it swims
			float2 n = RippleSlope( hitM, t );
			float cosUp = saturate( ray.z + dot( n, ray.xy ) * 0.06 );
			// Snell's window: inside acos(1/1.33) ≈ 48.6° the sky comes through (Fresnel climbs toward the edge); outside,
			// total internal reflection mirrors the water below
			float window = smoothstep( 0.63, 0.72, cosUp );
			float2 swim = n * 0.01;
			float3 through = g_tColorBuffer.SampleLevel( g_sBilinearClamp, i.uv + swim, 0 ).rgb;
			float3 mirrored = scatter * lightHere * (0.30 * daylight) * (0.55 + 0.12 * n.x);
			float rim = smoothstep( 0.6, 0.66, cosUp ) * (1.0 - smoothstep( 0.66, 0.76, cosUp ));
			// The underside of a rippled surface is a bright, moving web, not glass: the caustic net at surface scale
			float2 netUv = hitM / 6.0;
			float netA = g_tUnderwaterCaustics.SampleLevel( g_sUnderwaterWrap, netUv + t * float2( 0.02, 0.012 ), 2.0 ).r;
			float netB = g_tUnderwaterCaustics.SampleLevel( g_sUnderwaterWrap, netUv * 0.83 + float2( 0.41, 0.17 ) - t * float2( 0.016, -0.014 ), 2.0 ).r;
			float net = min( netA, netB ) * (1.0 - smoothstep( 8.0, 40.0, surfDist / UNITS_PER_METER ));
			color = lerp( mirrored, through * (0.9 + 0.25 * net), window ) + (rim + net * 0.1) * scatter * daylight * 0.25;
			pathM = surfDist / UNITS_PER_METER;
		}
		else
		{
			// A point under the water: lit by sunlight that came down through the water above it. Only DAYLIGHT is dimmed
			// by the depth — the frame cannot tell a torch's light from the sun's, and dimming all of it left torches,
			// lanterns and bulbs nearly black at depth (Mark 2026-10-25: "I wanna see the depths"). By night the light on
			// screen is the lamps', so the depth dims it hardly at all.
			float3 p = cam + ray * sceneDist;
			float pointDepthM = sky ? camDepthM : max( (surfaceZ - p.z) / UNITS_PER_METER, 0.0 );
			float3 lightAt = lerp( 1.0, pow( tint, (pointDepthM + 0.5) / lightClearM ), saturate( daylight ) );
			color = scene * lightAt;

			// Caustics on upward-facing ground near the camera
			float distM = sceneDist / UNITS_PER_METER;
			if ( !sky && distM < 30.0 && pointDepthM > 0.05 )
			{
				float3 nrm = normalize( cross( ddx( p ), ddy( p ) ) );
				float up = saturate( abs( nrm.z ) * 1.4 - 0.4 );
				float2 uv = (p.xy / UNITS_PER_METER) / max( g_vUnderwaterSurface.z, 0.2 );
				float a = g_tUnderwaterCaustics.SampleLevel( g_sUnderwaterWrap, uv + t * float2( 0.021, 0.013 ), 0 ).r;
				float b = g_tUnderwaterCaustics.SampleLevel( g_sUnderwaterWrap, uv * 1.17 + float2( 0.37, 0.11 ) - t * float2( 0.017, -0.019 ), 0 ).r;
				float fade = smoothstep( 0.05, 0.4, pointDepthM ) * (1.0 - smoothstep( 4.0, 14.0, pointDepthM )) * (1.0 - smoothstep( 12.0, 30.0, distM ));
				color += scene * lightAt * min( a, b ) * fade * up * g_vUnderwaterSurface.w * saturate( daylight ) * 2.0;
			}
			pathM = sky ? 4000.0 : sceneDist / UNITS_PER_METER;
		}

		// Through the water to the camera
		float3 keep = pow( tint, min( pathM, 4000.0 ) / clearM );
		float3 underwater = lerp( waterColor, color, keep );
		return float4( lerp( scene, underwater, amount ), 1.0 );
	}
}
