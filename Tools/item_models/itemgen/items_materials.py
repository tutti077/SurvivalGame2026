"""Ores and minerals, crafted materials and parts, raw meats and animal parts, plus the few misc
pickups (coins, augment core, rabbit's foot, glow-bug jar, wire stripper). All centred on X/Y,
standing on z = 0. Real-world sizes; small things (teeth, talons, chips, bugs) scaled up to ~0.18 m+."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace, basis_from_dir, card
from .kit import registry, ground, butt_at_origin, paint, squash, loft, torus, broad_leaf, subdivide
from .items_resources import _meat, _pelt_outline

ITEMS, item = registry()


def C(rgb, d=10, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.06), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


def facet_flecks(m, rng, mat, frac=0.18, min_area_q=0.6):
	"""Mineral showing on a few of the bigger facets (never speckle): pick large triangles, then grow
	each pick to its neighbours by centroid distance so the flecks read as veins / patches."""
	a = m.areas()
	big = np.where(a > np.quantile(a, min_area_q))[0]
	n = max(2, int(len(m.tris) * frac / 6))
	seeds = rng.choice(big, size=min(n, len(big)), replace=False)
	c = m.centroids()
	scale = (m.bounds()[1] - m.bounds()[0]).max()
	for s in seeds:
		d = np.linalg.norm(c - c[s], axis=1)
		m.mat[d < scale * (0.06 + 0.05 * rng.random())] = mat
	return m


# ---------------------------------------------------------------- ores and minerals


def _ore(rng, host, fleck, frac=0.2, size=0.15, flat=0.75):
	cells = [host, C(np.array(host.color) * 0.88), fleck]
	m = blob(rng, size, subdiv=1, jitter=0.6, noise=0.32, noise_cells=2.0, scale=(1.2, 1.0, flat), mat=0, flatten_bottom=0.7)
	paint(m, 1, lambda c, n: n[:, 2] < -0.2)
	facet_flecks(m, rng, 2, frac=frac)
	return ground(m), cells


ROCK = C((112, 110, 106), 10)


@item("ore_copper", "materials", surface="rock")
def ore_copper(rng):
	# copper ore: grey host rock with green malachite and copper-orange patches
	m, cells = _ore(rng, ROCK, C((76, 150, 112)), frac=0.22)
	cells.append(C((196, 112, 64)))
	facet_flecks(m, rng, 3, frac=0.08)
	return m, cells


@item("ore_iron", "materials", surface="rock")
def ore_iron(rng):
	# iron ore: red-brown haematite banding in dark rock
	return _ore(rng, C((86, 78, 74)), C((150, 70, 48)), frac=0.3)


@item("ore_tin", "materials", surface="rock")
def ore_tin(rng):
	# tin ore (cassiterite): pale rock with glassy dark brown-black crystals
	return _ore(rng, C((150, 146, 136)), C((58, 48, 42)), frac=0.22)


@item("ore_titanium", "materials", surface="rock")
def ore_titanium(rng):
	# titanium ore (rutile / ilmenite): dark rock with metallic silver-blue patches
	return _ore(rng, C((70, 70, 74)), C((168, 180, 196)), frac=0.24)


@item("lithium", "materials", surface="rock")
def lithium(rng):
	# lithium (spodumene): pale pinkish-lilac rock with long flat crystal faces
	cells = [C((206, 186, 190)), C((180, 160, 168)), C((228, 214, 220))]
	m = blob(rng, 0.13, subdiv=1, jitter=0.8, noise=0.25, scale=(1.5, 0.85, 0.7), mat=0, flatten_bottom=0.6)
	paint(m, 1, lambda c, n: n[:, 2] < -0.2)
	facet_flecks(m, rng, 2, frac=0.25)
	return ground(m), cells


@item("obsidian", "materials", surface="rock", roughness=0.15)
def obsidian(rng):
	# obsidian: a black glassy shard with conchoidal (big curved) facets and sharp edges
	cells = [C((22, 22, 26), 6), C((44, 44, 52), 6)]
	m = blob(rng, 0.13, subdiv=1, jitter=1.0, noise=0.3, noise_cells=1.6, scale=(1.4, 0.8, 0.65), mat=0)
	pts = m.tris.reshape(-1, 3)
	key = np.round(pts / 1e-5).astype(np.int64)
	uniq, inv = np.unique(key, axis=0, return_inverse=True)
	push = np.where(rng.random(len(uniq)) < 0.2, 1.0 + rng.random(len(uniq)) * 0.45, 1.0)
	pts *= push[inv][:, None]
	m.tris = pts.reshape(-1, 3, 3)
	paint(m, 1, lambda c, n: n[:, 2] > 0.7)
	return ground(m), cells


@item("sulfur", "materials", surface="rock")
def sulfur(rng):
	# sulfur: a crusty lump of yellow crystals growing off a pale rock base
	cells = [C((226, 204, 62)), C((204, 180, 48)), C((150, 146, 128))]
	m = blob(rng, 0.11, subdiv=1, jitter=0.5, noise=0.3, scale=(1.3, 1.0, 0.5), mat=2, flatten_bottom=0.7)
	for k in range(9):
		a = rng.random() * 2 * math.pi
		r = rng.random() * 0.08
		cr = blob(rng, 0.035 + rng.random() * 0.02, subdiv=0, jitter=0.4, noise=0.2, scale=(1, 1, 1.3), mat=k % 2)
		m.add(cr.translate(1.2 * r * math.cos(a), r * math.sin(a), 0.03 + rng.random() * 0.02))
	return ground(m), cells


@item("quartz", "materials", surface="rock", roughness=0.25)
def quartz(rng):
	# quartz (silica): a cluster of six-sided crystal points on a rock base
	cells = [C((226, 230, 232), 8), C((200, 208, 214), 8), C((120, 116, 110))]
	m = blob(rng, 0.1, subdiv=1, jitter=0.5, noise=0.3, scale=(1.3, 1.1, 0.4), mat=2, flatten_bottom=0.7)
	for k in range(7):
		h = 0.09 + rng.random() * 0.1
		r = 0.018 + rng.random() * 0.012
		prof = [(r, 0.0), (r * 1.05, h * 0.75), (0.0, h)]
		cr = lathe(prof, segs=6, mat=k % 2, rng=rng, jitter=0.1)
		tilt = rng.random() * 40
		cr.rotate("y", tilt).rotate("z", rng.random() * 360)
		a = rng.random() * 2 * math.pi
		cr.translate(0.05 * math.cos(a) * rng.random(), 0.04 * math.sin(a) * rng.random(), 0.02)
		m.add(cr)
	return ground(m), cells


@item("salt", "materials")
def salt(rng):
	# rock salt: a heap of white cubic crystals
	cells = [C((236, 234, 228), 6), C((222, 218, 210), 6), C((244, 242, 238), 6)]
	m = Mesh()
	for k in range(14):
		s = 0.035 + rng.random() * 0.035
		b = box(size=(s, s * (0.8 + rng.random() * 0.4), s * (0.7 + rng.random() * 0.3)), center=(0, 0, 0), mat=k % 3, jitter=0.15, rng=rng)
		b.rotate("x", rng.random() * 30).rotate("y", rng.random() * 30).rotate("z", rng.random() * 90)
		a = rng.random() * 2 * math.pi
		r = (rng.random() ** 0.7) * 0.09
		b.translate(r * math.cos(a), r * math.sin(a), s * 0.4 + max(0, 0.06 - r) * 0.8)
		m.add(b)
	return ground(m), cells


@item("scrap", "materials", surface="metal", roughness=0.8)
def scrap(rng):
	# a handful of scrap: a bent rusty plate, a length of pipe, a gear, a few bolts
	cells = [C((128, 96, 72)), C((110, 108, 104)), C((84, 66, 52)), C((150, 142, 132))]
	plate = subdivide(prism([(-0.13, -0.09), (0.1, -0.1), (0.14, 0.05), (0.0, 0.1), (-0.12, 0.07)], 0.008, mat=0, side_mat=2, rng=rng, jitter=0.02), 2)
	squash(plate, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.09 * np.clip((p[:, 0] - 0.02) / 0.12, 0, 1) ** 1.5 + 0.03 * np.clip(-p[:, 1] / 0.1, 0, 1) ** 2]))
	weld_displace(plate, rng, 0.006, cells=5)
	m = plate
	m.add(tube(rng, length=0.22, radius=0.02, segs=7, rings=2, jitter=0.06, mat=1, axis="x", cap_mat=2).rotate("z", 30).translate(-0.12, -0.04, 0.03))
	g = gear(rng, 0.05, 0.014, 9, mat=1, hub_mat=2)
	m.add(g.rotate("x", 20).translate(-0.02, 0.05, 0.035))
	bracket = prism([(0, 0), (0.08, 0), (0.08, 0.02), (0.02, 0.02), (0.02, 0.07), (0, 0.07)], 0.025, mat=3, side_mat=2, rng=rng, jitter=0.003)
	m.add(bracket.rotate("x", 90).rotate("z", -20).translate(-0.06, 0.02, 0.012))
	for k in range(3):
		bolt = tube(rng, length=0.045, radius=0.006, segs=5, rings=1, mat=3, axis="x")
		bolt.add(lathe([(0.0, 0), (0.012, 0.0), (0.012, 0.007), (0.0, 0.007)], segs=6, mat=3).rotate("y", 90))
		m.add(bolt.rotate("z", rng.random() * 180).translate(-0.06 + 0.05 * k, 0.09, 0.012))
	return ground(m), cells


@item("crude_oil", "materials", surface="metal", roughness=0.4)
def crude_oil(rng):
	# crude oil: a dented steel can of it with an ochre band, black oil dribbled down the side
	cells = [C((96, 98, 104)), C((28, 26, 24), 4), C((150, 112, 48)), C((70, 72, 76))]
	can = lathe([(0.075, 0.0), (0.08, 0.01), (0.08, 0.17), (0.075, 0.18), (0.03, 0.185), (0.0, 0.186)], segs=12, mat=0, rng=rng, jitter=0.06)
	can.add(lathe([(0.082, 0.07), (0.082, 0.11)], segs=12, mat=2, closed_bottom=False, closed_top=False))
	can.add(torus(rng, 0.078, 0.004, n=12, segs=4, mat=3).translate(0, 0, 0.06))
	can.add(torus(rng, 0.078, 0.004, n=12, segs=4, mat=3).translate(0, 0, 0.12))
	can.add(lathe([(0.016, 0.18), (0.016, 0.2), (0.0, 0.2)], segs=7, mat=1).translate(0.04, 0, 0))
	for a in (0.4, 2.2):
		drip = path_tube(rng, np.array([(0.082 * math.cos(a), 0.082 * math.sin(a), z) for z in (0.18, 0.13, 0.08)]), radius=0.006, segs=4,
						 radius_fn=lambda t: 1.0 + 0.4 * t, mat=1)
		can.add(drip)
	return ground(can), cells


# ---------------------------------------------------------------- crafted materials


def gear(rng, r, thick, teeth, mat=0, hub_mat=None, axis_up=True):
	pts = []
	for k in range(teeth * 2):
		a = math.pi * k / teeth
		rr = r * (1.0 if k % 2 == 0 else 0.8)
		da = math.pi / teeth * 0.35
		pts += [(rr * math.cos(a - da), rr * math.sin(a - da)), (rr * math.cos(a + da), rr * math.sin(a + da))]
	g = prism(pts, thick, z0=-thick / 2, mat=mat, side_mat=mat)
	g.add(lathe([(r * 0.3, -thick * 0.7), (r * 0.3, thick * 0.7)], segs=8, mat=hub_mat if hub_mat is not None else mat))
	return g


@item("gears", "materials", surface="metal", roughness=0.5)
def gears(rng):
	cells = [C((150, 152, 156)), C((96, 98, 104)), C((176, 130, 64))]
	# three meshed gears lying flat, one brass
	m = gear(rng, 0.085, 0.018, 12, mat=0, hub_mat=1).translate(0, 0, 0.009)
	m.add(gear(rng, 0.055, 0.016, 9, mat=2, hub_mat=1).translate(0.12, 0.05, 0.008))
	m.add(gear(rng, 0.045, 0.014, 8, mat=0, hub_mat=1).rotate("z", 10).translate(-0.06, 0.1, 0.025))
	return ground(m), cells


def ingot(rng, mat, top_mat, L=0.2, W=0.08, H=0.05):
	# a cast bar: chamfered corners, sides drafted in toward the top, the top face a little sunken and rough
	ch = 0.012
	out = [(-L / 2 + ch, -W / 2), (L / 2 - ch, -W / 2), (L / 2, -W / 2 + ch), (L / 2, W / 2 - ch), (L / 2 - ch, W / 2), (-L / 2 + ch, W / 2), (-L / 2, W / 2 - ch),
		   (-L / 2, -W / 2 + ch)]
	m = subdivide(prism(out, H, mat=mat, side_mat=mat, rng=rng, jitter=0.003, taper=0.82), 1)
	paint(m, top_mat, lambda c, n: n[:, 2] > 0.9)
	pts = m.tris.reshape(-1, 3)
	inner = (pts[:, 2] > H * 0.98) & (np.abs(pts[:, 0]) < L * 0.3) & (np.abs(pts[:, 1]) < W * 0.25)
	pts[inner, 2] -= 0.004
	m.tris = pts.reshape(-1, 3, 3)
	weld_displace(m, rng, 0.0015, cells=8)
	return m


def _ingots(name, base, top, n=2, rough=0.4):
	@item(name, "materials", surface="metal", roughness=rough)
	def build(rng):
		cells = [C(base), C(top, 6)]
		m = ingot(rng, 0, 1)
		if n > 1:
			m.add(ingot(rng, 0, 1).rotate("z", 90).translate(0, 0, 0.05))
		return ground(m), cells
	return build


_ingots("ingot_copper", (190, 110, 66), (214, 138, 86))
_ingots("ingot_iron", (92, 92, 96), (120, 120, 124), rough=0.6)
_ingots("ingot_titanium", (160, 172, 186), (196, 206, 218))
_ingots("silica_ingot", (200, 214, 214), (226, 236, 236), rough=0.2)


@item("ingot_arcsteel", "materials", surface="metal", roughness=0.4)
def ingot_arcsteel(rng):
	# steel ingot (crafted_ingotArcsteel): bright steel bars with the teal arc line stamped along the top
	cells = [C((170, 174, 180)), C((204, 208, 214), 6), Cell((70, 230, 210), noise=0.04)]
	m = ingot(rng, 0, 1)
	m.add(ingot(rng, 0, 1).rotate("z", 90).translate(0, 0, 0.05))
	m.add(box(size=(0.004, 0.13, 0.003), center=(0, 0, 0.1), mat=2))
	return ground(m), cells


@item("charcoal", "materials", surface="wood")
def charcoal(rng):
	# lump charcoal: black split chunks keeping the wood grain, a few glints
	cells = [C((34, 32, 30), 6), C((50, 48, 46), 6), C((70, 68, 66), 6)]
	m = Mesh()
	for k in range(6):
		b = box(size=(0.07 + rng.random() * 0.06, 0.04 + rng.random() * 0.02, 0.035 + rng.random() * 0.02), mat=k % 2, jitter=0.35, rng=rng)
		weld_displace(b, rng, 0.004)
		b.rotate("z", rng.random() * 180).rotate("x", rng.random() * 20)
		a = 2 * math.pi * k / 6
		r = 0.05 if k else 0.0
		b.translate(r * math.cos(a), r * math.sin(a), 0.02 + (0.03 if k == 0 else 0))
		m.add(b)
	paint(m, 2, lambda c, n: n[:, 2] > 0.95)
	return ground(m), cells


@item("clay_bricks", "materials", surface="rock")
def clay_bricks(rng):
	# three fired clay bricks stacked, the top one crossways
	cells = [C((168, 84, 56)), C((148, 72, 48)), C((186, 104, 72))]
	m = Mesh()
	for k, (x, y, z, r) in enumerate(((0, -0.055, 0, 0), (0, 0.055, 0, 0), (0, 0, 0.065, 90))):
		b = box(size=(0.215, 0.1, 0.065), center=(0, 0, 0.0325), mat=k % 2, jitter=0.03, rng=rng)
		weld_displace(b, rng, 0.003)
		paint(b, 2, lambda c, n: np.abs(n[:, 0]) > 0.8)
		m.add(b.rotate("z", r + (rng.random() - 0.5) * 6).translate(x, y, z))
	return ground(m), cells


@item("glass", "materials", roughness=0.1)
def glass(rng):
	# a stack of three cut glass panes, pale green edges, one leaning chipped corner
	cells = [C((196, 222, 214), 6), C((120, 170, 150), 6)]
	m = Mesh()
	for k in range(3):
		out = [(-0.14, -0.1), (0.14, -0.1), (0.14, 0.1), (-0.14, 0.1)] if k < 2 else [(-0.13, -0.09), (0.1, -0.09), (0.13, -0.05), (0.13, 0.09), (-0.13, 0.09)]
		p = prism(out, 0.008, z0=0.009 * k, mat=0, side_mat=1, rng=rng, jitter=0.003)
		m.add(p.rotate("z", (rng.random() - 0.5) * 10))
	return ground(m), cells


@item("chains", "materials", surface="metal", roughness=0.5)
def chains(rng):
	cells = [C((104, 106, 112)), C((80, 82, 88))]
	# a length of chain lying in a loose S: oval links alternating flat and on edge
	m = Mesh()
	pts = [(0.09 * math.sin(t * 2.0), -0.17 + 0.34 * t / math.pi, 0.0) for t in np.linspace(0, math.pi, 13)]
	for k in range(len(pts) - 1):
		a, b = np.array(pts[k]), np.array(pts[k + 1])
		mid = (a + b) / 2
		d = b - a
		ang = math.degrees(math.atan2(d[1], d[0]))
		link = Mesh()
		ring = [(0.028 * math.cos(t), 0.016 * math.sin(t), 0) for t in np.linspace(0, 2 * math.pi, 11)]
		link.add(path_tube(rng, np.array(ring), radius=0.0055, segs=5, jitter=0.04, cap=False, mat=k % 2))
		if k % 2:
			link.rotate("x", 90)
		link.rotate("z", ang).translate(mid[0], mid[1], 0.017)
		m.add(link)
	return ground(m), cells


@item("nails", "materials", surface="metal", roughness=0.6)
def nails(rng):
	cells = [C((120, 122, 128)), C((96, 98, 104))]
	# a small pile of big forged nails (~2x life so they read), square shanks, flat heads
	m = Mesh()
	for k in range(10):
		n = tube(rng, length=0.1, radius=0.008, segs=4, rings=2, profile=lambda t: 1.0 - 0.8 * t ** 3, mat=k % 2, axis="x")
		n.add(lathe([(0.0, -0.005), (0.017, -0.005), (0.017, 0.002), (0.0, 0.002)], segs=7, mat=1).rotate("y", 90))
		n.rotate("y", (rng.random() - 0.5) * 8).rotate("z", -35 + rng.random() * 70 + (180 if k % 3 == 0 else 0))
		n.translate((rng.random() - 0.5) * 0.06, (rng.random() - 0.5) * 0.1, 0.014 + 0.011 * (k % 3))
		m.add(n)
	return ground(m), cells


@item("pipes", "materials", surface="metal", roughness=0.5)
def pipes(rng):
	cells = [C((150, 152, 156)), C((60, 60, 64)), C((110, 112, 118))]
	# three steel pipes bundled with a strap, one with a threaded coupling
	m = Mesh()
	for k, (y, z) in enumerate(((-0.026, 0.026), (0.026, 0.026), (0.0, 0.07))):
		p = lathe([(0.024, 0.0), (0.024, 0.4), (0.018, 0.4), (0.018, 0.0)], segs=9, mat=0, rng=rng, jitter=0.03, closed_bottom=False, closed_top=False)
		p.add(lathe([(0.018, 0.0), (0.024, 0.0)], segs=9, mat=1, closed_bottom=False, closed_top=False))
		p.add(lathe([(0.024, 0.4), (0.018, 0.4)], segs=9, mat=1, closed_bottom=False, closed_top=False))
		if k == 2:
			p.add(lathe([(0.03, 0.34), (0.03, 0.4)], segs=9, mat=2, closed_bottom=True, closed_top=False))
		m.add(p.rotate("y", 90).translate(-0.2, y, z))
	m.add(torus(rng, 0.06, 0.005, n=10, segs=4, mat=1, axis="x").translate(0.05, 0, 0.042))
	return ground(m), cells


@item("scrap_pipe", "materials", surface="metal", roughness=0.85)
def scrap_pipe(rng):
	cells = [C((128, 96, 72)), C((84, 66, 52)), C((110, 108, 104))]
	# a bent rusty pipe with a crushed end and a bit of elbow fitting
	path = np.array([(-0.2 + 0.25 * t, 0.0, 0.03) for t in np.linspace(0, 1, 4)] + [(0.05 + 0.1 * math.sin(a), 0.1 - 0.1 * math.cos(a), 0.03) for a in np.linspace(0.3, 1.4, 4)])
	m = path_tube(rng, path, radius=0.026, segs=8, jitter=0.08, mat=0, cap_mat=1)
	m.add(lathe([(0.032, 0.0), (0.032, 0.04)], segs=8, mat=2, rng=rng, jitter=0.08).rotate("y", 90).translate(-0.21, 0, 0.03))
	squash(m, lambda p: p * np.array([1, 1, 1]) - np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.01 * np.clip((p[:, 1] - 0.1) / 0.05, 0, 1)]))
	return ground(m), cells


def _coil(rng, R, r, turns, pitch, mat, n_per=10, z0=0.0, wobble=0.0):
	pts = []
	n = int(turns * n_per)
	for i in range(n + 1):
		a = 2 * math.pi * i / n_per
		rr = R * (1 + wobble * math.sin(a * 2.3))
		pts.append((rr * math.cos(a), rr * math.sin(a), z0 + pitch * a / (2 * math.pi)))
	return path_tube(rng, np.array(pts), radius=r, segs=5, jitter=0.05, mat=mat)


@item("scrap_wire", "materials", surface="metal", roughness=0.6)
def scrap_wire(rng):
	cells = [C((186, 108, 60)), C((70, 70, 74)), C((160, 40, 34))]
	# a tangled coil of salvaged copper wire, some lengths still in red or black insulation
	m = _coil(rng, 0.09, 0.0045, 5, 0.006, 0, wobble=0.15).rotate("x", 70).translate(0, 0, 0.09)
	m.add(_coil(rng, 0.08, 0.005, 3, 0.01, 2, wobble=0.25).rotate("x", 64).rotate("z", 40).translate(0.02, 0.0, 0.085))
	m.add(path_tube(rng, np.array([(0.05, -0.05, 0.03), (0.12, -0.08, 0.01), (0.18, -0.04, 0.006)]), radius=0.005, segs=4, mat=1))
	return ground(m), cells


@item("springs", "materials", surface="metal", roughness=0.5)
def springs(rng):
	cells = [C((150, 152, 156)), C((110, 112, 118))]
	# two coil springs, one standing, one on its side
	m = _coil(rng, 0.035, 0.005, 7, 0.022, 0)
	m.add(_coil(rng, 0.028, 0.0045, 6, 0.018, 1).rotate("y", 90).translate(0.08, 0.03, 0.033))
	return ground(m), cells


@item("hydraulic_parts", "materials", surface="metal", roughness=0.45)
def hydraulic_parts(rng):
	cells = [C((200, 150, 40)), C((190, 194, 200)), C((40, 40, 44)), C((90, 92, 98))]
	# a hydraulic ram: yellow cylinder, chrome rod, clevis eyes on both ends, a black hose coiled off a port
	m = lathe([(0.0, 0.0), (0.04, 0.0), (0.042, 0.01), (0.042, 0.2), (0.036, 0.21), (0.0, 0.21)], segs=10, mat=0, rng=rng, jitter=0.04).rotate("y", 90)
	m.add(tube(rng, length=0.14, radius=0.016, segs=8, rings=1, mat=1, axis="x").translate(0.21, 0, 0))
	for x in (-0.02, 0.36):
		eye = torus(rng, 0.018, 0.008, n=9, segs=5, mat=3, axis="y").translate(x, 0, 0)
		m.add(eye)
	hose = path_tube(rng, np.array([(0.05, 0.0, 0.045), (0.05, 0.03, 0.07), (0.0, 0.07, 0.06), (-0.04, 0.09, 0.02), (0.02, 0.12, -0.03)]), radius=0.009, segs=5, mat=2)
	m.add(hose)
	m.add(lathe([(0.012, 0.0), (0.012, 0.012)], segs=6, mat=3).translate(0.05, 0, 0.038))
	return ground(m), cells


@item("lubricant", "materials", surface="metal", roughness=0.4)
def lubricant(rng):
	cells = [C((60, 110, 60)), C((180, 160, 70)), C((150, 152, 156)), C((40, 38, 32))]
	# a pump oil can: squat green body, yellow label band, a long thin spout
	m = lathe([(0.06, 0.0), (0.065, 0.01), (0.065, 0.06), (0.05, 0.08), (0.02, 0.095), (0.012, 0.1)], segs=11, mat=0, rng=rng, jitter=0.05)
	c = m.centroids()
	m.mat[(np.abs(c[:, 2] - 0.035) < 0.017) & (np.linalg.norm(c[:, :2], axis=1) > 0.06)] = 1
	spout = tube(rng, length=0.16, radius=0.006, segs=5, rings=2, profile=lambda t: 1.0 - 0.6 * t, mat=2).rotate("y", 35).translate(0, 0, 0.095)
	m.add(spout)
	m.add(torus(rng, 0.025, 0.004, n=9, segs=4, mat=2, axis="y").translate(-0.07, 0, 0.05))
	return ground(m), cells


@item("chlorine", "materials", roughness=0.6)
def chlorine(rng):
	cells = [C((232, 232, 226), 6), C((200, 210, 70)), C((50, 90, 160)), C((210, 206, 196), 6)]
	# a plastic chemical jug: white body, handle loop, a blue cap and the yellow-green hazard label
	out = [(-0.07, -0.05), (0.07, -0.05), (0.075, 0.045), (-0.075, 0.05)]
	body = prism(out, 0.2, mat=0, side_mat=0, rng=rng, jitter=0.004, taper=0.8)
	weld_displace(body, rng, 0.004)
	body.add(box(size=(0.09, 0.004, 0.07), center=(0, -0.054, 0.08), mat=1).rotate("x", -4))
	body.add(lathe([(0.022, 0.2), (0.024, 0.205), (0.024, 0.23), (0.0, 0.232)], segs=9, mat=2, rng=rng, jitter=0.05).translate(-0.03, 0, 0))
	handle = path_tube(rng, np.array([(0.02, 0, 0.19), (0.04, 0, 0.23), (0.07, 0, 0.215), (0.07, 0, 0.16)]), radius=0.011, segs=5, mat=3)
	body.add(handle)
	return ground(body), cells


@item("fuel", "materials", surface="metal", roughness=0.5)
def fuel(rng):
	cells = [C((180, 46, 38)), C((140, 34, 30)), C((60, 60, 64)), C((220, 200, 70))]
	# a jerry can: red pressed-steel body with the X stamp, three-bar handle on top, a spout cap
	m = box(size=(0.17, 0.11, 0.25), center=(0, 0, 0.125), mat=0, jitter=0.03, rng=rng)
	weld_displace(m, rng, 0.003)
	for s in (-1, 1):
		for sgn in (-1, 1):
			bar = box(size=(0.012, 0.004, 0.2), center=(0, 0, 0), mat=1).rotate("y", sgn * 30)
			m.add(bar.translate(0, s * 0.057, 0.125))
	for x in (-0.05, 0.0, 0.05):
		m.add(box(size=(0.014, 0.03, 0.03), center=(x, 0, 0.265), mat=1))
	m.add(box(size=(0.12, 0.03, 0.012), center=(0, 0, 0.283), mat=1))
	m.add(lathe([(0.018, 0.25), (0.018, 0.275), (0.0, 0.278)], segs=8, mat=2, rng=rng, jitter=0.05).translate(0.06, 0, 0))
	m.add(box(size=(0.06, 0.004, 0.03), center=(-0.04, -0.058, 0.2), mat=3))
	return ground(m), cells


@item("power_cell", "materials", surface="metal", roughness=0.45)
def power_cell(rng):
	cells = [C((58, 62, 70)), C((110, 116, 126)), Cell((90, 240, 220), noise=0.04), C((180, 130, 60))]
	# a charged cell: a heavy capped cylinder with a glowing window band and brass terminals
	m = lathe([(0.045, 0.0), (0.052, 0.008), (0.052, 0.03), (0.046, 0.034), (0.046, 0.15), (0.052, 0.154), (0.052, 0.178), (0.044, 0.186), (0.0, 0.188)],
			  segs=10, mat=0, rng=rng, jitter=0.03)
	c = m.centroids()
	m.mat[(c[:, 2] < 0.034) | (c[:, 2] > 0.15)] = 1
	for k in range(4):
		a = 2 * math.pi * k / 4
		m.add(box(size=(0.012, 0.004, 0.09), center=(0, 0, 0), mat=2).translate(0, 0.047, 0.09).rotate("z", math.degrees(a)))
	m.add(lathe([(0.01, 0.186), (0.01, 0.2), (0.0, 0.2)], segs=7, mat=3))
	return ground(m), cells


@item("microchips", "materials", surface="default", roughness=0.5)
def microchips(rng):
	cells = [C((40, 104, 60)), C((28, 28, 32), 4), C((200, 170, 80)), C((60, 130, 80))]
	# a circuit board (~2x life): green board, black chips with gold legs, traces
	m = prism([(-0.11, -0.075), (0.11, -0.075), (0.11, 0.075), (-0.11, 0.075)], 0.006, mat=0, side_mat=3, rng=rng, jitter=0.002)
	for (x, y, w, d) in ((-0.05, -0.02, 0.06, 0.06), (0.05, 0.03, 0.05, 0.03), (0.06, -0.04, 0.03, 0.03), (-0.05, 0.05, 0.04, 0.02)):
		m.add(box(size=(w, d, 0.008), center=(x, y, 0.01), mat=1))
		for k in range(4):
			for s in (-1, 1):
				m.add(box(size=(0.004, 0.008, 0.003), center=(x - w * 0.35 + k * w * 0.23, y + s * (d / 2 + 0.003), 0.0075), mat=2))
	for y in (-0.06, 0.0, 0.065):
		m.add(box(size=(0.18, 0.003, 0.0015), center=(0, y, 0.0067), mat=2))
	return ground(m), cells


@item("wiring_harness", "materials", surface="default", roughness=0.6)
def wiring_harness(rng):
	cells = [C((170, 40, 34)), C((40, 70, 160)), C((210, 190, 60)), C((32, 32, 34), 4), C((210, 210, 204), 6)]
	# a wiring harness: a bundle of coloured wires taped together, branching into plug connectors
	m = Mesh()
	trunk = [(-0.18, 0, 0.012), (-0.06, 0.02, 0.014), (0.04, 0.0, 0.014)]
	for k in range(4):
		off = np.array([0, 0.007 * (k - 1.5), 0.004 * (k % 2)])
		branch_end = [(0.12, -0.08 + 0.055 * k, 0.012), (0.18, -0.1 + 0.07 * k, 0.012)]
		pts = np.array([np.array(p) + off for p in trunk] + branch_end)
		m.add(path_tube(rng, pts, radius=0.0045, segs=4, jitter=0.05, mat=k % 3 if k < 3 else 3))
		m.add(box(size=(0.03, 0.02, 0.016), center=branch_end[-1], mat=4 if k % 2 else 3, jitter=0.06, rng=rng))
	for x in (-0.14, -0.08, -0.02):
		m.add(lathe([(0.014, 0.0), (0.014, 0.012)], segs=7, mat=3, rng=rng, jitter=0.1).rotate("y", 90).translate(x, 0.007, 0.014))
	m.add(box(size=(0.035, 0.03, 0.022), center=(-0.19, 0, 0.012), mat=4))
	return ground(m), cells


def _roll(rng, R, W, mat, edge_mat, flat_len=0.12, t=0.006):
	# a roll of sheet: a lathe drum lying on its side with a flat tongue unrolled onto the ground
	d = lathe([(R, 0.0), (R, W), (R * 0.25, W), (R * 0.25, 0.0)], segs=12, mat=mat, rng=rng, jitter=0.03)
	paint(d, edge_mat, lambda c, n: np.abs(n[:, 2]) > 0.8)
	d.rotate("x", 90).translate(0, W / 2, R)
	tongue = box(size=(flat_len, W * 0.98, t), center=(R * 0.3 + flat_len / 2, 0, t / 2), mat=mat)
	d.add(tongue)
	return d


@item("carbon_fiber", "materials", roughness=0.35)
def carbon_fiber(rng):
	cells = [C((44, 46, 50), 6), C((24, 24, 28), 4), C((70, 72, 78), 6)]
	# a roll of carbon-fibre cloth, the unrolled tongue showing the woven check (painted in a grid)
	m = _roll(rng, 0.055, 0.26, 0, 1, flat_len=0.16)
	c = m.centroids()
	sel = (c[:, 2] < 0.008) & (((np.floor(c[:, 0] / 0.02) + np.floor(c[:, 1] / 0.02)) % 2) == 0)
	m.mat[sel] = 2
	return ground(m), cells


@item("vinyl", "materials", roughness=0.5)
def vinyl(rng):
	cells = [C((60, 120, 150)), C((40, 90, 116)), C((90, 150, 176))]
	# a roll of vinyl sheet with a curled tongue
	m = _roll(rng, 0.05, 0.3, 0, 1, flat_len=0.12, t=0.004)
	return ground(m), cells


@item("synthetic_rubber", "materials", roughness=0.8)
def synthetic_rubber(rng):
	cells = [C((36, 36, 38), 4), C((54, 54, 58), 4)]
	# a bale of synthetic rubber: a soft-edged black block with a slab peeled off the top
	out = [(-0.13, -0.09), (0.13, -0.09), (0.135, 0.09), (-0.13, 0.092)]
	m = subdivide(prism(out, 0.1, mat=0, side_mat=0, rng=rng, jitter=0.008, taper=0.94), 2)
	weld_displace(m, rng, 0.007, cells=4)
	slab = prism([(-0.12, -0.08), (0.12, -0.08), (0.12, 0.08), (-0.12, 0.08)], 0.02, z0=0.1, mat=1, side_mat=1, rng=rng, jitter=0.006)
	squash(slab, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.05 * np.clip(p[:, 0] / 0.12, 0, 1) ** 2]))
	m.add(slab)
	return ground(m), cells


@item("plastics", "materials", roughness=0.4)
def plastics(rng):
	cells = [C((214, 214, 206), 6), C((60, 120, 190)), C((200, 70, 50)), C((230, 190, 60))]
	# salvaged plastics: a stack of moulded sheets in a few colours, a crumpled bottle on top
	m = Mesh()
	for k in range(4):
		p = prism([(-0.13, -0.09), (0.13, -0.09), (0.13, 0.09), (-0.13, 0.09)], 0.012, z0=0.013 * k, mat=k % 4, side_mat=k % 4, rng=rng, jitter=0.006)
		m.add(p.rotate("z", (rng.random() - 0.5) * 14))
	bottle = lathe([(0.03, 0.0), (0.034, 0.02), (0.033, 0.1), (0.016, 0.13), (0.012, 0.15), (0.0, 0.15)], segs=8, mat=1, rng=rng, jitter=0.2)
	squash(bottle, lambda p: p * np.array([1.0, 0.6, 1.0]))
	m.add(bottle.rotate("y", 90).rotate("z", 20).translate(-0.07, 0.0, 0.07))
	return ground(m), cells


# ---------------------------------------------------------------- raw meats (sizes by animal)

MEAT_RED = P.meat_red
MEAT_PINK = P.meat_pink


def meat_cell(rgb):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=0.08, tones=[tuple(b * 0.9), tuple(b), tuple(np.clip(b * 1.08, 0, 255))], streak="x", streak_amt=0.16,
				stripes=(236, 214, 190), stripe_period=9, stripe_amt=0.45)


def _drumstick(name, rgb, size):
	@item(name, "resources")
	def build(rng):
		return _meat(rng, meat_cell(rgb), size)
	return build


# fallbackColor from resources.json, size = the meat lump radius (haunch / drumstick)
_drumstick("blackbear_meat", (186, 84, 86), 0.13)
_drumstick("boar_meat", (206, 106, 106), 0.11)
_drumstick("coyote_meat", (200, 104, 98), 0.085)
_drumstick("goat_meat", (206, 104, 96), 0.095)
_drumstick("lynx_meat", (166, 92, 72), 0.085)
_drumstick("wolf_meat", (194, 92, 90), 0.09)
_drumstick("jackrabbit_meat", (218, 130, 126), 0.06)
_drumstick("snowhare_meat", (222, 138, 134), 0.058)
_drumstick("otter_meat", (210, 110, 106), 0.065)


@item("bullfrog_meat", "resources")
def bullfrog_meat(rng):
	cells = [meat_cell((230, 178, 168)), C((236, 220, 200)), C((120, 130, 70))]
	# a pair of frog legs joined at the hip: long thighs, bent knees, a scrap of green skin left on the feet
	m = Mesh()
	for s in (-1, 1):
		thigh = loft(rng, 0.1, lambda t: 0.02 * (1 - 0.4 * t), lambda t: 0.018 * (1 - 0.4 * t), rings=4, segs=7, mat=0)
		shin = loft(rng, 0.09, lambda t: 0.012 * (1 - 0.3 * t), lambda t: 0.011 * (1 - 0.3 * t), rings=3, segs=6, mat=0)
		shin.rotate("z", s * 150).translate(0.1, 0, 0)
		thigh.add(shin)
		foot = prism([(0, -0.015), (0.04, -0.02), (0.045, 0.02), (0, 0.012)], 0.004, mat=2, side_mat=2, rng=rng, jitter=0.004)
		ex = 0.1 + 0.09 * math.cos(math.radians(s * 150))
		ey = 0.09 * math.sin(math.radians(s * 150))
		thigh.add(foot.rotate("z", s * 150).translate(ex, ey, -0.002))
		thigh.add(tube(rng, length=0.012, radius=0.005, segs=5, rings=1, mat=1, axis="x").translate(-0.006, 0, 0))
		m.add(thigh.rotate("z", s * 18))
	weld_displace(m, rng, 0.002, cells=8)
	return ground(m), cells


@item("crab_meat", "resources")
def crab_meat(rng):
	cells = [C((206, 76, 52)), C((234, 210, 196)), C((176, 58, 40))]
	# a cooked-red crab claw cracked open to the white meat, with a couple of leg sections
	claw = blob(rng, 0.05, subdiv=1, jitter=0.25, noise=0.15, scale=(1.5, 0.9, 0.7), mat=0)
	for s, ln in ((1, 0.08), (-1, 0.06)):
		f = tube(rng, length=ln, radius=0.016, segs=6, rings=2, jitter=0.08, profile=lambda t: 1 - 0.85 * t, mat=2, axis="x")
		f.rotate("z", s * 12).translate(0.06, s * 0.012, 0)
		claw.add(f)
	claw.add(blob(rng, 0.03, subdiv=1, jitter=0.3, noise=0.2, scale=(1.4, 0.9, 0.7), mat=1).translate(-0.06, 0, 0.02))
	m = claw
	for k in range(2):
		leg = tube(rng, length=0.1, radius=0.011, segs=6, rings=2, jitter=0.08, mat=0 if k == 0 else 2, axis="x")
		leg.add(lathe([(0.008, 0), (0.008, 0.006)], segs=6, mat=1).rotate("y", -90))
		m.add(leg.rotate("z", 60 + 40 * k).translate(-0.03 + 0.05 * k, 0.06, -0.02))
	return ground(m), cells


@item("iguana_meat", "resources")
def iguana_meat(rng):
	cells = [meat_cell((212, 140, 122)), C((100, 120, 70)), C((236, 220, 200))]
	# a skinned iguana tail section: a long tapering cut with a ring of scaly skin left at the thick end
	m = loft(rng, 0.26, lambda t: 0.03 * (1 - 0.7 * t), lambda t: 0.026 * (1 - 0.7 * t), rings=8, segs=8, mat=0)
	m.add(loft(rng, 0.03, lambda t: 0.033, lambda t: 0.029, rings=1, segs=8, mat=1).translate(-0.005, 0, 0))
	m.add(lathe([(0.006, 0), (0.006, 0.004)], segs=6, mat=2).rotate("y", -90).translate(-0.006, 0, 0))
	squash(m, lambda p: p + np.column_stack([np.zeros(len(p)), 0.04 * np.sin(p[:, 0] / 0.26 * math.pi), np.zeros(len(p))]))
	return ground(m), cells


def _steak(rng, meat, skin, fat, L=0.26, W=0.16, H=0.045):
	out = [(-L / 2, -W * 0.35), (-L * 0.2, -W / 2), (L * 0.3, -W * 0.48), (L / 2, -W * 0.1), (L * 0.42, W * 0.35), (0, W / 2), (-L * 0.4, W * 0.4)]
	m = subdivide(prism(out, H, mat=meat, side_mat=skin, rng=rng, jitter=0.01, taper=0.95), 2)
	weld_displace(m, rng, 0.004, cells=6)
	c = m.centroids()
	n = m.normals()
	m.mat[(n[:, 2] > 0.8) & (np.abs(np.sin(c[:, 0] * 40 + c[:, 1] * 25)) < 0.25)] = fat
	return m


@item("shark_meat", "resources")
def shark_meat(rng):
	cells = [meat_cell((232, 180, 174)), C((110, 114, 120)), C((240, 226, 216))]
	# a thick shark steak: pale pink flesh, grey skin around the edge, a ring of cartilage at the centre
	m = _steak(rng, 0, 1, 2, L=0.24, W=0.2, H=0.05)
	m.add(lathe([(0.018, 0.049), (0.012, 0.052), (0.0, 0.053)], segs=7, mat=2))
	return ground(m), cells


@item("stingray_filet", "resources")
def stingray_filet(rng):
	cells = [meat_cell((240, 202, 192)), C((206, 170, 160)), C((240, 226, 216))]
	# a stingray wing filet: a thin wedge fanning out, the cartilage ribs radiating across it
	out = [(0, -0.02), (0.22, -0.11), (0.27, 0.0), (0.22, 0.11), (0, 0.02)]
	m = subdivide(prism(out, 0.025, mat=0, side_mat=1, rng=rng, jitter=0.01, taper=0.9), 2)
	squash(m, lambda p: p * np.array([1, 1, 1]) + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), -0.012 * (p[:, 0] / 0.27)]))
	for a in np.linspace(-20, 20, 5):
		r = box(size=(0.2, 0.005, 0.003), center=(0.12, 0, 0.026), mat=2).rotate("z", a)
		m.add(r)
	weld_displace(m, rng, 0.003, cells=5)
	return ground(m), cells


# ---------------------------------------------------------------- pelts, scales, horns, teeth


def _pelt(rng, fur, belly, back, w, l, leg, wiggle=0.02, pattern=None):
	outline = _pelt_outline(rng, w, l, leg, wiggle=wiggle)
	m = subdivide(prism(outline, 0.03, mat=back, side_mat=back), 2)
	top = m.normals()[:, 2] > 0.5
	m.mat[top] = fur
	c = m.centroids()
	if belly is not None:
		m.mat[top & (np.abs(c[:, 0]) < w * 0.2) & (c[:, 1] < l * 0.25) & (c[:, 1] > -l * 0.35)] = belly
	if pattern is not None:
		m.mat[top & pattern(c)] = pattern.mat
	weld_displace(m, rng, 0.01, cells=7.0)
	return ground(m)


def _spots_on(rng, n, r, mat, w, l):
	pts = np.column_stack([rng.uniform(-w * 0.45, w * 0.45, n), rng.uniform(-l * 0.4, l * 0.4, n)])

	def fn(c):
		d = np.min(np.hypot(c[:, 0:1] - pts[None, :, 0], c[:, 1:2] - pts[None, :, 1]), axis=1)
		return d < r
	fn.mat = mat
	return fn


def fur(rgb, d=12):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=0.08, tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], streak="y", streak_amt=0.18)


@item("blackbear_fur", "resources")
def blackbear_fur(rng):
	cells = [fur((52, 44, 40)), fur((90, 70, 54)), P.leather_dark()]
	m = _pelt(rng, 0, None, 2, 0.6, 0.85, 0.13)
	return m, cells


@item("lynx_pelt", "resources")
def lynx_pelt(rng):
	cells = [fur((190, 160, 120)), fur((230, 218, 196)), P.leather_dark(), fur((110, 88, 66))]
	m = _pelt(rng, 0, 1, 2, 0.34, 0.6, 0.07, pattern=_spots_on(rng, 16, 0.025, 3, 0.34, 0.6))
	return m, cells


@item("otter_pelt", "resources")
def otter_pelt(rng):
	cells = [fur((110, 78, 52)), fur((150, 120, 92)), P.leather_dark()]
	# long and narrow, short legs, a thick tail
	m = _pelt(rng, 0, 1, 2, 0.18, 0.7, 0.035)
	return m, cells


@item("wolf_pelt", "resources")
def wolf_pelt(rng):
	cells = [fur((140, 138, 136)), fur((206, 202, 196)), P.leather_dark(), fur((90, 88, 88))]

	def saddle(c):
		return (np.abs(c[:, 0]) < 0.08) & (c[:, 1] > -0.2)
	saddle.mat = 3
	m = _pelt(rng, 0, 1, 2, 0.42, 0.75, 0.09, pattern=saddle)
	return m, cells


@item("dunewyrm_scales", "resources", surface="rock")
def dunewyrm_scales(rng):
	cells = [C((206, 170, 104)), C((176, 140, 84)), C((226, 196, 136))]
	# a few big overlapping wyrm scales (each a curved shield ~0.14 m), sandy with darker ridges
	m = Mesh()
	for k in range(5):
		out = [(0, -0.07), (0.05, -0.05), (0.07, 0.02), (0.0, 0.08), (-0.07, 0.02), (-0.05, -0.05)]
		s = prism(out, 0.008, mat=k % 2, side_mat=1, rng=rng, jitter=0.008)
		squash(s, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.025 * (1 - (p[:, 0] / 0.07) ** 2)]))
		s.add(box(size=(0.008, 0.11, 0.006), center=(0, 0.005, 0.032), mat=2))
		a = 2 * math.pi * k / 5
		s.rotate("x", 10).rotate("z", math.degrees(a) + 90).translate(0.06 * math.cos(a), 0.06 * math.sin(a), 0.006 * k)
		m.add(s)
	return ground(m), cells


@item("goat_horns", "resources")
def goat_horns(rng):
	cells = [C((176, 162, 140)), C((140, 126, 106)), C((206, 194, 172))]
	# a pair of mountain goat horns: ridged, curving back in an arc, lying side by side
	m = Mesh()
	for s in (-1, 1):
		path = np.array([(0.0, 0, 0.0)] + [(0.12 * math.sin(a), 0, 0.12 * (1 - math.cos(a))) for a in np.linspace(0.2, 2.0, 7)])
		h = path_tube(rng, path, radius=0.026, segs=7, jitter=0.08, radius_fn=lambda t: 1.0 - 0.85 * t, mat=0, cap_mat=2)
		c = h.centroids()
		h.mat[(np.sin(np.linalg.norm(c, axis=1) * 160) > 0.4)] = 1
		h.rotate("x", 90).translate(0, s * 0.04, 0)
		m.add(h.rotate("z", s * 8))
	return ground(m), cells


def _teeth(rng, n, L, r, mat, curve=0.3, spread=0.09, string=None):
	m = Mesh()
	for k in range(n):
		path = np.array([(0, 0, 0)] + [(L * t, 0, curve * L * t * t) for t in np.linspace(0.25, 1, 4)])
		tooth = path_tube(rng, path, radius=r, segs=6, jitter=0.08, radius_fn=lambda t: 1.0 - 0.9 * t, mat=mat)
		a = 2 * math.pi * k / n + rng.random() * 0.5
		tooth.rotate("x", 90 * (k % 2)).rotate("z", math.degrees(a)).translate(spread * 0.5 * math.cos(a), spread * 0.5 * math.sin(a), r)
		m.add(tooth)
	return m


@item("alligator_teeth", "resources")
def alligator_teeth(rng):
	cells = [C((232, 228, 210)), C((204, 196, 170)), P.rope()]
	# alligator teeth (~2x life) strung on a cord: conical, slightly curved, yellowed at the root
	m = Mesh()
	for k in range(7):
		t = tube(rng, length=0.06, radius=0.012, segs=6, rings=2, jitter=0.08, profile=lambda s: 1 - 0.9 * s, mat=k % 2)
		m.add(t.rotate("z", k * 13).translate(-0.12 + 0.04 * k, 0.012 * math.sin(k), 0.01))
	cord = path_tube(rng, np.array([(-0.17, 0.02, 0.005), (-0.12, 0.0, 0.012), (0.0, 0.006, 0.012), (0.13, 0.0, 0.012), (0.18, 0.03, 0.005)]), radius=0.004,
					 segs=4, mat=2)
	m.add(cord)
	return ground(m), cells


@item("wolf_fangs", "resources")
def wolf_fangs(rng):
	cells = [C((236, 230, 214)), C((210, 200, 176)), P.leather_dark()]
	# four curved canine fangs (~2.5x life) bound in a leather tie
	m = Mesh()
	for k in range(4):
		path = np.array([(0, 0, 0)] + [(0.08 * t, 0, 0.03 * t * t) for t in np.linspace(0.25, 1, 4)])
		f = path_tube(rng, path, radius=0.014, segs=6, jitter=0.06, radius_fn=lambda t: 1.0 - 0.9 * t, mat=k % 2)
		m.add(f.rotate("x", 90).rotate("z", -30 + 20 * k).translate(0, 0, 0.014))
	m.add(torus(rng, 0.016, 0.005, n=9, segs=4, mat=2).translate(0.004, 0, 0.014))
	m.scale(1.7)
	return ground(m), cells


@item("vulture_talons", "resources")
def vulture_talons(rng):
	cells = [C((72, 60, 54)), C((44, 38, 34)), C((150, 130, 110))]
	# a vulture's foot (~2x life): scaly toes splayed, black hooked talons
	m = Mesh()
	m.add(loft(rng, 0.09, lambda t: 0.014, lambda t: 0.013, rings=3, segs=6, mat=0).rotate("z", 180).translate(0, 0, 0.014))
	for a in (-40, 0, 40, 180):
		L = 0.08 if a != 180 else 0.05
		toe = path_tube(rng, np.array([(0, 0, 0.012), (L * 0.6, 0, 0.012), (L, 0, 0.008)]), radius=0.009, segs=5, mat=0)
		claw = path_tube(rng, np.array([(L, 0, 0.008), (L + 0.025, 0, 0.004), (L + 0.035, 0, -0.008)]), radius=0.006, segs=5, radius_fn=lambda t: 1 - 0.9 * t, mat=1)
		toe.add(claw)
		m.add(toe.rotate("z", a + (180 if a == 180 else 0) * 0 + (-150 if a == 180 else 0)))
	return ground(m), cells


@item("badger_claws", "resources")
def badger_claws(rng):
	cells = [C((214, 202, 172)), C((146, 132, 104)), C((44, 42, 40)), C((70, 67, 63))]
	# a badger's fore paw (~2x life): a black furred pad with five long digging claws fanned forward, horn-brown at
	# the root and pale toward the tip
	m = Mesh()
	m.add(blob(rng, 0.042, subdiv=1, jitter=0.25, noise=0.2, scale=(1.0, 1.35, 0.55), mat=2).translate(-0.018, 0, 0.022))
	m.add(blob(rng, 0.026, subdiv=1, jitter=0.25, noise=0.2, scale=(1.0, 1.2, 0.6), mat=3).translate(-0.05, 0, 0.022))
	for k in range(5):
		u = (k - 2) / 2.0
		L = 0.088 * (1.0 - 0.2 * abs(u)) * (1.0 + rng.uniform(-0.05, 0.05))
		root = path_tube(rng, np.array([(0, 0, 0.022), (L * 0.42, 0, 0.024)]), radius=0.0095, segs=5, jitter=0.05, mat=1)
		tip = path_tube(rng, np.array([(L * 0.42, 0, 0.024), (L * 0.8, 0, 0.014), (L, 0, -0.002)]), radius=0.0085, segs=5,
						jitter=0.05, radius_fn=lambda t: 1.0 - 0.85 * t, mat=0)
		root.add(tip)
		m.add(root.rotate("z", u * 16).translate(0.012, u * 0.026, 0))
	return ground(m), cells


@item("stingray_barbs", "resources")
def stingray_barbs(rng):
	cells = [C((200, 190, 166)), C((150, 140, 120))]
	# three stingray barbs (~1.5x life): flat serrated spines, the teeth along both edges
	m = Mesh()
	for k in range(3):
		pts = [(0, -0.006)]
		for i in range(1, 9):
			x = 0.02 * i
			w = 0.012 * (1 - i / 9) + 0.001
			pts.append((x, -w - (0.004 if i % 2 else 0)))
		pts.append((0.19, 0))
		for i in range(8, 0, -1):
			x = 0.02 * i
			w = 0.012 * (1 - i / 9) + 0.001
			pts.append((x, w + (0.004 if i % 2 else 0)))
		pts.append((0, 0.006))
		b = prism(pts, 0.004, mat=k % 2, side_mat=1, rng=rng, jitter=0.001)
		m.add(b.rotate("z", -15 + 15 * k).translate(-0.09, -0.03 + 0.03 * k, 0.004 * k))
	return ground(m), cells


@item("turtle_shell", "resources", surface="rock")
def turtle_shell(rng):
	cells = [C((98, 112, 62)), C((70, 84, 44)), C((150, 136, 90)), C((180, 166, 120))]
	# a turtle carapace: a domed oval with the scute plates (centre row, side rows) edged dark, a pale rim
	m = lathe([(0.17, 0.0), (0.175, 0.012), (0.16, 0.04), (0.13, 0.075), (0.08, 0.1), (0.0, 0.11)], segs=14, mat=0, rng=rng, jitter=0.05)
	squash(m, lambda p: p * np.array([1.0, 0.78, 1.0]))
	c = m.centroids()
	ang = np.arctan2(c[:, 1] / 0.78, c[:, 0])
	rr = np.hypot(c[:, 0], c[:, 1] / 0.78)
	m.mat[(np.abs(np.sin(ang * 4)) < 0.16) & (rr > 0.06)] = 1
	m.mat[(np.abs(rr - 0.09) < 0.01)] = 1
	m.mat[c[:, 2] < 0.015] = 2
	m.mat[(c[:, 2] > 0.095)] = 3
	return ground(m), cells


@item("dragonfly_wings", "resources", roughness=0.3)
def dragonfly_wings(rng):
	cells = [C((178, 206, 222), 6), C((70, 80, 90)), C((200, 220, 232), 6)]
	# two pairs of dragonfly wings (~3x life) still joined at a scrap of thorax, veined in a dark grid
	m = blob(rng, 0.014, subdiv=1, jitter=0.2, noise=0.1, scale=(1.6, 1.0, 1.0), mat=1).translate(0, 0, 0.012)
	for s in (-1, 1):
		for k, (x, L, W) in enumerate(((0.012, 0.17, 0.045), (-0.012, 0.16, 0.05))):
			out = [(0, -W * 0.2), (L * 0.3, -W * 0.5), (L * 0.85, -W * 0.45), (L, 0), (L * 0.8, W * 0.4), (L * 0.2, W * 0.35), (0, W * 0.2)]
			w = prism(out, 0.002, mat=0 if k == 0 else 2, side_mat=1, rng=rng, jitter=0.002)
			c = w.centroids()
			w.mat[(w.normals()[:, 2] > 0.5) & ((np.abs(np.sin(c[:, 0] * 90)) < 0.15) | (np.abs(c[:, 1]) < 0.004))] = 1
			w.rotate("z", s * (90 - 12 + 24 * k)).translate(x, 0, 0.008)
			m.add(w)
	return ground(m), cells


@item("spider_silk", "resources")
def spider_silk(rng):
	cells = [C((232, 232, 236), 5), C((214, 214, 222), 5), P.wood_pale()]
	# a skein of spider silk wound on a short stick, loose strands trailing
	m = tube(rng, length=0.22, radius=0.007, segs=5, rings=2, jitter=0.08, mat=2, axis="x").translate(-0.11, 0, 0.04)
	ball = blob(rng, 0.06, subdiv=2, jitter=0.2, noise=0.15, scale=(1.4, 1.0, 0.9), mat=0).translate(0, 0, 0.045)
	c = ball.centroids()
	ball.mat[np.sin(c[:, 0] * 120 + c[:, 2] * 60) > 0.3] = 1
	m.add(ball)
	for k in range(3):
		strand = np.array([(0.05 + 0.04 * t, 0.03 * k - 0.03 + 0.05 * t * (k - 1), 0.04 - 0.035 * t) for t in np.linspace(0, 1, 5)])
		m.add(path_tube(rng, strand, radius=0.0018, segs=3, mat=0))
	return ground(m), cells


# ---------------------------------------------------------------- misc


@item("gold_coins", "misc", surface="metal", roughness=0.3)
def gold_coins(rng):
	cells = [C((220, 176, 60)), C((190, 146, 44)), C((240, 204, 96))]
	# a small stack of fat gold coins (~2x life) with a few spilled beside it
	m = Mesh()
	for k in range(6):
		cn = lathe([(0.0, 0.0), (0.035, 0.0), (0.037, 0.005), (0.035, 0.01), (0.0, 0.01)], segs=11, mat=k % 2, rng=rng, jitter=0.04)
		paint(cn, 2, lambda c, n: n[:, 2] > 0.9)
		m.add(cn.translate((rng.random() - 0.5) * 0.006, (rng.random() - 0.5) * 0.006, 0.0105 * k))
	for k in range(4):
		cn = lathe([(0.0, 0.0), (0.035, 0.0), (0.037, 0.005), (0.035, 0.01), (0.0, 0.01)], segs=11, mat=0, rng=rng, jitter=0.04)
		paint(cn, 2, lambda c, n: n[:, 2] > 0.9)
		a = 0.8 + 1.4 * k
		tilt = 0 if k % 2 else 18
		m.add(cn.rotate("x", tilt).translate(0.08 * math.cos(a), 0.08 * math.sin(a), 0.006 if tilt else 0.0))
	return ground(m), cells


@item("augment_core", "misc", surface="metal", roughness=0.35)
def augment_core(rng):
	cells = [C((60, 66, 78)), C((120, 130, 146)), Cell((100, 190, 240), noise=0.04), Cell((200, 240, 255), noise=0.03)]
	# an augment core: a hexagonal capsule housing with a glowing blue crystal core showing through
	# windows on every face, capped top and bottom
	hexo = [(0.05 * math.cos(a), 0.05 * math.sin(a)) for a in np.linspace(0, 2 * math.pi, 6, endpoint=False)]
	m = prism(hexo, 0.03, mat=0, side_mat=0)
	m.add(prism([(x * 0.9, y * 0.9) for x, y in hexo], 0.08, z0=0.03, mat=1, side_mat=2))
	m.add(prism(hexo, 0.03, z0=0.11, mat=0, side_mat=0, taper=0.7))
	for k in range(6):
		a = math.pi / 3 * k + math.pi / 6
		post = box(size=(0.012, 0.012, 0.08), center=(0.047 * math.cos(a), 0.047 * math.sin(a), 0.07), mat=1)
		m.add(post)
	m.add(blob(rng, 0.018, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1, 1.5), mat=3).translate(0, 0, 0.15))
	return ground(m), cells


@item("lucky_rabbits_foot", "misc")
def lucky_rabbits_foot(rng):
	cells = [fur((226, 218, 196)), fur((200, 188, 164)), P.metal(), C((150, 120, 90))]
	# a rabbit's foot keychain (~2x life): a furry foot with toe tufts, a metal cap and a ring
	m = loft(rng, 0.12, lambda t: 0.022 * (0.9 + 0.3 * math.sin(math.pi * t)), lambda t: 0.02 * (0.9 + 0.25 * math.sin(math.pi * t)), rings=6, segs=8, mat=0)
	for k in range(4):
		toe = blob(rng, 0.012, subdiv=1, jitter=0.25, noise=0.15, scale=(1.4, 0.8, 0.8), mat=1).translate(0.12, -0.015 + 0.01 * k, 0.0)
		m.add(toe)
	m.add(tube(rng, length=0.02, radius=0.022, segs=8, rings=1, mat=2, axis="x").translate(-0.015, 0, 0))
	m.add(torus(rng, 0.018, 0.0035, n=10, segs=4, mat=2, axis="y").translate(-0.035, 0, 0))
	weld_displace(m, rng, 0.003, cells=8)
	return ground(m), cells


@item("glow_bugs", "food", roughness=0.2)
def glow_bugs(rng):
	cells = [C((200, 220, 212), 4), Cell((210, 250, 120), noise=0.04), C((120, 92, 60)), C((60, 70, 40))]
	# glow bugs for bait: a corked glass jar, the glowing bugs crawling on the glass
	jar = lathe([(0.045, 0.0), (0.05, 0.01), (0.052, 0.1), (0.04, 0.12), (0.032, 0.125), (0.032, 0.135)], segs=10, mat=0, rng=rng, jitter=0.04,
				closed_top=False)
	jar.add(lathe([(0.03, 0.125), (0.034, 0.13), (0.034, 0.16), (0.0, 0.162)], segs=8, mat=2, rng=rng, jitter=0.06))
	for k in range(7):
		a = 2 * math.pi * k / 7 + rng.random() * 0.5
		z = 0.025 + rng.random() * 0.075
		bug = blob(rng, 0.009, subdiv=0, jitter=0.2, noise=0.1, scale=(1.0, 1.6, 0.5), mat=1).rotate("z", math.degrees(a))
		jar.add(bug.translate(0.051 * math.cos(a), 0.051 * math.sin(a), z))
	jar.add(leaf(rng, 0.06, 0.025, mat=3, bend=0.1, segments=3).rotate("x", 70).translate(0, 0, 0.005))
	return ground(jar), cells


@item("lightning_bug", "resources")
def lightning_bug(rng):
	cells = [C((50, 44, 38)), Cell((220, 250, 120), noise=0.04), C((200, 80, 50)), C((180, 190, 186), 4)]
	# a lightning bug (firefly) ~10x life so it reads: dark body, red-edged head shield, a glowing tail
	m = loft(rng, 0.16, lambda t: 0.032 * math.sin(math.pi * min(1, 0.1 + t)) ** 0.5, lambda t: 0.022 * math.sin(math.pi * min(1, 0.1 + t)) ** 0.5,
			 rings=7, segs=8, mat=0)
	c = m.centroids()
	m.mat[c[:, 0] < 0.05] = 1
	m.mat[c[:, 0] > 0.13] = 2
	for s in (-1, 1):
		w = prism([(0, 0), (-0.1, -0.025), (-0.11, 0.0), (0, 0.02)], 0.002, mat=3, side_mat=3)
		m.add(w.rotate("z", s * 12).translate(0.13, s * 0.012, 0.022))
		for k in range(3):
			leg = path_tube(rng, np.array([(0.06 + 0.03 * k, s * 0.02, -0.01), (0.06 + 0.03 * k, s * 0.05, -0.02), (0.05 + 0.03 * k, s * 0.06, -0.032)]),
							radius=0.0025, segs=4, mat=0)
			m.add(leg)
		m.add(path_tube(rng, np.array([(0.16, s * 0.006, 0.005), (0.19, s * 0.02, 0.02), (0.21, s * 0.035, 0.03)]), radius=0.0018, segs=3, mat=0))
	return ground(m), cells


@item("wire_stripper", "tools", surface="metal", roughness=0.5)
def wire_stripper(rng):
	cells = [C((160, 40, 34)), C((40, 70, 160)), C((150, 152, 156)), C((90, 92, 98))]
	# wire strippers (~1.3x life): two handles in red and blue grips, crossed jaws with stripping notches
	m = Mesh()
	for s, mat in ((1, 0), (-1, 1)):
		h = path_tube(rng, np.array([(0, s * 0.03, 0), (0.08, s * 0.022, 0), (0.15, s * 0.008, 0)]), radius=0.01, segs=6, mat=mat)
		jaw = prism([(0.15, -0.006), (0.24, -0.004 + 0.003 * s), (0.25, 0.004), (0.15, 0.008)], 0.008, z0=-0.004 + 0.008 * (s > 0) - 0.004, mat=2, side_mat=3)
		for k in range(3):
			jaw.add(box(size=(0.006, 0.004, 0.009), center=(0.18 + 0.02 * k, 0.0, jaw.bounds()[0][2] + 0.004), mat=3))
		m.add(h).add(jaw)
	m.add(lathe([(0.012, -0.008), (0.012, 0.012)], segs=8, mat=3).translate(0.155, 0, 0))
	m.add(_coil(rng, 0.008, 0.0018, 2, 0.006, 2).rotate("x", 90).translate(0.13, 0, 0))
	return butt_at_origin(m), cells
