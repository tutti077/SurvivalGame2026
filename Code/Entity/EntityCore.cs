using System.Collections.Generic;
using Sandbox;

namespace Game;

public enum EntityKind : byte
{
	Player = 0,
	Enemy = 1,
	Neutral = 2
}

[Title( "Entity Core" )]
[Category( "Entity" )]
public sealed class EntityCore : Component
{
	private static int _nextPlayerId = 1;
	private static readonly Dictionary<string, int> EnemyCounters = new();

	[Property] public EntityKind Kind { get; set; } = EntityKind.Enemy;
	[Property] public string EnemyName { get; set; } = "enemyName";
	[Property] public string DisplayNameOverride { get; set; } = "";
	[Property] public bool DisplayUsesInternalId { get; set; }
	[Property] public Vector3 OverheadAnchorLocalOffset { get; set; } = new Vector3( 0f, 0f, 42f );
	[Property] public float PlayerBaseMaxHealth { get; set; } = 100f;
	[Property] public float EnemyBaseMaxHealth { get; set; } = 100f;
	[Property] public float NeutralBaseMaxHealth { get; set; } = 100f;
	[Property] public bool EnableStamina { get; set; } = true;
	[Property] public bool EnableAir { get; set; } = false;

	[Property, ReadOnly] public string EntityId { get; private set; } = "";
	[Property, ReadOnly] public string DisplayName { get; private set; } = "";

	protected override void OnStart()
	{
		EnsureIdentity();
	}

	public void EnsureIdentity()
	{
		if ( !string.IsNullOrWhiteSpace( EntityId ) && !string.IsNullOrWhiteSpace( DisplayName ) )
			return;

		string baseName;
		if ( Kind == EntityKind.Player )
		{
			var playerIndex = _nextPlayerId++;
			baseName = "player";
			EntityId = $"{baseName}({playerIndex})";
			DisplayName = ResolveDisplayName( baseName );
			return;
		}

		baseName = Kind == EntityKind.Enemy
			? (string.IsNullOrWhiteSpace( EnemyName ) ? "enemyName" : EnemyName.Trim())
			: "entity";
		if ( !EnemyCounters.TryGetValue( baseName, out var n ) )
			n = 0;
		n++;
		EnemyCounters[baseName] = n;

		EntityId = $"{baseName}({n})";
		DisplayName = ResolveDisplayName( baseName );
	}

	private string ResolveDisplayName( string baseName )
	{
		if ( !string.IsNullOrWhiteSpace( DisplayNameOverride ) )
			return DisplayNameOverride.Trim();
		if ( DisplayUsesInternalId )
			return EntityId;
		return baseName;
	}

	public static EntityCore FindOnHierarchy( GameObject start )
	{
		for ( var go = start; go is not null; go = go.Parent )
		{
			var core = go.Components.Get<EntityCore>();
			if ( core is not null )
				return core;
		}

		return null;
	}

	public static EntityCore EnsureOn( GameObject go, EntityKind fallbackKind = EntityKind.Enemy )
	{
		if ( go is null || !go.IsValid() )
			return null;

		var core = go.Components.Get<EntityCore>();
		if ( core is null )
		{
			core = go.Components.Create<EntityCore>();
			core.Kind = fallbackKind;
		}

		core.EnsureIdentity();
		return core;
	}

	public float GetConfiguredBaseMaxHealth()
	{
		return Kind switch
		{
			EntityKind.Player => Math.Max( 1f, PlayerBaseMaxHealth ),
			EntityKind.Enemy => Math.Max( 1f, EnemyBaseMaxHealth ),
			_ => Math.Max( 1f, NeutralBaseMaxHealth )
		};
	}

	public GameObject EnsureOverheadAnchor()
	{
		GameObject chosen = null;
		foreach ( var child in GameObject.Children )
		{
			if ( child is null || !child.IsValid() )
				continue;
			if ( child.Name != "EntityOverheadAnchor" )
				continue;

			if ( chosen is null )
				chosen = child;
			else
				child.Destroy();
		}

		chosen ??= new GameObject( true, "EntityOverheadAnchor" );
		chosen.Parent = GameObject;
		chosen.LocalPosition = OverheadAnchorLocalOffset;
		chosen.LocalRotation = Rotation.Identity;
		chosen.WorldScale = Vector3.One;
		return chosen;
	}
}
