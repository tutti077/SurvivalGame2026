// Hub fish-tank window (Tools/hub/generate_hub.py): a plain alpha-blended pane — a faint tint, a little more opaque at
// grazing angles (Fresnel) so the glass reads as glass, no refraction. The engine glass shader needs a framebuffer copy
// that offscreen captures (the editor's camera screenshots) do not have, so this one is drawn the same way everywhere.
HEADER
{
	Description = "Hub window glass: alpha-blended tint with a Fresnel edge";
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

	float3 HubGlassTint < UiType( Color ); Default3( 0.7, 0.9, 1.0 ); UiGroup( "Material,10/10" ); >;
	float HubGlassOpacity < Default( 0.08 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/20" ); >;
	float HubGlassEdgeOpacity < Default( 0.45 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/30" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 vPositionWs = i.vPositionWithOffsetWs + g_vCameraPositionWs;
		float3 V = normalize( g_vCameraPositionWs - vPositionWs );
		float flFacing = abs( dot( normalize( i.vNormalWs ), V ) );
		float flAlpha = lerp( HubGlassEdgeOpacity, HubGlassOpacity, flFacing );
		return float4( HubGlassTint, flAlpha );
	}
}
