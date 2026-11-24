//
// sea_waves.hlsl — the sea's depth, swell and shore, shared by seawater.shader (vertex + pixel stages) and the foam
// memory pass (sea_foam_memory.shader). Included at COMMON level after common/shared.hlsl. Self-contained: the compiler
// builds an include as its own module, so everything it needs is declared here. SeaWaves.cs is the CPU mirror — keep
// every constant in step.
//
//   depth  : recomputed exactly as the terrain carve does it (TerrainPreviewPipeline LakeFloorMeters / SampleRimOcean):
//            the lake mask raster + traced shoreline for lakes, the shelf past the land edge for the ocean.
//   swell  : 14 swells (Gerstner sideways motion, loop-free). Phase = k × (dir · warped p + detour) − ωt, where detour is the refraction travel
//            distance from SeaSwellTravelField (one continuous field marched over the sea floor): crests turn toward the
//            beach by themselves and stay one unbroken line (2026-10-26 — the old lerp of two phases drew bands across
//            the shelf). Each swell shoals on its own; the set is capped at the breaking height 0.39 × depth and the
//            excess is the breaking amount. As the set nears that height every swell leans forward (a second and third
//            harmonic: gentle back, steep front — a bore), and the white water sits on those front faces (the roller).
//   chop   : 10 short waves from the live wind; which wavelengths carry it follows the weather (SeaWaves.Spectrum).
//   shore  : the swash. On the ocean the five swells round the peak are read at the waterline as ONE wave (their summed
//            phase and height): each of its crests sends a lap in over the last metres, up the sand and back, as tall
//            as that crest — sets and lulls come from the swell itself. Everywhere, the chop does the same in small.
//
#define SEA_UNITS_PER_METER 40.0
#define WATER_STENCIL_LIFT_UNITS 40.0
#define SEA_WAVES 24
#define SEA_SWELLS 14
#define SEA_CHOPS 10
// Sum of the swells' crest sharpness (Gerstner Q·k·a) at most: under 1 the surface never loops over itself
// (SeaWaves.CrestBudget)
#define SEA_CREST_BUDGET 0.8
// How far a swell at the breaking height leans forward: 0 = a sine, 1 = a sawtooth (SeaWaves.Lean)
#define SEA_LEAN 0.9
// A crest may stand this fraction of the depth before it breaks (SeaWaves.BreakingRatio)
#define SEA_BREAKING_RATIO 0.39
#include "river_mouths.hlsl"

// ---- published by TerrainWorldManager.SeaWater (one source for the shader and SeaWaves.cs)
// x = world radius (m), y = land radius (m), z = ocean max depth (m), w = ocean shelf width (m)
float4 g_vSeaShape < Attribute( "SeaShape" ); Default4( 20000.0, 20000.0, 300.0, 900.0 ); >;
// x = ocean shelf depth (m), y = ocean drop-off width (m), z = lake shelf depth (m), w = lake drop-off width (m)
float4 g_vSeaShelf < Attribute( "SeaShelf" ); Default4( 30.0, 300.0, 15.0, 300.0 ); >;
// x = lake shore threshold (mask), y = lake max depth (m), z = lake shelf width (m), w = metres of run per metre the deep bed falls
float4 g_vSeaLake < Attribute( "SeaLake" ); Default4( 2.0, 50.0, 200.0, 1.5 ); >;
// x = swell amplitude (m), y = chop amplitude (m), z = lake chop share, w = shore lap (m)
float4 g_vSeaWaves < Attribute( "SeaWaves" ); Default4( 1.6, 0.35, 0.35, 0.12 ); >;
// x = clock (s), y = far-tier sink under the near chunks (m, pixel_lit F_FAR_SINK), w = 1 once published
float4 g_vSeaClock < Attribute( "SeaClock" ); Default4( 0.0, 2.5, 0.0, 0.0 ); >;
// x = shore foam width (m), z = shore surge (m), w = run-up reach (m) — TerrainWorldManager "Sea" knobs
float4 g_vSeaShore < Attribute( "SeaShore" ); Default4( 4.0, 0.0, 0.45, 15.0 ); >;
Texture2D g_tSeaLakeMask < Attribute( "SeaLakeMask" ); SrgbRead( false ); >;
// Signed metres to the traced lake shoreline (TerrainPreviewLakeShoreDistance: + dry, - in a lake), 16 m nodes from
// (-world radius, -world radius), x NOT mirrored. R32F: bilinear by hand like the mask.
Texture2D g_tSeaLakeShore < Attribute( "SeaLakeShore" ); SrgbRead( false ); >;
// 1 over ocean (water joined to the rim, and everything past the land disc), 0 over water enclosed by land; the lake
// mask's raster layout (TerrainPreviewLandDiskFields.SampleOceanness01)
Texture2D g_tSeaOceanness < Attribute( "SeaOceanness" ); SrgbRead( false ); >;
// Metres the surface is lifted above sea level per lake-mask cell: a tarn on a mountain top and the ring around it
// (TerrainPreviewMountainLakes), 0 everywhere else. Point-sampled on purpose: the step down to sea level happens one
// cell outside the tarn, inside the mountain, where the ground hides it.
Texture2D g_tSeaLakeLevel < Attribute( "SeaLakeLevel" ); SrgbRead( false ); >;
// The swell travel field (SeaSwellTravelField): R = detour S - wind·p (m), GB = ∇S, A = exposure. RGBA32F, bilinear by hand.
Texture2D g_tSeaSwellField < Attribute( "SeaSwellField" ); SrgbRead( false ); >;
// x = field half size (m), y = nodes (0 = no field: plane waves), z / w = the swell heading the field was marched for
float4 g_vSeaSwellFieldInfo < Attribute( "SeaSwellFieldInfo" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
// x = depth where the swell starts (m), y = depth carrying the full swell (m) — lakes only
float4 g_vSeaSwellDepth < Attribute( "SeaSwellDepth" ); Default4( 10.0, 30.0, 0.0, 0.0 ); >;
// The swell for the current weather (SeaWaves.Spectrum): amplitudes (m) of the 14 swells, then crest factor Q, total
float4 g_vSeaSwellA < Attribute( "SeaSwellA" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
float4 g_vSeaSwellB < Attribute( "SeaSwellB" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
float4 g_vSeaSwellC < Attribute( "SeaSwellC" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
float4 g_vSeaSwellD < Attribute( "SeaSwellD" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
// x = the swell carrying the most of the sea (index): its crests time the laps on the ocean's beaches; y = rms amplitude (m)
float4 g_vSeaSwellE < Attribute( "SeaSwellE" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
// The chop for the current weather (SeaWaves.Spectrum): the 10 chop waves' shares of the chop amplitude (sum 1), then
// C.z = crest factor Q, C.w = the chop wave carrying the most (index 0..9: its crests time the small laps)
float4 g_vSeaChopA < Attribute( "SeaChopA" ); Default4( 0.06, 0.08, 0.10, 0.12 ); >;
float4 g_vSeaChopB < Attribute( "SeaChopB" ); Default4( 0.13, 0.13, 0.12, 0.11 ); >;
float4 g_vSeaChopC < Attribute( "SeaChopC" ); Default4( 0.09, 0.06, 0.5, 5.0 ); >;

// WindSystem (same names grass_blade.shader reads)
float3 g_vSeaWindDirection < Attribute( "WindDirection" ); Default3( 1.0, 0.0, 0.0 ); >;
float g_flSeaWindStrength < Attribute( "WindStrength" ); Default( 0.25 ); >;
float g_flSeaWindGust < Attribute( "WindGust" ); Default( 0.0 ); >;

// The lake mask is R32F, which GPUs do not filter: a "bilinear" sampler on it returned the nearest texel and the
// waterline drew the raster cells as right-angle stairs (2026-10-22). Bilinear by hand, texel centres, 0 outside —
// exactly TerrainPreviewLandDiskFields.SampleLakeMaskGridBilinear.
float SeaMaskBilinearTexel( int2 p, int2 size )
{
	if ( p.x < 0 || p.y < 0 || p.x >= size.x || p.y >= size.y )
		return 0.0;
	return g_tSeaLakeMask.Load( int3( p, 0 ) ).r;
}
float SeaMaskBilinear( float2 uv )
{
	uint w, h;
	g_tSeaLakeMask.GetDimensions( w, h );
	int2 size = int2( w, h );
	float2 f = uv * float2( size ) - 0.5;
	float2 i0 = floor( f );
	float2 t = f - i0;
	int2 a = int2( i0 );
	float m0 = lerp( SeaMaskBilinearTexel( a, size ), SeaMaskBilinearTexel( a + int2( 1, 0 ), size ), t.x );
	float m1 = lerp( SeaMaskBilinearTexel( a + int2( 0, 1 ), size ), SeaMaskBilinearTexel( a + int2( 1, 1 ), size ), t.x );
	return lerp( m0, m1, t.y );
}

// Oceanness, bilinear by hand like the mask (R32F), clamped at the raster's edge
float SeaOceanness( float2 uv )
{
	uint w, h;
	g_tSeaOceanness.GetDimensions( w, h );
	if ( w < 2 )
		return 0.0;
	int2 size = int2( w, h );
	float2 f = clamp( uv * float2( size ) - 0.5, 0.0, float2( size ) - 1.001 );
	int2 a = int2( floor( f ) );
	float2 t = f - float2( a );
	float m0 = lerp( g_tSeaOceanness.Load( int3( a, 0 ) ).r, g_tSeaOceanness.Load( int3( a + int2( 1, 0 ), 0 ) ).r, t.x );
	float m1 = lerp( g_tSeaOceanness.Load( int3( a + int2( 0, 1 ), 0 ) ).r, g_tSeaOceanness.Load( int3( a + int2( 1, 1 ), 0 ) ).r, t.x );
	return lerp( m0, m1, t.y );
}

// Metres a tarn lifts the water surface here (TerrainPreviewLandDiskFields.SampleWaterSurfaceMeters - sea level); 0 on
// the sea and on ordinary lakes. Same raster layout as the lake mask (x mirrored).
float SeaLakeLevelLiftMeters( float2 xyMeters )
{
	uint w, h;
	g_tSeaLakeLevel.GetDimensions( w, h );
	if ( w < 2 )
		return 0.0;
	float worldRadius = g_vSeaShape.x;
	float diameter = worldRadius * 2.0;
	float2 uv = float2( 1.0 - (xyMeters.x + worldRadius) / diameter, (xyMeters.y + worldRadius) / diameter );
	int2 p = int2( floor( uv * float2( w, h ) ) );
	if ( p.x < 0 || p.y < 0 || p.x >= int( w ) || p.y >= int( h ) )
		return 0.0;
	return g_tSeaLakeLevel.Load( int3( p, 0 ) ).r;
}

// Signed metres to the lake shoreline, bilinear by hand on the R32F field (TerrainPreviewLakeShoreDistance.SignedMeters)
float SeaLakeShoreMeters( float2 xyMeters )
{
	uint w, h;
	g_tSeaLakeShore.GetDimensions( w, h );
	float2 f = (xyMeters + g_vSeaShape.x) / 16.0;
	if ( w < 2 || f.x < 0.0 || f.y < 0.0 || f.x >= float( w - 1 ) || f.y >= float( h - 1 ) )
		return 2000.0;
	int2 a = int2( floor( f ) );
	float2 t = f - float2( a );
	float m0 = lerp( g_tSeaLakeShore.Load( int3( a, 0 ) ).r, g_tSeaLakeShore.Load( int3( a + int2( 1, 0 ), 0 ) ).r, t.x );
	float m1 = lerp( g_tSeaLakeShore.Load( int3( a + int2( 0, 1 ), 0 ) ).r, g_tSeaLakeShore.Load( int3( a + int2( 1, 1 ), 0 ) ).r, t.x );
	return lerp( m0, m1, t.y );
}

// The swell travel data at a point: x = detour (m), yz = ∇S, w = exposure (SeaSwellTravelField.Sample). Plane values
// without a field or outside it.
float4 SeaSwellTravel( float2 xyMeters )
{
	float2 wind = g_vSeaSwellFieldInfo.zw;
	float4 plane = float4( 0.0, wind, 1.0 );
	float nodes = g_vSeaSwellFieldInfo.y;
	if ( nodes < 2.0 )
		return plane;
	float2 f = (xyMeters + g_vSeaSwellFieldInfo.x) / 40.0;
	if ( f.x < 0.0 || f.y < 0.0 || f.x >= nodes - 1.0 || f.y >= nodes - 1.0 )
		return plane;
	int2 a = int2( floor( f ) );
	float2 t = f - float2( a );
	float4 m0 = lerp( g_tSeaSwellField.Load( int3( a, 0 ) ), g_tSeaSwellField.Load( int3( a + int2( 1, 0 ), 0 ) ), t.x );
	float4 m1 = lerp( g_tSeaSwellField.Load( int3( a + int2( 0, 1 ), 0 ) ), g_tSeaSwellField.Load( int3( a + int2( 1, 1 ), 0 ) ), t.x );
	return lerp( m0, m1, t.y );
}

// Depth (m) of the water inside the land disc; 0 on dry land. TerrainPreviewPipeline.WaterDepthMeters without its
// relief: enclosed water down to Lake Max Depth, ocean (SeaOceanness) to Ocean Max Depth, a shelf and then the fall.
// oceanness = 1 where this water is joined to the rim ocean, 0 in a lake.
float SeaInteriorDepthMeters( float2 xyMeters, out float oceanness )
{
	oceanness = 0.0;
	float worldRadius = g_vSeaShape.x;
	// The lake raster: x mirrored, exactly TerrainPreviewLandDiskFields.WorldMetersToRasterFloat
	float diameter = worldRadius * 2.0;
	float2 uv = float2( 1.0 - (xyMeters.x + worldRadius) / diameter, (xyMeters.y + worldRadius) / diameter );
	// Under water: over the mask threshold and inside the traced shoreline (TerrainPreviewLakeShoreHeight.
	// IsSubmergedByLakeMask - a filtered-out speck is dry land)
	float shore = SeaLakeShoreMeters( xyMeters );
	if ( SeaMaskBilinear( uv ) <= g_vSeaLake.x || shore >= 0.0 )
		return 0.0;
	float ocean = saturate( SeaOceanness( uv ) );
	oceanness = ocean;
	float maxDepth = lerp( g_vSeaLake.y, max( g_vSeaLake.y, g_vSeaShape.z ), ocean * ocean * (3.0 - 2.0 * ocean) );
	float shelfDepth = min( g_vSeaShelf.z, maxDepth );
	float dropWidth = max( max( 1.0, g_vSeaShelf.w ), (maxDepth - shelfDepth) * g_vSeaLake.w );
	// Distance in from the traced shoreline (TerrainPreviewPipeline.MetersIntoLake)
	float into = -shore;
	float t = saturate( into / max( 1.0, g_vSeaLake.z ) );
	float d = saturate( (into - g_vSeaLake.z) / dropWidth );
	return shelfDepth * t * (2.0 - t) + (maxDepth - shelfDepth) * d * (2.0 - d);
}

// The point just inside the land edge on a rim point's radius: where the sea inside the disc hands over to the rim
float2 SeaEdgePoint( float2 xyMeters, float dist )
{
	return xyMeters * ((g_vSeaShape.y - 1.0) / max( dist, 1.0 ));
}

// Water depth (m) under a point, as the terrain carve made it; 0 on land. openness = 0 shallow … 1 deep enough for the
// full swell (lakes: Swell Min / Full Depth; the rim ocean and the water joined to it near the land edge: 1).
float SeaDepthMetersOpen( float2 xyMeters, out float openness )
{
	float landRadius = g_vSeaShape.y;
	float dist = length( xyMeters );
	float depth;
	if ( dist > landRadius )
	{
		float past = dist - landRadius;
		// TerrainPreviewPipeline.RimOceanDepthMeters: the shelf on t*(2-t), then the drop-off on d*(2-d), never shallower
		// than the sea inside the disc is where this radius crosses the edge
		float t = saturate( past / max( 1.0, g_vSeaShape.w ) );
		float d = saturate( (past - g_vSeaShape.w) / max( 1.0, g_vSeaShelf.y ) );
		depth = g_vSeaShelf.x * t * (2.0 - t) + max( g_vSeaShape.z - g_vSeaShelf.x, 0.0 ) * d * (2.0 - d);
		float edgeOcean;
		depth = max( depth, SeaInteriorDepthMeters( SeaEdgePoint( xyMeters, dist ), edgeOcean ) );
		openness = 1.0;
	}
	else
	{
		float ocean;
		depth = SeaInteriorDepthMeters( xyMeters, ocean );
		// The coast is carved in from the land edge: the real beaches lie tens of metres inside the disc, in water joined
		// to the ocean. That water carries the ocean's swell right up to the sand (it used to be treated as a lake, whose
		// shallows carry none: the surf stopped dead at the land-edge circle, short of every beach — 2026-11-19). The
		// share fades out 0.6 - 2 km in from the edge, so a sea arm reaching far inland calms down; the travel field's
		// exposure shelters the rest.
		float reach = ocean * smoothstep( landRadius - 2000.0, landRadius - 600.0, dist );
		openness = max( smoothstep( g_vSeaSwellDepth.x, g_vSeaSwellDepth.y, depth ), reach );
	}
	return depth;
}

// Water depth (m) the sea / lake surface covers: open water, plus the last stretch of each river channel at a mouth
// (river_mouths.hlsl — the sea takes over the river there)
float SeaDepthMeters( float2 xyMeters, out float openness )
{
	float open = SeaDepthMetersOpen( xyMeters, openness );
	return max( open, RiverMouthAt( xyMeters ).y );
}

// Signed metres from the waterline: + out on the water, - onto the land. Inside the land disc the traced shoreline
// (which has no shore along the disc's edge where a sea runs through it); past the edge, the distance out from it plus
// however far the water already reaches inside it on this radius - open sea across the edge is not a coast.
float SeaShoreX( float2 xyMeters )
{
	float dist = length( xyMeters );
	float ocean = dist - g_vSeaShape.y;
	if ( ocean > 0.0 )
		return ocean + max( 0.0, -SeaLakeShoreMeters( SeaEdgePoint( xyMeters, dist ) ) );
	return max( ocean, -SeaLakeShoreMeters( xyMeters ) );
}

// How far onto dry land a point is (m)
float SeaLandMeters( float2 xyMeters )
{
	return max( 0.0, -SeaShoreX( xyMeters ) );
}

// How hard the sea hits this shore: full on the ocean rim, a quarter on lakes, times the weather's swell against
// the default sea (calm 0.3x ... storm 2.8x, softened)
float SeaShoreEnergy( float2 p )
{
	float rim = smoothstep( g_vSeaShape.y - 600.0, g_vSeaShape.y - 100.0, length( p ) );
	float weather = g_vSeaSwellD.w / max( g_vSeaWaves.x * 1.2 * 1.3, 0.01 );
	return lerp( 0.25, 1.0, rim ) * clamp( pow( max( weather, 0.0 ), 0.6 ), 0.4, 1.9 );
}

// The sea's components (SeaWaves.cs Waves - keep in step): heading off the wind (rad), wavelength (m), phase (rad).
// 14 swells (amplitudes per weather from SeaSwellA..D), 10 chop waves (shares per weather from SeaChopA..C);
// wavelengths spread log-evenly with jitter, so no two are harmonics of each other.
static const float3 c_vSeaWaves[SEA_WAVES] =
{
	float3( 0.061, 17.03, 3.054 ),
	float3( 0.402, 21.79, 0.055 ),
	float3( -0.406, 25.71, 4.065 ),
	float3( 0.500, 30.37, 3.130 ),
	float3( 1.200, 36.92, 0.332 ),
	float3( -0.997, 43.66, 3.119 ),
	float3( 0.285, 55.61, 2.435 ),
	float3( -0.078, 69.53, 1.935 ),
	float3( -0.333, 82.04, 0.440 ),
	float3( 0.247, 99.29, 4.725 ),
	float3( -0.245, 109.09, 4.500 ),
	float3( 0.436, 146.17, 3.002 ),
	float3( -0.420, 163.12, 0.960 ),
	float3( 0.613, 191.73, 0.978 ),
	float3( -0.677, 2.09, 0.382 ),
	float3( -0.278, 2.60, 2.480 ),
	float3( 0.091, 3.55, 4.212 ),
	float3( -0.719, 4.36, 2.844 ),
	float3( -0.873, 4.87, 1.755 ),
	float3( 0.843, 6.72, 4.059 ),
	float3( -1.052, 7.94, 3.728 ),
	float3( -0.535, 11.00, 4.510 ),
	float3( -1.089, 13.07, 0.502 ),
	float3( 0.067, 14.75, 0.509 ),
};
// Wave groups (Mark 2026-10-25: "the frequencies are too matched and perfect so it's just the same repeating
// pattern"): every wave rises and fades over 6-14 of its own wavelengths - envelope heading off the wave's own
// (rad), envelope length (m), phase - drifting along at the group speed (half the wave's).
static const float3 c_vSeaGroups[SEA_WAVES] =
{
	float3( -1.464, 222.1, 5.520 ),
	float3( -2.837, 218.2, 0.362 ),
	float3( 2.376, 342.4, 6.113 ),
	float3( 0.935, 422.6, 1.161 ),
	float3( -0.927, 367.2, 1.358 ),
	float3( -0.351, 454.7, 4.986 ),
	float3( -0.059, 527.5, 1.087 ),
	float3( 1.191, 630.0, 5.576 ),
	float3( 2.953, 1121.0, 2.837 ),
	float3( -2.648, 701.4, 2.094 ),
	float3( 2.867, 1249.3, 3.006 ),
	float3( 2.005, 1311.0, 5.245 ),
	float3( -1.933, 1378.3, 4.677 ),
	float3( -1.494, 2313.7, 0.998 ),
	float3( 1.290, 24.3, 2.461 ),
	float3( 0.166, 25.2, 5.611 ),
	float3( -0.843, 39.1, 2.019 ),
	float3( 1.474, 49.5, 5.527 ),
	float3( -0.267, 32.5, 6.064 ),
	float3( 1.764, 72.0, 3.376 ),
	float3( -0.515, 101.5, 5.212 ),
	float3( -0.274, 150.5, 3.908 ),
	float3( -0.855, 147.5, 4.050 ),
	float3( 0.500, 114.3, 0.993 ),
};

float SeaSwellAmplitude( int w )
{
	if ( w < 4 ) return g_vSeaSwellA[w];
	if ( w < 8 ) return g_vSeaSwellB[w - 4];
	if ( w < 12 ) return g_vSeaSwellC[w - 8];
	return g_vSeaSwellD[w - 12];
}

// Share of the chop amplitude chop wave c (0..9) carries in this weather
float SeaChopShare( int c )
{
	if ( c < 4 ) return g_vSeaChopA[c];
	if ( c < 8 ) return g_vSeaChopB[c - 4];
	return g_vSeaChopC[c - 8];
}

// The chop amplitude (m) of open water for the live wind, before the shallows take it down
float SeaChopOpenMeters()
{
	float windAmt = saturate( g_flSeaWindStrength + g_flSeaWindGust * 0.5 );
	return g_vSeaWaves.y * lerp( 0.3, 1.6, windAmt );
}

// Crests that bend: every wave's phase is taken at a gently warped position (up to ~4 m over ~60-110 m, never
// repeating at the waves' own lengths), so crossing wave trains wander instead of locking into a straight diamond
// lattice (SeaWaves.Warp)
float2 SeaWarp( float2 p )
{
	return float2(
		sin( p.y * 0.0571 + 1.3 ) * 2.4 + sin( (p.x + p.y) * 0.0233 + 4.1 ) * 1.6,
		sin( p.x * 0.0613 + 0.7 ) * 2.4 + sin( (p.x - p.y) * 0.0197 + 2.2 ) * 1.6 );
}

// A wave's group envelope at a point and time: 0.15 .. 1 (SeaWaves.GroupEnvelope)
float SeaGroupEnvelope( int w, float2 p, float2 dir, float omega, float k, float t )
{
	float3 g = c_vSeaGroups[w];
	float s = sin( g.x ), c = cos( g.x );
	float2 e = float2( dir.x * c - dir.y * s, dir.x * s + dir.y * c );
	float ke = 6.2831853 / g.y;
	// The envelope rides with the wave train at the group speed (half the phase speed, deep water)
	float travel = 0.5 * (omega / k) * t * dot( e, dir );
	return 0.575 + 0.425 * sin( ke * (dot( e, p ) - travel) + g.z );
}

// Linear shoaling coefficient of a wave over this depth: 1 in deep water, growing as it shallows (SeaWaves.Shoal)
float SeaShoal( float k, float depth )
{
	float kd = k * max( depth, 0.05 );
	if ( kd > 6.0 )
		return 1.0;
	float th = tanh( kd );
	float s2 = sinh( 2.0 * kd );
	float n = 1.0 + 2.0 * kd / max( s2, 1e-4 );
	return min( 1.5, rsqrt( max( th * n, 1e-3 ) ) );
}

// The swell's component heading for the field's wind
float2 SeaSwellDir( int w, float2 wind )
{
	float s = sin( c_vSeaWaves[w].x ), c = cos( c_vSeaWaves[w].x );
	return float2( wind.x * c - wind.y * s, wind.x * s + wind.y * c );
}

// Scallops along the shore, a few metres to a few tens of metres long, drifting slowly: -1..1. A lap reaches further up
// the sand in one place than beside it and arrives a moment sooner — a real swash front is never a ruled line.
float SeaLobes( float2 p, float t )
{
	return sin( p.x * 0.19 + p.y * 0.23 + t * 0.11 ) * 0.45
		+ sin( p.y * 0.52 - p.x * 0.31 - t * 0.17 + 1.7 ) * 0.33
		+ sin( p.y * 0.93 + p.x * 0.77 + 4.1 ) * 0.22;
}

// The ocean swell at a point of the waterline as ONE wave: the five swells round the peak, summed as phasors. theta is
// the phase of that sum (a crest at pi/2), strength its height against all five in step (0..1) times the field's
// exposure. Because it is the swell's own sum, the laps come in sets and lulls exactly as the crests offshore do.
void SeaSwellCarrier( float2 shore, float t, out float theta, out float strength )
{
	int peak = (int)g_vSeaSwellE.x;
	float2 wind = g_vSeaSwellFieldInfo.zw;
	float4 travel = SeaSwellTravel( shore );
	float2 sw = shore + SeaWarp( shore );
	float2 cs = 0.0;
	float total = 0.0;
	[unroll]
	for ( int j = -2; j <= 2; j++ )
	{
		int w = clamp( peak + j, 0, SEA_SWELLS - 1 );
		float3 wave = c_vSeaWaves[w];
		float2 dir = SeaSwellDir( w, wind );
		float k = 6.2831853 / wave.y;
		float omega = sqrt( 9.81 * k );
		float full = SeaSwellAmplitude( w );
		float a = full * SeaGroupEnvelope( w, shore, dir, omega, k, t );
		float ph = k * (dot( dir, sw ) + travel.x) - omega * t + wave.z;
		cs += a * float2( cos( ph ), sin( ph ) );
		total += full;
	}
	theta = atan2( cs.y, cs.x + 1e-6 );
	strength = saturate( length( cs ) / max( total, 1e-3 ) * 1.8 ) * travel.w;
}

// The chop at a point as one wave: the three chop waves round the one carrying the most. amp = its height as a share
// of the chop amplitude.
void SeaChopCarrier( float2 p, float t, out float theta, out float amp )
{
	int peak = (int)g_vSeaChopC.w;
	float2 wind = normalize( g_vSeaWindDirection.xy + float2( 1e-4, 0.0 ) );
	float2 pw = p + SeaWarp( p );
	float2 cs = 0.0;
	[unroll]
	for ( int j = -1; j <= 1; j++ )
	{
		int c = clamp( peak + j, 0, SEA_CHOPS - 1 );
		int w = SEA_SWELLS + c;
		float3 wave = c_vSeaWaves[w];
		float2 dir = SeaSwellDir( w, wind );
		float k = 6.2831853 / wave.y;
		float omega = sqrt( 9.81 * k );
		float a = SeaChopShare( c ) * SeaGroupEnvelope( w, p, dir, omega, k, t );
		float ph = k * dot( dir, pw ) - omega * t + wave.z;
		cs += a * float2( cos( ph ), sin( ph ) );
	}
	theta = atan2( cs.y, cs.x + 1e-6 );
	amp = length( cs );
}

// One lap (Mark 2026-10-25: the waves "don't go up the shoreline like they should … it should be sloped based, height
// based"; 2026-11-19: "the waves crashing on the shore look really bad"). x = signed metres from the carve's waterline,
// + out to sea. age counts in wave periods from when the lap sets off `start` metres out: it comes in as a bore at a
// steady pace (until age = arrive), runs up the sand slowing down like anything thrown uphill (over `up`), stands, and
// drains back speeding up (gone by `end`). The water is a wedge tallest just behind its front on the way in and a
// thinning film on the way back; where it is lower than the ground the terrain hides it, so how far the water really
// gets follows the beach. Foam: a churned band right behind the front (wide on a big lap, thinning to a line as the
// lap slows), a dissolving trail behind that, and the line of bubbles left where the lap turned. size scales the
// metre lengths (1 = an ocean lap, small for a wavelet). Returns x = elevation (m), y = foam.
float2 SeaLap( float x, float age, float start, float reach, float height, float strength, float size, float arrive, float up, float end )
{
	float sUp = saturate( (age - arrive) / up );
	float sBack = saturate( (age - arrive - up) / (end - arrive - up) );
	float front = age < arrive
		? start * (1.0 - age / arrive)
		: -reach * (1.0 - (1.0 - sUp) * (1.0 - sUp)) + reach * 1.05 * sBack * sBack;
	float behind = x - front;                                    // > 0: seaward of the front, under the lap
	float born = smoothstep( 0.0, arrive * 0.5, age );
	float drain = 1.0 - sBack;
	float sheet = smoothstep( -0.25 * size, 0.6 * size, behind ) * exp( -max( behind, 0.0 ) / ((6.0 + 10.0 * sBack) * size) ) * born * drain * sqrt( drain );
	float width = (0.5 + 2.2 * strength) * size * (1.0 - 0.8 * sUp);
	float band = smoothstep( -0.2 * size, 0.1 * size, behind ) * (1.0 - smoothstep( width * 0.4, width, behind )) * born * saturate( 1.0 - sBack * 3.0 );
	float trail = behind > 0.0
		? exp( -behind / ((1.5 + 4.0 * age) * size) ) * 0.55 * born * (1.0 - smoothstep( arrive + up * 0.6, arrive + up + 0.25, age ))
		: 0.0;
	float atTurn = x + reach;
	float strand = exp( -atTurn * atTurn / (0.4 * size * size) ) * smoothstep( arrive + up * 0.8, arrive + up, age ) * (1.0 - smoothstep( arrive + up, end, age )) * 0.7;
	return float2( height * sheet, max( max( band, trail ), strand ) * saturate( strength * 2.5 ) );
}

// The swash at a point: x = signed metres from the waterline (+ out to sea). On the ocean rim, the swell's laps — the
// crest arriving now and the one before it, which is still draining back when the next bore runs in over it; everywhere
// (lakes too) the chop's small laps, a hand's width high in a breeze. x = elevation of the water (m), y = foam amount.
float2 SeaShoreLaps( float2 p, float x, float energy, float t )
{
	float lobes = SeaLobes( p, t );
	float start = g_vSeaShore.x * 2.5;
	float2 lap = 0.0;
	float rim = smoothstep( g_vSeaShape.y - 400.0, g_vSeaShape.y - 200.0, length( p ) );
	if ( rim > 0.0 )
	{
		float2 shore = normalize( p + float2( 1e-3, 0.0 ) ) * g_vSeaShape.y;
		float theta, strength;
		SeaSwellCarrier( shore, t, theta, strength );
		strength *= rim;
		// The crest (theta = pi/2) reaches the waterline at age 0.6: the lap set off 0.6 of a period earlier, out at `start`
		float age = frac( (1.5707963 - theta) / 6.2831853 + 0.6 + 0.05 * lobes );
		float height = g_vSeaShore.z * energy * strength;
		float reach = g_vSeaShore.w * clamp( energy, 0.3, 1.5 ) * strength * (0.78 + 0.22 * lobes);
		lap = max( SeaLap( x, age, start, reach, height, strength, 1.0, 0.6, 0.45, 1.8 ),
			SeaLap( x, age + 1.0, start, reach, height, strength, 1.0, 0.6, 0.45, 1.8 ) );
	}
	// The chop lapping at the edge: a few centimetres of water running a metre or two up a flat beach, every second or
	// two, white only once the wind has it standing a hand high
	float theta2, share;
	SeaChopCarrier( p, t, theta2, share );
	float small = 2.5 * share * SeaChopOpenMeters() * lerp( g_vSeaWaves.z, 1.0, rim );
	float reach2 = min( small * 30.0, 6.0 ) * (0.78 + 0.22 * lobes);
	float size = clamp( reach2 / 8.0, 0.12, 1.0 );
	float age2 = frac( (1.5707963 - theta2) / 6.2831853 + 0.3 );
	lap = max( lap, SeaLap( x, age2, 3.0 * size, reach2, small, saturate( small / 0.15 ), size, 0.3, 0.3, 0.95 ) );
	float strip = 1.0 - smoothstep( start, start * 1.6, x );
	return lap * strip;
}

// White water of the breaking set: on the front faces (the roller, SeaWaveSum), a crest here and there where the set
// first reaches the breaking height, most faces white in the last metres of water — the inner surf is bore after bore.
float SeaBreakFoam( float breaking, float roller, float depth )
{
	float inner = 1.0 - smoothstep( 0.5, 4.0, depth );
	float from = lerp( 0.8, 0.4, inner );
	return smoothstep( 0.0, 0.35, breaking ) * smoothstep( from, from + 0.5, roller );
}

// Wave sum at a point (metres). spacing = mesh / pixel spacing there (m): a wave shorter than ~4 spacings fades out
// instead of aliasing. Returns the displacement (m); normal, crest-folding (whitecaps), the breaking amount (0 = the
// set stands under the breaking height, 1 = it would stand twice it) and the roller (how much of the set's front faces
// meet here, in units of the set's own rms: > ~0.5 is a breaking face) as outputs. SeaWaves.WaveSum is the CPU mirror.
float3 SeaWaveSum( float2 p, float depth, float openness, float spacing, out float3 normal, out float folding, out float breaking, out float roller )
{
	float2 liveWind = normalize( g_vSeaWindDirection.xy + float2( 1e-4, 0.0 ) );
	float2 swellWind = g_vSeaSwellFieldInfo.zw;
	float4 travelData = SeaSwellTravel( p );
	float detour = travelData.x;
	float2 grad = travelData.yz;
	float exposure = travelData.w;
	float t = g_vSeaClock.x;
	float3 disp = 0;
	float3 dn = float3( 0, 0, 1 );
	folding = 0.0;
	float2 pw = p + SeaWarp( p );

	// Chop from the live wind, faded out in the shallows (the shore belongs to the swell running up)
	float chop = SeaChopOpenMeters() * lerp( g_vSeaWaves.z, 1.0, saturate( openness * 4.0 ) );
	chop *= lerp( 0.15, 1.0, smoothstep( 0.5, 4.0, depth ) );
	chop = min( chop, 0.3 * depth );

	// Each swell's amplitude here before the groups, the set's crest estimate against the breaking height, and the set's
	// steepness (sum of k·a) with the crests as close together as the travel field packs them here (index = |∇S|)
	float index = max( 1.0, length( grad ) );
	float amp[SEA_SWELLS];
	float squares = 0.0;
	float kaSum = 0.0;
	[unroll]
	for ( int i = 0; i < SEA_SWELLS; i++ )
	{
		float k = 6.2831853 / c_vSeaWaves[i].y;
		amp[i] = SeaSwellAmplitude( i ) * openness * exposure * SeaShoal( k, depth );
		squares += amp[i] * amp[i];
		kaSum += k * amp[i];
	}
	float crestEstimate = 2.0 * sqrt( squares );
	float cap = SEA_BREAKING_RATIO * depth + g_vSeaWaves.w;
	float scale = min( 1.0, cap / max( crestEstimate, 1e-3 ) );
	breaking = saturate( crestEstimate / max( cap, 1e-3 ) - 1.0 );
	// 0 in deep water … 1 from where the set reaches the breaking height, all the way in
	float steep = saturate( crestEstimate / max( cap, 1e-3 ) );
	// Sharp crests, wide troughs: the weather's crest factor in deep water, everything the surface can take without
	// looping over itself once the set breaks (the old per-wave 0.4 summed past 1: the mesh folded through itself)
	float budget = SEA_CREST_BUDGET / max( kaSum * scale * index, 1e-4 );
	float crestQ = min( lerp( g_vSeaSwellD.z, budget, smoothstep( 0.6, 1.0, steep ) ), budget );
	// The lean: a shoaling wave's crest catches up with its trough ahead — gentle back, steep front, a bore. It grows
	// through the surf zone (a third of it where the set first breaks, all of it in the last metre and a half of water)
	// and only the long swells take it: on every component the harmonics of the short ones crossed each other and the
	// whole zone read as twisted rope (2026-11-19)
	float lean = SEA_LEAN * smoothstep( 0.35, 1.0, steep ) * lerp( 1.0, 0.35, smoothstep( 1.5, 6.0, depth ) );
	float rollerSum = 0.0;
	float rollerSquares = 0.0;

	[unroll]
	for ( int w = 0; w < SEA_WAVES; w++ )
	{
		float3 wave = c_vSeaWaves[w];
		float lambda = wave.y;
		float k = 6.2831853 / lambda;
		float omega = sqrt( 9.81 * k );
		float fade = saturate( 2.0 - spacing * 4.0 / lambda );
		float2 dir, travel;
		// kl = the wave's wavenumber HERE (the field packs the crests), h2 / h3 = its second / third harmonic,
		// face = 1 for a swell (it counts toward the roller)
		float a, phase0, qka, kl, h2, h3, face;
		if ( w < SEA_SWELLS )
		{
			dir = SeaSwellDir( w, swellWind );
			a = amp[w] * scale * fade * SeaGroupEnvelope( w, p, dir, omega, k, t );
			phase0 = k * (dot( dir, pw ) + detour);
			float2 along = dir + grad - swellWind;
			float packed = max( length( along ), 0.5 );
			travel = along / packed;
			kl = k * packed;
			qka = crestQ * kl * a;
			float waveLean = lean * smoothstep( 30.0, 60.0, lambda );
			h2 = 0.5 * waveLean;
			h3 = 0.33 * waveLean * waveLean;
			face = 1.0;
		}
		else
		{
			dir = SeaSwellDir( w, liveWind );
			a = SeaChopShare( w - SEA_SWELLS ) * chop * fade * SeaGroupEnvelope( w, p, dir, omega, k, t );
			phase0 = k * dot( dir, pw );
			travel = dir;
			kl = k;
			qka = g_vSeaChopC.z * k * a;
			h2 = 0.0;
			h3 = 0.0;
			face = 0.0;
		}
		float phase = phase0 - omega * t + wave.z;
		float sp = sin( phase ), cp = cos( phase );
		// sin 2φ, cos 2φ, sin 3φ, cos 3φ from the pair above
		float s2 = 2.0 * sp * cp, c2 = 1.0 - 2.0 * sp * sp;
		float s3 = sp * (3.0 - 4.0 * sp * sp), c3 = cp * (4.0 * cp * cp - 3.0);
		disp.xy += travel * (qka / kl * cp);
		disp.z += a * (sp - h2 * s2 + h3 * s3);
		dn.xy -= travel * (kl * a * (cp - 2.0 * h2 * c2 + 3.0 * h3 * c3));
		dn.z -= qka * sp;
		folding += qka * sp;
		// The front face: 1.1 rad ahead of the crest
		rollerSum += face * qka * (sp * 0.45 - cp * 0.89);
		rollerSquares += face * qka * qka;
	}
	roller = rollerSum * rsqrt( max( rollerSquares, 1e-8 ) );
	normal = normalize( dn );
	return disp;
}
