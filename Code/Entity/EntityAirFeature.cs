using System;
using Sandbox;

namespace Game;

[Title( "Entity Air Feature" )]
[Category( "Entity" )]
public sealed class EntityAirFeature : Component
{
	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnMaxAirChanged ) )]
	public float MaxAir { get; set; } = 100f;

	[Sync( SyncFlags.FromHost ), Change( nameof( OnCurrentAirChanged ) )]
	public float CurrentAir { get; set; } = 100f;

	[Property] public bool EnabledForThisEntity { get; set; }
	[Property] public float AirDrainPerSecondUnderwater { get; set; } = 12f;
	[Property] public float AirRefillPerSecondAboveWater { get; set; } = 35f;
	[Property] public float DrownDamagePerSecond { get; set; } = 8f;

	public event Action<float, float> OnAirChanged;

	public float AirFraction => MaxAir > 0.001f ? Math.Clamp( CurrentAir / MaxAir, 0f, 1f ) : 0f;

	protected override void OnStart()
	{
		var core = EntityCore.FindOnHierarchy( GameObject );
		if ( core is not null )
			EnabledForThisEntity = core.EnableAir;

		if ( !IsAuthority() )
			return;
		MaxAir = Math.Max( 1f, MaxAir );
		CurrentAir = Math.Clamp( CurrentAir, 0f, MaxAir );
	}

	protected override void OnFixedUpdate()
	{
		if ( !EnabledForThisEntity || !IsAuthority() )
			return;

		var pc = FindPlayerController();
		if ( pc is null || !pc.IsValid() )
			return;

		if ( pc.IsSwimming )
		{
			CurrentAir = Math.Max( 0f, CurrentAir - Math.Max( 0f, AirDrainPerSecondUnderwater ) * Time.Delta );
			if ( CurrentAir <= 0.001f )
			{
				var health = EntityHealthFeatureFinder( pc.GameObject );
				health?.RemoveHealth( Math.Max( 0f, DrownDamagePerSecond ) * Time.Delta );
			}
		}
		else
		{
			CurrentAir = Math.Min( MaxAir, CurrentAir + Math.Max( 0f, AirRefillPerSecondAboveWater ) * Time.Delta );
		}
	}

	private static bool IsAuthority()
	{
		if ( !Networking.IsActive ) return true;
		return Networking.IsHost;
	}

	private void OnCurrentAirChanged( float oldValue, float newValue ) => OnAirChanged?.Invoke( CurrentAir, MaxAir );
	private void OnMaxAirChanged( float oldValue, float newValue ) => OnAirChanged?.Invoke( CurrentAir, MaxAir );

	public static EntityAirFeature FindForEntityRoot( GameObject start )
	{
		if ( start is null || !start.IsValid() ) return null;
		for ( var go = start; go is not null; go = go.Parent )
		{
			var air = go.Components.Get<EntityAirFeature>();
			if ( air is not null ) return air;
		}
		return start.Components.Get<EntityAirFeature>();
	}

	private PlayerController FindPlayerController()
	{
		for ( var go = GameObject; go is not null; go = go.Parent )
		{
			var pc = go.Components.Get<PlayerController>();
			if ( pc is not null ) return pc;
		}
		return GameObject.Components.Get<PlayerController>();
	}

	private static EntityHealthFeature EntityHealthFeatureFinder( GameObject start )
	{
		for ( var go = start; go is not null; go = go.Parent )
		{
			var h = go.Components.Get<EntityHealthFeature>();
			if ( h is not null ) return h;
		}
		return start.Components.Get<EntityHealthFeature>();
	}
}
