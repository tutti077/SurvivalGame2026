//
// aerial_perspective — the air between the camera and everything it sees (Code/World/Environment/AerialPerspective.cs).
//
// Mark 2026-11-08: "things that are far away seem like they're far away … terrain and object colors become more white
// instead of being totally saturated … it shouldn't make stuff impossible to see, just hard to discern using colors".
// One full-screen pass before post-processing, from the scene depth, so every shader — ground, grass, trees, far cards,
// water, entities, the caldera ring — breathes the same air:
//   1. desaturation: colour slides toward its own luminance with distance (the hue goes first, the shapes stay);
//   2. haze: then toward the sky's horizon colour (warmed toward the sun glow on the sun's side), capped at Max Haze so
//      the far world is pale, never gone.
// Both run through an exponential atmosphere: optical depth = distance × air density averaged between the camera's
// altitude and the point's (sea level = 0, 1/e per Scale Height), so valleys drown first and high ground stays crisper.
// The sky itself (depth past the dome) is left alone — the dome IS the far colour.
//
//   ColorBuffer       the frame so far (GrabFrameTexture)
//   AerialHaze        x = 1 / haze distance, y = 1 / scale height (both 1/engine units), z = max haze, w = sky cut-off (engine units)
//   AerialDesat       x = 1 / desaturate distance (1/engine units), y = max desaturation, z = sun warmth, w = strength (0 = off)
//   AerialHorizon     haze colour (the preset horizon, from the cycle)
//   AerialSunGlow     rgb = sun glow colour, a = glow strength (the preset's), toward AerialSunDirection
//
HEADER
{
	DevShader = true;
	Description = "Aerial perspective: distance desaturation + haze toward the horizon through an exponential atmosphere";
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

	float4 g_vAerialHaze < Attribute( "AerialHaze" ); Default4( 0.00000125, 0.000025, 0.9, 3000000.0 ); >;
	float4 g_vAerialDesat < Attribute( "AerialDesat" ); Default4( 0.0000167, 0.85, 0.6, 1.0 ); >;
	float3 g_vAerialHorizon < Attribute( "AerialHorizon" ); Default3( 0.72, 0.76, 0.84 ); >;
	float4 g_vAerialSunGlow < Attribute( "AerialSunGlow" ); Default4( 1.0, 0.9, 0.7, 0.0 ); >;
	float3 g_vAerialSunDirection < Attribute( "AerialSunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;

	// World-space view ray through a screen pixel (g_matProjectionToWorld is camera-relative)
	float3 ViewRay( float2 screenPos )
	{
		float3 ndc = float3( screenPos * g_vInvViewportSize, 0.5 );
		ndc.y = 1.0 - ndc.y;
		ndc.xy = 2.0 * ndc.xy - 1.0;
		float4 p = mul( g_matProjectionToWorld, float4( ndc, 1.0 ) );
		return normalize( p.xyz / p.w );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 ss = i.vPositionSs.xy;
		float3 scene = g_tColorBuffer.SampleLevel( g_sBilinearClamp, i.uv, 0 ).rgb;
		float strength = g_vAerialDesat.w;
		if ( strength <= 0.0 )
			return float4( scene, 1.0 );

		// The sky (and anything drawn at the dome) keeps its colour: the dome already is the far colour.
		float viewDepth = Depth::GetLinear( ss );
		if ( viewDepth >= g_vAerialHaze.w )
			return float4( scene, 1.0 );

		float3 ray = ViewRay( ss );
		float dist = viewDepth / max( dot( ray, g_vCameraDirWs ), 0.05 );
		float3 hit = g_vCameraPositionWs + ray * dist;

		// Air density averaged along the ray through an exponential atmosphere (sea level = 0)
		float k = g_vAerialHaze.y;
		float h0 = max( g_vCameraPositionWs.z, 0.0 ) * k;
		float h1 = max( hit.z, 0.0 ) * k;
		float dh = h1 - h0;
		float density = abs( dh ) > 1e-3 ? ( exp( -h0 ) - exp( -h1 ) ) / dh : exp( -h0 );
		float optical = dist * density;

		// 1. the hue goes: toward the pixel's own luminance
		float desat = g_vAerialDesat.y * ( 1.0 - exp( -optical * g_vAerialDesat.x ) );
		float lum = dot( scene, float3( 0.2126, 0.7152, 0.0722 ) );
		float3 colour = lerp( scene, float3( lum, lum, lum ), desat );

		// 2. the air lights up: toward the horizon, warmer on the sun's side
		float haze = min( 1.0 - exp( -optical * g_vAerialHaze.x ), g_vAerialHaze.z );
		float sunSide = pow( saturate( dot( ray, normalize( g_vAerialSunDirection ) ) ), 6.0 ) * g_vAerialSunGlow.a;
		float3 hazeColour = g_vAerialHorizon + g_vAerialSunGlow.rgb * sunSide * g_vAerialDesat.z;
		colour = lerp( colour, hazeColour, haze );

		return float4( lerp( scene, colour, strength ), 1.0 );
	}
}
