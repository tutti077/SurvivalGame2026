using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Medusa Eye (passive, once per cooldown). Hold a robot or ascended entity in the middle third of
/// the screen — the same stare test the fox uses to get spooked — for EffectSeconds with clear line
/// of sight and it freezes in place for EffectScale seconds (host pin, like a trap hold). Only
/// machines and the ascended are affected; ferals and animals ignore the stare.
/// </summary>
public sealed partial class PlayerAugments
{
	const float MedusaPollSeconds = 0.1f;
	const float MedusaDefaultRangeMeters = 25f;
	const float MedusaDefaultFreezeSeconds = 5f;
	const float MedusaEntityEyeHeight = 60f;
	const float MedusaPlayerEyeHeight = 64f;

	GameObject _medusaStareTarget;
	double _medusaStareStartedAt;
	double _medusaNextPollAt;
	double _medusaLocalReadyAt;

	/// <summary>Owner-side progress for feedback: 0..1 of the stare needed, or 0 when nothing is held.</summary>
	public float MedusaStare01 { get; private set; }

	string _medusaReadout = string.Empty;

	void TickMedusaEye()
	{
		DrawMedusaReadout();

		if ( Time.NowDouble < _medusaNextPollAt )
			return;

		_medusaNextPollAt = Time.NowDouble + MedusaPollSeconds;

		if ( !IsAbilityOn( AugmentAbility.MedusaEye ) || !TryGetActiveDefinition( AugmentAbility.MedusaEye, out var def ) )
		{
			_medusaReadout = string.Empty;
			ResetMedusaStare();
			return;
		}

		if ( Time.NowDouble < _medusaLocalReadyAt )
		{
			_medusaReadout = $"Medusa Eye: recharging {_medusaLocalReadyAt - Time.NowDouble:0}s";
			ResetMedusaStare();
			return;
		}

		var target = FindStaredMachine( def, out var why );
		if ( target is null )
		{
			_medusaReadout = $"Medusa Eye: ready — {why}";
			ResetMedusaStare();
			return;
		}

		if ( _medusaStareTarget != target )
		{
			_medusaStareTarget = target;
			_medusaStareStartedAt = Time.NowDouble;
		}

		var needed = Math.Max( 0.5f, def.EffectSeconds > 0f ? def.EffectSeconds : 5f );
		var held = (float)( Time.NowDouble - _medusaStareStartedAt );
		MedusaStare01 = Math.Clamp( held / needed, 0f, 1f );
		_medusaReadout = $"Medusa Eye: staring {held:0.0} / {needed:0.0}s";
		if ( held < needed )
			return;

		// Stared long enough — petrify. The host owns the pin and the real cooldown.
		_medusaLocalReadyAt = Time.NowDouble + def.ResolvedCooldownSeconds;
		_medusaReadout = "Medusa Eye: PETRIFY";
		Log.Info( $"[MedusaEye] {GameObject.Name} stared down {target.Name} — requesting freeze." );
		ResetMedusaStare();

		if ( HasHostAuthority )
			HostMedusaFreeze( target.Id );
		else
			RpcHostMedusaFreeze( target.Id );
	}

	void ResetMedusaStare()
	{
		_medusaStareTarget = null;
		_medusaStareStartedAt = 0;
		MedusaStare01 = 0f;
	}

	/// <summary>Small line under the crosshair while the passive is installed: ready / staring / recharging, and why nothing is held.</summary>
	void DrawMedusaReadout()
	{
		if ( string.IsNullOrWhiteSpace( _medusaReadout ) )
			return;

		var size = Screen.Size;
		DebugOverlay.ScreenText( new Vector2( size.x * 0.5f - 120f, size.y * 0.5f + 40f ), _medusaReadout, size: 14f );
	}

	/// <summary>Nearest living robot / ascended entity whose eye sits in the middle third of the local screen with clear line of sight.</summary>
	GameObject FindStaredMachine( AugmentDefinition def, out string why )
	{
		why = "no camera";
		var cam = Scene.Camera;
		if ( cam is null || !cam.IsValid() )
			return null;

		var size = Screen.Size;
		if ( size.x < 1f || size.y < 1f )
			return null;

		var range = TerrainWorldUnits.MetersToEngine( def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : MedusaDefaultRangeMeters );
		var playerEye = WorldPosition + Vector3.Up * MedusaPlayerEyeHeight;

		GameObject best = null;
		var bestDist = float.MaxValue;
		var machines = 0;
		var inRange = 0;
		var centred = 0;
		foreach ( var vitals in Scene.GetAllComponents<EntityVitals>() )
		{
			if ( vitals is null || !vitals.IsValid() || vitals.IsDead || !vitals.IsMachineOrAscended )
				continue;

			machines++;
			var root = vitals.GameObject;
			var dist = Vector3.DistanceBetween( WorldPosition, root.WorldPosition );
			if ( dist > range || dist >= bestDist )
				continue;

			inRange++;
			var eye = root.WorldPosition + Vector3.Up * MedusaEntityEyeHeight;
			var px = cam.BBoxToScreenPixels( BBox.FromPositionAndSize( eye, 4f ), out var onScreen );
			if ( !onScreen && px.Width < 0.5f && px.Height < 0.5f )
				continue;

			var cx = px.Left + px.Width * 0.5f;
			var cy = px.Top + px.Height * 0.5f;
			var isCentred = cx >= size.x / 3f && cx <= size.x * ( 2f / 3f )
			                && cy >= size.y / 3f && cy <= size.y * ( 2f / 3f );
			if ( !isCentred )
				continue;

			centred++;
			if ( !HasStareLos( playerEye, eye, root ) )
				continue;

			best = root;
			bestDist = dist;
		}

		why = machines == 0 ? "no robot / ascended entity in the scene"
			: inRange == 0 ? $"{machines} machine(s), none within range"
			: centred == 0 ? $"{inRange} in range, none in the middle of the screen"
			: best is null ? "target centred but line of sight blocked"
			: "ok";
		return best;
	}

	/// <summary>Straight ray from the player's eye to the entity's eye; only geometry that is not the target itself blocks it.</summary>
	bool HasStareLos( Vector3 from, Vector3 to, GameObject targetRoot )
	{
		var tr = Scene.Trace.Ray( from, to )
			.IgnoreGameObjectHierarchy( GameObject.Root )
			.WithoutTags( "buildpreview" )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return true;

		for ( var p = tr.GameObject; p.IsValid(); p = p.Parent )
		{
			if ( p == targetRoot )
				return true;
		}

		return false;
	}

	[Rpc.Host]
	void RpcHostMedusaFreeze( Guid entityId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		HostMedusaFreeze( entityId );
	}

	void HostMedusaFreeze( Guid entityId )
	{
		if ( !HasHostAuthority || !TryGetActiveDefinition( AugmentAbility.MedusaEye, out var def ) )
			return;

		var root = Scene.Directory.FindByGuid( entityId );
		if ( root is null || !root.IsValid() )
		{
			Log.Warning( "[MedusaEye] host: target entity not found." );
			return;
		}

		var vitals = root.Components.Get<EntityVitals>();
		var locomotion = root.Components.Get<EntityLocomotion>();
		if ( vitals is null || vitals.IsDead || !vitals.IsMachineOrAscended || locomotion is null || !locomotion.IsValid() )
		{
			Log.Warning( $"[MedusaEye] host: {root.Name} refused — kind={vitals?.Kind} dead={vitals?.IsDead} locomotion={(locomotion is not null)}" );
			return;
		}

		var range = TerrainWorldUnits.MetersToEngine( def.EffectRadiusMeters > 0f ? def.EffectRadiusMeters : MedusaDefaultRangeMeters );
		if ( Vector3.DistanceBetween( WorldPosition, root.WorldPosition ) > range * 1.25f )
			return;

		// One petrify per cooldown, on the host's clock.
		if ( !HostTryConsumePassiveCooldown( AugmentAbility.MedusaEye ) )
		{
			Log.Info( "[MedusaEye] host: still on cooldown." );
			return;
		}

		Log.Info( $"[MedusaEye] host: petrified {root.Name}." );

		var freeze = def.EffectScale > 0f ? def.EffectScale : MedusaDefaultFreezeSeconds;
		locomotion.HostSetTrapped( true, root.WorldPosition );
		_hostPins.Add( (locomotion, Time.NowDouble + freeze) );
		// Petrified: no swings, no chase, grey tint — a pinned foot alone still lets it hit you.
		root.Components.Get<EntityBrain>()?.HostStun( freeze );
	}
}
