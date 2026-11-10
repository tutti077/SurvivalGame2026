"""Low-poly whitetail deer (buck): skinned model, five clips, 256 px texture, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_deer.py"
    ... --python "Blender/scripts/create_deer.py" -- check      build + bake, print the foot / IK report, write nothing

Writes
  Assets/models/entities/deer_v2.fbx                     skinned mesh (deer_lod0..3) + armature, bind pose
  Assets/models/entities/animations/deer_v2@<clip>.fbx   one clip each (armature only)
  Assets/models/entities/deer_v2.vmdl                    ScaleAndMirror 0.4 (Blender metres -> 40 u/m), LODs, AnimationList
  Assets/materials/entities/deer_v2.png + .vmat          256 x 256, pixel_lit, point filtered, faceted
  Blender/blenderprojects/entities/deer.blend
File names carry the model version (NAME); the skeleton and LOD mesh names stay "deer_*" (RIG) across versions.

Scale: modelled at life size in Blender metres - withers 0.97 m, chest-to-rump 1.15 m, nose-to-rump 1.6 m, antler tips
1.57 m. The FBX reads as 100 u per Blender metre and the vmdl ScaleAndMirror 0.4 makes that 40 u/m for mesh AND clips,
so the shoulder stands ~39 engine units (fox: ~20 u).

Frame: Blender -Y = deer forward (engine +X), Blender +X = the deer's left (engine +Y), Z up. Origin on the ground
between the four hooves.

Rig: root -> pelvis -> spine -> neck -> head -> ear_L / ear_R; pelvis -> tail, hind legs; spine -> front legs.
Each leg is two bones (upper: scapula / hip pivot -> carpus / hock, lower: -> hoof contact) solved with analytic
two-bone IK every frame, so a planted hoof never slides.

Clips (sequence names played by SkinnedModelRenderer.Sequence.Name), 30 fps, all loops:
  idle    4.00 s   weight shift, breathing, slow look around, left ear flick, right ear double flick, tail flick
  walk    0.733 s  lateral-sequence 4-beat walk at 1.3 m/s  -> 0.953 m travelled per cycle
  run     0.400 s  rotary bounding gallop at 8 m/s, tail flagged up  -> 3.200 m travelled per cycle
  graze   3.00 s   head down, nose on the grass, nibbling, ear turns, one tail flick
  alert   2.00 s   frozen head-up stare, ears forward, tail half up, breathing only
Metres per cycle = speed x cycle length; play the clip at PlaybackRate = actual speed / clip speed to keep feet planted.
"""
import bpy, bmesh, math, os, struct, sys, zlib
import numpy as np
from mathutils import Vector, Matrix, Quaternion

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "entities")
ANIM_DIR = os.path.join(MODEL_DIR, "animations")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "entities")
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "deer.blend")
NAME = "deer_v2"     # file version: v2 = the 2026-11-02 width pass (v1 deer.* files deleted)
RIG = "deer"         # skeleton / mesh object names, unchanged across versions
FPS = 30
SEED = 23
TEX = 256

# Part ids (face attribute "part"), used for weights and paint.
TRUNK, LEG_FR, LEG_FL, LEG_HR, LEG_HL, TAIL, EAR_R, EAR_L, ANTLER, EYE = range(10)
LEG_PARTS = (LEG_FR, LEG_FL, LEG_HR, LEG_HL)

X = Vector((1, 0, 0))
Y = Vector((0, 1, 0))
Z = Vector((0, 0, 1))


def smoothstep(e0, e1, x):
	t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
	return t * t * (3.0 - 2.0 * t)


# ------------------------------------------------------------------------------------------ proportions
# Trunk + neck + head loft, rump -> nose: (y, z, tilt deg, half-width, half-height up, half-height down).
# tilt leans the section's "up" axis toward +Y (back) - the neck sections are tilted so the loft turns up into it.
TRUNK_RINGS = [
	(0.560, 0.760, -20, 0.070, 0.100, 0.100),
	(0.525, 0.755, -8, 0.140, 0.170, 0.170),
	(0.440, 0.760, 0, 0.172, 0.195, 0.195),
	(0.320, 0.772, 0, 0.168, 0.173, 0.172),
	(0.170, 0.760, 0, 0.178, 0.175, 0.175),
	(0.010, 0.738, 0, 0.190, 0.198, 0.198),
	(-0.140, 0.730, 0, 0.185, 0.225, 0.225),
	(-0.260, 0.738, 0, 0.170, 0.238, 0.238),
	(-0.360, 0.742, 12, 0.150, 0.225, 0.228),
	(-0.445, 0.800, 32, 0.130, 0.165, 0.195),
	(-0.508, 0.895, 46, 0.118, 0.130, 0.145),
	(-0.568, 0.985, 44, 0.104, 0.108, 0.115),
	(-0.618, 1.060, 32, 0.090, 0.092, 0.098),
	(-0.662, 1.120, 5, 0.074, 0.070, 0.088),
	(-0.727, 1.125, -16, 0.078, 0.064, 0.078),
	(-0.807, 1.095, -24, 0.056, 0.048, 0.054),
	(-0.877, 1.060, -25, 0.040, 0.037, 0.042),
	(-0.924, 1.038, -25, 0.025, 0.024, 0.028),
]
# Everything on the head (ears, eyes, antlers, head bone) was laid out 4 cm higher; HOFF moves it onto the head.
HOFF = Vector((0.0, -0.012, -0.040))
NOSE_TIP = Vector((0.0, -0.947, 1.031))
RUMP_TIP = Vector((0.0, 0.585, 0.765))
TRUNK_ANGLES = [90, 58, 27, 0, -31, -63, -90, -117, -149, 180, 153, 122]

# Right legs (x < 0); the left ones are mirrored. (x, y, z, half-width, half-depth front, half-depth back)
FRONT_LEG = [
	(-0.098, -0.262, 0.880, 0.050, 0.095, 0.095),
	(-0.116, -0.292, 0.715, 0.074, 0.112, 0.108),
	(-0.108, -0.312, 0.565, 0.064, 0.072, 0.098),
	(-0.100, -0.345, 0.450, 0.045, 0.050, 0.050),
	(-0.097, -0.368, 0.330, 0.032, 0.034, 0.034),
	(-0.096, -0.376, 0.285, 0.034, 0.038, 0.030),
	(-0.094, -0.378, 0.170, 0.022, 0.026, 0.026),
	(-0.093, -0.381, 0.088, 0.026, 0.030, 0.032),
	(-0.092, -0.393, 0.050, 0.024, 0.026, 0.026),
	(-0.091, -0.401, 0.033, 0.030, 0.036, 0.020),
	(-0.090, -0.410, 0.000, 0.032, 0.050, 0.024),
]
HIND_LEG = [
	(-0.096, 0.330, 0.870, 0.060, 0.120, 0.120),
	(-0.120, 0.375, 0.735, 0.098, 0.150, 0.165),
	(-0.114, 0.395, 0.610, 0.088, 0.140, 0.160),
	(-0.105, 0.440, 0.500, 0.052, 0.075, 0.072),
	(-0.099, 0.488, 0.405, 0.036, 0.040, 0.052),
	(-0.097, 0.482, 0.360, 0.028, 0.030, 0.034),
	(-0.094, 0.456, 0.220, 0.022, 0.026, 0.026),
	(-0.093, 0.432, 0.088, 0.026, 0.030, 0.032),
	(-0.092, 0.420, 0.050, 0.024, 0.026, 0.026),
	(-0.091, 0.410, 0.033, 0.030, 0.036, 0.020),
	(-0.090, 0.400, 0.000, 0.032, 0.050, 0.024),
]
LEG_ANGLES = [90, 40, -5, -50, -90, -138, 172, 128]   # 90 = front (-Y), 0 = outward

# Bones (head, tail) in Blender metres. Legs: upper = pivot -> carpus / hock, lower = -> hoof contact on the ground.
BONES = {
	"root": ((0, 0, 0), (0, -0.25, 0), None),
	"pelvis": ((0, 0.40, 0.80), (0, 0.05, 0.80), "root"),
	"spine": ((0, 0.05, 0.80), (0, -0.40, 0.80), "pelvis"),
	"neck": ((0, -0.42, 0.80), (0, -0.652, 1.110), "spine"),
	"head": ((0, -0.652, 1.110), (0, -0.932, 1.030), "neck"),
	"ear_L": ((0.050, -0.677, 1.182), (0.180, -0.660, 1.312), "head"),
	"ear_R": ((-0.050, -0.677, 1.182), (-0.180, -0.660, 1.312), "head"),
	"tail": ((0, 0.555, 0.835), (0, 0.630, 0.640), "pelvis"),
	"leg_upper_FR": ((-0.098, -0.262, 0.860), (-0.097, -0.378, 0.290), "spine"),
	"leg_lower_FR": ((-0.097, -0.378, 0.290), (-0.090, -0.405, 0.0), "leg_upper_FR"),
	"leg_upper_FL": ((0.098, -0.262, 0.860), (0.097, -0.378, 0.290), "spine"),
	"leg_lower_FL": ((0.097, -0.378, 0.290), (0.090, -0.405, 0.0), "leg_upper_FL"),
	"leg_upper_HR": ((-0.100, 0.360, 0.800), (-0.099, 0.488, 0.405), "pelvis"),
	"leg_lower_HR": ((-0.099, 0.488, 0.405), (-0.090, 0.405, 0.0), "leg_upper_HR"),
	"leg_upper_HL": ((0.100, 0.360, 0.800), (0.099, 0.488, 0.405), "pelvis"),
	"leg_lower_HL": ((0.099, 0.488, 0.405), (0.090, 0.405, 0.0), "leg_upper_HL"),
}
# Width pass (Mark 2026-11-02: too thin from behind, easier to hit with an arrow): the barrel and hips are 40 % wider
# across (Blender X = engine Y), the chest front 20 %; the hind legs sit 35 % further apart with 30 % fuller hams, the
# front legs 15 %. Seen from behind the body goes 0.43 -> 0.57 m (+33 %), the barrel 0.39 -> 0.54 m. Length, heights
# and bone names are unchanged - only the leg bones move outward with their legs.
BODY_WIDEN, CHEST_WIDEN, HIND_SPREAD, HAM_WIDEN, FRONT_SPREAD = 1.40, 1.20, 1.35, 1.30, 1.15
TRUNK_RINGS = [(y, z, t, hw * (BODY_WIDEN if i <= 8 else CHEST_WIDEN if i == 9 else 1.0), hu, hd)
			   for i, (y, z, t, hw, hu, hd) in enumerate(TRUNK_RINGS)]
FRONT_LEG = [(x * FRONT_SPREAD, y, z, w, f, bk) for (x, y, z, w, f, bk) in FRONT_LEG]
HIND_LEG = [(x * HIND_SPREAD, y, z, w * (HAM_WIDEN if i in (1, 2) else 1.0), f, bk) for i, (x, y, z, w, f, bk) in enumerate(HIND_LEG)]
for _bn in list(BONES):
	if _bn.startswith("leg_"):
		_k = HIND_SPREAD if _bn[-2] == "H" else FRONT_SPREAD
		_h, _t, _par = BONES[_bn]
		BONES[_bn] = ((_h[0] * _k, _h[1], _h[2]), (_t[0] * _k, _t[1], _t[2]), _par)

# leg -> (upper, lower, bend direction: +1 = joint apex forward (front carpus), -1 = back (hind hock), body bone)
LEGS = {
	"FR": ("leg_upper_FR", "leg_lower_FR", 1.0, "spine"),
	"FL": ("leg_upper_FL", "leg_lower_FL", 1.0, "spine"),
	"HR": ("leg_upper_HR", "leg_lower_HR", -1.0, "pelvis"),
	"HL": ("leg_upper_HL", "leg_lower_HL", -1.0, "pelvis"),
}
LEG_OF_PART = {LEG_FR: "FR", LEG_FL: "FL", LEG_HR: "HR", LEG_HL: "HL"}
EYE_POS = Vector((0.070, -0.745, 1.146))

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
C_DORSAL = (104, 72, 46)
C_COAT = (140, 99, 63)
C_FLANK = (163, 120, 80)
C_CREAM = (200, 174, 138)
C_WHITE = (226, 221, 210)
C_FACE = (118, 92, 70)
C_BRIDGE = (88, 66, 50)
C_NOSE = (32, 27, 25)
C_HOOF = (44, 37, 33)
C_EYE = (18, 15, 14)
C_LEG = (150, 112, 76)
C_LEG_LOW = (134, 104, 76)
C_EAR_IN = (192, 172, 152)
C_EAR_RIM = (62, 50, 42)
C_ANTLER = (176, 154, 118)
C_ANTLER_BASE = (118, 96, 70)
C_ANTLER_TIP = (206, 192, 162)


def mix(a, b, t):
	return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


# ------------------------------------------------------------------------------------------ geometry
class Builder:
	def __init__(self):
		self.bm = bmesh.new()
		self.part = self.bm.faces.layers.int.new("part")

	def loft(self, rings, part, cap0=None, cap1=None):
		"""rings: list of point lists (same count). cap0 / cap1: None = flat n-gon, Vector = fan to that point."""
		vs = [[self.bm.verts.new(p) for p in r] for r in rings]
		k = len(rings[0])
		for a, b in zip(vs, vs[1:]):
			for i in range(k):
				f = self.bm.faces.new((a[i], a[(i + 1) % k], b[(i + 1) % k], b[i]))
				f[self.part] = part
		for ring, cap, rev in ((vs[0], cap0, True), (vs[-1], cap1, False)):
			if cap is None:
				f = self.bm.faces.new(ring[::-1] if rev else ring)
				f[self.part] = part
			else:
				c = self.bm.verts.new(cap)
				for i in range(k):
					tri = (c, ring[(i + 1) % k], ring[i]) if rev else (c, ring[i], ring[(i + 1) % k])
					f = self.bm.faces.new(tri)
					f[self.part] = part

	def closed(self, verts, faces, part):
		vs = [self.bm.verts.new(v) for v in verts]
		for fi in faces:
			f = self.bm.faces.new([vs[i] for i in fi])
			f[self.part] = part


def sym_jitter(rng, angles, amount):
	"""Radial jitter per ring vertex, identical for the mirror pair (a, 180 - a) so the silhouette stays symmetric."""
	vals = {}
	out = []
	for a in angles:
		key = (round(math.sin(math.radians(a)), 3), round(abs(math.cos(math.radians(a))), 3))
		if key not in vals:
			vals[key] = rng.uniform(-amount, amount)
		out.append(vals[key])
	return out


def section(c, lat, up, hw, hu, hd, angles, jit, ex=0.8, ey=0.9):
	pts = []
	for a, j in zip(angles, jit):
		r = math.radians(a)
		ca, sa = math.cos(r), math.sin(r)
		x = hw * math.copysign(abs(ca) ** ex, ca)
		v = (hu if sa > 0 else hd) * math.copysign(abs(sa) ** ey, sa)
		pts.append(c + lat * (x * (1 + j)) + up * (v * (1 + j)))
	return pts


def build_trunk(b, rng):
	rings = []
	for (y, z, tilt, hw, hu, hd) in TRUNK_RINGS:
		t = math.radians(tilt)
		up = Vector((0, math.sin(t), math.cos(t)))
		jit = sym_jitter(rng, TRUNK_ANGLES, 0.05)
		rings.append(section(Vector((0, y, z)), X, up, hw, hu, hd, TRUNK_ANGLES, jit))
	# a flatter, square-ish back over the loins and a deeper keel under the chest
	b.loft(rings, TRUNK, cap0=RUMP_TIP, cap1=NOSE_TIP)


def build_leg(b, rng, spec, part):
	pts = [Vector(s[:3]) for s in spec]
	rings = []
	for i, s in enumerate(spec):
		a = pts[max(0, i - 1)]
		c = pts[min(len(pts) - 1, i + 1)]
		d = (c - a).normalized()
		fwd = Vector((0, -1, 0))
		fwd = (fwd - d * fwd.dot(d)).normalized()
		out = Vector((-1, 0, 0))   # outward for a right leg
		out = (out - d * out.dot(d) - fwd * out.dot(fwd)).normalized()
		jit = [rng.uniform(-0.06, 0.06) for _ in LEG_ANGLES]
		ring = []
		for a_deg, j in zip(LEG_ANGLES, jit):
			r = math.radians(a_deg)
			ca, sa = math.cos(r), math.sin(r)
			x = s[3] * math.copysign(abs(ca) ** 0.85, ca)
			v = (s[4] if sa > 0 else s[5]) * math.copysign(abs(sa) ** 0.85, sa)
			ring.append(pts[i] + out * (x * (1 + j)) + fwd * (v * (1 + j)))
		rings.append(ring)
	b.loft(rings, part, cap0=pts[0] + Vector((0, 0, 0.04)), cap1=None)


def build_tail(b, rng):
	# hangs down-back from the croup; local up (dorsal) faces back (+Y) while it hangs
	spine = [Vector((0, 0.540, 0.845)), Vector((0, 0.582, 0.790)), Vector((0, 0.610, 0.725)), Vector((0, 0.626, 0.665))]
	widths = [0.040, 0.058, 0.055, 0.034]
	thick = [0.030, 0.026, 0.022, 0.016]
	angles = [90, 30, -30, -90, -150, 150]
	rings = []
	for i, p in enumerate(spine):
		a = spine[max(0, i - 1)]
		c = spine[min(len(spine) - 1, i + 1)]
		d = (c - a).normalized()
		dors = (Y - d * Y.dot(d)).normalized()
		jit = sym_jitter(rng, angles, 0.05)
		rings.append(section(p, X, dors, widths[i], thick[i], thick[i] * 0.8, angles, jit, ex=0.7))
	b.loft(rings, TAIL, cap0=None, cap1=Vector((0, 0.630, 0.618)))


def build_ear(b, side, part):
	"""Cupped ear: base on the poll, tip up-out-back. Front (cup) face looks forward-out."""
	s = side
	base = Vector((0.052 * s, -0.665, 1.218)) + HOFF
	tip = Vector((0.192 * s, -0.645, 1.368)) + HOFF
	ax = (tip - base).normalized()
	fwd = Vector((0.25 * s, -1.0, 0.1))
	fwd = (fwd - ax * fwd.dot(ax)).normalized()
	wid = ax.cross(fwd).normalized()
	L = (tip - base).length
	# outline across the ear at fractions of its length: (t, half-width, cup depth)
	prof = [(0.0, 0.022, 0.004), (0.22, 0.046, 0.014), (0.55, 0.052, 0.016), (0.82, 0.034, 0.011)]
	front, back = [], []
	for t, hw, cup in prof:
		c = base + ax * (L * t)
		for k in (-1, 0, 1):
			p = c + wid * (hw * k)
			if k == 0:
				p = p - fwd * cup          # cup hollow
			front.append(p + fwd * 0.004)
			back.append(p - fwd * (0.010 if k == 0 else 0.004) + (-fwd * cup * 0.6 if k == 0 else Vector()))
	verts = front + back + [tip + fwd * 0.002, tip - fwd * 0.004]
	n = len(prof)
	nf = 3 * n
	tf, tb = 2 * nf, 2 * nf + 1
	faces = []
	for r in range(n - 1):
		a = 3 * r
		bb = 3 * (r + 1)
		faces += [(a, a + 1, bb + 1, bb), (a + 1, a + 2, bb + 2, bb + 1)]
		faces += [(nf + bb, nf + bb + 1, nf + a + 1, nf + a), (nf + bb + 1, nf + bb + 2, nf + a + 2, nf + a + 1)]
		faces += [(a, bb, nf + bb, nf + a), (a + 2, nf + a + 2, nf + bb + 2, bb + 2)]
	last = 3 * (n - 1)
	faces += [(last, last + 1, tf), (last + 1, last + 2, tf)]
	faces += [(nf + last + 1, nf + last, tb), (nf + last + 2, nf + last + 1, tb)]
	faces += [(last, tf, tb, nf + last), (last + 2, nf + last + 2, tb, tf)]
	faces += [(0, nf, nf + 1, 1), (1, nf + 1, nf + 2, 2)]
	b.closed(verts, faces, part)


def tube(b, pts, radii, part, k=5, rng=None):
	rings = []
	for i, p in enumerate(pts):
		a = pts[max(0, i - 1)]
		c = pts[min(len(pts) - 1, i + 1)]
		d = (c - a).normalized()
		u = Z if abs(d.dot(Z)) < 0.9 else Y
		e1 = d.cross(u).normalized()
		e2 = d.cross(e1).normalized()
		ring = []
		for j in range(k):
			ang = 2 * math.pi * j / k + (0.4 * i)
			r = radii[i] * (1.0 + (rng.uniform(-0.12, 0.12) if rng is not None else 0.0))
			ring.append(p + e1 * (math.cos(ang) * r) + e2 * (math.sin(ang) * r))
		rings.append(ring)
	tip = pts[-1] + (pts[-1] - pts[-2]).normalized() * radii[-1] * 1.8
	b.loft(rings, part, cap0=None, cap1=tip)


def build_antlers(b, rng):
	o = Vector((0, 0.035, -0.047)) + HOFF
	for s in (1, -1):
		def V(x, y, z):
			return Vector((x * s, y, z)) + o
		beam = [V(0.040, -0.715, 1.282), V(0.076, -0.702, 1.342), V(0.122, -0.714, 1.398), V(0.158, -0.760, 1.436),
				V(0.156, -0.828, 1.458), V(0.120, -0.884, 1.466)]
		tube(b, beam, [0.020, 0.018, 0.015, 0.013, 0.010, 0.007], ANTLER, k=5, rng=rng)

		def tine(a, m, t, r):
			a, m, t = V(*a), V(*m), V(*t)
			tube(b, [a, a + (m - a) * 0.8, a + (t - a) * 0.75], r, ANTLER, k=4, rng=rng)
		# brow tine, two standing tines (a young buck's 8-point rack)
		tine((0.078, -0.706, 1.348), (0.076, -0.734, 1.392), (0.070, -0.752, 1.424), [0.009, 0.007, 0.005])
		tine((0.138, -0.730, 1.416), (0.146, -0.742, 1.474), (0.140, -0.754, 1.520), [0.011, 0.009, 0.006])
		tine((0.158, -0.792, 1.448), (0.155, -0.808, 1.502), (0.146, -0.820, 1.538), [0.010, 0.008, 0.005])


def build_eyes(b):
	for s in (1, -1):
		c = Vector((EYE_POS.x * s, EYE_POS.y, EYE_POS.z))
		o = Vector((s, -0.35, 0.12)).normalized()
		fw = Vector((0, -1, 0))
		up = o.cross(fw).normalized() if s > 0 else fw.cross(o).normalized()
		fw = up.cross(o).normalized() if s > 0 else o.cross(up).normalized()
		r = 0.016
		ring = [c + fw * r * 1.25, c + up * r * 0.8, c - fw * r * 1.1, c - up * r * 0.8]
		apex = c + o * 0.010
		back = c - o * 0.012
		verts = ring + [apex, back]
		faces = [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (1, 0, 5), (2, 1, 5), (3, 2, 5), (0, 3, 5)]
		b.closed(verts, faces, EYE)


def mirror_part(bm, part_layer, src, dst):
	geom = [f for f in bm.faces if f[part_layer] == src]
	vmap = {}
	for f in geom:
		for v in f.verts:
			if v not in vmap:
				vmap[v] = bm.verts.new(Vector((-v.co.x, v.co.y, v.co.z)))
	for f in geom:
		nf = bm.faces.new([vmap[v] for v in reversed(f.verts)])
		nf[part_layer] = dst


def build_mesh(lod, mat):
	rng = np.random.default_rng(SEED)
	b = Builder()
	build_trunk(b, rng)
	build_leg(b, rng, FRONT_LEG, LEG_FR)
	build_leg(b, rng, HIND_LEG, LEG_HR)
	mirror_part(b.bm, b.part, LEG_FR, LEG_FL)
	mirror_part(b.bm, b.part, LEG_HR, LEG_HL)
	build_tail(b, rng)
	if lod <= 2:
		build_ear(b, 1, EAR_L)
		build_ear(b, -1, EAR_R)
	if lod <= 1:
		build_eyes(b)
	if lod <= 2:
		build_antlers(b, rng)
	bmesh.ops.recalc_face_normals(b.bm, faces=b.bm.faces)
	bmesh.ops.triangulate(b.bm, faces=b.bm.faces)
	name = f"{RIG}_lod{lod}"
	me = bpy.data.meshes.new(name)
	b.bm.to_mesh(me)
	b.bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	obj.data.materials.append(mat)
	# Varied face sizes: an edge-collapse decimate with X symmetry on the big organic parts only.
	ratio = {0: 0.62, 1: 0.36, 2: 0.2, 3: 0.11}[lod]
	vg = obj.vertex_groups.new(name="decimate")
	part = me.attributes["part"].data
	soft = set()
	for p in me.polygons:
		if part[p.index].value in (TRUNK, TAIL) + LEG_PARTS:
			soft.update(p.vertices)
	vg.add(list(soft), 1.0, "REPLACE")
	mod = obj.modifiers.new("dec", "DECIMATE")
	mod.decimate_type = "COLLAPSE"
	mod.ratio = ratio
	mod.use_symmetry = True
	mod.symmetry_axis = "X"
	mod.vertex_group = "decimate"
	mod.vertex_group_factor = 1.0
	mod.use_collapse_triangulate = True
	bpy.context.view_layer.objects.active = obj
	obj.select_set(True)
	bpy.ops.object.modifier_apply(modifier="dec")
	obj.vertex_groups.remove(obj.vertex_groups["decimate"])
	for p in obj.data.polygons:
		p.use_smooth = False
	return obj


# ------------------------------------------------------------------------------------------ uv + paint
def unwrap(obj):
	bpy.ops.object.select_all(action="DESELECT")
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.object.mode_set(mode="EDIT")
	bpy.ops.mesh.select_all(action="SELECT")
	bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.012, area_weight=0.0, scale_to_bounds=False)
	bpy.ops.object.mode_set(mode="OBJECT")


def value_noise3(p, scale, seed):
	"""Cheap smooth 3D value noise in [0,1]."""
	x, y, z = p.x * scale, p.y * scale, p.z * scale
	xi, yi, zi = math.floor(x), math.floor(y), math.floor(z)
	fx, fy, fz = x - xi, y - yi, z - zi

	def h(i, j, k):
		n = (i * 73856093) ^ (j * 19349663) ^ (k * 83492791) ^ (seed * 2654435761)
		n = (n ^ (n >> 13)) * 1274126177
		return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0

	def s(t):
		return t * t * (3 - 2 * t)

	sx, sy, sz = s(fx), s(fy), s(fz)
	acc = 0.0
	for i in (0, 1):
		for j in (0, 1):
			for k in (0, 1):
				w = (sx if i else 1 - sx) * (sy if j else 1 - sy) * (sz if k else 1 - sz)
				acc += w * h(xi + i, yi + j, zi + k)
	return acc


def trunk_frame(y):
	"""Interpolated trunk ring at y (barrel only): (centre z, half-height up, half-height down)."""
	rings = sorted([r for r in TRUNK_RINGS if r[0] > -0.40], key=lambda r: r[0])
	if y <= rings[0][0]:
		r = rings[0]
		return r[1], r[4], r[5]
	for a, b in zip(rings, rings[1:]):
		if a[0] <= y <= b[0]:
			t = (y - a[0]) / (b[0] - a[0])
			return a[1] + (b[1] - a[1]) * t, a[4] + (b[4] - a[4]) * t, a[5] + (b[5] - a[5]) * t
	r = rings[-1]
	return r[1], r[4], r[5]


def coat(p, n):
	"""Body coat by height around the barrel (not by facet normal, so the bands run smoothly along the body):
	dark back line, tan sides, lighter lower flank, cream edge, white belly. Boundaries wobble with slow noise."""
	zc, hu, hd = trunk_frame(p.y)
	v = (p.z - zc) / (hu if p.z > zc else hd)
	v += (value_noise3(p, 7.0, 1) - 0.5) * 0.18
	if v > 0.80:
		return C_DORSAL
	if v > 0.05:
		return C_COAT
	if v > -0.50:
		return C_FLANK
	belly = -0.40 < p.y < 0.36
	if v > -0.68 or not belly:
		return C_CREAM if belly else C_FLANK
	return C_WHITE


def neck_coat(p, n):
	"""Neck: coat on the crest and sides, paler toward the throat (by normal - the neck is a slanted tube)."""
	down_fwd = Vector((0, -0.84, -0.54))
	d = n.dot(down_fwd) + (value_noise3(p, 8.0, 2) - 0.5) * 0.25
	if d > 0.55:
		return C_FLANK
	if n.z > 0.55:
		return mix(C_DORSAL, C_FACE, 0.3)
	return mix(C_COAT, C_FACE, 0.3)


def paint(part, p, n):
	if part == EYE:
		return C_EYE
	if part == ANTLER:
		d = p.z - 1.21
		if d < 0.03:
			return C_ANTLER_BASE
		if p.z > 1.43 or (abs(p.x) < 0.13 and p.y < -0.85):
			return C_ANTLER_TIP
		return C_ANTLER
	if part in (EAR_L, EAR_R):
		s = 1.0 if part == EAR_L else -1.0
		fwd = Vector((0.25 * s, -1.0, 0.1)).normalized()
		tip_d = (p - Vector((0.192 * s, -0.645, 1.368)) - HOFF).length
		if tip_d < 0.03:
			return C_EAR_RIM
		if n.dot(fwd) > 0.25:
			# inner cup: pale, a dark rim along the outer edge
			return C_EAR_RIM if abs(p.x) > 0.18 else C_EAR_IN
		return C_FACE
	if part == TAIL:
		if n.y > 0.15:
			return C_DORSAL if abs(p.x) < 0.02 else C_COAT
		return C_WHITE
	if part in LEG_PARTS:
		front = part in (LEG_FR, LEG_FL)
		if p.z < 0.034:
			return C_HOOF
		if p.z < 0.062:
			return C_CREAM if n.y > -0.2 else C_LEG_LOW
		inward = (-n.x if p.x > 0 else n.x)    # normal pointing toward the midline
		knee = 0.30 if front else 0.40
		if p.z > knee + 0.06 and inward > 0.35:
			return C_WHITE if p.z < (0.62 if front else 0.66) else C_CREAM
		if p.z > 0.66:
			return C_COAT if p.z > 0.74 else C_FLANK
		if p.z < knee:
			return C_LEG_LOW
		return C_LEG if n.y < 0.3 else mix(C_LEG, C_CREAM, 0.35)
	# trunk / neck / head
	y, z = p.y, p.z
	if y < -0.65 and z > 0.94:
		# head
		if (p - NOSE_TIP).length < 0.048 and n.y < 0.1:
			return C_NOSE
		if -0.930 < y < -0.892:
			return C_WHITE if n.z < 0.6 else C_FACE        # pale band behind the black nose
		if y < -0.80 and n.z < -0.45:
			return C_WHITE                                  # chin
		ex = Vector((math.copysign(EYE_POS.x, p.x), EYE_POS.y, EYE_POS.z))
		if (p - ex).length < 0.026:
			return C_CREAM                                  # thin pale eye ring
		if n.z > 0.55 and y < -0.71:
			return C_BRIDGE                                 # nose bridge / forehead
		return C_FACE
	if -0.69 < y < -0.585 and 0.94 < z < 1.07:
		# white throat patch (front / under side of the upper neck)
		down_fwd = Vector((0, -0.84, -0.54))
		if n.dot(down_fwd) > 0.55 and abs(p.x) < 0.07:
			return C_WHITE
	if y > 0.52 and abs(p.x) < 0.065 and 0.62 < z < 0.82:
		return C_WHITE                                      # under the tail
	zc, hu, hd = trunk_frame(y)
	if y < -0.40 and z > zc + hu * 0.2 or y < -0.47:
		return neck_coat(p, n)
	return coat(p, n)


def paint_texture(objs, paint_fn=None, grain_sigma=3.2, fill=C_COAT, per_face=False):
	"""Rasterise paint_fn(part, point, face normal) into the active UV layout of objs[0] (create_fox_texture.py reuses
	this with its own paint function, no grain and no "part" attribute: part is then None). per_face: one colour per
	face, sampled at its centre, so colour borders follow the facets."""
	paint_fn = paint_fn or paint
	img = np.zeros((TEX, TEX, 3), np.float64)
	filled = np.zeros((TEX, TEX), bool)
	rng = np.random.default_rng(SEED + 5)
	obj = objs[0]
	me = obj.data
	uv = me.uv_layers.active.data
	part = me.attributes["part"].data if "part" in me.attributes else None
	mw = obj.matrix_world
	nm = mw.to_3x3().inverted().transposed()
	for poly in me.polygons:
		pid = part[poly.index].value if part is not None else None
		n = (nm @ poly.normal).normalized()
		li = list(poly.loop_indices)
		vs = [mw @ me.vertices[me.loops[l].vertex_index].co for l in li]
		uvs = [Vector((uv[l].uv.x * TEX, (1.0 - uv[l].uv.y) * TEX)) for l in li]
		face_col = paint_fn(pid, sum(vs, Vector()) / len(vs), n) if per_face else None
		for k in range(1, len(li) - 1):
			tri = (0, k, k + 1)
			A, B, C = (uvs[i] for i in tri)
			PA, PB, PC = (vs[i] for i in tri)
			x0 = int(max(0, math.floor(min(A.x, B.x, C.x))))
			x1 = int(min(TEX - 1, math.ceil(max(A.x, B.x, C.x))))
			y0 = int(max(0, math.floor(min(A.y, B.y, C.y))))
			y1 = int(min(TEX - 1, math.ceil(max(A.y, B.y, C.y))))
			den = (B.y - C.y) * (A.x - C.x) + (C.x - B.x) * (A.y - C.y)
			if abs(den) < 1e-12:
				continue
			cen = (A + B + C) / 3.0
			for py in range(y0, y1 + 1):
				for px in range(x0, x1 + 1):
					sx, sy = px + 0.5, py + 0.5
					w0 = ((B.y - C.y) * (sx - C.x) + (C.x - B.x) * (sy - C.y)) / den
					w1 = ((C.y - A.y) * (sx - C.x) + (A.x - C.x) * (sy - C.y)) / den
					w2 = 1.0 - w0 - w1
					if min(w0, w1, w2) < -0.08:
						continue
					inside = min(w0, w1, w2) >= 0.0
					if filled[py, px] and not inside:
						continue
					w0c, w1c, w2c = max(w0, 0), max(w1, 0), max(w2, 0)
					s = w0c + w1c + w2c
					pt = (PA * w0c + PB * w1c + PC * w2c) / s
					img[py, px] = face_col if per_face else paint_fn(pid, pt, n)
					filled[py, px] = True
			# tiny triangles: make sure the centroid texel carries this face's colour
			cx, cy = int(min(TEX - 1, max(0, cen.x))), int(min(TEX - 1, max(0, cen.y)))
			if not filled[cy, cx]:
				img[cy, cx] = face_col if per_face else paint_fn(pid, (PA + PB + PC) / 3.0, n)
				filled[cy, cx] = True
	# subtle per-texel grain (low contrast, no hue shift)
	if grain_sigma > 0.0:
		grain = rng.normal(0.0, grain_sigma, (TEX, TEX))[..., None]
		img = img + grain * filled[..., None]
	# dilate into the gutters so point sampling at island edges never reads black
	for _ in range(8):
		if filled.all():
			break
		acc = np.zeros_like(img)
		cnt = np.zeros((TEX, TEX))
		for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
			sh = np.roll(np.roll(img, dy, 0), dx, 1)
			fm = np.roll(np.roll(filled, dy, 0), dx, 1)
			acc += sh * fm[..., None]
			cnt += fm
		grow = (~filled) & (cnt > 0)
		img[grow] = acc[grow] / cnt[grow][..., None]
		filled = filled | grow
	img[~filled] = fill
	out = np.zeros((TEX, TEX, 4), np.uint8)
	out[..., :3] = np.clip(img, 0, 255).astype(np.uint8)
	out[..., 3] = 255
	return out


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


# ------------------------------------------------------------------------------------------ armature + weights
def build_armature():
	arm = bpy.data.armatures.new(RIG + "_skeleton")
	rig = bpy.data.objects.new(RIG + "_skeleton", arm)
	bpy.context.scene.collection.objects.link(rig)
	bpy.context.view_layer.objects.active = rig
	bpy.ops.object.mode_set(mode="EDIT")
	eb = arm.edit_bones
	for name, (h, t, parent) in BONES.items():
		e = eb.new(name)
		e.head = Vector(h)
		e.tail = Vector(t)
		e.roll = 0.0
		if parent:
			e.parent = eb[parent]
			e.use_connect = name.startswith("leg_lower") or name in ("spine", "head")
	bpy.ops.object.mode_set(mode="OBJECT")
	for pb in rig.pose.bones:
		pb.rotation_mode = "QUATERNION"
	return rig


def seg_param(p, a, b):
	ab = b - a
	return (p - a).dot(ab) / ab.length_squared


def vertex_weights(pid, p):
	"""-> {bone: weight} for a rest-pose vertex of part pid."""
	if pid in (EYE, ANTLER):
		return {"head": 1.0}
	if pid in (EAR_L, EAR_R):
		bn = "ear_L" if pid == EAR_L else "ear_R"
		h, t, _ = BONES[bn]
		s = seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.005, 0.03, s)
		return {bn: w, "head": 1.0 - w}
	if pid == TAIL:
		h, t, _ = BONES["tail"]
		s = seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.01, 0.05, s)
		return {"tail": w, "pelvis": 1.0 - w}
	if pid in LEG_PARTS:
		up, lo, _, body = LEGS[LEG_OF_PART[pid]]
		P = Vector(BONES[up][0])
		K = Vector(BONES[up][1])
		wl = smoothstep(K.z + 0.04, K.z - 0.04, p.z)
		wb = smoothstep(P.z - 0.16, P.z, p.z) * (1.0 - wl)
		return {lo: wl, body: wb, up: 1.0 - wl - wb}
	# trunk: plane blends rump -> spine -> neck -> head
	yz = Vector((p.y, p.z))
	nb = Vector((-1.0, 0.3)).normalized()
	d_neck = (yz - Vector((-0.42, 0.80))).dot(nb)
	wn_all = smoothstep(-0.05, 0.16, d_neck)
	hb = Vector((-0.939, 0.344)).normalized()
	d_head = (yz - Vector((-0.652, 1.110))).dot(hb)
	wh = smoothstep(-0.04, 0.03, d_head) * wn_all if p.z > 0.91 else 0.0
	wn = wn_all - wh
	rest = 1.0 - wn_all
	wp = rest * smoothstep(-0.10, 0.22, p.y)
	return {"pelvis": wp, "spine": rest - wp, "neck": wn, "head": wh}


def skin(obj, rig):
	for name in BONES:
		obj.vertex_groups.new(name=name)
	me = obj.data
	part = me.attributes["part"].data
	vpart = {}
	for poly in me.polygons:
		for vi in poly.vertices:
			vpart[vi] = part[poly.index].value
	for v in me.vertices:
		ws = vertex_weights(vpart.get(v.index, TRUNK), v.co)
		for bn, w in ws.items():
			if w > 1e-4:
				obj.vertex_groups[bn].add([v.index], w, "REPLACE")
	obj.parent = rig
	mod = obj.modifiers.new("Armature", "ARMATURE")
	mod.object = rig


# ------------------------------------------------------------------------------------------ posing
def local_quat(pb, q_arm):
	"""Rotation written in armature axes (relative to the parent) -> the bone's local basis quaternion."""
	mr = pb.bone.matrix_local.to_3x3()
	return (mr.inverted() @ q_arm.to_matrix() @ mr).to_quaternion()


def rot(axis, deg):
	return Quaternion(axis, math.radians(deg))


def euler_q(pitch=0.0, yaw=0.0, roll=0.0):
	"""pitch + = nose down (about +X), yaw + = turn to the deer's left (about +Z), roll + = about +Y."""
	return rot(Z, yaw) @ rot(X, pitch) @ rot(Y, roll)


class Poser:
	def __init__(self, rig, bones, legs):
		self.rig = rig
		self.bones = bones
		self.legs = legs
		self.report = {}
		self.rest_feet = {k: Vector(bones[v[1]][1]) for k, v in legs.items()}

	def solve_leg(self, key, foot):
		up, lo, bend, _ = self.legs[key]
		pbu = self.rig.pose.bones[up]
		pbl = self.rig.pose.bones[lo]
		P = pbu.head.copy()
		L1 = pbu.bone.length
		L2 = pbl.bone.length
		d = foot - P
		dist = d.length
		over = max(0.0, dist - (L1 + L2 - 1e-4))
		dist = min(dist, L1 + L2 - 1e-4)
		dist = max(dist, abs(L1 - L2) + 1e-4)
		u = d.normalized()
		# bend direction: forward (-Y) for the carpus, back for the hock, perpendicular to u in the sagittal plane
		fwd = Vector((0, -1, 0))
		perp = (fwd - u * fwd.dot(u)).normalized() * bend
		a = (L1 * L1 + dist * dist - L2 * L2) / (2 * dist)
		h = math.sqrt(max(0.0, L1 * L1 - a * a))
		K = P + u * a + perp * h
		F = P + u * dist
		self.report[key] = max(self.report.get(key, 0.0), over)
		return P, K, F

	def set_bone_dir(self, pb, head, tail):
		m0 = pb.bone.matrix_local.to_3x3()
		rest_dir = m0.col[1].normalized()
		q = rest_dir.rotation_difference((tail - head).normalized())
		m = Matrix.Translation(head) @ (q.to_matrix() @ m0).to_4x4()
		pb.matrix = m

	def apply(self, pose):
		"""pose: {"rot": {bone: Quaternion (armature axes, relative)}, "pelvis_loc": Vector, "feet": {leg: Vector}}"""
		for pb in self.rig.pose.bones:
			pb.rotation_quaternion = Quaternion()
			pb.location = Vector()
		for bn, q in pose.get("rot", {}).items():
			pb = self.rig.pose.bones[bn]
			pb.rotation_quaternion = local_quat(pb, q)
		pl = pose.get("pelvis_loc", Vector())
		pbp = self.rig.pose.bones["pelvis"]
		pbp.location = pbp.bone.matrix_local.to_3x3().inverted() @ pl
		bpy.context.view_layer.update()
		sol = {}
		for key in self.legs:
			foot = pose.get("feet", {}).get(key, self.rest_feet[key])
			sol[key] = self.solve_leg(key, foot)
		for key, (P, K, F) in sol.items():
			self.set_bone_dir(self.rig.pose.bones[self.legs[key][0]], P, K)
		bpy.context.view_layer.update()
		for key, (P, K, F) in sol.items():
			self.set_bone_dir(self.rig.pose.bones[self.legs[key][1]], K, F)
		bpy.context.view_layer.update()


def gait_foot(rest, phase, start, duty, half, lift, center_dy=0.0, peak=0.75):
	"""Foot target in armature space for a stance/swing cycle; the ground slides +Y (backward) during stance."""
	p = (phase - start) % 1.0
	y0 = rest.y + center_dy
	if p < duty:
		s = p / duty
		return Vector((rest.x, y0 - half + 2 * half * s, rest.z))
	s = (p - duty) / (1.0 - duty)
	e = s * s * (3 - 2 * s)
	z = lift * math.sin(math.pi * s ** peak)
	return Vector((rest.x, y0 + half - 2 * half * e, rest.z + max(0.0, z)))


def pulse(t, t0, dur):
	if t0 <= t <= t0 + dur:
		return math.sin(math.pi * (t - t0) / dur)
	return 0.0


# ------------------------------------------------------------------------------------------ deer clips
WALK_SPEED = 1.3
WALK_FRAMES = 22
RUN_SPEED = 8.0
RUN_FRAMES = 12


def rest_feet():
	return {k: Vector(BONES[v[1]][1]) for k, v in LEGS.items()}


def walk_pose(t):
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.56
	half = WALK_SPEED * T * duty * 0.5
	feet = {}
	starts = {"HL": 0.0, "FL": 0.25, "HR": 0.5, "FR": 0.75}
	rf = rest_feet()
	for k, st in starts.items():
		front = k[0] == "F"
		feet[k] = gait_foot(rf[k], ph, st, duty, half, 0.11 if front else 0.075, center_dy=0.10 if front else -0.05,
							peak=0.75 if front else 1.0)
	w = 2 * math.pi * ph
	bob = -0.040 + 0.010 * math.cos(2 * w)
	return {
		"pelvis_loc": Vector((0, 0, bob)),
		"rot": {
			"pelvis": euler_q(pitch=0.0, yaw=1.5 * math.sin(w), roll=2.0 * math.sin(w)),
			"spine": euler_q(yaw=-1.0 * math.sin(w), roll=-1.6 * math.sin(w)),
			"neck": euler_q(pitch=3.0 * math.cos(2 * w + 0.6), yaw=-0.8 * math.sin(w)),
			"head": euler_q(pitch=-2.0 * math.cos(2 * w + 0.6)),
			"tail": euler_q(pitch=4.0, yaw=6.0 * math.sin(w)),
			"ear_L": euler_q(pitch=-4.0 * math.sin(w)),
			"ear_R": euler_q(pitch=4.0 * math.sin(w + 0.7)),
		},
		"feet": feet,
	}


def run_pose(t):
	T = RUN_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.22
	half = RUN_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "HR": 0.07, "FR": 0.34, "FL": 0.43}
	rf = rest_feet()
	feet = {}
	for k, st in starts.items():
		front = k[0] == "F"
		feet[k] = gait_foot(rf[k], ph, st, duty, half, 0.26 if front else 0.22, center_dy=0.085 if front else -0.02)
	w = 2 * math.pi * ph
	pitch = -7.0 * math.cos(w - 2 * math.pi * 0.12)           # front up in the hind stance, down in the front stance
	flex = 9.0 * math.cos(w - 2 * math.pi * 0.85)            # + = gathered (arched back)
	lift = -0.085 + 0.055 * (0.5 + 0.5 * math.cos(w - 2 * math.pi * 0.84))
	return {
		"pelvis_loc": Vector((0, 0, lift)),
		"rot": {
			"pelvis": euler_q(pitch=pitch - flex * 0.5),
			"spine": euler_q(pitch=flex),
			"neck": euler_q(pitch=-0.6 * (pitch + flex * 0.5) + 14.0),
			"head": euler_q(pitch=-6.0),
			"tail": euler_q(pitch=128.0 + 8.0 * math.sin(w)),
			"ear_L": euler_q(pitch=-28.0),
			"ear_R": euler_q(pitch=-28.0),
		},
		"feet": feet,
	}


IDLE_LEN = 4.0
GRAZE_LEN = 3.0
ALERT_LEN = 2.0


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 3 / IDLE_LEN)
	ear_l = pulse(t, 1.00, 0.22)
	ear_r = pulse(t, 2.55, 0.18) + pulse(t, 2.80, 0.18)
	tail = pulse(t, 2.0, 0.35)
	return {
		"pelvis_loc": Vector((0.012 * math.sin(w), 0, -0.008 + 0.002 * breath)),
		"rot": {
			"pelvis": euler_q(roll=1.4 * math.sin(w)),
			"spine": euler_q(pitch=0.5 * breath, roll=-1.0 * math.sin(w)),
			"neck": euler_q(pitch=-2.0 + 2.5 * math.sin(2 * w + 0.3), yaw=9.0 * math.sin(w + 1.0)),
			"head": euler_q(pitch=1.5 * math.sin(2 * w), yaw=4.0 * math.sin(w + 1.6)),
			"tail": euler_q(pitch=2.0 + 30.0 * tail, yaw=24.0 * tail),
			"ear_L": euler_q(pitch=-30.0 * ear_l, yaw=12.0 * ear_l),
			"ear_R": euler_q(pitch=-26.0 * ear_r, yaw=-10.0 * ear_r),
		},
	}


def graze_pose(t):
	w = 2 * math.pi * t / GRAZE_LEN
	nib = math.sin(2 * math.pi * t * 4 / GRAZE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	feet["FL"] = rf["FL"] + Vector((0, -0.09, 0))
	tail = pulse(t, 1.6, 0.4)
	return {
		"pelvis_loc": Vector((0, 0, -0.03)),
		"rot": {
			"pelvis": euler_q(pitch=2.0),
			"spine": euler_q(pitch=10.0),
			"neck": euler_q(pitch=99.0 + 1.5 * nib, yaw=7.0 * math.sin(w)),
			"head": euler_q(pitch=-47.0 + 4.0 * nib, yaw=3.0 * math.sin(w + 0.5)),
			"tail": euler_q(pitch=2.0 + 30.0 * tail, yaw=20.0 * tail),
			"ear_L": euler_q(pitch=-10.0 + 8.0 * math.sin(w)),
			"ear_R": euler_q(pitch=-10.0 + 8.0 * math.sin(w + 2.0)),
		},
		"feet": feet,
	}


def alert_pose(t):
	breath = math.sin(2 * math.pi * t * 2 / ALERT_LEN)
	tw = pulse(t, 1.2, 0.15)
	return {
		"pelvis_loc": Vector((0, 0, -0.012 + 0.0015 * breath)),
		"rot": {
			"pelvis": euler_q(pitch=0.0),
			"spine": euler_q(pitch=-1.0 + 0.3 * breath),
			"neck": euler_q(pitch=-15.0),
			"head": euler_q(pitch=-6.0),
			"tail": euler_q(pitch=40.0),
			"ear_L": euler_q(pitch=18.0 + 6.0 * tw, yaw=-8.0),
			"ear_R": euler_q(pitch=18.0, yaw=8.0),
		},
	}


CLIPS = [
	("idle", IDLE_LEN, idle_pose, 0.3),
	("walk", WALK_FRAMES / FPS, walk_pose, 0.2),
	("run", RUN_FRAMES / FPS, run_pose, 0.15),
	("graze", GRAZE_LEN, graze_pose, 0.4),
	("alert", ALERT_LEN, alert_pose, 0.25),
]


# ------------------------------------------------------------------------------------------ bake / export
def bake_clip(rig, poser, name, length, fn, loop=True, tag=NAME):
	rig.animation_data_create()
	action = bpy.data.actions.new(name)
	action.use_fake_user = True
	rig.animation_data.action = action
	slot = action.slots.new(id_type="OBJECT", name=rig.name)
	rig.animation_data.action_slot = slot
	n = int(round(length * FPS))
	poser.report = {}
	nose_min = 9.0
	for k in range(n + 1):
		t = (k % n) / FPS if loop else k / FPS     # a loop's last frame repeats frame 0 so it closes exactly
		bpy.context.scene.frame_set(k + 1)
		poser.apply(fn(t))
		if os.environ.get("DEER_DEBUG") == name:
			bits = []
			for leg, (up, lo, _, _) in poser.legs.items():
				pb = rig.pose.bones[lo]
				tgt = fn(t).get("feet", {}).get(leg, poser.rest_feet[leg])
				bits.append(f"{leg} {(pb.tail - tgt).length * 100:4.1f}")
			print(f"   f{k:02d} " + " ".join(bits))
		nose_min = min(nose_min, (rig.matrix_world @ rig.pose.bones["head"].tail).z)
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
	over = ", ".join(f"{k} {v * 100:0.1f}cm" for k, v in sorted(poser.report.items()))
	print(f"[{tag}] clip {name}: {n} frames @ {FPS} = {n / FPS:0.3f} s | IK overreach {over} | head-tip min z {nose_min:0.3f}")
	return action, n


def foot_slide_report(rig, poser, name, n, speed, tag=NAME):
	"""World foot positions with the body moving forward at `speed`: max drift while a foot is on the ground."""
	worst = 0.0
	for leg, (up, lo, _, _) in poser.legs.items():
		prev = None
		for k in range(n + 1):
			bpy.context.scene.frame_set(k + 1)
			bpy.context.view_layer.update()
			f = rig.pose.bones[lo].tail.copy()
			world = f + Vector((0, -speed * k / FPS, 0))
			if f.z < 0.003 and prev is not None and prev[1] < 0.003:
				worst = max(worst, (world - prev[0]).length)
			prev = (world, f.z)
	print(f"[{tag}]   {name}: max planted-foot drift per frame {worst * 100:0.2f} cm")


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


VMAT = """// Whitetail deer (Blender/scripts/create_deer.py): 256 px point-filtered texture, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/{name}.png"
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
						source_filename = "models/entities/animations/{model}@{name}.fbx"
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
						filename = "models/entities/{model}.fbx"
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
							exception_list = [ {others} ]
						}}
					}},
"""

LOD_GROUP = """					{{
						_class = "LODGroup"
						switch_threshold = {thr}
						meshes = [ "{mesh}" ]
					}},
"""

VMDL_HEAD = """<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "ModelModifierList"
				children =
				[
					{{
						_class = "ModelModifier_ScaleAndMirror"
						note = "{note}"
						scale = {scale}
						mirror_x = false
						mirror_y = false
						mirror_z = false
						flip_bone_forward = false
						swap_left_and_right_bones = false
					}},
				]
			}},
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[
{remaps}						]
						use_global_default = {use_global}
						global_default_material = "{material}"
					}},
				]
			}},
{physics}			{{
				_class = "RenderMeshList"
				children =
				[
"""

VMDL_MID = """				]
			}},
			{{
				_class = "LODGroupList"
				children =
				[
"""

VMDL_ANIMS = """				]
			}},
			{{
				_class = "AnimationList"
				children =
				[
"""

VMDL_TAIL = """				]
				default_root_bone_name = "root"
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""

PHYSICS_FROM_RENDER = """			{
				_class = "PhysicsShapeList"
				children =
				[
					{
						_class = "PhysicsMeshFromRender"
						parent_bone = ""
						surface_prop = "default"
						collision_tags = "solid"
					},
				]
			},
"""


def write_vmdl(model, meshes, thresholds, clips, scale, note, material, remap_from, use_global=False, physics=""):
	remaps = ""
	if remap_from:
		remaps = (f"\t\t\t\t\t\t\t{{\n\t\t\t\t\t\t\t\tfrom = \"{remap_from}\"\n\t\t\t\t\t\t\t\tto = \"{material}\"\n"
				  f"\t\t\t\t\t\t\t}},\n")
	body = VMDL_HEAD.format(note=note, scale=scale, remaps=remaps, use_global="true" if use_global else "false",
							material=material, physics=physics)
	for m in meshes:
		others = ", ".join(f"\"{o}\"" for o in meshes if o != m)
		body += RENDER_MESH.format(mesh=m, model=model, others=others)
	if len(meshes) > 1:
		body += VMDL_MID.format()
		for m, thr in zip(meshes, thresholds):
			body += LOD_GROUP.format(mesh=m, thr=f"{thr:0.1f}")
		body += VMDL_ANIMS.format()
	else:
		body += VMDL_ANIMS.format()
	for clip in clips:
		name, fade = clip[0], clip[1]
		looping = clip[2] if len(clip) > 2 else True
		body += ANIM_FILE.format(name=name, model=model, fade=fade, looping="true" if looping else "false")
	body += VMDL_TAIL.format()
	with open(os.path.join(MODEL_DIR, model + ".vmdl"), "w", newline="\n") as f:
		f.write(body)


# ------------------------------------------------------------------------------------------ main
def lod_uvs_from(src, dst):
	"""Give a LOD mesh UVs that sample the LOD0 texture: each LOD face takes the UVs of the LOD0 surface point nearest
	to its own corners (BVH closest point -> barycentric UV on that LOD0 triangle), so no extra texture space is used."""
	from mathutils.bvhtree import BVHTree
	import bmesh as _bm
	sb = _bm.new()
	sb.from_mesh(src.data)
	_bm.ops.triangulate(sb, faces=sb.faces)
	uvl = sb.loops.layers.uv.active
	pl = sb.faces.layers.int.get("part")
	sb.faces.ensure_lookup_table()
	tree = BVHTree.FromBMesh(sb)
	me = dst.data
	if not me.uv_layers:
		me.uv_layers.new(name="UVMap")
	uv = me.uv_layers.active.data
	dpart = me.attributes["part"].data
	for poly in me.polygons:
		pid = dpart[poly.index].value
		cen = poly.center
		for li in poly.loop_indices:
			co = me.vertices[me.loops[li].vertex_index].co
			q = co * 0.85 + cen * 0.15          # pull toward the face centre so the corner lands on the same part
			best = None
			for cand in tree.find_nearest_range(q, 0.08) or []:
				f = sb.faces[cand[2]]
				if f[pl] == pid and (best is None or cand[3] < best[3]):
					best = cand
			if best is None:
				best = tree.find_nearest(q)
			loc, _, fi, _ = best
			f = sb.faces[fi]
			a, b_, c = (l.vert.co for l in f.loops)
			from mathutils.geometry import barycentric_transform
			uva, uvb, uvc = (Vector((l[uvl].uv.x, l[uvl].uv.y, 0)) for l in f.loops)
			r = barycentric_transform(loc, a, b_, c, uva, uvb, uvc)
			uv[li].uv = (r.x, r.y)
	sb.free()


def build_scene():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	sc = bpy.context.scene
	sc.unit_settings.system = "METRIC"
	sc.unit_settings.scale_length = 1.0
	sc.render.fps = FPS
	mat = bpy.data.materials.new(NAME)
	rig = build_armature()
	meshes = [build_mesh(l, mat) for l in range(4)]
	unwrap(meshes[0])
	for m in meshes[1:]:
		lod_uvs_from(meshes[0], m)
	for m in meshes:
		skin(m, rig)
	return rig, meshes


def main(check_only=False):
	rig, meshes = build_scene()
	for m in meshes:
		print(f"[{NAME}] {m.name}: {len(m.data.vertices)} verts, {len(m.data.polygons)} tris")
	poser = Poser(rig, BONES, LEGS)
	if not check_only:
		os.makedirs(MODEL_DIR, exist_ok=True)
		os.makedirs(ANIM_DIR, exist_ok=True)
		os.makedirs(MAT_DIR, exist_ok=True)
		os.makedirs(os.path.dirname(BLEND), exist_ok=True)
		write_png(os.path.join(MAT_DIR, NAME + ".png"), paint_texture(meshes))
		with open(os.path.join(MAT_DIR, NAME + ".vmat"), "w", newline="\n") as f:
			f.write(VMAT.replace("{name}", NAME))
		bpy.ops.object.select_all(action="DESELECT")
		for o in [rig] + meshes:
			o.select_set(True)
		bpy.context.view_layer.objects.active = rig
		bpy.ops.export_scene.fbx(
			filepath=os.path.join(MODEL_DIR, NAME + ".fbx"), use_selection=True, object_types={"ARMATURE", "MESH"},
			add_leaf_bones=False, bake_anim=False, mesh_smooth_type="OFF", path_mode="STRIP", embed_textures=False,
			apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", bake_space_transform=False,
			axis_forward="-Z", axis_up="Y", use_armature_deform_only=False)
	speeds = {"walk": WALK_SPEED, "run": RUN_SPEED}
	for name, length, fn, _ in CLIPS:
		action, n = bake_clip(rig, poser, name, length, fn)
		foot_slide_report(rig, poser, name, n, speeds.get(name, 0.0))
		if not check_only:
			export_clip(rig, os.path.join(ANIM_DIR, f"{NAME}@{name}.fbx"))
	if check_only:
		return
	write_vmdl(NAME, [m.name for m in meshes], [0.0, 400.0, 800.0, 1200.0], [(c[0], c[3]) for c in CLIPS], 0.4,
			   "Blender metres export as 100 u/m; 0.4 gives the project's 40 u/m for mesh and clips (shoulder ~39 u).",
			   f"materials/entities/{NAME}.vmat", f"{NAME}.vmat")
	rig.animation_data.action = None
	for pb in rig.pose.bones:
		pb.rotation_quaternion = Quaternion()
		pb.location = Vector()
	if os.path.exists(BLEND):
		os.remove(BLEND)
	bpy.ops.wm.save_as_mainfile(filepath=BLEND)
	m = meshes[0]
	bb = [m.matrix_world @ Vector(c) for c in m.bound_box]
	lo = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
	hi = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
	print(f"[{NAME}] bounds {lo} .. {hi}")


if __name__ == "__main__":
	argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
	main(check_only=bool(argv) and argv[0] == "check")
