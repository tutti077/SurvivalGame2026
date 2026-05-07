using System;
using System.Threading.Tasks;
using Sandbox;

namespace Game;

/// <summary>
/// Add next to <see cref="PlayerController"/> on the player. When <see cref="Networking.IsActive"/>, health is host-authoritative.
/// Other clients see a world-space bar above this character; the owning client sees health + stamina in <see cref="PlayerVitalsHud"/> (bottom-left).
/// </summary>
public sealed partial class PlayerHealth : Component
{
	private static int _nextEnemyId = 1;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxHealthChanged ) )]
	public float MaxHealth { get; set; } = 100f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentHealthChanged ) )]
	public float CurrentHealth { get; set; } = 100f;

	/// <summary>World UI offset from this object (typically Z = up along body).</summary>
	[Property] public Vector3 WorldBarLocalOffset { get; set; } = new Vector3( 0f, 0f, 82f );

	/// <summary>3D scale for the overhead bar (<c>WorldPanel.RenderScale</c>).</summary>
	[Property] public float WorldPanelRenderScale { get; set; } = 1.35f;

	/// <summary>Force this health to render only world-space UI (never local screen HUD).</summary>
	[Property] public bool WorldBarOnly { get; set; }

	/// <summary>Enemy label prefix for generated IDs (e.g. Enemy_1, Enemy_2).</summary>
	[Property] public string EnemyNamePrefix { get; set; } = "Enemy";

	/// <summary>Stable identity key for HUD binding/debug. Players use username; enemies use prefix+counter.</summary>
	[Property] public string EntityId { get; private set; } = "";

	/// <summary>Non-player respawn behavior with a simple scale animation.</summary>
	[Property] public bool EnableNonPlayerRespawnAnimation { get; set; } = true;
	[Property] public float NonPlayerRespawnDelaySeconds { get; set; } = 2.0f;
	[Property] public float NonPlayerDeathAnimSeconds { get; set; } = 0.2f;
	[Property] public float NonPlayerRespawnAnimSeconds { get; set; } = 0.2f;

	/// <summary>Invoked locally when <see cref="CurrentHealth"/> or <see cref="MaxHealth"/> changes (including from sync).</summary>
	public event Action<float, float> OnHealthChanged;

	/// <summary>Invoked locally when health reaches zero from damage.</summary>
	public event Action OnDied;

	private GameObject _worldUi;
	private GameObject _screenUi;
	private bool _respawnRoutineRunning;

	/// <summary>0..1</summary>
	public float HealthFraction => MaxHealth > 0.001f ? Math.Clamp( CurrentHealth / MaxHealth, 0f, 1f ) : 0f;

	public bool IsAlive => CurrentHealth > 0.001f;

	private void OnCurrentHealthChanged( float oldValue, float newValue )
	{
		OnHealthChanged?.Invoke( CurrentHealth, MaxHealth );
		if ( newValue <= 0.001f && oldValue > 0.001f )
			OnDied?.Invoke();
	}

	private void OnMaxHealthChanged( float oldValue, float newValue )
	{
		OnHealthChanged?.Invoke( CurrentHealth, MaxHealth );
	}

	protected override void OnStart()
	{
		if ( IsHealthAuthority() )
		{
			MaxHealth = Math.Max( 1f, MaxHealth );
			CurrentHealth = CurrentHealth <= 0.001f
				? MaxHealth
				: Math.Clamp( CurrentHealth, 0f, MaxHealth );
		}

		EnsureEntityId();
		CreateHealthUi();
		OnDied += HandleNonPlayerDeath;
	}

	protected override void OnDestroy()
	{
		OnDied -= HandleNonPlayerDeath;
		_worldUi?.Destroy();
		_screenUi?.Destroy();
		_worldUi = null;
		_screenUi = null;
	}

	private static bool IsHealthAuthority()
	{
		if ( !Networking.IsActive )
			return true;

		return Networking.IsHost;
	}

	private bool IsLocalOwnerForUi()
	{
		var n = GameObject.Network;
		if ( n is null || !n.Active )
			return true;

		return n.IsOwner;
	}

	private void CreateHealthUi()
	{
		var hasPlayerController = FindPlayerControllerAncestor( GameObject ) is not null;
		var worldOnly = WorldBarOnly || !hasPlayerController;
		var local = IsLocalOwnerForUi();

		if ( worldOnly || !local )
		{
			_worldUi = new GameObject( true, "PlayerHealthWorldUi" );
			_worldUi.Parent = GameObject;
			_worldUi.LocalPosition = WorldBarLocalOffset;
			_worldUi.LocalRotation = Rotation.Identity;

			var wp = _worldUi.Components.Create<Sandbox.WorldPanel>();
			wp.RenderScale = WorldPanelRenderScale;
			wp.PanelSize = new Vector2( 280f, 44f );
			wp.LookAtCamera = true;

			var bar = _worldUi.Components.Create<PlayerHealthWorldBar>();
			bar.Health = this;
			return;
		}

		// Defer ScreenPanel/VitalsHud until after the first engine tick — mirrors the Alt+Enter "focus refresh" workaround
		// where immediate UI mount could leave mouse routed to overlay instead of PlayerController.
		_ = CreateLocalScreenUiHostDeferredAsync();
	}

	private void EnsureEntityId()
	{
		if ( !string.IsNullOrWhiteSpace( EntityId ) )
			return;

		var pc = FindPlayerControllerAncestor( GameObject );
		if ( pc is not null )
		{
			var name = pc.Network?.Owner?.DisplayName;
			EntityId = string.IsNullOrWhiteSpace( name ) ? pc.GameObject.Name : name;
			return;
		}

		EntityId = $"{EnemyNamePrefix}_{_nextEnemyId++}";
	}

	private bool IsNonPlayerHealth()
		=> FindPlayerControllerAncestor( GameObject ) is null;

	private void HandleNonPlayerDeath()
	{
		if ( _respawnRoutineRunning || !EnableNonPlayerRespawnAnimation || !IsNonPlayerHealth() )
			return;

		_ = RunNonPlayerRespawnRoutineAsync();
	}

	private async Task RunNonPlayerRespawnRoutineAsync()
	{
		_respawnRoutineRunning = true;
		var baseScale = GameObject.LocalScale;

		var deathDur = Math.Max( 0.01f, NonPlayerDeathAnimSeconds );
		for ( var t = 0f; t < deathDur && GameObject.IsValid(); t += Time.Delta )
		{
			var a = Math.Clamp( t / deathDur, 0f, 1f );
			GameObject.LocalScale = Vector3.Lerp( baseScale, baseScale * 0.15f, a );
			await GameTask.Yield();
		}

		if ( !GameObject.IsValid() )
			return;

		GameObject.LocalScale = baseScale * 0.15f;
		await GameTask.DelaySeconds( Math.Max( 0f, NonPlayerRespawnDelaySeconds ) );
		if ( !GameObject.IsValid() )
			return;

		ResetToFull();

		var respawnDur = Math.Max( 0.01f, NonPlayerRespawnAnimSeconds );
		for ( var t = 0f; t < respawnDur && GameObject.IsValid(); t += Time.Delta )
		{
			var a = Math.Clamp( t / respawnDur, 0f, 1f );
			GameObject.LocalScale = Vector3.Lerp( baseScale * 0.15f, baseScale, a );
			await GameTask.Yield();
		}

		if ( GameObject.IsValid() )
			GameObject.LocalScale = baseScale;

		_respawnRoutineRunning = false;
	}

	private static PlayerController FindPlayerControllerAncestor( GameObject start )
	{
		for ( var go = start; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null )
				return pc;
		}

		return null;
	}

	private async Task CreateLocalScreenUiHostDeferredAsync()
	{
		await GameTask.Yield();
		await GameTask.Yield();

		if ( !GameObject.IsValid() || !IsLocalOwnerForUi() )
			return;

		EnsureLocalScreenUiHost();
	}

	/// <summary>Idempotent: local owner screen-space host (one <see cref="Sandbox.ScreenPanel"/> + vitals). <see cref="PlayerInventory"/> attaches its HUD here so a second ScreenPanel cannot eat the whole viewport.</summary>
	internal GameObject EnsureLocalScreenUiHost()
	{
		if ( !IsLocalOwnerForUi() )
			return null;

		if ( _screenUi is not null && _screenUi.IsValid() )
			return _screenUi;

		_screenUi = new GameObject( true, "PlayerLocalScreenUi" );
		_screenUi.Parent = GameObject;

		_ = _screenUi.Components.Create<Sandbox.ScreenPanel>();

		var hud = _screenUi.Components.Create<PlayerVitalsHud>();
		hud.Health = this;
		hud.Stamina = FindPlayerStamina();

		return _screenUi;
	}

	private PlayerStamina FindPlayerStamina()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var s = go.Components.Get<PlayerStamina>();
			if ( s is not null )
				return s;
		}

		return GameObject.Components.Get<PlayerStamina>();
	}

	/// <summary>Remove health. Only the host applies when online. Returns true if the target is dead after the hit.</summary>
	public bool RemoveHealth( float amount )
	{
		if ( amount <= 0f || !IsHealthAuthority() )
			return !IsAlive;

		CurrentHealth = Math.Max( 0f, CurrentHealth - amount );
		return !IsAlive;
	}

	/// <summary>Add health without exceeding max.</summary>
	public void AddHealth( float amount )
	{
		if ( amount <= 0f || !IsHealthAuthority() )
			return;

		CurrentHealth = Math.Min( MaxHealth, CurrentHealth + amount );
	}

	/// <summary>Set current health (clamped 0..max).</summary>
	public void SetHealth( float value )
	{
		if ( !IsHealthAuthority() )
			return;

		CurrentHealth = Math.Clamp( value, 0f, MaxHealth );
	}

	/// <summary>Set max health. Optionally keeps the same health ratio.</summary>
	public void SetMaxHealth( float newMax, bool keepHealthRatio = false )
	{
		if ( !IsHealthAuthority() )
			return;

		newMax = Math.Max( 1f, newMax );
		var prevMax = MaxHealth;
		var prevCur = CurrentHealth;

		if ( keepHealthRatio && prevMax > 0.001f )
		{
			var ratio = prevCur / prevMax;
			MaxHealth = newMax;
			CurrentHealth = Math.Clamp( newMax * ratio, 0f, MaxHealth );
		}
		else
		{
			MaxHealth = newMax;
			CurrentHealth = Math.Clamp( prevCur, 0f, MaxHealth );
		}
	}

	/// <summary>Increase max health by delta. If <paramref name="increaseCurrentToMatch"/> is true, current goes up by the same amount (capped to new max).</summary>
	public void AddMaxHealth( float delta, bool increaseCurrentToMatch = true )
	{
		if ( delta == 0f || !IsHealthAuthority() )
			return;

		MaxHealth = Math.Max( 1f, MaxHealth + delta );
		if ( increaseCurrentToMatch )
			CurrentHealth = Math.Min( MaxHealth, CurrentHealth + Math.Max( 0f, delta ) );
		else
			CurrentHealth = Math.Clamp( CurrentHealth, 0f, MaxHealth );
	}

	/// <summary>Restore to full health.</summary>
	public void ResetToFull()
	{
		if ( !IsHealthAuthority() )
			return;

		CurrentHealth = MaxHealth;
	}
}
