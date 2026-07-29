using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Hidden HP on a tree: melee hits (via <see cref="DamageReceiver"/>) chop it; at 0 HP the tree
/// hides and drops wood as world pickups. Requires an equipped Axe harvest tool.
/// </summary>
[Title( "Chopable Tree" )]
public sealed class ChopableTree : Component
{
	[Property, Group( "Chop" ), Title( "Max Health" ), Range( 1f, 500f )]
	public float MaxHealth { get; set; } = 40f;

	[Property, Group( "Chop" ), Title( "Current Health" )]
	public float CurrentHealth { get; private set; }

	[Property, Group( "Loot" ), Title( "Wood Resource Id" )]
	public string WoodResourceId { get; set; } = "resource_woodBasic";

	[Property, Group( "Loot" ), Title( "Wood Drop Min" ), Range( 1, 50 )]
	public int WoodDropMin { get; set; } = 10;

	[Property, Group( "Loot" ), Title( "Wood Drop Max" ), Range( 1, 50 )]
	public int WoodDropMax { get; set; } = 10;

	[Property, Group( "Chop" ), Title( "Require Axe" )]
	public bool RequireAxe { get; set; } = true;

	[Property, Group( "Debug" )]
	public bool LogChop { get; set; }

	bool _broken;

	public bool IsBroken => _broken;

	bool IsHostAuthority =>
		GameObject.Network is not { Active: true } || Networking.IsHost;

	protected override void OnStart()
	{
		base.OnStart();
		if ( CurrentHealth <= 0f || CurrentHealth > MaxHealth )
			CurrentHealth = Math.Max( 1f, MaxHealth );
	}

	/// <summary>Host: apply chop damage from a melee hit. Returns HP removed.</summary>
	public float ApplyChopDamage( float amount, Component attacker )
	{
		if ( !IsHostAuthority || _broken || amount <= 0f )
			return 0f;

		if ( RequireAxe && !AttackerHasAxe( attacker ) )
		{
			if ( LogChop )
				Log.Info( $"[ChopableTree] {GameObject.Name}: hit ignored — axe required." );
			return 0f;
		}

		var before = CurrentHealth;
		CurrentHealth = Math.Max( 0f, CurrentHealth - amount );
		var dealt = before - CurrentHealth;

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: -{dealt:0.#} HP ({CurrentHealth:0.#}/{MaxHealth:0.#})." );

		if ( CurrentHealth <= 1e-3f )
			BreakAndDrop( attacker );

		return dealt;
	}

	void BreakAndDrop( Component attacker )
	{
		if ( _broken )
			return;

		_broken = true;
		CurrentHealth = 0f;
		ApplyBrokenVisual();

		var min = Math.Max( 1, Math.Min( WoodDropMin, WoodDropMax ) );
		var max = Math.Max( min, WoodDropMax );
		var count = min;
		if ( max > min )
			count = min + (int)MathF.Floor( Sandbox.Game.Random.Float( 0f, max - min + 0.999f ) );
		var resourceId = string.IsNullOrWhiteSpace( WoodResourceId ) ? "resource_woodBasic" : WoodResourceId;

		var scene = GameObject.Scene.IsValid() ? GameObject.Scene : Sandbox.Game.ActiveScene;
		var ignore = (attacker?.GameObject.IsValid() == true ? attacker.GameObject : GameObject);

		for ( var i = 0; i < count; i++ )
		{
			var yaw = Sandbox.Game.Random.Float( 0f, 360f );
			var dist = Sandbox.Game.Random.Float( 18f, 55f );
			var outward = Rotation.FromYaw( yaw ).Forward;
			var offset = outward * dist + Vector3.Up * Sandbox.Game.Random.Float( 12f, 28f );
			var instance = HeldStackWorldDrop.TrySpawnWorldDrop(
				scene,
				resourceId,
				1,
				GameObject.WorldPosition + offset,
				ignore,
				applyDropperSelfPickupDelay: false );
			if ( instance is not null && instance.IsValid() )
				HeldStackWorldDrop.ApplyScatterBurst( instance, outward );
		}

		if ( LogChop )
			Log.Info( $"[ChopableTree] {GameObject.Name}: broken — dropped {count}x {resourceId}." );

		if ( GameObject.Network is { Active: true } )
			RpcBroadcastBroken();

		EntityNoiseBus.Emit( scene, GameObject.WorldPosition, EntityNoiseKind.ChopTree, ignore );
	}

	void ApplyBrokenVisual()
	{
		foreach ( var renderer in Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( renderer is null )
				continue;
			renderer.Enabled = false;
		}

		foreach ( var col in Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( col is null || col.IsTrigger )
				continue;
			col.Enabled = false;
		}

		foreach ( var prop in Components.GetAll<Prop>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( prop is not null )
				prop.Enabled = false;
		}

		foreach ( var body in Components.GetAll<Rigidbody>( FindMode.EverythingInSelfAndDescendants ) )
		{
			if ( body is not null )
				body.Enabled = false;
		}
	}

	[Rpc.Broadcast( NetFlags.HostOnly )]
	void RpcBroadcastBroken()
	{
		_broken = true;
		CurrentHealth = 0f;
		ApplyBrokenVisual();
	}

	static bool AttackerHasAxe( Component attacker )
	{
		if ( attacker is null || !attacker.GameObject.IsValid() )
			return false;

		PlayerEquippedItem equipped = null;
		for ( var p = attacker.GameObject; p.IsValid(); p = p.Parent )
		{
			equipped = p.Components.Get<PlayerEquippedItem>();
			if ( equipped is not null )
				break;
		}

		equipped ??= attacker.GameObject.Components.Get<PlayerEquippedItem>( FindMode.EverythingInSelfAndDescendants );
		if ( equipped is null )
			return false;

		var id = equipped.EquippedResourceId;
		if ( string.IsNullOrWhiteSpace( id ) )
			id = equipped.ActiveHotbarResourceId;

		if ( string.IsNullOrWhiteSpace( id ) )
			return false;

		if ( EquipmentCatalog.TryGet( id, out var profile )
		     && string.Equals( profile.HarvestToolType, "Axe", StringComparison.OrdinalIgnoreCase ) )
			return true;

		return id.Contains( "axe", StringComparison.OrdinalIgnoreCase );
	}
}
