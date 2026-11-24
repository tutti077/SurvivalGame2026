"""Redwood forest trees: giant coast redwoods (environment_redwood1..5_v2) and Douglas firs
(environment_fir1..4_v1), choppable like the elm (tree type 1: fells into a log that splits).

Run headless from the repo root:
    blender -b --python "Blender/scripts/create_redwood_trees.py"                                 # both species
    blender -b --python "Blender/scripts/create_redwood_trees.py" -- only redwood render <dir>   # one species + previews
`only <species>` rebuilds that species and deletes only its older-version files; the other species is untouched.

Real-world scale (Blender metres exported 1:1; the .vmdl import_scale 0.4 lands on the 40 u/m world):
    Coast redwood v2 (Mark 2026-11-16 after v1 in game: "supposed to be like 3x taller than the elm trees and thick
        enough to support a tree that tall ... more brush at the top that's concentrated enough to sorta block the
        sunlight ... a dark and creepy forest"): 81-101 m (elms are 26-31 m in game), trunks 4.8-6.6 m across at
        1.3 m with a lobed, fluted buttress flaring to ~1.5x that at the ground and a slow taper; dead stubs on the
        bare trunk; a dense dark crown from the lowest live limb at 52-60 % of the height, ~29-32 m across, so trees
        ~20 m apart close overhead (canopy test: ~14 % sky within 30 deg of zenith from the floor of a 20 m cell).
        redwood2 / redwood4 keep a few long HORIZONTAL limbs partway up the bare trunk. One log (20 m long, as thick as
        that trunk at the 1 m cut) + half per variant.
    Douglas fir v1: 17-29 m, a conical crown of tiered whorls that runs nearly to the ground, lower branches
        drooping and upper ones climbing, dark blue-green sprays, grey-brown furrowed bark.

Structure (same pipeline as create_elm_tree.py, whose helpers this script imports):
    <name>_wood    trunk tube (flat-shaded, varied ring spacing and angular jitter so no face sizes repeat) +
                   limb / branch tubes; this is also the physics mesh (PhysicsMeshFile, LOD0 wood)
    <name>_leaves  needle-spray cards: two quads hinged on a centre crease (the stem), drooping like a tent,
                   cluster-shaded out from the trunk axis. build_lods() thins them per LOD (nested sets).
    LODs 0..3 switch at 100 / 200 / 300 m (vmdl switch_threshold 400 / 800 / 1200); <name>_far.vmdl is LOD3 for the
    far-forest ring, <name>_lod0..2.vmdl the single-LOD models the game cross-fades between.
    Felling: <name>_stump (the tree's own wood below 1 m, end-grain cap), one species log (point down, origin at
    the point) and its half (origin at its centre), as for the elm. Prefabs: ModelRenderer + ModelCollider +
    DamageReceiver + ChopableTree (elm_felling.write_prefab), logs add Rigidbody + TowableProp + "tow".

Textures (pixel_lit, point-sampled):
    redwood / fir bark   256 x 256 on a 2 m tile = 128 texels / m, colour + normal + roughness from one height field
    end grain            64 x 64
    needle sprays        256 x 256 card design, alpha-tested (TextureTranslucency mask)
"""
import bpy, bmesh, glob, math, os, random, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from mathutils import Vector

import create_elm_tree as elm
import elm_felling as fell

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
# Each species carries its own version: the redwoods went to v2 (Mark 2026-11-16, after seeing v1 in game:
# "supposed to be like 3x taller than the elm trees ... more brush at the top ... a dark and creepy forest"),
# the firs stayed v1. `-- only redwood` rebuilds one species and leaves the other's files alone.
RV = "_v2"
FV = "_v1"
ASSET_DIR = "models/environment/redwoodForest/trees"
MAT_REL = "materials/environment/redwoodForest/trees"
PREFAB_REL = "prefabs/environment/redwoodForest/trees"
MODEL_DIR = os.path.join(ROOT, "Assets", *ASSET_DIR.split("/"))
MAT_DIR = os.path.join(ROOT, "Assets", *MAT_REL.split("/"))
PREFAB_DIR = os.path.join(ROOT, "Assets", *PREFAB_REL.split("/"))
ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []

UP = Vector((0, 0, 1))
BARK_TEX = 256
BARK_TILE = 2.0            # m per 256 px tile = 128 texels / m (Mark 2026-11-16)
SPRAY_TEX = 256
WOOD_BOTTOM = -1.0         # m; trunks run straight down below the origin so a slope never shows the cut
STUMP_H = fell.STUMP_H     # 1.0 m, the stump cut and the log spawn height (ChopableTree.StumpTopMeters)
GOLDEN = math.pi * (3 - math.sqrt(5))

# ---------------------------------------------------------------------------- species

SPECIES = {
	"redwood": dict(
		ver=RV,
		bark=f"redwood_bark{RV}", grain=f"redwood_endgrain{RV}", needles=f"redwood_needles{RV}",
		grain_px=256,   # a redwood cut face is up to 3 m across
		# v2: one log + half per variant (environment_redwood<N>_log / _loghalf), sized to that trunk at the cut
		log=None, half=None,
		# cinnamon red-brown, low contrast (Mark's elm rule: 3-4 tones in use, no hue tricks)
		bark_ramp=[(44, 24, 19), (60, 33, 25), (78, 43, 32), (95, 54, 40), (110, 64, 48), (124, 75, 57), (138, 87, 67)],
		# v2: 11 ridges per 2 m tile (~18 cm ridges) - the giant trunks wanted coarser bark than v1's 15
		bark_style=dict(furrows=11, wander=0.7, groove=0.36, depth=1.0, fiber=0.16, seed=31),
		grain_ramp=[(110, 52, 36), (138, 70, 46), (160, 86, 56), (178, 102, 68), (194, 118, 80), (208, 134, 94), (220, 150, 108)],
		grain_rim=((112, 64, 46), (84, 46, 34)),
		# v2: dark redwood green (a dark, creepy forest), dark back layer + slightly lighter front layer
		needle_ramp=[(12, 24, 15), (17, 33, 20), (24, 44, 26), (32, 56, 33), (42, 68, 41), (54, 82, 50), (68, 96, 62)],
		spray=dict(aspect=0.78, side_angle=52, side_len=1.0, side_step=0.042, needle_len=(5.5, 8.5), needle_angle=70,
				   needle_step=1.9, sub=True, seed=5),
		log_len=20.0, log_r=None, log_point=None,     # radius = the trunk at the cut, per variant
		# pixel_lit caps WindTreeHeightMeters at 60 and WindCrownRadiusMeters at 30 (shader Range), so the
		# 80-100 m trees bend over their lower 60 m and the whole crown rides the full sway
		wind=dict(H=60.0, R=13.0, sway=1.6, rock=0.25, bob=0.6, limb_hz=0.4),
		# LODs (switch m, wood tris, card fraction kept, kept-card scale): ~1000 cards at LOD0, so the far levels
		# keep a smaller share than the elm's and grow each card more; LOD3 (the far ring, drawn hundreds of times
		# out to 1200 m) keeps ~10 % of the cards and 300 wood tris
		lods=[(0.0, None, 1.0, 1.0), (100.0, 2500, 0.5, 1.25), (200.0, 900, 0.25, 1.6), (300.0, 300, 0.08, 2.6)],
	),
	"fir": dict(
		ver=FV,
		bark=f"fir_bark{FV}", grain=f"fir_endgrain{FV}", needles=f"fir_needles{FV}",
		log=f"environment_fir_log{FV}", half=f"environment_fir_loghalf{FV}",
		bark_ramp=[(44, 38, 34), (60, 52, 46), (76, 66, 58), (92, 80, 70), (106, 93, 81), (120, 106, 93), (134, 120, 106)],
		bark_style=dict(furrows=11, wander=1.1, groove=0.34, depth=0.95, fiber=0.08, seed=47),
		grain_ramp=fell.ENDGRAIN_RAMP,
		grain_rim=((92, 84, 74), (70, 63, 55)),
		needle_ramp=[(14, 30, 24), (20, 40, 30), (28, 52, 38), (36, 64, 46), (46, 78, 56), (60, 92, 68), (76, 106, 82)],
		spray=dict(aspect=0.72, side_angle=62, side_len=0.95, side_step=0.045, needle_len=(3.5, 5.5), needle_angle=65,
				   needle_step=1.9, sub=True, seed=8),
		log_len=8.0, log_r=0.42, log_point=0.5,
		wind=dict(H=24.0, R=5.0, sway=1.0, rock=0.45, bob=0.35, limb_hz=0.6),
		lods=elm.TREE_LODS,
	),
}

# name, species, seed, params.
# Redwood v2 (elms measure 26-31 m in game; redwoods are ~3x that): H = height (m), dbh = trunk across at 1.3 m
# as authored (the measured value with the lobes is ~10 % more; the buttress flares to ~1.5x at the ground), crown = lowest live limb / H, reach = longest crown branch (m,
# so the crown is ~2 x reach + a few m across), limbs = long horizontal limbs on the bare trunk, stubs = dead stubs.
REDWOODS = [
	(f"environment_redwood1{RV}", "redwood", 101, dict(H=92.0, dbh=5.2, crown=0.55, reach=8.5, limbs=0, stubs=12, lean=1.0)),
	(f"environment_redwood2{RV}", "redwood", 202, dict(H=84.0, dbh=4.6, crown=0.58, reach=8.0, limbs=4, stubs=8, lean=1.4)),
	(f"environment_redwood3{RV}", "redwood", 303, dict(H=100.0, dbh=5.8, crown=0.56, reach=9.0, limbs=0, stubs=14, lean=1.8)),
	(f"environment_redwood4{RV}", "redwood", 404, dict(H=80.0, dbh=4.2, crown=0.52, reach=7.5, limbs=3, stubs=6, lean=0.8)),
	(f"environment_redwood5{RV}", "redwood", 505, dict(H=88.0, dbh=5.0, crown=0.60, reach=8.5, limbs=0, stubs=16, lean=1.2)),
]
FIRS = [
	(f"environment_fir1{FV}", "fir", 11, dict(H=24.0, r=0.42, low=1.1)),
	(f"environment_fir2{FV}", "fir", 22, dict(H=18.0, r=0.32, low=0.7)),
	(f"environment_fir3{FV}", "fir", 33, dict(H=29.0, r=0.52, low=2.4)),
	(f"environment_fir4{FV}", "fir", 44, dict(H=21.0, r=0.38, low=0.9)),
]

FAR_LOD = elm.FAR_LOD                # LOD3: the far-forest ring's model


# ---------------------------------------------------------------------------- textures

def periodic_noise(X, Y, rng, terms, kx_max, ky_max):
	"""Sum of integer-frequency sines: tiles exactly on the unit square."""
	out = np.zeros_like(X)
	for _ in range(terms):
		kx, ky = rng.randint(-kx_max, kx_max), rng.randint(0, ky_max)
		if kx == 0 and ky == 0:
			ky = 1
		out += np.sin(2 * math.pi * (kx * X + ky * Y) + rng.uniform(0, 2 * math.pi)) / math.hypot(kx, ky)
	return out / (np.abs(out).max() + 1e-6)


def make_bark(ramp, furrows, wander, groove, depth, fiber, seed):
	"""Thick fibrous bark of long vertical ridges: `furrows` deep grooves per tile run almost straight up
	(wrapping in x; a slow integer-sine wander in y keeps the tile seamless and lets neighbouring furrows
	converge and part, so the ridges braid). `groove` is the groove's half-width as a share of the ridge spacing;
	each furrow's depth swells and fades along its length, so some ridges merge where a furrow dies out. Ridge
	tops are rounded (height rises with distance from the nearest groove) and carry fine vertical fibre streaks.
	Returns (rgba, height 0..1)."""
	rng = random.Random(seed)
	n = BARK_TEX
	c = (np.arange(n) + 0.5) / n
	X, Y = np.meshgrid(c, 1.0 - c)          # row 0 = top of the image
	spacing = n / furrows
	# one shared, gentle, tile-periodic warp moves every furrow together (its x-slope stays < 1, so furrows
	# never cross); each furrow adds only a small wobble of its own (< a quarter of the ridge spacing)
	warp = np.zeros((n, n))
	for kx, ky in ((0, 1), (1, 1)):
		warp += np.sin(2 * math.pi * (kx * X + ky * Y) + rng.uniform(0, 6.3)) / (1 + abs(kx) + ky)
	warp *= wander / furrows / np.abs(warp).max()
	Xw = X - warp
	g = np.zeros((n, n))
	dmin = np.full((n, n), 1e9)
	for i in range(furrows):
		x0 = (i + rng.uniform(-0.2, 0.2)) / furrows
		xs = x0 + 0.14 / furrows * np.sin(2 * math.pi * rng.randint(1, 3) * Y + rng.uniform(0, 6.3))
		dx = np.abs(((Xw - xs + 0.5) % 1.0) - 0.5) * n                      # texels, wrapped
		w = groove * spacing * rng.uniform(0.8, 1.25) * (1 + 0.25 * np.sin(2 * math.pi * rng.randint(1, 3) * Y + rng.uniform(0, 6.3)))
		# depth swells and fades along the furrow; where it fades the two ridges beside it merge (braiding)
		d = depth * np.clip(0.75 + 0.55 * np.sin(2 * math.pi * rng.randint(1, 2) * Y + rng.uniform(0, 6.3)), 0.0, 1.0)
		g = np.maximum(g, d * np.clip(1 - dx / w, 0, 1) ** 0.8)
		dmin = np.minimum(dmin, np.where(d > 0.35, dx, 1e9))
	crest = np.clip(dmin / (0.5 * spacing), 0, 1) ** 0.5
	streak = periodic_noise(X, Y, rng, 16, 48, 3)
	slow = periodic_noise(X, Y, rng, 5, 4, 2)
	h = 0.6 * (1.0 - g) + 0.3 * crest + fiber * streak * (1 - g) + 0.06 * slow
	h = (h - h.min()) / (h.max() - h.min())
	idx = np.clip(np.round(0.6 + h * 5.0), 0, 6).astype(int)
	rgb = np.array(ramp, float)[idx]
	rgba = np.dstack([rgb, np.full((n, n), 255)]).astype(np.uint8)
	return rgba, h


def make_endgrain(ramp, rim, tex=128):
	old = fell.ENDGRAIN_RAMP
	fell.ENDGRAIN_RAMP = ramp
	img = fell.make_endgrain_texture(tex)   # never under 128 px (Mark 2026-11-23)
	fell.ENDGRAIN_RAMP = old
	for src, dst in zip(((92, 84, 74), (70, 63, 55)), rim):
		m = (img[..., 0] == src[0]) & (img[..., 1] == src[1]) & (img[..., 2] == src[2])
		img[m, :3] = dst
	return img


def make_spray(ramp, stem_rgb, aspect, side_angle, side_len, side_step, needle_len, needle_angle, needle_step, sub, seed):
	"""Needle spray card (256 x 256, card space x = (u - 0.5) * aspect, y = v from base to tip). A thin stem runs
	up the middle (the card's crease), side branchlets leave it alternately, angled forward and shorter toward the
	tip; fir branchlets fork once more. Every axis is sleeved in short flat needles in two ranks (redwood / fir
	needles lie in a flat spray), a darker back pass under a lighter front pass, so the spray reads as soft
	needle mass with gaps between the branchlets. No baked light."""
	rng = random.Random(seed)
	W = H = SPRAY_TEX
	tone = np.full((H, W), -1, int)

	def px(p):                     # card space -> pixel (x right, y up; flipped when written)
		return ((p[0] / aspect + 0.5) * W, p[1] * H)

	def stroke(a, b, width, t):
		(ax, ay), (bx, by) = px(a), px(b)
		x0, x1 = int(max(0, math.floor(min(ax, bx) - width - 1))), int(min(W - 1, math.ceil(max(ax, bx) + width + 1)))
		y0, y1 = int(max(0, math.floor(min(ay, by) - width - 1))), int(min(H - 1, math.ceil(max(ay, by) + width + 1)))
		if x1 < x0 or y1 < y0:
			return
		xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
		dx, dy = bx - ax, by - ay
		tt = np.clip(((xs - ax) * dx + (ys - ay) * dy) / (dx * dx + dy * dy + 1e-9), 0, 1)
		m = np.hypot(xs - (ax + tt * dx), ys - (ay + tt * dy)) < width
		reg = tone[y0:y1 + 1, x0:x1 + 1]
		reg[m] = t

	half = aspect * 0.5 * 0.94
	axes = []                       # (points list in card space)
	# main axis: base to tip with a slight S curve
	main = [(0.03 * math.sin(k * 0.9 + seed) * (k / 8), 0.02 + 0.95 * k / 8) for k in range(9)]
	axes.append((main, True))
	t, side = 0.10, 1
	while t < 0.9:
		k = t * 8
		i0 = min(7, int(k))
		f = k - i0
		base = (main[i0][0] * (1 - f) + main[i0 + 1][0] * f, main[i0][1] * (1 - f) + main[i0 + 1][1] * f)
		ang = math.radians(side_angle + rng.uniform(-10, 10))
		reach = half * side_len * (1.0 - 0.75 * max(0.0, t - 0.15) / 0.75) * rng.uniform(0.8, 1.1)
		L = reach / math.sin(ang)
		tip = (base[0] + side * reach, base[1] + L * math.cos(ang))
		mid = ((base[0] + tip[0]) / 2 + side * 0.01, (base[1] + tip[1]) / 2 - 0.012)
		pts = [base, mid, tip]
		axes.append((pts, False))
		if sub and reach > half * 0.45:
			for s_ in (0.45, 0.75):
				sb = (base[0] + (tip[0] - base[0]) * s_, base[1] + (tip[1] - base[1]) * s_)
				sl = reach * 0.32
				axes.append(([sb, (sb[0] + side * sl * 0.35, sb[1] + sl * 0.95)], False))
		side = -side
		t += side_step * rng.uniform(0.8, 1.25)

	def needles(pts, tones, length_scale, width):
		for a, b in zip(pts, pts[1:]):
			pa, pb = px(a), px(b)
			seg = math.hypot(pb[0] - pa[0], pb[1] - pa[1])
			if seg < 1e-6:
				continue
			ux, uy = (pb[0] - pa[0]) / seg, (pb[1] - pa[1]) / seg
			s = 0.0
			while s < seg:
				cx, cy = pa[0] + ux * s, pa[1] + uy * s
				for sgn in (-1, 1):
					ang = math.radians(needle_angle + rng.uniform(-12, 12))
					nl = rng.uniform(*needle_len) * length_scale
					# rotate the axis direction by +-ang (forward-leaning two-ranked needles)
					ca, sa = math.cos(ang), math.sin(ang) * sgn
					vx, vy = ux * ca - uy * sa, ux * sa + uy * ca
					a_c = ((cx / W - 0.5) * aspect, cy / H)
					b_c = (((cx + vx * nl) / W - 0.5) * aspect, (cy + vy * nl) / H)
					stroke(a_c, b_c, width, rng.choice(tones))
				s += needle_step * rng.uniform(0.8, 1.2)

	for pts, _ in axes:
		needles(pts, (1, 2, 2), 1.15, 0.9)
	for pts, _ in axes:
		needles(pts, (3, 4, 4, 5), 0.85, 0.7)
	stem = np.zeros((H, W), bool)
	for pts, is_main in axes:
		for a, b in zip(pts, pts[1:]):
			stroke(a, b, 0.75 if is_main else 0.55, 99)
	rgb = np.zeros((H, W, 3))
	lut = np.array(ramp, float)
	leaf = (tone >= 0) & (tone < 7)
	rgb[leaf] = lut[tone[leaf]]
	rgb[tone == 99] = stem_rgb
	alpha = np.where(tone >= 0, 255, 0)
	rgba = np.dstack([rgb, alpha]).astype(np.uint8)[::-1].copy()   # row 0 = tip (v = 1)
	return rgba


# ---------------------------------------------------------------------------- vmat / vmdl text

def vmat_bark(name, wind=None):
	head = '\tshader "shaders/pixel_lit.shader"\n' + ("\tF_WIND 1\n" if wind else "")
	return ("Layer0\n{\n" + head + "\n"
			'\tPixelRoughness "1.000"\n\tPixelNormalStrength "1.000"\n'
			f'\tTextureColor "{MAT_REL}/{name}.png"\n'
			f'\tTextureNormal "{MAT_REL}/{name}_normal.png"\n'
			f'\tTextureRoughness "{MAT_REL}/{name}_rough.png"\n'
			+ (wind_params(wind) if wind else "") + "}\n")


def vmat_plain(name):
	return ("Layer0\n{\n\tshader \"shaders/pixel_lit.shader\"\n\n\tPixelRoughness \"0.900\"\n"
			f'\tTextureColor "{MAT_REL}/{name}.png"\n}}\n')


def wind_params(w):
	return (f'\n\tWindTreeHeightMeters "{w["H"]:.3f}"\n\tWindCrownRadiusMeters "{w["R"]:.3f}"\n'
			f'\tWindTrunkSwayMeters "{w["sway"]:.3f}"\n\tWindTrunkRockHz "{w["rock"]:.3f}"\n'
			f'\tWindLimbBobMeters "{w["bob"]:.3f}"\n\tWindLimbHz "{w["limb_hz"]:.3f}"\n'
			'\tWindBendPower "1.500"\n\tWindFadeStartMeters "300.000"\n\tWindFadeEndMeters "360.000"\n')


def vmat_needles(name, w):
	return ("Layer0\n{\n\tshader \"shaders/pixel_lit.shader\"\n\tF_ALPHA_TEST 1\n\tF_RENDER_BACKFACES 1\n\tF_WIND 1\n\n"
			'\tPixelRoughness "0.900"\n\tPixelAlphaCutoff "0.500"\n\tPixelNormalUp "0.000"\n\tPixelDirectMax "0.550"\n'
			f'\tTextureColor "{MAT_REL}/{name}.png"\n\tTextureTranslucency "{MAT_REL}/{name}_mask.png"\n'
			+ wind_params(w) + '\tWindLeafFlutterMeters "0.060"\n\tWindLeafHz "2.200"\n\tWindLeafGustMeters "0.300"\n}\n')


VMDL_HEAD = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->\n"


def remap_block(pairs, default):
	# The fbx material names do not resolve to a sibling vmat (materials live in materials/...), so the bare
	# "<name>.vmat" is what the compiler sees - the same form create_camp_props.py uses.
	rm = "".join(f'\t\t\t\t\t\t\t{{\n\t\t\t\t\t\t\t\tfrom = "{a}.vmat"\n\t\t\t\t\t\t\t\tto = "{MAT_REL}/{b}.vmat"\n\t\t\t\t\t\t\t}},\n'
				 for a, b in pairs)
	return ("\t\t\t{\n\t\t\t\t_class = \"MaterialGroupList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n"
			"\t\t\t\t\t\t_class = \"DefaultMaterialGroup\"\n\t\t\t\t\t\tremaps =\n\t\t\t\t\t\t[\n" + rm +
			"\t\t\t\t\t\t]\n\t\t\t\t\t\tuse_global_default = false\n"
			f'\t\t\t\t\t\tglobal_default_material = "{MAT_REL}/{default}.vmat"\n\t\t\t\t\t}},\n\t\t\t\t]\n\t\t\t}},\n')


def render_mesh(fbx, obj=None):
	filt = (f'\t\t\t\t\t\timport_filter =\n\t\t\t\t\t\t{{\n\t\t\t\t\t\t\texclude_by_default = true\n'
			f'\t\t\t\t\t\t\texception_list = [ "{obj}" ]\n\t\t\t\t\t\t}}\n') if obj else \
		   ('\t\t\t\t\t\timport_filter =\n\t\t\t\t\t\t{\n\t\t\t\t\t\t\texclude_by_default = false\n'
			'\t\t\t\t\t\t\texception_list = [  ]\n\t\t\t\t\t\t}\n')
	return ("\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"RenderMeshFile\"\n" + (f'\t\t\t\t\t\tname = "{obj}"\n' if obj else "") +
			f'\t\t\t\t\t\tfilename = "{ASSET_DIR}/{fbx}.fbx"\n'
			"\t\t\t\t\t\timport_translation = [ 0.0, 0.0, 0.0 ]\n\t\t\t\t\t\timport_rotation = [ 0.0, 0.0, 0.0 ]\n"
			"\t\t\t\t\t\timport_scale = 0.4\n\t\t\t\t\t\talign_origin_x_type = \"None\"\n"
			"\t\t\t\t\t\talign_origin_y_type = \"None\"\n\t\t\t\t\t\talign_origin_z_type = \"None\"\n"
			"\t\t\t\t\t\tparent_bone = \"\"\n" + filt + "\t\t\t\t\t},\n")


def vmdl_doc(blocks):
	return (VMDL_HEAD + "{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n" + "".join(blocks) +
			"\t\t]\n\t\tmodel_archetype = \"\"\n\t\tprimary_associated_entity = \"\"\n\t\tanim_graph_name = \"\"\n"
			"\t\tbase_model_name = \"\"\n\t}\n}\n")


def tree_vmdl(name, sp):
	remaps = remap_block([(sp["bark"], sp["bark"] + "_wind"), (sp["needles"], sp["needles"])], sp["bark"] + "_wind")
	phys = ("\t\t\t{\n\t\t\t\t_class = \"PhysicsShapeList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n\t\t\t\t\t{\n"
			"\t\t\t\t\t\t_class = \"PhysicsMeshFile\"\n"
			f'\t\t\t\t\t\tname = "{name}_wood_physics"\n\t\t\t\t\t\tparent_bone = ""\n\t\t\t\t\t\tsurface_prop = "wood"\n'
			'\t\t\t\t\t\tcollision_tags = "solid"\n\t\t\t\t\t\trecenter_on_parent_bone = false\n'
			"\t\t\t\t\t\toffset_origin = [ 0.0, 0.0, 0.0 ]\n\t\t\t\t\t\toffset_angles = [ 0.0, 0.0, 0.0 ]\n"
			'\t\t\t\t\t\talign_origin_x_type = "None"\n\t\t\t\t\t\talign_origin_y_type = "None"\n\t\t\t\t\t\talign_origin_z_type = "None"\n'
			f'\t\t\t\t\t\tfilename = "{ASSET_DIR}/{name}.fbx"\n\t\t\t\t\t\timport_scale = 0.4\n'
			"\t\t\t\t\t\tmaxMeshVertices = 0\n\t\t\t\t\t\tqemError = 0.0\n"
			f'\t\t\t\t\t\timport_filter =\n\t\t\t\t\t\t{{\n\t\t\t\t\t\t\texclude_by_default = true\n\t\t\t\t\t\t\texception_list = [ "{name}_wood" ]\n\t\t\t\t\t\t}}\n'
			"\t\t\t\t\t},\n\t\t\t\t]\n\t\t\t},\n")
	lods_t = sp["lods"]
	meshes = "".join(render_mesh(name, elm.merged_lod_name(name, i)) for i in range(len(lods_t)))
	rml = "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" + meshes + "\t\t\t\t]\n\t\t\t},\n"
	groups = "".join(f'\t\t\t\t\t{{\n\t\t\t\t\t\t_class = "LODGroup"\n\t\t\t\t\t\tswitch_threshold = {lods_t[i][0] * elm.LOD_THRESHOLD_PER_METER:.1f}\n'
					 f'\t\t\t\t\t\tmeshes =\n\t\t\t\t\t\t[\n\t\t\t\t\t\t\t"{elm.merged_lod_name(name, i)}",\n\t\t\t\t\t\t]\n\t\t\t\t\t}},\n'
					 for i in range(len(lods_t)))
	lods = "\t\t\t{\n\t\t\t\t_class = \"LODGroupList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" + groups + "\t\t\t\t]\n\t\t\t},\n"
	return vmdl_doc([remaps, phys, rml, lods])


def single_lod_vmdl(name, sp, level):
	remaps = remap_block([(sp["bark"], sp["bark"] + "_wind"), (sp["needles"], sp["needles"])], sp["bark"] + "_wind")
	rml = ("\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" +
		   render_mesh(name, elm.merged_lod_name(name, level)) + "\t\t\t\t]\n\t\t\t},\n")
	return vmdl_doc([remaps, rml])


def piece_vmdl(name, sp, physics):
	remaps = remap_block([(sp["bark"], sp["bark"]), (sp["grain"], sp["grain"])], sp["bark"])
	kind = ("\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"PhysicsHullFromRender\"\n\t\t\t\t\t\tparent_bone = \"\"\n"
			"\t\t\t\t\t\tsurface_prop = \"wood\"\n\t\t\t\t\t\tcollision_tags = \"solid\"\n\t\t\t\t\t\tfaceMergeAngle = 10.0\n"
			"\t\t\t\t\t\tmaxHullVertices = 0\n\t\t\t\t\t},\n") if physics == "hull" else \
		   ("\t\t\t\t\t{\n\t\t\t\t\t\t_class = \"PhysicsMeshFromRender\"\n\t\t\t\t\t\tparent_bone = \"\"\n"
			"\t\t\t\t\t\tsurface_prop = \"wood\"\n\t\t\t\t\t\tcollision_tags = \"solid\"\n\t\t\t\t\t},\n")
	phys = "\t\t\t{\n\t\t\t\t_class = \"PhysicsShapeList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" + kind + "\t\t\t\t]\n\t\t\t},\n"
	rml = "\t\t\t{\n\t\t\t\t_class = \"RenderMeshList\"\n\t\t\t\tchildren =\n\t\t\t\t[\n" + render_mesh(name) + "\t\t\t\t]\n\t\t\t},\n"
	return vmdl_doc([remaps, phys, rml])


# ---------------------------------------------------------------------------- geometry

class Buf:
	"""Mesh under construction: verts, faces with per-corner UVs."""
	def __init__(self):
		self.v, self.f, self.uv = [], [], []

	def tube(self, centers, rfn, sides, jitter, ref_r, tip_point=None, cap_base=False, tile=BARK_TILE):
		"""Rings around a polyline. rfn(i, angle) -> radius of ring i at that angle. `jitter` is a per-column
		angular offset (radians) so faces vary in width. A seam column is duplicated for clean UVs.
		Bark u is constant per column (no shear between rings), sized from ref_r: 128 texels / m at that girth,
		squeezed where the tube is thinner. tip_point closes the end as a cone; cap_base closes the start."""
		frames = []
		n_prev = None
		for i, c in enumerate(centers):
			t = (centers[min(i + 1, len(centers) - 1)] - centers[max(i - 1, 0)]).normalized()
			if n_prev is None:
				ref = Vector((0, 0, 1)) if abs(t.z) < 0.9 else Vector((1, 0, 0))
				n = (ref - t * ref.dot(t)).normalized()
			else:
				n = (n_prev - t * n_prev.dot(t)).normalized()
			b = t.cross(n)
			frames.append((n, b))
			n_prev = n
		rings, dist = [], 0.0
		mean_r = []
		for i, (c, (n, b)) in enumerate(zip(centers, frames)):
			if i:
				dist += (c - centers[i - 1]).length
			ring = []
			rs = []
			for k in range(sides + 1):
				a = 2 * math.pi * (k % sides) / sides + jitter[k % sides]
				r = rfn(i, a)
				rs.append(r)
				ring.append(len(self.v))
				self.v.append(c + (n * math.cos(a) + b * math.sin(a)) * r)
			mean_r.append(sum(rs[:sides]) / sides)
			rings.append((ring, dist))
		circ = max(1, round(2 * math.pi * max(ref_r, 0.03) / tile))     # whole tiles around: seamless
		for (r0, d0), (r1, d1) in zip(rings, rings[1:]):
			for k in range(sides):
				self.f.append((r0[k], r0[k + 1], r1[k + 1], r1[k]))
				self.uv.append([(k / sides * circ, d0 / tile), ((k + 1) / sides * circ, d0 / tile),
								((k + 1) / sides * circ, d1 / tile), (k / sides * circ, d1 / tile)])
		last, dl = rings[-1]
		if tip_point is not None:
			ti = len(self.v)
			self.v.append(tip_point)
			dt = dl + (tip_point - centers[-1]).length
			for k in range(sides):
				self.f.append((last[k], last[k + 1], ti))
				self.uv.append([(k / sides * circ, dl / tile), ((k + 1) / sides * circ, dl / tile),
								((k + 0.5) / sides * circ, dt / tile)])
		if cap_base:
			first = rings[0][0][:sides]
			self.f.append(tuple(reversed(first)))
			self.uv.append([(0.5 + 0.4 * math.cos(2 * math.pi * k / sides), 0.5 + 0.4 * math.sin(2 * math.pi * k / sides))
							for k in reversed(range(sides))])

	def to_object(self, name, mat):
		me = bpy.data.meshes.new(name)
		me.from_pydata([tuple(p) for p in self.v], [], self.f)
		uvl = me.uv_layers.new(name="UVMap")
		for poly, quv in zip(me.polygons, self.uv):
			for li, u in zip(poly.loop_indices, quv):
				uvl.data[li].uv = u
		me.validate()
		me.shade_flat()
		me.materials.append(mat)
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def jitter_cols(rng, sides, amount):
	return [rng.uniform(-amount, amount) * 2 * math.pi / sides for _ in range(sides)]


def ring_heights(rng, H, base_steps, step_range, top_gap):
	zs = [WOOD_BOTTOM] + list(base_steps)
	z = zs[-1]
	while True:
		z += rng.uniform(*step_range)
		if z > H - top_gap:
			break
		zs.append(z)
	return zs


def lobe_field(rng, n_terms, kmin, kmax):
	terms = [(rng.randint(kmin, kmax), rng.uniform(0, 2 * math.pi), rng.uniform(0.5, 1.0)) for _ in range(n_terms)]
	norm = sum(w for _, _, w in terms)
	return lambda a: sum(w * math.cos(k * a + p) for k, p, w in terms) / norm


def trunk(buf, rng, H, r_bh, r_tip, taper_pow, flare, flare_h, lobes, flutes, flute_amp, sides, lean, steps, step_range,
		  flare2=0.0, flare2_h=1.0, lobe_h=None):
	"""Trunk along a gently leaning axis; returns axis(z) and radius(z) for placing limbs.
	Base swell = flare * exp(-z / flare_h) (the sharp foot) + flare2 * exp(-z / flare2_h) (a broad swell up the
	lower trunk); buttress lobes fade over lobe_h (default flare_h); flutes run up the trunk above the base."""
	lobe_h = lobe_h or flare_h
	lean_az = rng.uniform(0, 2 * math.pi)
	ld = Vector((math.cos(lean_az), math.sin(lean_az), 0))
	wob = (rng.uniform(0, 6.3), rng.uniform(0, 6.3))

	def axis(z):
		zz = max(z, 0.0)
		return ld * lean * (zz / H) ** 2 + Vector((0.12 * math.sin(zz * 0.21 + wob[0]), 0.12 * math.sin(zz * 0.17 + wob[1]), z))

	def radius(z):
		zz = max(z, 0.0)
		return r_tip + (r_bh - r_tip) * max(0.0, 1 - zz / H) ** taper_pow

	lobe = lobe_field(rng, 5, 3, 6)
	zs = ring_heights(rng, H, steps, step_range, top_gap=1.2)
	per_v = [[rng.uniform(0.95, 1.05) for _ in range(sides)] for _ in zs]

	def swell(z):
		return 1 + flare * math.exp(-z / flare_h) + flare2 * math.exp(-z / flare2_h)

	def rfn(i, a):
		z = max(zs[i], 0.0)
		e = math.exp(-z / flare_h)
		el = math.exp(-z / lobe_h)
		r = radius(z) * swell(z) * (1 + lobes * el * max(-0.4, lobe(a)))
		if flutes:
			r *= 1 + flute_amp * (1 - e) * math.cos(flutes * a + 0.15 * z)
		k = int(round((a % (2 * math.pi)) / (2 * math.pi) * sides)) % sides
		return r * per_v[i][k]

	ref_r = radius(1.3) * swell(1.3)
	buf.tube([axis(z) for z in zs], rfn, sides, jitter_cols(rng, sides, 0.22), ref_r, tip_point=axis(H), cap_base=True)
	return axis, radius, swell


def limb(buf, pts, r0, r1, sides, rng, upto=1.0, tip_len=None):
	"""Tapered closed tube along pts (only the first `upto` of its length: the sprays carry the rest)."""
	if upto < 1.0:
		pts = [point_on(pts, upto * i / (len(pts) - 1))[0] for i in range(len(pts))]
	n = len(pts)
	rs = [r0 + (r1 - r0) * (i / (n - 1)) ** 0.8 for i in range(n)]
	pv = [[rng.uniform(0.9, 1.1) for _ in range(sides)] for _ in range(n)]

	def rfn(i, a):
		k = int(round((a % (2 * math.pi)) / (2 * math.pi) * sides)) % sides
		return rs[i] * pv[i][k]

	tip = pts[-1] + (pts[-1] - pts[-2]).normalized() * (max(r1 * 3, 0.1) if tip_len is None else tip_len)
	buf.tube(pts, rfn, sides, jitter_cols(rng, sides, 0.15), r0, tip_point=tip, cap_base=False)


def curve(start, d_h, L, rise0, sag, tipup, segs, lateral=0.0, side=None):
	"""Branch polyline from start along horizontal direction d_h: height offset rise0*s - sag*s^2 + tipup*s^3
	(all in units of L), with an optional sideways bow."""
	side = side or d_h.cross(UP)
	pts = []
	for i in range(segs + 1):
		s = i / segs
		pts.append(start + d_h * (L * s) + UP * (L * (rise0 * s - sag * s * s + tipup * s ** 3))
				   + side * (L * lateral * math.sin(math.pi * s)))
	return pts


def point_on(pts, s):
	"""Point and tangent at fraction s of the polyline's length."""
	segl = [(b - a).length for a, b in zip(pts, pts[1:])]
	target = s * sum(segl)
	for (a, b), l in zip(zip(pts, pts[1:]), segl):
		if target <= l or l == segl[-1] and b == pts[-1]:
			f = target / l if l else 0
			return a + (b - a) * min(f, 1.0), (b - a).normalized()
		target -= l
	return pts[-1], (pts[-1] - pts[-2]).normalized()


class Cards:
	"""Needle-spray cards: two quads hinged on the centre crease (u = 0.5 = the stem), each half folded DOWN by
	`fold` from the card plane like a tent, so a spray droops on both sides of its stem. Cluster normals point out
	from the trunk axis and up, so the crown shades as one soft cone instead of flickering facets."""
	def __init__(self, aspect, fold_deg):
		self.v, self.f, self.uv, self.home = [], [], [], []
		self.aspect = aspect
		self.fold = math.radians(fold_deg)

	def add(self, base, d, n, length, home):
		side = d.cross(n).normalized()
		w = length * self.aspect * 0.5
		tip = base + d * length
		lo = side * (-math.cos(self.fold) * w) - n * (math.sin(self.fold) * w)
		ro = side * (math.cos(self.fold) * w) - n * (math.sin(self.fold) * w)
		i = len(self.v)
		self.v.extend([base, tip, base + lo, tip + lo, base + ro, tip + ro])
		self.f.append((i + 2, i + 0, i + 1, i + 3))
		self.uv.append(((0, 0), (0.5, 0), (0.5, 1), (0, 1)))
		self.f.append((i + 0, i + 4, i + 5, i + 1))
		self.uv.append(((0.5, 0), (1, 0), (1, 1), (0.5, 1)))
		self.home.append(home)

	def spray(self, rng, base, d, length, home, roll=35):
		"""A card along direction d with its face turned up (rolled at random)."""
		d = d.normalized()
		n0 = UP - d * UP.dot(d)
		n0 = n0.normalized() if n0.length > 1e-3 else d.orthogonal().normalized()
		r = math.radians(rng.uniform(-roll, roll))
		n = (n0 * math.cos(r) + d.cross(n0) * math.sin(r)).normalized()
		self.add(base, d, n, length, home)

	def to_object(self, name, mat, axis):
		me = bpy.data.meshes.new(name)
		me.from_pydata([tuple(p) for p in self.v], [], self.f)
		uvl = me.uv_layers.new(name="UVMap")
		for poly, quv in zip(me.polygons, self.uv):
			for li, u in zip(poly.loop_indices, quv):
				uvl.data[li].uv = u
		loop_normals = []
		for poly in me.polygons:
			hp = self.home[poly.index // 2]
			for li in poly.loop_indices:
				q = me.vertices[me.loops[li].vertex_index].co
				c = axis(q.z)
				o = Vector((q.x - c.x, q.y - c.y, 0))
				o = (o.normalized() * 0.75 if o.length > 1e-3 else Vector()) + UP * 0.65 + (q - hp).normalized() * 0.2
				o.normalize()
				fn = poly.normal.copy()
				if fn.dot(o) < 0:
					fn = -fn
				loop_normals.append((fn * 0.2 + o * 0.8).normalized())
		me.normals_split_custom_set(loop_normals)
		me.materials.append(mat)
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def droop_dir(d_h, angle_deg):
	a = math.radians(angle_deg)
	return (d_h * math.cos(a) + UP * math.sin(a)).normalized()


def build_redwood(name, rng, P, bark, needles):
	"""Giant coast redwood (v2): an 80-100 m column, heavy lobed + fluted buttress, slow taper, dead stubs on the
	bare trunk, optional long horizontal limbs, and a DENSE crown over the upper ~40-45 %: heavy drooping branches
	carrying overlapping clumps of big dark sprays, layered so the sky is mostly shut out from below."""
	H, dbh = P["H"], P["dbh"]
	# swell at 1.3 m = 1 + 1.0 e^(-1.3) + 0.35 e^(-1.3 / 7) = 1.56; at the ground 2.35 (~1.5x the breast-height girth)
	r_core = dbh / 2 / 1.56
	wood = Buf()
	axis, radius, swell = trunk(
		wood, rng, H, r_core, r_tip=0.25, taper_pow=0.55, flare=1.0, flare_h=1.0, lobes=0.6, flutes=12,
		flute_amp=0.06, sides=24, lean=P["lean"], steps=(0.0, 0.4, 0.9, 1.5, 2.3, 3.3, 4.6, 6.2, 8.2),
		step_range=(3.0, 5.5), flare2=0.35, flare2_h=7.0, lobe_h=3.0)

	def girth(z):
		return radius(z) * swell(max(z, 0.0))

	cards = Cards(SPECIES["redwood"]["spray"]["aspect"], 24)
	zc = P["crown"] * H
	azi = rng.uniform(0, 2 * math.pi)

	# dead stubs on the bare trunk below the crown
	for _ in range(P["stubs"]):
		z = rng.uniform(0.22 * H, zc - 2.0)
		a = rng.uniform(0, 2 * math.pi)
		d_h = Vector((math.cos(a), math.sin(a), 0))
		L = rng.uniform(1.5, 4.0)
		start = axis(z) + d_h * girth(z) * 0.6
		pts = curve(start, d_h, L + girth(z) * 0.4, rng.uniform(-0.4, -0.1), 0.0, 0.0, 2)
		r0 = rng.uniform(0.18, 0.32)
		limb(wood, pts, r0, r0 * 0.55, 5, rng, tip_len=0.08)          # snapped off: a blunt broken end

	def clump(p, tg, size, n, home, up=(-28, 6)):
		tg_h = Vector((tg.x, tg.y, 0))
		tg_h = tg_h.normalized() if tg_h.length > 1e-4 else Vector((1, 0, 0))
		for _ in range(n):
			swing = math.radians(rng.uniform(-80, 80))
			dh = Vector((tg_h.x * math.cos(swing) - tg_h.y * math.sin(swing),
						 tg_h.x * math.sin(swing) + tg_h.y * math.cos(swing), 0))
			base = p - tg * size * 0.3 + UP * rng.uniform(-0.5, 0.5)
			cards.spray(rng, base, droop_dir(dh, rng.uniform(*up)), size * rng.uniform(0.8, 1.15), home, roll=25)

	# long horizontal limbs on the bare trunk, each a dense green shelf
	n_l = P["limbs"]
	for i in range(n_l):
		z = H * rng.uniform(0.3, 0.46)
		a = azi + i * 2 * math.pi / n_l + rng.uniform(-0.5, 0.5)
		d_h = Vector((math.cos(a), math.sin(a), 0))
		L = rng.uniform(12.0, 17.0) * H / 90
		pts = curve(axis(z), d_h, L, rng.uniform(0.02, 0.06), rng.uniform(0.04, 0.08), rng.uniform(0.03, 0.08), 6,
					lateral=rng.uniform(-0.06, 0.06))
		limb(wood, pts, min(1.0, 0.3 * girth(z)), 0.22, 8, rng)
		s = 0.28
		while s < 1.0:
			p, t = point_on(pts, s)
			clump(p + UP * 0.25, t, rng.uniform(3.2, 4.4), rng.randint(3, 4), p)
			s += rng.uniform(0.08, 0.12)
		tip, t = point_on(pts, 1.0)
		clump(tip, t, 4.2, 7, tip, up=(-25, 25))

	# crown: heavy drooping branches from the lowest live limb to the top, each carrying overlapping clumps of big
	# sprays; consecutive tiers overlap in plan so the crown is a dark layered mass with few sky gaps
	z = zc
	Lmax = P["reach"]
	while z < H - 2.0:
		t = (z - zc) / (H - zc)
		for _ in range(2 if rng.random() < 0.55 else 3):
			azi += GOLDEN + rng.uniform(-0.35, 0.35)
			if rng.random() < 0.06:
				continue
			d_h = Vector((math.cos(azi), math.sin(azi), 0))
			L = (Lmax * (1 - t) ** 0.7 + 2.5) * rng.uniform(0.75, 1.1)
			rise = rng.uniform(-0.2, 0.0) if t < 0.5 else rng.uniform(-0.08, 0.2)
			start = axis(z) + d_h * girth(z) * 0.6
			pts = curve(start, d_h, L, rise, rng.uniform(0.12, 0.25), rng.uniform(0.05, 0.15), 3)
			if L > 4.5:
				limb(wood, pts, min(0.45, 0.08 + 0.028 * L), 0.06, 4, rng, upto=0.72)
			n_clump = 1 + int(L / 3.6)
			size = max(3.0, min(5.2, 0.3 * L + 2.4))
			for c in range(n_clump):
				s = 0.35 + 0.65 * (c + 1) / n_clump
				p, tg = point_on(pts, min(s, 1.0))
				clump(p, tg, size, rng.randint(3, 5) if t < 0.5 else rng.randint(3, 4), p, up=(-28, 6) if t < 0.7 else (-15, 25))
			# inner fill: short sprays near the trunk so the crown has no hollow core from below
			if rng.random() < 0.45:
				p, tg = point_on(pts, 0.2)
				clump(p, tg, size * 0.75, 2, p)
		z += rng.uniform(0.7, 1.15)
	# spire
	top = axis(H - 0.6)
	for k in range(8):
		a = azi + k * 2 * math.pi / 8 + rng.uniform(-0.3, 0.3)
		d_h = Vector((math.cos(a), math.sin(a), 0))
		cards.spray(rng, axis(H - 4.0 + k * 0.45), droop_dir(d_h, rng.uniform(30, 70)), rng.uniform(2.4, 3.4), top, roll=20)

	w = wood.to_object(name + "_wood", bark)
	lv = cards.to_object(name + "_leaves", needles, axis)
	return w, lv, len(cards.home)


def build_fir(name, rng, P, bark, needles):
	H, r_bh = P["H"], P["r"]
	wood = Buf()
	axis, radius, _ = trunk(wood, rng, H, r_bh, r_tip=0.05, taper_pow=1.0, flare=0.3, flare_h=0.6,
						 lobes=0.25, flutes=0, flute_amp=0.0, sides=10, lean=0.25,
						 steps=(0.0, 0.4, 1.0, 1.8), step_range=(1.6, 3.4))
	cards = Cards(SPECIES["fir"]["spray"]["aspect"], 22)
	R = 0.23 * H
	z = P["low"]
	az0 = rng.uniform(0, 2 * math.pi)
	while z < H - 0.7:
		t = z / H
		n = rng.randint(4, 6)
		az0 += rng.uniform(0.4, 1.0)
		for i in range(n):
			if rng.random() < 0.08:
				continue
			a = az0 + i * 2 * math.pi / n + rng.uniform(-0.25, 0.25)
			d_h = Vector((math.cos(a), math.sin(a), 0))
			L = (R * (1 - t) ** 0.95 + 0.4) * rng.uniform(0.85, 1.1)
			ang = -26 + 52 * t ** 1.2 + rng.uniform(-6, 6)
			rise = math.tan(math.radians(ang))
			start = axis(z) + d_h * radius(z) * 0.5
			sag = 0.22 if t < 0.6 else 0.1
			tipup = 0.12 if t < 0.6 else 0.04
			drop = L * (rise - sag + tipup)               # tip height change; low whorls must clear the ground
			if start.z + drop < 0.5 + 0.3 * L * sag:
				rise += (0.5 + 0.3 * L * sag - start.z - drop) / L
			pts = curve(start, d_h, L, rise, sag, tipup, 3)
			if L > 2.4:
				limb(wood, pts, min(0.13, 0.03 + 0.022 * L), 0.025, 4, rng, upto=0.65)
			p0, tg = point_on(pts, 0.05)
			length = min(L * 1.0 + 0.5, 4.6)
			cards.spray(rng, p0, (tg + UP * 0.04).normalized(), length, start, roll=25)
			if L > 1.8:
				for sgn in (-1, 1):
					p, tg2 = point_on(pts, rng.uniform(0.35, 0.55))
					tg_h = Vector((tg2.x, tg2.y, 0)).normalized()
					sd = tg_h.cross(UP) * sgn
					cards.spray(rng, p, droop_dir((tg_h + sd * 0.8).normalized(), rng.uniform(-22, 4)),
								min(0.55 * L + 0.4, 2.6), p, roll=25)
		z += rng.uniform(0.6, 0.95) * (H / 22) ** 0.5
	top = axis(H)
	for k in range(4):
		a = az0 + k * math.pi / 2 + rng.uniform(-0.3, 0.3)
		d_h = Vector((math.cos(a), math.sin(a), 0))
		cards.spray(rng, axis(H - 1.6 + k * 0.25), droop_dir(d_h, rng.uniform(45, 75)), rng.uniform(1.0, 1.5), top, roll=15)
	cards.spray(rng, axis(H - 1.4), UP, 1.6, top, roll=0)

	w = wood.to_object(name + "_wood", bark)
	lv = cards.to_object(name + "_leaves", needles, axis)
	return w, lv, len(cards.home)


# ---------------------------------------------------------------------------- export

def export(objs, path):
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, global_scale=1.0, apply_unit_scale=True,
							 object_types={'MESH'}, mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)


def write(path, text):
	with open(path, "w", newline="\n") as f:
		f.write(text)


def remove_old(keys):
	"""Delete every file of the rebuilt species that is not of its current version (models, materials, prefabs,
	source + .meta + compiled), and the old Tripo redwood (replaced, Mark 2026-11-16). Species not in `keys` are
	left untouched."""
	old = glob.glob(os.path.join(MODEL_DIR, "redwoodbasepacked*")) if "redwood" in keys else []
	for key in keys:
		ver = SPECIES[key]["ver"]
		for d in (MODEL_DIR, MAT_DIR, PREFAB_DIR):
			for pat in (f"environment_{key}*", f"{key}_*"):
				old += [f for f in glob.glob(os.path.join(d, pat))
						if ver + "." not in os.path.basename(f) and ver + "_" not in os.path.basename(f)]
	for f in set(old):
		try:
			os.remove(f)
		except PermissionError:
			print("LOCKED, delete later:", f)
	tex = os.path.join(MODEL_DIR, "textures")
	if "redwood" in keys and os.path.isdir(tex):
		shutil.rmtree(tex, ignore_errors=True)
	print(f"removed {len(set(old))} old files")


def make_materials(key):
	sp = SPECIES[key]
	os.makedirs(MAT_DIR, exist_ok=True)
	rgba, h = make_bark(sp["bark_ramp"], **sp["bark_style"])
	bark_png = os.path.join(MAT_DIR, sp["bark"] + ".png")
	elm.write_png(bark_png, rgba)
	elm.write_png(os.path.join(MAT_DIR, sp["bark"] + "_normal.png"), elm.make_bark_normal(h, 3.0))
	elm.write_png(os.path.join(MAT_DIR, sp["bark"] + "_rough.png"), elm.make_bark_roughness(h))
	write(os.path.join(MAT_DIR, sp["bark"] + ".vmat"), vmat_bark(sp["bark"]))
	write(os.path.join(MAT_DIR, sp["bark"] + "_wind.vmat"), vmat_bark(sp["bark"], sp["wind"]))
	grain_png = os.path.join(MAT_DIR, sp["grain"] + ".png")
	elm.write_png(grain_png, make_endgrain(sp["grain_ramp"], sp["grain_rim"], sp.get("grain_px", 128)))
	write(os.path.join(MAT_DIR, sp["grain"] + ".vmat"), vmat_plain(sp["grain"]))
	spray = make_spray(sp["needle_ramp"], sp["bark_ramp"][2], **sp["spray"])
	needle_png = os.path.join(MAT_DIR, sp["needles"] + ".png")
	elm.write_png(needle_png, spray)
	mask = np.repeat(spray[..., 3:4], 4, axis=2)
	mask[..., 3] = 255
	elm.write_png(os.path.join(MAT_DIR, sp["needles"] + "_mask.png"), mask)
	write(os.path.join(MAT_DIR, sp["needles"] + ".vmat"), vmat_needles(sp["needles"], sp["wind"]))
	return (elm.blender_material(sp["bark"], bark_png, False),
			elm.blender_material(sp["needles"], needle_png, True),
			elm.blender_material(sp["grain"], grain_png, False))


def write_log_pieces(sp, log_name, half_name, L, R, point, bark, grain):
	"""A log (point down, origin at the point) + its half (origin at its centre), elm_felling meshes, and their
	prefabs with the elm's chop health / wood numbers and masses."""
	log = fell.build_log(log_name, L, R, point, BARK_TILE, bark, grain, 5)
	half = fell.build_log(half_name, L / 2, R, 0.0, BARK_TILE, bark, grain, 6)
	for v in half.data.vertices:
		v.co.z -= L / 4
	for o in (log, half):
		o.data.shade_flat()
		export([o], os.path.join(MODEL_DIR, o.name + ".fbx"))
		write(os.path.join(MODEL_DIR, o.name + ".vmdl"), piece_vmdl(o.name, sp, "hull"))
	fell.write_prefab(os.path.join(PREFAB_DIR, half_name + ".prefab"), half_name, f"{ASSET_DIR}/{half_name}.vmdl",
					  {"MaxHealth": fell.HALF_HP, "CurrentHealth": fell.HALF_HP,
					   "WoodDropMin": fell.HALF_WOOD[0], "WoodDropMax": fell.HALF_WOOD[1]}, True, fell.HALF_MASS)
	fell.write_prefab(os.path.join(PREFAB_DIR, log_name + ".prefab"), log_name, f"{ASSET_DIR}/{log_name}.vmdl",
					  {"MaxHealth": fell.LOG_HP, "CurrentHealth": fell.LOG_HP, "WoodDropMin": 0, "WoodDropMax": 0,
					   "SplitPiecePrefab": f"{PREFAB_REL}/{half_name}.prefab",
					   "SplitPieceOffsetMeters": L / 4, "SplitCenterOffsetMeters": L / 2}, True, fell.LOG_MASS)
	return log, half


def cut_radius(stump):
	"""Mean radius of the stump's end-grain cap (the trunk at the cut)."""
	gi = len(stump.data.materials) - 1
	pts = [stump.data.vertices[vi].co for p in stump.data.polygons if p.material_index == gi for vi in p.vertices]
	cx = sum(p.x for p in pts) / len(pts)
	cy = sum(p.y for p in pts) / len(pts)
	ring = [math.hypot(p.x - cx, p.y - cy) for p in pts]
	return sum(ring) / len(ring), max(ring)


def build_tree(name, key, seed, P, mats, report):
	sp = SPECIES[key]
	ver = sp["ver"]
	bark, needles, grain = mats
	rng = random.Random(seed)
	builder = build_redwood if key == "redwood" else build_fir
	wood, leaves, ncards = builder(name, rng, P, bark, needles)
	wtris = sum(len(p.vertices) - 2 for p in wood.data.polygons)
	zs = [v.co.z for v in leaves.data.vertices] + [v.co.z for v in wood.data.vertices]
	xs = [v.co.x for v in leaves.data.vertices]
	ys = [v.co.y for v in leaves.data.vertices]
	near = (P.get("dbh", 2 * P.get("r", 1.0))) * 1.6
	ring = [v.co for v in wood.data.vertices if 0.9 < v.co.z < 1.7 and math.hypot(v.co.x, v.co.y) < near]
	dbh = (max(c.x for c in ring) - min(c.x for c in ring) + max(c.y for c in ring) - min(c.y for c in ring)) / 2 if ring else 0
	at40 = [v.co for v in wood.data.vertices if 37 < v.co.z < 43 and math.hypot(v.co.x, v.co.y) < near]
	d40 = (max(c.x for c in at40) - min(c.x for c in at40) + max(c.y for c in at40) - min(c.y for c in at40)) / 2 if at40 else 0
	print(f"TREE {name}: height {max(zs):.1f} m, crown {max(xs) - min(xs):.1f} x {max(ys) - min(ys):.1f} m, "
		  f"trunk {dbh:.2f} m across at 1.3 m ({d40:.2f} m at 40 m), wood tris {wtris}, cards {ncards}")

	lod_objs = elm.build_lods(name, wood, leaves, sp["lods"])
	merged = elm.merge_lod_meshes(name, wood, leaves, lod_objs)
	export(merged + [wood], os.path.join(MODEL_DIR, name + ".fbx"))
	write(os.path.join(MODEL_DIR, name + ".vmdl"), tree_vmdl(name, sp))
	write(os.path.join(MODEL_DIR, name + "_far.vmdl"), single_lod_vmdl(name, sp, FAR_LOD))
	for level in range(FAR_LOD):
		write(os.path.join(MODEL_DIR, f"{name}_lod{level}.vmdl"), single_lod_vmdl(name, sp, level))
	far_tris = sum(len(p.vertices) - 2 for p in merged[FAR_LOD].data.polygons)
	for o in lod_objs + merged:
		o.hide_render = True
		o.hide_set(True)

	sname = name.replace(ver, "_stump" + ver)
	stump = fell.build_stump(sname, wood, grain)
	stump.data.shade_flat()
	export([stump], os.path.join(MODEL_DIR, sname + ".fbx"))
	write(os.path.join(MODEL_DIR, sname + ".vmdl"), piece_vmdl(sname, sp, "mesh"))
	pieces = [stump]
	if sp["log"]:
		log_prefab = sp["log"]
		cut = (0.0, 0.0)
	else:
		# per-variant log: diameter = this trunk at the cut (mean radius of the stump cap), elm_felling's 0.5 m
		# point scaled with the radius
		r_mean, r_max = cut_radius(stump)
		log_prefab = name.replace(ver, "_log" + ver)
		half_prefab = name.replace(ver, "_loghalf" + ver)
		pieces += write_log_pieces(sp, log_prefab, half_prefab, sp["log_len"], r_mean,
								   r_mean * fell.LOG_POINT / fell.LOG_R, bark, grain)
		cut = (r_mean, r_max)
	fell.write_prefab(os.path.join(PREFAB_DIR, name + ".prefab"), name, f"{ASSET_DIR}/{name}.vmdl",
					  {"MaxHealth": fell.TREE_HP, "CurrentHealth": fell.TREE_HP,
					   "StumpModel": f"{ASSET_DIR}/{sname}.vmdl", "StumpTopMeters": STUMP_H,
					   "FelledLogPrefab": f"{PREFAB_REL}/{log_prefab}.prefab", "StumpHealth": fell.STUMP_HP,
					   "StumpWoodMin": fell.STUMP_WOOD[0], "StumpWoodMax": fell.STUMP_WOOD[1],
					   "WoodDropMin": 0, "WoodDropMax": 0}, False)
	report.append((name, max(zs), dbh, d40, max(xs) - min(xs), max(ys) - min(ys), wtris, ncards, far_tris, cut[0] * 2))
	for o in pieces:
		o.hide_render = True
	return wood, leaves, pieces


# ---------------------------------------------------------------------------- previews

def setup_preview_scene():
	scene = bpy.context.scene
	for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
		try:
			scene.render.engine = eng
			break
		except TypeError:
			pass
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
	gm.from_pydata([(-300, -300, 0), (300, -300, 0), (300, 300, 0), (-300, 300, 0)], [], [(0, 1, 2, 3)])
	ground = bpy.data.objects.new("ground", gm)
	gmat = bpy.data.materials.new("duff")
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.16, 0.1, 0.06, 1)
	gm.materials.append(gmat)
	scene.collection.objects.link(ground)
	bpy.ops.mesh.primitive_cylinder_add(radius=0.22, depth=1.8, location=(0, 0, 0.9))
	person = bpy.context.active_object
	person.name = "person_1p8m"
	cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
	scene.collection.objects.link(cam)
	scene.camera = cam
	return scene, cam, person


def look(cam, loc, target):
	cam.location = loc
	d = (Vector(target) - Vector(loc))
	cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


def render_previews(trees, out_dir):
	"""Per variant: whole tree, base with the 1.8 m post, crown up close."""
	os.makedirs(out_dir, exist_ok=True)
	scene, cam, person = setup_preview_scene()
	outputs = []
	for name, (wood, leaves, H, r) in trees.items():
		for other, (w2, l2, _, _) in trees.items():
			w2.hide_render = l2.hide_render = other != name
			w2.location = l2.location = (0, 0, 0)
		person.location = (r * 1.6 + 1.0, -r * 1.2, 0.9)
		scene.render.resolution_x, scene.render.resolution_y = 900, 1300
		cam.data.lens = 35
		look(cam, (0, -1.15 * H, 0.08 * H), (0, 0, 0.47 * H))
		scene.render.filepath = os.path.join(out_dir, name + ".png")
		bpy.ops.render.render(write_still=True)
		outputs.append(scene.render.filepath)
		scene.render.resolution_x, scene.render.resolution_y = 1000, 800
		cam.data.lens = 28
		look(cam, (r * 1.2 + 3.5, -r * 2.2 - 6.0, 1.8), (0, 0, 2.0))
		scene.render.filepath = os.path.join(out_dir, name + "_base.png")
		bpy.ops.render.render(write_still=True)
		outputs.append(scene.render.filepath)
		look(cam, (0, -0.4 * H, 0.6 * H), (0, 0, 0.78 * H))
		scene.render.filepath = os.path.join(out_dir, name + "_crown.png")
		bpy.ops.render.render(write_still=True)
		outputs.append(scene.render.filepath)
	print("RENDERS", outputs)
	return scene, cam, person


def import_elm(path, height):
	"""environment_elm3_v76 LOD0 from its fbx with the elm's own textures, scaled to its in-game height."""
	before = set(bpy.data.objects)
	bpy.ops.import_scene.fbx(filepath=path)
	new = [o for o in bpy.data.objects if o not in before]
	keep = next(o for o in new if o.name.endswith("_lod0"))
	for o in new:
		if o is not keep:
			bpy.data.objects.remove(o, do_unlink=True)
	keep.location = (0, 0, 0)
	keep.rotation_euler = (0, 0, 0)
	keep.scale = (1, 1, 1)
	bpy.context.view_layer.update()
	zmax = max(v.co.z for v in keep.data.vertices)
	s = height / zmax
	keep.scale = (s, s, s)
	elm_dir = os.path.dirname(path)
	for i, slot in enumerate(keep.material_slots):
		nm = slot.material.name if slot.material else ""
		if "leaves" in nm:
			keep.material_slots[i].material = elm.blender_material("elm_leaves_preview", os.path.join(elm_dir, "elm_leaves_v76.png"), True)
		elif "bark" in nm:
			keep.material_slots[i].material = elm.blender_material("elm_bark_preview", os.path.join(elm_dir, "elm_bark_v76.png"), False)
	print(f"ELM {keep.name}: fbx height {zmax:.1f} m, shown at {height:.1f} m (x{s:.2f})")
	return keep


def render_redwood_set(trees, out_dir, scene, cam, person):
	"""Lineup of the five redwoods beside elm3 (31 m, as measured in game) and the 1.8 m post, then the canopy test:
	all five spaced ~20 m apart, looked at straight up from the forest floor between them (sky share measured on a
	transparent-film render) and from a standing eye looking up at an angle."""
	names = [n for n in trees if "_redwood" in n]
	for n, (w, l, _, _) in trees.items():
		w.hide_render = l.hide_render = n not in names
	elm_obj = import_elm(os.path.join(ROOT, "Assets", "models", "environment", "cloverHills", "trees", "elm",
									  "environment_elm3_v76.fbx"), 31.0)
	gap = 32.0
	xs = [(i - len(names) / 2) * gap for i in range(len(names) + 1)]
	for n, x in zip(names, xs[1:]):
		trees[n][0].location = trees[n][1].location = (x, 0, 0)
	elm_obj.location = (xs[0], 0, 0)
	person.location = (xs[0] + 9, -6, 0.9)
	Hmax = max(trees[n][2] for n in names)
	scene.render.resolution_x, scene.render.resolution_y = 1800, 1100
	cam.data.lens = 30
	look(cam, (0, -1.05 * Hmax - 60, 0.12 * Hmax), (0, 0, 0.46 * Hmax))
	out = []
	scene.render.filepath = os.path.join(out_dir, "lineup_redwood_v2.png")
	bpy.ops.render.render(write_still=True)
	out.append(scene.render.filepath)
	# the bases with the elm and the post
	scene.render.resolution_x, scene.render.resolution_y = 1600, 900
	cam.data.lens = 24
	look(cam, (xs[0] + 12, -34, 2.0), (xs[0] + 18, 0, 10.0))
	scene.render.filepath = os.path.join(out_dir, "lineup_redwood_v2_bases.png")
	bpy.ops.render.render(write_still=True)
	out.append(scene.render.filepath)
	elm_obj.hide_render = True

	# canopy: four trees on the corners of a 20 m square around the camera (14 m from each trunk, the open middle
	# of a forest cell) and a fifth 20 m beyond, so neighbours stand ~20 m apart
	spots = [(-10, -10), (10, -10), (10, 10), (-10, 10), (10, 30)]
	for i, (n, (x, y)) in enumerate(zip(names, spots)):
		trees[n][0].location = trees[n][1].location = (x, y, 0)
		trees[n][0].rotation_euler = trees[n][1].rotation_euler = (0, 0, i * 1.3)
	person.location = (3.0, 2.0, 0.9)
	scene.render.resolution_x, scene.render.resolution_y = 1000, 1000
	cam.data.lens = 12
	look(cam, (0, 0, 1.7), (0.001, 0, 100))
	scene.render.filepath = os.path.join(out_dir, "canopy_up_v2.png")
	bpy.ops.render.render(write_still=True)
	out.append(scene.render.filepath)
	scene.render.film_transparent = True
	alpha_path = os.path.join(out_dir, "canopy_up_alpha_v2.png")
	scene.render.filepath = alpha_path
	bpy.ops.render.render(write_still=True)
	scene.render.film_transparent = False
	img = bpy.data.images.load(alpha_path)
	px = np.empty(len(img.pixels), np.float32)
	img.pixels.foreach_get(px)
	a = px.reshape(-1, 4)[:, 3].reshape(img.size[1], img.size[0])
	yy, xx = np.mgrid[0:a.shape[0], 0:a.shape[1]]
	half_w = a.shape[1] / 2
	tan_half = 18.0 / cam.data.lens                     # 36 mm sensor across the square frame
	off = np.degrees(np.arctan(np.hypot(xx - half_w, yy - a.shape[0] / 2) / half_w * tan_half))
	sky_all = float((a < 0.5).mean()) * 100
	sky_30 = float((a[off < 30] < 0.5).mean()) * 100
	sky_45 = float((a[off < 45] < 0.5).mean()) * 100
	print(f"CANOPY sky visible looking straight up: {sky_30:.1f} % within 30 deg of zenith, {sky_45:.1f} % within 45 deg, "
		  f"{sky_all:.1f} % of the whole frame")
	bpy.data.images.remove(img)
	os.remove(alpha_path)
	cam.data.lens = 18
	look(cam, (-8, -6, 1.7), (14, 10, 30))
	scene.render.resolution_x, scene.render.resolution_y = 1600, 1000
	scene.render.filepath = os.path.join(out_dir, "canopy_floor_v2.png")
	bpy.ops.render.render(write_still=True)
	out.append(scene.render.filepath)
	print("RENDERS", out)
	return sky_30, sky_45


# ---------------------------------------------------------------------------- main

def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	for d in (MODEL_DIR, MAT_DIR, PREFAB_DIR):
		os.makedirs(d, exist_ok=True)
	keys = [ARGS[ARGS.index("only") + 1]] if "only" in ARGS else ["redwood", "fir"]
	remove_old(keys)
	trees, report = {}, []
	for key in keys:
		variants = REDWOODS if key == "redwood" else FIRS
		sp = SPECIES[key]
		mats = make_materials(key)
		if sp["log"]:
			for o in write_log_pieces(sp, sp["log"], sp["half"], sp["log_len"], sp["log_r"], sp["log_point"], mats[0], mats[2]):
				o.hide_render = True
				o.hide_set(True)
		for name, _, seed, P in variants:
			wood, leaves, pieces = build_tree(name, key, seed, P, mats, report)
			trees[name] = (wood, leaves, P["H"], P.get("dbh", 2 * P.get("r", 0.5)) / 2)
			for o in pieces:
				o.hide_set(True)
	print("SUMMARY")
	for row in report:
		print("  %s  %.1f m tall, %.2f m trunk at 1.3 m, %.2f m at 40 m, crown %.1f x %.1f m, %d wood tris, %d cards, "
			  "LOD3 %d tris, log %.2f m across" % row)
	if "render" in ARGS:
		out = ARGS[ARGS.index("render") + 1]
		scene, cam, person = render_previews(trees, out)
		if "redwood" in keys:
			render_redwood_set(trees, out, scene, cam, person)
		bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "redwood_trees.blend"))


if __name__ == "__main__":
	main()
