# Blender scripts

Python scripts for generating or editing assets outside the s&box editor.

## Running a script

**Inside Blender**

1. Open Blender → **Scripting** workspace.
2. **Text → Open** and pick a file from `scripts/`.
3. Click **Run Script** (or Alt+P).

**From the command line** (headless)

```bash
blender --background --python "Blender/scripts/create_low_poly_rock.py"
```

Exported meshes can be saved as `.fbx` / `.gltf` from Blender and imported into your s&box asset pipeline.

## Scripts

| Script | Description |
|--------|-------------|
| `scripts/create_low_poly_rock.py` | Procedural low-poly rock mesh (~80 triangles, flat shaded) |
| `scripts/create_build_kit.py` | Every wood build piece in `Assets/data/build_pieces.json` — floors, walls, door, gables, roof panel + corners, straight and quarter-turn stairs, beams — each at true size around its own origin |
| `scripts/split_build_kit.py` | Writes one single-object `.blend` and one `.fbx` per piece, each sitting on the world origin |
| `scripts/create_grass_clump.py` | Four single-plane grass clumps (`grass_clump1..4`, 7/5/9/3 blades) — flat blade fans with wind weights in vertex colour, exported to `Assets/models/environment/grass/` for the Clutter grass system |
| `scripts/measure_crown_silhouette.py` | Measures how lumpy a crown's top outline is (lumpiness / dip % of crown width) from a photo or render; `create_elm_tree.py` imports it to score seeds |
| `scripts/create_elm_sapling.py` | Five elm saplings `environment_elmsapling1..5_v<N>` (1.6–1.95 m incl. leaves, 4–6 cm stems): one continuous wood mesh, small separate leaf tufts on 0.45 m cards, smooth grey young-elm bark with dark lenticel dashes (own texture, 1.6 cm/texel); plain ChopableTree prefabs (12 HP, 2–4 wood). Own `VERSION`, reuses create_elm_tree helpers; `-- render` writes `blenderprojects/elm_sapling_preview/` |
| `scripts/swap_elm_scene_version.py` | Re-points every `environment_elm<N>_v<OLD>` prefab instance in a `.scene` at the `v<NEW>` prefabs (path, asset guid, override targets), keeping transforms — run with the editor closed after a regeneration orphaned the placed trees |
| `scripts/create_elm_tree.py` | Six trees of three kinds — tiered central-trunk trees `environment_elm1/3/4_v<N>` (limbs in 2–3 layers ending in flat leaf pads + a top crown, sky between layers), classic American-elm vases `environment_elm2/5_v<N>` (20–27 m tall, 20–27 m crowns, 1.5–2.0 m trunks with lobed flare, 6–8 leaders from a low split; sizes from published elm data) plus `environment_elm6_v<N>`, a central-trunk tree from Mark's concept art (trunk runs up through the crown, spiral side limbs steepening toward the top, a cloud cluster on each limb end); 5–8 short surface roots per tree (mixed 1–2.6 m lengths, uneven spacing, weaving in and out of the ground, tips buried) fused into the one wood mesh; crowns built as ~10–14 overlapping floret clusters and seed-picked against outline measurements of reference photos (`scripts/measure_crown_silhouette.py`) — every regeneration bumps `VERSION` in the script, renaming all models + materials and deleting the older version's files: one continuous voxel-fused wood mesh (trunk forks into leaders, then space-colonised limbs that fill the whole crown) + folded leaf cards in clumps on the limb ends, 256×256 American-elm bark (grey-brown, low contrast: a connected net of cracks — two families of full-height lines leaning ±8° that cross into stretched diamonds, plus crack-to-crack ties, every crack broken by gaps and sideways jogs so none runs the tile unbroken — with flat plate bodies between, lit left edge / shaded right edge, 3–4 tones; Mark's sketch + photo, 2026-10-02; UVs are per vertex with the angle frame blended along each limb, so every limb opens on one lengthwise seam and nothing is sliced across; faces the wrap would skew more than 1.8× get a flat projection, and the decimated LOD woods are unwrapped again rather than carrying smeared UVs; collision is the LOD0 wood itself, 4k tris, so the collider sits on the bark)) and 256×256 leaf card of true-scale (12–18 cm) elm leaves in a dark back pass and a light front pass, deep elm green — indiscernible foliage mass with depth (Mark's pick of ten samples, 2026-10-02), no baked lighting, written at true size for pixel_lit's point sampling `elm_bark_v<N>` / `elm_leaves_v<N>` textures (+ `elm_leaves_v<N>_mask` cutout for complex.shader's TextureTranslucency); writes fbx + vmdl (import_scale 0.4 = 40 u/m) + vmat + textures together into `Assets/models/environment/tests/` (the generated-model demo folder). `-- render` also writes previews to `blenderprojects/elm_preview/` |

## Build kit

`scripts/create_build_kit.py` is the one generator for the whole wood kit. It builds 22 objects
named exactly after their piece ids, sorted into a collection per family, and reports each
piece's bounds against the size table in `Code/Building/BuildModuleDimensions.cs` so a size
drift shows up as a `MISMATCH` line instead of a bad export.

`blenderprojects/buildKit.blend` is that script already run and saved. Delete the objects you
do not want and save it under whatever name you need.

Sizes, orientation and origin are documented at the top of the script. Two things worth
knowing before exporting:

- Pieces are spread out on a grid using the **object** location, never baked into the mesh.
  Clear it (Alt+G) before exporting by hand, or set `LAYOUT_SPACING = 0`.
- **Nothing relies on a runtime rotation.** Every piece is modelled in the orientation it
  should appear in at yaw 0 — a roof is already pitched, a 45° brace already leans. The two
  families the prefab would otherwise turn carry that rotation in the mesh instead, so the
  prefab has to cancel what it would apply on top:

| Family | Prefab rotation | `ModelRenderer` child `LocalRotation` |
|--------|-----------------|--------------------------------------|
| Roofs | −45° about X | +45° X — `0.3826834,0,0,0.9238795` |
| 45° beams | −45° about Y | +45° Y — `0,0.3826834,0,0.9238795` |

  Net effect is identical to an unrotated mesh on the root, so placement, snapping and
  collision are untouched. It only changes what the asset looks like on its own.

- The direction is **not** a free choice: the mesh has to lie in the volume the collider
  already occupies, which is the declared size box turned by that same prefab rotation. −45°
  about X sends local +Y down, so the roof slab descends towards +Y — the panel drops 2 m over
  a 2 m run towards +Y, the hip is high at (−X, −Y), the valley is low at (+X, +Y). −45° about
  Y sends local +X up, so a brace rises as it runs towards +X.
- Both families therefore fall outside the size check: a pitched roof measures about
  2 × 2.04 × 2.04 instead of 2 × 2.82 × 0.06, and a leaning 2 m brace 1.56 × 0.2 × 1.56
  instead of 2 × 0.2 × 0.2.

### Stairs

All three stair pieces climb **1 m** across a **2 × 2 m** footprint, so two of them make one
2 m storey and a flight can turn part way up. They are built from separate tread and riser
plates (`STAIR_PLATE`, 0.06 m) rather than one solid wedge, so the underside is **open**.

| Piece | Shape | Enter | Leave |
|-------|-------|-------|-------|
| `build_wood_stairs` | 8 straight steps, 0.125 m rise / 0.25 m going | −X face | +X face, 1 m up |
| `build_wood_stairsSpiralLeft` | quarter-turn winder, treads fanning from the (−X, +Y) corner | −X face | +Y face, 1 m up |
| `build_wood_stairsSpiralRight` | mirror of the left one, pivot at (−X, −Y) | −X face | −Y face, 1 m up |

Both winders keep the straight run's entry face, so a straight piece feeds either hand with no
extra rotation. Step 1 stands on the piece floor and the last tread is flush with the ceiling,
which is the floor of the level above.

`SPIRAL_TURN_DEGREES` is the sweep of a winder and `STAIR_RISE` the climb of every stair piece;
change `STAIR_RISE` and `BuildModuleDimensions.SizesMeters` together or the mesh leaves its
collider.

### Folded roof corners

Hip (`build_wood_45roofOutsideCorner`) and valley (`build_wood_45roofInsideCorner`) pieces fill
a **2 × 2 × 2 m** module cube — not the flat 2 × 2.828 × 0.06 plate box. The mesh is already
folded, so the prefab does **not** apply `RoofPrefabLocalRotation` on top.

Each corner exposes **six snap points** (`Fold0`–`Fold5` in code), matching the six structural
vertices: four fold corners plus the midpoint of each 2.828 m slope edge where a straight panel
butts in. Edge seams are the west/south/east/north lines connecting those corners; vertices 4 and
5 are point snaps on the slope mids.

### One file per piece

`scripts/split_build_kit.py` rebuilds each piece on its own, in an otherwise empty scene at the
world origin, and writes it twice:

| Output | Path |
|--------|------|
| Single-object `.blend` | `blenderprojects/buildKit/<pieceId>.blend` |
| Mesh for a `.vmdl` | `../Assets/models/building/<pieceId>.fbx` |

It rebuilds rather than copying objects out of `buildKit.blend` on purpose: the combined file
spreads the kit on a grid using object locations, and FBX bakes the object transform, so
anything lifted straight out of it would export off-centre. Set `ONLY_PIECES` to redo a single
piece, or `WRITE_FBX` / `WRITE_BLENDS` to skip half the work.
