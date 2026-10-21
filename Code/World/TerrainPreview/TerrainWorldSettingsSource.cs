namespace Survival;

/// <summary>Where <see cref="TerrainWorldManager"/> loads generation knobs before chunk meshing.</summary>
public enum TerrainWorldSettingsSource
{
	/// <summary>Latest editor Generate bundle, then saved world recipe, then component defaults. A Generate in the tool silently changes the world, so not the default any more (2026-10-08).</summary>
	TunedPreviewFirst,

	/// <summary>Saved <c>WorldSaves/&lt;name&gt;/world.json</c> recipe, then latest bundle.</summary>
	WorldRecipeFirst,

	/// <summary>Component world scalars + C# knob defaults only (no bundle/recipe).</summary>
	ComponentDefaultsOnly,
}
