//
// pixel_lit — standard lit surface that samples its colour texture with POINT filtering.
//
// The engine's complex.shader samples bilinearly no matter what the vmat asks for, so a true
// 64x64 texture turned into a smooth blur in game and the generated textures were written as
// 512x512 nearest-neighbour block images instead. This shader keeps the real 64x64 (or 128x128)
// files and shows hard texels at any distance: colour + cutout from one point-sampled texture,
// flat material parameters, PBR-style lighting.
//
// Per-texel lighting (PixelTexelLighting, default on — the Valheim sconce look): direct light is
// evaluated once per TEXEL, not per screen pixel. The shading point and every shadow lookup are
// moved to the centre of the texel under the pixel (from the screen-space derivatives of position
// and uv), and the normal is already per texel (flat facet + point-sampled normal map), so a texel
// is lit as one block — it glows or it does not — while falloff between texels stays smooth.
// PixelLightSteps posterises local lights into brightness bands on top of that (default 6, so the
// fall-off from a torch goes 0 % / 17 % / 33 % … in texel-edged steps, not a smooth glow);
// PixelLightCutoff and the sun's own PixelSunSteps default 0 = off. Indirect light (sky, cubemaps, AO) stays the
// engine's own, through ShadingModelStandard's combiner.
//
// Used by the generated elm bark / end grain / leaf-card materials (Blender/scripts/create_elm_tree.py),
// the build-kit wood, weapons and the buggy atlas.
//
// Notes for anyone editing: F_RENDER_BACKFACES is defined by the engine for every shader (do not
// redeclare it), and the cutout is a plain clip() under its own combo rather than S_ALPHA_TEST,
// whose shading-model path expects the complex shader's material inputs. Parameter names carry a
// Pixel prefix: a plain 'Roughness' collided with the engine's struct of that name. Derivatives
// (ddx / ddy, ComputeShadowReceiverNormal) must run before the light loop, in uniform control flow.
//
HEADER
{
	Description = "Lit surface, point-sampled pixel-art texture, per-texel lighting, optional alpha cutout";
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
	#include "common/classes/Light.hlsl"

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

	// --- Per-texel lighting ("Lighting" group) ---
	// 1 = direct light is evaluated at the texel centre so every texel is one lit block; 0 = the engine's per-pixel standard model.
	float PixelTexelLighting < Default( 1.0 ); Range( 0.0, 1.0 ); UiGroup( "Lighting,20/10" ); >;
	// Local lights (torch, sconce, campfire): N brightness levels (6 = 0 %, 17 %, 33 % … bands whose edges
	// sit on texel boundaries — the boxy Valheim fall-off); 0 = smooth falloff between texels.
	float PixelLightSteps < Default( 6.0 ); Range( 0.0, 16.0 ); UiGroup( "Lighting,20/20" ); >;
	// Local lights: a texel receiving less than this fraction of the light shows none of it.
	float PixelLightCutoff < Default( 0.0 ); Range( 0.0, 1.0 ); UiGroup( "Lighting,20/30" ); >;
	// Sun / moon: 0 = smooth (default — the sun should not band); N = N levels.
	float PixelSunSteps < Default( 0.0 ); Range( 0.0, 16.0 ); UiGroup( "Lighting,20/40" ); >;

	float QuantizeLight( float f, float steps, float cutoff )
	{
		if ( cutoff > 0.0 && f < cutoff )
			return 0.0;
		if ( steps >= 1.0 )
			f = floor( f * steps + 0.5 ) / steps;
		return f;
	}

	// World position of the centre of the texel under this pixel, on the surface plane: solve the
	// 2x2 screen-derivative system for the pixel offset that lands on the texel centre, then walk
	// the position derivatives by that offset. Call in uniform control flow (uses ddx / ddy).
	float3 TexelCentreWs( float3 vPositionWs, float2 vUv )
	{
		float2 vTexSize;
		g_tColor.GetDimensions( vTexSize.x, vTexSize.y );
		vTexSize = max( vTexSize, 1.0 );

		float2 vUvCentre = ( floor( vUv * vTexSize ) + 0.5 ) / vTexSize;
		float2 dUv = vUvCentre - vUv;

		float2 dUvDx = ddx( vUv );
		float2 dUvDy = ddy( vUv );
		float3 dPDx = ddx( vPositionWs );
		float3 dPDy = ddy( vPositionWs );

		float det = dUvDx.x * dUvDy.y - dUvDy.x * dUvDx.y;
		if ( abs( det ) < 1e-14 )
			return vPositionWs;

		float a = ( dUv.x * dUvDy.y - dUvDy.x * dUv.y ) / det;
		float b = ( dUvDx.x * dUv.y - dUv.x * dUvDx.y ) / det;
		// A texel seen edge-on can be hundreds of pixels long; cap the walk so a degenerate quad never flies off.
		a = clamp( a, -256.0, 256.0 );
		b = clamp( b, -256.0, 256.0 );
		return vPositionWs + dPDx * a + dPDy * b;
	}

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

		// Legacy per-pixel path: the engine's own standard model, untouched.
		if ( PixelTexelLighting < 0.5 )
			return ShadingModelStandard::Shade( i, m );

		// ---- Per-texel direct lighting ----
		// Everything derivative-based happens here, before any divergent branch.
		float3 vShadePosWs = TexelCentreWs( m.WorldPosition, vUv );
		float3 vReceiverNormalWs = ComputeShadowReceiverNormal( m.WorldPosition );

		Decals::Apply( m.WorldPosition, m );

		float3 N = m.Normal;
		float3 V = normalize( g_vCameraPositionWs - m.WorldPosition );
		float flRough = saturate( m.Roughness );
		// Modest Blinn-Phong highlight — bark and planks are rough, so this mostly matters on wet-looking props.
		float flGloss = lerp( 96.0, 4.0, flRough );
		float flSpecWeight = ( 1.0 - flRough ) * 0.12;

		float3 vDirectDiffuse = 0.0;
		float3 vDirectSpecular = 0.0;

		// Sun / moon through the directional constants (the engine's fast path).
		bool bSunDone = false;
		if ( g_DirectionalLightEnabled )
		{
			bSunDone = true;
			float3 L = normalize( -g_DirectionalLightDirection.xyz );
			float flNdotL = saturate( dot( N, L ) );
			float flVis = 1.0;
			if ( g_DirectionalLightCascadeCount > 0 )
				flVis = DirectionalLightShadow::GetVisibility( vShadePosWs, vReceiverNormalWs, i.vPositionSs );

			float f = QuantizeLight( flNdotL * flVis, PixelSunSteps, 0.0 );
			vDirectDiffuse += g_DirectionalLightColor.rgb * f;
			float3 H = normalize( L + V );
			vDirectSpecular += g_DirectionalLightColor.rgb * ( pow( saturate( dot( N, H ) ), flGloss ) * flSpecWeight * f );
		}

		// Local lights from the cluster list (plus baked ones when a probe / lightmap is bound).
		uint nLightCount = Light::Count( i.vPositionSs );
		[loop]
		for ( uint n = 0; n < nLightCount; n++ )
		{
			Light light = Light::From( vShadePosWs, i.vPositionSs, n, i.vLightmapUV, vReceiverNormalWs );
			bool bDirectional = light.LightData.Type == LightType::LightTypeDirectional;
			if ( bDirectional && bSunDone )
				continue; // already lit through the constants above

			if ( !bDirectional )
			{
				float3 vToLight = light.Position - vShadePosWs;
				if ( dot( vToLight, vToLight ) > light.LightData.RadiusSquared )
					continue;
			}

			float flNdotL = saturate( dot( N, light.Direction ) );
			float f = flNdotL * light.Attenuation * light.Visibility;
			f = bDirectional
				? QuantizeLight( f, PixelSunSteps, 0.0 )
				: QuantizeLight( f, PixelLightSteps, PixelLightCutoff );
			if ( f <= 0.0 )
				continue;

			vDirectDiffuse += light.Color * f;
			float3 H = normalize( light.Direction + V );
			vDirectSpecular += light.Color * ( pow( saturate( dot( N, H ) ), flGloss ) * flSpecWeight * f );
		}

		// ---- Indirect light from the engine (sky ambient, cubemaps, AO) ----
		CombinerInput ci = ShadingModelStandard::MaterialToCombinerInput( m );
		LightingTerms_t lt = InitLightingTerms();
		CalculateIndirectLighting( lt, ci );

		float3 vDiffuseAO = CalculateDiffuseAmbientOcclusion( ci, lt );
		float3 vSpecularAO = CalculateSpecularAmbientOcclusion( ci, lt );
		lt.vIndirectDiffuse.rgb *= vDiffuseAO;
		lt.vIndirectSpecular.rgb *= vSpecularAO;
		vDirectDiffuse *= lerp( float3( 1.0, 1.0, 1.0 ), vDiffuseAO, ci.flAmbientOcclusionDirectDiffuse );

		float3 vColor = ( vDirectDiffuse + lt.vIndirectDiffuse.rgb ) * ci.vDiffuseColor.rgb
		              + vDirectSpecular + lt.vIndirectSpecular.rgb + ci.vEmissive.rgb;

		if ( DepthNormals::WantsDepthNormals() )
			return DepthNormals::Output( m.Normal, m.Roughness, 1.0 );

		return DoAtmospherics( m.WorldPosition, m.ScreenPosition.xy, float4( vColor, 1.0 ) );
	}
}
