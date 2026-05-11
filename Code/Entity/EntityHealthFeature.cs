using System;
using System.Threading.Tasks;
using Sandbox;

namespace Game;

[Title( "Entity Health Feature" )]
[Category( "Entity" )]
public class EntityHealthFeature : Component
{
	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxHealthChanged ) )]
	public float MaxHealth { get; set; } = 100f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentHealthChanged ) )]
	public float CurrentHealth { get; set; } = 100f;

	[Property] public float ArmorFlatReduction { get; set; } = 0f;
	[Property] public float ArmorPercentReduction { get; set; } = 0f;
	[Property] public bool UseEntityCoreDefaultMaxHealth { get; set; } = true;

	[Property] public Vector3 WorldBarLocalOffset { get; set; } = new Vector3( 0f, 0f, 0f );
	[Property] public float WorldPanelRenderScale { get; set; } = 1.35f;
	[Property] public bool WorldBarFaceCameraYawOnly { get; set; } = true;
	[Property] public bool WorldBarOnly { get; set; }

	[Property] public bool EnableRespawnAnimation { get; set; } = true;
	[Property] public float RespawnDelaySeconds { get; set; } = 2.0f;
	[Property] public float DeathAnimSeconds { get; set; } = 0.2f;
	[Property] public float RespawnAnimSeconds { get; set; } = 0.2f;

	public event Action<float, float> OnHealthChanged;
	public event Action OnDied;

	private GameObject _worldUi;
	private GameObject _screenUi;
	private bool _respawnRoutineRunning;
	private bool _hasBeenDamaged;

	public float HealthFraction => MaxHealth > 0.001f ? Math.Clamp( CurrentHealth / MaxHealth, 0f, 1f ) : 0f;
	public bool IsAlive => CurrentHealth > 0.001f;

	protected override void OnStart()
	{
		var core = EntityCore.EnsureOn( GameObject, fallbackKind: EntityKind.Enemy );
		if ( core is not null && core.Kind != EntityKind.Player )
			WorldBarOnly = true;

		if ( IsAuthority() )
		{
			if ( UseEntityCoreDefaultMaxHealth && core is not null )
			{
				var coreMax = core.GetConfiguredBaseMaxHealth();
				var atDefault = Math.Abs( MaxHealth - 100f ) < 0.01f && Math.Abs( CurrentHealth - 100f ) < 0.01f;
				if ( atDefault )
				{
					MaxHealth = coreMax;
					CurrentHealth = coreMax;
				}
			}

			MaxHealth = Math.Max( 1f, MaxHealth );
			CurrentHealth = CurrentHealth <= 0.001f ? MaxHealth : Math.Clamp( CurrentHealth, 0f, MaxHealth );
		}

		EnsureWorldBar();
		UpdateWorldBarVisibility();
		CreateHealthUi();
		OnDied += HandleDeath;
	}

	protected override void OnDestroy()
	{
		OnDied -= HandleDeath;
		_worldUi?.Destroy();
		_screenUi?.Destroy();
		_worldUi = null;
		_screenUi = null;
	}

	private static bool IsAuthority()
	{
		if ( !Networking.IsActive )
			return true;
		return Networking.IsHost;
	}

	private void OnCurrentHealthChanged( float oldValue, float newValue )
	{
		OnHealthChanged?.Invoke( CurrentHealth, MaxHealth );
		if ( newValue < oldValue )
			EnsureWorldBar();
		UpdateWorldBarVisibility();
		if ( newValue <= 0.001f && oldValue > 0.001f )
			OnDied?.Invoke();
	}

	private void OnMaxHealthChanged( float oldValue, float newValue )
	{
		OnHealthChanged?.Invoke( CurrentHealth, MaxHealth );
		UpdateWorldBarVisibility();
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
		var core = EntityCore.FindOnHierarchy( GameObject );
		var isPlayer = core?.Kind == EntityKind.Player;
		var local = IsLocalOwnerForUi();

		EnsureWorldBar();

		if ( !WorldBarOnly && isPlayer && local )
			_ = CreateLocalScreenUiHostDeferredAsync();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( !WorldBarFaceCameraYawOnly || _worldUi is null || !_worldUi.IsValid() )
			return;

		var cam = Scene.Camera;
		if ( cam is null )
			return;

		var toCamera = cam.WorldPosition - _worldUi.WorldPosition;
		toCamera = toCamera.WithZ( 0f );
		if ( toCamera.IsNearlyZero( 0.0001f ) )
			return;

		_worldUi.WorldRotation = Rotation.LookAt( toCamera.Normal, Vector3.Up );
	}

	public void EnsureWorldBar()
	{
		var core = EntityCore.EnsureOn( GameObject, EntityKind.Enemy );
		var anchor = core?.EnsureOverheadAnchor() ?? GameObject;
		GameObject chosen = null;
		foreach ( var child in anchor.Children )
		{
			if ( child is null || !child.IsValid() )
				continue;
			if ( child.Name != "EntityHealthWorldUi" )
				continue;

			if ( chosen is null )
				chosen = child;
			else
				child.Destroy();
		}

		_worldUi = chosen ?? new GameObject( true, "EntityHealthWorldUi" );
		_worldUi.Parent = anchor;
		_worldUi.Name = "EntityHealthWorldUi";
		_worldUi.LocalPosition = WorldBarLocalOffset;
		_worldUi.LocalRotation = Rotation.Identity;
		_worldUi.WorldScale = Vector3.One;

		if ( WorldBarFaceCameraYawOnly )
		{
			var cam = Scene.Camera;
			if ( cam is not null )
			{
				var toCamera = cam.WorldPosition - _worldUi.WorldPosition;
				toCamera = toCamera.WithZ( 0f );
				if ( !toCamera.IsNearlyZero( 0.0001f ) )
					_worldUi.WorldRotation = Rotation.LookAt( toCamera.Normal, Vector3.Up );
			}
		}

		var wp = _worldUi.Components.Get<WorldPanel>() ?? _worldUi.Components.Create<WorldPanel>();
		wp.RenderScale = Math.Max( 2.0f, WorldPanelRenderScale );
		wp.PanelSize = new Vector2( 800f, 34f );
		wp.LookAtCamera = !WorldBarFaceCameraYawOnly;

		var bar = _worldUi.Components.Get<SimpleEnemyHealthWorldBar>() ?? _worldUi.Components.Create<SimpleEnemyHealthWorldBar>();
		bar.Enabled = true;
		bar.Health = this;
		DedupeBarComponents( _worldUi, wp, bar );
	}

	public void ShowWorldBar()
	{
		// Single API for other systems (e.g. melee) to request world-bar visibility.
		EnsureWorldBar();
		if ( _worldUi is not null && _worldUi.IsValid() )
			_worldUi.Enabled = true;
	}

	private void UpdateWorldBarVisibility()
	{
		if ( _worldUi is null || !_worldUi.IsValid() )
			return;

		// Show after first real damage, keep while damaged, hide when fully healed or dead.
		var shouldShow = _hasBeenDamaged && CurrentHealth > 0.001f && CurrentHealth < MaxHealth - 0.001f;
		_worldUi.Enabled = shouldShow;
	}

	private async Task CreateLocalScreenUiHostDeferredAsync()
	{
		await GameTask.Yield();
		await GameTask.Yield();
		if ( !GameObject.IsValid() || !IsLocalOwnerForUi() )
			return;
		EnsureLocalScreenUiHost();
	}

	internal GameObject EnsureLocalScreenUiHost()
	{
		if ( !IsLocalOwnerForUi() )
			return null;
		if ( _screenUi is not null && _screenUi.IsValid() )
			return _screenUi;

		_screenUi = new GameObject( true, "EntityLocalScreenUi" );
		_screenUi.Parent = GameObject;
		_ = _screenUi.Components.Create<ScreenPanel>();

		var hud = _screenUi.Components.Create<PlayerVitalsHud>();
		hud.Health = this;
		hud.Stamina = EntityStaminaFeature.FindForEntityRoot( GameObject );
		hud.Air = EntityAirFeature.FindForEntityRoot( GameObject );
		return _screenUi;
	}

	public float ComputeFinalDamage( float baseDamage )
	{
		var v = Math.Max( 0f, baseDamage - Math.Max( 0f, ArmorFlatReduction ) );
		var pct = Math.Clamp( ArmorPercentReduction, 0f, 0.95f );
		return v * (1f - pct);
	}

	public bool RemoveHealth( float amount )
	{
		if ( amount <= 0f || !IsAuthority() )
			return !IsAlive;

		var prev = CurrentHealth;
		CurrentHealth = Math.Max( 0f, CurrentHealth - ComputeFinalDamage( amount ) );
		if ( CurrentHealth < prev - 0.001f )
			_hasBeenDamaged = true;
		UpdateWorldBarVisibility();
		return !IsAlive;
	}

	public void AddHealth( float amount )
	{
		if ( amount <= 0f || !IsAuthority() )
			return;
		CurrentHealth = Math.Min( MaxHealth, CurrentHealth + amount );
		if ( CurrentHealth >= MaxHealth - 0.001f )
			_hasBeenDamaged = false;
		UpdateWorldBarVisibility();
	}

	public void SetHealth( float value )
	{
		if ( !IsAuthority() )
			return;
		CurrentHealth = Math.Clamp( value, 0f, MaxHealth );
		if ( CurrentHealth >= MaxHealth - 0.001f )
			_hasBeenDamaged = false;
		else if ( CurrentHealth < MaxHealth - 0.001f )
			_hasBeenDamaged = true;
		UpdateWorldBarVisibility();
	}

	public void SetMaxHealth( float newMax, bool keepHealthRatio = false )
	{
		if ( !IsAuthority() )
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

	public void ResetToFull()
	{
		if ( !IsAuthority() )
			return;
		CurrentHealth = MaxHealth;
	}

	private void HandleDeath()
	{
		var core = EntityCore.FindOnHierarchy( GameObject );
		if ( _respawnRoutineRunning || !EnableRespawnAnimation || core?.Kind == EntityKind.Player )
			return;
		_ = RunRespawnRoutineAsync();
	}

	private async Task RunRespawnRoutineAsync()
	{
		_respawnRoutineRunning = true;
		var baseScale = GameObject.LocalScale;

		var deathDur = Math.Max( 0.01f, DeathAnimSeconds );
		for ( var t = 0f; t < deathDur && GameObject.IsValid(); t += Time.Delta )
		{
			var a = Math.Clamp( t / deathDur, 0f, 1f );
			GameObject.LocalScale = Vector3.Lerp( baseScale, baseScale * 0.15f, a );
			await GameTask.Yield();
		}

		if ( !GameObject.IsValid() )
			return;
		GameObject.LocalScale = baseScale * 0.15f;
		await GameTask.DelaySeconds( Math.Max( 0f, RespawnDelaySeconds ) );
		if ( !GameObject.IsValid() )
			return;

		ResetToFull();
		var respawnDur = Math.Max( 0.01f, RespawnAnimSeconds );
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

	private static void DedupeBarComponents( GameObject host, WorldPanel keepWp, SimpleEnemyHealthWorldBar keepBar )
	{
		if ( host is null || !host.IsValid() )
			return;

		foreach ( var wp in host.Components.GetAll<WorldPanel>() )
		{
			if ( wp is null || !wp.IsValid() || ReferenceEquals( wp, keepWp ) )
				continue;
			wp.Destroy();
		}

		foreach ( var bar in host.Components.GetAll<SimpleEnemyHealthWorldBar>() )
		{
			if ( bar is null || !bar.IsValid() || ReferenceEquals( bar, keepBar ) )
				continue;
			bar.Destroy();
		}

		foreach ( var verbose in host.Components.GetAll<PlayerHealthWorldBar>() )
		{
			if ( verbose is null || !verbose.IsValid() )
				continue;
			verbose.Enabled = false;
		}
	}

	private static System.Collections.Generic.IEnumerable<GameObject> EnumerateSelfAndDescendants( GameObject root )
	{
		if ( root is null || !root.IsValid() )
			yield break;

		yield return root;
		foreach ( var child in root.Children )
		{
			foreach ( var c in EnumerateSelfAndDescendants( child ) )
				yield return c;
		}
	}
}
