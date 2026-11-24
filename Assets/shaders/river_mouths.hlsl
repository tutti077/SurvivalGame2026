//
// River mouths (Mark 2026-10-24/25: "the rivers … need to blend together" with the ocean / lakes). Where a river crosses
// into a lake or the sea (TerrainWorldManager.PublishRiverMouths) the LAKE / SEA surface owns the water: it reaches up the
// river channel over a hand-over stretch (1.5 river widths, 20–60 m) and fades in there while the river surface — which
// stops at the shoreline — fades out. One water body, no strip edges sticking out, the colour changing over tens of
// metres like a real river mixing into a lake. Both water shaders include this (the sea in its vertex stage too).
//
// Per mouth, two float4: ( x, y, flow dir x, flow dir y ) in metres, ( half width m, bed depth m, hand-over m, open-water level m ).
//
#define RIVER_MOUTH_CAPACITY 64
StructuredBuffer<float4> g_RiverMouths < Attribute( "RiverMouths" ); >;
float4 g_vRiverMouthInfo < Attribute( "RiverMouthInfo" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;   // x = count

// x = the sea's share of the water here (1 everywhere but a mouth's hand-over stretch), y = the river channel's carved
// depth here (m, 0 outside a mouth's channel), z = how much this point is inside a mouth's channel (0..1), w = the
// river's hand-over progress (0 upstream … 1 at the shoreline and beyond)
float4 RiverMouthAt( float2 pMeters )
{
	float4 best = float4( 1.0, 0.0, 0.0, 1.0 );
	int count = min( (int)g_vRiverMouthInfo.x, RIVER_MOUTH_CAPACITY );
	[loop]
	for ( int k = 0; k < count; k++ )
	{
		float4 a = g_RiverMouths[k * 2];
		float4 b = g_RiverMouths[k * 2 + 1];
		float handOver = max( b.z, 1.0 );
		float2 d = pMeters - a.xy;
		float along = dot( d, a.zw );
		if ( along < -handOver - 4.0 || along > handOver )
			continue;
		float halfW = max( b.x, 0.5 );
		float u = abs( d.x * a.w - d.y * a.z ) / halfW;
		float side = 1.0 - smoothstep( 0.8, 1.15, u );
		if ( side <= best.z )
			continue;
		float progress = smoothstep( -handOver, 0.0, along );
		float depth = u < 1.0 ? b.y * (1.0 - u * u) : 0.0;
		best = float4( lerp( 1.0, progress, side ), depth, side, progress );
	}
	return best;
}

// How much the RIVER surface has faded out here (0 = all river, 1 = gone), for a river surface at surfaceMeters. Over the
// hand-over stretch it fades only where the lake / sea surface can really take over - where the river is down at that
// level. A river that comes down a slope into the lake is still above it there: faded by distance alone, it left a band
// of dry bed between the two waters (Mark 2026-11-18). Such a river keeps its surface to the shoreline and thins out over
// the last 3 m. x = the fade, y = inside a mouth's channel (0..1).
float2 RiverMouthRiverFade( float2 pMeters, float surfaceMeters )
{
	float2 best = float2( 1.0, 0.0 );
	int count = min( (int)g_vRiverMouthInfo.x, RIVER_MOUTH_CAPACITY );
	[loop]
	for ( int k = 0; k < count; k++ )
	{
		float4 a = g_RiverMouths[k * 2];
		float4 b = g_RiverMouths[k * 2 + 1];
		float handOver = max( b.z, 1.0 );
		float2 d = pMeters - a.xy;
		float along = dot( d, a.zw );
		if ( along < -handOver - 4.0 || along > handOver )
			continue;
		float halfW = max( b.x, 0.5 );
		float u = abs( d.x * a.w - d.y * a.z ) / halfW;
		float side = 1.0 - smoothstep( 0.8, 1.15, u );
		if ( side <= best.y )
			continue;
		float covered = 1.0 - smoothstep( 0.15, 0.6, surfaceMeters - b.w );
		float fade = max( smoothstep( -handOver, 0.0, along ) * covered, smoothstep( -3.0, 0.0, along ) );
		best = float2( fade, side );
	}
	return best;
}

// How much shore foam a lake / sea surface may draw here: none along a river's corridor, from up the channel to a
// hand-over length out past the shoreline (the old shoreline runs straight across every mouth, and its waterline foam
// drew a line of white bits out in the water — Mark 2026-10-25), the corridor widening to 1.6 half widths
float RiverMouthFoamKeep( float2 pMeters )
{
	float keep = 1.0;
	int count = min( (int)g_vRiverMouthInfo.x, RIVER_MOUTH_CAPACITY );
	[loop]
	for ( int k = 0; k < count; k++ )
	{
		float4 a = g_RiverMouths[k * 2];
		float4 b = g_RiverMouths[k * 2 + 1];
		float handOver = max( b.z, 1.0 );
		float2 d = pMeters - a.xy;
		float along = dot( d, a.zw );
		if ( along < -handOver - 4.0 || along > handOver * 1.5 )
			continue;
		float halfW = max( b.x, 0.5 ) * 1.6;
		float across = abs( d.x * a.w - d.y * a.z );
		float inside = (1.0 - smoothstep( halfW * 0.8, halfW, across )) * (1.0 - smoothstep( handOver, handOver * 1.5, along ));
		keep = min( keep, 1.0 - inside );
	}
	return keep;
}
