"""
Split the wood build kit into one .blend, one .fbx and one .vmdl per piece, plus the two kit
textures.

Run in Blender Scripting workspace, or:
  blender --background --python split_build_kit.py

Each piece is rebuilt from create_build_kit.py into an otherwise empty scene, sitting on the
world origin with no object transform, then written as:

  Blender/blenderprojects/buildKit/<pieceId>.blend    LOD0 + LOD1 objects, ready to edit
  Assets/models/building/<pieceId>.fbx                 both LODs (<pieceId>, <pieceId>_lod1)
  Assets/models/building/<pieceid>.vmdl                lowercase, as BuildPieceVisual loads it
  Assets/materials/building/build_wood_{plank,beam}.{png,vmat}   128 texels / m, pixel_lit

The vmdl imports at 0.4 (Blender metres export as 100 u/m; the kit is 40 u/m), switches to LOD1
at 100 m (switch_threshold 400), and builds its physics from the LOD0 mesh only — stairs, roofs,
the door frame and the triangles collide with that mesh (BuildPieceVisual.UsesMeshCollision);
every other piece is boxed by BuildPieceCollider and ignores it.

Rebuilding rather than copying out of buildKit.blend is deliberate: the combined file spreads
the kit out on a grid using object locations, and FBX bakes the object transform.

CENTRING: every piece is authored around the centre of its declared size box. The roof panel
and the two thatched roof corners sit on their 2 m module cube instead (the straw and purlins
hang under the roof surface and the pitched slab reaches past the cube), so their bounding box
centre reads off origin — do not "fix" that.
"""

import os
import sys

import bpy

_SCRIPT_DIR = os.path.dirname( os.path.abspath( __file__ ) )
if _SCRIPT_DIR not in sys.path:
	sys.path.insert( 0, _SCRIPT_DIR )

import create_build_kit as kit

_REPO_ROOT = os.path.dirname( os.path.dirname( _SCRIPT_DIR ) )

# --- tweak these ---
BLEND_DIR = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "buildKit" )
FBX_DIR = os.path.join( _REPO_ROOT, "Assets", "models", "building" )
MATERIAL_DIR = os.path.join( _REPO_ROOT, "Assets", "materials", "building" )
WRITE_BLENDS = True
WRITE_FBX = True
WRITE_VMDL = True
WRITE_TEXTURES = True
ONLY_PIECES = []          # e.g. [ "build_wood_stairs" ] to redo a single piece; [] does all
CENTRE_TOLERANCE = 0.001  # m; anything further off origin than this gets called out
IMPORT_SCALE = 0.4
LOD1_SWITCH = 400.0       # 100 m at 4 screen-size units per metre, the project's LOD1 distance


def _clear_scene() -> None:
	"""Empty the file down to nothing so each piece is written on its own."""
	for obj in list( bpy.data.objects ):
		bpy.data.objects.remove( obj, do_unlink=True )

	for mesh in list( bpy.data.meshes ):
		if mesh.users == 0:
			bpy.data.meshes.remove( mesh )

	for collection in list( bpy.data.collections ):
		bpy.data.collections.remove( collection )


def _describe_centring( obj: bpy.types.Object ) -> str:
	center = kit.bounds_center( obj )
	worst = max( abs( value ) for value in center )
	if worst <= CENTRE_TOLERANCE:
		return "centred on origin"

	return (
		f"bounds centre {center[0]:+.4f}, {center[1]:+.4f}, {center[2]:+.4f} "
		f"(module-cube origin, see the note at the top of this script)" )


def _render_mesh( mesh_name: str, fbx: str ) -> str:
	return f"""					{{
						_class = "RenderMeshFile"
						name = "{mesh_name}"
						filename = "{fbx}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = {IMPORT_SCALE}
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{mesh_name}" ]
						}}
					}},
"""


def _remaps() -> str:
	lines = []
	for material in kit.MATERIALS:
		target = f"materials/building/{material}.vmat"
		for source in ( material, f"{material}.vmat" ):
			lines.append(
				"\t\t\t\t\t\t\t{\n"
				f"\t\t\t\t\t\t\t\tfrom = \"{source}\"\n"
				f"\t\t\t\t\t\t\t\tto = \"{target}\"\n"
				"\t\t\t\t\t\t\t},\n" )
	return "".join( lines )


def vmdl_text( name: str ) -> str:
	fbx = f"models/building/{name}.fbx"
	low = name + kit.LOD1_SUFFIX
	return f"""<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
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
{_remaps()}						]
						use_global_default = false
						global_default_material = "materials/building/{kit.MATERIALS[kit.PLANK]}.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsMeshFile"
						name = "{name}_physics"
						parent_bone = ""
						surface_prop = "wood"
						collision_tags = "solid"
						recenter_on_parent_bone = false
						offset_origin = [ 0.0, 0.0, 0.0 ]
						offset_angles = [ 0.0, 0.0, 0.0 ]
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						filename = "{fbx}"
						import_scale = {IMPORT_SCALE}
						maxMeshVertices = 0
						qemError = 0.0
						import_filter =
						{{
							exclude_by_default = true
							exception_list = [ "{name}" ]
						}}
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
{_render_mesh( name, fbx )}{_render_mesh( low, fbx )}				]
			}},
			{{
				_class = "LODGroupList"
				children =
				[
					{{
						_class = "LODGroup"
						switch_threshold = 0.0
						meshes = [ "{name}" ]
					}},
					{{
						_class = "LODGroup"
						switch_threshold = {LOD1_SWITCH}
						meshes = [ "{low}" ]
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


def split_build_kit():
	if WRITE_TEXTURES:
		kit.write_textures( MATERIAL_DIR )

	definitions = kit.piece_definitions()
	if ONLY_PIECES:
		wanted = { name.lower() for name in ONLY_PIECES }
		definitions = [ entry for entry in definitions if entry[0].lower() in wanted ]

	if WRITE_BLENDS:
		os.makedirs( BLEND_DIR, exist_ok=True )

	written = []
	mismatches = 0
	for name, _collection_name, lod0, lod1 in definitions:
		_clear_scene()

		obj, low = kit.build_piece( name, lod0, lod1, bpy.context.scene.collection, ( 0.0, 0.0, 0.0 ) )
		low.location = ( 0.0, 0.0, 0.0 )
		if not kit.report_piece( obj, low ):
			mismatches += 1
		print( f"[split_kit] {name}: {_describe_centring( obj )}" )

		if WRITE_FBX:
			kit.export_piece_fbx( [ obj, low ], name, FBX_DIR )

		if WRITE_VMDL:
			vmdl_path = os.path.join( FBX_DIR, f"{name.lower()}.vmdl" )
			with open( vmdl_path, "w", newline="\n" ) as handle:
				handle.write( vmdl_text( name ) )
			print( f"[split_kit] wrote {vmdl_path}" )

		if WRITE_BLENDS:
			blend_path = os.path.join( BLEND_DIR, f"{name}.blend" )
			bpy.ops.wm.save_as_mainfile( filepath=blend_path, copy=True )
			print( f"[split_kit] saved {blend_path}" )

		written.append( name )

	print( f"[split_kit] {len( written )} pieces written, {mismatches} size mismatch(es)" )
	return written


if __name__ == "__main__":
	split_build_kit()
