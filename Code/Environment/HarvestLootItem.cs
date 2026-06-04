namespace Survival;

/// <summary>Resolved loot from one harvest tick (after chance and amount rolls).</summary>
public readonly record struct HarvestLootItem( string ResourceId, int Amount, string DisplayName );
