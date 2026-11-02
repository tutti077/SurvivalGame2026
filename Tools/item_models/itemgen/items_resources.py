"""Resource drops: centred on X/Y, standing on z = 0 (the environment frame)."""
import math
import numpy as np

from . import palette as P
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace, rot_matrix

ITEMS = []


def item(name, category="resources", surface="default", roughness=0.9, specular=1.0):
	def deco(fn):
		ITEMS.append(dict(name=name, category=category, build=fn, surface=surface, roughness=roughness, specular=specular))
		return fn
	return deco


def ground(m):
	lo, hi = m.bounds()
	return m.translate(-(lo[0] + hi[0]) / 2, -(lo[1] + hi[1]) / 2, -lo[2])


# ---------------------------------------------------------------- wood


@item("wood_log", surface="wood")
def wood_log(rng):
	cells = [P.bark(), P.wood_end(), P.bark_dark()]
	m = tube(rng, length=1.0, radius=0.085, segs=9, rings=6, jitter=0.12, wobble=0.25,
			 profile=lambda t: 1.0 + 0.12 * math.sin(t * 7.1) - 0.08 * t, mat=0, cap_mat=1, axis="x")
	# a couple of knots and a stub branch
	for _ in range(2):
		k = blob(rng, 0.045, subdiv=1, jitter=0.3, noise=0.3, mat=2)
		a = rng.random() * 2 * math.pi
		k.translate(0.15 + rng.random() * 0.7, 0.07 * math.cos(a), 0.07 * math.sin(a))
		m.add(k)
	stub = tube(rng, length=0.14, radius=0.028, segs=6, rings=2, jitter=0.2, profile=lambda t: 1 - 0.4 * t, mat=2, cap_mat=1)
	stub.rotate("x", -55).rotate("z", 20).translate(0.62, 0.05, 0.05)
	m.add(stub)
	return ground(m), cells


@item("stone", surface="rock")
def stone(rng):
	cells = [P.stone(), P.stone()]
	m = blob(rng, 0.17, subdiv=2, jitter=0.35, noise=0.22, noise_cells=2.2, scale=(1.15, 1.0, 0.8), mat=0, flatten_bottom=0.75)
	return ground(m), cells


@item("dirt")
def dirt(rng):
	cells = [P.dirt(), P.stone(meters=0.15)]
	m = blob(rng, 0.19, subdiv=2, jitter=0.5, noise=0.3, noise_cells=3.0, scale=(1.2, 1.05, 0.55), mat=0, flatten_bottom=0.6)
	for _ in range(5):
		pb = blob(rng, 0.02 + rng.random() * 0.015, subdiv=1, jitter=0.3, noise=0.3, mat=1 if rng.random() < 0.5 else 0)
		a = rng.random() * 2 * math.pi
		r = 0.05 + rng.random() * 0.12
		pb.translate(r * math.cos(a), r * math.sin(a), 0.04 + rng.random() * 0.05)
		m.add(pb)
	return ground(m), cells


@item("resin", roughness=0.18, specular=1.0)
def resin(rng):
	cells = [P.resin()]
	# a sagging drop: taller than wide, bulging low, with two smaller drips fused on the side
	m = blob(rng, 0.075, subdiv=1, jitter=0.22, noise=0.12, noise_cells=1.6, scale=(1.0, 0.92, 1.4), mat=0, flatten_bottom=0.7)
	pts = m.tris.reshape(-1, 3)
	# pinch the top into a teardrop tip
	z = pts[:, 2]
	zmax = z.max()
	f = np.clip((z - 0.0) / max(zmax, 1e-6), 0, 1)
	pts[:, :2] *= (1 - 0.55 * f ** 2.2)[:, None]
	m.tris = pts.reshape(-1, 3, 3)
	for ang, rad in ((0.6, 0.03), (2.9, 0.024)):
		d = blob(rng, rad, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 1.2), mat=0)
		d.translate(0.062 * math.cos(ang), 0.062 * math.sin(ang), rad * 0.8)
		m.add(d)
	return ground(m), cells


@item("plant_fiber")
def plant_fiber(rng):
	cells = [P.leaf_green(), P.leaf_dark(), P.rope(meters=0.08)]
	m = Mesh()
	n = 16
	for i in range(n):
		L = 0.26 + rng.random() * 0.12
		lf = leaf(rng, length=L, width=0.03 + rng.random() * 0.025, mat=i % 2, bend=0.25 + rng.random() * 0.35, segments=5)
		yaw = 360 * i / n + (rng.random() - 0.5) * 25
		pitch = 50 + rng.random() * 32  # a sheaf: mostly upright, fanning out at the tips
		lf.rotate("z", (rng.random() - 0.5) * 40).rotate("x", pitch).rotate("z", yaw)
		lf.translate(0.012 * math.cos(math.radians(yaw)), 0.012 * math.sin(math.radians(yaw)), 0.0)
		m.add(lf)
	tie = lathe([(0.04, 0.05), (0.047, 0.06), (0.047, 0.078), (0.04, 0.088)], segs=9, mat=2, rng=rng, jitter=0.2)
	m.add(tie)
	return ground(m), cells


@item("flint", surface="rock")
def flint(rng):
	cells = [P.flint(), P.stone(meters=0.2)]
	m = blob(rng, 0.11, subdiv=1, jitter=0.9, noise=0.35, noise_cells=2.0, scale=(1.25, 0.8, 0.5), mat=0)
	# chip facets: pull a few vertices outward hard to get sharp, knapped-looking spikes
	pts = m.tris.reshape(-1, 3)
	key = np.round(pts / 1e-5).astype(np.int64)
	uniq, inv = np.unique(key, axis=0, return_inverse=True)
	push = np.where(rng.random(len(uniq)) < 0.22, 1.0 + rng.random(len(uniq)) * 0.5, 1.0)
	pts *= push[inv][:, None]
	m.tris = pts.reshape(-1, 3, 3)
	m.rotate("y", 12).rotate("z", 25)
	return ground(m), cells


@item("bone")
def bone(rng):
	cells = [P.bone(), P.bone_dirty()]
	shaft = tube(rng, length=0.38, radius=0.03, segs=7, rings=4, jitter=0.1, wobble=0.15,
				 profile=lambda t: 1.0 + 0.5 * (abs(t - 0.5) * 2) ** 3, mat=0, cap_start=False, cap_end=False, axis="x")
	shaft.translate(0.06, 0, 0)
	m = shaft
	for x in (0.04, 0.46):
		for dy in (-0.045, 0.045):
			k = blob(rng, 0.052, subdiv=1, jitter=0.25, noise=0.2, scale=(0.85, 1.0, 1.0), mat=1 if rng.random() < 0.4 else 0)
			k.translate(x, dy, 0)
			m.add(k)
		c = blob(rng, 0.038, subdiv=1, jitter=0.2, noise=0.15, mat=0).translate(x + (0.02 if x < 0.2 else -0.02), 0, 0)
		m.add(c)
	return ground(m), cells


@item("animal_fat", roughness=0.6)
def animal_fat(rng):
	cells = [P.fat()]
	m = blob(rng, 0.11, subdiv=2, jitter=0.4, noise=0.28, noise_cells=2.4, scale=(1.3, 1.0, 0.7), mat=0, flatten_bottom=0.7)
	for _ in range(3):
		b = blob(rng, 0.045 + rng.random() * 0.02, subdiv=1, jitter=0.3, noise=0.25, mat=0)
		a = rng.random() * 2 * math.pi
		b.translate(0.07 * math.cos(a), 0.06 * math.sin(a), 0.07 + rng.random() * 0.04)
		m.add(b)
	return ground(m), cells


def _meat(rng, meat_cell, size):
	cells = [meat_cell, P.bone(), P.fat()]
	# drumstick: a fat meat lump, tapering into a bone with a knob at the end
	m = blob(rng, size, subdiv=2, jitter=0.3, noise=0.2, noise_cells=2.3, scale=(1.45, 1.0, 0.9), mat=0, flatten_bottom=0.75)
	pts = m.tris.reshape(-1, 3)
	x = pts[:, 0]
	f = np.clip((x - 0.1 * size) / (1.4 * size), 0, 1)
	pts[:, 1:] *= (1 - 0.55 * f ** 1.5)[:, None]
	m.tris = pts.reshape(-1, 3, 3)
	# a fat band
	band = blob(rng, size * 0.45, subdiv=1, jitter=0.3, noise=0.2, scale=(0.7, 1.0, 0.8), mat=2).translate(-size * 0.9, 0, size * 0.1)
	m.add(band)
	bone_l = size * 1.3
	b = tube(rng, length=bone_l, radius=size * 0.16, segs=6, rings=2, jitter=0.1, mat=1, axis="x").translate(size * 1.1, 0, size * 0.05)
	m.add(b)
	knob = blob(rng, size * 0.24, subdiv=1, jitter=0.2, noise=0.2, mat=1).translate(size * 1.1 + bone_l, 0, size * 0.05)
	m.add(knob)
	return ground(m), cells


@item("fox_meat")
def fox_meat(rng):
	return _meat(rng, P.meat_red(), 0.075)


@item("whitetail_meat")
def whitetail_meat(rng):
	return _meat(rng, P.meat_pink(), 0.1)


def _pelt_outline(rng, w, l, leg, wiggle=0.01):
	"""Flat pelt: head at +y, four legs, tail at -y. Returns 2D outline points (CCW)."""
	hw = w / 2
	pts = [
		(-0.06 * w, l * 0.5 + 0.08 * l), (0.06 * w, l * 0.5 + 0.08 * l),            # head top (ears)
		(0.18 * w, l * 0.42),                                                              # neck
		(hw * 0.75, l * 0.36), (hw + leg * 0.9, l * 0.42), (hw + leg, l * 0.3), (hw * 0.95, l * 0.2),  # front leg R
		(hw * 0.9, -l * 0.1), (hw + leg, -l * 0.3), (hw + leg * 0.8, -l * 0.42), (hw * 0.7, -l * 0.36),  # hind leg R
		(0.1 * w, -l * 0.46), (0.04 * w, -l * 0.62), (-0.04 * w, -l * 0.62), (-0.1 * w, -l * 0.46),     # tail
		(-hw * 0.7, -l * 0.36), (-hw - leg * 0.8, -l * 0.42), (-hw - leg, -l * 0.3), (-hw * 0.9, -l * 0.1),
		(-hw * 0.95, l * 0.2), (-hw - leg, l * 0.3), (-hw - leg * 0.9, l * 0.42), (-hw * 0.75, l * 0.36),
		(-0.18 * w, l * 0.42),
	]
	return [(x + (rng.random() - 0.5) * wiggle, y + (rng.random() - 0.5) * wiggle) for x, y in pts]


@item("fox_pelt")
def fox_pelt(rng):
	cells = [P.fur_orange(), P.fur_cream(), P.leather_dark(meters=0.3)]
	outline = _pelt_outline(rng, 0.28, 0.5, 0.06)
	m = prism(outline, 0.025, mat=2, side_mat=2)
	top = m.normals()[:, 2] > 0.5
	m.mat[top] = 0
	c = m.centroids()
	belly = top & (np.abs(c[:, 0]) < 0.07) & (c[:, 1] < 0.14) & (c[:, 1] > -0.2)
	m.mat[belly] = 1
	weld_displace(m, rng, 0.006, cells=6.0)
	return ground(m), cells


@item("deer_hide")
def deer_hide(rng):
	cells = [P.hide_brown(), P.leather_dark(meters=0.3)]
	outline = _pelt_outline(rng, 0.42, 0.62, 0.07, wiggle=0.02)
	m = prism(outline, 0.02, mat=1, side_mat=1)
	m.mat[m.normals()[:, 2] > 0.5] = 0
	weld_displace(m, rng, 0.008, cells=5.0)
	return ground(m), cells


@item("jay_feather")
def jay_feather(rng):
	cells = [P.feather_blue(), P.feather_white(), P.black(meters=0.2), P.bone(meters=0.1)]
	L = 0.22
	m = Mesh()
	# quill: thin tube along +x with a slight upward bow
	path = np.array([(t * L, 0, 0.02 * math.sin(math.pi * t)) for t in np.linspace(0, 1, 6)])
	m.add(path_tube(rng, path, radius=0.004, segs=5, radius_fn=lambda t: 1.2 - 0.8 * t, mat=3))
	# vanes: two strips either side of the quill, barred blue/white/black along the length
	ns = 9
	for side in (-1, 1):
		rows = []
		for i, t in enumerate(np.linspace(0.18, 1.0, ns)):
			w = 0.03 * math.sin(math.pi * (0.15 + 0.85 * (t - 0.18) / 0.82)) + 0.004
			x = t * L
			z = 0.02 * math.sin(math.pi * t)
			rows.append([(x, 0, z), (x + 0.012, side * w * 0.6, z + 0.002), (x + 0.02, side * w, z - 0.004)])
		g = from_quads_grid(np.array(rows), mat=0)
		g.add(g.copy().flip())
		m.add(g)
	# colour bands along the vane: white near the quill base, black bars at intervals
	c = m.centroids()
	isvane = m.mat == 0
	band = ((c[:, 0] / L) * 7).astype(int)
	m.mat[isvane & (band % 3 == 2)] = 2
	m.mat[isvane & (c[:, 0] < L * 0.38)] = 1
	m.rotate("z", 20)
	return ground(m), cells


@item("rope")
def rope(rng):
	cells = [P.rope(), P.rope()]
	# a rope coiled flat on the ground: a spiral winding inward, then a second layer spiralling back
	# out on top of it, and a loose tail
	pts = []
	n = 80
	for i in range(n):
		t = i / (n - 1)
		a = 2 * math.pi * 2.6 * t
		r = 0.125 - 0.085 * t
		pts.append((r * math.cos(a), r * math.sin(a), 0.016 + 0.002 * math.sin(a * 5)))
	for i in range(n):
		t = i / (n - 1)
		a = 2 * math.pi * 2.6 * (1 - t) + 2 * math.pi * 2.6 + 0.6
		r = 0.055 + 0.07 * t
		pts.append((r * math.cos(a), r * math.sin(a), 0.045 + 0.003 * math.sin(a * 4)))
	last = np.array(pts[-1])
	for k in range(1, 9):
		pts.append((last[0] + 0.022 * k, last[1] - 0.008 * k, max(0.016, last[2] - 0.006 * k)))
	m = path_tube(rng, np.array(pts), radius=0.016, segs=6, jitter=0.12, mat=0)
	# a short binding wrapped across the coil
	wrap = lathe([(0.04, 0.0), (0.048, 0.012), (0.048, 0.03), (0.04, 0.042)], segs=8, mat=1, rng=rng, jitter=0.15)
	wrap.rotate("y", 90).translate(0.0, 0.1, 0.04)
	m.add(wrap)
	return ground(m), cells


@item("bone_glue", roughness=0.5)
def bone_glue(rng):
	cells = [P.white(), P.Cell((240, 120, 40), noise=0.06), P.blue_dark(), P.bone(meters=0.1)]
	prof = [(0.045, 0.0), (0.056, 0.012), (0.06, 0.05), (0.058, 0.13), (0.05, 0.16), (0.036, 0.175), (0.036, 0.2)]
	body = lathe(prof, segs=10, mat=0, rng=rng, jitter=0.08)
	# label band: triangles in the lower-middle height band
	c = body.centroids()
	body.mat[(c[:, 2] > 0.025) & (c[:, 2] < 0.065)] = 2
	cap = lathe([(0.038, 0.2), (0.04, 0.215), (0.032, 0.235), (0.012, 0.25), (0.012, 0.27), (0.006, 0.285)], segs=8, mat=1, rng=rng, jitter=0.08)
	body.add(cap)
	return ground(body), cells


@item("leather")
def leather(rng):
	cells = [P.leather(), P.leather_dark(), P.leather(meters=0.2)]
	m = Mesh()
	# a folded stack: three slabs, each offset and slightly rotated, with rounded jittered corners
	for i, (w, d, h) in enumerate(((0.3, 0.2, 0.04), (0.29, 0.19, 0.035), (0.27, 0.18, 0.03))):
		z = sum(s[2] for s in ((0.3, 0.2, 0.04), (0.29, 0.19, 0.035), (0.27, 0.18, 0.03))[:i])
		pts = [(-w / 2, -d / 2), (-w / 2 + 0.02, -d / 2 - 0.004), (w / 2 - 0.015, -d / 2), (w / 2, -d / 2 + 0.03), (w / 2, d / 2 - 0.02),
			   (w / 2 - 0.03, d / 2), (-w / 2 + 0.03, d / 2 + 0.003), (-w / 2, d / 2 - 0.03)]
		slab = prism(pts, h, z0=z, mat=0 if i % 2 == 0 else 2, side_mat=1, rng=rng, jitter=0.006)
		slab.rotate("z", (rng.random() - 0.5) * 8)
		m.add(slab)
	weld_displace(m, rng, 0.004, cells=5.0)
	return ground(m), cells
