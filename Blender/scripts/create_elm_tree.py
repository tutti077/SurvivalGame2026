"""Classic vase-shaped elm trees (American elm silhouette) — environment_elm1..5_v<VERSION>.

Run headless:
    blender -b --python "Blender/scripts/create_elm_tree.py"            # build + save + export
    blender -b --python "Blender/scripts/create_elm_tree.py" -- render  # also write preview PNGs

Real-world scale (Blender units are meters, exported 1:1; the .vmdl uses import_scale 0.4
so the world lands on the terrain's 40 u/m — same pipeline as create_grass_clump.py):
    mature open-grown American elm: 18–27 m tall, crown 60–90 % as wide as tall,
    trunk ~0.6–1.0 m across at breast height with a flared base, splitting into several
    upswept leaders a quarter to a third of the way up. Leaves 7–15 cm.
    Variants: elm1 21 m / elm2 18 m young (0.75 m trunk); elm3 28 m / elm4 26 m / elm5 30 m mature
    (1.5 m trunk, buttress flare, 26-32 m umbrella crown). Every crown is checked for bald
    patches from above and both sides; the best of 4 seeds is kept. Limbs above the leaders grow by
    space colonisation, so they reach the whole crown (top included); leaves are only ever clumps
    on limb ends. Textures are 64 px designs saved 8x nearest-upscaled so the pixels stay hard in-engine.

Structure of each model (one .fbx, two objects):
    <name>_wood    ONE continuous closed mesh: trunk -> leaders, and nothing forking off a leader in
                   wood (every space-colonised limb becomes branch cards, see split_limbs). Each
                   skeleton segment is a tapered tube with a ball at every joint; a voxel remesh
                   fuses them into one surface (real crotches, nothing intersecting), then it is
                   decimated, smooth shaded, and bark UVs run along each limb. Leader ends are ~40 cm
                   across (stylised, never stick-thin).
    <name>_leaves  bushy branch cards (Valheim-style, ~60 per tree): each card is two quads hinged on a
                   centre crease, 3/4 as wide as long, base on a thick limb, running out to where the
                   fine limbs it replaces reached (7-11 m). The 256x256 texture is Mark's reference
                   card: a short brown stem forking into three, then leaf-sleeved arms with irregular
                   gaps between them (~45 % opaque). The crown lets sky through between the branches
                   on purpose (sky_through prints the % per tree). No collision on leaves (vmdl physics = wood only).

Textures (true 256x256 bark, 64x64 end grain, 256x256 leaf card; shaders/pixel_lit.shader point-samples them):
    Assets/models/environment/tests/elm_bark_v<N>.png        grey-brown ridged bark (7-tone ramp), 1.25 cm per texel (Mark's D3 pick, 2026-10-02)
    Assets/models/environment/tests/elm_leaves_v<N>.png      bushy branch: brown stem, leaf-sleeved arms of small flat-toned leaves, no baked light
    Assets/models/environment/tests/elm_leaves_v<N>_mask.png cutout mask (pixel_lit TextureTranslucency)
"""
import bpy, bmesh, math, os, random, struct, sys, zlib
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_crown_silhouette import measure as measure_outline
import elm_felling as fell
import numpy as np
from mathutils import Vector, noise

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
# Mark's demo folder for generated models: models AND their materials/textures live here.
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "environment", "tests")
MAT_DIR = MODEL_DIR
# where earlier versions were written; remove_old_versions() also sweeps these
OLD_DIRS = [os.path.join(ROOT, "Assets", "models", "environment"),
			os.path.join(ROOT, "Assets", "materials", "environment")]
# Every regenerated set gets a new version number in all model + material names, and older
# versions' files are deleted, so s&box never serves a stale compiled model or texture.
VERSION = 76
BARK = f"elm_bark_v{VERSION}"
GRAIN = f"elm_endgrain_v{VERSION}"
LOG = f"environment_elm_log_v{VERSION}"
HALF = f"environment_elm_loghalf_v{VERSION}"
ASSET_DIR = "models/environment/tests"
PREFAB_DIR = os.path.join(ROOT, "Assets", "prefabs", "environment", "tests")
PREFAB_REL = "prefabs/environment/tests"
LEAVES = f"elm_leaves_v{VERSION}"
BLEND_OUT = os.path.join(ROOT, "Blender", "blenderprojects", f"environment_elm_v{VERSION}.blend")
PREVIEW_DIR = os.path.join(ROOT, "Blender", "blenderprojects", "elm_preview")
ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

TEX = 64
# v57: textures are written at their TRUE size (64 x 64 end grain, 256 x 256 bark and leaf card) and
# the materials use shaders/pixel_lit.shader, which samples with POINT filtering. Before that the
# engine's complex.shader blurred a real 64 px file, so designs were saved as 512 px blocks.
PIXEL_BLOCK = 1
# Bark is a 256 x 256 design on the same 3.2 m tile (Mark, 2026-10-02: picked "D3" from 256 px options):
# 4x the texels of the old 64 px bark, ridges 44 per tile (~7 cm, real elm plate width) instead of 11.
# End grain stays at TEX.
BARK_TEX = 256
# v76 bark relief (Mark, 2026-10-02 "make it gorgeous"): the crack net is carved into a CONTINUOUS height field
# (rolling ridges + grooves with sloped walls), and colour, normal map and roughness all come from that one field.
BARK_GROOVE_W = 3          # groove wall width beside a crack, texels (~4 cm each side)
BARK_GROOVE_DEPTH = 0.55   # groove depth, fraction of the height range (major cracks go deeper)
BARK_RIDGE_AMP = 0.22      # rolling ridge undulation across the plates (slow, tile-periodic)
BARK_NORMAL_STRENGTH = 4.0 # slope multiplier for the normal map
BARK_ROUGH = (0.60, 0.95)  # roughness at plate tops .. crack bottoms (plate tops take a soft sun glint)
BARK_WARM = 0.04           # lit plate tops drift this much warmer, crack bottoms this much cooler (subtle)
# Leaf cards: a 256 x 256 design on a card 3/4 as wide as it is long, so texels are ~3 x 2.3 cm in
# the world - close to the bark's - and the small leaves are ~5 texels (~30 cm) instead of one
# blob. Both sides MUST be powers of two: a 96 x 128 design never compiled and every card drew the
# engine's red error material.
LEAF_TEX_W, LEAF_TEX_H = 256, 256   # v68: 256 so a 45 cm elm leaf has ~13 texels for a real silhouette
CARD_ASPECT = 0.75       # card width / length; the design is drawn in card space so its angles are true
LEAF_BLOCK = 1
BARK_TILE = 3.2         # m of bark per 256 px tile = 1.25 cm per texel on every limb
UP = Vector((0, 0, 1))

# Sizes from published American elm data (Minnesota DNR, Morton Arboretum, UF/IFAS ST649): usually
# 15-21 m tall (to 27+), 15-21 m spread and "often wider than high", trunk 0.6-1.5 m and up to ~2 m on
# old trees, trunk "quickly divides into several massive branches" that arch and droop at the ends.
def elm(s, E=2.6, split=4.5):
	"""Old American elm, ~22 m tall x 24 m wide at s=1 (grows outward with age): a massive
	1.6 m trunk with a lobed buttress flare, a SHORT trunk that splits low (split m) into a fan of
	6-8 heavy leaders, each as thick as a young tree, that climb in a vase and arch over."""
	R = 10.5 * s   # the clusters add ~20 % on top of this envelope
	return dict(
		base_r=0.80 * s, trunk_r=0.80 * s, top_r=0.86 * s, split_h=split * s,   # swells into the fork
		flare_h=2.6 * s, flare=0.8,
		lead_tilt=(26, 46), central_tilt=(5, 14), lead_len=7.0 * s, lead_segs=6,
		zc=15.5 * s, R=R, H=6.5 * s, E=E,
		floret=(0.28, 0.42),                  # cluster radius range, fraction of R
		attractors=int(15 * R * R), step=0.7, influence=0.42 * R, kill=1.5,
		# wood_tris (all variants): 4000 since v39 — Mark could not tell 24k from 8k; 4k was the last
		# budget before twig tips collapse into spikes (2k), compared side by side.
		droop=0.28, wood_tris=4000,
	)


def central(s, limbs_from=4.8):
	"""Central-trunk tree (Mark's concept art, tree 6): the trunk runs up through the crown to
	~80 % of the height, tapering, and heavy side limbs leave it all the way up in a spiral - long
	and ~45 deg off vertical low down, shorter and steeper (more vertical) near the top - each
	ending in its own cloud cluster, so the crown sits in tiers around the trunk."""
	R = 10.0 * s
	return dict(
		form="central",
		base_r=0.80 * s, trunk_r=0.80 * s, top_r=0.34 * s, split_h=limbs_from * s,
		trunk_h=18.0 * s,                     # where the central trunk ends inside the crown
		flare_h=3.0 * s, flare=1.0,           # spreading root buttresses, as in the concept
		limb_tilt=(45, 22),                   # deg off vertical: lowest limb .. highest limb
		limb_len=(0.85 * R, 0.45 * R), lead_segs=6,
		zc=14.0 * s, R=R, H=9.0 * s, E=2.2,
		floret=(0.28, 0.40),
		attractors=int(15 * R * R), step=0.7, influence=0.42 * R, kill=1.5,
		droop=0.10, wood_tris=4000,
	)


# Outline targets measured from Mark's reference photos with measure_crown_silhouette.py
# (lumpiness 1.0-3.5 %, deepest dip 2.3-5.6 % of crown width); seeds are scored against these.
LUMP_TARGET, DIP_TARGET = 2.3, 3.8

def tiered(tiers, trunk_h, top_cr, trunk_r=0.8):
	"""Tiered central-trunk tree (Mark's concept art): a tall trunk runs up to trunk_h and carries
	heavy limbs in tiers; each tier's limbs end in broad, flattened leaf pads (plus a smaller pad
	part-way along), and the trunk top wears its own crown. No filler clusters, so there is sky
	between the layers. tiers: dicts of h (m up the trunk), n (limbs), tilt (deg off vertical
	range), len (m), cr (pad radius m), flat (pad height / width)."""
	reach = max(T["len"] * math.sin(math.radians(sum(T["tilt"]) / 2)) + T["cr"] for T in tiers)
	zlo = min(T["h"] + T["len"] * math.cos(math.radians(T["tilt"][1])) - T["cr"] * T["flat"] for T in tiers)
	zhi = trunk_h + top_cr * 1.3
	area = sum(T["n"] * math.pi * T["cr"] ** 2 * 1.36 for T in tiers) + math.pi * top_cr ** 2
	R = reach
	return dict(
		form="central", tiers=tiers, top_cr=top_cr,
		base_r=trunk_r, trunk_r=trunk_r, top_r=trunk_r * 0.42,
		split_h=min(T["h"] for T in tiers) * 0.8, trunk_h=trunk_h,
		flare_h=3.0, flare=1.0, lead_segs=6,
		zc=(zlo + zhi) / 2, R=R, H=(zhi - zlo) / 2, E=2.6,
		floret=(0.28, 0.40),
		attractors=int(5 * area), step=0.7, influence=max(4.0, 0.4 * R), kill=1.5,
		droop=0.12, wood_tris=4000,
	)


# name, first seed tried, profile, leader count (side-limb count for the central form). 1-5 are
# old American elms of different size and form (trunk across = 2 x trunk_r); 6 is the
# central-trunk tree from Mark's concept art.
VARIANTS = [
	(f"environment_elm1_v{VERSION}", 11, tiered(                        # two layers + crown (concept art)
		[dict(h=7.0, n=3, tilt=(66, 76), len=9.0, cr=4.2, flat=0.55),
		 dict(h=11.5, n=3, tilt=(50, 60), len=5.5, cr=3.6, flat=0.6)], trunk_h=16.0, top_cr=4.6), 0),
	(f"environment_elm2_v{VERSION}", 23, elm(1.00), 7),                 # ~22 m, 1.6 m trunk
	(f"environment_elm3_v{VERSION}", 41, elm(1.10, E=3.1), 8),          # ~24 m flat-topped, 1.75 m trunk
	(f"environment_elm4_v{VERSION}", 53, elm(0.95, split=6.8), 6),      # ~21 m, tall clear trunk
	(f"environment_elm5_v{VERSION}", 67, elm(1.15, E=2.8, split=3.6), 7),  # ~25 m, low fan, 1.85 m trunk
	(f"environment_elm6_v{VERSION}", 83, central(1.0), 9),                 # central trunk, 9 side limbs
]


# ---------------------------------------------------------------------------- textures

def write_png(path, rgba, block=PIXEL_BLOCK):
	"""rgba: (h, w, 4) uint8, row 0 = top of the image; written upscaled by `block` (nearest)."""
	rgba = np.ascontiguousarray(rgba.repeat(block, axis=0).repeat(block, axis=1))
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

	png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
		   + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
	with open(path, "wb") as f:
		f.write(png)


def tile_noise(x, y, rng, terms, fmax):
	"""Sum of integer-frequency sines: tiles exactly on the unit square."""
	out = np.zeros_like(x)
	for _ in range(terms):
		kx, ky = rng.randint(-fmax, fmax), rng.randint(1, fmax)
		ph = rng.uniform(0, 2 * math.pi)
		out += np.sin(2 * math.pi * (kx * x + ky * y) + ph) / math.hypot(kx, ky)
	return out / (np.abs(out).max() + 1e-6)


# Chosen by Mark from rendered options (2026-09-27): bark "D1" and leaves "B2".
# v65 (Mark, 2026-10-02, art routine): American elm bark - grey-brown with brown in it, low contrast.
# 0 unused, 1 crack, 2 shaded plate edge, 3-4 plate body, 5 lit plate edge, 6 unused. Also the leaf-card stems.
BARK_RAMP = [(52, 44, 38), (72, 62, 54), (94, 82, 72), (116, 102, 90), (138, 124, 110), (158, 144, 128), (176, 162, 146)]
BARK_LINES = 36            # crack lines per family per 3.2 m tile (spacing ~9 cm)
BARK_LEAN_K = 6            # lean: a line drifts K spacings across the tile height -> ~8 deg off vertical
BARK_DRIFT = 1.2           # per-line lateral drift range across the tile height, in spacings (ridges converge / diverge)
BARK_TIES = 60             # short cross-cracks per tile, each running crack to crack
BARK_CRACK_W = ((0.0, 0.3), (0.5, 1.0), (1.3, 2.2))   # crack half-width ranges, texels: hairline (65 %), medium (27 %), major (8 %)
BARK_LIP = 1               # width of the lit / shaded plate edges beside a crack, texels
BARK_RELIEF = 1.0          # strength of those edges in ramp steps
BARK_GROUND_VAR = 0.6      # slow tonal variation of the plate body
BARK_GRAIN = 0.3           # fine vertical grain on the plate body
BARK_BREAK = 0.35          # share of every crack's length left undrawn (gaps), so no line runs top to bottom
BARK_JOG = 2.0             # sideways step a crack takes between gaps, texels
BARK_MAX_SKEW = 1.8        # a wrapped face whose texels are stretched more than this one way gets a flat projection
STEM_RGB = BARK_RAMP[2]   # the card's stem in the bark's own grey-brown, so it blends into the limb it leaves (Mark, v48)
# Leaf bases use tones 3 / 4 / 4 / 5 (midrib +1, veins -1). Tone 4 is Mark's base green #448c37
# (v38: the old lime ramp read almost pastel); the ramp keeps that hue, darker below and lighter above.
# v69 (Mark picked sample 10 of ten, 2026-10-02: "indiscernible leaves is ideal"): true-scale elm leaves
# (12-18 cm, 3-5 texels) in two passes - a darker back layer, then a lighter front layer - so clumps read as
# fine foliage mass with depth, no outlines, no lit / shaded halves. Deep, realistic elm green.
LEAF_RAMP = [(26, 44, 24), (36, 62, 32), (48, 82, 42), (62, 102, 52), (80, 122, 64), (100, 142, 80), (126, 162, 100)]
LEAF_LEN = (0.014, 0.02)   # leaf length in card units (~12-18 cm on a 9 m card)
LEAF_BACK = (60000, 1.4, (1, 2, 2))    # back layer: sleeve density, spread (x half-width), tones
LEAF_FRONT = (45000, 0.9, (3, 4, 4, 5))  # front layer: tighter to the arm, lighter


def make_bark_texture():
	"""American elm bark after Mark's sketch + photo (2026-10-02, v65, art routine): the CRACKS are a
	connected net and the flat grey-brown plates between them are the ridges. Two families of full-height
	lines (BARK_LINES each, one leaning right and one left by atan(BARK_LEAN_K * spacing / tile)) start at
	evenly spaced x so each line's foot lands on another's head at the tile edge (seamless), wobble with a
	y-periodic sine and cross into stretched diamonds; per-line drift (BARK_DRIFT) varies the spacing.
	BARK_TIES short cross-cracks run from one crack to the next and stop there (no free ends). Each crack
	is one step above the darkest tone, its left plate edge one step lighter and its right edge one step
	darker (BARK_LIP / BARK_RELIEF), on a mid ground with slow variation. 7-step ramp, 3-4 tones in use."""
	n, k, drift, tie_n, crack_w = BARK_LINES, BARK_LEAN_K, BARK_DRIFT, BARK_TIES, BARK_CRACK_W
	lip, relief, ground_var, grain, ramp, seed = BARK_LIP, BARK_RELIEF, BARK_GROUND_VAR, BARK_GRAIN, BARK_RAMP, 11
	rng = random.Random(seed)
	crack = np.zeros((BARK_TEX, BARK_TEX), bool)
	spacing = BARK_TEX / n
	# Line i leaves the tile top where line (i + k) starts, so the lines of one family form gcd(n, k)
	# chains that wrap the tile. Every line gets its own lateral drift (so ridges converge and diverge)
	# and its own width and wobble; the drifts of each chain are centred to sum to zero and the start
	# of each line is propagated from its predecessor, so every crack is continuous across the edge.
	g = math.gcd(n, k)
	L = n // g
	for sign in (1, -1):
		for c in range(g):
			drifts = [rng.uniform(-drift, drift) * spacing for _ in range(L)]
			mean = sum(drifts) / L
			drifts = [d - mean for d in drifts]
			x0 = c * spacing + rng.uniform(-0.3, 0.3) * spacing
			for j in range(L):
				# width: mostly hairlines, some medium, a few wide major cracks (Mark: "not all the lines
				# should be the same thickness"), and each crack pinches / widens along its length
				u = rng.random()
				w = rng.uniform(*crack_w[0]) if u < 0.65 else rng.uniform(*crack_w[1]) if u < 0.92 else rng.uniform(*crack_w[2])
				wm = rng.choice((1, 2)); wph = rng.uniform(0, 6.28)
				amp = rng.uniform(0.3, 1.4) * spacing * 0.35
				m = rng.choice((1, 2, 3))
				# breaks + jogs: a crack is drawn only where a y-periodic break function is high, and it
				# steps sideways by up to BARK_JOG texels between breaks, so no line runs the tile unbroken
				bf1, bf2, bf3 = rng.choice((3, 4, 5, 6)), rng.choice((7, 9, 11)), rng.choice((2, 3))
				bp1, bp2, bp3 = rng.uniform(0, 6.28), rng.uniform(0, 6.28), rng.uniform(0, 6.28)
				for yy in range(BARK_TEX * 2):
					y = yy / 2.0
					brk = (math.sin(2 * math.pi * bf1 * y / BARK_TEX + bp1) + 0.6 * math.sin(2 * math.pi * bf2 * y / BARK_TEX + bp2)) / 1.6
					if brk < -1 + 2 * BARK_BREAK:
						continue
					jog = round(BARK_JOG * math.sin(2 * math.pi * bf3 * y / BARK_TEX + bp3))
					x = x0 + (sign * k * spacing + drifts[j]) * y / BARK_TEX + amp * math.sin(2 * math.pi * m * y / BARK_TEX) + jog
					wy = w * (0.55 + 0.45 * math.sin(2 * math.pi * wm * y / BARK_TEX + wph))
					xi = int(round(x))
					for wx in range(-3, 4):
						if (wx + 0.5 if wx >= 0 else wx - 0.5) * (1 if wx >= 0 else -1) <= wy + 0.5 and abs(wx) <= wy + 0.5:
							crack[int(y) % BARK_TEX, (xi + wx) % BARK_TEX] = True
				x0 = x0 + sign * k * spacing + drifts[j]      # next line in the chain starts where this one left
	# ties: from a random point, walk left and right (slightly tilted) until a crack is hit
	for _ in range(tie_n):
		x0, y0 = rng.uniform(0, BARK_TEX), rng.uniform(0, BARK_TEX)
		tilt = rng.uniform(-0.45, 0.45)
		ends = []
		for d in (1, -1):
			for step in range(1, int(spacing * 2.2)):
				px, py = int(x0 + d * step) % BARK_TEX, int(y0 + d * step * tilt) % BARK_TEX
				if crack[py, px]:
					ends.append(step); break
			else:
				ends.append(None)
		if None in ends:
			continue
		wide_tie = rng.random() < 0.3
		for d, step_max in zip((1, -1), ends):
			for step in range(step_max):
				yy_, xx_ = int(y0 + d * step * tilt) % BARK_TEX, int(x0 + d * step) % BARK_TEX
				crack[yy_, xx_] = True
				if wide_tie:
					crack[(yy_ + 1) % BARK_TEX, xx_] = True
	wide = crack & np.roll(crack, 1, 1) & np.roll(crack, -1, 1) & np.roll(crack, 2, 1) & np.roll(crack, -2, 1)   # 5+ texels wide
	left1 = np.roll(crack, -lip, 1) & ~crack
	left2 = np.roll(wide, -2 * lip, 1) & ~crack & ~left1
	right1 = np.roll(crack, lip, 1) & ~crack
	right2 = np.roll(wide, 2 * lip, 1) & ~crack & ~right1
	r2 = random.Random(seed + 1)
	x, y = np.meshgrid((np.arange(BARK_TEX) + 0.5) / BARK_TEX, (np.arange(BARK_TEX) + 0.5) / BARK_TEX)

	# --- v76 height field (0 = crack bottom .. 1 = highest plate crest), tile-periodic everywhere.
	# Plates: slow rolling ridges plus a little vertical grain, so no plate is a dead-flat sheet.
	h = 0.72 + BARK_RIDGE_AMP * tile_noise(x * 2, y, r2, 8, 3) + 0.05 * tile_noise(x * 4, y, r2, 10, 24)
	# Grooves: distance to the nearest crack texel (8-neighbour dilation, wrapping), walls BARK_GROOVE_W wide.
	dist = np.full((BARK_TEX, BARK_TEX), BARK_GROOVE_W + 1, float)
	reach = crack.copy()
	dist[crack] = 0
	for d in range(1, BARK_GROOVE_W + 1):
		grown = reach.copy()
		for dy_ in (-1, 0, 1):
			for dx_ in (-1, 0, 1):
				grown |= np.roll(np.roll(reach, dy_, 0), dx_, 1)
		dist[grown & ~reach] = d
		reach = grown
	wall = np.clip(1.0 - dist / (BARK_GROOVE_W + 1), 0.0, 1.0) ** 1.6
	h -= BARK_GROOVE_DEPTH * wall
	h -= 0.15 * np.clip(1.0 - dist / 2.0, 0.0, 1.0) * (wide | np.roll(wide, 1, 1) | np.roll(wide, -1, 1))
	h = (h - h.min()) / (h.max() - h.min() + 1e-6)

	# Colour: the ramp indexed by height (tones 1..6 - dark crack bottoms, light plate crests), with a
	# subtle warm drift on the crests and cool drift in the grooves. No hue tricks beyond that (Mark).
	idx = np.clip(np.round(1.0 + h * 5.0), 0, 6).astype(int)
	rgb = np.array(ramp, float)[idx]
	warm = (h - 0.5) * 2.0 * BARK_WARM
	rgb = rgb * np.dstack([1.0 + warm, np.ones_like(warm), 1.0 - warm])
	rgb = np.clip(np.round(rgb), 0, 255)
	colour = np.dstack([rgb, np.full((BARK_TEX, BARK_TEX), 255)]).astype(np.uint8)
	return colour, h


def make_bark_normal(h, strength=BARK_NORMAL_STRENGTH):
	"""Tangent-space normal map from the bark height field. Point-sampled like the colour, so every texel on
	a flat facet tilts its own way and catches the sun differently - the per-pixel light inside one face that
	flat shading alone cannot give. Green-up convention; strength scales the slope."""
	dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5      # +x = +u (right)
	dy = (np.roll(h, 1, 0) - np.roll(h, -1, 0)) * 0.5      # row 0 is the top of the image: +v = up
	# Sign checked in engine (v75): with -dx/-dy the cracks lit as raised veins; +dx/+dy makes them recessed.
	n = np.dstack([dx * strength, dy * strength, np.ones_like(h)])
	n /= np.linalg.norm(n, axis=2, keepdims=True)
	rgb = np.clip(np.round((n * 0.5 + 0.5) * 255), 0, 255)
	return np.dstack([rgb, np.full((BARK_TEX, BARK_TEX), 255)]).astype(np.uint8)


def make_bark_roughness(h):
	"""Roughness from the same height field: plate crests smooth enough for a soft sun glint, crack bottoms matte."""
	r = BARK_ROUGH[1] + (BARK_ROUGH[0] - BARK_ROUGH[1]) * h
	g = np.clip(np.round(r * 255), 0, 255)
	return np.dstack([g, g, g, np.full((BARK_TEX, BARK_TEX), 255)]).astype(np.uint8)



def connected_to(mask, seed):
	"""The 4-connected part of `mask` reachable from any True cell of `seed` (both (h, w) bool)."""
	keep = mask & seed
	while True:
		grown = keep.copy()
		grown[1:] |= keep[:-1]
		grown[:-1] |= keep[1:]
		grown[:, 1:] |= keep[:, :-1]
		grown[:, :-1] |= keep[:, 1:]
		grown &= mask
		if (grown == keep).all():
			return keep
		keep = grown


def make_leaf_texture():
	"""Bushy branch card (v46, Mark's reference): a short brown stem forking into three, then a leafy
	main axis up the middle with side arms leaving alternately (long and flat low down, shorter and
	steeper near the top), each arm with sub-arms forking forward. Every arm is a SLEEVE of small
	leaves - nothing is drawn as a line - so the branch structure reads only through the irregular
	gaps between the leafy arms (~45 % opaque). No baked lighting: flat per-leaf tones.
	v69: true-scale tiny leaves in a dark back pass and a light front pass (LEAF_BACK / LEAF_FRONT), so
	the foliage reads as an indiscernible mass with depth rather than countable leaves.
	Everything not connected to the stem is erased (no leaf floats free). Drawn in card space
	(x = (u - 0.5) * CARD_ASPECT, y = v) so the angles are true on the card."""
	rng = random.Random(9)
	W, H = LEAF_TEX_W, LEAF_TEX_H
	pu, pv = np.meshgrid((np.arange(W) + 0.5) / W, 1.0 - (np.arange(H) + 0.5) / H)
	px = (pu - 0.5) * CARD_ASPECT
	tone = np.full((H, W), -1)
	stem = np.zeros((H, W), bool)

	def line(mask, a, b, w):
		(ax, ay), (bx, by) = a, b
		dx, dy = bx - ax, by - ay
		t = np.clip(((px - ax) * dx + (pv - ay) * dy) / (dx * dx + dy * dy + 1e-9), 0, 1)
		mask[np.hypot(px - (ax + t * dx), pv - (ay + t * dy)) < w] = True

	# short brown stem forking into three, like the reference: ~20 % of the card
	line(stem, (0.0, 0.02), (0.0, 0.12), 0.0035)   # v53 (Mark): thin, ~one texel
	forks = [(-0.04, 0.21), (0.005, 0.23), (0.045, 0.2)]
	for f in forks:
		line(stem, (0.0, 0.11), f, 0.003)

	leaves = []
	segs = []

	def sleeve(a, b, half_w, density):
		"""Leaves along a branch segment: the branch is a sleeve of leaves, never a drawn line.
		Records the segment; the two passes below scatter leaves along every segment."""
		segs.append((a, b, half_w))

	def scatter(density, spread, tones):
		for (ax, ay), (bx, by), half_w in segs:
			L = math.hypot(bx - ax, by - ay)
			nx, ny = -(by - ay) / (L + 1e-9), (bx - ax) / (L + 1e-9)
			for _ in range(int(L * half_w * density)):
				t = rng.random()
				off = rng.gauss(0, 0.5) * half_w
				if abs(off) > half_w * spread:
					continue
				x, y = ax + (bx - ax) * t + nx * off, ay + (by - ay) * t + ny * off
				ang = math.atan2(ny, nx) * (1 if off > 0 else -1) + rng.uniform(-1.2, 1.2)
				leaves.append((x, y, ang, rng.uniform(*LEAF_LEN), rng.choice(tones)))

	def arm(p, ang, length, level):
		"""One leafy arm: a sleeved segment that ends in a tuft, with sub-arms forking forward."""
		for _ in range(6):                        # stay inside the card
			e = (p[0] + math.sin(ang) * length, p[1] + math.cos(ang) * length)
			if abs(e[0]) < 0.30 and e[1] < 0.9:
				break
			length *= 0.75
		if length < 0.04:
			return
		sleeve(p, e, 0.025 - 0.003 * level, 30000)
		sleeve(e, (e[0] + math.sin(ang) * 0.025, e[1] + math.cos(ang) * 0.025), 0.042, 30000)
		if level >= 2:
			return
		for t, da in ((0.55, -1), (1.0, 1))[:2 - level]:
			q = (p[0] + (e[0] - p[0]) * t, p[1] + (e[1] - p[1]) * t)
			arm(q, ang + da * math.radians(rng.uniform(30, 45)), length * rng.uniform(0.45, 0.6), level + 1)

	# main leafy axis up the middle, from the fork to the top
	axis = [(0.0, 0.22)]
	while axis[-1][1] < 0.9:
		x, y = axis[-1]
		axis.append((x + rng.uniform(-0.03, 0.03), y + 0.13))
	for p, q in zip(axis, axis[1:]):
		sleeve(p, q, 0.029, 30000)
	sleeve(axis[-1], (axis[-1][0], axis[-1][1] + 0.03), 0.044, 30000)
	# side arms leave the axis alternately, long and flat low down, shorter and steeper higher up
	side = -1
	for i, (x, y) in enumerate(axis[:-1]):
		t = i / max(1, len(axis) - 2)
		ang = side * math.radians(70 - 30 * t + rng.uniform(-8, 8))
		arm((x, y + rng.uniform(0.0, 0.06)), ang, (0.30 - 0.14 * t) * rng.uniform(0.85, 1.15), 0)
		side = -side
	# the two lowest forks each throw one low arm out sideways too (the fan is widest low down)
	arm(forks[0], math.radians(-75), 0.2, 1)
	arm(forks[2], math.radians(74), 0.2, 1)
	scatter(*LEAF_BACK)        # dark back layer first, then the lighter front layer paints over it
	scatter(*LEAF_FRONT)
	for lx, ly, ang, length, base in leaves:
		d = np.array([math.cos(ang), math.sin(ang)])
		rx_, ry_ = px - lx, pv - ly
		a = (rx_ * d[0] + ry_ * d[1]) / length
		b = (-rx_ * d[1] + ry_ * d[0]) / (length * 0.4)
		prof = np.clip(a, 0, 1) ** 0.5 * np.clip(1 - a, 0, 1) ** 0.7 / (0.4 ** 0.5 * 0.6 ** 0.7)
		inside = (a > 0) & (a < 1) & (np.abs(b) < prof)
		tone[inside] = base
	tone[(pv > 0.97) | (np.abs(px) > CARD_ASPECT * 0.5 - 0.02)] = -1
	alpha = connected_to((tone >= 0) | stem, stem)
	tone[~alpha] = -1
	ramp = np.array(LEAF_RAMP, float)
	rgb = ramp[np.clip(tone, 0, 6)]
	rgb[stem & (tone < 0)] = STEM_RGB
	rgb[~alpha] = ramp[3]              # bleed colour under the cutout, never black
	print(f"leaf texture: {len(leaves)} leaves, {alpha.mean() * 100:.0f} % opaque")
	return np.dstack([rgb, np.where(alpha, 255, 0)]).astype(np.uint8)


# ---------------------------------------------------------------------------- skeleton

class Node:
	__slots__ = ("pos", "r", "parent", "depth")

	def __init__(self, pos, r, parent, depth):
		self.pos, self.r, self.parent, self.depth = pos, r, parent, depth


def rand_unit(rng):
	while True:
		v = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))
		if 0.05 < v.length <= 1:
			return v.normalized()


def outward(pos):
	h = Vector((pos.x, pos.y, 0))
	return h.normalized() if h.length > 1e-4 else Vector((1, 0, 0))


def envelope(p, P):
	"""< 1 inside the crown: a superellipsoid around zc (squarer shoulders as E grows)."""
	rh = math.hypot(p[0], p[1]) / P["R"]
	return rh ** P["E"] + ((p[2] - P["zc"]) / P["H"]) ** 2


def envelope_normal(p, P):
	rh = math.hypot(p.x, p.y)
	h = Vector((p.x, p.y, 0)).normalized() if rh > 1e-4 else Vector((0, 0, 0))
	g = h * (P["E"] * (rh / P["R"]) ** (P["E"] - 1) / P["R"])
	g.z = 2 * (p.z - P["zc"]) / (P["H"] * P["H"])
	return g.normalized() if g.length > 1e-6 else UP.copy()


def make_florets(rng, P, seeds=()):
	"""The crown as a bunch of overlapping clusters (broccoli florets): dart-throw cluster centres
	over the upper crown envelope, each sunk 25-65 % of its radius so it bulges through the envelope,
	plus a few inner clusters to fill the core. Neighbours are ~1.4 radii apart: they always
	overlap (no holes between branches) but leave a notch in the outline between bumps.
	`seeds` are clusters placed first (the central-trunk tree puts one on every limb end); the
	envelope pass then only fills the gaps left between them."""
	zc, R, H, E = P["zc"], P["R"], P["H"], P["E"]
	lo, hi = P["floret"]
	florets = list(seeds)   # (centre, horizontal radius, vertical radius)

	def fits(c, r, spacing):
		for c2, r2, _ in florets:
			if (c - c2).length < spacing * 0.5 * (r + r2):
				return False
		return True

	# clusters bulge past the envelope, so they are seeded on a slightly smaller one to keep the
	# overall crown on the published size
	Rs, Hs = R * 0.86, H * 0.86
	passes = () if P.get("tiers") else ((1.0, 1.7, 400),) if seeds else ((1.0, 1.4, 3000), (0.5, 1.6, 600))
	for layer, spacing, tries in passes:
		for _ in range(tries):
			phi = rng.uniform(0, 2 * math.pi)
			z0 = rng.uniform(-0.45, 1.0)
			qh = max(0.0, 1 - z0 * z0) ** (1 / E)
			on = Vector((qh * Rs * math.cos(phi), qh * Rs * math.sin(phi), zc + z0 * Hs))
			r = rng.uniform(lo, hi) * R
			n = envelope_normal(on, P)
			sink = rng.uniform(0.25, 0.65)   # uneven: some clusters stand proud, some sit back
			c = (on - n * sink * r) if layer == 1.0 else Vector((on.x * 0.5, on.y * 0.5, zc + z0 * Hs * 0.5))
			if fits(c, r, spacing):
				florets.append((c, r, r * rng.uniform(0.7, 0.85)))
	return florets


def floret_value(p, fl):
	"""< 1 inside the cluster; flatter underside, like a broccoli floret."""
	c, rh, rv = fl
	d = p - c
	vz = rv if d.z > 0 else rv * 0.6
	return (d.x * d.x + d.y * d.y) / (rh * rh) + (d.z / vz) ** 2


def tilt(d, angle, az):
	"""Rotate direction d away from itself by `angle`, toward azimuth `az` around it."""
	a = d.orthogonal().normalized()
	b = d.cross(a).normalized()
	side = a * math.cos(az) + b * math.sin(az)
	return (d * math.cos(angle) + side * math.sin(angle)).normalized()


def smooth_limbs(nodes, iterations=8):
	"""Round off kinks (a limb that swung sharply upward read badly): every limb node with exactly one child (not a fork, not the
	trunk) is pulled halfway toward the midpoint of its parent and child, a few times over.
	Forks and trunk nodes stay put, so the branching structure doesn't move."""
	kids = {}
	for i, n in enumerate(nodes):
		if n.parent >= 0:
			kids.setdefault(n.parent, []).append(i)
	movable = [i for i, n in enumerate(nodes)
			   if n.depth >= 1 and len(kids.get(i, ())) == 1 and nodes[n.parent].depth >= 1]
	for _ in range(iterations):
		new = {}
		for i in movable:
			mid = (nodes[nodes[i].parent].pos + nodes[kids[i][0]].pos) * 0.5
			new[i] = nodes[i].pos.lerp(mid, 0.5)
		for i, p_ in new.items():
			nodes[i].pos = p_


def add_roots(nodes, base, P, rr):
	"""Surface roots (for trees that get partly buried): 5-8 per tree, unevenly spaced, each a
	different length (~0.1-0.25 of the crown radius, so ~1-2.6 m on a full-size elm) from the base.
	Each drops to the ground, then weaves: stretches arch clear of the dirt (a gap under them),
	others sink below it, and every tip dives underground. Roots are ordinary skeleton segments
	(depth 0, kept out of leaves and limb growth), so the voxel fuse makes them part of the one
	wood surface - no separate root pieces."""
	count = rr.randint(5, 8)
	az0 = rr.uniform(0, 2 * math.pi)
	trunk_r = P["trunk_r"]
	for k in range(count):
		az = az0 + k * 2 * math.pi / count + rr.uniform(-0.45, 0.45)   # uneven spacing: never square
		length = rr.uniform(0.1, 0.25) * P["R"]
		r0 = trunk_r * rr.uniform(0.34, 0.48)
		r1 = 0.08
		amp_k = rr.uniform(0.5, 1.6)              # arch height in root radii (low hugging .. clear arch)
		waves = rr.uniform(0.6, 1.4)              # arches along the root
		ph = rr.uniform(0, 2 * math.pi)
		steps = max(6, int(length / 0.35))
		heading = az
		start = Vector((math.cos(az), math.sin(az), 0)) * trunk_r * 0.75 + Vector((0, 0, 0.35))
		prev = len(nodes)
		nodes.append(Node(start, r0, base, 0))
		pos = start.copy()
		for i in range(1, steps + 1):
			t = i / steps
			heading += rr.uniform(-0.12, 0.12)    # gentle wander
			pos = pos + Vector((math.cos(heading), math.sin(heading), 0)) * (length / steps)
			r = r0 + (r1 - r0) * t ** 1.3            # stays thick well out from the trunk
			# settle to the ground over the first 30 %, then weave (centre height relative to the
			# ground: > r arches clear, < 0 sinks in), then dive under at the tip
			settle = max(0.0, 1 - t / 0.3) ** 2
			wave = amp_k * r * math.sin(2 * math.pi * waves * t + ph) * min(1.0, t / 0.3)
			dive = -0.5 * max(0.0, (t - 0.85) / 0.15)
			z = 0.35 * settle + r * 0.5 + wave + dive
			nodes.append(Node(Vector((pos.x, pos.y, z)), r, prev, 0))
			prev = len(nodes) - 1


TIPS_PER_CARD = 2.3      # one branch card per ~2.3 fine-limb tips it replaces: ~60 cards per tree (Mark)
CARDS_MAX_PER_ROOT = 8   # cap per fine-limb subtree
TIP_FAN, TIP_BURY = 4, 3.8   # cards per leader end, and how far (m) their bases sit back along the limb
CARDS_PER_TREE = 100     # every tree gets exactly this many cards (Mark, v51: "100 so it's a little more filled in")
CARDS_PER_FLORET_M = 0.85 # v50: every crown cluster gets at least (radius / this) cards, filled from the nearest wood
CARD_BASE_MIN = 0.7      # a card's base must sit above zc - CARD_BASE_MIN * H: no cards halfway down the leaders (Mark)


def split_limbs(nodes, P, florets, rng):
	"""v44 (Mark, from Valheim): the wood is the trunk and a few thick limbs; everything finer is
	drawn by the leaf cards, which read as branches themselves. v51 (Mark: "no additional hard limbs
	that split off" a limb): the wood is ONLY the trunk and the leaders - nothing forks off a leader
	in wood; every space-colonised limb is cut. Each cut-off subtree (rooted where it left a leader) is replaced by
	branch cards fanning from that point to its farthest-apart tips, one per ~TIPS_PER_CARD tips,
	so the cards reach exactly where the space-colonised limbs did and the crown keeps its shape.
	Returns (wood nodes, compacted and re-linked; cards as (base, direction, length))."""
	n = len(nodes)
	keep = [nd.depth == 0 or (nd.depth == 1 and nd.r > 0) for nd in nodes]
	# v51: a leader whose tip stops below the crown used to be carried up by its sub-limbs; with no
	# wood forking off leaders it would stand as a bare stub, so the whole leader is dropped.
	z_min = P["zc"] - CARD_BASE_MIN * P["H"]
	lead_kid = [False] * n
	for i, nd in enumerate(nodes):
		if nd.depth == 1 and nd.parent >= 0 and nodes[nd.parent].depth == 1:
			lead_kid[nd.parent] = True
	for i, nd in enumerate(nodes):
		if nd.depth == 1 and keep[i] and not lead_kid[i] and nd.pos.z < z_min:
			j = i
			while j >= 0 and nodes[j].depth == 1:
				keep[j] = False
				j = nodes[j].parent
	kids = {}
	for i, nd in enumerate(nodes):
		if nd.parent >= 0:
			kids.setdefault(nd.parent, []).append(i)
	branches = []
	for i, nd in enumerate(nodes):
		if keep[i] or nd.parent < 0 or not keep[nd.parent]:
			continue
		# subtree of i: collect its tips
		tips, stack = [], [i]
		while stack:
			j = stack.pop()
			ks = kids.get(j, [])
			if ks:
				stack.extend(ks)
			else:
				tips.append(nodes[j].pos)
		root = nodes[nd.parent].pos
		want = max(1, min(CARDS_MAX_PER_ROOT, round(len(tips) / TIPS_PER_CARD)))
		chosen = [max(tips, key=lambda t: (t - root).length)]
		while len(chosen) < want and len(chosen) < len(tips):
			chosen.append(max(tips, key=lambda t: min((t - c).length for c in chosen)))
		for t in chosen:
			v = t - root
			if v.length < 1e-3:
				continue
			branches.append((root, v.normalized(), v.length))
		# v49 (Mark: "not enough up top"): fine limbs in the upper crown are short, so every subtree
		# rooted above the crown centre also throws one card straight up-and-out
		if root.z > P["zc"]:
			up_out = (outward(root) * 0.9 + UP * 0.6).normalized()
			branches.append((root, up_out, max(4.0, 0.6 * max((t - root).length for t in tips))))
	# v50 (Mark): no cards halfway down the tree - fine limbs that sprouted low on a leader put
	# their cards near the fork. A card's base has to be up in the crown.
	branches = [b for b in branches if b[0].z > z_min]
	# v52 (Mark): bury every leader END in leaves - TIP_FAN cards per wood tip, bases set back inside
	# the wood, fanned about the limb direction, so the end of the limb cannot be seen.
	has_kid = [False] * n
	for i, nd in enumerate(nodes):
		if keep[i] and nd.parent >= 0:
			has_kid[nd.parent] = True
	for i, nd in enumerate(nodes):
		if keep[i] and nd.depth >= 1 and not has_kid[i] and nd.pos.z > z_min:
			d = (nd.pos - nodes[nd.parent].pos).normalized() if nd.parent >= 0 else UP
			for k in range(TIP_FAN):
				yaw = math.radians(rng.uniform(-35, 35))
				dk = (d * math.cos(yaw) + d.cross(UP).normalized() * math.sin(yaw) + UP * rng.uniform(-0.25, 0.2)).normalized()
				branches.append((nd.pos - d * TIP_BURY, dk, 6.5))
	if len(branches) > CARDS_PER_TREE:
		# keep a spatially EVEN subset (farthest-point over card tips), not the longest: dropping the
		# short ones stripped whole limb ends bare (elm5, v50)
		tips_of = [b[0] + b[1] * b[2] for b in branches]
		chosen = [max(range(len(branches)), key=lambda i: branches[i][2])]
		dist = [(tips_of[i] - tips_of[chosen[0]]).length for i in range(len(branches))]
		while len(chosen) < CARDS_PER_TREE:
			j = max(range(len(branches)), key=lambda i: dist[i])
			chosen.append(j)
			dist = [min(dist[i], (tips_of[i] - tips_of[j]).length) for i in range(len(branches))]
		branches = [branches[i] for i in chosen]
	# v50 (Mark): cards only left the wood where a fine limb did, so whole flanks of the crown were
	# bald higher up. Every cluster (floret) now gets at least its share of cards: any short cluster
	# is filled from the wood nodes inside or beside it, each card aimed at a random point in the
	# cluster's upper half.
	wood = [nd.pos for i, nd in enumerate(nodes) if keep[i] and nd.depth >= 1 and nd.pos.z > z_min]

	def fill(fl):
		c, rh, rv = fl
		near = [w for w in wood if floret_value(w, fl) < 2.5] or [min(wood, key=lambda w: (w - c).length)]
		w = rng.choice(near)
		aim = c + Vector((rng.uniform(-0.8, 0.8) * rh, rng.uniform(-0.8, 0.8) * rh, rng.uniform(-0.35, 0.7) * rv))
		v = aim - w
		if v.length < 1.0:
			v = (outward(w) + UP).normalized() * rh
		branches.append((w, v.normalized(), v.length + 0.5 * rh))

	if wood:
		for fl in florets:
			have = sum(1 for b in branches if floret_value(b[0] + b[1] * b[2] * 0.7, fl) < 1.0)
			for _ in range(max(3, int(fl[1] / CARDS_PER_FLORET_M)) - have):
				fill(fl)
		# then top the tree up to CARDS_PER_TREE, clusters weighted by their volume
		weights = [rh * rh * rv for _, rh, rv in florets]
		while len(branches) < CARDS_PER_TREE:
			fill(rng.choices(florets, weights)[0])
	remap, out = {}, []
	for i, nd in enumerate(nodes):
		if not keep[i]:
			continue
		remap[i] = len(out)
		nd.parent = remap[nd.parent] if nd.parent >= 0 else -1
		out.append(nd)
	return out, branches


def build_skeleton(rng, P, leader_count):
	"""Trunk + leaders (the elm vase) or a central trunk with spiral side limbs (form="central"),
	then space colonisation: attraction points fill the crown
	shell and limbs grow toward them in small steps until every part of the crown - the top
	included - has a limb in it. Radii come from the pipe model (r^2.5 of children adds up), so
	limbs thicken toward the trunk naturally. Returns the node list."""
	nodes = []

	def add(pos, r, parent, depth):
		nodes.append(Node(pos, r, parent, depth))
		return len(nodes) - 1

	def grow(j, d, length, r0, r1, depth, segs, out_bend):
		path = [j]
		pos = nodes[j].pos.copy()
		for k in range(1, segs + 1):
			d = (d + outward(pos) * out_bend + rand_unit(rng) * 0.06).normalized()
			pos = pos + d * (length / segs)
			path.append(add(pos, r0 + (r1 - r0) * k / segs, path[-1], depth))
		return path, d

	# Trunk with a slight lean; radius from the profile (1.35-1.85 m across).
	base = add(Vector((0, 0, -0.4)), P["base_r"], -1, 0)
	lean = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), 0)) * 0.05
	central = P.get("form") == "central"
	limb_clusters = []
	if central:
		# Central trunk up through the crown: thick and nearly even to the first limb, then tapering
		# to top_r at trunk_h. Side limbs leave it in a spiral (137.5 deg apart), lowest ones long
		# and ~55 deg off vertical, higher ones shorter and steeper, each ~60 % of the trunk there.
		low, d = grow(base, (UP + lean).normalized(), P["split_h"] + 0.4, P["trunk_r"], P["trunk_r"] * 0.9, 0, 5, 0.0)
		up, _ = grow(low[-1], d, P["trunk_h"] - P["split_h"], P["trunk_r"] * 0.9, P["top_r"], 0, 12, 0.0)
		az0 = rng.uniform(0, 2 * math.pi)
		for ti, T in enumerate(P.get("tiers", ())):
			# tier limbs; each tier is turned so its limbs sit between the ones below
			j = min(up[:-1], key=lambda q: abs(nodes[q].pos.z - T["h"]))
			az_t = az0 + ti * 2.4
			for k in range(T["n"]):
				az = az_t + k * 2 * math.pi / T["n"] + rng.uniform(-0.35, 0.35)
				length = T["len"] * rng.uniform(0.85, 1.1)
				lr = nodes[j].r * 0.6
				path, ld = grow(j, tilt(UP, math.radians(rng.uniform(*T["tilt"])), az), length, lr, lr * 0.55,
								1, P["lead_segs"], 0.0)
				r = T["cr"] * rng.uniform(0.85, 1.15)
				limb_clusters.append((nodes[path[-1]].pos + UP * 0.25 * r, r, r * T["flat"]))
				mid = nodes[path[len(path) * 3 // 5]].pos
				limb_clusters.append((mid + UP * 0.35 * r, r * 0.6, r * 0.6 * T["flat"]))
		if P.get("tiers"):
			r = P["top_cr"]
			limb_clusters.append((nodes[up[-1]].pos + UP * 0.3 * r, r, r * 0.75))
		for k in range(leader_count):
			t = k / max(1, leader_count - 1)
			j = up[min(len(up) - 2, int(round(t * (len(up) - 3))))]
			tilt_deg = P["limb_tilt"][0] + (P["limb_tilt"][1] - P["limb_tilt"][0]) * t + rng.uniform(-6, 6)
			length = (P["limb_len"][0] + (P["limb_len"][1] - P["limb_len"][0]) * t) * rng.uniform(0.85, 1.1)
			lr = nodes[j].r * 0.62
			path, ld = grow(j, tilt(UP, math.radians(tilt_deg), az0 + k * math.radians(137.5)), length, lr,
							lr * 0.6, 1, P["lead_segs"], 0.02)
			# a cloud cluster sits on the end of every limb: the crown is tiers of limb-end masses
			r = rng.uniform(*P["floret"]) * P["R"] * (1.15 - 0.3 * t)
			limb_clusters.append((nodes[path[-1]].pos + ld * 0.3 * r + UP * 0.15 * r, r, r * 0.75))
		if not P.get("tiers"):
			top = nodes[up[-1]].pos
			r = P["floret"][1] * P["R"]
			limb_clusters.append((top + UP * 0.35 * r, r, r * 0.8))   # crown cluster on the trunk top
		leader_count = 0   # no fan of leaders
	else:
		trunk, td = grow(base, (UP + lean).normalized(), P["split_h"] + 0.4, P["trunk_r"], P["top_r"], 0, 6, 0.0)
	# Leaders fan out of a short fork zone as a chain of Y forks 0.5-0.9 m apart (they never leave
	# from one point); the last one is the near-vertical central leader. Thickness follows the
	# pipe model: n leaders carry the trunk's r^2.5, so each is r_top * n^-0.4 (~0.3 m radius).
	top_r = P["top_r"]
	lr = top_r * max(1, leader_count) ** -0.4 * 1.15
	if not central:
		j, jd = trunk[-1], td
	az0 = rng.uniform(0, 2 * math.pi)
	for i in range(leader_count):
		az = az0 + i * 2 * math.pi / leader_count + rng.uniform(-0.25, 0.25)
		tilt_deg = rng.uniform(*P["central_tilt"]) if i == leader_count - 1 else rng.uniform(*P["lead_tilt"])
		# Start each leader out on the trunk's rim, a little below the top and thick at its base, so
		# the leaders fill the whole trunk top instead of leaving a flat shelf around them.
		lrb = lr * 1.45
		rim_d = max(0.0, nodes[j].r - lrb)         # flush with the trunk surface: no lip / groove
		rim = add(nodes[j].pos + Vector((math.cos(az), math.sin(az), 0)) * rim_d - UP * 0.6, lrb, j, 1)
		grow(rim, tilt(UP, math.radians(tilt_deg), az), P["lead_len"] * rng.uniform(0.85, 1.1), lrb, lr * 0.72,
			 1, P["lead_segs"], 0.03)
		if i < leader_count - 2:
			left = leader_count - 1 - i          # leaders still carried by the trunk above this fork
			r_here = top_r * (left / leader_count) ** 0.4
			path, jd = grow(j, jd, rng.uniform(0.5, 0.9),   # straight: a zig-zag here pinched a crease
							nodes[j].r, r_here, 0, 1, 0.0)
			j = path[-1]

	# Attraction points inside the clusters, so limbs grow into each floret and leave the notches
	# between florets open: the crown outline is a bunch of bumps, not one dome.
	zc, R, H, E = P["zc"], P["R"], P["H"], P["E"]
	florets = make_florets(rng, P, limb_clusters)
	ar = np.random.default_rng(rng.randrange(1 << 30))
	vols = np.array([rh * rh * rv for _, rh, rv in florets])
	per_f = np.maximum(3, (P["attractors"] * vols / vols.sum()).astype(int))
	pts = []
	for (c, rh, rv), k in zip(florets, per_f):
		q = ar.uniform(-1, 1, (k * 6, 3))
		qq = (q ** 2).sum(1)
		q = q[(qq < 1) & (qq > 0.25) & (q[:, 2] > -0.7)][:k]
		pts.append(np.stack([c.x + q[:, 0] * rh, c.y + q[:, 1] * rh,
							 c.z + q[:, 2] * np.where(q[:, 2] > 0, rv, rv * 0.6)], 1))
	attract = np.concatenate(pts)
	alive = np.ones(len(attract), bool)

	# limbs (and, on a central-trunk tree, the trunk inside the crown) sprout the finer limbs
	grow_from = [i for i, n in enumerate(nodes)
				 if (n.depth >= 1 or central) and n.pos.z > P["split_h"] + 1.0]
	step, di2, dk2 = P["step"], P["influence"] ** 2, P["kill"] ** 2
	for _ in range(200):
		if not alive.any():
			break
		gi = np.array(grow_from)
		G = np.array([nodes[i].pos for i in gi])
		A = attract[alive]
		d2 = ((A[:, None, :] - G[None]) ** 2).sum(2)
		near = d2.argmin(1)
		ok = d2[np.arange(len(A)), near] < di2
		if not ok.any():
			break
		pull = {}
		for a, gnode in zip(A[ok], near[ok]):
			v = a - G[gnode]
			pull.setdefault(gnode, np.zeros(3))
			pull[gnode] += v / (np.linalg.norm(v) + 1e-9)
		added = []
		for gnode, v in pull.items():
			parent = int(gi[gnode])
			ppos = nodes[parent].pos
			d = Vector(v).normalized()
			# edges arch over and droop (the elm umbrella)
			rh = math.hypot(ppos.x, ppos.y) / R
			d = (d - UP * P["droop"] * rh * rh + rand_unit(rng) * 0.08).normalized()
			added.append(add(ppos + d * step, 0.0, parent, 2))
		grow_from.extend(added)
		# kill attractors reached by the new nodes
		N = np.array([nodes[i].pos for i in added])
		dd = ((attract[:, None, :] - N[None]) ** 2).sum(2).min(1)
		alive &= dd >= dk2

	# Pipe-model radii for everything above the trunk; leaders keep at least their authored taper.
	TIP_R = 0.10   # 20 cm across at the tips: chunky stylised limbs, never sticks
	PIPE = 2.0     # area-preserving (da Vinci): limbs thicken fast toward the trunk
	smooth_limbs(nodes)
	add_roots(nodes, base, P, random.Random(len(nodes) * 7919 + int(P["R"] * 1000)))

	# A leader that grew no limbs (its part of the crown was claimed by others) would be a bare,
	# flat-cut stump: drop it (radius 0 = not meshed, no leaves).
	feeds = [n.depth == 2 for n in nodes]
	for i in range(len(nodes) - 1, 0, -1):
		if feeds[i]:
			feeds[nodes[i].parent] = True
	child_sum = [0.0] * len(nodes)
	has_child = [False] * len(nodes)
	# Caps (Mark, v37: elm3 had one leader as thick as the trunk ending in a blunt stub). A leader that
	# claims a big share of the crown collects a huge pipe sum; nothing held it to its authored size.
	# Leaders now stay within LEADER_CAP of their authored taper, and a limb is never thicker than
	# LIMB_CAP of the leader it grows from (ancestor radii are known: parents precede children).
	LEADER_CAP, LIMB_CAP = 1.25, 0.85
	authored = [n.r for n in nodes]
	lead_r = [0.0] * len(nodes)
	for i, n in enumerate(nodes):
		if n.depth == 1:
			lead_r[i] = authored[i]
		elif n.depth == 2 and i > 0:
			lead_r[i] = lead_r[n.parent] if lead_r[n.parent] > 0 else authored[n.parent]
	for i in range(len(nodes) - 1, 0, -1):
		n = nodes[i]
		if n.depth == 0:
			continue
		if n.depth == 1 and not feeds[i]:
			n.r = 0.0
			continue
		r_pipe = TIP_R if not has_child[i] else child_sum[i] ** (1 / PIPE)
		# leaders keep their authored heavy taper; the thicker pipe model below them means the
		# limbs they hand off to are close in size, so there is no pinch at the leader end
		if n.depth == 1:
			n.r = min(max(n.r, r_pipe), authored[i] * LEADER_CAP)
		else:
			n.r = min(r_pipe, lead_r[i] * LIMB_CAP) if lead_r[i] > 0 else r_pipe
			n.r = max(n.r, TIP_R)
		child_sum[n.parent] += n.r ** PIPE
		has_child[n.parent] = True
	return nodes, florets


# ---------------------------------------------------------------------------- wood mesh

WOOD_VOXEL = 0.03      # m; thinnest meshed limb is 10 cm across so this keeps every end closed
WOOD_MIN_R = 0.05      # m; thinner twig steps are not meshed (hidden in their leaf clump)
WOOD_FLOOR = -0.15     # m; the wood is squashed flat here - v53 (Mark): the flared trunk continued 0.9 m underground and read as a ball wherever the terrain sat below the origin


def bake(obj, *mods):
	"""Apply modifiers through the depsgraph and swap in the result."""
	for kind, props in mods:
		m = obj.modifiers.new(kind, kind)
		for k, v in props.items():
			setattr(m, k, v)
	dg = bpy.context.evaluated_depsgraph_get()
	baked = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
	obj.modifiers.clear()
	old, obj.data = obj.data, baked
	name = old.name
	bpy.data.meshes.remove(old)
	baked.name = name


def build_wood(name, nodes, rng, P):
	# Rough volume: a tapered 8-sided tube per skeleton segment and a ball at every node, all
	# overlapping. The voxel remesh then fuses them into ONE closed surface (every fork becomes a
	# real crotch, nothing intersects), and a light smooth rounds the crotches off.
	bm = bmesh.new()
	SIDES = 8
	for i, n in enumerate(nodes):
		if n.r < WOOD_MIN_R:
			continue  # the last twig steps live inside the leaf clumps; no wood for them
		if n.parent < 0:
			continue  # v43: no ball at the base node - it bulged out of the ground as a ball around the trunk foot
		ball = bmesh.ops.create_icosphere(bm, subdivisions=1, radius=n.r)
		bmesh.ops.translate(bm, verts=ball["verts"], vec=n.pos)
		p = nodes[n.parent]
		d = (n.pos - p.pos).normalized()
		a1 = d.orthogonal().normalized()
		a2 = d.cross(a1)
		rings = []
		for c, r in ((p.pos, p.r), (n.pos, n.r)):
			rings.append([bm.verts.new(c + (a1 * math.cos(k * 2 * math.pi / SIDES)
											 + a2 * math.sin(k * 2 * math.pi / SIDES)) * r)
						  for k in range(SIDES)])
		for k in range(SIDES):
			k2 = (k + 1) % SIDES
			bm.faces.new((rings[0][k], rings[0][k2], rings[1][k2], rings[1][k]))
		bm.faces.new(list(reversed(rings[0])))
		bm.faces.new(rings[1])
	me = bpy.data.meshes.new(name)
	bm.to_mesh(me)
	bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	bake(obj, ('REMESH', {"mode": 'VOXEL', "voxel_size": WOOD_VOXEL, "adaptivity": 0.0}),
		 ('SMOOTH', {"factor": 0.5, "iterations": 6}))
	# A shoot tip thinner than ~2 voxels can pinch off into a crumb; keep only the main body so
	# the wood is always ONE mesh.
	bm = bmesh.new()
	bm.from_mesh(obj.data)
	parts = []
	seen = set()
	for v in bm.verts:
		if v in seen:
			continue
		part, stack = [], [v]
		seen.add(v)
		while stack:
			c = stack.pop()
			part.append(c)
			for e in c.link_edges:
				o = e.other_vert(c)
				if o not in seen:
					seen.add(o)
					stack.append(o)
		parts.append(part)
	parts.sort(key=len)
	for part in parts[:-1]:
		bmesh.ops.delete(bm, geom=part, context='VERTS')
	bm.to_mesh(obj.data)
	bm.free()

	# Root flare + organic wobble on the dense surface, before decimation.
	ph = rng.uniform(0, 2 * math.pi)
	seed = Vector((rng.uniform(0, 100), rng.uniform(0, 100), rng.uniform(0, 100)))
	for v in obj.data.vertices:
		co = v.co
		wob = noise.noise_vector(co * 0.35 + seed) * 0.05
		if co.z < P["flare_h"]:
			fade = (1 - max(co.z, 0.0) / P["flare_h"]) ** 2.8   # no extra push below ground
			theta = math.atan2(co.y, co.x)
			radial = Vector((co.x, co.y, 0))
			rlen = radial.length
			# round flare with a slight irregular swell (mixed harmonics, never an even 4-lobe
			# square); the roots supply the real buttresses. The push is sized by the trunk and
			# fades out ~1.5 trunk radii from it, so the roots keep their own shape.
			near = min(1.0, max(0.0, 1 - (rlen - P["trunk_r"]) / (P["trunk_r"] * 1.5)))
			lobe = 0.8 + 0.12 * math.cos(2 * theta + ph) + 0.1 * math.cos(3 * theta + 2.1 * ph) 				+ 0.08 * math.cos(5 * theta + 0.7 * ph)
			if rlen > 1e-6:
				co += radial / rlen * (P["trunk_r"] * P["flare"] * fade * lobe * near)
		v.co = co + wob
		if v.co.z < WOOD_FLOOR:
			v.co.z = WOOD_FLOOR

	# Round off the fork zone: where the trunk hands over to its leaders the fused tubes can leave a
	# pinched crease that reads as a gap in the trunk. Extra Laplacian smoothing on that band only.
	bm = bmesh.new()
	bm.from_mesh(obj.data)
	z0, z1 = P["split_h"] - 1.5, P["split_h"] + 2.0
	band = [v for v in bm.verts if z0 < v.co.z < z1 and math.hypot(v.co.x, v.co.y) < P["trunk_r"] * 2.6]
	for _ in range(12):
		bmesh.ops.smooth_vert(bm, verts=band, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
	bm.to_mesh(obj.data)
	bm.free()

	tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
	bake(obj, ('DECIMATE', {"ratio": min(1.0, P["wood_tris"] / tris)}))
	# v73: wood is shade-flat (faceted, Valheim-style) — every triangle takes its own light. The FBX
	# export keeps per-face normals (mesh_smooth_type OFF), so the engine lights it faceted with no
	# shader help (PixelFlatShading stays 0 in the generated vmats).
	obj.data.shade_flat()
	return obj


def bark_uvs(me, nodes, P):
	"""Cylindrical bark UVs, u around a limb and v along its cumulative length, at a fixed texel
	size. Every VERTEX is mapped against the limb segment nearest to it (v66, Mark: "open each limb
	longways instead of slicing it horizontally"): v is the unclamped axis position, u the angle
	around the axis measured from a frame BLENDED between the segment's two end-node frames, so
	along one limb u and v are continuous from base to tip and the only seam is the lengthwise one
	where the wrap meets itself. A face takes the limb most of its corners belong to and re-maps
	the odd corner against that limb's nearest segment, so forks and root junctions show one clean
	seam instead of smeared faces; the old per-face segment choice put a ring at every node.
	The ROOTS (v54) are one world-aligned box projection at the same texel size: side faces take
	(horizontal, height), top / underside faces take (x, y), since wrapping each root around its
	own axis made neighbouring faces pick different segments and the foot broke into herringbone
	patches (Mark). The flare used to share that box projection, which drew a ring where the box
	met the trunk's wrap-around mapping ~2 m up (Mark, v63: "the texture is splitting halfway up");
	now every flare face wraps around the TRUNK segment like the trunk above it, so the bark runs
	unbroken from the ground to the first fork. Each limb gets its own random offset + mirror of
	the pattern so forks never show repeated bark."""
	N = len(nodes)
	# Which child continues its parent's bark: a trunk section (depth 0) always does - a leader can
	# be thicker than the short trunk piece above a fork, and giving the trunk a new offset there
	# drew a hard line across it that read as a crack. Otherwise the thickest child continues.
	first_child = {}
	for i in range(1, N):
		p_ = nodes[i].parent
		cur = first_child.get(p_)
		key = (nodes[i].depth == nodes[p_].depth == 0, nodes[i].r)
		if cur is None or key > (nodes[cur].depth == nodes[p_].depth == 0, nodes[cur].r):
			first_child[p_] = i
	vcoord = [0.0] * N
	ref = [Vector((1, 0, 0))] * N
	for i in range(1, N):  # parents always precede children
		p = nodes[i].parent
		seg = nodes[i].pos - nodes[p].pos
		d = seg.normalized()
		if first_child[p] == i or p == 0:
			vcoord[i] = vcoord[p] + seg.length / BARK_TILE
		else:
			# a branch starts at the height where it joins its parent (leaders start a little below
			# the fork): measure its first step by height so its bark lines up with the parent's
			vcoord[i] = vcoord[p] + seg.z / BARK_TILE
		# Angle frame for u: parallel-transported node to node (rotation-minimising), seeded from world +x
		# at the base. The mapping below blends the two end-node frames along every segment, so the frame
		# is continuous along a limb and the bark never rotates at a node.
		rr = ref[p] - d * ref[p].dot(d)
		if rr.length < 1e-4:
			rr = d.orthogonal()
		ref[i] = rr.normalized()
	# Every limb that branches off gets its own slide of the bark pattern (around + along) and a
	# random mirror, so limbs meeting at a fork never show the same bark side by side; along one
	# limb the offset is inherited, so its bark stays continuous. Node 0's children (trunk + each
	# root) and every fork's side branches all count as new limbs.
	ou, ov, flip = [0.0] * N, [0.0] * N, [1.0] * N
	for i in range(1, N):
		p = nodes[i].parent
		if first_child[p] == i and p != 0:
			ou[i], ov[i], flip[i] = ou[p], ov[p], flip[p]
		elif nodes[p].depth == 0 and nodes[i].depth == 1 and p != 0:
			# leaders growing straight off the trunk keep the trunk's bark where they merge into
			# it (a sideways jump there read as a crack); they diverge as they grow apart
			ou[i], ov[i], flip[i] = ou[p], ov[p], flip[p]
		elif i != 1:                        # node 1 = the trunk: keep it aligned with the flare
			# rotate around the limb + mirror, but keep the parent's along-the-bark position, so
			# the pattern stays aligned where the limb leaves its parent (no ring at forks)
			r_ = random.Random(i * 7919 + 17)
			ou[i], ov[i] = r_.uniform(0, 8), ov[p]
			flip[i] = r_.choice((1.0, -1.0))
	segs = [(nodes[i].parent, i) for i in range(1, N) if nodes[i].r >= WOOD_MIN_R]
	A = np.array([nodes[a].pos for a, _ in segs])
	B = np.array([nodes[b].pos for _, b in segs])
	AB = B - A
	L2 = (AB ** 2).sum(1)
	# One segment per face: the one whose axis is nearest the face centre.
	C = np.array([f.center for f in me.polygons])
	RA = np.array([nodes[a].r for a, _ in segs])
	RB = np.array([nodes[b].r for _, b in segs])
	fseg = np.zeros(len(C), int)
	for c0 in range(0, len(C), 256):  # chunked: thousands of faces x thousands of segments
		AP = C[c0:c0 + 256, None, :] - A[None]
		t = np.clip((AP * AB[None]).sum(2) / L2[None], 0, 1)
		d = np.sqrt(((AP - t[..., None] * AB[None]) ** 2).sum(2))
		fseg[c0:c0 + 256] = d.argmin(1)
	# The base zone: every face on the flare or on a root (a root is a depth-0 chain hanging off
	# node 0 that is not the trunk). Mapped below as one box projection.
	is_root = [False] * N
	for i in range(1, N):
		is_root[i] = (nodes[i].parent == 0 and i != 1) or is_root[nodes[i].parent]
	root_seg = np.array([is_root[b] for _, b in segs])
	near_foot = (C[:, 2] < P["flare_h"] + 0.3) & ((np.hypot(C[:, 0], C[:, 1]) < P["trunk_r"] * 1.9) | root_seg[fseg])
	base_zone = near_foot & root_seg[fseg]               # roots: box projection
	flare_zone = near_foot & ~root_seg[fseg]             # flare: wrap around the trunk segment
	trunk_si = next(k for k, (_, b) in enumerate(segs) if b == 1)
	z0 = nodes[0].pos.z          # the trunk's v starts here, so the box mapping's height lines up with it

	D = AB / np.sqrt(L2)[:, None]
	VA = np.array([vcoord[a] for a, _ in segs])
	VB = np.array([vcoord[b] for _, b in segs])
	REFA = np.array([ref[a] for a, _ in segs])
	REFB = np.array([ref[b] for _, b in segs])
	LIMB = np.array([b for _, b in segs])          # the node whose ou / ov / flip apply
	co = np.array([v.co for v in me.vertices])
	# nearest segment per VERTEX (chunked)
	vseg = np.zeros(len(co), int)
	for c0 in range(0, len(co), 256):
		AP = co[c0:c0 + 256, None, :] - A[None]
		t = np.clip((AP * AB[None]).sum(2) / L2[None], 0, 1)
		d = np.sqrt(((AP - t[..., None] * AB[None]) ** 2).sum(2))
		vseg[c0:c0 + 256] = d.argmin(1)
	# which limb a segment belongs to: the node that was given its own ou / ov / flip above. Walk up
	# while a node inherited its parent's offsets (continuing chain, or a leader off the trunk).
	def inherits(i):
		p_ = nodes[i].parent
		if p_ is None or p_ <= 0:
			return False
		return first_child.get(p_) == i or (nodes[p_].depth == 0 and nodes[i].depth == 1)
	limb_of = {}
	for si, (a_, b_) in enumerate(segs):
		n_ = b_
		while inherits(n_):
			n_ = nodes[n_].parent
		limb_of[si] = n_
	seg_of_limb = {}
	for si in range(len(segs)):
		seg_of_limb.setdefault(limb_of[si], []).append(si)
	# one box frame per limb for the fallback faces: e1 along the limb, e2 horizontal across it, e3 = e1 x e2
	limb_frame = {}
	for limb, sis in seg_of_limb.items():
		d_ = sum((AB[si] for si in sis), np.zeros(3))
		d_ = d_ / (np.linalg.norm(d_) + 1e-9)
		e2 = np.cross(np.array([0.0, 0.0, 1.0]), d_)
		if np.linalg.norm(e2) < 1e-3:
			e2 = np.array([1.0, 0.0, 0.0])
		e2 = e2 / np.linalg.norm(e2)
		limb_frame[limb] = (d_, e2, np.cross(d_, e2))

	def map_vertex(pw, si):
		"""(u, v, period) of world point pw against segment si, frame blended along the segment."""
		Pp = pw - A[si]
		tu = Pp @ AB[si] / L2[si]                      # unclamped: v continues past the segment ends
		tc = min(max(tu, 0.0), 1.0)
		rad = Pp - tu * AB[si]
		Ra = REFA[si] - D[si] * (REFA[si] @ D[si])
		Rb = REFB[si] - D[si] * (REFB[si] @ D[si])
		R = (1 - tc) * Ra + tc * Rb
		if np.linalg.norm(R) < 1e-6:
			R = Rb
		R = R / np.linalg.norm(R)
		S = np.cross(D[si], R)
		ang = math.atan2(rad @ S, rad @ R)
		period = 2 * math.pi * max(np.linalg.norm(rad), 0.02) / BARK_TILE
		return ang / (2 * math.pi) * period, VA[si] + (VB[si] - VA[si]) * tu, period

	def nearest_in_limb(pw, limb):
		best, bsi = 1e9, None
		for si in seg_of_limb[limb]:
			Pp = pw - A[si]
			t = min(max(Pp @ AB[si] / L2[si], 0.0), 1.0)
			d = np.linalg.norm(Pp - t * AB[si])
			if d < best:
				best, bsi = d, si
		return bsi

	uv = me.uv_layers.new(name="UVMap")
	for poly in me.polygons:
		vis = [me.loops[li].vertex_index for li in poly.loop_indices]
		if base_zone[poly.index]:
			# roots: box projection from the face's dominant axis (v54): side faces keep the bark streaks
			# vertical (v = height, continuous with the trunk's v above), tops and undersides take (x, y).
			n = np.array(poly.normal)
			ax = int(np.argmax(np.abs(n)))
			for li, pw in zip(poly.loop_indices, co[vis]):
				if ax == 2:
					uv.data[li].uv = (pw[0] / BARK_TILE + ou[1], pw[1] / BARK_TILE + ov[1])
				else:
					side = pw[1] if ax == 0 else pw[0]
					uv.data[li].uv = (side / BARK_TILE + ou[1], (pw[2] - z0) / BARK_TILE + ov[1])
			continue
		# the limb most corners belong to; the trunk segment for flare faces
		limbs = [limb_of[trunk_si] if flare_zone[poly.index] else limb_of[vseg[vi]] for vi in vis]
		limb = max(set(limbs), key=limbs.count)
		b_ = limb
		us, vs, periods = [], [], []
		for vi, pw in zip(vis, co[vis]):
			si = vseg[vi] if (limb_of[vseg[vi]] == limb and not flare_zone[poly.index]) else nearest_in_limb(pw, limb)
			u_, v_, per = map_vertex(pw, si)
			us.append(u_); vs.append(v_); periods.append(per)
		# keep the face on one side of the angle wrap
		for k_ in range(1, len(us)):
			if us[k_] - us[0] > periods[k_] / 2:
				us[k_] -= periods[k_]
			elif us[0] - us[k_] > periods[k_] / 2:
				us[k_] += periods[k_]
		# faces the wrap cannot cover without smearing get a flat projection onto their own plane instead:
		# a seam beats a smear (v67). Two tests: UV area > 3x off the texel size (fork saddles facing along
		# the limb), and texels more than BARK_MAX_SKEW times longer one way than the other (v70: the lobed
		# flare bulges faster than r * dtheta, which stretched texels sideways at the flare top).
		au = sum((us[k_] - us[0]) * (vs[k_ + 1] - vs[0]) - (us[k_ + 1] - us[0]) * (vs[k_] - vs[0])
				 for k_ in range(1, len(us) - 1)) / 2
		ratio = abs(au) * BARK_TILE * BARK_TILE / max(poly.area, 1e-9)
		skew = 1.0
		if len(us) >= 3:
			n_ = np.array(poly.normal)
			t1_ = co[vis[1]] - co[vis[0]]
			t1_ = t1_ - n_ * (n_ @ t1_)
			if np.linalg.norm(t1_) > 1e-6:
				t1_ = t1_ / np.linalg.norm(t1_)
				t2_ = np.cross(n_, t1_)
				c3_ = co[vis].mean(0)
				Auv = np.array([[u_ - np.mean(us), v_ - np.mean(vs)] for u_, v_ in zip(us, vs)])
				B3 = np.array([[(pw - c3_) @ t1_, (pw - c3_) @ t2_] for pw in co[vis]])
				J_, *_ = np.linalg.lstsq(Auv, B3, rcond=None)
				sv_ = np.linalg.svd(J_, compute_uv=False)
				skew = sv_[0] / max(sv_[1], 1e-9)
		if not (1 / 3 < ratio < 3) or skew > BARK_MAX_SKEW:
			# limb-aligned box projection (v72): every rescued face of a limb projects onto the same two
			# planes (across-limb axis chosen by the face normal, v along the limb), so neighbouring rescued
			# faces line up with each other; per-face planes (v67-v71) made a patchwork at the flare top.
			n = np.array(poly.normal)
			e1, e2, e3 = limb_frame[limb]
			across = e3 if abs(n @ e2) > abs(n @ e3) else e2
			for li, pw in zip(poly.loop_indices, co[vis]):
				uv.data[li].uv = (pw @ across / BARK_TILE + ou[b_], pw @ e1 / BARK_TILE + ov[b_])
			continue
		for li, uu, vv in zip(poly.loop_indices, us, vs):
			uv.data[li].uv = (flip[b_] * uu + ou[b_], vv + ov[b_])


def uv_stretch_report(me):
	"""Share of bark faces whose UV area is > 3x off the ideal (5 cm per texel):
	the smeared / squashed faces that show as messy texture."""
	uvl = me.uv_layers.active.data
	bad = total = 0
	for poly in me.polygons:
		a3 = poly.area
		if a3 < 1e-6:
			continue
		pts = [uvl[li].uv for li in poly.loop_indices]
		au = 0.0
		for k in range(1, len(pts) - 1):
			au += ((pts[k] - pts[0]).cross(pts[k + 1] - pts[0])) / 2
		ratio = au * BARK_TILE * BARK_TILE / a3
		total += 1
		if not (1 / 3 < abs(ratio) < 3):
			bad += 1
	return 100.0 * bad / max(1, total)


def island_count(me):
	bm = bmesh.new()
	bm.from_mesh(me)
	seen, islands = set(), 0
	for v in bm.verts:
		if v.index in seen:
			continue
		islands += 1
		stack = [v]
		seen.add(v.index)
		while stack:
			c = stack.pop()
			for e in c.link_edges:
				o = e.other_vert(c)
				if o.index not in seen:
					seen.add(o.index)
					stack.append(o)
	bm.free()
	return islands


# ---------------------------------------------------------------------------- leaf cards

# Performance (Mark): alpha-tested double-sided cards are the most expensive pixels in the scene (no
# early-z, several stacked layers per pixel), so the count is kept low and the cards big.
# v44 (Mark, Valheim screenshots): "they make a few limbs, 3-5, and then the cards themselves appear
# as if they're limbs". Each card is a branch: its base ON the wood where a fine limb left it, running
# roughly out to where that limb's tips were but tilted off the limb axis at random (CARD_SCATTER),
# face turned up-and-out of its cluster and rolled freely about its own length (CARD_ROLL).
CARD_LEN = (7.0, 11.0)   # m; clamp on the replaced limb's reach (plus the tuft beyond the last tip)
CARD_EXTRA = 1.5         # m added past the farthest tip so the card's end tuft covers it
CARD_ROLL = 80           # deg, random either way about the branch
CARD_SCATTER = 0.5       # random tilt off the limb axis (unit-vector weight); v47's cards all lay along their limbs and left V-shaped sky gaps between the limbs (Mark)


def build_leaves(name, branches, florets, rng, P):
	"""Branch cards: one folded card per (base, direction, reach) from split_limbs, base on the wood,
	fold along the branch, face turned up-and-out of its cluster with a random roll. Cards are
	shaded as part of their floret (normals out from its centre): lit tops, darker undersides."""

	def home(p):
		vals = [floret_value(p, f) for f in florets]
		k = int(np.argmin(vals))
		return k, vals[k]

	verts, faces, uvs = [], [], []
	fold = math.radians(28)

	def card(base, t, n, length):
		side = t.cross(n).normalized()
		w = length * CARD_ASPECT * 0.5
		tip = base + t * length
		lo = side * (-math.cos(fold) * w) + n * (math.sin(fold) * w)
		ro = side * (math.cos(fold) * w) + n * (math.sin(fold) * w)
		i = len(verts)
		verts.extend([base, tip, base + lo, tip + lo, base + ro, tip + ro])
		# left half (u 0..0.5) and right half (u 0.5..1), crease = u 0.5
		faces.append((i + 2, i + 0, i + 1, i + 3))
		uvs.append(((0, 0), (0.5, 0), (0.5, 1), (0, 1)))
		faces.append((i + 0, i + 4, i + 5, i + 1))
		uvs.append(((0.5, 0), (1, 0), (1, 1), (0.5, 1)))

	picked, card_home = [], []
	for base, d0, reach in branches:
		length = min(CARD_LEN[1], max(CARD_LEN[0], reach + CARD_EXTRA))
		# scatter the card off the limb axis so neighbouring fans cross instead of lining up. v52 (Mark,
		# elm1: "all standing directly upward"): no upward bias any more - outer cards may droop a
		# little, but never hang (v49: cards hung below the crown)
		d = (d0 + rand_unit(rng) * CARD_SCATTER + UP * rng.uniform(-0.1, 0.25)).normalized()
		if d.z < -0.2:
			d = Vector((d.x, d.y, -0.2)).normalized()
		mid = base + d * length * 0.5
		k, _ = home(mid)
		o = (mid - florets[k][0]).normalized()
		n0 = o + UP * 0.6
		n0 = n0 - d * n0.dot(d)
		n0 = n0.normalized() if n0.length > 1e-3 else d.orthogonal().normalized()
		roll = math.radians(rng.uniform(-CARD_ROLL, CARD_ROLL))
		n = (n0 * math.cos(roll) + d.cross(n0) * math.sin(roll)).normalized()
		card(base, d, n, length)
		picked += [tuple(mid), tuple(base + d * length)]
		card_home.append(k)

	me = bpy.data.meshes.new(name)
	me.from_pydata(verts, [], faces)
	uvl = me.uv_layers.new(name="UVMap")
	for poly, quv in zip(me.polygons, uvs):
		for li, u in zip(poly.loop_indices, quv):
			uvl.data[li].uv = u
	# Cluster normals: blend each card toward "out from its floret's centre" so every cluster
	# shades as its own soft ball (the broccoli read) instead of hundreds of flickering facets.
	loop_normals = []
	for poly in me.polygons:
		fc = florets[card_home[poly.index // 2]][0]   # two quads per card
		for li in poly.loop_indices:
			q = me.vertices[me.loops[li].vertex_index].co
			fn = poly.normal.copy()
			o = (q - fc).normalized()
			if fn.dot(o) < 0:
				fn = -fn
			loop_normals.append((fn * 0.15 + o * 0.85).normalized())
	try:
		me.normals_split_custom_set(loop_normals)
	except Exception as e:
		print("custom normals skipped:", e)
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	return obj, len(faces) // 2, np.array(picked)


def largest_patch(empty):
	"""Size (cells) of the biggest 4-connected region of True cells."""
	seen = np.zeros_like(empty)
	best = 0
	h, w = empty.shape
	for i in range(h):
		for j in range(w):
			if not empty[i, j] or seen[i, j]:
				continue
			size, stack = 0, [(i, j)]
			seen[i, j] = True
			while stack:
				a, b = stack.pop()
				size += 1
				for c, d in ((a + 1, b), (a - 1, b), (a, b + 1), (a, b - 1)):
					if 0 <= c < h and 0 <= d < w and empty[c, d] and not seen[c, d]:
						seen[c, d] = True
						stack.append((c, d))
			best = max(best, size)
	return best


SIL_ORTHO, SIL_Z = 40, 13   # orthographic side view used for every outline measurement


def outline_score(obj):
	"""Render the crown's silhouette (transparent film, orthographic) from two sides and measure
	each with the same code used on the reference photos. Returns (lumpiness %, deepest dip %) of
	the FLATTER side, so a tree can't pass by being lumpy from one side only."""
	scene = bpy.context.scene
	for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
		try:
			scene.render.engine = eng
			break
		except TypeError:
			pass
	# exactly the framing of the preview "_sil" render, so both measurements agree
	scene.render.resolution_x, scene.render.resolution_y = 900, 1200
	scene.render.film_transparent = True
	cam = bpy.data.objects.new("sil_cam", bpy.data.cameras.new("sil_cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam
	cam.data.type = 'ORTHO'
	cam.data.ortho_scale = SIL_ORTHO
	path = os.path.join(PREVIEW_DIR, "_probe_sil.png")
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	out = []
	for loc, rot in (((0, -80, SIL_Z), (math.radians(90), 0, 0)),
					 ((80, 0, SIL_Z), (math.radians(90), 0, math.radians(90)))):
		cam.location, cam.rotation_euler = loc, rot
		scene.render.filepath = path
		bpy.ops.render.render(write_still=True)
		img = bpy.data.images.load(path)
		a = np.array(img.pixels[:]).reshape(img.size[1], img.size[0], 4)[::-1, :, 3] > 0.5
		bpy.data.images.remove(img)
		tops = np.full(a.shape[1], np.nan)
		for x in range(a.shape[1]):
			run = a[:-2, x] & a[1:-1, x] & a[2:, x]
			idx = np.flatnonzero(run)
			if len(idx):
				tops[x] = idx[0] + 2
		m = measure_outline(tops)
		out.append((m["lumpiness"], m["dip"]))
	bpy.data.objects.remove(cam, do_unlink=True)
	scene.render.film_transparent = False
	os.remove(path)
	return min(out, key=lambda o: o[0] + o[1])


def crown_bald_spot(pts, P):
	"""Biggest bald patch in the crown, in 1.5 m cells: leaf-card positions projected from above
	and from two sides; only cells well inside the crown silhouette count. Small sky gaps are
	fine (the reference photo has them) — one big connected hole is what reads as a bald spot."""
	cell = 1.5
	R, H, zc = P["R"], P["H"], P["zc"]
	n = int(R / cell) + 1
	worst = 0
	# top view: cells inside 85 % of the crown radius
	grid = np.zeros((2 * n, 2 * n), bool)
	ix = np.floor(pts[:, :2] / cell).astype(int) + n
	ok = (ix >= 0).all(1) & (ix < 2 * n).all(1)
	grid[ix[ok, 0], ix[ok, 1]] = True
	inside = np.zeros_like(grid)
	for i in range(2 * n):
		for j in range(2 * n):
			inside[i, j] = math.hypot((i - n + 0.5) * cell, (j - n + 0.5) * cell) < 0.85 * R
	worst = max(worst, largest_patch(inside & ~grid))
	# side views (x-z, y-z): upper-crown cells whose centre-plane envelope value is < 0.55
	nz = int(2 * H / cell) + 1
	if P.get("tiers"):
		return worst   # tiered crowns have sky between layers on purpose: top view only
	for axis in (0, 1):
		g = np.zeros((2 * n, nz), bool)
		a = np.floor(pts[:, axis] / cell).astype(int) + n
		b = np.floor((pts[:, 2] - (zc - H)) / cell).astype(int)
		ok = (a >= 0) & (a < 2 * n) & (b >= 0) & (b < nz)
		g[a[ok], b[ok]] = True
		inside = np.zeros_like(g)
		for i in range(2 * n):
			for j in range(nz):
				cx, cz = (i - n + 0.5) * cell, zc - H + (j + 0.5) * cell
				inside[i, j] = cz > zc - 0.3 * H and envelope((cx, 0, cz), P) < 0.55
		worst = max(worst, largest_patch(inside & ~g))
	return worst


def sky_through(leaves, P):
	"""% of the crown footprint (a disc of 0.85 R, seen straight down, orthographic, transparent film)
	that shows sky through the leaf cards. Mark's "see the sky from beneath" check, printed per tree
	so a density change can be compared against the previous version's numbers."""
	scene = bpy.context.scene
	for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
		try:
			scene.render.engine = eng
			break
		except TypeError:
			pass
	res = 400
	scene.render.resolution_x = scene.render.resolution_y = res
	scene.render.film_transparent = True
	cam = bpy.data.objects.new("sky_cam", bpy.data.cameras.new("sky_cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam
	cam.data.type = 'ORTHO'
	cam.data.ortho_scale = 2 * P["R"]
	cam.location, cam.rotation_euler = (0, 0, P["zc"] + P["H"] + 20), (0, 0, 0)
	path = os.path.join(PREVIEW_DIR, "_probe_sky.png")
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	hidden = [(o, o.hide_render) for o in bpy.data.objects if o.type == 'MESH' and o is not leaves]
	for o, _ in hidden:
		o.hide_render = True
	scene.render.filepath = path
	bpy.ops.render.render(write_still=True)
	for o, h in hidden:
		o.hide_render = h
	img = bpy.data.images.load(path)
	a = np.array(img.pixels[:]).reshape(res, res, 4)[:, :, 3] > 0.5
	bpy.data.images.remove(img)
	yy, xx = np.mgrid[0:res, 0:res]
	disc = np.hypot(xx - res / 2 + 0.5, yy - res / 2 + 0.5) < 0.85 * res / 2
	bpy.data.objects.remove(cam, do_unlink=True)
	scene.render.film_transparent = False
	os.remove(path)
	return 100.0 * (~a & disc).sum() / disc.sum()


# ---------------------------------------------------------------------------- materials

def blender_material(name, png, alpha_clip):
	mat = bpy.data.materials.new(name)
	mat.use_nodes = True
	nt = mat.node_tree
	bsdf = nt.nodes["Principled BSDF"]
	bsdf.inputs["Roughness"].default_value = 0.9
	tex = nt.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(png, check_existing=True)
	tex.interpolation = 'Closest'
	nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	if alpha_clip:
		gt = nt.nodes.new("ShaderNodeMath")
		gt.operation = 'GREATER_THAN'
		gt.inputs[1].default_value = 0.5
		nt.links.new(tex.outputs["Alpha"], gt.inputs[0])
		nt.links.new(gt.outputs[0], bsdf.inputs["Alpha"])
		mat.use_backface_culling = False
		if hasattr(mat, "surface_render_method"):
			mat.surface_render_method = 'DITHERED'
		else:
			mat.blend_method = 'CLIP'
	return mat


VMAT_BARK = """Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.900"
	TextureColor "models/environment/tests/elm_bark.png"
}
"""

# Bark: colour + the crack-net normal map (end grain keeps VMAT_BARK, flat normal).
VMAT_BARK_NORMAL = """Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "1.000"
	PixelNormalStrength "1.000"
	TextureColor "models/environment/tests/elm_bark.png"
	TextureNormal "models/environment/tests/elm_bark_normal.png"
	TextureRoughness "models/environment/tests/elm_bark_rough.png"
}
"""

VMAT_LEAVES = """Layer0
{
	shader "shaders/pixel_lit.shader"
	F_ALPHA_TEST 1
	F_RENDER_BACKFACES 1

	PixelRoughness "0.900"
	PixelAlphaCutoff "0.500"
	PixelNormalUp "0.000"
	TextureColor "models/environment/tests/elm_leaves.png"
	TextureTranslucency "models/environment/tests/elm_leaves_mask.png"
}
"""


# ---------------------------------------------------------------------------- LOD chain

# s&box LODGroupList: (switch_threshold, wood tris, fraction of leaf cards kept, kept-card scale).
# switch_threshold is ModelDoc's LOD switch distance (bigger = farther; compiled as SwitchDistance). The
# citizen uses 5 / 20 / 40 / 70 for a 1.8 m body, so a 25 m tree needs far bigger numbers — 4 / 10 / 24
# put every tree on LOD3 almost at once. Calibrate in ModelDoc with "Set LOD threshold from current
# camera position" and copy the numbers back here.
# Mark's LOD distances for generated models: 100 / 200 / 300 m. The first column is METERS; the vmdl
# switch_threshold is not: thresholds of 100 / 200 / 300 switched in game at ~25 / 50 / 100 m, so the
# engine value is ~4 per meter. vmdl_text multiplies by LOD_THRESHOLD_PER_METER.
LOD_THRESHOLD_PER_METER = 4.0
# Gentle steps so the swap does not pop: each level drops ~30-50 % of the remaining cards (never half
# the crown at once), and kept cards grow only ~kept^-0.35 (partial area compensation) so the pixel
# leaves do not visibly jump in size. Cards are nested (LOD3 subset of LOD2 subset of LOD1): a card
# that survives a swap never moves.
TREE_LODS = [
	(0.0, None, 1.0, 1.0),
	(100.0, 2000, 0.7, 1.13),
	(200.0, 800, 0.4, 1.38),
	(300.0, 300, 0.2, 1.76),
]
# Collision uses this LOD's wood. The wood is 4k tris now (it was 24k when LOD2 was chosen), so the render
# mesh itself is cheap enough: the collider then sits exactly on the bark (Mark, v71: the LOD2 collider sat up to
# 5 cm inside the rendered surface).
TREE_PHYSICS_LOD = 0



def lod_name(name, part, level):
	return f"{name}_{part}" if level == 0 else f"{name}_{part}_lod{level}"


def merged_lod_name(name, level):
	return f"{name}_lod{level}"


def merge_lod_meshes(name, wood, leaves, lod_objs):
	"""One export object per LOD level: copies of that level's wood + leaves joined into <name>_lod<N>
	(one material slot each, custom leaf normals kept by the join).
	The separate wood / leaves objects stay in the .blend (stumps, previews) and the physics
	LOD's wood is exported alongside for the vmdl's PhysicsMeshFile. Returns [merged lod0, lod1, ...]."""
	groups = [(wood, leaves)]
	groups += [(lod_objs[i], lod_objs[i + 1]) for i in range(0, len(lod_objs), 2)]
	merged = []
	for level, group in enumerate(groups):
		parts = []
		for src in group:
			c = src.copy()
			c.data = src.data.copy()
			bpy.context.scene.collection.objects.link(c)
			parts.append(c)
		with bpy.context.temp_override(active_object=parts[0], object=parts[0],
									   selected_objects=parts, selected_editable_objects=parts):
			bpy.ops.object.join()
		m = parts[0]
		m.name = m.data.name = merged_lod_name(name, level)
		merged.append(m)
	return merged


def build_lods(name, wood, leaves, lods, uv_fn=None):
	"""Extra LOD objects (level 1+) as copies of the finished wood / leaves: wood decimated to the
	level's triangle budget (then unwrapped again through uv_fn - UVs carried by the decimate were
	interpolated across collapsed edges and smeared, v70), leaves thinned (cards are 6 verts / 2 quads,
	generated clump by clump) with each kept card scaled about its own centre. Each card gets a fixed
	golden-ratio rank, and a level keeps the cards ranked below its fraction: the kept sets are NESTED
	(nothing reappears or moves at a swap) and evenly spread through every clump.
	Custom (cluster) normals are copied from the source card."""
	made = []
	src = leaves.data
	cards = len(src.polygons) // 2
	uv_src = src.uv_layers[0].data
	corner_n = [tuple(c.vector) for c in src.corner_normals]
	for level, (_, wood_tris, keep, scale) in enumerate(lods):
		if level == 0:
			continue
		w = wood.copy()
		w.data = wood.data.copy()
		w.name = w.data.name = lod_name(name, "wood", level)
		bpy.context.scene.collection.objects.link(w)
		tris = sum(len(p.vertices) - 2 for p in w.data.polygons)
		if wood_tris and tris > wood_tris:
			bake(w, ('DECIMATE', {"ratio": wood_tris / tris}))
			w.data.shade_flat()
			if uv_fn:
				w.data.uv_layers.remove(w.data.uv_layers[0])
				uv_fn(w.data)

		verts, faces, uvs, normals = [], [], [], []
		for c in range(cards):
			if (c * 0.6180339887) % 1.0 >= keep:
				continue
			polys = (src.polygons[c * 2], src.polygons[c * 2 + 1])
			ids = sorted({vi for p in polys for vi in p.vertices})
			centre = sum((src.vertices[i].co for i in ids), Vector()) / len(ids)
			remap = {}
			for i in ids:
				remap[i] = len(verts)
				verts.append(centre + (src.vertices[i].co - centre) * scale)
			for p in polys:
				faces.append(tuple(remap[vi] for vi in p.vertices))
				uvs.append([tuple(uv_src[li].uv) for li in p.loop_indices])
				normals += [corner_n[li] for li in p.loop_indices]
		me = bpy.data.meshes.new(lod_name(name, "leaves", level))
		me.from_pydata(verts, [], faces)
		uvl = me.uv_layers.new(name="UVMap")
		for poly, quv in zip(me.polygons, uvs):
			for li, u in zip(poly.loop_indices, quv):
				uvl.data[li].uv = u
		me.normals_split_custom_set(normals)
		for mat in src.materials:
			me.materials.append(mat)
		lv = bpy.data.objects.new(me.name, me)
		bpy.context.scene.collection.objects.link(lv)
		made += [w, lv]
		print(f"  LOD{level} {name}: wood tris {sum(len(p.vertices) - 2 for p in w.data.polygons)}, "
			  f"cards {len(faces) // 2} of {cards}")
	return made


def vmdl_text(name, lods=((0.0, None, 1.0, 1.0),), physics_lod=0):
	def render_node(obj):
		return f"""					{{
						_class = "RenderMeshFile"
						name = "{obj}"
						filename = "models/environment/tests/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{obj}" ]
						}}
					}},
"""
	physics_mesh = lod_name(name, "wood", physics_lod)
	# One render mesh per level (wood + leaves merged, see merge_lod_meshes): the engine picks a LOD per
	# render mesh from that mesh's own bounds, so separate trunk / crown meshes switched at different distances.
	render_nodes = "".join(render_node(merged_lod_name(name, i)) for i in range(len(lods)))
	lod_groups = ""
	if len(lods) > 1:
		groups = "".join(f"""
					{{
						_class = "LODGroup"
						switch_threshold = {lods[i][0] * LOD_THRESHOLD_PER_METER:.1f}
						meshes =
						[
							"{merged_lod_name(name, i)}",
						]
					}},""" for i in range(len(lods)))
		lod_groups = f"""
			{{
				_class = "LODGroupList"
				children =
				[{groups}
				]
			}},"""
	return f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[
							{{
								from = "elm_bark.vmat"
								to = "models/environment/tests/elm_bark.vmat"
							}},
							{{
								from = "elm_leaves.vmat"
								to = "models/environment/tests/elm_leaves.vmat"
							}}
						]
						use_global_default = false
						global_default_material = "models/environment/tests/elm_bark.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsMeshFile"
						name = "{name}_wood_physics"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						recenter_on_parent_bone = false
						offset_origin = [ 0.0, 0.0, 0.0 ]
						offset_angles = [ 0.0, 0.0, 0.0 ]
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						filename = "models/environment/tests/{name}.fbx"
						import_scale = 0.4
						maxMeshVertices = 0
						qemError = 0.0
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{physics_mesh}" ]
						}}
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
{render_nodes}				]
			}},{lod_groups}
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""


# ---------------------------------------------------------------------------- preview

def render_previews(objs_by_name):
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	scene = bpy.context.scene
	for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
		try:
			scene.render.engine = eng
			break
		except TypeError:
			pass
	scene.render.resolution_x, scene.render.resolution_y = 900, 1200
	world = bpy.data.worlds.new("sky")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.62, 0.9, 1)
	world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
	scene.world = world
	sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
	sun.data.energy = 4.0
	sun.rotation_euler = (math.radians(50), 0, math.radians(35))
	scene.collection.objects.link(sun)
	gm = bpy.data.meshes.new("ground")
	gm.from_pydata([(-80, -80, 0), (80, -80, 0), (80, 80, 0), (-80, 80, 0)], [], [(0, 1, 2, 3)])
	ground = bpy.data.objects.new("ground", gm)
	gmat = bpy.data.materials.new("grass")
	gmat.diffuse_color = (0.25, 0.4, 0.12, 1)
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.25, 0.4, 0.12, 1)
	gm.materials.append(gmat)
	scene.collection.objects.link(ground)
	# 1.8 m reference person
	bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=1.8, location=(4, -3, 0.9))
	person = bpy.context.active_object
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	cam.data.lens = 24
	scene.collection.objects.link(cam)
	scene.camera = cam
	for name, objs in objs_by_name.items():
		for other, oo in objs_by_name.items():
			for o in oo:
				o.hide_render = other != name
		cam.location = (0, -44, 1.7)
		cam.rotation_euler = (math.radians(106), 0, 0)
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + ".png")
		bpy.ops.render.render(write_still=True)
		print("RENDER", scene.render.filepath)
		# silhouette for measure_crown_silhouette.py: orthographic side view, no ground or person
		ground.hide_render = person.hide_render = True
		cam.data.type = 'ORTHO'
		cam.data.ortho_scale = SIL_ORTHO
		cam.location = (0, -80, SIL_Z)
		cam.rotation_euler = (math.radians(90), 0, 0)
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_sil.png")
		bpy.ops.render.render(write_still=True)
		cam.data.type = 'PERSP'
		ground.hide_render = person.hide_render = False
		# top-down: bald-spot check by eye
		cam.location = (0, 0, 80)
		cam.rotation_euler = (0, 0, 0)
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_top.png")
		bpy.ops.render.render(write_still=True)
		# bark up close: texels should read as chunky pixels
		cam.location = (0, -3.2, 1.6)
		cam.rotation_euler = (math.radians(88), 0, 0)
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_bark.png")
		bpy.ops.render.render(write_still=True)
		# looking up into the crown from under it (see-through check)
		cam.location = (4, -6, 1.7)
		cam.rotation_euler = (math.radians(150), 0, math.radians(-20))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_up.png")
		bpy.ops.render.render(write_still=True)
		# the base up close: flare + roots, where the bark has to read as one pattern
		cam.location = (1.8, -5.2, 1.9)
		cam.rotation_euler = (math.radians(68), 0, math.radians(18))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_base.png")
		bpy.ops.render.render(write_still=True)
		# roots against the ground: arches clear of the dirt, sections sunk under it
		cam.location = (1.5, -13, 3.2)
		cam.rotation_euler = (math.radians(74), 0, math.radians(6))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_roots.png")
		bpy.ops.render.render(write_still=True)
		# plan view of the root spread: orthographic, from 3.5 m up, so the crown is behind the camera
		cam.data.type = 'ORTHO'
		cam.data.ortho_scale = 24
		cam.location = (0, 0, 3.5)
		cam.rotation_euler = (0, 0, 0)
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_rootplan.png")
		bpy.ops.render.render(write_still=True)
		cam.data.type = 'PERSP'
		# close-up: fork, bark and folded leaf cards
		cam.location = (3.5, -9, 9)
		cam.rotation_euler = (math.radians(75), 0, math.radians(20))
		scene.render.filepath = os.path.join(PREVIEW_DIR, name + "_close.png")
		bpy.ops.render.render(write_still=True)
	bpy.data.objects.remove(person, do_unlink=True)
	for o in (sun, ground, cam):
		bpy.data.objects.remove(o, do_unlink=True)


def render_pieces(pieces, log, half):
	"""The felling pieces: elm2's stump with the log fallen beside it and the two halves, and the
	log standing on its point on the stump as it spawns."""
	scene = bpy.context.scene
	for o in bpy.data.objects:
		if o.type == 'MESH':
			o.hide_render = True
	stump = next(o for o in pieces if o.name.startswith("environment_elm2_stump"))
	stump.hide_render = False
	log.hide_render = False
	log.location, log.rotation_euler = (1.5, -0.5, fell.LOG_R), (0, math.radians(90), math.radians(10))
	h2 = half.copy()
	scene.collection.objects.link(h2)
	for o, loc in ((half, (1.0, -3.2, fell.LOG_R)), (h2, (5.8, -3.6, fell.LOG_R))):
		o.hide_render = False
		o.location, o.rotation_euler = loc, (0, math.radians(90), math.radians(-8))
	world = bpy.data.worlds.new("sky")
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.62, 0.9, 1)
	scene.world = world
	sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
	sun.data.energy = 4.0
	sun.rotation_euler = (math.radians(50), 0, math.radians(35))
	scene.collection.objects.link(sun)
	gm = bpy.data.meshes.new("ground")
	gm.from_pydata([(-80, -80, 0), (80, -80, 0), (80, 80, 0), (-80, 80, 0)], [], [(0, 1, 2, 3)])
	ground = bpy.data.objects.new("ground", gm)
	gmat = bpy.data.materials.new("grass")
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.25, 0.4, 0.12, 1)
	gm.materials.append(gmat)
	scene.collection.objects.link(ground)
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	cam.data.lens = 28
	scene.collection.objects.link(cam)
	scene.camera = cam
	cam.location = (3.5, -14, 5.5)
	cam.rotation_euler = (math.radians(68), 0, math.radians(8))
	scene.render.resolution_x, scene.render.resolution_y = 1200, 800
	scene.render.filepath = os.path.join(PREVIEW_DIR, f"felling_pieces_v{VERSION}.png")
	bpy.ops.render.render(write_still=True)
	log.location, log.rotation_euler = (0, 0, fell.STUMP_H), (0, 0, 0)
	for o in (half, h2):
		o.hide_render = True
	cam.location = (0, -16, 5)
	cam.rotation_euler = (math.radians(80), 0, 0)
	scene.render.resolution_x, scene.render.resolution_y = 800, 1000
	scene.render.filepath = os.path.join(PREVIEW_DIR, f"felling_spawn_v{VERSION}.png")
	bpy.ops.render.render(write_still=True)


# ---------------------------------------------------------------------------- main

def remove_old_versions():
	"""Delete every elm model / material file (source, .meta, compiled) not of this VERSION."""
	import glob
	tag = f"_v{VERSION}"
	old = []
	for d in [MODEL_DIR] + OLD_DIRS:
		old += glob.glob(os.path.join(d, "environment_elm*")) + glob.glob(os.path.join(d, "elm_*"))
	old = [f for f in set(old) if "sapling" not in os.path.basename(f)
		   and (tag not in os.path.basename(f) or os.path.dirname(f) != MODEL_DIR)]
	old += [f for f in glob.glob(os.path.join(os.path.dirname(BLEND_OUT), "environment_elm*.blend*"))
			if tag not in os.path.basename(f) and "sapling" not in os.path.basename(f)]
	old += [f for f in glob.glob(os.path.join(PREVIEW_DIR, "*.png")) if tag not in os.path.basename(f)]
	old += [f for f in glob.glob(os.path.join(PREFAB_DIR, "environment_elm*"))
			if tag not in os.path.basename(f) and "sapling" not in os.path.basename(f)]
	locked = []
	for f in old:
		try:
			os.remove(f)
		except PermissionError:
			locked.append(f)      # the s&box editor holds compiled files open; they go next run
	print(f"removed {len(old) - len(locked)} files from older elm versions"
		  + (f"; LOCKED (editor open), delete later: {[os.path.basename(f) for f in locked]}" if locked else ""))


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0

	remove_old_versions()
	bark_png = os.path.join(MAT_DIR, BARK + ".png")
	leaf_png = os.path.join(MAT_DIR, LEAVES + ".png")
	bark_rgba, bark_h = make_bark_texture()
	write_png(bark_png, bark_rgba)
	write_png(os.path.join(MAT_DIR, BARK + "_normal.png"), make_bark_normal(bark_h))
	write_png(os.path.join(MAT_DIR, BARK + "_rough.png"), make_bark_roughness(bark_h))
	leaf_rgba = make_leaf_texture()
	write_png(leaf_png, leaf_rgba, LEAF_BLOCK)
	# complex.shader's alpha test reads TextureTranslucency, not the colour PNG's alpha
	mask = np.repeat(leaf_rgba[..., 3:4], 4, axis=2)
	mask[..., 3] = 255
	write_png(os.path.join(MAT_DIR, LEAVES + "_mask.png"), mask, LEAF_BLOCK)
	with open(os.path.join(MAT_DIR, BARK + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT_BARK_NORMAL.replace("elm_bark", BARK))
	with open(os.path.join(MAT_DIR, LEAVES + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT_LEAVES.replace("elm_leaves", LEAVES))
	bark = blender_material(BARK, bark_png, False)
	leaves_mat = blender_material(LEAVES, leaf_png, True)
	grain_png = os.path.join(MAT_DIR, GRAIN + ".png")
	write_png(grain_png, fell.make_endgrain_texture(TEX))
	with open(os.path.join(MAT_DIR, GRAIN + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT_BARK.replace("elm_bark", GRAIN))
	grain = blender_material(GRAIN, grain_png, False)

	def export(objs, fname, vmdl):
		for o in bpy.data.objects:
			o.select_set(False)
		for o in objs:
			o.select_set(True)
		bpy.context.view_layer.objects.active = objs[0]
		bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, fname + ".fbx"), use_selection=True,
								 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
								 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
		with open(os.path.join(MODEL_DIR, fname + ".vmdl"), "w", newline="\n") as f:
			f.write(vmdl)

	# the one universal log + its half (shared by every tree)
	log = fell.build_log(LOG, fell.LOG_LEN, fell.LOG_R, fell.LOG_POINT, BARK_TILE, bark, grain, 5)
	half = fell.build_log(HALF, fell.HALF_LEN, fell.LOG_R, 0.0, BARK_TILE, bark, grain, 6)
	for v in half.data.vertices:
		v.co.z -= fell.HALF_LEN / 2                 # half: origin at its centre
	export([log], LOG, fell.single_vmdl(LOG, ASSET_DIR, [BARK, GRAIN], "hull"))
	export([half], HALF, fell.single_vmdl(HALF, ASSET_DIR, [BARK, GRAIN], "hull"))
	fell.write_prefab(os.path.join(PREFAB_DIR, HALF + ".prefab"), HALF, f"{ASSET_DIR}/{HALF}.vmdl",
					  {"MaxHealth": fell.HALF_HP, "CurrentHealth": fell.HALF_HP,
					   "WoodDropMin": fell.HALF_WOOD[0], "WoodDropMax": fell.HALF_WOOD[1]}, True, fell.HALF_MASS)
	fell.write_prefab(os.path.join(PREFAB_DIR, LOG + ".prefab"), LOG, f"{ASSET_DIR}/{LOG}.vmdl",
					  {"MaxHealth": fell.LOG_HP, "CurrentHealth": fell.LOG_HP, "WoodDropMin": 0, "WoodDropMax": 0,
					   "SplitPiecePrefab": f"{PREFAB_REL}/{HALF}.prefab",
					   "SplitPieceOffsetMeters": fell.HALF_LEN / 2,
					   "SplitCenterOffsetMeters": fell.LOG_LEN / 2}, True, fell.LOG_MASS)
	pieces = [log, half]

	made = {}
	for name, seed0, P, leaders in VARIANTS:
		# Deterministic seed search over 8 seeds: no bald patch first, then the crown outline closest
		# to the reference photos' lumpiness and dip.
		scores = []
		for seed in range(seed0, seed0 + 8):
			rng = random.Random(seed)
			nodes, florets = build_skeleton(rng, P, leaders)
			_, branches = split_limbs(nodes, P, florets, rng)
			leaves, cards, pts = build_leaves(name + "_probe", branches, florets, rng, P)
			lump, dip = outline_score(leaves)
			bald = crown_bald_spot(pts, P)
			scores.append((bald, abs(lump - LUMP_TARGET) + abs(dip - DIP_TARGET), seed, lump, dip, len(florets)))
			bpy.data.meshes.remove(leaves.data)
		holes, _, seed, lump, dip, nfl = min(scores)
		print(f"  {name}: (bald, outline error, seed, lump %, dip %, clusters) {[tuple(round(v, 2) for v in s_) for s_ in scores]}")
		rng = random.Random(seed)
		nodes, florets = build_skeleton(rng, P, leaders)
		nodes, branches = split_limbs(nodes, P, florets, rng)
		leaves, cards, pts = build_leaves(name + "_leaves", branches, florets, rng, P)
		wood = build_wood(name + "_wood", nodes, rng, P)
		bark_uvs(wood.data, nodes, P)
		wood.data.materials.append(bark)
		leaves.data.materials.append(leaves_mat)
		sky = sky_through(leaves, P)
		made[name] = (wood, leaves)
		zs = [v.co.z for v in leaves.data.vertices] + [v.co.z for v in wood.data.vertices]
		xs = [v.co.x for v in leaves.data.vertices]
		ys = [v.co.y for v in leaves.data.vertices]
		wtris = sum(len(p.vertices) - 2 for p in wood.data.polygons)
		# trunk across at breast height (1.3 m), measured on the finished mesh
		ring = [v.co for v in wood.data.vertices if 0.9 < v.co.z < 1.7] or [Vector()]   # decimated wood can have no ring at 1.3 m exactly
		dbh = (max(c.x for c in ring) - min(c.x for c in ring) + max(c.y for c in ring) - min(c.y for c in ring)) / 2
		print(f"TREE {name} clusters {nfl}, outline lump {lump:.2f} % dip {dip:.2f} %, wood nodes {len(nodes)} (seed {seed}, biggest bald patch {holes} cells, sky through crown {sky:.1f} %): height {max(zs):.1f} m, crown {max(xs) - min(xs):.1f} x {max(ys) - min(ys):.1f} m, "
			  f"trunk {dbh:.2f} m across at 1.3 m, wood islands {island_count(wood.data)}, bad bark UV faces {uv_stretch_report(wood.data):.1f} %, wood tris {wtris}, cards {cards} ({cards * 4} tris)")

		lod_objs = build_lods(name, wood, leaves, TREE_LODS, lambda me: bark_uvs(me, nodes, P))
		merged = merge_lod_meshes(name, wood, leaves, lod_objs)
		phys = wood if TREE_PHYSICS_LOD == 0 else lod_objs[(TREE_PHYSICS_LOD - 1) * 2]
		for o in bpy.data.objects:
			o.select_set(False)
		for o in merged + [phys]:
			o.select_set(True)
		bpy.context.view_layer.objects.active = merged[0]
		bpy.ops.export_scene.fbx(filepath=os.path.join(MODEL_DIR, name + ".fbx"), use_selection=True,
								 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
								 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
		with open(os.path.join(MODEL_DIR, name + ".vmdl"), "w", newline="\n") as f:
			f.write(vmdl_text(name, TREE_LODS, TREE_PHYSICS_LOD)
					.replace("elm_bark", BARK).replace("elm_leaves", LEAVES))
		for o in lod_objs + merged:
			o.hide_render = True
			o.hide_set(True)

		# stump: this tree's own base cut at STUMP_H, and the tree prefab that fells into it
		sname = name.replace(f"_v{VERSION}", f"_stump_v{VERSION}")
		stump = fell.build_stump(sname, wood, grain)
		export([stump], sname, fell.single_vmdl(sname, ASSET_DIR, [BARK, GRAIN], "mesh"))
		fell.write_prefab(os.path.join(PREFAB_DIR, name + ".prefab"), name, f"{ASSET_DIR}/{name}.vmdl",
						  {"MaxHealth": fell.TREE_HP, "CurrentHealth": fell.TREE_HP,
						   "StumpModel": f"{ASSET_DIR}/{sname}.vmdl", "StumpTopMeters": fell.STUMP_H,
						   "FelledLogPrefab": f"{PREFAB_REL}/{LOG}.prefab", "StumpHealth": fell.STUMP_HP,
						   "StumpWoodMin": fell.STUMP_WOOD[0], "StumpWoodMax": fell.STUMP_WOOD[1],
						   "WoodDropMin": 0, "WoodDropMax": 0}, False)
		pieces.append(stump)

	for o in pieces:
		o.hide_render = True
	# Line the variants up in the .blend for review.
	for i, objs in enumerate(made.values()):
		for o in objs:
			o.location.x = (i - 2.5) * 34
	bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
	if "render" in ARGS:
		for objs in made.values():
			for o in objs:
				o.location.x = 0
		render_previews({n: list(v) for n, v in made.items()})
		render_pieces(pieces, log, half)


if __name__ == "__main__":   # importable: create_elm_sapling.py reuses the textures / helpers
	main()
