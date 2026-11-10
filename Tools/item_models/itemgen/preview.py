"""Tiny software rasteriser so every generated model can be looked at (textured, flat lit) without an
engine: orthographic 3/4 view, z-buffer, nearest texture sampling - the same look pixel_lit gives."""
import math
import numpy as np
from PIL import Image, ImageDraw

from .mesh import rot_matrix


def render(mesh, tex_img, size=256, azimuth=35.0, elevation=28.0, bg=(58, 58, 62), sun=(-0.4, -0.6, 0.7),
		   ambient=0.42, margin=0.1, flat_plane=False, cull=1):
	tex = np.asarray(tex_img.convert("RGB"), float)
	th, tw = tex.shape[:2]
	# camera sits in front (-y) and above: tilting the world by +elevation about x brings high points
	# nearer (smaller view y), so the z-buffer sees the top faces first
	R = rot_matrix("x", elevation) @ rot_matrix("z", -azimuth)
	pts = mesh.tris @ R.T  # view space: x right, y depth (into screen), z up
	# we look along +y; screen x = x, screen y = -z ; depth = y
	lo = pts.reshape(-1, 3).min(0)
	hi = pts.reshape(-1, 3).max(0)
	ext = max(hi[0] - lo[0], hi[2] - lo[2], 1e-6)
	s = size * (1 - 2 * margin) / ext
	cx = (lo[0] + hi[0]) / 2
	cz = (lo[2] + hi[2]) / 2
	sx = (pts[..., 0] - cx) * s + size / 2
	sy = (cz - pts[..., 2]) * s + size / 2
	depth = pts[..., 1]
	img = np.zeros((size, size, 3), float)
	img[...] = bg
	zbuf = np.full((size, size), np.inf)
	n = mesh.normals()
	sunv = np.array(sun, float)
	sunv /= np.linalg.norm(sunv)
	light = ambient + (1 - ambient) * np.clip(n @ sunv, 0, 1)
	# soft darkening for downward faces
	light *= 0.85 + 0.15 * np.clip(n[:, 2] * 0.5 + 0.5, 0, 1)
	order = np.argsort(-depth.mean(1))
	for i in order:
		x = sx[i]
		y = sy[i]
		x0, x1 = int(max(np.floor(x.min()), 0)), int(min(np.ceil(x.max()), size - 1))
		y0, y1 = int(max(np.floor(y.min()), 0)), int(min(np.ceil(y.max()), size - 1))
		if x1 < x0 or y1 < y0:
			continue
		# back-face: skip triangles facing away (screen-space winding)
		area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
		if abs(area) < 1e-9 or (cull and area * cull > 0):
			continue
		yy, xx = np.mgrid[y0:y1 + 1, x0:x1 + 1]
		px = xx + 0.5
		py = yy + 0.5
		w0 = ((x[1] - px) * (y[2] - py) - (x[2] - px) * (y[1] - py)) / area
		w1 = ((x[2] - px) * (y[0] - py) - (x[0] - px) * (y[2] - py)) / area
		w2 = 1 - w0 - w1
		inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
		if not inside.any():
			continue
		d = w0 * depth[i, 0] + w1 * depth[i, 1] + w2 * depth[i, 2]
		zb = zbuf[y0:y1 + 1, x0:x1 + 1]
		mask = inside & (d < zb)
		if not mask.any():
			continue
		uv = mesh.uv[i]
		u = w0 * uv[0, 0] + w1 * uv[1, 0] + w2 * uv[2, 0]
		v = w0 * uv[0, 1] + w1 * uv[1, 1] + w2 * uv[2, 1]
		tx = np.clip((u * tw).astype(int), 0, tw - 1)
		ty = np.clip(((1 - v) * th).astype(int), 0, th - 1)
		col = tex[ty, tx] * light[i]
		# a light wireframe-free facet edge: slightly darken the triangle edges for readability
		sub = img[y0:y1 + 1, x0:x1 + 1]
		sub[mask] = col[mask]
		zb[mask] = d[mask]
	return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def contact_sheet(entries, cols=4, size=256, label_h=18):
	"""entries: [(name, PIL image)] → one sheet."""
	rows = (len(entries) + cols - 1) // cols
	sheet = Image.new("RGB", (cols * size, rows * (size + label_h)), (30, 30, 30))
	d = ImageDraw.Draw(sheet)
	for i, (name, im) in enumerate(entries):
		x = (i % cols) * size
		y = (i // cols) * (size + label_h)
		sheet.paste(im.resize((size, size)), (x, y))
		d.text((x + 4, y + size + 2), name, fill=(230, 230, 230))
	return sheet
