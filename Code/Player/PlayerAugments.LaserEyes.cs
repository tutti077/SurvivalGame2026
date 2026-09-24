using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Laser Eyes (key-bind one-shot). For EffectSeconds two red beams run from the eyes to wherever
/// the crosshair points. The owner aims (view-ray trace → <see cref="LaserEndWorld"/>, synced), the
/// host re-traces eye → end point every <see cref="LaserTickSeconds"/> and applies EffectScale
/// damage to a living damageable it touches, and every client draws the beams from the synced state.
/// </summary>
public sealed partial class PlayerAugments
{
	public const float LaserTickSeconds = 0.2f;
	const float LaserDefaultRangeMeters = 30f;
	const float LaserEyeSpacing = 1.3f;

	static readonly Color LaserColor = new( 1f, 0.08f, 0.05f );

	/// <summary>Owner-authored: beams on. Mirrors to every peer.</summary>
	[Sync] public bool LaserActive { get; private set; }

	/// <summary>Owner-authored: where the crosshair beam lands this frame.</summary>
	[Sync] public Vector3 LaserEndWorld { get; private set; }

	double _laserUntil;
	double _laserNextDamageAt;
	SkinnedModelRenderer _laserBody;

	/// <summary>Owner: one-shot activation from the key bind. Always succeeds — the beam just starts.</summary>
	bool StartLaserEyes( AugmentDefinition def )
	{
		_laserUntil = Time.NowDouble + Math.Max( 0.2f, def.EffectSeconds > 0f ? def.EffectSeconds : 3f );
		_laserNextDamageAt = 0;
		TickLaserAim( def );
		return true;
	}

	/// <summary>Owner, every frame: keep the end point on the crosshair, end the beam on time.</summary>
	void TickLaserEyesOwner()
	{
		if ( _laserUntil <= 0 )
			return;

		if ( Time.NowDouble >= _laserUntil || !TryGetActiveDefinition( AugmentAbility.LaserEyes, out var def ) )
		{
			_laserUntil = 0;
			LaserActive = false;
			return;
		}

		TickLaserAim( def );
	}

	void TickLaserAim( AugmentDefinition def )
	{
		var range = TerrainWorldUnits.MetersToEngine( def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : LaserDefaultRangeMeters );
		if ( !BuildViewCamera.TryGetViewRay( GameObject, out var origin, out var direction ) || direction.LengthSquared < 1e-8f )
		{
			origin = WorldPosition + Vector3.Up * 64f;
			direction = WorldRotation.Forward;
		}

		var end = origin + direction.Normal * range;
		var tr = Scene.Trace.Ray( origin, end )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( "buildpreview" )
			.Run();

		LaserEndWorld = tr.Hit ? tr.HitPosition : end;
		LaserActive = true;
	}

	/// <summary>Host, every frame: damage ticks along the synced beam. Runs for local and remote owners alike.</summary>
	void TickLaserEyesHost()
	{
		if ( !LaserActive || Time.NowDouble < _laserNextDamageAt )
			return;

		_laserNextDamageAt = Time.NowDouble + LaserTickSeconds;

		if ( !TryGetActiveDefinition( AugmentAbility.LaserEyes, out var def ) )
			return;

		var range = TerrainWorldUnits.MetersToEngine( def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : LaserDefaultRangeMeters );
		var (left, right) = ResolveEyePositions();
		var eye = ( left + right ) * 0.5f;
		var toEnd = LaserEndWorld - eye;
		if ( toEnd.LengthSquared < 1e-4f || toEnd.Length > range * 1.25f )
			return;

		// Re-trace from the actual eyes: walls between the pawn and the claimed end point block the beam.
		var tr = Scene.Trace.Ray( eye, eye + toEnd.Normal * MathF.Min( toEnd.Length + 4f, range ) )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( "buildpreview" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return;

		if ( !CombatAuthority.TryFindDamageable( tr.GameObject, out var receiver ) || receiver is not DamageReceiver dmg )
			return;

		if ( !CombatAuthority.MayApplyMeleeDamageFromAttackerToReceiver( GameObject, dmg ) || !CombatAuthority.IsDamageVictimAlive( dmg ) )
			return;

		dmg.TakeDamage( Math.Max( 0.1f, def.EffectScale ), Components.Get<PlayerCombat>() ?? (Component)this );
	}

	/// <summary>Every client, every frame: two beams from the eyes to the synced end point.</summary>
	void DrawLaserEyes()
	{
		if ( !LaserActive )
			return;

		var (left, right) = ResolveEyePositions();
		DebugOverlay.Line( left, LaserEndWorld, LaserColor, 0f );
		DebugOverlay.Line( right, LaserEndWorld, LaserColor, 0f );
	}

	(Vector3 Left, Vector3 Right) ResolveEyePositions()
	{
		if ( _laserBody is null || !_laserBody.IsValid() )
			_laserBody = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );

		Transform eyes;
		var attachment = _laserBody is not null && _laserBody.IsValid() ? _laserBody.GetAttachment( "eyes" ) : null;
		if ( attachment.HasValue )
		{
			eyes = attachment.Value;
		}
		else
		{
			var controller = Components.Get<PlayerController>();
			var bodyHeight = controller is not null && controller.IsValid() ? Math.Max( 24f, controller.BodyHeight ) : 72f;
			eyes = new Transform( WorldPosition + Vector3.Up * ( bodyHeight * 0.92f ), WorldRotation );
		}

		var side = eyes.Rotation.Left * LaserEyeSpacing;
		return (eyes.Position + side, eyes.Position - side);
	}
}
