"""Low-poly Canada lynx: skinned model, six clips, coat texture at 128 texels per metre, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_lynx.py"
    ... --python "Blender/scripts/create_lynx.py" -- check        build + bake, print the foot / IK report, write nothing
    ... --python "Blender/scripts/create_lynx.py" -- render <dir> also render preview PNGs and per-clip frame folders
                                                                  (<dir>/<clip>/frame_NNN.png, for assemble_gif.py)

Writes
  Assets/models/entities/lynx_v1.fbx                     skinned mesh (lynx_lod0..3) + armature, bind pose
  Assets/models/entities/animations/lynx_v1@<clip>.fbx   one clip each (armature only)
  Assets/models/entities/lynx_v1.vmdl                    ScaleAndMirror 0.4 (Blender metres -> 40 u/m), LODs 0/400/800/1200
  Assets/materials/entities/lynx_v1.png + .vmat          coat strokes + spots, point filtered, faceted; texture size picked so
                                                         the surface gets exactly 128 texels per metre (Mark 2026-11-16)
  Blender/blenderprojects/entities/lynx.blend
File names carry the model version (NAME); the skeleton and LOD mesh names stay "lynx_*" (RIG) across versions.

Scale: life size in world metres - shoulder 0.56 m, hips 0.58 m (the long hind legs lift the rump), nose to rump 0.93 m,
ear-tuft tips 0.80 m. The FBX reads 100 u per Blender metre and the vmdl 0.4 makes that 40 u/m (shoulder ~22 units).

Frame: Blender -Y = lynx forward (engine +X), Blender +X = its left (engine +Y), Z up, origin on the ground.

Look: short compact body on long legs with big round snowshoe paws, round head with the flared facial ruff, tall pointed
ears with black tufts, a short stub tail with a black tip. Texture: grey-tan grizzled coat with faint dark spots on the
flanks and legs, cream belly, chin and muzzle, black bars in the ruff, dark ear backs.

Rig: root -> pelvis -> spine -> neck -> head -> ear_L / ear_R; pelvis -> tail, hind legs; spine -> front legs.
Two-bone legs solved with the analytic IK in create_deer.Poser, so planted paws never slide.

Clips (SkinnedModelRenderer.Sequence.Name, played by AnimalBrain.Animation.cs), 30 fps:
  idle    4.00 s   loop   breathing, look around, ear swivels, tail flicks
  walk    0.500 s  loop   4-beat cat walk at 1.1 m/s     -> 0.550 m per cycle
  run     0.300 s  loop   bounding gallop at 8.5 m/s     -> 2.550 m per cycle
  sneak   0.467 s  loop   crouched stalk at 1.8 m/s      -> 0.840 m per cycle, belly low, head level and forward
  graze   3.00 s   loop   head down, tearing / chewing at the ground (the Eating state)
  bite    0.50 s   once   crouch, pounce forward with both fore paws up, bite at 0.30 s, back to the stance
Speeds match data/animal_behaviors.json "lynx" (walk / run / sneak m/s): the brain plays them at actual / clip speed.
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
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "lynx.blend")
NAME = "lynx_v1"     # file version (the next iteration is lynx_v2 and deletes these files)
RIG = "lynx"
FPS = cd.FPS
SEED = 61
TEXELS_PER_METRE = 128.0

TRUNK, LEG_FR, LEG_FL, LEG_HR, LEG_HL, TAIL, EAR_R, EAR_L, EYE, TUFT, RUFF, EARTUFT = range(12)
LEG_PARTS = (LEG_FR, LEG_FL, LEG_HR, LEG_HL)
LEG_OF_PART = {LEG_FR: "FR", LEG_FL: "FL", LEG_HR: "HR", LEG_HL: "HL"}

# ------------------------------------------------------------------------------------------ proportions
# Trunk + head loft, rump -> nose: (y, z, tilt deg, half-width, half-height up, half-height down). Compact body, the
# neck rising to a round head carried above the back, the broad cheek ruff, a short muzzle.
TRUNK_RINGS = [
	(0.370, 0.505, -16, 0.060, 0.058, 0.062),
	(0.320, 0.496, -5, 0.106, 0.086, 0.104),
	(0.220, 0.488, 0, 0.118, 0.092, 0.122),
	(0.080, 0.478, 0, 0.104, 0.080, 0.112),
	(-0.060, 0.476, 0, 0.114, 0.088, 0.136),
	(-0.180, 0.484, 4, 0.120, 0.094, 0.146),
	(-0.255, 0.502, 12, 0.104, 0.090, 0.124),
	(-0.310, 0.540, 24, 0.080, 0.076, 0.086),
	(-0.350, 0.576, 14, 0.086, 0.082, 0.078),
	(-0.392, 0.592, 4, 0.098, 0.090, 0.074),
	(-0.432, 0.588, -2, 0.084, 0.074, 0.066),
	(-0.468, 0.570, -6, 0.050, 0.048, 0.046),
	(-0.495, 0.560, -8, 0.030, 0.030, 0.030),
]
NOSE_TIP = Vector((0.0, -0.512, 0.556))
RUMP_TIP = Vector((0.0, 0.398, 0.508))
TRUNK_ANGLES = [90, 60, 30, 0, -30, -60, -90, -120, -150, 180, 150, 120]

# Right legs (x < 0); the left ones are mirrored. (x, y, z, half-width, half-depth front, half-depth back)
FRONT_LEG = [
	(-0.068, -0.195, 0.470, 0.058, 0.072, 0.074),
	(-0.074, -0.200, 0.340, 0.050, 0.054, 0.064),
	(-0.078, -0.228, 0.210, 0.038, 0.040, 0.040),
	(-0.080, -0.244, 0.105, 0.034, 0.036, 0.034),
	(-0.080, -0.240, 0.050, 0.038, 0.042, 0.036),
	(-0.080, -0.245, 0.018, 0.056, 0.070, 0.046),
	(-0.080, -0.248, 0.000, 0.054, 0.074, 0.044),
]
HIND_LEG = [
	(-0.068, 0.245, 0.470, 0.068, 0.090, 0.092),
	(-0.074, 0.232, 0.350, 0.058, 0.070, 0.074),
	(-0.077, 0.285, 0.230, 0.040, 0.042, 0.048),
	(-0.080, 0.338, 0.135, 0.034, 0.035, 0.040),
	(-0.080, 0.305, 0.062, 0.034, 0.037, 0.034),
	(-0.080, 0.272, 0.018, 0.056, 0.070, 0.046),
	(-0.080, 0.266, 0.000, 0.054, 0.074, 0.044),
]
LEG_ANGLES = [90, 40, -5, -50, -90, -138, 172, 128]

BONES = {
	"root": ((0, 0, 0), (0, -0.20, 0), None),
	"pelvis": ((0, 0.330, 0.490), (0, 0.020, 0.478), "root"),
	"spine": ((0, 0.020, 0.478), (0, -0.260, 0.500), "pelvis"),
	"neck": ((0, -0.260, 0.500), (0, -0.355, 0.584), "spine"),
	"head": ((0, -0.355, 0.584), NOSE_TIP.to_tuple(), "neck"),
	"ear_L": ((0.058, -0.378, 0.655), (0.078, -0.372, 0.730), "head"),
	"ear_R": ((-0.058, -0.378, 0.655), (-0.078, -0.372, 0.730), "head"),
	"tail": ((0, 0.380, 0.520), (0, 0.468, 0.440), "pelvis"),
	"leg_upper_FR": ((-0.072, -0.205, 0.440), (-0.080, -0.250, 0.110), "spine"),
	"leg_lower_FR": ((-0.080, -0.250, 0.110), (-0.080, -0.248, 0.0), "leg_upper_FR"),
	"leg_upper_FL": ((0.072, -0.205, 0.440), (0.080, -0.250, 0.110), "spine"),
	"leg_lower_FL": ((0.080, -0.250, 0.110), (0.080, -0.248, 0.0), "leg_upper_FL"),
	"leg_upper_HR": ((-0.072, 0.258, 0.440), (-0.080, 0.335, 0.135), "pelvis"),
	"leg_lower_HR": ((-0.080, 0.335, 0.135), (-0.080, 0.266, 0.0), "leg_upper_HR"),
	"leg_upper_HL": ((0.072, 0.258, 0.440), (0.080, 0.335, 0.135), "pelvis"),
	"leg_lower_HL": ((0.080, 0.335, 0.135), (0.080, 0.266, 0.0), "leg_upper_HL"),
}
LEGS = {
	"FR": ("leg_upper_FR", "leg_lower_FR", 1.0, "spine"),
	"FL": ("leg_upper_FL", "leg_lower_FL", 1.0, "spine"),
	"HR": ("leg_upper_HR", "leg_lower_HR", -1.0, "pelvis"),
	"HL": ("leg_upper_HL", "leg_lower_HL", -1.0, "pelvis"),
}
EYE_Y = -0.446       # head section and angle the eyes sit on (body_point)
EYE_ANGLE = 40.0
TAIL_TIP = Vector((0.0, 0.472, 0.430))

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
# Darker grey-brown than the first pass: in-engine the 150 / 138 / 118 coat read near-white under the sun (2026-11-16)
C_DARK = (92, 80, 66)
C_COAT = (114, 99, 80)
C_LIGHT = (138, 123, 101)
C_SPOT = (76, 64, 53)
C_CREAM = (176, 166, 146)
C_CREAM_SHADE = (150, 140, 122)
C_BLACK = (30, 27, 25)
C_NOSE = (118, 82, 72)
C_EYE = (150, 122, 52)
C_EAR_BACK = (70, 64, 58)


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
			x = s[3] * math.copysign(abs(ca) ** 1.05, ca)
			v = (s[4] if sa > 0 else s[5]) * math.copysign(abs(sa) ** 1.05, sa)
			ring.append(pts[i] + out * (x * (1 + j)) + fwd * (v * (1 + j)))
		rings.append(ring)
	b.loft(rings, part, cap0=pts[0] + Vector((0, 0, 0.03)), cap1=None)


def build_tail(b, rng):
	"""Short thick stub, held a little down, rounded black tip."""
	spine = [Vector((0, 0.378, 0.522)), Vector((0, 0.410, 0.505)), Vector((0, 0.440, 0.478)), Vector((0, 0.462, 0.448))]
	widths = [0.034, 0.036, 0.032, 0.022]
	thick = [0.032, 0.034, 0.030, 0.020]
	angles = [90, 30, -30, -90, -150, 150]
	rings = []
	for i, p in enumerate(spine):
		a = spine[max(0, i - 1)]
		c = spine[min(len(spine) - 1, i + 1)]
		d = (c - a).normalized()
		up = (Z - d * Z.dot(d)).normalized()
		jit = sym_jitter(rng, angles, 0.08)
		rings.append(section(p, X, up, widths[i], thick[i], thick[i] * 0.9, angles, jit, ex=0.7))
	b.loft(rings, TAIL, cap0=None, cap1=TAIL_TIP)


def build_ear(b, side, part):
	"""Tall pointed ear: a thick cupped triangle, the black tuft rising off its tip (built separately)."""
	h, t, _ = BONES["ear_L" if side > 0 else "ear_R"]
	base, tip = Vector(h), Vector(t)
	ax = (tip - base).normalized()
	fwd = Vector((0.20 * side, -1.0, 0.0))
	fwd = (fwd - ax * fwd.dot(ax)).normalized()
	wid = ax.cross(fwd).normalized()
	L = (tip - base).length
	# outline: base corners, mid flare, tip
	prof = [(-1.0, 0.0), (-0.85, 0.40), (-0.45, 0.78), (0.0, 1.0), (0.45, 0.78), (0.85, 0.40), (1.0, 0.0)]
	ring_f, ring_b = [], []
	for u, v in prof:
		p = base + wid * (u * 0.034 * (1.0 - 0.15 * v)) + ax * (v * L)
		ring_f.append(p + fwd * 0.006)
		ring_b.append(p - fwd * 0.010)
	cf = base + ax * (L * 0.40) - fwd * 0.002
	cb = base + ax * (L * 0.40) - fwd * 0.014
	verts = ring_f + ring_b + [cf, cb]
	n = len(ring_f)
	faces = []
	for i in range(n - 1):
		faces.append((2 * n, i + 1, i) if side > 0 else (2 * n, i, i + 1))
		faces.append((2 * n + 1, n + i, n + i + 1) if side > 0 else (2 * n + 1, n + i + 1, n + i))
		faces.append((i, i + 1, n + i + 1, n + i))
	faces.append((0, n, 2 * n - 1, n - 1))
	b.closed(verts, faces, part)


def build_ear_tufts(b, rng):
	"""The black pencil tufts on the ear tips: thin 4-sided tubes, ~5 cm, leaning a little in and back."""
	for side in (1, -1):
		h, t, _ = BONES["ear_L" if side > 0 else "ear_R"]
		tip = Vector(t)
		pts = [tip - Vector((0, 0, 0.006)), tip + Vector((-0.004 * side, 0.004, 0.026)), tip + Vector((-0.008 * side, 0.010, 0.050))]
		tube(b, pts, [0.0055, 0.0040, 0.0018], EARTUFT, k=4, rng=rng)


def build_eyes(b):
	"""Almond eyes set on the head surface where the brow turns into the muzzle, looking forward (cats do)."""
	for s in (1, -1):
		c, n = body_point(EYE_Y, EYE_ANGLE)
		c.x *= s
		n.x *= s
		o = (n * 0.45 + Vector((0, -1, 0)) * 0.55).normalized()
		lat = Z.cross(o).normalized()
		up = o.cross(lat).normalized()
		r = 0.016
		c = c + n * 0.002
		ring = [c + lat * r * 1.25, c + up * r * 0.8, c - lat * r * 1.25, c - up * r * 0.8]
		apex = c + o * 0.006
		back = c - o * 0.012
		faces = [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (1, 0, 5), (2, 1, 5), (3, 2, 5), (0, 3, 5)]
		b.closed(ring + [apex, back], faces, EYE)


def build_ruff(b, rng, lod):
	"""Facial ruff: tufts from the cheeks and jaw hanging down and back, flared outward."""
	rows = [(-0.405, 0, 1.0), (-0.395, -25, 1.1), (-0.385, -50, 1.15), (-0.372, -75, 1.0), (-0.360, -100, 0.9)]
	for i, (y, ang, ln) in enumerate(rows if lod == 0 else rows[::2]):
		for s in (1, -1):
			p, n = body_point(y, ang)
			p.x *= s
			n.x *= s
			lean = Vector((0.75 * s, 0.55, -0.55 - 0.004 * abs(ang)))
			tuft(b, p, n, lean, 0.026, 0.048 * ln + rng.uniform(-0.004, 0.006), part=RUFF, rng=rng)


def build_fur(b, rng, lod):
	"""Light guard hair over the backs of the haunches; a lynx coat is dense, not shaggy."""
	if lod > 1:
		return
	for ang in (10, -20):
		for s in (1, -1):
			p, n = body_point(0.33, ang)
			p.x *= s
			n.x *= s
			tuft(b, p, n, Vector((0.3 * s, 1.0, -0.7)), 0.026, 0.034, rng=rng)


def build_mesh(lod, mat):
	rng = np.random.default_rng(SEED)
	b = Builder()
	build_trunk(b, rng)
	build_leg(b, rng, FRONT_LEG, LEG_FR)
	build_leg(b, rng, HIND_LEG, LEG_HR)
	mirror_part(b.bm, b.part, LEG_FR, LEG_FL)
	mirror_part(b.bm, b.part, LEG_HR, LEG_HL)
	build_tail(b, rng)
	build_ear(b, 1, EAR_L)
	build_ear(b, -1, EAR_R)
	if lod <= 2:
		build_ear_tufts(b, rng)
	if lod <= 1:
		build_eyes(b)
	if lod <= 2:
		build_ruff(b, np.random.default_rng(SEED + 2), lod)
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
	# Varied face sizes: symmetric edge-collapse decimate on the big organic parts only (tufts, ears stay crisp).
	ratio = {0: 0.74, 1: 0.42, 2: 0.24, 3: 0.14}[lod]
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
	q = Vector((p.dot(f) * 10.0, p.dot(a) * 70.0, p.dot(c) * 70.0))
	return 0.8 * value_noise3(q, 1.0, seed) + 0.2 * value_noise3(p, 5.0, seed + 7)


def grizzle(p, flow, dark, base, light, bias=0.0):
	s = hair_noise(p, flow, 3) + bias
	if s > 0.68:
		return light
	if s < 0.32:
		return dark
	return base


def spotted(p, amount):
	"""Faint lynx spotting: blobs of a coarse noise field above a threshold (amount 0..1 = how much of the coat)."""
	s = value_noise3(p, 22.0, 11)
	return s > 1.0 - 0.20 * amount


def paint(part, p, n):
	if part == EYE:
		return C_EYE
	if part == EARTUFT:
		return C_BLACK
	if part in (EAR_L, EAR_R):
		h, t, _ = BONES["ear_L" if part == EAR_L else "ear_R"]
		front = n.y < -0.2
		if (p - Vector(t)).length < 0.026:
			return C_BLACK                              # the dark tip under the tuft
		if front:
			return C_CREAM_SHADE                       # pale inner ear hair
		return C_EAR_BACK
	if part == TAIL:
		if (p - TAIL_TIP).length < 0.040:
			return C_BLACK
		return grizzle(p, Vector((0, 1, -0.3)), C_DARK, C_COAT, C_LIGHT)
	if part == RUFF:
		# pale ruff with the black bars at its lower edge
		if p.z < 0.548 and value_noise3(p, 40.0, 9) > 0.55:
			return C_BLACK
		return grizzle(p, Vector((0, 0.5, -1)), C_CREAM_SHADE, C_CREAM, C_LIGHT)
	if part in LEG_PARTS:
		flow = Vector((0, 0.1, -1))
		inner = (p.x * (1 if part in (LEG_FL, LEG_HL) else -1)) < 0.072
		if p.z < 0.06:
			return grizzle(p, flow, C_CREAM_SHADE, C_LIGHT, C_CREAM)     # pale furry paws
		if spotted(p, 0.8) and not inner:
			return C_SPOT
		return grizzle(p, flow, C_DARK, C_COAT, C_LIGHT, bias=0.04 if inner else 0.0)
	# trunk / tufts / head
	if (p - NOSE_TIP).length < 0.020:
		return C_NOSE
	zc, hw, hu, hd, _ = trunk_frame(p.y)
	v = (p.z - zc) / (hu if p.z > zc else hd)
	v += (value_noise3(p, 8.0, 1) - 0.5) * 0.14
	if p.y < -0.345:
		# head: cream muzzle, chin and lips, grizzled crown, a dark line from the eye corner back
		if p.y < -0.462 or v < -0.35:
			return grizzle(p, Vector((0, 1, 0)), C_CREAM_SHADE, C_CREAM, C_CREAM)
		if -0.462 < p.y < -0.418 and 0.1 < v < 0.5 and abs(p.x) > 0.035:
			return C_DARK
		return grizzle(p, Vector((0, 1, 0.3)), C_DARK, C_COAT, C_LIGHT, bias=0.04)
	flow = Vector((0, 1, -0.2 - 0.5 * max(0.0, -v)))
	if v < -0.55:
		return grizzle(p, flow, C_CREAM_SHADE, C_CREAM, C_CREAM)
	if v < -0.30:
		return grizzle(p, flow, C_COAT, C_LIGHT, C_CREAM_SHADE)
	if spotted(p, 0.55 if v < 0.5 else 0.2):
		return C_SPOT
	if v > 0.75:
		return grizzle(p, flow, C_DARK, C_DARK, C_COAT)                  # darker line down the back
	return grizzle(p, flow, C_DARK, C_COAT, C_LIGHT)


# ------------------------------------------------------------------------------------------ weights
def vertex_weights(pid, p):
	if pid in (EYE, RUFF):
		return {"head": 1.0}
	if pid in (EAR_L, EAR_R) or pid == EARTUFT:
		key = "ear_L" if (pid == EAR_L or (pid == EARTUFT and p.x > 0)) else "ear_R"
		return {key: 0.9, "head": 0.1}
	if pid == TAIL:
		h, t, _ = BONES["tail"]
		s = cd.seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.01, 0.04, s)
		return {"tail": w, "pelvis": 1.0 - w}
	if pid in LEG_PARTS:
		up, lo, _, body = LEGS[LEG_OF_PART[pid]]
		P = Vector(BONES[up][0])
		K = Vector(BONES[up][1])
		wl = smoothstep(K.z + 0.025, K.z - 0.025, p.z)
		wb = smoothstep(P.z - 0.06, P.z + 0.04, p.z) * (1.0 - wl)
		return {lo: wl, body: wb, up: 1.0 - wl - wb}
	# trunk + tufts: blends along the body, rump -> spine -> neck -> head
	y = p.y
	head = smoothstep(-0.340, -0.380, y)
	neck_all = smoothstep(-0.22, -0.30, y)
	rest = 1.0 - neck_all
	wp = rest * smoothstep(-0.02, 0.18, y)
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
WALK_SPEED = 1.1       # animal_behaviors.json lynx walkSpeedMps
WALK_FRAMES = 15
RUN_SPEED = 8.5        # runSpeedMps
RUN_FRAMES = 9
SNEAK_SPEED = 1.8      # sneakSpeedMps
SNEAK_FRAMES = 14
IDLE_LEN = 4.0
GRAZE_LEN = 3.0
BITE_LEN = 0.5


def rest_feet():
	return {k: Vector(BONES[v[1]][1]) for k, v in LEGS.items()}


def walk_pose(t):
	"""Cat walk: lateral-sequence 4-beat, the hind paw landing where the fore paw was, a level back and swaying tail."""
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.60
	half = WALK_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.25, "HR": 0.5, "FR": 0.75}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.050 if k[0] == "F" else 0.045,
						 center_dy=0.010 if k[0] == "F" else -0.010) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0.004 * math.sin(w), 0, -0.040 + 0.005 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(yaw=3.0 * math.sin(w), roll=2.0 * math.sin(w)),
			"spine": euler_q(yaw=-2.0 * math.sin(w), roll=-1.5 * math.sin(w)),
			"neck": euler_q(pitch=2.0 + 1.5 * math.cos(2 * w + 0.5), yaw=-2.0 * math.sin(w)),
			"head": euler_q(pitch=-1.5 * math.cos(2 * w + 0.5), yaw=3.0 * math.sin(w + 0.8)),
			"tail": euler_q(pitch=-8.0, yaw=10.0 * math.sin(w)),
		},
		"feet": feet,
	}


def run_pose(t):
	T = RUN_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.18
	half = RUN_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "HR": 0.08, "FL": 0.46, "FR": 0.54}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.11 if k[0] == "F" else 0.10,
						 center_dy=0.02 if k[0] == "F" else -0.02) for k, st in starts.items()}
	w = 2 * math.pi * ph
	flex = 16.0 * math.cos(w - 2 * math.pi * 0.80)          # + = gathered (back arched): cats flex a lot
	pitch = -5.0 * math.cos(w - 2 * math.pi * 0.10)
	lift = -0.075 + 0.030 * (0.5 + 0.5 * math.cos(w - 2 * math.pi * 0.8))
	return {
		"pelvis_loc": Vector((0, 0, lift)),
		"rot": {
			"pelvis": euler_q(pitch=pitch - flex * 0.5),
			"spine": euler_q(pitch=flex),
			"neck": euler_q(pitch=-0.5 * (pitch + flex * 0.5) + 2.0),
			"head": euler_q(pitch=-4.0),
			"tail": euler_q(pitch=-4.0 + 8.0 * math.sin(w)),
			"ear_L": euler_q(pitch=-14.0),
			"ear_R": euler_q(pitch=-14.0),
		},
		"feet": feet,
	}


def sneak_pose(t):
	"""Stalk: belly low, shoulders blade-high, head level and forward, slow deliberate placement, tail low and still."""
	T = SNEAK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.66
	half = SNEAK_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.25, "HR": 0.5, "FR": 0.75}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.035 if k[0] == "F" else 0.030,
						 center_dy=0.015 if k[0] == "F" else -0.010) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0.003 * math.sin(w), 0, -0.120 + 0.004 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(pitch=3.0, yaw=2.0 * math.sin(w)),
			"spine": euler_q(pitch=-2.0, roll=-1.0 * math.sin(w)),
			"neck": euler_q(pitch=14.0),
			"head": euler_q(pitch=-12.0),
			"tail": euler_q(pitch=-12.0),
			"ear_L": euler_q(pitch=4.0),
			"ear_R": euler_q(pitch=4.0),
		},
		"feet": feet,
	}


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 3 / IDLE_LEN)
	ear = pulse(t, 1.2, 0.25)
	ear2 = pulse(t, 2.9, 0.25)
	flick = pulse(t, 0.6, 0.3) + pulse(t, 3.3, 0.3)
	return {
		"pelvis_loc": Vector((0.002 * math.sin(w), 0, -0.010 + 0.002 * breath)),
		"rot": {
			"spine": euler_q(pitch=0.6 * breath),
			"neck": euler_q(pitch=-2.0 + 2.0 * math.sin(2 * w + 0.4), yaw=16.0 * math.sin(w + 1.0)),
			"head": euler_q(pitch=1.5 * math.sin(2 * w), yaw=8.0 * math.sin(w + 1.7)),
			"tail": euler_q(pitch=-10.0 + 14.0 * flick, yaw=6.0 * math.sin(w * 2)),
			"ear_L": euler_q(yaw=-25.0 * ear, pitch=-6.0 * ear2),
			"ear_R": euler_q(yaw=25.0 * ear2),
		},
	}


def graze_pose(t):
	"""Eating at a kill: head down, tearing pulls back and chewing, the fore legs spread a little."""
	w = 2 * math.pi * t / GRAZE_LEN
	chew = math.sin(2 * math.pi * t * 6 / GRAZE_LEN)
	tear = pulse(t, 0.8, 0.4) + pulse(t, 2.2, 0.4)
	rf = rest_feet()
	feet = dict(rf)
	feet["FL"] = rf["FL"] + Vector((0.015, -0.04, 0))
	feet["FR"] = rf["FR"] + Vector((-0.015, -0.02, 0))
	return {
		"pelvis_loc": Vector((0, 0.01, -0.080)),
		"rot": {
			"pelvis": euler_q(pitch=-6.0),
			"spine": euler_q(pitch=14.0),
			"neck": euler_q(pitch=66.0 - 12.0 * tear + 2.0 * chew, yaw=6.0 * math.sin(w)),
			"head": euler_q(pitch=26.0 - 6.0 * tear + 3.0 * chew, yaw=4.0 * math.sin(w + 0.6)),
			"tail": euler_q(pitch=-12.0, yaw=8.0 * math.sin(w * 2)),
			"ear_L": euler_q(pitch=-10.0),
			"ear_R": euler_q(pitch=-10.0),
		},
		"feet": feet,
	}


# (t, forward m, drop m, spine pitch, neck pitch, head pitch, paws up) - pitch + = nose down
BITE_KEYS = [
	(0.00, 0.000, 0.000, 0.0, 0.0, 0.0, 0.0),
	(0.13, -0.040, -0.060, 6.0, 4.0, 0.0, 0.0),
	(0.25, 0.090, -0.010, -10.0, -12.0, -8.0, 1.0),
	(0.31, 0.100, -0.030, 2.0, 12.0, 16.0, 0.6),
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
	_, fwd, drop, spine, neck, head, paws = v
	rf = rest_feet()
	feet = dict(rf)
	# both fore paws come up and reach forward (the pounce), the hind feet stay planted
	for k in ("FL", "FR"):
		feet[k] = rf[k] + Vector((0, -0.10 * paws - fwd, 0.16 * paws))
	return {
		"pelvis_loc": Vector((0, -fwd, drop)),
		"rot": {
			"pelvis": euler_q(pitch=-spine * 0.3),
			"spine": euler_q(pitch=spine),
			"neck": euler_q(pitch=neck),
			"head": euler_q(pitch=head),
			"tail": euler_q(pitch=-8.0 + 10.0 * paws),
			"ear_L": euler_q(pitch=-1.5 * abs(neck)),
			"ear_R": euler_q(pitch=-1.5 * abs(neck)),
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

VMAT = """// Lynx (Blender/scripts/create_lynx.py): coat strokes + spots at 128 texels per metre, point filtered, faceted.
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
	target = Vector((0, -0.06, 0.36))
	views = {"side": Vector((-2.6, -0.06, 0.50)), "front34": Vector((-1.6, -2.0, 0.80)),
			 "rear34": Vector((1.6, 2.0, 0.9)), "front": Vector((0.0, -2.6, 0.55)), "top": Vector((0.01, 0.0, 2.8)),
			 "head": Vector((-0.55, -1.25, 0.72))}
	for name, pos in views.items():
		aim(cam, pos, target if name != "head" else Vector((0, -0.44, 0.62)))
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
		aim(cam, Vector((-2.2, -0.9, 0.65)), target)
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
				  "Blender metres export as 100 u/m; 0.4 gives the project's 40 u/m for mesh and clips (shoulder ~22 u).",
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
