"""Low-poly American black bear: skinned model, six clips, coat texture at 128 texels per metre, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_black_bear.py"
    ... --python "Blender/scripts/create_black_bear.py" -- check        build + bake, print the foot / IK report, write nothing
    ... --python "Blender/scripts/create_black_bear.py" -- render <dir> also render preview PNGs and per-clip frame folders
                                                                        (<dir>/<clip>/frame_NNN.png, for assemble_gif.py)

Writes
  Assets/models/entities/blackbear_v1.fbx                     skinned mesh (blackbear_lod0..3) + armature, bind pose
  Assets/models/entities/animations/blackbear_v1@<clip>.fbx   one clip each (armature only)
  Assets/models/entities/blackbear_v1.vmdl                    ScaleAndMirror 0.4 (Blender metres -> 40 u/m), LODs 0/400/800/1200
  Assets/materials/entities/blackbear_v1.png + .vmat          coat strokes, point filtered, faceted; texture size picked so the
                                                              surface gets exactly 128 texels per metre (Mark 2026-11-16)
  Blender/blenderprojects/entities/blackbear.blend
File names carry the model version (NAME); the skeleton and LOD mesh names stay "blackbear_*" (RIG) across versions.

Scale: life size in world metres - shoulder hump 0.95 m, back over the hips 0.88 m, nose to rump 1.72 m, 0.66 m across
the belly. The FBX reads 100 u per Blender metre and the vmdl 0.4 makes that 40 u/m (hump ~38 engine units; the pawn is 72).

Frame: Blender -Y = bear forward (engine +X), Blender +X = its left (engine +Y), Z up, origin on the ground.

Look: heavy barrel body with the shoulder hump, the head carried at shoulder height on a thick neck, long tan muzzle,
small round ears, plantigrade hind feet. Coat geometry: faceted guard-hair tufts (never alpha cards) along the hump,
a shaggy fringe off the lower flanks and the backs of the forelegs, a throat ruff and the rump. Texture: near-black coat
with brown-grey grizzled tips (three low-contrast tones), the tan muzzle, dark nose, horn-grey claws.

Rig: root -> pelvis -> spine -> neck -> head -> ear_L / ear_R; pelvis -> tail, hind legs; spine -> front legs.
Two-bone legs solved with the analytic IK in create_deer.Poser, so planted paws never slide.

Clips (SkinnedModelRenderer.Sequence.Name, played by AnimalBrain.Animation.cs), 30 fps:
  idle    4.00 s   loop   breathing, head swing, sniffing the air, ear flick
  walk    0.700 s  loop   heavy 4-beat pacing walk at 1.0 m/s  -> 0.700 m per cycle, the body rolls over each side
  run     0.400 s  loop   rotary gallop at 7.0 m/s             -> 2.800 m per cycle
  sneak   0.600 s  loop   low stalking walk at 1.4 m/s         -> 0.840 m per cycle, head low and forward
  graze   3.00 s   loop   nose down rooting, the left fore paw raking the ground (also the Eating state)
  bite    0.50 s   once   rear back, lunge, right fore paw swipe + bite at 0.30 s, back to the stance
Speeds match data/animal_behaviors.json "blackBear" (walk / run / sneak m/s): the brain plays them at actual / clip speed.
"""
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector, Quaternion

sys.dont_write_bytecode = True    # importing create_deer must not leave a __pycache__ entry
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_deer as cd  # noqa: E402
from create_deer import (Builder, Poser, euler_q, gait_foot, pulse, smoothstep, section, sym_jitter, mirror_part,  # noqa: E402
						 value_noise3, tube, X, Y, Z)
from create_badger import tuft, pack_tight  # noqa: E402

ROOT = cd.ROOT
MODEL_DIR = cd.MODEL_DIR
ANIM_DIR = cd.ANIM_DIR
MAT_DIR = cd.MAT_DIR
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "blackbear.blend")
NAME = "blackbear_v1"   # file version (the next iteration is blackbear_v2 and deletes these files)
RIG = "blackbear"
FPS = cd.FPS
SEED = 53
TEXELS_PER_METRE = 128.0

TRUNK, LEG_FR, LEG_FL, LEG_HR, LEG_HL, TAIL, EAR_R, EAR_L, EYE, TUFT, CLAW_FR, CLAW_FL, CLAW_HR, CLAW_HL = range(14)
LEG_PARTS = (LEG_FR, LEG_FL, LEG_HR, LEG_HL)
CLAW_PARTS = (CLAW_FR, CLAW_FL, CLAW_HR, CLAW_HL)
LEG_OF_PART = {LEG_FR: "FR", LEG_FL: "FL", LEG_HR: "HR", LEG_HL: "HL",
			   CLAW_FR: "FR", CLAW_FL: "FL", CLAW_HR: "HR", CLAW_HL: "HL"}

# ------------------------------------------------------------------------------------------ proportions
# Trunk + head loft, rump -> nose: (y, z, tilt deg, half-width, half-height up, half-height down). Barrel body, the hump
# over the shoulders, thick neck, broad skull, the muzzle tapering forward and a little down.
TRUNK_RINGS = [
	(0.700, 0.680, -28, 0.130, 0.110, 0.120),
	(0.650, 0.690, -12, 0.250, 0.195, 0.235),
	(0.540, 0.680, -3, 0.310, 0.232, 0.280),
	(0.360, 0.660, 0, 0.330, 0.226, 0.300),
	(0.160, 0.650, 0, 0.340, 0.222, 0.318),
	(-0.040, 0.660, 0, 0.340, 0.240, 0.312),
	(-0.200, 0.685, 3, 0.325, 0.272, 0.300),
	(-0.340, 0.700, 7, 0.280, 0.252, 0.280),
	(-0.450, 0.708, 9, 0.215, 0.200, 0.222),
	(-0.550, 0.715, 6, 0.182, 0.175, 0.178),
	(-0.640, 0.708, 2, 0.172, 0.170, 0.146),
	(-0.720, 0.688, -2, 0.140, 0.136, 0.122),
	(-0.795, 0.654, -6, 0.088, 0.090, 0.082),
	(-0.865, 0.628, -8, 0.066, 0.066, 0.064),
	(-0.930, 0.614, -8, 0.044, 0.046, 0.044),
]
NOSE_TIP = Vector((0.0, -0.968, 0.612))
RUMP_TIP = Vector((0.0, 0.738, 0.690))
TRUNK_ANGLES = [90, 60, 30, 0, -30, -60, -90, -120, -150, 180, 150, 120]

# Right legs (x < 0); the left ones are mirrored. (x, y, z, half-width, half-depth front, half-depth back)
FRONT_LEG = [
	(-0.195, -0.265, 0.620, 0.140, 0.170, 0.170),
	(-0.205, -0.290, 0.460, 0.135, 0.140, 0.175),
	(-0.214, -0.325, 0.320, 0.105, 0.110, 0.100),
	(-0.220, -0.345, 0.190, 0.090, 0.090, 0.085),
	(-0.223, -0.345, 0.100, 0.088, 0.090, 0.080),
	(-0.225, -0.350, 0.035, 0.095, 0.110, 0.075),
	(-0.225, -0.352, 0.000, 0.092, 0.120, 0.070),
]
HIND_LEG = [
	(-0.195, 0.470, 0.630, 0.160, 0.190, 0.200),
	(-0.205, 0.495, 0.450, 0.140, 0.150, 0.170),
	(-0.214, 0.525, 0.290, 0.100, 0.095, 0.100),
	(-0.220, 0.548, 0.160, 0.085, 0.080, 0.085),
	(-0.223, 0.530, 0.075, 0.080, 0.090, 0.075),
	(-0.225, 0.505, 0.030, 0.088, 0.140, 0.075),
	(-0.225, 0.495, 0.000, 0.088, 0.150, 0.072),
]
LEG_ANGLES = [90, 40, -5, -50, -90, -138, 172, 128]
PAW_FRONT_Y = {"F": -0.352 - 0.112, "H": 0.495 - 0.142}

BONES = {
	"root": ((0, 0, 0), (0, -0.30, 0), None),
	"pelvis": ((0, 0.560, 0.690), (0, 0.060, 0.690), "root"),
	"spine": ((0, 0.060, 0.690), (0, -0.400, 0.712), "pelvis"),
	"neck": ((0, -0.400, 0.712), (0, -0.600, 0.712), "spine"),
	"head": ((0, -0.600, 0.712), NOSE_TIP.to_tuple(), "neck"),
	"ear_L": ((0.120, -0.630, 0.835), (0.158, -0.628, 0.925), "head"),
	"ear_R": ((-0.120, -0.630, 0.835), (-0.158, -0.628, 0.925), "head"),
	"tail": ((0, 0.700, 0.720), (0, 0.790, 0.680), "pelvis"),
	"leg_upper_FR": ((-0.210, -0.285, 0.560), (-0.222, -0.360, 0.200), "spine"),
	"leg_lower_FR": ((-0.222, -0.360, 0.200), (-0.225, -0.352, 0.0), "leg_upper_FR"),
	"leg_upper_FL": ((0.210, -0.285, 0.560), (0.222, -0.360, 0.200), "spine"),
	"leg_lower_FL": ((0.222, -0.360, 0.200), (0.225, -0.352, 0.0), "leg_upper_FL"),
	"leg_upper_HR": ((-0.210, 0.480, 0.560), (-0.222, 0.560, 0.165), "pelvis"),
	"leg_lower_HR": ((-0.222, 0.560, 0.165), (-0.225, 0.495, 0.0), "leg_upper_HR"),
	"leg_upper_HL": ((0.210, 0.480, 0.560), (0.222, 0.560, 0.165), "pelvis"),
	"leg_lower_HL": ((0.222, 0.560, 0.165), (0.225, 0.495, 0.0), "leg_upper_HL"),
}
LEGS = {
	"FR": ("leg_upper_FR", "leg_lower_FR", 1.0, "spine"),
	"FL": ("leg_upper_FL", "leg_lower_FL", 1.0, "spine"),
	"HR": ("leg_upper_HR", "leg_lower_HR", -1.0, "pelvis"),
	"HL": ("leg_upper_HL", "leg_lower_HL", -1.0, "pelvis"),
}
EYE_POS = Vector((0.092, -0.722, 0.752))

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
C_BLACK = (24, 22, 22)
C_COAT = (42, 38, 35)
C_TIP = (70, 61, 54)
C_BELLY = (30, 28, 27)
C_MUZZLE = (152, 118, 82)
C_MUZZLE_DARK = (118, 90, 64)
C_NOSE = (18, 16, 16)
C_EYE = (10, 9, 9)
C_CLAW = (86, 80, 72)
C_CLAW_BASE = (44, 40, 37)
C_PAD = (30, 27, 26)


# ------------------------------------------------------------------------------------------ geometry
def trunk_frame(y):
	"""Interpolated trunk ring at y: (centre z, half-width, half-height up, half-height down, tilt deg)."""
	rings = sorted(TRUNK_RINGS, key=lambda r: r[0])
	if y <= rings[0][0]:
		r = rings[0]
		return r[1], r[3], r[4], r[5], r[2]
	for a, b in zip(rings, rings[1:]):
		if a[0] <= y <= b[0]:
			t = (y - a[0]) / (b[0] - a[0])
			return tuple(a[i] + (b[i] - a[i]) * t for i in (1, 3, 4, 5, 2))
	r = rings[-1]
	return r[1], r[3], r[4], r[5], r[2]


def body_point(y, ang):
	"""Point on the trunk surface at y and section angle ang (90 = top, 0 = left, -90 = belly) + outward direction."""
	zc, hw, hu, hd, _ = trunk_frame(y)
	r = math.radians(ang)
	ca, sa = math.cos(r), math.sin(r)
	x = hw * math.copysign(abs(ca) ** 0.8, ca)
	v = (hu if sa > 0 else hd) * math.copysign(abs(sa) ** 0.9, sa)
	p = Vector((x, y, zc + v))
	n = Vector((ca / max(hw, 1e-3), 0.0, sa / max(hu if sa > 0 else hd, 1e-3))).normalized()
	return p, n


def build_trunk(b, rng):
	rings = []
	for (y, z, tilt, hw, hu, hd) in TRUNK_RINGS:
		t = math.radians(tilt)
		up = Vector((0, math.sin(t), math.cos(t)))
		jit = sym_jitter(rng, TRUNK_ANGLES, 0.05)
		rings.append(section(Vector((0, y, z)), X, up, hw, hu, hd, TRUNK_ANGLES, jit))
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
		out = Vector((-1, 0, 0))
		out = (out - d * out.dot(d) - fwd * out.dot(fwd)).normalized()
		jit = [rng.uniform(-0.06, 0.06) for _ in LEG_ANGLES]
		ring = []
		for a_deg, j in zip(LEG_ANGLES, jit):
			r = math.radians(a_deg)
			ca, sa = math.cos(r), math.sin(r)
			x = s[3] * math.copysign(abs(ca) ** 1.05, ca)     # rounder than the badger's square legs
			v = (s[4] if sa > 0 else s[5]) * math.copysign(abs(sa) ** 1.05, sa)
			ring.append(pts[i] + out * (x * (1 + j)) + fwd * (v * (1 + j)))
		rings.append(ring)
	b.loft(rings, part, cap0=pts[0] + Vector((0, 0, 0.05)), cap1=None)


def build_claws(b, rng, front, part):
	"""Five short dark claws per paw along the toe line (black bears climb: short, curved, 3-4 cm)."""
	spec = FRONT_LEG if front else HIND_LEG
	px = spec[-1][0]
	y0 = PAW_FRONT_Y["F" if front else "H"] + 0.020
	L = 0.042 if front else 0.030
	for k in range(5):
		u = (k - 2) / 2.0
		x = px + u * 0.060
		ln = L * (1.0 - 0.2 * abs(u)) * (1 + rng.uniform(-0.08, 0.08))
		base = Vector((x, y0 + 0.010 * abs(u), 0.028))
		mid = base + Vector((u * 0.004, -ln * 0.55, -0.006))
		tip = base + Vector((u * 0.008, -ln, -0.026))
		r0 = 0.011 if front else 0.009
		tube(b, [base, mid, tip], [r0, r0 * 0.7, r0 * 0.4], part, k=4, rng=rng)


def build_tail(b, rng):
	"""The stub: a short faceted cone under the rump fur."""
	spine = [Vector((0, 0.700, 0.730)), Vector((0, 0.745, 0.718)), Vector((0, 0.785, 0.698))]
	widths = [0.050, 0.042, 0.024]
	thick = [0.040, 0.034, 0.020]
	angles = [90, 30, -30, -90, -150, 150]
	rings = []
	for i, p in enumerate(spine):
		a = spine[max(0, i - 1)]
		c = spine[min(len(spine) - 1, i + 1)]
		d = (c - a).normalized()
		up = (Z - d * Z.dot(d)).normalized()
		jit = sym_jitter(rng, angles, 0.1)
		rings.append(section(p, X, up, widths[i], thick[i], thick[i] * 0.8, angles, jit, ex=0.7))
	b.loft(rings, TAIL, cap0=None, cap1=Vector((0, 0.812, 0.680)))


def build_ear(b, side, part):
	"""Small round ear: a thick cupped half disc on top of the skull, turned a little outward."""
	h, t, _ = BONES["ear_L" if side > 0 else "ear_R"]
	base, tip = Vector(h), Vector(t)
	ax = (tip - base).normalized()
	fwd = Vector((0.25 * side, -1.0, 0.0))
	fwd = (fwd - ax * fwd.dot(ax)).normalized()
	wid = ax.cross(fwd).normalized()
	L = (tip - base).length
	ring_f, ring_b = [], []
	for i in range(7):
		a = math.pi * i / 6.0
		c = base + ax * (L * 0.10)
		p = c + wid * (math.cos(a) * 0.062) + ax * (math.sin(a) * L * 1.0)
		ring_f.append(p + fwd * 0.010)
		ring_b.append(p - fwd * 0.016)
	cf = base + ax * (L * 0.42) - fwd * 0.004
	cb = base + ax * (L * 0.42) - fwd * 0.026
	verts = ring_f + ring_b + [cf, cb]
	n = len(ring_f)
	faces = []
	for i in range(n - 1):
		faces.append((2 * n, i + 1, i) if side > 0 else (2 * n, i, i + 1))
		faces.append((2 * n + 1, n + i, n + i + 1) if side > 0 else (2 * n + 1, n + i + 1, n + i))
		faces.append((i, i + 1, n + i + 1, n + i))
	faces.append((0, n, 2 * n - 1, n - 1))
	b.closed(verts, faces, part)


def build_eyes(b):
	for s in (1, -1):
		c = Vector((EYE_POS.x * s, EYE_POS.y, EYE_POS.z))
		o = Vector((s, -0.6, 0.25)).normalized()
		fw = Vector((0, -1, 0))
		up = o.cross(fw).normalized() if s > 0 else fw.cross(o).normalized()
		fw = up.cross(o).normalized() if s > 0 else o.cross(up).normalized()
		r = 0.013
		ring = [c + fw * r * 1.2, c + up * r * 0.8, c - fw * r * 1.1, c - up * r * 0.8]
		apex = c + o * 0.007
		back = c - o * 0.012
		b.closed(ring + [apex, back], [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (1, 0, 5), (2, 1, 5), (3, 2, 5), (0, 3, 5)], EYE)


def build_fur(b, rng, lod):
	"""Guard-hair silhouette: hump ridge, shaggy lower flanks, throat ruff, rump, the backs of the forelegs."""
	step = 1 if lod == 0 else 2
	# shaggy fringe off the lower flank, clumps of uneven length and lean
	skirt = [(0.56, -30), (0.44, -38), (0.32, -42), (0.20, -44), (0.08, -46), (-0.04, -46), (-0.16, -44), (-0.27, -40)]
	for y, ang in skirt[::step]:
		for s in (1, -1):
			ln = rng.uniform(0.060, 0.100)
			sz = rng.uniform(0.050, 0.068)
			lean = Vector((rng.uniform(0.05, 0.18) * s, rng.uniform(0.25, 0.55), -1.0))
			p, n = body_point(y, ang + rng.uniform(-4.0, 4.0))
			p.x *= s
			n.x *= s
			tuft(b, p, n, lean, sz, ln, rng=rng)
	# hump ridge: tufts either side of the spine over the shoulders, laid back
	if lod <= 1:
		for y in (-0.36, -0.28, -0.20, -0.12, -0.04):
			for s in (1, -1):
				p, n = body_point(y + (0.02 if s > 0 else -0.01), 68)
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.15 * s, 1.0, 0.35)), 0.055, 0.060 + rng.uniform(-0.01, 0.015), rng=rng)
	# a mid-flank row so the side reads layered
	if lod <= 1:
		for i, y in enumerate((0.50, 0.36, 0.22, 0.08, -0.06, -0.20)):
			for s in (1, -1):
				p, n = body_point(y + 0.03 * (i % 2), -8 + 6 * (i % 2))
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.12 * s, 1.0, -0.45)), 0.040, 0.050 + rng.uniform(-0.008, 0.012), rng=rng)
	# throat ruff under the jaw and neck
	if lod <= 2:
		for y, ang in ((-0.50, -60), (-0.58, -70), (-0.50, -100)):
			for s in (1, -1):
				p, n = body_point(y, ang)
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.3 * s, 0.6, -1.0)), 0.050, 0.070, rng=rng)
	# rump fringe over the tail stub and the backs of the thighs
	if lod <= 2:
		for ang in (40, 0, -30):
			for s in (1, -1):
				p, n = body_point(0.66, ang)
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.3 * s, 1.0, -0.5)), 0.052, 0.065, rng=rng)
	# backs of the forelegs (feathering)
	if lod <= 1:
		for z in (0.46, 0.34):
			for s in (1, -1):
				x = FRONT_LEG[1][0] * s
				p = Vector((x, -0.300 + 0.12, z))
				tuft(b, p, Vector((0, 1, 0)), Vector((0.1 * s, 0.6, -1.0)), 0.045, 0.060, rng=rng)


def build_mesh(lod, mat):
	rng = np.random.default_rng(SEED)
	b = Builder()
	build_trunk(b, rng)
	build_leg(b, rng, FRONT_LEG, LEG_FR)
	build_leg(b, rng, HIND_LEG, LEG_HR)
	if lod <= 2:
		build_claws(b, rng, True, CLAW_FR)
		build_claws(b, rng, False, CLAW_HR)
	mirror_part(b.bm, b.part, LEG_FR, LEG_FL)
	mirror_part(b.bm, b.part, LEG_HR, LEG_HL)
	if lod <= 2:
		mirror_part(b.bm, b.part, CLAW_FR, CLAW_FL)
		mirror_part(b.bm, b.part, CLAW_HR, CLAW_HL)
	build_tail(b, rng)
	if lod <= 2:
		build_ear(b, 1, EAR_L)
		build_ear(b, -1, EAR_R)
	if lod <= 1:
		build_eyes(b)
	if lod <= 2:
		build_fur(b, np.random.default_rng(SEED + 1), lod)
	bmesh.ops.recalc_face_normals(b.bm, faces=b.bm.faces)
	bmesh.ops.triangulate(b.bm, faces=b.bm.faces)
	name = f"{RIG}_lod{lod}"
	me = bpy.data.meshes.new(name)
	b.bm.to_mesh(me)
	b.bm.free()
	obj = bpy.data.objects.new(name, me)
	bpy.context.scene.collection.objects.link(obj)
	obj.data.materials.append(mat)
	# Varied face sizes: symmetric edge-collapse decimate on the big organic parts only (tufts, claws, ears stay crisp).
	ratio = {0: 0.74, 1: 0.42, 2: 0.24, 3: 0.13}[lod]
	vg = obj.vertex_groups.new(name="decimate")
	part = me.attributes["part"].data
	soft = set()
	for p in me.polygons:
		if part[p.index].value in (TRUNK,) + LEG_PARTS:
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


# ------------------------------------------------------------------------------------------ uv density
def fit_texel_density(obj):
	"""Pick the smallest power-of-two texture that reaches TEXELS_PER_METRE and shrink the islands to exactly that."""
	me = obj.data
	uv = me.uv_layers.active.data
	a3 = 0.0
	a2 = 0.0
	for poly in me.polygons:
		li = list(poly.loop_indices)
		vs = [me.vertices[me.loops[l].vertex_index].co for l in li]
		us = [uv[l].uv for l in li]
		for k in range(1, len(li) - 1):
			a3 += ((vs[k] - vs[0]).cross(vs[k + 1] - vs[0])).length * 0.5
			e1 = us[k] - us[0]
			e2 = us[k + 1] - us[0]
			a2 += abs(e1.x * e2.y - e1.y * e2.x) * 0.5
	per_unit = math.sqrt(a2 / a3)
	size = 128
	while size * per_unit < TEXELS_PER_METRE and size < 2048:
		size *= 2
	s = TEXELS_PER_METRE / (size * per_unit)
	for l in uv:
		l.uv = (l.uv.x * s, l.uv.y * s)
	print(f"[{NAME}] surface {a3:0.3f} m2, uv fill {a2 * s * s * 100:0.1f} %, texture {size} px -> "
		  f"{size * per_unit * s:0.1f} texels / m")
	return size


# ------------------------------------------------------------------------------------------ paint
def hair_noise(p, flow, seed):
	"""Anisotropic value noise: long along the hair flow, fine across it -> hair strokes (scaled for 128 texels / m)."""
	f = flow.normalized()
	a = f.cross(Z if abs(f.z) < 0.9 else X).normalized()
	c = f.cross(a).normalized()
	q = Vector((p.dot(f) * 9.0, p.dot(a) * 60.0, p.dot(c) * 60.0))
	return 0.8 * value_noise3(q, 1.0, seed) + 0.2 * value_noise3(p, 4.0, seed + 7)


def grizzle(p, flow, dark, base, light, bias=0.0):
	s = hair_noise(p, flow, 3) + bias
	if s > 0.68:
		return light
	if s < 0.32:
		return dark
	return base


def paint(part, p, n):
	if part == EYE:
		return C_EYE
	if part in CLAW_PARTS:
		front = part in (CLAW_FR, CLAW_FL)
		d = PAW_FRONT_Y["F" if front else "H"] + 0.020 - p.y
		return C_CLAW_BASE if d < 0.012 else C_CLAW
	if part in (EAR_L, EAR_R):
		return grizzle(p, Vector((0, 0.3, 1)), C_BLACK, C_COAT, C_TIP)
	if part == TAIL:
		return grizzle(p, Vector((0, 1, -0.3)), C_BLACK, C_COAT, C_TIP)
	if part in LEG_PARTS:
		if p.z < 0.012 and n.z < -0.5:
			return C_PAD
		return grizzle(p, Vector((0, 0.15, -1)), C_BLACK, C_BLACK, C_COAT, bias=-0.05)
	# trunk / tufts / head
	if p.y < -0.905 and (p - NOSE_TIP).length < 0.060:
		return C_NOSE
	if p.y < -0.745:
		# the tan muzzle, a shade darker on top of the snout, blending into the black at the brow
		zc, hw, hu, hd, _ = trunk_frame(p.y)
		top = (p.z - zc) / max(hu, 1e-3)
		edge = smoothstep(-0.745, -0.785, p.y + (value_noise3(p, 18.0, 5) - 0.5) * 0.03)
		if edge < 0.5:
			return grizzle(p, Vector((0, 1, 0)), C_BLACK, C_COAT, C_MUZZLE_DARK)
		return C_MUZZLE_DARK if top > 0.55 else grizzle(p, Vector((0, 1, 0)), C_MUZZLE_DARK, C_MUZZLE, C_MUZZLE, bias=0.08)
	zc, hw, hu, hd, _ = trunk_frame(p.y)
	v = (p.z - zc) / (hu if p.z > zc else hd)
	v += (value_noise3(p, 6.0, 1) - 0.5) * 0.16
	flow = Vector((0, 1, -0.2 - 0.5 * max(0.0, -v)))
	if part == TUFT:
		return grizzle(p, flow, C_COAT, C_COAT, C_TIP, bias=0.06)    # the guard hairs catch the light: browner tips
	if v < -0.6:
		return grizzle(p, flow, C_BLACK, C_BELLY, C_BELLY)
	if v > 0.75 and -0.42 < p.y < 0.0:
		return grizzle(p, flow, C_COAT, C_TIP, C_TIP, bias=0.04)     # sun-bleached hump
	return grizzle(p, flow, C_BLACK, C_COAT, C_TIP)


# ------------------------------------------------------------------------------------------ weights
def vertex_weights(pid, p):
	if pid == EYE:
		return {"head": 1.0}
	if pid in (EAR_L, EAR_R):
		return {"ear_L" if pid == EAR_L else "ear_R": 0.85, "head": 0.15}
	if pid == TAIL:
		h, t, _ = BONES["tail"]
		s = cd.seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.01, 0.05, s)
		return {"tail": w, "pelvis": 1.0 - w}
	if pid in CLAW_PARTS:
		return {LEGS[LEG_OF_PART[pid]][1]: 1.0}
	if pid in LEG_PARTS:
		up, lo, _, body = LEGS[LEG_OF_PART[pid]]
		P = Vector(BONES[up][0])
		K = Vector(BONES[up][1])
		wl = smoothstep(K.z + 0.04, K.z - 0.04, p.z)
		wb = smoothstep(P.z - 0.08, P.z + 0.06, p.z) * (1.0 - wl)
		return {lo: wl, body: wb, up: 1.0 - wl - wb}
	# trunk + tufts: blends along the body, rump -> spine -> neck -> head
	y = p.y
	head = smoothstep(-0.56, -0.66, y)
	neck_all = smoothstep(-0.34, -0.46, y)
	rest = 1.0 - neck_all
	wp = rest * smoothstep(0.02, 0.28, y)
	return {"pelvis": wp, "spine": rest - wp, "neck": neck_all - head, "head": head}


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
		for bn, w in vertex_weights(vpart.get(v.index, TRUNK), v.co).items():
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


# ------------------------------------------------------------------------------------------ clips
WALK_SPEED = 1.0       # animal_behaviors.json blackBear walkSpeedMps
WALK_FRAMES = 21
RUN_SPEED = 7.0        # runSpeedMps
RUN_FRAMES = 12
SNEAK_SPEED = 1.4      # sneakSpeedMps
SNEAK_FRAMES = 18
IDLE_LEN = 4.0
GRAZE_LEN = 3.0
BITE_LEN = 0.5


def rest_feet():
	return {k: Vector(BONES[v[1]][1]) for k, v in LEGS.items()}


def walk_pose(t):
	"""Bears pace: both legs of a side move nearly together (lateral sequence), so the heavy body rolls side to side."""
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.62
	half = WALK_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.12, "HR": 0.5, "FR": 0.62}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.075 if k[0] == "F" else 0.060,
						 center_dy=0.015 if k[0] == "F" else -0.010) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0.012 * math.sin(w), 0, -0.050 + 0.010 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(yaw=3.0 * math.sin(w), roll=4.0 * math.sin(w)),
			"spine": euler_q(yaw=-2.5 * math.sin(w), roll=-3.0 * math.sin(w)),
			"neck": euler_q(pitch=5.0 + 2.5 * math.cos(2 * w + 0.5), yaw=-3.0 * math.sin(w)),
			"head": euler_q(pitch=2.0 - 2.5 * math.cos(2 * w + 0.5), yaw=5.0 * math.sin(w + 0.8)),
			"tail": euler_q(pitch=-3.0, yaw=5.0 * math.sin(w)),
		},
		"feet": feet,
	}


def run_pose(t):
	T = RUN_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.22
	half = RUN_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "HR": 0.10, "FL": 0.45, "FR": 0.56}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.16 if k[0] == "F" else 0.13,
						 center_dy=0.03 if k[0] == "F" else -0.03) for k, st in starts.items()}
	w = 2 * math.pi * ph
	flex = 10.0 * math.cos(w - 2 * math.pi * 0.80)          # + = gathered (back arched)
	pitch = -5.0 * math.cos(w - 2 * math.pi * 0.10)
	lift = -0.090 + 0.035 * (0.5 + 0.5 * math.cos(w - 2 * math.pi * 0.8))
	return {
		"pelvis_loc": Vector((0, 0, lift)),
		"rot": {
			"pelvis": euler_q(pitch=pitch - flex * 0.5),
			"spine": euler_q(pitch=flex),
			"neck": euler_q(pitch=-0.5 * (pitch + flex * 0.5) + 6.0),
			"head": euler_q(pitch=-6.0 + 3.0 * math.sin(w)),
			"tail": euler_q(pitch=-6.0),
			"ear_L": euler_q(pitch=-14.0),
			"ear_R": euler_q(pitch=-14.0),
		},
		"feet": feet,
	}


def sneak_pose(t):
	"""Stalking: body low, head low and forward, slow deliberate lateral steps with little roll."""
	T = SNEAK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.66
	half = SNEAK_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.20, "HR": 0.5, "FR": 0.70}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.055 if k[0] == "F" else 0.045,
						 center_dy=0.025 if k[0] == "F" else -0.015) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0.006 * math.sin(w), 0, -0.110 + 0.006 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(pitch=2.0, yaw=2.0 * math.sin(w), roll=2.0 * math.sin(w)),
			"spine": euler_q(pitch=3.0, roll=-1.5 * math.sin(w)),
			"neck": euler_q(pitch=10.0),
			"head": euler_q(pitch=-6.0, yaw=2.0 * math.sin(w + 0.8)),
			"tail": euler_q(pitch=-6.0),
			"ear_L": euler_q(pitch=-8.0),
			"ear_R": euler_q(pitch=-8.0),
		},
		"feet": feet,
	}


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 3 / IDLE_LEN)
	sniff = pulse(t, 1.3, 0.18) + pulse(t, 1.55, 0.18) + pulse(t, 3.0, 0.2)
	ear = pulse(t, 2.5, 0.22)
	return {
		"pelvis_loc": Vector((0.008 * math.sin(w), 0, -0.020 + 0.005 * breath)),
		"rot": {
			"pelvis": euler_q(roll=1.5 * math.sin(w)),
			"spine": euler_q(pitch=0.8 * breath, roll=-1.0 * math.sin(w)),
			"neck": euler_q(pitch=1.0 - 6.0 * sniff * 0.5 + 3.0 * math.sin(2 * w + 0.4), yaw=14.0 * math.sin(w + 1.0)),
			"head": euler_q(pitch=-6.0 * sniff + 2.0 * math.sin(2 * w), yaw=6.0 * math.sin(w + 1.7)),
			"tail": euler_q(pitch=-2.0),
			"ear_L": euler_q(pitch=-18.0 * ear),
			"ear_R": euler_q(pitch=-6.0 * ear),
		},
	}


def graze_pose(t):
	"""Rooting: nose down in the duff, the left fore paw raking back twice per loop."""
	w = 2 * math.pi * t / GRAZE_LEN
	nose = math.sin(2 * math.pi * t * 5 / GRAZE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	dig = (t / GRAZE_LEN * 2.0) % 1.0
	if dig < 0.55:
		s = dig / 0.55
		feet["FL"] = rf["FL"] + Vector((0, -0.12 + 0.18 * s, 0.020 * math.sin(math.pi * s)))
	else:
		s = (dig - 0.55) / 0.45
		feet["FL"] = rf["FL"] + Vector((0, 0.06 - 0.18 * s, 0.090 * math.sin(math.pi * s)))
	return {
		"pelvis_loc": Vector((0, 0.01, -0.060)),
		"rot": {
			"pelvis": euler_q(pitch=-3.0),
			"spine": euler_q(pitch=6.0, roll=1.5 * math.sin(w)),
			"neck": euler_q(pitch=44.0 + 2.0 * nose, yaw=6.0 * math.sin(w)),
			"head": euler_q(pitch=24.0 + 4.0 * nose, yaw=5.0 * math.sin(w + 0.6)),
			"tail": euler_q(pitch=-3.0),
			"ear_L": euler_q(pitch=-6.0),
			"ear_R": euler_q(pitch=-6.0),
		},
		"feet": feet,
	}


# (t, forward m, drop m, spine pitch, neck pitch, head pitch, FR paw raise) - pitch + = nose down
BITE_KEYS = [
	(0.00, 0.000, 0.000, 0.0, 0.0, 0.0, 0.0),
	(0.14, -0.050, -0.010, -8.0, -8.0, -6.0, 0.6),
	(0.27, 0.110, -0.030, 4.0, -16.0, -14.0, 1.0),
	(0.33, 0.120, -0.040, 6.0, 10.0, 18.0, 0.2),
	(0.50, 0.000, 0.000, 0.0, 0.0, 0.0, 0.0),
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
	_, fwd, drop, spine, neck, head, paw = v
	rf = rest_feet()
	feet = dict(rf)
	# the right fore paw comes up and swipes forward-in, the other feet stay planted
	feet["FR"] = rf["FR"] + Vector((0.06 * paw, -0.18 * paw + fwd, 0.30 * paw))
	feet["FL"] = rf["FL"] + Vector((0, -0.5 * fwd, 0.0))     # the planted fore paw shuffles with the lunge
	return {
		"pelvis_loc": Vector((0, -fwd, drop)),
		"rot": {
			"pelvis": euler_q(pitch=-spine * 0.3),
			"spine": euler_q(pitch=spine),
			"neck": euler_q(pitch=neck),
			"head": euler_q(pitch=head),
			"ear_L": euler_q(pitch=-1.2 * abs(neck)),
			"ear_R": euler_q(pitch=-1.2 * abs(neck)),
		},
		"feet": feet,
	}


CLIPS = [
	# name, seconds, pose fn, fade, loop, speed
	("idle", IDLE_LEN, idle_pose, 0.3, True, 0.0),
	("walk", WALK_FRAMES / FPS, walk_pose, 0.2, True, WALK_SPEED),
	("run", RUN_FRAMES / FPS, run_pose, 0.15, True, RUN_SPEED),
	("sneak", SNEAK_FRAMES / FPS, sneak_pose, 0.25, True, SNEAK_SPEED),
	("graze", GRAZE_LEN, graze_pose, 0.35, True, 0.0),
	("bite", BITE_LEN, bite_pose, 0.08, False, 0.0),
]

VMAT = """// Black bear (Blender/scripts/create_black_bear.py): coat strokes at 128 texels per metre, point filtered, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/{name}.png"
}
"""


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
	sc.render.resolution_x = 900
	sc.render.resolution_y = 640
	sc.render.film_transparent = False
	world = bpy.data.worlds.new("w")
	sc.world = world
	world.color = (0.45, 0.5, 0.42)
	cam_data = bpy.data.cameras.new("cam")
	cam_data.lens = 60
	cam = bpy.data.objects.new("cam", cam_data)
	sc.collection.objects.link(cam)
	sc.camera = cam
	return sc, cam


def aim(cam, pos, target):
	cam.location = pos
	cam.rotation_euler = (target - pos).to_track_quat("-Z", "Y").to_euler()


def render_previews(rig, meshes, tex_path, out_dir):
	"""Workbench renders of LOD0 (textured, point sampled, flat) + every clip as a frame folder for assemble_gif.py."""
	os.makedirs(out_dir, exist_ok=True)
	sc, cam = setup_render(meshes, tex_path)
	target = Vector((0, -0.10, 0.50))
	views = {"side": Vector((-4.6, -0.10, 0.80)), "front34": Vector((-3.0, -3.4, 1.3)),
			 "rear34": Vector((2.8, 3.4, 1.5)), "front": Vector((0.0, -4.6, 0.80)), "top": Vector((0.01, 0.0, 4.8))}
	for name, pos in views.items():
		aim(cam, pos, target)
		sc.render.filepath = os.path.join(out_dir, f"{NAME}_{name}.png")
		bpy.ops.render.render(write_still=True)
	sc.render.resolution_x = 480
	sc.render.resolution_y = 480
	for clip, length, _, _, _, _ in CLIPS:
		act = bpy.data.actions.get(clip)
		if act is None:
			continue
		rig.animation_data.action = act
		rig.animation_data.action_slot = act.slots[0]
		n = int(round(length * FPS))
		step = max(1, n // 30)
		folder = os.path.join(out_dir, clip)
		os.makedirs(folder, exist_ok=True)
		aim(cam, Vector((-4.2, -1.6, 1.1)), target)
		for k in range(0, n, step):
			sc.frame_set(k + 1)
			sc.render.filepath = os.path.join(folder, f"frame_{k:03d}.png")
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
	tex = fit_texel_density(meshes[0])
	for m in meshes[1:]:
		cd.lod_uvs_from(meshes[0], m)
	for m in meshes:
		skin(m, rig)
	return rig, meshes, tex


def main(check_only=False, render_dir=None):
	rig, meshes, tex = build_scene()
	for m in meshes:
		print(f"[{NAME}] {m.name}: {len(m.data.vertices)} verts, {len(m.data.polygons)} tris")
	poser = Poser(rig, BONES, LEGS)
	tex_path = os.path.join(MAT_DIR, NAME + ".png")
	if not check_only:
		for d in (MODEL_DIR, ANIM_DIR, MAT_DIR, os.path.dirname(BLEND)):
			os.makedirs(d, exist_ok=True)
		cd.TEX = tex                               # paint_texture rasterises at the module's TEX
		cd.write_png(tex_path, cd.paint_texture(meshes, paint_fn=paint, grain_sigma=1.5, fill=C_COAT))
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
		action, n = cd.bake_clip(rig, poser, name, length, fn, loop=loop, tag=NAME)
		if speed > 0.0:
			cd.foot_slide_report(rig, poser, name, n, speed, tag=NAME)
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
				  "Blender metres export as 100 u/m; 0.4 gives the project's 40 u/m for mesh and clips (hump ~38 u).",
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
