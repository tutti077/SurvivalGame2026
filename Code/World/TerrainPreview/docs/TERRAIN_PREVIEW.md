# Terrain preview — architecture

Source of truth for procedural terrain preview. Read before adding knobs or pipeline stages.

## Mental model

```
Lake mask noise (TerrainPreviewLakeMap)
        ↓
Land disk @ preview÷2
  1. Land biomes + mountains on full land circle (independent of lakes)
  2. Lake mask — quantile threshold + one speck pass (no threshold iteration)
  3. Biome patch merge — single scan on dry land; tiny patches → neighbor biome
        ↓
Land height = base noise → biome sculpt → peaks → rim coast
  • dry land clamped ≥ SeaLevel + margin
        ↓
Open water = exactly SeaLevelMeters (default 0)
```

**Rules**

- Inland water = speck-filtered mask only (not height valleys).
- All water surfaces are flat at `SeaLevelMeters`.
- Base height noise feeds biome sculpting only.
- Lake straits/islands emerge from noise overlap + shore detail — no artificial breaker pass.

## Lakes tab (designer knobs)

| Knob | Effect |
|------|--------|
| Target Lake Coverage | Fraction of **land circle** that is open lake water — matches **Water on land** in Generate Stats |
| Macro Frequency | Higher = smaller lakes. **1.0 ≈ 2.2 km basins** on a 20 km world (old default). |
| Medium Frequency | **1.0 ≈ 650 m** shore/depth detail on a 20 km world. |
| Macro Octaves | 1–5; lower = rounder blobs |
| Shore Detail | Ridged coast wiggle; tiny islands flooded if < Min Speck |
| Mask Offset X/Y | Slide lake field; auto-solved on generate |
| Max Auto Offset | Spawn solve search radius (default 1500 m) |
| Showcase Water Radius (m) | Spawn solve prefers open lake within this distance of spawn (spawn stays dry) |

## Spawn solve (replaces Valley Auto)

On **Generate** when **World - Solve Spawn On Generate** is on:

1. Build land biomes once per seed.
2. Sample lake mask once; quantile threshold; compute **Lakes - Mask Offset X/Y** from wet centroid + dry-spawn nudge (no spiral search).
3. Apply water mask once; validate coverage, dry spawn, **Lakes - Showcase Water Radius (m)**.
4. Reject seed only if dry spawn or coverage constraints fail after that single offset (+ small post-build nudge if needed).

**Showcase water** (informational): nearest **open lake** shoreline within **Lakes - Showcase Water Radius (m)** of spawn. Does not fail the solve — status line reports distance.

## Core files

| File | Role |
|------|------|
| `TerrainPreviewLakeMap.cs` | Lake noise (macro FBM + domain warp + ridged shore) |
| `TerrainPreviewLandDiskFields.cs` | Threshold, speck, island fill, biome cache |
| `TerrainPreviewLakeCombine.cs` | Flat water + dry-land clamp |
| `TerrainPreviewLakeSpawnSolver.cs` | Mask offset + seed retry |
| `TerrainPreviewPatchFilter.cs` | Min-diameter morphology |

## Commandment #3

When replacing behavior, delete old types, toggles, and post-process hooks in the **same change**. See `AGENTS.md` and `.cursor/rules/deprecate-cleanly.mdc`.

## Runtime world generation (`terrainTest.scene`)

Editor **Generate** and runtime meshes share **`TerrainPreviewPipeline.Sample()`** — no PNG is read back for height.

| Step | What happens |
|------|----------------|
| 1. Tune | Terrain Preview Tool → **Generate** → bundle under `Assets/terrain/preview/` + `.latest_preview.json` |
| 2. Play | `TerrainWorldManager` with **World - Settings Source** = **Tuned Preview First** loads `preview_settings.json` (full `generation` block) |
| 3. Stream | Existing forward-cone + side-square chunk streaming; each vertex calls the backend sampler |
| 4. Persist | First play writes `WorldSaves/<WorldName>/world.json` with full `PreviewSettings` |

**World - Override World Scalars From Component** — off (default): seed, diameter, height, ocean ring, and lake offsets match the tuned bundle (same as PNG). Turn on only to force component inspector values.

**World - Run Lake Spawn Solve On Load** — only for **Component Defaults Only** source. Tuned bundles already include solved **Lakes - Mask Offset X/Y**.

Biome-specific sculpting (`TerrainPreviewBiomeTerrainShaper`, etc.) runs in the shared pipeline — tune under **Biome Terrain** tab; re-Generate, then play.

**Height (runtime + preview):** base noise uses **world-meter wavelengths** (**World - Hill Wavelength**, default 400 m) so 64 m chunks show rolling relief. Continental macro stays broad; biome sculpt adds on top. **Biome Terrain - Slope Smoothing** is off by default (it was flattening chunks). Shores ease toward sea on rim + lakes. Re-Generate after tuning, then play.

**Playtest (`terrainTest.scene`):** fly camera **Follow terrain height** optional. Press **J** to spawn a scale-reference player at the camera. Chunk meshes run **border-aware land speck** (interior dry islands only — mainland touching chunk edges is kept).

**Biome edges:** **Biomes - Continuous Placement At Sample** makes blobby patches. **Biomes - Edge Color Blend (0–1)** (~0.35) softens borders only — interiors stay dominant biome color. Shader splats can replace this later.

**Azure Coast:** **Biomes - Azure Coast** paints teal **Biomes - Azure Coast Width (m)** strips on inland lake shores, **Biomes - Azure Coast Min Distance From Spawn (m)**+ only, ~**Biomes - Azure Coast Target Region Count** sparse regions with **Biomes - Azure Coast Along-Shore Run (m)** strips.

## Mountain mask tab

Ridged-noise ranges inside the inner/outer spawn band (`TerrainPreviewMountainSpawnMask.cs`). Every knob in the tab affects `Sample()` output.

| Knob | Effect |
|------|--------|
| Macro / Medium Wavelength (m) | Range patch size and branch detail (meters on world diameter) |
| Ridge Sharpness | Thinner bright crests on Mountain Field |
| Field Floor | Cuts weak field values before **Min Mountain Mask** threshold |
| Breaker * | Ridged gaps that split large masses into separate chains |
| Min Patch Support + Grid Steps | Sample-time speck filter (disk vote inside patch diameter) |
| Drop Isolated Specks | Enables sample-time filter; Mountain Mask PNG also runs raster min-diameter pass |

**Min Mountain Mask** lives on the Biomes tab — binary threshold for mountain biome / white mask pixels.

Legacy JSON keys (`MountainSpawnMacroMin01`, spawn-solve offsets, region gate, etc.) deserialize via `TerrainPreviewSettings.Legacy.cs` but do not change output.

## Generate metrics (tuning loop)

Each PNG export includes `generation_metrics.json` (lake patch count, median diameter, archipelago score, mountain land %). See `.cursor/rules/terrain-preview-tuning.mdc`. Latest bundle: `Assets/terrain/preview/.latest_preview.json`.
