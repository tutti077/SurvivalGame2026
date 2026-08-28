//
// tempwater — proof-of-concept water surface for SurvivalGame2026.
// Depth-tinted refraction: samples the frame buffer behind the surface and fades it
// toward a deep color by the distance light travels through the water, so the water
// body actually reads as water instead of clear glass. Procedural ripple normals,
// no texture inputs. Structure follows base glass.shader (the known-good FB-copy path).
//
HEADER
{
	Description = "Temp depth-tinted water";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
}

COMMON
{
	#define S_SPECULAR 1
	#define BLEND_MODE_ALREADY_SET 1
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	#if ( PROGRAM == VFX_PROGRAM_PS )
		bool bIsFrontface : SV_IsFrontFace;
	#endif
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput i )
	{
		PixelInput o = ProcessVertex( i );
		return FinalizeVertex( o );
	}
}

PS
{
	#define DEPTH_STATE_ALREADY_SET 1
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1

	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"
	#include "common/classes/Depth.hlsl"

	// Visible from below as well (removes the need for a second flipped plane)
	RenderState( CullMode, NONE );

	BoolAttribute( bWantsFBCopyTexture, true );
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	float3 ShallowColor < UiType( Color ); Default3( 0.10, 0.42, 0.45 ); UiGroup( "Water,10/10" ); >;
	float3 DeepColor < UiType( Color ); Default3( 0.01, 0.09, 0.16 ); UiGroup( "Water,10/20" ); >;
	// Engine units: 40 u/m, so 160 = light is mostly gone after ~4 m of water
	float MurkDistance < Default( 160.0 ); Range( 8.0, 2000.0 ); UiGroup( "Water,10/30" ); >;
	float WaterRoughness < Default( 0.05 ); Range( 0.01, 1.0 ); UiGroup( "Water,10/40" ); >;
	float RefractionStrength < Default( 0.35 ); Range( 0.0, 1.0 ); UiGroup( "Water,10/50" ); >;

	float RippleScale < Default( 24.0 ); Range( 1.0, 256.0 ); UiGroup( "Ripples,20/10" ); >;
	float RippleSpeed < Default( 1.0 ); Range( 0.0, 8.0 ); UiGroup( "Ripples,20/20" ); >;
	float RippleStrength < Default( 0.08 ); Range( 0.0, 1.0 ); UiGroup( "Ripples,20/30" ); >;

	float3 GetRippleNormal( float3 worldPos, float3 baseNormal )
	{
		float t = g_flTime * RippleSpeed;
		float2 p = worldPos.xy / RippleScale;

		// Two directions, two frequencies so the ripples don't read as a grid
		float nx = sin( p.x * 2.1 + t * 1.7 ) + 0.5 * sin( p.x * 5.3 - p.y * 1.3 + t * 2.9 );
		float ny = cos( p.y * 1.9 - t * 1.3 ) + 0.5 * cos( p.y * 4.7 + p.x * 1.7 + t * 2.3 );

		return normalize( baseNormal + float3( nx, ny, 0 ) * RippleStrength );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vCameraPositionWs;

		float3 baseNormal = normalize( i.vNormalWs );
		if ( !i.bIsFrontface )
			baseNormal = -baseNormal;

		float3 normal = GetRippleNormal( worldPos, baseNormal );

		bool bOrtho = g_matViewToProjection[3].w != 0;
		float3 vViewRayWs = bOrtho ? g_vCameraDirWs : normalize( i.vPositionWithOffsetWs.xyz );

		// Scene position behind the surface, from the opaque depth buffer
		float flDepthPs = 1.0f - Depth::GetNormalized( i.vPositionSs.xy );
		float3 vSceneWs = RecoverWorldPosFromProjectedDepthAndRay( flDepthPs, vViewRayWs );
		float flWaterDist = distance( i.vPositionWithOffsetWs.xyz, vSceneWs );

		// Ripple-distorted screen UV; distortion eases out in shallow water so
		// shorelines and objects poking through the surface don't smear
		float2 uv = i.vPositionSs.xy * g_vInvViewportSize;
		float2 uvR = uv + normal.xy * RefractionStrength * 0.05 * saturate( flWaterDist / 32.0 );

		// Reject distorted samples that land on geometry in front of the water
		float flDepthR = 1.0f - Depth::GetNormalized( uvR / g_vInvViewportSize );
		float3 vSceneR = RecoverWorldPosFromProjectedDepthAndRay( flDepthR, vViewRayWs );
		if ( length( vSceneR ) < length( i.vPositionWithOffsetWs.xyz ) )
		{
			uvR = uv;
		}
		else
		{
			flWaterDist = distance( i.vPositionWithOffsetWs.xyz, vSceneR );
		}

		float3 sceneColor = g_tFrameBufferCopyTexture.SampleLevel( g_sTrilinearMirror, uvR * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb;

		// Beer-Lambert-ish absorption: what's behind fades into the water color with distance
		float murk = 1.0f - exp( -flWaterDist / MurkDistance );
		float3 waterTint = lerp( ShallowColor, DeepColor, murk );
		float3 refracted = lerp( sceneColor, waterTint, murk );

		// Fresnel energy split: transmitted light in, reflection handled by standard shading
		float flNDotV = saturate( dot( normal, -vViewRayWs ) );
		float3 vEnvBRDF = CalcBRDFReflectionFactor( flNDotV, WaterRoughness, 0.02 );

		Material m = Material::Init( i );
		m.Normal = normal;
		m.Albedo = 0;
		m.Metalness = 0;
		m.Roughness = WaterRoughness;
		m.AmbientOcclusion = 1;
		m.Opacity = 1;
		m.Emission = lerp( refracted, 0, vEnvBRDF );

		float4 output = ShadingModelStandard::Shade( i, m );
		output.rgb = Fog::Apply( worldPos, i.vPositionSs.xy, output.rgb );
		output.a = 1.0f;

		return output;
	}
}
