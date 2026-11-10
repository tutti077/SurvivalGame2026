"""The ten catchable fish (Assets/data/resources.json fish_*), real catch sizes, lying on their side on
the ground with the right flank up. One lofted body builder; each species sets its outline, fins and
the flank markings painted per triangle (bars, chain net, spots, lateral band)."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, prism, fan, weld_displace
from .kit import registry, ground, paint, squash, loft

ITEMS, item = registry()


def C(rgb, d=10, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.06), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


# cell slots every fish uses: 0 back, 1 flank, 2 belly, 3 mark (bars / spots / net), 4 fin, 5 eye, 6 pupil, 7 accent
def fin(outline, thick, mat, rng, x=0.0, z=0.0, y=0.0):
	"""A fin outline [(x, z)] extruded thin across Y (the fish's width), placed at (x, y, z)."""
	f = prism(outline, thick, z0=-thick / 2, mat=mat, side_mat=mat, rng=rng, jitter=thick * 0.3)
	f.rotate("x", 90)  # outline plane becomes XZ
	return f.translate(x, y, z)


def fish(rng, L, half_h, width, head_t=0.8, snout=0.5, tail="fork", dorsal=(), anal=(0.25, 0.42, 0.5), pectoral=0.12,
		 mark=None, eye_r=0.05, eye_t=0.88, eye_z=0.3, mouth=0.4, adipose=None, barbels=False, tail_h=1.0, curl=0.04, profile=None,
		 belly_line=-0.35, back_line=0.45):
	"""Body along +X (tail at x = 0, snout at x = L). half_h(t) shape is ``profile`` or a default
	fusiform curve. dorsal: list of (t0, t1, height, spiny). Returns the fish lying on its left side."""
	if profile is None:
		def profile(t):
			# narrow caudal peduncle, deepest a little ahead of mid, rounding into the head
			return 0.28 + 0.72 * math.sin(math.pi * min(1.0, 0.12 + 0.88 * t) ** 0.85) ** 0.9 * (1 - snout * max(0, t - head_t) ** 2 / (1 - head_t) ** 2)
	rz = lambda t: half_h * profile(t)
	ry = lambda t: half_h * width * profile(t) * (1 - 0.3 * max(0, 0.25 - t) / 0.25)
	cz = lambda t: -0.15 * half_h * (1 - profile(t))
	ts = np.concatenate([np.linspace(0.0, 0.85, 20), np.linspace(0.88, 1.0, 6)])
	body = loft(rng, L, ry, rz, cz=cz, segs=14, mat=1, jitter=0.03, rings_t=ts)
	# colour zones: dark back, flank, pale belly, then the species marking
	c = body.centroids()
	u = np.clip(c[:, 0] / L, 0, 1)
	hh = np.array([rz(t) for t in u])
	v = (c[:, 2] - np.array([cz(t) for t in u])) / np.maximum(hh, 1e-6)
	body.mat[v > back_line] = 0
	body.mat[v < belly_line] = 2
	if mark is not None:
		sel = mark(u, v, c)
		body.mat[sel] = 3
	m = body
	# tail fin behind x = 0
	th = half_h * 1.15 * tail_h
	if tail == "fork":
		out = [(0.02 * L, -0.25 * half_h), (-0.16 * L, -th), (-0.13 * L, -0.25 * th), (-0.09 * L, 0.0), (-0.13 * L, 0.25 * th), (-0.16 * L, th), (0.02 * L, 0.25 * half_h)]
	elif tail == "round":
		out = [(0.02 * L, -0.25 * half_h), (-0.08 * L, -0.85 * th), (-0.15 * L, -0.5 * th), (-0.17 * L, 0.0), (-0.15 * L, 0.5 * th), (-0.08 * L, 0.85 * th), (0.02 * L, 0.25 * half_h)]
	elif tail == "eel":
		out = [(0.02 * L, -0.5 * half_h), (-0.05 * L, -0.4 * half_h), (-0.08 * L, 0.0), (-0.05 * L, 0.4 * half_h), (0.02 * L, 0.5 * half_h)]
	else:  # truncate / slightly forked
		out = [(0.02 * L, -0.25 * half_h), (-0.14 * L, -th), (-0.12 * L, 0.0), (-0.14 * L, th), (0.02 * L, 0.25 * half_h)]
	m.add(fin(out, max(0.003, half_h * 0.06), 4, rng, z=cz(0.0)))
	# dorsal fins along the back: spiny ones get a ragged edge
	for (t0, t1, h, spiny) in dorsal:
		pts = [(t0 * L, rz(t0) * 0.8)]
		n = 7 if spiny else 4
		for k in range(n + 1):
			t = t0 + (t1 - t0) * k / n
			top = rz(t) + h * half_h * math.sin(math.pi * min(1, 0.15 + 0.85 * k / n)) ** 0.5
			if spiny:
				top *= 1.0 if k % 2 == 0 else 0.86
			pts.append((t * L, top))
		pts.append((t1 * L, rz(t1) * 0.8))
		m.add(fin(pts, max(0.0025, half_h * 0.05), 4, rng, z=cz((t0 + t1) / 2)))
	if anal:
		t0, t1, h = anal
		pts = [(t0 * L, -rz(t0) * 0.8), ((t0 + 0.3 * (t1 - t0)) * L, -rz(t0) - h * half_h), (t1 * L, -rz(t1) - 0.3 * h * half_h), (t1 * L, -rz(t1) * 0.8)]
		m.add(fin(pts, max(0.0025, half_h * 0.05), 4, rng, z=cz(t0)))
	if adipose is not None:
		t0 = adipose
		m.add(fin([(t0 * L, rz(t0) * 0.85), ((t0 + 0.03) * L, rz(t0) + 0.25 * half_h), ((t0 + 0.07) * L, rz(t0) * 0.9)], max(0.0025, half_h * 0.05), 4, rng,
				  z=cz(t0)))
	# pectoral and pelvic fins: small flaps on the up flank (+Y) and below
	if pectoral:
		tp = eye_t - 0.16
		pf = fin([(0, 0), (-pectoral * L, 0.2 * half_h), (-pectoral * L * 0.9, -0.15 * half_h)], max(0.002, half_h * 0.04), 4, rng)
		pf.rotate("z", -18).translate(tp * L, ry(tp) * 0.9, cz(tp) - 0.25 * rz(tp))
		m.add(pf)
		pv = fin([(0, 0), (-pectoral * L * 0.8, -0.35 * half_h), (-pectoral * L * 0.5, -0.05 * half_h)], max(0.002, half_h * 0.04), 4, rng)
		m.add(pv.translate((tp - 0.12) * L, ry(tp - 0.12) * 0.4, cz(tp - 0.12) - 0.85 * rz(tp - 0.12)))
	# eye on the up flank and a dark gape line
	er = eye_r * half_h * 2
	ex = eye_t * L
	ey = ry(eye_t) * 0.92
	ez = cz(eye_t) + eye_z * rz(eye_t)
	m.add(blob(rng, er, subdiv=1, jitter=0.1, noise=0.05, scale=(1, 0.45, 1), mat=5).translate(ex, ey, ez))
	m.add(blob(rng, er * 0.55, subdiv=1, jitter=0.1, noise=0.05, scale=(1, 0.4, 1), mat=6).translate(ex + er * 0.1, ey + er * 0.25, ez))
	if mouth:
		gape = path_tube(rng, np.array([(L * 1.0, ry(0.99) * 0.5, cz(1.0) - 0.05 * half_h), (L * (1 - 0.06 * mouth / 0.4), ry(0.95) * 0.9, cz(0.95) - 0.12 * half_h),
										 (L * (1 - 0.1 * mouth / 0.4), ry(0.92) * 0.95, cz(0.92) - 0.28 * half_h)]), radius=max(0.0018, half_h * 0.035), segs=4,
						 jitter=0.0, mat=6)
		m.add(gape)
	if barbels:
		for k, (dy, dz, ln) in enumerate(((0.6, 0.1, 0.22), (0.4, -0.2, 0.12), (0.2, -0.4, 0.1))):
			p0 = np.array([L * 0.985, ry(0.97) * dy, cz(0.97) + dz * rz(0.97)])
			path = [p0 + np.array([ln * L * t, ln * L * 0.4 * t + 0.01 * math.sin(t * 4), -ln * L * 0.3 * t * t]) for t in np.linspace(0, 1, 5)]
			m.add(path_tube(rng, np.array(path), radius=0.0025, segs=4, jitter=0.0, radius_fn=lambda t: 1.0 - 0.7 * t, mat=6))
	weld_displace(m, rng, half_h * 0.015, cells=8.0)
	m.rotate("x", 90)  # lie on the left side: +Y (eye side) up
	m.translate(-L / 2, 0, 0)
	if curl:
		squash(m, lambda p: p + np.column_stack([np.zeros(len(p)), curl * L * ((p[:, 0] / (L / 2)) ** 2 - 0.5), np.zeros(len(p))]))
	return ground(m)


def spots(rng, n, size, u_rng=(0.15, 0.85), v_rng=(-0.4, 0.7)):
	pts = np.column_stack([rng.uniform(*u_rng, n), rng.uniform(*v_rng, n)])

	def fn(u, v, c):
		d = np.min(np.hypot((u[:, None] - pts[None, :, 0]) * 3.0, v[:, None] - pts[None, :, 1]), axis=1)
		return d < size
	return fn


def bars(n, width=0.35, u_rng=(0.15, 0.8), v_min=-0.2):
	def fn(u, v, c):
		f = (u - u_rng[0]) / (u_rng[1] - u_rng[0]) * n
		return (u > u_rng[0]) & (u < u_rng[1]) & ((f % 1) < width) & (v > v_min - 0.15 * np.sin(f * 2.1))
	return fn


EYE = C((214, 196, 120), 6)
PUPIL = P.black()


@item("bass", "fish", roughness=0.6)
def bass(rng):
	# largemouth bass, 0.42 m: olive back, a dark ragged lateral band, cream belly, big jaw
	cells = [C((62, 84, 46)), C((112, 128, 70)), C((220, 214, 176)), C((52, 64, 38)), C((96, 108, 64)), EYE, PUPIL]

	def band(u, v, c):
		return (np.abs(v - 0.02 - 0.08 * np.sin(u * 37)) < 0.16) & (u > 0.1) & (u < 0.82)
	m = fish(rng, 0.42, 0.062, 0.5, head_t=0.75, snout=0.35, tail="truncate", dorsal=[(0.32, 0.52, 0.5, True), (0.18, 0.32, 0.55, False)],
			 mark=band, mouth=0.6, eye_r=0.09, eye_t=0.9)
	return m, cells


@item("bluegill", "fish", roughness=0.6)
def bluegill(rng):
	# bluegill, 0.21 m: a deep disc, olive-blue with faint dark vertical bars, the black ear flap, yellow breast
	cells = [C((52, 72, 96)), C((92, 112, 120)), C((214, 176, 92)), C((64, 78, 92)), C((72, 86, 100)), EYE, PUPIL, P.black()]
	prof = lambda t: 0.25 + 0.75 * math.sin(math.pi * min(1.0, 0.1 + 0.95 * t)) ** 0.6
	bar = bars(6, 0.35, (0.15, 0.75))

	def mk(u, v, c):
		return bar(u, v, c) & (v > -0.3)
	m = fish(rng, 0.21, 0.058, 0.32, tail="fork", dorsal=[(0.2, 0.7, 0.35, True)], anal=(0.2, 0.45, 0.4), mark=mk, eye_r=0.1, eye_t=0.86, mouth=0.2,
			 profile=prof)
	# ear flap: a dark patch behind the eye
	ear = blob(rng, 0.011, subdiv=1, jitter=0.1, noise=0.05, scale=(1.2, 1.0, 0.35), mat=7)
	lo, hi = m.bounds()
	m.add(ear.translate(0.055, 0.008, hi[2] - 0.004))
	return ground(m), cells


@item("catfish", "fish", roughness=0.5)
def catfish(rng):
	# channel catfish, 0.58 m: slate grey-brown, smooth skin, a broad flat head with whiskers, pale belly
	cells = [C((76, 76, 72)), C((112, 108, 98)), C((212, 206, 190)), C((70, 70, 66)), C((88, 86, 80)), C((60, 60, 54)), PUPIL]
	prof = lambda t: 0.3 + 0.7 * math.sin(math.pi * min(1.0, 0.15 + 0.85 * t) ** 0.7) ** 0.7 * (1 - 0.25 * max(0, t - 0.85) / 0.15)
	m = fish(rng, 0.58, 0.055, 0.95, head_t=0.85, tail="fork", dorsal=[(0.62, 0.72, 0.8, False)], adipose=0.25, anal=(0.12, 0.42, 0.35), mark=spots(rng, 6, 0.06),
			 eye_r=0.05, eye_t=0.92, eye_z=0.15, mouth=0.5, barbels=True, profile=prof, belly_line=-0.45)
	return m, cells


@item("eel", "fish", roughness=0.4)
def eel(rng):
	# american eel, 0.75 m: a rope of dark olive muscle, one long fin round the tail, lying in an S
	cells = [C((52, 52, 36)), C((76, 72, 46)), C((150, 140, 96)), C((60, 58, 40)), C((66, 62, 42)), EYE, PUPIL]
	L, hh = 0.75, 0.022
	prof = lambda t: 0.35 + 0.65 * min(1.0, t * 3) ** 0.7 * (1 - 0.4 * max(0, t - 0.9) / 0.1)
	m = fish(rng, L, hh, 0.85, tail="eel", dorsal=[(0.05, 0.6, 0.6, False)], anal=(0.05, 0.5, 0.5), pectoral=0.03, eye_r=0.12, eye_t=0.96, mouth=0.3,
			 profile=prof, curl=0.0)
	# bend into an S lying on the ground
	squash(m, lambda p: p + np.column_stack([np.zeros(len(p)), 0.08 * np.sin((p[:, 0] - p[:, 0].min()) / L * 2 * math.pi), np.zeros(len(p))]))
	return ground(m), cells


@item("perch", "fish", roughness=0.6)
def perch(rng):
	# yellow perch, 0.26 m: banded gold flanks (six dark saddles), green back, orange lower fins
	cells = [C((96, 104, 46)), C((206, 170, 64)), C((232, 222, 186)), C((70, 76, 40)), C((150, 128, 70)), EYE, PUPIL, C((214, 120, 48))]
	m = fish(rng, 0.26, 0.038, 0.45, tail="fork", dorsal=[(0.4, 0.68, 0.7, True), (0.18, 0.38, 0.5, False)], mark=bars(6, 0.42, (0.1, 0.78), v_min=-0.35),
			 eye_r=0.12, eye_t=0.88, mouth=0.35)
	return m, cells


def chain(u, v, c):
	a = (u * 16 + v * 1.6) % 1.0
	b = (u * 16 - v * 1.6) % 1.0
	return ((a < 0.18) | (b < 0.18)) & (v > -0.4) & (u > 0.08) & (u < 0.86)


@item("pickerel", "fish", roughness=0.6)
def pickerel(rng):
	# chain pickerel, 0.42 m: slim, duck-bill snout, a dark chain net over green-gold flanks
	cells = [C((60, 76, 40)), C((150, 150, 72)), C((222, 214, 170)), C((48, 58, 32)), C((120, 120, 66)), EYE, PUPIL]
	prof = lambda t: 0.4 + 0.6 * min(1.0, 0.25 + t * 1.5) ** 0.5 * (1 - 0.55 * max(0, t - 0.78) ** 1.2 / 0.22 ** 1.2)
	m = fish(rng, 0.42, 0.03, 0.75, tail="fork", dorsal=[(0.14, 0.26, 0.9, False)], anal=(0.12, 0.25, 0.8), mark=chain, eye_r=0.13, eye_t=0.84, eye_z=0.35,
			 mouth=0.8, profile=prof)
	return m, cells


@item("pike", "fish", roughness=0.6)
def pike(rng):
	# northern pike, 0.78 m: long and toothy, dark green with rows of pale bean spots, fins set far back
	cells = [C((44, 62, 38)), C((84, 106, 56)), C((222, 220, 186)), C((176, 178, 120)), C((96, 92, 52)), EYE, PUPIL]
	prof = lambda t: 0.4 + 0.6 * min(1.0, 0.25 + t * 1.5) ** 0.5 * (1 - 0.6 * max(0, t - 0.78) ** 1.2 / 0.22 ** 1.2)
	m = fish(rng, 0.78, 0.048, 0.75, tail="fork", dorsal=[(0.12, 0.24, 0.9, False)], anal=(0.1, 0.22, 0.8), mark=spots(rng, 30, 0.13, (0.1, 0.8), (-0.3, 0.6)),
			 eye_r=0.1, eye_t=0.86, eye_z=0.35, mouth=0.8, profile=prof)
	return m, cells


@item("sunfish", "fish", roughness=0.6)
def sunfish(rng):
	# pumpkinseed sunfish, 0.19 m: a bright little disc, teal-green with orange spots, orange belly,
	# a black ear spot tipped red
	cells = [C((52, 96, 84)), C((80, 136, 118)), C((226, 150, 60)), C((214, 140, 60)), C((70, 110, 96)), EYE, PUPIL, C((190, 50, 40))]
	prof = lambda t: 0.25 + 0.75 * math.sin(math.pi * min(1.0, 0.1 + 0.95 * t)) ** 0.55
	m = fish(rng, 0.19, 0.056, 0.32, tail="fork", dorsal=[(0.2, 0.7, 0.35, True)], anal=(0.2, 0.45, 0.4), mark=spots(rng, 14, 0.12, (0.15, 0.8), (-0.3, 0.7)),
			 eye_r=0.1, eye_t=0.86, mouth=0.2, profile=prof, belly_line=-0.3)
	lo, hi = m.bounds()
	ear = blob(rng, 0.009, subdiv=1, jitter=0.1, noise=0.05, scale=(1.2, 1.0, 0.35), mat=6)
	ear.add(blob(rng, 0.004, subdiv=0, scale=(1, 1, 0.4), mat=7).translate(-0.006, 0, 0.002))
	m.add(ear.translate(0.05, 0.008, hi[2] - 0.004))
	return ground(m), cells


@item("trout", "fish", roughness=0.55)
def trout(rng):
	# brown trout, 0.42 m: golden-brown flank, black spots above, red spots along the lateral line, cream belly
	cells = [C((92, 80, 50)), C((170, 140, 84)), C((232, 220, 180)), C((40, 34, 28)), C((140, 116, 74)), EYE, PUPIL, C((196, 70, 50))]
	blk = spots(rng, 22, 0.1, (0.12, 0.85), (0.05, 0.8))
	red = spots(rng, 9, 0.1, (0.15, 0.8), (-0.2, 0.1))

	def mk(u, v, c):
		return blk(u, v, c)
	m = fish(rng, 0.42, 0.056, 0.55, tail="truncate", dorsal=[(0.4, 0.56, 0.6, False)], adipose=0.22, mark=mk, eye_r=0.09, eye_t=0.88, mouth=0.45)
	# the red spots on a second pass (painted on the up flank after lying down: centroid x/y)
	c = m.centroids()
	lo, hi = m.bounds()
	u = (c[:, 0] - lo[0]) / (hi[0] - lo[0])
	sel = (m.mat == 1) & (m.normals()[:, 2] > 0.5)
	for k in range(9):
		uu = 0.25 + 0.06 * k
		yy = lo[1] + (hi[1] - lo[1]) * (0.48 + 0.05 * math.sin(k * 2.3))
		sel_k = sel & (np.hypot((u - uu) * 4.0, (c[:, 1] - yy) / 0.03) < 0.12)
		m.mat[sel_k] = 7
	return m, cells


@item("walleye", "fish", roughness=0.55)
def walleye(rng):
	# walleye, 0.52 m: olive-gold with faint dark saddles, a big glassy eye
	cells = [C((96, 92, 50)), C((170, 156, 82)), C((226, 220, 190)), C((110, 104, 56)), C((140, 128, 70)), C((226, 222, 190), 4), C((80, 80, 70))]
	m = fish(rng, 0.52, 0.055, 0.6, tail="fork", dorsal=[(0.44, 0.66, 0.65, True), (0.22, 0.4, 0.55, False)], mark=bars(5, 0.4, (0.15, 0.75), v_min=0.1),
			 eye_r=0.16, eye_t=0.89, eye_z=0.35, mouth=0.55)
	return m, cells
