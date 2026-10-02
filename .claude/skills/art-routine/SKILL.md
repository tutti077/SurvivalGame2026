---
name: art-routine
description: The ART ROUTINE — redesign a named model, texture or material so it accomplishes stated visual objectives, working from real-life source material. Triggers on "art routine", "do our art routine on <asset>", "redesign <model> to …". Blender + numpy + sbox editor verification; 256×256 textures; low-poly art direction.
---

# Art routine

Read `.claude/ROUTINES.md` first — the shared contract applies.

## Goal

Take one asset Mark names and change its shape, texture or material until it accomplishes the
objectives he lists, looking like the real-world thing it represents filtered through this game's
low-poly pixel style. The output is a drop-in replacement for the existing asset, verified in the
engine, with before/after renders.

## Brief format

> "art routine on **<asset>** to **<objective 1>** and **<objective 2>** [constraints]"

| Field | Required | Default when absent |
|-------|----------|---------------------|
| asset | yes | — (stop: the work is useless without it) |
| objectives | yes | — (stop) |
| real-world subject | no | infer from the asset name and its JSON / prefab usage (e.g. `axe_stone` → a stone-headed hand axe); state the inference in the restatement |
| texture size | no | **256×256** always, unless Mark says otherwise in this brief |
| keep silhouette / keep UVs / keep chop pieces | no | keep whatever is not named in the objectives |
| iteration budget | no | 3 self-reviewed iterations, then report |

## In scope

- `Assets/models/**` (fbx, vmdl, blend sources beside them), `Assets/materials/**` (png, vmat),
  `Assets/shaders/pixel_lit.shader` usage (not edits to the shader unless the objective is a shader look).
- `Blender/scripts/` — generator / split / render scripts (`create_*.py`, `split_*.py`, `render_*.py`).
- The prefab or JSON line that points at the model (`heldModel` in `equipment_profiles.json`, a
  prefab's `Visual` child, `build_pieces.json` model path) — only to swap the reference.
- `Assets/scenes/lightTestScene.scene` as the viewing stage.

## Out of scope

- Gameplay code. A model swap must not require code changes; if it does, stop and report why.
- Other assets in the same family unless the brief says "all <x>" — one asset per brief.
- Collision sizes that gameplay depends on (`BuildModuleDimensions.SizesMeters`, weapon `length=`)
  unless an objective is explicitly about size.

## Technologies

- **Blender headless** (`blender -b … --python Blender/scripts/<script>.py -- args`). Prefer a
  parametric generator script over hand-modelling so the result is reproducible; follow the
  `create_*` / `split_*` naming and keep a `VERSION` constant that bumps each iteration.
- **Texture authoring**: write the PNG with numpy at its real size (256×256). Never block-upscale.
  2–3 flat tones in broad shapes, low contrast, no baked lighting on foliage, bark furrows never
  near-black. Materials use `shaders/pixel_lit.shader` (point sampler) — see
  `Blender/scripts/import_artist_pack.py` for the vmat it writes.
- **Artist packs** (Rumple's Tripo `.blend`): go through `import_artist_pack.py`; do not re-bake.
- **Engine verification** through the sbox MCP: `switch_scene` / `open_scene` lightTestScene,
  `spawn_model`, `set_editor_camera`, `editor_camera_screenshot`. Screenshots in one `call_tools`
  batch share a frame — take them in a later call. A scene edited on disk needs `close_scene`
  then `open_scene`.
- **Reference lookup**: `WebSearch` / `WebFetch` and the built-in browser for images.

## Source material (mandatory)

Before modelling, gather **2–3 real-life references of exactly the thing being built** — the
species, the tool, the material — not stylised game art. For each, record in the report:

- the URL,
- what you took from it (proportions, the two or three dominant colours, the one feature that
  makes it recognisable),
- any published measurement used (trunk diameter at breast height, head length, etc.).

Sizes come from published data, not guesses. Pixel-style references (Valheim) are allowed as a
**second** layer — "how does this style render that feature" — never as a substitute for the
real-world reference. If Mark is unreachable and the subject is ambiguous, pick the most common
real-world variant, name it, and continue.

## Procedure

1. **Restate** the brief (asset, subject, objectives, what is kept). Locate the asset: model,
   materials, vmdl, blend source, every prefab / JSON that references it (`grep` the path).
2. **Capture "before"**: render the current asset in lightTestScene from two angles (front ¾ and
   top for trees / rocks, side profile for weapons) at player eye height.
3. **References**: gather and note them as above.
4. **Plan** one paragraph per objective: what geometry / texture change achieves it and which
   checklist items it risks (silhouette, texel density, density of foliage …).
5. **Build** iteration N in the generator script. Render preview PNGs from Blender to the
   scratchpad. Self-review against the **Quality bar** below; write the pass/fail per item.
6. **Export**: FBX + authored `.vmdl` beside it (the engine does not create one), `.png` + `.vmat`
   on `pixel_lit.shader`. Same import scale as the category (0.4 weapons / environment, 0.5 building).
7. **Verify in engine**: spawn in lightTestScene, screenshot the same two angles as step 2. If the
   asset has chop / smash pieces, spawn those too and check they assemble at one origin.
8. **Loop** steps 5–7 until every objective passes and no checklist item regresses, or the
   iteration budget is spent. Keep each iteration's renders (`<asset>_v<N>_<angle>.png`).
9. **Swap the reference** (prefab / JSON) only when the final version passes. Delete superseded
   files from the old version in the same change (deprecate cleanly); blend sources stay beside
   the models.
10. **Report** and send before/after renders with `SendUserFile`.

## Quality bar

Every item is pass/fail in the report.

- **Objectives**: each one visibly achieved in the engine screenshot, not only in Blender.
- **Squint test**: the silhouette does not read as a cube / pill / sphere; asymmetry and lean present.
- **Face variety**: no geodesic uniformity; big flat facets mixed with small ones.
- **Texel density**: ~5 cm per texel in the world, fixed density across the model, visible texels
  at player distance (256×256 real file, point sampled).
- **Tones**: 2–3 flat tones, low contrast, no per-texel noise, no baked lighting on foliage.
- **Foliage** (when present): layered alpha leaf cards hanging off real limbs, some sky visible
  through the crown; never solid geometry, never free-floating cards.
- **Wood / rock**: one continuous surface, no intersecting separate tubes; roots are part of the loft.
- **Chop / smash pieces** (when present): stump + trunk + log conventions kept; smashable rocks
  stay pre-fractured chunks sharing the origin.
- **Scale**: matches the published real-world size after the category import scale.
- **No regressions**: everything the brief said to keep still matches the "before" render.

## Report

- Restated brief and the subject inferred.
- Reference list with what each contributed.
- Per-iteration one-liner: what changed, which checklist items flipped.
- Final checklist with pass/fail.
- Files changed / added / deleted.
- Before/after renders sent as files.
- **Out of scope, noticed**.
- **VERIFY rows added** (`VERIFY.md` IDs for everything only Mark can judge at the PC).
- Standard footer: build label (patch bumped) + **What's needed from you** (prefab edits to review,
  an in-game look at the asset, decisions on anything that was inferred).
