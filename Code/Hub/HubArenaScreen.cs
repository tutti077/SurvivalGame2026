using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// The bar's massive screen. One feed camera renders a small render target that <c>shaders/hub_screen.shader</c> shows
/// on the screen box; the host picks the shot and every peer puts the camera there:
/// <list type="bullet">
/// <item>the fixed <see cref="Angles"/> round the coliseum cycle every <see cref="SecondsPerAngle"/>; while the volunteer
/// cameraman films (<see cref="ArenaSession.CameramanId"/>) every other shot is their own view;</item>
/// <item>a commend (<see cref="HostCommend"/>, the button by the screen — any one press, Mark 2026-11-10) locks the
/// screen on the cameraman for the rest of the fight.</item>
/// </list>
/// The camera only renders while the local view is near the screen, so the second render costs nothing elsewhere.
/// </summary>
[Title( "Hub Arena Screen" )]
public sealed class HubArenaScreen : Component
{
	[Property] public CameraComponent FeedCamera { get; set; }
	[Property] public ModelRenderer Screen { get; set; }
	[Property] public ArenaSession Arena { get; set; }

	/// <summary>Fixed shots: each object's position and facing is one camera angle.</summary>
	[Property] public List<GameObject> Angles { get; set; } = new();

	[Property, Title( "Seconds Per Angle" ), Range( 2f, 60f )]
	public float SecondsPerAngle { get; set; } = 10f;

	/// <summary>Feed resolution (16:9). Low on purpose: the pixel look, and a cheap second render.</summary>
	[Property, Title( "Feed Width (px)" ), Range( 160, 1280 )]
	public int FeedWidth { get; set; } = 480;

	/// <summary>The feed renders only while the local camera is within this distance of the screen.</summary>
	[Property, Title( "Watch Distance (m)" ), Range( 5f, 300f )]
	public float WatchDistanceMeters { get; set; } = 60f;

	/// <summary>Index into <see cref="Angles"/>, or -1 = the cameraman's view (host → all).</summary>
	[Sync( SyncFlags.FromHost )] public int AngleIndex { get; private set; }

	/// <summary>Someone commended the cameraman: the screen stays on them until the fight ends (host → all).</summary>
	[Sync( SyncFlags.FromHost )] public bool LockedOnCameraman { get; private set; }

	Texture _target;
	double _nextAngleAt;
	int _fixedCursor;

	bool IsHost => GameObject.Network is not { Active: true } || Networking.IsHost;

	bool CameramanFilming => Arena is { IsValid: true } a && a.CameramanFilming && a.CameramanPawn is not null;

	protected override void OnStart()
	{
		base.OnStart();
		var w = Math.Max( 16, FeedWidth );
		var h = Math.Max( 9, w * 9 / 16 );
		_target = Texture.CreateRenderTarget( "hub_arena_feed", ImageFormat.RGBA8888, new Vector2( w, h ) );
		if ( FeedCamera is { IsValid: true } cam )
		{
			cam.IsMainCamera = false;
			cam.RenderTarget = _target;
			cam.Enabled = false;
		}
	}

	/// <summary>The commend button's prompt for this moment.</summary>
	public string CommendPrompt =>
		!CameramanFilming ? "No cameraman this fight"
		: LockedOnCameraman ? "Locked on the cameraman"
		: "Commend Cameraman";

	/// <summary>Host: lock the screen on the cameraman for the rest of the fight (any one commend).</summary>
	public void HostCommend( GameObject pawn )
	{
		if ( !IsHost || !CameramanFilming || LockedOnCameraman )
			return;

		LockedOnCameraman = true;
		AngleIndex = -1;
		Log.Info( $"[Hub] {HubIdentity.DisplayName( pawn )} commended the cameraman — the screen stays on them." );
	}

	protected override void OnUpdate()
	{
		if ( IsHost )
			HostTickAngle();

		if ( _target is null || FeedCamera is not { IsValid: true } cam || Screen is not { IsValid: true } screen )
			return;

		var view = Scene.Camera;
		var watching = view is { IsValid: true } && view != cam
		               && (view.WorldPosition - screen.WorldPosition).Length < TerrainWorldUnits.MetersToEngine( WatchDistanceMeters );
		if ( cam.Enabled != watching )
			cam.Enabled = watching;
		if ( !watching )
			return;

		PlaceCamera( cam );

		// The renderer can rebuild its scene object (LOD / hotload); setting the attribute every frame is one map write.
		screen.SceneObject?.Attributes.Set( "HubScreen", _target );
	}

	void HostTickAngle()
	{
		var filming = CameramanFilming;
		if ( LockedOnCameraman && !filming )
			LockedOnCameraman = false;   // the fight ended: back to the rotation

		if ( LockedOnCameraman )
		{
			if ( AngleIndex != -1 )
				AngleIndex = -1;
			return;
		}

		if ( Time.NowDouble < _nextAngleAt )
			return;
		_nextAngleAt = Time.NowDouble + Math.Max( 2f, SecondsPerAngle );

		// Rotation: the fixed angles in turn; while someone films, every other shot is the cameraman's view.
		if ( filming && AngleIndex != -1 )
		{
			AngleIndex = -1;
			return;
		}

		AngleIndex = _fixedCursor++ % Math.Max( 1, Angles.Count );
	}

	void PlaceCamera( CameraComponent cam )
	{
		if ( AngleIndex < 0 && Arena?.CameramanPawn is { } man )
		{
			// The cameraman's own eye: where they stand, where they look.
			var pc = man.Components.Get<PlayerController>();
			var eye = man.WorldPosition + Vector3.Up * (pc is not null ? pc.BodyHeight * 0.9f : 64f);
			cam.WorldPosition = eye;
			cam.WorldRotation = pc is not null ? pc.EyeAngles.ToRotation() : man.WorldRotation;
			return;
		}

		if ( Angles.Count == 0 )
			return;
		var anchor = Angles[Math.Clamp( AngleIndex, 0, Angles.Count - 1 )];
		if ( anchor is not { IsValid: true } )
			return;
		cam.WorldPosition = anchor.WorldPosition;
		cam.WorldRotation = anchor.WorldRotation;
	}

	protected override void OnDestroy()
	{
		_target?.Dispose();
		_target = null;
		base.OnDestroy();
	}
}
