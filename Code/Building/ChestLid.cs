using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// A container's hinged lid (Mark 2026-11-10, prompt 25: Rumple's chest "shut by default and open when a player
/// accesses it"). Sits on the chest root beside <see cref="ContainerInventory"/>; <see cref="Lid"/> is the child that
/// renders <c>chest_lid.vmdl</c>, placed at the hinge (its model origin is the hinge axis, see
/// <c>Blender/scripts/split_chest_lid.py</c>).
///
/// Viewers are counted on the host: <see cref="PlayerInventoryInteraction"/> reports open / close of its container
/// view through <see cref="SetViewing"/>, the host keeps the set of viewing connections and syncs
/// <see cref="IsOpen"/>; every peer swings the lid toward the synced state. The swing only ticks while the angle is
/// still moving - a settled lid costs nothing per frame.
/// </summary>
[Title( "Chest Lid" )]
public sealed class ChestLid : Component
{
	[Property, Title( "Lid" )]
	public GameObject Lid { get; set; }

	/// <summary>Hinge axis in the lid child's local frame (the lid model's origin is the hinge).</summary>
	[Property, Title( "Hinge Axis (local)" )]
	public Vector3 HingeAxis { get; set; } = new( 0f, 1f, 0f );

	/// <summary>Degrees the lid swings when open. Negative swings the other way round the axis.</summary>
	[Property, Title( "Open Angle (deg)" ), Range( -150f, 150f ), Step( 1f )]
	public float OpenAngleDegrees { get; set; } = 95f;

	[Property, Title( "Open Time (s)" ), Range( 0.05f, 2f ), Step( 0.05f )]
	public float OpenSeconds { get; set; } = 0.35f;

	[Property, Title( "Close Time (s)" ), Range( 0.05f, 2f ), Step( 0.05f )]
	public float CloseSeconds { get; set; } = 0.5f;

	/// <summary>Host truth: somebody has this container's view open.</summary>
	[Sync( SyncFlags.FromHost )]
	public bool IsOpen { get; private set; }

	readonly HashSet<Guid> _viewers = new();
	Rotation _lidRestLocal = Rotation.Identity;
	bool _restCaptured;
	float _angle01;
	bool _settled = true;

	bool HasHostAuthority => GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		CaptureRest();
		// Spawn in the synced pose with no swing: a chest already open when you walk up is open.
		_angle01 = IsOpen ? 1f : 0f;
		ApplyAngle();
		_settled = true;
	}

	void CaptureRest()
	{
		if ( _restCaptured || Lid is null || !Lid.IsValid() )
			return;
		_lidRestLocal = Lid.LocalRotation;
		_restCaptured = true;
	}

	/// <summary>Finds the lid on a container's object (or above it) - null for lidless containers.</summary>
	public static ChestLid FindFor( ContainerInventory container )
	{
		if ( container is null || !container.IsValid() )
			return null;
		return container.GameObject.Components.Get<ChestLid>( FindMode.EverythingInSelfAndAncestors );
	}

	/// <summary>Client / owner entry: this pawn opened or closed the container view.</summary>
	public void SetViewing( bool viewing )
	{
		if ( HasHostAuthority && GameObject.Network is not { Active: true } )
		{
			ApplyViewer( Guid.Empty, viewing );
			return;
		}

		RpcHostSetViewing( viewing );
	}

	[Rpc.Host]
	void RpcHostSetViewing( bool viewing )
	{
		var id = Rpc.Caller?.Id ?? Guid.Empty;
		ApplyViewer( id, viewing );
	}

	void ApplyViewer( Guid connectionId, bool viewing )
	{
		if ( viewing )
			_viewers.Add( connectionId );
		else
			_viewers.Remove( connectionId );

		// A viewer that left the game never sends its close.
		if ( Networking.IsActive )
			_viewers.RemoveWhere( id => id != Guid.Empty && Connection.Find( id ) is null );

		var open = _viewers.Count > 0;
		if ( open == IsOpen )
			return;

		IsOpen = open;
		_settled = false;
	}

	protected override void OnUpdate()
	{
		var target = IsOpen ? 1f : 0f;
		if ( _settled && MathF.Abs( _angle01 - target ) < 1e-4f )
			return;

		CaptureRest();
		var seconds = Math.Max( 0.05f, IsOpen ? OpenSeconds : CloseSeconds );
		var step = Time.Delta / seconds;
		_angle01 = _angle01 < target ? MathF.Min( target, _angle01 + step ) : MathF.Max( target, _angle01 - step );
		ApplyAngle();
		_settled = MathF.Abs( _angle01 - target ) < 1e-4f;
	}

	void ApplyAngle()
	{
		if ( Lid is null || !Lid.IsValid() )
			return;

		CaptureRest();
		// Ease: a lid thrown open overshoots nothing, it just slows into place.
		var eased = _angle01 * _angle01 * (3f - 2f * _angle01);
		var axis = HingeAxis.LengthSquared > 1e-6f ? HingeAxis.Normal : Vector3.Left;
		Lid.LocalRotation = _lidRestLocal * Rotation.FromAxis( axis, OpenAngleDegrees * eased );
	}
}
