"""The chop sequence as frames + a GIF, from the current elm .blend:

    blender -b Blender/blenderprojects/environment_elm_v<N>.blend --python Blender/scripts/render_elm_felling_gif.py

Standing tree (with three axe-hit shakes) -> swap to its stump with the universal log stood on
the stump's point -> the log tips over under gravity, slides off and settles on the ground ->
(next chop) the log splits into its two halves. Also writes stills of the stump, the log on the
ground and the halves. Output: Blender/blenderprojects/elm_felling/ (frames + felling.gif).
Uses elm2; the log and halves are shared by every tree."""
import bpy, math, os, re, sys
from mathutils import Vector

sys.path.insert(0, os.path.join(os.path.dirname(bpy.data.filepath), "..", "scripts"))
import elm_felling as fell

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
OUT = os.path.join(ROOT, "Blender", "blenderprojects", "elm_felling")
os.makedirs(OUT, exist_ok=True)
FPS, SIZE = 12, 640

ver = re.search(r"_v(\d+)\.blend$", bpy.data.filepath).group(1)
tree = "environment_elm2_v" + ver
wood, leaves = bpy.data.objects[tree + "_wood"], bpy.data.objects[tree + "_leaves"]
stump = bpy.data.objects[f"environment_elm2_stump_v{ver}"]
log = bpy.data.objects[f"environment_elm_log_v{ver}"]
half = bpy.data.objects[f"environment_elm_loghalf_v{ver}"]
half2 = half.copy()
half2.data = half.data
bpy.context.scene.collection.objects.link(half2)

scene = bpy.context.scene
for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
	try:
		scene.render.engine = eng
		break
	except TypeError:
		pass
scene.render.resolution_x = scene.render.resolution_y = SIZE
scene.render.film_transparent = False
world = bpy.data.worlds.new("sky")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.52, 0.68, 0.92, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.9
scene.world = world
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
sun.data.energy = 4.5
sun.rotation_euler = (math.radians(48), 0, math.radians(30))
scene.collection.objects.link(sun)
gm = bpy.data.meshes.new("ground")
gm.from_pydata([(-300, -300, 0), (300, -300, 0), (300, 300, 0), (-300, 300, 0)], [], [(0, 1, 2, 3)])
ground = bpy.data.objects.new("ground", gm)
gmat = bpy.data.materials.new("grass")
gmat.use_nodes = True
gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.27, 0.42, 0.14, 1)
gm.materials.append(gmat)
scene.collection.objects.link(ground)
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
scene.camera = cam

for o in bpy.data.objects:
	if o.type == 'MESH' and o is not ground:
		o.hide_render = True
		o.hide_set(False)
for o in (wood, leaves, stump, log, half, half2):
	o.location = (0, 0, 0)
	o.rotation_euler = (0, 0, 0)


def look_at(loc, target):
	cam.location = loc
	cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()


def show(*objs):
	for o in (wood, leaves, stump, log, half, half2):
		o.hide_render = o not in objs


def shot(name):
	scene.render.filepath = os.path.join(OUT, name + ".png")
	bpy.ops.render.render(write_still=True)


h = max((wood.matrix_world @ v.co).z for v in leaves.data.vertices)
cam.data.lens = 38
WIDE = ((22, -30, 7.5), (1.5, 0, h * 0.5))      # the whole tree
CLOSE = ((12, -15, 4.5), (3.0, 0, 3.0))          # stump + the falling log
look_at(*WIDE)


def push(s):
	"""camera from the wide tree shot to the close stump shot, s = 0..1 (smoothstep)"""
	s = s * s * (3 - 2 * s)
	look_at(tuple(a + (b - a) * s for a, b in zip(WIDE[0], CLOSE[0])),
			tuple(a + (b - a) * s for a, b in zip(WIDE[1], CLOSE[1])))

# ---- the sequence
frames = []
f = 0


def frame():
	global f
	shot(f"frame_{f:03d}")
	frames.append(f)
	f += 1


# 1. standing, with three axe hits (a tiny shake each) in the second second
show(wood, leaves)
hit_frames = {14, 18, 22}
for k in range(26):
	for o in (wood, leaves):
		o.rotation_euler = (math.radians(0.7) if k in hit_frames else 0.0, 0, 0)
	frame()
for o in (wood, leaves):
	o.rotation_euler = (0, 0, 0)

# 2. swap: stump + the log stood on the stump's point (log origin = its cone point)
show(stump, log)
apex = Vector((0, 0, fell.STUMP_H))
log.location = apex
log.rotation_euler = (0, 0, 0)
for k in range(4):
	push(k / 12)
	frame()

# 3. tip over: rotate about the cone point, gravity ease (angle ~ t^2), falling toward +x
T = 22
for k in range(1, T + 1):
	s = k / T
	ang = math.radians(90) * (s * s)
	log.location = apex
	log.rotation_euler = (0, ang, 0)          # +y rotation: +z axis swings toward +x
	push(min(1.0, (4 + k) / 12))
	frame()

# 4. the point slides off the stump and the log drops flat, with a small bounce
for k, (dz, dx) in enumerate(((-0.1, 0.3), (-0.25, 0.6), (-0.4, 0.8), (-0.33, 0.9), (-0.4, 0.95), (-0.4, 0.95))):
	log.location = apex + Vector((dx, 0, dz))
	log.rotation_euler = (0, math.radians(90), 0)
	frame()
rest = log.location.copy()
for k in range(12):
	frame()

# 5. next chop: the log splits crosswise into its two halves (origin at each half's centre)
show(stump, half, half2)
ax = Vector((1, 0, 0))
for o, along, roll in ((half, fell.HALF_LEN / 2 - 0.2, 0.0), (half2, fell.HALF_LEN * 1.5 + 0.2, math.radians(14))):
	o.location = rest + ax * along + Vector((0, 0.15 if o is half2 else 0, 0))
	o.rotation_euler = (roll, math.radians(90), 0)
for k in range(14):
	frame()

# ---- stills
cam.data.lens = 40
show(stump)
look_at((6, -8, 3.2), (0, 0, 0.7))
shot("still_stump")
show(stump, log)
log.location, log.rotation_euler = rest, (0, math.radians(90), 0)
look_at((9, -11, 4), (4, 0, 0.6))
shot("still_log_on_ground")
show(stump, half, half2)
shot("still_halves")
show(log)
log.location, log.rotation_euler = (0, 0, 0), (0, 0, 0)
look_at((9, -11, 4.5), (0, 0, 4))
shot("still_log_standing")

# ---- gif: assembled afterwards by Blender/scripts/assemble_gif.py (Blender's python has no PIL)
