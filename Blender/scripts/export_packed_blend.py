"""Export a single-mesh .blend with a packed base-colour texture (Tripo-style downloads) into the game.

Run:  blender -b <file.blend> --python Blender/scripts/export_packed_blend.py -- <model_name> <material_name>
  e.g. ... redwoodbasepacked.blend ...        -- environment_redwoodBase redwood_base
       ... crystalformationpacked.blend ...   -- environment_crystalFormation crystal_formation
Writes Assets/models/environment/<model_name>.fbx and Assets/materials/environment/<material_name>.png.
The .vmdl / .vmat are hand-written next to them (see environment_redwoodBase.vmdl).
"""
import bpy, os, sys

model_name, material_name = sys.argv[sys.argv.index("--") + 1:][:2]
ROOT = r"M:\s&box\Projects\SurvivalGame2026\Assets"
FBX = os.path.join(ROOT, "models", "environment", model_name + ".fbx")
PNG = os.path.join(ROOT, "materials", "environment", material_name + ".png")

mesh = next(o for o in bpy.data.objects if o.type == 'MESH')
mesh.name = model_name
mat = mesh.data.materials[0]

# Unpacked colour texture -> png next to the other environment materials.
img = next(i for i in bpy.data.images if i.packed_file is not None)
img.filepath_raw = PNG
img.file_format = 'PNG'
img.save()

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

# Bake the object rotation/scale into the mesh so the fbx carries plain geometry.
for o in list(bpy.data.objects):
    if o != mesh:
        bpy.data.objects.remove(o, do_unlink=True)
bpy.context.view_layer.objects.active = mesh
mesh.select_set(True)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
mat.name = material_name

bpy.ops.export_scene.fbx(filepath=FBX, use_selection=True, global_scale=0.5,
                         apply_unit_scale=True, object_types={'MESH'},
                         mesh_smooth_type='FACE', path_mode='STRIP', embed_textures=False)
print("EXPORTED", FBX, tuple(round(v, 2) for v in mesh.dimensions))
