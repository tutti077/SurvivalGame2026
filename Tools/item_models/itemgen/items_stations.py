"""Temporary crafting-station models (2026-11-08): workbench, drying rack, furnace / crucible, anvil,
refinery, cauldron, fabricator, chemistry station and the augment station (a medical chair with a tool
arm). Build-piece frame: metres, bbox centred on the origin, 40 u/m (``import_scale`` 40 like the wood
kit since its 2026-11-08 move to BuildModuleDimensions.UnitsPerMeter), the wide side along engine Y - the axis BuildModuleDimensions.SizesMeters calls X - the depth
along engine X and height along Z. Prefabs live in Assets/prefabs/build/<id>.prefab; the printed size
goes into the SizesMeters row for the id."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, card, leaf, weld_displace
from .kit import registry, paint, torus

ITEMS, item = registry()

BUILD_SCALE = 40.0


def station(name, **kw):
	"""Registers a build-piece model under Assets/models/building/<name>/ at 40 u/m (BuildModuleDimensions.UnitsPerMeter)."""
	kw.setdefault("roughness", 0.85)

	def deco(fn):
		item(name, "building", **kw)(fn)
		ITEMS[-1]["import_scale"] = BUILD_SCALE
		return fn
	return deco


def C(rgb, d=12, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.07), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


def centered(m):
	"""Build-piece frame: the OBJ round trip (export.write_obj Y-up, engine import) yaws a mesh +90 deg about Z
	(authored +Y lands on engine -X), so stations are pre-rotated -90 deg and land in the engine exactly as
	authored - wide side along Y, front toward -X. Then the bbox is centred on the origin."""
	m.rotate("z", -90)
	lo, hi = m.bounds()
	c = (lo + hi) / 2
	return m.translate(-c[0], -c[1], -c[2])


def dark_iron():
	return C((58, 60, 64), 8, streak="x", streak_amt=0.1)


def rust():
	return Cell((118, 76, 52), noise=0.10, tones=[(104, 66, 46), (118, 76, 52), (132, 90, 60)], blotch=(90, 58, 42), blotch_amt=0.5)


def brick():
	return Cell((156, 94, 72), noise=0.08, tones=[(142, 84, 64), (156, 94, 72), (170, 106, 82)], streak="y", stripes=(198, 188, 176),
				stripe_period=10, stripe_amt=0.6)


def glass():
	return C((194, 214, 210), 6, noise=0.03)


def vinyl():
	return C((108, 150, 146), 8, noise=0.05)


# ---------------------------------------------------------------- shared parts


def planks(rng, length_y, width_x, thick, count, z, mat, end_mat=None, gap=0.012, x_center=0.0):
	"""count planks side by side along X, each running the full length along Y, top faces at z + thick."""
	total = count * width_x + (count - 1) * gap
	xs = x_center - total / 2 + width_x / 2 + np.arange(count) * (width_x + gap)
	m = Mesh()
	for x in xs:
		L = length_y * (1 + (rng.random() - 0.5) * 0.03)
		b = box((width_x, L, thick), (x, (rng.random() - 0.5) * 0.01, z + thick / 2), mat=mat, jitter=0.03, rng=rng)
		if end_mat is not None:
			paint(b, end_mat, lambda c, n: np.abs(n[:, 1]) > 0.9)
		m.add(b)
	return m


def post(rng, x, y, z0, z1, w, mat, jitter=0.03):
	return box((w, w, z1 - z0), (x, y, (z0 + z1) / 2), mat=mat, jitter=jitter, rng=rng)


def beam(rng, a, b, w, mat, jitter=0.02):
	"""Square-section beam from point a to point b (axis aligned on one axis)."""
	a = np.asarray(a, float)
	b = np.asarray(b, float)
	size = np.abs(b - a)
	size[size < 1e-6] = w
	return box(tuple(size), tuple((a + b) / 2), mat=mat, jitter=jitter, rng=rng)


def pole(rng, a, b, r, mat, segs=6):
	return path_tube(rng, np.array([a, b], float), radius=r, segs=segs, jitter=0.08, mat=mat)


def cylinder(rng, r, z0, z1, mat, segs=10, jitter=0.03, cap_mat=None, top=True, bottom=True):
	return lathe([(r, z0), (r, z1)], segs=segs, mat=mat, rng=rng, jitter=jitter, cap_mat=cap_mat, closed_top=top, closed_bottom=bottom)


def hammer(rng, handle_mat, head_mat, L=0.32):
	m = tube(rng, length=L, radius=0.014, segs=6, rings=3, jitter=0.06, mat=handle_mat, axis="x")
	m.add(box((0.05, 0.11, 0.05), (L - 0.02, 0, 0), mat=head_mat, jitter=0.04, rng=rng))
	return m


def fire(rng, r, z, flame_mat, ember_mat, h=1.0):
	m = blob(rng, r, subdiv=1, jitter=0.2, noise=0.2, scale=(1, 1, 0.35), mat=ember_mat, flatten_bottom=0.1).translate(0, 0, z)
	for k in range(4):
		a = 2 * math.pi * k / 4 + rng.random()
		rr = r * (0.3 + 0.3 * rng.random())
		f = blob(rng, r * 0.42, subdiv=1, jitter=0.2, noise=0.2, scale=(0.8, 0.8, 1.8 * h), mat=flame_mat)
		m.add(f.translate(rr * math.cos(a), rr * math.sin(a), z + r * 0.5 * h))
	return m


# ---------------------------------------------------------------- 1. workbench


@station("workbench")
def workbench(rng):
	cells = [P.wood_pale(), P.wood_handle(), P.wood_end(), P.metal(), dark_iron(), P.bark(), P.rope()]
	W, D, H = 2.0, 1.0, 0.9   # wide along Y, deep along X, top at H
	m = Mesh()
	# legs, aprons, plank top, lower shelf
	for sx in (-1, 1):
		for sy in (-1, 1):
			m.add(post(rng, sx * 0.42, sy * 0.85, 0.0, H - 0.06, 0.1, 1))
		m.add(beam(rng, (sx * 0.42, -0.9, H - 0.14), (sx * 0.42, 0.9, H - 0.06), 0.08, 1))
		m.add(beam(rng, (-0.42, sx * 0.85, H - 0.14), (0.42, sx * 0.85, H - 0.06), 0.08, 1))
		m.add(beam(rng, (sx * 0.42, -0.9, 0.22), (sx * 0.42, 0.9, 0.3), 0.07, 1))
	m.add(planks(rng, W - 0.1, 0.185, 0.07, 5, H - 0.07, 0, end_mat=2))
	m.add(planks(rng, 1.7, 0.2, 0.04, 3, 0.3, 1, end_mat=2, gap=0.04))
	# a leg vice on the +Y end: two jaws, a screw and its tommy bar
	m.add(box((0.3, 0.07, 0.12), (0.0, 0.95, H - 0.01), mat=4, jitter=0.03, rng=rng))
	m.add(box((0.3, 0.05, 0.1), (0.0, 1.0, H - 0.02), mat=4, jitter=0.03, rng=rng))
	m.add(tube(rng, length=0.14, radius=0.016, segs=6, rings=2, jitter=0.04, mat=3, axis="y").translate(0, 0.9, H - 0.04))
	m.add(tube(rng, length=0.26, radius=0.009, segs=5, rings=2, jitter=0.04, mat=3, axis="x").translate(-0.13, 1.03, H - 0.04))
	# tools lying on the top: a hammer, a hand saw, a chisel, two offcuts and a coil of rope
	m.add(hammer(rng, 1, 4).rotate("z", 20).translate(-0.3, -0.55, H + 0.028))
	saw = box((0.42, 0.012, 0.11), (0, 0, 0), mat=3)
	paint(saw, 1, lambda c, n: c[:, 0] > 0.15)
	m.add(saw.rotate("x", 80).rotate("z", -50).translate(0.1, 0.1, H + 0.06))
	m.add(tube(rng, length=0.22, radius=0.012, segs=5, rings=2, mat=1, axis="x", profile=lambda t: 1 - 0.5 * t).rotate("z", 75).translate(0.3, 0.55, H + 0.013))
	for k in range(2):
		log = tube(rng, length=0.26 + 0.1 * k, radius=0.045, segs=7, rings=2, jitter=0.1, mat=5, cap_mat=2, axis="y")
		m.add(log.rotate("z", 10 * k).translate(-0.25 + 0.1 * k, 0.35 + 0.1 * k, H + 0.045))
	m.add(torus(rng, 0.07, 0.022, n=10, segs=5, mat=6).translate(0.3, -0.7, H + 0.022))
	m.add(torus(rng, 0.065, 0.02, n=10, segs=5, mat=6).translate(0.31, -0.69, H + 0.06))
	return centered(m), cells


# ---------------------------------------------------------------- 2. drying rack


@station("drying_rack")
def drying_rack(rng):
	cells = [P.bark(), P.rope(), P.meat_red(), P.hide_brown(), P.fur_cream(), P.wood_end(), P.meat_pink()]
	H = 1.75
	m = Mesh()
	# two lashed A-frames joined by a ridge pole and a rail on each slope
	for sy in (-1, 1):
		y = sy * 0.95
		for sx in (-1, 1):
			m.add(pole(rng, (sx * 0.4, y, 0.0), (sx * 0.03, y, H + 0.1), 0.035, 0))
		m.add(torus(rng, 0.055, 0.014, n=8, segs=4, mat=1, axis="y").translate(0, y, H - 0.02))
		for sx in (-1, 1):
			m.add(torus(rng, 0.05, 0.012, n=8, segs=4, mat=1).rotate("y", sx * 20).translate(sx * 0.165, y, 1.05))
	m.add(pole(rng, (0, -1.1, H), (0, 1.1, H), 0.03, 0))
	for sx in (-1, 1):
		m.add(pole(rng, (sx * 0.16, -1.05, 1.05), (sx * 0.16, 1.05, 1.05), 0.026, 0))
	# meat strips hanging from the ridge, hides draped over the rails
	for k in range(7):
		y = -0.8 + k * 0.27 + (rng.random() - 0.5) * 0.06
		L = 0.45 + rng.random() * 0.25
		strip = leaf(rng, length=L, width=0.09 + rng.random() * 0.04, mat=2 if k % 3 else 6, bend=0.15, segments=4, taper_tip=False)
		strip.rotate("x", -92 + (rng.random() - 0.5) * 10).rotate("z", 90 + (rng.random() - 0.5) * 30).translate(0, y, H - 0.02)
		m.add(strip)
	for k, (y, w) in enumerate(((-0.6, 0.55), (0.25, 0.7))):
		sx = -1 if k == 0 else 1
		hide = Mesh()
		# fold line along the rail: the two halves hang down the slope
		for side in (-1, 1):
			drop = 0.5 + 0.2 * rng.random()
			h = card(w, drop, mat=3 if k == 0 else 4, double=True).rotate("z", 90)  # width along Y, hanging along Z, facing X
			h.translate(side * 0.028, 0, -drop)
			paint(h, 4 if k == 0 else 3, lambda c, n, s=side: n[:, 0] * s < 0)
			hide.add(h)
		m.add(hide.rotate("y", -sx * 12).translate(sx * 0.16, y, 1.06))
	return centered(m), cells


# ---------------------------------------------------------------- 3. furnace / crucible


@station("furnace")
def furnace(rng):
	cells = [P.stone(), brick(), P.black(), P.ember(), P.fire(), dark_iron(), C((188, 180, 170), 8), P.metal()]
	m = Mesh()
	# stone plinth, brick body, tapered chimney
	m.add(weld_displace(box((1.2, 1.2, 0.36), (0, 0, 0.18), mat=0, jitter=0.03, rng=rng), rng, 0.012, cells=2.5))
	m.add(box((1.0, 1.0, 0.8), (0, 0, 0.76), mat=1, jitter=0.015, rng=rng))
	m.add(prism([(-0.3, -0.3), (0.3, -0.3), (0.3, 0.3), (-0.3, 0.3)], 0.75, z0=1.16, mat=1, taper=0.62, rng=rng, jitter=0.02))
	m.add(box((0.46, 0.46, 0.05), (0, 0, 1.92), mat=0, jitter=0.03, rng=rng))
	m.add(cylinder(rng, 0.17, 1.9, 2.0, 2, segs=9, bottom=False))
	# iron straps and the firebox mouth on the -X face with the fire inside
	for z in (0.5, 1.02):
		m.add(box((1.04, 1.04, 0.05), (0, 0, z), mat=5, jitter=0.0))
	m.add(box((0.08, 0.42, 0.34), (-0.5, 0, 0.62), mat=2))
	m.add(box((0.1, 0.5, 0.06), (-0.52, 0, 0.82), mat=5, jitter=0.02, rng=rng))
	m.add(fire(rng, 0.14, 0.46, 4, 3, h=0.8).scale(0.9, 1.0, 1.0).translate(-0.47, 0, 0))
	# crucible on the body top beside the chimney, molten metal glowing; tongs leaning on the plinth
	m.add(lathe([(0.1, 0.0), (0.14, 0.02), (0.15, 0.2), (0.17, 0.24), (0.15, 0.26)], segs=9, mat=5, rng=rng, jitter=0.05, closed_top=False).translate(0.3, 0.3, 1.16))
	m.add(lathe([(0.14, 1.38), (0.0, 1.4)], segs=9, mat=4, rng=rng, jitter=0.03, closed_bottom=False).translate(0.3, 0.3, 0))
	m.add(pole(rng, (-0.55, 0.52, 0.36), (-0.75, 0.62, 1.0), 0.012, 7))
	m.add(pole(rng, (-0.58, 0.46, 0.36), (-0.75, 0.62, 1.0), 0.012, 7))
	return centered(m), cells


# ---------------------------------------------------------------- 4. anvil


@station("anvil")
def anvil(rng):
	cells = [dark_iron(), P.metal(), P.bark(), P.wood_end(), P.wood_handle(), P.black(), P.rope()]
	m = Mesh()
	# a stump, the anvil on it, a hammer resting on the face
	stump = lathe([(0.25, 0.0), (0.27, 0.05), (0.24, 0.3), (0.25, 0.5)], segs=12, mat=2, rng=rng, jitter=0.07, cap_mat=3)
	m.add(weld_displace(stump, rng, 0.01, cells=2.0))
	m.add(box((0.3, 0.5, 0.07), (0, 0, 0.535), mat=0, jitter=0.03, rng=rng))
	m.add(box((0.2, 0.3, 0.2), (0, 0, 0.67), mat=0, jitter=0.03, rng=rng))
	m.add(box((0.24, 0.54, 0.12), (0, 0.02, 0.83), mat=0, jitter=0.02, rng=rng))
	m.add(box((0.2, 0.5, 0.012), (0, 0.02, 0.895), mat=1))
	m.add(tube(rng, length=0.3, radius=0.065, segs=7, rings=3, jitter=0.05, mat=0, axis="y", profile=lambda t: 1 - 0.78 * t).translate(0, 0.28, 0.83))
	m.add(box((0.05, 0.05, 0.03), (0.05, -0.2, 0.9), mat=5))
	m.add(hammer(rng, 4, 1, L=0.3).rotate("z", -25).translate(-0.1, -0.18, 0.92))
	# rope ring and a tong hanging from a peg on the stump
	m.add(torus(rng, 0.05, 0.012, n=8, segs=4, mat=6, axis="x").translate(-0.26, 0.1, 0.4))
	m.add(pole(rng, (-0.27, 0.12, 0.4), (-0.29, 0.09, 0.05), 0.01, 0))
	m.add(pole(rng, (-0.27, 0.08, 0.4), (-0.29, 0.14, 0.05), 0.01, 0))
	return centered(m), cells


# ---------------------------------------------------------------- 5. refinery


@station("refinery")
def refinery(rng):
	cells = [rust(), dark_iron(), C((150, 92, 62), 10, streak="x", streak_amt=0.1), C((172, 142, 72), 10), P.white(), P.ember(), P.metal(), P.black()]
	m = Mesh()
	# a horizontal boiler on a cradle, a distillation column, pipes, a valve wheel and a gauge
	for sy in (-1, 1):
		for sx in (-1, 1):
			m.add(post(rng, sx * 0.42, sy * 0.7, 0.0, 0.72, 0.08, 1))
		m.add(beam(rng, (-0.46, sy * 0.7, 0.6), (0.46, sy * 0.7, 0.68), 0.08, 1))
		m.add(box((0.9, 0.12, 0.2), (0, sy * 0.7, 0.75), mat=1, jitter=0.02, rng=rng))
	boiler = lathe([(0.0, -0.9), (0.28, -0.86), (0.4, -0.76), (0.42, -0.6), (0.42, 0.6), (0.4, 0.76), (0.28, 0.86), (0.0, 0.9)], segs=14, mat=0, rng=rng, jitter=0.02)
	m.add(boiler.rotate("x", -90).translate(0, 0, 1.08))
	for y in (-0.45, 0.0, 0.45):
		m.add(lathe([(0.43, -0.03), (0.44, 0.03)], segs=14, mat=1, rng=rng, jitter=0.0, closed_top=False, closed_bottom=False).rotate("x", -90).translate(0, y, 1.08))
	m.add(box((0.5, 0.38, 0.22), (-0.3, 0.0, 0.3), mat=1, jitter=0.02, rng=rng))
	m.add(box((0.06, 0.26, 0.14), (-0.55, 0.0, 0.3), mat=7))
	m.add(fire(rng, 0.1, 0.25, 5, 5, h=0.5).translate(-0.53, 0, 0))
	column = lathe([(0.2, 0.3), (0.24, 0.36), (0.24, 2.1), (0.2, 2.16), (0.12, 2.2), (0.14, 2.34), (0.26, 2.38), (0.26, 2.42), (0.0, 2.44)], segs=12, mat=2, rng=rng, jitter=0.02)
	m.add(column.translate(0.3, 0.72, 0))
	for z in (0.9, 1.4, 1.9):
		m.add(lathe([(0.25, z - 0.02), (0.26, z + 0.02)], segs=12, mat=3, rng=rng, jitter=0.0, closed_top=False, closed_bottom=False).translate(0.3, 0.72, 0))
	m.add(box((0.5, 0.12, 0.12), (0.2, 0.72, 0.12), mat=1, jitter=0.02, rng=rng))
	# pipes: boiler top -> column, column side -> a tap at the front, a feed into the boiler end
	m.add(path_tube(rng, np.array([(0.0, 0.3, 1.45), (0.0, 0.3, 1.75), (0.3, 0.3, 1.75), (0.3, 0.5, 1.75)]), radius=0.05, segs=7, jitter=0.03, mat=2))
	m.add(path_tube(rng, np.array([(0.3, 0.5, 1.25), (-0.2, 0.5, 1.25), (-0.6, 0.5, 1.25), (-0.6, 0.5, 0.9)]), radius=0.04, segs=7, jitter=0.03, mat=2))
	m.add(path_tube(rng, np.array([(0.0, -0.95, 1.1), (0.0, -1.05, 1.1), (0.0, -1.05, 0.4), (0.0, -0.9, 0.4), (0.0, -0.6, 0.4)]), radius=0.035, segs=6, jitter=0.03, mat=6))
	wheel = torus(rng, 0.11, 0.014, n=12, segs=5, mat=3, axis="x")
	for k in range(4):
		wheel.add(tube(rng, length=0.2, radius=0.008, segs=4, rings=1, mat=3, axis="y").translate(0, -0.1, 0).rotate("x", 45 * k))
	m.add(wheel.translate(-0.66, 0.5, 1.25))
	m.add(lathe([(0.0, 0.0), (0.09, 0.0), (0.1, 0.04), (0.0, 0.045)], segs=10, mat=1, rng=rng, cap_mat=4).rotate("y", -90).translate(-0.41, -0.2, 1.3))
	m.add(lathe([(0.0, 0.0), (0.06, 0.0), (0.065, 0.035)], segs=9, mat=1, rng=rng, cap_mat=4).rotate("y", -90).translate(0.06, 0.72, 1.55))
	return centered(m), cells


# ---------------------------------------------------------------- 6. cauldron


@station("cauldron", roughness=0.7)
def cauldron(rng):
	cells = [C((40, 40, 44), 6), C((72, 74, 78), 8), P.stone(), P.ember(), P.fire(), C((96, 162, 82), 10), P.wood_handle(), C((150, 210, 130), 8)]
	m = Mesh()
	# a ring of fire stones, the fire, three legs and the pot with its brew
	for k in range(10):
		a = 2 * math.pi * k / 10 + (rng.random() - 0.5) * 0.3
		r = 0.44 + (rng.random() - 0.5) * 0.04
		s = blob(rng, 0.09 + rng.random() * 0.04, subdiv=1, jitter=0.25, noise=0.25, scale=(1.2, 1, 0.75), mat=2, flatten_bottom=0.2)
		m.add(s.rotate("z", rng.random() * 360).translate(r * math.cos(a), r * math.sin(a), 0.06))
	m.add(fire(rng, 0.22, 0.03, 4, 3, h=0.9))
	for k in range(3):
		a = 2 * math.pi * k / 3 + 0.5
		m.add(pole(rng, (0.3 * math.cos(a), 0.3 * math.sin(a), 0.0), (0.22 * math.cos(a), 0.22 * math.sin(a), 0.42), 0.03, 0, segs=5))
	pot = lathe([(0.0, 0.32), (0.22, 0.33), (0.4, 0.45), (0.47, 0.7), (0.44, 0.95), (0.42, 1.0), (0.47, 1.03), (0.46, 1.07)], segs=14, mat=0, rng=rng, jitter=0.025, closed_top=False)
	m.add(pot)
	m.add(lathe([(0.46, 1.07), (0.4, 1.06), (0.39, 0.9), (0.0, 0.9)], segs=14, mat=1, rng=rng, jitter=0.02, closed_top=False, closed_bottom=False, cap_mat=5))
	paint(m, 5, lambda c, n: (c[:, 2] > 0.89) & (c[:, 2] < 0.91) & (n[:, 2] > 0.9))
	for k in range(6):  # bubbles on the brew
		a = rng.random() * 2 * math.pi
		r = rng.random() * 0.3
		m.add(blob(rng, 0.03 + rng.random() * 0.03, subdiv=1, jitter=0.2, noise=0.1, mat=7, flatten_bottom=0.0).translate(r * math.cos(a), r * math.sin(a), 0.9))
	# lugs and a wooden ladle hooked on the rim
	for sy in (-1, 1):
		m.add(torus(rng, 0.06, 0.016, n=8, segs=4, mat=1, axis="x").translate(0, sy * 0.47, 1.0))
	m.add(pole(rng, (0.18, 0.0, 1.3), (0.38, 0.0, 0.95), 0.016, 6))
	m.add(lathe([(0.0, 0.0), (0.05, 0.0), (0.06, 0.04), (0.055, 0.05)], segs=8, mat=6, rng=rng, jitter=0.05, closed_top=False).rotate("y", 30).translate(0.4, 0.0, 0.96))
	return centered(m), cells


# ---------------------------------------------------------------- 7. fabricator


@station("fabricator", roughness=0.6)
def fabricator(rng):
	cells = [C((78, 82, 90), 8, streak="x", streak_amt=0.08), P.metal(), P.teal(), P.black(), P.red_glow(), P.yellow(), C((172, 142, 72), 10), C((140, 144, 150), 6)]
	m = Mesh()
	# base cabinet with panel lines, deck plate, two pillars carrying a gantry, a print head over the deck
	m.add(box((0.9, 1.6, 0.88), (0, 0, 0.44), mat=0, jitter=0.0))
	for k in range(3):
		m.add(box((0.02, 0.44, 0.6), (-0.455, -0.52 + 0.52 * k, 0.44), mat=7))
	m.add(box((0.04, 0.4, 0.05), (-0.47, 0.52, 0.72), mat=6))
	m.add(box((0.96, 1.66, 0.06), (0, 0, 0.91), mat=1, jitter=0.0))
	m.add(box((0.7, 1.3, 0.012), (-0.05, 0, 0.946), mat=3))
	for sy in (-1, 1):
		m.add(box((0.18, 0.14, 0.9), (0.28, sy * 0.72, 1.39), mat=0, jitter=0.0))
	m.add(box((0.22, 1.6, 0.14), (0.28, 0, 1.78), mat=0, jitter=0.0))
	m.add(box((0.05, 1.5, 0.03), (0.15, 0, 1.74), mat=1))
	head = box((0.28, 0.26, 0.2), (0.22, 0.12, 1.58), mat=1, jitter=0.0)
	head.add(lathe([(0.1, 0.0), (0.11, 0.04), (0.06, 0.1), (0.03, 0.18), (0.02, 0.2)], segs=9, mat=0, rng=rng, jitter=0.02).rotate("x", 180).translate(0.1, 0.12, 1.48))
	head.add(lathe([(0.0, 0.0), (0.025, 0.0), (0.02, 0.05), (0.0, 0.05)], segs=7, mat=2, rng=rng).rotate("x", 180).translate(0.1, 0.12, 1.28))
	m.add(head)
	m.add(path_tube(rng, np.array([(0.3, 0.12, 1.68), (0.3, 0.3, 1.9), (0.42, 0.7, 1.9), (0.42, 0.72, 1.3)]), radius=0.018, segs=5, jitter=0.0, mat=3))
	# the part on the deck: a stepped bright block, half built
	part = box((0.26, 0.4, 0.12), (-0.05, 0.1, 1.01), mat=7, jitter=0.02, rng=rng)
	part.add(box((0.16, 0.24, 0.1), (-0.05, 0.14, 1.12), mat=7, jitter=0.02, rng=rng))
	m.add(part)
	# back panel: a screen and buttons on the +X side
	m.add(box((0.08, 1.6, 0.8), (0.42, 0, 1.3), mat=0, jitter=0.0))
	m.add(box((0.02, 0.5, 0.3), (0.375, -0.45, 1.45), mat=3))
	m.add(box((0.012, 0.44, 0.24), (0.364, -0.45, 1.45), mat=2))
	for k, c in enumerate((4, 5, 2)):
		m.add(cylinder(rng, 0.03, 0.0, 0.03, c, segs=8).rotate("y", -90).translate(0.385, 0.25 + 0.12 * k, 1.5))
	m.add(box((0.02, 0.3, 0.1), (0.37, 0.4, 1.3), mat=3))
	return centered(m), cells


# ---------------------------------------------------------------- 8. chemistry station


@station("chemistry_station", roughness=0.6)
def chemistry_station(rng):
	cells = [C((62, 64, 68), 6), P.metal(), glass(), C((92, 182, 112), 10), C((152, 82, 172), 10), P.honey(), P.wood_handle(), C((150, 92, 62), 10), P.fire(), P.black(), P.stone()]
	m = Mesh()
	# a pipe-legged bench with a slate top and a back shelf
	for sx in (-1, 1):
		for sy in (-1, 1):
			m.add(cylinder(rng, 0.025, 0.0, 0.82, 1, segs=7).translate(sx * 0.3, sy * 0.72, 0.0))
		m.add(beam(rng, (sx * 0.3, -0.72, 0.18), (sx * 0.3, 0.72, 0.22), 0.04, 1))
	m.add(box((0.7, 1.6, 0.05), (0, 0, 0.845), mat=0, jitter=0.0))
	for sy in (-1, 1):
		m.add(box((0.05, 0.05, 0.78), (0.3, sy * 0.76, 1.26), mat=6, jitter=0.02, rng=rng))
	m.add(box((0.22, 1.62, 0.035), (0.28, 0, 1.4), mat=6, jitter=0.0))

	def flask(x, y, z, scale, liquid):
		f = lathe([(0.0, 0.0), (0.1, 0.0), (0.11, 0.02), (0.085, 0.09), (0.03, 0.2), (0.03, 0.26), (0.036, 0.275)], segs=9, mat=2, rng=rng, jitter=0.02, closed_top=False)
		paint(f, liquid, lambda c, n: c[:, 2] < 0.07)
		return f.scale(scale).translate(x, y, z)

	def beaker(x, y, z, r, h, liquid, fill=0.5):
		b = lathe([(r, 0.0), (r, h)], segs=9, mat=2, rng=rng, jitter=0.02, closed_top=False)
		paint(b, liquid, lambda c, n: c[:, 2] < h * fill)
		b.add(lathe([(r * 0.97, h * fill), (0.0, h * fill)], segs=9, mat=liquid, rng=rng, closed_bottom=False, closed_top=False))
		return b.translate(x, y, z)

	def round_flask(x, y, z, liquid):
		f = blob(rng, 0.09, subdiv=2, jitter=0.1, noise=0.05, mat=2, flatten_bottom=0.6).translate(0, 0, 0.055)
		paint(f, liquid, lambda c, n: c[:, 2] < 0.05)
		f.add(tube(rng, length=0.14, radius=0.025, segs=7, rings=2, jitter=0.03, mat=2, cap_end=False).translate(0, 0, 0.12))
		return f.translate(x, y, z)

	top = 0.87
	m.add(flask(-0.1, -0.6, top, 1.0, 3))
	m.add(flask(0.12, -0.3, top, 0.7, 5))
	m.add(beaker(-0.15, 0.05, top, 0.07, 0.16, 4, fill=0.45))
	m.add(round_flask(0.1, 0.45, top, 3))
	# a burner under a tripod with a beaker on it, and a copper coil condenser between two flasks
	m.add(lathe([(0.07, 0.0), (0.075, 0.015), (0.025, 0.02), (0.02, 0.1), (0.03, 0.11)], segs=8, mat=1, rng=rng, jitter=0.03).translate(-0.12, 0.55, top))
	m.add(blob(rng, 0.035, subdiv=1, jitter=0.2, noise=0.2, scale=(0.7, 0.7, 1.4), mat=8).translate(-0.12, 0.55, top + 0.15))
	for k in range(3):
		a = 2 * math.pi * k / 3
		m.add(pole(rng, (-0.12 + 0.1 * math.cos(a), 0.55 + 0.1 * math.sin(a), top), (-0.12 + 0.08 * math.cos(a), 0.55 + 0.08 * math.sin(a), top + 0.22), 0.006, 9, segs=4))
	m.add(beaker(-0.12, 0.55, top + 0.22, 0.065, 0.12, 5, fill=0.5))
	coil = [(0.1 + 0.03 * math.cos(t), -0.3 + 0.04 * t / (2 * math.pi), top + 0.3 + 0.03 * math.sin(t)) for t in np.linspace(0, 6 * math.pi, 25)]
	m.add(path_tube(rng, np.array([(0.12, -0.3, top + 0.2)] + coil + [(0.1, 0.0, top + 0.3), (-0.15, 0.05, top + 0.25)]), radius=0.012, segs=5, jitter=0.0, mat=7))
	# shelf: jars and bottles in a row, a test tube rack on the bench
	for k in range(6):
		y = -0.65 + 0.26 * k + (rng.random() - 0.5) * 0.04
		if k % 2:
			j = lathe([(0.0, 0.0), (0.05, 0.0), (0.055, 0.12), (0.04, 0.14), (0.04, 0.16)], segs=8, mat=2, rng=rng, jitter=0.03)
			paint(j, (3, 4, 5)[k % 3], lambda c, n: c[:, 2] < 0.1)
		else:
			j = lathe([(0.0, 0.0), (0.035, 0.0), (0.04, 0.1), (0.015, 0.14), (0.015, 0.2), (0.02, 0.21)], segs=7, mat=2, rng=rng, jitter=0.03)
			paint(j, (4, 5, 3)[k % 3], lambda c, n: c[:, 2] < 0.08)
			j.add(cylinder(rng, 0.017, 0.21, 0.235, 9, segs=6))
		m.add(j.translate(0.28, y, 1.418))
	rack = box((0.08, 0.3, 0.03), (0.2, -0.6, top + 0.015), mat=6, jitter=0.02, rng=rng)
	rack.add(box((0.06, 0.3, 0.02), (0.2, -0.6, top + 0.12), mat=6))
	for k in range(5):
		t = tube(rng, length=0.14, radius=0.011, segs=6, rings=2, jitter=0.02, mat=2, cap_end=False).translate(0.2, -0.72 + 0.06 * k, top + 0.02)
		paint(t, (3, 4, 5, 3, 4)[k], lambda c, n: c[:, 2] < top + 0.08)
		rack.add(t)
	m.add(rack)
	# mortar and pestle
	m.add(lathe([(0.0, 0.0), (0.05, 0.0), (0.075, 0.06), (0.07, 0.07), (0.055, 0.07), (0.0, 0.03)], segs=9, mat=10, rng=rng, jitter=0.03).translate(0.15, 0.65, top))
	m.add(tube(rng, length=0.12, radius=0.013, segs=6, rings=2, jitter=0.04, mat=10, profile=lambda t: 1 + 0.6 * t).rotate("y", 50).translate(0.12, 0.65, top + 0.03))
	return centered(m), cells


# ---------------------------------------------------------------- 9. augment station (medical chair with a tool arm)


@station("augment_station", roughness=0.6)
def augment_station(rng):
	cells = [C((70, 74, 80), 8, streak="x", streak_amt=0.08), vinyl(), P.metal(), P.teal(), P.white(), P.black(), P.leather_dark(), P.red_glow()]
	m = Mesh()
	# pedestal and seat; body axis along Y with the head toward +Y
	m.add(lathe([(0.34, 0.0), (0.36, 0.04), (0.2, 0.08), (0.13, 0.12), (0.13, 0.42), (0.26, 0.46)], segs=12, mat=0, rng=rng, jitter=0.02))
	m.add(box((0.64, 0.64, 0.1), (0, 0, 0.5), mat=0, jitter=0.0))
	m.add(box((0.6, 0.6, 0.1), (0, 0, 0.6), mat=1, jitter=0.02, rng=rng))
	# backrest reclined from the seat's +Y edge, headrest at its top
	back = box((0.6, 0.85, 0.1), (0, 0.425, 0.05), mat=1, jitter=0.02, rng=rng)
	back.add(box((0.64, 0.85, 0.05), (0, 0.425, -0.02), mat=0, jitter=0.0))
	back.add(box((0.3, 0.2, 0.09), (0, 0.78, 0.14), mat=1, jitter=0.03, rng=rng))
	m.add(back.rotate("x", 48).translate(0, 0.28, 0.55))
	# leg rest tilting down from the seat's -Y edge
	legs = box((0.56, 0.7, 0.09), (0, -0.35, 0.045), mat=1, jitter=0.02, rng=rng)
	legs.add(box((0.6, 0.7, 0.05), (0, -0.35, -0.02), mat=0, jitter=0.0))
	m.add(legs.rotate("x", 22).translate(0, -0.3, 0.55))
	# armrests with straps, a red indicator
	for sx in (-1, 1):
		m.add(box((0.1, 0.5, 0.06), (sx * 0.36, 0.05, 0.74), mat=1, jitter=0.02, rng=rng))
		m.add(box((0.06, 0.06, 0.14), (sx * 0.36, 0.2, 0.64), mat=0))
		m.add(box((0.14, 0.05, 0.1), (sx * 0.36, 0.0, 0.77), mat=6, jitter=0.0))
	m.add(box((0.05, 0.05, 0.02), (0.36, 0.28, 0.78), mat=7))
	# tool arm: a column beside the chair, two jointed links reaching over the chest, a tool head and a lamp
	m.add(lathe([(0.22, 0.0), (0.23, 0.04), (0.06, 0.08), (0.06, 1.35), (0.09, 1.4)], segs=10, mat=0, rng=rng, jitter=0.02).translate(0.62, 0.55, 0))
	arm = np.array([(0.62, 0.55, 1.38), (0.45, 0.45, 1.62), (0.12, 0.3, 1.62), (0.0, 0.25, 1.4)])
	m.add(path_tube(rng, arm, radius=0.04, segs=7, jitter=0.02, mat=2))
	for p in arm[1:3]:
		m.add(blob(rng, 0.065, subdiv=1, jitter=0.1, noise=0.05, mat=0).translate(*p))
	head = lathe([(0.0, 0.0), (0.09, 0.0), (0.1, 0.1), (0.07, 0.16), (0.07, 0.2)], segs=9, mat=0, rng=rng, jitter=0.02).rotate("x", 180).translate(0, 0.25, 1.4)
	head.add(tube(rng, length=0.12, radius=0.012, segs=5, rings=2, jitter=0.0, mat=2, profile=lambda t: 1 - 0.7 * t).rotate("x", 180).translate(0.03, 0.25, 1.2))
	head.add(tube(rng, length=0.1, radius=0.006, segs=4, rings=1, jitter=0.0, mat=2).rotate("x", 180).translate(-0.04, 0.25, 1.2))
	head.add(lathe([(0.0, 0.0), (0.02, 0.0), (0.018, 0.03), (0.0, 0.03)], segs=6, mat=3, rng=rng).rotate("x", 180).translate(0.0, 0.3, 1.2))
	m.add(head)
	lamp_arm = np.array([(0.62, 0.55, 1.1), (0.5, 0.8, 1.3), (0.2, 0.75, 1.5)])
	m.add(path_tube(rng, lamp_arm, radius=0.022, segs=5, jitter=0.02, mat=2))
	lamp = lathe([(0.0, 0.0), (0.16, 0.0), (0.17, 0.05), (0.1, 0.1), (0.03, 0.11)], segs=10, mat=0, rng=rng, jitter=0.02, cap_mat=4)
	m.add(lamp.rotate("y", 180).rotate("x", -20).translate(0.2, 0.75, 1.5))
	# instrument tray on the other side: a post, the tray, a few tools and a vial
	m.add(cylinder(rng, 0.03, 0.0, 0.85, 0, segs=7).translate(-0.6, 0.3, 0))
	m.add(lathe([(0.18, 0.0), (0.19, 0.03)], segs=9, mat=0, rng=rng).translate(-0.6, 0.3, 0))
	m.add(box((0.34, 0.46, 0.03), (-0.6, 0.3, 0.86), mat=2, jitter=0.0))
	for k in range(3):
		m.add(box((0.012, 0.16, 0.008), (-0.68 + 0.05 * k, 0.22, 0.88), mat=2))
	m.add(tube(rng, length=0.14, radius=0.012, segs=6, rings=2, jitter=0.0, mat=4, axis="y").translate(-0.5, 0.33, 0.89))
	m.add(lathe([(0.0, 0.0), (0.025, 0.0), (0.03, 0.06), (0.012, 0.075), (0.012, 0.09)], segs=7, mat=3, rng=rng, jitter=0.02).translate(-0.5, 0.5, 0.875))
	m.add(box((0.2, 0.14, 0.06), (-0.6, 0.45, 0.905), mat=5, jitter=0.0))
	return centered(m), cells
