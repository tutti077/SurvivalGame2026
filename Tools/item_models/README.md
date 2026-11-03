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
python Tools/item_models/generate_item_models.py                 # all 51
python Tools/item_models/generate_item_models.py stone rope      # named items
python Tools/item_models/generate_item_models.py --category trophies
```

A software render of every model lands in `Tools/item_models/preview/<name>.png` with one contact
sheet per category (`sheet_<category>.png`), so a change can be checked by eye without the editor.

## Categories and frames

| Category | Items | Frame |
|----------|-------|-------|
| `resources` | wood_log, stone, dirt, resin, plant_fiber, flint, bone, animal_fat, fox_meat, whitetail_meat, fox_pelt, deer_hide, jay_feather, rope, bone_glue, leather | centred on X/Y, standing on z = 0 |
| `tools` | torch, stone_axe, build_hammer, hoe, grapple | handle along +X, butt at the origin |
| `weapons` | wooden_club, flint_spear, bola, simple_bow, bone_sword, wood_arrow, fire_arrow, flint_arrow | handle along +X, butt at the origin (`HeldModelFit`: head wide along Z, thin along Y) |
| `armor` | cowl, headdress, leather_cap, leather_chest, leather_pants, bandana, hand_wraps (clothes folded flat, wraps rolled) | standing on z = 0 |
| `food` | night_crawlers, blueberry, honey (honeycomb), wheat | standing on z = 0 |
| `seeds` | elm_seed, ash_seed, wheat_seed, daisy_seed, blueberry_seed (all pouches with a logo) | standing on z = 0 |
| `trophies` | scav_trophy, boneback_trophy, fox_trophy, jay_trophy, feral_chieftain_trophy (human-shaped heads from `human_head`), scav_figurine | plaque in the XZ plane, head toward -Y, standing on z = 0 |

Not generated: the wooden shield (`Assets/models/weapons/shield_wood` already exists from
`Blender/scripts/create_shield_wood.py`).

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

Seeds are fixed per item name, so re-running reproduces the same meshes; change the geometry in the
builder to iterate, and look at the preview before committing.
