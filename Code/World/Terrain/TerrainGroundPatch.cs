namespace Survival;

/// <summary>
/// Ground the world generator lays down rather than the hoe: today the dark tilled soil under every wild-wheat cluster
/// (Mark 2026-11-02: "can you make the wheat spawn on tilled soil?"). One patch = a centre, a radius and a hashed rim
/// wobble, all in world metres; <see cref="Coverage"/> is the one rule every reader uses, so the pieces always agree:
/// <list type="bullet">
/// <item>the heightfield chunk's splat (<see cref="TerrainGroundSplat.ApplyTilledPatches"/>: sand + darkening channel,
/// the same contract as the hoe's tilled paint on voxel ground);</item>
/// <item>a voxel column made on that chunk (<see cref="VoxelField.SeedTilled"/> — the patch becomes real tilled paint);</item>
/// <item>the world grass (no clumps from 0.5 coverage up) and farming (<see cref="FarmingRules.IsTilledGroundAt"/>
/// sows from 0.5 up through <c>TerrainWorldManager.TilledAt</c>).</item>
/// </list>
/// Planned per chunk by <see cref="TerrainVegetationScatter.PlanGroundPatches"/> from the scatter's own cluster cells.
/// </summary>
public readonly struct TerrainGroundPatch
{
	public float X { get; init; }
	public float Y { get; init; }
	/// <summary>Mean rim radius (m) — the cluster radius plus the cluster's tilled pad.</summary>
	public float RadiusMeters { get; init; }
	/// <summary>Rim wobble phase (radians), hashed per patch so no two patches share an outline.</summary>
	public float Phase { get; init; }

	/// <summary>Soft edge: coverage eases 1 → 0 across this width at the rim.</summary>
	public const float EdgeMeters = 0.75f;

	/// <summary>The furthest the wobbled rim plus its soft edge reaches from the centre.</summary>
	public float ReachMeters => (RadiusMeters * 1.2f) + EdgeMeters;

	/// <summary>Tilled coverage 0..1 at a world point (m): 1 inside, easing to 0 across <see cref="EdgeMeters"/> at a rim that wobbles ±19 % (two low lobes).</summary>
	public float Coverage( float x, float y )
	{
		var dx = x - X;
		var dy = y - Y;
		var d2 = (dx * dx) + (dy * dy);
		var reach = ReachMeters;
		if ( d2 >= reach * reach )
			return 0f;
		var d = MathF.Sqrt( d2 );
		var a = MathF.Atan2( dy, dx );
		var rim = RadiusMeters * (1f + (0.12f * MathF.Sin( (3f * a) + Phase )) + (0.07f * MathF.Sin( (5f * a) + (Phase * 1.7f) )));
		return Math.Clamp( (rim + (EdgeMeters * 0.5f) - d) / EdgeMeters, 0f, 1f );
	}

	/// <summary>The strongest coverage of any patch at a world point (m). Null / empty = 0.</summary>
	public static float CoverageAt( TerrainGroundPatch[] patches, float x, float y )
	{
		if ( patches is null )
			return 0f;
		var best = 0f;
		for ( var i = 0; i < patches.Length && best < 1f; i++ )
			best = MathF.Max( best, patches[i].Coverage( x, y ) );
		return best;
	}
}
