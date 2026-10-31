//
// water_common.hlsl — what every water surface shares (riverwater.shader, seawater.shader): the SkyDome's sky along a
// reflected ray, the GGX highlight of a light on the surface, and the rings / wakes of pawns cutting the water.
// Included inside PS {} after common/pixel.hlsl. Self-contained: the compiler builds it as its own module, so it
// sees none of the including shader's macros or parameters (FresnelF0 is passed in).
//
static const float WATER_UNITS_PER_METER = 40.0;

// The dome's cloud noise (sky_clouds.png: R = masses, G = wisps) — the same texture sky_dome.vmat uses
CreateInputTexture2D( CloudNoise, Linear, 8, "", "", "Clouds,30/10", Default3( 0.5, 0.5, 0.0 ) );
Texture2D g_tCloudNoise < Channel( RGBA, Box( CloudNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
SamplerState g_sCloudSampler < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;
// Last-resort sky when neither a SkyDome nor gradient fog is in the scene: terrainTest's camera background
float3 NoDomeSkyColor < UiType( Color ); Default3( 0.333, 0.463, 0.522 ); UiGroup( "Reflection,15/60" ); >;
// Published scene-wide every frame by SkyDome (same names as sky_dome.shader): the sky the dome is drawing
// right now. SkyDomeActive is 0 until a dome has published once.
float3 g_vSkyZenith < Attribute( "SkyZenith" ); Default3( 0.25, 0.45, 0.85 ); >;
float3 g_vSkyHorizon < Attribute( "SkyHorizon" ); Default3( 0.70, 0.80, 0.95 ); >;
float4 g_vSkySunGlow < Attribute( "SkySunGlow" ); Default4( 1.0, 0.9, 0.7, 0.5 ); >;
float3 g_vCloudLit < Attribute( "CloudLit" ); Default3( 1.0, 1.0, 1.0 ); >;
float3 g_vCloudShade < Attribute( "CloudShade" ); Default3( 0.6, 0.65, 0.75 ); >;
float g_flCloudCover < Attribute( "CloudCover" ); Default( 0.4 ); >;
float g_flCloudScale < Attribute( "CloudScale" ); Default( 0.35 ); >;
float2 g_vCloudOffset < Attribute( "CloudOffset" ); Default2( 0.0, 0.0 ); >;
float3 g_vSunDirection < Attribute( "SunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;
float g_flSkyDomeActive < Attribute( "SkyDomeActive" ); Default( 0.0 ); >;

// Ring sources (TerrainWorldManager.RiverWater TickWaterRipples): xyz = where a pawn's feet were (world units),
// w = when (WaterTrailInfo.y clock, s). Pawns drop one every 0.8 m moved / 0.6 s standing; each expands as a thin
// wobbly white ring (Valheim) — a swimmer's overlapping rings make its wake, and only where it actually went.
#define WATER_TRAIL_CAPACITY 48
#define WATER_RING_LIFE 3.5
StructuredBuffer<float4> g_WaterTrail < Attribute( "WaterTrail" ); >;
float4 g_vWaterTrailInfo < Attribute( "WaterTrailInfo" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

// ---- swimmers
float SwimmerRingStrength < Default( 1.0 ); Range( 0.0, 3.0 ); UiGroup( "Swimmers,27/10" ); >;
float SwimmerFoam < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Swimmers,27/30" ); >;

// Rings of every source near this point: xy = slope (world), z = foam
float3 WaterRings( float3 worldPos )
{
	float3 o = 0;
	int count = (int)g_vWaterTrailInfo.x;
	float now = g_vWaterTrailInfo.y;
	float waterZ = worldPos.z;
	[loop]
	for ( int k = 0; k < count; k++ )
	{
		float4 src = g_WaterTrail[k];
		float age = now - src.w;
		if ( age < 0.0 || age > WATER_RING_LIFE )
			continue;
		float2 d = (worldPos.xy - src.xy) / WATER_UNITS_PER_METER;
		float r = length( d );
		// A ring travels ~0.3–0.9 m/s, slowing as it spreads
		float radius = 0.2 + 0.85 * pow( age, 0.75 );
		if ( r > radius + 0.6 )
			continue;
		// The body (feet .. 72 u up — the Citizen) straddled the water surface when it dropped this source
		float cut = saturate( (waterZ - src.z + 4.0) / 8.0 ) * saturate( (src.z + 72.0 - waterZ) / 6.0 );
		if ( cut <= 0.0 )
			continue;
		float angle = atan2( d.y, d.x );
		float seed = frac( src.w * 7.31 ) * 6.283;
		float wobble = 0.05 * sin( angle * 5.0 + seed ) + 0.03 * sin( angle * 9.0 - seed * 1.7 );
		float x = r - (radius + wobble * (0.5 + age));
		float width = 0.08 + 0.06 * age;   // Valheim's rings are bold lines, ~10 cm
		float crest = exp( -x * x / (width * width) );
		float life = 1.0 - age / WATER_RING_LIFE;
		float amp = life * life * cut;
		float2 radial = d / max( r, 1e-3 );
		// A small swell under the white line, and the line itself breaking up as it ages
		o.xy += radial * (-x / (width * width)) * crest * amp * 0.012 * SwimmerRingStrength;
		o.z = max( o.z, crest * sqrt( amp ) * smoothstep( WATER_RING_LIFE, 0.3, age ) * SwimmerRingStrength );
		// White water at the body while it is still there
		o.z = max( o.z, exp( -r * r * 10.0 ) * saturate( 1.0 - age * 2.5 ) * cut );
	}
	o.z *= SwimmerFoam;
	return o;
}

// Density of both cloud layers at a ceiling uv — identical to sky_dome.shader so the mirrored clouds are the
// clouds overhead: masses carry the shape, wisps break the edges up.
float CloudDensity( float2 uv )
{
	float masses = g_tCloudNoise.Sample( g_sCloudSampler, uv ).r;
	float wisps = g_tCloudNoise.Sample( g_sCloudSampler, uv * 2.3 + g_vCloudOffset * 0.6 + 0.37 ).g;
	return masses * 0.75 + wisps * 0.35;
}

// The sky along a direction, as sky_dome.shader draws it (gradient, sun-side band, halo, clouds; no stars):
// what the water mirrors. Without a dome, the scene's fog colour; without either, the camera background.
float3 SkyReflection( float3 dir )
{
	if ( g_flSkyDomeActive < 0.5 )
		return g_bGradientFogEnabled ? g_vGradientFogColor_Opacity.rgb : NoDomeSkyColor;

	float h = dir.z;
	float3 sunDir = normalize( g_vSunDirection );

	// Gradient: horizon colour hugs the rim, zenith fills the top. A ray below the horizon (a ripple tilted it) sees
	// the horizon colour — the bank occlusion, not this, is what makes low reflections dark.
	float3 sky = lerp( g_vSkyHorizon, g_vSkyZenith, pow( saturate( h ), 0.5 ) );

	// Warm band along the horizon on the sun's side (dawn / dusk does most of its work here).
	float2 flatDir = normalize( dir.xy + 1e-4 );
	float2 flatSun = normalize( sunDir.xy + 1e-4 );
	float sunSide = pow( saturate( dot( flatDir, flatSun ) * 0.5 + 0.5 ), 4.0 );
	float band = 1.0 - saturate( abs( h ) * 4.0 );
	sky = lerp( sky, g_vSkySunGlow.rgb, saturate( g_vSkySunGlow.a * sunSide * band ) );

	// Halo round the sun itself.
	float sunDot = saturate( dot( dir, sunDir ) );
	sky += g_vSkySunGlow.rgb * g_vSkySunGlow.a * ( pow( sunDot, 48.0 ) * 0.8 + pow( sunDot, 6.0 ) * 0.15 );

	// Clouds on a flat ceiling: uv = direction projected through the ceiling plane.
	float ceilingH = max( h, 0.0 ) + 0.08;
	float2 uv = dir.xy / ceilingH * g_flCloudScale + g_vCloudOffset;
	float density = CloudDensity( uv );

	float cover = saturate( g_flCloudCover );
	float edge = 1.0 - cover;
	float cloud = smoothstep( edge, edge + 0.22, density );

	// Fake self-shadow: density a little toward the sun darkens the near side.
	float towardSun = CloudDensity( uv + flatSun * 0.035 );
	float lightAmt = saturate( 1.0 - ( towardSun - edge ) * 2.5 );
	float3 cloudColor = lerp( g_vCloudShade, g_vCloudLit, lightAmt );

	// Silver lining: thin cloud edges near the sun pick up the glow.
	cloudColor += g_vSkySunGlow.rgb * g_vSkySunGlow.a * pow( sunDot, 8.0 ) * ( 1.0 - cloud ) * 0.8;

	float horizonFade = smoothstep( 0.0, 0.22, h );
	sky = lerp( sky, cloudColor, cloud * horizonFade );

	return sky;
}

// The sky seen in a rough surface: far water's ripples are still there, below a pixel, and they blur the mirror — the
// reflection becomes the sky's average around the ray (gradient, cloud cover as a mean tint, no crisp clouds or halo).
// rough 0 = SkyReflection exactly; 1 = fully blurred. (Mark 2026-10-22: from far away and up high the water read as a
// mirror, which felt wrong.)
float3 SkyReflectionRough( float3 dir, float rough )
{
	float3 sharp = SkyReflection( dir );
	if ( rough <= 0.001 || g_flSkyDomeActive < 0.5 )
		return sharp;
	float h = saturate( dir.z + rough * 0.35 );
	float3 gradient = lerp( g_vSkyHorizon, g_vSkyZenith, pow( h, 0.5 ) );
	float3 cloudMean = lerp( g_vCloudShade, g_vCloudLit, 0.55 );
	float3 blurred = lerp( gradient, cloudMean, saturate( g_flCloudCover ) * 0.6 * smoothstep( 0.0, 0.3, h ) );
	return lerp( sharp, blurred, saturate( rough ) );
}

// Fresnel of a rough surface: sub-pixel ripples take a little off the grazing mirror (at most a quarter — the far water
// is still mostly sky; a full falloff turned the distance into a dark band before the far shore)
float FresnelRough( float NdotV, float f0, float rough )
{
	float peak = max( 1.0 - 0.25 * saturate( rough ), f0 );
	return f0 + (peak - f0) * pow( 1.0 - saturate( NdotV ), 5.0 );
}

// ---- caustics: sunlight focused by the ripples onto the bed (Blender/scripts/create_water_caustics.py — light shot
// through a rippled surface and counted where it lands). Two copies drift against each other; their minimum moves like
// the real net does.
CreateInputTexture2D( Caustics, Linear, 8, "", "", "Caustics,28/5", Default4( 0.0, 0.0, 0.0, 1.0 ) );
Texture2D g_tCaustics < Channel( RGBA, Box( Caustics ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
SamplerState g_sCausticsSampler < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;
float CausticStrength < Default( 0.8 ); Range( 0.0, 6.0 ); UiGroup( "Caustics,28/10" ); >;
float CausticScaleMeters < Default( 4.5 ); Range( 0.2, 10.0 ); UiGroup( "Caustics,28/20" ); >;

// Caustic light on the bed at bedMeters (world xy, m) under depthMeters of water: 0..~1, fading out with depth and
// with distance — the lines are a few centimetres wide, so once a pixel covers more than that they blur into the bed's
// own brightness in reality (drawn anyway they turn into a repeating dot texture).
float WaterCaustics( float2 bedMeters, float depthMeters, float t, float footprintMeters )
{
	// Gone well before the net is a few pixels across (Mark 2026-10-22: a speckle carpet over the far sand)
	float distanceFade = 1.0 - smoothstep( 0.01, 0.035, footprintMeters );
	if ( depthMeters < 0.03 || depthMeters > 8.0 || distanceFade <= 0.0 )
		return 0.0;
	float2 uv = bedMeters / CausticScaleMeters;
	float a = g_tCaustics.Sample( g_sCausticsSampler, uv + t * float2( 0.021, 0.013 ) ).r;
	float b = g_tCaustics.Sample( g_sCausticsSampler, uv * 1.17 + float2( 0.37, 0.11 ) - t * float2( 0.017, -0.019 ) ).r;
	float fade = smoothstep( 0.03, 0.3, depthMeters ) * (1.0 - smoothstep( 1.5, 8.0, depthMeters )) * distanceFade;
	return min( a, b ) * fade;
}

// GGX highlight of one light on the water (Kelemen visibility). Fresnel by the half vector.
float SpecularGGX( float3 N, float3 V, float3 L, float alpha, float f0 )
{
	float3 H = normalize( L + V );
	float NdotH = saturate( dot( N, H ) );
	float NdotL = saturate( dot( N, L ) );
	float VdotH = max( dot( V, H ), 0.05 );
	float a2 = alpha * alpha;
	float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
	float D = a2 / (3.14159265 * denom * denom);
	float F = f0 + (1.0 - f0) * pow( 1.0 - VdotH, 5.0 );
	return D * F * NdotL / (4.0 * VdotH * VdotH);
}

// ---- open water (sea + lakes), for surfaces that meet it: published by TerrainWorldManager.SeaWater. The same depth
// as seawater.shader's SeaDepthMeters (keep the two in step): 0 on land, the carved depth over lakes and the sea.
float4 g_vOpenWaterShape < Attribute( "SeaShape" ); Default4( 20000.0, 20000.0, 300.0, 900.0 ); >;
float4 g_vOpenWaterLake < Attribute( "SeaLake" ); Default4( 2.0, 8.0, 100.0, 1500.0 ); >;
float4 g_vOpenWaterShelf < Attribute( "SeaShelf" ); Default4( 30.0, 700.0, 0.0, 0.0 ); >;
float4 g_vOpenWaterClock < Attribute( "SeaClock" ); Default4( 0.0, 2.5, 512.0, 0.0 ); >;
Texture2D g_tOpenWaterLakeMask < Attribute( "SeaLakeMask" ); SrgbRead( false ); >;
SamplerState g_sOpenWaterMaskSampler < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// The lake mask is R32F, which GPUs do not filter: a "bilinear" sampler on it returned the nearest texel and the
	// waterline drew the raster cells as right-angle stairs (2026-10-22). Bilinear by hand, texel centres, 0 outside —
	// exactly TerrainPreviewLandDiskFields.SampleLakeMaskGridBilinear.
	float OpenWaterMaskBilinearTexel( int2 p, int2 size )
	{
		if ( p.x < 0 || p.y < 0 || p.x >= size.x || p.y >= size.y )
			return 0.0;
		return g_tOpenWaterLakeMask.Load( int3( p, 0 ) ).r;
	}
	float OpenWaterMaskBilinear( float2 uv )
	{
		uint w, h;
		g_tOpenWaterLakeMask.GetDimensions( w, h );
		int2 size = int2( w, h );
		float2 f = uv * float2( size ) - 0.5;
		float2 i0 = floor( f );
		float2 t = f - i0;
		int2 a = int2( i0 );
		float m0 = lerp( OpenWaterMaskBilinearTexel( a, size ), OpenWaterMaskBilinearTexel( a + int2( 1, 0 ), size ), t.x );
		float m1 = lerp( OpenWaterMaskBilinearTexel( a + int2( 0, 1 ), size ), OpenWaterMaskBilinearTexel( a + int2( 1, 1 ), size ), t.x );
		return lerp( m0, m1, t.y );
	}

float OpenWaterDepthMeters( float2 xyMeters )
{
	if ( g_vOpenWaterClock.w < 0.5 )
		return 0.0;   // no sea surface published: nothing to meet
	float worldRadius = g_vOpenWaterShape.x;
	float landRadius = g_vOpenWaterShape.y;
	float dist = length( xyMeters );
	if ( dist > landRadius )
	{
		float past = dist - landRadius;
		float t = saturate( past / max( 1.0, g_vOpenWaterShape.w ) );
		float d = saturate( (past - g_vOpenWaterShape.w) / max( 1.0, g_vOpenWaterShelf.y ) );
		return g_vOpenWaterShelf.x * t * (2.0 - t) + max( g_vOpenWaterShape.z - g_vOpenWaterShelf.x, 0.0 ) * d * d * (3.0 - 2.0 * d);   // TerrainPreviewPipeline.OceanDepthMeters
	}
	float diameter = worldRadius * 2.0;
	float2 uv = float2( 1.0 - (xyMeters.x + worldRadius) / diameter, (xyMeters.y + worldRadius) / diameter );
	float mask = OpenWaterMaskBilinear( uv );
	float excess = mask - g_vOpenWaterLake.x;
	if ( excess <= 0.0 )
		return 0.0;
	// Distance in from the shore = excess over the mask gradient (TerrainPreviewPipeline.MetersIntoLake), then the shelf
	float2 maskSize;
	g_tOpenWaterLakeMask.GetDimensions( maskSize.x, maskSize.y );
	float texel = 1.0 / max( maskSize.x, 2.0 );   // one raster cell (see seawater.shader)
	float2 du = float2( texel, 0.0 );
	float2 dv = float2( 0.0, texel );
	float gx = OpenWaterMaskBilinear( uv - du ) - OpenWaterMaskBilinear( uv + du );
	float gy = OpenWaterMaskBilinear( uv + dv ) - OpenWaterMaskBilinear( uv - dv );
	float grad = length( float2( gx, gy ) ) / (2.0 * texel * worldRadius * 2.0);   // per metre
	float into = grad > 1e-7 ? excess / grad : 1e6;
	float t = saturate( into / max( 1.0, g_vOpenWaterLake.z ) );
	return g_vOpenWaterLake.y * t * (2.0 - t);
}

// ---- foam lace: what real foam looks like up close — a net of bubbles with holes, never a tiled picture. Domain-warped,
// rotated cloud noise at three unrelated scales (metres in), so no tile repeats in view. 0..1, about uniform.
float FoamLace( float2 p, float t )
{
	float2 warp = float2(
		g_tCloudNoise.Sample( g_sCloudSampler, p * 0.031 + float2( 0.17, 0.61 ) + t * 0.004 ).g,
		g_tCloudNoise.Sample( g_sCloudSampler, p * 0.027 + float2( 0.53, 0.29 ) - t * 0.003 ).r ) - 0.5;
	float2 q = p + warp * 7.0;
	float2 q1 = float2( q.x * 0.866 - q.y * 0.5, q.x * 0.5 + q.y * 0.866 );
	float2 q2 = float2( q.x * 0.342 + q.y * 0.940, -q.x * 0.940 + q.y * 0.342 );
	float a = g_tCloudNoise.Sample( g_sCloudSampler, q1 * 0.173 + float2( t * 0.011, 0.0 ) ).r;
	float b = g_tCloudNoise.Sample( g_sCloudSampler, q2 * 0.461 - float2( 0.0, t * 0.017 ) ).g;
	float c = g_tCloudNoise.Sample( g_sCloudSampler, q * 1.237 + 0.41 ).r;
	return saturate( (a * 0.5 + b * 0.33 + c * 0.17 - 0.5) * 1.8 + 0.5 );
}

// Foam of a given amount (0 none … 1 solid) through the lace: thick foam is solid white, thinning foam breaks into
// wisps and bubbles and then holes — how foam actually dissolves.
float FoamThroughLace( float amount, float lace )
{
	float edge = 1.0 - saturate( amount );
	return smoothstep( edge, edge + 0.18, lace ) * saturate( amount * 3.0 );
}
