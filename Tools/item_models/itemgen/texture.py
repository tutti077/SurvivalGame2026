"""256 x 256 point-sampled item textures built from a 4 x 4 grid of 64 px material cells.

Every triangle of a Mesh carries a cell index (its ``mat``). assign_uvs projects each triangle flat
onto its cell at a given texel density, so point sampling shows the cell's grain over the facet.
The texture itself is a few tones of value noise, grain streaks and speckles per cell - the
low-contrast, low-res look pixel_lit.shader expects (CLAUDE.md: 256 x 256, no baked lighting).
"""
import numpy as np
from PIL import Image

TEX = 256
CELL = 64
GRID = TEX // CELL


class Cell:
	def __init__(self, color, noise=0.08, tones=None, streak=None, streak_amt=0.0, speckle=None,
				 speckle_density=0.0, stripes=None, stripe_period=0, stripe_amt=0.0, cells=4, meters=0.25,
				 rings=None, ring_amt=0.0, blotch=None, blotch_amt=0.0):
		"""color: base rgb 0-255. noise: +/- fraction of value noise. tones: list of rgb picked by
		noise bands (replaces single color). streak: 'x'|'y' grain direction with streak_amt.
		speckle: rgb dots at speckle_density (0..1). stripes: rgb at stripe_period px along streak.
		meters: how many metres one 64 px cell spans when projected (texel density)."""
		self.color = np.array(color, float)
		self.noise = noise
		self.tones = [np.array(t, float) for t in tones] if tones else None
		self.streak = streak
		self.streak_amt = streak_amt
		self.speckle = np.array(speckle, float) if speckle is not None else None
		self.speckle_density = speckle_density
		self.stripes = np.array(stripes, float) if stripes is not None else None
		self.stripe_period = stripe_period
		self.stripe_amt = stripe_amt
		self.cells = cells
		self.meters = meters
		self.rings = np.array(rings, float) if rings is not None else None
		self.ring_amt = ring_amt
		self.blotch = np.array(blotch, float) if blotch is not None else None
		self.blotch_amt = blotch_amt


def value_noise_2d(rng, h, w, cells):
	grid = rng.random((cells + 1, cells + 1))
	ys = np.linspace(0, cells, h, endpoint=False)
	xs = np.linspace(0, cells, w, endpoint=False)
	y0 = np.floor(ys).astype(int)
	x0 = np.floor(xs).astype(int)
	fy = (ys - y0)[:, None]
	fx = (xs - x0)[None, :]
	fy = fy * fy * (3 - 2 * fy)
	fx = fx * fx * (3 - 2 * fx)
	a = grid[y0][:, x0]
	b = grid[y0][:, x0 + 1]
	c = grid[y0 + 1][:, x0]
	d = grid[y0 + 1][:, x0 + 1]
	return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def render_cell(rng, cell):
	n = value_noise_2d(rng, CELL, CELL, cell.cells)
	fine = value_noise_2d(rng, CELL, CELL, cell.cells * 4)
	n = 0.7 * n + 0.3 * fine
	if cell.tones:
		k = len(cell.tones)
		idx = np.clip((n * k).astype(int), 0, k - 1)
		img = np.stack(cell.tones)[idx]
		img = img * (1 + (fine - 0.5) * cell.noise)[..., None]
	else:
		img = cell.color[None, None, :] * (1 + (n - 0.5) * 2 * cell.noise)[..., None]
	if cell.streak and cell.streak_amt:
		# grain lines run along the streak axis: brightness varies across it only
		s = np.repeat(value_noise_2d(rng, CELL, 1, 12), CELL, axis=1)  # varies by row, constant along x
		if cell.streak == "y":
			s = s.T
		img = img * (1 + (s - 0.5) * 2 * cell.streak_amt)[..., None]
	if cell.stripes is not None and cell.stripe_period:
		yy, xx = np.mgrid[0:CELL, 0:CELL]
		coord = xx if cell.streak == "x" else yy
		wob = (value_noise_2d(rng, CELL, CELL, 3) - 0.5) * 4
		mask = ((coord + wob) % cell.stripe_period) < max(1, cell.stripe_period // 3)
		img = np.where(mask[..., None], img * (1 - cell.stripe_amt) + cell.stripes * cell.stripe_amt, img)
	if cell.rings is not None and cell.ring_amt:
		yy, xx = np.mgrid[0:CELL, 0:CELL]
		r = np.sqrt((xx - CELL / 2) ** 2 + (yy - CELL / 2) ** 2)
		wob = (value_noise_2d(rng, CELL, CELL, 3) - 0.5) * 3
		mask = ((r + wob) % 7) < 2
		img = np.where(mask[..., None], img * (1 - cell.ring_amt) + cell.rings * cell.ring_amt, img)
	if cell.blotch is not None and cell.blotch_amt:
		b = value_noise_2d(rng, CELL, CELL, 3)
		mask = b > 0.62
		img = np.where(mask[..., None], img * (1 - cell.blotch_amt) + cell.blotch * cell.blotch_amt, img)
	if cell.speckle is not None and cell.speckle_density:
		m = rng.random((CELL, CELL)) < cell.speckle_density
		img = np.where(m[..., None], cell.speckle, img)
	return np.clip(img, 0, 255)


def render_texture(rng, cells):
	img = np.zeros((TEX, TEX, 3), float)
	img[...] = 40
	for i, cell in enumerate(cells[:GRID * GRID]):
		r, c = divmod(i, GRID)
		img[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = render_cell(rng, cell)
	return Image.fromarray(img.astype(np.uint8), "RGB")


def assign_uvs(mesh, cells, rng):
	"""Per-triangle planar projection into the triangle's material cell. Image row 0 = v 1 (OBJ v is
	up), so the cell at grid row r spans v in [1 - (r+1)/GRID, 1 - r/GRID]."""
	n = mesh.normals()
	pad = 1.5 / TEX
	uv = np.zeros((len(mesh.tris), 3, 2))
	for i in range(len(mesh.tris)):
		cell = cells[mesh.mat[i] % len(cells)]
		r, c = divmod(int(mesh.mat[i]) % (GRID * GRID), GRID)
		u0, u1 = c / GRID + pad, (c + 1) / GRID - pad
		v0, v1 = 1 - (r + 1) / GRID + pad, 1 - r / GRID - pad
		nn = n[i]
		ax = int(np.argmax(np.abs(nn)))
		if ax == 2:
			e1, e2 = np.array([1.0, 0, 0]), np.array([0, 1.0, 0])
		elif ax == 1:
			e1, e2 = np.array([1.0, 0, 0]), np.array([0, 0, 1.0])
		else:
			e1, e2 = np.array([0, 1.0, 0]), np.array([0, 0, 1.0])
		p = mesh.tris[i]
		cen = p.mean(0)
		q = np.column_stack([(p - cen) @ e1, (p - cen) @ e2]) / cell.meters / GRID  # fraction of the texture
		# shrink if the facet is larger than the cell
		ext = max(np.abs(q).max() * 2, 1e-9)
		room = (u1 - u0) * 0.96
		if ext > room:
			q *= room / ext
		span_u = (u1 - u0) - (q[:, 0].max() - q[:, 0].min())
		span_v = (v1 - v0) - (q[:, 1].max() - q[:, 1].min())
		ou = u0 - q[:, 0].min() + rng.random() * max(span_u, 0)
		ov = v0 - q[:, 1].min() + rng.random() * max(span_v, 0)
		uv[i] = q + (ou, ov)
	mesh.uv = uv
	return mesh
