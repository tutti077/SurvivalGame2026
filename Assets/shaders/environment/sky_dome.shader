//
// sky_dome — Valheim-style painted sky on a camera-centred sphere (Code/World/Environment/SkyDome.cs).
//
// Everything is driven per frame from C# through renderer attributes, which EnvironmentDayNightCycle
// samples from the active preset in data/sky_presets.json:
//   SkyZenith / SkyHorizon      gradient, horizon → straight up
//   SkySunGlow (rgb, a=strength) halo round the sun + warm horizon band on the sun's side
//   CloudLit / CloudShade        cloud colour facing / away from the sun
//   CloudCover (0–1)             how much of the noise becomes cloud
//   CloudScale / CloudOffset     projection size + wind-integrated scroll (uv units, wraps at 1)
//   SunDirection                 unit vector toward the sun
//   StarVisibility (0–1)         star layer fade: 0 by day, 1 deep in the night
//
// Stars are procedural: the view direction is hashed on a 3D grid, one star per cell that clears a
// threshold, denser along a tilted Milky Way band. They sit under the clouds, so cloud covers them.
//
// Clouds are two layers of one tileable noise (sky_clouds.png: R = masses, G = wisps, from
// Blender/scripts/create_sky_clouds.py) projected onto a flat ceiling, faded out at the horizon.
// No fog: the dome IS the far colour.
//
HEADER
{
	Description = "Sky dome — gradient, sun glow, procedural stars, two wind-scrolled cloud layers.";
	DevShader = true;
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
	#include "common/utils/Material.CommonInputs.hlsl"
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	CreateInputTexture2D( CloudNoise, Linear, 8, "", "", "Clouds,10/10", Default3( 0.5, 0.5, 0.0 ) );
	Texture2D g_tCloudNoise < Channel( RGBA, Box( CloudNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sCloudSampler < Filter( BILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	float3 g_vSkyZenith < Attribute( "SkyZenith" ); Default3( 0.25, 0.45, 0.85 ); >;
	float3 g_vSkyHorizon < Attribute( "SkyHorizon" ); Default3( 0.70, 0.80, 0.95 ); >;
	float4 g_vSkySunGlow < Attribute( "SkySunGlow" ); Default4( 1.0, 0.9, 0.7, 0.5 ); >;
	float3 g_vCloudLit < Attribute( "CloudLit" ); Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vCloudShade < Attribute( "CloudShade" ); Default3( 0.6, 0.65, 0.75 ); >;
	float g_flCloudCover < Attribute( "CloudCover" ); Default( 0.4 ); >;
	float g_flCloudScale < Attribute( "CloudScale" ); Default( 0.35 ); >;
	float2 g_vCloudOffset < Attribute( "CloudOffset" ); Default2( 0.0, 0.0 ); >;
	float3 g_vSunDirection < Attribute( "SunDirection" ); Default3( 0.0, 0.0, 1.0 ); >;
	float g_flStarVisibility < Attribute( "StarVisibility" ); Default( 0.0 ); >;

	float Hash13( float3 p )
	{
		p = frac( p * 0.1031 );
		p += dot( p, p.zyx + 31.32 );
		return frac( ( p.x + p.y ) * p.z );
	}

	float3 Hash33( float3 p )
	{
		p = frac( p * float3( 0.1031, 0.1030, 0.0973 ) );
		p += dot( p, p.yxz + 33.33 );
		return frac( ( p.xxy + p.yxx ) * p.zyx );
	}

	// One star per grid cell that clears the threshold. Rarer hashes = bigger, brighter stars; the
	// threshold drops inside the Milky Way band so it reads as a denser river of small stars.
	float3 Stars( float3 dir )
	{
		const float Cells = 220.0;            // ~0.26° per cell: a star is 1–2 px at 80° FOV, 1080p
		float3 p = dir * Cells;
		float3 cell = floor( p );
		float3 centre = cell + 0.3 + Hash33( cell ) * 0.4;   // stays clear of the cell walls
		float d = length( p - centre );

		float3 galaxyUp = normalize( float3( 0.0, 0.848, 0.530 ) );   // band tilted ~58° off the zenith
		float band = exp( -pow( dot( dir, galaxyUp ) / 0.14, 2.0 ) );
		float threshold = lerp( 0.975, 0.90, band );

		float seed = Hash13( cell + 7.0 );
		float isStar = step( threshold, seed );
		float bright = saturate( ( seed - threshold ) / ( 1.0 - threshold ) );
		float radius = lerp( 0.12, 0.28, bright * bright );
		float disc = 1.0 - smoothstep( radius * 0.5, radius, d );

		float tint = Hash13( cell + 13.0 );
		float3 colour = tint < 0.12 ? float3( 1.0, 0.86, 0.62 ) : ( tint < 0.30 ? float3( 0.72, 0.82, 1.0 ) : float3( 1.0, 1.0, 1.0 ) );
		return colour * ( disc * isStar * lerp( 0.35, 1.1, bright ) );
	}

	// Density of both layers at a ceiling uv: masses carry the shape, wisps break the edges up.
	float CloudDensity( float2 uv )
	{
		float masses = g_tCloudNoise.Sample( g_sCloudSampler, uv ).r;
		float wisps = g_tCloudNoise.Sample( g_sCloudSampler, uv * 2.3 + g_vCloudOffset * 0.6 + 0.37 ).g;
		return masses * 0.75 + wisps * 0.35;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 dir = normalize( i.vPositionWithOffsetWs.xyz );
		float h = dir.z;
		float3 sunDir = normalize( g_vSunDirection );

		// Gradient: horizon colour hugs the rim, zenith fills the top.
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

		// Stars: faded by the cycle, gone at the rim, and drawn before the clouds so cloud covers them.
		sky += Stars( dir ) * ( g_flStarVisibility * smoothstep( 0.0, 0.15, h ) );

		// Below the horizon: darken toward the ground so a gap in terrain does not glow.
		sky = lerp( sky, g_vSkyHorizon * 0.55, saturate( -h * 4.0 ) );

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

		return float4( sky, 1.0 );
	}
}
