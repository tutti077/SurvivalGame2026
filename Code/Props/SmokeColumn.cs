using System;
using Sandbox;

namespace Survival;

/// <summary>
/// A standing column of smoke that marks a place from far off (Mark 2026-11-19: "a trail of smoke that trails into the air
/// ... smoke trails that lead players to crashed stasis pods"). The project has no particle system: the column is
/// <see cref="PuffCount"/> faceted blobs (the stasis pod's puff model) that this component owns and moves itself - one
/// loop, no spawning or destroying after start. Each blob rides the same path with its own phase: it leaves
/// <see cref="OriginMeters"/> small and dark, climbs to <see cref="HeightMeters"/> over <see cref="RiseSeconds"/> while it
/// swells to <see cref="TopDiameterMeters"/> and pales, leans downwind (<see cref="WindSystem"/>) the higher it gets, and
/// shrinks to nothing over the last part of the climb. Purely local on every machine (nothing is networked or saved); a
/// dedicated server builds none.
/// <para>The column is a function of WHERE it stands and the scene clock (blob phases, wander and spin come from a hash of
/// its position and <c>Time.Now</c>), so two columns at one place are the same column: the far stand-in
/// (<c>TerrainWorldManager.FarSmoke.cs</c>, a bare object carrying only this component out to the far-tree range) and the
/// crashed pod's own column swap without a blob moving (Mark 2026-11-23: "make the LOD smoke stack move even from far
/// away"). <see cref="FadeIn"/> grows it from nothing when it appears ("dont make it just pop in").</para>
/// <para>With <see cref="StopWhenMinedOut"/> the column dies down once every section of the <see cref="MineableRock"/> on
/// the same object is gone (the crashed pod has been taken apart).</para>
/// </summary>
[Title( "Smoke Column" )]
public sealed class SmokeColumn : Component
{
	[Property, Group( "Column" ), Title( "Origin (m)" ), Description( "Where the smoke leaves the object, local metres." )]
	public Vector3 OriginMeters { get; set; } = new( 0f, 0f, 0.8f );

	[Property, Group( "Column" ), Title( "Height (m)" ), Range( 5f, 150f ), Description( "How high a blob climbs before it is gone. 60 m clears the elms and reads from 200 m and more." )]
	public float HeightMeters { get; set; } = 60f;

	[Property, Group( "Column" ), Title( "Rise Time (s)" ), Range( 2f, 120f ), Description( "Seconds one blob takes from the origin to the top." )]
	public float RiseSeconds { get; set; } = 26f;

	[Property, Group( "Column" ), Title( "Blobs" ), Range( 4, 60 )]
	public int PuffCount { get; set; } = 26;

	[Property, Group( "Column" ), Title( "Base Diameter (m)" ), Range( 0.1f, 10f )]
	public float BaseDiameterMeters { get; set; } = 1.1f;

	[Property, Group( "Column" ), Title( "Top Diameter (m)" ), Range( 0.5f, 40f )]
	public float TopDiameterMeters { get; set; } = 11f;

	[Property, Group( "Column" ), Title( "Downwind Lean (m)" ), Range( 0f, 80f ), Description( "How far downwind the top of the column stands from its foot at full wind." )]
	public float LeanMeters { get; set; } = 22f;

	[Property, Group( "Column" ), Title( "Wander (m)" ), Range( 0f, 10f ), Description( "Sideways drift of each blob off the centre line near the top." )]
	public float WanderMeters { get; set; } = 2.5f;

	[Property, Group( "Look" ), Title( "Blob Model" )]
	public Model PuffModel { get; set; }

	[Property, Group( "Look" ), Title( "Blob Material" )]
	public Material PuffMaterial { get; set; }

	[Property, Group( "Look" ), Title( "Tint At The Foot" )]
	public Color FootTint { get; set; } = new( 0.16f, 0.15f, 0.15f );

	[Property, Group( "Look" ), Title( "Tint At The Top" )]
	public Color TopTint { get; set; } = new( 0.62f, 0.62f, 0.64f );

	[Property, Group( "Column" ), Title( "Fade In" ), Description( "Start from nothing and grow in over a few seconds (the far stand-in entering range); off = standing at once." )]
	public bool FadeIn { get; set; }

	[Property, Group( "Column" ), Title( "Stop When Mined Out" ), Description( "The column dies down once the Mineable Rock on this object has no section left." )]
	public bool StopWhenMinedOut { get; set; }

	GameObject[] _puffs;
	ModelRenderer[] _renderers;
	float[] _seeds;
	MineableRock _rock;
	/// <summary>Offset of this column's clock from the scene clock, from its position.</summary>
	float _phase;
	float _strength = 1f;

	/// <summary>0-1 from a position (engine units, rounded to the metre) and a salt: the same place gives the same column.</summary>
	static float PlaceHash( Vector3 position, float salt )
	{
		var m = position / TerrainWorldUnits.MetersToEngine( 1f );
		var v = MathF.Sin( (MathF.Round( m.x ) * 12.9898f) + (MathF.Round( m.y ) * 78.233f) + (MathF.Round( m.z ) * 37.719f) + (salt * 4.1414f) ) * 43758.5453f;
		return v - MathF.Floor( v );
	}

	protected override void OnStart()
	{
		base.OnStart();
		if ( Application.IsHeadless || PuffModel is null )
			return;

		_rock = StopWhenMinedOut ? Components.Get<MineableRock>() : null;
		var count = Math.Clamp( PuffCount, 4, 60 );
		_puffs = new GameObject[count];
		_renderers = new ModelRenderer[count];
		_seeds = new float[count];
		for ( var i = 0; i < count; i++ )
		{
			var go = new GameObject( true, "smoke_blob" );
			go.NetworkMode = NetworkMode.Never;
			go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			go.Parent = GameObject;
			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = PuffModel;
			if ( PuffMaterial is not null )
				renderer.MaterialOverride = PuffMaterial;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_puffs[i] = go;
			_renderers[i] = renderer;
			_seeds[i] = PlaceHash( WorldPosition, i + 1 ) * 100f;
		}
		_phase = PlaceHash( WorldPosition, 0f ) * RiseSeconds;
		if ( FadeIn )
			_strength = 0f;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( _puffs is null )
			return;

		var minedOut = _rock is not null && IsMinedOut( _rock );
		_strength = MathX.Approach( _strength, minedOut ? 0f : 1f, Time.Delta / 6f );
		var _clock = Time.Now + _phase;

		var unit = TerrainWorldUnits.MetersToEngine( 1f );
		var wind = WindSystem.Current;
		var windDir = wind is not null ? wind.Direction.WithZ( 0f ).Normal : Vector3.Forward;
		var windStrength = wind is not null ? 0.35f + (0.65f * wind.Strength01) : 0.5f;
		var side = new Vector3( -windDir.y, windDir.x, 0f );
		var foot = WorldPosition + (WorldRotation * (OriginMeters * unit));
		var rise = MathF.Max( 2f, RiseSeconds );
		var count = _puffs.Length;

		for ( var i = 0; i < count; i++ )
		{
			var go = _puffs[i];
			if ( !go.IsValid() )
				continue;

			var t = ((_clock / rise) + ((float)i / count)) % 1f;
			// climbs fast off the fire, slows as it cools; leans further downwind the higher it stands
			var climb = MathF.Pow( t, 0.8f );
			var lean = LeanMeters * windStrength * t * t;
			var seed = _seeds[i];
			var wander = WanderMeters * t * MathF.Sin( (_clock * 0.21f) + seed );
			var bob = WanderMeters * 0.4f * t * MathF.Sin( (_clock * 0.33f) + (seed * 1.7f) );
			var position = foot + (Vector3.Up * (HeightMeters * climb * unit)) + (windDir * ((lean + bob) * unit)) + (side * (wander * unit));

			var swell = BaseDiameterMeters + ((TopDiameterMeters - BaseDiameterMeters) * MathF.Pow( t, 0.9f ));
			var birth = MathF.Min( 1f, t / 0.04f );
			var thin = t < 0.8f ? 1f : 1f - ((t - 0.8f) / 0.2f);
			var meters = swell * birth * MathF.Max( 0f, thin ) * _strength;

			go.WorldPosition = position;
			go.WorldRotation = Rotation.From( seed * 40f, (seed * 90f) + (_clock * 6f), seed * 25f );
			// the blob model is a unit blob: radius 1 m at scale 1 (imported at 40 u/m), so diameter d metres = scale d / 2
			go.WorldScale = Vector3.One * MathF.Max( 0.001f, meters * 0.5f * unit / 40f );
			_renderers[i].Tint = Color.Lerp( FootTint, TopTint, MathF.Min( 1f, t * 1.4f ) );
		}
	}

	static bool IsMinedOut( MineableRock rock )
	{
		var count = rock.Sections.Count;
		if ( count == 0 )
			return false;
		var all = count >= 31 ? -1 : (1 << count) - 1;
		return (rock.BrokenMask & all) == all;
	}
}
