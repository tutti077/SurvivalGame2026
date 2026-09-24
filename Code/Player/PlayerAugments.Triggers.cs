using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Activation side of augments: the six key binds (1–6), the F radial wheel, per-augment
/// cooldown / toggle / battery state, and the host effects (Sonic Burst pin, War Cry provoke).
/// <para>
/// Owner drives input and previews cooldowns; effects that touch other pawns go through
/// <see cref="RpcHostActivate"/> where the host re-checks the socket and its own cooldown once.
/// Passives with a once-per-window effect (Death Grip, Armor Plating) use
/// <see cref="HostTryConsumePassiveCooldown"/> on the host.
/// </para>
/// </summary>
public sealed partial class PlayerAugments
{
	public const int BindCount = 6;
	/// <summary>Number row 1–6 — the hotbar no longer listens to these.</summary>
	static readonly string[] BindActions = { "Slot1", "Slot2", "Slot3", "Slot4", "Slot5", "Slot6" };

	public const string WheelAction = "Shove";
	const float WheelDeadZonePixels = 36f;

	/// <summary>Owner: an activatable augment fired or switched on (HUD / feedback).</summary>
	public event Action<AugmentDefinition> AugmentActivated;
	public event Action BindsChanged;

	readonly string[] _binds = new string[BindCount];
	readonly Dictionary<string, TriggerState> _triggerStates = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<AugmentAbility, double> _passiveCooldownUntil = new();
	readonly Dictionary<string, double> _hostCooldownUntil = new( StringComparer.OrdinalIgnoreCase );
	readonly List<(EntityLocomotion Entity, double Until)> _hostPins = new();
	readonly List<AugmentDefinition> _wheelEntries = new();

	int _bindsValidatedVersion = -1;
	int _wheelBuiltVersion = -1;
	double _lastTriggerTickAt;

	PlayerController _wheelController;
	bool _wheelSavedLook;
	Vector2 _wheelVector;

	sealed class TriggerState
	{
		public double CooldownUntil;
		public bool On;
		public float Battery;
	}

	// ── Read ────────────────────────────────────────────────────────────────────────────────

	public string GetBind( int index ) =>
		index < 0 || index >= BindCount ? string.Empty : _binds[index] ?? string.Empty;

	/// <summary>Live ability check: passives while installed + paid, toggles only while switched on, one-shots never.</summary>
	public bool IsAbilityOn( AugmentAbility ability )
	{
		if ( !TryGetActiveDefinition( ability, out var def ) )
			return false;

		if ( def.ResolvedActivation == AugmentActivation.Passive )
			return true;

		if ( def.ResolvedMode != AugmentMode.Toggle )
			return false;

		return _triggerStates.TryGetValue( def.Id, out var state ) && state.On;
	}

	/// <summary>Cooldown left, toggle state and battery fill for the HUD. False when the augment has no runtime state yet.</summary>
	public bool TryGetTriggerState( string augmentId, out float cooldownRemaining, out float cooldownTotal, out bool on, out float battery01 )
	{
		cooldownRemaining = 0f;
		cooldownTotal = 0f;
		on = false;
		battery01 = 1f;
		if ( string.IsNullOrWhiteSpace( augmentId ) || !AugmentCatalog.TryGet( augmentId, out var def ) )
			return false;

		cooldownTotal = def.ResolvedCooldownSeconds;
		if ( !_triggerStates.TryGetValue( def.Id, out var state ) )
			return true;

		cooldownRemaining = (float)Math.Max( 0.0, state.CooldownUntil - Time.NowDouble );
		on = state.On;
		battery01 = def.HasBattery ? Math.Clamp( state.Battery / def.BatterySeconds, 0f, 1f ) : 1f;
		return true;
	}

	public bool HasWheelAugments => WheelEntries.Count > 0;

	/// <summary>Installed + paid wheel augments, in socket order.</summary>
	public IReadOnlyList<AugmentDefinition> WheelEntries
	{
		get
		{
			if ( _wheelBuiltVersion != ContentsVersion )
			{
				_wheelBuiltVersion = ContentsVersion;
				_wheelEntries.Clear();
				for ( var i = 0; i < AugmentSlots.Count; i++ )
				{
					if ( !IsSlotActive( (AugmentSlot)i ) )
						continue;

					if ( AugmentCatalog.TryGet( _installed[i].ResourceId, out var def )
					     && def.ResolvedActivation == AugmentActivation.Wheel )
						_wheelEntries.Add( def );
				}
			}

			return _wheelEntries;
		}
	}

	public bool IsWheelOpen { get; private set; }
	public int WheelSelectedIndex { get; private set; } = -1;
	/// <summary>Accumulated mouse travel since the wheel opened (HUD pointer dot).</summary>
	public Vector2 WheelVector => _wheelVector;

	// ── Binds (owner-local) ─────────────────────────────────────────────────────────────────

	/// <summary>Assign a trigger augment to key <paramref name="index"/>+1. The augment must be installed and paid for.</summary>
	public bool OwnerTrySetBind( int index, string augmentId )
	{
		if ( !IsLocalManagingClient() || index < 0 || index >= BindCount )
			return false;

		if ( !AugmentCatalog.TryGet( augmentId, out var def ) || def.ResolvedActivation != AugmentActivation.Trigger )
			return false;

		if ( !IsInstalledActive( def.Id ) )
			return false;

		// One key per augment — moving it clears its old slot.
		for ( var i = 0; i < BindCount; i++ )
		{
			if ( i != index && ResourceCatalog.ResourceIdsMatch( _binds[i], def.Id ) )
				_binds[i] = string.Empty;
		}

		_binds[index] = def.Id;
		BindsChanged?.Invoke();
		return true;
	}

	public bool OwnerClearBind( int index )
	{
		if ( !IsLocalManagingClient() || index < 0 || index >= BindCount || string.IsNullOrWhiteSpace( _binds[index] ) )
			return false;

		_binds[index] = string.Empty;
		BindsChanged?.Invoke();
		return true;
	}

	/// <summary>Binds only point at augments that are still installed + paid; re-checked when the sockets change, not per frame.</summary>
	void ValidateBindsIfChanged()
	{
		if ( _bindsValidatedVersion == ContentsVersion )
			return;

		_bindsValidatedVersion = ContentsVersion;
		var changed = false;
		for ( var i = 0; i < BindCount; i++ )
		{
			if ( string.IsNullOrWhiteSpace( _binds[i] ) || IsInstalledActive( _binds[i] ) )
				continue;

			_binds[i] = string.Empty;
			changed = true;
		}

		// A toggle that lost its socket switches off with it.
		foreach ( var (id, state) in _triggerStates )
		{
			if ( state.On && !IsInstalledActive( id ) )
			{
				state.On = false;
				changed = true;
			}
		}

		if ( changed )
			BindsChanged?.Invoke();
	}

	// ── Tick ────────────────────────────────────────────────────────────────────────────────

	protected override void OnUpdate()
	{
		base.OnUpdate();

		if ( HasHostAuthority )
			TickHostPins();

		if ( !IsLocalManagingClient() )
			return;

		PushHackFlags();

		var now = Time.NowDouble;
		var dt = _lastTriggerTickAt > 0 ? (float)Math.Clamp( now - _lastTriggerTickAt, 0.0, 0.25 ) : 0f;
		_lastTriggerTickAt = now;

		ValidateBindsIfChanged();
		TickBatteries( dt );

		var menuOpen = Components.Get<PlayerGameMenuController>() is { IsMenuOpen: true };
		TickWheel( menuOpen );
		if ( !menuOpen && !IsWheelOpen )
			TickBindKeys();
	}

	void TickBindKeys()
	{
		for ( var i = 0; i < BindCount; i++ )
		{
			if ( string.IsNullOrWhiteSpace( _binds[i] ) || !Input.Pressed( BindActions[i] ) )
				continue;

			OwnerTryActivate( _binds[i] );
		}
	}

	void TickBatteries( float dt )
	{
		if ( dt <= 0f )
			return;

		var now = Time.NowDouble;
		foreach ( var (id, state) in _triggerStates )
		{
			if ( !AugmentCatalog.TryGet( id, out var def ) || !def.HasBattery )
				continue;

			if ( state.On )
			{
				state.Battery -= dt;
				if ( state.Battery > 0f )
					continue;

				state.Battery = 0f;
				state.On = false;
				state.CooldownUntil = now + def.ResolvedCooldownSeconds;
				continue;
			}

			if ( state.Battery < def.BatterySeconds )
				state.Battery = Math.Min( def.BatterySeconds, state.Battery + dt * def.BatterySeconds / def.ResolvedBatteryRechargeSeconds );
		}
	}

	// ── Wheel (hold F, release to pick; plain tap = shove) ──────────────────────────────────

	void TickWheel( bool menuOpen )
	{
		if ( !IsWheelOpen )
		{
			if ( menuOpen || !HasWheelAugments || !Input.Pressed( WheelAction ) )
				return;

			OpenWheel();
			return;
		}

		_wheelVector += ReadWheelDelta();
		WheelSelectedIndex = ResolveWheelIndex( _wheelVector, WheelEntries.Count );

		if ( menuOpen )
		{
			CloseWheel( pick: false );
			return;
		}

		if ( Input.Released( WheelAction ) )
			CloseWheel( pick: true );
	}

	void OpenWheel()
	{
		IsWheelOpen = true;
		_wheelVector = Vector2.Zero;
		WheelSelectedIndex = -1;

		_wheelController = Components.Get<PlayerController>();
		if ( _wheelController is not null && _wheelController.IsValid() )
		{
			_wheelSavedLook = _wheelController.UseLookControls;
			_wheelController.UseLookControls = false;
		}
	}

	void CloseWheel( bool pick )
	{
		var picked = pick ? WheelSelectedIndex : -1;
		IsWheelOpen = false;
		WheelSelectedIndex = -1;

		if ( _wheelController is not null && _wheelController.IsValid() )
			_wheelController.UseLookControls = _wheelSavedLook;
		_wheelController = null;

		if ( !pick )
			return;

		var entries = WheelEntries;
		if ( picked >= 0 && picked < entries.Count )
		{
			OwnerTryActivate( entries[picked].Id );
			return;
		}

		// Tap with no pick: F still shoves.
		Components.Get<PlayerCombat>()?.OwnerTryShove();
	}

	static Vector2 ReadWheelDelta()
	{
		var delta = Input.MouseDelta;
		if ( delta.LengthSquared > 1e-6f )
			return delta;

		return Mouse.Delta;
	}

	/// <summary>Segment 0 sits at the top, then clockwise. Inside the dead zone nothing is selected.</summary>
	static int ResolveWheelIndex( Vector2 v, int count )
	{
		if ( count <= 0 || v.Length < WheelDeadZonePixels )
			return -1;

		var angle = MathF.Atan2( v.x, -v.y );
		if ( angle < 0f )
			angle += MathF.PI * 2f;

		var step = MathF.PI * 2f / count;
		return (int)MathF.Round( angle / step ) % count;
	}

	// ── Activation ──────────────────────────────────────────────────────────────────────────

	/// <summary>Owner: fire / toggle an installed trigger or wheel augment. False when locked, cooling down, empty, or not built yet.</summary>
	public bool OwnerTryActivate( string augmentId )
	{
		if ( !IsLocalManagingClient() || !AugmentCatalog.TryGet( augmentId, out var def ) )
			return false;

		if ( !def.IsActivatable || !def.Implemented || !IsInstalledActive( def.Id ) )
			return false;

		var now = Time.NowDouble;
		var state = GetOrCreateState( def );

		if ( def.ResolvedMode == AugmentMode.Toggle )
		{
			if ( state.On )
			{
				state.On = false;
				state.CooldownUntil = now + def.ResolvedCooldownSeconds;
				return true;
			}

			if ( now < state.CooldownUntil || ( def.HasBattery && state.Battery <= 0.05f ) )
				return false;

			state.On = true;
			AugmentActivated?.Invoke( def );
			return true;
		}

		if ( now < state.CooldownUntil )
			return false;

		// Owner-side effects only count when they actually happened (no cooldown for a no-op).
		if ( !ApplyOwnerEffect( def ) )
			return false;

		state.CooldownUntil = now + def.ResolvedCooldownSeconds;
		AugmentActivated?.Invoke( def );

		if ( NeedsHostEffect( def.ResolvedAbility ) )
		{
			if ( HasHostAuthority )
				HostApplyEffect( def );
			else
				RpcHostActivate( def.Id );
		}

		return true;
	}

	TriggerState GetOrCreateState( AugmentDefinition def )
	{
		if ( _triggerStates.TryGetValue( def.Id, out var state ) )
			return state;

		state = new TriggerState { Battery = def.BatterySeconds };
		_triggerStates[def.Id] = state;
		return state;
	}

	static bool NeedsHostEffect( AugmentAbility ability ) =>
		ability is AugmentAbility.SonicBurst or AugmentAbility.WarCry;

	/// <summary>Owner-local one-shot effects. Host-side effects return true here and run on the host.</summary>
	bool ApplyOwnerEffect( AugmentDefinition def )
	{
		var movement = Components.Get<PlayerMovement>();
		switch ( def.ResolvedAbility )
		{
			case AugmentAbility.SpringLegs:
				return movement is not null && movement.TryAugmentSpringJump( def.EffectScale );
			case AugmentAbility.RecoverySlide:
				return movement is not null && movement.TryAugmentRecoverySlide( def );
			default:
				return true;
		}
	}

	// ── Host effects + passive cooldowns ────────────────────────────────────────────────────

	[Rpc.Host]
	void RpcHostActivate( string augmentId )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerOwner() )
			return;

		if ( AugmentCatalog.TryGet( augmentId, out var def ) )
			HostApplyEffect( def );
	}

	void HostApplyEffect( AugmentDefinition def )
	{
		if ( !HasHostAuthority || def is null || !IsInstalledActive( def.Id ) )
			return;

		// The host keeps its own clock — a modified client cannot spam past the cooldown.
		var now = Time.NowDouble;
		if ( _hostCooldownUntil.TryGetValue( def.Id, out var until ) && now < until )
			return;

		_hostCooldownUntil[def.Id] = now + def.ResolvedCooldownSeconds;

		var radius = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, def.EffectRadiusMeters ) );
		var origin = GameObject.WorldPosition;

		switch ( def.ResolvedAbility )
		{
			case AugmentAbility.SonicBurst:
				foreach ( var locomotion in Scene.GetAllComponents<EntityLocomotion>() )
				{
					if ( locomotion is null || !locomotion.IsValid() )
						continue;

					if ( Vector3.DistanceBetween( origin, locomotion.GameObject.WorldPosition ) > radius )
						continue;

					locomotion.HostSetTrapped( true, locomotion.GameObject.WorldPosition );
					_hostPins.Add( (locomotion, now + Math.Max( 0.5f, def.EffectSeconds )) );
				}
				break;

			case AugmentAbility.WarCry:
				foreach ( var brain in Scene.GetAllComponents<EntityBrain>() )
				{
					if ( brain is null || !brain.IsValid() )
						continue;

					if ( Vector3.DistanceBetween( origin, brain.GameObject.WorldPosition ) > radius )
						continue;

					brain.HostProvoke( GameObject );
				}
				break;
		}
	}

	/// <summary>Host: release Sonic Burst pins when their time is up.</summary>
	void TickHostPins()
	{
		if ( _hostPins.Count == 0 )
			return;

		var now = Time.NowDouble;
		for ( var i = _hostPins.Count - 1; i >= 0; i-- )
		{
			var (entity, until) = _hostPins[i];
			if ( now < until )
				continue;

			if ( entity is not null && entity.IsValid() )
				entity.HostSetTrapped( false );

			_hostPins.RemoveAt( i );
		}
	}

	/// <summary>
	/// Host: a passive with a once-per-window effect (Death Grip, Armor Plating). True = the effect
	/// applies now and its cooldown starts; false = not installed or still cooling down.
	/// </summary>
	public bool HostTryConsumePassiveCooldown( AugmentAbility ability )
	{
		if ( !HasHostAuthority || !IsAbilityOn( ability ) || !TryGetActiveDefinition( ability, out var def ) )
			return false;

		var now = Time.NowDouble;
		if ( _passiveCooldownUntil.TryGetValue( ability, out var until ) && now < until )
			return false;

		_passiveCooldownUntil[ability] = now + def.ResolvedCooldownSeconds;
		return true;
	}

	/// <summary>Host: Armor Plating roll — EffectChance to ignore the hit, then its cooldown.</summary>
	public bool HostTryArmorPlatingBlock()
	{
		if ( !HasHostAuthority || !IsAbilityOn( AugmentAbility.ArmorPlating )
		     || !TryGetActiveDefinition( AugmentAbility.ArmorPlating, out var def ) )
			return false;

		var now = Time.NowDouble;
		if ( _passiveCooldownUntil.TryGetValue( AugmentAbility.ArmorPlating, out var until ) && now < until )
			return false;

		if ( Random.Shared.NextSingle() >= Math.Clamp( def.EffectChance, 0f, 1f ) )
			return false;

		_passiveCooldownUntil[AugmentAbility.ArmorPlating] = now + def.ResolvedCooldownSeconds;
		return true;
	}
}
