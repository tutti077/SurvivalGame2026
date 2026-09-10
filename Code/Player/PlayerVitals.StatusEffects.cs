#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox;

namespace Survival;

/// <summary>One active buff / debuff as every machine sees it (parsed from the synced mirror).</summary>
public readonly record struct StatusEffectView( string Id, double ExpiresAt );

/// <summary>
/// Status buffs / debuffs: a timed stack of <see cref="StatusEffectData"/> entries whose modifiers
/// change what the pawn has — pool caps, food duration, HP / stamina per second. Host-owned; the
/// owner mirrors the stack through one synced string so the HUD can draw icons and timers.
/// Sources: comfort (<c>rested</c>, see the comfort partial), future poison / cold / wet, and the
/// <c>status</c> console hack for testing.
/// </summary>
public sealed partial class PlayerVitals
{
	/// <summary>Per-second HP / stamina modifiers are batched until this much has accrued (fewer authority deltas).</summary>
	const float StatusRegenBatchThreshold = 0.25f;

	[Property, Group( "Status Effects" ), Title( "Log status effects" )]
	public bool LogStatusEffects { get; set; }

	/// <summary>Host → everyone: "id:expiresAt|id:expiresAt". Rewritten only when an effect is added, removed or re-timed.</summary>
	[Sync] public string ActiveStatusEffectsSync { get; private set; } = string.Empty;

	/// <summary>Raised on every machine when the active set or any expiry changes.</summary>
	public event Action StatusEffectsChanged;

	readonly List<StatusEffectView> _statusEffects = new();
	string _statusEffectsParsedFrom = string.Empty;
	float _statusHealthCarry;
	float _statusStaminaCarry;

	/// <summary>Active effects (parsed mirror — valid on host, owner and proxies).</summary>
	public IReadOnlyList<StatusEffectView> ActiveStatusEffects
	{
		get
		{
			RefreshStatusEffectsFromSync();
			return _statusEffects;
		}
	}

	public bool HasStatusEffect( string id )
	{
		RefreshStatusEffectsFromSync();
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( string.Equals( _statusEffects[i].Id, id, StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	/// <summary>Seconds left on an effect, or -1 when it is not active. Rested holds at its comfort value (see comfort partial).</summary>
	public float GetStatusEffectRemainingSeconds( string id )
	{
		RefreshStatusEffectsFromSync();
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( !string.Equals( _statusEffects[i].Id, id, StringComparison.OrdinalIgnoreCase ) )
				continue;

			var remaining = (float)Math.Max( 0.0, _statusEffects[i].ExpiresAt - Time.NowDouble );
			if ( string.Equals( id, StatusEffectCatalog.RestedId, StringComparison.OrdinalIgnoreCase ) )
				remaining = Math.Max( remaining, ComfortHeldRestedSeconds() );

			return remaining;
		}

		return -1f;
	}

	/// <summary>Product of every active effect's max-health percent (1 = unchanged).</summary>
	public float StatusMaxHealthMultiplier => StatusMultiplier( e => e.MaxHealthPercent );

	/// <summary>Product of every active effect's max-stamina percent (1 = unchanged).</summary>
	public float StatusMaxStaminaMultiplier => StatusMultiplier( e => e.MaxStaminaPercent );

	/// <summary>Product of every active effect's food-duration percent (1 = unchanged).</summary>
	public float StatusFoodDurationMultiplier => StatusMultiplier( e => e.FoodDurationPercent );

	float StatusMultiplier( Func<StatusEffectData, float> percent )
	{
		RefreshStatusEffectsFromSync();
		var multiplier = 1f;
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( StatusEffectCatalog.TryGet( _statusEffects[i].Id, out var effect ) )
				multiplier *= Math.Max( 0f, 1f + percent( effect ) / 100f );
		}

		return multiplier;
	}

	/// <summary>Host / offline: apply an effect for <paramref name="durationSeconds"/>; an existing entry keeps the later expiry.</summary>
	public bool HostApplyStatusEffect( string id, float durationSeconds )
	{
		if ( !IsHostOrOffline || durationSeconds <= 0f )
			return false;

		return HostSetStatusEffectExpiry( id, Time.NowDouble + durationSeconds, extendOnly: true );
	}

	/// <summary>
	/// Host / offline: set an effect's absolute expiry. With <paramref name="extendOnly"/> an earlier
	/// expiry than the current one is ignored (the comfort timer banks its best value this way).
	/// </summary>
	public bool HostSetStatusEffectExpiry( string id, double expiresAt, bool extendOnly )
	{
		if ( !IsHostOrOffline || !StatusEffectCatalog.TryGet( id, out var effect ) )
			return false;

		RefreshStatusEffectsFromSync();
		var index = IndexOfStatusEffect( effect.Id );
		if ( index >= 0 )
		{
			var current = _statusEffects[index].ExpiresAt;
			if ( extendOnly && expiresAt <= current )
				return false;

			_statusEffects[index] = new StatusEffectView( effect.Id, expiresAt );
		}
		else
		{
			_statusEffects.Add( new StatusEffectView( effect.Id, expiresAt ) );
			if ( LogStatusEffects )
				Log.Info( $"{VitalsLogPrefix()} {GameObject.Name}: +{effect.Id} ({expiresAt - Time.NowDouble:0}s)" );
		}

		HostPublishStatusEffects();
		return true;
	}

	/// <summary>Host / offline: drop one effect now.</summary>
	public bool HostRemoveStatusEffect( string id )
	{
		if ( !IsHostOrOffline )
			return false;

		RefreshStatusEffectsFromSync();
		var index = IndexOfStatusEffect( id );
		if ( index < 0 )
			return false;

		if ( LogStatusEffects )
			Log.Info( $"{VitalsLogPrefix()} {GameObject.Name}: -{_statusEffects[index].Id}" );

		_statusEffects.RemoveAt( index );
		HostPublishStatusEffects();
		return true;
	}

	/// <summary>Host / offline: drop every effect (death).</summary>
	public void HostClearStatusEffects()
	{
		if ( !IsHostOrOffline )
			return;

		RefreshStatusEffectsFromSync();
		if ( _statusEffects.Count == 0 )
			return;

		_statusEffects.Clear();
		HostPublishStatusEffects();
	}

	int IndexOfStatusEffect( string id )
	{
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( string.Equals( _statusEffects[i].Id, id, StringComparison.OrdinalIgnoreCase ) )
				return i;
		}

		return -1;
	}

	/// <summary>Host: serialise the list into the synced mirror, then react to the new set locally.</summary>
	void HostPublishStatusEffects()
	{
		var sb = new StringBuilder();
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( '|' );
			sb.Append( _statusEffects[i].Id ).Append( ':' )
				.Append( _statusEffects[i].ExpiresAt.ToString( "R", CultureInfo.InvariantCulture ) );
		}

		var text = sb.ToString();
		ActiveStatusEffectsSync = text;
		_statusEffectsParsedFrom = text;
		OnStatusEffectSetChanged();
	}

	/// <summary>Owner / proxy: rebuild the list when the host's mirror changes. Host writes keep the parse key in step, so this is a string compare per call.</summary>
	void RefreshStatusEffectsFromSync()
	{
		var text = ActiveStatusEffectsSync ?? string.Empty;
		if ( string.Equals( text, _statusEffectsParsedFrom, StringComparison.Ordinal ) )
			return;

		_statusEffectsParsedFrom = text;
		_statusEffects.Clear();

		if ( text.Length > 0 )
		{
			var entries = text.Split( '|' );
			for ( var i = 0; i < entries.Length; i++ )
			{
				var sep = entries[i].LastIndexOf( ':' );
				if ( sep <= 0 )
					continue;

				var id = entries[i].Substring( 0, sep );
				if ( !double.TryParse( entries[i].Substring( sep + 1 ), NumberStyles.Float, CultureInfo.InvariantCulture, out var expires ) )
					continue;

				_statusEffects.Add( new StatusEffectView( id, expires ) );
			}
		}

		OnStatusEffectSetChanged();
	}

	void OnStatusEffectSetChanged()
	{
		if ( IsHostOrOffline )
			HostRecalculatePoolMaxes();

		StatusEffectsChanged?.Invoke();
	}

	/// <summary>Host / offline, every frame: expire finished effects and apply per-second HP / stamina modifiers in batches.</summary>
	void TickStatusEffects( float dt )
	{
		if ( !IsHostOrOffline || dt <= 0f )
			return;

		RefreshStatusEffectsFromSync();
		if ( _statusEffects.Count == 0 )
			return;

		var now = Time.NowDouble;
		var removed = false;
		for ( var i = _statusEffects.Count - 1; i >= 0; i-- )
		{
			var view = _statusEffects[i];
			// Rested is held open by comfort even when its banked expiry has passed.
			if ( string.Equals( view.Id, StatusEffectCatalog.RestedId, StringComparison.OrdinalIgnoreCase ) && ComfortHeldRestedSeconds() > 0f )
				continue;

			if ( now < view.ExpiresAt )
				continue;

			if ( LogStatusEffects )
				Log.Info( $"{VitalsLogPrefix()} {GameObject.Name}: {view.Id} expired" );
			_statusEffects.RemoveAt( i );
			removed = true;
		}

		if ( removed )
			HostPublishStatusEffects();

		var hpPerSecond = 0f;
		var stPerSecond = 0f;
		for ( var i = 0; i < _statusEffects.Count; i++ )
		{
			if ( !StatusEffectCatalog.TryGet( _statusEffects[i].Id, out var effect ) )
				continue;

			hpPerSecond += effect.HealthPerSecond;
			stPerSecond += effect.StaminaPerSecond;
		}

		if ( hpPerSecond == 0f && stPerSecond == 0f )
		{
			_statusHealthCarry = 0f;
			_statusStaminaCarry = 0f;
			return;
		}

		_statusHealthCarry += hpPerSecond * dt;
		_statusStaminaCarry += stPerSecond * dt;
		if ( MathF.Abs( _statusHealthCarry ) < StatusRegenBatchThreshold && MathF.Abs( _statusStaminaCarry ) < StatusRegenBatchThreshold )
			return;

		var hp = _statusHealthCarry;
		var st = _statusStaminaCarry;
		_statusHealthCarry = 0f;
		_statusStaminaCarry = 0f;

		if ( hp < 0f )
		{
			// Damage over time goes through the armor-less damage path so death / logging behave like any hit.
			var auth = VitalsAuthority.Instance;
			if ( auth is not null )
				auth.TryApplyDeltas( GameObject, hp, 0f, this, damageSource: this );
			else
				RequestVitalsDelta( hp, 0f );
			hp = 0f;
		}

		if ( hp != 0f || st != 0f )
			RequestVitalsDelta( hp, st );
	}

	/// <summary>Owner (any machine): ask the host to apply / clear an effect — the <c>status</c> console hack path.</summary>
	public void OwnerRequestDebugStatusEffect( string id, float durationSeconds )
	{
		if ( !IsLocalInputOwnedPawn() )
			return;

		if ( IsHostOrOffline )
		{
			HostApplyDebugStatusEffect( id, durationSeconds );
			return;
		}

		RpcHostDebugStatusEffect( id ?? string.Empty, durationSeconds );
	}

	[Rpc.Host]
	void RpcHostDebugStatusEffect( string id, float durationSeconds )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() )
			return;

		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller && !ConnectionIdentity.SameClient( caller, owner ) )
			return;

		HostApplyDebugStatusEffect( id, durationSeconds );
	}

	void HostApplyDebugStatusEffect( string id, float durationSeconds )
	{
		if ( string.Equals( id, "clear", StringComparison.OrdinalIgnoreCase ) )
		{
			HostClearStatusEffects();
			return;
		}

		if ( durationSeconds <= 0f )
		{
			HostRemoveStatusEffect( id );
			return;
		}

		if ( !HostSetStatusEffectExpiry( id, Time.NowDouble + durationSeconds, extendOnly: false ) )
			Log.Warning( $"[Hacks] status: unknown effect '{id}' (see data/status_effects.json)" );
	}
}
