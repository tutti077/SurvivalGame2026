//
// sea_waves.hlsl — the sea's depth, swell and shore, shared by seawater.shader (vertex + pixel stages) and the foam
// memory pass (sea_foam_memory.shader). Included at COMMON level after common/shared.hlsl. Self-contained: the compiler
// builds an include as its own module, so everything it needs is declared here. SeaWaves.cs is the CPU mirror — keep
// every constant in step.
//
//   depth  : recomputed exactly as the terrain carve does it (TerrainPreviewPipeline LakeFloorMeters / SampleRimOcean):
//            the lake mask raster + traced shoreline for lakes, the shelf past the land edge for the ocean.
//   swell  : 14 Gerstner swells. Phase = k × (dir · warped p + detour) − ωt, where detour is the refraction travel
//            distance from SeaSwellTravelField (one continuous field marched over the sea floor): crests turn toward the
//            beach by themselves and stay one unbroken line (2026-10-26 — the old lerp of two phases drew bands across
//            the shelf). Each swell shoals on its own; the set is capped at the breaking height 0.39 × depth and the
//            excess is the breaking amount (sharper crests, white water).
//   chop   : 10 short waves from the live wind and its gusts, faded out in the shallows.
//   shore  : laps timed from the peak swell's crests on the ocean, two laps a period apart on lakes, running up the sand.
//
#define SEA_UNITS_PER_METER 40.0
#define WATER_STENCIL_LIFT_UNITS 40.0
#define SEA_WAVES 24
#define SEA_SWELLS 14
#define SEA_CHOP_CREST 0.03
// A crest may stand this fraction of the depth before it breaks (SeaWaves.BreakingRatio)
#define SEA_BREAKING_RATIO 0.39
#include "river_mouths.hlsl"

// ---- published by TerrainWorldManager.SeaWater (one source for the shader and SeaWaves.cs)
// x = world radius (m), y = land radius (m), z = ocean max depth (m), w = ocean shelf width (m)
float4 g_vSeaShape < Attribute( "SeaShape" ); Default4( 20000.0, 20000.0, 300.0, 900.0 ); >;
// x = ocean shelf depth (m), y = ocean drop-off width (m)
float4 g_vSeaShelf < Attribute( "SeaShelf" ); Default4( 30.0, 700.0, 0.0, 0.0 ); >;
// x = lake shore threshold (mask), y = lake max depth (m), z = lake shelf width (m)
float4 g_vSeaLake < Attribute( "SeaLake" ); Default4( 2.0, 8.0, 100.0, 1500.0 ); >;
// x = swell amplitude (m), y = chop amplitude (m), z = lake chop share, w = shore lap (m)
float4 g_vSeaWaves < Attribute( "SeaWaves" ); Default4( 1.6, 0.35, 0.35, 0.12 ); >;
// x = clock (s), y = far-tier sink under the near chunks (m, pixel_lit F_FAR_SINK), w = 1 once published
float4 g_vSeaClock < Attribute( "SeaClock" ); Default4( 0.0, 2.5, 0.0, 0.0 ); >;
// x = shore foam width (m), y = lap period (s), z = shore surge (m), w = run-up reach (m) — TerrainWorldManager "Sea" knobs
float4 g_vSeaShore < Attribute( "SeaShore" ); Default4( 4.0, 7.0, 0.45, 15.0 ); >;
// x = lake breaker height (m)
float4 g_vSeaShore2 < Attribute( "SeaShore2" ); Default4( 0.5, 0.0, 0.0, 0.0 ); >;
Texture2D g_tSeaLakeMask < Attribute( "SeaLakeMask" ); SrgbRead( false ); >;
// Signed metres to the traced lake shoreline (TerrainPreviewLakeShoreDistance: + dry, - in a lake), 16 m nodes from
// (-world radius, -world radius), x NOT mirrored. R32F: bilinear by hand like the mask.
Texture2D g_tSeaLakeShore < Attribute( "SeaLakeShore" ); SrgbRead( false ); >;
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

// Water depth (m) under a point, as the terrain carve made it; 0 on land. openness = 0 shallow … 1 deep enough for the
// full swell (lakes: Swell Min / Full Depth; the ocean is always 1).
float SeaDepthMetersOpen( float2 xyMeters, out float openness )
{
	float worldRadius = g_vSeaShape.x;
	float landRadius = g_vSeaShape.y;
	float dist = length( xyMeters );
	float depth;
	if ( dist > landRadius )
	{
		float past = dist - landRadius;
		// TerrainPreviewPipeline.OceanDepthMeters: shelf on t*(2-t), then the drop-off to the deep floor
		float t = saturate( past / max( 1.0, g_vSeaShape.w ) );
		float d = saturate( (past - g_vSeaShape.w) / max( 1.0, g_vSeaShelf.y ) );
		depth = g_vSeaShelf.x * t * (2.0 - t) + max( g_vSeaShape.z - g_vSeaShelf.x, 0.0 ) * d * d * (3.0 - 2.0 * d);
	}
	else
	{
		// The lake raster: x mirrored, exactly TerrainPreviewLandDiskFields.WorldMetersToRasterFloat
		float diameter = worldRadius * 2.0;
		float2 uv = float2( 1.0 - (xyMeters.x + worldRadius) / diameter, (xyMeters.y + worldRadius) / diameter );
		// Under a lake: over the mask threshold and inside the traced shoreline (TerrainPreviewLakeShoreHeight.
		// IsSubmergedByLakeMask - a filtered-out speck is dry land)
		float shore = SeaLakeShoreMeters( xyMeters );
		if ( SeaMaskBilinear( uv ) <= g_vSeaLake.x || shore >= 0.0 )
		{
			openness = 0.0;
			return 0.0;
		}
		// Distance in from the traced shoreline (TerrainPreviewPipeline.MetersIntoLake), then the shelf: Lake Max
		// Depth (y) reached over Lake Shelf Width (z, m) on t*(2-t)
		float into = -shore;
		float t = saturate( into / max( 1.0, g_vSeaLake.z ) );
		depth = g_vSeaLake.y * t * (2.0 - t);
	}
	openness = dist > landRadius ? 1.0 : smoothstep( g_vSeaSwellDepth.x, g_vSeaSwellDepth.y, depth );
	return depth;
}

// Water depth (m) the sea / lake surface covers: open water, plus the last stretch of each river channel at a mouth
// (river_mouths.hlsl — the sea takes over the river there)
float SeaDepthMeters( float2 xyMeters, out float openness )
{
	float open = SeaDepthMetersOpen( xyMeters, openness );
	return max( open, RiverMouthAt( xyMeters ).y );
}

// Signed metres from the waterline: + out on the water, - onto the land. The ocean's coast is the land-edge circle, a
// lake's the traced shoreline; whichever is nearer (one lookup — the old depth-gradient estimate cost four depth
// evaluations per pixel and read the slope, not the distance).
float SeaShoreX( float2 xyMeters )
{
	float ocean = length( xyMeters ) - g_vSeaShape.y;
	float lake = -SeaLakeShoreMeters( xyMeters );
	return max( ocean, lake );
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

float SeaAlongShift( float2 p )
{
	return sin( p.x * 0.0123 + p.y * 0.0071 ) * 0.5 + sin( p.y * 0.0139 - p.x * 0.0047 ) * 0.5;
}

// Lakes only: over the shallows small breakers roll in parallel to the waterline, two per lap period, peaked and growing
// as the water shoals, gone in the last half metre where the laps take over. On the ocean the breakers are the swell
// itself. x = elevation (m); crestFoam = white water on the crests where they break.
float SeaBreakers( float2 p, float x, float depth, float energy, float t, out float crestFoam )
{
	crestFoam = 0.0;
	float lake = 1.0 - smoothstep( g_vSeaShape.y - 600.0, g_vSeaShape.y - 100.0, length( p ) );
	if ( lake <= 0.0 )
		return 0.0;
	float band = smoothstep( 0.2, 1.0, depth ) * (1.0 - smoothstep( 3.0, 6.0, depth ));
	float phase = 6.2831853 * ( x / 13.0 + 2.0 * (t / g_vSeaShore.y + SeaAlongShift( p ) * 0.35) );
	float s = 0.5 + 0.5 * sin( phase );
	float shoal = lerp( 1.0, 0.55, saturate( (depth - 1.0) / 4.0 ) );
	crestFoam = band * smoothstep( 0.82, 0.99, s ) * (1.0 - smoothstep( 1.0, 2.2, depth )) * saturate( energy ) * 0.6 * lake;
	return g_vSeaShore2.x * energy * band * shoal * s * s * s * lake;
}

// The sea's components (SeaWaves.cs Waves - keep in step): heading off the wind (rad), wavelength (m), phase (rad),
// chop share. 14 swells (amplitudes per weather from SeaSwellA..D), 10 chop waves (shares of the chop amplitude);
// wavelengths spread log-evenly with jitter, so no two are harmonics of each other.
static const float4 c_vSeaWaves[SEA_WAVES] =
{
	float4( 0.061, 17.03, 3.054, 0.00 ),
	float4( 0.402, 21.79, 0.055, 0.00 ),
	float4( -0.406, 25.71, 4.065, 0.00 ),
	float4( 0.500, 30.37, 3.130, 0.00 ),
	float4( 1.200, 36.92, 0.332, 0.00 ),
	float4( -0.997, 43.66, 3.119, 0.00 ),
	float4( 0.285, 55.61, 2.435, 0.00 ),
	float4( -0.078, 69.53, 1.935, 0.00 ),
	float4( -0.333, 82.04, 0.440, 0.00 ),
	float4( 0.247, 99.29, 4.725, 0.00 ),
	float4( -0.245, 109.09, 4.500, 0.00 ),
	float4( 0.436, 146.17, 3.002, 0.00 ),
	float4( -0.420, 163.12, 0.960, 0.00 ),
	float4( 0.613, 191.73, 0.978, 0.00 ),
	float4( -0.677, 2.09, 0.382, 0.06 ),
	float4( -0.278, 2.60, 2.480, 0.08 ),
	float4( 0.091, 3.55, 4.212, 0.10 ),
	float4( -0.719, 4.36, 2.844, 0.12 ),
	float4( -0.873, 4.87, 1.755, 0.13 ),
	float4( 0.843, 6.72, 4.059, 0.13 ),
	float4( -1.052, 7.94, 3.728, 0.12 ),
	float4( -0.535, 11.00, 4.510, 0.11 ),
	float4( -1.089, 13.07, 0.502, 0.09 ),
	float4( 0.067, 14.75, 0.509, 0.06 ),
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

// Ocean beaches: when the crest of the swell carrying the most of the sea reaches this stretch of shore (0 = just
// arrived, rising toward 1 until the next), and how tall that crest is here (its wave-group envelope) - the lap is the
// swell itself running up the sand (Mark 2026-10-26). The phase is the field's, taken at the waterline point.
float SeaSwellArrival( float2 p, float t, out float envelope )
{
	int w = (int)g_vSeaSwellE.x;
	float4 wave = c_vSeaWaves[w];
	float2 wind = g_vSeaSwellFieldInfo.zw;
	float2 dir = SeaSwellDir( w, wind );
	float k = 6.2831853 / wave.y;
	float omega = sqrt( 9.81 * k );
	float2 shore = normalize( p + float2( 1e-3, 0.0 ) ) * g_vSeaShape.y;
	float4 travel = SeaSwellTravel( shore );
	float phase = k * (dot( dir, shore + SeaWarp( shore ) ) + travel.x) - omega * t + wave.z;
	envelope = SeaGroupEnvelope( w, shore, dir, omega, k, t ) * travel.w;
	// A crest is sin(phase) = 1; the lap front sets off ~10 m out, a little before the crest reaches the waterline
	return frac( (1.5707963 - phase - k * g_vSeaShore.x * 2.5) / 6.2831853 );
}

// The laps (Mark 2026-10-25: the waves "don't go up the shoreline like they should … it should be sloped based, height
// based"). x = signed metres from the carve's waterline, + out to sea, - onto the land. Each lap's front rushes in from
// ~10 m out and up the sand to the run-up reach, decelerating, as a sheet of water that is tallest at the front; then
// the sheet drains and the front slides back. Where the sheet is lower than the ground the terrain simply hides it, so
// how far up the beach the water gets follows the beach's height. x = elevation of the water (m), y = foam amount.
float2 SeaShoreLaps( float2 p, float x, float energy, float t )
{
	float shift = SeaAlongShift( p );
	float start = g_vSeaShore.x * 2.5;
	float reach = g_vSeaShore.w * clamp( energy, 0.3, 1.5 );
	float height = g_vSeaShore.z * energy;
	float elev = 0.0;
	float foam = 0.0;
	// Ocean: one lap per arriving swell crest, as tall as that crest's group; lakes: two laps a Lap Period apart
	bool ocean = length( p ) > g_vSeaShape.y - 400.0;
	float envelope = 1.0;
	float swellAge = ocean ? SeaSwellArrival( p, t, envelope ) : 0.0;
	if ( ocean )
		height *= saturate( envelope * 1.3 );
	[unroll]
	for ( int k = 0; k < 2; k++ )
	{
		if ( ocean && k == 1 )
			break;
		float age = ocean ? swellAge : frac( t / g_vSeaShore.y + k * 0.5 + shift * 0.35 );
		float up = saturate( age / 0.4 );
		float front = age < 0.4
			? start - (start + reach) * (1.0 - (1.0 - up) * (1.0 - up))
			: -reach + (reach + start) * smoothstep( 0.4, 1.0, age );
		float behind = x - front;                                   // > 0: seaward of the front, under the lap
		// Fades in as it sets off and is gone before it gets back to the start, so where the timing wraps from one lap
		// to the next (it drifts along the shore) there is nothing to see - that seam drew white lines across the beach
		float fill = smoothstep( 0.0, 0.08, age ) * (age < 0.4 ? 1.0 : 1.0 - smoothstep( 0.4, 0.9, age ));
		float sheet = smoothstep( -0.3, 0.4, behind ) * exp( -max( behind, 0.0 ) / 6.0 ) * fill;
		elev = max( elev, height * sheet );
		float edgeLine = exp( -behind * behind / 0.25 ) * smoothstep( 0.0, 0.08, age ) * (age < 0.45 ? 1.0 : 1.0 - smoothstep( 0.45, 0.8, age ));
		float trail = behind > 0.0 ? exp( -behind / (0.8 + age * 3.0) ) * (1.0 - age) * smoothstep( 0.0, 0.08, age ) : 0.0;
		foam = max( foam, edgeLine + trail * 0.6 );
	}
	float strip = 1.0 - smoothstep( start, start * 1.6, x );
	return float2( elev * strip, foam * strip );
}

// Gerstner sum at a point (metres). spacing = mesh / pixel spacing there (m): a wave shorter than ~4 spacings fades out
// instead of aliasing. Returns the displacement (m); normal, crest-folding (whitecaps) and the breaking amount (0 = the
// set stands under the breaking height, 1 = it would stand twice it) as outputs. SeaWaves.WaveSum is the CPU mirror.
float3 SeaWaveSum( float2 p, float depth, float openness, float spacing, out float3 normal, out float folding, out float breaking )
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
	float windAmt = saturate( g_flSeaWindStrength + g_flSeaWindGust * 0.5 );
	float chop = g_vSeaWaves.y * lerp( 0.3, 1.6, windAmt ) * lerp( g_vSeaWaves.z, 1.0, saturate( openness * 4.0 ) );
	chop *= lerp( 0.15, 1.0, smoothstep( 0.5, 4.0, depth ) );
	chop = min( chop, 0.3 * depth );

	// Each swell's amplitude here before the groups, and the set's crest estimate against the breaking height
	float amp[SEA_SWELLS];
	float squares = 0.0;
	[unroll]
	for ( int i = 0; i < SEA_SWELLS; i++ )
	{
		float k = 6.2831853 / c_vSeaWaves[i].y;
		amp[i] = SeaSwellAmplitude( i ) * openness * exposure * SeaShoal( k, depth );
		squares += amp[i] * amp[i];
	}
	float crestEstimate = 2.0 * sqrt( squares );
	float cap = SEA_BREAKING_RATIO * depth + g_vSeaWaves.w;
	float scale = min( 1.0, cap / max( crestEstimate, 1e-3 ) );
	breaking = saturate( crestEstimate / max( cap, 1e-3 ) - 1.0 );

	[unroll]
	for ( int w = 0; w < SEA_WAVES; w++ )
	{
		float4 wave = c_vSeaWaves[w];
		float lambda = wave.y;
		float k = 6.2831853 / lambda;
		float omega = sqrt( 9.81 * k );
		float fade = saturate( 2.0 - spacing * 4.0 / lambda );
		float2 dir, travel;
		float a, phase0, qka;
		if ( w < SEA_SWELLS )
		{
			dir = SeaSwellDir( w, swellWind );
			a = amp[w] * scale * fade * SeaGroupEnvelope( w, p, dir, omega, k, t );
			phase0 = k * (dot( dir, pw ) + detour);
			travel = normalize( dir + grad - swellWind + float2( 1e-4, 0.0 ) );
			// Sharp crests, wide troughs (Gerstner): one crest factor (Q, from the weather); a breaking crest sharpens
			// toward folding over, whatever its height (only waves of a few centimetres stay round)
			qka = min( lerp( g_vSeaSwellD.z * k * a, 0.4, breaking * saturate( a / 0.2 ) ), 0.45 );
		}
		else
		{
			dir = SeaSwellDir( w, liveWind );
			a = wave.w * chop * fade * SeaGroupEnvelope( w, p, dir, omega, k, t );
			phase0 = k * dot( dir, pw );
			travel = dir;
			qka = min( SEA_CHOP_CREST, 6.0 * k * a );
		}
		float phase = phase0 - omega * t + wave.z;
		float sp = sin( phase ), cp = cos( phase );
		disp.xy += travel * (qka / k * cp);
		disp.z += a * sp;
		dn.xy -= travel * (k * a * cp);
		dn.z -= qka * sp;
		folding += qka * sp;
	}
	normal = normalize( dn );
	return disp;
}
