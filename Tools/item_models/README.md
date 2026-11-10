# Item models (procedural low-poly, no Blender)

`generate_item_models.py` builds the pickup / held / display models for inventory items from
faceted primitives in plain Python (numpy + Pillow) and writes each one as

```
Assets/models/<category>/<name>/<name>.obj    metres, Y-up (Blender OBJ export convention)
Assets/models/<category>/<name>/<name>.vmdl   import_scale 40 (40 u/m), single hull collision
Assets/models/<category>/<name>/<name>.png    256 x 256, 4 x 4 grid of 64 px material cells, 256 texels per metre
Assets/models/<category>/<name>/<name>.vmat   pixel_lit, point sampled, PixelFlatShading 1
```

Run from the repo root:

```
python Tools/item_models/generate_item_models.py                 # all 252
python Tools/item_models/generate_item_models.py dirt rope       # named items
python Tools/item_models/generate_item_models.py --category trophies
python Tools/item_models/generate_item_models.py --module items_fish --sheet fish_check   # one builder module, own sheet
```

A software render of every model lands in `Tools/item_models/preview/<name>.png` with one contact
sheet per category (`sheet_<category>.png`, each label carries the model's longest side in metres),
so a change can be checked by eye without the editor. `--sheet <name>` puts just the built items on
`sheet_<name>.png` instead of overwriting the category sheets. The preview culls back faces like the
engine, so an inside-out part shows up as a hole.

## Categories and frames

| Category | Items | Frame |
|----------|-------|-------|
| `resources` | wood_log, dirt, resin, plant_fiber, flint, bone, animal_fat, fox_meat, whitetail_meat, fox_pelt, deer_hide, jay_feather, rope, bone_glue, leather; daisy, hobbits_leaf, mycelium, vines, sand, clay, peat, kelp, wings (`rawmeat_bird`); meats blackbear / boar / coyote / goat / lynx / wolf / jackrabbit / snowhare / otter (drumsticks sized by animal), bullfrog_meat (legs), crab_meat (claw), iguana_meat (tail cut), shark_meat (steak), stingray_filet; pelts blackbear_fur / lynx_pelt / otter_pelt / wolf_pelt; dunewyrm_scales, goat_horns, alligator_teeth, wolf_fangs, vulture_talons, badger_claws, stingray_barbs, turtle_shell, dragonfly_wings, spider_silk, lightning_bug | centred on X/Y, standing on z = 0 |
| `materials` | ores ore_copper / ore_iron / ore_tin / ore_titanium (rock lumps, mineral on a few big facets), lithium, obsidian, sulfur, quartz (`resource_silica`), salt, scrap, crude_oil; ingots ingot_copper / ingot_iron / ingot_titanium / ingot_arcsteel / silica_ingot; charcoal, clay_bricks, glass, chains, nails, pipes, scrap_pipe, scrap_wire, springs, gears, hydraulic_parts, lubricant, chlorine, fuel (jerry can), power_cell, microchips, wiring_harness, carbon_fiber, vinyl, synthetic_rubber, plastics | standing on z = 0 |
| `fish` | bass, bluegill, catfish, eel, perch, pickerel, pike, sunfish, trout, walleye (real catch lengths 0.19-0.78 m, lying on the left flank; one lofted-body builder in `items_fish.py`, markings painted per triangle) | standing on z = 0 |
| `tools` | torch, stone_axe, build_hammer, hoe, grapple, fishing_rod, wire_stripper; axe_scrap / _iron / _steel / _titanium / _plasma, pickaxe_scrap / _iron / _steel / _titanium / _reforged / _plasma | handle along +X, butt at the origin; lantern stands on z = 0 |
| `weapons` | wooden_club, flint_spear, bola, simple_bow, bone_sword, wood_arrow, fire_arrow, flint_arrow; sword_scrap / _iron / _titanium / _arcsteel / _pulse, greatsword_scrap / _iron / _reforged / _arcsteel (servo), dagger_reforged, club_scrap, club_iron / mace_reforged (flanged), club_arcsteel (morning star), mace_arcsteel (coil), hammer_arcsteel, spear_iron, spear_reforged (pike), fist_scrap / _iron / _arcsteel, bow_long, bow_quickdraw, bow_iron; simple_bow_held (the held bow: simple_bow without its string, which `PlayerAnimation.Bow.cs` draws live) | handle along +X, butt at the origin (`HeldModelFit`: head wide along Z, thin along Y) |
| `armor` | cowl, headdress, leather_cap, leather_chest, leather_pants, bandana, hand_wraps (clothes folded flat, wraps rolled); helmet / chestplate / pants _scrap and _reforged, arms_reforged, gloves_reforged, boots_reforged, goggles_sand, basic_wingsuit | standing on z = 0 |
| `food` | night_crawlers, blueberry, honey (honeycomb), wheat; mushroom, radish, raspberry, berry_skewers, hunter_stew, roasted_mushroom / _lynx (skewers), roasted_fox / _whitetail / _bird (on a board), glow_bugs (`bait_glowBug`, a jar) | standing on z = 0 |
| `seeds` | elm_seed, ash_seed, wheat_seed, daisy_seed, blueberry_seed, carrot_seed, radish_seed, potato_seed, pepper_seed, raspberry_seed, watermelon_seed, cacti_seed, aloe_seed, joshua_seed (all pouches with a logo) | standing on z = 0 |
| `trophies` | scav_trophy, boneback_trophy, fox_trophy, jay_trophy, feral_chieftain_trophy (human-shaped heads from `human_head`), scav_figurine; `<creature>_trophy` for every `trophy_*` id in `items_heads.py` (animal heads from one muzzle / ears builder, raiders from `human_head` plus gear, machines from `robot_head`) | plaque in the XZ plane, head toward -Y, standing on z = 0 |
| `cyber` | cyber_acid, cyber_arcDischarge, cyber_bladeSwarm, cyber_fireball, cyber_iceShard (implant module housing with the technique's emblem) | standing on z = 0 |
| `misc` | gold_coins, augment_core, lucky_rabbits_foot | standing on z = 0 |
| `building` | crafting stations (`items_stations.py`): workbench, drying_rack, furnace (with its crucible), anvil, refinery, cauldron, fabricator, chemistry_station, augment_station (medical chair + tool arm) - temp models, 2026-11-08 | build-piece frame: bbox centred on the origin, wide side along Y, **40 u/m** (`import_scale` 40 = `BuildModuleDimensions.UnitsPerMeter`, like the wood kit); the printed size is the `BuildModuleDimensions.SizesMeters` row and the prefab box collider |
| `building` | crafting stations (`items_stations.py`): workbench, drying_rack, furnace (with its crucible), anvil, refinery, cauldron, fabricator, chemistry_station, augment_station (medical chair + tool arm) - temp models, 2026-11-08 | build-piece frame: bbox centred on the origin, wide side along Y, **40 u/m** (`import_scale` 40 = `BuildModuleDimensions.UnitsPerMeter`, like the wood kit); the printed size is the `BuildModuleDimensions.SizesMeters` row and the prefab box collider |

Metal tiers (`itemgen/kit.py` `TIERS`): one builder per weapon / tool shape takes a tier - scrap (rusty
brown with riveted grey patch plates, rope grips), iron (dark grey), steel (bright grey), titanium
(pale blue-grey), arcsteel (near-black with teal glow lines; also used for the `_steel` ids, whose
display names say Arcsteel), plasma (dark housing with a magenta glowing cell), reforged (blue-grey
steel, brass rivets, weathered wood - matched to `materials/weapons/axe_reforged.png`).

Not generated: the stone (`resource_stone` drops as one of the four `stone_drop*_v1` gunmetal stones from
`Blender/scripts/create_mineable_rocks.py`) and the wooden shield (`Assets/models/weapons/shield_wood` already exists from
`Blender/scripts/create_shield_wood.py`). `sword_reforged` / `axe_reforged` drop as their existing
`models/weapons/*_reforged.vmdl`, and `shield_scrap` / `shield_tower` / `shield_reforged` / `buckler_reforged`
as `shield_wood` (their held model) - all wired in `Assets/data/item_world_models.json`.

## How a model is built

- `itemgen/mesh.py` — triangle-soup `Mesh` plus primitives: noisy icospheres (`blob`), faceted
  tubes and swept tubes (`tube`, `path_tube`), lathes, extruded outlines (`prism`), folded cards
  (`leaf`), boxes; `weld_displace` lumps a surface without opening cracks. Every quad is split on
  its shorter diagonal and vertices are jittered so facets vary in size (no primitive-readable
  silhouettes).
- `itemgen/texture.py` / `itemgen/palette.py` — each item's texture is up to 16 cells of
  low-contrast value noise, grain streaks, stripes, rings or speckle; a triangle's `mat` index
  picks its cell and `assign_uvs` projects the facet flat into that cell at the cell's texel
  density.
- `itemgen/export.py` — OBJ / vmdl / vmat writers. `itemgen/preview.py` — the rasteriser.
- `itemgen/items_resources.py`, `items_held.py`, `items_wearables.py` — one builder per item,
  registered with the `@item(name, category, surface, roughness)` decorator. Add an item there and
  run the script; shiny things (resin, honey, blueberry) lower `roughness` on the vmat.
- `itemgen/items_nature.py` (Clover Hills plants, ground materials, cooked food, new seed pouches,
  fishing rod, lantern), `items_fish.py`, `items_metal.py` (tiered weapons / tools / bows / metal
  armour), `items_materials.py` (ores, crafted parts, meats, animal parts, misc), `items_heads.py`
  (trophies, cyberware) — family builders on the shared helpers in `itemgen/kit.py` (`registry`,
  `loft` bodies along X, `broad_leaf`, `subdivide`, `paint` for per-triangle markings, `TIERS`).
  Markings (fish bars, spots, scute lines) are painted per triangle with `paint`, never into the
  texture: `assign_uvs` gives every facet a random patch of its cell.

Seeds are fixed per item name, so re-running reproduces the same meshes; change the geometry in the
builder to iterate, and look at the preview before committing.
