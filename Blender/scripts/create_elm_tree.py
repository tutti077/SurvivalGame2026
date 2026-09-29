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
    <name>_wood    ONE continuous closed mesh: trunk -> leaders -> space-colonised limbs. Each
                   skeleton segment is a tapered tube with a ball at every joint; a voxel remesh
                   fuses them into one surface (real crotches, nothing intersecting), then it is
                   decimated, smooth shaded, and bark UVs run along each limb. Limb tips are 20 cm
                   across (stylised, never stick-thin).
    <name>_leaves  folded leaf cards in clumps on the limb ends: each card is two quads hinged on a
                   centre crease, the 64x64 texture holds a twig with alternate (two-ranked) elm
                   leaves, one rank per half of the fold. Cards are 1.8 m (stylised ~30 cm
                   leaves, ~3 cm per texel) so the crown blocks the sky. No collision on leaves (vmdl physics = wood only).

Textures (64x64 designs, saved as 512x512 nearest-neighbour blocks so pixels stay hard):
    Assets/models/environment/tests/elm_bark_v<N>.png        grey-brown ridged bark + lichen (7-tone ramp), 5 cm per texel
    Assets/models/environment/tests/elm_leaves_v<N>.png      big flat-toned lime leaves, midrib + faint veins, no baked light
    Assets/models/environment/tests/elm_leaves_v<N>_mask.png cutout mask (complex.shader TextureTranslucency)
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
VERSION = 34
BARK = f"elm_bark_v{VERSION}"
GRAIN = f"elm_endgrain_v{VERSION}"
LOG = f"environment_elm_log_v{VERSION}"
HALF = f"environment_elm_loghalf_v{VERSION}"
ASSET_DIR = "models/environment/tests"
PREFAB_DIR = os.path.join(ROOT, "Assets", "prefabs", "environment", "tests")
PREFAB_REL = "prefabs/environment/tests"
PREFAB_TEMPLATE = os.path.join(ROOT, "Assets", "prefabs", "environment", "temp_tree_2.prefab")
LEAVES = f"elm_leaves_v{VERSION}"
BLEND_OUT = os.path.join(ROOT, "Blender", "blenderprojects", f"environment_elm_v{VERSION}.blend")
PREVIEW_DIR = os.path.join(ROOT, "Blender", "blenderprojects", "elm_preview")
ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

TEX = 64
# The engine's complex.shader samples bilinearly no matter what the vmat asks for, so the 64 px
# design is written nearest-neighbour upscaled: each design pixel becomes a PIXEL_BLOCK square
# block, and bilinear only softens a 1/8-texel seam between blocks -> hard, visible pixels.
PIXEL_BLOCK = 8
BARK_TILE = 3.2         # m of bark per 64 px tile = 5 cm per texel on every limb (chunky pixels)
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
		base_r=1.05 * s, trunk_r=0.80 * s, top_r=0.86 * s, split_h=split * s,   # swells into the fork
		flare_h=2.6 * s, flare=0.8,
		lead_tilt=(26, 46), central_tilt=(5, 14), lead_len=7.0 * s, lead_segs=6,
		zc=15.5 * s, R=R, H=6.5 * s, E=E,
		floret=(0.28, 0.42),                  # cluster radius range, fraction of R
		attractors=int(15 * R * R), step=0.7, influence=0.42 * R, kill=1.5,
		droop=0.28, cards=int(45 * R * R), wood_tris=24000,
	)


def central(s, limbs_from=4.8):
	"""Central-trunk tree (Mark's concept art, tree 6): the trunk runs up through the crown to
	~80 % of the height, tapering, and heavy side limbs leave it all the way up in a spiral - long
	and ~45 deg off vertical low down, shorter and steeper (more vertical) near the top - each
	ending in its own cloud cluster, so the crown sits in tiers around the trunk."""
	R = 10.0 * s
	return dict(
		form="central",
		base_r=1.10 * s, trunk_r=0.80 * s, top_r=0.34 * s, split_h=limbs_from * s,
		trunk_h=18.0 * s,                     # where the central trunk ends inside the crown
		flare_h=3.0 * s, flare=1.0,           # spreading root buttresses, as in the concept
		limb_tilt=(45, 22),                   # deg off vertical: lowest limb .. highest limb
		limb_len=(0.85 * R, 0.45 * R), lead_segs=6,
		zc=14.0 * s, R=R, H=9.0 * s, E=2.2,
		floret=(0.28, 0.40),
		attractors=int(15 * R * R), step=0.7, influence=0.42 * R, kill=1.5,
		droop=0.10, cards=int(45 * R * R), wood_tris=26000,
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
		base_r=trunk_r * 1.35, trunk_r=trunk_r, top_r=trunk_r * 0.42,
		split_h=min(T["h"] for T in tiers) * 0.8, trunk_h=trunk_h,
		flare_h=3.0, flare=1.0, lead_segs=6,
		zc=(zlo + zhi) / 2, R=R, H=(zhi - zlo) / 2, E=2.6,
		floret=(0.28, 0.40),
		attractors=int(5 * area), step=0.7, influence=max(4.0, 0.4 * R), kill=1.5,
		droop=0.12, cards=int(13 * area), wood_tris=24000,
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
BARK_RAMP = [(44, 38, 33), (62, 55, 48), (82, 74, 65), (102, 94, 83), (122, 114, 102), (142, 135, 122), (166, 160, 148)]
LICHEN = [(104, 116, 84), (132, 144, 106), (160, 170, 132)]
LEAF_RAMP = [(40, 66, 28), (62, 94, 32), (90, 124, 36), (122, 152, 44), (156, 180, 58), (190, 204, 84), (222, 226, 130)]


def make_bark_texture():
	"""Grey-brown elm bark with lichen, 7-tone ramp. Interlacing ridge strips between meandering
	furrows; each strip shaded as a rounded ridge lit from the left, with fibre streaks, short
	cracks with a lit lip, pale lichen blotches, and furrows kept two tones up (never near-black).
	Tiles seamlessly."""
	rng = np.random.default_rng(11)
	N = 11
	yy = (np.arange(TEX) + 0.5) / TEX
	lines = np.zeros((N, TEX))
	for k in range(N):
		off = np.zeros(TEX)
		for m in (1, 2, 3):
			off += rng.uniform(0.4, 1.0) / m * np.sin(2 * math.pi * (m * yy + rng.random()))
		lines[k] = (k + 0.5) * TEX / N + off * TEX / N * 0.55
	px = np.arange(TEX) + 0.5
	sd = px[None, None, :] - lines[:, :, None]
	sd -= np.round(sd / TEX) * TEX
	left_d = np.where(sd > 0, sd, np.inf).min(0)       # distance to the furrow on the left
	right_d = np.where(sd < 0, -sd, np.inf).min(0)     # ... and on the right
	left_k = np.where(sd > 0, sd, np.inf).argmin(0)
	s = left_d / (left_d + right_d)                    # 0 at the left furrow .. 1 at the right
	dist = np.minimum(left_d, right_d)
	yi = np.arange(TEX)[:, None].repeat(TEX, 1)
	xi = np.arange(TEX)[None, :].repeat(TEX, 0)
	shade = 0.78 - 0.55 * s ** 1.3 + 0.12 * np.sin(math.pi * s)          # rounded ridge
	shade += (rng.random((N, 4)) - 0.5)[left_k, yi // 16] * 0.16         # plates differ a little
	fib = rng.random((TEX // 3 + 1, TEX)) < 0.22                          # fibre streaks
	shade = np.where(fib[yi // 3, xi] & (dist > 1.2), shade - 0.12, shade)
	crack_rows = rng.random((N, TEX // 8)) < 0.12                         # short cracks + lit lip
	crack = crack_rows[left_k, yi // 8] & ((yi % 8) == 3) & (dist > 1.0)
	lip = crack_rows[left_k, yi // 8] & ((yi % 8) == 2) & (dist > 1.0)
	shade = np.where(crack, 0.3, np.where(lip, shade + 0.12, shade))
	shade += rng.uniform(-0.03, 0.03, shade.shape)
	idx = np.clip(np.round(np.clip(shade, 0, 1) * 6), 0, 6).astype(int)
	idx = np.where(dist < 0.6, 2, np.where(dist < 1.3, np.minimum(idx, 3), idx))   # furrows
	rgb = np.array(BARK_RAMP, float)[idx]
	r2 = random.Random(3)
	x, y = np.meshgrid(np.arange(TEX) / TEX, np.arange(TEX) / TEX)
	blot = tile_noise(x, y, r2, 8, 3)
	lich = (blot > 0.45) & (dist > 1.4) & (rng.random((TEX, TEX)) < 0.8)
	lt = np.clip((shade * 3).astype(int), 0, 2)
	rgb[lich] = np.array(LICHEN, float)[lt[lich]]
	return np.dstack([rgb, np.full((TEX, TEX), 255)]).astype(np.uint8)


def make_leaf_texture():
	"""Twig up the middle (the card's fold line), alternate elm leaves on both sides, sunlit-lime
	ramp. No baked lighting (the game lights the cards): each leaf is one flat tone with a little
	per-leaf variation, a paler midrib and faint darker lateral veins; small serration teeth."""
	rng = random.Random(9)
	px, py = np.meshgrid((np.arange(TEX) + 0.5) / TEX, 1.0 - (np.arange(TEX) + 0.5) / TEX)
	tone = np.full((TEX, TEX), -1)
	leaves = []
	count = 3
	for i in range(count):
		for side in (-1, 1):
			t = i / (count - 1)
			ay = 0.03 + t * 0.52 + (0.10 if side > 0 else 0.0)
			ang = math.pi / 2 - side * math.radians(55 - 20 * t + rng.uniform(-5, 5))
			length = 0.60 - 0.12 * t + rng.uniform(-0.03, 0.03)
			leaves.append((ay, 0.5 + 0.012 * side, ang, length))
	leaves.sort()
	leaves.append((0.62, 0.5, math.pi / 2 + rng.uniform(-0.1, 0.1), 0.37))   # terminal leaf
	for ay, ax, ang, length in leaves:
		width = length * 0.54
		d = np.array([math.cos(ang), math.sin(ang)])
		p = np.array([-d[1], d[0]])
		rx, ry = px - ax, py - ay
		a = (rx * d[0] + ry * d[1]) / length
		b = (rx * p[0] + ry * p[1]) / (width * 0.5)
		a_side = np.where(b > 0, (a - 0.07) / 0.93, a)                    # elm: lopsided base
		prof = np.clip(a_side, 0, 1) ** 0.55 * np.clip(1 - a, 0, 1) ** 0.85 / (0.39 ** 0.55 * 0.61 ** 0.85)
		prof = prof * (1 - 0.07 * (np.sin(a * 38) > 0.3))
		inside = (a > 0) & (a < 1) & (np.abs(b) < prof)
		base = rng.choice((3, 4, 4, 5))
		leaf = np.where(np.abs(b) < 0.1, base + 1, np.full((TEX, TEX), base))
		vein = (np.mod(a * 6.5 - np.abs(b) * 1.3, 1.0) < 0.14) & (np.abs(b) > 0.2) & (np.abs(b) < prof - 0.15)
		leaf = np.where(vein, base - 1, leaf)
		tone[inside] = np.clip(leaf, 0, 6)[inside]
	stem = (np.abs(px - 0.5) < 0.016) & (py < 0.80)
	ramp = np.array(LEAF_RAMP, float)
	rgb = ramp[np.clip(tone, 0, 6)]
	rgb[stem] = (84, 70, 44)
	alpha = (tone >= 0) | stem
	rgb[~alpha] = ramp[3]              # bleed colour under the cutout, never black
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
		n.r = max(n.r, r_pipe) if n.depth == 1 else r_pipe
		child_sum[n.parent] += n.r ** PIPE
		has_child[n.parent] = True
	return nodes, florets


# ---------------------------------------------------------------------------- wood mesh

WOOD_VOXEL = 0.03      # m; thinnest meshed limb is 10 cm across so this keeps every end closed
WOOD_MIN_R = 0.05      # m; thinner twig steps are not meshed (hidden in their leaf clump)


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
		ball = bmesh.ops.create_icosphere(bm, subdivisions=1, radius=n.r)
		bmesh.ops.translate(bm, verts=ball["verts"], vec=n.pos)
		if n.parent < 0:
			continue
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
			fade = (1 - max(co.z, -0.4) / P["flare_h"]) ** 2.8
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
	obj.data.shade_smooth()
	return obj


def bark_uvs(me, nodes, P):
	"""Cylindrical bark UVs, u around a limb and v along its cumulative length, at a fixed
	5 cm per texel. Every FACE takes one limb segment (the one nearest its centre) and maps all of
	its corners against that segment's axis, so a face never mixes two limbs: where limbs fork or
	roots leave the trunk the bark just changes direction at a clean seam instead of smearing one
	face across two different parts of the texture. Along one limb the frames are parallel-
	transported and v uses the unclamped axis position, so neighbouring faces still line up.
	Faces that point along their limb (fork saddles, the flare) get a flat projection at the
	same texel size instead, since no wrap-around mapping can cover them without stretching.
	The whole trunk flare maps off the trunk axis as one surface. Each limb / root gets its own
	random offset + mirror of the pattern so forks never show repeated bark."""
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
	# The flare around the trunk base maps as ONE surface off the trunk's first segment (node 0 ->
	# node 1): otherwise faces there flip between trunk, root and flat mappings and the base turns
	# into a patchwork of short seams. Roots take their own mapping once clear of the flare.
	# Flare faces map off the TRUNK's own segments (nearest one along the trunk chain), not one
	# straight line from the ground: the trunk wanders a little, and a single straight axis drifted
	# off it by the top of the flare, drawing a ring across the trunk where the mappings met.
	trunk_segs = np.array([k for k, (a, b) in enumerate(segs)
						   if nodes[b].depth == 0 and b != 0 and math.hypot(nodes[b].pos.x, nodes[b].pos.y) < P["trunk_r"]
						   and nodes[b].pos.z > 0.0])
	flare_zone = (np.hypot(C[:, 0], C[:, 1]) < P["trunk_r"] * 1.9) & (C[:, 2] < P["flare_h"] + 0.3)
	own_seg = fseg.copy()          # nearest segment, used if the flare mapping squashes a face
	if len(trunk_segs):
		Cz = C[flare_zone]
		AP = Cz[:, None, :] - A[trunk_segs][None]
		t = np.clip((AP * AB[trunk_segs][None]).sum(2) / L2[trunk_segs][None], 0, 1)
		near = ((AP - t[..., None] * AB[trunk_segs][None]) ** 2).sum(2).argmin(1)
		fseg[flare_zone] = trunk_segs[near]

	D = AB / np.sqrt(L2)[:, None]
	R0 = np.array([ref[b] for _, b in segs])
	S0 = np.cross(D, R0)
	VA = np.array([vcoord[a] for a, _ in segs])
	VB = np.array([vcoord[b] for _, b in segs])
	co = np.array([v.co for v in me.vertices])
	uv = me.uv_layers.new(name="UVMap")
	def stretch_ok(u, v, area, lo=1 / 3, hi=3):
		au = sum((u[k] - u[0]) * (v[k + 1] - v[0]) - (u[k + 1] - u[0]) * (v[k] - v[0])
				 for k in range(1, len(u) - 1)) / 2
		return lo < abs(au) * BARK_TILE * BARK_TILE / max(area, 1e-9) < hi

	for poly in me.polygons:
		si = fseg[poly.index]
		vis = [me.loops[li].vertex_index for li in poly.loop_indices]
		n = np.array(poly.normal)
		along = n @ D[si]

		def flat():
			# project flat onto the face at the same 5 cm texels, bark streaks along the limb
			t1 = D[si] - n * along
			t1 = t1 / np.linalg.norm(t1) if np.linalg.norm(t1) > 1e-4 else np.array(Vector(n).orthogonal())
			t2 = np.cross(n, t1)
			b_ = segs[si][1]
			for li, pw in zip(poly.loop_indices, co[vis]):
				uv.data[li].uv = (pw @ t2 / BARK_TILE + ou[b_], pw @ t1 / BARK_TILE + ov[b_])

		if flare_zone[poly.index]:
			# Flare: wrap around the trunk axis, and measure "along the bark" down the flare's
			# slope (height minus how far the surface has swelled out), so the trunk's streaks run
			# straight down and spill out over the flare instead of breaking into patches.
			# Same axis frame as the trunk segment above it; the swell correction fades to zero at
			# the zone's top edge, so where the flare meets the trunk both mappings are identical
			# (no ring / seam around the trunk).
			Pp = co[vis] - A[si]
			t = Pp @ AB[si] / L2[si]
			rad = Pp - np.outer(t, AB[si])
			rl = np.maximum(np.linalg.norm(rad, axis=1), 0.02)
			th = np.arctan2(rad @ S0[si], rad @ R0[si])
			period = 2 * np.pi * rl / BARK_TILE
			u = th / (2 * np.pi) * period
			ztop = P["flare_h"] + 0.3
			fade = np.clip((ztop - co[vis][:, 2]) / (0.5 * ztop), 0, 1)
			v = VA[si] + (VB[si] - VA[si]) * t - fade * np.maximum(0.0, rl - P["trunk_r"]) / BARK_TILE
			for k in range(1, len(u)):
				if u[k] - u[0] > period[k] / 2:
					u[k] -= period[k]
				elif u[0] - u[k] > period[k] / 2:
					u[k] += period[k]
			if stretch_ok(u, v, poly.area):
				for li, uu, vv in zip(poly.loop_indices, u, v):
					uv.data[li].uv = (uu, vv)
				continue
			# a root's side wall leaving the flare: map it off its own root instead
			si = own_seg[poly.index]
			along = n @ D[si]
		if abs(along) > 0.6:
			flat()   # saddle faces facing along the limb: wrap-around can only smear them
			continue
		Pp = co[vis] - A[si]
		t = Pp @ AB[si] / L2[si]                      # unclamped: continuous past segment ends
		rad = Pp - np.outer(t, AB[si])
		ang = np.arctan2(rad @ S0[si], rad @ R0[si])
		period = 2 * np.pi * np.maximum(np.linalg.norm(rad, axis=1), 0.02) / BARK_TILE
		u = ang / (2 * np.pi) * period
		v = VA[si] + (VB[si] - VA[si]) * t
		# keep the face on one side of the angle wrap
		for k in range(1, len(u)):
			if u[k] - u[0] > period[k] / 2:
				u[k] -= period[k]
			elif u[0] - u[k] > period[k] / 2:
				u[k] += period[k]
		# safety net: if the wrap-around mapping still puts this face > 3x off the texel size,
		# use the flat projection instead (a seam is better than a smear)
		if not stretch_ok(u, v, poly.area):
			flat()
			continue
		b_ = segs[si][1]
		for li, uu, vv in zip(poly.loop_indices, u, v):
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

CARD_LEN = 1.8    # m; stylised big leaves (~30 cm) at ~3 cm per texel, dense enough to block the sky


def build_leaves(name, nodes, florets, rng, P):
	"""Leaf clumps on the thin ends of the limbs: every card sits within ~0.9 m of a twig node
	(radius < 16 cm), so each one belongs to a clump that hangs off a real limb. Cards stay inside
	their floret (slightly inflated) so each cluster keeps a crisp rounded edge, and they are
	shaded as part of that floret (normals out from its centre): lit tops, darker undersides."""
	anchors = [n.pos for n in nodes if n.depth >= 1 and 0 < n.r < 0.16]

	def home(p):
		vals = [floret_value(p, f) for f in florets]
		k = int(np.argmin(vals))
		return k, vals[k]

	verts, faces, uvs = [], [], []
	fold = math.radians(28)

	def card(base, t, n, length):
		side = t.cross(n).normalized()
		w = length * 0.5
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
	per = P["cards"] / max(1, len(anchors))
	for a in anchors:
		for _ in range(int(per) + (1 if rng.random() < per - int(per) else 0)):
			for _try in range(4):
				base = a + rand_unit(rng) * rng.uniform(0.0, 0.9)
				k, v = home(base)
				if v < 1.15:
					break
			else:
				continue
			o = (base - florets[k][0]).normalized()
			t = (o * 0.6 + UP * 0.25 + rand_unit(rng) * 0.8).normalized()
			n = o + rand_unit(rng) * 0.7 + UP * 0.3
			n = n - t * n.dot(t)
			if n.length < 1e-3:
				n = t.orthogonal()
			card(base - t * CARD_LEN * 0.3, t, n.normalized(), rng.uniform(CARD_LEN * 0.88, CARD_LEN * 1.12))
			picked.append(tuple(base))
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
	shader "shaders/complex.shader"

	g_flMetalness "0.000"
	g_vColorTint "[1.000000 1.000000 1.000000 0.000000]"

	TextureColor "models/environment/tests/elm_bark.png"
	TextureNormal "materials/default/default_normal.tga"
	TextureRoughness "materials/default/default_rough.tga"
	TextureAmbientOcclusion "materials/default/default_ao.tga"
}
"""

VMAT_LEAVES = """Layer0
{
	shader "shaders/complex.shader"
	F_ALPHA_TEST 1
	F_RENDER_BACKFACES 1

	g_flMetalness "0.000"
	g_flAlphaTestReference "0.500"
	g_vColorTint "[1.000000 1.000000 1.000000 0.000000]"

	TextureColor "models/environment/tests/elm_leaves.png"
	TextureTranslucency "models/environment/tests/elm_leaves_mask.png"
	TextureNormal "materials/default/default_normal.tga"
	TextureRoughness "materials/default/default_rough.tga"
	TextureAmbientOcclusion "materials/default/default_ao.tga"
}
"""


# ---------------------------------------------------------------------------- LOD chain

# s&box LODGroupList: (switch_threshold, wood tris, fraction of leaf cards kept, kept-card scale).
# switch_threshold is ModelDoc's LOD switch distance (bigger = farther; compiled as SwitchDistance). The
# citizen uses 5 / 20 / 40 / 70 for a 1.8 m body, so a 25 m tree needs far bigger numbers — 4 / 10 / 24
# put every tree on LOD3 almost at once. Calibrate in ModelDoc with "Set LOD threshold from current
# camera position" and copy the numbers back here.
# Mark's LOD distances for generated models: 100 / 200 / 300 m.
# Gentle steps so the swap does not pop: each level drops ~30-50 % of the remaining cards (never half
# the crown at once), and kept cards grow only ~kept^-0.35 (partial area compensation) so the pixel
# leaves do not visibly jump in size. Cards are nested (LOD3 subset of LOD2 subset of LOD1): a card
# that survives a swap never moves.
TREE_LODS = [
	(0.0, None, 1.0, 1.0),
	(100.0, 6000, 0.7, 1.13),
	(200.0, 1500, 0.4, 1.38),
	(300.0, 400, 0.2, 1.76),
]
# Collision uses this LOD's wood: a full-res 24k-tri physics mesh per tree is slow to build and query.
TREE_PHYSICS_LOD = 2


def lod_name(name, part, level):
	return f"{name}_{part}" if level == 0 else f"{name}_{part}_lod{level}"


def merged_lod_name(name, level):
	return f"{name}_lod{level}"


def merge_lod_meshes(name, wood, leaves, lod_objs):
	"""One export object per LOD level: copies of that level's wood + leaves joined into
	<name>_lod<N> (two material slots, custom leaf normals kept by the join). The separate wood / leaves
	objects stay in the .blend (stumps, previews) and the physics LOD's wood is exported alongside
	for the vmdl's PhysicsMeshFile. Returns [merged lod0, lod1, ...]."""
	pairs = [(wood, leaves)] + [(lod_objs[i], lod_objs[i + 1]) for i in range(0, len(lod_objs), 2)]
	merged = []
	for level, (w, l) in enumerate(pairs):
		parts = []
		for src in (w, l):
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


def build_lods(name, wood, leaves, lods):
	"""Extra LOD objects (level 1+) as copies of the finished wood / leaves: wood decimated to the
	level's triangle budget (UVs carried by the decimate), leaves thinned (cards are 6 verts / 2 quads,
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
			w.data.shade_smooth()

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
						switch_threshold = {lods[i][0]:.1f}
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
							}},
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
	for f in old:
		os.remove(f)
	print(f"removed {len(old)} files from older elm versions")


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0

	remove_old_versions()
	bark_png = os.path.join(MAT_DIR, BARK + ".png")
	leaf_png = os.path.join(MAT_DIR, LEAVES + ".png")
	write_png(bark_png, make_bark_texture())
	leaf_rgba = make_leaf_texture()
	write_png(leaf_png, leaf_rgba)
	# complex.shader's alpha test reads TextureTranslucency, not the colour PNG's alpha
	mask = np.repeat(leaf_rgba[..., 3:4], 4, axis=2)
	mask[..., 3] = 255
	write_png(os.path.join(MAT_DIR, LEAVES + "_mask.png"), mask)
	with open(os.path.join(MAT_DIR, BARK + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT_BARK.replace("elm_bark", BARK))
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
					   "WoodDropMin": fell.HALF_WOOD[0], "WoodDropMax": fell.HALF_WOOD[1]}, True, PREFAB_TEMPLATE)
	fell.write_prefab(os.path.join(PREFAB_DIR, LOG + ".prefab"), LOG, f"{ASSET_DIR}/{LOG}.vmdl",
					  {"MaxHealth": fell.LOG_HP, "CurrentHealth": fell.LOG_HP, "WoodDropMin": 0, "WoodDropMax": 0,
					   "SplitPiecePrefab": f"{PREFAB_REL}/{HALF}.prefab",
					   "SplitPieceOffsetMeters": fell.HALF_LEN / 2}, True, PREFAB_TEMPLATE)
	pieces = [log, half]

	made = {}
	for name, seed0, P, leaders in VARIANTS:
		# Deterministic seed search over 8 seeds: no bald patch first, then the crown outline closest
		# to the reference photos' lumpiness and dip.
		scores = []
		for seed in range(seed0, seed0 + 8):
			rng = random.Random(seed)
			nodes, florets = build_skeleton(rng, P, leaders)
			leaves, cards, pts = build_leaves(name + "_probe", nodes, florets, rng, P)
			lump, dip = outline_score(leaves)
			bald = crown_bald_spot(pts, P)
			scores.append((bald, abs(lump - LUMP_TARGET) + abs(dip - DIP_TARGET), seed, lump, dip, len(florets)))
			bpy.data.meshes.remove(leaves.data)
		holes, _, seed, lump, dip, nfl = min(scores)
		print(f"  {name}: (bald, outline error, seed, lump %, dip %, clusters) {[tuple(round(v, 2) for v in s_) for s_ in scores]}")
		rng = random.Random(seed)
		nodes, florets = build_skeleton(rng, P, leaders)
		leaves, cards, pts = build_leaves(name + "_leaves", nodes, florets, rng, P)
		wood = build_wood(name + "_wood", nodes, rng, P)
		bark_uvs(wood.data, nodes, P)
		wood.data.materials.append(bark)
		leaves.data.materials.append(leaves_mat)
		made[name] = (wood, leaves)
		zs = [v.co.z for v in leaves.data.vertices] + [v.co.z for v in wood.data.vertices]
		xs = [v.co.x for v in leaves.data.vertices]
		ys = [v.co.y for v in leaves.data.vertices]
		wtris = sum(len(p.vertices) - 2 for p in wood.data.polygons)
		# trunk across at breast height (1.3 m), measured on the finished mesh
		ring = [v.co for v in wood.data.vertices if 1.2 < v.co.z < 1.4]
		dbh = (max(c.x for c in ring) - min(c.x for c in ring) + max(c.y for c in ring) - min(c.y for c in ring)) / 2
		print(f"TREE {name} clusters {nfl}, outline lump {lump:.2f} % dip {dip:.2f} %, nodes {len(nodes)} (seed {seed}, biggest bald patch {holes} cells): height {max(zs):.1f} m, crown {max(xs) - min(xs):.1f} x {max(ys) - min(ys):.1f} m, "
			  f"trunk {dbh:.2f} m across at 1.3 m, wood islands {island_count(wood.data)}, bad bark UV faces {uv_stretch_report(wood.data):.1f} %, wood tris {wtris}, cards {cards} ({cards * 4} tris)")

		lod_objs = build_lods(name, wood, leaves, TREE_LODS)
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
			f.write(vmdl_text(name, TREE_LODS, TREE_PHYSICS_LOD).replace("elm_bark", BARK).replace("elm_leaves", LEAVES))
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
						   "WoodDropMin": 0, "WoodDropMax": 0}, False, PREFAB_TEMPLATE)
		pieces.append(stump)

	for o in pieces:
		o.hide_render = True
	# Line the variants up in the .blend for review.
	for i, (wood, leaves) in enumerate(made.values()):
		wood.location.x = leaves.location.x = (i - 2.5) * 34
	bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
	if "render" in ARGS:
		for wood, leaves in made.values():
			wood.location.x = leaves.location.x = 0
		render_previews({n: list(v) for n, v in made.items()})
		render_pieces(pieces, log, half)


if __name__ == "__main__":   # importable: create_elm_sapling.py reuses the textures / helpers
	main()
