"""Shared helpers for the item families added 2026-11-03 (nature, fish, metal tiers, materials, heads):
a per-module registry, the two frames, a lofted body along X, broad leaves, and the metal tier
palettes every weapon / tool / armour builder takes.

Everything is metres, Z-up, like mesh.py. Markings are painted per triangle (``paint``), never into
the texture: assign_uvs gives each triangle a random patch of its cell, so texture patterns do not
line up across facets."""
import math
import numpy as np

from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, lathe, fan, from_quads_grid, prism, box


def registry():
	items = []

	def item(name, category, surface="default", roughness=0.9, specular=1.0):
		def deco(fn):
			items.append(dict(name=name, category=category, build=fn, surface=surface, roughness=roughness, specular=specular))
			return fn
		return deco
	return items, item


def ground(m):
	lo, hi = m.bounds()
	return m.translate(-(lo[0] + hi[0]) / 2, -(lo[1] + hi[1]) / 2, -lo[2])


def butt_at_origin(m):
	lo, hi = m.bounds()
	return m.translate(-lo[0], -(lo[1] + hi[1]) / 2, -(lo[2] + hi[2]) / 2)


def paint(m, mat, where):
	"""Set mat on triangles where where(centroids, normals) is true (only triangles of the given mesh)."""
	c = m.centroids()
	n = m.normals()
	m.mat[where(c, n)] = mat
	return m


def squash(m, fn):
	"""Apply fn(points (n,3)) -> points to every vertex."""
	pts = m.tris.reshape(-1, 3)
	m.tris = fn(pts.copy()).reshape(-1, 3, 3)
	return m


def loft(rng, length, ry, rz, cz=None, rings=14, segs=10, mat=0, jitter=0.06, cap=True, x0=0.0, rings_t=None):
	"""Closed body along +X from x0..x0+length: at t in 0..1 an ellipse of half-widths ry(t) (Y) and rz(t) (Z)
	centred at z = cz(t). The ring points get a little radial jitter so facets vary in size."""
	ts = np.linspace(0, 1, rings + 1) if rings_t is None else np.asarray(rings_t, float)
	grid = np.zeros((len(ts), segs, 3))
	phase = rng.random() * 2 * math.pi
	for i, t in enumerate(ts):
		a_y, a_z = max(ry(t), 1e-4), max(rz(t), 1e-4)
		zc = cz(t) if cz else 0.0
		for j in range(segs):
			a = phase + 2 * math.pi * j / segs + (rng.random() - 0.5) * (2 * math.pi / segs) * 0.3
			k = 1 + (rng.random() - 0.5) * 2 * jitter
			grid[i, j] = (x0 + t * length, a_y * k * math.cos(a), zc + a_z * k * math.sin(a))
	m = from_quads_grid(grid, closed_v=True, mat=mat).flip()
	if cap:
		m.add(fan(grid[0][::-1], mat=mat))
		m.add(fan(grid[-1], mat=mat))
	return m


def broad_leaf(rng, length, width, mat=0, rib_mat=None, segments=6, cols=5, bend=0.12, droop=0.25, tip=0.15):
	"""A broad flat leaf along +Y (stem at y = 0) with a raised midrib, its edges drooping: more columns
	than mesh.leaf so the blade facets vary. Double sided."""
	rows = []
	for i, t in enumerate(np.linspace(0, 1, segments + 1)):
		w = width * (math.sin(math.pi * (0.06 + 0.94 * t)) ** 0.8) * (1 - tip * t) + 0.002
		zc = bend * length * t * t
		row = []
		for j, s in enumerate(np.linspace(-1, 1, cols)):
			x = s * w / 2 + (rng.random() - 0.5) * w * 0.06
			z = zc - droop * w * s * s * 0.5 + (0.004 if j == cols // 2 else 0.0)
			row.append((x, t * length + (rng.random() - 0.5) * 0.004, z))
		rows.append(row)
	g = from_quads_grid(np.array(rows), mat=mat)
	g.add(g.copy().flip())
	if rib_mat is not None:
		path = np.array([(0, t * length * 0.92, bend * length * t * t + 0.004) for t in np.linspace(0, 1, 6)])
		g.add(path_tube(rng, path, radius=max(width * 0.035, 0.002), segs=4, jitter=0.05, radius_fn=lambda t: 1.2 - 0.9 * t, mat=rib_mat))
	return g


def subdivide(m, times=1):
	"""Split every triangle into four (midpoints), keeping mats - so big flat faces (a pelt's top, an
	ingot's sides) get enough facets for weld_displace and painted markings."""
	for _ in range(times):
		t = m.tris
		a, b, c = t[:, 0], t[:, 1], t[:, 2]
		ab, bc, ca = (a + b) / 2, (b + c) / 2, (c + a) / 2
		m.tris = np.concatenate([np.stack([a, ab, ca], 1), np.stack([ab, b, bc], 1), np.stack([ca, bc, c], 1), np.stack([ab, bc, ca], 1)])
		m.mat = np.tile(m.mat, 4)
	return m


def ring_path(radius, n=12, z=0.0, axis="z"):
	pts = []
	for t in np.linspace(0, 2 * math.pi, n + 1):
		if axis == "z":
			pts.append((radius * math.cos(t), radius * math.sin(t), z))
		elif axis == "x":
			pts.append((z, radius * math.cos(t), radius * math.sin(t)))
		else:
			pts.append((radius * math.cos(t), z, radius * math.sin(t)))
	return np.array(pts)


def torus(rng, R, r, n=12, segs=5, mat=0, axis="z"):
	return path_tube(rng, ring_path(R, n=n, axis=axis), radius=r, segs=segs, jitter=0.08, cap=False, mat=mat)


# ---------------------------------------------------------------- tier palettes (CLAUDE.md: low contrast, 3-4 tones)


def _c(base, d=12, **kw):
	b = np.array(base, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.06), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


class Tier:
	"""Cells for one metal tier: metal (main), edge (bright bevel), dark (fittings), accent (glow / rivets),
	grip and wood. Builders reference them by index from Tier.cells: 0 metal, 1 edge, 2 dark, 3 accent,
	4 grip, 5 wood, 6 patch (scrap's second plate colour; the others reuse metal)."""

	def __init__(self, name, metal, edge, dark, accent, grip, wood, patch=None, glow=False, rough=0.55):
		self.name = name
		self.glow = glow
		self.rough = rough
		self.cells = [_c(metal, streak="x", streak_amt=0.1), _c(edge, 8), _c(dark, 8), Cell(accent, noise=0.04), _c(grip, 10, streak="x", streak_amt=0.12),
					  _c(wood, 10, streak="x", streak_amt=0.16), _c(patch if patch is not None else metal, 10, blotch=tuple(np.array(patch if patch is not None else metal) * 0.85), blotch_amt=0.4)]


TIERS = {
	# rusty patched: brown-orange rust over grey, a second plate tone for the patches, rope grip
	"scrap": Tier("scrap", metal=(128, 96, 72), edge=(150, 142, 132), dark=(84, 66, 52), accent=(164, 150, 130), grip=(150, 118, 74),
				  wood=(110, 82, 56), patch=(112, 108, 102), rough=0.85),
	"iron": Tier("iron", metal=(86, 88, 92), edge=(132, 134, 138), dark=(54, 54, 58), accent=(110, 112, 116), grip=(92, 58, 38),
				 wood=(118, 84, 54), rough=0.6),
	"steel": Tier("steel", metal=(170, 174, 180), edge=(214, 218, 222), dark=(96, 98, 104), accent=(196, 200, 206), grip=(64, 46, 36),
				  wood=(96, 70, 48), rough=0.4),
	"titanium": Tier("titanium", metal=(150, 166, 182), edge=(196, 208, 220), dark=(96, 108, 122), accent=(176, 190, 206), grip=(58, 62, 70),
					 wood=(80, 84, 92), rough=0.45),
	# dark metal with teal glow lines
	"arcsteel": Tier("arcsteel", metal=(52, 56, 64), edge=(104, 112, 122), dark=(32, 34, 40), accent=(70, 230, 210), grip=(40, 42, 48),
					 wood=(60, 62, 68), glow=True, rough=0.45),
	# industrial housing with a bright cell
	"plasma": Tier("plasma", metal=(72, 76, 84), edge=(150, 156, 164), dark=(40, 42, 48), accent=(255, 120, 210), grip=(36, 36, 40),
				   wood=(196, 150, 40), glow=True, rough=0.5),
	# the existing reforged look (materials/weapons/axe_reforged.png): blue-grey steel, brass rivets, weathered wood
	"reforged": Tier("reforged", metal=(84, 94, 116), edge=(176, 184, 196), dark=(56, 62, 78), accent=(170, 140, 80), grip=(104, 72, 48),
					 wood=(128, 116, 96), rough=0.5),
}
