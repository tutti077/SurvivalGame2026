"""Triangle-soup mesh helpers for the low-poly item generator (numpy only, no Blender).

A Mesh is a list of flat-shaded triangles: ``tris`` (n,3,3) metres, one ``mat`` id per triangle
(an index into the item's texture cells, see texture.py) and ``uv`` (n,3,2) filled in by
texture.assign_uvs just before export. Everything is authored Z-up, metres, like the Blender scripts.
"""
import math
import numpy as np

# ------------------------------------------------------------------ noise


def value_noise_3d(rng, pts, cells=3.0, octaves=2):
	"""Smooth-ish 0..1 noise at points (n,3) from a hashed lattice (deterministic for a seed)."""
	seed = int(rng.integers(0, 2**31 - 1))
	out = np.zeros(len(pts))
	amp = 1.0
	total = 0.0
	for o in range(octaves):
		p = pts * cells * (2 ** o)
		p0 = np.floor(p).astype(np.int64)
		f = p - p0
		f = f * f * (3 - 2 * f)
		acc = np.zeros(len(pts))
		for dx in (0, 1):
			for dy in (0, 1):
				for dz in (0, 1):
					c = p0 + (dx, dy, dz)
					h = (c[:, 0] * 73856093) ^ (c[:, 1] * 19349663) ^ (c[:, 2] * 83492791) ^ seed
					h = (h * 2654435761) & 0xFFFFFFFF
					v = (h % 10007) / 10007.0
					w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
					acc += v * w
		out += acc * amp
		total += amp
		amp *= 0.5
	return out / total


# ------------------------------------------------------------------ Mesh


class Mesh:
	def __init__(self, tris=None, mat=None):
		self.tris = np.zeros((0, 3, 3)) if tris is None else np.asarray(tris, float)
		self.mat = np.zeros(0, int) if mat is None else np.asarray(mat, int)
		self.uv = None

	def copy(self):
		m = Mesh(self.tris.copy(), self.mat.copy())
		return m

	def add(self, other):
		self.tris = np.concatenate([self.tris, other.tris])
		self.mat = np.concatenate([self.mat, other.mat])
		return self

	# --- transforms (in place, chainable)
	def translate(self, x=0.0, y=0.0, z=0.0):
		self.tris = self.tris + np.array([x, y, z], float)
		return self

	def scale(self, x=1.0, y=None, z=None):
		if y is None:
			y = x
		if z is None:
			z = y if y is not None else x
		self.tris = self.tris * np.array([x, y, z], float)
		if (x < 0) ^ (y < 0) ^ (z < 0):
			self.tris = self.tris[:, ::-1, :]
		return self

	def rotate(self, axis, degrees):
		R = rot_matrix(axis, degrees)
		self.tris = self.tris @ R.T
		return self

	def transform(self, R):
		self.tris = self.tris @ np.asarray(R, float).T
		return self

	def set_mat(self, m):
		self.mat[:] = m
		return self

	def flip(self):
		self.tris = self.tris[:, ::-1, :]
		return self

	# --- queries
	def bounds(self):
		p = self.tris.reshape(-1, 3)
		return p.min(0), p.max(0)

	def centroids(self):
		return self.tris.mean(1)

	def normals(self):
		a = self.tris[:, 1] - self.tris[:, 0]
		b = self.tris[:, 2] - self.tris[:, 0]
		n = np.cross(a, b)
		l = np.linalg.norm(n, axis=1)
		l[l == 0] = 1
		return n / l[:, None]

	def areas(self):
		a = self.tris[:, 1] - self.tris[:, 0]
		b = self.tris[:, 2] - self.tris[:, 0]
		return 0.5 * np.linalg.norm(np.cross(a, b), axis=1)

	def drop_degenerate(self, eps=1e-9):
		keep = self.areas() > eps
		self.tris = self.tris[keep]
		self.mat = self.mat[keep]
		return self

	def recenter_xy(self, ground=True):
		lo, hi = self.bounds()
		c = (lo + hi) / 2
		self.translate(-c[0], -c[1], -lo[2] if ground else -c[2])
		return self


def rot_matrix(axis, degrees):
	a = math.radians(degrees)
	c, s = math.cos(a), math.sin(a)
	if axis == "x":
		return np.array([[1, 0, 0], [0, c, -s], [0, s, c]], float)
	if axis == "y":
		return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]], float)
	return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], float)


def basis_from_dir(d):
	"""Rotation whose +Z is d (unit)."""
	d = np.asarray(d, float)
	d = d / np.linalg.norm(d)
	up = np.array([0, 0, 1.0]) if abs(d[2]) < 0.9 else np.array([1.0, 0, 0])
	x = np.cross(up, d)
	x /= np.linalg.norm(x)
	y = np.cross(d, x)
	return np.stack([x, y, d], axis=1)


# ------------------------------------------------------------------ vertex-indexed builder


def from_quads_grid(grid, closed_u=False, closed_v=False, mat=0, flip=False):
	"""grid (nu, nv, 3) of points → triangles between neighbours. Each quad split along its shorter
	diagonal so the facet pattern stays varied rather than a regular zigzag."""
	nu, nv = grid.shape[:2]
	tris = []
	ur = range(nu if closed_u else nu - 1)
	vr = range(nv if closed_v else nv - 1)
	for i in ur:
		i2 = (i + 1) % nu
		for j in vr:
			j2 = (j + 1) % nv
			a, b, c, d = grid[i, j], grid[i2, j], grid[i2, j2], grid[i, j2]
			if np.linalg.norm(a - c) < np.linalg.norm(b - d):
				tris.append((a, b, c))
				tris.append((a, c, d))
			else:
				tris.append((a, b, d))
				tris.append((b, c, d))
	m = Mesh(np.array(tris), np.full(len(tris), mat))
	if flip:
		m.flip()
	return m.drop_degenerate()


def fan(points, center=None, mat=0, flip=False):
	pts = np.asarray(points, float)
	if center is None:
		center = pts.mean(0)
	tris = [(center, pts[i], pts[(i + 1) % len(pts)]) for i in range(len(pts))]
	m = Mesh(np.array(tris), np.full(len(tris), mat))
	if flip:
		m.flip()
	return m.drop_degenerate()


def polygon_fill(points2d, z=0.0, mat=0, flip=False):
	"""Ear-clip a simple 2D polygon (x,y) into triangles at height z, CCW = facing +Z."""
	pts = [np.array([p[0], p[1]], float) for p in points2d]
	# ensure CCW
	area = 0.0
	for i in range(len(pts)):
		x1, y1 = pts[i]
		x2, y2 = pts[(i + 1) % len(pts)]
		area += x1 * y2 - x2 * y1
	if area < 0:
		pts = pts[::-1]
	idx = list(range(len(pts)))
	tris = []

	def cross(o, a, b):
		return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

	def inside(p, a, b, c):
		return cross(a, b, p) >= -1e-12 and cross(b, c, p) >= -1e-12 and cross(c, a, p) >= -1e-12

	guard = 0
	while len(idx) > 3 and guard < 10000:
		guard += 1
		n = len(idx)
		found = False
		for k in range(n):
			i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % n]
			a, b, c = pts[i0], pts[i1], pts[i2]
			if cross(a, b, c) <= 1e-12:
				continue
			ok = True
			for j in idx:
				if j in (i0, i1, i2):
					continue
				if inside(pts[j], a, b, c):
					ok = False
					break
			if ok:
				tris.append((a, b, c))
				idx.pop(k)
				found = True
				break
		if not found:
			break
	if len(idx) == 3:
		tris.append((pts[idx[0]], pts[idx[1]], pts[idx[2]]))
	t3 = np.array([[(p[0], p[1], z) for p in t] for t in tris]) if tris else np.zeros((0, 3, 3))
	m = Mesh(t3, np.full(len(t3), mat))
	if flip:
		m.flip()
	return m


# ------------------------------------------------------------------ primitives


def blob(rng, radius=0.1, subdiv=2, jitter=0.25, noise=0.2, noise_cells=2.5, scale=(1, 1, 1), mat=0,
		 flatten_bottom=None, seed_shift=0):
	"""Icosphere, each vertex pushed in/out by low-frequency noise plus a little random jitter, so the
	facets come out in varied sizes and no part reads as a clean sphere."""
	verts, faces = icosphere(subdiv)
	v = verts.copy()
	n = value_noise_3d(rng, v + seed_shift, cells=noise_cells)
	r = radius * (1.0 + noise * (n - 0.5) * 2.0)
	v = v * r[:, None]
	v += (rng.random(v.shape) - 0.5) * jitter * radius / (subdiv + 1)
	v *= np.asarray(scale, float)
	if flatten_bottom is not None:
		v[:, 2] = np.maximum(v[:, 2], -radius * scale[2] * flatten_bottom)
	tris = v[faces]
	return Mesh(tris, np.full(len(tris), mat)).drop_degenerate()


_ICO_CACHE = {}


def icosphere(subdiv):
	if subdiv in _ICO_CACHE:
		return _ICO_CACHE[subdiv]
	t = (1 + 5 ** 0.5) / 2
	verts = np.array([[-1, t, 0], [1, t, 0], [-1, -t, 0], [1, -t, 0], [0, -1, t], [0, 1, t], [0, -1, -t], [0, 1, -t],
					  [t, 0, -1], [t, 0, 1], [-t, 0, -1], [-t, 0, 1]], float)
	verts /= np.linalg.norm(verts, axis=1)[:, None]
	faces = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6),
			 (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10),
			 (8, 6, 7), (9, 8, 1)]
	verts = list(map(tuple, verts))
	for _ in range(subdiv):
		cache = {}
		new = []

		def mid(a, b):
			k = (min(a, b), max(a, b))
			if k in cache:
				return cache[k]
			p = (np.array(verts[a]) + np.array(verts[b])) / 2
			p /= np.linalg.norm(p)
			verts.append(tuple(p))
			cache[k] = len(verts) - 1
			return cache[k]

		for a, b, c in faces:
			ab, bc, ca = mid(a, b), mid(b, c), mid(c, a)
			new += [(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]
		faces = new
	out = (np.array(verts), np.array(faces))
	_ICO_CACHE[subdiv] = out
	return out


def tube(rng, length=1.0, radius=0.05, segs=7, rings=4, profile=None, jitter=0.15, twist=0.0, bend=None,
		 cap_start=True, cap_end=True, mat=0, cap_mat=None, axis="z", wobble=0.0):
	"""Faceted tube along +axis from 0..length. profile(t)->radius multiplier; jitter = radial noise;
	bend(t)->(dx,dy) centre-line offset; wobble = ring offset noise. Ring segment counts are kept odd/varied."""
	if cap_mat is None:
		cap_mat = mat
	ts = np.linspace(0, 1, rings + 1)
	grid = np.zeros((rings + 1, segs, 3))
	for i, t in enumerate(ts):
		rm = profile(t) if profile else 1.0
		base = rng.random() * 2 * math.pi * 0.0 + twist * t
		off = np.array(bend(t)) if bend else np.zeros(2)
		off = off + (rng.random(2) - 0.5) * wobble * radius
		for j in range(segs):
			a = base + 2 * math.pi * j / segs + (rng.random() - 0.5) * (2 * math.pi / segs) * 0.35
			rr = radius * rm * (1 + (rng.random() - 0.5) * 2 * jitter)
			grid[i, j] = (off[0] + rr * math.cos(a), off[1] + rr * math.sin(a), t * length)
	m = from_quads_grid(grid, closed_v=True, mat=mat).flip()
	if cap_start:
		m.add(fan(grid[0][::-1], mat=cap_mat))
	if cap_end:
		m.add(fan(grid[-1], mat=cap_mat))
	if axis == "x":
		m.rotate("y", 90)
	elif axis == "y":
		m.rotate("x", -90)
	return m


def path_tube(rng, path, radius=0.02, segs=6, jitter=0.15, radius_fn=None, cap=True, mat=0, cap_mat=None):
	"""Faceted tube swept along a polyline path (n,3) with rotation-minimising frames."""
	if cap_mat is None:
		cap_mat = mat
	path = np.asarray(path, float)
	n = len(path)
	tangents = np.zeros_like(path)
	tangents[1:-1] = path[2:] - path[:-2]
	tangents[0] = path[1] - path[0]
	tangents[-1] = path[-1] - path[-2]
	tangents /= np.linalg.norm(tangents, axis=1)[:, None]
	# initial normal
	t0 = tangents[0]
	ref = np.array([0, 0, 1.0]) if abs(t0[2]) < 0.9 else np.array([1.0, 0, 0])
	nrm = np.cross(t0, ref)
	nrm /= np.linalg.norm(nrm)
	grid = np.zeros((n, segs, 3))
	phase = rng.random(segs) * 0.35 * 2 * math.pi / segs
	for i in range(n):
		t = tangents[i]
		nrm = nrm - t * (nrm @ t)
		nrm /= np.linalg.norm(nrm)
		b = np.cross(t, nrm)
		r = radius * (radius_fn(i / (n - 1)) if radius_fn else 1.0)
		for j in range(segs):
			a = 2 * math.pi * j / segs + phase[j]
			rr = r * (1 + (rng.random() - 0.5) * 2 * jitter)
			grid[i, j] = path[i] + nrm * rr * math.cos(a) + b * rr * math.sin(a)
	m = from_quads_grid(grid, closed_v=True, mat=mat).flip()
	if cap:
		m.add(fan(grid[0][::-1], mat=cap_mat))
		m.add(fan(grid[-1], mat=cap_mat))
	return m


def box(size=(1, 1, 1), center=(0, 0, 0), mat=0, jitter=0.0, rng=None):
	sx, sy, sz = np.asarray(size, float) / 2
	c = np.asarray(center, float)
	corners = np.array([[x, y, z] for z in (-sz, sz) for y in (-sy, sy) for x in (-sx, sx)])
	if jitter and rng is not None:
		corners = corners + (rng.random(corners.shape) - 0.5) * jitter * np.asarray(size)
	corners = corners + c
	# faces (quads) with outward winding
	q = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]
	tris = []
	for a, b, c_, d in q:
		tris.append((corners[a], corners[b], corners[c_]))
		tris.append((corners[a], corners[c_], corners[d]))
	return Mesh(np.array(tris), np.full(len(tris), mat))


def prism(points2d, height, z0=0.0, mat=0, side_mat=None, taper=1.0, rng=None, jitter=0.0):
	"""Extrude a 2D polygon from z0 up by height (optionally tapering the top)."""
	if side_mat is None:
		side_mat = mat
	pts = np.array([(p[0], p[1]) for p in points2d], float)
	area = 0.0
	for i in range(len(pts)):
		x1, y1 = pts[i]
		x2, y2 = pts[(i + 1) % len(pts)]
		area += x1 * y2 - x2 * y1
	if area < 0:
		pts = pts[::-1]
	if rng is not None and jitter:
		pts = pts + (rng.random(pts.shape) - 0.5) * jitter
	c = pts.mean(0)
	top = c + (pts - c) * taper
	bottom3 = np.column_stack([pts, np.full(len(pts), z0)])
	top3 = np.column_stack([top, np.full(len(pts), z0 + height)])
	m = polygon_fill(pts, z=z0, mat=mat).flip()
	m.add(polygon_fill(top, z=z0 + height, mat=mat))
	grid = np.stack([bottom3, top3], axis=0)  # (2, n, 3)
	m.add(from_quads_grid(grid, closed_v=True, mat=side_mat).flip())
	return m


def lathe(profile, segs=8, mat=0, rng=None, jitter=0.0, closed_top=True, closed_bottom=True, cap_mat=None):
	"""Revolve profile [(r, z), ...] (bottom→top) around Z."""
	if cap_mat is None:
		cap_mat = mat
	prof = np.asarray(profile, float)
	grid = np.zeros((len(prof), segs, 3))
	for i, (r, z) in enumerate(prof):
		for j in range(segs):
			a = 2 * math.pi * j / segs
			if rng is not None and jitter:
				a += (rng.random() - 0.5) * jitter * 2 * math.pi / segs
				rr = r * (1 + (rng.random() - 0.5) * jitter)
			else:
				rr = r
			grid[i, j] = (rr * math.cos(a), rr * math.sin(a), z)
	m = from_quads_grid(grid, closed_v=True, mat=mat).flip()
	if closed_bottom and prof[0][0] > 1e-6:
		m.add(fan(grid[0][::-1], mat=cap_mat))
	if closed_top and prof[-1][0] > 1e-6:
		m.add(fan(grid[-1], mat=cap_mat))
	return m


def card(w, h, mat=0, double=True, center=(0, 0, 0), normal="y"):
	"""A rectangle in the XZ plane (facing +Y) of width w, height h, bottom edge at z=0."""
	a = np.array([-w / 2, 0, 0.0])
	b = np.array([w / 2, 0, 0.0])
	c = np.array([w / 2, 0, h])
	d = np.array([-w / 2, 0, h])
	tris = [(a, c, b), (a, d, c)]
	if double:
		tris += [(a, b, c), (a, c, d)]
	m = Mesh(np.array(tris), np.full(len(tris), mat))
	if normal == "z":
		m.rotate("x", 90)
	return m.translate(*center)


def leaf(rng, length=0.2, width=0.08, mat=0, bend=0.3, segments=4, double=True, taper_tip=True):
	"""A folded leaf/strip: centre spine with a crease, curving up along its length."""
	ts = np.linspace(0, 1, segments + 1)
	rows = []
	for t in ts:
		w = width * (math.sin(math.pi * (0.1 + 0.9 * t)) if taper_tip else 1.0)
		z = bend * length * (t ** 2)
		crease = width * 0.25
		rows.append([(-w / 2, t * length, z), (0, t * length, z + crease), (w / 2, t * length, z)])
	grid = np.array(rows)  # (segments+1, 3, 3)
	m = from_quads_grid(grid, mat=mat)
	if double:
		m.add(m.copy().flip())
	return m


def jitter_vertices(mesh, rng, amount):
	"""Independent per-triangle vertex jitter (breaks shared edges slightly - use small amounts)."""
	mesh.tris = mesh.tris + (rng.random(mesh.tris.shape) - 0.5) * amount
	return mesh


def displace(mesh, rng, amount, cells=3.0):
	"""Push every vertex along its triangle normal by noise, keeping shared vertices consistent."""
	n = mesh.normals()
	flat = mesh.tris.reshape(-1, 3)
	noise = value_noise_3d(rng, flat, cells=cells)
	nn = np.repeat(n, 3, axis=0)
	mesh.tris = (flat + nn * ((noise - 0.5) * 2 * amount)[:, None]).reshape(-1, 3, 3)
	return mesh


def weld_displace(mesh, rng, amount, cells=3.0):
	"""Displace by position-hashed noise along the *averaged* normal of shared vertices (no cracks)."""
	flat = mesh.tris.reshape(-1, 3)
	key = np.round(flat / 1e-5).astype(np.int64)
	uniq, inv = np.unique(key, axis=0, return_inverse=True)
	n = np.repeat(mesh.normals(), 3, axis=0)
	avg = np.zeros((len(uniq), 3))
	np.add.at(avg, inv, n)
	l = np.linalg.norm(avg, axis=1)
	l[l == 0] = 1
	avg /= l[:, None]
	upos = np.zeros((len(uniq), 3))
	np.add.at(upos, inv, flat)
	cnt = np.zeros(len(uniq))
	np.add.at(cnt, inv, 1)
	upos /= cnt[:, None]
	noise = value_noise_3d(rng, upos, cells=cells)
	d = avg * ((noise - 0.5) * 2 * amount)[:, None]
	mesh.tris = (flat + d[inv]).reshape(-1, 3, 3)
	return mesh
