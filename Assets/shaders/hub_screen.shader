// The bar's big screen (Code/Hub/HubArenaScreen.cs): shows the render target of the coliseum camera, set per object
// on the renderer's scene object attributes as "HubScreen". Unlit (a TV glows, it is not lit by the room), point
// sampled so the low-res feed keeps the pixel look. Uses the mesh UVs: the dev box maps 0..1 across each face.
HEADER
{
	Description = "Hub big screen: unlit render-target feed";
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
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/pixel.hlsl"

	Texture2D g_tHubScreen < Attribute( "HubScreen" ); SrgbRead( false ); >;
	SamplerState g_sScreenSampler < Filter( POINT ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	// Brightness of the feed (the room is dim; the screen should read as a light source).
	float HubScreenBrightness < Default( 1.4 ); Range( 0.0, 8.0 ); UiGroup( "Material,10/10" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( normalize( i.vNormalWs ), 1.0, 1.0 );

		float3 c = g_tHubScreen.Sample( g_sScreenSampler, i.vTextureCoords.xy ).rgb;
		return float4( c * HubScreenBrightness, 1.0 );
	}
}
