//
// thermal_view — Thermal Eye augment, world pass.
// Runs after the opaque pass: the whole frame is remapped by luminance onto a cold blue
// palette (deep navy → blue → pale cyan) with a little sensor grain. Creatures are NOT touched
// here — their renderers wear thermal_body.shader, which is translucent and therefore draws
// after this pass, so the heat colours stay untinted.
//
HEADER
{
	DevShader = true;
	Description = "Thermal Eye — cold blue world";
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

	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

	// 0 = untouched frame, 1 = full thermal view (lets the effect fade in / out).
	float g_flThermalStrength < Attribute( "ThermalStrength" ); Default( 1.0f ); >;

	float3 ColdPalette( float t )
	{
		const float3 deep = float3( 0.01, 0.02, 0.10 );
		const float3 mid = float3( 0.08, 0.28, 0.75 );
		const float3 pale = float3( 0.55, 0.85, 1.00 );

		return t < 0.6
			? lerp( deep, mid, t / 0.6 )
			: lerp( mid, pale, ( t - 0.6 ) / 0.4 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = CalculateViewportUv( i.vPositionSs.xy );
		float3 scene = g_tColorBuffer.SampleLevel( g_sTrilinearClamp, uv, 0 ).rgb;

		float lum = dot( scene, float3( 0.299, 0.587, 0.114 ) );
		float t = pow( saturate( lum * 1.15 ), 0.8 );
		float3 cold = ColdPalette( t );

		// Sensor grain: cheap hash on the pixel position, ±3%.
		float grain = frac( sin( dot( uv * g_vRenderTargetSize.xy, float2( 12.9898, 78.233 ) ) ) * 43758.5453 );
		cold += ( grain - 0.5 ) * 0.06;

		return float4( lerp( scene, saturate( cold ), saturate( g_flThermalStrength ) ), 1.0 );
	}
}
