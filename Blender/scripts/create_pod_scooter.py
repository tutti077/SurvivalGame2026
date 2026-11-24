"""Pod scooter: the small folding moped every stasis pod carries (Mark 2026-11-19: "a kinda small moped scooter inside
the pod all folded up ... cyberpunk themed military grade (simple and effective but not fancy)").

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_pod_scooter.py

Writes Assets/models/vehicles/pod_scooter/:
    pod_scooter.fbx / .vmdl            the frame: step-through deck, leg shield + head yoke, tube seat frame, saddle,
                                       the fibre reactor under the seat (the fuel hopper), rear rack, mudguard, tail lamp,
                                       whip aerial. Origin = the chassis reference the Vehicle springs hang from: wheel
                                       hubs sit at z 0 when fully compressed. `pod_scooter_phys` = one convex hull.
    pod_scooter_wheel.fbx / .vmdl      one fat knobby wheel, axle = Y, origin = hub (both wheels use it)
    pod_scooter_steer.fbx / .vmdl      handlebar, column, clamp, fork tubes, headlamp, number plate. Origin on the
                                       steering axis at chassis height (the prefab's `Steer` child yaws with the steering)
    pod_scooter_fork.fbx / .vmdl       fork sliders + axle + front mudguard. Origin = the front hub (the prefab's
                                       `ForkLower` child rides the front spring)
    pod_scooter_swingarm.fbx / .vmdl   rear swing arm, origin = its pivot, tail (-X) at the rear hub (`SwingArm` child)
    pod_scooter_folded.fbx / .vmdl     the folded package: both wheels side by side on one axle, the deck stood on end
                                       beside them, saddle outside, handlebar across the top, two hazard straps. Origin
                                       = bottom centre. Docked under the pod seat, and the item's world-drop model.
    pod_scooter_atlas.png / .vmat      256 px atlas of 32 px cells (pixel_lit, point sampled, flat shaded)
plus Assets/prefabs/build/vehicle_pod_scooter.prefab (the Vehicle with DriveModel = Bike and every tuning number),
Assets/ui/items/vehicle_pod_scooter_folded.png (64 px icon, drawn from the folded silhouette) and previews in
Blender/blenderprojects/pod_scooter_preview/.

Blender metres, exported 1:1, vmdls use import_scale 0.4 (40 u/m, CLAUDE.md commandment 5). Modelled forward = +X,
left = +Y; the export quarter turn makes that the engine's +X / +Y (same as create_dune_buggy.py).
"""
import bpy, math, os, struct, zlib, uuid, json
import numpy as np
from mathutils import Vector, Matrix

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
ASSET_REL = "models/vehicles/pod_scooter"
OUT_DIR = os.path.join(ROOT, "Assets", "models", "vehicles", "pod_scooter")
PREFAB_PATH = os.path.join(ROOT, "Assets", "prefabs", "build", "vehicle_pod_scooter.prefab")
ICON_PATH = os.path.join(ROOT, "Assets", "ui", "items", "vehicle_pod_scooter_folded.png")
PREVIEW_DIR = os.path.join(ROOT, "Blender", "blenderprojects", "pod_scooter_preview")
ATLAS = "pod_scooter_atlas"
BODY, WHEEL, STEER, FORK, ARM, FOLDED = ("pod_scooter", "pod_scooter_wheel", "pod_scooter_steer", "pod_scooter_fork",
										 "pod_scooter_swingarm", "pod_scooter_folded")
UNITS_PER_M = 40.0

# ---------------------------------------------------------------- dimensions (metres, chassis frame)
WHEEL_R = 0.21
WHEEL_W = 0.11
HALF_BASE = 0.55            # hubs at x +-0.55 (wheel base 1.10)
SPRING = 0.16               # spring travel below the mount (hub z 0 = fully compressed, -0.16 = full droop)
REST_DROP = 0.08            # where the hub sits at rest (the previews pose the wheels here)
PIVOT = Vector((-0.20, 0.0, -0.06))   # swing arm pivot
SEAT_TOP = 0.43
SEAT_X = -0.22
# the reactor (fuel hopper) box under the saddle: centre + size, also the prefab's `vehiclestorage` collider
HOPPER_C = Vector((-0.20, 0.0, 0.14))
HOPPER_S = Vector((0.24, 0.22, 0.38))
# folded package envelope (model frame: long axis X, thickness Y, origin bottom centre)
FOLD_SIZE = Vector((0.54, 0.26, 0.46))

# ---------------------------------------------------------------- atlas
TEX = 256
CELL = 32
# name -> (col, row, span cells)
CELLS = {
	"olive": (0, 0, 1), "olive_dark": (1, 0, 1), "gunmetal": (2, 0, 1), "steel": (3, 0, 1),
	"rubber": (4, 0, 1), "tread": (5, 0, 1), "seat": (6, 0, 1), "grip": (7, 0, 1),
	"hazard": (0, 1, 1), "cyan": (1, 1, 1), "red": (2, 1, 1), "lamp": (3, 1, 1),
	"stencil": (4, 1, 2), "reactor": (6, 1, 2),
	"rim": (0, 2, 1), "rimslot": (1, 2, 1), "display": (2, 2, 1), "glow": (3, 2, 1),
}
BASE = {
	"olive": (86, 96, 66), "olive_dark": (58, 66, 48), "gunmetal": (66, 70, 76), "steel": (142, 146, 152),
	"rubber": (30, 30, 33), "tread": (36, 36, 40), "seat": (34, 34, 38), "grip": (48, 50, 52),
	"hazard": (226, 178, 36), "cyan": (70, 230, 255), "red": (232, 60, 50), "lamp": (236, 240, 226),
	"stencil": (86, 96, 66), "reactor": (66, 70, 76),
	"rim": (110, 114, 104), "rimslot": (24, 24, 26), "display": (16, 30, 34), "glow": (120, 240, 120),
}


def write_png(path, rgba):
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

	png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
		   + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
	with open(path, "wb") as f:
		f.write(png)


# 3x5 stencil glyphs for the plate text
GLYPHS = {
	"P": ["111", "101", "111", "100", "100"], "S": ["111", "100", "111", "001", "111"],
	"0": ["111", "101", "101", "101", "111"], "7": ["111", "001", "001", "010", "100"],
	"-": ["000", "000", "111", "000", "000"],
}


def make_atlas():
	img = np.zeros((TEX, TEX, 4), np.uint8)
	img[..., 3] = 255
	rng = np.random.default_rng(11)
	for name, (cx, cy, span) in CELLS.items():
		size = CELL * span
		base = np.array(BASE[name], float)
		block = np.repeat(np.repeat(base[None, None, :], size, 0), size, 1)
		yy, xx = np.mgrid[0:size, 0:size]
		if name in ("olive", "olive_dark", "stencil"):
			# painted plate: soft mottling in 4 px blotches, a darker seam line near two edges, a few paint chips
			blot = rng.integers(-7, 8, (size // 4, size // 4, 1)).repeat(4, 0).repeat(4, 1)
			block += blot
			block[1, :, :] -= 20
			block[:, 1, :] -= 20
			block[size - 2, :, :] += 10
			for _ in range(5 * span):
				px, py = rng.integers(3, size - 3, 2)
				block[py, px:px + 2] = (128, 130, 124)
		if name == "stencil":
			text = "PS-07"
			scale = 3
			x0 = (size - (len(text) * 4 - 1) * scale) // 2
			y0 = (size - 5 * scale) // 2
			for i, ch in enumerate(text):
				for gy, row in enumerate(GLYPHS[ch]):
					for gx, bit in enumerate(row):
						if bit == "1":
							px, py = x0 + (i * 4 + gx) * scale, y0 + gy * scale
							block[py:py + scale, px:px + scale] = (214, 214, 196)
			block[y0 + 5 * scale + 4:y0 + 5 * scale + 6, x0:x0 + 30] = (226, 178, 36)
		if name in ("gunmetal", "steel", "rim"):
			block += rng.integers(-5, 6, (size, 1, 1))          # brushed along U
			block[::8, :, :] -= 6
		if name == "rubber":
			block += rng.integers(-3, 4, (size, size, 1))
			block[10:12, :, :] += 14                              # a moulded sidewall ring
		if name == "tread":
			# chevron lugs: dark grooves between raised blocks
			lug = ((xx + np.abs(yy - size / 2).astype(int)) % 8) < 5
			block[lug] += 16
			block[~lug] -= 12
		if name == "seat":
			block += rng.integers(-3, 4, (size, size, 1))
			block[:, ::8, :] -= 10                                # stitched ribs
		if name == "grip":
			diamond = ((xx + yy) % 6 < 2) | ((xx - yy) % 6 < 2)
			block[diamond] += 22
		if name == "hazard":
			stripe = ((xx + yy) % 16) < 8
			block[~stripe] = (28, 28, 30)
		if name in ("cyan", "red", "lamp", "glow"):
			block[0, :, :] *= 0.6
			block[-1, :, :] *= 0.6
			block[:, 0, :] *= 0.6
			block[:, -1, :] *= 0.6
		if name == "reactor":
			# canister face: bolted frame, vent slats, a green sight glass, a hazard tag
			block[:3, :, :] -= 22
			block[-3:, :, :] -= 22
			block[:, :3, :] -= 22
			block[:, -3:, :] -= 22
			for bx, by in ((5, 5), (size - 7, 5), (5, size - 7), (size - 7, size - 7)):
				block[by:by + 2, bx:bx + 2] = (150, 154, 160)
			for row in range(12, 30, 4):
				block[row:row + 2, 10:size - 10] = (26, 28, 30)
			block[36:52, 12:28] = (34, 60, 40)
			block[38:50, 14:26] = (120, 240, 120)
			block[42:44, 14:26] = (190, 255, 190)
			tag = ((xx + yy) % 8) < 4
			tagzone = np.zeros((size, size), bool)
			tagzone[38:50, 34:54] = True
			block[tagzone & tag] = (226, 178, 36)
			block[tagzone & ~tag] = (28, 28, 30)
		if name == "display":
			block[6:8, 5:27] = (70, 230, 255)
			block[12:14, 5:19] = (70, 230, 255)
			block[18:24, 5:11] = (120, 240, 120)
			block[18:24, 14:27] = (226, 178, 36)
		x0, y0 = cx * CELL, cy * CELL
		img[y0:y0 + size, x0:x0 + size, :3] = np.clip(block, 0, 255).astype(np.uint8)
	return img


def cell_uv(name, fx=0.5, fy=0.5):
	cx, cy, span = CELLS[name]
	size = CELL * span
	# half a texel inside the cell so point sampling never bleeds into a neighbour
	fx = min(max(fx, 0.5 / size), 1 - 0.5 / size)
	fy = min(max(fy, 0.5 / size), 1 - 0.5 / size)
	return ((cx * CELL + fx * size) / TEX, 1.0 - (cy * CELL + fy * size) / TEX)


def quad_uvs(name):
	return [cell_uv(name, 0, 1), cell_uv(name, 1, 1), cell_uv(name, 1, 0), cell_uv(name, 0, 0)]


# ---------------------------------------------------------------- mesh builder
class Mesh:
	"""Faceted polygon soup. Every face is wound to face away from a reference point the primitive supplies, so no
	primitive can come out inside-out. `xf` is applied to everything added while it is set (the folded package
	reuses the wheel builder that way)."""

	def __init__(self):
		self.verts, self.faces, self.uvs = [], [], []
		self.xf = Matrix.Identity(4)

	def face(self, pts, uvs, away_from):
		pts = [Vector(p) for p in pts]
		if len(pts) < 3:
			return
		n = (pts[1] - pts[0]).cross(pts[2] - pts[0])
		c = sum(pts, Vector()) / len(pts)
		if n.dot(c - Vector(away_from)) < 0:
			pts.reverse()
			uvs = list(reversed(uvs))
		base = len(self.verts)
		self.verts.extend(self.xf @ p for p in pts)
		self.faces.append(tuple(range(base, base + len(pts))))
		self.uvs.append([tuple(u) for u in uvs])

	def to_object(self, name, mat):
		me = bpy.data.meshes.new(name)
		me.from_pydata([v[:] for v in self.verts], [], self.faces)
		me.update()
		uv = me.uv_layers.new(name="UVMap")
		for poly, face_uvs in zip(me.polygons, self.uvs):
			poly.use_smooth = False
			for li, u in zip(poly.loop_indices, face_uvs):
				uv.data[li].uv = u
		me.materials.append(mat)
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def obox(m, centre, ax, ay, az, half, cell, top=None, taper=1.0):
	"""Oriented box: unit axes ax / ay / az, half sizes along them. `top` = another cell for the +az face,
	`taper` shrinks the +az face (a cushion)."""
	c = Vector(centre)
	ax, ay, az = Vector(ax), Vector(ay), Vector(az)
	hx, hy, hz = half

	def p(sx, sy, sz):
		k = taper if sz > 0 else 1.0
		return c + ax * (sx * hx * k) + ay * (sy * hy * k) + az * (sz * hz)

	quads = [
		((p(-1, -1, 1), p(1, -1, 1), p(1, 1, 1), p(-1, 1, 1)), top or cell),
		((p(-1, -1, -1), p(1, -1, -1), p(1, 1, -1), p(-1, 1, -1)), cell),
		((p(-1, -1, -1), p(1, -1, -1), p(1, -1, 1), p(-1, -1, 1)), cell),
		((p(-1, 1, -1), p(1, 1, -1), p(1, 1, 1), p(-1, 1, 1)), cell),
		((p(-1, -1, -1), p(-1, 1, -1), p(-1, 1, 1), p(-1, -1, 1)), cell),
		((p(1, -1, -1), p(1, 1, -1), p(1, 1, 1), p(1, -1, 1)), cell),
	]
	for pts, cl in quads:
		m.face(pts, quad_uvs(cl), c)


def box(m, centre, size, cell, top=None, taper=1.0):
	obox(m, centre, (1, 0, 0), (0, 1, 0), (0, 0, 1), (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), cell, top, taper)


def beam(m, a, b, width, thick, cell, top=None):
	"""A flat bar from a to b: `width` across (the axis most square to the bar and to world Y, else Y itself),
	`thick` through."""
	a, b = Vector(a), Vector(b)
	d = (b - a)
	length = d.length
	d.normalize()
	side = Vector((0, 1, 0))
	if abs(d.dot(side)) > 0.9:
		side = Vector((1, 0, 0))
	n = d.cross(side).normalized()
	side = n.cross(d).normalized()
	obox(m, (a + b) * 0.5, d, side, n, (length * 0.5, width * 0.5, thick * 0.5), cell, top)


def tube(m, a, b, r, cell, segs=6, r2=None):
	a, b = Vector(a), Vector(b)
	d = (b - a).normalized()
	side = d.cross(Vector((0, 0, 1)))
	if side.length < 1e-4:
		side = d.cross(Vector((1, 0, 0)))
	side.normalize()
	nrm = side.cross(d).normalized()
	r2 = r if r2 is None else r2
	ra = [a + (side * math.cos(2 * math.pi * k / segs) + nrm * math.sin(2 * math.pi * k / segs)) * r for k in range(segs)]
	rb = [b + (side * math.cos(2 * math.pi * k / segs) + nrm * math.sin(2 * math.pi * k / segs)) * r2 for k in range(segs)]
	mid = (a + b) * 0.5
	for k in range(segs):
		j = (k + 1) % segs
		m.face([ra[k], ra[j], rb[j], rb[k]], quad_uvs(cell), mid)
	m.face(ra, [cell_uv(cell)] * segs, mid)
	m.face(rb, [cell_uv(cell)] * segs, mid)


def arc_plate(m, centre, radius, a0, a1, width, thick, cell, segs=5):
	"""A curved strip in the XZ plane around `centre` (a mudguard): angles in degrees from +X toward +Z."""
	c = Vector(centre)
	for i in range(segs):
		t0 = math.radians(a0 + (a1 - a0) * i / segs)
		t1 = math.radians(a0 + (a1 - a0) * (i + 1) / segs)
		p0 = c + Vector((math.cos(t0), 0, math.sin(t0))) * radius
		p1 = c + Vector((math.cos(t1), 0, math.sin(t1))) * radius
		beam(m, p0, p1, width, thick, cell)


def wheel(m, centre=(0, 0, 0), segs=12):
	"""Knobby tyre on a slotted disc rim, lathed around Y through `centre`."""
	c = Vector(centre)
	w = WHEEL_W * 0.5
	# (radius, y, cell of the band that ENDS at this point)
	profile = [
		(0.045, -w * 0.75, None), (0.135, -w * 0.62, "rim"), (0.150, -w * 0.95, "rubber"), (0.185, -w, "rubber"),
		(WHEEL_R, -w * 0.68, "rubber"), (WHEEL_R, w * 0.68, "tread"), (0.185, w, "rubber"), (0.150, w * 0.95, "rubber"),
		(0.135, w * 0.62, "rubber"), (0.045, w * 0.75, "rim"),
	]

	def pt(r, y, k):
		t = 2 * math.pi * k / segs
		return c + Vector((r * math.cos(t), y, r * math.sin(t)))

	for k in range(segs):
		j = k + 1
		tm = 2 * math.pi * (k + 0.5) / segs
		core = c + Vector((0.14 * math.cos(tm), 0, 0.14 * math.sin(tm)))   # inside the tyre's cross-section
		for (r0, y0, _), (r1, y1, cell) in zip(profile, profile[1:]):
			if cell == "rim" and k % 3 == 2:
				cell = "rimslot"                                              # every third segment is a dark slot
			m.face([pt(r0, y0, k), pt(r0, y0, j), pt(r1, y1, j), pt(r1, y1, k)], quad_uvs(cell), core)
	# hub
	tube(m, c + Vector((0, -w * 0.9, 0)), c + Vector((0, w * 0.9, 0)), 0.05, "steel", segs=8)


# ---------------------------------------------------------------- the parts
def build_body(m):
	# step-through deck: painted pan, diamond grip mat on top, hazard edge strips, skid plate under
	box(m, (0.04, 0, -0.075), (0.52, 0.30, 0.05), "olive")
	box(m, (0.04, 0, -0.046), (0.46, 0.24, 0.008), "grip")
	for sy in (1, -1):
		box(m, (0.04, sy * 0.153, -0.075), (0.50, 0.008, 0.028), "hazard")
		box(m, (0.21, sy * 0.158, -0.060), (0.10, 0.006, 0.012), "cyan")
	box(m, (0.02, 0, -0.108), (0.40, 0.20, 0.016), "gunmetal")
	# leg shield up to the head yoke, with the stencilled unit plate on its front
	beam(m, (0.30, 0, -0.06), (0.45, 0, 0.30), 0.20, 0.028, "olive")
	beam(m, (0.335, 0, 0.00), (0.445, 0, 0.26), 0.15, 0.006, "stencil")
	for sy in (1, -1):
		tube(m, (0.27, sy * 0.115, -0.06), (0.47, sy * 0.075, 0.33), 0.016, "gunmetal")
	box(m, (0.52, 0, 0.33), (0.17, 0.19, 0.04), "gunmetal")
	tube(m, (HALF_BASE, 0, 0.30), (HALF_BASE, 0, 0.37), 0.038, "steel", segs=8)
	# seat frame: two hoops off the deck, rails running back into the rack
	for sy in (1, -1):
		tube(m, (0.00, sy * 0.13, -0.05), (-0.04, sy * 0.12, 0.34), 0.014, "gunmetal")
		tube(m, (-0.21, sy * 0.13, -0.05), (-0.36, sy * 0.12, 0.34), 0.014, "gunmetal")
		tube(m, (-0.04, sy * 0.12, 0.34), (-0.70, sy * 0.11, 0.34), 0.014, "gunmetal")
		tube(m, (-0.70, sy * 0.11, 0.34), (-0.75, sy * 0.05, 0.285), 0.010, "gunmetal")
	tube(m, (-0.70, -0.11, 0.34), (-0.70, 0.11, 0.34), 0.014, "gunmetal")
	tube(m, (-0.50, -0.11, 0.34), (-0.50, 0.11, 0.34), 0.012, "gunmetal")
	# saddle
	box(m, (SEAT_X, 0, SEAT_TOP - 0.035), (0.42, 0.27, 0.07), "seat", taper=0.9)
	# fibre reactor under the saddle (the fuel hopper): canister faces on the sides, feed chute on the right
	box(m, HOPPER_C, (HOPPER_S.x, HOPPER_S.y - 0.02, HOPPER_S.z), "reactor", top="gunmetal")
	box(m, (HOPPER_C.x + 0.02, -HOPPER_S.y * 0.5 - 0.005, 0.26), (0.14, 0.03, 0.10), "hazard", top="gunmetal")
	box(m, (HOPPER_C.x + 0.02, -HOPPER_S.y * 0.5 - 0.022, 0.26), (0.10, 0.006, 0.06), "rimslot")
	tube(m, (-0.31, 0.06, 0.22), (-0.43, 0.085, 0.31), 0.022, "steel", r2=0.026)   # exhaust stub
	# rack plate, rear mudguard, tail lamp
	box(m, (-0.58, 0, 0.352), (0.22, 0.20, 0.010), "gunmetal")
	box(m, (-0.565, 0, 0.278), (0.40, 0.13, 0.014), "olive_dark")
	box(m, (-0.770, 0, 0.278), (0.012, 0.10, 0.03), "red")
	# swing arm pivot boss
	tube(m, PIVOT + Vector((0, -0.105, 0)), PIVOT + Vector((0, 0.105, 0)), 0.028, "steel", segs=8)
	# whip aerial with a status tip
	tube(m, (-0.66, 0.11, 0.34), (-0.69, 0.11, 0.80), 0.005, "gunmetal", segs=4)
	box(m, (-0.69, 0.11, 0.81), (0.016, 0.016, 0.03), "cyan")


def build_body_phys(m):
	"""Points of the single convex hull: the deck pan, the saddle, the handlebar ends and the rack tail."""
	box(m, (0.02, 0, -0.06), (0.86, 0.32, 0.11), "gunmetal")            # deck + skid plate, a little long
	box(m, (SEAT_X, 0, SEAT_TOP - 0.03), (0.42, 0.26, 0.06), "gunmetal")
	box(m, (HALF_BASE, 0, 0.70), (0.04, 0.56, 0.04), "gunmetal")       # handlebar
	box(m, (-0.72, 0, 0.31), (0.08, 0.22, 0.08), "gunmetal")           # rack tail


def build_steer(m):
	"""Frame: origin = (HALF_BASE, 0, 0) in the chassis, on the steering axis."""
	box(m, (0, 0, 0.283), (0.07, 0.22, 0.028), "gunmetal")              # lower clamp, under the head yoke
	tube(m, (0, 0, 0.37), (0, 0, 0.685), 0.022, "gunmetal", segs=8)
	box(m, (0, 0, 0.690), (0.05, 0.09, 0.03), "steel")
	tube(m, (0, -0.27, 0.70), (0, 0.27, 0.70), 0.015, "gunmetal")
	for sy in (1, -1):
		tube(m, (0, sy * 0.19, 0.70), (0, sy * 0.30, 0.70), 0.022, "rubber", segs=8)
		tube(m, (0.035, sy * 0.15, 0.702), (0.05, sy * 0.27, 0.706), 0.006, "steel", segs=4)
		tube(m, (0, sy * 0.085, 0.29), (0, sy * 0.085, -0.02), 0.014, "steel")
	# instrument slab, tilted toward the rider
	obox(m, (-0.035, 0, 0.735), (0.8, 0, 0.6), (0, 1, 0), (-0.6, 0, 0.8), (0.03, 0.055, 0.010), "gunmetal", top="display")
	# headlamp bar + number plate
	box(m, (0.050, 0, 0.42), (0.05, 0.17, 0.075), "gunmetal")
	box(m, (0.078, 0, 0.42), (0.008, 0.14, 0.048), "lamp")
	box(m, (0.045, 0, 0.545), (0.012, 0.15, 0.13), "stencil")


def build_fork(m):
	"""Frame: origin = the front hub."""
	for sy in (1, -1):
		tube(m, (0, sy * 0.085, -0.02), (0, sy * 0.085, 0.19), 0.021, "olive_dark", segs=8)
	tube(m, (0, -0.108, 0), (0, 0.108, 0), 0.012, "steel")
	arc_plate(m, (0, 0, 0), WHEEL_R + 0.028, 20, 160, 0.12, 0.012, "olive_dark", segs=6)


def build_arm(m):
	"""Frame: origin = the pivot; the tail at -0.35 carries the rear hub."""
	tail = HALF_BASE + PIVOT.x     # 0.35
	for sy in (1, -1):
		beam(m, (0, sy * 0.085, 0), (-tail - 0.03, sy * 0.085, 0), 0.045, 0.016, "gunmetal")
	tube(m, (-0.07, -0.085, 0), (-0.07, 0.085, 0), 0.014, "gunmetal")
	tube(m, (-tail, -0.108, 0), (-tail, 0.108, 0), 0.012, "steel")
	# hub motor on the left, belt guard on the right
	tube(m, (-tail, 0.058, 0), (-tail, 0.082, 0), 0.075, "steel", segs=10)
	box(m, (-tail * 0.55, -0.098, 0.012), (tail * 0.9, 0.010, 0.07), "olive_dark")


def build_folded(m):
	"""Frame: X long axis, Y thickness, origin bottom centre. Wheels side by side on one axle, the deck on end."""
	hub = Vector((-0.05, 0, WHEEL_R + 0.004))
	for sy in (1, -1):
		wheel(m, hub + Vector((0, sy * (WHEEL_W * 0.5 + 0.004), 0)))
	tube(m, hub + Vector((0, -0.122, 0)), hub + Vector((0, 0.122, 0)), 0.028, "steel", segs=8)
	# deck stood on end, grip mat outward, the saddle strapped to it
	box(m, (0.185, 0, 0.225), (0.034, 0.23, 0.42), "olive")
	box(m, (0.204, 0, 0.225), (0.006, 0.19, 0.36), "grip")
	box(m, (0.236, 0, 0.20), (0.05, 0.20, 0.28), "seat", taper=0.92)
	# fork legs laid diagonally on both faces, handlebar across the top with its grips
	for sy in (1, -1):
		tube(m, (-0.24, sy * 0.121, 0.07), (0.13, sy * 0.121, 0.37), 0.011, "steel")
		box(m, (0.185, sy * 0.119, 0.225), (0.05, 0.008, 0.40), "olive_dark")
	tube(m, (-0.21, 0, 0.440), (0.21, 0, 0.440), 0.015, "gunmetal")
	for sx in (1, -1):
		tube(m, (sx * 0.15, 0, 0.440), (sx * 0.25, 0, 0.440), 0.021, "rubber", segs=8)
	# two hazard straps round the pair of wheels, a clasp and a status lamp
	for x in (-0.12, 0.02):
		box(m, (x, 0, hub.z), (0.016, 0.250, 2 * WHEEL_R + 0.010), "gunmetal")
	box(m, (-0.05, 0, hub.z + WHEEL_R + 0.004), (0.20, 0.250, 0.010), "hazard")
	box(m, (-0.05, 0, 0.436), (0.07, 0.06, 0.022), "gunmetal")
	box(m, (0.263, 0, 0.375), (0.006, 0.07, 0.022), "cyan")
	box(m, (0.263, 0, 0.06), (0.006, 0.15, 0.05), "stencil")


# ---------------------------------------------------------------- vmdl / vmat
def vmdl_text(name, phys=None):
	phys_txt = ""
	if phys:
		phys_txt = f"""
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsHullFile"
						filename = "{ASSET_REL}/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						surface_prop = "metal"
						collision_tags = "solid"
						faceMergeAngle = 10.0
						maxHullVertices = 32
						hull_mode = "SingleHull"
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
						[
							{{
								from = "{ATLAS}.vmat"
								to = "{ASSET_REL}/{ATLAS}.vmat"
							}},
							{{
								from = "{ATLAS}"
								to = "{ASSET_REL}/{ATLAS}.vmat"
							}},
						]
						use_global_default = false
						global_default_material = "{ASSET_REL}/{ATLAS}.vmat"
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
						filename = "{ASSET_REL}/{name}.fbx"
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
							exception_list = [ "{name}" ]
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


VMAT = f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.700"
	PixelSpecular "1.000"
	PixelFlatShading "1.000"
	TextureColor "{ASSET_REL}/{ATLAS}.png"
}}
"""

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
	with open(os.path.join(OUT_DIR, name + ".vmdl"), "w", newline="\n") as f:
		f.write(vmdl_text(name, phys=objs[1].name if len(objs) > 1 else None))


# ---------------------------------------------------------------- prefab
def guid(tag):
	return str(uuid.uuid5(uuid.UUID("7a1c2e3f-4b5d-4c6e-9f70-812a3b4c5d6e"), "pod_scooter/" + tag))


def eng(v):
	v = Vector(v)
	return f"{v.x * UNITS_PER_M:.3f},{v.y * UNITS_PER_M:.3f},{v.z * UNITS_PER_M:.3f}"


HOOKS = {"OnComponentDestroy": None, "OnComponentDisabled": None, "OnComponentEnabled": None,
		 "OnComponentFixedUpdate": None, "OnComponentStart": None, "OnComponentUpdate": None}
TRIGGER_HOOKS = {"OnObjectTriggerEnter": None, "OnObjectTriggerExit": None, "OnTriggerEnter": None, "OnTriggerExit": None}


def model_renderer(gid, model):
	return {"__type": "Sandbox.ModelRenderer", "__guid": gid, "__enabled": True, "Flags": 0,
			"BodyGroups": 18446744073709551615, "CreateAttachments": False, "LodOverride": None, "MaterialGroup": None,
			"MaterialOverride": None, "Materials": None, "Model": model, **HOOKS,
			"RenderOptions": {"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
			"RenderType": "On", "Tint": "1,1,1,1"}


def child(name, pos, components, tags=""):
	return {"__guid": guid("child/" + name), "__version": 2, "Flags": 0, "Name": name, "Position": pos,
			"Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": tags, "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0,
			"NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1, "Components": components, "Children": []}


def part(name, model, pos):
	return child(name, eng(pos), [model_renderer(guid(name + "/renderer"), f"{ASSET_REL}/{model}.vmdl")])


def write_prefab():
	"""Every tuning number lives here (the prefab bakes them; a code default alone does nothing). Metres, m/s, Hz."""
	vehicle = {
		"__type": "Survival.Vehicle", "__guid": guid("vehicle"), "__enabled": True, "Flags": 0,
		"DisplayName": "Pod Scooter", "DriveModel": "Bike", "StowItemId": "vehicle_pod_scooter_folded",
		# 60 mph = 26.8 m/s (Mark 2026-11-19)
		"MaxSpeedMetersPerSecond": 26.8, "ReverseSpeedMetersPerSecond": 3, "AccelerationMetersPerSecond2": 6,
		"BrakeDecelerationMetersPerSecond2": 9, "RollingDragMetersPerSecond2": 2.5, "OutOfFuelCoastDragMetersPerSecond2": 1.2,
		"TyreGripCoefficient": 1.1, "TyreSlideGripCoefficient": 0.9, "TyrePeakSlipDegrees": 10, "TyreGripBiasFront": 0.48,
		"MaxSteerDegrees": 34, "SteerSpeedDegreesPerSecond": 140, "SteerSlipAssistFraction": 0.8,
		"WheelBaseMeters": HALF_BASE * 2, "TrackWidthMeters": 0, "WheelRadiusMeters": WHEEL_R,
		"SuspensionLengthMeters": SPRING, "SuspensionFrequencyHz": 2.6, "SuspensionDampingRatio": 0.8, "SuspensionBumpStopMultiple": 4,
		"BikeBalanceFrequencyHz": 2.5, "BikeBalanceDampingRatio": 1, "BikeMaxLeanDegrees": 32, "BikeLeanResponse": 6,
		"AirPitchRateDegreesPerSecond": 90, "AirRollRateDegreesPerSecond": 60, "AirControlResponse": 2, "BikeRightingSeconds": 1.5,
		"UseReachMeters": 3, "ExitSideMeters": 1.2,
		"FlipCooldownSeconds": 1, "FlipPushLiftFraction": 0.5, "FlipPushSeconds": 0.3, "FlipPushWeightMultiple": 0.6,
		# 5 plant fiber per minute of throttle = 12 s a unit
		"FuelResourceId": "resource_plantFiber", "FuelLabel": "Fiber", "SecondsPerFuelUnit": 12,
		"MaxHealth": 60, "BuildableDeck": False, "MapPinIcon": "", **HOOKS,
	}
	root = {
		"RootObject": {
			"__guid": guid("root"), "__version": 2, "Flags": 0, "Name": "vehicle_pod_scooter",
			"Position": "0,0,0", "Rotation": "0,0,0,1", "Scale": "1,1,1", "Tags": "vehicle,grapple", "Enabled": True,
			"NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0, "NetworkTransmit": True, "OwnerTransfer": 1,
			"Components": [
				{"__type": "Sandbox.Rigidbody", "__guid": guid("body"), "__enabled": True, "Flags": 0,
				 "AngularDamping": 0.3, "EnableImpactDamage": False, "EnhancedCcd": False, "Gravity": True, "GravityScale": 1,
				 "ImpactDamage": 0, "LinearDamping": 0.05,
				 "Locking": {"Pitch": False, "Yaw": False, "Roll": False, "X": False, "Y": False, "Z": False},
				 "MassCenterOverride": "0,0,2", "MassOverride": 140, "MinImpactDamageSpeed": 0, "MotionEnabled": True, **HOOKS,
				 "OverrideMassCenter": True, "RigidbodyFlags": 0, "SleepThreshold": 0, "StartAsleep": False},
				{"__type": "Sandbox.ModelCollider", "__guid": guid("collider"), "__enabled": True, "Flags": 0,
				 "ColliderFlags": 0, "Elasticity": None, "Friction": 0.6, "IsTrigger": False, "Model": f"{ASSET_REL}/{BODY}.vmdl",
				 **HOOKS, **TRIGGER_HOOKS, "RollingResistance": None, "Static": False, "Surface": None, "SurfaceVelocity": "0,0,0"},
				vehicle,
				# one slot, plant fiber only: the tank. No storage grid (Mark: "just be 1 slot for fuel").
				{"__type": "Survival.ContainerInventory", "__guid": guid("tank"), "__enabled": True, "Flags": 0,
				 "Columns": 1, "DisplayName": "Fiber Hopper", **HOOKS, "RestrictedSlotIndex": 0, "RestrictedSlotLabel": "FIBER",
				 "RestrictedSlotResourceId": "resource_plantFiber", "SlotCount": 1},
				{"__type": "Survival.DamageReceiver", "__guid": guid("damage"), "__enabled": True, "Flags": 0,
				 "DamageMultiplier": 1, **HOOKS},
			],
			"Children": [
				part("Body", BODY, (0, 0, 0)),
				part("WheelF", WHEEL, (HALF_BASE, 0, -REST_DROP)),
				part("WheelR", WHEEL, (-HALF_BASE, 0, -REST_DROP)),
				part("Steer", STEER, (HALF_BASE, 0, 0)),
				part("ForkLower", FORK, (HALF_BASE, 0, -REST_DROP)),
				part("SwingArm", ARM, PIVOT),
				# the chair sit puts the pelvis ~0.45 m above the pawn root: root just under the deck top, feet on the mat
				child("Seat_Driver", eng((SEAT_X + 0.02, 0, SEAT_TOP - 0.46)), [
					{"__type": "Survival.VehicleSeat", "__guid": guid("seat"), "__enabled": True, "Flags": 0, "SeatIndex": 0, "Standing": False, **HOOKS}]),
				# the reactor under the saddle: E on it opens the one-slot hopper, E anywhere else rides
				child("Hopper", eng(HOPPER_C), [
					{"__type": "Sandbox.BoxCollider", "__guid": guid("hopper/box"), "__enabled": True, "Flags": 0,
					 "Center": "0,0,0", "ColliderFlags": 0, "Elasticity": None, "Friction": None, "IsTrigger": False, **HOOKS, **TRIGGER_HOOKS,
					 "RollingResistance": None, "Scale": eng(HOPPER_S + Vector((0.02, 0.04, 0.0))), "Static": False, "Surface": None,
					 "SurfaceVelocity": "0,0,0"}], tags="vehiclestorage"),
			],
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
	with open(PREFAB_PATH, "w", newline="\n") as f:
		json.dump(root, f, indent=2)
		f.write("\n")


# ---------------------------------------------------------------- previews + icon
def render(tag, cam_from, look_at, res=1100, ortho=None, transparent=False):
	scene = bpy.context.scene
	scene.render.engine = 'BLENDER_WORKBENCH'
	scene.display.shading.light = 'STUDIO'
	scene.display.shading.color_type = 'TEXTURE'
	scene.display.shading.show_shadows = True
	scene.display.shading.show_cavity = True
	scene.display.shading.show_backface_culling = True   # an inside-out face shows as a hole, like the engine
	scene.render.resolution_x = res
	scene.render.resolution_y = res
	scene.render.film_transparent = transparent
	scene.world = scene.world or bpy.data.worlds.new("World")
	scene.world.color = (0.18, 0.2, 0.24)
	cam = bpy.data.objects.get("PreviewCam")
	if cam is None:
		cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
		scene.collection.objects.link(cam)
	cam.data.type = 'ORTHO' if ortho else 'PERSP'
	cam.data.lens = 50
	if ortho:
		cam.data.ortho_scale = ortho
	cam.location = Vector(cam_from)
	cam.rotation_euler = (Vector(look_at) - cam.location).to_track_quat('-Z', 'Y').to_euler()
	scene.camera = cam
	os.makedirs(PREVIEW_DIR, exist_ok=True)
	path = os.path.join(PREVIEW_DIR, f"pod_scooter_{tag}.png")
	scene.render.filepath = path
	bpy.ops.render.render(write_still=True)
	print("preview", path)
	return path


def write_icon(src):
	"""64 px item icon: the transparent folded render, point-reduced so it keeps hard pixel edges."""
	img = bpy.data.images.load(src)
	w, h = img.size
	px = np.array(img.pixels[:], np.float32).reshape(h, w, 4)[::-1]
	step = w // 64
	small = px[step // 2::step, step // 2::step][:64, :64]
	out = (np.clip(small, 0, 1) * 255).astype(np.uint8)
	out[..., 3] = np.where(out[..., 3] > 127, 255, 0)
	write_png(ICON_PATH, out)
	print("icon", ICON_PATH)


def main():
	for o in list(bpy.data.objects):
		bpy.data.objects.remove(o, do_unlink=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(OUT_DIR, exist_ok=True)

	png = os.path.join(OUT_DIR, ATLAS + ".png")
	write_png(png, make_atlas())
	with open(os.path.join(OUT_DIR, ATLAS + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)
	mat = bpy.data.materials.new(ATLAS)
	mat.use_nodes = True
	bsdf = mat.node_tree.nodes["Principled BSDF"]
	bsdf.inputs["Roughness"].default_value = 0.7
	tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(png, check_existing=True)
	tex.interpolation = 'Closest'
	mat.node_tree.nodes.active = tex
	mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

	objs = {}
	for name, fn in ((BODY, build_body), (BODY + "_phys", build_body_phys), (WHEEL, wheel), (STEER, build_steer),
					 (FORK, build_fork), (ARM, build_arm), (FOLDED, build_folded)):
		m = Mesh()
		fn(m)
		objs[name] = m.to_object(name, mat)

	export([objs[BODY], objs[BODY + "_phys"]], BODY)
	for name in (WHEEL, STEER, FORK, ARM, FOLDED):
		export([objs[name]], name)
	write_prefab()

	for name, o in objs.items():
		tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
		xs = [v.co.x for v in o.data.vertices]
		ys = [v.co.y for v in o.data.vertices]
		zs = [v.co.z for v in o.data.vertices]
		print(f"{name}: {tris} tris, x {min(xs):.3f}..{max(xs):.3f} y {min(ys):.3f}..{max(ys):.3f} z {min(zs):.3f}..{max(zs):.3f}")

	# previews: the scooter assembled at rest, then the folded package on its own
	objs[BODY + "_phys"].hide_render = True
	rear = objs[WHEEL].copy()
	bpy.context.scene.collection.objects.link(rear)
	objs[WHEEL].location = (HALF_BASE, 0, -REST_DROP)
	rear.location = (-HALF_BASE, 0, -REST_DROP)
	objs[STEER].location = (HALF_BASE, 0, 0)
	objs[FORK].location = (HALF_BASE, 0, -REST_DROP)
	objs[ARM].location = PIVOT
	objs[ARM].rotation_euler = (0, -math.atan2(-REST_DROP - PIVOT.z, HALF_BASE + PIVOT.x), 0)
	objs[FOLDED].hide_render = True
	sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN'))
	bpy.context.scene.collection.objects.link(sun)
	sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
	mid = (0, 0, 0.2)
	render("front34", (2.1, -1.7, 1.0), mid)
	render("rear34", (-2.2, 1.6, 1.0), mid)
	render("side", (0, -3.0, 0.25), (0, 0, 0.25), ortho=1.9)
	render("top", (0.0, 0.0, 3.0), (0, 0.001, 0), ortho=1.9)
	steer_rot = objs[STEER].rotation_euler
	objs[STEER].rotation_euler = (0, 0, math.radians(30))
	objs[FORK].rotation_euler = (0, 0, math.radians(30))
	objs[WHEEL].rotation_euler = (0, 0, math.radians(30))
	render("steer30", (2.1, -1.7, 1.0), mid)
	objs[STEER].rotation_euler = objs[FORK].rotation_euler = objs[WHEEL].rotation_euler = (0, 0, 0)

	for name in (BODY, WHEEL, STEER, FORK, ARM):
		objs[name].hide_render = True
	rear.hide_render = True
	objs[FOLDED].hide_render = False
	render("folded_34", (1.1, -1.0, 0.75), (0, 0, 0.22))
	render("folded_back", (-1.1, 1.0, 0.75), (0, 0, 0.22))
	icon_src = render("folded_icon", (1.6, -1.5, 1.05), (0, 0, 0.23), res=512, ortho=0.72, transparent=True)
	write_icon(icon_src)

	for name in (BODY, WHEEL, STEER, FORK, ARM):
		objs[name].hide_render = False
	rear.hide_render = False
	objs[FOLDED].location = (0, 1.2, -0.29)
	blend = os.path.join(ROOT, "Blender", "blenderprojects", "pod_scooter.blend")
	bpy.ops.wm.save_as_mainfile(filepath=blend)
	print("saved", blend)


if __name__ == "__main__":
	main()
