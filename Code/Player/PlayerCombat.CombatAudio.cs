using Sandbox;

namespace Survival;

/// <summary>The melee exchange outcomes that each get their own sound (Mark 2026-11-14, split 2026-11-16).</summary>
public enum CombatAudioCue : byte
{
	/// <summary>A swing landed on another player.</summary>
	HitPlayer = 0,
	/// <summary>A swing landed on anything else that takes damage (scavs, animals, damageable props).</summary>
	HitOther = 1,
	/// <summary>A light swing stopped by the right guard.</summary>
	Blocked = 2,
	/// <summary>A heavy swing stopped by the right guard (the blocker is staggered).</summary>
	BlockedHeavy = 3,
	/// <summary>A chamber: two blades met, the defender's own swing intercepted the incoming one.</summary>
	Chamber = 4,
}

/// <summary>
/// Combat audio cues (Mark 2026-11-14: "different audio cues for blocked attack, landed a hit, blocked heavy attack,
/// chamber an attack"). The host decides every outcome, so it broadcasts the cue once and every peer plays it at the
/// contact point, always on the <b>attacker's</b> combat component: the swing runtime for a
/// landed hit, <see cref="ServerApplyBlockOutcome"/> for block / heavy block / chamber. A swing that hits nothing is
/// silent (the miss swoosh was removed 2026-11-16, Mark: "that's bad ... it's playing mid-other audio clips"). Each cue has an inspector <see cref="SoundEvent"/> slot; empty slots play the
/// project's own events in <c>Assets/sounds/combat/</c>, synthesised by <c>Tools/combat_sounds/generate_combat_sounds.py</c>:
/// a fleshy thud for a hit on a player and a duller clank for anything else (Mark 2026-11-16: the thud "sounds like we're
/// hitting corrugated metal ... keep that for when we hit anything but another player"; 4 variants each),
/// a dry stick clack for a block - "CLACK CLACK" (5), clack + wooden clunk for a blocked heavy (3), and a double clack for a
/// chamber, fixed pitch so it is always the same. A parry "ting" (<c>melee_parry.sound</c>) is ready for the timed parry,
/// which does not exist yet - no cue plays it. The events add a little random pitch.
/// </summary>
public partial class PlayerCombat
{
	[Property, Group( "Combat — Audio" ), Title( "Hit a player sound (empty = sounds/combat/melee_hit_player)" )]
	public SoundEvent HitPlayerSound { get; set; }

	[Property, Group( "Combat — Audio" ), Title( "Hit anything else sound (empty = sounds/combat/melee_hit_other)" )]
	public SoundEvent HitOtherSound { get; set; }

	[Property, Group( "Combat — Audio" ), Title( "Blocked sound (empty = sounds/combat/melee_block)" )]
	public SoundEvent BlockedSound { get; set; }

	[Property, Group( "Combat — Audio" ), Title( "Blocked heavy sound (empty = sounds/combat/melee_block_heavy)" )]
	public SoundEvent BlockedHeavySound { get; set; }

	[Property, Group( "Combat — Audio" ), Title( "Chamber sound (empty = sounds/combat/melee_chamber)" )]
	public SoundEvent ChamberSound { get; set; }

	/// <summary>Host, tests only: the last cue this component broadcast and how many it has.</summary>
	internal CombatAudioCue TestLastCue { get; private set; }
	internal int TestCueCount { get; private set; }

	/// <summary>The project event each cue plays when its inspector slot is empty.</summary>
	static string CueDefaultPath( CombatAudioCue cue ) => cue switch
	{
		CombatAudioCue.HitPlayer => "sounds/combat/melee_hit_player.sound",
		CombatAudioCue.HitOther => "sounds/combat/melee_hit_other.sound",
		CombatAudioCue.Blocked => "sounds/combat/melee_block.sound",
		CombatAudioCue.BlockedHeavy => "sounds/combat/melee_block_heavy.sound",
		_ => "sounds/combat/melee_chamber.sound",
	};

	SoundEvent CueOverride( CombatAudioCue cue ) => cue switch
	{
		CombatAudioCue.HitPlayer => HitPlayerSound,
		CombatAudioCue.HitOther => HitOtherSound,
		CombatAudioCue.Blocked => BlockedSound,
		CombatAudioCue.BlockedHeavy => BlockedHeavySound,
		_ => ChamberSound,
	};

	/// <summary>Host: play <paramref name="cue"/> at <paramref name="at"/> on every peer.</summary>
	internal void ServerBroadcastCombatCue( CombatAudioCue cue, Vector3 at )
	{
		TestLastCue = cue;
		TestCueCount++;
		if ( GameObject.Network is { Active: true } )
			RpcPlayCombatCue( (byte)cue, at );
		else
			PlayCombatCueLocal( cue, at );
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcPlayCombatCue( byte cue, Vector3 at ) => PlayCombatCueLocal( (CombatAudioCue)cue, at );

	void PlayCombatCueLocal( CombatAudioCue cue, Vector3 at )
	{
		// The event carries its own variants, random pitch and volume.
		var evt = CueOverride( cue ) ?? ResourceLibrary.Get<SoundEvent>( CueDefaultPath( cue ) );
		if ( evt is null )
			return;
		Sound.Play( evt, at );
	}
}
