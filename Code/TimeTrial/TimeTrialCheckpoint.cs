using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Ordered race gate. Next required checkpoint uses active scale (10×10); others use inactive (3×3).
/// Host detects body overlap via trigger <see cref="Collider.Touching"/>.
/// </summary>
[Title( "Time Trial Checkpoint" )]
public sealed class TimeTrialCheckpoint : Component
{
	static readonly List<TimeTrialCheckpoint> Active = new();

	[Property, Title( "Order (0 = start)" ), Range( 0, 32 )]
	public int Order { get; set; }

	[Property, Title( "Inactive Scale" )]
	public Vector3 InactiveScale { get; set; } = new( 1f, 3f, 3f );

	[Property, Title( "Active (next) Scale" )]
	public Vector3 ActiveScale { get; set; } = new( 1f, 10f, 10f );

	public static IReadOnlyList<TimeTrialCheckpoint> All => Active;

	bool _highlighted;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	protected override void OnDestroy()
	{
		Active.Remove( this );
		base.OnDestroy();
	}

	protected override void OnStart()
	{
		base.OnStart();
		ApplyVisualScale( highlighted: false );
	}

	public void SetHighlighted( bool highlighted )
	{
		if ( _highlighted == highlighted )
			return;

		_highlighted = highlighted;
		ApplyVisualScale( highlighted );
	}

	void ApplyVisualScale( bool highlighted )
	{
		GameObject.LocalScale = highlighted ? ActiveScale : InactiveScale;
	}

	/// <summary>Host: true when this player's body overlaps the checkpoint (distance vs trigger radius).</summary>
	public bool HostIsPlayerInside( GameObject playerRoot )
	{
		if ( playerRoot is null || !playerRoot.IsValid() )
			return false;

		var center = GameObject.WorldPosition;
		var radius = ResolveTriggerRadiusWorld();
		if ( radius <= 0.5f )
			radius = 25f;

		// Body center — feet alone can sit under a raised sphere.
		var body = playerRoot.WorldPosition + Vector3.Up * 36f;
		var maxDist = radius + 40f; // pad for controller capsule
		return (body - center).LengthSquared <= maxDist * maxDist;
	}

	float ResolveTriggerRadiusWorld()
	{
		var best = 0f;
		foreach ( var col in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || !col.Enabled || !col.IsTrigger )
				continue;

			if ( col is SphereCollider sphere )
			{
				var s = sphere.GameObject.WorldScale;
				var scale = Math.Max( Math.Abs( s.x ), Math.Max( Math.Abs( s.y ), Math.Abs( s.z ) ) );
				best = Math.Max( best, Math.Max( 1f, sphere.Radius ) * Math.Max( 0.01f, scale ) );
				continue;
			}

			if ( col is BoxCollider box )
			{
				var s = box.GameObject.WorldScale;
				var extents = box.Scale * 0.5f;
				var wx = Math.Abs( extents.x * s.x );
				var wy = Math.Abs( extents.y * s.y );
				var wz = Math.Abs( extents.z * s.z );
				best = Math.Max( best, MathF.Sqrt( wx * wx + wy * wy + wz * wz ) );
			}
		}

		return best;
	}

	public static void RefreshHighlights( int nextOrderForLocalOrRace )
	{
		for ( var i = 0; i < Active.Count; i++ )
		{
			var cp = Active[i];
			if ( cp is null || !cp.IsValid() )
				continue;

			cp.SetHighlighted( cp.Order == nextOrderForLocalOrRace );
		}
	}

	/// <summary>Resolves checkpoints in the exact variation sequence (Order ids).</summary>
	public static bool TryGetRoute( IReadOnlyList<int> orders, out List<TimeTrialCheckpoint> route )
	{
		route = new List<TimeTrialCheckpoint>();
		if ( orders is null || orders.Count < 2 )
			return false;

		for ( var i = 0; i < orders.Count; i++ )
		{
			var want = orders[i];
			TimeTrialCheckpoint found = null;
			for ( var a = 0; a < Active.Count; a++ )
			{
				var cp = Active[a];
				if ( cp is null || !cp.IsValid() || !cp.Enabled || cp.Order != want )
					continue;
				found = cp;
				break;
			}

			if ( found is null )
			{
				Log.Warning( $"[TimeTrial] Variation route missing checkpoint Order={want}." );
				route.Clear();
				return false;
			}

			route.Add( found );
		}

		return route.Count >= 2;
	}

	public static bool TryGetOrdered( out List<TimeTrialCheckpoint> ordered )
	{
		ordered = new List<TimeTrialCheckpoint>();
		for ( var i = 0; i < Active.Count; i++ )
		{
			var cp = Active[i];
			if ( cp is not null && cp.IsValid() && cp.Enabled )
				ordered.Add( cp );
		}

		if ( ordered.Count == 0 )
			return false;

		ordered.Sort( static ( a, b ) => a.Order.CompareTo( b.Order ) );
		return true;
	}
}
