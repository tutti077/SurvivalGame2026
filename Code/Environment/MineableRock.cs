using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// A rock the pickaxe takes apart section by section (Mark 2026-11: "the spire should be split into 6 or so chunks so that
/// when the player starts destroying the rock it doesn't just explode on a few hits. Each section will have to be destroyed
/// manually. The rock cannot remain floating without connecting to bottom pieces so if theyre gone the top should just
/// explode into stone bits and rain onto the ground" + "big boulders like 3m in diameter that are like the spires too").
/// <para>Sections are the children tagged <see cref="SectionTag"/> (renderer + static collider each, all sharing the rock's
/// origin), in name order. <see cref="GroundedSections"/> stand on the ground; <see cref="SectionLinks"/> says which section
/// rests on which ("upper-lower"; Blender/scripts/create_mineable_rocks.py measures both from the cut faces). A section
/// takes <see cref="HitsPerSection"/> pickaxe hits; when it breaks, every section that no longer rests (through other
/// sections) on a grounded one collapses with it. A breaking section bursts into debris on every peer: its pre-fractured
/// <see cref="PieceModels"/> (the small breakable rocks) or <see cref="BitsPerSection"/> stone bits filling its volume
/// (<see cref="BitModels"/>: spires, boulders) - short-lived local <see cref="ChopDebris"/>, never networked. The host turns
/// some of that debris into resource_stone drops where it comes to rest: the bits leave, the stone stays.</para>
/// <para>Host authority: the pickaxe sends intent through <see cref="PlayerTerraform.OwnerRequestMine"/>; the host checks
/// reach, tool and stamina once and calls <see cref="HostApplyHit"/>. The broken sections are one bit mask broadcast through
/// <see cref="WorldScatterIdentity"/> (the felled-tree path), remembered for the session, and re-applied silently when a
/// scattered rock is planted again after its chunk unloads.</para>
/// </summary>
[Title( "Mineable Rock" )]
public sealed class MineableRock : Component
{
	/// <summary>Children with this tag are the sections.</summary>
	public const string SectionTag = "rock_section";

	/// <summary>Sections are a bit mask in one int.</summary>
	public const int MaxSections = 30;

	[Property, Group( "Mining" ), Title( "Hits Per Section" ), Range( 1, 40 ), Description( "Pickaxe hits one section takes before it breaks." )]
	public int HitsPerSection { get; set; } = 4;

	[Property, Group( "Mining" ), Title( "Require Pickaxe" ), Description( "Only a tool with the Mine action hits it." )]
	public bool RequireMineTool { get; set; } = true;

	[Property, Group( "Sections" ), Title( "Grounded Sections" ), Description( "Section indices that stand on the ground (never collapse while unbroken)." )]
	public List<int> GroundedSections { get; set; } = [0];

	[Property, Group( "Sections" ), Title( "Section Links" ), Description( "upper-lower pairs: section upper rests on section lower. A section with no unbroken support left collapses." )]
	public List<string> SectionLinks { get; set; } = [];

	[Property, Group( "Loot" ), Title( "Stone Resource Id" )]
	public string StoneResourceId { get; set; } = "resource_stone";

	[Property, Group( "Loot" ), Title( "Mined Section Stone Min" ), Range( 0, 50 ), Description( "Stone a section the pickaxe breaks leaves." )]
	public int StoneMin { get; set; } = 3;

	[Property, Group( "Loot" ), Title( "Mined Section Stone Max" ), Range( 0, 50 )]
	public int StoneMax { get; set; } = 5;

	[Property, Group( "Loot" ), Title( "Collapsed Section Stone Min" ), Range( 0, 50 ), Description( "Stone each section that loses its support rains down." )]
	public int CollapseStoneMin { get; set; } = 2;

	[Property, Group( "Loot" ), Title( "Collapsed Section Stone Max" ), Range( 0, 50 )]
	public int CollapseStoneMax { get; set; } = 4;

	[Property, Group( "Debris" ), Title( "Burst Pieces" ), Description( "Pre-fractured pieces sharing the section's origin (one-section rocks). Empty = stone bits." )]
	public List<Model> PieceModels { get; set; } = [];

	[Property, Group( "Debris" ), Title( "Stone Bits" ), Description( "Bits a section without burst pieces breaks into, spread through its volume." )]
	public List<Model> BitModels { get; set; } = [];

	[Property, Group( "Debris" ), Title( "Bits Per Section" ), Range( 0, 40 )]
	public int BitsPerSection { get; set; } = 8;

	[Property, Group( "Debris" ), Title( "Bit Scale" ), Range( 0.25f, 4f ), Description( "Size of the stone bits (x the rock's own scale): big sections break into big bits." )]
	public float BitScale { get; set; } = 1f;

	[Property, Group( "Debris" ), Title( "Hit Chips" ), Description( "Tiny stones the miner sees fly off each hit (local only)." )]
	public List<Model> ChipModels { get; set; } = [];

	[Property, Group( "Debris" ), Title( "Debris Lifetime (s)" ), Range( 0.3f, 15f )]
	public float DebrisLifetimeSeconds { get; set; } = 3f;

	[Property, Group( "Debris" ), Title( "Debris Scatter (m/s)" ), Range( 0f, 10f ), Description( "Outward speed the debris leaves with; gravity does the rest." )]
	public float DebrisScatterMeters { get; set; } = 2f;

	[Property, Group( "Debug" )]
	public bool LogMining { get; set; }

	/// <summary>Bit i set = section i is gone.</summary>
	public int BrokenMask { get; private set; }

	List<GameObject> _sections;
	List<int>[] _restsOn;
	int[] _hits;

	static bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	/// <summary>The sections, cached once (authored on the prefab).</summary>
	public IReadOnlyList<GameObject> Sections
	{
		get
		{
			if ( _sections is not null )
				return _sections;
			_sections = new List<GameObject>();
			foreach ( var child in GameObject.Children )
			{
				if ( child.IsValid() && child.Tags.Has( SectionTag ) )
					_sections.Add( child );
			}
			_sections.Sort( ( a, b ) => string.CompareOrdinal( a.Name, b.Name ) );
			if ( _sections.Count > MaxSections )
				_sections.RemoveRange( MaxSections, _sections.Count - MaxSections );
			return _sections;
		}
	}

	public bool IsSectionBroken( int index ) => (BrokenMask & (1 << index)) != 0;

	/// <summary>The rock and section a trace hit landed on, or false.</summary>
	public static bool TryResolve( GameObject hit, out MineableRock rock, out int section )
	{
		rock = null;
		section = -1;
		GameObject child = null;
		for ( var g = hit; g.IsValid(); g = g.Parent )
		{
			rock = g.Components.Get<MineableRock>();
			if ( rock is not null )
				break;
			child = g;
		}
		if ( rock is null || child is null )
			return false;
		var sections = rock.Sections;
		for ( var i = 0; i < sections.Count; i++ )
		{
			if ( sections[i] == child )
			{
				section = i;
				return !rock.IsSectionBroken( i );
			}
		}
		return false;
	}

	/// <summary>Host: is this world point on (or right by) the section? One bounds test per swing.</summary>
	public bool IsPointOnSection( int index, Vector3 worldPoint, float slackUnits )
	{
		if ( index < 0 || index >= Sections.Count )
			return false;
		var renderer = Sections[index].Components.Get<ModelRenderer>();
		if ( renderer is null )
			return false;
		var box = renderer.Bounds;
		var slack = new Vector3( slackUnits );
		return new BBox( box.Mins - slack, box.Maxs + slack ).Contains( worldPoint );
	}

	/// <summary>Host: one pickaxe hit on a section. True when it landed (the swing is paid for).</summary>
	public bool HostApplyHit( int index, GameObject miner )
	{
		if ( !IsHostAuthority || index < 0 || index >= Sections.Count || IsSectionBroken( index ) )
			return false;

		_hits ??= new int[Sections.Count];
		_hits[index]++;
		var needed = Math.Max( 1, HitsPerSection );
		if ( LogMining )
			Log.Info( $"[MineableRock] {GameObject.Name} section {index}: hit {_hits[index]}/{needed}" );
		if ( _hits[index] < needed )
			return true;

		var mask = BrokenMask | (1 << index) | UnsupportedAfter( BrokenMask | (1 << index) );
		if ( LogMining )
			Log.Info( $"[MineableRock] {GameObject.Name} section {index} broke, mask {Convert.ToString( BrokenMask, 2 )} -> {Convert.ToString( mask, 2 )}" );
		ApplyBroken( mask, true, s => s == index ? Roll( StoneMin, StoneMax ) : Roll( CollapseStoneMin, CollapseStoneMax ), miner );
		Broadcast( mask );
		return true;
	}

	/// <summary>Every peer (and the host's own echo): sections in <paramref name="mask"/> are gone. Idempotent.
	/// <paramref name="animate"/> = play the burst (a live break); false = a re-planted rock that was already broken.</summary>
	public void ApplyBrokenMask( int mask, bool animate ) => ApplyBroken( mask, animate, null, null );

	void ApplyBroken( int mask, bool animate, Func<int, int> stoneFor, GameObject miner )
	{
		var fresh = mask & ~BrokenMask;
		if ( fresh == 0 )
			return;
		BrokenMask |= mask;
		var sections = Sections;
		for ( var i = 0; i < sections.Count; i++ )
		{
			if ( (fresh & (1 << i)) == 0 || !sections[i].IsValid() )
				continue;
			if ( animate )
				SpawnDebris( sections[i], stoneFor?.Invoke( i ) ?? 0, miner );
			sections[i].Enabled = false;
		}
	}

	/// <summary>Sections left with no path down to an unbroken grounded section once <paramref name="broken"/> are gone.</summary>
	int UnsupportedAfter( int broken )
	{
		var count = Sections.Count;
		var restsOn = RestsOn();
		var supported = 0;
		foreach ( var g in GroundedSections ?? [] )
		{
			if ( g >= 0 && g < count && (broken & (1 << g)) == 0 )
				supported |= 1 << g;
		}
		for ( var changed = true; changed; )
		{
			changed = false;
			for ( var s = 0; s < count; s++ )
			{
				var bit = 1 << s;
				if ( (supported & bit) != 0 || (broken & bit) != 0 )
					continue;
				foreach ( var below in restsOn[s] )
				{
					if ( (supported & (1 << below)) == 0 )
						continue;
					supported |= bit;
					changed = true;
					break;
				}
			}
		}
		var all = count >= 31 ? -1 : (1 << count) - 1;
		return all & ~supported & ~broken;
	}

	List<int>[] RestsOn()
	{
		if ( _restsOn is not null )
			return _restsOn;
		var count = Sections.Count;
		_restsOn = new List<int>[count];
		for ( var i = 0; i < count; i++ )
			_restsOn[i] = new List<int>();
		foreach ( var link in SectionLinks ?? [] )
		{
			var parts = link?.Split( '-' );
			if ( parts is not { Length: 2 } || !int.TryParse( parts[0], out var upper ) || !int.TryParse( parts[1], out var lower ) )
				continue;
			if ( upper >= 0 && upper < count && lower >= 0 && lower < count && upper != lower )
				_restsOn[upper].Add( lower );
		}
		return _restsOn;
	}

	void Broadcast( int mask )
	{
		var identity = Components.Get<WorldScatterIdentity>( FindMode.EverythingInSelfAndAncestors );
		if ( identity is not null && !string.IsNullOrWhiteSpace( identity.StableKey ) )
			WorldScatterIdentity.HostBroadcastBroken( identity.StableKey, mask );
		else if ( GameObject.Network is { Active: true } )
			RpcBroadcastMask( mask );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcBroadcastMask( int mask ) => ApplyBrokenMask( mask, true );

	static int Roll( int min, int max )
	{
		min = Math.Max( 0, Math.Min( min, max ) );
		max = Math.Max( min, max );
		return min + (int)MathF.Floor( Sandbox.Game.Random.Float( 0f, max - min + 0.999f ) );
	}

	// ------------------------------------------------------------------------------------------------------ debris

	/// <summary>Every peer: the section bursts. Host: <paramref name="stones"/> of the debris turn into stone drops where they end up.</summary>
	void SpawnDebris( GameObject section, int stones, GameObject miner )
	{
		var renderer = section.Components.Get<ModelRenderer>();
		if ( renderer?.Model is null )
			return;
		var xf = section.WorldTransform;
		var centre = renderer.Bounds.Center;
		var scatter = TerrainWorldUnits.MetersToEngine( DebrisScatterMeters );
		var debris = new List<GameObject>();

		if ( PieceModels is { Count: > 0 } )
		{
			// the pre-fractured pieces assemble at the section's own transform and fly apart from its centre
			foreach ( var model in PieceModels )
			{
				if ( model is null || model.IsError )
					continue;
				var piece = SpawnPiece( model, xf );
				var outward = (piece.WorldTransform.PointToWorld( model.Bounds.Center ) - centre).WithZ( 0f );
				outward = outward.Length > 0.5f ? outward.Normal : Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
				Launch( piece, outward * scatter * Sandbox.Game.Random.Float( 0.6f, 1.2f ) + Vector3.Up * scatter * Sandbox.Game.Random.Float( 0.5f, 1f ) );
				debris.Add( piece );
			}
		}
		else if ( BitModels is { Count: > 0 } && BitsPerSection > 0 )
		{
			// stone bits on a jittered grid through the section's box (never two in one cell: no depenetration blast)
			var local = renderer.Model.Bounds;
			var cells = StratifiedCells( local, BitsPerSection );
			foreach ( var cell in cells )
			{
				var model = BitModels[Sandbox.Game.Random.Int( 0, BitModels.Count - 1 )];
				if ( model is null || model.IsError )
					continue;
				var at = new Transform( xf.PointToWorld( cell ), Rotation.Random, xf.Scale * Math.Max( 0.05f, BitScale ) * Sandbox.Game.Random.Float( 0.8f, 1.2f ) );
				var bit = SpawnPiece( model, at );
				var outward = (at.Position - centre).WithZ( 0f );
				outward = outward.Length > 0.5f ? outward.Normal : Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward;
				Launch( bit, outward * scatter * Sandbox.Game.Random.Float( 0.3f, 1.1f ) + Vector3.Up * scatter * Sandbox.Game.Random.Float( -0.2f, 0.4f ) );
				debris.Add( bit );
			}
		}

		if ( !IsHostAuthority || stones <= 0 || string.IsNullOrWhiteSpace( StoneResourceId ) )
			return;

		var scene = Scene;
		var resourceId = StoneResourceId;
		if ( debris.Count == 0 )
		{
			DropStones( scene, StoneResourceId, centre, stones, miner );
			return;
		}
		// the first few pieces carry the stone: it drops where each of them ends up
		var perCarrier = new int[Math.Min( stones, debris.Count )];
		for ( var i = 0; i < stones; i++ )
			perCarrier[i % perCarrier.Length]++;
		for ( var i = 0; i < perCarrier.Length; i++ )
		{
			var count = perCarrier[i];
			var chop = debris[i].Components.Get<ChopDebris>();
			if ( chop is not null )
				chop.OnExpired = at => DropStones( scene, resourceId, at, count, miner );
		}
	}

	/// <summary>Owner (local cosmetic): a couple of chips off the hit point.</summary>
	public void PlayHitChips( Vector3 hitPosition, Vector3 normal )
	{
		if ( ChipModels is not { Count: > 0 } )
			return;
		var scatter = TerrainWorldUnits.MetersToEngine( 2.5f );
		for ( var i = 0; i < 2; i++ )
		{
			var model = ChipModels[Sandbox.Game.Random.Int( 0, ChipModels.Count - 1 )];
			if ( model is null || model.IsError )
				continue;
			var chip = SpawnPiece( model, new Transform( hitPosition + normal * 4f, Rotation.Random, 0.6f ), 0.7f );
			Launch( chip, (normal + Vector3.Random * 0.6f).Normal * scatter + Vector3.Up * scatter * 0.4f );
		}
	}

	GameObject SpawnPiece( Model model, Transform at, float lifetime = -1f )
	{
		var go = new GameObject( false, "rock_debris" );
		go.NetworkMode = NetworkMode.Never;
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = Scene;
		go.WorldTransform = at;
		go.Tags.Add( "debris" );
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = model;
		var collider = go.Components.Create<ModelCollider>();
		collider.Model = model;
		var body = go.Components.Create<Rigidbody>();
		body.Gravity = true;
		var chop = go.Components.Create<ChopDebris>();
		chop.LifetimeSeconds = lifetime > 0f ? lifetime : DebrisLifetimeSeconds * Sandbox.Game.Random.Float( 0.85f, 1.15f );
		go.Enabled = true;
		return go;
	}

	static void Launch( GameObject piece, Vector3 velocity )
	{
		var chop = piece.Components.Get<ChopDebris>();
		if ( chop is null )
			return;
		chop.Velocity = velocity;
		chop.AngularVelocity = Vector3.Random * 6f;
	}

	/// <summary>Up to <paramref name="count"/> distinct cells of a grid laid through the (local) box, jittered inside their cell.</summary>
	static List<Vector3> StratifiedCells( BBox box, int count )
	{
		var size = box.Size;
		var n = Math.Max( 1, (int)MathF.Ceiling( MathF.Pow( count * 2f, 1f / 3f ) ) );
		var nz = Math.Max( 1, (int)MathF.Round( n * Math.Clamp( size.z / Math.Max( 1f, (size.x + size.y) * 0.5f ), 0.5f, 3f ) ) );
		var cells = new List<Vector3>();
		for ( var x = 0; x < n; x++ )
		for ( var y = 0; y < n; y++ )
		for ( var z = 0; z < nz; z++ )
			cells.Add( new Vector3( (x + 0.5f) / n, (y + 0.5f) / n, (z + 0.5f) / nz ) );
		var picked = new List<Vector3>( count );
		while ( picked.Count < count && cells.Count > 0 )
		{
			var i = Sandbox.Game.Random.Int( 0, cells.Count - 1 );
			var c = cells[i];
			cells.RemoveAt( i );
			// shrink toward the axis a little: the box corners are mostly air on a tapering rock
			var t = new Vector3(
				0.5f + (c.x - 0.5f + Sandbox.Game.Random.Float( -0.2f, 0.2f ) / n) * 0.75f,
				0.5f + (c.y - 0.5f + Sandbox.Game.Random.Float( -0.2f, 0.2f ) / n) * 0.75f,
				c.z + Sandbox.Game.Random.Float( -0.2f, 0.2f ) / nz );
			picked.Add( box.Mins + size * t );
		}
		return picked;
	}

	/// <summary>Host: <paramref name="count"/> single stones at a point (the scene is captured at the break: the rock may have unloaded since).</summary>
	static void DropStones( Scene scene, string resourceId, Vector3 at, int count, GameObject miner )
	{
		if ( scene is null || !scene.IsValid() )
			return;
		if ( miner is not null && !miner.IsValid() )
			miner = null;
		for ( var i = 0; i < count; i++ )
		{
			var drop = HeldStackWorldDrop.TrySpawnWorldDrop( scene, resourceId, 1, at + Vector3.Up * TerrainWorldUnits.MetersToEngine( 0.3f ), miner );
			if ( drop is not null && count > 1 )
				HeldStackWorldDrop.ApplyScatterBurst( drop, Rotation.FromYaw( Sandbox.Game.Random.Float( 0f, 360f ) ).Forward );
		}
	}
}
