# Voxel terrain (2026-10-26 → 2026-10-29)

Mark: "I want this game to be a digging game for sure" — **true voxels**: caves, tunnels, overhangs, digging straight
down (not Valheim-style height deltas). Later: **simulated water** that flows into dug holes. The field, mesher and
brushes started standalone in `Assets/scenes/voxelTest.scene`; since 2026-10-29 the live heightfield world digs too
(**voxel columns**, below): terrainTest swaps each 64 m chunk the player touches for voxel ground.

## What is here

| File | Role |
|------|------|
| `VoxelField.cs` | One dense grid of "solidness" samples (float, metres: > 0 ground, < 0 air, capped ±4 m), any cell count per axis (mesh chunks of 32³, the last one partial). Seeded from a height function; the puck brushes dig / fill, `ApplyPlane` lays a rake plane; reports the dirty chunks. `VoxelRake.Plan` (static) is the rake's planner over any surface function. |
| `VoxelMesher.cs` | Naive **surface nets** per chunk (no marching-cubes tables): one vertex per cell that straddles the surface at the mean of its edge crossings, one quad per crossing grid edge. Normals from the field gradient; splat colour / extras from an optional `VoxelSurfaceStyle`; an optional `Clip` square (see columns). Runs on a worker thread, returns arrays. |
| `VoxelChunkSet.cs` | The chunk objects over one field: a child object per mesh chunk (`ModelRenderer` + static `ModelCollider`, both from one `ModelBuilder` with a collision + trace mesh), a dirty queue, 6 builds in flight, 3 models made per frame. Shared by the prototype and the world's columns; the owner calls `Tick()`. |
| `VoxelEditLog.cs` | `VoxelEdit` (one stroke: centre, radius, mode, and for a rake stroke the plane it laid), the `voxel_edits.json` reader / writer, `IVoxelBrushTarget` (what the digger and the console brush: the prototype or the world) and `VoxelBrushTargets.Find`. |
| `VoxelTerrain.cs` | The standalone prototype component (voxelTest): seeds one square field on a worker thread, owns a `VoxelChunkSet`, host-authoritative brushes (`RequestBrush` → `[Rpc.Host]` check → `[Rpc.Broadcast]` apply), the edit log for World Name. |
| `VoxelCommands.cs` | The `voxel_*` console commands, on whichever target the active scene has. |
| `VoxelDigger.cs` | Fly-camera input: Attack1 digs, Attack2 fills, Reload (R) rakes where the scene camera looks, within Reach; F swaps the rake flat / slope; a ring shows the circle the rake will level. On the VoxelWorld object in voxelTest and the TerrainWorld object in terrainTest. Stands down while a playable pawn is up (`TerrainTestPlayerSpawn.PlayableActive`): then the pawn's tools dig (*Tools*, below). |
| `VoxelBrushRings.cs` | The debug-overlay circles: the rake's ring on the plane it will lay (blue flat / orange slope / red nothing to level) and a disc outline lying on a surface (the pickaxe's chip, the hoe's paint). Digger and tools share it. |
| `../../Player/PlayerTerraform.cs`, `ToolPickaxe.cs`, `ToolHoe.cs`, `../../UI/Build/HoeMenuHud.cs` | The pawn side: tools aim and preview on the client, `PlayerTerraform` carries the stroke to the host (reach + tool check, one durability tick when the ground changed) and into the scene's `IVoxelBrushTarget`. |
| `../Terrain/TerrainWorldManager.Voxel.cs` | The live world's side: voxel columns per touched chunk, the swap, the world-wide rake plan, RPCs, the edit log and restore on chunk load. |

## Tools (2026-11-02)

Mark: "I want the pickaxe tool to work like the tool we used in the voxeltest scene, where it removes just a little bit at a
time in a circle projected onto the surface of where youre looking. not a sphere! Thats how pickaxes work. Hoes should open
up a menu like the build menu and one of them is for creating grass for that biome, bare ground for that biome, tilled
soil, or the grading tool which flattens the surface which we worked on previously."

- **Pickaxe** (`ToolPickaxe`, equipment action `Mine`; `equipment_profiles.json` rows for `pickaxe_scrap / reforged / iron /
  steel / titanium`, all on `prefabs/tools/pickaxe_tool.prefab`). Aim at the ground within **Reach (m)** 4: a yellow ring
  shows the circle the pick will take, drawn ON the surface (flat on a floor, upright on a cliff). Attack1 chips it:
  `VoxelBrushMode.Chip` = the dig puck with its axis along the hit normal (`VoxelField.ApplyChip`: the offset is expressed
  in the frame (tangent, bitangent, normal) and fed to the same `PuckDistance`), **Chip Radius (m)** 0.6 wide and **Chip
  Depth (m)** 0.3 deep under the surface, reaching max( 2 depth, r ) out on the air side so ground rising above the disc
  inside the circle is cut too (no lip - the same lesson as the dig puck). Holding the button swings again every **Swing
  Interval (s)** 0.5. Only the world's ground takes a chip (`ToolPickaxe.IsGround`: tags `terrain` / `voxel`), never a
  build piece or a rock prop. The edit row carries the normal (`Nx Ny Nz`) and depth (`D`) so the replay and every peer cut
  the same disc; the host clamps the radius to Max Brush Radius and the depth to (0.05 m .. r).
- **Hoe menu** (`HoeMenuHud`, Attack2, the build menu's overlay and tile styling; `hoe_menu` / `hoe_mode <till|grass|bare|grade>`
  from the console): four `HoeMode`s, click to pick and close, right-click / Escape / a click beside the panel closes.
  - **Grass** / **Bare ground**: a green / brown ring of **Paint Radius (m)** 1.5 on the ground; Attack1 (hold to keep
    painting, every **Paint Interval (s)** 0.25) paints the circle. **F** swaps the patch for an axis-aligned **square** of
    side 2 r whose centre snaps to the 0.5 m voxel grid, so its edges land exactly on sample columns (Mark 2026-11-02:
    "should be like a circle for the most part but i want players to be able to make a perfect square patch"); the menu has
    the same toggle as a checkbox, and the edit row carries it (`Sq`). `VoxelField.ApplyPaint` keeps two **coverage** floats
    per xy column (`_bare`, `_grass`, beside `_groundZ`): a circle's rim gets fractional coverage over one cell (an
    anti-aliased edge), a square's edge is hard; bare raises bare and lowers grass, grass the opposite, so the strokes undo
    each other. The mesher samples both bilinearly at the vertex (`PaintAt`) and blends over the seed style and the depth
    rule, so the outline reads round instead of the 0.5 m stair-steps the first per-column byte gave (Mark: "adds dirt to
    the ground in squares"): **Grass** fades sand and rock out (a pit floor can be grassed); **Bare** fades sand in (the
    soil tile dug walls already show). "For that biome" is the seeded biome mix at that spot; there is one soil tile for
    every biome today (an art decision for later).
    Grass clutter: `SampleGrassGround` now stands clumps on a swapped column's voxel surface (`VoxelField.TrySurface`, the
    field gradient for the normal) and returns none where the paint is Bare; the biome mask (Clover only) still decides
    where clutter grows at all, so Grass in a redwood chunk is texture only. A paint stroke makes a column like a dig does
    (the seed is the chunk's own mesh, so the swap is invisible) and invalidates only the grass tiles under the stroke
    (`NotifyGrassAreaChanged`): an invalidated clutter tile drops its clumps at once and the engine refills 8 tiles a
    frame, so the old whole-chunk invalidate (~36 tiles) blinked the chunk's grass for several frames (Mark 2026-11-03);
    a stroke's 1–4 tiles refill in the frame after, before the batches rebuild.
  - **Tilled soil** (Mark 2026-11-02: "Tilled ground should work the same as the bare ground just be dark brown, also
    have no grass there"): on the world's voxel ground a third paint, `PaintTilled` / `VoxelPaint.Tilled` (`_tilled`
    coverage), the same circle / square stroke as bare; the mesher writes it to TexCoord1.x (the splat's darkening channel,
    which `pixel_lit` now lerps toward **SplatTilledColor** earth brown where the ground is sand under it, and toward the
    far canopy colour elsewhere as before), no grass clutter on it, and seeds sow
    on it: `FarmingRules.IsTilledGroundAt` asks `IVoxelBrushTarget.TilledAt` (0.5 coverage or more), `CanSowAt` accepts it
    with no `TilledSoil` tile, the plant spawns at the aim point. On a `dirt`-tagged plane (testscene1) the hoe still lays
    the old 1 m `TilledSoil` tile. Uproot on plants is unchanged.
  - **Grade**: the rake from the digger, on the pawn: **Grade Radius (m)** 2.5, the ring on the planned plane, Attack1 lays
    it (`Flatten` / `Slope`), **F** (`Flashlight`) swaps flat / slope.
  - **Raise ground** (Mark 2026-11-02: "a way to build upward a little bit ... make it cost some dirt ... only like a thin
    layer of it gets increased per action"): `VoxelBrushMode.Raise`, `VoxelField.ApplyRaise`: every column inside the paint
    circle / square comes up by **Raise Layer (m)** 0.25 (the edit row's `D`), following the ground it sits on; a circle's
    rim fades the layer out over half a metre, a square is a hard step. Fill only (never cuts), through the same
    `LayColumn` the rake uses (the two samples bracketing the new surface take its exact signed height, air below fills
    down to existing ground). One layer per press. Costs **Raise Dirt Cost** 1 `resource_dirt` (a new resource) and the
    pickaxe's chip hands back what it hit, per stroke that changed the ground: **Chip Stone Yield** 1 `resource_stone` where the
    surface is rock (`IVoxelBrushTarget.RockAt` >= 0.5, read just under the hit before the chip - the same `VoxelMesher.RockShare`
    rule that paints the rock tile: mountain weight, 4-8 m under the original surface, cobblestone paint; Mark 2026-11-02: "when i
    destroy rock i should get stone"), else **Chip Dirt Yield** 1 dirt - both on `PlayerTerraform`.
    **Free while everything else is** (Mark 2026-11-02): `PlayerTerraform.MaterialsAreFree` = the prefab flag **Free
    Materials** (on, like the hammer's Free Build Enabled) OR the owner's `allCrafting` hack (the console command that
    already makes crafting and augments free, read through `PlayerCrafting.AllCraftingHack` so the host honours it).
  - **Cobblestone** (Mark 2026-11-02: "add a cobblestone setting in the hoe? should require 1 rock per action"):
    `PaintCobble` / `VoxelPaint.Cobble` (`_cobble` coverage), the same circle / square footprint, one press per patch; the
    mesher drives the mountain weight to 1 (the rock tile) and sand to 0 under it, no grass clutter. Costs **Cobble Stone
    Cost** 1 `resource_stone`, waived while materials are free.
  - **Stamina** (Mark 2026-11-02: "the hoe and the pickaxe take stamina when used"): tuning on `PlayerVitals` (the vitals
    owner) - **Pickaxe Swing Stamina Cost** 4, **Hoe Paint Stamina Cost** 1 (per grass / bare / tilled stroke; a held
    paint repeats 4 a second), **Hoe Grade Stamina Cost** 3, **Hoe Raise Stamina Cost** 3; the old tile till and the uproot
    take the paint cost. The tool checks `PlayerTerraform.CanAffordStroke` for the preview (the ring turns red when too
    tired or short of dirt) and the host spends only when the ground actually changed.
  - **F to punch**: F always throws the punch (at thin air too since 2026-11-05) - never while the hoe is out
    (its F is the mode swap); with a shield out it is a shield bash.
- **Host path**: `PlayerTerraform` on `basicplayer.prefab` (the farming pattern: owner intent -> `[Rpc.Host]` -> caller is
  the owner -> target ready, tool not broken, point within reach + 1.5 m slack -> `IVoxelBrushTarget.RequestChip` /
  `RequestBrush`, which now return whether the stroke landed -> `ToolDurability.HostAddWearToActiveTool` only then).
  Both targets build the stroke as a `VoxelEdit` (`VoxelEdit.FromWire`), validate once in `HostBrush` / `HostVoxelBrush`,
  broadcast the row's fields (`mode, plane, normal, depth`) and apply through the one `VoxelReplay.Apply`.

Units: the field is in **metres** (the design language); positions become engine units (× 40) only when the mesh
and the chunk objects are made — one conversion, at the read site.

## Decisions

- **Cell 0.5 m, chunk 32³ cells = 16 m.** 0.5 m keeps a 1.5 m brush round and a 1-wide tunnel walkable; 1 m cells read
  as lumpy for hand digging. A 128 m × 128 m × 64 m prototype field is 257 × 257 × 129 samples = 8.5 M floats (34 MB),
  65 chunks per layer — fine for a test; the real world will stream chunks and keep only edited ones in memory.
- **One global array, not per-chunk arrays.** A brush writes each sample once; every chunk reads its borders from the
  same array, so chunk borders can never disagree (no cracks). Each chunk also meshes the ring of cells one past its
  own on the low sides, so border quads find their four vertices locally (border vertices are duplicated, never shared).
- **Surface nets over marching cubes**: no 256-entry tables to get wrong, smooth results, watertight, and the vertex
  placement handles sharp dig edges well enough. Dual contouring (sharper features) can replace it later behind the
  same `MeshData` output.
- **Edit log over chunk snapshots** for persistence: exact, tiny, order-preserving. A rake row carries the plane it
  laid (`HasPlane`, `PlaneZ`, `PlaneGx/Gy`), so a replay lands the same ground whatever the surroundings look like by
  then (planning again on load would read neighbours that are not voxel yet, or were edited later). When edits get
  numerous (thousands), switch to saving the samples of edited chunks (sparse: chunk coord → float[33³], compressed) and
  keep the log only as the network replay for late joiners.
- **Puck brush, a true signed distance** (Mark 2026-10-29: "there's still tears in our terrain. When we remove or add it
  should do it in a circle but not a perfect sphere"): the dig / fill shape is a rounded puck — a circle of the brush
  radius on the ground with a flat floor h = radius × **Brush Flatness** (0.55) under the centre, edges rounded by a
  cell — and the brush writes its own signed distance (`VoxelField.PuckDistance`, metres): dig = `min( f, d )`, fill =
  `max( f, −d )`, so the field stays distance-like on both sides of the cut and the surface nets find every crossing on
  a clean curve. The puck is lopsided on purpose: a dig's top reaches 2 h up, so where the ground surface rises above
  the brush centre the well still cuts straight up through it. **That lip was the tearing**: every earlier shape
  (sphere, ellipsoid, block style) stopped under a higher surface and left a thin overhanging skin round the rim that
  read as black cracks from above, and block style's hard jump beside continuous ground added paper-thin shards on top.
  A fill is the mirror (flat top at h, reaching 2 h down) so a mound sits into the ground instead of hanging its edge
  over it. Block style, and the sphere before it (Mark 2026-10-27: "too spherical"; "a flattened sphere, not a perfect
  sphere"), are gone. After **every** stroke (dig, fill, rake)
  `VoxelField.RemoveFloating` flood-fills the ground in the brush box grown by 16 cells (8 m — at 8 cells a ledge cut
  loose by a second stroke beside the first still reached the box and survived, Mark 2026-10-28) and turns any piece
  that never reaches the box boundary into air, six-connected so corner-touching ground counts as hanging — a pure
  function of the field, so the replayed edit log and every peer agree.
- **The rake: flatten and slope** (Mark 2026-10-29: "it should work EXACTLY like enshrouded … either smooth it out
  horizontally or swap modes and smooth it slanted like, at a max of the max walkable slope, so I think that's 45
  degrees … displays a circle on the terrain where it will be adjusted and then when you click it just fills the hole
  into the same height as the surrounding terrain or flattens out a lump to the surrounding terrain"). Reload / R
  (`VoxelBrushMode.Flatten` / `Slope`, `voxel_flatten` / `voxel_slope`), F swaps the mode. `VoxelField.TryPlanFlatten`
  reads the surface height on a ring just outside the brush (two radii × 24 directions, `SurfaceZ` = the highest
  air-over-ground crossing of the column within 2 r + 2 m of the aim): **flat** lays a horizontal plane at their
  median (a hole or lump inside the circle never pulls it); **slope** fits a least-squares plane through them with the
  grade capped at `MaxSlopeGrade` (1 = 45°), so a hole on a hillside fills to the hillside. Fewer than six ring
  surfaces (hanging over a cliff) = no stroke, red ring. `VoxelField.Flatten` then writes the plane's signed height
  inside the circle: above it ground is cut (up to 2 r + 1 m — a taller lump keeps its top), below it air is filled
  **down to the first sample that was already ground**, so a hole fills to its own floor and a cave under the plane
  keeps its roof instead of getting a slab bridged over it; full inside the radius, fading over 0.5 m. The digger draws
  the ring on the planned plane every frame (blue flat, orange slope) so it shows exactly what the click does. The
  previous R brushes — a blur, Valheim-style levelling to the aimed height ("just seems to smooth it horizontally"), and
  a grid tighten — are gone. The edit log carries the mode (`Mode`; older rows fall back to `Dig`; old `2` rows replay as
  Flatten). The two samples bracketing the plane take its exact signed height (min / max alone against the old values
  left the floor a shallow dish, 5 cm high at 1 m from the centre — probed in terrainTest 2026-10-29).
- **Material**: `materials/terrain/terrain_ground.vmat` (pixel_lit `F_TERRAIN_SPLAT`). The mesher fills the splat inputs
  by depth under the ORIGINAL surface: sand (TexCoord0.x) for the first 1.5 m of a dug wall, rock (Color.a) past ~4–8 m,
  Clover grass on top — so a pit shows soil on its sides and rock at the bottom without any new material.
  Steep dug walls get the tiles face-on and their shadows read at the right spot since the splat went triplanar
  (pixel_lit, 2026-10-29 — the picket-fence stripes on pit walls were the top-projected texel walk of the per-texel
  lighting flying along the wall, one stripe per texel column).
- **Seed**: `ProceduralHills` (a fixed formula) by default; `WorldRecipe` samples the real world's pipeline
  (`TerrainHeightQuery`) for World Name, so the dug ground matches terrainTest at the same x,y. The recipe path resolves
  the full settings (lake fields etc.) the first time, which can take seconds.

## Console

`voxel_dig [r] [x y z]`, `voxel_fill [r] [x y z]`, `voxel_flatten [r] [x y z]`, `voxel_slope [r] [x y z]` (the rake; metres; without x y z: where the camera looks), `voxel_chip [r] [depth]` (the pickaxe's disc where the camera looks), `voxel_paint <grass|bare> [r] [x y z]` (the hoe's paint), `voxel_brush <r>`,
`voxel_info` (the prototype's field, or the world's columns: chunks, vertices, queue, last worker build ms), `voxel_reset`
(the prototype reseeds; the world drops every column and puts the heightfield chunks back), `voxel_probe <x> <y> [axis]`
(trace straight down either side of a line and log the hit object, z and normal — for checking a seam). They work in
voxelTest and terrainTest alike (`VoxelBrushTargets.Find`). In both scenes **L** spawns the playable pawn
(TerrainTestPlayerSpawn); with a pawn up the digger stands down and the pawn's pickaxe / hoe do the digging (*Tools*).

## In the live world: voxel columns (2026-10-29, Mark: "implement the voxel terrain in our terrain test")

`TerrainWorldManager.Voxel.cs`. The heightfield sampler stays the authority for undug ground; a 64 m chunk the player
digs, fills or rakes gets a **voxel column**:

- **Seed = the chunk's own mesh.** The field (0.5 m cells, 128 + 2 × 2 margin cells a side, from Depth Below Ground
  (24 m) under the chunk's lowest meshed point to Head Room (8 m) above its highest) is seeded by bilinear sampling of
  the loaded chunk's `HeightsZ` grid — the mesh the player sees, smoothing and LOD included — and the margin from the
  neighbours' grids (the sampler where none is loaded yet). The splat weights / extras come from the same vertices
  (`BuildResult.Colors` / `Splat`, kept on `LoadedChunk`), baked per sample column on the main thread so the worker-side
  mesher reads only those arrays (~6 ms). The field itself — seed, wild-wheat tilled beds, every logged stroke replayed —
  is built off the main thread when a chunk with strokes loads (`TryRestoreVoxelColumn` → `BuildVoxelField` on a worker,
  landed by `LandPendingVoxelColumns`): a column with 222 strokes took 2.1 s on the main thread at load (2026-11-03). A
  stroke on a chunk with no column builds it inline (one stroke: ~60 ms) and drops a restore still on its worker.
- **The swap.** The column's chunks are a `VoxelChunkSet` under a `VoxelColumn (x,y)` root (tags `voxel` + `terrain`).
  Once its first meshes have all landed (`Settled`) the heightfield chunk's renderer and collider switch off
  (`LoadedChunk.Voxel.Swapped`; `UpdateChunkColliders` / `ApplyChunkRemesh` keep them off, tree colliders keep following
  Collision Range through `LoadedChunk.VegetationNear`) and the grass tiles under the strokes applied before the swap
  (`VoxelColumn.GrassAreas`) regenerate on the voxel surface (`SampleGrassGround` reads the field once swapped). Trees, props, saplings and scavs stay where they were — a tree
  over a pit floats until scatter reads the voxel surface (step 3 below). Nav is not rebaked for voxel ground yet.
- **Seams.** The field carries a two-cell margin past the square, so a brush at a chunk border writes the same samples
  into both columns, and `VoxelMesher.Clip` moves every vertex of a cell outside the square onto the square's edge —
  along one axis by interpolating toward the inside neighbour, at a corner to the corner itself — so both columns land
  identical edge vertices (probed 2026-10-29: 2 mm across the border, the pit floor flat through it). Against an
  undug neighbour the column's edge sits on the heightfield's border line (bilinear = linear along the edge) and the
  neighbour's 1.5 m skirt hides any hairline.
- **Strokes.** `RequestBrush` (digger, console) → the host clamps the radius (Max Brush Radius 4 m), needs a loaded
  chunk under the centre, and for a rake stroke plans the plane ONCE over the whole world surface
  (`WorldSurfaceZMeters`: a column's `SurfaceZ` where one stands, else the chunk mesh) so a stroke across a border lays
  one plane; then `[Rpc.Broadcast]` → every peer applies the stroke to every loaded chunk its reach (radius + 4 m clamp
  band) overlaps, creating columns as needed. Dig / fill replay the puck per column (same signed distance → same
  samples), rake strokes lay the logged plane.
- **Persistence.** `WorldSaves/<WorldName>/voxel_edits.json` (`VoxelEditLog`). A chunk load (`FinishChunkLoad`) that
  finds logged strokes on it makes its column at once and replays them; a column dies with its chunk (`DestroyChunk`,
  `UnloadAll`). Clients do not read the log — a late joiner only gets strokes made after it joined (same gap as the
  prototype; the host should replay its log to joiners).
- **Shading.** Voxel ground uses the same vertex contract as the heightfield (`Color` = land shares + mountain,
  `TexCoord0` = sand / wet), so it is painted by `terrain_ground.vmat` unchanged; dug walls add sand and rock by depth
  under the seed surface. A faint brightness step along a chunk border where two columns meet is the terrain's own
  crease (the heightfield smooths interiors only, so slopes change at chunk edges; the voxel seed reproduces it and the
  faceted shading shows it) — the probes found no step in the geometry.

Properties on `TerrainWorldManager` (group **Voxel**): Voxel Digging, Depth Below Ground (m), Head Room (m), Max
Brush Radius (m), Brush Flatness, Save Edits, Log Edits. `voxel_info` lists the columns; `voxel_reset` drops them and
clears the log.

## What is left of the plan

The live world is a pure (x, y) sampler (`TerrainPreviewPipeline.Sample`) feeding `TerrainMeshBuilder` and thirteen
other readers; the voxel layer coexists with it as above. Done: streaming columns (1) and collision / traces (2, nav
rebake still missing). Next, in order:

3. **Height readers → 3-D queries**: scatter / vegetation placement, grass clutter, saplings, camps, the minimap
   altimeter, `TerrainHeightQuery`. Each needs "top solid z at x,y" (`VoxelField.SurfaceZ`) or "is this point solid"
   (`VoxelField.IsSolidAt`) where a column stands. Rivers and the sea keep their carve in the sampler; digging below sea
   level near a coast should *flood* — the water system's job (next item).
4. **Water that flows into holes** (Mark's choice): a cell automaton on the same grid (water volume per air cell,
   pressure / flow to lower neighbours, source cells at sea level and river beds, evaporation off). The field's API
   already answers air / solid per cell; water becomes a second sparse grid over the same chunks, rendered as a
   separate surface mesh per chunk.
5. **Caves authored by the generator**: carve tunnels / caverns into the seed density (`SeedFromHeights` + 3-D noise
   and the CaveLayout tubes from `Code/Dungeon`) so caves exist without digging, and the far tier draws the surface only.
6. **Memory**: a column is 133 × 133 × ~75 samples (5 MB); hundreds of dug chunks want sparse storage (edited 16 m
   chunks only) and the edit log swapped for chunk snapshots.

## Open questions for Mark

- Cell size: 0.5 m (default) or 0.25 m near the player for finer sculpting (4× the memory)?
- Should digging yield resources (dirt / stone items) and should filling cost them? (Inventory hook in `HostBrush`.)
- Max dig depth per world (the field's Depth Below Ground) — bedrock at −24 m under the lowest ground for now.
- Tool: the pickaxe chips (2026-11-02, durability through `PlayerTerraform`). Still open: a shovel (the big dig / fill puck as an item?), and whether tiers change radius / depth / swing rate.
- Bare ground per biome: one soil tile today (the sand channel); does each biome want its own bare tile?
