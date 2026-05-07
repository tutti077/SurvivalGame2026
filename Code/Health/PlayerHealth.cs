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
	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxHealthChanged ) )]
	public float MaxHealth { get; set; } = 100f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentHealthChanged ) )]
	public float CurrentHealth { get; set; } = 100f;

	/// <summary>World UI offset from this object (typically Z = up along body).</summary>
	[Property] public Vector3 WorldBarLocalOffset { get; set; } = new Vector3( 0f, 0f, 82f );

	/// <summary>3D scale for the overhead bar (<c>WorldPanel.RenderScale</c>).</summary>
	[Property] public float WorldPanelRenderScale { get; set; } = 1.35f;

	/// <summary>Invoked locally when <see cref="CurrentHealth"/> or <see cref="MaxHealth"/> changes (including from sync).</summary>
	public event Action<float, float> OnHealthChanged;

	/// <summary>Invoked locally when health reaches zero from damage.</summary>
	public event Action OnDied;

	private GameObject _worldUi;
	private GameObject _screenUi;

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

		CreateHealthUi();
	}

	protected override void OnDestroy()
	{
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
		var local = IsLocalOwnerForUi();

		if ( !local )
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
