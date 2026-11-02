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



def folded_garment(rng, w, d, h, mat, side_mat, flap_mat=None, flap=0.45, crease=True):
	"""A garment folded into a soft rectangular stack: rounded jittered slab, a flap folded over the
	top from one side (slightly thicker edge) and a crease groove down the middle."""
	def outline(ww, dd):
		return [(-ww / 2, -dd / 2), (-ww / 2 + 0.02, -dd / 2 - 0.006), (ww / 2 - 0.02, -dd / 2), (ww / 2, -dd / 2 + 0.03),
				(ww / 2, dd / 2 - 0.025), (ww / 2 - 0.03, dd / 2), (-ww / 2 + 0.03, dd / 2 + 0.004), (-ww / 2, dd / 2 - 0.03)]
	m = prism(outline(w, d), h * 0.6, mat=mat, side_mat=side_mat, rng=rng, jitter=0.008)
	top = prism(outline(w * flap, d * 0.96), h * 0.4, z0=h * 0.6, mat=flap_mat if flap_mat is not None else mat,
				side_mat=side_mat, rng=rng, jitter=0.008)
	top.translate(-w / 2 + w * flap / 2 + 0.01, 0, 0)
	m.add(top)
	if crease:
		m.add(box(size=(0.012, d * 0.9, 0.01), center=(-w / 2 + w * flap + 0.008, 0, h * 0.6 + 0.002), mat=side_mat))
	weld_displace(m, rng, 0.004, cells=4.0)
	return m


def human_head(rng, skin, eye_mat, mouth_mat, y=-0.1, z=0.25, r=0.1, teeth_mat=None, brow=True):
	"""A human-shaped head facing -Y: skull, brow ridge, cheekbones, nose wedge, jaw, ears, neck stump."""
	m = blob(rng, r, subdiv=2, jitter=0.25, noise=0.1, noise_cells=2.0, scale=(0.9, 1.0, 1.15), mat=skin)
	pts = m.tris.reshape(-1, 3)
	# flatten the face plane a little and narrow the jaw
	front = pts[:, 1] < -0.3 * r
	pts[front, 1] = pts[front, 1] * 0.8 - 0.06 * r
	low = pts[:, 2] < -0.3 * r
	pts[low, 0] *= 1 - 0.3 * np.clip((-pts[low, 2] / r - 0.3) / 0.9, 0, 1)
	m.tris = pts.reshape(-1, 3, 3)
	if brow:
		b = blob(rng, r * 0.42, subdiv=1, jitter=0.25, noise=0.12, scale=(1.7, 0.5, 0.35), mat=skin).translate(0, -r * 0.84, r * 0.3)
		m.add(b)
	for sx in (-1, 1):
		cheek = blob(rng, r * 0.3, subdiv=1, jitter=0.25, noise=0.12, scale=(1.0, 0.7, 0.8), mat=skin).translate(sx * r * 0.62, -r * 0.7, r * 0.02)
		m.add(cheek)
		ear = blob(rng, r * 0.22, subdiv=1, jitter=0.25, noise=0.12, scale=(0.35, 1.0, 1.3), mat=skin).translate(sx * r * 0.9, 0.0, r * 0.08)
		m.add(ear)
		eye = blob(rng, r * 0.13, subdiv=1, jitter=0.2, noise=0.1, scale=(1.1, 0.5, 0.8), mat=eye_mat).translate(sx * r * 0.36, -r * 0.97, r * 0.2)
		m.add(eye)
	nose = prism([(-r * 0.14, 0), (r * 0.14, 0), (0, r * 0.4)], r * 0.22, mat=skin, side_mat=skin, rng=rng, jitter=r * 0.02)
	nose.rotate("x", 90).rotate("x", 180).translate(0, -r * 0.9, r * 0.12)  # wedge pointing out of the face, tip low
	m.add(nose)
	jaw = blob(rng, r * 0.5, subdiv=1, jitter=0.25, noise=0.12, scale=(1.25, 0.9, 0.7), mat=skin).translate(0, -r * 0.45, -r * 0.6)
	m.add(jaw)
	mouth = box(size=(r * 0.5, r * 0.12, r * 0.08), center=(0, -r * 0.95, -r * 0.35), mat=mouth_mat)
	m.add(mouth)
	if teeth_mat is not None:
		for i in range(5):
			m.add(box(size=(r * 0.07, r * 0.08, r * 0.09), center=(-r * 0.18 + i * r * 0.09, -r * 0.98, -r * 0.35), mat=teeth_mat))
	neck = tube(rng, length=r * 0.5, radius=r * 0.45, segs=8, rings=1, jitter=0.1, mat=skin).rotate("x", -90).translate(0, 0, -r * 0.5)
	m.add(neck)
	weld_displace(m, rng, r * 0.03, cells=4.0)
	return m.translate(0, y, z)


# ---------------------------------------------------------------- armor


@item("cowl", "armor")
def cowl(rng):
	cells = [P.leather(), P.black(), P.leather_dark()]
	# the hood folded flat: a rounded slab with the dark face opening showing on top, the point of
	# the hood folded over one corner
	outline = [(-0.16, -0.14), (0.16, -0.14), (0.17, 0.02), (0.13, 0.12), (0.04, 0.17), (-0.06, 0.17), (-0.14, 0.11), (-0.17, 0.0)]
	m = prism(outline, 0.045, mat=0, side_mat=2, rng=rng, jitter=0.008)
	top = m.normals()[:, 2] > 0.5
	c = m.centroids()
	face = top & (np.abs(c[:, 0]) < 0.085) & (c[:, 1] < 0.02) & (c[:, 1] > -0.1)
	m.mat[face] = 1
	m.tris[face] = m.tris[face] - np.array([0, 0, 0.01])
	peak = prism([(-0.1, 0.08), (0.08, 0.14), (-0.02, 0.2)], 0.02, z0=0.045, mat=0, side_mat=2, rng=rng, jitter=0.006)
	m.add(peak)
	seam = box(size=(0.012, 0.22, 0.008), center=(0.12, -0.02, 0.047), mat=2)
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
	m = folded_garment(rng, 0.32, 0.26, 0.09, mat=0, side_mat=1, flap=0.5)
	# the lacing shows on the folded-over flap
	for y in np.linspace(-0.08, 0.08, 4):
		m.add(box(size=(0.06, 0.012, 0.012), center=(-0.08, y, 0.09), mat=2))
	return ground(m), cells


@item("leather_pants", "armor")
def leather_pants(rng):
	cells = [P.leather_red(), P.leather_dark(), P.rope(meters=0.06)]
	# trousers folded in half: the two legs lie side by side as a stack, waistband at the near edge
	m = folded_garment(rng, 0.3, 0.24, 0.08, mat=0, side_mat=1, flap=0.5, crease=False)
	m.add(box(size=(0.3, 0.012, 0.03), center=(0, -0.125, 0.045), mat=1))      # groove between the legs
	m.add(box(size=(0.006, 0.22, 0.082), center=(0.0, 0.0, 0.041), mat=1))      # seam between the two legs
	m.add(box(size=(0.05, 0.012, 0.012), center=(0.07, -0.12, 0.081), mat=2))   # belt tie
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
	# the wraps rolled into two bandage rolls lying side by side, a leather tie around each
	m = Mesh()
	for side in (-1, 1):
		roll = lathe([(0.03, 0), (0.038, 0.01), (0.04, 0.05), (0.04, 0.09), (0.038, 0.13), (0.03, 0.14)], segs=9, mat=0, rng=rng, jitter=0.1)
		roll.rotate("x", 90).rotate("z", side * 8).translate(side * 0.05, 0, 0.04)
		tie = lathe([(0.041, 0.05), (0.047, 0.056), (0.047, 0.074), (0.041, 0.08)], segs=9, mat=1, rng=rng, jitter=0.1, closed_bottom=False, closed_top=False)
		tie.rotate("x", 90).rotate("z", side * 8).translate(side * 0.05, 0, 0.04)
		roll.add(tie)
		# the loose end of the strip trailing off the roll
		tail = leaf(rng, 0.09, 0.05, mat=0, bend=0.05, segments=3, taper_tip=False).rotate("z", 180 + side * 20).translate(side * 0.05, 0.06, 0.004)
		roll.add(tail)
		m.add(roll)
	weld_displace(m, rng, 0.003, cells=4.0)
	return ground(m), cells


@item("night_crawlers", "food")
def night_crawlers(rng):
	worm = P.Cell((120, 124, 170), noise=0.08, tones=[(104, 108, 156), (120, 124, 170), (138, 142, 186)], streak="y",
				  stripes=(90, 92, 134), stripe_period=5, stripe_amt=0.55, meters=0.1)
	cells = [P.dirt(), worm, P.Cell((160, 150, 180), noise=0.06, meters=0.1)]
	m = blob(rng, 0.15, subdiv=2, jitter=0.4, noise=0.25, noise_cells=2.5, scale=(1.3, 1.0, 0.35), mat=0, flatten_bottom=0.5)
	# one big night crawler looping over the mound
	path = []
	for t in np.linspace(0, 1, 26):
		# a fat S-curve draped over the mound, head lifting off the dirt at the end
		x = -0.17 + 0.34 * t
		y = 0.07 * math.sin(t * math.pi * 2.0) + 0.02 * math.sin(t * 11)
		z = 0.04 + 0.025 * math.sin(t * math.pi) + 0.03 * max(0.0, t - 0.8) * 5
		path.append((x, y, z))
	w = path_tube(rng, np.array(path), radius=0.03, segs=7, jitter=0.1,
				  radius_fn=lambda t: 0.5 + 0.55 * math.sin(math.pi * (0.1 + 0.9 * t)) ** 0.5, mat=1)
	c = w.centroids()
	# the clitellum: a paler band a third of the way along
	w.mat[(np.arange(len(w.tris)) // 14 >= 15) & (np.arange(len(w.tris)) // 14 <= 17)] = 2
	m.add(w)
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


@item("honey", "food", roughness=0.3)
def honey(rng):
	cells = [P.honey(), P.Cell((198, 132, 30), noise=0.06, meters=0.1), P.Cell((236, 176, 60), noise=0.06, meters=0.1)]
	# a broken chunk of honeycomb: a flat hex slab with raised wax walls on top and honey drips
	R = 0.15
	outline = [((R + (rng.random() - 0.5) * 0.04) * math.cos(a), (R + (rng.random() - 0.5) * 0.04) * math.sin(a)) for a in np.linspace(0, 2 * math.pi, 9, endpoint=False)]
	m = prism(outline, 0.045, mat=0, side_mat=1, rng=rng, jitter=0.01)
	cell_r = 0.028
	dx = cell_r * math.sqrt(3)
	dy = cell_r * 1.5
	for j in range(-4, 5):
		for i in range(-4, 5):
			cx = (i + 0.5 * (j % 2)) * dx
			cy = j * dy
			if math.hypot(cx, cy) > R - 0.03:
				continue
			for k in range(6):
				a0 = math.pi / 6 + k * math.pi / 3
				a1 = a0 + math.pi / 3
				p0 = np.array([cx + cell_r * math.cos(a0), cy + cell_r * math.sin(a0), 0.045])
				p1 = np.array([cx + cell_r * math.cos(a1), cy + cell_r * math.sin(a1), 0.045])
				mid = (p0 + p1) / 2
				L = np.linalg.norm(p1 - p0)
				wall = box(size=(L, 0.006, 0.012), center=(0, 0, 0.006), mat=2)
				ang = math.degrees(math.atan2(p1[1] - p0[1], p1[0] - p0[0]))
				wall.rotate("z", ang).translate(mid[0], mid[1], mid[2])
				m.add(wall)
	for a, r in ((0.4, 0.02), (2.6, 0.016), (4.4, 0.018)):
		drip = blob(rng, r, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 1.6), mat=0).translate(R * 0.95 * math.cos(a), R * 0.95 * math.sin(a), 0.01)
		m.add(drip)
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
	def logo(rng):
		m = blob(rng, 0.012, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 0.5, 1), mat=6).translate(0, -0.072, 0.08)
		for i in range(8):
			a = 360 * i / 8
			p = leaf(rng, 0.03, 0.014, mat=5, bend=0.0, segments=2).rotate("x", 90).rotate("y", a).translate(0, -0.072, 0.08)
			m.add(p)
		return m
	m, cells = _pouch(rng, logo)
	return m, cells + [P.petal_white(meters=0.06), P.yellow(meters=0.05)]


@item("blueberry_seed", "seeds")
def blueberry_seed(rng):
	def logo(rng):
		m = Mesh()
		for dx, dz in ((-0.018, 0.075), (0.018, 0.075), (0.0, 0.1)):
			m.add(blob(rng, 0.015, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 0.5, 1), mat=5).translate(dx, -0.072, dz))
		return m
	m, cells = _pouch(rng, logo)
	return m, cells + [P.blue_berry(meters=0.05)]


# ---------------------------------------------------------------- trophies and figurine


def _plaque(rng, mat=0):
	# a shield-shaped plaque in the XZ plane, face toward -Y, standing on z = 0
	outline = [(-0.2, 0.42), (0.2, 0.42), (0.21, 0.3), (0.15, 0.1), (0.0, 0.0), (-0.15, 0.1), (-0.21, 0.3)]
	p = prism(outline, 0.035, z0=0, mat=mat, side_mat=mat, rng=rng, jitter=0.008)
	p.rotate("x", 90)  # extrusion along -Y: front face at y = -0.035
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
	m.add(human_head(rng, skin=1, eye_mat=2, mouth_mat=4, teeth_mat=3, y=-0.09, z=0.25, r=0.1))
	return ground(m), cells


@item("boneback_trophy", "trophies", surface="wood")
def boneback_trophy(rng):
	cells = [P.plaque(), P.fur_grey(), P.red_glow(), P.bone(meters=0.08), P.black(meters=0.1)]
	m = _plaque(rng, 0)
	m.add(human_head(rng, skin=1, eye_mat=2, mouth_mat=4, y=-0.09, z=0.25, r=0.1))
	# a crest of bone spikes from the brow back over the crown
	for i in range(6):
		z = 0.33 + 0.01 * i - 0.004 * i * i
		y = -0.16 + i * 0.035
		spike = tube(rng, length=0.1 - 0.008 * abs(i - 2), radius=0.012, segs=5, rings=2, jitter=0.1, profile=lambda t: 1.0 - 0.85 * t, mat=3)
		spike.rotate("x", -25 + i * 8).translate(0, y, z)
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
	head = human_head(rng, skin=1, eye_mat=2, mouth_mat=6, y=-0.09, z=0.25, r=0.1)
	# war paint: a red band across the cheeks under the eyes
	c = head.centroids()
	n = head.normals()
	head.mat[(head.mat == 1) & (c[:, 2] > 0.255) & (c[:, 2] < 0.28) & (c[:, 1] < -0.15) & (n[:, 1] < -0.2)] = 3
	m.add(head)
	band = lathe([(0.1, 0.3), (0.105, 0.31), (0.105, 0.33), (0.1, 0.34)], segs=10, mat=3, rng=rng, jitter=0.06)
	pts = band.tris.reshape(-1, 3)
	pts[:, 0] *= 0.95
	pts[:, 1] = pts[:, 1] * 1.0 - 0.09
	band.tris = pts.reshape(-1, 3, 3)
	m.add(band)
	for i, (a, mat) in enumerate(((-28, 4), (0, 5), (28, 4))):
		f = feather_card(rng, 0.17, 0.05, mat, bend=0.12).rotate("x", 84).rotate("z", a).translate(a * 0.002, -0.07, 0.35)
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
