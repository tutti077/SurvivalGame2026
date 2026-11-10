"""Stasis pod the players climb out of when the game starts (Mark 2026-11-08: "kinda like a cryochamber from
Futurama ... little window showing the player's head and the top of their torso, blinking lights on the side,
sleek shiny metal exterior, padded interior with a diamond-stitched chair, tubes that were connected to the
player's body like when Neo wakes up").

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_stasis_pod.py

Writes Assets/models/props/stasis_pod/:
    stasis_pod.fbx / .vmdl          the upright pod: plinth, hollow C-shell (open at the front), roof cap, padded
                                    interior (back pad, headrest, seat, armrests), dangling body tubes, side lamp
                                    panels, rear conduit. `stasis_pod_phys` = coarse hollow shell for a concave
                                    PhysicsMeshFile, so the pawn can stand inside and walk out.
    stasis_pod_door.fbx / .vmdl     the door leaf with the window hole + a glass pane (shaders/glass.shader) and a
                                    handle. Origin = the hinge line (left jamb, +Y) so the prefab yaws it open.
    stasis_pod_lamp.fbx / .vmdl     one chamfered lamp dome. The prefab places eight (4 a side); StasisPod blinks
                                    them by swapping the material override between stasis_pod_lamp_*.vmat.
    stasis_pod_shell.png/.vmat      256 px brushed-steel tile (PixelRoughness 0.3 = the shiny exterior)
    stasis_pod_pad.png/.vmat        256 px diamond-quilt leather tile (12.5 cm diamonds at 1 UV / m)
    stasis_pod_trim.png/.vmat       256 px atlas: tube rib band + 32 px cells (chrome, dark trim, brass cuffs, grate,
                                    screen, side panel, seal, warning stripe, nameplate)
    stasis_pod_glass.png/.vmat      window glass (glass.shader, simple quality, translucency map)
    stasis_pod_lamp.png/.vmat (off) + stasis_pod_lamp_{cyan,green,amber,red}.png/.vmat   flat 8 px lamp colours
and Assets/prefabs/props/stasis_pod.prefab (root renderer + static mesh collider + Survival.StasisPod, Door child at
the hinge, Lamp_* children on the side panels, an InteriorLight point light). Blender metres, exported 1:1, the
vmdls use import_scale 0.4 (40 u/m, CLAUDE.md commandment 5). Modelled +X = the door = engine +X (same export yaw as
create_hover_platform.py), origin on the floor under the pod centre.

Cross-sections are superellipses (n = 4 rounded rectangles) tapered along Z: narrow at the feet, widest at the
shoulders (z 1.45), narrowing again toward the head, so the silhouette reads as a sarcophagus rather than a box or
a cylinder. Rings are sampled at 30 angles: 12 across the door arc (|theta| <= 45 deg, 7.5 deg steps) and 18 around
the body (15 deg steps). A shell / door / pad is a "thick grid": outer surface, inner surface offset along the ring
normal, and side walls wherever a grid cell has no neighbour (edges and the window hole) - one builder for all.

Previews (Workbench, textured, flat) land in Blender/blenderprojects/stasis_pod_preview/.
"""
import bpy, math, os, struct, zlib, uuid, json
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
OUT_DIR = os.path.join(ROOT, "Assets", "models", "props", "stasis_pod")
PREFAB_DIR = os.path.join(ROOT, "Assets", "prefabs", "props")
PREVIEW_DIR = os.path.join(ROOT, "Blender", "blenderprojects", "stasis_pod_preview")
ASSET_REL = "models/props/stasis_pod"
POD = "stasis_pod"
DOOR = "stasis_pod_door"
GLASS = "stasis_pod_glass"
LAMP = "stasis_pod_lamp"
UNITS_PER_M = 40.0

# ---------------------------------------------------------------- dimensions (metres, Blender frame: +X door, +Y left, +Z up)
PLINTH_H = 0.14
SHELL_Z0 = PLINTH_H
SHELL_Z1 = 2.30
CAP_Z1 = 2.54
SHELL_T = 0.06
DOOR_T = 0.07
PAD_T = 0.07
SUPER_N = 4.0
# z -> (half depth a, half width b): the taper rows (linear between them). Sized so the pawn's capsule (BodyRadius 16 u
# = 0.40 m, BodyHeight 72 u) stands clear of every rigid interior solid at the occupant anchor (PAWN_ANCHOR_X): back pad
# face at about -0.50, seat nose at -0.30, door inner face at +0.46 or more, so the pawn (x -0.25 .. +0.55) touches nothing.
PROFILE = [(0.14, 0.57, 0.50), (0.60, 0.60, 0.56), (1.00, 0.62, 0.60), (1.45, 0.63, 0.62), (1.90, 0.60, 0.58), (2.30, 0.54, 0.50)]
PAWN_ANCHOR_X = 0.15      # where the occupant stands (pod local, metres) - also the release point
SEAT_DEPTH = 0.20
SCREEN = "stasis_pod_screen"
PUFF = "stasis_pod_puff"
SCREEN_Z = 1.62
DOOR_HALF_DEG = 45.0
DOOR_SEGS = 12
BODY_STEP_DEG = 15.0
SHELL_ROWS = [0.14, 0.45, 0.80, 1.15, 1.40, 1.72, 2.05, 2.30]
WINDOW_Z = (1.40, 2.05)          # row indices 4..6 of SHELL_ROWS
WINDOW_COLS = (3, 9)             # door grid columns (12 segments: 0..12) -> theta -22.5 .. +22.5 deg
LAMP_Z = [1.22, 1.40, 1.58, 1.76]
LAMP_X = -0.10
PANEL_Z = (1.05, 1.95)
PANEL_X = (-0.26, 0.20)

# ---------------------------------------------------------------- textures
TEX = 256
CELL = 32
# trim atlas: rows 0-1 (y 0..63) = tube rib band (tiles along U); cells from row 2 down. name -> (col, row, size cells)
CELLS = {
	"steel_dark": (0, 2, 1), "chrome": (1, 2, 1), "trim_dark": (2, 2, 1), "cuff": (3, 2, 1),
	"port": (4, 2, 1), "seal": (5, 2, 1), "panel": (6, 2, 1), "warning": (7, 2, 1),
	"grate": (0, 3, 1), "label": (1, 3, 1), "rubber": (2, 3, 1), "brass_dark": (3, 3, 1),
	"screen": (4, 3, 2),            # 2 x 2 cells (64 px)
	"vent": (6, 3, 1), "red_dark": (7, 3, 1),
}
RIB_BAND = (0, 64)   # y range of the rib band in the trim atlas


def write_png(path, rgba):
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)

	png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
	png += chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
	with open(path, "wb") as f:
		f.write(png)


def rgba(rgb_img):
	img = np.zeros((rgb_img.shape[0], rgb_img.shape[1], 4), np.uint8)
	img[..., :3] = np.clip(rgb_img, 0, 255).astype(np.uint8)
	img[..., 3] = 255
	return img


def smooth_rows(noise, k):
	out = noise.copy()
	for _ in range(k):
		out = (np.roll(out, 1) + out + np.roll(out, -1)) / 3.0
	return out


def make_shell():
	"""Brushed steel, vertical brush along U (= height on the pod), a panel seam cross with rivets."""
	rng = np.random.default_rng(7)
	base = np.array((152, 158, 168), float)
	img = np.repeat(np.repeat(base[None, None, :], TEX, 0), TEX, 1)
	# brush streaks: per-row brightness (rows = V = around the pod; U runs along x) - streaks run along x
	streak = smooth_rows(rng.normal(0, 9, TEX), 2)
	img += streak[:, None, None]
	img += rng.integers(-3, 4, (TEX, TEX, 1))
	# panel seams: a horizontal seam at y 128 and vertical seams at x 64 / 192 - dark groove, light lip below / right
	for y in (127, 128):
		img[y, :] = base - 48
	img[129, :] = base + 18
	for x in (63, 64, 191, 192):
		img[:, x] = base - 44
	img[:, 65] = base + 14
	img[:, 193] = base + 14
	# flush rivets along the seams every 32 px
	for x in range(16, TEX, 32):
		for y in (122, 134):
			img[y:y + 2, x:x + 2] = base - 58
			img[y - 1, x:x + 2] = base + 22
	for y in range(16, TEX, 32):
		for x in (58, 70, 186, 198):
			img[y:y + 2, x:x + 2] = base - 58
			img[y - 1, x:x + 2] = base + 22
	return rgba(img)


def make_pad():
	"""Diamond-quilted leather: 32 px diamond lattice, pale thread, puffed shading toward each diamond centre."""
	rng = np.random.default_rng(3)
	base = np.array((48, 54, 70), float)
	img = np.repeat(np.repeat(base[None, None, :], TEX, 0), TEX, 1)
	ys, xs = np.mgrid[0:TEX, 0:TEX]
	s = (xs + ys) % 32
	d = (xs - ys) % 32
	# distance to the nearest stitch line in the two diagonal families -> puff
	ds = np.minimum(s, 32 - s)
	dd = np.minimum(d, 32 - d)
	puff = np.minimum(ds, dd) / 16.0          # 0 at a stitch, 1 at the centre
	img += (puff[..., None] * 26.0 - 8.0)
	img += rng.integers(-3, 4, (TEX, TEX, 1))
	stitch = (s <= 1) | (d <= 1)
	img[stitch] = (128, 124, 112)
	# thread dashes: break the stitch line every few texels so it reads as sewing, not a wire
	dash = ((xs + 2 * ys) // 3) % 3 == 0
	img[stitch & dash] = (92, 90, 84)
	# a dimple button where the lines cross
	cross = (s <= 1) & (d <= 1)
	img[cross] = (150, 146, 134)
	return rgba(img)


def make_trim():
	rng = np.random.default_rng(11)
	img = np.zeros((TEX, TEX, 3), float)
	img[:] = (40, 42, 46)
	# rib band: dark rubber with a raised ring every 16 px along x (U), the ring 5 px wide, a 1 px highlight
	y0, y1 = RIB_BAND
	band = np.repeat(np.repeat(np.array((38, 40, 44), float)[None, None, :], y1 - y0, 0), TEX, 1)
	band += rng.integers(-3, 4, (y1 - y0, TEX, 1))
	for x in range(0, TEX, 16):
		band[:, x:x + 5] = (66, 70, 78)
		band[:, x + 1] = (88, 92, 100)
		band[:, x + 5] = (24, 26, 30)
	img[y0:y1] = band

	def cell_block(name, fill):
		cx, cy, n = CELLS[name]
		size = CELL * n
		blk = np.repeat(np.repeat(np.array(fill, float)[None, None, :], size, 0), size, 1)
		blk += rng.integers(-3, 4, (size, size, 1))
		return blk

	def put(name, blk):
		cx, cy, n = CELLS[name]
		size = CELL * n
		img[cy * CELL:cy * CELL + size, cx * CELL:cx * CELL + size] = blk

	put("steel_dark", cell_block("steel_dark", (84, 90, 100)))
	chrome = cell_block("chrome", (200, 206, 216))
	chrome[10:14, :] = (236, 240, 246)
	chrome[20:23, :] = (150, 156, 168)
	put("chrome", chrome)
	put("trim_dark", cell_block("trim_dark", (30, 32, 37)))
	cuff = cell_block("cuff", (186, 148, 82))
	cuff[8:10, :] = (222, 190, 120)
	cuff[22:24, :] = (120, 92, 46)
	put("cuff", cuff)
	port = cell_block("port", (34, 36, 40))
	yy, xx = np.mgrid[0:CELL, 0:CELL]
	r = np.hypot(xx - 15.5, yy - 15.5)
	port[(r > 10) & (r <= 13)] = (170, 176, 186)
	port[(r > 5) & (r <= 7)] = (96, 100, 108)
	put("port", port)
	put("seal", cell_block("seal", (16, 16, 18)))
	panel = cell_block("panel", (54, 60, 72))
	panel[2:4, :] = (74, 80, 94)
	put("panel", panel)
	warn = cell_block("warning", (232, 178, 40))
	stripes = ((xx + yy) // 6) % 2 == 1
	warn[stripes] = (26, 26, 28)
	put("warning", warn)
	grate = cell_block("grate", (58, 62, 68))
	grate[::8, :] = (96, 102, 110)
	grate[:, ::8] = (96, 102, 110)
	grate[1::8, :] = (30, 32, 36)
	grate[:, 1::8] = (30, 32, 36)
	put("grate", grate)
	label = cell_block("label", (168, 172, 178))
	label[6:8, 4:20] = (40, 42, 48)
	label[11:13, 4:26] = (40, 42, 48)
	label[16:18, 4:14] = (40, 42, 48)
	label[22:26, 4:28] = (190, 60, 50)
	label[23:25, 6:26] = (230, 230, 232)
	put("label", label)
	put("rubber", cell_block("rubber", (44, 46, 50)))
	put("brass_dark", cell_block("brass_dark", (120, 94, 50)))
	screen = cell_block("screen", (18, 44, 54))
	screen[4:6, 4:60] = (70, 220, 240)
	screen[10:12, 4:40] = (70, 220, 240)
	screen[16:18, 4:52] = (70, 220, 240)
	for i, h in enumerate((14, 22, 9, 26, 18, 12, 24)):
		x = 6 + i * 8
		screen[60 - h:60, x:x + 5] = (90, 235, 200)
	screen[28:30, 4:60] = (40, 110, 130)
	screen[36:38, 4:28] = (240, 180, 60)
	put("screen", screen)
	vent = cell_block("vent", (46, 50, 56))
	vent[::6, :] = (20, 22, 26)
	vent[1::6, :] = (20, 22, 26)
	put("vent", vent)
	put("red_dark", cell_block("red_dark", (110, 30, 30)))
	return rgba(img)


def make_glass():
	img = np.zeros((16, 16, 4), np.uint8)
	img[..., :3] = (196, 226, 250)
	for i in range(16):
		x = (i + 5) % 16
		img[i, x, :3] = (236, 246, 255)
	img[..., 3] = 255
	trans = np.zeros((16, 16, 4), np.uint8)
	trans[..., :3] = 72
	trans[..., 3] = 255
	return img, trans


LAMP_COLORS = {"off": (34, 36, 40), "cyan": (70, 230, 255), "green": (90, 240, 120), "amber": (255, 176, 48), "red": (255, 70, 58)}


def make_screen(lit):
	"""64 x 48 readout: the trim atlas's screen cell (lit) or the same panel gone dark with a faint frame (off)."""
	rng = np.random.default_rng(13)
	img = np.zeros((48, 64, 4), np.uint8)
	img[..., 3] = 255
	if not lit:
		img[..., :3] = (14, 18, 22)
		img[0, :, :3] = img[-1, :, :3] = (30, 34, 40)
		img[:, 0, :3] = img[:, -1, :3] = (30, 34, 40)
		return img
	panel = np.repeat(np.repeat(np.array((18, 44, 54), float)[None, None, :], 48, 0), 64, 1)
	panel += rng.integers(-3, 4, (48, 64, 1))
	panel[3:5, 4:60] = (70, 220, 240)
	panel[8:10, 4:40] = (70, 220, 240)
	panel[13:15, 4:52] = (70, 220, 240)
	for i, h in enumerate((10, 16, 7, 19, 13, 9, 17)):
		x = 6 + i * 8
		panel[44 - h:44, x:x + 5] = (90, 235, 200)
	panel[22:24, 4:60] = (40, 110, 130)
	panel[27:29, 4:28] = (240, 180, 60)
	img[..., :3] = np.clip(panel, 0, 255).astype(np.uint8)
	return img


def make_lamp(rgb):
	img = np.zeros((8, 8, 4), np.uint8)
	img[..., :3] = rgb
	img[..., 3] = 255
	# a rim one shade darker so the dome's base ring reads
	img[0, :, :3] = np.clip(np.array(rgb) * 0.6, 0, 255).astype(np.uint8)
	return img


# ---------------------------------------------------------------- mesh builder
class Mesh:
	"""Faceted polygon soup: every face has its own vertices (no sharing - everything is flat shaded anyway)."""

	def __init__(self, materials):
		self.verts = []
		self.faces = []
		self.uvs = []
		self.mats = []
		self.mat_index = {m: i for i, m in enumerate(materials)}

	def face(self, pts, uvs, mat):
		if len(pts) < 3:
			return
		base = len(self.verts)
		self.verts.extend(Vector(p) for p in pts)
		self.faces.append(tuple(range(base, base + len(pts))))
		self.uvs.append([tuple(u) for u in uvs])
		# a cell name stands for the trim atlas material (every cell lives on stasis_pod_trim.png)
		mi = self.mat_index.get(mat)
		if mi is None and mat in CELLS:
			mi = self.mat_index["stasis_pod_trim"]
		if mi is None:
			raise KeyError(f"unknown material / cell '{mat}'")
		self.mats.append(mi)

	def quad(self, a, b, c, d, uvs, mat):
		self.face((a, b, c, d), uvs, mat)

	def to_object(self, name, materials):
		me = bpy.data.meshes.new(name)
		me.from_pydata([v[:] for v in self.verts], [], self.faces)
		me.update()
		uv = me.uv_layers.new(name="UVMap")
		for poly, face_uvs, mi in zip(me.polygons, self.uvs, self.mats):
			poly.use_smooth = False
			poly.material_index = mi
			for li, u in zip(poly.loop_indices, face_uvs):
				uv.data[li].uv = u
		for m in materials:
			me.materials.append(m)
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def cell_uv(name, fx=0.5, fy=0.5):
	cx, cy, n = CELLS[name]
	size = CELL * n
	u = (cx * CELL + fx * size) / TEX
	v = 1.0 - (cy * CELL + fy * size) / TEX
	return (u, v)


def cell_uvs(name, n=4):
	"""Corner UVs of a cell in quad order (bottom-left, bottom-right, top-right, top-left); an n-gon walks the
	cell's inscribed circle so a round port / disc shows the whole cell."""
	if n == 4:
		return [cell_uv(name, 0, 1), cell_uv(name, 1, 1), cell_uv(name, 1, 0), cell_uv(name, 0, 0)]
	return [cell_uv(name, 0.5 + 0.5 * math.cos(2 * math.pi * k / n), 0.5 - 0.5 * math.sin(2 * math.pi * k / n)) for k in range(n)]


def flat_uvs(name, n):
	return [cell_uv(name) if name in CELLS else (0.5, 0.5)] * n


def rib_uv(along, around):
	"""Tube rib band: U tiles along the tube (1 U = 0.4 m, 16 ribs), V inside the band = around the tube."""
	y0, y1 = RIB_BAND
	return (along / 0.4, 1.0 - (y0 + around * (y1 - y0)) / TEX)


# ---------------------------------------------------------------- profile rings
def profile_at(z):
	rows = PROFILE
	if z <= rows[0][0]:
		return rows[0][1], rows[0][2]
	for (z0, a0, b0), (z1, a1, b1) in zip(rows, rows[1:]):
		if z <= z1:
			t = (z - z0) / (z1 - z0)
			return a0 + (a1 - a0) * t, b0 + (b1 - b0) * t
	return rows[-1][1], rows[-1][2]


def ring_thetas():
	door = [math.radians(-DOOR_HALF_DEG + i * (2 * DOOR_HALF_DEG / DOOR_SEGS)) for i in range(DOOR_SEGS + 1)]
	body = [math.radians(DOOR_HALF_DEG + i * BODY_STEP_DEG) for i in range(int((360 - 2 * DOOR_HALF_DEG) / BODY_STEP_DEG) + 1)]
	return door, body


def super_point(theta, a, b, n=SUPER_N):
	c, s = math.cos(theta), math.sin(theta)
	x = a * math.copysign(abs(c) ** (2.0 / n), c)
	y = b * math.copysign(abs(s) ** (2.0 / n), s)
	return x, y


def super_normal(theta, a, b, n=SUPER_N):
	"""Outward normal of the superellipse (gradient of |x/a|^n + |y/b|^n)."""
	x, y = super_point(theta, a, b, n)
	nx = n * math.copysign(abs(x / a) ** (n - 1), x) / a if abs(x) > 1e-9 else 0.0
	ny = n * math.copysign(abs(y / b) ** (n - 1), y) / b if abs(y) > 1e-9 else 0.0
	l = math.hypot(nx, ny) or 1.0
	return nx / l, ny / l


def ring_point(theta, z, inset=0.0, scale=1.0, dz=0.0):
	a, b = profile_at(z)
	a, b = a * scale, b * scale
	x, y = super_point(theta, a, b)
	nx, ny = super_normal(theta, a, b)
	return Vector((x - nx * inset, y - ny * inset, z + dz))


def arc_length_table(thetas, z):
	"""Cumulative arc length along the ring at height z, for tiling UVs around the pod."""
	pts = [ring_point(t, z) for t in thetas]
	acc = [0.0]
	for p, q in zip(pts, pts[1:]):
		acc.append(acc[-1] + (q - p).length)
	return acc


def thick_grid(mesh, thetas, zs, inset_out, thickness, mat_out, mat_in, mat_wall, holes=(), uv_scale=1.0,
			   uv_out=None, scale_fn=None):
	"""Hollow lofted panel over a theta x z grid. Cells in `holes` (set of (col,row)) are left open and walled.
	uv_out: callable(col,row) -> uv for the outer surface; default tiles height along U and arc length along V."""
	cols, rows = len(thetas), len(zs)
	arc = arc_length_table(thetas, zs[len(zs) // 2])

	def outer(c, r):
		s = scale_fn(zs[r]) if scale_fn else 1.0
		return ring_point(thetas[c], zs[r], inset_out, s)

	def inner(c, r):
		s = scale_fn(zs[r]) if scale_fn else 1.0
		return ring_point(thetas[c], zs[r], inset_out + thickness, s)

	def uv(c, r):
		if uv_out:
			return uv_out(c, r)
		return (zs[r] * uv_scale, arc[c] * uv_scale)

	holes = set(holes)
	for r in range(rows - 1):
		for c in range(cols - 1):
			if (c, r) in holes:
				continue
			o = [outer(c, r), outer(c + 1, r), outer(c + 1, r + 1), outer(c, r + 1)]
			i = [inner(c, r), inner(c + 1, r), inner(c + 1, r + 1), inner(c, r + 1)]
			us = [uv(c, r), uv(c + 1, r), uv(c + 1, r + 1), uv(c, r + 1)]
			mesh.face(o, us, mat_out)
			in_uv = flat_uvs(mat_in, 4) if mat_in in CELLS else list(reversed(us))
			mesh.face(list(reversed(i)), in_uv, mat_in)
			# walls where the neighbour is missing
			wall_uv = flat_uvs(mat_wall, 4) if mat_wall in CELLS else us
			if c == 0 or (c - 1, r) in holes:            # left edge (theta low side)
				mesh.face([o[0], o[3], i[3], i[0]], wall_uv, mat_wall)
			if c == cols - 2 or (c + 1, r) in holes:     # right edge
				mesh.face([o[1], i[1], i[2], o[2]], wall_uv, mat_wall)
			if r == 0 or (c, r - 1) in holes:            # bottom
				mesh.face([o[0], i[0], i[1], o[1]], wall_uv, mat_wall)
			if r == rows - 2 or (c, r + 1) in holes:     # top
				mesh.face([o[3], o[2], i[2], i[3]], wall_uv, mat_wall)


def closed_loft(mesh, thetas, rows, mat, uv_fn, cap_top=None, cap_bottom=None):
	"""rows: list of (z, scale, inset). Closed ring loft (thetas should run a full circle, first == last angle)."""
	rings = [[ring_point(t, z, inset, scale) for t in thetas] for (z, scale, inset) in rows]
	for r in range(len(rings) - 1):
		for c in range(len(thetas) - 1):
			pts = [rings[r][c], rings[r][c + 1], rings[r + 1][c + 1], rings[r + 1][c]]
			mesh.face(pts, uv_fn(c, r, pts), mat)
	if cap_top:
		ring = rings[-1][:-1]
		centre = sum(ring, Vector()) / len(ring)
		for c in range(len(ring)):
			pts = [ring[c], ring[(c + 1) % len(ring)], centre]
			mesh.face(pts, flat_uvs(cap_top, 3), cap_top)
	if cap_bottom:
		ring = rings[0][:-1]
		centre = sum(ring, Vector()) / len(ring)
		for c in range(len(ring)):
			pts = [ring[(c + 1) % len(ring)], ring[c], centre]
			mesh.face(pts, flat_uvs(cap_bottom, 3), cap_bottom)
	return rings


def annulus(mesh, ring_a, ring_b, mat, flip=False):
	"""Quads between two rings of equal length (first == last). Facing up when ring_a is the outer ring."""
	for c in range(len(ring_a) - 1):
		pts = [ring_a[c], ring_a[c + 1], ring_b[c + 1], ring_b[c]]
		if flip:
			pts.reverse()
		mesh.face(pts, flat_uvs(mat, 4), mat)


def tapered_box(mesh, centre, size_bottom, size_top, mat, uv_mode="flat", uv_scale=1.0, rot=None):
	"""A frustum box (soft cushion look when the top is a little smaller). size = (sx, sy, sz)."""
	cx, cy, cz = centre
	bx, by, sz = size_bottom[0] * 0.5, size_bottom[1] * 0.5, size_bottom[2]
	tx, ty = size_top[0] * 0.5, size_top[1] * 0.5
	z0, z1 = cz - sz * 0.5, cz + sz * 0.5
	B = [Vector((-bx, -by, z0)), Vector((bx, -by, z0)), Vector((bx, by, z0)), Vector((-bx, by, z0))]
	T = [Vector((-tx, -ty, z1)), Vector((tx, -ty, z1)), Vector((tx, ty, z1)), Vector((-tx, ty, z1))]
	if rot is not None:
		B = [rot @ (p - Vector((0, 0, cz))) + Vector((0, 0, cz)) for p in B]
		T = [rot @ (p - Vector((0, 0, cz))) + Vector((0, 0, cz)) for p in T]
	B = [p + Vector((cx, cy, 0)) for p in B]
	T = [p + Vector((cx, cy, 0)) for p in T]

	def uvs(pts):
		if uv_mode == "flat":
			return flat_uvs(mat, len(pts))
		if uv_mode == "cell":
			return cell_uvs(mat, len(pts))
		# planar: project on the face's two largest axes, scaled in metres
		n = (pts[1] - pts[0]).cross(pts[2] - pts[0])
		ax = sorted(range(3), key=lambda i: abs(n[i]))[:2]
		return [(p[ax[0]] * uv_scale, p[ax[1]] * uv_scale) for p in pts]

	faces = [
		[T[0], T[1], T[2], T[3]],              # top
		[B[3], B[2], B[1], B[0]],              # bottom
		[B[0], B[1], T[1], T[0]],              # -y
		[B[1], B[2], T[2], T[1]],              # +x
		[B[2], B[3], T[3], T[2]],              # +y
		[B[3], B[0], T[0], T[3]],              # -x
	]
	for f in faces:
		mesh.face(f, uvs(f), mat)


def box(mesh, centre, size, mat, uv_mode="flat", uv_scale=1.0, rot=None):
	tapered_box(mesh, centre, size, size, mat, uv_mode, uv_scale, rot)


def cylinder(mesh, centre, radius, depth, mat, segs=8, axis='Z', radius2=None, uv_mode="flat"):
	r2 = radius if radius2 is None else radius2
	rot = {'Z': Matrix.Identity(3), 'X': Matrix.Rotation(math.radians(90), 3, 'Y'), 'Y': Matrix.Rotation(math.radians(-90), 3, 'X')}[axis]
	c = Vector(centre)
	bot, top = [], []
	for i in range(segs):
		t = 2 * math.pi * i / segs
		bot.append(c + rot @ Vector((radius * math.cos(t), radius * math.sin(t), -depth * 0.5)))
		top.append(c + rot @ Vector((r2 * math.cos(t), r2 * math.sin(t), depth * 0.5)))
	for i in range(segs):
		j = (i + 1) % segs
		pts = [bot[i], bot[j], top[j], top[i]]
		mesh.face(pts, cell_uvs(mat) if uv_mode == "cell" else flat_uvs(mat, 4), mat)
	mesh.face(list(reversed(bot)), flat_uvs(mat, segs), mat)
	mesh.face(top, flat_uvs(mat, segs), mat)


def bezier(p0, p1, p2, n):
	p0, p1, p2 = Vector(p0), Vector(p1), Vector(p2)
	return [(1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t * t * p2 for t in (i / n for i in range(n + 1))]


def path_tube(mesh, pts, radius, mat="rib", segs=8):
	"""Swept tube along a polyline; ribbed UVs tile along the length."""
	rings = []
	along = 0.0
	up = Vector((0, 0, 1))
	for i, p in enumerate(pts):
		d = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
		side = d.cross(up)
		if side.length < 1e-4:
			side = d.cross(Vector((1, 0, 0)))
		side.normalize()
		nrm = side.cross(d).normalized()
		if i > 0:
			along += (p - pts[i - 1]).length
		ring = []
		for k in range(segs):
			t = 2 * math.pi * k / segs
			ring.append((p + (side * math.cos(t) + nrm * math.sin(t)) * radius, along, k / segs))
		rings.append(ring)
	# (side, nrm, d) is left-handed - side x nrm = -d - so the ring runs clockwise seen along d; wind the
	# quads the other way round or every tube renders inside out
	for a, b in zip(rings, rings[1:]):
		for k in range(segs):
			j = (k + 1) % segs
			p0, l0, v0 = a[k]
			p1, l1, v1 = a[j]
			p2, l2, v2 = b[j]
			p3, l3, v3 = b[k]
			v1 = v1 if j else 1.0
			v2 = v2 if j else 1.0
			mesh.face([p0, p3, p2, p1], [rib_uv(l0, v0), rib_uv(l3, v3), rib_uv(l2, v2), rib_uv(l1, v1)], mat)


def tube_frame(d):
	"""Orthonormal (side, nrm) across a tube direction d. Rings built as side*cos + nrm*sin run clockwise seen
	along d (side x nrm = -d), so a ring polygon in that order faces -d."""
	side = d.cross(Vector((0, 0, 1)))
	if side.length < 1e-4:
		side = d.cross(Vector((1, 0, 0)))
	side.normalize()
	nrm = side.cross(d).normalized()
	return side, nrm


def prism(mesh, start, d, radius, length, cell, mat, segs=8, uvs=None):
	"""Closed n-gon prism from `start` along unit `d`; outward side walls, both caps facing out."""
	side, nrm = tube_frame(d)
	a = [start + (side * math.cos(2 * math.pi * k / segs) + nrm * math.sin(2 * math.pi * k / segs)) * radius for k in range(segs)]
	b = [p + d * length for p in a]
	for k in range(segs):
		j = (k + 1) % segs
		mesh.face([a[k], b[k], b[j], a[j]], uvs or flat_uvs(cell, 4), mat)
	mesh.face(a, flat_uvs(cell, segs), mat)                  # start cap faces -d
	mesh.face(list(reversed(b)), flat_uvs(cell, segs), mat)  # end cap faces +d


# ---------------------------------------------------------------- the pod
SHELL_MATS = ["stasis_pod_shell", "stasis_pod_trim", "stasis_pod_pad"]
RIB = "stasis_pod_trim"


def add_interior(mesh, body_t, pad):
	"""Back pad, shoulder wings, headrest, seat + pedestal, armrests. Called for the render mesh AND the physics mesh
	so the pawn cannot push into any of it (Mark 2026-11-09: "the seat is mushing ... make it a hard rigid model")."""
	back_t = [t for t in body_t if math.radians(120) - 1e-6 <= t <= math.radians(240) + 1e-6]
	pad_rows = [0.34, 0.70, 1.05, 1.40, 1.75, 2.12]
	arc = arc_length_table(back_t, 1.45)
	# (pads start 1 cm inside the wall so their hidden back face never shares a plane with the shell's inner face)
	thick_grid(mesh, back_t, pad_rows, SHELL_T - 0.01, PAD_T + 0.01, pad, pad, pad,
			   uv_out=lambda c, r: (pad_rows[r], arc[c]))
	# shoulder wings: shorter pads either side of the back pad
	for wing in ([t for t in body_t if math.radians(90) - 1e-6 <= t <= math.radians(120) + 1e-6],
				 [t for t in body_t if math.radians(240) - 1e-6 <= t <= math.radians(270) + 1e-6]):
		warc = arc_length_table(wing, 1.45)
		wrows = [1.05, 1.40, 1.75, 2.00]
		thick_grid(mesh, wing, wrows, SHELL_T - 0.01, PAD_T * 0.8 + 0.01, pad, pad, pad, uv_out=lambda c, r, warc=warc, wrows=wrows: (wrows[r], warc[c] + 0.37))
	# headrest: a soft wedge at head height (nose 16 cm off the pad)
	a_back = profile_at(1.70)[0] - SHELL_T - PAD_T
	tapered_box(mesh, (-a_back + 0.08, 0.0, 1.70), (0.16, 0.30, 0.26), (0.12, 0.24, 0.20), pad, uv_mode="planar")
	# seat: a perch cantilevered from the back pad on a steel pedestal, nose tapered
	a_seat = profile_at(0.56)[0] - SHELL_T - PAD_T
	tapered_box(mesh, (-a_seat + SEAT_DEPTH * 0.5, 0.0, 0.56), (SEAT_DEPTH, 0.56, 0.13), (SEAT_DEPTH - 0.05, 0.50, 0.13), pad, uv_mode="planar")
	box(mesh, (-a_seat + 0.08, 0.0, (SHELL_Z0 + 0.5) * 0.5 + 0.02), (0.14, 0.30, 0.5 - SHELL_Z0 - 0.02), "steel_dark")
	# armrests: short pads off the side walls (10 cm proud, clear of the pawn's capsule)
	for sy in (1, -1):
		b_arm = profile_at(0.92)[1] - SHELL_T
		tapered_box(mesh, (-0.20, sy * (b_arm - 0.05), 0.92), (0.30, 0.10, 0.08), (0.26, 0.07, 0.08), pad, uv_mode="planar")


def build_pod(mats):
	mesh = Mesh(SHELL_MATS)
	door_t, body_t = ring_thetas()
	full = body_t + door_t[1:]        # 45 .. 315 then -45 .. 45 : closed, first == last angle
	full_closed = body_t + [t + 2 * math.pi for t in door_t[1:]]
	shell = "stasis_pod_shell"
	trim = "stasis_pod_trim"
	pad = "stasis_pod_pad"

	# --- plinth: a wider foot ring, chrome lip, then the annulus the shell stands on
	def plinth_uv(c, r, pts):
		return flat_uvs("steel_dark", 4)
	rings = closed_loft(mesh, full_closed, [(0.0, 1.14, 0.0), (0.05, 1.14, 0.0), (0.10, 1.07, 0.0), (SHELL_Z0, 1.05, 0.0)],
						trim, plinth_uv, cap_bottom="trim_dark")
	# a chrome band on the lip
	for c in range(len(full_closed) - 1):
		pts = [rings[1][c], rings[1][c + 1], rings[2][c + 1], rings[2][c]]
		mesh.face(pts, flat_uvs("chrome", 4), trim)
	shell_base = [ring_point(t, SHELL_Z0) for t in full_closed]
	annulus(mesh, rings[-1], shell_base, "steel_dark")

	# --- hollow C-shell (open across the door arc)
	def shell_uv(c, r):
		arc = arc_length_table(body_t, 1.45)
		return (SHELL_ROWS[r] * 1.0, arc[c] * 1.0)
	thick_grid(mesh, body_t, SHELL_ROWS, 0.0, SHELL_T, shell, "panel", "trim_dark", uv_out=shell_uv)
	# chrome jamb strips on both door edges, a hair proud of the dark jamb wall (toward the opening)
	for theta, sgn in ((body_t[0], 1), (body_t[-1], -1)):
		th = theta - sgn * math.radians(0.3)
		for z0, z1 in zip(SHELL_ROWS, SHELL_ROWS[1:]):
			o0, o1 = ring_point(th, z0, -0.004), ring_point(th, z1, -0.004)
			i0, i1 = ring_point(th, z0, SHELL_T + 0.004), ring_point(th, z1, SHELL_T + 0.004)
			pts = [o0, o1, i1, i0] if sgn > 0 else [o0, i0, i1, o1]
			mesh.face(pts, flat_uvs("chrome", 4), trim)

	# --- roof cap: lip, dome, flat top with a vent cell
	def cap_uv(c, r, pts):
		return flat_uvs("steel_dark" if r == 0 else "chrome" if r == 1 else "steel_dark", 4)
	cap_rows = [(SHELL_Z1, 1.06, 0.0), (SHELL_Z1 + 0.05, 1.06, 0.0), (SHELL_Z1 + 0.10, 1.0, 0.0), (CAP_Z1 - 0.06, 0.82, 0.0), (CAP_Z1, 0.55, 0.0)]
	cap_rings = closed_loft(mesh, full_closed, cap_rows, trim, cap_uv, cap_top="vent")
	# the dome rows in shell steel (override the two upper bands): rebuild those two bands with the shell material
	# (closed_loft gave them trim/steel_dark; add shell quads a hair outward so they win)
	for r in (2, 3):
		for c in range(len(full_closed) - 1):
			pts = [cap_rings[r][c], cap_rings[r][c + 1], cap_rings[r + 1][c + 1], cap_rings[r + 1][c]]
			pts = [p + Vector((0, 0, 0.002)) for p in pts]
			mesh.face(pts, [(p.z, 0.0) for p in pts], shell)
	# underside annulus from the cap lip to the shell top (closes the shell top / door header)
	shell_top = [ring_point(t, SHELL_Z1, SHELL_T) for t in full_closed]
	annulus(mesh, cap_rings[0], shell_top, "steel_dark", flip=True)
	# ceiling inside the pod
	ceil = [ring_point(t, SHELL_Z1, SHELL_T) for t in full_closed[:-1]]
	centre = sum(ceil, Vector()) / len(ceil)
	for c in range(len(ceil)):
		mesh.face([ceil[(c + 1) % len(ceil)], ceil[c], centre], flat_uvs("panel", 3), trim)

	# --- interior floor: grate tiles clipped to the inner footprint
	fz = SHELL_Z0 + 0.02
	a_in, b_in = profile_at(SHELL_Z0)
	a_in, b_in = a_in - SHELL_T - 0.02, b_in - SHELL_T - 0.02
	tile = 0.12
	nx, ny = int(2 * a_in / tile), int(2 * b_in / tile)
	for ix in range(nx):
		for iy in range(ny):
			x0, y0 = -a_in + ix * tile, -b_in + iy * tile
			corners = [(x0, y0), (x0 + tile, y0), (x0 + tile, y0 + tile), (x0, y0 + tile)]
			inside = all((abs(x / a_in) ** SUPER_N + abs(y / b_in) ** SUPER_N) <= 1.0 for x, y in corners)
			if not inside:
				continue
			mesh.face([Vector((x, y, fz)) for x, y in corners], cell_uvs("grate"), trim)
	# the floor plate under the tiles, so gaps at the rim show steel, not the void
	fl = [ring_point(t, SHELL_Z0, SHELL_T - 0.005, dz=0.012) for t in full_closed[:-1]]
	centre = sum(fl, Vector()) / len(fl)
	for c in range(len(fl)):
		mesh.face([fl[c], fl[(c + 1) % len(fl)], centre], flat_uvs("steel_dark", 3), trim)

	# --- interior padding and furniture (shared with the physics mesh: every one of these is rigid)
	add_interior(mesh, body_t, pad)
	a_seat = profile_at(0.56)[0] - SHELL_T - PAD_T
	# lumbar nameplate on the seat pedestal
	box(mesh, (-a_seat + 0.145, 0.0, 0.30), (0.01, 0.16, 0.08), "label", uv_mode="cell")

	# --- body tubes: wall port, ribbed hose along a sagging bezier, brass cuff + dark plug at the loose end
	def tube(port, ctrl, end, r=0.028):
		pts = bezier(port, ctrl, end, 10)
		path_tube(mesh, pts, r, RIB)
		d = (pts[-1] - pts[-2]).normalized()
		# loose end: a brass cuff then the dark plug that used to sit in the body
		prism(mesh, pts[-1], d, 0.042, 0.06, "cuff", trim)
		prism(mesh, pts[-1] + d * 0.06, d, 0.03, 0.05, "trim_dark", trim)

	def wall_port(theta, z):
		"""A socket disc 4 mm proud of the inner wall at ring angle theta, its dark centre recessed; returns
		the hose start point just inside the pod."""
		p = ring_point(theta, z, SHELL_T + 0.004)
		n = Vector(super_normal(theta, *profile_at(z)) + (0.0,))     # outward (into the wall)
		side, up = n.cross(Vector((0, 0, 1))).normalized(), Vector((0, 0, 1))
		# side x up = -n, so a ring in (side, up) order faces -n = into the pod
		for rad, m, depth in ((0.075, "port", 0.0), (0.045, "trim_dark", 0.012)):
			c0 = p + n * depth
			ring = [c0 + (side * math.cos(2 * math.pi * k / 8) + up * math.sin(2 * math.pi * k / 8)) * rad for k in range(8)]
			back = [q + n * 0.03 for q in ring]
			mesh.face(ring, cell_uvs(m, 8) if m == "port" else flat_uvs(m, 8), trim)
			for k in range(8):
				j = (k + 1) % 8
				mesh.face([ring[k], ring[j], back[j], back[k]], flat_uvs("steel_dark", 4), trim)
		return p - n * 0.02

	# shoulder tubes from the side walls, sag down and inward to where the shoulder blades were
	for sgn in (1, -1):
		p = wall_port(math.radians(90 if sgn > 0 else 270), 1.62)
		tube(p, (p.x - 0.05, p.y - sgn * 0.12, 1.30), (-0.20, sgn * 0.10, 1.22))
	# nape tube from the back wall above the headrest, falls over the headrest
	p = wall_port(math.radians(180), 2.02)
	tube(p, (p.x + 0.16, 0.06, 1.95), (-0.16, 0.08, 1.50))
	# hip tubes from the low side walls
	for sgn in (1, -1):
		p = wall_port(math.radians(100 if sgn > 0 else 260), 0.98)
		tube(p, (p.x + 0.08, p.y - sgn * 0.06, 0.70), (-0.02, sgn * 0.17, 0.66))

	# --- side lamp panels: an inset dark panel proud of the shell with lamp sockets, a readout screen and a vent
	for sgn in (1, -1):
		b_hi = max(profile_at(z)[1] for z in PANEL_Z)
		b_lo = min(profile_at(z)[1] for z in PANEL_Z)
		y_out = sgn * (b_hi + 0.012)
		y_in = sgn * (b_lo - 0.03)
		cy = (y_out + y_in) * 0.5
		th = abs(y_out - y_in)
		zc = (PANEL_Z[0] + PANEL_Z[1]) * 0.5
		xc = (PANEL_X[0] + PANEL_X[1]) * 0.5
		box(mesh, (xc, cy, zc), (PANEL_X[1] - PANEL_X[0], th, PANEL_Z[1] - PANEL_Z[0]), "panel")
		# chrome frame lip around the panel
		box(mesh, (xc, sgn * (b_hi + 0.016), zc), (PANEL_X[1] - PANEL_X[0] + 0.03, 0.008, PANEL_Z[1] - PANEL_Z[0] + 0.03), "chrome")
		box(mesh, (xc, sgn * (b_hi + 0.02), zc), (PANEL_X[1] - PANEL_X[0] - 0.02, 0.004, PANEL_Z[1] - PANEL_Z[0] - 0.02), "panel")
		# lamp sockets (the lamp domes are prefab children at LAMP_X / LAMP_Z)
		for z in LAMP_Z:
			cylinder(mesh, (LAMP_X, sgn * (b_hi + 0.026), z), 0.038, 0.012, "trim_dark", segs=8, axis='Y')
		# readout screen recess (the screen itself is the prefab's Screen_L / Screen_R child, so it can power down)
		box(mesh, (0.08, sgn * (b_hi + 0.024), SCREEN_Z), (0.19, 0.008, 0.14), "trim_dark")
		box(mesh, (0.08, sgn * (b_hi + 0.026), 1.30), (0.17, 0.012, 0.10), "vent", uv_mode="cell")
		# warning stripe along the panel's lower edge
		box(mesh, (xc, sgn * (b_hi + 0.026), PANEL_Z[0] + 0.05), (PANEL_X[1] - PANEL_X[0] - 0.06, 0.008, 0.035), "warning", uv_mode="cell")

	# --- rear conduit: a thick ribbed trunk from the cap down into a junction box on the plinth, two small hoses beside it
	a_back_out = profile_at(1.45)[0]
	trunk = bezier((-profile_at(2.30)[0] - 0.04, 0.0, 2.30), (-a_back_out - 0.22, 0.0, 1.3), (-profile_at(0.3)[0] - 0.16, 0.0, 0.32), 10)
	path_tube(mesh, trunk, 0.065, RIB, segs=8)
	box(mesh, (-profile_at(0.3)[0] - 0.16, 0.0, 0.22), (0.26, 0.34, 0.20), "steel_dark")
	box(mesh, (-profile_at(0.3)[0] - 0.295, 0.0, 0.22), (0.01, 0.20, 0.10), "label", uv_mode="cell")
	for sgn in (1, -1):
		h = bezier((-profile_at(2.0)[0] + 0.02, sgn * 0.22, 2.0), (-a_back_out - 0.14, sgn * 0.30, 1.1), (-profile_at(0.3)[0] - 0.1, sgn * 0.14, 0.33), 8)
		path_tube(mesh, h, 0.024, RIB, segs=6)
	# cap-top beacon housing
	cylinder(mesh, (-0.12, 0.0, CAP_Z1 + 0.03), 0.06, 0.06, "trim_dark", segs=8, radius2=0.05)
	cylinder(mesh, (-0.12, 0.0, CAP_Z1 + 0.075), 0.035, 0.03, "red_dark", segs=8)

	obj = mesh.to_object(POD, [mats[m] for m in SHELL_MATS])
	return obj


def build_pod_phys(mats):
	"""Hollow shell for the concave PhysicsMeshFile: plinth block, C-shell walls, roof slab, the floor plate and every
	rigid interior solid (pads, headrest, seat, pedestal, armrests). No door - the door leaf has its own hull. The
	tubes stay out (they hang where the pawn stands)."""
	mesh = Mesh(SHELL_MATS)
	door_t, body_t = ring_thetas()
	full_closed = body_t + [t + 2 * math.pi for t in door_t[1:]]
	coarse_rows = [SHELL_Z0, 0.80, 1.45, 1.90, SHELL_Z1]
	thick_grid(mesh, body_t, coarse_rows, 0.0, SHELL_T, "stasis_pod_trim", "stasis_pod_trim", "stasis_pod_trim")
	closed_loft(mesh, full_closed, [(0.0, 1.14, 0.0), (SHELL_Z0 + 0.02, 1.05, 0.0)], "stasis_pod_trim", lambda c, r, p: flat_uvs("steel_dark", 4), cap_top="steel_dark", cap_bottom="steel_dark")
	closed_loft(mesh, full_closed, [(SHELL_Z1 - 0.02, 1.06, SHELL_T), (SHELL_Z1, 1.06, 0.0), (CAP_Z1, 0.55, 0.0)], "stasis_pod_trim", lambda c, r, p: flat_uvs("steel_dark", 4), cap_top="steel_dark", cap_bottom="steel_dark")
	add_interior(mesh, body_t, "stasis_pod_pad")
	return mesh.to_object(POD + "_phys", [mats[m] for m in SHELL_MATS])


def build_screen(mats):
	"""The side-panel readout: a thin plate the prefab places twice; its own material is the lit screen, the
	powered-down look is the stasis_pod_screen_off override."""
	mesh = Mesh([SCREEN])
	w, t, h = 0.17, 0.012, 0.12
	pts = {
		"a": Vector((-w / 2, -t / 2, -h / 2)), "b": Vector((w / 2, -t / 2, -h / 2)), "c": Vector((w / 2, -t / 2, h / 2)), "d": Vector((-w / 2, -t / 2, h / 2)),
		"e": Vector((-w / 2, t / 2, -h / 2)), "f": Vector((w / 2, t / 2, -h / 2)), "g": Vector((w / 2, t / 2, h / 2)), "h": Vector((-w / 2, t / 2, h / 2)),
	}
	full = [(0, 1), (1, 1), (1, 0), (0, 0)]
	edge = [(0.02, 0.98)] * 4
	# -Y face reads left-to-right seen from -Y, +Y face seen from +Y (mirrored order) - both show the readout upright
	mesh.face([pts["a"], pts["b"], pts["c"], pts["d"]], full, SCREEN)
	mesh.face([pts["f"], pts["e"], pts["h"], pts["g"]], full, SCREEN)
	mesh.face([pts["d"], pts["c"], pts["g"], pts["h"]], edge, SCREEN)
	mesh.face([pts["a"], pts["e"], pts["f"], pts["b"]], edge, SCREEN)
	mesh.face([pts["a"], pts["d"], pts["h"], pts["e"]], edge, SCREEN)
	mesh.face([pts["b"], pts["f"], pts["g"], pts["c"]], edge, SCREEN)
	return mesh.to_object(SCREEN, [mats[SCREEN]])


def build_puff(mats):
	"""Unit low-poly smoke blob (radius 1 m, scaled at runtime): an octahedron subdivided once, vertices jittered, so
	the crash dust and the door's air release read as faceted puffs, not spheres."""
	mesh = Mesh([PUFF])
	rng = np.random.default_rng(5)
	base = [Vector((1, 0, 0)), Vector((-1, 0, 0)), Vector((0, 1, 0)), Vector((0, -1, 0)), Vector((0, 0, 1)), Vector((0, 0, -1))]
	faces = [(0, 2, 4), (2, 1, 4), (1, 3, 4), (3, 0, 4), (2, 0, 5), (1, 2, 5), (3, 1, 5), (0, 3, 5)]
	cache = {}

	def jitter(v):
		key = (round(v.x, 4), round(v.y, 4), round(v.z, 4))
		if key not in cache:
			cache[key] = v.normalized() * (0.78 + 0.3 * rng.random())
		return cache[key]

	for i, j, k in faces:
		a, b, c = base[i], base[j], base[k]
		ab, bc, ca = (a + b) * 0.5, (b + c) * 0.5, (c + a) * 0.5
		for tri in ((a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca)):
			mesh.face([jitter(p) for p in tri], [(0.5, 0.5)] * 3, PUFF)
	return mesh.to_object(PUFF, [mats[PUFF]])


# ---------------------------------------------------------------- the door
def door_hinge():
	"""Hinge line: vertical axis through the left (+Y) jamb at mid height, a little outside the shell face."""
	theta = math.radians(DOOR_HALF_DEG)
	p = ring_point(theta, 1.45, -0.01)
	return Vector((p.x, p.y, 0.0))


def build_door(mats):
	door_t, body_t = ring_thetas()
	shell, trim, glass = "stasis_pod_shell", "stasis_pod_trim", GLASS
	mesh = Mesh([shell, trim, glass])
	rows = SHELL_ROWS
	r0 = rows.index(WINDOW_Z[0])
	r1 = rows.index(WINDOW_Z[1])
	c0, c1 = WINDOW_COLS
	holes = {(c, r) for c in range(c0, c1) for r in range(r0, r1)}
	arc = arc_length_table(door_t, 1.45)
	# the leaf sits 4 mm proud of the shell so the seam reads
	thick_grid(mesh, door_t, rows, -0.004, DOOR_T, shell, "panel", "trim_dark", holes=holes,
			   uv_out=lambda c, r: (rows[r], arc[c] + 2.0))
	# window bezel: the hole walls are dark; chrome strips 3 cm wide lie on the outer face just outside the hole
	for c in range(c0, c1):
		for r_edge, dz in ((r0, -1), (r1, 1)):
			z = rows[r_edge]
			o0 = ring_point(door_t[c], z, -0.008)
			o1 = ring_point(door_t[c + 1], z, -0.008)
			o2 = ring_point(door_t[c + 1], z + 0.03 * dz, -0.008)
			o3 = ring_point(door_t[c], z + 0.03 * dz, -0.008)
			pts = [o0, o1, o2, o3] if dz > 0 else [o1, o0, o3, o2]
			mesh.face(pts, flat_uvs("chrome", 4), trim)
	for r in range(r0, r1):
		for c_edge, dt in ((c0, -1), (c1, 1)):
			th = door_t[c_edge]
			th2 = door_t[c_edge] + dt * math.radians(1.6)
			o0 = ring_point(th, rows[r], -0.008)
			o1 = ring_point(th, rows[r + 1], -0.008)
			o2 = ring_point(th2, rows[r + 1], -0.008)
			o3 = ring_point(th2, rows[r], -0.008)
			pts = [o0, o1, o2, o3] if dt < 0 else [o1, o0, o3, o2]
			mesh.face(pts, flat_uvs("chrome", 4), trim)
	# glass pane at mid thickness over the hole cells (both sides rendered by the glass material)
	for c in range(c0, c1):
		for r in range(r0, r1):
			pts = [ring_point(door_t[c], rows[r], DOOR_T * 0.5), ring_point(door_t[c + 1], rows[r], DOOR_T * 0.5),
				   ring_point(door_t[c + 1], rows[r + 1], DOOR_T * 0.5), ring_point(door_t[c], rows[r + 1], DOOR_T * 0.5)]
			us = [((c - c0) / (c1 - c0), (r - r0) / (r1 - r0)), ((c + 1 - c0) / (c1 - c0), (r - r0) / (r1 - r0)),
				  ((c + 1 - c0) / (c1 - c0), (r + 1 - r0) / (r1 - r0)), ((c - c0) / (c1 - c0), (r + 1 - r0) / (r1 - r0))]
			mesh.face(pts, us, glass)
	def face_plate(th, z, w, h, inset, cell, planar):
		"""A w x h plate on the door's outer face, facing out. side x Z = -n, so wind (-w,-h) -> (-w,h) -> (w,h) -> (w,-h)."""
		pc = ring_point(th, z, inset)
		n = Vector(super_normal(th, *profile_at(z)) + (0.0,))
		side = n.cross(Vector((0, 0, 1))).normalized()
		corners = ((-w, -h), (-w, h), (w, h), (w, -h))
		pts = [pc + side * cx + Vector((0, 0, cz)) for cx, cz in corners]
		mesh.face(pts, cell_uvs(cell) if planar else flat_uvs(cell, 4), trim)
		return pc, n

	# handle: recessed dark plate + chrome bar near the free (-Y) edge
	plate_c, n = face_plate(door_t[2], 1.02, 0.06, 0.20, -0.006, "trim_dark", False)
	for dz in (-0.13, 0.13):
		prism(mesh, plate_c + Vector((0, 0, dz)), n, 0.012, 0.03, "chrome", trim)
	prism(mesh, plate_c + n * 0.03 + Vector((0, 0, -0.145)), Vector((0, 0, 1)), 0.016, 0.29, "chrome", trim)
	# door-edge chrome strip on the free edge, dark seal on the hinge edge (a hair outside the leaf's own edge walls)
	for c_edge, m, sgn in ((0, "chrome", -1), (DOOR_SEGS, "seal", 1)):
		th = door_t[c_edge] + sgn * math.radians(0.3)
		for z0, z1 in zip(rows, rows[1:]):
			o0, o1 = ring_point(th, z0, -0.008), ring_point(th, z1, -0.008)
			i0, i1 = ring_point(th, z0, DOOR_T + 0.004), ring_point(th, z1, DOOR_T + 0.004)
			pts = [o0, o1, i1, i0] if c_edge == 0 else [o0, i0, i1, o1]
			mesh.face(pts, flat_uvs(m, 4), trim)
	# a nameplate under the window and a warning stripe at knee height
	cmid = door_t[DOOR_SEGS // 2]
	face_plate(cmid, 1.30, 0.09, 0.05, -0.012, "label", True)
	face_plate(cmid, 0.70, 0.22, 0.03, -0.012, "warning", True)

	obj = mesh.to_object(DOOR, [mats[shell], mats[trim], mats[glass]])
	# phys: a convex slab over the leaf (window ignored)
	pm = Mesh([trim])
	thick_grid(pm, [door_t[0], door_t[DOOR_SEGS // 2], door_t[-1]], [rows[0], 1.45, rows[-1]], -0.004, DOOR_T, trim, trim, trim)
	phys = pm.to_object(DOOR + "_phys", [mats[trim]])
	hinge = door_hinge()
	for o in (obj, phys):
		o.data.transform(Matrix.Translation(-hinge))
		o.data.update()
	return obj, phys, hinge


# ---------------------------------------------------------------- the lamp dome
def build_lamp(mats):
	mesh = Mesh([LAMP])
	rings = []
	for r, z in ((0.030, 0.0), (0.030, 0.010), (0.026, 0.020), (0.016, 0.030)):
		rings.append([Vector((r * math.cos(2 * math.pi * k / 8), r * math.sin(2 * math.pi * k / 8), z)) for k in range(8)])
	for a, b in zip(rings, rings[1:]):
		for k in range(8):
			j = (k + 1) % 8
			mesh.face([a[k], a[j], b[j], b[k]], [(0.5, 0.5)] * 4, LAMP)
	mesh.face(rings[-1], [(0.5, 0.5)] * 8, LAMP)
	mesh.face(list(reversed(rings[0])), [(0.5, 0.95)] * 8, LAMP)   # base disc samples the darker rim row
	return mesh.to_object(LAMP, [mats[LAMP]])


# ---------------------------------------------------------------- vmdl / vmat / prefab writers
def vmdl_text(name, rel, render_names, remaps, phys=None, phys_mode="hull", surface="metal"):
	remap_txt = "".join(
		f"""
							{{
								from = "{frm}"
								to = "{rel}/{to}.vmat"
							}},""" for frm, to in remaps)
	render_list = ", ".join(f'"{n}"' for n in render_names)
	phys_txt = ""
	if phys:
		cls = "PhysicsHullFile" if phys_mode == "hull" else "PhysicsMeshFile"
		extra = """
						faceMergeAngle = 10.0
						maxHullVertices = 64
						hull_mode = "SingleHull\"""" if phys_mode == "hull" else """
						maxMeshVertices = 0
						qemError = 0.0"""
		phys_txt = f"""
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "{cls}"
						name = "{name}_physics"
						filename = "{rel}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						surface_prop = "{surface}"
						collision_tags = "solid"{extra}
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{phys}" ]
						}}
					}},
				]
			}},"""
	return f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[{remap_txt}
						]
						use_global_default = false
						global_default_material = "{rel}/{remaps[0][1]}.vmat"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						name = "{name}_lod0"
						filename = "{rel}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ {render_list} ]
						}}
					}},
				]
			}},{phys_txt}
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""


def pixel_vmat(tex, roughness, flat=True, specular=1.0):
	return f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "{roughness:.3f}"
	PixelSpecular "{specular:.3f}"
	PixelFlatShading "{1.0 if flat else 0.0:.3f}"
	TextureColor "{ASSET_REL}/{tex}.png"
}}
"""


GLASS_VMAT = f"""Layer0
{{
	shader "shaders/glass.shader"

	F_GLASS_QUALITY 1
	F_RENDER_BACKFACES 1
	g_flTintColor "[0.780000 0.900000 1.000000]"
	g_flBlurAmount "0.000"
	g_flRefractionStrength "1.000"
	TextureColor "{ASSET_REL}/{GLASS}.png"
	TextureTranslucency "{ASSET_REL}/{GLASS}_trans.png"
}}
"""


def guid(tag):
	return str(uuid.uuid5(uuid.UUID("5d0a1b2c-3e4f-4a5b-8c6d-7e8f9a0b1c2d"), "stasis_pod/" + tag))


def eng(v):
	"""Blender metres -> engine units string (the export yaw makes the axes line up 1:1)."""
	return f"{v.x * UNITS_PER_M:.3f},{v.y * UNITS_PER_M:.3f},{v.z * UNITS_PER_M:.3f}"


def quat_yaw(deg):
	h = math.radians(deg) * 0.5
	return f"0,0,{math.sin(h):.6f},{math.cos(h):.6f}"


def quat_pitch_roll_for_lamp(sgn):
	"""Lamp dome local +Z must point along world ±Y (out of the side panel). Roll about X by -90 deg points +Z to +Y."""
	ang = math.radians(-90.0 if sgn > 0 else 90.0) * 0.5
	return f"{math.sin(ang):.6f},0,0,{math.cos(ang):.6f}"


def model_renderer(gid, model, tint="1,1,1,1"):
	return {
		"__type": "Sandbox.ModelRenderer", "__guid": gid, "__enabled": True, "Flags": 0,
		"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
		"MaterialOverride": None, "Materials": None, "Model": model,
		"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
		"OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None,
		"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
		"RenderType": "On", "Tint": tint,
	}


def model_collider(gid, model, static):
	return {
		"__type": "Sandbox.ModelCollider", "__guid": gid, "__enabled": True, "Flags": 0,
		"ColliderFlags": 0, "Elasticity": None, "Friction": None, "IsTrigger": False, "Model": model,
		"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
		"OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None,
		"OnObjectTriggerEnter": None, "OnObjectTriggerExit": None, "OnTriggerEnter": None, "OnTriggerExit": None,
		"RollingResistance": None, "Static": static, "Surface": None, "SurfaceVelocity": "0,0,0",
	}


def child(gid, name, pos, rot, components, tags="", enabled=True):
	return {
		"__guid": gid, "__version": 2, "Flags": 0, "Name": name, "Position": pos, "Rotation": rot, "Scale": "1,1,1",
		"Tags": tags, "Enabled": enabled, "NetworkMode": 2, "NetworkFlags": 0, "NetworkOrphaned": 0,
		"NetworkTransmit": True, "OwnerTransfer": 1, "Components": components, "Children": [],
	}


def text_renderer(gid, scale):
	"""A world-text plate; StasisPod writes the serial into its TextScope at runtime."""
	return {
		"__type": "Sandbox.TextRenderer", "__guid": gid, "__enabled": True, "Flags": 0,
		"Scale": scale,
		"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
		"OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None,
	}


def write_prefab(hinge):
	door_guid = guid("door")
	light_guid = guid("light")
	occupant_guid = guid("occupant")
	# serial plate on the door's outer face above the window, in door-local (hinge-relative) coordinates
	plate = ring_point(0.0, 2.17, -0.02) - hinge
	children = [
		child(door_guid, "Door", eng(hinge), "0,0,0,1", [
			model_renderer(guid("door/renderer"), f"{ASSET_REL}/{DOOR}.vmdl"),
			model_collider(guid("door/collider"), f"{ASSET_REL}/{DOOR}.vmdl", static=False),
		], tags="doorleaf"),
		child(occupant_guid, "Occupant", eng(Vector((PAWN_ANCHOR_X, 0.0, SHELL_Z0 + 0.02))), "0,0,0,1", []),
	]
	# TextRenderer text reads correctly from the object's -X side, so both plates turn their back (+X) to the reader
	children[0]["Children"].append(child(guid("door/plate"), "SerialPlate", eng(plate), quat_yaw(180.0), [text_renderer(guid("door/plate/text"), 0.09)]))
	a_in = profile_at(2.20)[0] - SHELL_T
	children.append(child(guid("plate/inside"), "SerialPlateInside", eng(Vector((-a_in + 0.02, 0.0, 2.20))), quat_yaw(180.0), [text_renderer(guid("plate/inside/text"), 0.055)]))
	b_hi = max(profile_at(z)[1] for z in PANEL_Z)
	for sgn, side in ((1, "L"), (-1, "R")):
		for i, z in enumerate(LAMP_Z):
			pos = Vector((LAMP_X, sgn * (b_hi + 0.032), z))
			children.append(child(guid(f"lamp/{side}{i}"), f"Lamp_{side}{i + 1}", eng(pos), quat_pitch_roll_for_lamp(sgn), [
				model_renderer(guid(f"lamp/{side}{i}/renderer"), f"{ASSET_REL}/{LAMP}.vmdl"),
			]))
		children.append(child(guid(f"screen/{side}"), f"Screen_{side}", eng(Vector((0.08, sgn * (b_hi + 0.030), SCREEN_Z))), "0,0,0,1", [
			model_renderer(guid(f"screen/{side}/renderer"), f"{ASSET_REL}/{SCREEN}.vmdl"),
		]))
	children.append(child(light_guid, "InteriorLight", eng(Vector((-0.05, 0.0, 2.05))), "0,0,0,1", [{
		"__type": "Sandbox.PointLight", "__guid": guid("light/point"), "__enabled": True, "Flags": 0,
		"Attenuation": 1, "Contribution": "Diffuse, Specular, Transmissive", "FogMode": "Enabled", "FogStrength": 0.6,
		"LightColor": "0.55,0.9,1,1",
		"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
		"OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None,
		"Radius": 110, "ShadowBias": 0, "ShadowHardness": 0, "Shadows": False,
	}], tags="light_point,light"))
	root = {
		"RootObject": {
			"__guid": guid("root"), "__version": 2, "Flags": 0, "Name": "stasis_pod",
			"Position": "0,0,0", "Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "solid,grapple",
			"Enabled": True, "NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1,
			"Components": [
				model_renderer(guid("root/renderer"), f"{ASSET_REL}/{POD}.vmdl"),
				model_collider(guid("root/collider"), f"{ASSET_REL}/{POD}.vmdl", static=True),
				{
					"__type": "Survival.StasisPod", "__guid": guid("root/stasispod"), "__enabled": True, "Flags": 0,
					"Door": {"_type": "gameobject", "go": door_guid},
					"OccupantAnchor": {"_type": "gameobject", "go": occupant_guid},
					"DoorOpenDegrees": 115, "DoorSwingSeconds": 1.8,
					"InteriorLight": {"_type": "component", "component_id": guid("light/point"), "go": light_guid, "component_type": "PointLight"},
					"LightPulseHz": 0.35, "BlinkSeed": 0,
					"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
					"OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None,
				},
			],
			"Children": children,
			"__properties": {
				"NetworkInterpolation": True, "TimeScale": 1, "WantsSystemScene": True, "Metadata": {},
				"NavMesh": {"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True, "EditorAutoUpdate": False,
							"AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18, "AgentMaxSlope": 40, "ExcludedBodies": "",
							"IncludedBodies": "", "DeferGeneration": False, "CustomBounds": False},
			},
			"__variables": [],
		},
		"ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None, "DontBreakAsTemplate": False,
		"__references": [], "__version": 2,
	}
	os.makedirs(PREFAB_DIR, exist_ok=True)
	with open(os.path.join(PREFAB_DIR, "stasis_pod.prefab"), "w", newline="\n") as f:
		json.dump(root, f, indent=2)
		f.write("\n")


# ---------------------------------------------------------------- export / preview
EXPORT_YAW = Matrix.Rotation(math.radians(-90.0), 4, 'Z')


def export(objs, name):
	for o in objs:
		o.data.transform(EXPORT_YAW)
		o.data.update()
	for o in bpy.data.objects:
		o.select_set(False)
	for o in objs:
		o.select_set(True)
	bpy.context.view_layer.objects.active = objs[0]
	bpy.ops.export_scene.fbx(filepath=os.path.join(OUT_DIR, name + ".fbx"), use_selection=True,
							 global_scale=1.0, apply_unit_scale=True, object_types={'MESH'},
							 mesh_smooth_type='OFF', path_mode='STRIP', embed_textures=False)
	for o in objs:
		o.data.transform(EXPORT_YAW.inverted())
		o.data.update()


def make_material(name, png, roughness):
	mat = bpy.data.materials.new(name)
	mat.use_nodes = True
	bsdf = mat.node_tree.nodes["Principled BSDF"]
	bsdf.inputs["Roughness"].default_value = roughness
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(png, check_existing=True)
	tex.interpolation = 'Closest'
	mat.node_tree.nodes.active = tex
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	if name == GLASS:
		bsdf.inputs["Alpha"].default_value = 0.35
		mat.diffuse_color = (0.78, 0.9, 1.0, 0.35)
	return mat


def preview(pod, door, lamps, hinge, tag, door_yaw_deg, cam_from, look_at, ortho=False):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_WORKBENCH'
	scene.display.shading.light = 'STUDIO'
	scene.display.shading.color_type = 'TEXTURE'
	scene.display.shading.show_shadows = True
	scene.display.shading.show_cavity = True
	scene.display.shading.show_backface_culling = True   # an inside-out face shows as a hole, like the engine
	scene.render.resolution_x = 1100
	scene.render.resolution_y = 1100
	scene.render.film_transparent = False
	scene.world = scene.world or bpy.data.worlds.new("World")
	scene.world.color = (0.18, 0.2, 0.24)
	door.location = hinge
	door.rotation_euler = (0, 0, math.radians(door_yaw_deg))
	cam = bpy.data.objects.get("PreviewCam")
	if cam is None:
		cam_data = bpy.data.cameras.new("PreviewCam")
		cam = bpy.data.objects.new("PreviewCam", cam_data)
		scene.collection.objects.link(cam)
	cam.data.type = 'ORTHO' if ortho else 'PERSP'
	cam.data.lens = 40
	cam.data.ortho_scale = 3.0
	cam.location = Vector(cam_from)
	d = Vector(look_at) - cam.location
	cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
	scene.camera = cam
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	scene.render.filepath = os.path.join(PREVIEW_DIR, f"stasis_pod_{tag}.png")
	bpy.ops.render.render(write_still=True)
	print("preview", scene.render.filepath)


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(OUT_DIR, exist_ok=True)

	# textures + materials
	pngs = {
		"stasis_pod_shell": make_shell(),
		"stasis_pod_pad": make_pad(),
		"stasis_pod_trim": make_trim(),
	}
	for name, img in pngs.items():
		write_png(os.path.join(OUT_DIR, name + ".png"), img)
	g, gt = make_glass()
	write_png(os.path.join(OUT_DIR, GLASS + ".png"), g)
	write_png(os.path.join(OUT_DIR, GLASS + "_trans.png"), gt)
	# screens: the lit readout is the model's own material, the powered-down look an override; the puff is flat grey
	write_png(os.path.join(OUT_DIR, SCREEN + ".png"), make_screen(True))
	write_png(os.path.join(OUT_DIR, SCREEN + "_off.png"), make_screen(False))
	with open(os.path.join(OUT_DIR, SCREEN + ".vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat(SCREEN, 0.4, flat=True, specular=0.0))
	with open(os.path.join(OUT_DIR, SCREEN + "_off.vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat(SCREEN + "_off", 0.3, flat=True, specular=1.0))
	write_png(os.path.join(OUT_DIR, PUFF + ".png"), make_lamp((198, 200, 206)))
	with open(os.path.join(OUT_DIR, PUFF + ".vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat(PUFF, 1.0, flat=True, specular=0.0))
	# crash dust: the same blob in a dry earth tone (StasisPod overrides the puff material for the landing ring)
	write_png(os.path.join(OUT_DIR, PUFF + "_dust.png"), make_lamp((132, 118, 96)))
	with open(os.path.join(OUT_DIR, PUFF + "_dust.vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat(PUFF + "_dust", 1.0, flat=True, specular=0.0))
	# the off look is the lamp model's own material: s&box resolves an FBX material to the vmat named like the texture
	# the FBX references (stasis_pod_lamp.png -> stasis_pod_lamp.vmat), and that wins over the vmdl remap - so the
	# Blender lamp material must carry the OFF png. The lit colours are runtime overrides (StasisPod).
	for cname, rgb in LAMP_COLORS.items():
		base = LAMP if cname == "off" else f"{LAMP}_{cname}"
		write_png(os.path.join(OUT_DIR, base + ".png"), make_lamp(rgb))
		with open(os.path.join(OUT_DIR, base + ".vmat"), "w", newline="\n") as f:
			f.write(pixel_vmat(base, 0.25 if cname == "off" else 0.15, flat=True, specular=1.0 if cname == "off" else 0.0))
	with open(os.path.join(OUT_DIR, "stasis_pod_shell.vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat("stasis_pod_shell", 0.30))
	with open(os.path.join(OUT_DIR, "stasis_pod_pad.vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat("stasis_pod_pad", 0.75))
	with open(os.path.join(OUT_DIR, "stasis_pod_trim.vmat"), "w", newline="\n") as f:
		f.write(pixel_vmat("stasis_pod_trim", 0.55))
	with open(os.path.join(OUT_DIR, GLASS + ".vmat"), "w", newline="\n") as f:
		f.write(GLASS_VMAT)

	mats = {
		"stasis_pod_shell": make_material("stasis_pod_shell", os.path.join(OUT_DIR, "stasis_pod_shell.png"), 0.3),
		"stasis_pod_pad": make_material("stasis_pod_pad", os.path.join(OUT_DIR, "stasis_pod_pad.png"), 0.75),
		"stasis_pod_trim": make_material("stasis_pod_trim", os.path.join(OUT_DIR, "stasis_pod_trim.png"), 0.55),
		GLASS: make_material(GLASS, os.path.join(OUT_DIR, GLASS + ".png"), 0.1),
		LAMP: make_material(LAMP, os.path.join(OUT_DIR, LAMP + ".png"), 0.2),   # the off png: the engine picks the vmat by the FBX texture name
		SCREEN: make_material(SCREEN, os.path.join(OUT_DIR, SCREEN + ".png"), 0.4),
		PUFF: make_material(PUFF, os.path.join(OUT_DIR, PUFF + ".png"), 1.0),
	}

	pod = build_pod(mats)
	pod_phys = build_pod_phys(mats)
	door, door_phys, hinge = build_door(mats)
	lamp = build_lamp(mats)
	screen = build_screen(mats)
	puff = build_puff(mats)

	export([pod, pod_phys], POD)
	export([door, door_phys], DOOR)
	export([lamp], LAMP)
	export([screen], SCREEN)
	export([puff], PUFF)

	with open(os.path.join(OUT_DIR, POD + ".vmdl"), "w", newline="\n") as f:
		f.write(vmdl_text(POD, ASSET_REL, [POD], [(m + ".vmat", m) for m in SHELL_MATS] + [(m, m) for m in SHELL_MATS],
						  phys=POD + "_phys", phys_mode="mesh"))
	with open(os.path.join(OUT_DIR, DOOR + ".vmdl"), "w", newline="\n") as f:
		door_mats = ["stasis_pod_shell", "stasis_pod_trim", GLASS]
		f.write(vmdl_text(DOOR, ASSET_REL, [DOOR], [(m + ".vmat", m) for m in door_mats] + [(m, m) for m in door_mats],
						  phys=DOOR + "_phys", phys_mode="hull"))
	with open(os.path.join(OUT_DIR, LAMP + ".vmdl"), "w", newline="\n") as f:
		f.write(vmdl_text(LAMP, ASSET_REL, [LAMP], [(LAMP + ".vmat", LAMP), (LAMP, LAMP)]))
	with open(os.path.join(OUT_DIR, SCREEN + ".vmdl"), "w", newline="\n") as f:
		f.write(vmdl_text(SCREEN, ASSET_REL, [SCREEN], [(SCREEN + ".vmat", SCREEN), (SCREEN, SCREEN)]))
	with open(os.path.join(OUT_DIR, PUFF + ".vmdl"), "w", newline="\n") as f:
		f.write(vmdl_text(PUFF, ASSET_REL, [PUFF], [(PUFF + ".vmat", PUFF), (PUFF, PUFF)]))
	write_prefab(hinge)

	for o in (pod, pod_phys, door, door_phys, lamp, screen, puff):
		tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
		print(f"{o.name}: {len(o.data.polygons)} faces, {tris} tris")
	xs = [v.co.x for v in pod.data.vertices]
	ys = [v.co.y for v in pod.data.vertices]
	zs = [v.co.z for v in pod.data.vertices]
	print(f"pod bounds x {min(xs):.2f}..{max(xs):.2f} y {min(ys):.2f}..{max(ys):.2f} z {min(zs):.2f}..{max(zs):.2f}")
	print(f"hinge (blender m) {hinge.x:.3f},{hinge.y:.3f}  engine {eng(hinge)}")

	# previews: lamp copies on the panels, door closed / open, a front view and a look inside
	pod_phys.hide_render = True
	door_phys.hide_render = True
	b_hi = max(profile_at(z)[1] for z in PANEL_Z)
	for sgn in (1, -1):
		for z in LAMP_Z:
			l = lamp.copy()
			l.data = lamp.data
			bpy.context.scene.collection.objects.link(l)
			l.location = (LAMP_X, sgn * (b_hi + 0.032), z)
			l.rotation_euler = (math.radians(-90 if sgn > 0 else 90), 0, 0)
	lamp.hide_render = True
	sun = bpy.data.lights.new("Sun", 'SUN')
	sun.energy = 3.0
	so = bpy.data.objects.new("Sun", sun)
	bpy.context.scene.collection.objects.link(so)
	so.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
	preview(pod, door, lamp, hinge, "closed_front34", 0, (3.4, -2.6, 1.9), (0, 0, 1.25))
	preview(pod, door, lamp, hinge, "open_front34", 115, (3.4, -2.6, 1.9), (0, 0, 1.25))
	preview(pod, door, lamp, hinge, "open_inside", 115, (2.2, 0.3, 1.5), (-0.3, 0, 1.25))
	preview(pod, door, lamp, hinge, "back34", 0, (-3.2, 2.4, 1.9), (0, 0, 1.25))
	preview(pod, door, lamp, hinge, "side", 0, (0.0, -4.0, 1.3), (0, 0, 1.25), ortho=True)
	door.rotation_euler = (0, 0, 0)

	blend_dir = os.path.join(ROOT, "Blender", "blenderprojects")
	os.makedirs(blend_dir, exist_ok=True)
	blend = os.path.join(blend_dir, "stasis_pod.blend")
	bpy.ops.wm.save_as_mainfile(filepath=blend)
	print("saved", blend)


if __name__ == "__main__":
	main()
