// Hub surfaces (Code/Hub, Tools/hub/generate_hub.py): the hub is built from dev boxes of every size, whose 0..1 face UVs
// would stretch a texture with the box scale. This shader ignores the mesh UVs and projects the texture on the world
// planes the face points along (box faces are axis-aligned, so one projection per face, no blend), one repeat per
// HubTileMeters. Point sampled like pixel_lit (the pixel look is asset-side) while a texel covers a screen pixel or more;
// once the texture is minified (far walls, grazing floor) it fades over one mip level to an anisotropic mip-mapped
// sample, so the thin neon lines average out instead of breaking into moire (Mark 2026-11-10). TextureGlow is added on top of the lit
// colour as emission, so the neon lines read at night and through fog; the panels around them are lit normally by the
// engine's standard model (sun, shadows, AO).
HEADER
{
	Description = "Hub surface: world-projected point-sampled tile + emissive glow layer";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
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

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureGlow, Srgb, 8, "", "_glow", "Material,10/20", Default3( 0.0, 0.0, 0.0 ) );
	Texture2D g_tColor < Channel( RGB, Box( TextureColor ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	Texture2D g_tGlow < Channel( RGB, Box( TextureGlow ), Srgb ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;
	SamplerState g_sPointSampler < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sFarSampler < Filter( ANISOTROPIC ); MaxAniso( 16 ); AddressU( WRAP ); AddressV( WRAP ); >;

	// One texture repeat per this many metres, on every face.
	float HubTileMeters < Default( 4.0 ); Range( 0.25, 64.0 ); UiGroup( "Material,10/30" ); >;
	// Emission multiplier on TextureGlow (0 = no glow).
	float HubGlow < Default( 2.0 ); Range( 0.0, 16.0 ); UiGroup( "Material,10/40" ); >;
	float HubRoughness < Default( 0.8 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/50" ); >;
	float HubMetalness < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/60" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 vPositionWs = i.vPositionWithOffsetWs + g_vCameraPositionWs;
		float3 vN = normalize( i.vNormalWs );
		float3 vA = abs( vN );
		float flTile = max( HubTileMeters, 0.01 ) * 40.0;
		// Pick the plane the face looks along; mirror on the back faces so text-free tiles never read flipped.
		float2 vUv = vA.z >= max( vA.x, vA.y )
			? vPositionWs.xy / flTile
			: ( vA.x >= vA.y ? float2( vPositionWs.y * sign( vN.x ), -vPositionWs.z ) : float2( -vPositionWs.x * sign( vN.y ), -vPositionWs.z ) ) / flTile;

		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( vN, HubRoughness, 1.0 );

		// Texture LOD: texels per screen pixel along the most-minified axis. <= 1 texel: point (crisp pixels up close);
		// >= 2 texels: the filtered mips (coverage-correct lines far away); in between a blend.
		uint2 vDim = TextureDimensions2D( g_tGlow, 0 );
		float2 vTexel = vUv * float2( vDim );
		float flRho = max( length( ddx( vTexel ) ), length( ddy( vTexel ) ) );
		float flFar = saturate( log2( max( flRho, 1e-4 ) ) );

		Material m = Material::Init( i );
		m.Albedo = lerp( g_tColor.Sample( g_sPointSampler, vUv ).rgb, g_tColor.Sample( g_sFarSampler, vUv ).rgb, flFar );
		m.Normal = vN;
		m.Roughness = HubRoughness;
		m.Metalness = HubMetalness;
		m.AmbientOcclusion = 1.0;
		m.Opacity = 1.0;
		m.Emission = lerp( g_tGlow.Sample( g_sPointSampler, vUv ).rgb, g_tGlow.Sample( g_sFarSampler, vUv ).rgb, flFar ) * HubGlow;
		return ShadingModelStandard::Shade( i, m );
	}
}
