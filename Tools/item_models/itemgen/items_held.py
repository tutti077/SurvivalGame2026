"""Held tools and weapons in the weapons frame (Blender/scripts/import_artist_pack.py, weapons):
handle along +X, head / tip at +X, butt of the handle at the origin. Code/Player/HeldModelFit.cs
reads the longest axis as the handle and the wider cross axis as the blade edge, so heads are
modelled wide along Z and thin along Y."""
import math
import numpy as np

from . import palette as P
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace, card

ITEMS = []


def item(name, category, surface="default", roughness=0.9, specular=1.0):
	def deco(fn):
		ITEMS.append(dict(name=name, category=category, build=fn, surface=surface, roughness=roughness, specular=specular))
		return fn
	return deco


def butt_at_origin(m):
	lo, hi = m.bounds()
	return m.translate(-lo[0], -(lo[1] + hi[1]) / 2, -(lo[2] + hi[2]) / 2)


def handle(rng, length, radius, mat=0, cap_mat=None, segs=7, rings=5, wobble=0.1, profile=None):
	return tube(rng, length=length, radius=radius, segs=segs, rings=rings, jitter=0.08, wobble=wobble,
				profile=profile or (lambda t: 1.0 + 0.1 * math.sin(t * 9)), mat=mat, cap_mat=cap_mat, axis="x")


def lashing(rng, x, radius, width=0.04, mat=0, turns=5):
	"""Rope wound around a handle at x: a few stacked rings, slightly offset, as one lathe."""
	prof = []
	for i in range(turns + 1):
		prof.append((radius * (1.18 + 0.06 * (i % 2)), i * width / turns))
	m = lathe(prof, segs=8, mat=mat, rng=rng, jitter=0.12, closed_top=True, closed_bottom=True)
	return m.rotate("y", 90).translate(x, 0, 0)


# ---------------------------------------------------------------- tools


@item("torch", "tools", surface="wood", roughness=0.85)
def torch(rng):
	cells = [P.wood_handle(), P.cloth_cream(meters=0.12), P.fire(meters=0.12), P.ember(meters=0.12), P.rope(meters=0.08)]
	m = handle(rng, 0.62, 0.018, mat=0)
	# cloth head wrapped around the top 14 cm, charred at the very end
	wrap = lathe([(0.02, 0), (0.034, 0.015), (0.036, 0.06), (0.034, 0.11), (0.028, 0.135)], segs=8, mat=1, rng=rng, jitter=0.12)
	wrap.rotate("y", 90).translate(0.5, 0, 0)
	c = wrap.centroids()
	wrap.mat[c[:, 0] > 0.6] = 3
	m.add(wrap)
	m.add(lashing(rng, 0.46, 0.02, width=0.03, mat=4, turns=4))
	# flame: a tall jittery blob with two licks, off the charred end
	flame = blob(rng, 0.05, subdiv=1, jitter=0.35, noise=0.3, scale=(1.5, 0.8, 0.8), mat=2)
	flame.translate(0.68, 0, 0)
	for a, r in ((0.9, 0.025), (-1.2, 0.02)):
		lick = blob(rng, r, subdiv=1, jitter=0.3, noise=0.3, scale=(1.6, 0.7, 0.7), mat=2)
		lick.translate(0.72 + r, 0.02 * math.sin(a), 0.035 * math.cos(a))
		m.add(lick)
	m.add(flame)
	return butt_at_origin(m), cells


@item("stone_axe", "tools", surface="wood")
def stone_axe(rng):
	cells = [P.wood_handle(), P.stone(meters=0.15), P.rope(meters=0.06), P.wood_end(meters=0.1)]
	m = handle(rng, 0.72, 0.022, mat=0, cap_mat=3, profile=lambda t: 1.0 + 0.25 * t + 0.08 * math.sin(t * 11))
	# head: a knapped wedge in the XZ plane - fat at the haft, flaring to an edge at +Z, thin in Y
	outline = [(-0.05, -0.05), (0.05, -0.05), (0.07, 0.0), (0.085, 0.08), (0.06, 0.15), (0.0, 0.17), (-0.06, 0.15),
			   (-0.085, 0.08), (-0.07, 0.0)]
	head = prism(outline, 0.06, z0=-0.03, mat=1, side_mat=1, rng=rng, jitter=0.012)
	head.rotate("x", 90)  # extrusion (was Z) now runs along -Y: the plane of the wedge is XZ
	pts = head.tris.reshape(-1, 3)
	f = np.clip((pts[:, 2] + 0.05) / 0.22, 0, 1)
	pts[:, 1] *= (1 - 0.8 * f ** 1.6)  # thin toward the edge
	head.tris = pts.reshape(-1, 3, 3)
	head.translate(0.64, 0, 0.02)
	m.add(head)
	m.add(lashing(rng, 0.58, 0.028, width=0.05, mat=2, turns=6))
	m.add(lashing(rng, 0.655, 0.028, width=0.035, mat=2, turns=4))
	return butt_at_origin(m), cells


@item("build_hammer", "tools", surface="wood")
def build_hammer(rng):
	cells = [P.wood_handle(), P.bark_dark(meters=0.2), P.wood_end(meters=0.15), P.rope(meters=0.06)]
	m = handle(rng, 0.78, 0.024, mat=0, cap_mat=2)
	head = box(size=(0.11, 0.1, 0.34), center=(0.73, 0, 0), mat=1, jitter=0.06, rng=rng)
	# end grain on the two striking faces (+/-Z)
	n = head.normals()
	head.mat[np.abs(n[:, 2]) > 0.7] = 2
	m.add(head)
	m.add(lashing(rng, 0.6, 0.026, width=0.05, mat=3, turns=6))
	return butt_at_origin(m), cells


@item("hoe", "tools", surface="wood")
def hoe(rng):
	cells = [P.wood_handle(), P.metal(meters=0.15), P.rope(meters=0.06)]
	m = handle(rng, 1.15, 0.018, mat=0, rings=7)
	# blade: a flat plate hanging off the end, swept forward (toward +Z) and angled back toward the user
	plate = box(size=(0.012, 0.2, 0.17), center=(0, 0, 0.11), mat=1, jitter=0.05, rng=rng)
	plate.rotate("y", -62).translate(1.14, 0, 0.0)
	m.add(plate)
	neck = tube(rng, length=0.07, radius=0.012, segs=6, rings=2, jitter=0.1, mat=1, axis="x").rotate("y", -62).translate(1.1, 0, -0.01)
	m.add(neck)
	m.add(lashing(rng, 1.03, 0.02, width=0.045, mat=2, turns=5))
	return butt_at_origin(m), cells


@item("grapple", "tools", surface="metal", roughness=0.7)
def grapple(rng):
	cells = [P.metal(meters=0.12), P.rope(meters=0.06), P.metal(meters=0.08)]
	# shaft with an eye ring at the butt, three hooked prongs curving back from the head
	m = tube(rng, length=0.22, radius=0.014, segs=6, rings=3, jitter=0.08, mat=0, axis="x").translate(0.1, 0, 0)
	for k in range(3):
		a = 2 * math.pi * k / 3 + 0.5
		d = np.array([0.0, math.sin(a), math.cos(a)])
		path = []
		for t in np.linspace(0, 1, 8):
			th = t * math.radians(165)
			r = 0.07
			p = np.array([0.3, 0, 0]) + np.array([r * math.sin(th), 0, 0]) + d * (r * (1 - math.cos(th)))
			path.append(p)
		m.add(path_tube(rng, np.array(path), radius=0.011, segs=5, jitter=0.1, radius_fn=lambda t: 1.1 - 0.75 * t, mat=0))
	# eye: a ring in the YZ plane at the butt
	ring = []
	for t in np.linspace(0, 2 * math.pi, 13):
		ring.append((0.095, 0.03 * math.cos(t), 0.03 * math.sin(t)))
	m.add(path_tube(rng, np.array(ring), radius=0.008, segs=5, jitter=0.05, cap=False, mat=2))
	# a short length of rope through the eye, trailing toward the butt
	rp = [(0.085 - t * 0.085, 0.01 * math.sin(t * 9), -0.03 + 0.012 * math.sin(t * 6)) for t in np.linspace(0, 1, 8)]
	m.add(path_tube(rng, np.array(rp), radius=0.009, segs=5, jitter=0.1, mat=1))
	return butt_at_origin(m), cells


# ---------------------------------------------------------------- weapons


@item("wooden_club", "weapons", surface="wood")
def wooden_club(rng):
	cells = [P.wood_handle(), P.stem_green(meters=0.08), P.flint(meters=0.08), P.wood_end(meters=0.1)]
	prof = [(0.022, 0.0), (0.024, 0.1), (0.022, 0.2), (0.03, 0.32), (0.045, 0.45), (0.055, 0.56), (0.05, 0.64), (0.03, 0.68)]
	m = lathe(prof, segs=8, mat=0, rng=rng, jitter=0.1, cap_mat=3)
	m.rotate("y", 90)
	# vine wraps around the thick end, flint chips poking out between them
	for x, r in ((0.4, 0.04), (0.5, 0.051), (0.58, 0.054)):
		m.add(lashing(rng, x, r, width=0.03, mat=1, turns=4))
	for k in range(7):
		a = 2 * math.pi * k / 7 + rng.random() * 0.5
		x = 0.44 + 0.03 * (k % 3) + rng.random() * 0.02
		chip = blob(rng, 0.016, subdiv=0, jitter=0.5, noise=0.3, scale=(1.0, 0.6, 1.8), mat=2)
		r = 0.052
		chip.rotate("x", math.degrees(a)).translate(x, r * math.sin(a) * -1, r * math.cos(a))
		m.add(chip)
	return butt_at_origin(m), cells


@item("flint_spear", "weapons", surface="wood")
def flint_spear(rng):
	cells = [P.wood_handle(), P.flint(meters=0.1), P.rope(meters=0.05)]
	m = handle(rng, 1.45, 0.016, mat=0, rings=8, profile=lambda t: 1.0 + 0.05 * math.sin(t * 13))
	# flint point: a knapped leaf shape, wide in Z, thin in Y
	pt = blob(rng, 0.07, subdiv=1, jitter=0.5, noise=0.25, scale=(1.9, 0.3, 0.7), mat=1)
	pts = pt.tris.reshape(-1, 3)
	f = np.clip(pts[:, 0] / 0.13, 0, 1)
	pts[:, 1:] *= (1 - 0.85 * f ** 1.3)[:, None]
	pt.tris = pts.reshape(-1, 3, 3)
	pt.translate(1.5, 0, 0)
	m.add(pt)
	m.add(lashing(rng, 1.38, 0.02, width=0.07, mat=2, turns=8))
	return butt_at_origin(m), cells


@item("bola", "weapons", surface="rock")
def bola(rng):
	cells = [P.rope(meters=0.06), P.stone(meters=0.12), P.leather_dark(meters=0.08)]
	m = Mesh()
	knot = blob(rng, 0.03, subdiv=1, jitter=0.3, noise=0.2, mat=2)
	m.add(knot)
	for k in range(3):
		a = math.radians(90 + 120 * k)
		d = np.array([math.cos(a), 0.0, math.sin(a)])
		path = [tuple(d * (0.03 + 0.3 * t) + (0, 0.015 * math.sin(t * 7), 0)) for t in np.linspace(0, 1, 7)]
		m.add(path_tube(rng, np.array(path), radius=0.009, segs=5, jitter=0.12, mat=0))
		ball = blob(rng, 0.055, subdiv=1, jitter=0.3, noise=0.18, mat=1)
		ball.translate(*(d * 0.37))
		m.add(ball)
	m.rotate("y", 90)  # the Y lies along the handle axis convention: long axis X
	return butt_at_origin(m), cells


@item("simple_bow", "weapons", surface="wood")
def simple_bow(rng):
	cells = [P.wood_handle(), P.leather(meters=0.1), P.rope(meters=0.04)]
	L = 1.25
	path = []
	for t in np.linspace(0, 1, 17):
		# recurve-ish D: belly toward +Z, flattening near the tips
		z = 0.16 * math.sin(math.pi * t) ** 0.9
		path.append((t * L, 0, z))
	m = path_tube(rng, np.array(path), radius=0.016, segs=6, jitter=0.1,
				  radius_fn=lambda t: 0.5 + 0.8 * math.sin(math.pi * t) ** 0.6, mat=0)
	# string: straight tip to tip
	string = tube(rng, length=L - 0.04, radius=0.003, segs=4, rings=2, jitter=0.0, wobble=0.0, profile=lambda t: 1.0, mat=2, axis="x")
	m.add(string.translate(0.02, 0, 0.004))
	# grip wrap at the centre
	grip = lathe([(0.02, 0), (0.026, 0.01), (0.026, 0.14), (0.02, 0.15)], segs=7, mat=1, rng=rng, jitter=0.1)
	m.add(grip.rotate("y", 90).translate(L / 2 - 0.075, 0, 0.16))
	return butt_at_origin(m), cells


@item("bone_sword", "weapons", surface="default")
def bone_sword(rng):
	cells = [P.bone(meters=0.15), P.bone_dirty(meters=0.1), P.leather_dark(meters=0.06), P.rope(meters=0.04)]
	# a sword carved from one long bone: flat blade, with the vertebra spines left on the back edge
	rows = []
	n = 9
	for i, t in enumerate(np.linspace(0, 1, n)):
		x = 0.22 + t * 0.6
		w = 0.03 * (1 - t ** 3.5) + 0.002 * (1 - t)
		th = 0.013 * (1 - 0.75 * t)
		rows.append([(x, -th / 2, -w), (x, -th, 0), (x, -th / 2, w), (x, th / 2, w), (x, th, 0), (x, th / 2, -w)])
	g = np.array(rows)
	g = g + (rng.random(g.shape) - 0.5) * 0.004
	blade = from_quads_grid(g, closed_v=True, mat=0)
	blade.add(fan(g[0][::-1], mat=1))
	blade.add(fan(g[-1], mat=0))
	m = blade
	# jagged spine along the back edge (+Z): a row of swept-back bony teeth, shrinking toward the tip
	for i in range(9):
		t = i / 9
		x = 0.26 + t * 0.5
		h = 0.034 * (1 - 0.6 * t) + 0.004 * ((i % 2) * 2 - 1)
		tooth = prism([(-0.014, 0), (0.016, 0), (-0.028, h)], 0.009, mat=1, side_mat=1, rng=rng, jitter=0.003)
		tooth.rotate("x", 90).translate(x, 0.0045, 0.03 * (1 - t ** 3.5) - 0.004)
		m.add(tooth)
	# guard: a knobbly bone cross piece, grip wrapped in leather and twine, bone knob pommel
	guard = blob(rng, 0.03, subdiv=1, jitter=0.3, noise=0.2, scale=(0.9, 0.9, 2.6), mat=1).translate(0.215, 0, 0)
	m.add(guard)
	grip = lathe([(0.015, 0), (0.018, 0.03), (0.016, 0.09), (0.019, 0.15), (0.016, 0.2)], segs=7, mat=2, rng=rng, jitter=0.1)
	m.add(grip.rotate("y", 90).translate(0.02, 0, 0))
	m.add(lashing(rng, 0.07, 0.016, width=0.03, mat=3, turns=4))
	m.add(lashing(rng, 0.14, 0.016, width=0.03, mat=3, turns=4))
	pommel = blob(rng, 0.028, subdiv=1, jitter=0.25, noise=0.15, scale=(0.9, 1.2, 1.2), mat=1).translate(0.02, 0, 0)
	m.add(pommel)
	return butt_at_origin(m), cells


def _arrow(rng, head_cells, head_fn, length=0.78):
	cells = [P.wood_pale(meters=0.1), P.feather_white(meters=0.05), P.cloth_red(meters=0.05)] + head_cells
	m = tube(rng, length=length, radius=0.0065, segs=5, rings=3, jitter=0.06, wobble=0.05, profile=lambda t: 1.0, mat=0, axis="x")
	# nock: a small notch block at the butt
	nock = box(size=(0.02, 0.012, 0.014), center=(0.01, 0, 0), mat=0)
	m.add(nock)
	# three fletching vanes: flat cards along the shaft near the nock, red tipped white
	for k in range(3):
		a = 2 * math.pi * k / 3
		vane = card(0.11, 0.028, mat=1, double=True)  # width along x, height along z, facing +y
		vane.translate(0.055 + 0.015, 0, 0.004)
		# red rear third
		c = vane.centroids()
		vane.mat[c[:, 0] < 0.045] = 2
		vane.rotate("x", math.degrees(a))
		m.add(vane)
	m.add(head_fn(rng, length))
	return butt_at_origin(m), cells


@item("wood_arrow", "weapons", surface="wood")
def wood_arrow(rng):
	def head(rng, L):
		return tube(rng, length=0.06, radius=0.0065, segs=5, rings=2, jitter=0.05, profile=lambda t: 1.0 - 0.95 * t, mat=0,
					cap_mat=0, axis="x").translate(L, 0, 0)
	return _arrow(rng, [], head)


@item("fire_arrow", "weapons", surface="wood")
def fire_arrow(rng):
	def head(rng, L):
		h = tube(rng, length=0.05, radius=0.0065, segs=5, rings=2, jitter=0.05, profile=lambda t: 1.0 - 0.9 * t, mat=0, axis="x").translate(L, 0, 0)
		wrap = lathe([(0.008, 0), (0.016, 0.01), (0.017, 0.05), (0.013, 0.065)], segs=7, mat=4, rng=rng, jitter=0.12)
		h.add(wrap.rotate("y", 90).translate(L - 0.06, 0, 0))
		flame = blob(rng, 0.03, subdiv=1, jitter=0.35, noise=0.3, scale=(1.5, 0.9, 1.0), mat=3).translate(L - 0.02, 0, 0.02)
		h.add(flame)
		h.add(blob(rng, 0.018, subdiv=1, jitter=0.35, noise=0.3, scale=(1.4, 0.8, 1.0), mat=3).translate(L + 0.02, 0.01, 0.045))
		return h
	return _arrow(rng, [P.fire(meters=0.08), P.ember(meters=0.08)], head)


@item("flint_arrow", "weapons", surface="wood")
def flint_arrow(rng):
	def head(rng, L):
		pt = blob(rng, 0.03, subdiv=1, jitter=0.5, noise=0.25, scale=(1.9, 0.25, 0.65), mat=3)
		pts = pt.tris.reshape(-1, 3)
		f = np.clip(pts[:, 0] / 0.055, 0, 1)
		pts[:, 1:] *= (1 - 0.85 * f ** 1.3)[:, None]
		pt.tris = pts.reshape(-1, 3, 3)
		pt.translate(L + 0.02, 0, 0)
		pt.add(lashing(rng, L - 0.03, 0.007, width=0.03, mat=4, turns=5))
		return pt
	return _arrow(rng, [P.flint(meters=0.06), P.rope(meters=0.03)], head)
