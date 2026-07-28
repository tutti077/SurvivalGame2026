HEADER
{
	Description = "Unlit sun/moon disks — opaque, ignores grey mesh vertex colors.";
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

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 tex = g_tColor.Sample( TextureFiltering, i.vTextureCoords.xy );
		float3 color = tex.rgb * g_flTintColor.rgb * i.vVertexColor.rgb;
		return float4( color, 1.0 );
	}
}
