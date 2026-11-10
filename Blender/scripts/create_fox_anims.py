"""Rig and animate the existing Tripo fox (look unchanged): armature + weights in fox.blend, six clips, vmdl.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_fox_anims.py"
    ... --python "Blender/scripts/create_fox_anims.py" -- check      rig + bake, print the foot / IK report, write nothing

Reads   Blender/blenderprojects/entities/fox.blend (mesh tripo_node_703ba6ca; an earlier fox_skeleton is rebuilt)
Writes  Assets/models/entities/fox_v2.fbx                     same mesh, same size and rest pose, skinned to fox_skeleton
        Assets/models/entities/animations/fox_v2@<clip>.fbx   one clip each (armature only)
        Assets/models/entities/fox_v2.vmdl                    ScaleAndMirror 0.5 (50 u per Blender metre, 1.0 m Blender
                                                              nose-to-tail = 50 u), material materials/entities/fox_v2.vmat
        Blender/blenderprojects/entities/fox.blend            saved with the rig and the actions
The UVs + texture come from Blender/scripts/create_fox_texture.py, which runs this script after painting (v2 = textured;
the v1 fox.* / fox@*.fbx files were deleted). File names carry the version (NAME); the skeleton stays "fox_skeleton".

Scale: the fox mesh is 1.0 Blender m nose to tail, back at 0.40 m. At 50 u/m that is a 20 u shoulder = 0.5 world metres
(the project's 40 u/m), so one Blender metre of the fox is 1.25 world metres: foot speeds below are world m/s x 0.8.

Frame: Blender -Y = fox forward (engine +X), Blender +X = the fox's left, Z up, origin on the ground (unchanged).

Rig: root -> pelvis -> spine -> neck -> head -> ear_L / ear_R; pelvis -> tail_1 -> tail_2, hind legs; spine -> front legs.
Legs are two bones each (upper: shoulder / hip -> carpus / hock, lower: -> paw contact), analytic two-bone IK per frame
(shared with Blender/scripts/create_deer.py), so a planted paw never slides.

Clips (SkinnedModelRenderer.Sequence.Name), 30 fps:
  idle    4.00 s   loop   breathing, look around, tail sway, ear flicks
  walk    0.300 s  loop   diagonal trot at 3 m/s       -> 0.900 m travelled per cycle
  run     0.333 s  loop   rotary gallop at 7 m/s       -> 2.333 m travelled per cycle
  sneak   0.600 s  loop   low stalking creep, 1.2 m/s  -> 0.720 m travelled per cycle
  graze   3.00 s   loop   nose down, sniffing along the ground
  bite    0.50 s   once   crouch, lunge, snap at 0.30 s, recover to the idle stance
Metres per cycle are world metres; play at PlaybackRate = actual speed / clip speed to keep the paws planted.
"""
import bpy, math, os, sys
from mathutils import Vector, Quaternion

sys.dont_write_bytecode = True    # importing create_deer must not leave a __pycache__ entry
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_deer as cd  # noqa: E402
from create_deer import euler_q, gait_foot, pulse, smoothstep, Poser, X, Y, Z  # noqa: E402

ROOT = cd.ROOT
MODEL_DIR = cd.MODEL_DIR
ANIM_DIR = cd.ANIM_DIR
BLEND = os.path.join(ROOT, "Blender", "blenderprojects", "entities", "fox.blend")
NAME = "fox_v2"                   # file version (v2 = red-fox texture, 2026-11-02)
RIG = "fox"                       # skeleton object name, unchanged across versions
MESH = "tripo_node_703ba6ca"
FPS = cd.FPS
SCALE = 0.5                       # vmdl ScaleAndMirror: Blender metre -> 50 u
WORLD_TO_BLEND = 40.0 / (100.0 * SCALE)     # world metres -> fox Blender metres (0.8)

BONES = {
	"root": ((0, 0, 0), (0, -0.12, 0), None),
	"pelvis": ((0, 0.10, 0.30), (0, -0.06, 0.31), "root"),
	"spine": ((0, -0.06, 0.31), (0, -0.24, 0.32), "pelvis"),
	"neck": ((0, -0.26, 0.33), (0, -0.36, 0.43), "spine"),
	"head": ((0, -0.36, 0.43), (0, -0.50, 0.40), "neck"),
	"ear_L": ((0.050, -0.375, 0.500), (0.075, -0.372, 0.565), "head"),
	"ear_R": ((-0.050, -0.375, 0.500), (-0.075, -0.372, 0.565), "head"),
	"tail_1": ((0, 0.15, 0.30), (0, 0.30, 0.22), "pelvis"),
	"tail_2": ((0, 0.30, 0.22), (0, 0.49, 0.06), "tail_1"),
	"leg_upper_FR": ((-0.055, -0.205, 0.330), (-0.052, -0.222, 0.085), "spine"),
	"leg_lower_FR": ((-0.052, -0.222, 0.085), (-0.055, -0.255, 0.0), "leg_upper_FR"),
	"leg_upper_FL": ((0.055, -0.205, 0.330), (0.052, -0.222, 0.085), "spine"),
	"leg_lower_FL": ((0.052, -0.222, 0.085), (0.055, -0.255, 0.0), "leg_upper_FL"),
	"leg_upper_HR": ((-0.070, 0.080, 0.300), (-0.075, 0.150, 0.110), "pelvis"),
	"leg_lower_HR": ((-0.075, 0.150, 0.110), (-0.088, 0.105, 0.0), "leg_upper_HR"),
	"leg_upper_HL": ((0.070, 0.080, 0.300), (0.075, 0.150, 0.110), "pelvis"),
	"leg_lower_HL": ((0.075, 0.150, 0.110), (0.088, 0.105, 0.0), "leg_upper_HL"),
}
LEGS = {
	"FR": ("leg_upper_FR", "leg_lower_FR", 1.0, "spine"),
	"FL": ("leg_upper_FL", "leg_lower_FL", 1.0, "spine"),
	"HR": ("leg_upper_HR", "leg_lower_HR", -1.0, "pelvis"),
	"HL": ("leg_upper_HL", "leg_lower_HL", -1.0, "pelvis"),
}


# ------------------------------------------------------------------------------------------ weights
def leg_window(y, front):
	if front:
		return smoothstep(-0.37, -0.32, y) * smoothstep(-0.09, -0.14, y)
	return smoothstep(-0.08, -0.03, y) * smoothstep(0.215, 0.17, y)


def vertex_weights(p):
	x, y, z = p.x, p.y, p.z
	side = "L" if x > 0 else "R"
	w = {}
	# tail: behind the rump and above the hind paws
	if y > 0.13 and (z > 0.17 or y > 0.21):
		t2 = smoothstep(0.27, 0.34, y)
		t1 = smoothstep(0.13, 0.20, y) * (1.0 - t2)
		w["tail_2"] = t2
		w["tail_1"] = t1
		w["pelvis"] = 1.0 - t1 - t2
		return w
	# legs: everything low inside a leg window, off the belly midline
	lat = smoothstep(0.004, 0.03, abs(x))
	total = 0.0
	for front in (True, False):
		top = 0.27 if front else 0.25
		L = leg_window(y, front) * smoothstep(top, top - 0.07, z) * lat
		if L <= 0.0:
			continue
		key = ("F" if front else "H") + side
		up, lo, _, _ = LEGS[key]
		kz = BONES[up][1][2]
		wl = smoothstep(kz + 0.025, kz - 0.025, z)
		w[lo] = w.get(lo, 0.0) + L * wl
		w[up] = w.get(up, 0.0) + L * (1.0 - wl)
		total += L
	rest = max(0.0, 1.0 - total)
	if rest <= 0.0:
		return w
	# trunk -> neck -> head planes, ears on top of the head
	yz = Vector((y, z))
	d_neck = (yz - Vector((-0.27, 0.33))).dot(Vector((-1.0, 0.3)).normalized())
	wn_all = smoothstep(-0.02, 0.06, d_neck)
	d_head = (yz - Vector((-0.36, 0.43))).dot(Vector((-0.939, 0.344)).normalized())
	wh = smoothstep(-0.02, 0.02, d_head) * wn_all if z > 0.30 else 0.0
	wn = wn_all - wh
	we = 0.0
	if z > 0.49 and -0.43 < y < -0.32:
		we = smoothstep(0.495, 0.525, z) * wh
		wh -= we
	body = 1.0 - wn_all
	wp = body * smoothstep(-0.10, 0.08, y)
	for bn, v in (("pelvis", wp), ("spine", body - wp), ("neck", wn), ("head", wh), ("ear_" + side, we)):
		w[bn] = w.get(bn, 0.0) + v * rest
	return w


def build_rig_and_skin():
	bpy.context.scene.render.fps = FPS
	for o in list(bpy.data.objects):
		if o.type == "ARMATURE":
			bpy.data.objects.remove(o, do_unlink=True)
	for a in list(bpy.data.actions):
		bpy.data.actions.remove(a)
	mesh = bpy.data.objects[MESH]
	mesh.parent = None
	for m in list(mesh.modifiers):
		mesh.modifiers.remove(m)
	mesh.vertex_groups.clear()
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
			e.use_connect = name.startswith("leg_lower") or name in ("spine", "head", "tail_2")
	bpy.ops.object.mode_set(mode="OBJECT")
	for pb in rig.pose.bones:
		pb.rotation_mode = "QUATERNION"
	for name in BONES:
		mesh.vertex_groups.new(name=name)
	mw = mesh.matrix_world
	for v in mesh.data.vertices:
		ws = vertex_weights(mw @ v.co)
		s = sum(ws.values())
		for bn, wv in ws.items():
			if wv > 1e-4:
				mesh.vertex_groups[bn].add([v.index], wv / s, "REPLACE")
	mesh.parent = rig
	mod = mesh.modifiers.new("Armature", "ARMATURE")
	mod.object = rig
	return rig, mesh


# ------------------------------------------------------------------------------------------ clips
def B(world_mps):
	return world_mps * WORLD_TO_BLEND


def rest_feet():
	return {k: Vector(BONES[v[1]][1]) for k, v in LEGS.items()}


WALK_SPEED, WALK_FRAMES = 3.0, 9
RUN_SPEED, RUN_FRAMES = 7.0, 10
SNEAK_SPEED, SNEAK_FRAMES = 1.2, 18
IDLE_LEN, GRAZE_LEN, BITE_LEN = 4.0, 3.0, 0.5


def walk_pose(t):
	"""Trot: diagonal pairs (FL + HR, FR + HL), short suspension, tail streaming a little."""
	T = WALK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.42
	half = B(WALK_SPEED) * T * duty * 0.5
	starts = {"FL": 0.0, "HR": 0.02, "FR": 0.5, "HL": 0.52}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.065 if k[0] == "F" else 0.055,
						 center_dy=0.055 if k[0] == "F" else -0.01, peak=0.8) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0, 0, -0.030 + 0.006 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(roll=2.0 * math.sin(w), yaw=1.5 * math.sin(w)),
			"spine": euler_q(roll=-1.5 * math.sin(w)),
			"neck": euler_q(pitch=12.0 + 2.0 * math.cos(2 * w)),
			"head": euler_q(pitch=-8.0 - 2.0 * math.cos(2 * w)),
			"tail_1": euler_q(pitch=8.0, yaw=5.0 * math.sin(w)),
			"tail_2": euler_q(pitch=4.0, yaw=7.0 * math.sin(w - 0.8)),
			"ear_L": euler_q(pitch=-6.0),
			"ear_R": euler_q(pitch=-6.0),
		},
		"feet": feet,
	}


def run_pose(t):
	"""Rotary gallop with back flexion; tail streams straight back and waves."""
	T = RUN_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.22
	half = B(RUN_SPEED) * T * duty * 0.5
	starts = {"HL": 0.0, "HR": 0.08, "FR": 0.36, "FL": 0.46}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.10 if k[0] == "F" else 0.09,
						 center_dy=0.055 if k[0] == "F" else -0.01) for k, st in starts.items()}
	w = 2 * math.pi * ph
	pitch = -6.0 * math.cos(w - 2 * math.pi * 0.14)
	flex = 12.0 * math.cos(w - 2 * math.pi * 0.86)
	lift = -0.054 + 0.036 * (0.5 + 0.5 * math.cos(w - 2 * math.pi * 0.70))
	return {
		"pelvis_loc": Vector((0, 0, lift)),
		"rot": {
			"pelvis": euler_q(pitch=pitch - flex * 0.5),
			"spine": euler_q(pitch=flex),
			"neck": euler_q(pitch=-0.6 * (pitch + flex * 0.5) + 28.0),
			"head": euler_q(pitch=-14.0),
			"tail_1": euler_q(pitch=24.0 + 6.0 * math.sin(w)),
			"tail_2": euler_q(pitch=4.0 + 8.0 * math.sin(w - 1.2)),
			"ear_L": euler_q(pitch=-22.0),
			"ear_R": euler_q(pitch=-22.0),
		},
		"feet": feet,
	}


def sneak_pose(t):
	"""Low stalking creep: body dropped, head level and forward, tail low and still, slow lateral walk."""
	T = SNEAK_FRAMES / FPS
	ph = (t / T) % 1.0
	duty = 0.66
	half = B(SNEAK_SPEED) * T * duty * 0.5
	starts = {"HL": 0.0, "FL": 0.25, "HR": 0.5, "FR": 0.75}
	rf = rest_feet()
	feet = {k: gait_foot(rf[k], ph, st, duty, half, 0.045 if k[0] == "F" else 0.04,
						 center_dy=0.035 if k[0] == "F" else 0.0, peak=0.9) for k, st in starts.items()}
	w = 2 * math.pi * ph
	return {
		"pelvis_loc": Vector((0, 0, -0.065 + 0.003 * math.cos(2 * w))),
		"rot": {
			"pelvis": euler_q(pitch=-3.0, roll=1.0 * math.sin(w)),
			"spine": euler_q(pitch=4.0),
			"neck": euler_q(pitch=38.0),
			"head": euler_q(pitch=-30.0, yaw=1.5 * math.sin(w)),
			"tail_1": euler_q(pitch=10.0),
			"tail_2": euler_q(pitch=8.0, yaw=2.0 * math.sin(w)),
			"ear_L": euler_q(pitch=12.0),
			"ear_R": euler_q(pitch=12.0),
		},
		"feet": feet,
	}


def idle_pose(t):
	w = 2 * math.pi * t / IDLE_LEN
	breath = math.sin(2 * math.pi * t * 4 / IDLE_LEN)
	el = pulse(t, 0.9, 0.2)
	er = pulse(t, 2.6, 0.16) + pulse(t, 2.85, 0.16)
	return {
		"pelvis_loc": Vector((0.004 * math.sin(w), 0, -0.004 + 0.0012 * breath)),
		"rot": {
			"pelvis": euler_q(roll=1.2 * math.sin(w)),
			"spine": euler_q(pitch=0.8 * breath, roll=-0.8 * math.sin(w)),
			"neck": euler_q(pitch=2.0 * math.sin(2 * w + 0.4), yaw=10.0 * math.sin(w + 1.0)),
			"head": euler_q(pitch=1.5 * math.sin(2 * w), yaw=6.0 * math.sin(w + 1.7), roll=4.0 * pulse(t, 1.7, 0.9)),
			"tail_1": euler_q(pitch=2.0, yaw=7.0 * math.sin(w)),
			"tail_2": euler_q(pitch=2.0, yaw=10.0 * math.sin(w - 0.9)),
			"ear_L": euler_q(pitch=-28.0 * el, yaw=10.0 * el),
			"ear_R": euler_q(pitch=-24.0 * er, yaw=-10.0 * er),
		},
	}


def graze_pose(t):
	"""Nose to the ground, quick sniffing nods, head sweeping a little side to side."""
	w = 2 * math.pi * t / GRAZE_LEN
	sniff = math.sin(2 * math.pi * t * 9 / GRAZE_LEN)
	rf = rest_feet()
	feet = dict(rf)
	feet["FR"] = rf["FR"] + Vector((0, -0.04, 0))
	return {
		"pelvis_loc": Vector((0, 0, -0.02)),
		"rot": {
			"pelvis": euler_q(pitch=1.0),
			"spine": euler_q(pitch=8.0),
			"neck": euler_q(pitch=104.0 + 1.2 * sniff, yaw=9.0 * math.sin(w)),
			"head": euler_q(pitch=-70.0 + 3.5 * sniff, yaw=4.0 * math.sin(w + 0.6)),
			"tail_1": euler_q(pitch=6.0, yaw=6.0 * math.sin(w)),
			"tail_2": euler_q(pitch=4.0, yaw=8.0 * math.sin(w - 0.9)),
			"ear_L": euler_q(pitch=-22.0 + 6.0 * math.sin(w)),
			"ear_R": euler_q(pitch=-22.0 + 6.0 * math.sin(w + 2.0)),
		},
		"feet": feet,
	}


BITE_KEYS = [
	# t, body forward (m, -Y), body drop, spine, neck, head, tail
	(0.00, 0.000, 0.000, 0.0, 0.0, 0.0, 2.0),
	(0.13, -0.030, -0.035, -3.0, -12.0, -6.0, 10.0),
	(0.25, 0.065, -0.010, 6.0, 34.0, -24.0, 18.0),
	(0.30, 0.070, -0.012, 7.0, 38.0, -6.0, 18.0),
	(0.36, 0.060, -0.010, 6.0, 32.0, -20.0, 14.0),
	(0.50, 0.000, 0.000, 0.0, 0.0, 0.0, 2.0),
]


def bite_pose(t):
	"""One-shot lunge snap: wind back and down, lunge forward with the head driving out, snap (head nods down
	and up) at 0.30 s, settle back to the idle stance at 0.50 s. Paws stay planted."""
	keys = BITE_KEYS
	for i in range(len(keys) - 1):
		if t <= keys[i + 1][0] or i == len(keys) - 2:
			a, b = keys[i], keys[i + 1]
			u = min(1.0, max(0.0, (t - a[0]) / (b[0] - a[0])))
			u = u * u * (3 - 2 * u)
			v = [a[j] + (b[j] - a[j]) * u for j in range(len(a))]
			break
	_, fwd, drop, spine, neck, head, tail = v
	return {
		"pelvis_loc": Vector((0, -fwd, drop)),
		"rot": {
			"pelvis": euler_q(pitch=-spine * 0.3),
			"spine": euler_q(pitch=spine),
			"neck": euler_q(pitch=neck),
			"head": euler_q(pitch=head),
			"tail_1": euler_q(pitch=tail),
			"tail_2": euler_q(pitch=tail * 0.5),
			"ear_L": euler_q(pitch=-0.6 * neck),
			"ear_R": euler_q(pitch=-0.6 * neck),
		},
	}


CLIPS = [
	# name, seconds, pose fn, fade, loop, world speed
	("idle", IDLE_LEN, idle_pose, 0.3, True, 0.0),
	("walk", WALK_FRAMES / FPS, walk_pose, 0.2, True, WALK_SPEED),
	("run", RUN_FRAMES / FPS, run_pose, 0.15, True, RUN_SPEED),
	("sneak", SNEAK_FRAMES / FPS, sneak_pose, 0.25, True, SNEAK_SPEED),
	("graze", GRAZE_LEN, graze_pose, 0.3, True, 0.0),
	("bite", BITE_LEN, bite_pose, 0.08, False, 0.0),
]


def main(check_only=False):
	bpy.ops.wm.open_mainfile(filepath=BLEND)
	rig, mesh = build_rig_and_skin()
	poser = Poser(rig, BONES, LEGS)
	if not check_only:
		os.makedirs(ANIM_DIR, exist_ok=True)
		bpy.ops.object.select_all(action="DESELECT")
		for o in (rig, mesh):
			o.select_set(True)
		bpy.context.view_layer.objects.active = rig
		bpy.ops.export_scene.fbx(
			filepath=os.path.join(MODEL_DIR, NAME + ".fbx"), use_selection=True, object_types={"ARMATURE", "MESH"},
			add_leaf_bones=False, bake_anim=False, mesh_smooth_type="OFF", path_mode="STRIP", embed_textures=False,
			apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE", bake_space_transform=False,
			axis_forward="-Z", axis_up="Y", use_armature_deform_only=False)
	for name, length, fn, _, loop, speed in CLIPS:
		action, n = cd.bake_clip(rig, poser, name, length, fn, loop=loop, tag=NAME)
		cd.foot_slide_report(rig, poser, name, n, B(speed), tag=NAME)
		if not check_only:
			cd.export_clip(rig, os.path.join(ANIM_DIR, f"{NAME}@{name}.fbx"))
	if check_only:
		return
	cd.write_vmdl(NAME, [MESH], [0.0], [(c[0], c[3], c[4]) for c in CLIPS], SCALE,
				  "Fox: Blender metres export as 100 u/m; 0.5 keeps the original size (50 u nose to tail, 20 u shoulder).",
				  f"materials/entities/{NAME}.vmat", f"{NAME}.vmat", physics=cd.PHYSICS_FROM_RENDER)
	rig.animation_data.action = None
	for pb in rig.pose.bones:
		pb.rotation_quaternion = Quaternion()
		pb.location = Vector()
	bpy.context.preferences.filepaths.save_version = 0      # no fox.blend1 backup next to the blend
	bpy.ops.wm.save_as_mainfile(filepath=BLEND)


if __name__ == "__main__":
	argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
	main(check_only=bool(argv) and argv[0] == "check")
