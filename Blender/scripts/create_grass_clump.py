"""
Grass tuft generator (v2) - chunky low-poly tufts for the engine Clutter system.

Headless, from the repo root:
  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python Blender/scripts/create_grass_clump.py
  ... -- render <dir>     also writes one preview PNG per variant into <dir>

What it builds
--------------
Six tufts (grass_clump1_v2 .. grass_clump6_v2), each a different PLANT SHAPE, not the same fan with another blade
count (Mark 2026-11-18: "too similar to each other ... too thin and not low poly"):

  1  broad tuft      five wide blades splayed round the root
  2  tall spray      six long blades arching out, the tallest
  3  rosette         four short, very wide leaves lying low
  4  bent tuft       five blades with a knee: they stand, then fold over
  5  seed stalks     two stiff stalks with a faceted seed head over three short leaves
  6  windswept       four blades all leaning the same way

A blade is a strip folded along its midrib (a shallow V, three vertices across), tapering to one tip vertex: two flat
facets per segment, so it reads as a solid low-poly leaf from every side and never vanishes edge-on like the single-plane
v1 fans did. Blades stand round the root at their own compass bearing and lean outward. 6 triangles per blade (10 with
a knee), 24-40 per tuft.

Vertex colours (exported LINEAR, read raw by shaders/grass_blade.shader)
------------------------------------------------------------------------
  R = height, 0 at the root -> 1 at the top of the TUFT's tallest blade scaled per blade, so the shader's R^BendPower
      pins the roots and lets the tips carry the sway.
  G = per-blade random phase (0..1) so blades in a tuft do not move in unison.
  B = 0.5 (unused: the shader hashes its per-clump random from the instance position).
  A = 1

UVs and the blade texture
-------------------------
  u = across the blade (0 edge, 0.5 midrib, 1 edge), v = height (two tiles per tuft height).
  grass_blade_detail.png (128 px, written here beside grass_blade.vmat) is a tint the shader multiplies over its
  Base -> Tip colour (x2, so mid grey changes nothing): lengthwise fibres of uneven tone, a pale midrib, darker edges,
  a few dry nicks. Mark 2026-11-23: "theres no textures on the grass either, can you add some?" and no environment
  texture below 128 px or of one flat colour.

Units & export
--------------
Modelled in metres, origin at the root on Z = 0. FBX with the build kit's settings (apply_unit_scale, global_scale 1);
the .vmdl (written here too) imports at 0.4 to land on the terrain's 40 u/m.

Outputs
-------
  Assets/models/environment/cloverHills/groundFoliage/grass/grass_clump<N>_v2.fbx + .vmdl
  Blender/blenderprojects/grass_clumps.blend           (all variants on a row)
"""

import math
import os
import random
import sys

import bpy

_REPO_ROOT = os.path.abspath( os.path.join( os.path.dirname( os.path.abspath( __file__ ) ), "..", ".." ) )
MODEL_REL_DIR = "models/environment/cloverHills/groundFoliage/grass"
FBX_DIR = os.path.join( _REPO_ROOT, "Assets", *MODEL_REL_DIR.split( "/" ) )
BLEND_PATH = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "grass_clumps.blend" )
MATERIAL_PATH = "materials/environment/cloverHills/groundFoliage/grass_blade.vmat"

VERSION = "v2"
LAYOUT_SPACING = 1.2      # metres between variants in the saved .blend (object location only)
MATERIAL_NAME = "grass_blade"
ROOT_SINK = 0.03          # metres the roots sit below Z=0 so uneven ground never shows a gap

# One dict per variant. Metres. "blades": list of blade specs, or a count with the shared ranges below.
#   height (lo, hi) · width at the base / at the widest row · lean (lo, hi) = how far the tip ends up from the root,
#   as a share of the blade's height · fold = midrib depth as a share of the width · knee = where the blade folds over
#   (0 = no knee) · bearing_spread = 1 blades all round the compass, 0 all on one bearing
VARIANTS = [
	{ "name": "grass_clump1", "seed": 11, "blades": 5, "height": ( 0.34, 0.52 ), "base_width": 0.070, "mid_width": 0.105,
	  "lean": ( 0.25, 0.55 ), "fold": 0.35, "knee": 0.0, "bearing_spread": 1.0, "root_radius": 0.08 },
	{ "name": "grass_clump2", "seed": 23, "blades": 6, "height": ( 0.58, 0.88 ), "base_width": 0.055, "mid_width": 0.080,
	  "lean": ( 0.30, 0.75 ), "fold": 0.40, "knee": 0.0, "bearing_spread": 1.0, "root_radius": 0.09 },
	{ "name": "grass_clump3", "seed": 37, "blades": 4, "height": ( 0.16, 0.26 ), "base_width": 0.090, "mid_width": 0.150,
	  "lean": ( 0.9, 1.5 ), "fold": 0.30, "knee": 0.0, "bearing_spread": 1.0, "root_radius": 0.03 },
	{ "name": "grass_clump4", "seed": 51, "blades": 5, "height": ( 0.50, 0.75 ), "base_width": 0.060, "mid_width": 0.100,
	  "lean": ( 0.35, 0.60 ), "fold": 0.35, "knee": 0.80, "bearing_spread": 1.0, "root_radius": 0.08 },
	{ "name": "grass_clump5", "seed": 67, "blades": 3, "height": ( 0.18, 0.30 ), "base_width": 0.070, "mid_width": 0.100,
	  "lean": ( 0.5, 0.9 ), "fold": 0.35, "knee": 0.0, "bearing_spread": 1.0, "root_radius": 0.04,
	  "stalks": 2, "stalk_height": ( 0.70, 0.95 ) },
	{ "name": "grass_clump6", "seed": 83, "blades": 4, "height": ( 0.36, 0.60 ), "base_width": 0.065, "mid_width": 0.095,
	  "lean": ( 0.55, 0.95 ), "fold": 0.35, "knee": 0.0, "bearing_spread": 0.22, "root_radius": 0.07 },
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
		mat.diffuse_color = ( 0.22, 0.42, 0.10, 1.0 )
	return mat


DETAIL_TEXTURE_PATH = os.path.join( _REPO_ROOT, "Assets", "materials", "environment", "cloverHills", "groundFoliage", "grass_blade_detail.png" )


def write_detail_texture( path=DETAIL_TEXTURE_PATH, size=128 ):
	"""The blade tint (see the module docstring). Tiles in v; fibres are 8 texels wide so they read as texels on a blade."""
	import struct
	import zlib
	rng = random.Random( 4111 )
	cols = 16
	col_w = size // cols
	rows = [ [ None ] * size for _ in range( size ) ]
	for c in range( cols ):
		edge = abs( ( c + 0.5 ) / cols - 0.5 ) * 2.0                 # 0 at the midrib, 1 at the blade's edge
		base = 1.0 - 0.32 * edge * edge + ( 0.28 if c in ( cols // 2 - 1, cols // 2 ) else 0.0 )
		# a fibre keeps one tone for a stretch, then changes; stretches wrap so the tile repeats in v
		y = 0
		start = rng.randrange( size )
		while y < size:
			run = min( rng.randint( 10, 40 ), size - y )
			tone = base * rng.uniform( 0.7, 1.24 )
			warm = rng.uniform( -0.05, 0.08 )                         # some fibres a little yellower, some cooler
			for k in range( run ):
				yy = ( start + y + k ) % size
				for x in range( c * col_w, ( c + 1 ) * col_w ):
					grain = 1.0 + rng.uniform( -0.06, 0.06 )
					rows[yy][x] = ( tone * ( 1.0 + warm ) * grain, tone * grain, tone * ( 1.0 - warm * 1.6 ) * grain )
			y += run
	# the crease: one dark texel line down the midrib
	for yy in range( size ):
		r, g, b = rows[yy][size // 2]
		rows[yy][size // 2] = ( r * 0.6, g * 0.6, b * 0.6 )
	# dry nicks: small straw-brown blotches
	for _ in range( 9 ):
		cx, cy, w, h = rng.randrange( size ), rng.randrange( size ), rng.randint( 3, 7 ), rng.randint( 4, 10 )
		for dy in range( h ):
			for dx in range( w ):
				x, yy = ( cx + dx ) % size, ( cy + dy ) % size
				r, g, b = rows[yy][x]
				rows[yy][x] = ( r * 1.25, g * 0.95, b * 0.6 )

	def enc( v ):
		lin = max( 0.0, min( 1.0, v * 0.5 ) )                        # the shader multiplies by 2
		srgb = 12.92 * lin if lin <= 0.0031308 else 1.055 * lin ** ( 1.0 / 2.4 ) - 0.055
		return int( round( max( 0.0, min( 1.0, srgb ) ) * 255.0 ) )

	raw = b"".join( b"\x00" + bytes( ch for px in row for ch in ( enc( px[0] ), enc( px[1] ), enc( px[2] ), 255 ) ) for row in rows )

	def chunk( tag, data ):
		body = struct.pack( ">I", len( data ) ) + tag + data
		return body + struct.pack( ">I", zlib.crc32( tag + data ) & 0xffffffff )

	with open( path, "wb" ) as f:
		f.write( b"\x89PNG\r\n\x1a\n" + chunk( b"IHDR", struct.pack( ">IIBBBBB", size, size, 8, 6, 0, 0, 0 ) )
			+ chunk( b"IDAT", zlib.compress( raw, 9 ) ) + chunk( b"IEND", b"" ) )
	print( f"[grass] wrote {path}" )


class _Builder:
	def __init__( self ):
		self.verts = []
		self.faces = []
		self.colors = []
		self.us = []

	def add( self, co, height01, phase, u=0.5 ):
		"""u = across the blade, 0 one edge -> 0.5 the midrib -> 1 the other edge (the blade texture's column)."""
		self.verts.append( co )
		self.colors.append( ( max( 0.0, min( 1.0, height01 ) ), phase, 0.5, 1.0 ) )
		self.us.append( u )
		return len( self.verts ) - 1


def _blade( b: _Builder, rng: random.Random, root, bearing, height, base_w, mid_w, lean, fold, knee, top_height ):
	"""One folded blade. `lean` = horizontal travel of the tip (m); `knee` = share of the height where it folds over."""
	out = ( math.cos( bearing ), math.sin( bearing ) )        # the way it leans
	across = ( -out[1], out[0] )                                # its width axis
	phase = rng.random()

	# Rows up the blade: (share of height, horizontal travel share, half width, extra drop)
	if knee > 0.0:
		rows = [
			( 0.0, 0.0, base_w * 0.5 ),
			( knee * 0.6, 0.30, mid_w * 0.5 ),
			( knee, 0.70, mid_w * 0.45 ),
		]
		# Past the knee the blade folds over: it travels outward and droops back DOWN below the knee
		tip = ( knee * 0.78, 1.0 )
		knee_travel = height * ( 1.0 - knee ) * 0.9
	else:
		rows = [
			( 0.0, 0.0, base_w * 0.5 ),
			( 0.45, 0.40, mid_w * 0.5 ),
		]
		tip = ( 1.0, 1.0 )
		knee_travel = 0.0

	def point( t, travel, half, sign, fold_depth ):
		reach = lean * travel + ( knee_travel if ( knee > 0.0 and travel >= 1.0 ) else 0.0 )
		x = root[0] + out[0] * ( reach - fold_depth ) + across[0] * half * sign
		y = root[1] + out[1] * ( reach - fold_depth ) + across[1] * half * sign
		z = -ROOT_SINK + ( height + ROOT_SINK ) * t
		return ( x, y, z )

	ring = []
	for t, travel, half in rows:
		h01 = ( height * t ) / top_height
		left = b.add( point( t, travel, half, -1.0, 0.0 ), h01, phase, 0.0 )
		# The midrib sits back from the edges: the fold. (Toward the root side, so the V opens outward / upward.)
		centre = b.add( point( t, travel, 0.0, 0.0, half * 2.0 * fold ), h01, phase )
		right = b.add( point( t, travel, half, 1.0, 0.0 ), h01, phase, 1.0 )
		ring.append( ( left, centre, right ) )
	for lo, hi in zip( ring, ring[1:] ):
		b.faces.append( ( lo[0], lo[1], hi[1], hi[0] ) )
		b.faces.append( ( lo[1], lo[2], hi[2], hi[1] ) )
	tip_index = b.add( point( tip[0], tip[1], 0.0, 0.0, 0.0 ), ( height * tip[0] ) / top_height, phase )
	last = ring[-1]
	b.faces.append( ( last[0], last[1], tip_index ) )
	b.faces.append( ( last[1], last[2], tip_index ) )


def _stalk( b: _Builder, rng: random.Random, root, bearing, height, lean, top_height ):
	"""A stiff stalk (a thin three-sided spike) carrying a faceted seed head (a stretched octahedron)."""
	phase = rng.random()
	out = ( math.cos( bearing ), math.sin( bearing ) )
	r = 0.014
	head_len = height * 0.24
	neck = height - head_len

	def at( z_share ):
		# the stalk bows a little: travel grows with the square of the height
		reach = lean * ( z_share ** 2 )
		return ( root[0] + out[0] * reach, root[1] + out[1] * reach, -ROOT_SINK + ( height + ROOT_SINK ) * z_share )

	base_c = at( 0.0 )
	neck_c = at( neck / height )
	base = [ b.add( ( base_c[0] + math.cos( a ) * r, base_c[1] + math.sin( a ) * r, base_c[2] ), 0.0, phase, a / 4.189 ) for a in ( 0.0, 2.094, 4.189 ) ]
	top = [ b.add( ( neck_c[0] + math.cos( a ) * r * 0.6, neck_c[1] + math.sin( a ) * r * 0.6, neck_c[2] ), neck / top_height, phase, a / 4.189 ) for a in ( 0.0, 2.094, 4.189 ) ]
	for k in range( 3 ):
		n = ( k + 1 ) % 3
		b.faces.append( ( base[k], base[n], top[n], top[k] ) )

	# Seed head: four-sided double pyramid, fattest a third of the way up
	mid_c = at( ( neck + head_len * 0.38 ) / height )
	tip_c = at( 1.0 )
	hr = 0.042
	spin = rng.uniform( 0.0, math.pi )
	belt = [ b.add( ( mid_c[0] + math.cos( spin + k * math.pi * 0.5 ) * hr, mid_c[1] + math.sin( spin + k * math.pi * 0.5 ) * hr, mid_c[2] ),
		( neck + head_len * 0.38 ) / top_height, phase, float( k % 2 ) ) for k in range( 4 ) ]
	bottom = b.add( neck_c, neck / top_height, phase )
	apex = b.add( tip_c, height / top_height, phase )
	for k in range( 4 ):
		n = ( k + 1 ) % 4
		b.faces.append( ( bottom, belt[n], belt[k] ) )
		b.faces.append( ( belt[k], belt[n], apex ) )


def create_clump( spec: dict ) -> bpy.types.Object:
	rng = random.Random( spec["seed"] )
	b = _Builder()
	count = int( spec["blades"] )
	stalks = int( spec.get( "stalks", 0 ) )

	heights = [ rng.uniform( *spec["height"] ) for _ in range( count ) ]
	stalk_heights = [ rng.uniform( *spec["stalk_height"] ) for _ in range( stalks ) ]
	top_height = max( heights + stalk_heights )

	base_bearing = rng.uniform( 0.0, math.tau )
	spread = spec["bearing_spread"]
	for k in range( count ):
		# Evenly round the compass with jitter (or bunched on one bearing: the windswept tuft)
		bearing = base_bearing + ( k / count ) * math.tau * spread + rng.uniform( -0.35, 0.35 ) * max( spread, 0.4 )
		rr = spec["root_radius"] * rng.uniform( 0.3, 1.0 )
		root_bearing = bearing if spread > 0.5 else rng.uniform( 0.0, math.tau )
		root = ( math.cos( root_bearing ) * rr, math.sin( root_bearing ) * rr )
		h = heights[k]
		_blade( b, rng, root, bearing, h,
			spec["base_width"] * rng.uniform( 0.8, 1.2 ), spec["mid_width"] * rng.uniform( 0.8, 1.25 ),
			h * rng.uniform( *spec["lean"] ), spec["fold"], spec["knee"], top_height )

	for k in range( stalks ):
		bearing = base_bearing + 0.9 + k * 2.6 + rng.uniform( -0.4, 0.4 )
		root = ( math.cos( bearing ) * 0.02, math.sin( bearing ) * 0.02 )
		_stalk( b, rng, root, bearing, stalk_heights[k], stalk_heights[k] * rng.uniform( 0.08, 0.22 ), top_height )

	name = f"{spec['name']}_{VERSION}"
	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( b.verts, [], b.faces )
	mesh.update()
	for poly in mesh.polygons:
		poly.use_smooth = False      # faceted: every face is one flat tone

	uv_layer = mesh.uv_layers.new( name="UVMap" )
	for loop in mesh.loops:
		# v in tuft heights at 2 tiles per tuft: the texture's fibres run the length of the blade
		uv_layer.data[loop.index].uv = ( b.us[loop.vertex_index], b.colors[loop.vertex_index][0] * 2.0 )

	color_attr = mesh.color_attributes.new( name="Color", type="FLOAT_COLOR", domain="CORNER" )
	for loop in mesh.loops:
		color_attr.data[loop.index].color = b.colors[loop.vertex_index]

	mesh.materials.append( _get_material() )
	obj = bpy.data.objects.new( name, mesh )
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


VMDL_TEMPLATE = """<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc30:version{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1} -->
{
	rootNode =
	{
		_class = "RootNode"
		children =
		[
			{
				_class = "MaterialGroupList"
				children =
				[
					{
						_class = "DefaultMaterialGroup"
						remaps = [  ]
						use_global_default = true
						global_default_material = "%(material)s"
					},
				]
			},
			{
				_class = "RenderMeshList"
				children =
				[
					{
						_class = "RenderMeshFile"
						filename = "%(fbx)s"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{
							exclude_by_default = false
							exception_list = [  ]
						}
					},
				]
			},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}
}
"""


def write_vmdl( name: str ) -> None:
	path = os.path.join( FBX_DIR, f"{name}.vmdl" )
	with open( path, "w", encoding="utf-8", newline="\n" ) as f:
		f.write( VMDL_TEMPLATE % { "material": MATERIAL_PATH, "fbx": f"{MODEL_REL_DIR}/{name}.fbx" } )
	print( f"[grass] wrote {path}" )


def render_previews( objects, directory: str ) -> None:
	"""One PNG per variant: workbench, flat faces, a three-quarter view, a 1.8 m post beside it for scale."""
	os.makedirs( directory, exist_ok=True )
	scene = bpy.context.scene
	scene.render.engine = "BLENDER_WORKBENCH"
	scene.display.shading.light = "STUDIO"
	scene.display.shading.color_type = "MATERIAL"
	scene.display.shading.show_cavity = True
	scene.render.resolution_x = 640
	scene.render.resolution_y = 640
	scene.render.film_transparent = False
	scene.world = bpy.data.worlds.new( "w" )
	scene.world.color = ( 0.55, 0.6, 0.65 )

	cam_data = bpy.data.cameras.new( "cam" )
	cam = bpy.data.objects.new( "cam", cam_data )
	scene.collection.objects.link( cam )
	scene.camera = cam

	ground = bpy.data.meshes.new( "ground" )
	ground.from_pydata( [ ( -2, -2, 0 ), ( 2, -2, 0 ), ( 2, 2, 0 ), ( -2, 2, 0 ) ], [], [ ( 0, 1, 2, 3 ) ] )
	gmat = bpy.data.materials.new( "ground" )
	gmat.diffuse_color = ( 0.16, 0.24, 0.10, 1.0 )
	ground.materials.append( gmat )
	ground_obj = bpy.data.objects.new( "ground", ground )
	scene.collection.objects.link( ground_obj )

	for obj in objects:
		for other in objects:
			other.hide_render = other is not obj
		ground_obj.location = obj.location
		cam.location = ( obj.location.x + 1.05, obj.location.y - 1.05, 0.62 )
		direction = ( obj.location.x - cam.location.x, obj.location.y - cam.location.y, 0.32 - cam.location.z )
		import mathutils
		cam.rotation_euler = mathutils.Vector( direction ).to_track_quat( "-Z", "Y" ).to_euler()
		scene.render.filepath = os.path.join( directory, f"{obj.name}.png" )
		bpy.ops.render.render( write_still=True )
		print( f"[grass] rendered {scene.render.filepath}" )


def main() -> None:
	argv = sys.argv[sys.argv.index( "--" ) + 1:] if "--" in sys.argv else []
	_reset_scene()

	objects = []
	for index, spec in enumerate( VARIANTS ):
		obj = create_clump( spec )
		obj.location = ( index * LAYOUT_SPACING, 0.0, 0.0 )
		objects.append( obj )
		mesh = obj.data
		zs = [ v.co.z for v in mesh.vertices ]
		xs = [ v.co.x for v in mesh.vertices ]
		tris = sum( len( p.vertices ) - 2 for p in mesh.polygons )
		print( f"[grass] {obj.name}: {len( mesh.vertices )} verts, {tris} tris, height {max( zs ):.2f} m, width {max( xs ) - min( xs ):.2f} m" )

	for obj in objects:
		export_fbx( obj, FBX_DIR )
		write_vmdl( obj.name )

	write_detail_texture()

	os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
	bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
	print( f"[grass] saved {BLEND_PATH}" )

	if len( argv ) >= 2 and argv[0] == "render":
		render_previews( objects, argv[1] )


if __name__ == "__main__":
	main()
