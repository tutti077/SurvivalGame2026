"""Clover Hills drops added 2026-11-03: flowers and leaves, mushrooms, ground materials, wings, cooked
food, the new seed pouches, the fishing rod and the lantern. Ground items centred on X/Y standing on
z = 0; the fishing rod in the held frame (handle +X, butt at the origin)."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace
from .kit import registry, ground, butt_at_origin, paint, squash, loft, broad_leaf, torus
from .items_wearables import _pouch

ITEMS, item = registry()


def C(rgb, d=12, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.07), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


# ---------------------------------------------------------------- flowers, leaves, fungi


def _daisy_head(rng, petal_mat, disc_mat, r=0.055, n=15):
	m = blob(rng, r * 0.36, subdiv=1, jitter=0.2, noise=0.15, scale=(1, 1, 0.55), mat=disc_mat)
	for i in range(n):
		a = 360 * i / n + (rng.random() - 0.5) * 9
		p = leaf(rng, r * (0.95 + rng.random() * 0.2), r * 0.3, mat=petal_mat, bend=0.12, segments=3, taper_tip=True)
		p.rotate("x", -8 - rng.random() * 10).translate(0, r * 0.2, -0.004).rotate("z", a)
		m.add(p)
	return m


@item("daisy", "resources")
def daisy(rng):
	cells = [P.stem_green(), P.leaf_green(), P.petal_white(), P.yellow(), P.leaf_dark()]
	# a cut daisy lying on the ground (head ~1.6x life size so it reads), stem curving, head tipped to the sky
	path = np.array([(-0.17 + 0.3 * t, 0.025 * math.sin(t * 3.4), 0.012 + 0.03 * t ** 3) for t in np.linspace(0, 1, 9)])
	m = path_tube(rng, path, radius=0.0055, segs=5, jitter=0.1, radius_fn=lambda t: 1.0 - 0.25 * t, mat=0)
	for x, s, L in ((-0.08, 1, 0.09), (-0.02, -1, 0.075)):
		lf = leaf(rng, L, 0.026, mat=1 if s > 0 else 4, bend=0.2, segments=4).rotate("z", -90 + s * 40).translate(x, 0.0, 0.012)
		m.add(lf)
	head = _daisy_head(rng, 2, 3).rotate("y", 62).translate(0.145, path[-1][1], 0.06)
	m.add(head)
	return ground(m), cells


@item("hobbits_leaf", "resources")
def hobbits_leaf(rng):
	cells = [C((90, 148, 58)), C((74, 128, 48)), C((150, 176, 96)), P.rope(), C((112, 140, 60))]
	# a hand of broad leaves (pipe-weed, cured on the stalk) tied at the stems with twine, fanned out flat
	m = Mesh()
	for i, (a, L, W) in enumerate(((-24, 0.3, 0.15), (-8, 0.34, 0.17), (9, 0.32, 0.16), (25, 0.28, 0.14), (0, 0.24, 0.12))):
		lf = broad_leaf(rng, L, W, mat=i % 2 if i < 4 else 4, rib_mat=2, segments=6, cols=5, bend=0.08 + rng.random() * 0.06, droop=0.35)
		lf.rotate("z", a).translate(0, 0, 0.006 * i + 0.008)
		m.add(lf)
		stalk = tube(rng, length=0.07, radius=0.005, segs=5, rings=1, jitter=0.1, mat=2).rotate("x", 90).rotate("z", a + 180)
		m.add(stalk.translate(0, 0.005, 0.01 + 0.004 * i))
	tie = lathe([(0.018, 0.0), (0.022, 0.005), (0.022, 0.02), (0.018, 0.025)], segs=8, mat=3, rng=rng, jitter=0.12)
	m.add(tie.scale(1, 1, 1).rotate("x", 90).translate(0, 0.03, 0.014))
	m.rotate("z", 90)
	return ground(m), cells


def _mushroom(rng, cap_mat, gill_mat, stem_mat, h=0.15, cap_r=0.08, lean=0.0):
	stem = lathe([(0.03, 0.0), (0.034, 0.02), (0.028, h * 0.5), (0.022, h * 0.9), (0.02, h)], segs=8, mat=stem_mat, rng=rng, jitter=0.15)
	cap = lathe([(cap_r * 0.25, h * 0.92), (cap_r * 0.9, h * 0.95), (cap_r * 1.02, h * 1.02), (cap_r * 0.95, h * 1.12), (cap_r * 0.7, h * 1.26),
				 (cap_r * 0.35, h * 1.33), (0.0, h * 1.35)], segs=10, mat=cap_mat, rng=rng, jitter=0.18)
	paint(cap, gill_mat, lambda c, n: n[:, 2] < -0.3)
	stem.add(cap)
	weld_displace(stem, rng, cap_r * 0.05, cells=4.0)
	return stem.rotate("y", lean)


@item("mushroom", "food")
def mushroom(rng):
	cells = [C((140, 98, 60)), C((206, 186, 150)), C((222, 210, 186)), P.dirt()]
	# a pair of brown-capped boletes pulled up together (~1.5x life size), a clod of soil on the base
	m = _mushroom(rng, 0, 1, 2, h=0.14, cap_r=0.075, lean=-8)
	m.add(_mushroom(rng, 0, 1, 2, h=0.09, cap_r=0.05, lean=24).rotate("z", 30).translate(0.06, 0.02, 0))
	m.add(blob(rng, 0.04, subdiv=1, jitter=0.4, noise=0.3, scale=(1.6, 1.2, 0.5), mat=3).translate(0.025, 0.01, 0.005))
	return ground(m), cells


@item("roasted_mushroom", "food")
def roasted_mushroom(rng):
	cells = [C((104, 64, 36)), C((150, 110, 70)), C((176, 150, 110)), P.wood_pale(), P.ember()]
	# three mushrooms browned on a green-wood skewer, lying on the ground
	m = tube(rng, length=0.34, radius=0.006, segs=5, rings=3, jitter=0.08, profile=lambda t: 1.0 - 0.6 * max(0, t - 0.85) / 0.15, mat=3, axis="x")
	for k, x in enumerate((0.13, 0.2, 0.27)):
		mu = _mushroom(rng, 0, 1, 2, h=0.05, cap_r=0.034 + 0.004 * (k % 2), lean=0)
		paint(mu, 4, lambda c, n: (n[:, 2] > 0.6) & (np.abs(c[:, 0] - 0.0) < 0.012))
		mu.translate(0, 0, -0.04).rotate("y", -90).rotate("x", rng.random() * 360).translate(x, 0, 0)
		m.add(mu)
	m.rotate("z", 15)
	return ground(m), cells


@item("mycelium", "resources")
def mycelium(rng):
	cells = [C((206, 180, 210)), C((228, 214, 230)), C((176, 150, 180)), P.dirt()]
	# a peeled mat of fungal threads: a flat felted pad, threads trailing off its edges, pinhead buds on top
	m = blob(rng, 0.13, subdiv=2, jitter=0.5, noise=0.35, noise_cells=3.0, scale=(1.3, 1.0, 0.18), mat=0, flatten_bottom=0.6)
	paint(m, 3, lambda c, n: n[:, 2] < -0.5)
	for k in range(9):
		a = 2 * math.pi * k / 9 + rng.random() * 0.4
		r0 = 0.11
		path = []
		for t in np.linspace(0, 1, 5):
			r = r0 + 0.09 * t
			path.append((1.25 * r * math.cos(a + 0.4 * t * (k % 2 * 2 - 1)), r * math.sin(a + 0.3 * t), 0.008 * (1 - t)))
		m.add(path_tube(rng, np.array(path), radius=0.004, segs=4, jitter=0.15, radius_fn=lambda t: 1.0 - 0.7 * t, mat=1))
	for k in range(6):
		a = rng.random() * 2 * math.pi
		r = rng.random() * 0.08
		bud = blob(rng, 0.008 + rng.random() * 0.005, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 0.8), mat=1)
		stalk = tube(rng, length=0.025, radius=0.0025, segs=4, rings=1, mat=1)
		stalk.add(bud.translate(0, 0, 0.028))
		m.add(stalk.translate(1.2 * r * math.cos(a), r * math.sin(a), 0.015))
	return ground(m), cells


@item("vines", "resources")
def vines(rng):
	cells = [C((78, 112, 46)), C((60, 124, 48)), C((48, 102, 40)), C((96, 84, 52))]
	# a loose coil of cut vine: two strands spiralling, leaves along them, one end trailing off
	m = Mesh()
	for strand in range(2):
		pts = []
		n = 70
		turns = 2.2
		for i in range(n):
			t = i / (n - 1)
			a = 2 * math.pi * turns * t + strand * 2.4
			r = 0.13 - 0.05 * t + 0.012 * math.sin(t * 23 + strand)
			pts.append((r * math.cos(a), 0.85 * r * math.sin(a), 0.012 + 0.03 * t + 0.008 * math.sin(a * 3)))
		last = np.array(pts[-1])
		for k in range(1, 7):
			pts.append((last[0] + 0.03 * k, last[1] + 0.012 * k * (strand * 2 - 1), max(0.01, last[2] - 0.008 * k)))
		pts = np.array(pts)
		m.add(path_tube(rng, pts, radius=0.007 - 0.0015 * strand, segs=5, jitter=0.15, mat=0 if strand == 0 else 3))
		for i in range(4, len(pts) - 2, 6):
			d = pts[i + 1] - pts[i]
			ang = math.degrees(math.atan2(d[1], d[0]))
			lf = leaf(rng, 0.05 + rng.random() * 0.02, 0.035, mat=1 + (i // 6) % 2, bend=0.2, segments=3)
			side = 1 if (i // 6) % 2 else -1
			lf.rotate("x", 18).rotate("z", ang - 90 + side * 70).translate(*pts[i])
			m.add(lf)
	return ground(m), cells


# ---------------------------------------------------------------- ground materials


@item("sand", "resources")
def sand(rng):
	cells = [C((214, 184, 118), 10), C((196, 166, 104), 10), C((228, 202, 140), 10)]
	# a poured heap of sand at its angle of repose: a cone with a slumped lobe on one side, slide ridges
	# down the flanks and a few loose clumps at the foot
	prof = [(0.16, 0.0), (0.14, 0.022), (0.11, 0.048), (0.08, 0.075), (0.05, 0.1), (0.025, 0.118), (0.0, 0.124)]
	m = lathe(prof, segs=15, mat=0, rng=rng, jitter=0.18, closed_bottom=True)

	def slump(p):
		a = np.arctan2(p[:, 1], p[:, 0])
		r = np.hypot(p[:, 0], p[:, 1])
		lobe = 1 + 0.35 * np.exp(-((a - 0.6) / 0.7) ** 2) * (1 - np.clip(p[:, 2] / 0.12, 0, 1))
		rid = 1 + 0.06 * np.sin(a * 7) * np.clip(p[:, 2] / 0.05, 0, 1) * (1 - np.clip(p[:, 2] / 0.12, 0, 1))
		k = lobe * rid
		p[:, 0] = r * k * np.cos(a)
		p[:, 1] = r * k * np.sin(a)
		return p
	squash(m, slump)
	weld_displace(m, rng, 0.006, cells=4.0)
	paint(m, 1, lambda c, n: (c[:, 2] < 0.03))
	paint(m, 2, lambda c, n: (c[:, 2] > 0.09))
	for k in range(4):
		a = rng.random() * 2 * math.pi
		clump = blob(rng, 0.014 + rng.random() * 0.01, subdiv=1, jitter=0.4, noise=0.3, scale=(1.3, 1, 0.55), mat=1)
		m.add(clump.translate(0.19 * math.cos(a), 0.19 * math.sin(a), 0.0))
	return ground(m), cells


@item("clay", "resources")
def clay(rng):
	cells = [C((178, 104, 66)), C((160, 90, 56)), C((196, 124, 84))]
	# a wire-cut block of wet clay from the bank: soft rounded block, one end sliced flat and smoother,
	# a thumb print pressed in the top and a torn-off pinch lying beside it
	out = [(0.11 * np.sign(math.cos(a)) * abs(math.cos(a)) ** 0.5, 0.075 * np.sign(math.sin(a)) * abs(math.sin(a)) ** 0.6) for a in np.linspace(0, 2 * math.pi, 14, endpoint=False)]
	m = prism(out, 0.05, mat=0, side_mat=0, rng=rng, jitter=0.012, taper=0.97)
	m.add(prism(out, 0.045, z0=0.05, mat=0, side_mat=0, rng=rng, jitter=0.012, taper=0.8))
	weld_displace(m, rng, 0.012, cells=3.0)

	def press(p):
		d = np.hypot(p[:, 0] + 0.03, p[:, 1] - 0.01)
		p[:, 2] -= 0.018 * np.exp(-(d / 0.025) ** 2) * (p[:, 2] > 0.08)
		return p
	squash(m, press)
	paint(m, 2, lambda c, n: n[:, 0] > 0.8)
	paint(m, 1, lambda c, n: n[:, 2] < -0.5)
	m.add(blob(rng, 0.035, subdiv=1, jitter=0.35, noise=0.25, scale=(1.3, 1, 0.75), mat=1, flatten_bottom=0.6).translate(0.15, -0.05, 0.0))
	return ground(m), cells


@item("peat", "resources")
def peat(rng):
	cells = [C((74, 52, 34), 10), C((60, 42, 28), 10), C((96, 72, 46), 10), C((120, 98, 60), 10)]
	# two cut peat sods stacked crosswise: spade-cut bricks, fibrous with root ends sticking out of the faces
	m = Mesh()
	for k, (w, d, h, rot, z) in enumerate(((0.28, 0.13, 0.085, 0, 0.0), (0.26, 0.12, 0.08, 74, 0.085))):
		out = [(-w / 2, -d / 2), (0, -d / 2 - 0.006), (w / 2, -d / 2 + 0.004), (w / 2 + 0.006, 0), (w / 2, d / 2), (0, d / 2 + 0.005), (-w / 2, d / 2 - 0.004),
			   (-w / 2 - 0.007, 0)]
		s = prism(out, h, z0=z, mat=0, side_mat=1, rng=rng, jitter=0.012)
		weld_displace(s, rng, 0.006, cells=6.0)
		paint(s, 2, lambda c, n: (np.abs(n[:, 0]) > 0.7))
		s.rotate("z", rot)
		m.add(s)
	for k in range(8):
		a = rng.random() * 2 * math.pi
		z = 0.02 + rng.random() * 0.13
		r0 = 0.07 if z > 0.085 else 0.07
		root = tube(rng, length=0.025 + rng.random() * 0.02, radius=0.0025, segs=4, rings=1, jitter=0.1, mat=3, axis="x")
		root.rotate("y", -20 + rng.random() * 40).rotate("z", math.degrees(a)).translate(r0 * math.cos(a), r0 * math.sin(a), z)
		m.add(root)
	return ground(m), cells


# ---------------------------------------------------------------- bird wings (rawmeat_bird)


def _wing(rng, skin, fat, s=1.0):
	# a raw wing laid flat: the drumette (a little drumstick, knobbed at the shoulder), the flat (a wide
	# paddle with two bone ridges) bent at the elbow, and the thin pointed tip folded back
	m = loft(rng, 0.075 * s, lambda t: 0.012 * s + 0.013 * s * math.sin(math.pi * min(1, 0.2 + t)) ** 0.7 + 0.006 * s * max(0, 0.25 - t) * 4,
			 lambda t: 0.011 * s + 0.011 * s * math.sin(math.pi * min(1, 0.2 + t)) ** 0.7, rings=7, segs=8, mat=skin)
	paint(m, fat, lambda c, n: c[:, 0] < 0.012 * s)
	el = 52
	flat = loft(rng, 0.09 * s, lambda t: 0.03 * s * (0.75 + 0.35 * math.sin(math.pi * t)), lambda t: 0.008 * s * (1 + 0.3 * math.sin(math.pi * t)),
				rings=7, segs=10, mat=skin)
	for dy in (-0.009 * s, 0.009 * s):
		flat.add(tube(rng, length=0.08 * s, radius=0.0045 * s, segs=5, rings=2, jitter=0.1, mat=skin, axis="x").translate(0.005 * s, dy, 0.006 * s))
	flat.rotate("z", el).translate(0.068 * s, 0.0, 0)
	m.add(flat)
	ex = 0.068 * s + 0.09 * s * math.cos(math.radians(el))
	ey = 0.09 * s * math.sin(math.radians(el))
	tip = prism([(0, -0.016 * s), (0.055 * s, -0.004 * s), (0.06 * s, 0.002 * s), (0, 0.014 * s)], 0.008 * s, z0=-0.004 * s, mat=skin, side_mat=skin, rng=rng,
				jitter=0.003 * s)
	tip.rotate("z", el + 115).translate(ex, ey, 0)
	m.add(tip)
	weld_displace(m, rng, 0.0022 * s, cells=10.0)
	return m


@item("wings", "resources")
def wings(rng):
	cells = [C((232, 190, 166)), C((246, 226, 190)), C((220, 172, 150))]
	m = _wing(rng, 0, 1, 1.4)
	m.add(_wing(rng, 2, 1, 1.35).scale(1, -1, 1).rotate("z", 170).translate(0.17, 0.09, 0.014))
	return ground(m), cells


@item("kelp", "resources")
def kelp(rng):
	cells = [C((92, 110, 42)), C((76, 96, 36)), C((120, 106, 50)), C((104, 92, 44))]
	# bull kelp washed up: a stipe ending in a float bulb, long wavy blades fanning from the bulb, lying flat
	path = np.array([(-0.22 + 0.2 * t, 0.02 * math.sin(t * 5), 0.012) for t in np.linspace(0, 1, 6)])
	m = path_tube(rng, path, radius=0.008, segs=5, jitter=0.1, radius_fn=lambda t: 0.8 + 0.6 * t, mat=2)
	m.add(blob(rng, 0.028, subdiv=1, jitter=0.2, noise=0.15, scale=(1.3, 1, 0.9), mat=3).translate(-0.01, path[-1][1], 0.025))
	for k in range(6):
		a = -38 + 15 * k + (rng.random() - 0.5) * 8
		L = 0.28 + rng.random() * 0.1
		rows = []
		for t in np.linspace(0, 1, 9):
			w = 0.035 * math.sin(math.pi * (0.1 + 0.9 * t)) ** 0.6 + 0.004
			z = 0.012 + 0.008 * math.sin(t * 17 + k) + 0.004 * k
			ripple = 0.008 * math.sin(t * 25 + k * 2)
			rows.append([(t * L, -w, z + ripple), (t * L, 0, z), (t * L, w, z - ripple)])
		bl = from_quads_grid(np.array(rows), mat=k % 2)
		bl.add(bl.copy().flip())
		m.add(bl.rotate("z", a).translate(0.0, path[-1][1], 0.0))
	return ground(m), cells


# ---------------------------------------------------------------- foods


def _plank(rng, w, d, mat, end_mat):
	out = [(-w / 2, -d / 2), (w / 2 - 0.02, -d / 2 - 0.003), (w / 2, -d / 2 + 0.02), (w / 2 + 0.004, d / 2 - 0.02), (w / 2 - 0.02, d / 2), (-w / 2 + 0.01, d / 2 + 0.003),
		   (-w / 2 - 0.004, 0)]
	p = prism(out, 0.022, mat=mat, side_mat=end_mat, rng=rng, jitter=0.006)
	return p


def _roast_leg(rng, meat, crust, bone, size):
	# a roasted haunch: a browned meat lump tapering to a bone with a knob
	m = blob(rng, size, subdiv=2, jitter=0.3, noise=0.18, noise_cells=2.3, scale=(1.45, 1.0, 0.85), mat=meat, flatten_bottom=0.7)

	def taper(p):
		f = np.clip((p[:, 0] - 0.1 * size) / (1.4 * size), 0, 1)
		p[:, 1:] *= (1 - 0.55 * f ** 1.5)[:, None]
		return p
	squash(m, taper)
	paint(m, crust, lambda c, n: (n[:, 2] > 0.55) & (np.sin(c[:, 0] * 90 + c[:, 1] * 40) > 0.2))
	bl = size * 1.1
	m.add(tube(rng, length=bl, radius=size * 0.15, segs=6, rings=2, jitter=0.1, mat=bone, axis="x").translate(size * 1.15, 0, size * 0.05))
	m.add(blob(rng, size * 0.22, subdiv=1, jitter=0.2, noise=0.2, mat=bone).translate(size * 1.15 + bl, 0, size * 0.05))
	return m


ROAST = C((150, 84, 42))
CRUST = C((104, 56, 28))
BONE = P.bone()
HERB = P.leaf_dark()


@item("roasted_fox", "food")
def roasted_fox(rng):
	cells = [ROAST, CRUST, BONE, P.wood_pale(), P.wood_end(), HERB]
	m = _plank(rng, 0.36, 0.2, 3, 4)
	m.add(_roast_leg(rng, 0, 1, 2, 0.07).rotate("z", 12).translate(-0.04, 0, 0.022))
	for k in range(3):
		m.add(leaf(rng, 0.04, 0.015, mat=5, bend=0.1, segments=2).rotate("z", 40 + 70 * k).translate(0.11, -0.05 + 0.02 * k, 0.024))
	return ground(m), cells


@item("roasted_whitetail", "food")
def roasted_whitetail(rng):
	cells = [C((140, 74, 40)), CRUST, BONE, P.wood_pale(), P.wood_end(), HERB, C((190, 120, 80))]
	# a venison leg roast on a board, one end carved to show the pink inside
	m = _plank(rng, 0.46, 0.24, 3, 4)
	leg = _roast_leg(rng, 0, 1, 2, 0.095)
	paint(leg, 6, lambda c, n: n[:, 0] < -0.75)
	m.add(leg.rotate("z", -10).translate(-0.06, 0, 0.022))
	for k in range(2):
		sl = prism([(-0.035, -0.025), (0.035, -0.028), (0.04, 0.02), (-0.03, 0.026)], 0.012, mat=6, side_mat=1, rng=rng, jitter=0.006)
		m.add(sl.rotate("y", 18).translate(-0.17 + 0.012 * k, 0.07, 0.025 + 0.01 * k))
	return ground(m), cells


@item("roasted_lynx", "food")
def roasted_lynx(rng):
	cells = [C((138, 76, 40)), CRUST, P.wood_pale(), C((176, 112, 70)), P.ember()]
	# chunks of roast lynx on a spit stick, charred where they touched the coals
	m = tube(rng, length=0.42, radius=0.008, segs=6, rings=3, jitter=0.1, profile=lambda t: 1.0 - 0.7 * max(0, t - 0.88) / 0.12, mat=2, axis="x")
	for k, x in enumerate((0.12, 0.19, 0.26, 0.33)):
		ch = blob(rng, 0.032 + 0.004 * (k % 2), subdiv=1, jitter=0.5, noise=0.3, scale=(0.9, 1.1, 1.0), mat=0).translate(x, 0, 0)
		paint(ch, 1, lambda c, n: n[:, 2] < -0.4)
		paint(ch, 3, lambda c, n: (np.abs(n[:, 0]) > 0.8))
		m.add(ch)
	return ground(m), cells


@item("roasted_bird", "food")
def roasted_bird(rng):
	cells = [C((176, 110, 52)), C((140, 82, 38)), BONE, P.wood_pale(), P.wood_end(), HERB]
	# a whole roast bird breast-up on a board: plump body, two drumsticks with bone ends, folded wings
	m = _plank(rng, 0.38, 0.26, 3, 4)
	body = blob(rng, 0.085, subdiv=2, jitter=0.25, noise=0.12, scale=(1.35, 1.0, 0.75), mat=0, flatten_bottom=0.6)
	body.add(blob(rng, 0.05, subdiv=1, jitter=0.25, noise=0.15, scale=(1.0, 1.2, 0.6), mat=0).translate(-0.06, 0, 0.04))  # breast ridge
	paint(body, 1, lambda c, n: n[:, 2] < -0.2)
	for s in (-1, 1):
		leg = loft(rng, 0.09, lambda t: 0.03 * (1 - 0.6 * t), lambda t: 0.028 * (1 - 0.6 * t), rings=4, segs=7, mat=0)
		leg.add(tube(rng, length=0.03, radius=0.007, segs=5, rings=1, mat=2, axis="x").translate(0.085, 0, 0))
		leg.rotate("z", s * 25).translate(0.06, s * 0.05, 0.035)
		body.add(leg)
		wing = blob(rng, 0.04, subdiv=1, jitter=0.3, noise=0.2, scale=(1.4, 0.5, 0.6), mat=1).translate(-0.02, s * 0.08, 0.035)
		body.add(wing)
	m.add(body.translate(0, 0, 0.022))
	for k in range(3):
		m.add(leaf(rng, 0.04, 0.015, mat=5, bend=0.1, segments=2).rotate("z", 30 + 100 * k).translate(-0.14, 0.08 - 0.02 * k, 0.024))
	return ground(m), cells


@item("hunter_stew", "food")
def hunter_stew(rng):
	cells = [P.wood_handle(), P.wood_end(), C((118, 70, 34)), C((150, 84, 46)), C((214, 120, 40)), P.leaf_dark(), P.wood_pale()]
	# a carved wooden bowl of thick brown stew: meat chunks, carrot rounds, herbs, a spoon resting in it
	prof = [(0.06, 0.0), (0.075, 0.006), (0.1, 0.03), (0.112, 0.06), (0.116, 0.08), (0.104, 0.082), (0.098, 0.06), (0.08, 0.03), (0.05, 0.018), (0.0, 0.016)]
	m = lathe(prof, segs=12, mat=0, rng=rng, jitter=0.08)
	paint(m, 1, lambda c, n: (c[:, 2] > 0.077))
	surf = lathe([(0.1, 0.064), (0.06, 0.068), (0.0, 0.07)], segs=12, mat=2, rng=rng, jitter=0.15, closed_bottom=False)
	m.add(surf)
	for k in range(7):
		a = rng.random() * 2 * math.pi
		r = 0.02 + rng.random() * 0.06
		if k < 4:
			ch = blob(rng, 0.016, subdiv=1, jitter=0.5, noise=0.3, mat=3)
		elif k < 6:
			ch = lathe([(0.012, 0), (0.012, 0.006)], segs=7, mat=4, rng=rng, jitter=0.1).rotate("x", 70 + rng.random() * 30)
		else:
			ch = leaf(rng, 0.03, 0.012, mat=5, bend=0.1, segments=2)
		m.add(ch.translate(r * math.cos(a), r * math.sin(a), 0.07))
	spoon = tube(rng, length=0.16, radius=0.006, segs=5, rings=2, jitter=0.08, mat=6, axis="x")
	spoon.add(blob(rng, 0.022, subdiv=1, jitter=0.15, noise=0.1, scale=(1.3, 1.0, 0.4), mat=6).translate(-0.005, 0, 0))
	m.add(spoon.rotate("y", -24).rotate("z", 30).translate(0.0, 0.0, 0.07))
	return ground(m), cells


@item("berry_skewers", "food")
def berry_skewers(rng):
	cells = [P.wood_pale(), C((186, 40, 64)), P.blue_berry(), C((120, 52, 136)), P.leaf_green()]
	# two skewers of raspberries, blueberries and dark blackberries, crossed
	m = Mesh()
	for s, rot in ((0, -8), (1, 14)):
		sk = tube(rng, length=0.3, radius=0.004, segs=5, rings=2, jitter=0.06, profile=lambda t: 1.0 - 0.8 * max(0, t - 0.9) / 0.1, mat=0, axis="x")
		for k, x in enumerate(np.linspace(0.07, 0.25, 6)):
			kind = (k + s) % 3
			if kind == 0:
				b = Mesh()
				for j in range(9):  # drupelets
					a = 2 * math.pi * j / 9
					b.add(blob(rng, 0.008, subdiv=0, jitter=0.2, noise=0.1, mat=1).translate(0.012 * math.cos(a), 0.012 * math.sin(a), 0.005 * (j % 3 - 1)))
				b.add(blob(rng, 0.014, subdiv=1, jitter=0.2, noise=0.1, mat=1))
			elif kind == 1:
				b = blob(rng, 0.016, subdiv=1, jitter=0.2, noise=0.1, mat=2)
			else:
				b = blob(rng, 0.015, subdiv=1, jitter=0.5, noise=0.25, scale=(1.2, 1, 1), mat=3)
			sk.add(b.translate(x, 0, 0))
		m.add(sk.rotate("z", rot).translate(0, 0.03 * s, 0.03 * s))
	return ground(m), cells


@item("radish", "food")
def radish(rng):
	cells = [C((206, 54, 76)), C((236, 226, 214)), P.leaf_green(), P.stem_green(), P.leaf_dark()]
	# a garden radish (~2.5x life size): round red root, white taproot tip, a fan of green leaves, lying on its side
	root = lathe([(0.0, -0.06), (0.006, -0.045), (0.018, -0.02), (0.038, 0.0), (0.046, 0.025), (0.04, 0.05), (0.022, 0.065), (0.0, 0.07)],
				 segs=10, mat=0, rng=rng, jitter=0.12)
	paint(root, 1, lambda c, n: c[:, 2] < -0.025)
	for k in range(5):
		st = tube(rng, length=0.1, radius=0.004, segs=4, rings=2, jitter=0.1, mat=3)
		lf = broad_leaf(rng, 0.1, 0.05, mat=2 if k % 2 else 4, segments=4, cols=3, bend=0.15, droop=0.2).rotate("x", 90).translate(0, 0, 0.09)
		st.add(lf)
		st.rotate("x", 10 + rng.random() * 18).rotate("z", 360 * k / 5).translate(0, 0, 0.06)
		root.add(st)
	root.rotate("y", 80)
	return ground(root), cells


@item("raspberry", "food", roughness=0.6)
def raspberry(rng):
	cells = [C((192, 40, 68)), C((172, 32, 58)), P.leaf_green(), P.stem_green()]
	# a little heap of raspberries (~2.5x life size), each a cone of drupelets, one with its calyx left on
	m = Mesh()
	for (x, y, z, tilt) in ((0, 0, 0.0, 10), (0.055, 0.02, 0.0, 70), (-0.04, 0.04, 0.0, 100), (0.01, -0.05, 0.0, 85), (0.015, 0.015, 0.045, 30)):
		b = Mesh()
		for ring, (rz, rr, n) in enumerate(((0.0, 0.022, 9), (0.016, 0.02, 8), (0.03, 0.013, 6), (0.04, 0.0, 1))):
			for j in range(n):
				a = 2 * math.pi * j / n + ring * 0.3
				b.add(blob(rng, 0.0085, subdiv=0, jitter=0.25, noise=0.1, mat=ring % 2).translate(rr * math.cos(a), rr * math.sin(a), rz))
		b.add(blob(rng, 0.018, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 1.3), mat=0).translate(0, 0, 0.018))
		b.rotate("x", tilt).rotate("z", rng.random() * 360).translate(x, y, z + 0.03)
		m.add(b)
	cal = Mesh()
	for k in range(5):
		cal.add(leaf(rng, 0.022, 0.01, mat=2, bend=0.0, segments=2).rotate("x", -12).rotate("z", 72 * k))
	cal.add(tube(rng, length=0.015, radius=0.002, segs=4, rings=1, mat=3))
	m.add(cal.translate(0.015, 0.015, 0.105))
	return ground(m), cells


# ---------------------------------------------------------------- seed pouches


def _logo_root(mat_root, mat_leaf, mat_tip=None, shape="carrot"):
	def fn(rng):
		if shape == "carrot":
			r = tube(rng, length=0.07, radius=0.012, segs=5, rings=2, jitter=0.1, profile=lambda t: 1.0 - 0.9 * t, mat=mat_root).rotate("x", 180)
			r.translate(0, 0, 0.11)
			for a in (-25, 0, 25):
				r.add(leaf(rng, 0.03, 0.01, mat=mat_leaf, bend=0.0, segments=2).rotate("x", 90).rotate("y", a).translate(0, 0, 0.11))
		elif shape == "radish":
			r = blob(rng, 0.017, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 0.5, 1), mat=mat_root).translate(0, 0, 0.075)
			r.add(tube(rng, length=0.02, radius=0.003, segs=4, rings=1, mat=mat_tip).rotate("x", 180).translate(0, 0, 0.062))
			for a in (-22, 22):
				r.add(leaf(rng, 0.03, 0.014, mat=mat_leaf, bend=0.0, segments=2).rotate("x", 90).rotate("y", a).translate(0, 0, 0.09))
		else:  # potato
			r = blob(rng, 0.02, subdiv=1, jitter=0.3, noise=0.2, scale=(1.4, 0.5, 1), mat=mat_root).translate(0, 0, 0.08)
			r.add(blob(rng, 0.004, subdiv=0, mat=mat_tip).translate(0.01, -0.01, 0.085))
		return r.translate(0, -0.072, 0)
	return fn


def _seed_item(name, extra_cells, logo):
	@item(name, "seeds")
	def build(rng):
		m, cells = _pouch(rng, logo)
		return m, cells + extra_cells
	build.__name__ = name
	return build


ORANGE = C((226, 120, 36))
RED = C((200, 46, 50))
GREEN = C((66, 128, 56))
DKGREEN = C((44, 92, 46))
POTATO = C((176, 136, 86))
PALE = C((232, 220, 196))
AGAVE = C((112, 150, 110))

# pouch cells 0-4 are [sack, rope, seed_brown, leaf_green, wheat]; extras start at 5

_seed_item("carrot_seed", [ORANGE, GREEN], _logo_root(5, 6, shape="carrot"))
_seed_item("radish_seed", [RED, GREEN, PALE], _logo_root(5, 6, 7, shape="radish"))
_seed_item("potato_seed", [POTATO, C((120, 90, 56))], _logo_root(5, 5, 6, shape="potato"))


def _logo_pepper(rng):
	m = path_tube(rng, np.array([(0.0, 0, 0.11), (0.006, 0, 0.09), (0.008, 0, 0.07), (0.0, 0, 0.05), (-0.012, 0, 0.04)]), radius=0.009, segs=5,
				  radius_fn=lambda t: 1.0 - 0.85 * t, mat=5)
	m.add(tube(rng, length=0.015, radius=0.003, segs=4, rings=1, mat=6).translate(0, 0, 0.11))
	return m.translate(0, -0.075, 0)


_seed_item("pepper_seed", [RED, GREEN], _logo_pepper)


def _logo_raspberry(rng):
	m = Mesh()
	for j, (dx, dz) in enumerate(((-0.01, 0.07), (0.01, 0.07), (0.0, 0.085), (-0.012, 0.09), (0.012, 0.09), (0.0, 0.1), (0.0, 0.06))):
		m.add(blob(rng, 0.008, subdiv=0, jitter=0.2, noise=0.1, scale=(1, 0.5, 1), mat=5).translate(dx, -0.072, dz))
	m.add(leaf(rng, 0.022, 0.012, mat=6, bend=0.0, segments=2).rotate("x", 90).translate(0, -0.072, 0.105))
	return m


_seed_item("raspberry_seed", [C((192, 40, 68)), GREEN], _logo_raspberry)


def _logo_watermelon(rng):
	# a melon slice: green rind arc, pale band, red flesh with dark seeds, standing in the pouch face plane
	out = [(0.035 * math.cos(a), 0.035 * math.sin(a)) for a in np.linspace(math.pi, 2 * math.pi, 8)]
	rind = prism([(-0.037, 0.002)] + [(x * 1.07, y * 1.07) for x, y in out] + [(0.037, 0.002)], 0.006, mat=6, side_mat=6)
	flesh = prism([(-0.03, 0.003)] + [(x * 0.86, y * 0.86) for x, y in out[1:-1]] + [(0.03, 0.003)], 0.006, z0=0.003, mat=5, side_mat=5)
	rind.add(flesh)
	for dx in (-0.012, 0.0, 0.012):
		rind.add(blob(rng, 0.0035, subdiv=0, mat=7).translate(dx, -0.012, 0.01))
	rind.rotate("x", 90).translate(0, -0.07, 0.1)
	return rind


_seed_item("watermelon_seed", [C((212, 64, 70)), C((70, 132, 60)), P.black()], _logo_watermelon)


def _logo_cactus(rng):
	m = tube(rng, length=0.07, radius=0.009, segs=6, rings=2, jitter=0.1, profile=lambda t: 1.0 - 0.2 * t ** 4, mat=5).translate(0, 0, 0.04)
	for s, z, h in ((-1, 0.07, 0.03), (1, 0.06, 0.025)):
		arm = path_tube(rng, np.array([(0, 0, z), (s * 0.016, 0, z), (s * 0.02, 0, z + 0.008), (s * 0.02, 0, z + h)]), radius=0.006, segs=5, mat=5)
		m.add(arm)
	return m.translate(0, -0.072, 0)


_seed_item("cacti_seed", [GREEN], _logo_cactus)


def _logo_aloe(rng):
	m = Mesh()
	for k, a in enumerate((-50, -25, 0, 25, 50)):
		m.add(leaf(rng, 0.05 - 0.008 * abs(k - 2), 0.012, mat=5, bend=0.15, segments=3).rotate("x", 90).rotate("y", a).translate(0, 0, 0.055))
	return m.translate(0, -0.074, 0)


_seed_item("aloe_seed", [AGAVE], _logo_aloe)


def _logo_joshua(rng):
	# a joshua tree: forked trunk with spiky leaf tufts on the branch ends
	m = path_tube(rng, np.array([(0, 0, 0.04), (0, 0, 0.07), (0.0, 0, 0.075)]), radius=0.005, segs=4, mat=6)
	for s in (-1, 1):
		m.add(path_tube(rng, np.array([(0, 0, 0.07), (s * 0.012, 0, 0.085), (s * 0.02, 0, 0.1)]), radius=0.004, segs=4, mat=6))
		for k in range(4):
			m.add(leaf(rng, 0.016, 0.005, mat=5, bend=0.0, segments=1).rotate("x", 90).rotate("y", -60 + 40 * k).translate(s * 0.02, 0, 0.1))
	return m.translate(0, -0.073, 0)


_seed_item("joshua_seed", [AGAVE, C((120, 96, 66))], _logo_joshua)


# ---------------------------------------------------------------- fishing rod and lantern


@item("fishing_rod", "tools", surface="wood")
def fishing_rod(rng):
	cells = [C((128, 92, 50)), C((176, 140, 96)), P.metal(), P.black(), C((230, 230, 220)), C((150, 40, 34))]
	# a 1.9 m two-piece rod: cork grip at the butt, a little spinning reel, line guides, tapered tip and slack line
	L = 1.9
	m = tube(rng, length=L, radius=0.011, segs=6, rings=10, jitter=0.04, wobble=0.0, profile=lambda t: 1.0 - 0.82 * t, mat=0, axis="x")
	m.add(tube(rng, length=0.28, radius=0.016, segs=7, rings=3, jitter=0.08, profile=lambda t: 1.0 + 0.15 * math.sin(math.pi * t), mat=1, axis="x").translate(0.02, 0, 0))
	m.add(tube(rng, length=0.02, radius=0.017, segs=7, rings=1, mat=3, axis="x").translate(0.0, 0, 0))
	# reel under the rod (toward -Z): a foot, a spool drum, a crank handle
	foot = box(size=(0.05, 0.012, 0.03), center=(0.33, 0, -0.02), mat=2)
	drum = lathe([(0.03, 0.0), (0.034, 0.008), (0.034, 0.03), (0.026, 0.038)], segs=9, mat=2, rng=rng, jitter=0.06).rotate("y", -90).translate(0.36, 0, -0.06)
	crank = box(size=(0.008, 0.05, 0.008), center=(0.34, 0.03, -0.06), mat=3)
	knob = blob(rng, 0.008, subdiv=0, mat=3).translate(0.34, 0.055, -0.06)
	m.add(foot).add(drum).add(crank).add(knob)
	# guides: small rings under the rod, getting smaller toward the tip
	for k, x in enumerate((0.62, 0.95, 1.25, 1.5, 1.72)):
		r = 0.016 * (1 - 0.13 * k)
		ring = torus(rng, r, 0.0022, n=8, segs=4, mat=2, axis="x").translate(x, 0, -0.012 - r)
		m.add(ring)
	# line from the reel along the guides to the tip, then a loose hanging bit with a red-white bobber
	line_pts = [(0.36, 0, -0.05)] + [(x, 0, -0.012 - 0.016 * (1 - 0.13 * k) * 1.3) for k, x in enumerate((0.62, 0.95, 1.25, 1.5, 1.72))] + [(L, 0, -0.003)]
	for t in np.linspace(0, 1, 5)[1:]:
		line_pts.append((L - 0.06 * t, 0.02 * t, -0.003 - 0.16 * math.sin(math.pi * t * 0.5)))
	m.add(path_tube(rng, np.array(line_pts), radius=0.0016, segs=3, jitter=0.0, mat=4))
	bob = blob(rng, 0.018, subdiv=1, jitter=0.1, noise=0.05, mat=5)
	paint(bob, 4, lambda c, n: c[:, 2] < 0)
	m.add(bob.translate(L - 0.06, 0.02, -0.18))
	return butt_at_origin(m), cells


@item("lantern", "tools", surface="metal", roughness=0.5)
def lantern(rng):
	cells = [C((58, 60, 64)), C((96, 98, 104)), C((250, 206, 110)), C((255, 236, 170)), C((224, 236, 232), 6), C((150, 110, 60))]
	# a hurricane lantern, standing (~0.32 m with the bail up): fuel tank, wire cage around a glass globe,
	# a vented cap and a wire bail handle
	tank = lathe([(0.07, 0.0), (0.078, 0.01), (0.08, 0.035), (0.07, 0.05), (0.05, 0.058)], segs=12, mat=0, rng=rng, jitter=0.05)
	globe = lathe([(0.045, 0.058), (0.062, 0.09), (0.066, 0.13), (0.058, 0.17), (0.04, 0.19)], segs=10, mat=4, rng=rng, jitter=0.04,
				  closed_bottom=False, closed_top=False)
	flame = blob(rng, 0.022, subdiv=1, jitter=0.2, noise=0.15, scale=(0.8, 0.8, 1.6), mat=2).translate(0, 0, 0.11)
	flame.add(blob(rng, 0.012, subdiv=1, jitter=0.15, noise=0.1, scale=(0.8, 0.8, 1.4), mat=3).translate(0, 0, 0.105))
	cap = lathe([(0.042, 0.19), (0.055, 0.2), (0.05, 0.215), (0.03, 0.235), (0.02, 0.245), (0.0, 0.25)], segs=10, mat=0, rng=rng, jitter=0.05)
	m = tank.add(globe).add(flame).add(cap)
	for k in range(4):  # cage wires
		a = 2 * math.pi * k / 4 + 0.4
		path = np.array([(r * math.cos(a), r * math.sin(a), z) for r, z in ((0.05, 0.056), (0.072, 0.09), (0.076, 0.13), (0.066, 0.17), (0.045, 0.192))])
		m.add(path_tube(rng, path, radius=0.0032, segs=4, jitter=0.0, mat=1))
	m.add(torus(rng, 0.075, 0.003, n=12, segs=4, mat=1).translate(0, 0, 0.13))
	bail = np.array([(0.058 * math.cos(t), 0.0, 0.2 + 0.09 * math.sin(t)) for t in np.linspace(0, math.pi, 11)])
	m.add(path_tube(rng, bail, radius=0.003, segs=4, jitter=0.0, mat=1))
	knob = lathe([(0.012, 0.0), (0.012, 0.01)], segs=7, mat=5).rotate("y", 90).translate(0.07, 0.0, 0.02)
	m.add(knob)
	return ground(m), cells
