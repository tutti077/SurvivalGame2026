using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Tracks living / dead / permanent biome population slots across chunk load/unload.
/// Respawn delay and mini-boss permanent death live here (not per-frame world scans).
/// </summary>
public static class BiomePopulationRegistry
{
	sealed class SlotState
	{
		public bool PermanentDead;
		/// <summary>Despawned into <see cref="EntityChunkMemory"/> — the slot is occupied until the memory restores it (or it dies).</summary>
		public bool Remembered;
		public double RespawnAt;
		public bool WaitingRespawn;
		public WeakReference<GameObject> Living;
	}

	static readonly Dictionary<string, SlotState> Slots = new( StringComparer.Ordinal );

	public static void Clear() => Slots.Clear();

	public static bool ShouldSpawnNow( string slotKey )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) )
			return true;

		if ( !Slots.TryGetValue( slotKey, out var state ) || state is null )
			return true;

		if ( state.PermanentDead || state.Remembered )
			return false;

		if ( state.Living is not null
		     && state.Living.TryGetTarget( out var go )
		     && go.IsValid() )
			return false;

		state.Living = null;

		if ( state.WaitingRespawn )
			return Time.NowDouble >= state.RespawnAt;

		return true;
	}

	public static void NotifySpawned( string slotKey, GameObject living )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) || living is null || !living.IsValid() )
			return;

		if ( !Slots.TryGetValue( slotKey, out var state ) || state is null )
		{
			state = new SlotState();
			Slots[slotKey] = state;
		}

		state.PermanentDead = false;
		state.Remembered = false;
		state.WaitingRespawn = false;
		state.RespawnAt = 0d;
		state.Living = new WeakReference<GameObject>( living );
	}

	public static void NotifyDied( string slotKey, bool respawn, float respawnDelaySeconds )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) )
			return;

		if ( !Slots.TryGetValue( slotKey, out var state ) || state is null )
		{
			state = new SlotState();
			Slots[slotKey] = state;
		}

		state.Living = null;
		state.Remembered = false;

		if ( !respawn )
		{
			state.PermanentDead = true;
			state.WaitingRespawn = false;
			return;
		}

		state.PermanentDead = false;
		state.WaitingRespawn = true;
		state.RespawnAt = Time.NowDouble + Math.Max( 0f, respawnDelaySeconds );
	}

	/// <summary>The slot's entity left the world into <see cref="EntityChunkMemory"/>: a chunk reload must not spawn a fresh one at the slot's origin.</summary>
	public static void NotifyRemembered( string slotKey )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) )
			return;

		if ( !Slots.TryGetValue( slotKey, out var state ) || state is null )
		{
			state = new SlotState();
			Slots[slotKey] = state;
		}

		state.Living = null;
		state.Remembered = true;
	}

	public static void NotifyUnloaded( string slotKey )
	{
		if ( string.IsNullOrWhiteSpace( slotKey ) )
			return;

		if ( !Slots.TryGetValue( slotKey, out var state ) || state is null )
			return;

		state.Living = null;
		// Keep PermanentDead / WaitingRespawn so reload respects death rules.
	}
}
