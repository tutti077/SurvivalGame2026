//
// pixel_lit — standard lit surface that samples its colour texture with POINT filtering.
//
// The engine's complex.shader samples bilinearly no matter what the vmat asks for, so a true
// 64x64 texture turned into a smooth blur in game and the generated textures were written as
// 512x512 nearest-neighbour block images instead. This shader keeps the real 64x64 (or 128x128)
// files and shows hard texels at any distance: colour + cutout from one point-sampled texture,
// flat material parameters, the standard PBR lighting path (same as grass_blade.shader).
//
// Used by the generated elm bark / end grain / leaf-card materials (Blender/scripts/create_elm_tree.py).
//
// Notes for anyone editing: F_RENDER_BACKFACES is defined by the engine for every shader (do not
// redeclare it), and the cutout is a plain clip() under its own combo rather than S_ALPHA_TEST,
// whose shading-model path expects the complex shader's material inputs. Parameter names carry a
// Pixel prefix: a plain 'Roughness' collided with the engine's struct of that name.
//
HEADER
{
	Description = "Lit surface, point-sampled pixel-art texture, optional alpha cutout";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_ALPHA_TEST, 0..1, "Rendering" );
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

	StaticCombo( S_PIXEL_CUTOUT, F_ALPHA_TEST, Sys( ALL ) );

	RenderState( CullMode, F_RENDER_BACKFACES ? NONE : DEFAULT );

	CreateInputTexture2D( TextureColor, Srgb, 8, "", "_color", "Material,10/10", Default3( 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( TextureTranslucency, Linear, 8, "", "_trans", "Material,10/20", Default( 1.0 ) );
	// Optional tangent-space normal map, point-sampled like the colour: every texel on a flat facet gets
	// its own tilt, so the sun catches texels differently inside one face (Valheim-style per-pixel light).
	// The default flat normal leaves materials without a map untouched.
	CreateInputTexture2D( TextureNormal, Linear, 8, "NormalizeNormals", "_normal", "Material,10/70", Default3( 0.5, 0.5, 1.0 ) );

	// RGB = colour, A = cutout mask. Uncompressed: a 64x64 pixel-art tile is tiny anyway and
	// block compression would smear its hard edges.
	Texture2D g_tColor < Channel( RGB, Box( TextureColor ), Srgb ); Channel( A, Box( TextureTranslucency ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( true ); >;

	// The whole point of this shader: nearest texel, no blending between texels.
	Texture2D g_tNormal < Channel( RGB, Box( TextureNormal ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	// Optional per-texel roughness (multiplied with PixelRoughness; default 1 = flat roughness).
	CreateInputTexture2D( TextureRoughness, Linear, 8, "", "_rough", "Material,10/35", Default( 1.0 ) );
	Texture2D g_tRough < Channel( R, Box( TextureRoughness ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	SamplerState g_sPointSampler < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;

	float PixelRoughness < Default( 0.9 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/30" ); >;
	float PixelAlphaCutoff < Default( 0.5 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/40" ); >;
	// Leaf cards: blend the card normal toward world up so a crown shades as a soft mass, not flickering planes.
	float PixelNormalUp < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/50" ); >;
	// 1 = ignore the smooth vertex normals and light every triangle flat (faceted, low-poly look); 0 = smooth.
	float PixelFlatShading < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Material,10/60" ); >;
	// Scales the normal-map tilt (0 = ignore the map, 1 = as authored).
	float PixelNormalStrength < Default( 1.0 ); Range( 0.0, 3.0 ); UiGroup( "Material,10/80" ); >;
	// Tiling: texture repeats per UV unit (dev meshes map 0..1 across the whole face).
	float2 PixelTexCoordScale < Default2( 1.0, 1.0 ); Range2( 0.01, 0.01, 500.0, 500.0 ); UiGroup( "Material,10/90" ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 vUv = i.vTextureCoords.xy * PixelTexCoordScale;
		float4 tex = g_tColor.Sample( g_sPointSampler, vUv );

		#if ( S_PIXEL_CUTOUT )
			clip( tex.a - PixelAlphaCutoff );
		#endif

		Material m = Material::Init( i );

		// Double-sided cards: face the normal toward the camera so the back of a card is lit
		// like its front (the usual SV_IsFrontFace input is owned by the common pixel input).
		float3 vNormal = normalize( i.vNormalWs );
		float3 vPositionWs = i.vPositionWithOffsetWs + g_vCameraPositionWs;

		// Faceted: the plane normal of the triangle itself from screen-space derivatives, signed like the vertex normal.
		float3 vFlatNormal = normalize( cross( ddy( vPositionWs ), ddx( vPositionWs ) ) );
		if ( dot( vFlatNormal, vNormal ) < 0.0 )
			vFlatNormal = -vFlatNormal;
		vNormal = normalize( lerp( vNormal, vFlatNormal, saturate( PixelFlatShading ) ) );

		float3 vToCamera = g_vCameraPositionWs - vPositionWs;
		if ( dot( vNormal, vToCamera ) < 0.0 )
			vNormal = -vNormal;

		// Per-texel tilt from the normal map, in the tangent frame of the (possibly flattened) face normal.
		float3 vNormalTs = DecodeNormal( g_tNormal.Sample( g_sPointSampler, vUv ).rgb );
		vNormalTs.xy *= PixelNormalStrength;
		vNormal = TransformNormal( normalize( vNormalTs ), vNormal, i.vTangentUWs, i.vTangentVWs );

		m.Normal = normalize( lerp( vNormal, float3( 0.0, 0.0, 1.0 ), PixelNormalUp ) );

		m.Albedo = tex.rgb;
		m.Metalness = 0.0;
		m.Roughness = saturate( PixelRoughness * g_tRough.Sample( g_sPointSampler, vUv ).r );
		m.AmbientOcclusion = 1.0;
		m.Opacity = 1.0;

		return ShadingModelStandard::Shade( i, m );
	}
}
