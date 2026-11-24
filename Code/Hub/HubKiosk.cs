using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

public enum HubKioskKind
{
	/// <summary>The bar's bookie: next fights, fighter records, place a bet (<see cref="HubBetBoard"/>).</summary>
	Bets,
	/// <summary>The games wing's arcade cabinet (<see cref="StratagemMachine"/>).</summary>
	Stratagem,
	/// <summary>The button by the bar's big screen: E commends the cameraman (<see cref="HubArenaScreen.HostCommend"/>), no menu.</summary>
	Commend,
}

/// <summary>Something in the hub you look at and press E on. Its board / machine component sits on the same object.</summary>
[Title( "Hub Kiosk" )]
public sealed class HubKiosk : Component
{
	static readonly List<HubKiosk> Active = new();

	[Property] public HubKioskKind Kind { get; set; }

	/// <summary>Prompt text ("Place Bets", "Play Stratagem Hero").</summary>
	[Property] public string Prompt { get; set; } = "Use";

	[Property, Title( "Reach (m)" ), Range( 1f, 12f )]
	public float ReachMeters { get; set; } = 4f;

	[Property, Title( "Look Cone (°)" ), Range( 5f, 60f )]
	public float LookConeDegrees { get; set; } = 30f;

	/// <summary>Height of the look target above the origin (an NPC's chest, a cabinet's screen).</summary>
	[Property, Title( "Target Height (m)" ), Range( 0f, 4f )]
	public float TargetHeightMeters { get; set; } = 1.3f;

	protected override void OnEnabled()
	{
		base.OnEnabled();
		if ( !Active.Contains( this ) )
			Active.Add( this );
	}

	protected override void OnDisabled()
	{
		Active.Remove( this );
		base.OnDisabled();
	}

	/// <summary>What the prompt says right now (the commend button follows the screen's state).</summary>
	public string PromptText =>
		Kind == HubKioskKind.Commend && Components.Get<HubArenaScreen>( FindMode.EverythingInSelfAndAncestors ) is { } screen
			? screen.CommendPrompt
			: Prompt;

	Vector3 TargetWorld => WorldPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( TargetHeightMeters );

	/// <summary>The kiosk the viewer's camera points at, within its reach and look cone (closest, most centred wins).</summary>
	public static HubKiosk FindFocused( Vector3 eye, Vector3 viewDir )
	{
		HubKiosk best = null;
		var bestScore = float.MaxValue;
		foreach ( var k in Active )
		{
			if ( k is null || !k.IsValid() )
				continue;

			var to = k.TargetWorld - eye;
			var dist = to.Length;
			if ( dist < 1e-3f || dist > TerrainWorldUnits.MetersToEngine( k.ReachMeters ) )
				continue;

			var dot = Vector3.Dot( viewDir, to / dist );
			if ( dot < MathF.Cos( k.LookConeDegrees * (MathF.PI / 180f) ) )
				continue;

			var score = dist * (2f - dot);
			if ( score < bestScore )
			{
				bestScore = score;
				best = k;
			}
		}

		return best;
	}
}
