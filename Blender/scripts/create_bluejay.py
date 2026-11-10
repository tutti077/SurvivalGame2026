"""Low-poly blue jay entity: skinned model, three clips, 256 px texture, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_bluejay.py"
    ... --python "Blender/scripts/create_bluejay.py" -- preview <outDir>      workbench stills of each clip (geometry only)

Writes
  Assets/models/entities/bluejay.fbx                 skinned mesh (bluejay_lod0 + bluejay_lod1) + armature, bind pose
  Assets/models/entities/animations/bluejay@sit.fbx  / @fly / @takeoff   one clip each (armature only)
  Assets/models/entities/bluejay.vmdl                ScaleAndMirror 0.52 (Blender metres -> 52 u/m = 1.3x), LODs, AnimationList
  Assets/materials/entities/bluejay.png + .vmat      256 x 256, pixel_lit, faceted
  Blender/blenderprojects/entities/bluejay.blend

Scale: modelled at 2x life size in Blender metres (beak tip to tail tip 0.55 m). The FBX reads as 100 u per Blender metre
and the vmdl ScaleAndMirror 0.52 (40 u/m x 1.3, Mark 2026-11-02: "a tad bigger") scales mesh AND clips, so the bird
is ~29 engine units long (0.72 m at the project's 40 u/m).

Frame: Blender -Y = bird forward (engine +X), Blender +X = bird's left (engine +Y), Z up. Origin on the ground between the
feet. Geometry is authored in bird coordinates (f forward, l left, u up) and converted with P().

Rig (all rigid, 100 % weights except the neck ring and the wrist line):
  body (root, hip pivot) -> head, tail, legs, wing_upper_L/R -> wing_lower_L/R
Clips (sequence names played by SkinnedModelRenderer.Sequence.Name):
  sit      4.0 s loop  perched, body up 18 deg, wings folded, quick head turns, one tail flick
  fly      0.6 s loop  level flight, two wing beats (0.3 s each), legs tucked
  takeoff  0.4 s       crouch, wings up, one big downbeat, ends exactly on fly frame 0
Pose rotations are written in armature-rest axes relative to the parent and converted to bone-local quaternions.
"""
import bpy, bmesh, math, os, struct, sys, zlib
import numpy as np
from mathutils import Vector, Matrix, Quaternion

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "entities")
ANIM_DIR = os.path.join(MODEL_DIR, "animations")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "entities")
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "bluejay.blend")
NAME = "bluejay"
FPS = 60
SEED = 11
TEX = 256


def P(f, l, u):
	"""Bird coordinates (forward, left, up) -> Blender (x = left, y = -forward, z = up)."""
	return Vector((l, -f, u))


# ------------------------------------------------------------------------------------------ proportions (metres, 2x life)
BODY_U = 0.12
HIP = (0.0, 0.0, 0.078)
NECK = (0.105, 0.0, 0.145)
TAILP = (-0.088, 0.0, 0.117)
SHOULDER = (0.055, 0.041, 0.158)

# Body loft: (f, centre-u offset from BODY_U, half-width, half-height)
BODY_RINGS = [
	(-0.128, -0.006, 0.014, 0.012),
	(-0.098, -0.003, 0.034, 0.032),
	(-0.052, 0.000, 0.048, 0.053),
	(0.000, 0.000, 0.052, 0.062),
	(0.045, 0.004, 0.051, 0.060),
	(0.085, 0.014, 0.043, 0.048),
	(0.115, 0.026, 0.032, 0.036),
]
HEAD_RINGS = [
	(0.100, 0.030, 0.018, 0.020),
	(0.116, 0.040, 0.031, 0.036),
	(0.138, 0.046, 0.039, 0.041),
	(0.160, 0.043, 0.033, 0.035),
	(0.176, 0.036, 0.020, 0.022),
]

# Wing (left; mirrored). s = outward from the shoulder, d = forward offset of the edge from the shoulder.
W_ST = [0.0, 0.04, 0.085, 0.13, 0.17, 0.205, 0.24, 0.27]
W_LE = [0.012, 0.020, 0.024, 0.022, 0.014, 0.002, -0.016, -0.040]
W_TE = [-0.075, -0.083, -0.087, -0.088, -0.080, -0.072, -0.064, -0.055]
WRIST = 0.13
# Primary feather plates sticking out of the hand: (base s, base d, sweep-back deg, length, base width)
PRIMARIES = [(0.175, -0.050, 30, 0.060, 0.022), (0.205, -0.040, 22, 0.066, 0.020),
			 (0.232, -0.028, 14, 0.066, 0.018), (0.255, -0.016, 6, 0.060, 0.016)]
WING_UV_S = 0.34                 # planar map extents of the wing (s 0..0.34, d +0.035..-0.135)
WING_UV_D0, WING_UV_D1 = 0.035, -0.135

# Tail plate
TAIL_BASE_F = -0.075
TAIL_LEN = 0.24
TAIL_UV_L = 0.042
TAIL_UV_T = 1.08

BONES = ["body", "head", "tail", "legs",
		 "wing_upper_L", "wing_lower_L", "wing_upper_R", "wing_lower_R"]
BONE_INDEX = {n: i for i, n in enumerate(BONES)}

# ------------------------------------------------------------------------------------------ texture
# Layout: wing top (0..127, 0..127), wing underside = folded outer face (128..255, 0..127),
# tail top (0..63, 128..255), tail underside (64..127, 128..255), swatches 4 x 4 of 32 px (128..255, 128..255).
SWATCH = {
	"blue_back": (90, 122, 178), "blue_crown": (104, 140, 204), "blue_dark": (52, 76, 134), "white": (236, 237, 239),
	"white_grey": (214, 214, 218), "grey_flank": (182, 183, 190), "black": (24, 24, 30), "beak": (34, 34, 38),
	"eye": (14, 14, 18), "leg": (52, 52, 58), "wing_edge": (60, 90, 150), "crest_under": (70, 98, 156),
	"white_throat": (240, 240, 240), "blue_rump": (98, 130, 186), "blue_nape": (96, 128, 186), "grey_dark": (120, 122, 130),
}
SWATCH_NAMES = list(SWATCH.keys())


def value_noise(rng, h, w, cells):
	grid = rng.random((cells + 1, cells + 1))
	ys = np.linspace(0, cells, h, endpoint=False)
	xs = np.linspace(0, cells, w, endpoint=False)
	y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
	fy = (ys - y0)[:, None]; fx = (xs - x0)[None, :]
	a = grid[y0][:, x0]; b = grid[y0][:, x0 + 1]; c = grid[y0 + 1][:, x0]; d = grid[y0 + 1][:, x0 + 1]
	return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def interp(xs, ys, x):
	if x <= xs[0]:
		return ys[0]
	if x >= xs[-1]:
		return ys[-1]
	for i in range(len(xs) - 1):
		if xs[i] <= x <= xs[i + 1]:
			t = (x - xs[i]) / (xs[i + 1] - xs[i])
			return ys[i] + (ys[i + 1] - ys[i]) * t
	return ys[-1]


def frac(x):
	return x - math.floor(x)


def wing_pixel(s, d, under):
	"""Colour of the wing at (s, d). under = the face seen from outside when folded (and from below in flight)."""
	inside = s <= W_ST[-1] and interp(W_ST, W_TE, s) <= d <= interp(W_ST, W_LE, s)
	feather_line = frac(s / 0.021) < 0.13
	if not inside:
		# primary feather plates beyond the panel: dark blue with black bars, black ends
		c = (46, 78, 146) if s < 0.21 else (34, 46, 84)
		if frac(s / 0.016) < 0.22:
			c = (22, 24, 34)
		if d < -0.10 or s > 0.29:
			c = (24, 26, 36)
		return c
	le, te = interp(W_ST, W_LE, s), interp(W_ST, W_TE, s)
	t = (le - d) / max(le - te, 1e-6)
	if s < WRIST:
		if t < 0.26:
			return SWATCH["blue_back"] if not under else (88, 120, 178)
		if under:
			# folded outer face: chord runs down, span runs back -> bars are vertical (constant s)
			if 0.40 <= t < 0.48:
				return (232, 234, 238) if not feather_line else (150, 156, 170)    # greater covert white tips
			if t >= 0.86:
				if s < 0.05:
					return (70, 128, 206) if not feather_line else (40, 60, 110)
				return (236, 237, 240) if not feather_line else (150, 156, 170)    # secondary white tips
			if 0.80 <= t < 0.86:
				return (22, 22, 30)
			if frac(s / 0.0105) < 0.26:
				return (24, 26, 36)
			return (70, 128, 206)
		if t < 0.48:
			if frac((t - 0.26) / 0.075) < 0.2:
				return (26, 28, 40)
			return (70, 128, 205)
		if t < 0.82:
			if frac((t - 0.48) / 0.085) < 0.2:
				return (24, 26, 36)
			return (66, 124, 204)
		if t < 0.9:
			return (20, 22, 30)
		return (238, 238, 240) if not feather_line else (40, 44, 60)
	# hand
	if t < 0.28:
		if (not under and abs(t - 0.2) < 0.03) or (under and frac(s / 0.012) < 0.2):
			return (26, 28, 40)
		return (60, 112, 196)
	base = np.array((52, 98, 182), float)
	if s > 0.22:
		base *= 0.72
	if t > 0.88:
		return (26, 30, 46)
	bar = frac(s / 0.011) < 0.24 if under else frac(t / 0.1) < 0.18
	if bar:
		return (22, 24, 34)
	return tuple(base)


def tail_pixel(x, t, under):
	"""x = across (-1..1 of the local half width), t = along (0 base .. ~1.06 centre tip)."""
	feather = abs(x) * 6.0
	edge = frac(feather) < 0.14 or abs(x) < 0.03
	outer = abs(x) > 0.3
	if under:
		if t > 0.9 and outer:
			return (236, 236, 238)
		if t > 0.86:
			return (26, 26, 32)
		return (64, 68, 82) if not edge else (40, 42, 52)
	if t > 0.92:
		return (236, 236, 238) if outer and not edge else (28, 32, 50)
	if t > 0.85:
		return (20, 22, 30)
	if frac(t / 0.062) < 0.16:
		return (24, 26, 38)
	if edge:
		return (46, 76, 140)
	return (72, 118, 196)


def make_texture():
	rng = np.random.default_rng(SEED)
	img = np.zeros((TEX, TEX, 4), np.float64)
	img[..., 3] = 255
	soft = value_noise(rng, TEX, TEX, 12)
	# wings
	for region, under in ((0, False), (128, True)):
		for py in range(128):
			for px in range(128):
				s = (px + 0.5) / 128.0 * WING_UV_S
				d = WING_UV_D0 - (py + 0.5) / 128.0 * (WING_UV_D0 - WING_UV_D1)
				c = np.array(wing_pixel(s, d, under), float)
				img[py, region + px, :3] = c + (soft[py, region + px] - 0.5) * 10.0
	# tail
	for region, under in ((0, False), (64, True)):
		for py in range(128):
			for px in range(64):
				l = ((px + 0.5) / 64.0 * 2.0 - 1.0) * TAIL_UV_L
				t = (py + 0.5) / 128.0 * TAIL_UV_T
				hw = 0.022 + 0.018 * min(t, 1.0)
				x = max(-1.0, min(1.0, l / hw))
				c = np.array(tail_pixel(x, t, under), float)
				img[128 + py, region + px, :3] = c + (soft[128 + py, region + px] - 0.5) * 10.0
	# swatches
	sw = value_noise(rng, 128, 128, 10)
	for i, name in enumerate(SWATCH_NAMES):
		cx, cy = 128 + (i % 4) * 32, 128 + (i // 4) * 32
		base = np.array(SWATCH[name], float)
		amp = 14.0 if name not in ("black", "eye", "beak") else 6.0
		for py in range(32):
			for px in range(32):
				img[cy + py, cx + px, :3] = base + (sw[cy - 128 + py, cx - 128 + px] - 0.5) * amp
	return np.clip(img, 0, 255).astype(np.uint8)


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


def px_uv(px, py):
	return (px / TEX, 1.0 - py / TEX)


def wing_uv(s, d, under):
	px = (s / WING_UV_S) * 128.0 + (128.0 if under else 0.0)
	py = (WING_UV_D0 - d) / (WING_UV_D0 - WING_UV_D1) * 128.0
	return px_uv(min(max(px, 0.5), 255.5), min(max(py, 0.5), 127.5))


def tail_uv(l, t, under):
	px = (l / TAIL_UV_L + 1.0) * 0.5 * 64.0 + (64.0 if under else 0.0)
	py = 128.0 + t / TAIL_UV_T * 128.0
	return px_uv(min(max(px, 0.5), 127.5), min(max(py, 128.5), 255.5))


# ------------------------------------------------------------------------------------------ mesh builder
def newell(pts):
	n = Vector((0.0, 0.0, 0.0))
	for i in range(len(pts)):
		a, b = pts[i], pts[(i + 1) % len(pts)]
		n.x += (a.y - b.y) * (a.z + b.z)
		n.y += (a.z - b.z) * (a.x + b.x)
		n.z += (a.x - b.x) * (a.y + b.y)
	return n


class Builder:
	def __init__(self, seed):
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.dl = self.bm.verts.layers.deform.verify()
		self.rng = np.random.default_rng(seed)

	def face(self, bird_pts, uvs, weights, want):
		"""bird_pts: (f, l, u) tuples; want: Blender-space vector the normal should agree with."""
		pts = [P(*p) for p in bird_pts]
		n = newell(pts)
		if n.length < 1e-12:
			return None
		if want is not None and n.dot(want) < 0:
			pts, uvs, weights = pts[::-1], uvs[::-1], weights[::-1]
		verts = [self.bm.verts.new(p) for p in pts]
		for v, w in zip(verts, weights):
			for bone, val in w.items():
				v[self.dl][BONE_INDEX[bone]] = val
		try:
			f = self.bm.faces.new(verts)
		except ValueError:
			return None
		for loop, uv in zip(f.loops, uvs):
			loop[self.uv].uv = uv
		return f

	def swatch_uvs(self, name, n):
		i = SWATCH_NAMES.index(name)
		cx, cy = 128 + (i % 4) * 32, 128 + (i // 4) * 32
		px = cx + 4 + self.rng.random() * 24
		py = cy + 4 + self.rng.random() * 24
		return [px_uv(px + (self.rng.random() - 0.5) * 4.0, py + (self.rng.random() - 0.5) * 4.0) for _ in range(n)]

	def sw_face(self, bird_pts, name, weights, want):
		return self.face(bird_pts, self.swatch_uvs(name, len(bird_pts)), weights, want)

	def to_object(self, name, mat):
		me = bpy.data.meshes.new(name)
		self.bm.to_mesh(me)
		self.bm.free()
		me.materials.append(mat)
		for p in me.polygons:
			p.use_smooth = False
		me.update()
		obj = bpy.data.objects.new(name, me)
		bpy.context.scene.collection.objects.link(obj)
		for b in BONES:
			obj.vertex_groups.new(name=b)
		return obj


def centroid(pts):
	n = len(pts)
	return tuple(sum(p[k] for p in pts) / n for k in range(3))


def bvec(a, b):
	"""Blender-space vector from bird point b to bird point a."""
	return P(*a) - P(*b)


# ------------------------------------------------------------------------------------------ lofts (body, head)
def ring_angles(lod, rng):
	base = [0, 36, 78, 116, 150, 180] if lod == 0 else [0, 62, 122, 180]
	inner = [a + float(rng.uniform(-6, 6)) for a in base[1:-1]]
	half = [0.0] + inner + [180.0]
	return half + [-a for a in reversed(half[1:-1])]


def ring_points(f, uc, w, h, angles):
	pts = []
	for a in angles:
		r = math.radians(a)
		cu = math.cos(r)
		hh = h * (1.06 if cu < 0 else 0.96)
		pts.append((f, w * math.sin(r), uc + hh * cu))
	return pts


def loft(b, rings, lod, colour_fn, weight_fn, rear_apex, front_apex):
	rng = b.rng
	rp = [ring_points(f, BODY_U + du, w, h, ring_angles(lod, rng)) for f, du, w, h in rings]
	fs = [r[0] for r in rings]
	ucs = [BODY_U + r[1] for r in rings]
	hs = [r[3] for r in rings]

	def axis_at(f):
		return (f, 0.0, interp(fs, ucs, f))

	def emit(pts):
		c = centroid(pts)
		uc = interp(fs, ucs, c[0])
		h = interp(fs, hs, c[0])
		s = (c[2] - uc) / max(h, 1e-6)
		name = colour_fn(c, s)
		b.sw_face(pts, name, [weight_fn(p) for p in pts], bvec(c, axis_at(c[0])))

	n = len(rp[0])
	for i in range(len(rp) - 1):
		A, B = rp[i], rp[i + 1]
		for k in range(n):
			k1 = (k + 1) % n
			emit([A[k], A[k1], B[k1], B[k]])
	for ring, apex in ((rp[0], rear_apex), (rp[-1], front_apex)):
		for k in range(n):
			k1 = (k + 1) % n
			pts = [ring[k], ring[k1], apex]
			c = centroid(pts)
			uc = interp(fs, ucs, c[0])
			h = interp(fs, hs, c[0])
			name = colour_fn(c, (c[2] - uc) / max(h, 1e-6))
			# cap normals point away from the loft's middle
			mid = (sum(fs) / len(fs), 0.0, sum(ucs) / len(ucs))
			b.sw_face(pts, name, [weight_fn(p) for p in pts], bvec(c, mid))


def body_colour(c, s):
	f = c[0]
	if f > 0.07:
		if f > 0.1 and s > 0.35:
			return "blue_nape"
		if s > 0.35:
			return "blue_back"
		return "black"                                     # necklace across the upper breast and up the neck
	if f > 0.03:
		if s > 0.32:
			return "blue_back"
		if s > -0.15:
			return "grey_flank"
		return "white_grey"
	if f > -0.075:
		if s > 0.3:
			return "blue_back"
		if s > -0.2:
			return "grey_flank"
		return "white" if f < -0.02 else "white_grey"
	if s > 0.15:
		return "blue_rump"
	return "white"


def head_colour(c, s):
	f = c[0]
	if s > 0.42:
		return "blue_crown"
	if f < 0.127:
		if s < -0.45:
			return "white_throat"
		return "black"                                     # necklace behind the ear coverts
	if f > 0.156 and -0.2 < s < 0.45:
		return "black"                                     # bridle / lores
	return "white_throat" if s < -0.4 else "white"


def body_weights(p):
	if p[0] >= 0.11:
		return {"head": 0.55, "body": 0.45}
	return {"body": 1.0}


def head_weights(p):
	return {"head": 1.0}


# ------------------------------------------------------------------------------------------ parts
def build_beak(b):
	f0, u0 = 0.170, 0.157
	ring0 = [(f0, 0.011 * math.sin(math.radians(a)), u0 + 0.010 * math.cos(math.radians(a))) for a in (0, 72, 144, -144, -72)]
	f1, u1 = 0.198, 0.153
	ring1 = [(f1, 0.0062 * math.sin(math.radians(a)), u1 + 0.0062 * math.cos(math.radians(a))) for a in (0, 72, 144, -144, -72)]
	tip = (0.224, 0.0, 0.148)
	w = [{"head": 1.0}] * 4
	for k in range(5):
		k1 = (k + 1) % 5
		pts = [ring0[k], ring0[k1], ring1[k1], ring1[k]]
		c = centroid(pts)
		b.sw_face(pts, "beak", w, bvec(c, (c[0], 0.0, 0.155)))
		pts = [ring1[k], ring1[k1], tip]
		c = centroid(pts)
		b.sw_face(pts, "beak", w[:3], bvec(c, (c[0] - 0.01, 0.0, 0.152)))


def build_crest(b):
	C = [(0.150, 0.203), (0.128, 0.222), (0.107, 0.236), (0.088, 0.250)]
	W = [0.020, 0.017, 0.012, 0.006]
	D = [0.012, 0.030, 0.050, 0.050]
	tip = (0.072, 0.0, 0.257)
	secs = []
	for (f, u), w, d in zip(C, W, D):
		secs.append([(f, 0.0, u + 0.004), (f, w, u - d), (f, -w, u - d)])
	wts = [{"head": 1.0}] * 4
	for i in range(len(secs) - 1):
		A, B = secs[i], secs[i + 1]
		for k in range(3):
			k1 = (k + 1) % 3
			pts = [A[k], A[k1], B[k1], B[k]]
			c = centroid(pts)
			ref = centroid(A + B)
			under = (k == 1)                    # the two base points -> underside
			b.sw_face(pts, "crest_under" if under else "blue_crown", wts, bvec(c, ref))
	A = secs[-1]
	for k in range(3):
		k1 = (k + 1) % 3
		pts = [A[k], A[k1], tip]
		c = centroid(pts)
		b.sw_face(pts, "blue_crown", wts[:3], bvec(c, centroid(A)))
	pts = secs[0]
	b.sw_face(pts, "blue_crown", wts[:3], P(0, 0, 0) - P(-1, 0, 0))


def build_eyes(b):
	for sg in (1.0, -1.0):
		f, u, l0, l1, r = 0.150, 0.170, 0.034, 0.0405, 0.0058
		base = [(f + r, sg * l0, u), (f, sg * l0, u + r), (f - r, sg * l0, u), (f, sg * (l0 - 0.001), u - r)]
		apex = (f, sg * l1, u)
		w = [{"head": 1.0}] * 3
		for k in range(4):
			pts = [base[k], base[(k + 1) % 4], apex]
			c = centroid(pts)
			b.sw_face(pts, "eye", w, bvec(c, (f, sg * 0.02, u)))


def build_legs(b):
	w1 = {"legs": 1.0}
	for sg in (1.0, -1.0):
		top = (0.004, sg * 0.020, 0.072)
		bot = (0.009, sg * 0.021, 0.006)
		r0, r1 = 0.0046, 0.0034
		sq0 = [(top[0] + dx * r0, top[1] + dy * r0, top[2]) for dx, dy in ((1, 1), (-1, 1), (-1, -1), (1, -1))]
		sq1 = [(bot[0] + dx * r1, bot[1] + dy * r1, bot[2]) for dx, dy in ((1, 1), (-1, 1), (-1, -1), (1, -1))]
		for k in range(4):
			pts = [sq0[k], sq0[(k + 1) % 4], sq1[(k + 1) % 4], sq1[k]]
			c = centroid(pts)
			b.sw_face(pts, "leg", [w1] * 4, bvec(c, (c[0], top[1], c[2])))
		# toes: three forward, one back
		for yaw, length in ((-24, 0.032), (0, 0.040), (24, 0.031), (180, 0.028)):
			a = math.radians(yaw)
			df, dl = math.cos(a), math.sin(a) * sg
			hw0, hw1 = 0.0032, 0.0018
			p0 = (bot[0], bot[1], 0.0)
			p1 = (bot[0] + df * length, bot[1] + dl * length, 0.0)
			nf, nl = -dl, df                                 # across the toe
			corners = []
			for (pf, pl, _), hw, ht in ((p0, hw0, 0.0055), (p1, hw1, 0.0030)):
				corners.append([(pf + nf * hw, pl + nl * hw, 0.0), (pf - nf * hw, pl - nl * hw, 0.0),
								(pf - nf * hw, pl - nl * hw, ht), (pf + nf * hw, pl + nl * hw, ht)])
			A, B = corners
			mid = ((p0[0] + p1[0]) / 2, (p0[1] + p1[1]) / 2, 0.002)
			for k in range(4):
				pts = [A[k], A[(k + 1) % 4], B[(k + 1) % 4], B[k]]
				b.sw_face(pts, "leg", [w1] * 4, bvec(centroid(pts), mid))
			b.sw_face(B, "leg", [w1] * 4, bvec(centroid(B), mid))


def wing_weight(s, side):
	up, lo = "wing_upper_" + side, "wing_lower_" + side
	if s < WRIST - 1e-6:
		return {up: 1.0}
	if s < WRIST + 1e-6:
		return {up: 0.5, lo: 0.5}
	return {lo: 1.0}


def build_wing(b, sg, lod, jit):
	side = "L" if sg > 0 else "R"
	fr_base = [0.0, 0.2, 0.48, 0.76, 1.0] if lod == 0 else [0.0, 0.5, 1.0]
	sf, sl, su = SHOULDER
	n_st, n_ch = len(W_ST), len(fr_base)

	def th(t):
		return 0.003 + 0.009 * (1.0 - t) ** 1.5

	def camber(t):
		return 0.005 * math.sin(math.pi * t)

	top, bot, sd = [], [], []
	for i in range(n_st):
		rt, rb, rs = [], [], []
		for j in range(n_ch):
			t = fr_base[j]
			if 0 < j < n_ch - 1:
				t += jit[i][j]
			d = W_LE[i] + (W_TE[i] - W_LE[i]) * t
			s = W_ST[i]
			f, l = sf + d, sg * (sl + s)
			rt.append((f, l, su + camber(t) + th(t) * 0.5))
			rb.append((f, l, su + camber(t) - th(t) * 0.5))
			rs.append((s, d))
		top.append(rt); bot.append(rb); sd.append(rs)

	def wts(idx):
		return [wing_weight(sd[i][j][0], side) for i, j in idx]

	for i in range(n_st - 1):
		for j in range(n_ch - 1):
			idx = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)]
			b.face([top[a][c] for a, c in idx], [wing_uv(*sd[a][c], False) for a, c in idx], wts(idx), Vector((0, 0, 1)))
			b.face([bot[a][c] for a, c in idx], [wing_uv(*sd[a][c], True) for a, c in idx], wts(idx), Vector((0, 0, -1)))
	mid_j = n_ch // 2

	def strip(i0, j0, i1, j1, ref):
		pts = [top[i0][j0], top[i1][j1], bot[i1][j1], bot[i0][j0]]
		w = [wing_weight(sd[i0][j0][0], side), wing_weight(sd[i1][j1][0], side)]
		b.sw_face(pts, "wing_edge", [w[0], w[1], w[1], w[0]], bvec(centroid(pts), ref))

	for i in range(n_st - 1):
		ref = centroid([top[i][mid_j], top[i + 1][mid_j]])
		strip(i, 0, i + 1, 0, ref)
		strip(i, n_ch - 1, i + 1, n_ch - 1, ref)
	for j in range(n_ch - 1):
		strip(0, j, 0, j + 1, centroid([top[1][j], top[1][j + 1]]))
		strip(n_st - 1, j, n_st - 1, j + 1, centroid([top[n_st - 2][j], top[n_st - 2][j + 1]]))

	# primary feathers
	lw = {"wing_lower_" + side: 1.0}
	for k, (s0, d0, sweep, length, width) in enumerate(PRIMARIES):
		a = math.radians(sweep)
		ds, dd = math.cos(a), -math.sin(a)
		ns, nd = -dd, ds                                    # across the feather
		outline = [
			(s0 + ns * width * 0.5, d0 + nd * width * 0.5),
			(s0 + ds * length * 0.62 + ns * width * 0.45, d0 + dd * length * 0.62 + nd * width * 0.45),
			(s0 + ds * length, d0 + dd * length),
			(s0 + ds * length * 0.62 - ns * width * 0.42, d0 + dd * length * 0.62 - nd * width * 0.42),
			(s0 - ns * width * 0.5, d0 - nd * width * 0.5),
		]
		uc = su - 0.0016 - 0.0007 * k
		t2 = 0.0012
		T = [(sf + d, sg * (sl + s), uc + t2) for s, d in outline]
		B = [(sf + d, sg * (sl + s), uc - t2) for s, d in outline]
		b.face(T, [wing_uv(s, d, False) for s, d in outline], [lw] * 5, Vector((0, 0, 1)))
		b.face(B, [wing_uv(s, d, True) for s, d in outline], [lw] * 5, Vector((0, 0, -1)))
		ref = centroid(T)
		for m in range(5):
			m1 = (m + 1) % 5
			pts = [T[m], T[m1], B[m1], B[m]]
			b.sw_face(pts, "blue_dark", [lw] * 4, bvec(centroid(pts), ref))


def build_tail(b, lod, jit):
	cols = [-1, -0.78, -0.55, -0.3, 0, 0.3, 0.55, 0.78, 1] if lod == 0 else [-1, -0.5, 0, 0.5, 1]
	rows = [0, 0.2, 0.42, 0.63, 0.8, 0.92] if lod == 0 else [0, 0.42, 0.8]
	tw = {"tail": 1.0}

	def hw(t):
		return 0.022 + 0.018 * min(t, 1.0)

	grid_t = []
	for r, t in enumerate(rows):
		line = []
		for c, x in enumerate(cols):
			tt = t + (jit[r][c] if 0 < r else 0.0)
			line.append(tt)
		grid_t.append(line)
	tip = []
	for c, x in enumerate(cols):
		tt = 1.0 + 0.06 * (1 - x * x) - (0.016 if c % 2 == 1 else 0.0)
		tip.append(tt)
	grid_t.append(tip)

	def pt(t, x, top):
		f = TAIL_BASE_F - t * TAIL_LEN
		l = x * hw(t)
		thick = 0.008 * (1.0 - min(t, 1.0)) + 0.003
		u = TAILP[2] - 0.008 * t + 0.0025 * (1 - x * x) + (thick * 0.5 if top else -thick * 0.5)
		return (f, l, u), l

	nr, nc = len(grid_t), len(cols)
	T = [[pt(grid_t[r][c], cols[c], True) for c in range(nc)] for r in range(nr)]
	B = [[pt(grid_t[r][c], cols[c], False) for c in range(nc)] for r in range(nr)]
	for r in range(nr - 1):
		for c in range(nc - 1):
			idx = [(r, c), (r, c + 1), (r + 1, c + 1), (r + 1, c)]
			b.face([T[a][e][0] for a, e in idx], [tail_uv(T[a][e][1], grid_t[a][e], False) for a, e in idx], [tw] * 4, Vector((0, 0, 1)))
			b.face([B[a][e][0] for a, e in idx], [tail_uv(B[a][e][1], grid_t[a][e], True) for a, e in idx], [tw] * 4, Vector((0, 0, -1)))
	edges = []
	for r in range(nr - 1):
		edges.append(((r, 0), (r + 1, 0)))
		edges.append(((r, nc - 1), (r + 1, nc - 1)))
	for c in range(nc - 1):
		edges.append(((0, c), (0, c + 1)))
		edges.append(((nr - 1, c), (nr - 1, c + 1)))
	for (r0, c0), (r1, c1) in edges:
		pts = [T[r0][c0][0], T[r1][c1][0], B[r1][c1][0], B[r0][c0][0]]
		cc = centroid(pts)
		if c0 == c1:                                        # side edges face outward, base forward, tip back
			ref = (cc[0], 0.0, cc[2])
		elif r0 == 0:
			ref = (cc[0] - 0.05, cc[1], cc[2])
		else:
			ref = (cc[0] + 0.05, cc[1], cc[2])
		b.sw_face(pts, "blue_dark", [tw] * 4, bvec(cc, ref))


def build_bird(lod, mat):
	b = Builder(SEED + 7 + lod)
	rng = np.random.default_rng(SEED + 100)
	wing_jit = [[float(rng.uniform(-0.05, 0.05)) for _ in range(5)] for _ in range(len(W_ST))]
	tail_jit = [[float(rng.uniform(-0.03, 0.03)) for _ in range(9)] for _ in range(6)]
	loft(b, BODY_RINGS, lod, body_colour, body_weights,
		 rear_apex=(-0.142, 0.0, BODY_U - 0.006), front_apex=(0.122, 0.0, BODY_U + 0.028))
	loft(b, HEAD_RINGS, lod, head_colour, head_weights,
		 rear_apex=(0.094, 0.0, BODY_U + 0.030), front_apex=(0.181, 0.0, BODY_U + 0.035))
	build_beak(b)
	build_crest(b)
	for sg in (1.0, -1.0):
		build_wing(b, sg, lod, wing_jit)
	build_tail(b, lod, tail_jit)
	if lod == 0:
		build_eyes(b)
		build_legs(b)
	return b.to_object(f"{NAME}_lod{lod}", mat)


# ------------------------------------------------------------------------------------------ armature
def build_armature():
	arm = bpy.data.armatures.new(NAME + "_rig")
	rig = bpy.data.objects.new(NAME + "_rig", arm)
	bpy.context.scene.collection.objects.link(rig)
	bpy.context.view_layer.objects.active = rig
	bpy.ops.object.mode_set(mode="EDIT")
	eb = arm.edit_bones

	def bone(name, head, tail, parent=None, deform=True):
		e = eb.new(name)
		e.head = P(*head)
		e.tail = P(*tail)
		e.roll = 0.0
		e.use_deform = deform
		if parent:
			e.parent = eb[parent]
			e.use_connect = False
		return e

	bone("body", HIP, (HIP[0] + 0.08, 0, HIP[2]))
	bone("head", NECK, (NECK[0] + 0.06, 0, NECK[2] + 0.02), "body")
	bone("tail", TAILP, (TAILP[0] - 0.12, 0, TAILP[2]), "body")
	bone("legs", HIP, (HIP[0], 0, 0.01), "body")
	for sg, side in ((1.0, "L"), (-1.0, "R")):
		sh = SHOULDER
		wr = (sh[0], sg * (sh[1] + WRIST), sh[2])
		tp = (sh[0] - 0.03, sg * (sh[1] + 0.29), sh[2])
		bone("wing_upper_" + side, (sh[0], sg * sh[1], sh[2]), wr, "body")
		bone("wing_lower_" + side, wr, tp, "wing_upper_" + side)
	bpy.ops.object.mode_set(mode="OBJECT")
	for pb in rig.pose.bones:
		pb.rotation_mode = "QUATERNION"
	return rig


def skin(obj, rig):
	obj.parent = rig
	mod = obj.modifiers.new("Armature", "ARMATURE")
	mod.object = rig


# ------------------------------------------------------------------------------------------ poses
def R(axis, deg):
	return Matrix.Rotation(math.radians(deg), 3, axis)


def pitch_up(d):
	return R("X", -d)


def yaw_left(d):
	return R("Z", d)


def tilt(d):
	return R("Y", d)


MIRROR = Matrix.Diagonal((-1.0, 1.0, 1.0))


def mirror(m):
	return MIRROR @ m @ MIRROR


def smooth(t):
	t = max(0.0, min(1.0, t))
	return t * t * (3 - 2 * t)


def pose(**kw):
	"""Bone -> (Quaternion in armature-rest axes relative to the parent, translation Vector)."""
	out = {}
	for k, v in kw.items():
		if isinstance(v, tuple):
			m, t = v
		else:
			m, t = v, Vector((0, 0, 0))
		out[k] = (m.to_quaternion(), Vector(t))
	return out


def with_wings(p, upper_L, lower_L, shift_L=(0.0, 0.0, 0.0)):
	sh = Vector(shift_L)
	p["wing_upper_L"] = (upper_L.to_quaternion(), sh)
	p["wing_lower_L"] = (lower_L.to_quaternion(), Vector((0, 0, 0)))
	p["wing_upper_R"] = (mirror(upper_L).to_quaternion(), Vector((-sh.x, sh.y, sh.z)))
	p["wing_lower_R"] = (mirror(lower_L).to_quaternion(), Vector((0, 0, 0)))
	return p


# Folded wing (left): sweep the span back, roll the trailing edge down (the underside faces out), tilt the lower
# edge out to follow the flank, tips a little inward and down over the tail.
FOLD_UPPER = R("X", -5) @ R("Z", 8) @ R("Y", -96) @ R("Z", 90)
FOLD_LOWER = R("Y", -14)
FOLD_SHIFT = (0.013, 0.0, 0.010)         # shoulder out and up while folded so the plate sits on the flank, not in it

SIT_PITCH = 18.0
FLAP = 0.3
FLY_LEN = 0.6
SIT_LEN = 4.0
TAKEOFF_LEN = 0.4


def flap_angles(q):
	"""Wing beat at warped phase q (0 = bottom of the downstroke, 0.5 = top)."""
	c, s = math.cos(2 * math.pi * q), math.sin(2 * math.pi * q)
	e = 12.0 - 52.0 * c                      # arm elevation, tip up +
	h = -28.0 * s                            # hand lag
	sweep_hand = 32.0 * max(0.0, s)          # hand folds back on the upstroke
	sweep_arm = 8.0 * s
	twist = -10.0 * s                        # leading edge down on the downstroke
	return e, h, sweep_arm, sweep_hand, twist


def wing_open(e, h, sweep_arm, sweep_hand, twist):
	upper = R("Y", -e) @ R("X", twist) @ R("Z", sweep_arm)
	lower = R("Y", -h) @ R("Z", sweep_hand)
	return upper, lower


def warp(p):
	"""Upstroke 42 % of the beat, downstroke 58 %."""
	up = 0.42
	return 0.5 * p / up if p < up else 0.5 + 0.5 * (p - up) / (1 - up)


def key_track(keys, t):
	"""keys: (time, value...) with holds; smoothstep between neighbours."""
	if t <= keys[0][0]:
		return keys[0][1:]
	for i in range(len(keys) - 1):
		t0, t1 = keys[i][0], keys[i + 1][0]
		if t0 <= t <= t1:
			w = smooth((t - t0) / max(t1 - t0, 1e-6))
			return tuple(a + (b - a) * w for a, b in zip(keys[i][1:], keys[i + 1][1:]))
	return keys[-1][1:]


# head: (time, yaw left, tilt, pitch down)
SIT_HEAD = [(0.0, 0, 0, 0), (0.55, 0, 0, 0), (0.66, 32, 4, 0), (1.45, 32, 4, 0), (1.56, -8, -14, 4), (2.2, -8, -14, 4),
			(2.31, -30, 0, -2), (3.05, -30, 0, -2), (3.16, 6, 0, 6), (3.6, 6, 0, 6), (3.72, 0, 0, 0), (4.0, 0, 0, 0)]


def tail_flick(t):
	t0, rise, fall = 2.62, 0.06, 0.24
	if t < t0 or t > t0 + rise + fall:
		return 0.0
	if t < t0 + rise:
		return 16.0 * smooth((t - t0) / rise)
	return 16.0 * (1.0 - smooth((t - t0 - rise) / fall))


def sit_pose(t):
	yaw, tl, pd = key_track(SIT_HEAD, t)
	breath = 0.0012 * math.sin(2 * math.pi * t / 2.0)
	p = pose(
		body=(pitch_up(SIT_PITCH), (0, 0, breath)),
		head=pitch_up(-SIT_PITCH + 4 - pd) @ yaw_left(yaw) @ tilt(tl),
		tail=pitch_up(4 - tail_flick(t)),           # tail points back: pitch_up lowers it
		legs=pitch_up(-SIT_PITCH),
	)
	return with_wings(p, FOLD_UPPER, FOLD_LOWER, FOLD_SHIFT)


def fly_pose(t):
	q = warp(frac(t / FLAP))
	c, s = math.cos(2 * math.pi * q), math.sin(2 * math.pi * q)
	p = pose(
		body=(pitch_up(3 + 2 * c), (0, 0, 0.006 * c)),
		head=pitch_up(-3 - 2 * c),
		tail=pitch_up(-1 + 3 * s),
		legs=pitch_up(-100),
	)
	return with_wings(p, *wing_open(*flap_angles(q)))


def blend(a, b, w):
	out = {}
	for k in set(a) | set(b):
		qa, ta = a.get(k, (Quaternion(), Vector()))
		qb, tb = b.get(k, (Quaternion(), Vector()))
		out[k] = (qa.slerp(qb, w), ta.lerp(tb, w))
	return out


def takeoff_keys():
	sit0 = sit_pose(0.0)
	fly0 = fly_pose(0.0)
	top_u, top_l = wing_open(78, -10, 0, 6, 0)
	crouch = pose(body=(pitch_up(6), (0, -0.006, -0.008)), head=pitch_up(-2), tail=pitch_up(-10), legs=pitch_up(-6))
	half = blend(sit0, with_wings({}, top_u, top_l), 0.45)
	for k in ("wing_upper_L", "wing_lower_L", "wing_upper_R", "wing_lower_R"):
		crouch[k] = half[k]
	top = with_wings(pose(body=(pitch_up(10), (0, -0.008, -0.004)), head=pitch_up(-8), tail=pitch_up(-6), legs=pitch_up(-14)),
					 top_u, top_l)
	dn_u, dn_l = wing_open(-55, 20, 0, 0, 10)
	down = with_wings(pose(body=(pitch_up(4), (0, -0.004, 0.012)), head=pitch_up(-4), tail=pitch_up(4), legs=pitch_up(-55)),
					  dn_u, dn_l)
	return [(0.0, sit0), (0.10, crouch), (0.16, top), (0.31, down), (TAKEOFF_LEN, fly0)]


def takeoff_pose(t, keys):
	for i in range(len(keys) - 1):
		t0, a = keys[i]
		t1, b = keys[i + 1]
		if t <= t1 or i == len(keys) - 2:
			w = smooth((t - t0) / (t1 - t0))
			return blend(a, b, w)
	return keys[-1][1]


def apply_pose(rig, p):
	for pb in rig.pose.bones:
		q, t = p.get(pb.name, (Quaternion(), Vector()))
		mr = pb.bone.matrix_local.to_3x3()
		mi = mr.inverted()
		pb.rotation_quaternion = (mi @ q.to_matrix() @ mr).to_quaternion()
		pb.location = mi @ t


def bake_clip(rig, name, length, fn):
	rig.animation_data_create()
	action = bpy.data.actions.new(name)
	action.use_fake_user = True
	rig.animation_data.action = action
	slot = action.slots.new(id_type="OBJECT", name=rig.name)
	rig.animation_data.action_slot = slot
	n = int(round(length * FPS))
	for k in range(n + 1):
		t = k / FPS
		bpy.context.scene.frame_set(k + 1)
		apply_pose(rig, fn(min(t, length)))
		for pb in rig.pose.bones:
			pb.keyframe_insert("rotation_quaternion", frame=k + 1)
			pb.keyframe_insert("location", frame=k + 1)
	cb = action.layers[0].strips[0].channelbag(slot)
	for fc in cb.fcurves:
		for kp in fc.keyframe_points:
			kp.interpolation = "LINEAR"
		fc.update()
	bpy.context.scene.frame_start = 1
	bpy.context.scene.frame_end = n + 1
	return action, n


def export_clip(rig, path):
	bpy.ops.object.select_all(action="DESELECT")
	rig.select_set(True)
	bpy.context.view_layer.objects.active = rig
	bpy.ops.export_scene.fbx(
		filepath=path, use_selection=True, object_types={"ARMATURE"}, add_leaf_bones=False,
		bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
		bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0, bake_anim_step=1.0,
		apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", bake_space_transform=False,
		axis_forward="-Z", axis_up="Y", use_armature_deform_only=False)


# ------------------------------------------------------------------------------------------ vmdl / vmat
VMAT = """// Blue jay (Blender/scripts/create_bluejay.py): 256 px point-filtered texture, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/bluejay.png"
}
"""

ANIM_FILE = """					{{
						_class = "AnimFile"
						name = "{name}"
						activity_name = ""
						activity_weight = 1
						weight_list_name = ""
						fade_in_time = {fade}
						fade_out_time = {fade}
						looping = {looping}
						delta = false
						worldSpace = false
						hidden = false
						anim_markup_ordered = false
						disable_compression = false
						disable_interpolation = false
						enable_scale = false
						source_filename = "models/entities/animations/bluejay@{name}.fbx"
						start_frame = -1
						end_frame = -1
						framerate = -1.0
						take = 0
						reverse = false
					}},
"""

RENDER_MESH = """					{{
						_class = "RenderMeshFile"
						name = "{mesh}"
						filename = "models/entities/bluejay.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 1.0
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = false
							exception_list = [ "{other}" ]
						}}
					}},
"""

VMDL_HEAD = """<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->
{
	rootNode =
	{
		_class = "RootNode"
		children =
		[
			{
				_class = "ModelModifierList"
				children =
				[
					{
						_class = "ModelModifier_ScaleAndMirror"
						note = "Blender metres export as 100 u/m; 0.52 = the project's 40 u/m x 1.3 for mesh and clips (bird ~29 u long)."
						scale = 0.52
						mirror_x = false
						mirror_y = false
						mirror_z = false
						flip_bone_forward = false
						swap_left_and_right_bones = false
					},
				]
			},
			{
				_class = "MaterialGroupList"
				children =
				[
					{
						_class = "DefaultMaterialGroup"
						remaps =
						[
							{
								from = "bluejay.vmat"
								to = "materials/entities/bluejay.vmat"
							},
						]
						use_global_default = false
						global_default_material = "materials/entities/bluejay.vmat"
					},
				]
			},
			{
				_class = "RenderMeshList"
				children =
				[
"""

VMDL_MID = """				]
			},
			{
				_class = "LODGroupList"
				children =
				[
					{
						_class = "LODGroup"
						switch_threshold = 0.0
						meshes = [ "bluejay_lod0" ]
					},
					{
						_class = "LODGroup"
						switch_threshold = 400.0
						meshes = [ "bluejay_lod1" ]
					},
				]
			},
			{
				_class = "AnimationList"
				children =
				[
"""

VMDL_TAIL = """				]
				default_root_bone_name = ""
			},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}
}
"""

CLIPS = [("sit", SIT_LEN, True, 0.2), ("fly", FLY_LEN, True, 0.15), ("takeoff", TAKEOFF_LEN, False, 0.1)]


def write_vmdl():
	body = VMDL_HEAD
	body += RENDER_MESH.format(mesh="bluejay_lod0", other="bluejay_lod1")
	body += RENDER_MESH.format(mesh="bluejay_lod1", other="bluejay_lod0")
	body += VMDL_MID
	for name, _, looping, fade in CLIPS:
		body += ANIM_FILE.format(name=name, looping="true" if looping else "false", fade=fade)
	body += VMDL_TAIL
	with open(os.path.join(MODEL_DIR, NAME + ".vmdl"), "w", newline="\n") as f:
		f.write(body)


# ------------------------------------------------------------------------------------------ main
def clip_fn(name):
	if name == "sit":
		return sit_pose
	if name == "fly":
		return fly_pose
	keys = takeoff_keys()
	return lambda t: takeoff_pose(t, keys)


def build_scene():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	sc = bpy.context.scene
	sc.unit_settings.system = "METRIC"
	sc.unit_settings.scale_length = 1.0
	sc.render.fps = FPS
	mat = bpy.data.materials.new(NAME)
	mat.use_nodes = True
	rig = build_armature()
	meshes = [build_bird(0, mat), build_bird(1, mat)]
	for m in meshes:
		skin(m, rig)
	return rig, meshes


def preview(out_dir):
	rig, meshes = build_scene()
	meshes[1].hide_render = True
	os.makedirs(out_dir, exist_ok=True)
	sc = bpy.context.scene
	sc.render.engine = "BLENDER_WORKBENCH"
	sc.display.shading.light = "STUDIO"
	sc.display.shading.color_type = "TEXTURE"
	sc.render.resolution_x, sc.render.resolution_y = 640, 480
	img = bpy.data.images.load(os.path.join(MAT_DIR, NAME + ".png"))
	img.alpha_mode = "NONE"
	mat = meshes[0].data.materials[0]
	nt = mat.node_tree
	tex = nt.nodes.new("ShaderNodeTexImage")
	tex.image = img
	tex.interpolation = "Closest"
	bsdf = nt.nodes.get("Principled BSDF")
	nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
	cam_data = bpy.data.cameras.new("cam")
	cam_data.type = "ORTHO"
	cam_data.ortho_scale = 0.75
	cam = bpy.data.objects.new("cam", cam_data)
	sc.collection.objects.link(cam)
	sc.camera = cam
	views = {"side": ((1.2, -0.1, 0.15), (0, 0, 0)), "front34": ((0.7, -0.9, 0.5), None), "top": ((0, 0, 1.4), None)}
	target = Vector((0, 0, 0.12))
	for clip, length, _, _ in CLIPS:
		fn = clip_fn(clip)
		times = [0.0, length * 0.25, length * 0.5, length * 0.75] if clip != "takeoff" else [0.0, 0.1, 0.16, 0.24, 0.31, 0.4]
		for t in times:
			apply_pose(rig, fn(t))
			bpy.context.view_layer.update()
			for vname, (loc, _) in views.items():
				cam.location = Vector(loc)
				d = target - cam.location
				cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
				sc.render.filepath = os.path.join(out_dir, f"{clip}_{t:0.2f}_{vname}.png")
				bpy.ops.render.render(write_still=True)
	print("[bluejay] preview ->", out_dir)


def main():
	os.makedirs(MODEL_DIR, exist_ok=True)
	os.makedirs(ANIM_DIR, exist_ok=True)
	os.makedirs(MAT_DIR, exist_ok=True)
	os.makedirs(os.path.dirname(BLEND), exist_ok=True)
	write_png(os.path.join(MAT_DIR, NAME + ".png"), make_texture())
	with open(os.path.join(MAT_DIR, NAME + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)

	rig, meshes = build_scene()
	# bind-pose model
	bpy.ops.object.select_all(action="DESELECT")
	for o in [rig] + meshes:
		o.select_set(True)
	bpy.context.view_layer.objects.active = rig
	bpy.ops.export_scene.fbx(
		filepath=os.path.join(MODEL_DIR, NAME + ".fbx"), use_selection=True, object_types={"ARMATURE", "MESH"},
		add_leaf_bones=False, bake_anim=False, mesh_smooth_type="OFF", path_mode="STRIP", embed_textures=False,
		apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", bake_space_transform=False,
		axis_forward="-Z", axis_up="Y", use_armature_deform_only=False)

	for name, length, _, _ in CLIPS:
		action, n = bake_clip(rig, name, length, clip_fn(name))
		export_clip(rig, os.path.join(ANIM_DIR, f"{NAME}@{name}.fbx"))
		print(f"[bluejay] clip {name}: {n} frames @ {FPS} fps = {n / FPS:0.3f} s")
	write_vmdl()

	rig.animation_data.action = None
	for pb in rig.pose.bones:
		pb.rotation_quaternion = Quaternion()
		pb.location = Vector()
	if os.path.exists(BLEND):
		os.remove(BLEND)
	bpy.ops.wm.save_as_mainfile(filepath=BLEND)
	for m in meshes:
		bb = [m.matrix_world @ Vector(c) for c in m.bound_box]
		lo = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
		hi = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
		print(f"[bluejay] {m.name}: faces={len(m.data.polygons)} verts={len(m.data.vertices)} "
			  f"bind bounds x {lo.x:.3f}..{hi.x:.3f} y {lo.y:.3f}..{hi.y:.3f} z {lo.z:.3f}..{hi.z:.3f} m")


argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if argv and argv[0] == "preview":
	preview(argv[1])
else:
	main()
