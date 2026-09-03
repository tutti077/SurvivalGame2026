using System;
using System.Linq;
using Sandbox;

namespace Survival;

/// <summary>
/// Per-pawn bridge between gameplay and <see cref="QuestTracker"/> (client-local progress).
/// <list type="bullet">
/// <item><see cref="HostReport"/> — host-validated actions (craft, kill, build, grapple): the host
/// calls it on the acting pawn; it lands on the owning client via <see cref="RpcOwnerReport"/>.</item>
/// <item><see cref="OwnerReport"/> — actions the owner already knows locally (wingsuit, augment install).</item>
/// <item>Pickups come from <see cref="PlayerInventory.ResourcePickedUp"/>, which is already owner-side.</item>
/// <item>Biome entry is polled once a second from the pawn position (owner only).</item>
/// </list>
/// Authored on <c>basicplayer.prefab</c>.
/// </summary>
[Title( "Player Quests" )]
public sealed class PlayerQuests : Component
{
	[Property] public bool LogQuests { get; set; }

	/// <summary>How often the owner samples which biome the pawn stands in.</summary>
	[Property, Title( "Biome Poll Seconds" )] public float BiomePollSeconds { get; set; } = 1f;

	PlayerInventory _inventory;
	TerrainWorldManager _world;
	bool _worldSearched;
	TimeSince _sinceBiomePoll;
	TerrainPreviewBiomeId _lastBiome = TerrainPreviewBiomeId.None;

	bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	bool IsLocalManagingClient()
	{
		if ( GameObject.Network is not { Active: true } )
			return true;

		if ( GameObject.Network.Owner is not { } owner )
			return Networking.IsHost;

		return ConnectionIdentity.SameClient( owner, Connection.Local );
	}

	bool _ownerBound;

	protected override void OnStart()
	{
		base.OnStart();
		EnsureOwnerBound();
	}

	protected override void OnDestroy()
	{
		if ( _inventory is not null )
			_inventory.ResourcePickedUp -= OnResourcePickedUp;

		base.OnDestroy();
	}

	protected override void OnUpdate()
	{
		if ( !EnsureOwnerBound() )
			return;

		TickBiomeEntry();
	}

	/// <summary>Owner-side wiring, done once ownership is known (may be after OnStart on late-joining clients).</summary>
	bool EnsureOwnerBound()
	{
		if ( _ownerBound )
			return true;

		if ( !IsLocalManagingClient() )
			return false;

		_ownerBound = true;
		QuestTracker.EnsureLoaded();

		_inventory = Components.Get<PlayerInventory>();
		if ( _inventory is not null )
			_inventory.ResourcePickedUp += OnResourcePickedUp;

		return true;
	}

	/// <summary>Host-side emitters call this on the acting pawn; the report is delivered to the owner.</summary>
	public void HostReport( string eventId, string match = null, int amount = 1 )
	{
		if ( !HasHostAuthority || string.IsNullOrWhiteSpace( eventId ) || amount <= 0 )
			return;

		if ( IsLocalManagingClient() )
		{
			Deliver( eventId, match, amount );
			return;
		}

		if ( GameObject.Network is { Active: true } && Networking.IsHost && GameObject.Network.Owner is not null )
			RpcOwnerReport( eventId, match ?? string.Empty, amount );
	}

	/// <summary>Owner-side emitters (movement, UI) call this directly; proxies ignore it.</summary>
	public void OwnerReport( string eventId, string match = null, int amount = 1 )
	{
		if ( !IsLocalManagingClient() || string.IsNullOrWhiteSpace( eventId ) || amount <= 0 )
			return;

		Deliver( eventId, match, amount );
	}

	[Rpc.Owner]
	void RpcOwnerReport( string eventId, string match, int amount )
	{
		if ( !IsLocalManagingClient() )
			return;

		Deliver( eventId, match, amount );
	}

	void Deliver( string eventId, string match, int amount )
	{
		if ( LogQuests )
			Log.Info( $"[PlayerQuests] {GameObject.Name}: {eventId} '{match}' x{amount}" );

		QuestTracker.Report( eventId, match, amount );
	}

	void OnResourcePickedUp( ResourcePickupNotice notice )
	{
		if ( notice.Amount <= 0 || string.IsNullOrWhiteSpace( notice.ResourceId ) )
			return;

		Deliver( QuestEventIds.ResourceCollected, notice.ResourceId, notice.Amount );

		if ( FoodCatalog.IsEdible( notice.ResourceId ) )
			Deliver( QuestEventIds.FoodCollected, notice.ResourceId, notice.Amount );
	}

	// ---- Kills ------------------------------------------------------------------------------

	/// <summary>Host: an entity died from <paramref name="attacker"/>. Resolves the killer's pawn and species id.</summary>
	public static void HostReportKill( Component victim, Component attacker )
	{
		var quests = FindOnAttacker( attacker );
		if ( quests is null )
			return;

		var kind = ResolveKillKind( victim );
		if ( string.IsNullOrWhiteSpace( kind ) )
			return;

		quests.HostReport( QuestEventIds.EntityKilled, kind );
	}

	/// <summary>The <see cref="PlayerQuests"/> on the pawn that owns <paramref name="attacker"/> (arrow → shooter, melee → combat).</summary>
	public static PlayerQuests FindOnAttacker( Component attacker )
	{
		if ( attacker is null || !attacker.IsValid() || !attacker.GameObject.IsValid() )
			return null;

		return attacker.Components.Get<PlayerQuests>( FindMode.EverythingInSelfAndAncestors );
	}

	static string ResolveKillKind( Component victim )
	{
		if ( victim is null || !victim.IsValid() || !victim.GameObject.IsValid() )
			return null;

		var animal = victim.Components.Get<AnimalBrain>( FindMode.EverythingInSelfAndAncestors );
		if ( animal is not null )
			return animal.Species.ToString().ToLowerInvariant();

		var vitals = victim as EntityVitals
		             ?? victim.Components.Get<EntityVitals>( FindMode.EverythingInSelfAndAncestors );
		return vitals?.EnemyType.ToString().ToLowerInvariant();
	}

	// ---- Biome entry ------------------------------------------------------------------------

	void TickBiomeEntry()
	{
		if ( _sinceBiomePoll < Math.Max( 0.25f, BiomePollSeconds ) )
			return;

		_sinceBiomePoll = 0f;

		if ( !_worldSearched )
		{
			_worldSearched = true;
			_world = Scene?.GetAllComponents<TerrainWorldManager>().FirstOrDefault();
		}

		if ( _world is null || !_world.IsValid() )
			return;

		var settings = _world.BuildGenerationSettings();
		if ( settings is null )
			return;

		var pos = GameObject.WorldPosition;
		var wx = TerrainWorldUnits.EngineToMeters( pos.x );
		var wy = TerrainWorldUnits.EngineToMeters( pos.y );

		var backend = TerrainPreviewBackendRegistry.Active;
		var sample = backend is not null
			? backend.Sample( settings, wx, wy )
			: TerrainPreviewPipeline.Sample( settings, wx, wy );

		if ( !sample.IsInsideWorld )
			return;

		var biome = TerrainShorelineDisplay.IsDisplayWaterColor( settings, wx, wy )
			? TerrainPreviewBiomeId.Water
			: TerrainPreviewBiomeResolver.ResolveLandOverlay( settings, sample, wx, wy ).BiomeId;

		if ( biome == _lastBiome )
			return;

		_lastBiome = biome;
		if ( biome == TerrainPreviewBiomeId.None )
			return;

		Deliver( QuestEventIds.BiomeEntered, biome.ToString(), 1 );
	}
}
