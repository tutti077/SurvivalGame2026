"""Red-fox texture for the rigged Tripo fox: new UVs on the existing mesh, a 256 px painted PNG, pixel_lit vmat, then the
rig / clips / vmdl are re-exported through create_fox_anims.py (same mesh, same skeleton and bone names, same clips).

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_fox_texture.py"
    ... --python "Blender/scripts/create_fox_texture.py" -- texture      UVs + PNG + vmat + blend only, no FBX export

Reads   Blender/blenderprojects/entities/fox.blend (mesh tripo_node_703ba6ca)
Writes  Assets/materials/entities/fox_v2.png + .vmat      256 x 256, pixel_lit, point filtered, faceted
        Blender/blenderprojects/entities/fox.blend        mesh UVs replaced (smart project), material "fox_v2"
        then create_fox_anims.main(): Assets/models/entities/fox_v2.fbx, animations/fox_v2@<clip>.fbx, fox_v2.vmdl

Markings (real red fox): rust-orange coat, darker along the back and the top of the tail, paler on the lower flanks;
white chin, cheeks, throat, chest bib and belly; white tail tip; black lower legs ("stockings") and black ear backs and
tips, cream inside the ears; darker rust on the muzzle sides, black nose, dark eyes. One colour per face (the borders
follow the facets), 3-4 low-contrast tones per region, no grain, no speckle, no hue noise.

Frame (create_fox_anims.py): Blender -Y = fox forward, +X = the fox's left, Z up, nose at y -0.50, tail tip at y +0.50.
"""
import bpy, math, os, sys
from mathutils import Vector

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import create_deer as cd  # noqa: E402  (paint_texture / write_png only)
import create_fox_anims as fa  # noqa: E402

NAME = fa.NAME                       # "fox_v2"
MESH = fa.MESH
BLEND = fa.BLEND
MAT_DIR = cd.MAT_DIR
PNG = os.path.join(MAT_DIR, NAME + ".png")

# ------------------------------------------------------------------------------------------ palette (sRGB 0..255)
RUST_DARK = (156, 68, 30)        # back line, top of the tail, forehead
RUST = (186, 86, 38)             # main coat
ORANGE = (204, 106, 50)          # sides, cheeks above the white
ORANGE_PALE = (214, 138, 86)     # lower flank / tail underside, the step into the white
WHITE = (230, 224, 212)
WHITE_SHADE = (210, 204, 192)    # belly, inner legs
BLACK = (40, 33, 31)             # stockings, ear backs, ear tips
BLACK_BROWN = (74, 50, 38)       # top edge of the stockings
EAR_IN = (222, 204, 182)
MUZZLE_DARK = (126, 60, 36)
NOSE = (28, 24, 24)
EYE = (36, 26, 20)

NOSE_TIP = Vector((0.0, -0.500, 0.399))
EYE_POS = Vector((0.035, -0.418, 0.445))


def smooth(e0, e1, x):
	return cd.smoothstep(e0, e1, x)


def is_head(y, z):
	d = (Vector((y, z)) - Vector((-0.36, 0.43))).dot(Vector((-0.939, 0.344)).normalized())
	return z > 0.33 and d > -0.005


def head(p, n):
	x, y, z = p
	ax = abs(x)
	if z > 0.488 and -0.445 < y < -0.33 and ax > 0.036:
		# ears (off the forehead midline): black backs and tips, cream inside the cup, rust front rim
		if z > 0.540 or n.y > -0.05:
			return BLACK
		if n.y < -0.55 and 0.046 < ax < 0.084 and z < 0.535:
			return EAR_IN
		return RUST_DARK
	if (p - NOSE_TIP).length < 0.020:
		return NOSE
	if (p - Vector((math.copysign(EYE_POS.x, x), EYE_POS.y, EYE_POS.z))).length < 0.0145:
		return EYE
	white_line = 0.398 + 0.30 * min(0.15, y + 0.50)       # mouth -> cheek: 0.398 at the nose, 0.443 behind the eyes
	if z < white_line and n.z < 0.55:
		return WHITE if n.z < -0.2 or y < -0.40 else WHITE_SHADE
	if y < -0.432 and z < 0.432 and ax > 0.008 and abs(n.x) > 0.35:
		return MUZZLE_DARK                                     # dark muzzle sides, eye to nose
	if n.z > 0.62:
		return RUST_DARK if y > -0.47 else RUST
	if y < -0.44:
		return RUST
	return ORANGE


def fox_paint(_pid, p, n):
	x, y, z = p
	ax = abs(x)
	# tail (the create_fox_anims tail weight region): rust, dark top, pale underside, white tip
	if y > 0.13 and (z > 0.17 or y > 0.21):
		if y > 0.432:
			return WHITE if y > 0.452 else WHITE_SHADE
		if n.z > 0.45:
			return RUST_DARK
		if n.z < -0.35:
			return ORANGE_PALE
		return RUST
	if is_head(y, z):
		return head(p, n)
	# legs: black stockings, a brown-black step above, rust upper leg with a paler inner side
	front = -0.33 < y < -0.12
	hind = -0.06 < y < 0.22
	if (front and z < 0.235 or hind and z < 0.23) and ax > 0.018:
		stock = 0.165 if front else 0.125
		if z < stock:
			return BLACK
		if z < stock + 0.025:
			return BLACK_BROWN
		inward = -n.x if x > 0 else n.x
		if inward > 0.45:
			return ORANGE_PALE
		return RUST
	# throat + chest bib: forward / down facing faces on the midline in front of the shoulders
	if y < -0.20 and z < 0.445 and ax < 0.052 and (-n.y > 0.40 or n.z < -0.35):
		return WHITE if z > 0.30 else WHITE_SHADE
	# trunk: bands by facet normal
	if n.z > 0.72:
		return RUST_DARK
	if n.z > 0.05:
		return RUST
	if n.z > -0.45:
		return ORANGE
	if -0.30 < y < 0.12 and ax < 0.06:
		return WHITE_SHADE                                     # belly, joined to the chest bib
	return ORANGE_PALE


VMAT = """// Red fox (Blender/scripts/create_fox_texture.py): 256 px point-filtered texture, faceted.
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelFlatShading "1.000"
	TextureColor "materials/entities/%s.png"
}
""" % NAME


def unwrap(mesh):
	me = mesh.data
	while me.uv_layers:
		me.uv_layers.remove(me.uv_layers[0])
	me.uv_layers.new(name="UVMap")
	bpy.ops.object.select_all(action="DESELECT")
	mesh.hide_set(False)
	mesh.select_set(True)
	bpy.context.view_layer.objects.active = mesh
	bpy.ops.object.mode_set(mode="EDIT")
	bpy.ops.mesh.select_all(action="SELECT")
	bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.012, area_weight=0.0, scale_to_bounds=False)
	bpy.ops.object.mode_set(mode="OBJECT")


def material(mesh):
	mat = bpy.data.materials.get(NAME) or bpy.data.materials.new(NAME)
	mat.use_nodes = True
	nt = mat.node_tree
	for nd in list(nt.nodes):
		if nd.type == "TEX_IMAGE":
			nt.nodes.remove(nd)
	tex = nt.nodes.new("ShaderNodeTexImage")
	tex.image = bpy.data.images.load(PNG, check_existing=True)
	tex.image.reload()
	tex.interpolation = "Closest"
	nt.links.new(tex.outputs["Color"], nt.nodes["Principled BSDF"].inputs["Base Color"])
	mesh.data.materials.clear()
	mesh.data.materials.append(mat)
	for m in list(bpy.data.materials):
		if m.users == 0:
			bpy.data.materials.remove(m)
	for im in list(bpy.data.images):
		if im.users == 0:
			bpy.data.images.remove(im)


def main(texture_only=False):
	bpy.ops.wm.open_mainfile(filepath=BLEND)
	mesh = bpy.data.objects[MESH]
	for o in bpy.data.objects:
		if o.type == "ARMATURE":
			o.data.pose_position = "REST"
	unwrap(mesh)
	os.makedirs(MAT_DIR, exist_ok=True)
	cd.write_png(PNG, cd.paint_texture([mesh], paint_fn=fox_paint, grain_sigma=0.0, fill=RUST, per_face=True))
	with open(os.path.join(MAT_DIR, NAME + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)
	material(mesh)
	for o in bpy.data.objects:
		if o.type == "ARMATURE":
			o.data.pose_position = "POSE"
	bpy.context.preferences.filepaths.save_version = 0
	bpy.ops.wm.save_as_mainfile(filepath=BLEND)
	print(f"[fox] wrote {PNG}, {len(mesh.data.polygons)} faces painted")
	if not texture_only:
		fa.main()


if __name__ == "__main__":
	argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
	main(texture_only=bool(argv) and argv[0] == "texture")
