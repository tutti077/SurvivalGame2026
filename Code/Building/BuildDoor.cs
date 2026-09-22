using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Swinging leaf on the wood door piece (<c>build_wood_door</c>).
/// <para>
/// The frame (two jambs + header) is the piece's static solid — a <see cref="ModelCollider"/>
/// straight from the frame vmdl, so the doorway is a real hole. The old prefab carried a full
/// module <see cref="BoxCollider"/>, which filled the doorway in and made the "door" a wall.
/// </para>
/// <para>
/// The leaf hangs on a hinge child at one jamb and yaws open <b>away from whoever used it</b>:
/// the side is chosen from the user's position in door-local space at the moment it opens, never
/// a fixed hand. The host owns <see cref="IsOpen"/> / <see cref="SwingYawSign"/> (<c>[Sync]</c>);
/// every machine animates the hinge from that state, so proxies never fight a networked
/// transform. The leaf collider is keyframed (never <c>Static</c>) so physics follows the swing —
/// a Static body moved every frame is what made the old collider misbehave.
/// </para>
/// <para>
/// Toggling schedules the same nav rebake a placement does: a closed leaf blocks the mesh (so an
/// entity with no route breaches the door, three hits like any wood piece) and an open one lets
/// the path run through the doorway.
/// </para>
/// </summary>
[Title( "Build Door" )]
public sealed class BuildDoor : Component
{
	public const string HingeChildName = "DoorHinge";
	public const string LeafChildName = "DoorLeaf";

	/// <summary>Leaf objects carry this so nav static-promotion leaves the keyframed leaf alone.</summary>
	public const string LeafTag = "doorleaf";

	[Property, Group( "Door" ), Title( "Open angle (degrees)" ), Range( 45f, 135f )]
	public float OpenAngleDegrees { get; set; } = 90f;

	[Property, Group( "Door" ), Title( "Swing speed (degrees/s)" ), Range( 60f, 720f )]
	public float SwingDegreesPerSecond { get; set; } = 270f;

	[Property, Group( "Door" ), Title( "Use reach (m)" ), Range( 1f, 6f )]
	public float UseReachMeters { get; set; } = 3f;

	[Sync] public bool IsOpen { get; private set; }

	/// <summary>Hinge yaw sign for the current / last opening (+1 or -1) — picked per open from the user's side.</summary>
	[Sync] public int SwingYawSign { get; private set; } = 1;

	GameObject _hinge;
	GameObject _leaf;
	BuildPiece _piece;

	/// <summary>Leaf width runs along local Y (thin on X) — the kit's mesh frame. Measured, not assumed.</summary>
	bool _widthOnY = true;
	float _yaw;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	bool IsPreviewGhost => _piece is { IsPreviewGhost: true } || GameObject.Tags.Has( "buildpreview" );

	protected override void OnStart()
	{
		base.OnStart();
		_piece = Components.Get<BuildPiece>();
		LayoutLeaf();
		// Late joiners: snap to the synced state instead of swinging from closed.
		_yaw = TargetYaw;
		ApplyHingeYaw();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( IsPreviewGhost || _hinge is null || !_hinge.IsValid() )
			return;

		var target = TargetYaw;
		if ( Math.Abs( _yaw - target ) < 0.01f )
			return;

		_yaw = _yaw.Approach( target, SwingDegreesPerSecond * Time.Delta );
		ApplyHingeYaw();
	}

	float TargetYaw => IsOpen ? OpenAngleDegrees * Math.Sign( SwingYawSign == 0 ? 1 : SwingYawSign ) : 0f;

	void ApplyHingeYaw()
	{
		if ( _hinge is not null && _hinge.IsValid() )
			_hinge.LocalRotation = Rotation.FromYaw( _yaw );
	}

	/// <summary>
	/// Host: open toward the far side of <paramref name="user"/>, or close. Reach is the caller's
	/// job (owner trace, host re-check in the RPC) — this only commits the state.
	/// </summary>
	public void HostToggle( GameObject user )
	{
		if ( !HasHostAuthority || IsPreviewGhost || !GameObject.IsValid() )
			return;

		if ( !IsOpen && user.IsValid() )
		{
			// Which face of the door is the user on? Then yaw the leaf toward the other face.
			// Leaf extends along +width from the hinge; yaw +90° turns +Y onto -X (and +X onto +Y).
			var local = WorldTransform.PointToLocal( user.WorldPosition );
			var userSide = (_widthOnY ? local.x : local.y) >= 0f ? 1 : -1;
			SwingYawSign = _widthOnY ? userSide : -userSide;
		}

		IsOpen = !IsOpen;

		// Same bake a placement gets — the leaf is part of the piece's solid for entities.
		BuildNavMeshSync.OnBuildPieceChanged( Scene, GameObject );
	}

	/// <summary>Host re-validation for a client toggle: pawn within use reach plus one module of slack.</summary>
	public bool IsWithinUseReach( GameObject pawn )
	{
		if ( !pawn.IsValid() || !GameObject.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, UseReachMeters ) + BuildModuleDimensions.ModuleMeters );
		return Vector3.DistanceBetween( pawn.WorldPosition, GameObject.WorldPosition ) <= reach;
	}

	/// <summary>
	/// Hinge at one jamb, leaf hanging off it so it sits centred in the doorway with an equal gap
	/// each side, bottom on the sill. Sizes come from the authored models — nothing typed by hand.
	/// </summary>
	void LayoutLeaf()
	{
		_hinge = FindChild( GameObject, HingeChildName );
		_leaf = _hinge is not null ? FindChild( _hinge, LeafChildName ) : null;
		if ( _hinge is null || _leaf is null )
		{
			Log.Warning( $"[BuildDoor] {GameObject.Name} is missing its {HingeChildName}/{LeafChildName} children — author them on the prefab." );
			return;
		}

		var leafModel = _leaf.Components.Get<ModelRenderer>()?.Model;
		if ( leafModel is null || !leafModel.IsValid() )
			return;

		var pieceId = _piece?.PieceId ?? string.Empty;
		var frameHalf = BuildPieceModelCache.GetHalfExtents( pieceId );
		var frameCenter = BuildPieceModelCache.GetCenter( pieceId );

		var leafSize = leafModel.Bounds.Size;
		var leafHalf = leafSize * 0.5f;
		_widthOnY = leafSize.y >= leafSize.x;
		// Explicit axes — s&box's Vector3.Forward is +X and Right is -Y, so the named constants mislead here.
		var widthAxis = _widthOnY ? new Vector3( 0f, 1f, 0f ) : new Vector3( 1f, 0f, 0f );
		var halfWidth = _widthOnY ? leafHalf.y : leafHalf.x;

		// Build pieces are 50 u/m (BuildColliderSnap.PrefabColliderSize) — convert the doorway height once, here.
		var openingHeight = BuildModuleDimensions.DoorOpeningHeightMeters * BuildColliderSnap.PrefabColliderSize.z;
		var sillGap = Math.Max( 0f, (openingHeight - leafSize.z) * 0.5f );

		_hinge.LocalPosition = frameCenter + widthAxis * -halfWidth + new Vector3( 0f, 0f, -frameHalf.z + sillGap + leafHalf.z );
		_hinge.LocalScale = Vector3.One;

		_leaf.LocalPosition = widthAxis * halfWidth;
		_leaf.LocalRotation = Rotation.Identity;
		_leaf.LocalScale = Vector3.One;
		_leaf.Tags.Add( LeafTag );

		var box = _leaf.Components.Get<BoxCollider>();
		if ( box is null )
			return;

		box.Center = Vector3.Zero;
		box.Scale = leafSize;
		if ( IsPreviewGhost )
			return;

		// Keyframed: the body follows the hinge. Static would freeze the solid at the closed pose.
		box.Static = false;
		box.IsTrigger = false;
		box.Enabled = true;
	}

	static GameObject FindChild( GameObject parent, string name )
	{
		foreach ( var child in parent.Children )
		{
			if ( child.IsValid() && child.Name == name )
				return child;
		}

		return null;
	}

	/// <summary>
	/// Forgiveness around the crosshair for the door prompt (units, ≈0.2 m at build scale). The
	/// open leaf is 3 units thick edge-on, so a hairline ray needs a perfect angle to catch it.
	/// </summary>
	const float FocusSweepRadius = 8f;

	/// <summary>
	/// Placed door whose <b>leaf</b> is under the viewer's crosshair within <paramref name="reachMeters"/>.
	/// A short sphere sweep rather than a ray, so the skinny edge of an open leaf still reads from
	/// any side. The door's own frame never blocks its leaf; anything else in front does. Ghosts never count.
	/// </summary>
	public static bool TryFindFocusedDoor( GameObject viewer, float reachMeters, out BuildDoor door )
	{
		door = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		// Direction from the camera, origin from the pawn's eye (same as the chest prompt). The camera
		// itself ends up inside the 3-unit leaf when the pawn is pressed against the door, and the
		// third-person "push past the pawn" origin lands beyond the door — a sweep that starts inside
		// or behind a solid never hits it. The eye is always inside the capsule, so never inside the leaf.
		var cam = BuildViewCamera.Resolve( viewer );
		if ( !cam.IsValid() )
			return false;

		var direction = cam.WorldRotation.Forward.Normal;
		if ( direction.LengthSquared < 1e-8f )
			return false;

		var origin = ResolveEyeOrigin( viewer );

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		FocusHits.Clear();
		FocusHits.AddRange( scene.Trace.Ray( origin, origin + direction * reach )
			.Radius( FocusSweepRadius )
			.IgnoreGameObjectHierarchy( viewer )
			.RunAll() );
		FocusHits.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );

		for ( var i = 0; i < FocusHits.Count; i++ )
		{
			var hit = FocusHits[i];
			if ( !hit.Hit || hit.GameObject is null || !hit.GameObject.IsValid() )
				continue;

			var candidate = ResolveHit( hit.GameObject, out var onLeaf );

			// Something that is not a door is nearer than any leaf — the view is blocked.
			if ( candidate is null )
				return false;

			// Frame (jamb / header) of a door: just wall, but it never hides its own leaf.
			if ( !onLeaf )
				continue;

			if ( !candidate.Enabled || candidate.IsPreviewGhost )
				continue;

			door = candidate;
			return true;
		}

		return false;
	}

	static readonly List<SceneTraceResult> FocusHits = new();

	/// <summary>Door on the hit hierarchy, and whether the walk up passed the leaf on the way (else it was the frame).</summary>
	static BuildDoor ResolveHit( GameObject hitObject, out bool onLeaf )
	{
		onLeaf = false;
		for ( var go = hitObject; go.IsValid(); go = go.Parent )
		{
			if ( go.Name == LeafChildName || go.Tags.Has( LeafTag ) )
				onLeaf = true;

			var candidate = go.Components.Get<BuildDoor>();
			if ( candidate is not null )
				return candidate;
		}

		onLeaf = false;
		return null;
	}

	/// <summary>Pawn eye point: inside the capsule whatever the camera does (mirrors the container prompt).</summary>
	static Vector3 ResolveEyeOrigin( GameObject viewer )
	{
		var pc = viewer.Components.Get<PlayerController>();
		var eyeHeight = pc is not null && pc.IsValid()
			? Math.Max( 8f, pc.BodyHeight - pc.EyeDistanceFromTop )
			: 64f;

		return viewer.WorldPosition + Vector3.Up * eyeHeight;
	}
}
