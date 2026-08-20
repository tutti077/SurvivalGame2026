HEADER
{
	Description = "Unlit large black/grey/white world-space blobs for jungle gym scale reads.";
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
		return FinalizeVertex( o );
	}
}

PS
{
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	float g_flBlobSize < Default( 280.0 ); Range( 40.0, 1200.0 ); UiGroup( "Depth,10/10" ); >;

	float Hash13( float3 p )
	{
		return frac( sin( dot( p, float3( 127.1, 311.7, 74.7 ) ) ) * 43758.5453 );
	}

	float ValueNoise( float3 p )
	{
		float3 i = floor( p );
		float3 f = frac( p );
		f = f * f * ( 3.0 - 2.0 * f );

		float n000 = Hash13( i + float3( 0, 0, 0 ) );
		float n100 = Hash13( i + float3( 1, 0, 0 ) );
		float n010 = Hash13( i + float3( 0, 1, 0 ) );
		float n110 = Hash13( i + float3( 1, 1, 0 ) );
		float n001 = Hash13( i + float3( 0, 0, 1 ) );
		float n101 = Hash13( i + float3( 1, 0, 1 ) );
		float n011 = Hash13( i + float3( 0, 1, 1 ) );
		float n111 = Hash13( i + float3( 1, 1, 1 ) );

		float nx00 = lerp( n000, n100, f.x );
		float nx10 = lerp( n010, n110, f.x );
		float nx01 = lerp( n001, n101, f.x );
		float nx11 = lerp( n011, n111, f.x );
		float nxy0 = lerp( nx00, nx10, f.y );
		float nxy1 = lerp( nx01, nx11, f.y );
		return lerp( nxy0, nxy1, f.z );
	}

	float Blobs( float3 p )
	{
		float n = ValueNoise( p );
		n = lerp( n, ValueNoise( p * 2.03 + 17.3 ), 0.35 );
		return saturate( n );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 worldPos = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float size = max( 40.0, g_flBlobSize );
		float n = Blobs( worldPos / size );

		float black = 0.06;
		float grey = 0.42;
		float white = 0.92;

		float toGrey = smoothstep( 0.22, 0.42, n );
		float toWhite = smoothstep( 0.58, 0.78, n );
		float shade = lerp( black, grey, toGrey );
		shade = lerp( shade, white, toWhite );

		float3 col = shade.xxx;

		float3 normal = normalize( i.vNormalWs );
		float3 viewDir = normalize( -i.vPositionWithOffsetWs.xyz );
		float facing = saturate( abs( dot( normal, viewDir ) ) );
		col *= 0.88 + 0.12 * facing;

		return float4( col, 1.0 );
	}
}
