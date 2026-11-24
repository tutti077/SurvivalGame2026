"""Low-poly giant forest spider (redwood forest predator): skinned model, six clips, 256 px texture, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_giant_spider.py"
    ... --python "Blender/scripts/create_giant_spider.py" -- check           build + bake, print the IK / foot report, write nothing
    ... --python "Blender/scripts/create_giant_spider.py" -- render <dir>    also render previews + per-clip frame sequences

Writes
  Assets/models/entities/giantspider_v1.fbx                     skinned mesh (giantspider_lod0..3) + armature, bind pose
  Assets/models/entities/animations/giantspider_v1@<clip>.fbx   one clip each (armature only)
  Assets/models/entities/giantspider_v1.vmdl                    ScaleAndMirror 0.4 (Blender metres -> 40 u/m), LODs, AnimationList
  Assets/materials/entities/giantspider_v1.png + .vmat          256 x 256, pixel_lit, point filtered, faceted
  Blender/blenderprojects/entities/giantspider.blend
File names carry the model version (NAME); skeleton and LOD mesh names stay "giantspider_*" (RIG) across versions.

Scale: a giant forest spider, 1.95 m leg span tip to tip (L1 -> R4 diagonal; 1.76 m straight across the side legs),
body 1.07 m long (chelicerae to spinnerets), abdomen top 0.76 m, knees 0.80 m. Modelled in world metres; the FBX reads
100 u per Blender metre and the vmdl 0.4 makes that 40 u/m (knees ~32 u, span ~78 u).

Frame: Blender -Y = forward (engine +X), Blender +X = the spider's left (engine +Y), Z up, origin on the ground under the
cephalothorax.

Rig: root -> pelvis (cephalothorax) -> head -> chel_L / chel_R -> fang_L / fang_R, head -> palp_L / palp_R;
pelvis -> abdomen; pelvis -> leg_femur_<side><n> -> leg_tibia_* -> leg_tarsus_* for the eight legs (the coxa is part of the
body). Each leg is a 3-segment chain: femur (trochanter -> knee, rising), tibia (knee -> ankle, falling), tarsus
(metatarsus + tarsus, ankle -> foot). The ankle is placed from the foot along a steep metatarsus direction, then the
femur / tibia pair is solved with analytic two-bone IK (knee up), so a planted foot never slides.

Gait: alternating tetrapod - group A = L1 R2 L3 R4, group B = R1 L2 R3 L4, half a cycle apart.

Clips (SkinnedModelRenderer.Sequence.Name, what AnimalBrain.Animation.cs plays), 30 fps:
  idle    4.00 s   loop   abdomen breathing, palps twitching, a front-leg tap, small weight shifts
  walk    0.600 s  loop   tetrapod walk at 1.2 m/s        -> 0.720 m per cycle
  sneak   0.800 s  loop   low creeping tetrapod at 1.4 m/s -> 1.120 m per cycle (body 9 cm lower, front legs reaching)
  run     0.267 s  loop   tetrapod sprint at 7.0 m/s      -> 1.867 m per cycle
  graze   3.00 s   loop   feeding: body low, front legs holding, chelicerae + palps working
  bite    0.50 s   once   rear up on the back legs, front legs raised, lunge-bite with the fangs striking at 0.30 s
Speeds to set in data/animal_behaviors.json "giantSpider": walkSpeedMps 1.2, sneakSpeedMps 1.4, runSpeedMps 7.0 (the brain
plays a clip at actual / authored speed, clamped 0.6-1.8).
"""
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector, Quaternion, Matrix

sys.dont_write_bytecode = True    # importing create_deer must not leave a __pycache__ entry
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_deer as cd  # noqa: E402
from create_deer import Builder, euler_q, pulse, smoothstep, section, sym_jitter, mirror_part, value_noise3, X, Y, Z  # noqa: E402

ROOT = cd.ROOT
MODEL_DIR = cd.MODEL_DIR
ANIM_DIR = cd.ANIM_DIR
MAT_DIR = cd.MAT_DIR
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "giantspider.blend")
NAME = "giantspider_v1"   # file version (next iteration: giantspider_v2, delete these files)
RIG = "giantspider"
FPS = cd.FPS
SEED = 67
TEX = 256

CEPH, ABDO, EYE, CHEL_R, CHEL_L, FANG_R, FANG_L, PALP_R, PALP_L = range(9)
LEG_R = [10, 11, 12, 13]          # R1..R4
LEG_L = [20, 21, 22, 23]          # L1..L4
LEG_PARTS = tuple(LEG_R + LEG_L)
LEG_KEY = {p: ("R" if p < 20 else "L") + str((p % 10) + 1) for p in LEG_PARTS}

# ------------------------------------------------------------------------------------------ proportions
# Cephalothorax + abdomen lofts, rear -> front: (y, z, tilt deg, half-width, half-height up, half-height down).
CEPH_RINGS = [
	(0.085, 0.425, 0, 0.055, 0.045, 0.045),
	(0.045, 0.432, 0, 0.130, 0.075, 0.070),
	(-0.035, 0.442, 0, 0.165, 0.098, 0.080),
	(-0.125, 0.448, 0, 0.176, 0.104, 0.085),
	(-0.215, 0.440, 0, 0.158, 0.094, 0.080),
	(-0.285, 0.426, 0, 0.122, 0.080, 0.070),
	(-0.325, 0.412, 0, 0.082, 0.060, 0.056),
]
CEPH_BACK = Vector((0.0, 0.105, 0.425))
CEPH_FRONT = Vector((0.0, -0.346, 0.404))
ABDO_RINGS = [
	(0.735, 0.452, 28, 0.050, 0.050, 0.050),
	(0.690, 0.488, 22, 0.125, 0.118, 0.108),
	(0.600, 0.518, 14, 0.198, 0.168, 0.150),
	(0.495, 0.538, 6, 0.238, 0.208, 0.180),
	(0.375, 0.546, 0, 0.250, 0.218, 0.190),
	(0.255, 0.532, -8, 0.228, 0.200, 0.170),
	(0.160, 0.500, -16, 0.170, 0.150, 0.124),
	(0.110, 0.468, -26, 0.068, 0.060, 0.060),
]
ABDO_REAR = Vector((0.0, 0.762, 0.440))
ABDO_FRONT = Vector((0.0, 0.090, 0.452))
RING_ANGLES = [90, 68, 45, 22, 0, -22, -45, -68, -90, -112, -135, -158, 180, 158, 135, 112]

# Right legs (x < 0), the left ones mirrored. hip (coxa base on the body), trochanter (femur pivot), foot (ground).
# The knee and ankle are derived: the knee rises KNEE_UP over the trochanter at KNEE_OUT of the way to the foot,
# the ankle sits ANKLE_UP above the foot, ANKLE_IN back toward the body.
LEG_SPEC = {
	1: dict(hip=(-0.110, -0.250, 0.405), tro=(-0.165, -0.290, 0.395), foot=(-0.620, -0.790, 0.014), knee_up=0.370, knee_out=0.36, ankle_up=0.300, ankle_in=0.120, r=0.044),
	2: dict(hip=(-0.145, -0.165, 0.400), tro=(-0.210, -0.185, 0.392), foot=(-0.880, -0.390, 0.014), knee_up=0.405, knee_out=0.38, ankle_up=0.300, ankle_in=0.115, r=0.046),
	3: dict(hip=(-0.150, -0.075, 0.400), tro=(-0.215, -0.060, 0.392), foot=(-0.880, 0.180, 0.014), knee_up=0.395, knee_out=0.38, ankle_up=0.290, ankle_in=0.110, r=0.045),
	4: dict(hip=(-0.120, 0.010, 0.402), tro=(-0.175, 0.050, 0.394), foot=(-0.650, 0.720, 0.014), knee_up=0.375, knee_out=0.37, ankle_up=0.290, ankle_in=0.115, r=0.044),
}


def leg_points(n, side=-1):
	"""Rest chain for leg n on a side (-1 = right, +1 = left): hip, trochanter, knee, ankle, foot."""
	s = LEG_SPEC[n]
	m = Vector((-side, 1, 1)) if side > 0 else Vector((1, 1, 1))

	def V(t):
		return Vector((t[0] * m.x, t[1], t[2]))
	H, T, F = V(s["hip"]), V(s["tro"]), V(s["foot"])
	h = (F - T).to_2d()
	dh = h.length
	hn = h.normalized()
	K = T + Vector((hn.x, hn.y, 0)) * (dh * s["knee_out"]) + Vector((0, 0, s["knee_up"]))
	A = F - Vector((hn.x, hn.y, 0)) * s["ankle_in"] + Vector((0, 0, s["ankle_up"]))
	return H, T, K, A, F


BONES = {
	"root": ((0, 0, 0), (0, -0.15, 0), None),
	"pelvis": ((0, 0.060, 0.430), (0, -0.100, 0.430), "root"),
	"head": ((0, -0.100, 0.430), (0, -0.300, 0.440), "pelvis"),
	"abdomen": ((0, 0.100, 0.462), (0, 0.700, 0.480), "pelvis"),
	"chel_R": ((-0.042, -0.312, 0.398), (-0.047, -0.372, 0.272), "head"),
	"chel_L": ((0.042, -0.312, 0.398), (0.047, -0.372, 0.272), "head"),
	"fang_R": ((-0.047, -0.372, 0.272), (-0.014, -0.356, 0.238), "chel_R"),
	"fang_L": ((0.047, -0.372, 0.272), (0.014, -0.356, 0.238), "chel_L"),
	"palp_R": ((-0.078, -0.300, 0.385), (-0.110, -0.455, 0.215), "head"),
	"palp_L": ((0.078, -0.300, 0.385), (0.110, -0.455, 0.215), "head"),
}
LEGS = {}
for _n in (1, 2, 3, 4):
	for _side, _tag in ((-1, "R"), (1, "L")):
		_H, _T, _K, _A, _F = leg_points(_n, _side)
		key = f"{_tag}{_n}"
		BONES[f"leg_femur_{key}"] = (tuple(_T), tuple(_K), "pelvis")
		BONES[f"leg_tibia_{key}"] = (tuple(_K), tuple(_A), f"leg_femur_{key}")
		BONES[f"leg_tarsus_{key}"] = (tuple(_A), tuple(_F), f"leg_tibia_{key}")
		LEGS[key] = (f"leg_femur_{key}", f"leg_tibia_{key}", f"leg_tarsus_{key}")
GROUP_A = ("L1", "R2", "L3", "R4")
GROUP_B = ("R1", "L2", "R3", "L4")

# eyes: (x, y, z, radius) on the right; mirrored. Anterior medians largest.
EYES = [(-0.030, -0.322, 0.478, 0.022), (-0.076, -0.304, 0.468, 0.015), (-0.036, -0.284, 0.500, 0.014),
		(-0.082, -0.272, 0.488, 0.015)]

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
C_DARK = (40, 33, 27)
C_BASE = (58, 47, 37)
C_MID = (74, 60, 46)
C_BAND = (100, 82, 62)
C_MARK = (112, 90, 64)
C_VENTRAL = (34, 28, 24)
C_EYE = (14, 12, 12)
C_FANG = (38, 27, 20)
C_FANG_TIP = (84, 60, 40)


# ------------------------------------------------------------------------------------------ geometry
def densify(spec, steps=3):
	"""Insert interpolated rings (smoothstep between rows) so the body decimate has surface to collapse unevenly."""
	out = []
	for a, b in zip(spec, spec[1:]):
		for i in range(steps):
			t = i / steps
			out.append(tuple(a[j] + (b[j] - a[j]) * t for j in range(6)))
	out.append(spec[-1])
	return out


def build_lofts(b, rng):
	for spec, part, cap0, cap1 in ((CEPH_RINGS, CEPH, CEPH_BACK, CEPH_FRONT), (ABDO_RINGS, ABDO, ABDO_REAR, ABDO_FRONT)):
		rings = []
		for (y, z, tilt, hw, hu, hd) in densify(spec):
			t = math.radians(tilt)
			up = Vector((0, math.sin(t), math.cos(t)))
			jit = sym_jitter(rng, RING_ANGLES, 0.06)
			rings.append(section(Vector((0, y, z)), X, up, hw, hu, hd, RING_ANGLES, jit))
		b.loft(rings, part, cap0=cap0, cap1=cap1)


def tube(b, pts, radii, part, k, rng=None, twist=0.4, tip=True):
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
			ang = 2 * math.pi * j / k + twist * i
			r = radii[i] * (1.0 + (rng.uniform(-0.10, 0.10) if rng is not None else 0.0))
			ring.append(p + e1 * (math.cos(ang) * r) + e2 * (math.sin(ang) * r))
		rings.append(ring)
	cap1 = pts[-1] + (pts[-1] - pts[-2]).normalized() * radii[-1] * 1.6 if tip else None
	b.loft(rings, part, cap0=None, cap1=cap1)


def spine(b, base, normal, hair, size, length, part, rng):
	"""A faceted bristle: 3-sided pyramid rooted just inside the surface, tip along `hair`."""
	n = normal.normalized()
	h = hair.normalized()
	t1 = h - n * h.dot(n)
	if t1.length < 1e-4:
		t1 = n.orthogonal()
	t1.normalize()
	t2 = n.cross(t1).normalized()
	c = base - n * 0.006
	tri = [c + t1 * size, c + (t2 * 0.87 - t1 * 0.5) * size, c + (-t2 * 0.87 - t1 * 0.5) * size]
	tri = [q + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * size * 0.1 for q in tri]
	tip = base + h * length
	b.closed(tri + [tip], [(0, 1, 3), (1, 2, 3), (2, 0, 3)], part)


def leg_chain(n, side=-1):
	"""Sample points + radii along a leg: hip -> trochanter -> femur -> knee knob -> tibia -> ankle -> tarsus -> foot."""
	H, T, K, A, F = leg_points(n, side)
	r = LEG_SPEC[n]["r"]
	pts = [H, T, T + (K - T) * 0.35, T + (K - T) * 0.75, K - (K - T).normalized() * 0.03, K + (A - K).normalized() * 0.03,
		   K + (A - K) * 0.45, A - (A - K).normalized() * 0.02, A + (F - A).normalized() * 0.02, A + (F - A) * 0.5, F]
	radii = [r * 1.05, r * 0.95, r * 1.00, r * 0.92, r * 0.88, r * 0.84, r * 0.74, r * 0.60, r * 0.52, r * 0.38, r * 0.20]
	return pts, radii


def build_leg(b, rng, n, lod):
	part = LEG_R[n - 1]
	pts, radii = leg_chain(n)
	if lod >= 2:
		keep = [0, 1, 3, 4, 6, 8, 10]
		pts = [pts[i] for i in keep]
		radii = [radii[i] for i in keep]
	k = {0: 6, 1: 6, 2: 5, 3: 4}[lod]
	tube(b, pts, radii, part, k, rng=rng)
	if lod <= 1:
		# bristles: a few on the femur and tibia, laid along the leg toward the foot and outward
		H, T, K, A, F = leg_points(n)
		count = 6 if lod == 0 else 3
		for seg_a, seg_b, rr in ((T, K, LEG_SPEC[n]["r"] * 0.9), (K, A, LEG_SPEC[n]["r"] * 0.7)):
			d = (seg_b - seg_a)
			for i in range(count):
				t = 0.18 + 0.7 * (i + rng.uniform(-0.2, 0.2)) / count
				ang = rng.uniform(0, 2 * math.pi)
				dn = d.normalized()
				u = Z if abs(dn.dot(Z)) < 0.9 else Y
				e1 = dn.cross(u).normalized()
				e2 = dn.cross(e1).normalized()
				nrm = e1 * math.cos(ang) + e2 * math.sin(ang)
				p = seg_a + d * t + nrm * rr
				spine(b, p, nrm, dn * 0.8 + nrm * 0.6, rr * 0.45, rr * rng.uniform(1.1, 1.7), part, rng)


def build_chelicerae(b, rng, lod):
	k = 6 if lod <= 1 else 4
	h, t, _ = BONES["chel_R"]
	h, t = Vector(h), Vector(t)
	pts = [h + Vector((0, 0.015, 0.02)), h, h + (t - h) * 0.5 + Vector((-0.006, -0.008, 0)), t]
	tube(b, pts, [0.036, 0.040, 0.036, 0.026], CHEL_R, k, rng=rng, tip=False)
	fh, ft, _ = BONES["fang_R"]
	fh, ft = Vector(fh), Vector(ft)
	mid = fh + (ft - fh) * 0.5 + Vector((0, -0.010, -0.006))
	tube(b, [fh + (fh - ft).normalized() * 0.01, mid, ft], [0.016, 0.011, 0.005], FANG_R, 4 if lod <= 1 else 3, rng=rng)


def build_palp(b, rng, lod):
	h, t, _ = BONES["palp_R"]
	h, t = Vector(h), Vector(t)
	knee = h + (t - h) * 0.45 + Vector((-0.018, 0, 0.04))
	pts = [h, h + (knee - h) * 0.5, knee, knee + (t - knee) * 0.55, t]
	tube(b, pts, [0.022, 0.021, 0.019, 0.016, 0.012], PALP_R, 5 if lod <= 1 else 4, rng=rng)


def build_eyes(b):
	for (x, y, z, r) in EYES:
		for s in (1, -1):
			c = Vector((x * -s, y, z))
			o = Vector((c.x * 3.0, -1.0, 0.9)).normalized()
			t1 = o.cross(Z).normalized()
			t2 = o.cross(t1).normalized()
			ring = [c + (t1 * math.cos(a) + t2 * math.sin(a)) * r for a in (0.3, 1.9, 3.4, 4.9)]
			apex = c + o * r * 0.8
			back = c - o * r * 0.9
			b.closed(ring + [apex, back], [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (1, 0, 5), (2, 1, 5), (3, 2, 5), (0, 3, 5)], EYE)


def bm_to_object(b, name, mat):
	bmesh.ops.recalc_face_normals(b.bm, faces=b.bm.faces)
	bmesh.ops.triangulate(b.bm, faces=b.bm.faces)
	me = bpy.data.meshes.new(name)
	b.bm.to_mesh(me)
	b.bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	obj.data.materials.append(mat)
	return obj


def build_mesh(lod, mat):
	rng = np.random.default_rng(SEED)
	# body lofts on their own: the collapse decimate gives them varied face sizes without the legs' face count
	# steering the ratio (decimating the joined mesh crushed the body to an octahedron)
	body = Builder()
	build_lofts(body, rng)
	body_obj = bm_to_object(body, f"{RIG}_lod{lod}", mat)
	mod = body_obj.modifiers.new("dec", "DECIMATE")
	mod.decimate_type = "COLLAPSE"
	mod.ratio = {0: 0.42, 1: 0.26, 2: 0.15, 3: 0.09}[lod]
	mod.use_symmetry = True
	mod.symmetry_axis = "X"
	mod.use_collapse_triangulate = True
	bpy.ops.object.select_all(action="DESELECT")
	bpy.context.view_layer.objects.active = body_obj
	body_obj.select_set(True)
	bpy.ops.object.modifier_apply(modifier="dec")
	b = Builder()
	for n in (1, 2, 3, 4):
		build_leg(b, np.random.default_rng(SEED + n), n, lod)
	build_chelicerae(b, rng, lod)
	build_palp(b, rng, lod)
	for n in (1, 2, 3, 4):
		mirror_part(b.bm, b.part, LEG_R[n - 1], LEG_L[n - 1])
	mirror_part(b.bm, b.part, CHEL_R, CHEL_L)
	mirror_part(b.bm, b.part, FANG_R, FANG_L)
	mirror_part(b.bm, b.part, PALP_R, PALP_L)
	if lod <= 2:
		build_eyes(b)
	rest_obj = bm_to_object(b, f"{RIG}_lod{lod}_rest", mat)
	bpy.ops.object.select_all(action="DESELECT")
	rest_obj.select_set(True)
	body_obj.select_set(True)
	bpy.context.view_layer.objects.active = body_obj
	bpy.ops.object.join()
	obj = body_obj
	obj.data.name = f"{RIG}_lod{lod}"
	if len(obj.data.materials) > 1:
		while len(obj.data.materials) > 1:
			obj.data.materials.pop()
		for p in obj.data.polygons:
			p.material_index = 0
	for p in obj.data.polygons:
		p.use_smooth = False
	return obj


def pack_tight(obj):
	bpy.ops.object.select_all(action="DESELECT")
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.object.mode_set(mode="EDIT")
	bpy.ops.mesh.select_all(action="SELECT")
	bpy.ops.uv.select_all(action="SELECT")
	bpy.ops.uv.pack_islands(rotate=True, margin=0.004, shape_method="CONCAVE")
	bpy.ops.object.mode_set(mode="OBJECT")


# ------------------------------------------------------------------------------------------ paint
def leg_param(key, p):
	"""(segment index 0 hip..3 tarsus, arc metres from the trochanter, arc of knee, arc of ankle) for a point near leg key."""
	n = int(key[1])
	H, T, K, A, F = leg_points(n, -1 if key[0] == "R" else 1)
	segs = [(H, T), (T, K), (K, A), (A, F)]
	best = None
	arc0 = 0.0
	arcs = []
	for i, (a, c) in enumerate(segs):
		L = (c - a).length
		t = max(0.0, min(1.0, (p - a).dot(c - a) / (L * L)))
		d = (p - (a + (c - a) * t)).length
		if best is None or d < best[0]:
			best = (d, i, arc0 + t * L)
		arcs.append(arc0)
		arc0 += L
	return best[1], best[2], arcs


def paint(part, p, n):
	mott = value_noise3(p, 26.0, 3)
	if part == EYE:
		return C_EYE
	if part in (FANG_R, FANG_L):
		_, ft, _ = BONES["fang_R"]
		tipd = (Vector((-abs(p.x), p.y, p.z)) - Vector(ft)).length
		return C_FANG_TIP if tipd < 0.014 else C_FANG
	if part in (CHEL_R, CHEL_L):
		return cd.mix(C_DARK, C_BASE, 0.5 + 0.5 * (mott - 0.5))
	if part in LEG_PARTS or part in (PALP_R, PALP_L):
		if part in (PALP_R, PALP_L):
			return C_BAND if value_noise3(p, 40.0, 5) > 0.62 else (C_BASE if mott > 0.45 else C_DARK)
		seg, arc, arcs = leg_param(LEG_KEY[part], p)
		# low-contrast bands: a pale ring just before the knee and the ankle, a darker one after
		k_arc, a_arc = arcs[2], arcs[3]
		for joint in (k_arc, a_arc):
			if joint - 0.075 < arc < joint - 0.015:
				return C_BAND if mott > 0.30 else C_MID
			if joint + 0.015 < arc < joint + 0.06:
				return C_DARK
		if seg == 3 and arc > a_arc + 0.20:
			return C_DARK                                 # dark tarsus tips
		if n.z < -0.55:
			return C_VENTRAL
		return C_MID if mott > 0.62 else (C_BASE if mott > 0.32 else C_DARK)
	if part == CEPH:
		# carapace: mid tone in the middle, dark margin, faint radial striae and the dark fovea pit
		r = math.hypot(p.x / 0.17, (p.y + 0.12) / 0.22)
		if n.z < -0.3:
			return C_VENTRAL
		if abs(p.x) < 0.012 and -0.07 < p.y < -0.02:
			return C_DARK
		ang = math.atan2(p.x, p.y + 0.06)
		stria = 0.5 + 0.5 * math.cos(ang * 8.0)
		if r > 0.82:
			return C_DARK
		if stria > 0.82 and r > 0.25:
			return C_BASE
		return C_MID if mott > 0.35 else C_BASE
	# abdomen: dull folium - a chain of chevrons down the back, mottled flanks, dark belly
	if n.z < -0.45:
		return C_VENTRAL
	t = (p.y - 0.16) / 0.55
	if 0.0 < t < 1.0 and n.z > 0.35:
		width = 0.090 * (1.0 - 0.6 * t)
		phase = (t * 5.0) % 1.0
		chev = width * (0.55 + 0.45 * phase)
		if abs(p.x) < chev and abs(abs(p.x) - chev) < 0.03 + 0.012 * mott:
			return C_MARK
		if abs(p.x) < width * 0.35:
			return C_MID
	if mott > 0.66:
		return C_MID
	return C_BASE if mott > 0.30 else C_DARK


# ------------------------------------------------------------------------------------------ weights
def vertex_weights(pid, p):
	if pid == EYE:
		return {"head": 1.0}
	if pid in (CHEL_R, CHEL_L):
		bn = "chel_R" if pid == CHEL_R else "chel_L"
		h, _, _ = BONES[bn]
		w = smoothstep(Vector(h).z + 0.01, Vector(h).z - 0.03, p.z)
		return {bn: w, "head": 1.0 - w}
	if pid in (FANG_R, FANG_L):
		return {"fang_R" if pid == FANG_R else "fang_L": 1.0}
	if pid in (PALP_R, PALP_L):
		bn = "palp_R" if pid == PALP_R else "palp_L"
		h, t, _ = BONES[bn]
		s = cd.seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.005, 0.03, s)
		return {bn: w, "head": 1.0 - w}
	if pid in LEG_PARTS:
		key = LEG_KEY[pid]
		fem, tib, tar = LEGS[key]
		_, arc, arcs = leg_param(key, p)
		bw = 0.022
		t0 = smoothstep(arcs[1] - bw, arcs[1] + bw, arc)
		t1 = smoothstep(arcs[2] - bw, arcs[2] + bw, arc)
		t2 = smoothstep(arcs[3] - bw, arcs[3] + bw, arc)
		return {"pelvis": 1.0 - t0, fem: t0 - t1, tib: t1 - t2, tar: t2}
	if pid == ABDO:
		w = smoothstep(0.08, 0.16, p.y)
		return {"abdomen": w, "pelvis": 1.0 - w}
	# cephalothorax: front third rides the head bone
	w = smoothstep(-0.18, -0.26, p.y)
	return {"head": w, "pelvis": 1.0 - w}


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
		for bn, w in vertex_weights(vpart.get(v.index, CEPH), v.co).items():
			if w > 1e-4:
				obj.vertex_groups[bn].add([v.index], w, "REPLACE")
	obj.parent = rig
	mod = obj.modifiers.new("Armature", "ARMATURE")
	mod.object = rig


def build_armature():
	saved = cd.BONES, cd.RIG
	cd.BONES, cd.RIG = BONES, RIG
	try:
		return cd.build_armature()
	finally:
		cd.BONES, cd.RIG = saved


# ------------------------------------------------------------------------------------------ posing
class SpiderPoser:
	"""Like create_deer.Poser, for 3-bone legs: ankle from the foot along the metatarsus, femur / tibia by 2-bone IK."""

	def __init__(self, rig):
		self.rig = rig
		self.legs = LEGS
		self.report = {}
		self.rest_feet = {k: Vector(BONES[v[2]][1]) for k, v in LEGS.items()}
		self.tarsus_len = {k: (Vector(BONES[v[2]][1]) - Vector(BONES[v[2]][0])).length for k, v in LEGS.items()}
		self.tarsus_pitch = {}
		for k, v in LEGS.items():
			A, F = Vector(BONES[v[2]][0]), Vector(BONES[v[2]][1])
			d = A - F
			self.tarsus_pitch[k] = math.atan2(d.z, d.to_2d().length)

	def solve_leg(self, key, foot, tarsus_dir=None):
		fem, tib, tar = self.legs[key]
		pbf = self.rig.pose.bones[fem]
		P = pbf.head.copy()
		L1 = pbf.bone.length
		L2 = self.rig.pose.bones[tib].bone.length
		L3 = self.tarsus_len[key]
		if tarsus_dir is None:
			h = (P - foot).to_2d()
			h = h.normalized() if h.length > 1e-5 else Vector((0, 1))
			a = self.tarsus_pitch[key]
			tarsus_dir = Vector((h.x * math.cos(a), h.y * math.cos(a), math.sin(a)))
		A = foot + tarsus_dir.normalized() * L3
		d = A - P
		dist = d.length
		over = max(0.0, dist - (L1 + L2 - 1e-4))
		dist = min(dist, L1 + L2 - 1e-4)
		dist = max(dist, abs(L1 - L2) + 1e-4)
		u = d.normalized()
		out = Vector((P.x, P.y + 0.05, 0.0))
		out = out.normalized() if out.length > 1e-4 else X
		upv = Z + out * 0.35
		perp = (upv - u * upv.dot(u)).normalized()
		a = (L1 * L1 + dist * dist - L2 * L2) / (2 * dist)
		hgt = math.sqrt(max(0.0, L1 * L1 - a * a))
		K = P + u * a + perp * hgt
		A2 = P + u * dist
		F2 = A2 + (foot - A).normalized() * L3 if over > 0 else foot
		self.report[key] = max(self.report.get(key, 0.0), over)
		return P, K, A2, F2

	def set_bone_dir(self, pb, head, tail):
		m0 = pb.bone.matrix_local.to_3x3()
		rest_dir = m0.col[1].normalized()
		q = rest_dir.rotation_difference((tail - head).normalized())
		pb.matrix = Matrix.Translation(head) @ (q.to_matrix() @ m0).to_4x4()

	def apply(self, pose):
		for pb in self.rig.pose.bones:
			pb.rotation_quaternion = Quaternion()
			pb.location = Vector()
		for bn, q in pose.get("rot", {}).items():
			pb = self.rig.pose.bones[bn]
			pb.rotation_quaternion = cd.local_quat(pb, q)
		pl = pose.get("pelvis_loc", Vector())
		pbp = self.rig.pose.bones["pelvis"]
		pbp.location = pbp.bone.matrix_local.to_3x3().inverted() @ pl
		bpy.context.view_layer.update()
		feet = pose.get("feet", {})
		tdirs = pose.get("tarsus_dir", {})
		sol = {k: self.solve_leg(k, feet.get(k, self.rest_feet[k]), tdirs.get(k)) for k in self.legs}
		for level in range(3):
			for key, (P, K, A, F) in sol.items():
				bone = self.legs[key][level]
				head, tail = ((P, K), (K, A), (A, F))[level]
				self.set_bone_dir(self.rig.pose.bones[bone], head, tail)
			bpy.context.view_layer.update()


def gait_foot(rest, phase, start, duty, half, lift, reach_y=0.0):
	"""Foot target: stance slides the foot +Y (the ground passes backward), swing returns it with a lift."""
	p = (phase - start) % 1.0
	y0 = rest.y + reach_y
	if p < duty:
		s = p / duty
		return Vector((rest.x, y0 - half + 2 * half * s, rest.z))
	s = (p - duty) / (1.0 - duty)
	e = s * s * (3 - 2 * s)
	z = lift * math.sin(math.pi * s ** 0.8)
	return Vector((rest.x, y0 + half - 2 * half * e, rest.z + max(0.0, z)))


def rest_feet():
	return {k: Vector(BONES[v[2]][1]) for k, v in LEGS.items()}


def tetrapod_feet(ph, speed, T, duty, lift, inward=0.0, reach=None):
	half = speed * T * duty * 0.5
	rf = rest_feet()
	feet = {}
	for k in LEGS:
		st = 0.0 if k in GROUP_A else 0.5
		r = rf[k]
		if inward:
			r = Vector((r.x * (1.0 - inward), r.y, r.z))
		feet[k] = gait_foot(r, ph, st, duty, half, lift, reach.get(k, 0.0) if reach else 0.0)
	return feet


# ------------------------------------------------------------------------------------------ clips
WALK_SPEED, WALK_FRAMES = 1.2, 18
SNEAK_SPEED, SNEAK_FRAMES = 1.4, 24
RUN_SPEED, RUN_FRAMES = 7.0, 8
IDLE_LEN, GRAZE_LEN, BITE_LEN = 4.0, 3.0, 0.5


def chel_rot(open_deg, fang_deg):
	"""Chelicerae swing apart (yaw out) and forward; fangs unfold (+ = unfolded forward/down)."""
	return {
		"chel_R": euler_q(pitch=-0.5 * open_deg, yaw=-open_deg),
		"chel_L": euler_q(pitch=-0.5 * open_deg, yaw=open_deg),
		"fang_R": euler_q(pitch=-fang_deg),
		"fang_L": euler_q(pitch=-fang_deg),
	}


def walk_pose(t):
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	w = 2 * math.pi * ph
	feet = tetrapod_feet(ph, WALK_SPEED, T, 0.56, 0.085)
	rot = {
		"pelvis": euler_q(roll=1.6 * math.sin(w), yaw=1.2 * math.sin(w)),
		"abdomen": euler_q(pitch=1.5 * math.cos(2 * w), yaw=-3.0 * math.sin(w)),
		"palp_R": euler_q(pitch=8.0 * math.sin(w)),
		"palp_L": euler_q(pitch=-8.0 * math.sin(w)),
	}
	return {"pelvis_loc": Vector((0, 0, 0.008 * math.cos(2 * w))), "rot": rot, "feet": feet}


def sneak_pose(t):
	T = SNEAK_FRAMES / FPS
	ph = (t / T) % 1.0
	w = 2 * math.pi * ph
	feet = tetrapod_feet(ph, SNEAK_SPEED, T, 0.62, 0.060, inward=0.06,
						 reach={"R1": -0.06, "L1": -0.06, "R2": -0.03, "L2": -0.03})
	rot = {
		"pelvis": euler_q(pitch=4.0, roll=1.0 * math.sin(w)),
		"head": euler_q(pitch=3.0),
		"abdomen": euler_q(pitch=-5.0, yaw=-2.0 * math.sin(w)),
		"palp_R": euler_q(pitch=-12.0 + 5.0 * math.sin(w)),
		"palp_L": euler_q(pitch=-12.0 - 5.0 * math.sin(w)),
	}
	rot.update(chel_rot(4.0, 0.0))
	return {"pelvis_loc": Vector((0, 0, -0.090 + 0.004 * math.cos(2 * w))), "rot": rot, "feet": feet}


def run_pose(t):
	T = RUN_FRAMES / FPS
	ph = (t / T) % 1.0
	w = 2 * math.pi * ph
	feet = tetrapod_feet(ph, RUN_SPEED, T, 0.42, 0.13)
	rot = {
		"pelvis": euler_q(pitch=3.0, roll=2.5 * math.sin(w)),
		"abdomen": euler_q(pitch=-4.0 + 3.0 * math.cos(2 * w), yaw=-4.0 * math.sin(w)),
		"palp_R": euler_q(pitch=-20.0),
		"palp_L": euler_q(pitch=-20.0),
	}
	return {"pelvis_loc": Vector((0, 0, -0.030 + 0.016 * math.cos(2 * w))), "rot": rot, "feet": feet}


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 4 / IDLE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	tap = pulse(t, 1.30, 0.32) + pulse(t, 1.75, 0.30)
	feet["R1"] = rf["R1"] + Vector((0.02 * tap, -0.03 * tap, 0.07 * tap))
	shift = pulse(t, 2.9, 0.45)
	feet["L2"] = rf["L2"] + Vector((0.0, -0.06 * smoothstep(2.9, 3.3, t) * (1 - smoothstep(3.6, 3.95, t)), 0.06 * shift))
	rot = {
		"pelvis": euler_q(roll=0.8 * math.sin(w), yaw=2.0 * math.sin(w + 0.7)),
		"abdomen": euler_q(pitch=1.8 * breath, yaw=1.5 * math.sin(w)),
		"head": euler_q(pitch=1.0 * math.sin(2 * w)),
		"palp_R": euler_q(pitch=-14.0 * pulse(t, 0.5, 0.3) - 10.0 * pulse(t, 2.2, 0.25), yaw=-6.0 * pulse(t, 0.5, 0.3)),
		"palp_L": euler_q(pitch=-14.0 * pulse(t, 0.75, 0.3) - 10.0 * pulse(t, 3.3, 0.25), yaw=6.0 * pulse(t, 0.75, 0.3)),
	}
	rot.update(chel_rot(3.0 * pulse(t, 2.5, 0.4), 0.0))
	return {"pelvis_loc": Vector((0.004 * math.sin(w), 0, 0.004 * breath)), "rot": rot, "feet": feet}


def graze_pose(t):
	"""Feeding: crouched over the catch, front legs holding it under the mouth, chelicerae and palps working."""
	w = 2 * math.pi * t / GRAZE_LEN
	chew = 0.5 + 0.5 * math.sin(2 * math.pi * t * 6 / GRAZE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	for k, s in (("R1", -1), ("L1", 1)):
		feet[k] = Vector((0.20 * s, -0.56, 0.05 + 0.02 * math.sin(w * 2 + (0 if s < 0 else 1.6))))
	for k, s in (("R2", -1), ("L2", 1)):
		feet[k] = rf[k] + Vector((0.06 * -s, -0.06, 0.0))
	rot = {
		"pelvis": euler_q(pitch=9.0, roll=1.0 * math.sin(w)),
		"head": euler_q(pitch=6.0 + 2.0 * chew),
		"abdomen": euler_q(pitch=-10.0 + 1.5 * math.sin(w * 2)),
		"palp_R": euler_q(pitch=10.0 + 14.0 * chew, yaw=-10.0 * chew),
		"palp_L": euler_q(pitch=10.0 + 14.0 * (1 - chew), yaw=10.0 * (1 - chew)),
	}
	rot.update(chel_rot(10.0 * chew, 25.0 * chew))
	return {"pelvis_loc": Vector((0, 0.03, -0.085)), "rot": rot, "feet": feet,
			"tarsus_dir": {"R1": Vector((0.2, 0.6, 0.8)), "L1": Vector((-0.2, 0.6, 0.8))}}


# Bite keys: (t, pelvis y, pelvis z, pelvis pitch (+ nose down), front-leg raise 0..1, lunge 0..1, chel open deg, fang deg)
BITE_KEYS = [
	(0.00, 0.000, 0.000, 0.0, 0.0, 0.0, 0.0, 0.0),
	(0.15, 0.060, 0.160, -26.0, 1.0, 0.0, 22.0, 55.0),
	(0.22, 0.050, 0.150, -28.0, 1.0, 0.0, 26.0, 70.0),
	(0.30, -0.160, -0.040, 14.0, 0.0, 1.0, 6.0, -10.0),
	(0.38, -0.120, -0.030, 10.0, 0.0, 0.8, 4.0, 0.0),
	(0.50, 0.000, 0.000, 0.0, 0.0, 0.0, 0.0, 0.0),
]


def bite_pose(t):
	keys = BITE_KEYS
	for i in range(len(keys) - 1):
		if t <= keys[i + 1][0] or i == len(keys) - 2:
			a, b = keys[i], keys[i + 1]
			u = min(1.0, max(0.0, (t - a[0]) / (b[0] - a[0])))
			u = u * u * (3 - 2 * u)
			v = [a[j] + (b[j] - a[j]) * u for j in range(len(a))]
			break
	_, py, pz, pitch, raise_, lunge, chel_open, fang = v
	rf = rest_feet()
	feet = dict(rf)
	tdir = {}
	for k, s in (("R1", -1), ("L1", 1)):
		# raised: reaching forward and up at ~85 % of the leg, the tarsus tips pointing forward (threat display)
		up = Vector((0.50 * s, -0.92, 1.16))
		down = Vector((0.40 * s, -0.98, 0.020))
		base = rf[k]
		f = base.lerp(up, raise_)
		f = f.lerp(down, lunge)
		feet[k] = f
		if raise_ > 0.02:
			tdir[k] = Vector((-0.25 * s, 0.30, 0.92)).lerp(Vector((-0.35 * s, 0.62, 0.22)), raise_)
	for k, s in (("R2", -1), ("L2", 1)):
		up = Vector((0.80 * s, -0.48, 0.24))
		down = Vector((0.82 * s, -0.56, 0.014))
		feet[k] = rf[k].lerp(up, raise_).lerp(down, lunge)
	rot = {
		"pelvis": euler_q(pitch=pitch),
		"abdomen": euler_q(pitch=-0.6 * pitch),
		"head": euler_q(pitch=0.4 * pitch),
		"palp_R": euler_q(pitch=-30.0 * raise_ + 10.0 * lunge, yaw=-12.0 * raise_),
		"palp_L": euler_q(pitch=-30.0 * raise_ + 10.0 * lunge, yaw=12.0 * raise_),
	}
	rot.update(chel_rot(chel_open, fang))
	return {"pelvis_loc": Vector((0, py, pz)), "rot": rot, "feet": feet, "tarsus_dir": tdir}


CLIPS = [
	# name, seconds, pose fn, fade, loop, speed
	("idle", IDLE_LEN, idle_pose, 0.3, True, 0.0),
	("walk", WALK_FRAMES / FPS, walk_pose, 0.2, True, WALK_SPEED),
	("sneak", SNEAK_FRAMES / FPS, sneak_pose, 0.25, True, SNEAK_SPEED),
	("run", RUN_FRAMES / FPS, run_pose, 0.15, True, RUN_SPEED),
	("graze", GRAZE_LEN, graze_pose, 0.35, True, 0.0),
	("bite", BITE_LEN, bite_pose, 0.06, False, 0.0),
]

VMAT = """// Giant spider (Blender/scripts/create_giant_spider.py): 256 px point-filtered texture, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.900"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/{name}.png"
}
"""


# ------------------------------------------------------------------------------------------ bake / reports
def bake_clip(rig, poser, name, length, fn, loop=True):
	rig.animation_data_create()
	action = bpy.data.actions.new(name)
	action.use_fake_user = True
	rig.animation_data.action = action
	slot = action.slots.new(id_type="OBJECT", name=rig.name)
	rig.animation_data.action_slot = slot
	n = int(round(length * FPS))
	poser.report = {}
	body_min = 9.0
	for k in range(n + 1):
		t = (k % n) / FPS if loop else k / FPS
		bpy.context.scene.frame_set(k + 1)
		poser.apply(fn(t))
		body_min = min(body_min, rig.pose.bones["pelvis"].head.z)
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
	over = ", ".join(f"{k} {v * 100:0.1f}cm" for k, v in sorted(poser.report.items()) if v > 0.0005) or "none"
	print(f"[{NAME}] clip {name}: {n} frames = {n / FPS:0.3f} s | IK overreach {over} | pelvis min z {body_min:0.3f}")
	return action, n


def foot_slide_report(rig, name, n, speed):
	worst = 0.0
	for key, (_, _, tar) in LEGS.items():
		prev = None
		for k in range(n + 1):
			bpy.context.scene.frame_set(k + 1)
			bpy.context.view_layer.update()
			f = rig.pose.bones[tar].tail.copy()
			world = f + Vector((0, -speed * k / FPS, 0))
			if f.z < 0.017 and prev is not None and prev[1] < 0.017:
				worst = max(worst, (world - prev[0]).length)
			prev = (world, f.z)
	print(f"[{NAME}]   {name}: max planted-foot drift per frame {worst * 100:0.2f} cm")


# ------------------------------------------------------------------------------------------ preview renders
def setup_render(meshes, tex_path):
	img = bpy.data.images.load(tex_path)
	mat = meshes[0].data.materials[0]
	mat.use_nodes = True
	nt = mat.node_tree
	bsdf = nt.nodes.get("Principled BSDF")
	tn = nt.nodes.new("ShaderNodeTexImage")
	tn.image = img
	tn.interpolation = "Closest"
	nt.links.new(tn.outputs["Color"], bsdf.inputs["Base Color"])
	for m in meshes[1:]:
		m.hide_render = True
	sc = bpy.context.scene
	sc.render.engine = "BLENDER_WORKBENCH"
	sc.display.shading.light = "STUDIO"
	sc.display.shading.color_type = "TEXTURE"
	sc.display.shading.show_shadows = True
	sc.render.film_transparent = False
	world = bpy.data.worlds.new("w")
	sc.world = world
	world.color = (0.55, 0.6, 0.5)
	# ground plane for scale / contact
	bpy.ops.mesh.primitive_plane_add(size=6.0, location=(0, 0, 0))
	g = bpy.context.active_object
	gm = bpy.data.materials.new("ground")
	gm.diffuse_color = (0.42, 0.40, 0.33, 1)
	g.data.materials.append(gm)
	cam_data = bpy.data.cameras.new("cam")
	cam_data.lens = 50
	cam = bpy.data.objects.new("cam", cam_data)
	sc.collection.objects.link(cam)
	sc.camera = cam
	return cam


def aim(cam, pos, target):
	cam.location = pos
	cam.rotation_euler = (target - pos).to_track_quat("-Z", "Y").to_euler()


def render_previews(rig, meshes, tex_path, out_dir):
	os.makedirs(out_dir, exist_ok=True)
	cam = setup_render(meshes, tex_path)
	sc = bpy.context.scene
	sc.render.resolution_x = 960
	sc.render.resolution_y = 640
	target = Vector((0, 0.0, 0.32))
	views = {"side": Vector((-3.6, 0.0, 0.7)), "front34": Vector((-2.4, -2.6, 1.3)), "rear34": Vector((2.3, 2.6, 1.4)),
			 "front": Vector((0.0, -3.4, 0.55)), "top": Vector((0.01, 0.0, 3.8))}
	rig.animation_data.action = None
	for pb in rig.pose.bones:
		pb.rotation_quaternion = Quaternion()
		pb.location = Vector()
	bpy.context.view_layer.update()
	for name, pos in views.items():
		aim(cam, pos, target)
		sc.render.filepath = os.path.join(out_dir, f"{NAME}_{name}.png")
		bpy.ops.render.render(write_still=True)
	# frame sequences per clip (side + 3/4) for judging motion
	sc.render.resolution_x = 480
	sc.render.resolution_y = 360
	for clip, length, _, _, loop, _ in CLIPS:
		act = bpy.data.actions.get(clip)
		rig.animation_data.action = act
		rig.animation_data.action_slot = act.slots[0]
		n = int(round(length * FPS))
		step = max(1, n // 12)
		for view in ("side", "front34"):
			d = os.path.join(out_dir, f"{clip}_{view}")
			os.makedirs(d, exist_ok=True)
			aim(cam, views[view] * (0.85 if view == "front34" else 1.0), target)
			for i, k in enumerate(range(0, n + (0 if loop else 1), step)):
				sc.frame_set(k + 1)
				sc.render.filepath = os.path.join(d, f"frame_{i:03d}.png")
				bpy.ops.render.render(write_still=True)


# ------------------------------------------------------------------------------------------ main
def build_scene():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	sc = bpy.context.scene
	sc.unit_settings.system = "METRIC"
	sc.unit_settings.scale_length = 1.0
	sc.render.fps = FPS
	mat = bpy.data.materials.new(NAME)
	rig = build_armature()
	meshes = [build_mesh(l, mat) for l in range(4)]
	cd.unwrap(meshes[0])
	pack_tight(meshes[0])
	for m in meshes[1:]:
		cd.lod_uvs_from(meshes[0], m)
	for m in meshes:
		skin(m, rig)
	return rig, meshes


def main(check_only=False, render_dir=None):
	rig, meshes = build_scene()
	for m in meshes:
		print(f"[{NAME}] {m.name}: {len(m.data.vertices)} verts, {len(m.data.polygons)} tris")
	poser = SpiderPoser(rig)
	tex_path = os.path.join(MAT_DIR, NAME + ".png")
	if not check_only:
		for d in (MODEL_DIR, ANIM_DIR, MAT_DIR, os.path.dirname(BLEND)):
			os.makedirs(d, exist_ok=True)
		cd.TEX = TEX
		cd.write_png(tex_path, cd.paint_texture(meshes, paint_fn=paint, grain_sigma=1.6, fill=C_BASE))
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
	for name, length, fn, _, loop, speed in CLIPS:
		action, n = bake_clip(rig, poser, name, length, fn, loop=loop)
		if speed > 0.0:
			foot_slide_report(rig, name, n, speed)
		if not check_only:
			cd.export_clip(rig, os.path.join(ANIM_DIR, f"{NAME}@{name}.fbx"))
	m = meshes[0]
	bb = [m.matrix_world @ Vector(c) for c in m.bound_box]
	lo = Vector((min(v.x for v in bb), min(v.y for v in bb), min(v.z for v in bb)))
	hi = Vector((max(v.x for v in bb), max(v.y for v in bb), max(v.z for v in bb)))
	print(f"[{NAME}] bounds {lo} .. {hi}  (size {hi - lo})")
	if check_only:
		return
	cd.write_vmdl(NAME, [mm.name for mm in meshes], [0.0, 400.0, 800.0, 1200.0], [(c[0], c[3], c[4]) for c in CLIPS], 0.4,
				  "Blender metres export as 100 u/m; 0.4 gives the project's 40 u/m for mesh and clips (knees ~32 u, span ~78 u).",
				  f"materials/entities/{NAME}.vmat", f"{NAME}.vmat")
	if render_dir:
		render_previews(rig, meshes, tex_path, render_dir)
	rig.animation_data.action = None
	for pb in rig.pose.bones:
		pb.rotation_quaternion = Quaternion()
		pb.location = Vector()
	if os.path.exists(BLEND):
		os.remove(BLEND)
	bpy.ops.wm.save_as_mainfile(filepath=BLEND)


if __name__ == "__main__":
	argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
	main(check_only=bool(argv) and argv[0] == "check",
		 render_dir=argv[1] if len(argv) > 1 and argv[0] == "render" else None)
