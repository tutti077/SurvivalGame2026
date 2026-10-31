//
// sea_foam_memory — the sea's foam and wet-sand memory (TerrainWorldManager.SeaFoamMemory). Blitted into a small
// world-space render target under the camera right before the sea draws: each texel is one spot on the ground / water.
// Reads the previous frame's texel (through the previous rect — the camera's move shifts the rect by whole texels),
// decays it, and writes on top what the sea is doing there this instant (sea_waves.hlsl, the same sum the surface
// displaces by): crests breaking over the breaking height, the laps' fronts and trails, water standing on the sand.
//   R = foam 0..1 (fades over SeaFoamStep.y seconds)   G = wet 0..1 (dries over SeaFoamStep.z seconds)
// seawater.shader adds R to its foam; pixel_lit's terrain splat reads G as wet sand (gated to the shore band by height).
//
HEADER
{
	DevShader = true;
	Description = "Sea foam + wet sand memory (world-space ping-pong under the camera)";
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
	#include "sea_waves.hlsl"
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
	RenderState( DepthWriteEnable, false );
	RenderState( DepthEnable, false );
	RenderState( CullMode, NONE );

	Texture2D g_tSeaFoamPrev < Attribute( "SeaFoamPrev" ); SrgbRead( false ); >;
	SamplerState g_sFoamPrevSampler < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	// min x, min y, size (engine units), valid
	float4 g_vSeaFoamPrevRect < Attribute( "SeaFoamPrevRect" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vSeaFoamWriteRect < Attribute( "SeaFoamWriteRect" ); Default4( 0.0, 0.0, 1.0, 1.0 ); >;
	// x = frame dt (s), y = foam life (s), z = wet life (s)
	float4 g_vSeaFoamStep < Attribute( "SeaFoamStep" ); Default4( 0.016, 5.0, 45.0, 0.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// This texel's spot on the ground (engine units → metres), texel centres
		float2 worldXy = g_vSeaFoamWriteRect.xy + i.uv * g_vSeaFoamWriteRect.z;
		float2 p = worldXy / SEA_UNITS_PER_METER;

		// Last frame's memory at the same spot, decayed
		float2 prev = 0;
		if ( g_vSeaFoamPrevRect.w > 0.5 )
		{
			float2 puv = (worldXy - g_vSeaFoamPrevRect.xy) / g_vSeaFoamPrevRect.z;
			if ( all( puv >= 0.0 ) && all( puv <= 1.0 ) )
				prev = g_tSeaFoamPrev.SampleLevel( g_sFoamPrevSampler, puv, 0 ).rg;
		}
		float dt = g_vSeaFoamStep.x;
		prev.x *= exp( -dt / max( g_vSeaFoamStep.y, 0.1 ) );
		prev.y *= exp( -dt / max( g_vSeaFoamStep.z, 0.1 ) );

		// What the sea is doing here now
		float openness;
		float depth = SeaDepthMeters( p, openness );
		float energy = SeaShoreEnergy( p );
		float x = SeaShoreX( p );
		float reach = g_vSeaShore.w * clamp( energy, 0.3, 1.5 ) + 2.0;
		float foamNow = 0.0;
		float wetNow = 0.0;
		float t = g_vSeaClock.x;
		if ( depth > 0.0 )
		{
			float3 n;
			float folding, breaking;
			SeaWaveSum( p, depth, openness, 0.6, n, folding, breaking );
			// Breaking crests: white where the set stands over the breaking height and the surface is near a crest
			// (the crest top only, and only once the set stands well over the limit — a wide threshold painted the
			// whole surf zone solid white, 2026-10-26)
			float breakFoam = smoothstep( 0.15, 0.8, breaking ) * smoothstep( 0.3, 0.6, folding ) * 0.7;
			// Whitecaps out at sea where crests fold (the surface draws them too; the memory lets them linger as streaks)
			float caps = smoothstep( 0.55, 0.85, folding ) * smoothstep( 0.25, 0.7, openness ) * 0.35;
			float lapFoam = 0.0;
			if ( depth < 6.0 )
			{
				float2 laps = SeaShoreLaps( p, x, energy, t );
				lapFoam = laps.y * 0.5;
			}
			float mouth = RiverMouthFoamKeep( p );
			foamNow = saturate( max( max( breakFoam, caps ), lapFoam ) * saturate( energy ) ) * mouth;
			wetNow = 1.0;
		}
		else if ( x > -reach )
		{
			// The sand: wet wherever a lap's sheet stands right now (the terrain hides the sheet where the ground is higher,
			// and pixel_lit gates the wet by height above sea level for the same reason)
			float2 laps = SeaShoreLaps( p, x, energy, t );
			wetNow = smoothstep( 0.005, 0.03, laps.x );
			foamNow = laps.y * 0.5 * wetNow;
		}
		float foam = max( prev.x, foamNow );
		float wet = max( prev.y, wetNow );
		return float4( foam, wet, 0.0, 1.0 );
	}
}
// (rebuilt with sea_waves.hlsl — the include is not tracked as a dependency)
