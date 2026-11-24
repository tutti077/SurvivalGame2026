using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The player's corpse bag (Mark 2026-11-19: "a burlap sack tied with black string. About the same width as players,
/// about knee high, it should add a pin to the map where it is, and shoot a beacon up in the air 30m showing you where
/// it is physically in the world"). Sits on the object <see cref="DeathLootBag"/> spawns: the sack model and the loot
/// container are authored there; this component is who the bag belongs to, and the two ways its owner finds it again —
/// the map pin (<c>TerrainWorldMapFace.UpdateCorpseBagMarkers</c> reads <see cref="Registered"/>) and the beacon, a
/// thin column of light 30 m straight up from the sack. Both are shown to the owner only; the bag itself can still be
/// opened by anyone, as before. Presentation only: nothing here runs on the host path.
/// </summary>
[Title( "Corpse Bag" )]
public sealed class CorpseBag : Component
{
	public const string ModelPath = "models/props/corpse_bag/corpse_bag_v1.vmdl";
	public const string MapPinTexturePath = "ui/map/pin_corpse_bag.png";
	public const float BeaconHeightMeters = 30f;

	public static readonly List<CorpseBag> Registered = new();

	/// <summary>The dead player's connection (their session) and Steam account (so a reconnect still finds the bag).</summary>
	[Sync( SyncFlags.FromHost )] public Guid OwnerConnectionId { get; set; }
	[Sync( SyncFlags.FromHost )] public ulong OwnerSteamId { get; set; }
	[Sync( SyncFlags.FromHost )] public string OwnerName { get; set; } = "Player";

	CorpseBagBeaconVfx _beacon;

	/// <summary>True on the machine of the player this bag belongs to.</summary>
	public bool IsLocalPlayers
	{
		get
		{
			var local = Connection.Local;
			if ( local is null )
				return true;
			if ( OwnerConnectionId != Guid.Empty && local.Id == OwnerConnectionId )
				return true;
			return OwnerSteamId != 0UL && local.SteamId.ValueUnsigned == OwnerSteamId;
		}
	}

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Registered.Contains( this ) )
			Registered.Add( this );
	}

	protected override void OnDisabled()
	{
		Registered.Remove( this );
		_beacon?.Delete();
		_beacon = null;
		base.OnDisabled();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !IsLocalPlayers )
		{
			_beacon?.Delete();
			_beacon = null;
			return;
		}

		_beacon ??= new CorpseBagBeaconVfx( Scene.SceneWorld );
		var cam = Scene.Camera;
		_beacon.Tick(
			WorldPosition,
			TerrainWorldUnits.MetersToEngine( BeaconHeightMeters ),
			cam.IsValid() ? cam.WorldPosition : WorldPosition,
			cam.IsValid() ? cam.FieldOfView : 80f,
			Time.Now );
	}
}

/// <summary>The beacon: one unlit column (<see cref="UnlitVfxGeometry"/> cross-line) from the sack to 30 m up, fading out toward the top.</summary>
sealed class CorpseBagBeaconVfx : SceneCustomObject
{
	/// <summary>Never thinner than this on screen (px at 1080p), so it still reads from across a valley.</summary>
	const float MinPixels = 3f;
	/// <summary>Physical width up close (m).</summary>
	const float WidthMeters = 0.18f;
	static readonly Color BeaconColor = new( 1f, 0.74f, 0.32f, 1f );   // warm amber: the paler first pass washed out against a bright sky

	readonly List<Vertex> _verts = new( 32 );
	readonly Material _material;
	Vector3 _base;
	float _height;
	float _width;
	float _alpha;

	public CorpseBagBeaconVfx( SceneWorld world ) : base( world )
	{
		_material = UnlitVfxGeometry.CreateMaterial();
		Flags.CastShadows = false;
		Flags.IsOpaque = false;
		Flags.IsTranslucent = true;
		RenderingEnabled = false;
	}

	/// <summary>Main thread: where the column stands, how wide it is from this camera, a slow pulse.</summary>
	public void Tick( Vector3 basePosition, float height, Vector3 camera, float fov, float now )
	{
		_base = basePosition;
		_height = height;
		var perUnit = 2f * MathF.Tan( Math.Clamp( fov, 20f, 120f ) * 0.5f * (MathF.PI / 180f) ) / 1080f;
		_width = MathF.Max( TerrainWorldUnits.MetersToEngine( WidthMeters ), (basePosition - camera).Length * perUnit * MinPixels );
		_alpha = 0.78f + 0.16f * MathF.Sin( now * 2.2f );
		Bounds = new BBox( basePosition, basePosition + Vector3.Up * height ).Grow( _width + 8f );
		RenderingEnabled = true;
	}

	public override void RenderSceneObject()
	{
		_verts.Clear();
		// Two stretches so the lower two thirds stay bright and only the top third fades to nothing.
		var mid = _base + Vector3.Up * (_height * 0.66f);
		var top = _base + Vector3.Up * _height;
		var c0 = BeaconColor.WithAlpha( _alpha );
		var c1 = BeaconColor.WithAlpha( _alpha * 0.7f );
		UnlitVfxGeometry.AddCrossLine( _verts, _base, mid, _width, c0, c1 );
		UnlitVfxGeometry.AddCrossLine( _verts, mid, top, _width, c1, BeaconColor.WithAlpha( 0f ) );
		Graphics.Draw( _verts, _verts.Count, _material, Attributes, Graphics.PrimitiveType.Triangles );
	}
}
