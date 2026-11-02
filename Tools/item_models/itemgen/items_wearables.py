"""Armor pieces, plants and food, seed pouches, trophies and figurines: centred on X,Y standing on z = 0.
Trophies are wall plaques: the plaque in the XZ plane, the head projecting toward -Y (the viewer)."""
import math
import numpy as np

from . import palette as P
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace, card, polygon_fill

ITEMS = []


def item(name, category, surface="default", roughness=0.9, specular=1.0):
	def deco(fn):
		ITEMS.append(dict(name=name, category=category, build=fn, surface=surface, roughness=roughness, specular=specular))
		return fn
	return deco


def ground(m):
	lo, hi = m.bounds()
	return m.translate(-(lo[0] + hi[0]) / 2, -(lo[1] + hi[1]) / 2, -lo[2])


def feather_card(rng, length, width, mat, tip_mat=None, bend=0.25):
	f = leaf(rng, length=length, width=width, mat=mat, bend=bend, segments=4)
	if tip_mat is not None:
		c = f.centroids()
		f.mat[c[:, 1] > length * 0.7] = tip_mat
	return f


# ---------------------------------------------------------------- armor


@item("cowl", "armor")
def cowl(rng):
	cells = [P.leather(), P.black(), P.yellow(meters=0.05), P.leather_dark()]
	# a hood: bell lathe, open at the bottom, with the front faces cut as the dark face opening
	prof = [(0.17, 0.0), (0.175, 0.06), (0.165, 0.16), (0.14, 0.24), (0.1, 0.3), (0.05, 0.33), (0.0, 0.335)]
	m = lathe(prof, segs=11, mat=0, rng=rng, jitter=0.1, closed_bottom=False, closed_top=True)
	c = m.centroids()
	n = m.normals()
	face = (n[:, 1] < -0.55) & (c[:, 2] > 0.06) & (c[:, 2] < 0.26)
	m.mat[face] = 1
	# push the face triangles inward a touch so the opening reads as a recess
	m.tris[face] = m.tris[face] + np.array([0, 0.03, 0])
	for dx in (-0.045, 0.045):
		eye = blob(rng, 0.018, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 0.6, 1), mat=2).translate(dx, -0.12, 0.17)
		m.add(eye)
	# a seam strip down the back
	seam = box(size=(0.012, 0.01, 0.2), center=(0, 0.17, 0.14), mat=3)
	m.add(seam)
	weld_displace(m, rng, 0.004, cells=4.0)
	return ground(m), cells


@item("headdress", "armor")
def headdress(rng):
	cells = [P.leather(meters=0.2), P.teal(), P.cloth_red(meters=0.1), P.blue_dark(meters=0.1), P.cloth_cream(meters=0.1),
			 P.yellow(meters=0.1), P.feather_white(meters=0.1)]
	# band: a ring with a beaded teal stripe
	band = lathe([(0.11, 0.0), (0.115, 0.02), (0.115, 0.045), (0.11, 0.06)], segs=12, mat=0, rng=rng, jitter=0.08,
				 closed_bottom=False, closed_top=False)
	inner = lathe([(0.1, 0.0), (0.1, 0.06)], segs=12, mat=0, rng=rng, jitter=0.0, closed_bottom=False, closed_top=False).flip()
	band.add(inner)
	c = band.centroids()
	band.mat[(c[:, 2] > 0.022) & (c[:, 2] < 0.04) & (band.mat == 0) & (np.linalg.norm(c[:, :2], axis=1) > 0.105)] = 1
	m = band
	# feathers fanned up from the back half of the band
	colours = [2, 3, 4, 5, 6, 2, 4, 3, 6]
	n = len(colours)
	for i in range(n):
		a = math.radians(180 - 160 * (i / (n - 1)))  # from -x side over the back to +x side
		f = feather_card(rng, 0.28 + rng.random() * 0.06, 0.075, colours[i], tip_mat=None, bend=0.08)
		tilt = 82 - 40 * abs(i / (n - 1) - 0.5) * 2  # centre feathers stand tallest
		f.rotate("x", tilt).rotate("z", math.degrees(a) - 90)
		f.translate(0.1 * math.cos(a), 0.1 * math.sin(a), 0.05)
		m.add(f)
	return ground(m), cells


@item("leather_cap", "armor")
def leather_cap(rng):
	cells = [P.leather_red(), P.leather_dark()]
	prof = [(0.135, 0.0), (0.145, 0.02), (0.14, 0.05), (0.12, 0.09), (0.08, 0.12), (0.03, 0.135), (0.0, 0.138)]
	m = lathe(prof, segs=10, mat=0, rng=rng, jitter=0.1, closed_bottom=False)
	# seam strips over the crown
	for a in (0, 90):
		s = box(size=(0.012, 0.25, 0.012), center=(0, 0, 0.1), mat=1).rotate("z", a)
		m.add(s)
	weld_displace(m, rng, 0.004, cells=4.0)
	return ground(m), cells


@item("leather_chest", "armor")
def leather_chest(rng):
	cells = [P.leather_red(), P.leather_dark(), P.rope(meters=0.06)]
	# a sleeveless tunic silhouette in the XZ plane, extruded through Y
	outline = [(-0.18, 0.0), (0.18, 0.0), (0.2, 0.2), (0.25, 0.3), (0.26, 0.41), (0.15, 0.42), (0.12, 0.34), (0.07, 0.36),
			   (-0.07, 0.36), (-0.12, 0.34), (-0.15, 0.42), (-0.26, 0.41), (-0.25, 0.3), (-0.2, 0.2)]
	m = prism(outline, 0.2, z0=-0.1, mat=0, side_mat=1, rng=rng, jitter=0.01)
	m.rotate("x", 90)
	# round the chest: bulge the front/back faces out in the middle
	pts = m.tris.reshape(-1, 3)
	r = np.clip(1 - (np.abs(pts[:, 0]) / 0.2) ** 2, 0, 1) * np.clip(1 - ((pts[:, 2] - 0.2) / 0.25) ** 2, 0, 1)
	pts[:, 1] *= (1 + 0.35 * r)
	m.tris = pts.reshape(-1, 3, 3)
	# lacing down the front
	for z in np.linspace(0.08, 0.36, 5):
		lace = box(size=(0.08, 0.012, 0.012), center=(0, -0.15, z), mat=2)
		m.add(lace)
	weld_displace(m, rng, 0.004, cells=3.0)
	return ground(m), cells


@item("leather_pants", "armor")
def leather_pants(rng):
	cells = [P.leather_red(), P.leather_dark(), P.rope(meters=0.06)]
	m = Mesh()
	waist = lathe([(0.17, 0.42), (0.18, 0.45), (0.175, 0.5)], segs=10, mat=1, rng=rng, jitter=0.08, closed_bottom=False)
	pts = waist.tris.reshape(-1, 3)
	pts[:, 1] *= 0.7
	waist.tris = pts.reshape(-1, 3, 3)
	m.add(waist)
	for side in (-1, 1):
		leg = tube(rng, length=0.44, radius=0.08, segs=8, rings=5, jitter=0.06, wobble=0.1,
				   profile=lambda t: 0.85 + 0.3 * t, mat=0, cap_mat=1, cap_start=True, cap_end=False)
		leg.scale(1.0, 0.8, 1.0)
		leg.rotate("y", side * 6).translate(side * 0.09, 0, 0.0)
		m.add(leg)
	crotch = blob(rng, 0.09, subdiv=1, jitter=0.2, noise=0.1, scale=(1.3, 0.75, 0.7), mat=0).translate(0, 0, 0.42)
	m.add(crotch)
	belt = box(size=(0.07, 0.012, 0.012), center=(0, -0.13, 0.46), mat=2)
	m.add(belt)
	weld_displace(m, rng, 0.004, cells=3.0)
	return ground(m), cells


@item("bandana", "armor")
def bandana(rng):
	cells = [P.cloth_red(), P.cloth_red(meters=0.15)]
	# folded triangle of cloth lying on the ground, tied in a knot at the back corner
	outline = [(-0.22, 0.12), (0.22, 0.12), (0.2, 0.09), (0.0, -0.2), (-0.2, 0.09)]
	m = prism(outline, 0.012, mat=0, side_mat=1, rng=rng, jitter=0.01)
	# crumple
	pts = m.tris.reshape(-1, 3)
	pts[:, 2] += 0.02 * np.sin(pts[:, 0] * 25) * np.cos(pts[:, 1] * 18) + 0.012 * np.clip(0.12 - pts[:, 1], 0, 1)
	m.tris = pts.reshape(-1, 3, 3)
	knot = blob(rng, 0.035, subdiv=1, jitter=0.3, noise=0.2, scale=(1.4, 0.8, 0.7), mat=0).translate(0.0, 0.12, 0.03)
	m.add(knot)
	for s in (-1, 1):
		tail = leaf(rng, 0.09, 0.04, mat=1, bend=0.4, segments=3).rotate("z", 180 + s * 35).translate(s * 0.02, 0.14, 0.04)
		m.add(tail)
	return ground(m), cells


@item("hand_wraps", "armor")
def hand_wraps(rng):
	cells = [P.cloth_cream(), P.leather(meters=0.12)]
	m = Mesh()
	for side in (-1, 1):
		hand = blob(rng, 0.075, subdiv=1, jitter=0.25, noise=0.14, scale=(0.75, 1.3, 0.45), mat=0, flatten_bottom=0.5)
		hand.translate(side * 0.095, 0, 0.0)
		# wrap bands: brightness stripes come from the cloth cell; add a leather strap across the wrist
		strap = lathe([(0.04, -0.012), (0.044, -0.006), (0.044, 0.008), (0.04, 0.012)], segs=8, mat=1, rng=rng, jitter=0.1)
		pts = strap.tris.reshape(-1, 3)
		pts[:, 2] *= 0.6
		strap.tris = pts.reshape(-1, 3, 3)
		strap.rotate("x", 90).translate(side * 0.095, -0.085, 0.022)
		hand.add(strap)
		# thumb
		th = blob(rng, 0.025, subdiv=1, jitter=0.2, noise=0.1, scale=(0.8, 1.5, 0.7), mat=0).translate(side * 0.155, 0.03, 0.02)
		hand.add(th)
		m.add(hand)
	return ground(m), cells


# ---------------------------------------------------------------- plants, bait, food


@item("night_crawlers", "food")
def night_crawlers(rng):
	cells = [P.dirt(), P.Cell((214, 128, 128), noise=0.08, tones=[(196, 110, 112), (214, 128, 128), (226, 146, 146)], streak="x",
							  stripes=(180, 96, 100), stripe_period=4, stripe_amt=0.5, meters=0.08)]
	m = blob(rng, 0.16, subdiv=2, jitter=0.4, noise=0.25, noise_cells=2.5, scale=(1.3, 1.0, 0.4), mat=0, flatten_bottom=0.5)
	for k in range(3):
		x0 = -0.08 + 0.07 * k
		path = [(x0 + 0.03 * t * 4 * (0.6 if k == 1 else 1), -0.08 + 0.2 * t + 0.03 * math.sin(t * 9 + k),
				 0.045 + 0.03 * abs(math.sin(t * 6 + k * 2))) for t in np.linspace(0, 1, 11)]
		m.add(path_tube(rng, np.array(path), radius=0.013, segs=5, jitter=0.1, radius_fn=lambda t: 0.8 + 0.3 * math.sin(math.pi * t), mat=1))
	return ground(m), cells


def _flower(rng, cells, stem_h=0.32, petals=13, petal_len=0.075, petal_w=0.038, center_r=0.028):
	m = tube(rng, length=stem_h, radius=0.008, segs=5, rings=4, jitter=0.1, wobble=0.6, profile=lambda t: 1.0, mat=0)
	for i, (h, a) in enumerate(((0.12, 40), (0.2, 220))):
		lf = leaf(rng, 0.09, 0.035, mat=1, bend=0.4, segments=3).rotate("x", 35).rotate("z", a).translate(0, 0, h)
		m.add(lf)
	centre = blob(rng, center_r, subdiv=1, jitter=0.2, noise=0.12, scale=(1, 1, 0.55), mat=3).translate(0, 0, stem_h + 0.01)
	m.add(centre)
	for i in range(petals):
		a = 360 * i / petals + (rng.random() - 0.5) * 12
		p = leaf(rng, petal_len, petal_w, mat=2, bend=-0.1, segments=2, double=True).rotate("x", 6 + rng.random() * 8)
		p.rotate("z", a).translate(center_r * 0.7 * math.cos(math.radians(a + 90)), center_r * 0.7 * math.sin(math.radians(a + 90)), stem_h + 0.008)
		m.add(p)
	return m


@item("daisy", "food")
def daisy(rng):
	cells = [P.stem_green(), P.leaf_green(meters=0.1), P.petal_white(), P.yellow()]
	# a cut daisy (big enough to read at distance: ~2.5x life size)
	m = _flower(rng, cells)
	return ground(m), cells


@item("hobbits_leaf", "food")
def hobbits_leaf(rng):
	cells = [P.leaf_green(), P.leaf_dark(), P.stone(meters=0.1), P.stem_green()]
	m = tube(rng, length=0.05, radius=0.008, segs=5, rings=1, jitter=0.1, mat=3)
	for i in range(3):
		lf = leaf(rng, 0.3, 0.14, mat=i % 2, bend=0.1, segments=5).rotate("x", 70 + i * 4).rotate("z", -20 + 20 * i)
		lf.translate(0, 0, 0.04)
		m.add(lf)
	bud = blob(rng, 0.028, subdiv=1, jitter=0.2, noise=0.15, mat=2).translate(0.0, 0.0, 0.33)
	m.add(bud)
	return ground(m), cells


@item("blueberry", "food", roughness=0.55)
def blueberry(rng):
	cells = [P.blue_berry(), P.stem_green(meters=0.06), P.leaf_green(meters=0.1)]
	m = Mesh()
	positions = [(0, 0, 0.035), (0.06, 0.01, 0.03), (-0.055, 0.02, 0.032), (0.025, -0.055, 0.03), (-0.02, 0.06, 0.03),
				 (0.03, 0.04, 0.085), (-0.03, -0.03, 0.08)]
	for p in positions:
		b = blob(rng, 0.034 + rng.random() * 0.006, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 0.85), mat=0).translate(*p)
		# a dimpled crown: pull the top centre vertices in
		m.add(b)
	stem = tube(rng, length=0.09, radius=0.005, segs=4, rings=2, jitter=0.1, mat=1).rotate("y", 50).translate(-0.02, -0.01, 0.1)
	m.add(stem)
	lf = leaf(rng, 0.07, 0.03, mat=2, bend=0.3, segments=3).rotate("x", 60).rotate("z", 200).translate(0.03, 0.05, 0.1)
	m.add(lf)
	return ground(m), cells


@item("honey", "food", roughness=0.35)
def honey(rng):
	cells = [P.honey(), P.wood_pale(meters=0.1), P.Cell((196, 128, 24), noise=0.06, meters=0.1), P.cloth_cream(meters=0.08)]
	prof = [(0.055, 0.0), (0.068, 0.012), (0.072, 0.1), (0.07, 0.16), (0.058, 0.18), (0.05, 0.19)]
	m = lathe(prof, segs=10, mat=0, rng=rng, jitter=0.05)
	c = m.centroids()
	m.mat[(c[:, 2] > 0.06) & (c[:, 2] < 0.1) & (np.linalg.norm(c[:, :2], axis=1) > 0.05)] = 2
	lid = lathe([(0.052, 0.19), (0.062, 0.2), (0.062, 0.23), (0.05, 0.24)], segs=10, mat=1, rng=rng, jitter=0.06)
	m.add(lid)
	cloth = lathe([(0.062, 0.215), (0.07, 0.218), (0.064, 0.232), (0.0, 0.245)], segs=10, mat=3, rng=rng, jitter=0.1, closed_bottom=False)
	m.add(cloth)
	return ground(m), cells


@item("wheat", "food")
def wheat(rng):
	cells = [P.wheat(), P.Cell((190, 156, 72), noise=0.08, streak="x", streak_amt=0.2, meters=0.15), P.leather(meters=0.1)]
	m = Mesh()
	n = 11
	for i in range(n):
		a = 2 * math.pi * i / n
		r = 0.03 + 0.015 * (i % 2)
		x0, y0 = r * math.cos(a), r * math.sin(a)
		lean = 1.5 + rng.random() * 4
		stalk = tube(rng, length=0.46 + rng.random() * 0.08, radius=0.006, segs=4, rings=3, jitter=0.1, wobble=0.0, profile=lambda t: 1.0, mat=1)
		head = blob(rng, 0.03, subdiv=1, jitter=0.3, noise=0.2, scale=(0.6, 0.6, 1.9), mat=0).translate(0, 0, stalk.bounds()[1][2] + 0.04)
		stalk.add(head)
		stalk.rotate("y", lean).rotate("z", math.degrees(a)).translate(x0, y0, 0)
		m.add(stalk)
	band = lathe([(0.058, 0.2), (0.064, 0.21), (0.064, 0.25), (0.058, 0.26)], segs=10, mat=2, rng=rng, jitter=0.1, closed_bottom=False, closed_top=False)
	m.add(band)
	return ground(m), cells


# ---------------------------------------------------------------- seeds


def _pouch(rng, logo_fn=None):
	cells = [P.sack(), P.rope(meters=0.05), P.seed_brown(), P.leaf_green(meters=0.08), P.wheat(meters=0.08)]
	prof = [(0.07, 0.0), (0.085, 0.02), (0.09, 0.09), (0.08, 0.15), (0.05, 0.19), (0.04, 0.21), (0.055, 0.25), (0.0, 0.26)]
	m = lathe(prof, segs=9, mat=0, rng=rng, jitter=0.1)
	pts = m.tris.reshape(-1, 3)
	pts[:, 1] *= 0.75
	m.tris = pts.reshape(-1, 3, 3)
	tie = lathe([(0.045, 0.19), (0.052, 0.2), (0.052, 0.215), (0.045, 0.222)], segs=8, mat=1, rng=rng, jitter=0.1)
	pts = tie.tris.reshape(-1, 3)
	pts[:, 1] *= 0.8
	tie.tris = pts.reshape(-1, 3, 3)
	m.add(tie)
	for k in range(5):
		s = blob(rng, 0.014, subdiv=0, jitter=0.4, noise=0.2, scale=(1.4, 1, 0.8), mat=2)
		a = rng.random() * 2 * math.pi
		s.rotate("z", rng.random() * 360).translate(0.1 * math.cos(a) + 0.03, 0.08 * math.sin(a) - 0.02, 0.008)
		m.add(s)
	if logo_fn:
		m.add(logo_fn(rng))
	weld_displace(m, rng, 0.004, cells=3.0)
	return ground(m), cells


def _leaf_logo(mat):
	def fn(rng):
		lf = leaf(rng, 0.07, 0.045, mat=mat, bend=0.0, segments=3).rotate("x", 90).rotate("z", 180)
		return lf.translate(0, -0.071, 0.06)
	return fn


@item("elm_seed", "seeds")
def elm_seed(rng):
	return _pouch(rng, _leaf_logo(3))


@item("ash_seed", "seeds")
def ash_seed(rng):
	def logo(rng):
		m = _leaf_logo(3)(rng).scale(0.7, 1, 0.8).translate(-0.02, 0, 0.01)
		m.add(_leaf_logo(3)(rng).scale(0.7, 1, 0.8).translate(0.02, 0, 0.01))
		return m
	return _pouch(rng, logo)


@item("wheat_seed", "seeds")
def wheat_seed(rng):
	def logo(rng):
		m = Mesh()
		for dx in (-0.015, 0.015):
			head = blob(rng, 0.016, subdiv=1, jitter=0.3, noise=0.2, scale=(0.7, 0.5, 1.6), mat=4).translate(dx, -0.07, 0.09)
			m.add(head)
			m.add(tube(rng, length=0.05, radius=0.003, segs=4, rings=1, mat=4).translate(dx, -0.07, 0.035))
		return m
	return _pouch(rng, logo)


@item("daisy_seed", "seeds")
def daisy_seed(rng):
	cells = [P.seed_brown(meters=0.15), P.bark_dark(meters=0.1)]
	m = blob(rng, 0.055, subdiv=1, jitter=0.25, noise=0.12, scale=(2.4, 1.0, 0.7), mat=0, flatten_bottom=0.7)
	pts = m.tris.reshape(-1, 3)
	f = np.abs(pts[:, 0]) / 0.13
	pts[:, 1:] *= (1 - 0.7 * np.clip(f, 0, 1) ** 1.8)[:, None]
	m.tris = pts.reshape(-1, 3, 3)
	crease = box(size=(0.13, 0.006, 0.006), center=(0, 0, 0.036), mat=1)
	m.add(crease)
	m.rotate("z", 30)
	return ground(m), cells


@item("blueberry_seed", "seeds")
def blueberry_seed(rng):
	cells = [P.seed_brown(meters=0.08), P.blue_berry(meters=0.05)]
	m = Mesh()
	for k in range(6):
		s = blob(rng, 0.02, subdiv=1, jitter=0.3, noise=0.15, scale=(1.5, 1, 0.8), mat=0 if k % 3 else 1, flatten_bottom=0.6)
		a = 2 * math.pi * k / 6 + rng.random()
		r = 0.0 if k == 0 else 0.055 + rng.random() * 0.02
		s.rotate("z", rng.random() * 360).translate(r * math.cos(a), r * math.sin(a), 0)
		m.add(s)
	return ground(m), cells


# ---------------------------------------------------------------- trophies and figurine


def _plaque(rng, mat=0):
	# a shield-shaped plaque in the XZ plane, face toward -Y, standing on z = 0
	outline = [(-0.2, 0.42), (0.2, 0.42), (0.21, 0.3), (0.15, 0.1), (0.0, 0.0), (-0.15, 0.1), (-0.21, 0.3)]
	p = prism(outline, 0.035, z0=0, mat=mat, side_mat=mat, rng=rng, jitter=0.008)
	p.rotate("x", 90)  # extrusion along -Y: front face at y = -0.035
	# bevel: a slightly smaller raised panel on the front
	inner = prism([(x * 0.85, 0.03 + y * 0.85) for x, y in outline], 0.012, z0=0, mat=mat, side_mat=mat, rng=rng, jitter=0.004)
	inner.rotate("x", 90).translate(0, -0.035, 0)
	p.add(inner)
	return p


def _eyes(rng, mat, y, z, dx=0.05, r=0.014):
	m = Mesh()
	for s in (-1, 1):
		m.add(blob(rng, r, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 0.5, 1), mat=mat).translate(s * dx, y, z))
	return m


@item("scav_trophy", "trophies", surface="wood")
def scav_trophy(rng):
	cells = [P.plaque(), P.skin_grey(), P.red_glow(), P.bone(meters=0.08), P.black(meters=0.1)]
	m = _plaque(rng, 0)
	head = blob(rng, 0.11, subdiv=2, jitter=0.3, noise=0.14, scale=(0.95, 0.8, 1.15), mat=1).translate(0, -0.11, 0.25)
	m.add(head)
	m.add(_eyes(rng, 2, -0.2, 0.28))
	# a row of teeth in a dark mouth
	mouth = box(size=(0.1, 0.02, 0.03), center=(0, -0.2, 0.17), mat=4)
	m.add(mouth)
	for i in range(6):
		m.add(box(size=(0.011, 0.012, 0.024), center=(-0.04 + i * 0.016, -0.21, 0.17), mat=3))
	return ground(m), cells


@item("boneback_trophy", "trophies", surface="wood")
def boneback_trophy(rng):
	cells = [P.plaque(), P.fur_grey(), P.red_glow(), P.bone(meters=0.08), P.black(meters=0.1)]
	m = _plaque(rng, 0)
	head = blob(rng, 0.11, subdiv=2, jitter=0.3, noise=0.14, scale=(1.0, 0.8, 1.05), mat=1).translate(0, -0.11, 0.25)
	m.add(head)
	m.add(_eyes(rng, 2, -0.2, 0.27))
	mouth = box(size=(0.08, 0.015, 0.012), center=(0, -0.2, 0.18), mat=4)
	m.add(mouth)
	# a crest of bone spikes over the brow
	for i in range(6):
		x = -0.07 + i * 0.028
		spike = tube(rng, length=0.09 + 0.02 * math.sin(i * 1.5), radius=0.012, segs=5, rings=2, jitter=0.1,
					 profile=lambda t: 1.0 - 0.85 * t, mat=3)
		spike.rotate("x", -15).translate(x, -0.15, 0.33)
		m.add(spike)
	return ground(m), cells


@item("fox_trophy", "trophies", surface="wood")
def fox_trophy(rng):
	cells = [P.plaque(), P.fur_orange(), P.fur_cream(), P.black(meters=0.1)]
	m = _plaque(rng, 0)
	head = blob(rng, 0.1, subdiv=2, jitter=0.3, noise=0.12, scale=(1.0, 0.8, 0.95), mat=1).translate(0, -0.1, 0.25)
	m.add(head)
	muzzle = blob(rng, 0.055, subdiv=1, jitter=0.25, noise=0.1, scale=(1.1, 1.0, 0.8), mat=2).translate(0, -0.2, 0.2)
	m.add(muzzle)
	nose = blob(rng, 0.018, subdiv=1, jitter=0.2, noise=0.1, mat=3).translate(0, -0.25, 0.21)
	m.add(nose)
	m.add(_eyes(rng, 3, -0.19, 0.28, dx=0.045, r=0.012))
	for s in (-1, 1):
		ear = prism([(-0.035, 0), (0.035, 0), (0.0, 0.1)], 0.02, mat=1, side_mat=1, rng=rng, jitter=0.004)
		ear.rotate("x", 90).rotate("y", s * 22).translate(s * 0.075, -0.07, 0.335)
		m.add(ear)
	return ground(m), cells


@item("jay_trophy", "trophies", surface="wood")
def jay_trophy(rng):
	cells = [P.plaque(), P.feather_blue(meters=0.1), P.black(meters=0.1), P.feather_white(meters=0.1)]
	m = _plaque(rng, 0)
	head = blob(rng, 0.095, subdiv=2, jitter=0.3, noise=0.12, scale=(0.95, 0.8, 1.0), mat=1).translate(0, -0.1, 0.24)
	m.add(head)
	beak = tube(rng, length=0.1, radius=0.03, segs=5, rings=2, jitter=0.1, profile=lambda t: 1.0 - 0.9 * t, mat=2)
	beak.rotate("x", 100).translate(0, -0.17, 0.21)
	m.add(beak)
	m.add(_eyes(rng, 3, -0.175, 0.275, dx=0.05, r=0.016))
	m.add(_eyes(rng, 2, -0.185, 0.277, dx=0.05, r=0.009))
	# crest: three feathers up from the crown
	for i, a in enumerate((-20, 0, 20)):
		f = feather_card(rng, 0.11, 0.035, 1, bend=0.3).rotate("x", 75 + i * 5).rotate("z", a).translate(a * 0.001, -0.09, 0.32)
		m.add(f)
	return ground(m), cells


@item("feral_chieftain_trophy", "trophies", surface="wood")
def feral_chieftain_trophy(rng):
	cells = [P.plaque(), P.skin_tan(), P.red_glow(), P.cloth_red(meters=0.1), P.feather_white(meters=0.1), P.blue_dark(meters=0.1),
			 P.black(meters=0.1)]
	m = _plaque(rng, 0)
	head = blob(rng, 0.105, subdiv=2, jitter=0.3, noise=0.12, scale=(0.95, 0.8, 1.15), mat=1).translate(0, -0.11, 0.25)
	# war paint: a red band across the eyes
	c = head.centroids()
	head.mat[(c[:, 2] > 0.265) & (c[:, 2] < 0.3) & (c[:, 1] < -0.13)] = 3
	m.add(head)
	m.add(_eyes(rng, 2, -0.2, 0.28))
	mouth = box(size=(0.07, 0.015, 0.01), center=(0, -0.2, 0.18), mat=6)
	m.add(mouth)
	# headband and feathers
	band = lathe([(0.108, 0.285), (0.113, 0.295), (0.113, 0.318), (0.108, 0.328)], segs=10, mat=3, rng=rng, jitter=0.06)
	pts = band.tris.reshape(-1, 3)
	pts[:, 1] = pts[:, 1] * 0.85 - 0.11
	band.tris = pts.reshape(-1, 3, 3)
	m.add(band)
	for i, (a, mat) in enumerate(((-28, 4), (0, 5), (28, 4))):
		f = feather_card(rng, 0.17, 0.05, mat, bend=0.12).rotate("x", 84).rotate("z", a).translate(a * 0.002, -0.09, 0.35)
		m.add(f)
	return ground(m), cells


@item("scav_figurine", "trophies", surface="rock")
def scav_figurine(rng):
	cells = [P.stone(meters=0.1), P.skin_grey(meters=0.1), P.red_glow(meters=0.05), P.leather_dark(meters=0.08)]
	# a carved stone statuette of a scav: blocky body on a round base
	m = lathe([(0.07, 0.0), (0.075, 0.015), (0.06, 0.03)], segs=9, mat=0, rng=rng, jitter=0.1)
	legs = Mesh()
	for s in (-1, 1):
		legs.add(box(size=(0.03, 0.035, 0.1), center=(s * 0.022, 0, 0.08), mat=1, jitter=0.08, rng=rng))
	m.add(legs)
	torso = box(size=(0.085, 0.05, 0.1), center=(0, 0, 0.18), mat=1, jitter=0.08, rng=rng)
	m.add(torso)
	for s in (-1, 1):
		arm = box(size=(0.025, 0.03, 0.09), center=(s * 0.06, 0, 0.17), mat=1, jitter=0.1, rng=rng).rotate("y", s * -12)
		m.add(arm)
	head = blob(rng, 0.035, subdiv=1, jitter=0.25, noise=0.12, scale=(1, 0.9, 1.1), mat=1).translate(0, 0, 0.27)
	m.add(head)
	m.add(_eyes(rng, 2, -0.028, 0.28, dx=0.014, r=0.006))
	loin = box(size=(0.09, 0.055, 0.03), center=(0, 0, 0.125), mat=3, jitter=0.06, rng=rng)
	m.add(loin)
	weld_displace(m, rng, 0.003, cells=4.0)
	return ground(m), cells
