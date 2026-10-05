# VERIFY.md — things Mark asked to check later

A short list that **only Mark adds to**. Claude writes a row here only when Mark says so in chat
("add this to the verify list", "put that on the check list", …). Changes Claude could not test by
eye are mentioned in that reply's **What's needed from you**, not logged here.

- **IDs** are sequential `V-NNN` (restarted 2026-10-05 when the list was cleared).
- **What a row needs:** date, build, area, what to check, how to check it in under a minute, status.
- **Closing:** Mark marks rows `OK` / `BAD <note>` or asks Claude to clear them; Claude never removes
  a row on its own.

## Open

| ID | Date | Build | Area | What to check | How to check | Status |
|----|------|-------|------|---------------|--------------|--------|

## Fable review queue

Work done by Claude **Opus 5.5** that Mark wants re-checked by **Fable 5.1** when credits allow: read
the change, confirm it is correct and fits the project rules, fix or flag anything off. `F-NNN`,
same rules as above (Claude adds, only Mark closes).

| ID | Date | Build | Area | What to review | Files | Status |
|----|------|-------|------|----------------|-------|--------|
| F-001 | 2026-10-04 | 0.8.2742 | Sky dome + presets | New sky pipeline: shader math (gradient, sun band, cloud projection, fake self-shadow), runtime dome mesh + camera follow, preset catalog and keyframe blending, removed `*SkyTint` properties, scene wiring. Also check the preset colours against Valheim references. | `Assets/shaders/environment/sky_dome.shader`, `Code/World/Environment/SkyDome.cs`, `SkyPresetCatalog.cs`, `EnvironmentDayNightCycle.cs`, `Assets/data/sky_presets.json`, `Assets/scenes/environmentTest.scene`, `Blender/scripts/create_sky_clouds.py` | open |
| F-002 | 2026-10-05 | 0.8.2743 | Sky moved to lightingTest | Scene surgery (cloned Moon / disks / cycle with fresh guids and remapped refs, SkyBox2D removed, Z Far raised), new optional `Fog` hook on the cycle, and the fix that stopped the cycle's legacy cleanup from destroying any renderer named "SkyDome". | `Assets/scenes/lightingTest.scene`, `Assets/scenes/environmentTest.scene`, `Code/World/Environment/EnvironmentDayNightCycle.cs` | open |
| F-003 | 2026-10-05 | 0.8.2745 | Cycle stops re-aiming fly cam | Removed the TerrainTestFlyCamera.SetViewLookAt loop from EnvironmentDayNightCycle.OnStart. | `Code/World/Environment/EnvironmentDayNightCycle.cs` | open |
