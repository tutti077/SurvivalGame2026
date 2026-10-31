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
| `VoxelDigger.cs` | Input: Attack1 digs, Attack2 fills, Reload (R) rakes where the scene camera looks (fly camera or pawn), within Reach; F swaps the rake flat / slope; a ring shows the circle the rake will level. On the VoxelWorld object in voxelTest and the TerrainWorld object in terrainTest. |
| `../Terrain/TerrainWorldManager.Voxel.cs` | The live world's side: voxel columns per touched chunk, the swap, the world-wide rake plan, RPCs, the edit log and restore on chunk load. |

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

`voxel_dig [r] [x y z]`, `voxel_fill [r] [x y z]`, `voxel_flatten [r] [x y z]`, `voxel_slope [r] [x y z]` (the rake; metres; without x y z: where the camera looks), `voxel_brush <r>`,
`voxel_info` (the prototype's field, or the world's columns: chunks, vertices, queue, last worker build ms), `voxel_reset`
(the prototype reseeds; the world drops every column and puts the heightfield chunks back), `voxel_probe <x> <y> [axis]`
(trace straight down either side of a line and log the hit object, z and normal — for checking a seam). They work in
voxelTest and terrainTest alike (`VoxelBrushTargets.Find`). In both scenes **L** spawns the playable pawn
(TerrainTestPlayerSpawn) — Attack1 / Attack2 dig and fill from its camera too (in terrainTest that still swings the
pawn's melee attack as well; a shovel item is an open question below).

## In the live world: voxel columns (2026-10-29, Mark: "implement the voxel terrain in our terrain test")

`TerrainWorldManager.Voxel.cs`. The heightfield sampler stays the authority for undug ground; a 64 m chunk the player
digs, fills or rakes gets a **voxel column**:

- **Seed = the chunk's own mesh.** The field (0.5 m cells, 128 + 2 × 2 margin cells a side, from Depth Below Ground
  (24 m) under the chunk's lowest meshed point to Head Room (8 m) above its highest) is seeded by bilinear sampling of
  the loaded chunk's `HeightsZ` grid — the mesh the player sees, smoothing and LOD included — and the margin from the
  neighbours' grids (the sampler where none is loaded yet). The splat weights / extras come from the same vertices
  (`BuildResult.Colors` / `Splat`, kept on `LoadedChunk`), baked per sample column on the main thread so the worker-side
  mesher reads only those arrays. ~35 ms for one column, once.
- **The swap.** The column's chunks are a `VoxelChunkSet` under a `VoxelColumn (x,y)` root (tags `voxel` + `terrain`).
  Once its first meshes have all landed (`Settled`) the heightfield chunk's renderer and collider switch off
  (`LoadedChunk.Voxel.Swapped`; `UpdateChunkColliders` / `ApplyChunkRemesh` keep them off, tree colliders keep following
  Collision Range through `LoadedChunk.VegetationNear`) and the grass tiles over it regenerate without clumps
  (`SampleGrassGround` returns null for a swapped chunk). Trees, props, saplings and scavs stay where they were — a tree
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
- Tool: a shovel / pickaxe item with durability (the hoe pattern), or digging with any tool at different speeds?
