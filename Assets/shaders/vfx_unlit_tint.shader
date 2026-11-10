HEADER
{
	Description = "Unlit VFX for ModelRenderers: mesh vertex colour x renderer Tint (rgb + opacity), alpha blended, no depth write. Circuit node spheres at full brightness whatever the scene light.";
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
		o.vVertexColor = extra.vTint;
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( DepthWriteEnable, false );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float alpha = saturate( i.vVertexColor.a );
		clip( alpha - 0.004 );
		return float4( i.vVertexColor.rgb, alpha );
	}
}
