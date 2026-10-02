"""Import an artist pack (one Tripo-style .blend: a single mesh, a packed colour texture and a
Pixelate / Posterize node chain) into the game as model + material.

Run:
  blender -b <pack.blend> --python Blender/scripts/import_artist_pack.py -- <category> <name> [key=value ...]
  or Blender/scripts/import_artist_pack.bat <pack.blend> <category> <name> [key=value ...]

  category   weapons | building | environment  (picks the asset folders, the vmdl scale and the frame rules)
  name       asset name: Assets/models/<category>/<name>.fbx + .vmdl, Assets/materials/<category>/<name>.png + .vmat

  texture=N      bake size in texels (default: the Pixelate group's "Pixels" input, else the source image size)
  head=+x|-x|+y|-y|+z|-z   weapons only, REQUIRED: which way the head / blade tip points in the .blend
  length=M       weapons: butt-to-tip length in metres (default: as authored)
  scale=F        uniform scale on the authored metres (default 1)
  physics=hull|mesh|none   vmdl collision (default: weapons hull, building none, environment mesh)
  chain=full|raw  full (default) runs the artist's colour nodes (Posterize etc.); raw skips them and
                  uses the source texture as painted, only downsampled to the pixel grid. Use raw
                  when the Posterize steps destroy the texture (the sword's brown grip became
                  black / red bands at 8 steps; the chest and table were fine).

What it does
  1. Applies the artist's object rotation / scale (Rumple straightens Tripo meshes with the object
     rotation, so it is part of the model), drops cameras and lights, renames mesh + material to <name>.
  2. Bakes the material's Base Color through the WHOLE node chain (Pixelate, Posterize, Hue/Sat,
     Brightness/Contrast, whatever the artist added) with Cycles into one texels-for-texels PNG.
     The engine then shows exactly what Blender's material preview shows; pixel_lit.shader samples
     it point-filtered so the texels stay hard at any distance.
  3. Puts the mesh in the frame the game expects for the category:
       weapons      straightened so the handle runs along +X with the head at +X, the butt of the
                    handle at the origin (x = 0). Code/Player/HeldModelFit.cs reads "origin at one
                    end" as "that end is the butt", which is what makes a sword hold hilt-down
                    (its cross-section heuristic alone would grab a sword by the blade: the
                    cross-guard is the wider end).
       building     bounding box centred on the origin, width on X, depth on Y, height on Z - the
                    same frame as the wood kit, so Code/Building/BuildModuleDimensions.SizesMeters
                    takes the printed (X, Y, Z) metres and the ground-sit / box collider line up.
       environment  centred on X / Y, standing on z = 0.
  4. Exports the FBX (Blender metres x 100) and writes the .vmdl beside it: import_scale 0.4 for
     weapons / environment (40 units per metre, CLAUDE.md commandment 4) and 0.5 for building
     (the build kit's 50 units per metre), material remap to the baked .vmat.

Prints the authored size in metres and, for building, the SizesMeters line to paste.
"""
import bpy, os, sys
import numpy as np
from mathutils import Matrix, Vector

ROOT = r"M:\s&box\Projects\SurvivalGame2026\Assets"
IMPORT_SCALE = {"weapons": 0.4, "environment": 0.4, "building": 0.5}
DEFAULT_PHYSICS = {"weapons": "hull", "building": "none", "environment": "mesh"}
AXES = {"+x": (1, 0, 0), "-x": (-1, 0, 0), "+y": (0, 1, 0), "-y": (0, -1, 0), "+z": (0, 0, 1), "-z": (0, 0, -1)}

args = sys.argv[sys.argv.index("--") + 1:]
if len(args) < 2:
    raise SystemExit(__doc__)
category, name = args[0].lower(), args[1]
opts = dict(a.split("=", 1) for a in args[2:])
if category not in IMPORT_SCALE:
    raise SystemExit(f"category must be one of {list(IMPORT_SCALE)}, got {category!r}")
if category == "weapons" and opts.get("head", "").lower() not in AXES:
    raise SystemExit("weapons need head=+x|-x|+y|-y|+z|-z (which way the blade tip / head points in the blend)")
physics = opts.get("physics", DEFAULT_PHYSICS[category]).lower()
if physics not in ("hull", "mesh", "none"):
    raise SystemExit("physics must be hull, mesh or none")

MODEL_DIR = os.path.join(ROOT, "models", category)
MAT_DIR = os.path.join(ROOT, "materials", category)
os.makedirs(MODEL_DIR, exist_ok=True)
os.makedirs(MAT_DIR, exist_ok=True)
FBX = os.path.join(MODEL_DIR, name + ".fbx")
VMDL = os.path.join(MODEL_DIR, name + ".vmdl")
PNG = os.path.join(MAT_DIR, name + ".png")
VMAT = os.path.join(MAT_DIR, name + ".vmat")

# ---------------------------------------------------------------- 1. the mesh
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
if not meshes:
    raise SystemExit("no mesh object in the blend")
for o in list(bpy.data.objects):
    if o.type != 'MESH':
        bpy.data.objects.remove(o, do_unlink=True)
bpy.ops.object.select_all(action='DESELECT')
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
mesh = bpy.context.view_layer.objects.active
mesh.name = name
mesh.data.name = name
# The artist's object transform is part of the model (Rumple straightens Tripo output with it).
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
mat = mesh.data.materials[0]
mat.name = name
assert mat.use_nodes, "material has no node tree"
nodes = mat.node_tree.nodes

# ---------------------------------------------------------------- 2. bake the colour chain
# Evaluated texel for texel in numpy rather than with a Cycles bake: Cycles in this Blender build
# returns a flat colour for the packed Tripo texture (verified 2026-10-02 with EMIT and DIFFUSE
# bakes, packed / disk / in-memory copies), and a bake would also need UV margins and samples.
# The artist's chain is a UV snap (Pixelate) followed by per-colour nodes, so the baked texture is
# exactly: box-filter the source down to the pixel grid, then run every colour node on each texel.
principled = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
roughness = principled.inputs['Roughness']
roughness_value = float(roughness.default_value) if not roughness.is_linked else 0.9
pixelate = next((n for n in nodes if n.type == 'GROUP' and n.node_tree and 'Pixels' in n.inputs), None)
tex_node = next((n for n in nodes if n.type == 'TEX_IMAGE' and n.image), None)
if tex_node is None:
    raise SystemExit("material has no Image Texture node")
src_img = tex_node.image
W, H = src_img.size
if "texture" in opts:
    texture_size = int(opts["texture"])
elif pixelate is not None:
    texture_size = int(pixelate.inputs['Pixels'].default_value)
else:
    texture_size = W
texture_size = min(texture_size, W, H)


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


# Image.pixels of a byte image are the stored (sRGB-encoded) values; shader nodes see them linear.
src = np.array(src_img.pixels[:], dtype=np.float64).reshape(H, W, 4)[:, :, :3]
lin = srgb_to_linear(src)
N = texture_size
if W % N == 0 and H % N == 0:
    lin = lin.reshape(N, H // N, N, W // N, 3).mean(axis=(1, 3))
else:
    ys = (np.arange(N) + 0.5) * H / N
    xs = (np.arange(N) + 0.5) * W / N
    lin = lin[ys.astype(int)][:, xs.astype(int)]


def colour_chain():
    """Nodes between the Image Texture and the Principled Base Color, in evaluation order."""
    chain = []
    link = principled.inputs['Base Color'].links[0] if principled.inputs['Base Color'].is_linked else None
    while link is not None and link.from_node != tex_node:
        node = link.from_node
        chain.append(node)
        colour_in = next((i for i in node.inputs if i.is_linked and i.type in ('RGBA', 'VECTOR')), None)
        link = colour_in.links[0] if colour_in is not None else None
    if link is None:
        raise SystemExit("could not trace the Base Color chain back to the Image Texture node")
    chain.reverse()
    return chain


def unlinked(node, name):
    sock = node.inputs[name]
    if sock.is_linked:
        raise SystemExit(f"{node.name}.{name} is driven by another node - not supported, extend import_artist_pack.py")
    return float(sock.default_value)


def apply_node(node, c):
    if node.type == 'GROUP' and 'Steps' in node.inputs:  # Posterize
        steps = unlinked(node, 'Steps')
        return np.floor(c * steps) / steps
    if node.type == 'HUE_SAT':
        hue, sat, val, fac = (unlinked(node, k) for k in ('Hue', 'Saturation', 'Value', 'Fac'))
        import colorsys
        flat = c.reshape(-1, 3)
        out = np.empty_like(flat)
        for i, (r, g, b) in enumerate(flat):
            h, s, v = colorsys.rgb_to_hsv(r, g, b)
            h = (h + hue + 0.5) % 1.0
            s = min(max(s * sat, 0.0), 1.0)
            v = v * val
            out[i] = colorsys.hsv_to_rgb(h, s, v)
        out = np.maximum(out, 0.0)
        return (fac * out + (1.0 - fac) * flat).reshape(c.shape)
    if node.type == 'BRIGHTCONTRAST':
        bright, contrast = unlinked(node, 'Bright'), unlinked(node, 'Contrast')
        a = 1.0 + contrast
        b = bright - contrast * 0.5
        return np.maximum(a * c + b, 0.0)
    if node.type == 'GAMMA':
        return np.power(np.maximum(c, 0.0), unlinked(node, 'Gamma'))
    if node.type in ('REROUTE',):
        return c
    raise SystemExit(f"unsupported node {node.type} '{node.name}' in the colour chain - extend import_artist_pack.py")


chain_mode = opts.get("chain", "full").lower()
if chain_mode not in ("full", "raw"):
    raise SystemExit("chain must be full or raw")
chain = colour_chain() if chain_mode == "full" else []
for node in chain:
    lin = apply_node(node, lin)
out = np.concatenate([linear_to_srgb(lin), np.ones((N, N, 1))], axis=2)
baked = bpy.data.images.new(name + "_baked", N, N, alpha=False)
baked.pixels = out.ravel().tolist()
baked.filepath_raw = PNG
baked.file_format = 'PNG'
baked.save()
print(f"BAKED {PNG} {N}x{N} from {W}x{H} (chain {chain_mode}: {[n.name for n in chain]}, pixelate {pixelate.inputs['Pixels'].default_value if pixelate else 'none'})")

# ---------------------------------------------------------------- 3. frame for the category
def verts():
    return np.array([list(v.co) for v in mesh.data.vertices])

def bbox():
    p = verts()
    return p.min(0), p.max(0)

scale = float(opts.get("scale", 1.0))

if category == "weapons":
    p = verts()
    c = p.mean(0)
    w, v = np.linalg.eigh(np.cov((p - c).T))
    order = np.argsort(w)[::-1]
    long_axis, second = v[:, order[0]], v[:, order[1]]
    head = np.array(AXES[opts["head"].lower()], dtype=float)
    if np.dot(long_axis, head) < 0:
        long_axis = -long_axis
    x_axis = long_axis / np.linalg.norm(long_axis)
    z_axis = second - x_axis * np.dot(second, x_axis)
    z_axis /= np.linalg.norm(z_axis)
    y_axis = np.cross(z_axis, x_axis)
    rot = Matrix([[*map(float, x_axis)], [*map(float, y_axis)], [*map(float, z_axis)]])  # rows = new axes
    mesh.data.transform(rot.to_4x4())
    mn, mx = bbox()
    if "length" in opts:
        scale *= float(opts["length"]) / float(mx[0] - mn[0])
    mesh.data.transform(Matrix.Scale(scale, 4))
    mn, mx = bbox()
    centre = (mn + mx) * 0.5
    mesh.data.transform(Matrix.Translation(Vector((float(-mn[0]), float(-centre[1]), float(-centre[2])))))
elif category == "building":
    mesh.data.transform(Matrix.Scale(scale, 4))
    mn, mx = bbox()
    centre = (mn + mx) * 0.5
    mesh.data.transform(Matrix.Translation(Vector([float(-c) for c in centre])))
else:  # environment
    mesh.data.transform(Matrix.Scale(scale, 4))
    mn, mx = bbox()
    centre = (mn + mx) * 0.5
    mesh.data.transform(Matrix.Translation(Vector((float(-centre[0]), float(-centre[1]), float(-mn[2])))))

mesh.data.update()
mn, mx = bbox()
size = mx - mn

# ---------------------------------------------------------------- 4. fbx + vmdl + vmat
bpy.ops.object.select_all(action='DESELECT')
mesh.select_set(True)
bpy.ops.export_scene.fbx(filepath=FBX, use_selection=True, global_scale=1.0, apply_unit_scale=True,
                         object_types={'MESH'}, mesh_smooth_type='FACE', path_mode='STRIP',
                         embed_textures=False, axis_forward='-Z', axis_up='Y')

rel_fbx = f"models/{category}/{name}.fbx"
rel_vmat = f"materials/{category}/{name}.vmat"
rel_png = f"materials/{category}/{name}.png"
PHYSICS_BLOCKS = {
    "hull": """			{
				_class = "PhysicsShapeList"
				children =
				[
					{
						_class = "PhysicsHullFromRender"
						parent_bone = ""
						surface_prop = "default"
						collision_tags = "solid"
						faceMergeAngle = 20.0
						maxHullVertices = 32
						hull_mode = "HullPerElement"
					},
				]
			},
""",
    "mesh": """			{
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
""",
    "none": "",
}
physics_block = PHYSICS_BLOCKS[physics]

vmdl_text = f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps =
						[
							{{
								from = "{name}.vmat"
								to = "{rel_vmat}"
							}},
						]
						use_global_default = false
						global_default_material = "{rel_vmat}"
					}},
				]
			}},
{physics_block}			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						filename = "{rel_fbx}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = {IMPORT_SCALE[category]}
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = false
							exception_list = [  ]
						}}
					}},
				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""
with open(VMDL, "w", encoding="utf-8", newline="\n") as f:
    f.write(vmdl_text)

vmat_text = f"""Layer0
{{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "{roughness_value:.3f}"
	TextureColor "{rel_png}"
}}
"""
with open(VMAT, "w", encoding="utf-8", newline="\n") as f:
    f.write(vmat_text)

print(f"EXPORTED {FBX}")
print(f"WROTE {VMDL} (import_scale {IMPORT_SCALE[category]}, physics {physics}) and {VMAT} (PixelRoughness {roughness_value:.2f})")
print(f"SIZE metres x={size[0]:.3f} y={size[1]:.3f} z={size[2]:.3f}  bbox min {tuple(round(float(v), 3) for v in mn)} max {tuple(round(float(v), 3) for v in mx)}")
if category == "building":
    print(f'SizesMeters line:  ["{name}"] = new( {size[0]:.2f}f, {size[1]:.2f}f, {size[2]:.2f}f ),')
