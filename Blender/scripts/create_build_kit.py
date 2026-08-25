"""
Wood build kit generator — one object per piece id in Assets/data/build_pieces.json.

Run in Blender Scripting workspace, or:
  blender --background --python create_build_kit.py

Builds all 22 structural pieces at their true size, each authored around its own origin, so
you can delete the objects you do not want and save the file as many times as you need. Re-
running is safe: every object is rebuilt from scratch by name.

Geometry contract — keep in step with Code/Building/BuildModuleDimensions.cs:
  ModuleMeters     2.0     floor / wall module edge
  HalfModule       1.0     the 1 m size variants, and the rise of one stair piece
  ThinMeters       0.06    floor, wall and roof plate thickness
  BeamMeters       0.2     square section of every post / beam
  RoofSlopeMeters  2.8284  sqrt(2*2 + 2*2); the code constant is rounded to 2.82

STAIRS climb 1 m per piece over a 2 m footprint, so two pieces make one 2 m storey and a
flight can turn part way up. Three pieces share that rise: the straight run, and a left and
right quarter-turn winder that both keep the straight run's -X entry face.

ORIENTATION AND ORIGIN — this is what makes the pieces drop into the prefabs:
  * Z up, meters, 1 unit = 1 m.
  * Origin sits at the centre of the piece's declared size box, so the symmetric BoxCollider
    on the prefab wraps the mesh with no offset.
  * Floors are thin on Z, walls thin on Y with height on Z, beams run along their long axis
    (X for horizontal, Z for vertical).

NOTHING HERE RELIES ON A RUNTIME ROTATION. Every piece is modelled in the orientation it should
appear in at yaw 0 — a roof is already pitched, a 45 degree brace already leans. Two families
would otherwise be turned by BuildPrefabUtility.ApplyStandardPieceTransform, so their rotation
is baked into the mesh instead and the prefab has to cancel what it would apply on top:

  Family      Prefab rotation applied   Baked into the mesh   ModelRenderer child rotation
  Roofs       -45 deg about X           yes                   +45 X  (0.3826834,0,0,0.9238795)
  45 beams    -45 deg about Y           yes                   +45 Y  (0,0.3826834,0,0.9238795)

  Net effect is identical to an unrotated mesh on the root, so placement, snapping and
  collision are untouched. It only changes what the asset looks like on its own.

  DIRECTION IS NOT A FREE CHOICE — the mesh has to lie inside the volume the collider already
  occupies, which is the declared size box turned by that same prefab rotation. -45 degrees
  about X sends local +Y down, so the roof slab DESCENDS towards +Y: the panel drops 2 m over a
  2 m run towards +Y, the hip is high at (-X, -Y), the valley is low at (+X, +Y). -45 degrees
  about Y sends local +X up, so a brace RISES as it runs towards +X. Lean either the other way
  and the mesh crosses its own collider.

  Both families therefore fall outside the size table: a pitched roof measures about
  2 x 2.04 x 2.04 instead of 2 x 2.82 x 0.06 (its shell is 0.06 m perpendicular to the slope),
  and a leaning 2 m brace measures 1.56 x 0.2 x 1.56 instead of 2 x 0.2 x 0.2.

WORKFLOW NOTE: pieces are spread out on a grid using the OBJECT location, never baked into
the mesh. If you export by hand, clear the location first (Object > Clear > Location, Alt+G)
or set LAYOUT_SPACING to 0. The _export helper below already zeroes it for you.
"""

import math
import os

import bpy

# --- kit contract ---
MODULE = 2.0
HALF_MODULE = 1.0
THIN = 0.06
BEAM = 0.2
ROOF_SLOPE = math.sqrt( MODULE * MODULE + MODULE * MODULE )

# --- tweak these ---
DOOR_OPENING_WIDTH = 1.0          # doorway hole, centred on the wall (m)
DOOR_OPENING_HEIGHT = 1.8         # measured up from the wall bottom (m)
STAIR_RISE = HALF_MODULE          # climb per stair piece (m) — two pieces make one wall height
STAIR_STEP_COUNT = 8              # 8 steps over a 1 m rise = 0.125 m rise / 0.25 m going
STAIR_PLATE = THIN                # tread / riser plate thickness; stairs are hollow underneath
SPIRAL_STEP_COUNT = 8             # same rise per step as the straight run, so they read as one flight
SPIRAL_TURN_DEGREES = 90.0        # quarter turn inside the 2 x 2 footprint
BUILD_DOOR_LEAF = True            # extra loose leaf to sit in the doorway; not a catalog id
BUILD_REFERENCE = True            # assembled seam checks, in their own collection
LAYOUT_SPACING = 3.5              # grid gap between pieces (m); 0 stacks them all on origin
GENERATE_UVS = True

# Set to a folder to also export one .fbx per piece; "" skips exporting.
EXPORT_DIR = ""

FLOOR_COLLECTION = "build_kit_floors"
WALL_COLLECTION = "build_kit_walls"
ROOF_COLLECTION = "build_kit_roofs"
STAIR_COLLECTION = "build_kit_stairs"
BEAM_COLLECTION = "build_kit_beams"
EXTRA_COLLECTION = "build_kit_extra"
REFERENCE_COLLECTION = "build_kit_reference"

# Mirrors BuildModuleDimensions.SizesMeters. None = deliberately exempt (see the roof note).
EXPECTED_SIZES = {
	"build_wood_floor": ( MODULE, MODULE, THIN ),
	"build_wood_floor_2m1m": ( MODULE, HALF_MODULE, THIN ),
	"build_wood_floor_1m1m": ( HALF_MODULE, HALF_MODULE, THIN ),
	"build_wood_triangleFloor": ( MODULE, MODULE, THIN ),
	"build_wood_wall": ( MODULE, THIN, MODULE ),
	"build_wood_wall_2m1m": ( MODULE, THIN, HALF_MODULE ),
	"build_wood_wall_1m1m": ( HALF_MODULE, THIN, HALF_MODULE ),
	"build_wood_door": ( MODULE, THIN, MODULE ),
	"build_wood_45wallLeft": ( MODULE, THIN, MODULE ),
	"build_wood_45wallRight": ( MODULE, THIN, MODULE ),
	"build_wood_45roof": None,
	"build_wood_45roofInsideCorner": ( MODULE, MODULE, MODULE ),
	"build_wood_45roofOutsideCorner": ( MODULE, MODULE, MODULE ),
	"build_wood_stairs": ( MODULE, MODULE, STAIR_RISE ),
	"build_wood_stairsSpiralLeft": ( MODULE, MODULE, STAIR_RISE ),
	"build_wood_stairsSpiralRight": ( MODULE, MODULE, STAIR_RISE ),
	"build_wood_horizontalBeam_1m": ( HALF_MODULE, BEAM, BEAM ),
	"build_wood_horizontalBeam_2m": ( MODULE, BEAM, BEAM ),
	"build_wood_verticalBeam_1m": ( BEAM, BEAM, HALF_MODULE ),
	"build_wood_verticalBeam_2m": ( BEAM, BEAM, MODULE ),
	"build_wood_45Beam_1m": None,
	"build_wood_45Beam_2m": None,
}


# ---------------------------------------------------------------- blender plumbing

def _purge_object( name: str ) -> None:
	obj = bpy.data.objects.get( name )
	if obj is None:
		return
	mesh = obj.data
	bpy.data.objects.remove( obj, do_unlink=True )
	if mesh and mesh.users == 0:
		bpy.data.meshes.remove( mesh )


def _get_collection( name: str ) -> bpy.types.Collection:
	col = bpy.data.collections.get( name )
	if col is None:
		col = bpy.data.collections.new( name )
		bpy.context.scene.collection.children.link( col )
	return col


def _make_object( name, verts, faces, collection ) -> bpy.types.Object:
	_purge_object( name )

	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( verts, [], faces )
	mesh.validate()
	mesh.update()

	obj = bpy.data.objects.new( name, mesh )
	collection.objects.link( obj )

	for polygon in obj.data.polygons:
		polygon.use_smooth = False

	return obj


def _activate( obj: bpy.types.Object ) -> None:
	if bpy.context.object and bpy.context.object.mode != "OBJECT":
		bpy.ops.object.mode_set( mode="OBJECT" )

	bpy.ops.object.select_all( action="DESELECT" )
	obj.select_set( True )
	bpy.context.view_layer.objects.active = obj


def _generate_uvs( obj: bpy.types.Object ) -> None:
	try:
		_activate( obj )
		bpy.ops.object.mode_set( mode="EDIT" )
		bpy.ops.mesh.select_all( action="SELECT" )
		bpy.ops.uv.smart_project( angle_limit=math.radians( 66.0 ), island_margin=0.02 )
		bpy.ops.object.mode_set( mode="OBJECT" )
	except RuntimeError as err:
		print( f"[build_kit] UV unwrap skipped for {obj.name}: {err}" )
		if bpy.context.object and bpy.context.object.mode != "OBJECT":
			bpy.ops.object.mode_set( mode="OBJECT" )


def _solidify_down( obj: bpy.types.Object, thickness: float ) -> None:
	"""Grow a shell downwards so the authored surface stays the visible top face."""
	_activate( obj )

	mod = obj.modifiers.new( name="Thickness", type="SOLIDIFY" )
	mod.thickness = thickness
	mod.offset = -1.0
	mod.use_even_offset = True   # keeps thickness constant through a hip / valley fold
	mod.use_rim = True
	bpy.ops.object.modifier_apply( modifier=mod.name )


# ---------------------------------------------------------------- geometry primitives

def _extrude_polygon_z( points, z_lo, z_hi ):
	"""
	Solid prism from a polygon given counter-clockwise in (x, y), extruded along Z.

	Winding is solved once here so no piece has to reason about normals: the caps face +/-Z
	and every side face points out of the polygon.
	"""
	count = len( points )
	verts = [ ( x, y, z_lo ) for ( x, y ) in points ]
	verts += [ ( x, y, z_hi ) for ( x, y ) in points ]

	faces = [ tuple( range( count, count * 2 ) ) ]        # top cap, +Z
	faces.append( tuple( reversed( range( count ) ) ) )   # bottom cap, -Z
	for i in range( count ):
		j = ( i + 1 ) % count
		faces.append( ( i, j, j + count, i + count ) )    # side wall, outward

	return verts, faces


def _stand_up( geometry ):
	"""
	Rotate a Z-thin prism +90 degrees about X so it becomes Y-thin and stands up.

	Lets wall-family pieces be drawn face-on in (x, y) — x is wall width, y is wall height —
	and land with thickness on Y and height on Z. A pure rotation, so normals stay correct.
	"""
	verts, faces = geometry
	return [ ( x, -z, y ) for ( x, y, z ) in verts ], faces


def _pitch_x( geometry, degrees ):
	"""Rotate a piece about X. Pure rotation, so winding and normals stay correct."""
	verts, faces = geometry
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return [ ( x, y * cos_a - z * sin_a, y * sin_a + z * cos_a ) for ( x, y, z ) in verts ], faces


def _pitch_y( geometry, degrees ):
	"""Rotate a piece about Y — how a 45 degree brace gets its lean baked in."""
	verts, faces = geometry
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return [ ( x * cos_a + z * sin_a, y, -x * sin_a + z * cos_a ) for ( x, y, z ) in verts ], faces


def _yaw_z( geometry, degrees ):
	"""Rotate a piece about Z — used to lay reference neighbours around a corner."""
	verts, faces = geometry
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return [ ( x * cos_a - y * sin_a, x * sin_a + y * cos_a, z ) for ( x, y, z ) in verts ], faces


def _mirror_y( geometry ):
	"""
	Flip a piece across the XZ plane — how the right-hand winder is made from the left one.
	Mirroring reverses handedness, so every face is walked backwards to keep normals outward.
	"""
	verts, faces = geometry
	return [ ( x, -y, z ) for ( x, y, z ) in verts ], [ tuple( reversed( face ) ) for face in faces ]


def _merge( parts ):
	"""Combine several (verts, faces) islands into one mesh."""
	verts = []
	faces = []
	for part_verts, part_faces in parts:
		offset = len( verts )
		verts.extend( part_verts )
		faces.extend( tuple( index + offset for index in face ) for face in part_faces )
	return verts, faces


def _rect( width, height, center=( 0.0, 0.0 ) ):
	"""Counter-clockwise rectangle in (x, y), centred on `center`."""
	hw = width * 0.5
	hh = height * 0.5
	cx, cy = center
	return [
		( cx - hw, cy - hh ),
		( cx + hw, cy - hh ),
		( cx + hw, cy + hh ),
		( cx - hw, cy + hh ),
	]


def _box( size, center=( 0.0, 0.0, 0.0 ) ):
	"""Axis-aligned box from a (x, y, z) size, centred on `center`."""
	sx, sy, sz = size
	cx, cy, cz = center
	return _extrude_polygon_z( _rect( sx, sy, ( cx, cy ) ), cz - sz * 0.5, cz + sz * 0.5 )


def _plate( size ):
	"""Floor-family plate: thin on Z, origin at the box centre."""
	return _box( size )


def _wall( width, height, thickness=THIN ):
	"""Wall-family panel: width on X, thickness on Y, height on Z."""
	return _stand_up( _extrude_polygon_z( _rect( width, height ), -thickness * 0.5, thickness * 0.5 ) )


# ---------------------------------------------------------------- piece geometry

def triangle_floor_geometry():
	"""
	Half of a 2 x 2 floor, cut corner to corner. The right angle is at (-X, -Y) and the
	hypotenuse runs from (+X, -Y) to (-X, +Y); mirror in Blender if you want the other hand.
	Origin stays the centre of the full square so the module box collider still fits.
	"""
	half = MODULE * 0.5
	points = [ ( -half, -half ), ( half, -half ), ( -half, half ) ]
	return _extrude_polygon_z( points, -THIN * 0.5, THIN * 0.5 )


def door_geometry():
	"""
	Wall with a doorway punched out — two jambs and a header, so the outer size still matches
	build_wood_wall. No leaf: the piece is the frame (see BUILD_DOOR_LEAF for a loose one).
	"""
	jamb_width = ( MODULE - DOOR_OPENING_WIDTH ) * 0.5
	header_height = MODULE - DOOR_OPENING_HEIGHT
	jamb_x = ( MODULE - jamb_width ) * 0.5
	header_y = ( MODULE - header_height ) * 0.5

	parts = [
		_extrude_polygon_z( _rect( jamb_width, MODULE, ( -jamb_x, 0.0 ) ), -THIN * 0.5, THIN * 0.5 ),
		_extrude_polygon_z( _rect( jamb_width, MODULE, ( jamb_x, 0.0 ) ), -THIN * 0.5, THIN * 0.5 ),
		_extrude_polygon_z( _rect( DOOR_OPENING_WIDTH, header_height, ( 0.0, header_y ) ), -THIN * 0.5, THIN * 0.5 ),
	]
	return _stand_up( _merge( parts ) )


def _gable_points_tall_on_right():
	"""Right triangle drawn face-on: low at -X, full height at +X, hypotenuse between."""
	half = MODULE * 0.5
	return [ ( -half, -half ), ( half, -half ), ( half, half ) ]


def _mirror_points_x( points ):
	"""Mirroring flips the winding, so walk the outline backwards to keep it CCW."""
	return [ ( -x, y ) for ( x, y ) in reversed( points ) ]


def gable_wall_geometry( tall_on_right: bool ):
	"""
	Triangular gable that closes the space under a 45 degree roof. The hypotenuse is
	2.8284 m, the same slope length as build_wood_45roof, so it beds straight against the
	roof underside. Set one of each side by side and the apex meets in the middle.
	"""
	points = _gable_points_tall_on_right()
	if not tall_on_right:
		points = _mirror_points_x( points )

	return _stand_up( _extrude_polygon_z( points, -THIN * 0.5, THIN * 0.5 ) )


def _step_heights( count, rise_total ):
	"""
	Bottom and top of every step, walking up. Step 1 starts on the piece floor and the last
	tread lands exactly on the piece ceiling, which is the floor of the level above.
	"""
	half_rise = rise_total * 0.5
	rise = rise_total / count
	return [ ( -half_rise + ( i - 1 ) * rise, -half_rise + i * rise ) for i in range( 1, count + 1 ) ]


def stairs_geometry():
	"""
	Straight flight: climbs STAIR_RISE (1 m) over the full 2 m run in +X, module wide on Y.

	Treads and risers are separate STAIR_PLATE slabs rather than one solid wedge, so the
	underside is open — you get a proper flight you can walk beside and under, not a block.
	Riser 1 sits on the piece floor and tread 8 is flush with the ceiling, so a flight reads
	continuously into the floor above and into the next stair piece stacked on top.
	"""
	half = MODULE * 0.5
	going = MODULE / STAIR_STEP_COUNT

	parts = []
	for index, ( step_bottom, step_top ) in enumerate( _step_heights( STAIR_STEP_COUNT, STAIR_RISE ) ):
		back_x = -half + index * going
		front_x = back_x + going

		# Riser first: a wall across the width, from the step below up to this tread's underside.
		parts.append( _extrude_polygon_z(
			_rect( STAIR_PLATE, MODULE, ( back_x + STAIR_PLATE * 0.5, 0.0 ) ),
			step_bottom,
			step_top - STAIR_PLATE ) )

		# Then the tread it holds up.
		parts.append( _extrude_polygon_z(
			_rect( going, MODULE, ( ( back_x + front_x ) * 0.5, 0.0 ) ),
			step_top - STAIR_PLATE,
			step_top ) )

	return _merge( parts )


def _ensure_ccw( points ):
	"""Winding guard for hand-built outlines, so _extrude_polygon_z always gets CCW."""
	area = 0.0
	for i in range( len( points ) ):
		x0, y0 = points[i]
		x1, y1 = points[( i + 1 ) % len( points )]
		area += x0 * y1 - x1 * y0
	return points if area > 0.0 else list( reversed( points ) )


def _winder_pivot():
	"""Inside corner of the turn: the corner shared by the entry face and the exit face."""
	half = MODULE * 0.5
	return ( -half, half )


def _winder_ray( degrees ):
	"""
	Direction and length of the radial line at `degrees` through the turn, measured from the
	pivot. 0 deg lies along the entry face (x = -1, running -Y), 90 deg along the exit face
	(y = +1, running +X); every ray in between ends on the far edge of the 2 x 2 square.
	"""
	angle = math.radians( degrees )
	sin_a = math.sin( angle )
	cos_a = math.cos( angle )

	reach = MODULE
	length = min(
		reach / sin_a if sin_a > 1e-6 else math.inf,
		reach / cos_a if cos_a > 1e-6 else math.inf )
	return ( sin_a, -cos_a ), length


def _winder_boundary_point( degrees ):
	( dx, dy ), length = _winder_ray( degrees )
	px, py = _winder_pivot()
	return ( px + dx * length, py + dy * length )


def _winder_tread_points( degrees_start, degrees_end ):
	"""
	Wedge tread clipped to the module square: pivot, out along the first radial edge, round
	the far corner if this wedge crosses the diagonal, back down the second radial edge.
	"""
	half = MODULE * 0.5
	points = [ _winder_pivot(), _winder_boundary_point( degrees_start ) ]

	diagonal = SPIRAL_TURN_DEGREES * 0.5
	if degrees_start < diagonal < degrees_end:
		points.append( ( half, -half ) )

	points.append( _winder_boundary_point( degrees_end ) )
	return _ensure_ccw( points )


def _clamp_to_module( point ):
	"""
	Keep a point inside the 2 x 2 footprint. Radial lines already end on the boundary, so
	stepping sideways off one takes the outer corners a few cm out of the module and through
	whatever is snapped alongside; clamping trims those instead of shrinking the whole board.
	"""
	half = MODULE * 0.5
	x, y = point
	return ( min( max( x, -half ), half ), min( max( y, -half ), half ) )


def _winder_riser_points( degrees ):
	"""Thin wall standing on the radial edge at `degrees`, leaning into the next tread."""
	( dx, dy ), length = _winder_ray( degrees )
	px, py = _winder_pivot()
	nx, ny = ( -dy, dx )   # 90 deg towards the exit, so the wall sits under the tread it carries

	points = [
		( px, py ),
		( px + dx * length, py + dy * length ),
		( px + dx * length + nx * STAIR_PLATE, py + dy * length + ny * STAIR_PLATE ),
		( px + nx * STAIR_PLATE, py + ny * STAIR_PLATE ),
	]
	return _ensure_ccw( [ _clamp_to_module( point ) for point in points ] )


def winder_stairs_geometry( turns_left: bool ):
	"""
	Quarter-turn winder: climbs the same STAIR_RISE as the straight flight, fills the 2 x 2
	footprint, and turns 90 degrees so you walk in through one face and out through the one
	beside it. Same hollow tread + riser construction as the straight stairs.

	The left-hand piece is authored, the right-hand one is its mirror:
	  left   enter on -X heading +X, leave on +Y heading +Y   pivot at (-1, +1)
	  right  enter on -X heading +X, leave on -Y heading -Y   pivot at (-1, -1)

	Both keep the straight run's entry face, so a straight piece feeds either hand without
	rotating anything.
	"""
	sweep = SPIRAL_TURN_DEGREES / SPIRAL_STEP_COUNT

	parts = []
	for index, ( step_bottom, step_top ) in enumerate( _step_heights( SPIRAL_STEP_COUNT, STAIR_RISE ) ):
		degrees_start = index * sweep
		degrees_end = degrees_start + sweep

		parts.append( _extrude_polygon_z(
			_winder_riser_points( degrees_start ),
			step_bottom,
			step_top - STAIR_PLATE ) )
		parts.append( _extrude_polygon_z(
			_winder_tread_points( degrees_start, degrees_end ),
			step_top - STAIR_PLATE,
			step_top ) )

	geometry = _merge( parts )
	return geometry if turns_left else _mirror_y( geometry )


def roof_panel_geometry():
	"""
	Straight 45 degree panel in final world orientation: module wide on X, dropping 2 m over a
	2 m run towards +Y so it lies in the pitched collider slab, thickness perpendicular to the
	slope. Pitching the plate rather than drawing it slanted keeps the shell exactly THIN thick
	through the rotation, and rotating about the origin leaves it centred on its module cube.
	"""
	return _pitch_x( _plate( ( MODULE, ROOF_SLOPE, THIN ) ), -45.0 )


def outside_corner_plane():
	"""
	Hip: height is min(M-x, M-y), so the high point is the (0,0) corner and the two eaves sit
	on the z = 0 line along x = M and y = M. The fold from (0,0,M) to (M,M,0) is the ridge.
	Its two 2.8284 slope edges lie on the x = 0 and y = 0 faces, which is where the panels
	going into the corner mate — matching a panel that drops towards +Y.
	"""
	m = MODULE
	verts = [
		( 0.0, 0.0, m ),     # high outer corner
		( m, 0.0, 0.0 ),     # eave, along y = 0
		( m, m, 0.0 ),       # low corner
		( 0.0, m, 0.0 ),     # eave, along x = 0
	]
	return verts, [ ( 0, 1, 2 ), ( 0, 2, 3 ) ]


def inside_corner_plane():
	"""
	Valley: height is max(M-x, M-y), so the fold from (0,0,M) to (M,M,0) is a gutter and the
	two top edges sit flat at ridge height. Here the 2.8284 slope edges are on the x = M and
	y = M faces, again mating a panel that drops towards +Y.
	"""
	m = MODULE
	verts = [
		( 0.0, 0.0, m ),     # ridge height, outer corner
		( m, 0.0, m ),       # ridge height, along y = 0
		( m, m, 0.0 ),       # bottom of the valley
		( 0.0, m, m ),       # ridge height, along x = 0
	]
	return verts, [ ( 0, 1, 2 ), ( 0, 2, 3 ) ]


def brace_45_geometry( length ):
	"""
	Diagonal brace in final world orientation: a BeamMeters square section `length` long on its
	own axis, leaned -45 degrees about Y so it rises as it runs towards +X. That is the volume
	Beam45PrefabLocalRotation gives the collider, so the prefab cancels the rotation instead of
	applying it — see the orientation note at the top of this file.
	"""
	return _pitch_y( _box( ( length, BEAM, BEAM ) ), -45.0 )


def door_leaf_geometry():
	"""Loose leaf sized to swing inside the doorway, with a 0.02 m gap all round."""
	return _wall( DOOR_OPENING_WIDTH - 0.04, DOOR_OPENING_HEIGHT - 0.02, THIN )


# ---------------------------------------------------------------- build

def _recenter_module_corner( obj: bpy.types.Object ) -> None:
	"""Folded corners are authored in the 0..MODULE cube; pull them onto their own centre."""
	offset = MODULE * 0.5
	for vert in obj.data.vertices:
		vert.co.x -= offset
		vert.co.y -= offset
		vert.co.z -= offset


def _clamp_fold_to_module_box( obj: bpy.types.Object ) -> None:
	"""Keep folded roof corners inside their 2×2×2 module collider — solidify can poke past it."""
	half = MODULE * 0.5
	for vert in obj.data.vertices:
		vert.co.x = min( max( vert.co.x, -half ), half )
		vert.co.y = min( max( vert.co.y, -half ), half )
		vert.co.z = min( max( vert.co.z, -half ), half )


def build_piece( name, geometry, collection, location=( 0.0, 0.0, 0.0 ), folded=False ) -> bpy.types.Object:
	verts, faces = geometry
	obj = _make_object( name, verts, faces, collection )

	if folded:
		_solidify_down( obj, THIN )
		_recenter_module_corner( obj )
		_clamp_fold_to_module_box( obj )
		for polygon in obj.data.polygons:
			polygon.use_smooth = False

	if GENERATE_UVS:
		_generate_uvs( obj )

	obj.location = location
	return obj


def _kit_rows():
	"""
	(collection, [ (name, geometry, folded) ]) — one row per family, laid out along +X.
	Geometry is built eagerly here; it is cheap and keeps the table readable.
	"""
	return [
		( FLOOR_COLLECTION, [
			( "build_wood_floor", _plate( ( MODULE, MODULE, THIN ) ), False ),
			( "build_wood_floor_2m1m", _plate( ( MODULE, HALF_MODULE, THIN ) ), False ),
			( "build_wood_floor_1m1m", _plate( ( HALF_MODULE, HALF_MODULE, THIN ) ), False ),
			( "build_wood_triangleFloor", triangle_floor_geometry(), False ),
		] ),
		( WALL_COLLECTION, [
			( "build_wood_wall", _wall( MODULE, MODULE ), False ),
			( "build_wood_wall_2m1m", _wall( MODULE, HALF_MODULE ), False ),
			( "build_wood_wall_1m1m", _wall( HALF_MODULE, HALF_MODULE ), False ),
			( "build_wood_door", door_geometry(), False ),
			( "build_wood_45wallLeft", gable_wall_geometry( True ), False ),
			( "build_wood_45wallRight", gable_wall_geometry( False ), False ),
		] ),
		( ROOF_COLLECTION, [
			( "build_wood_45roof", roof_panel_geometry(), False ),
			( "build_wood_45roofInsideCorner", inside_corner_plane(), True ),
			( "build_wood_45roofOutsideCorner", outside_corner_plane(), True ),
		] ),
		( STAIR_COLLECTION, [
			( "build_wood_stairs", stairs_geometry(), False ),
			( "build_wood_stairsSpiralLeft", winder_stairs_geometry( True ), False ),
			( "build_wood_stairsSpiralRight", winder_stairs_geometry( False ), False ),
		] ),
		( BEAM_COLLECTION, [
			( "build_wood_horizontalBeam_1m", _box( ( HALF_MODULE, BEAM, BEAM ) ), False ),
			( "build_wood_horizontalBeam_2m", _box( ( MODULE, BEAM, BEAM ) ), False ),
			( "build_wood_verticalBeam_1m", _box( ( BEAM, BEAM, HALF_MODULE ) ), False ),
			( "build_wood_verticalBeam_2m", _box( ( BEAM, BEAM, MODULE ) ), False ),
			( "build_wood_45Beam_1m", brace_45_geometry( HALF_MODULE ), False ),
			( "build_wood_45Beam_2m", brace_45_geometry( MODULE ), False ),
		] ),
	]


def piece_definitions():
	"""
	Flat (name, collection, geometry, folded) list for the whole kit, with no layout applied.
	This is what split_build_kit.py walks to write one file per piece.
	"""
	definitions = []
	for collection_name, entries in _kit_rows():
		for name, geometry, folded in entries:
			definitions.append( ( name, collection_name, geometry, folded ) )

	if BUILD_DOOR_LEAF:
		definitions.append( ( "build_wood_door_leaf", EXTRA_COLLECTION, door_leaf_geometry(), False ) )

	return definitions


def _reference_geometry():
	"""
	Assembled seam checks, built from the SHIPPING geometry rather than a hand-written copy of
	it: a hip and a valley with the real roof panel butted onto both of their slope edges, and
	the gable pair set as one gable. If the kit is right these sit flush along the 2.8284 m
	edges with no gap and no step. Nothing here is a shippable piece.

	The hip's slope edges face -X and -Y, the valley's face +X and +Y, and the panel that mates
	a corner sideways is the same panel turned -90 degrees about Z.
	"""
	panel = roof_panel_geometry()
	panel_turned = _yaw_z( panel, -90.0 )

	return [
		( "reference_hip_corner", outside_corner_plane(), True, ( 0.0, 0.0, 0.0 ) ),
		( "reference_hip_panel_x", panel, False, ( -MODULE, 0.0, 0.0 ) ),
		( "reference_hip_panel_y", panel_turned, False, ( 0.0, -MODULE, 0.0 ) ),
		( "reference_valley_corner", inside_corner_plane(), True, ( 5.0 * MODULE, 0.0, 0.0 ) ),
		( "reference_valley_panel_x", panel, False, ( 6.0 * MODULE, 0.0, 0.0 ) ),
		( "reference_valley_panel_y", panel_turned, False, ( 5.0 * MODULE, MODULE, 0.0 ) ),
		( "reference_gable_left", gable_wall_geometry( True ), False, ( -MODULE * 0.5, 6.0, 0.0 ) ),
		( "reference_gable_right", gable_wall_geometry( False ), False, ( MODULE * 0.5, 6.0, 0.0 ) ),
	]


# ---------------------------------------------------------------- export / report

def export_piece_fbx( obj: bpy.types.Object, directory: str ) -> str:
	os.makedirs( directory, exist_ok=True )
	path = os.path.join( directory, f"{obj.name}.fbx" )

	# The grid layout lives on the object transform, and FBX bakes it, so park it on the
	# origin for the write and put it back afterwards.
	location = tuple( obj.location )
	obj.location = ( 0.0, 0.0, 0.0 )

	_activate( obj )
	bpy.ops.export_scene.fbx(
		filepath=path,
		use_selection=True,
		apply_unit_scale=True,
		global_scale=1.0,
		axis_forward="-Z",
		axis_up="Y",
		mesh_smooth_type="FACE",
	)

	obj.location = location
	print( f"[build_kit] exported {path}" )
	return path


def _measure( obj: bpy.types.Object ):
	mesh = obj.data
	spans = []
	for axis in range( 3 ):
		values = [ vert.co[axis] for vert in mesh.vertices ]
		spans.append( max( values ) - min( values ) )
	return spans


def bounds_center( obj: bpy.types.Object ):
	"""Mesh-local centre of the bounding box — (0,0,0) for a piece authored on its origin."""
	mesh = obj.data
	center = []
	for axis in range( 3 ):
		values = [ vert.co[axis] for vert in mesh.vertices ]
		center.append( ( max( values ) + min( values ) ) * 0.5 )
	return center


def report_piece( obj: bpy.types.Object ) -> bool:
	spans = _measure( obj )
	expected = EXPECTED_SIZES.get( obj.name, None )

	verdict = ""
	matches = True
	if expected is not None:
		matches = all( abs( spans[i] - expected[i] ) < 0.01 for i in range( 3 ) )
		if not matches:
			verdict = (
				f"  MISMATCH vs BuildModuleDimensions "
				f"({expected[0]:.4f} x {expected[1]:.4f} x {expected[2]:.4f})" )

	print(
		f"[build_kit] {obj.name}: {len( obj.data.polygons )} faces, "
		f"bounds {spans[0]:.4f} x {spans[1]:.4f} x {spans[2]:.4f} m{verdict}" )
	return matches


def create_build_kit():
	pieces = []

	for row_index, ( collection_name, entries ) in enumerate( _kit_rows() ):
		collection = _get_collection( collection_name )
		for column, ( name, geometry, folded ) in enumerate( entries ):
			location = ( column * LAYOUT_SPACING, -row_index * LAYOUT_SPACING, 0.0 )
			pieces.append( build_piece( name, geometry, collection, location, folded ) )

	if BUILD_DOOR_LEAF:
		extra = _get_collection( EXTRA_COLLECTION )
		build_piece( "build_wood_door_leaf", door_leaf_geometry(), extra,
			( 0.0, LAYOUT_SPACING, 0.0 ) )

	if BUILD_REFERENCE:
		reference = _get_collection( REFERENCE_COLLECTION )
		for name, geometry, folded, location in _reference_geometry():
			offset = ( location[0], location[1] + 3.0 * LAYOUT_SPACING, location[2] )
			build_piece( name, geometry, reference, offset, folded )

	mismatches = 0
	for piece in pieces:
		if not report_piece( piece ):
			mismatches += 1
		if EXPORT_DIR:
			export_piece_fbx( piece, EXPORT_DIR )

	print( f"[build_kit] {len( pieces )} pieces built, {mismatches} size mismatch(es)" )
	print(
		f"[build_kit] roof slope edge is {ROOF_SLOPE:.4f} m; "
		f"BuildModuleDimensions.RoofSlopeMeters is 2.82 "
		f"({ROOF_SLOPE - 2.82:+.4f} m difference)" )
	print(
		"[build_kit] roofs and 45 beams carry their rotation in the mesh, so they measure "
		"across the lean and are exempt from the size table" )
	print(
		"[build_kit] pieces are spread out by object location — clear it (Alt+G) before "
		"exporting by hand, or set LAYOUT_SPACING = 0" )
	return pieces


if __name__ == "__main__":
	create_build_kit()
