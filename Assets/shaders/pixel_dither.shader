//
// pixel_dither — full-screen post-process for lighting tests (Code/Rendering/PixelDitherPostProcess.cs).
//
// Three stages, each with its own knob on the component:
//   1. Pixelate: the frame is read once per PixelSize x PixelSize screen block (block centre).
//   2. Quantize: brightness (or, with LuminanceOnly off, every channel) is snapped to ColorLevels steps,
//      in gamma space so the steps look even. Brightness-only keeps each surface's own colour and only
//      bands the light / shadow falling on it — the pixel-art lighting look.
//   3. Dither: an ordered threshold pattern (2x2 checker / 4x4 / 8x8 Bayer) nudges each block up or down
//      one step before the snap, so neighbouring blocks alternate between two shades and the eye blends
//      them into in-between tones the palette does not have. SplitPixels evaluates the pattern at
//      half-block width, so every pixel block is cut into a left and right half that can land on
//      different shades — more apparent shades per block without more palette entries.
//
// SkipSky leaves pixels at the far plane (the 2D sky) untouched, so only lit geometry is banded.
// Strength 0 returns the untouched frame; everything in between crossfades.
//
HEADER
{
	DevShader = true;
	Description = "Pixelate + brightness quantize + ordered dither";
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
	#include "common/classes/Depth.hlsl"

	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;

	// Screen pixels per output block (1 = native resolution).
	float g_flPixelSize < Attribute( "PixelSize" ); Default( 12.0f ); >;
	// Shades after quantization (2 = light / dark only, 256 = no visible snap).
	float g_flColorLevels < Attribute( "ColorLevels" ); Default( 5.0f ); >;
	// 0 = plain snap, 1 = full ordered dither between the two nearest shades.
	float g_flDitherStrength < Attribute( "DitherStrength" ); Default( 1.0f ); >;
	// 0 = 2x2 checker, 1 = 4x4 Bayer, 2 = 8x8 Bayer.
	float g_flDitherPattern < Attribute( "DitherPattern" ); Default( 0.0f ); >;
	// 1 = evaluate the pattern per half block (left / right halves), 0 = per block.
	float g_flSplitPixels < Attribute( "SplitPixels" ); Default( 1.0f ); >;
	// 1 = quantize brightness only (surface colour kept), 0 = quantize every channel.
	float g_flLuminanceOnly < Attribute( "LuminanceOnly" ); Default( 1.0f ); >;
	// 1 = leave far-plane pixels (the sky) untouched.
	float g_flSkipSky < Attribute( "SkipSky" ); Default( 1.0f ); >;
	// 0 = untouched frame, 1 = full effect.
	float g_flStrength < Attribute( "Strength" ); Default( 1.0f ); >;

	// Ordered-dither thresholds in [0,1): the classic Bayer matrices.
	static const float BAYER2[4] =
	{
		0.0 / 4.0, 2.0 / 4.0,
		3.0 / 4.0, 1.0 / 4.0
	};

	static const float BAYER4[16] =
	{
		 0.0 / 16.0,  8.0 / 16.0,  2.0 / 16.0, 10.0 / 16.0,
		12.0 / 16.0,  4.0 / 16.0, 14.0 / 16.0,  6.0 / 16.0,
		 3.0 / 16.0, 11.0 / 16.0,  1.0 / 16.0,  9.0 / 16.0,
		15.0 / 16.0,  7.0 / 16.0, 13.0 / 16.0,  5.0 / 16.0
	};

	static const float BAYER8[64] =
	{
		 0.0 / 64.0, 32.0 / 64.0,  8.0 / 64.0, 40.0 / 64.0,  2.0 / 64.0, 34.0 / 64.0, 10.0 / 64.0, 42.0 / 64.0,
		48.0 / 64.0, 16.0 / 64.0, 56.0 / 64.0, 24.0 / 64.0, 50.0 / 64.0, 18.0 / 64.0, 58.0 / 64.0, 26.0 / 64.0,
		12.0 / 64.0, 44.0 / 64.0,  4.0 / 64.0, 36.0 / 64.0, 14.0 / 64.0, 46.0 / 64.0,  6.0 / 64.0, 38.0 / 64.0,
		60.0 / 64.0, 28.0 / 64.0, 52.0 / 64.0, 20.0 / 64.0, 62.0 / 64.0, 30.0 / 64.0, 54.0 / 64.0, 22.0 / 64.0,
		 3.0 / 64.0, 35.0 / 64.0, 11.0 / 64.0, 43.0 / 64.0,  1.0 / 64.0, 33.0 / 64.0,  9.0 / 64.0, 41.0 / 64.0,
		51.0 / 64.0, 19.0 / 64.0, 59.0 / 64.0, 27.0 / 64.0, 49.0 / 64.0, 17.0 / 64.0, 57.0 / 64.0, 25.0 / 64.0,
		15.0 / 64.0, 47.0 / 64.0,  7.0 / 64.0, 39.0 / 64.0, 13.0 / 64.0, 45.0 / 64.0,  5.0 / 64.0, 37.0 / 64.0,
		63.0 / 64.0, 31.0 / 64.0, 55.0 / 64.0, 23.0 / 64.0, 61.0 / 64.0, 29.0 / 64.0, 53.0 / 64.0, 21.0 / 64.0
	};

	float DitherThreshold( int2 cell )
	{
		int pattern = (int)round( g_flDitherPattern );
		if ( pattern <= 0 )
		{
			int2 c = cell & 1;
			return BAYER2[c.y * 2 + c.x];
		}
		if ( pattern == 1 )
		{
			int2 c = cell & 3;
			return BAYER4[c.y * 4 + c.x];
		}
		int2 c = cell & 7;
		return BAYER8[c.y * 8 + c.x];
	}

	// Snap a gamma-space value to the palette with the dither offset applied first.
	float Quantize( float gammaValue, float steps, float offset )
	{
		return floor( gammaValue * steps + 0.5 + offset ) / steps;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 pixel = i.vPositionSs.xy;
		float2 uv = CalculateViewportUv( pixel );
		float3 original = g_tColorBuffer.SampleLevel( g_sTrilinearClamp, uv, 0 ).rgb;

		// --- 1. Pixelate: one colour per block, read at the block centre.
		float blockSize = max( 1.0, round( g_flPixelSize ) );
		float2 block = floor( pixel / blockSize );
		float2 blockCentre = ( block + 0.5 ) * blockSize;
		float2 blockUv = uv + ( blockCentre - pixel ) / g_vRenderTargetSize.xy;
		float3 scene = g_tColorBuffer.SampleLevel( g_sTrilinearClamp, blockUv, 0 ).rgb;

		// Sky mask: nothing was drawn at the far plane, so the whole block keeps the original frame.
		if ( g_flSkipSky > 0.5 )
		{
			float depth = Depth::GetNormalized( blockCentre );
			if ( depth <= 0.00001 || depth >= 0.99999 )
				return float4( original, 1.0 );
		}

		// --- 2 + 3. Quantize with an ordered dither offset, in gamma space.
		float levels = max( 2.0, round( g_flColorLevels ) );
		float steps = levels - 1.0;

		// Dither cell: one per block, or one per half block (left / right) when SplitPixels is on.
		// The x index then runs twice as fast as the y index, so the pattern still tiles by block row.
		bool split = g_flSplitPixels > 0.5;
		float cellWidth = split ? blockSize * 0.5 : blockSize;
		int2 cell = int2( (int)floor( pixel.x / cellWidth ), (int)block.y );

		float threshold = DitherThreshold( cell );
		float offset = ( threshold - 0.5 ) * saturate( g_flDitherStrength );

		float3 result;
		if ( g_flLuminanceOnly > 0.5 )
		{
			// Band the light falling on the surface, keep its colour: scale rgb by quantized / actual brightness.
			float lum = max( GetLuminance( scene ), 0.0001 );
			float gammaLum = pow( saturate( lum ), 1.0 / 2.2 );
			float quantLum = pow( saturate( Quantize( gammaLum, steps, offset ) ), 2.2 );
			result = saturate( scene * ( quantLum / lum ) );
		}
		else
		{
			float3 gamma = pow( saturate( scene ), 1.0 / 2.2 );
			float3 quantized = float3(
				Quantize( gamma.r, steps, offset ),
				Quantize( gamma.g, steps, offset ),
				Quantize( gamma.b, steps, offset ) );
			result = pow( saturate( quantized ), 2.2 );
		}

		return float4( lerp( original, result, saturate( g_flStrength ) ), 1.0 );
	}
}
