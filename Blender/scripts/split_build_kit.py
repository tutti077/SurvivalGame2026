"""
Split the wood build kit into one .blend and one .fbx per piece.

Run in Blender Scripting workspace, or:
  blender --background --python split_build_kit.py

Each piece is rebuilt from create_build_kit.py into an otherwise empty scene, sitting on the
world origin with no object transform, then written twice:

  Blender/blenderprojects/buildKit/<pieceId>.blend    one object, ready to edit
  Assets/models/building/<pieceId>.fbx                 ready for a .vmdl

Rebuilding rather than copying out of buildKit.blend is deliberate: the combined file spreads
the kit out on a grid using object locations, and FBX bakes the object transform, so anything
lifted out of it would export off-centre.

CENTRING: every piece is authored around the centre of its declared size box, which is what
the symmetric BoxCollider on the prefab expects, and the script prints the mesh bounding box
centre for each one so you can see it is on the origin. The two folded roof corners are the
exception — they are centred on their 2 m module cube (the reference that makes roof seams
line up) and their thickened underside hangs a couple of cm past it, so their bounding box
centre reads slightly low. Do not "fix" that by re-centring on the bounds; it would drop the
roof surface below the panel it mates.
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
WRITE_BLENDS = True
WRITE_FBX = True
ONLY_PIECES = []          # e.g. [ "build_wood_stairs" ] to redo a single piece; [] does all
CENTRE_TOLERANCE = 0.001  # m; anything further off origin than this gets called out


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


def split_build_kit():
	definitions = kit.piece_definitions()
	if ONLY_PIECES:
		wanted = { name.lower() for name in ONLY_PIECES }
		definitions = [ entry for entry in definitions if entry[0].lower() in wanted ]

	if WRITE_BLENDS:
		os.makedirs( BLEND_DIR, exist_ok=True )

	written = []
	for name, _collection_name, geometry, folded in definitions:
		_clear_scene()

		obj = kit.build_piece( name, geometry, bpy.context.scene.collection, ( 0.0, 0.0, 0.0 ), folded )
		kit.report_piece( obj )
		print( f"[split_kit] {name}: {_describe_centring( obj )}" )

		if WRITE_FBX:
			kit.export_piece_fbx( obj, FBX_DIR )

		if WRITE_BLENDS:
			blend_path = os.path.join( BLEND_DIR, f"{name}.blend" )
			bpy.ops.wm.save_as_mainfile( filepath=blend_path, copy=True )
			print( f"[split_kit] saved {blend_path}" )

		written.append( name )

	print( f"[split_kit] {len( written )} pieces written" )
	if WRITE_BLENDS:
		print( f"[split_kit] blends in {BLEND_DIR}" )
	if WRITE_FBX:
		print( f"[split_kit] fbx in {FBX_DIR}" )
	return written


if __name__ == "__main__":
	split_build_kit()
