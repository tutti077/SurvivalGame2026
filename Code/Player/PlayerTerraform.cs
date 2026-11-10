using System;
using Sandbox;

namespace Survival;

/// <summary>
/// Ground work for the owning pawn: the pickaxe's chip (<see cref="ToolPickaxe"/>) and the hoe's grade / paint strokes
/// (<see cref="ToolHoe"/>). The tools aim and preview on the client; every stroke comes through here as intent, the host
/// checks it once (tool not broken, the point within the tool's reach, the voxel target's own radius / depth rules), applies
/// it through the scene's <see cref="IVoxelBrushTarget"/> and charges one durability tick <b>only when the ground actually
/// changed</b>. RPCs live on the networked pawn, never on the locally cloned tool prefab (the farming pattern). The pickaxe on
/// a <see cref="MineableRock"/> goes through the same door (<c>PlayerTerraform.Rock.cs</c>).
/// </summary>
[Title( "Player Terraform" )]
public sealed partial class PlayerTerraform : Component
{
	/// <summary>
	/// The hoe's dirt costs nothing while this is on, or while the owner's <c>allCrafting</c> hack is (the same command that
	/// makes crafting and augments free; the hammer's Free Build Enabled does the same for building). Mark 2026-11-02:
	/// "Right now everything is free ... make the hoe stuff free too under the same ingame command. Make it a command as
	/// well as a game flag we can set before runtime." This property is the flag; <c>allCrafting true|false</c> is the command.
	/// </summary>
	[Property, Group( "Materials" ), Title( "Free Materials" ), Description( "Raise costs no dirt and Cobblestone no stone (dev). Also free whenever the allCrafting hack is on." )] public bool FreeMaterials { get; set; } = true;
	[Property, Group( "Materials" ), Title( "Dirt Resource Id" )] public string DirtResourceId { get; set; } = "resource_dirt";
	[Property, Group( "Materials" ), Title( "Raise Dirt Cost" ), Range( 0, 20 ), Description( "Dirt one Raise layer costs (Mark 2026-11-02: \"make it cost some dirt\")." )] public int RaiseDirtCost { get; set; } = 1;
	[Property, Group( "Materials" ), Title( "Chip Dirt Yield" ), Range( 0, 20 ), Description( "Dirt one pickaxe chip of soil hands back - what the Raise layer is paid with." )] public int ChipDirtYield { get; set; } = 1;
	[Property, Group( "Materials" ), Title( "Chip Stone Yield" ), Range( 0, 20 ), Description( "Stone one pickaxe chip of rock hands back (rock tile: mountain, 4-8 m down, cobblestone) instead of dirt." )] public int ChipStoneYield { get; set; } = 1;
	[Property, Group( "Materials" ), Title( "Stone Resource Id" )] public string StoneResourceId { get; set; } = "resource_stone";
	[Property, Group( "Materials" ), Title( "Cobble Stone Cost" ), Range( 0, 20 ), Description( "Stone one Cobblestone stroke costs (Mark 2026-11-02: \"should require 1 rock per action\")." )] public int CobbleStoneCost { get; set; } = 1;
	[Property, Group( "Debug" )] public bool LogTerraform { get; set; }

	/// <summary>Host slack on top of a tool's reach so a lagging client is not rejected for a valid aim.</summary>
	const float ReachSlackMeters = 1.5f;

	/// <summary>Dirt and stone cost nothing: the prefab flag, or the owner's allCrafting hack (read from the pawn's synced mirror so the host honours the owner's setting).</summary>
	public bool MaterialsAreFree => FreeMaterials || (Components.Get<PlayerCrafting>()?.AllCraftingHack ?? GameHacks.AllCrafting);

	/// <summary>What a stroke of this kind costs in materials (null = nothing): Raise takes dirt, Cobblestone takes stone, both waived while <see cref="MaterialsAreFree"/>.</summary>
	CraftingIngredient[] MaterialCostFor( VoxelBrushMode mode )
	{
		if ( MaterialsAreFree )
			return null;
		if ( mode == VoxelBrushMode.Raise && RaiseDirtCost > 0 && !string.IsNullOrWhiteSpace( DirtResourceId ) )
			return new[] { new CraftingIngredient { ResourceId = DirtResourceId, Amount = RaiseDirtCost } };
		if ( mode == VoxelBrushMode.PaintCobble && CobbleStoneCost > 0 && !string.IsNullOrWhiteSpace( StoneResourceId ) )
			return new[] { new CraftingIngredient { ResourceId = StoneResourceId, Amount = CobbleStoneCost } };
		return null;
	}

	IVoxelBrushTarget _target;

	/// <summary>The scene's brush target (the prototype or the live world), or null in a scene without one.</summary>
	public IVoxelBrushTarget Target
	{
		get
		{
			if ( _target is Component c && !c.IsValid() )
				_target = null;
			return _target ??= VoxelBrushTargets.Find( Scene );
		}
	}

	/// <summary>Owner: the pickaxe landed on the ground at <paramref name="hitEngine"/> with surface normal <paramref name="normal"/>.</summary>
	public void OwnerRequestChip( Vector3 hitEngine, Vector3 normal, float radiusMeters, float depthMeters, float reachMeters )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostChip( hitEngine, normal, radiusMeters, depthMeters, reachMeters );
			return;
		}
		RpcHostChip( hitEngine, normal, radiusMeters, depthMeters, reachMeters );
	}

	/// <summary>Owner: the hoe's grade (Flatten / Slope) or paint (PaintGrass / PaintBare) at a ground point.</summary>
	public void OwnerRequestBrush( Vector3 hitEngine, float radiusMeters, VoxelBrushMode mode, float reachMeters, bool square = false, float depth = 0f )
	{
		if ( GameObject.Network is not { Active: true } || Networking.IsHost )
		{
			HostBrush( hitEngine, radiusMeters, (int)mode, reachMeters, square, depth );
			return;
		}
		RpcHostBrush( hitEngine, radiusMeters, (int)mode, reachMeters, square, depth );
	}

	/// <summary>Client preview: can the owner pay for a stroke of this kind right now (stamina; dirt for a Raise)? The host decides again.</summary>
	public bool CanAffordStroke( VoxelBrushMode mode, out string reason )
	{
		reason = string.Empty;
		var vitals = Components.Get<PlayerVitals>();
		var cost = mode == VoxelBrushMode.Chip ? vitals?.PickaxeSwingStaminaCost ?? 0f : vitals?.HoeStrokeStaminaCost( mode ) ?? 0f;
		if ( vitals is not null && !vitals.CanAffordStamina( cost ) )
		{
			reason = "too tired";
			return false;
		}
		var materials = MaterialCostFor( mode );
		if ( materials is not null )
		{
			var inventory = Components.Get<PlayerInventory>();
			if ( inventory is null || !inventory.HasResources( materials ) )
			{
				reason = $"needs {materials[0].Amount} {ResourceCatalog.Resolve( materials[0].ResourceId ).DisplayName}";
				return false;
			}
		}
		return true;
	}

	bool IsCallerTheOwner()
	{
		if ( GameObject.Network is { Active: true, Owner: { } owner } && Rpc.Caller is { } caller
		     && !ConnectionIdentity.SameClient( caller, owner ) )
			return false;
		return true;
	}

	[Rpc.Host]
	void RpcHostChip( Vector3 hitEngine, Vector3 normal, float radiusMeters, float depthMeters, float reachMeters )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;
		HostChip( hitEngine, normal, radiusMeters, depthMeters, reachMeters );
	}

	[Rpc.Host]
	void RpcHostBrush( Vector3 hitEngine, float radiusMeters, int mode, float reachMeters, bool square, float depth )
	{
		if ( !Networking.IsHost || !GameObject.IsValid() || !IsCallerTheOwner() )
			return;
		HostBrush( hitEngine, radiusMeters, mode, reachMeters, square, depth );
	}

	void HostChip( Vector3 hitEngine, Vector3 normal, float radiusMeters, float depthMeters, float reachMeters )
	{
		if ( !HostCanWork( hitEngine, reachMeters, out var reason ) )
		{
			if ( LogTerraform )
				Log.Info( $"[PlayerTerraform] chip rejected: {reason}" );
			return;
		}
		var vitals = Components.Get<PlayerVitals>();
		var stamina = vitals?.PickaxeSwingStaminaCost ?? 0f;
		if ( vitals is not null && !vitals.CanAffordStamina( stamina ) )
		{
			if ( LogTerraform )
				Log.Info( "[PlayerTerraform] chip rejected: too tired" );
			return;
		}
		// What the pick is hitting decides the drop (Mark 2026-11-02: "the pickaxe needs to check what its hitting"): read the
		// surface before the chip takes it away - rock (the shader's rock tile) gives stone, soil gives dirt.
		var rock = Target.RockAt( hitEngine - (normal.Normal * TerrainWorldUnits.MetersToEngine( Math.Max( 0.05f, depthMeters ) * 0.5f )) ) >= 0.5f;
		if ( !Target.RequestChip( hitEngine, normal, radiusMeters, depthMeters ) )
			return;
		// The ground changed: the swing is paid for - wear, stamina, and what it loosened goes to the bag
		ToolDurability.HostAddWearToActiveTool( GameObject );
		vitals?.TrySpendStamina( stamina );
		var yieldId = rock ? StoneResourceId : DirtResourceId;
		var yieldCount = rock ? ChipStoneYield : ChipDirtYield;
		if ( yieldCount > 0 && !string.IsNullOrWhiteSpace( yieldId ) )
			Components.Get<PlayerInventory>()?.HostTryAddResource( yieldId, yieldCount );
		if ( LogTerraform )
			Log.Info( $"[PlayerTerraform] chip r {radiusMeters:0.00} m, {depthMeters:0.00} m deep at {TerrainWorldUnits.EngineToMeters( hitEngine )}" );
	}

	void HostBrush( Vector3 hitEngine, float radiusMeters, int mode, float reachMeters, bool square, float depth )
	{
		var brush = (VoxelBrushMode)Math.Clamp( mode, 0, VoxelEdit.MaxMode );
		if ( brush is not (VoxelBrushMode.Flatten or VoxelBrushMode.Slope or VoxelBrushMode.PaintGrass or VoxelBrushMode.PaintBare or VoxelBrushMode.PaintTilled or VoxelBrushMode.PaintCobble or VoxelBrushMode.Raise) )
			return;   // the hoe grades, paints, paves and raises; dig / fill / chip are not its strokes
		if ( !HostCanWork( hitEngine, reachMeters, out var reason ) )
		{
			if ( LogTerraform )
				Log.Info( $"[PlayerTerraform] {brush} rejected: {reason}" );
			return;
		}
		var vitals = Components.Get<PlayerVitals>();
		var stamina = vitals?.HoeStrokeStaminaCost( brush ) ?? 0f;
		if ( vitals is not null && !vitals.CanAffordStamina( stamina ) )
		{
			if ( LogTerraform )
				Log.Info( $"[PlayerTerraform] {brush} rejected: too tired" );
			return;
		}
		var inventory = Components.Get<PlayerInventory>();
		var materials = MaterialCostFor( brush );
		if ( materials is not null && (inventory is null || !inventory.HasResources( materials )) )
		{
			if ( LogTerraform )
				Log.Info( $"[PlayerTerraform] {brush} rejected: needs {materials[0].Amount} {materials[0].ResourceId}" );
			return;
		}
		if ( !Target.RequestBrush( hitEngine, radiusMeters, brush, square, depth ) )
			return;
		// The ground changed: the stroke is paid for
		ToolDurability.HostAddWearToActiveTool( GameObject );
		vitals?.TrySpendStamina( stamina );
		if ( materials is not null )
			inventory.HostTryConsumeResources( materials );
		if ( LogTerraform )
			Log.Info( $"[PlayerTerraform] {brush} r {radiusMeters:0.00} m at {TerrainWorldUnits.EngineToMeters( hitEngine )}" );
	}

	/// <summary>Host rules shared by every stroke: a brush target, a working tool, the point within reach of the pawn.</summary>
	bool HostCanWork( Vector3 hitEngine, float reachMeters, out string reason )
	{
		reason = string.Empty;
		var target = Target;
		if ( target is null || !target.IsReady )
		{
			reason = "no voxel ground in this scene";
			return false;
		}
		if ( ToolDurability.IsActiveToolBroken( GameObject ) )
		{
			reason = "tool broken";
			return false;
		}
		var reach = TerrainWorldUnits.MetersToEngine( Math.Clamp( reachMeters, 0.5f, 10f ) + ReachSlackMeters );
		var eye = WorldPosition + Vector3.Up * TerrainWorldUnits.MetersToEngine( 1.6f );
		if ( (hitEngine - eye).Length > reach )
		{
			reason = "out of reach";
			return false;
		}
		return true;
	}
}
