using System;
using System.Collections.Generic;
using Sandbox;

namespace Survival;

/// <summary>
/// Placeable campfire: fuel 0–20 (each wood = 60s), auto-lit while fueled (orange / grey),
/// E adds wood from inventory. Proximity unlocks campfire cooking recipes.
/// </summary>
[Title( "Campfire" )]
public sealed class Campfire : Component
{
	public const string StationId = "campfire";
	public const string FuelResourceId = "resource_woodBasic";

	static readonly List<Campfire> Active = new();

	[Property, Group( "Campfire" ), Title( "Max fuel units" ), Range( 1, 40 )]
	public int MaxFuelUnits { get; set; } = 20;

	[Property, Group( "Campfire" ), Title( "Seconds per fuel unit" ), Range( 10f, 300f )]
	public float SecondsPerFuelUnit { get; set; } = 60f;

	[Property, Group( "Campfire" ), Title( "Cooking range (m)" ), Range( 1f, 20f )]
	public float CookingRangeMeters { get; set; } = 5f;

	[Property, Group( "Campfire" ), Title( "Add-fuel reach (m)" ), Range( 1f, 8f )]
	public float AddFuelReachMeters { get; set; } = 3f;

	[Property, Group( "Campfire" ), Title( "Lit color" )]
	public Color LitColor { get; set; } = new( 1f, 0.45f, 0.12f );

	[Property, Group( "Campfire" ), Title( "Unlit color" )]
	public Color UnlitColor { get; set; } = new( 0.45f, 0.45f, 0.48f );

	[Sync] public int FuelUnits { get; private set; }
	[Sync] public bool IsLit { get; private set; }

	float _burnProgress;
	ModelRenderer _renderer;

	public static IReadOnlyList<Campfire> All => Active;

	public bool HasHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

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

	protected override void OnDestroy()
	{
		Active.Remove( this );
		base.OnDestroy();
	}

	protected override void OnStart()
	{
		base.OnStart();
		_renderer = Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
		ApplyLitVisual();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		// Tint follows [Sync] IsLit on clients too.
		ApplyLitVisual();
	}

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( !HasHostAuthority )
			return;

		TickBurn( Time.Delta );
	}

	void TickBurn( float dt )
	{
		if ( FuelUnits <= 0 )
		{
			SetLit( false );
			_burnProgress = 0f;
			return;
		}

		SetLit( true );
		_burnProgress += Math.Max( 0f, dt );
		var perUnit = Math.Max( 1f, SecondsPerFuelUnit );
		while ( _burnProgress >= perUnit && FuelUnits > 0 )
		{
			_burnProgress -= perUnit;
			FuelUnits--;
		}

		if ( FuelUnits <= 0 )
		{
			FuelUnits = 0;
			_burnProgress = 0f;
			SetLit( false );
		}
	}

	void SetLit( bool lit )
	{
		if ( IsLit == lit )
		{
			ApplyLitVisual();
			return;
		}

		IsLit = lit;
		ApplyLitVisual();
	}

	void ApplyLitVisual()
	{
		_renderer ??= Components.Get<ModelRenderer>( FindMode.EverythingInSelf );
		if ( _renderer is null || !_renderer.IsValid() )
			return;

		_renderer.Tint = IsLit ? LitColor : UnlitColor;
	}

	public bool HostTryAddFuelFrom( PlayerInventory inventory )
	{
		if ( !HasHostAuthority || inventory is null || !inventory.HasHostAuthority )
			return false;

		if ( FuelUnits >= Math.Max( 1, MaxFuelUnits ) )
			return false;

		var ingredients = new[]
		{
			new CraftingIngredient { ResourceId = FuelResourceId, Amount = 1 }
		};
		if ( !inventory.HostTryConsumeResources( ingredients ) )
			return false;

		FuelUnits = Math.Min( MaxFuelUnits, FuelUnits + 1 );
		SetLit( true );
		return true;
	}

	public static bool IsPlayerNearLitOrFueledStation( GameObject player, string stationId, float? rangeMetersOverride = null )
	{
		if ( player is null || !player.IsValid() )
			return false;

		if ( !string.Equals( stationId, StationId, StringComparison.OrdinalIgnoreCase ) )
			return false;

		var origin = player.WorldPosition;

		for ( var i = 0; i < Active.Count; i++ )
		{
			var fire = Active[i];
			if ( fire is null || !fire.IsValid() || !fire.GameObject.IsValid() )
				continue;

			// Cooking available while the fire has fuel (auto-lit) or still showing lit.
			if ( fire.FuelUnits <= 0 && !fire.IsLit )
				continue;

			var meters = rangeMetersOverride ?? fire.CookingRangeMeters;
			var range = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, meters ) );
			if ( (fire.GameObject.WorldPosition - origin).LengthSquared <= range * range )
				return true;
		}

		return false;
	}

	public static bool TryFindFocusedCampfire( GameObject viewer, float reachMeters, out Campfire campfire )
	{
		campfire = null;
		if ( viewer is null || !viewer.IsValid() )
			return false;

		if ( !BuildViewCamera.TryGetViewRay( viewer, out var origin, out var direction ) )
			return false;

		var scene = viewer.Scene.IsValid() ? viewer.Scene : Sandbox.Game.ActiveScene;
		if ( scene is null || !scene.IsValid() )
			return false;

		var reach = TerrainWorldUnits.MetersToEngine( Math.Max( 0.5f, reachMeters ) );
		var tr = scene.Trace.Ray( origin, origin + direction * reach )
			.IgnoreGameObjectHierarchy( viewer )
			.Run();

		if ( !tr.Hit || !tr.GameObject.IsValid() )
			return false;

		for ( var go = tr.GameObject; go.IsValid(); go = go.Parent )
		{
			var c = go.Components.Get<Campfire>();
			if ( c is not null && c.Enabled )
			{
				campfire = c;
				return true;
			}
		}

		return false;
	}
}
