"""Export a single-mesh .blend with a packed base-colour texture (Tripo-style downloads) into the game.

Run:  blender -b <file.blend> --python Blender/scripts/export_packed_blend.py -- <category> <model_name> <material_name> [fbx_scale] [texture_size]
  e.g. ... crystalformationpacked.blend ... -- environment environment_crystalFormation crystal_formation
       ... axepack1.blend ...               -- weapons axe_reforged axe_reforged 1.0 256
Or double-click / run Blender/scripts/export_packed_blend.bat with the same arguments (blend path first).
Writes Assets/models/<category>/<model_name>.fbx and Assets/materials/<category>/<material_name>.png.
The .vmdl / .vmat are hand-written next to them (see environment_crystalFormation.vmdl, weapons/axe_reforged.vmdl).

fbx_scale is the Blender->FBX global scale. The environment rocks were sized by eye at 0.5 (default).
Weapons export at 1.0 so 1 Blender metre = 100 FBX units, and the .vmdl's import_scale 0.4 lands at the
pawn factor of 40 units per metre (CLAUDE.md commandment 4) - a 1 m axe in Blender is a 1 m axe in game.
texture_size downscales the packed texture to that square size (Tripo ships 512; the weapons use 256).
Pair the .vmat with shaders/pixel_lit.shader - complex.shader samples bilinearly and blurs the texels.
"""
import bpy, os, sys

args = sys.argv[sys.argv.index("--") + 1:]
category, model_name, material_name = args[:3]
fbx_scale = float(args[3]) if len(args) > 3 else 0.5
texture_size = int(args[4]) if len(args) > 4 else 0
ROOT = r"M:\s&box\Projects\SurvivalGame2026\Assets"
FBX = os.path.join(ROOT, "models", category, model_name + ".fbx")
PNG = os.path.join(ROOT, "materials", category, material_name + ".png")
os.makedirs(os.path.dirname(PNG), exist_ok=True)

mesh = next(o for o in bpy.data.objects if o.type == 'MESH')
mesh.name = model_name
mesh.data.name = model_name
mat = mesh.data.materials[0]

# Unpacked colour texture -> png next to the other materials of this category.
# Image.save() on a packed image writes the packed bytes unchanged (a Tripo .jpg would land
# under a .png name and fail the engine's PNG loader), so copy the pixels into a fresh image.
img = next(i for i in bpy.data.images if i.packed_file is not None)
png = bpy.data.images.new(material_name + "_png", img.size[0], img.size[1], alpha=False)
png.pixels = img.pixels[:]
if texture_size and (png.size[0] != texture_size or png.size[1] != texture_size):
    png.scale(texture_size, texture_size)
    print("TEXTURE scaled", tuple(img.size), "->", texture_size)
png.filepath_raw = PNG
png.file_format = 'PNG'
png.save()

# A Mapping node between UV Map and the texture only exists in the Blender shader;
# bake its offset/scale into the UVs so the engine samples the same texels.
mapping = next((n for n in mat.node_tree.nodes if n.type == 'MAPPING'), None)
if mapping:
    loc = mapping.inputs['Location'].default_value
    scl = mapping.inputs['Scale'].default_value
    rot = mapping.inputs['Rotation'].default_value
    assert abs(rot[2]) < 1e-5, "UV rotation in Mapping node not handled"
    for d in mesh.data.uv_layers.active.data:
        d.uv = (d.uv[0] * scl[0] + loc[0], d.uv[1] * scl[1] + loc[1])
    print("MAPPING baked", tuple(loc), tuple(scl))

# Bake the object rotation/scale into the mesh so the fbx carries plain geometry,
# and drop the camera / light so the fbx holds only the model.
for o in list(bpy.data.objects):
    if o != mesh:
        bpy.data.objects.remove(o, do_unlink=True)
bpy.context.view_layer.objects.active = mesh
mesh.select_set(True)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
mat.name = material_name

bpy.ops.export_scene.fbx(filepath=FBX, use_selection=True, global_scale=fbx_scale,
                         apply_unit_scale=True, object_types={'MESH'},
                         mesh_smooth_type='FACE', path_mode='STRIP', embed_textures=False)
print("EXPORTED", FBX, tuple(round(v, 2) for v in mesh.dimensions), "fbx_scale", fbx_scale)
