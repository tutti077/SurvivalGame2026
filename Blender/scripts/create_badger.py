"""Low-poly badger: skinned model, five clips, fur-stroke texture at 256 texels per metre, pixel_lit vmat, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_badger.py"
    ... --python "Blender/scripts/create_badger.py" -- check      build + bake, print the foot / IK report, write nothing
    ... --python "Blender/scripts/create_badger.py" -- render <dir> also render preview PNGs (side / 3-4 / front / top)

Writes
  Assets/models/entities/badger.fbx                     skinned mesh (badger_lod0..3) + armature, bind pose
  Assets/models/entities/animations/badger@<clip>.fbx   one clip each (armature only)
  Assets/models/entities/badger.vmdl                    ScaleAndMirror 0.4 (Blender metres -> 40 u/m), LODs, AnimationList
  Assets/materials/entities/badger.png + .vmat          fur strokes, point filtered, faceted; texture size picked so the
                                                        surface gets exactly 256 texels per metre (Mark 2026-11-08)
  Blender/blenderprojects/entities/badger.blend
File names carry the model version (NAME); the skeleton and LOD mesh names stay "badger_*" (RIG) across versions.

Scale (Mark 2026-11-08: "about the same size as the fox model"): the fox is 1.25 world m nose to tail with a 0.5 m
shoulder; the badger is 1.12 m nose to tail tip, back 0.42 m, 0.42 m across the fur skirt - lower and much wider, the
same bulk. Modelled in world metres; the FBX reads 100 u per Blender metre and the vmdl 0.4 makes that 40 u/m.

Frame: Blender -Y = badger forward (engine +X), Blender +X = its left (engine +Y), Z up, origin on the ground.

Fur: geometry gives the shaggy silhouette - a skirt of long guard-hair tufts hanging off each lower flank over the
legs, a ragged row along the back and haunches, cheek ruffs and a tufted tail (faceted pyramids, never alpha cards).
The texture carries hair strokes: anisotropic noise stretched along the coat's flow (back and a little down on the
body, down the legs) quantised to three low-contrast tones - grizzled grey body, black legs and belly, the white face
with its two black stripes over the eyes to the ears.

Rig: root -> pelvis -> spine -> neck -> head -> ear_L / ear_R; pelvis -> tail, hind legs; spine -> front legs.
Two-bone legs solved with the analytic IK in create_deer.Poser, so planted paws never slide.

Clips (SkinnedModelRenderer.Sequence.Name), 30 fps:
  idle    4.00 s   loop   breathing, sniffing, slow look around, ear twitch
  walk    0.400 s  loop   waddling 4-beat walk at 1.1 m/s  -> 0.440 m per cycle (short legs, quick steps)
  run     0.267 s  loop   bounding gallop at 6.0 m/s       -> 1.600 m per cycle
  graze   3.00 s   loop   nose down rooting, the left fore paw digging
  bite    0.50 s   once   crouch, lunge, snap at 0.30 s, back to the stance
Speeds match data/animal_behaviors.json "badger" (walkSpeedMps / runSpeedMps): the brain plays them at actual / clip speed.
"""
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector, Quaternion

sys.dont_write_bytecode = True    # importing create_deer must not leave a __pycache__ entry
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_deer as cd  # noqa: E402
from create_deer import (Builder, Poser, euler_q, gait_foot, pulse, smoothstep, section, sym_jitter, mirror_part,  # noqa: E402
						 value_noise3, mix, tube, X, Y, Z)

ROOT = cd.ROOT
MODEL_DIR = cd.MODEL_DIR
ANIM_DIR = cd.ANIM_DIR
MAT_DIR = cd.MAT_DIR
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "badger.blend")
NAME = "badger"      # file version (first version; the next iteration is badger_v2 and deletes these files)
RIG = "badger"
FPS = cd.FPS
SEED = 41
TEXELS_PER_METRE = 256.0

TRUNK, LEG_FR, LEG_FL, LEG_HR, LEG_HL, TAIL, EAR_R, EAR_L, EYE, TUFT, CLAW_FR, CLAW_FL, CLAW_HR, CLAW_HL = range(14)
LEG_PARTS = (LEG_FR, LEG_FL, LEG_HR, LEG_HL)
CLAW_PARTS = (CLAW_FR, CLAW_FL, CLAW_HR, CLAW_HL)
LEG_OF_PART = {LEG_FR: "FR", LEG_FL: "FL", LEG_HR: "HR", LEG_HL: "HL",
			   CLAW_FR: "FR", CLAW_FL: "FL", CLAW_HR: "HR", CLAW_HL: "HL"}

# ------------------------------------------------------------------------------------------ proportions
# Trunk + head loft, rump -> nose: (y, z, tilt deg, half-width, half-height up, half-height down). A low wedge: flat
# broad back, no visible neck, the head tapering to a long snout.
TRUNK_RINGS = [
	(0.405, 0.300, -18, 0.075, 0.078, 0.080),
	(0.360, 0.292, -6, 0.150, 0.124, 0.130),
	(0.270, 0.285, 0, 0.194, 0.136, 0.160),
	(0.140, 0.280, 0, 0.210, 0.138, 0.172),
	(0.000, 0.278, 0, 0.214, 0.136, 0.172),
	(-0.130, 0.278, 0, 0.202, 0.130, 0.160),
	(-0.235, 0.278, 4, 0.172, 0.120, 0.140),
	(-0.315, 0.280, 8, 0.134, 0.104, 0.112),
	(-0.375, 0.272, 8, 0.116, 0.096, 0.098),
	(-0.435, 0.258, 4, 0.098, 0.082, 0.086),
	(-0.490, 0.238, 0, 0.074, 0.062, 0.068),
	(-0.535, 0.218, -4, 0.046, 0.042, 0.046),
	(-0.570, 0.206, -6, 0.028, 0.028, 0.030),
]
NOSE_TIP = Vector((0.0, -0.592, 0.202))
RUMP_TIP = Vector((0.0, 0.432, 0.302))
TRUNK_ANGLES = [90, 60, 30, 0, -30, -60, -90, -120, -150, 180, 150, 120]

# Right legs (x < 0); the left ones are mirrored. (x, y, z, half-width, half-depth front, half-depth back)
FRONT_LEG = [
	(-0.118, -0.222, 0.300, 0.060, 0.080, 0.080),
	(-0.130, -0.244, 0.215, 0.070, 0.074, 0.070),
	(-0.137, -0.264, 0.150, 0.060, 0.060, 0.055),
	(-0.140, -0.282, 0.100, 0.050, 0.048, 0.046),
	(-0.141, -0.280, 0.055, 0.048, 0.046, 0.040),
	(-0.142, -0.272, 0.022, 0.055, 0.058, 0.040),
	(-0.142, -0.268, 0.000, 0.053, 0.062, 0.036),
]
HIND_LEG = [
	(-0.116, 0.258, 0.305, 0.074, 0.100, 0.100),
	(-0.132, 0.270, 0.222, 0.080, 0.094, 0.096),
	(-0.138, 0.285, 0.150, 0.060, 0.060, 0.066),
	(-0.140, 0.300, 0.095, 0.048, 0.046, 0.050),
	(-0.140, 0.285, 0.050, 0.046, 0.050, 0.046),
	(-0.140, 0.268, 0.020, 0.050, 0.064, 0.040),
	(-0.140, 0.262, 0.000, 0.050, 0.070, 0.036),
]
LEG_ANGLES = [90, 40, -5, -50, -90, -138, 172, 128]
PAW_FRONT_Y = {"F": -0.268 - 0.062, "H": 0.262 - 0.070}

BONES = {
	"root": ((0, 0, 0), (0, -0.15, 0), None),
	"pelvis": ((0, 0.33, 0.29), (0, 0.04, 0.29), "root"),
	"spine": ((0, 0.04, 0.29), (0, -0.24, 0.29), "pelvis"),
	"neck": ((0, -0.24, 0.29), (0, -0.36, 0.282), "spine"),
	"head": ((0, -0.36, 0.282), (0, -0.592, 0.202), "neck"),
	"ear_L": ((0.072, -0.372, 0.322), (0.108, -0.360, 0.372), "head"),
	"ear_R": ((-0.072, -0.372, 0.322), (-0.108, -0.360, 0.372), "head"),
	"tail": ((0, 0.400, 0.320), (0, 0.530, 0.300), "pelvis"),
	"leg_upper_FR": ((-0.125, -0.235, 0.250), (-0.140, -0.285, 0.100), "spine"),
	"leg_lower_FR": ((-0.140, -0.285, 0.100), (-0.142, -0.262, 0.0), "leg_upper_FR"),
	"leg_upper_FL": ((0.125, -0.235, 0.250), (0.140, -0.285, 0.100), "spine"),
	"leg_lower_FL": ((0.140, -0.285, 0.100), (0.142, -0.262, 0.0), "leg_upper_FL"),
	"leg_upper_HR": ((-0.125, 0.250, 0.250), (-0.140, 0.300, 0.095), "pelvis"),
	"leg_lower_HR": ((-0.140, 0.300, 0.095), (-0.140, 0.262, 0.0), "leg_upper_HR"),
	"leg_upper_HL": ((0.125, 0.250, 0.250), (0.140, 0.300, 0.095), "pelvis"),
	"leg_lower_HL": ((0.140, 0.300, 0.095), (0.140, 0.262, 0.0), "leg_upper_HL"),
}
LEGS = {
	"FR": ("leg_upper_FR", "leg_lower_FR", 1.0, "spine"),
	"FL": ("leg_upper_FL", "leg_lower_FL", 1.0, "spine"),
	"HR": ("leg_upper_HR", "leg_lower_HR", -1.0, "pelvis"),
	"HL": ("leg_upper_HL", "leg_lower_HL", -1.0, "pelvis"),
}
EYE_POS = Vector((0.050, -0.468, 0.282))

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
C_GREY_DARK = (108, 104, 98)
C_GREY = (138, 134, 125)
C_GRIZZLE = (166, 161, 150)
C_BELLY = (48, 46, 44)
C_BLACK = (30, 29, 28)
C_BLACK_SOFT = (46, 44, 42)
C_WHITE = (218, 214, 204)
C_WHITE_SHADE = (190, 186, 176)
C_NOSE = (38, 34, 34)
C_EYE = (12, 11, 11)
C_CLAW = (196, 184, 156)
C_CLAW_BASE = (132, 120, 98)


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
	"""Point on the (un-jittered) trunk surface at y and section angle ang (90 = top, 0 = left, -90 = belly), plus its
	outward direction."""
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
			x = s[3] * math.copysign(abs(ca) ** 0.85, ca)
			v = (s[4] if sa > 0 else s[5]) * math.copysign(abs(sa) ** 0.85, sa)
			ring.append(pts[i] + out * (x * (1 + j)) + fwd * (v * (1 + j)))
		rings.append(ring)
	b.loft(rings, part, cap0=pts[0] + Vector((0, 0, 0.03)), cap1=None)


def build_claws(b, rng, front, part):
	"""Five claws per paw along the toe line, curving forward and down to the ground. Fore claws are the long digging
	claws (6 cm), hind ones short (3 cm)."""
	spec = FRONT_LEG if front else HIND_LEG
	px = spec[-1][0]
	y0 = PAW_FRONT_Y["F" if front else "H"] + 0.012
	L = 0.062 if front else 0.032
	for k in range(5):
		u = (k - 2) / 2.0                        # -1 .. 1 across the paw
		x = px + u * 0.036
		ln = L * (1.0 - 0.18 * abs(u)) * (1 + rng.uniform(-0.06, 0.06))
		base = Vector((x, y0 + 0.004 * abs(u), 0.020))
		mid = base + Vector((u * 0.004, -ln * 0.55, -0.004))
		tip = base + Vector((u * 0.008, -ln, -0.018))
		r0 = 0.0085 if front else 0.007
		tube(b, [base, mid, tip], [r0, r0 * 0.7, r0 * 0.4], part, k=4, rng=rng)


def build_tail(b, rng):
	spine = [Vector((0, 0.392, 0.320)), Vector((0, 0.440, 0.318)), Vector((0, 0.490, 0.306)), Vector((0, 0.520, 0.296))]
	widths = [0.050, 0.056, 0.046, 0.026]
	thick = [0.040, 0.036, 0.028, 0.016]
	angles = [90, 30, -30, -90, -150, 150]
	rings = []
	for i, p in enumerate(spine):
		a = spine[max(0, i - 1)]
		c = spine[min(len(spine) - 1, i + 1)]
		d = (c - a).normalized()
		up = (Z - d * Z.dot(d)).normalized()
		jit = sym_jitter(rng, angles, 0.08)
		rings.append(section(p, X, up, widths[i], thick[i], thick[i] * 0.8, angles, jit, ex=0.7))
	b.loft(rings, TAIL, cap0=None, cap1=Vector((0, 0.548, 0.288)))


def build_ear(b, side, part):
	"""Small round ear set low on the side of the head: a thick cupped disc, rim slightly flared."""
	h, t, _ = BONES["ear_L" if side > 0 else "ear_R"]
	base, tip = Vector(h), Vector(t)
	ax = (tip - base).normalized()
	fwd = Vector((0.35 * side, -1.0, 0.0))
	fwd = (fwd - ax * fwd.dot(ax)).normalized()
	wid = ax.cross(fwd).normalized()
	L = (tip - base).length
	ring_f, ring_b = [], []
	for i in range(7):
		a = math.pi * i / 6.0                      # half disc, base edge to base edge over the top
		c = base + ax * (L * 0.15)
		p = c + wid * (math.cos(a) * 0.038) + ax * (math.sin(a) * L * 1.15)
		ring_f.append(p + fwd * 0.006)
		ring_b.append(p - fwd * 0.010)
	cf = base + ax * (L * 0.45) - fwd * 0.002      # cup hollow in the middle of the front face
	cb = base + ax * (L * 0.45) - fwd * 0.016
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
		o = Vector((s, -0.45, 0.25)).normalized()
		fw = Vector((0, -1, 0))
		up = o.cross(fw).normalized() if s > 0 else fw.cross(o).normalized()
		fw = up.cross(o).normalized() if s > 0 else o.cross(up).normalized()
		r = 0.010
		ring = [c + fw * r * 1.2, c + up * r * 0.8, c - fw * r * 1.1, c - up * r * 0.8]
		apex = c + o * 0.006
		back = c - o * 0.010
		b.closed(ring + [apex, back], [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (1, 0, 5), (2, 1, 5), (3, 2, 5), (0, 3, 5)], EYE)


def tuft(b, base, normal, hair, size, length, part=TUFT, rng=None):
	"""One faceted fur tuft: a 4-sided pyramid whose base sits a little inside the body surface and whose tip points
	along `hair` (the coat's flow). size = base half-width, length = tip distance."""
	n = normal.normalized()
	h = hair.normalized()
	t1 = h - n * h.dot(n)
	if t1.length < 1e-4:
		t1 = n.orthogonal()
	t1.normalize()
	t2 = n.cross(t1).normalized()
	c = base - n * 0.010
	quad = [c + t1 * size, c + t2 * size * 0.8, c - t1 * size * 0.9, c - t2 * size * 0.8]
	if rng is not None:
		quad = [q + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * size * 0.12 for q in quad]
	tip = base + h * length
	verts = quad + [tip]
	faces = [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4)]     # base sits inside the body: no cap, no texture spent
	b.closed(verts, faces, part)


def build_fur(b, rng, lod):
	"""Guard-hair silhouette: skirt along the lower flanks, ragged back, haunch fringe, cheek ruffs, tail tips."""
	step = 1 if lod == 0 else 2
	# skirt: the long coat hanging off the lower flank, over the tops of the legs
	skirt = [(0.34, -26), (0.27, -34), (0.20, -38), (0.13, -40), (0.06, -42), (-0.01, -42), (-0.08, -40),
			 (-0.15, -36), (-0.21, -30)]
	for row, (dy, dang, lmin, lmax) in enumerate(((0.0, 0.0, 0.085, 0.135), (0.035, 12.0, 0.060, 0.095))):
		if row == 1 and lod > 0:
			continue
		for i, (y, ang) in enumerate(skirt[::step]):
			for s in (1, -1):
				# clumps, not a comb: random length, width and lean, the upper row staggered between the lower one
				ln = rng.uniform(lmin, lmax)
				sz = rng.uniform(0.046, 0.062)
				lean = Vector((rng.uniform(0.12, 0.32) * s, rng.uniform(0.15, 0.45), -1.0))
				p, n = body_point(y + dy, ang + dang + rng.uniform(-3.0, 3.0))
				p.x *= s
				n.x *= s
				tuft(b, p, n, lean, sz, ln, rng=rng)
	# a second, shorter row higher up so the flank reads layered
	mid = [(0.31, -6), (0.22, -10), (0.12, -14), (0.02, -16), (-0.08, -14), (-0.18, -8)]
	if lod <= 1:
		for i, (y, ang) in enumerate(mid):
			ln = 0.060 + rng.uniform(-0.008, 0.012)
			for s in (1, -1):
				p, n = body_point(y + 0.03 * (i % 2), ang)
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.30 * s, 0.85, -0.55)), 0.036, ln, rng=rng)
	# ragged back: low tufts either side of the spine, laid back toward the tail
	back = [(0.30, 62), (0.17, 66), (0.03, 66), (-0.11, 64), (-0.22, 60)]
	if lod <= 1:
		for i, (y, ang) in enumerate(back):
			for s in (1, -1):
				p, n = body_point(y + (0.02 if s > 0 else -0.01), ang)
				p.x *= s
				n.x *= s
				tuft(b, p, n, Vector((0.15 * s, 1.0, 0.18)), 0.034, 0.040 + rng.uniform(-0.006, 0.008), rng=rng)
	# rump fringe over the back of the thighs
	for ang in (-10, -40) if lod <= 2 else ():
		for s in (1, -1):
			p, n = body_point(0.37, ang)
			p.x *= s
			n.x *= s
			tuft(b, p, n, Vector((0.3 * s, 0.9, -0.7)), 0.032, 0.050, rng=rng)
	# cheek ruffs behind the jaw
	if lod <= 2:
		for s in (1, -1):
			p, n = body_point(-0.345, -20)
			p.x *= s
			n.x *= s
			tuft(b, p, n, Vector((0.6 * s, 0.8, -0.3)), 0.034, 0.045, rng=rng)
	# tail tip hairs
	if lod <= 1:
		for ang, d in ((60, Vector((0.3, 1, 0.2))), (-60, Vector((-0.3, 1, 0.2))), (180, Vector((0, 1, -0.4)))):
			r = math.radians(ang)
			p = Vector((0.040 * math.cos(r) if ang != 180 else 0.0, 0.505, 0.304 + 0.020 * math.sin(r)))
			tuft(b, p, Vector((math.cos(r), 0.0, math.sin(r))), d, 0.022, 0.050, part=TAIL, rng=rng)


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
	ratio = {0: 0.70, 1: 0.42, 2: 0.24, 3: 0.14}[lod]
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
def pack_tight(obj):
	"""Re-pack the smart-project islands (rotated, concave) so a 512 texture can hold 256 texels per metre."""
	bpy.ops.object.select_all(action="DESELECT")
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.object.mode_set(mode="EDIT")
	bpy.ops.mesh.select_all(action="SELECT")
	bpy.ops.uv.select_all(action="SELECT")
	bpy.ops.uv.pack_islands(rotate=True, margin=0.003, shape_method="CONCAVE")
	bpy.ops.object.mode_set(mode="OBJECT")


def fit_texel_density(obj):
	"""Smart-project UVs are uniform in density but fill 0..1. Pick the smallest power-of-two texture that reaches
	TEXELS_PER_METRE, then shrink the islands so the density is exactly that. Returns the texture size."""
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
	per_unit = math.sqrt(a2 / a3)            # uv units per metre
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
	"""Anisotropic value noise: long along the hair flow, fine across it -> hair strokes."""
	f = flow.normalized()
	a = f.cross(Z if abs(f.z) < 0.9 else X).normalized()
	c = f.cross(a).normalized()
	q = Vector((p.dot(f) * 16.0, p.dot(a) * 120.0, p.dot(c) * 120.0))
	return 0.8 * value_noise3(q, 1.0, seed) + 0.2 * value_noise3(p, 6.0, seed + 7)


def grizzle(p, flow, dark, base, light, bias=0.0):
	s = hair_noise(p, flow, 3) + bias
	if s > 0.66:
		return light
	if s < 0.33:
		return dark
	return base


def head_colour(p, n):
	"""Head mask by the angle round the head axis: white blaze on top, a black stripe each side over the eye to the
	ear, white cheeks, black chin and throat. The blaze widens from the nose to the crown."""
	y = p.y
	zc, hw, hu, hd, _ = trunk_frame(y)
	ang = math.degrees(math.atan2(abs(p.x) / max(hw, 1e-3), (p.z - zc) / max(hu if p.z > zc else hd, 1e-3)))
	t = smoothstep(-0.58, -0.34, y)            # 0 at the nose, 1 at the crown
	blaze = 12.0 + 12.0 * t + (value_noise3(p, 30.0, 4) - 0.5) * 6.0
	stripe = blaze + 34.0 + 6.0 * t
	flow = Vector((0, 1, -0.15))
	if ang < blaze:
		return grizzle(p, flow, C_WHITE_SHADE, C_WHITE, C_WHITE, bias=0.12)
	if ang < stripe:
		return grizzle(p, flow, C_BLACK, C_BLACK_SOFT, C_BLACK_SOFT)
	if ang < 128.0:
		return grizzle(p, flow, C_WHITE_SHADE, C_WHITE, C_WHITE, bias=0.10)
	return C_BLACK_SOFT


def paint(part, p, n):
	if part == EYE:
		return C_EYE
	if part in CLAW_PARTS:
		front = part in (CLAW_FR, CLAW_FL)
		d = PAW_FRONT_Y["F" if front else "H"] + 0.012 - p.y   # distance out from the toe line
		return C_CLAW_BASE if d < (0.016 if front else 0.010) else C_CLAW
	if part in (EAR_L, EAR_R):
		h, t, _ = BONES["ear_L" if part == EAR_L else "ear_R"]
		if (p - Vector(h)).length > 0.040:
			return C_WHITE                         # pale rim along the top of the ear
		return C_BLACK_SOFT
	if part == TAIL:
		return grizzle(p, Vector((0, 1, 0)), C_GREY, C_GRIZZLE, C_GRIZZLE, bias=0.05)
	if part in LEG_PARTS:
		down = Vector((0, 0.15, -1))
		if p.z > 0.24 and abs(p.x) > 0.15:
			return grizzle(p, down, C_BLACK_SOFT, C_GREY_DARK, C_GREY)      # shoulder / thigh blends into the coat
		return grizzle(p, down, C_BLACK, C_BLACK_SOFT, C_GREY_DARK, bias=-0.08)
	# trunk / tufts / head
	if p.y < -0.555:
		return C_NOSE if (p - NOSE_TIP).length < 0.034 else C_BLACK_SOFT
	if p.y < -0.335 or (part != TUFT and p.y < -0.30 and abs(p.x) < 0.035 and p.z > 0.33):
		if p.y < -0.335:
			return head_colour(p, n)
		return grizzle(p, Vector((0, 1, 0)), C_WHITE_SHADE, C_GRIZZLE, C_WHITE)   # blaze fading onto the neck
	zc, hw, hu, hd, _ = trunk_frame(p.y)
	v = (p.z - zc) / (hu if p.z > zc else hd)
	v += (value_noise3(p, 9.0, 1) - 0.5) * 0.16
	flow = Vector((0, 1, -0.2 - 0.5 * max(0.0, -v)))
	if v < -0.62:
		return grizzle(p, flow, C_BLACK, C_BELLY, C_BELLY)
	if v < -0.30:
		return grizzle(p, flow, C_BELLY, C_GREY_DARK, C_GREY)
	if v > 0.80:
		return grizzle(p, flow, C_GREY_DARK, C_GREY, C_GRIZZLE, bias=-0.05)   # darker saddle
	return grizzle(p, flow, C_GREY_DARK, C_GREY, C_GRIZZLE, bias=0.04)


# ------------------------------------------------------------------------------------------ weights
def vertex_weights(pid, p):
	if pid == EYE:
		return {"head": 1.0}
	if pid in (EAR_L, EAR_R):
		return {"ear_L" if pid == EAR_L else "ear_R": 0.85, "head": 0.15}
	if pid == TAIL:
		h, t, _ = BONES["tail"]
		s = cd.seg_param(p, Vector(h), Vector(t)) * (Vector(t) - Vector(h)).length
		w = smoothstep(-0.01, 0.04, s)
		return {"tail": w, "pelvis": 1.0 - w}
	if pid in CLAW_PARTS:
		return {LEGS[LEG_OF_PART[pid]][1]: 1.0}
	if pid in LEG_PARTS:
		up, lo, _, body = LEGS[LEG_OF_PART[pid]]
		P = Vector(BONES[up][0])
		K = Vector(BONES[up][1])
		wl = smoothstep(K.z + 0.025, K.z - 0.025, p.z)
		wb = smoothstep(P.z - 0.05, P.z + 0.04, p.z) * (1.0 - wl)
		return {lo: wl, body: wb, up: 1.0 - wl - wb}
	# trunk + tufts: blends along the body, rump -> spine -> neck -> head
	y = p.y
	head = smoothstep(-0.33, -0.40, y)
	neck_all = smoothstep(-0.20, -0.29, y)
	rest = 1.0 - neck_all
	wp = rest * smoothstep(-0.02, 0.16, y)
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
WALK_SPEED = 1.1
WALK_FRAMES = 12
RUN_SPEED = 6.0
RUN_FRAMES = 8
IDLE_LEN = 4.0
GRAZE_LEN = 3.0
BITE_LEN = 0.5


def rest_feet():
	return {k: Vector(BONES[v[1]][1]) for k, v in LEGS.items()}


def walk_pose(t):
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.60
	half = WALK_SPEED * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.25, "HR": 0.5, "FR": 0.75}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.050 if k[0] == "F" else 0.040,
						 center_dy=0.020 if k[0] == "F" else -0.010) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0.006 * math.sin(w), 0, -0.032 + 0.006 * math.cos(2 * w))),
		"rot": {
			# the waddle: the low, wide body rolls and swings over each planted pair
			"pelvis": euler_q(yaw=3.0 * math.sin(w), roll=4.5 * math.sin(w)),
			"spine": euler_q(yaw=-2.0 * math.sin(w), roll=-3.0 * math.sin(w)),
			"neck": euler_q(pitch=4.0 + 2.0 * math.cos(2 * w + 0.5), yaw=-2.0 * math.sin(w)),
			"head": euler_q(pitch=3.0 - 2.0 * math.cos(2 * w + 0.5), yaw=4.0 * math.sin(w + 0.8)),
			"tail": euler_q(pitch=-4.0, yaw=8.0 * math.sin(w)),
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
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.075 if k[0] == "F" else 0.065,
						 center_dy=0.02 if k[0] == "F" else -0.015) for k, st in starts.items()}
	w = 2 * math.pi * ph
	flex = 11.0 * math.cos(w - 2 * math.pi * 0.80)          # + = gathered (back arched)
	pitch = -5.0 * math.cos(w - 2 * math.pi * 0.10)
	lift = -0.050 + 0.020 * (0.5 + 0.5 * math.cos(w - 2 * math.pi * 0.8))
	return {
		"pelvis_loc": Vector((0, 0, lift)),
		"rot": {
			"pelvis": euler_q(pitch=pitch - flex * 0.5),
			"spine": euler_q(pitch=flex),
			"neck": euler_q(pitch=-0.5 * (pitch + flex * 0.5) + 4.0),
			"head": euler_q(pitch=-4.0),
			"tail": euler_q(pitch=-14.0 + 6.0 * math.sin(w)),
			"ear_L": euler_q(pitch=-12.0),
			"ear_R": euler_q(pitch=-12.0),
		},
		"feet": feet,
	}


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 3 / IDLE_LEN)
	sniff = pulse(t, 1.4, 0.15) + pulse(t, 1.62, 0.15) + pulse(t, 3.1, 0.15)
	ear = pulse(t, 2.4, 0.2)
	return {
		"pelvis_loc": Vector((0.004 * math.sin(w), 0, -0.012 + 0.0025 * breath)),
		"rot": {
			"pelvis": euler_q(roll=1.5 * math.sin(w)),
			"spine": euler_q(pitch=0.6 * breath, roll=-1.0 * math.sin(w)),
			"neck": euler_q(pitch=2.0 + 3.0 * math.sin(2 * w + 0.4), yaw=12.0 * math.sin(w + 1.0)),
			"head": euler_q(pitch=-4.0 * sniff + 2.0 * math.sin(2 * w), yaw=5.0 * math.sin(w + 1.7)),
			"tail": euler_q(pitch=-2.0, yaw=6.0 * math.sin(w * 2)),
			"ear_L": euler_q(pitch=-20.0 * ear),
			"ear_R": euler_q(pitch=-6.0 * ear),
		},
	}


def graze_pose(t):
	"""Rooting for worms: nose down in the soil, the left fore paw scraping back twice per loop."""
	w = 2 * math.pi * t / GRAZE_LEN
	nose = math.sin(2 * math.pi * t * 5 / GRAZE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	dig = (t / GRAZE_LEN * 2.0) % 1.0
	if dig < 0.55:
		s = dig / 0.55
		feet["FL"] = rf["FL"] + Vector((0, -0.07 + 0.11 * s, 0.012 * math.sin(math.pi * s)))
	else:
		s = (dig - 0.55) / 0.45
		feet["FL"] = rf["FL"] + Vector((0, 0.04 - 0.11 * s, 0.045 * math.sin(math.pi * s)))
	return {
		"pelvis_loc": Vector((0, 0.01, -0.030)),
		"rot": {
			"pelvis": euler_q(pitch=-3.0),
			"spine": euler_q(pitch=6.0, roll=1.5 * math.sin(w)),
			"neck": euler_q(pitch=17.0 + 1.5 * nose, yaw=6.0 * math.sin(w)),
			"head": euler_q(pitch=12.0 + 3.0 * nose, yaw=4.0 * math.sin(w + 0.6)),
			"tail": euler_q(pitch=-6.0, yaw=10.0 * math.sin(w * 2)),
			"ear_L": euler_q(pitch=-6.0),
			"ear_R": euler_q(pitch=-6.0),
		},
		"feet": feet,
	}


# (t, forward m, drop m, spine pitch, neck pitch, head pitch) - pitch + = nose down
BITE_KEYS = [
	(0.00, 0.000, 0.000, 0.0, 0.0, 0.0),
	(0.12, -0.030, -0.022, 5.0, 8.0, 2.0),
	(0.25, 0.055, -0.010, -6.0, -18.0, -12.0),
	(0.31, 0.060, -0.012, -4.0, 8.0, 20.0),
	(0.50, 0.000, 0.000, 0.0, 0.0, 0.0),
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
	_, fwd, drop, spine, neck, head = v
	return {
		"pelvis_loc": Vector((0, -fwd, drop)),
		"rot": {
			"pelvis": euler_q(pitch=-spine * 0.3),
			"spine": euler_q(pitch=spine),
			"neck": euler_q(pitch=neck),
			"head": euler_q(pitch=head),
			"tail": euler_q(pitch=-10.0 * abs(neck) / 18.0),
			"ear_L": euler_q(pitch=-0.8 * abs(neck)),
			"ear_R": euler_q(pitch=-0.8 * abs(neck)),
		},
	}


CLIPS = [
	# name, seconds, pose fn, fade, loop, speed
	("idle", IDLE_LEN, idle_pose, 0.3, True, 0.0),
	("walk", WALK_FRAMES / FPS, walk_pose, 0.2, True, WALK_SPEED),
	("run", RUN_FRAMES / FPS, run_pose, 0.15, True, RUN_SPEED),
	("graze", GRAZE_LEN, graze_pose, 0.35, True, 0.0),
	("bite", BITE_LEN, bite_pose, 0.08, False, 0.0),
]

VMAT = """// Badger (Blender/scripts/create_badger.py): fur-stroke texture at 256 texels per metre, point filtered, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/{name}.png"
}
"""


# ------------------------------------------------------------------------------------------ preview renders
def render_previews(rig, meshes, tex_path, out_dir):
	"""Workbench renders of LOD0 with the texture, point sampled, flat - one per view."""
	os.makedirs(out_dir, exist_ok=True)
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
	target = Vector((0, -0.05, 0.2))
	views = {"side": Vector((-2.2, -0.05, 0.35)), "front34": Vector((-1.4, -1.6, 0.6)),
			 "rear34": Vector((1.3, 1.6, 0.7)), "front": Vector((0.0, -2.2, 0.35)), "top": Vector((0.01, 0.0, 2.3))}
	for name, pos in views.items():
		cam.location = pos
		d = target - pos
		cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
		sc.render.filepath = os.path.join(out_dir, f"{NAME}_{name}.png")
		bpy.ops.render.render(write_still=True)
	# a few posed frames from the clips
	for clip, frame in (("walk", 6), ("run", 3), ("graze", 30), ("bite", 9)):
		act = bpy.data.actions.get(clip)
		if act is None:
			continue
		rig.animation_data.action = act
		rig.animation_data.action_slot = act.slots[0]
		sc.frame_set(frame)
		cam.location = views["front34"]
		d = target - cam.location
		cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
		sc.render.filepath = os.path.join(out_dir, f"{NAME}_pose_{clip}.png")
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
		os.makedirs(MODEL_DIR, exist_ok=True)
		os.makedirs(ANIM_DIR, exist_ok=True)
		os.makedirs(MAT_DIR, exist_ok=True)
		os.makedirs(os.path.dirname(BLEND), exist_ok=True)
		cd.TEX = tex                               # paint_texture rasterises at the module's TEX
		cd.write_png(tex_path, cd.paint_texture(meshes, paint_fn=paint, grain_sigma=2.0, fill=C_GREY))
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
				  "Blender metres export as 100 u/m; 0.4 gives the project's 40 u/m for mesh and clips (back ~17 u).",
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
