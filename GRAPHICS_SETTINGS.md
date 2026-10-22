# GRAPHICS_SETTINGS.md — the video / GPU options checklist

The full set of player-facing video settings a modern PC game ships, so the ones we build get
ticked off here and the rest stay on the list (Mark 2026-10-20: "we're designing the game at max
quality at the moment; there'll come a time my PC can't even handle it"). Reference menu: the
Serious Sam-style Graphics / GPU / GPU Memory / Advanced / CPU screens.

Legend: `[x]` = exposed to the player in our settings menu · `[~]` = exists as a designer knob or an
engine setting but not in our menu yet (where it lives is noted) · `[ ]` = nothing yet.
"We design at" = the value the project is tuned and tested at today (RTX 4090, 2560×1288 editor view).

## Display

- [~] Resolution — engine video settings (s&box owns the window).
- [~] Full screen / borderless / windowed — engine video settings.
- [~] V-sync — engine video settings (no console command; the 240 Hz display is the perf cap).
- [~] Frame-rate cap — console `fps_max` / `fps_max_inactive` / `fps_max_menu` (we measure at 0).
- [ ] Refresh rate selection.
- [ ] Field of view — pawn camera FOV is a prefab value (pawn 60 ↔ fly camera 60); needs a slider.
- [ ] Letterboxing / aspect-ratio lock.
- [ ] Brightness / gamma / contrast.
- [ ] HDR output (SDR/HDR toggle, peak nits, paper white).
- [ ] Colour-blind modes.
- [ ] Monitor selection (multi-monitor).

## Resolution scaling and anti-aliasing

- [~] Render scale / max 3D rendering resolution — engine "Upscaling" (reports Off today).
- [ ] Upscaler choice: none / FSR / DLSS / XeSS, with quality / balanced / performance presets.
- [ ] Frame generation (DLSS FG / FSR FG) — if the engine exposes it.
- [~] MSAA — engine video setting; **we design at 8×** (every suite row measured with it; the single
      biggest "free" lever when the GPU is the wall).
- [ ] FXAA / fast-approximate AA.
- [ ] TAA / temporal AA (+ sharpening amount).
- [ ] Super-sampling (SSAA).
- [ ] Anisotropic filtering (2× / 4× / 8× / 16×).
- [ ] Texture filtering (bilinear / trilinear).
- [ ] Sharpening post-filter (CAS).

## Textures and memory

- [ ] Texture quality / resolution (low → ultra; our textures are 256×256 by art direction, so this
      is mostly about the engine's streaming budget).
- [ ] Texture streaming pool size.
- [ ] Compressed colour maps / normal maps / lightmaps quality.
- [ ] Shader quality (cheap vs full shader variants).

## Shadows

- [~] Sun shadow cascades — engine sun light (**we design at 4**; 4 → 2 saved ~1.3 ms CPU Render).
- [~] Shadow distance — `SunShadowDistance` on the scene Sun (multiplier on the cascade reach).
- [ ] Shadow map resolution (512 → 4096).
- [ ] Shadow filtering / softness (hard / PCF / PCSS).
- [ ] Shadow antialiasing / blurring.
- [~] Terrain chunk shadows — `perf_terrain_shadows` dev switch, not a setting yet.
- [~] Grass casts shadows — `CastShadows = false` on the clutter entries (designer-side only).
- [ ] Local light shadows count / quality (torches, lanterns, campfire).
- [ ] Contact shadows / screen-space shadows.

## Lighting and global illumination

- [~] Ambient occlusion (SSAO) on/off + quality — engine post stack on the camera (**we design with it on**).
- [ ] Ambient occlusion precision / radius.
- [ ] Global illumination quality (light probes, indirect bounce).
- [ ] Reflections: screen-space reflections on/off + quality.
- [ ] Mirror / planar reflections (water surface).
- [ ] Refractions.
- [ ] Volumetric lighting / god rays quality.
- [ ] Volumetric fog quality.
- [~] Per-texel lighting look (pixel_lit `PixelTexelLighting`, `PixelLightSteps`) — art direction,
      probably never a player setting; listed so nobody turns it into one by accident.
- [ ] Max dynamic lights / "fast lights".

## Post-processing

- [~] Bloom on/off (+ quality) — engine post stack (**on**).
- [~] Tonemapping / auto-exposure — engine post stack (**on**).
- [ ] Motion blur (camera / per-object) + amount.
- [ ] Depth of field.
- [ ] Chromatic aberration.
- [ ] Film grain.
- [ ] Vignette.
- [ ] Lens flares / light streaks quality.
- [ ] Heat shimmer / distortion effects.
- [ ] Zoom / gaussian blur quality.
- [ ] Color grading / LUT strength.

## World detail (ours — these are the knobs the suite actually bends)

- [~] View distance / draw distance — far ring **Far Trees — Card Range (m)** (**we design at 1200**),
      near chunk radius on `TerrainWorldManager`.
- [~] Tree LOD distances — **Tree LOD 1 / 2 / 3 (m)** = 180 / 270 / 330, plus per-tree jitter.
- [~] Far tree impostors on/off — `perf_impostors` dev switch.
- [~] Grass range — **Grass — Range (m)** (**we design at 150**; the engine always streams 12 clutter
      tiles, so this picks the tile size: 77 / 153 / 307 / 614 m).
- [~] Grass density — **Grass — Clumps Per m²** (**1.5**).
- [~] Grass thinning distances — `ThinFullMeters` / `ThinFarMeters` / `ThinCullMeters` on grass_blade.vmat.
- [~] Small-object (rocks, sticks, saplings) visible distance — **Vegetation — Sapling Visible (m)** (**150**).
- [~] Vegetation / scatter objects on/off — `perf_veg_objects` dev switch.
- [ ] Terrain mesh detail (chunk vertex spacing, currently 2 m).
- [ ] Terrain texture / splat detail.
- [ ] Water quality (reflection, refraction, wave detail).
- [ ] Sky / cloud quality (sky_dome layers, star layer).
- [~] Wind animation on/off — `WindSystem` Wind Enabled / `wind_toggle` (grass + elm sway).
- [ ] Decal quality / dynamic decals on-off.
- [ ] Particle density / quality.
- [ ] Weather effects quality.

## Characters and animation

- [~] Entity animation distance / dormancy — **EntityBrain Sleep Range (m)** (**350**).
- [~] Entity animgraph on/off — `perf_entity_anim` dev switch.
- [ ] Character detail / LOD bias.
- [ ] Ragdoll count / disable ragdoll deaths.
- [ ] Cloth / hair simulation.

## CPU / simulation

- [ ] Max corpses (solo / co-op) and max moving corpses.
- [ ] Max debris and max moving debris.
- [ ] Debris casts shadows.
- [ ] Physics quality / tick rate.
- [ ] Max active sounds.
- [ ] NPC / AI density cap.

## HUD and accessibility (lives with video in most menus)

- [ ] Show HUD / HUD scale.
- [~] Show FPS — console / perf overlay only.
- [ ] Show weapon / first-person arms.
- [ ] Crosshair style and scale.
- [ ] Subtitles, UI scale, text size.
- [ ] Screen shake amount.
- [ ] Camera motion sickness options (head bob, FOV changes).

## Presets

- [ ] Quality presets: Low / Medium / High / Ultra / Custom that set every row above.
- [ ] Auto-detect recommended preset from the GPU.
- [ ] Benchmark button — `perf_suite` already exists as the dev form (six scripted scenarios);
      a player-facing version would run it and recommend a preset.
