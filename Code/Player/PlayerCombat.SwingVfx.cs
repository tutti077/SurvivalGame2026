using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Combat presentation that replaced the debug overlay lines (Mark 2026-11-02): a white slash ribbon
/// shows where the swing just dealt damage, and a few white streaks burst off the shield when a block lands.
/// The ribbon is the damage arc itself (<see cref="MeleeAttackPath"/>, the path the host sweeps), fed from
/// <see cref="AdvanceAttackPath"/> on every peer that runs the swing (host authority or client replay) - one clean
/// swoop, never the visible blade's wrist roll (Mark 2026-11-05).
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — VFX" ), Title( "Show slash trail" )]
	public bool ShowSwingSlashTrail { get; set; } = true;

	[Property, Group( "Combat — VFX" ), Title( "Slash trail life (s)" ), Range( 0.05f, 0.5f ), Step( 0.01f )]
	public float SwingSlashTrailSeconds { get; set; } = 0.16f;

	/// <summary>How far in from the tip the ribbon reaches, as a fraction of tip to swing pivot (the shoulder).</summary>
	[Property, Group( "Combat — VFX" ), Title( "Slash width (fraction of reach)" ), Range( 0.1f, 0.9f ), Step( 0.05f )]
	public float SwingSlashWidthFraction { get; set; } = 0.45f;

	[Property, Group( "Combat — VFX" ), Title( "Slash colour" )]
	public Color SwingSlashColor { get; set; } = Color.White;

	[Property, Group( "Combat — VFX" ), Title( "Show block sparks" )]
	public bool ShowBlockSparks { get; set; } = true;

	[Property, Group( "Combat — VFX" ), Title( "Block spark length (m)" ), Range( 0.1f, 1.5f ), Step( 0.05f )]
	public float BlockSparkLengthMeters { get; set; } = 0.55f;

	[Property, Group( "Combat — VFX" ), Title( "Block spark count" ), Range( 1, 8 ), Step( 1 )]
	public int BlockSparkCount { get; set; } = 4;

	/// <summary>The glint that runs along the held weapon when a held swing becomes a heavy (at the class heavy windup).</summary>
	[Property, Group( "Combat — VFX" ), Title( "Show heavy-ready flash" )]
	public bool ShowHeavyReadyFlash { get; set; } = true;

	[Property, Group( "Combat — VFX" ), Title( "Heavy-ready flash (s)" ), Range( 0.1f, 0.8f ), Step( 0.02f )]
	public float HeavyReadyFlashSeconds { get; set; } = 0.3f;

	MeleeSwingVfx _swingVfx;

	MeleeSwingVfx ResolveSwingVfx()
	{
		if ( _swingVfx is not null )
			return _swingVfx;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() || scene.SceneWorld is null )
			return null;

		_swingVfx = new MeleeSwingVfx( scene.SceneWorld );
		return _swingVfx;
	}

	/// <summary>Ribbon samples per whole arc: the frames in between are filled so the swoop is a smooth curve at any frame rate.</summary>
	const int SlashArcSteps = 32;

	byte _slashArcType = byte.MaxValue;
	float _slashArcProgress = -1f;
	float _slashArcTime;

	/// <summary>
	/// Damage window, once per tick: extends the ribbon along the damage arc from the last fed progress to
	/// <paramref name="progress01"/> (in-between samples on interpolated times). A new swing - progress went back,
	/// another attack type, or a pause - starts a new ribbon at the start of the arc. Stabs have no arc and draw none.
	/// </summary>
	internal void FeedSwingVfxFromDamageArc( byte attackType, float progress01 )
	{
		if ( !ShowSwingSlashTrail || attackType == MeleeAttackTypes.Stab || !GameObject.IsValid() )
			return;

		var vfx = ResolveSwingVfx();
		if ( vfx is null )
			return;

		vfx.TrailSeconds = SwingSlashTrailSeconds;
		vfx.TrailColor = SwingSlashColor;

		var now = Time.Now;
		progress01 = Math.Clamp( progress01, 0f, 1f );
		var basis = GetMeleeCombatBasisRotation( attackType );

		if ( _slashArcProgress < 0f || attackType != _slashArcType || progress01 < _slashArcProgress - 1e-4f
		     || now - _slashArcTime > 0.25f )
		{
			_slashArcType = attackType;
			_slashArcProgress = 0f;
			_slashArcTime = now;
			PushSlashArcSample( vfx, attackType, 0f, basis, now, newRibbon: true );
		}

		var from = _slashArcProgress;
		if ( progress01 - from < 1e-4f )
			return;

		var steps = Math.Max( 1, (int)MathF.Ceiling( (progress01 - from) * SlashArcSteps ) );
		for ( var i = 1; i <= steps; i++ )
		{
			var f = i / (float)steps;
			PushSlashArcSample( vfx, attackType, from + (progress01 - from) * f, basis,
				_slashArcTime + (now - _slashArcTime) * f, newRibbon: false );
		}

		_slashArcProgress = progress01;
		_slashArcTime = now;
	}

	void PushSlashArcSample( MeleeSwingVfx vfx, byte attackType, float progress01, Rotation basis, float time, bool newRibbon )
	{
		MeleeAttackPath.EvaluateWorldBlade( GameObject, this, attackType, progress01, basis, out var tip, out _ );
		var pivot = MeleeAttackPath.GetSwingPivotWorld( GameObject, this, attackType, basis );
		var inner = Vector3.Lerp( tip, pivot, Math.Clamp( SwingSlashWidthFraction, 0.05f, 0.95f ) );
		vfx.PushSample( tip, inner, time, newRibbon );
	}

	/// <summary>Every frame for every pawn (<see cref="TickSceneCombatVisualizations"/>): ages the ribbon and sparks.</summary>
	internal void TickSwingVfx()
	{
		_swingVfx?.Tick( Time.Now );
	}

	/// <summary>
	/// Host: this pawn's held swing just reached the heavy windup as a heavy — every other peer plays the glint
	/// (the driving owner already played it on its own clock, <see cref="TickOwnerHeavyHold"/>).
	/// </summary>
	internal void ServerBroadcastHeavyReady()
	{
		if ( GameObject.Network is { Active: true } )
			RpcPlayHeavyReadyFlash();
		else if ( !IsLocalCombatDriver() )
			PlayHeavyReadyFlashLocal();
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcPlayHeavyReadyFlash()
	{
		if ( IsLocalCombatDriver() )
			return;

		PlayHeavyReadyFlashLocal();
	}

	void PlayHeavyReadyFlashLocal()
	{
		if ( !ShowHeavyReadyFlash )
			return;

		ResolveSwingVfx()?.StartFlash( HeavyReadyFlashSeconds, Time.Now );
	}

	/// <summary>Every rendered frame (<see cref="PlayerAnimation"/>, after the weapon is placed): the glint rides the held weapon.</summary>
	internal void FeedHeavyFlashBlade( Vector3 heel, Vector3 tip )
	{
		if ( _swingVfx is { WantsFlashBlade: true } vfx )
			vfx.SetFlashBlade( heel, tip );
	}

	internal bool WantsHeavyFlashBlade => _swingVfx is { WantsFlashBlade: true };

	/// <summary>Host: a block landed on this pawn - every peer plays the streaks off its shield.</summary>
	void ServerBroadcastBlockSparks( Vector3 hitWorld )
	{
		if ( GameObject.Network is { Active: true } )
			RpcPlayBlockSparks( hitWorld );
		else
			PlayBlockSparksLocal( hitWorld );
	}

	[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
	void RpcPlayBlockSparks( Vector3 hitWorld ) => PlayBlockSparksLocal( hitWorld );

	void PlayBlockSparksLocal( Vector3 hitWorld )
	{
		if ( !ShowBlockSparks )
			return;

		var vfx = ResolveSwingVfx();
		if ( vfx is null )
			return;

		var center = hitWorld;
		var normal = (hitWorld - WorldPosition).WithZ( 0f );
		normal = normal.LengthSquared > 1e-4f ? normal.Normal : WorldRotation.Forward;

		// The shield prop knows exactly where the guard is; without one the streaks leave the contact point.
		var anim = Components.Get<PlayerAnimation>();
		if ( anim is { IsValid: true } && anim.TryGetShieldWorld( out var shieldPos, out var shieldNormal ) )
		{
			center = shieldPos;
			normal = shieldNormal;
		}

		var controller = Components.Get<PlayerController>();
		var unitsPerMeter = (controller is { IsValid: true } ? MathF.Max( 24f, controller.BodyHeight ) : 72f) / 1.8f;
		vfx.AddSparks( center, normal, Math.Clamp( BlockSparkCount, 1, 8 ), MathF.Max( 0.1f, BlockSparkLengthMeters ) * unitsPerMeter, 0.22f, Time.Now );
	}

	protected override void OnDestroy()
	{
		_swingVfx?.Delete();
		_swingVfx = null;
		base.OnDestroy();
	}
}
