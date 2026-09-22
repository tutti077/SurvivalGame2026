"""
Grass clump generator — flat, single-plane blade fans for the engine Clutter system.

Run in Blender Scripting workspace, or headless:
  blender --background --python "Blender/scripts/create_grass_clump.py"

What it builds
--------------
Four clump variants (grass_clump1..4: 7, 5, 9 and 3 blades). Every clump is a fan of tapered, flat-shaded blades
that all lie in ONE plane (local X-Z, normal ±Y). Looked at edge-on the clump vanishes;
rotate around it and the blades appear, mirrored from behind. This is the Valheim-style
"card" grass, but built from real triangles instead of an alpha texture, so it stays clean
under MSAA and matches the low-poly direction.

Blades are stacked 3 mm apart in Y (an invisible offset) purely so coplanar blades can
never z-fight where they overlap.

Each blade = 5 vertices / 3 triangles: a base pair, a mid pair and a single tip vertex.

Vertex colours (exported LINEAR, read raw by shaders/grass_blade.shader)
------------------------------------------------------------------------
  R = height along the blade, 0 at the root → 1 at the tip. The shader bends by R^BendPower,
      so roots never move and tips carry the sway.
  G = per-blade random phase (0..1) so blades in a clump do not flap in unison.
  B = per-clump random (0..1), used for a little colour variation between clumps.
  A = 1

UV: u across the blade, v = the same normalised height (backup for R).

Units & export
--------------
Modelled in metres (Blender units), origin at the clump root on Z = 0. FBX is exported with
the same settings as the build kit (apply_unit_scale, global_scale 1); the .vmdl then uses
import_scale 0.4 to land on the terrain's 40 u/m.

Outputs
-------
  Assets/models/environment/grass/grass_clump<N>.fbx   (one per variant, origin at 0,0,0)
  Blender/blenderprojects/grass_clumps.blend           (all variants on a row for eyeballing)
"""

import os
import random

import bpy

_REPO_ROOT = os.path.abspath( os.path.join( os.path.dirname( os.path.abspath( __file__ ) ), "..", ".." ) )
FBX_DIR = os.path.join( _REPO_ROOT, "Assets", "models", "environment", "grass" )
BLEND_PATH = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "grass_clumps.blend" )

WRITE_FBX = True
WRITE_BLEND = True
LAYOUT_SPACING = 1.0      # metres between variants in the saved .blend (object location only)

MATERIAL_NAME = "grass_blade"
ROOT_SINK = 0.02          # metres the blade roots sit below Z=0 so uneven ground never shows a gap
BLADE_Y_STEP = 0.003      # metres between stacked blades — keeps coplanar blades from z-fighting
MID_ROW_T = 0.55          # where along the blade the middle vertex pair sits (0..1)

# One dict per variant. Metres.
VARIANTS = [
	{
		"name": "grass_clump1",
		"seed": 11,
		"blades": 7,
		"height": ( 0.42, 0.60 ),      # per-blade height range
		"base_spread": 0.22,           # width of the root line the blades grow from
		"lean": ( 0.10, 0.28 ),        # how far a tip leans sideways (outward from the clump centre)
		"base_width": 0.045,
		"mid_width": 0.030,
	},
	{
		"name": "grass_clump2",
		"seed": 23,
		"blades": 5,
		"height": ( 0.30, 0.46 ),
		"base_spread": 0.16,
		"lean": ( 0.06, 0.20 ),
		"base_width": 0.040,
		"mid_width": 0.026,
	},
	{
		"name": "grass_clump3",
		"seed": 37,
		"blades": 9,
		"height": ( 0.50, 0.74 ),
		"base_spread": 0.28,
		"lean": ( 0.12, 0.34 ),
		"base_width": 0.048,
		"mid_width": 0.032,
	},
	{
		"name": "grass_clump4",
		"seed": 51,
		"blades": 3,
		"height": ( 0.36, 0.52 ),
		"base_spread": 0.10,
		"lean": ( 0.08, 0.22 ),
		"base_width": 0.042,
		"mid_width": 0.028,
	},
]


def _reset_scene() -> None:
	bpy.ops.wm.read_factory_settings( use_empty=True )
	scene = bpy.context.scene
	scene.unit_settings.system = "METRIC"
	scene.unit_settings.scale_length = 1.0


def _get_material() -> bpy.types.Material:
	mat = bpy.data.materials.get( MATERIAL_NAME )
	if mat is None:
		mat = bpy.data.materials.new( MATERIAL_NAME )
		mat.diffuse_color = ( 0.25, 0.5, 0.12, 1.0 )
	return mat


def _blade_geometry( rng: random.Random, spec: dict, blade_index: int, blade_count: int ):
	"""Returns (verts, faces, heights01) for one blade, in metres, root on Z=-ROOT_SINK."""
	height = rng.uniform( *spec["height"] )
	spread = spec["base_spread"]

	# Root position along the fan line. Evenly spaced with a little jitter so the fan reads as
	# one clump, not a picket fence.
	if blade_count > 1:
		t = blade_index / ( blade_count - 1 )
	else:
		t = 0.5
	base_x = ( t - 0.5 ) * spread + rng.uniform( -0.02, 0.02 )

	# Outer blades lean outward, centre blades pick a side at random.
	side = 1.0 if base_x > 0.01 else ( -1.0 if base_x < -0.01 else rng.choice( ( -1.0, 1.0 ) ) )
	lean = rng.uniform( *spec["lean"] ) * side

	y = ( blade_index - ( blade_count - 1 ) * 0.5 ) * BLADE_Y_STEP

	bw = spec["base_width"] * rng.uniform( 0.85, 1.15 )
	mw = spec["mid_width"] * rng.uniform( 0.85, 1.15 )

	z_mid = height * MID_ROW_T
	x_mid = base_x + lean * ( MID_ROW_T ** 2 )     # quadratic lean = gentle arch
	x_tip = base_x + lean

	verts = [
		( base_x - bw * 0.5, y, -ROOT_SINK ),
		( base_x + bw * 0.5, y, -ROOT_SINK ),
		( x_mid - mw * 0.5, y, z_mid ),
		( x_mid + mw * 0.5, y, z_mid ),
		( x_tip, y, height ),
	]
	faces = [ ( 0, 1, 3, 2 ), ( 2, 3, 4 ) ]
	heights01 = [ 0.0, 0.0, MID_ROW_T, MID_ROW_T, 1.0 ]
	return verts, faces, heights01


def create_clump( spec: dict ) -> bpy.types.Object:
	rng = random.Random( spec["seed"] )
	blade_count = int( spec["blades"] )
	clump_random = rng.random()

	verts = []
	faces = []
	colors = []     # per vertex (r, g, b, a)
	uvs = []        # per vertex (u, v)

	for b in range( blade_count ):
		bverts, bfaces, heights01 = _blade_geometry( rng, spec, b, blade_count )
		phase = rng.random()
		offset = len( verts )
		verts.extend( bverts )
		faces.extend( tuple( i + offset for i in f ) for f in bfaces )
		for k, h in enumerate( heights01 ):
			colors.append( ( h, phase, clump_random, 1.0 ) )
			u = 0.0 if k in ( 0, 2 ) else ( 1.0 if k in ( 1, 3 ) else 0.5 )
			uvs.append( ( u, h ) )

	mesh = bpy.data.meshes.new( spec["name"] )
	mesh.from_pydata( verts, [], faces )
	mesh.update()

	# Flat shading — every blade is a card.
	for poly in mesh.polygons:
		poly.use_smooth = False

	uv_layer = mesh.uv_layers.new( name="UVMap" )
	for loop in mesh.loops:
		uv_layer.data[loop.index].uv = uvs[loop.vertex_index]

	color_attr = mesh.color_attributes.new( name="Color", type="FLOAT_COLOR", domain="CORNER" )
	for loop in mesh.loops:
		color_attr.data[loop.index].color = colors[loop.vertex_index]

	mesh.materials.append( _get_material() )

	obj = bpy.data.objects.new( spec["name"], mesh )
	bpy.context.scene.collection.objects.link( obj )
	return obj


def _activate( obj: bpy.types.Object ) -> None:
	bpy.ops.object.select_all( action="DESELECT" )
	obj.select_set( True )
	bpy.context.view_layer.objects.active = obj


def export_fbx( obj: bpy.types.Object, directory: str ) -> str:
	os.makedirs( directory, exist_ok=True )
	path = os.path.join( directory, f"{obj.name}.fbx" )

	location = tuple( obj.location )
	obj.location = ( 0.0, 0.0, 0.0 )

	_activate( obj )
	bpy.ops.export_scene.fbx(
		filepath=path,
		use_selection=True,
		object_types={ "MESH" },
		apply_unit_scale=True,
		global_scale=1.0,
		axis_forward="-Z",
		axis_up="Y",
		mesh_smooth_type="FACE",
		colors_type="LINEAR",
		use_mesh_modifiers=True,
		add_leaf_bones=False,
		bake_anim=False,
	)

	obj.location = location
	print( f"[grass] exported {path}" )
	return path


def main() -> None:
	_reset_scene()

	objects = []
	for index, spec in enumerate( VARIANTS ):
		obj = create_clump( spec )
		obj.location = ( index * LAYOUT_SPACING, 0.0, 0.0 )
		objects.append( obj )

		mesh = obj.data
		zs = [ v.co.z for v in mesh.vertices ]
		xs = [ v.co.x for v in mesh.vertices ]
		print( f"[grass] {spec['name']}: {len( mesh.vertices )} verts, {len( mesh.polygons )} faces, "
			   f"height {max( zs ):.2f} m, width {max( xs ) - min( xs ):.2f} m" )

	if WRITE_FBX:
		for obj in objects:
			export_fbx( obj, FBX_DIR )

	if WRITE_BLEND:
		os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
		bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
		print( f"[grass] saved {BLEND_PATH}" )


if __name__ == "__main__":
	main()
