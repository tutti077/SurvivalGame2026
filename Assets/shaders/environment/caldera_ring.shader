//
// caldera_ring — the mountain wall round the whole map (Code/World/Environment/CalderaRing.cs).
//
// One world-anchored mesh 23–30 km from the centre, 5–6.5 km tall. Lit rock only: the distance haze that pales
// it is the scene-wide aerial_perspective pass (one atmosphere for ground, sea and ring, capped so the wall never
// fades away), which reads this mesh's depth like any other. No engine fog. Lit from the cycle's sun / moon lights
// by C# attributes; faceted from screen derivatives (no shadows at 25 km anyway).
//
//   RingSunDirection / RingSunColor     toward the sun, sun DirectionalLight.LightColor
//   RingMoonDirection / RingMoonColor   same for the moon
//   RingAmbient                         sun light's SkyColor (the scene's ambient fill)
//
HEADER
{
	Description = "Caldera ring — far mountain wall, lit by the day/night cycle (haze from the aerial perspective pass).";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
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
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		o.vVertexColor = i.vColor;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	float3 g_vRingSunDirection < Attribute( "RingSunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vRingSunColor < Attribute( "RingSunColor" ); Default3( 1.0, 0.95, 0.85 ); >;
	float3 g_vRingMoonDirection < Attribute( "RingMoonDirection" ); Default3( 0.0, 0.0, -1.0 ); >;
	float3 g_vRingMoonColor < Attribute( "RingMoonColor" ); Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vRingAmbient < Attribute( "RingAmbient" ); Default3( 0.18, 0.23, 0.33 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 rel = i.vPositionWithOffsetWs.xyz;          // camera → point, engine units
		float dist = max( length( rel ), 1.0 );
		float3 viewDir = rel / dist;

		// Faceted: one normal per triangle from the screen derivatives, turned toward the camera.
		float3 n = normalize( cross( ddy( rel ), ddx( rel ) ) );
		if ( dot( n, viewDir ) > 0.0 )
			n = -n;

		float3 albedo = i.vVertexColor.rgb;
		float3 sunDir = normalize( g_vRingSunDirection );
		float3 light = g_vRingAmbient * lerp( 0.55, 1.0, saturate( n.z * 0.5 + 0.5 ) );
		light += g_vRingSunColor * saturate( dot( n, sunDir ) );
		light += g_vRingMoonColor * saturate( dot( n, normalize( g_vRingMoonDirection ) ) );
		return float4( albedo * light, 1.0 );
	}
}
