"""Showcase renders of the current elm set, for sharing.

    blender -b Blender/blenderprojects/environment_elm_v<N>.blend --python Blender/scripts/render_elm_showcase.py

Writes Blender/blenderprojects/elm_showcase/<tree>_hero.png (three-quarter view, tree fills the
frame), <tree>_crown.png (into the canopy from outside), <tree>_trunk.png (fork and base) and
lineup.png (all six side by side), 1600 px, EEVEE, sky + ground + sun."""
import bpy, math, os, re

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
OUT = os.path.join(ROOT, "Blender", "blenderprojects", "elm_showcase")
os.makedirs(OUT, exist_ok=True)
scene = bpy.context.scene
for eng in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
	try:
		scene.render.engine = eng
		break
	except TypeError:
		pass
scene.render.resolution_x = scene.render.resolution_y = 1600
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

# the trees: <name>_wood + <name>_leaves pairs (LOD and probe objects stay hidden)
trees = {}
for o in bpy.data.objects:
	m = re.fullmatch(r"(environment_elm\d_v\d+)_(wood|leaves)", o.name)
	if m:
		trees.setdefault(m.group(1), {})[m.group(2)] = o
for o in bpy.data.objects:
	if o.type == 'MESH' and o is not ground:
		o.hide_render = True


def look_at(loc, target):
	cam.location = loc
	d = (target - cam.location)
	cam.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()


def bounds(objs):
	from mathutils import Vector
	lo = Vector((1e9, 1e9, 1e9))
	hi = -lo
	for o in objs:
		for v in o.data.vertices:
			p = o.matrix_world @ v.co
			lo = Vector((min(lo.x, p.x), min(lo.y, p.y), min(lo.z, p.z)))
			hi = Vector((max(hi.x, p.x), max(hi.y, p.y), max(hi.z, p.z)))
	return lo, hi


from mathutils import Vector
saved = {n: (t["wood"].location.copy(), t["leaves"].location.copy()) for n, t in trees.items()}
for name, t in sorted(trees.items()):
	for o in (t["wood"], t["leaves"]):
		o.location = (0, 0, 0)
		o.hide_render = False
	lo, hi = bounds([t["wood"], t["leaves"]])
	h = hi.z
	r = max(hi.x - lo.x, hi.y - lo.y) / 2
	centre = Vector((0, 0, h * 0.55))
	# hero: three-quarter view, slightly below crown centre, the tree filling the frame
	cam.data.lens = 40
	dist = max(h, 2 * r) * 1.45
	look_at(Vector((dist * 0.62, -dist * 0.72, h * 0.42)), centre)
	scene.render.filepath = os.path.join(OUT, name + "_hero.png")
	bpy.ops.render.render(write_still=True)
	# crown: close on the canopy from just outside its edge, looking into the leaf cards
	cam.data.lens = 35
	k = (r + 6) / math.sqrt(2)
	look_at(Vector((k, -k, h * 0.74)), Vector((r * 0.3 / math.sqrt(2), -r * 0.3 / math.sqrt(2), h * 0.7)))
	scene.render.filepath = os.path.join(OUT, name + "_crown.png")
	bpy.ops.render.render(write_still=True)
	# trunk: fork, flare and roots, person-height camera
	cam.data.lens = 32
	look_at(Vector((7.5, -10.5, 2.0)), Vector((0, 0, h * 0.28)))
	scene.render.filepath = os.path.join(OUT, name + "_trunk.png")
	bpy.ops.render.render(write_still=True)
	print("RENDERED", name)
	for o in (t["wood"], t["leaves"]):
		o.hide_render = True

# lineup: all six in a row
names = sorted(trees)
for i, name in enumerate(names):
	for o in (trees[name]["wood"], trees[name]["leaves"]):
		o.location = ((i - (len(names) - 1) / 2) * 30, 0, 0)
		o.hide_render = False
scene.render.resolution_x, scene.render.resolution_y = 3200, 1200
cam.data.lens = 35
look_at(Vector((0, -190, 22)), Vector((0, 0, 14)))
scene.render.filepath = os.path.join(OUT, "lineup.png")
bpy.ops.render.render(write_still=True)
print("RENDERED lineup")
