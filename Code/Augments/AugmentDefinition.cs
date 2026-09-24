using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Survival;

/// <summary>
/// Which gameplay hook an augment drives. Only abilities listed here have code behind them;
/// every other augment in the catalog is data-only (<see cref="AugmentDefinition.Implemented"/> = false).
/// </summary>
public enum AugmentAbility
{
	None = 0,
	/// <summary>Trigger: grounded launch at EffectScale × jump speed.</summary>
	SpringLegs = 1,
	// 2 was LateralDash — retired when the dodge roll became core movement.
	/// <summary>Passive: one mid-air hop per flight (Jump key).</summary>
	DoubleJump = 3,
	/// <summary>Passive: sneaking costs no stamina.</summary>
	SneakyFeet = 4,
	/// <summary>Trigger: brief slide while sprinting that refunds EffectScale stamina.</summary>
	RecoverySlide = 5,
	/// <summary>Passive: sprint held while winching = EffectScale × winch rate.</summary>
	GrappleDrive = 6,
	/// <summary>Passive: the first hit per cooldown does not drop the grapple.</summary>
	DeathGrip = 7,
	/// <summary>Passive: trap holds last half as long.</summary>
	HeatBreaker = 8,
	/// <summary>Passive: EffectChance to ignore a whole hit, once per cooldown.</summary>
	ArmorPlating = 9,
	/// <summary>Passive: a perfect parry boosts stamina regen × EffectScale for EffectSeconds.</summary>
	ParryRecharge = 10,
	/// <summary>Trigger: pins every enemy within EffectRadiusMeters for EffectSeconds.</summary>
	SonicBurst = 11,
	/// <summary>Trigger: every enemy within EffectRadiusMeters targets you.</summary>
	WarCry = 12,
}

/// <summary>How the player turns an augment on.</summary>
public enum AugmentActivation
{
	/// <summary>Always on while installed and paid for.</summary>
	Passive = 0,
	/// <summary>Assigned to one of the six key binds (1–6) on the Augments page.</summary>
	Trigger = 1,
	/// <summary>Picked from the F radial wheel.</summary>
	Wheel = 2,
}

/// <summary>What a trigger / wheel activation does.</summary>
public enum AugmentMode
{
	/// <summary>Fires once, then the cooldown runs.</summary>
	OneShot = 0,
	/// <summary>Turns on and stays on (draining its battery if it has one) until picked again or empty.</summary>
	Toggle = 1,
}

public sealed class AugmentDefinitionFile
{
	[JsonPropertyName( "augments" )]
	public List<AugmentDefinition> Augments { get; set; } = new();
}

public sealed class AugmentDefinition
{
	public const float DefaultCooldownSeconds = 10f;

	/// <summary>Canonical item id (bank / bag / installed slot ResourceId).</summary>
	public string Id { get; set; } = string.Empty;
	public string DisplayName { get; set; } = string.Empty;
	[JsonPropertyName( "icon" )]
	public string Icon { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;

	/// <summary>Design tree: Melee / Ranged / Cyber, or empty for the general (N/A) pool.</summary>
	[JsonPropertyName( "school" )]
	public string School { get; set; } = string.Empty;

	/// <summary>Movement or Combat.</summary>
	[JsonPropertyName( "category" )]
	public string Category { get; set; } = string.Empty;

	/// <summary>
	/// Sockets this augment may install into (e.g. <c>["LegQuads"]</c>). Most augments list one;
	/// later augments may list several. An augment never fits a socket that is not listed here.
	/// </summary>
	[JsonPropertyName( "slots" )]
	public List<string> Slots { get; set; } = new();

	[JsonPropertyName( "ability" )]
	public string Ability { get; set; } = string.Empty;

	/// <summary>passive / trigger / wheel — see <see cref="AugmentActivation"/>.</summary>
	[JsonPropertyName( "activation" )]
	public string Activation { get; set; } = "passive";

	/// <summary>oneshot / toggle — see <see cref="AugmentMode"/>. Ignored for passives.</summary>
	[JsonPropertyName( "mode" )]
	public string Mode { get; set; } = "oneshot";

	/// <summary>Seconds before the augment can fire (or be switched on) again. Passives with a cooldown use it for their once-per-window effect.</summary>
	[JsonPropertyName( "cooldownSeconds" )]
	public float CooldownSeconds { get; set; } = DefaultCooldownSeconds;

	/// <summary>Toggle battery: seconds it can stay on. 0 = unlimited.</summary>
	[JsonPropertyName( "batterySeconds" )]
	public float BatterySeconds { get; set; }

	/// <summary>Seconds to recharge an empty battery while off. 0 = twice <see cref="BatterySeconds"/>.</summary>
	[JsonPropertyName( "batteryRechargeSeconds" )]
	public float BatteryRechargeSeconds { get; set; }

	/// <summary>Generic effect duration (slide length, stun length, regen window…).</summary>
	[JsonPropertyName( "effectSeconds" )]
	public float EffectSeconds { get; set; }

	/// <summary>Generic effect magnitude (jump multiplier, stamina refund, winch scale, regen multiplier…).</summary>
	[JsonPropertyName( "effectScale" )]
	public float EffectScale { get; set; } = 1f;

	/// <summary>Generic effect radius in designer meters (sonic burst, war cry).</summary>
	[JsonPropertyName( "effectRadiusMeters" )]
	public float EffectRadiusMeters { get; set; }

	/// <summary>Generic effect chance 0..1 (armor plating).</summary>
	[JsonPropertyName( "effectChance" )]
	public float EffectChance { get; set; } = 1f;

	/// <summary>False = crafts and installs, but has no gameplay effect yet (shown in its info block).</summary>
	[JsonPropertyName( "implemented" )]
	public bool Implemented { get; set; } = true;

	public List<CraftingIngredient> Ingredients { get; set; } = new();
	public List<CraftingStatLine> Stats { get; set; } = new();

	[JsonPropertyName( "maxStack" )]
	public int MaxStack { get; set; } = 1;

	public string UnlockId { get; set; } = string.Empty;

	public int ResolvedMaxStack => MaxStack > 0 ? MaxStack : 1;
	public bool IsUnlockedByDefault => string.IsNullOrWhiteSpace( UnlockId );
	public float ResolvedCooldownSeconds => Math.Max( 0f, CooldownSeconds );
	public bool HasBattery => BatterySeconds > 0f;
	public float ResolvedBatteryRechargeSeconds =>
		BatteryRechargeSeconds > 0f ? BatteryRechargeSeconds : Math.Max( 1f, BatterySeconds * 2f );

	public AugmentActivation ResolvedActivation => Activation?.Trim().ToLowerInvariant() switch
	{
		"trigger" => AugmentActivation.Trigger,
		"wheel" => AugmentActivation.Wheel,
		_ => AugmentActivation.Passive,
	};

	public AugmentMode ResolvedMode => Mode?.Trim().ToLowerInvariant() switch
	{
		"toggle" => AugmentMode.Toggle,
		_ => AugmentMode.OneShot,
	};

	/// <summary>Trigger or wheel — something the player activates.</summary>
	public bool IsActivatable => ResolvedActivation != AugmentActivation.Passive;

	List<AugmentSlot> _parsedSlots;

	/// <summary>Parsed <see cref="Slots"/> (unparseable entries dropped). Cached after the first read.</summary>
	public IReadOnlyList<AugmentSlot> AllowedSlots
	{
		get
		{
			if ( _parsedSlots is not null )
				return _parsedSlots;

			var list = new List<AugmentSlot>();
			if ( Slots is not null )
			{
				for ( var i = 0; i < Slots.Count; i++ )
				{
					if ( AugmentSlots.TryParse( Slots[i], out var slot ) && !list.Contains( slot ) )
						list.Add( slot );
				}
			}

			_parsedSlots = list;
			return list;
		}
	}

	public bool AllowsSlot( AugmentSlot slot )
	{
		var allowed = AllowedSlots;
		for ( var i = 0; i < allowed.Count; i++ )
		{
			if ( allowed[i] == slot )
				return true;
		}

		return false;
	}

	/// <summary>First listed socket — used as the list-group key and the quick-move fallback.</summary>
	public bool TryGetPrimarySlot( out AugmentSlot slot )
	{
		var allowed = AllowedSlots;
		slot = allowed.Count > 0 ? allowed[0] : default;
		return allowed.Count > 0;
	}

	/// <summary>Gold coins the augment step charges to put this augment into <paramref name="slot"/> (price belongs to the socket).</summary>
	public int InstallGoldCost( AugmentSlot slot ) => AugmentBodyParts.InstallGoldCost( slot );

	public AugmentAbility ResolvedAbility
	{
		get
		{
			if ( string.IsNullOrWhiteSpace( Ability ) )
				return AugmentAbility.None;

			return Enum.TryParse( Ability.Trim(), ignoreCase: true, out AugmentAbility ability )
				? ability
				: AugmentAbility.None;
		}
	}
}
