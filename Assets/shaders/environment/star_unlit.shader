HEADER
{
	Description = "Unlit stars/celestials — texture × tint, alpha from renderer Tint.a for night fade.";
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
		// Ignore grey mesh verts — use ModelRenderer.Tint (rgb + opacity).
		ExtraShaderData_t extra = GetExtraPerInstanceShaderData( i.nInstanceTransformID );
		o.vVertexColor = extra.vTint;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float alpha = saturate( i.vVertexColor.a );
		clip( alpha - 0.008 );

		float4 tex = g_tColor.Sample( TextureFiltering, i.vTextureCoords.xy );
		float3 color = tex.rgb * g_flTintColor.rgb * i.vVertexColor.rgb;
		return float4( color, alpha );
	}
}
