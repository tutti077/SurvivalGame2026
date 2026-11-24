HEADER
{
	Description = "Unlit sun/moon spheres with limb shading — opaque, ignores grey mesh vertex colors.";
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
		ExtraShaderData_t extra = GetExtraPerInstanceShaderData( i.nInstanceTransformID );
		o.vVertexColor.rgb = extra.vTint.rgb;
		o.vVertexColor.a = 1.0;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	// Limb shading (Mark 2026-11-23: "redesign the sun and moon so they look more realistic"): a real sun or full moon is
	// not a flat disc - it falls off toward its edge (the sun also reddens there). The body is a sphere, so the falloff is
	// the angle between its surface and the eye: colour x lerp( LimbTint, 1, (N.V) ^ LimbPower ). LimbPower 0 = flat.
	float g_flLimbPower < Default( 0.0 ); Range( 0.0, 4.0 ); UiGroup( "Celestial,10/10" ); >;
	float3 g_vLimbTint < UiType( Color ); Default3( 1.0, 1.0, 1.0 ); UiGroup( "Celestial,10/20" ); >;
	// HDR brightness. The renderer's tint arrives clamped to 1, and plain white through the filmic tonemap is a dull tan:
	// the sun was never brighter than a cloud. This is what makes it a light source on screen (and feeds the bloom).
	float g_flBrightness < Default( 1.0 ); Range( 0.0, 64.0 ); UiGroup( "Celestial,10/30" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 tex = g_tColor.Sample( TextureFiltering, i.vTextureCoords.xy );
		float3 color = tex.rgb * g_flTintColor.rgb * i.vVertexColor.rgb;
		if ( g_flLimbPower > 0.0 )
		{
			// vPositionWithOffsetWs is camera-relative: the eye is at the origin
			float facing = saturate( abs( dot( normalize( i.vNormalWs ), normalize( -i.vPositionWithOffsetWs.xyz ) ) ) );
			color *= lerp( g_vLimbTint, float3( 1.0, 1.0, 1.0 ), pow( facing, g_flLimbPower ) );
		}
		return float4( color * g_flBrightness, 1.0 );
	}
}
