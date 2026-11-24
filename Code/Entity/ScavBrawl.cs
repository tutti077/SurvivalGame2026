using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Scav-vs-scav brawl for tuning the difficulty ladder (Mark 2026-11-15: "pit like a lvl 5 scav against 5 level 1s? The
/// level 5 should win, that's about where the average experienced player will be"). <c>scav_brawl 5 1 5 3</c> spawns a
/// champion at one level and a mob at another 10 m in front of the host's pawn, each on its own
/// <see cref="EntityBrain.BrawlTeam"/> with its own <see cref="EntityCombat.DifficultyLevel"/>, and a transient referee
/// that plays the rounds one after another and logs <c>[Brawl]</c> lines and a <c>RESULT</c> tally. The fighters use the
/// normal scav path end to end — chase, attack tokens (2 attackers at a time, as against a player), duel AI, stamina,
/// staggers, friendly fire inside the mob.
/// </summary>
public static class ScavBrawl
{
	const string PrefabPath = "prefabs/entity/scavT1.prefab";

	[ConCmd( "scav_brawl" )]
	public static void ConCmdBrawl( int championLevel = 5, int mobLevel = 1, int mobCount = 5, int rounds = 1 )
	{
		if ( Networking.IsActive && !Networking.IsHost )
		{
			Log.Warning( "[Brawl] host only." );
			return;
		}

		var scene = Sandbox.Game.ActiveScene;
		PlayerVitals pawn = null;
		foreach ( var v in scene.GetAllComponents<PlayerVitals>() )
		{
			if ( !v.IsProxy && v.Components.Get<PlayerController>() is { Enabled: true } )
				pawn = v;
		}

		if ( pawn is null )
		{
			Log.Warning( "[Brawl] no local pawn to stage the brawl in front of." );
			return;
		}

		ConCmdStop();
		var forward = pawn.WorldRotation.Forward.WithZ( 0f );
		forward = forward.LengthSquared < 1e-6f ? Vector3.Forward : forward.Normal;
		var go = new GameObject( true, "ScavBrawlReferee" );
		var referee = go.Components.Create<ScavBrawlReferee>();
		referee.Setup( pawn.WorldPosition + forward * TerrainWorldUnits.MetersToEngine( 10f ), forward,
			Math.Clamp( championLevel, 1, ScavCombatStyleCatalog.LevelCount ), Math.Clamp( mobLevel, 1, ScavCombatStyleCatalog.LevelCount ),
			Math.Clamp( mobCount, 1, 8 ), Math.Clamp( rounds, 1, 20 ) );
	}

	[ConCmd( "scav_brawl_stop" )]
	public static void ConCmdStop()
	{
		foreach ( var referee in Sandbox.Game.ActiveScene.GetAllComponents<ScavBrawlReferee>().ToList() )
			referee.EndAll( "stopped" );
	}

	internal static GameObject SpawnFighter( Scene scene, Vector3 position, Vector3 facing, int team, int level )
	{
		var bot = EnemySpawnButton.HostSpawn( scene, PrefabPath, EnemyType.Scav, 1, position, Rotation.LookAt( facing, Vector3.Up ) );
		if ( bot is null )
			return null;

		if ( bot.Components.Get<EntityBrain>() is { } brain )
			brain.BrawlTeam = team;
		if ( bot.Components.Get<EntityCombat>() is { } combat )
			combat.DifficultyLevel = level;
		bot.Name = $"brawl_t{team}_L{level}";
		return bot;
	}
}

/// <summary>Transient referee of a <see cref="ScavBrawl"/>: stages each round, watches who is left, tallies, cleans up.</summary>
public sealed class ScavBrawlReferee : Component
{
	const float RoundTimeoutSeconds = 150f;

	Vector3 _center, _forward;
	int _championLevel, _mobLevel, _mobCount, _rounds;
	int _round, _championWins, _mobWins, _draws;
	readonly List<GameObject> _champions = new();
	readonly List<GameObject> _mob = new();
	double _roundStartedAt, _cleanupAt = double.NaN;

	internal void Setup( Vector3 center, Vector3 forward, int championLevel, int mobLevel, int mobCount, int rounds )
	{
		_center = center;
		_forward = forward;
		_championLevel = championLevel;
		_mobLevel = mobLevel;
		_mobCount = mobCount;
		_rounds = rounds;
		Log.Info( $"[Brawl] level {_championLevel} {ScavCombatStyleCatalog.Get( _championLevel ).Name} vs {_mobCount} x level {_mobLevel} {ScavCombatStyleCatalog.Get( _mobLevel ).Name}, {_rounds} round(s)" );
		StartRound();
	}

	void StartRound()
	{
		_round++;
		_champions.Clear();
		_mob.Clear();
		var right = Vector3.Cross( _forward, Vector3.Up ).Normal;
		var champion = ScavBrawl.SpawnFighter( Scene, _center - _forward * TerrainWorldUnits.MetersToEngine( 3f ), _forward, 1, _championLevel );
		if ( champion is not null )
			_champions.Add( champion );
		for ( var i = 0; i < _mobCount; i++ )
		{
			var lateral = (i - (_mobCount - 1) * 0.5f) * TerrainWorldUnits.MetersToEngine( 1.6f );
			var pos = _center + _forward * TerrainWorldUnits.MetersToEngine( 3f ) + right * lateral;
			if ( ScavBrawl.SpawnFighter( Scene, pos, -_forward, 2, _mobLevel ) is { } bot )
				_mob.Add( bot );
		}

		_roundStartedAt = Time.NowDouble;
		_cleanupAt = double.NaN;
	}

	static int Alive( List<GameObject> side ) =>
		side.Count( g => g.IsValid() && g.Components.Get<EntityVitals>() is { IsDead: false } );

	static float Health( List<GameObject> side ) =>
		side.Where( g => g.IsValid() ).Sum( g => g.Components.Get<EntityVitals>() is { IsDead: false } v ? v.CurrentHealth : 0f );

	protected override void OnUpdate()
	{
		if ( !double.IsNaN( _cleanupAt ) )
		{
			if ( Time.NowDouble < _cleanupAt )
				return;

			DestroyFighters();
			if ( _round >= _rounds )
				EndAll( "done" );
			else
				StartRound();
			return;
		}

		var elapsed = Time.NowDouble - _roundStartedAt;
		var champs = Alive( _champions );
		var mob = Alive( _mob );
		string outcome = null;
		if ( champs == 0 && mob == 0 )
			outcome = "both sides down";
		else if ( mob == 0 )
		{
			_championWins++;
			outcome = $"level {_championLevel} wins with {Health( _champions ):0} hp left";
		}
		else if ( champs == 0 )
		{
			_mobWins++;
			outcome = $"mob wins, {mob}/{_mobCount} standing ({Health( _mob ):0} hp total)";
		}
		else if ( elapsed > RoundTimeoutSeconds )
		{
			_draws++;
			outcome = $"timeout - champion {Health( _champions ):0} hp, mob {mob}/{_mobCount} standing";
		}

		if ( outcome is null )
			return;

		if ( outcome == "both sides down" )
			_draws++;
		var line = $"round {_round}/{_rounds} {elapsed:0.0}s: {outcome}";
		Log.Info( $"[Brawl] {line}" );
		_cleanupAt = Time.NowDouble + 2.0;
	}

	void DestroyFighters()
	{
		foreach ( var g in _champions.Concat( _mob ) )
		{
			if ( g.IsValid() )
				g.Destroy();
		}

		_champions.Clear();
		_mob.Clear();
	}

	internal void EndAll( string why )
	{
		DestroyFighters();
		Log.Info( $"[Brawl] RESULT ({why}) level {_championLevel} vs {_mobCount} x level {_mobLevel}: champion {_championWins}, mob {_mobWins}, draws {_draws} of {_round} round(s)" );
		GameObject.Destroy();
	}
}
