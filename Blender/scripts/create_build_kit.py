"""
Wood build kit generator — one object per piece id in Assets/data/build_pieces.json.

Run in Blender Scripting workspace, or:
  blender --background --python create_build_kit.py

Builds all 22 structural pieces at their true size, each authored around its own origin, so
you can delete the objects you do not want and save the file as many times as you need. Re-
running is safe: every object is rebuilt from scratch by name. split_build_kit.py writes the
shipping files (one FBX + vmdl per piece, the two wood textures and their vmats).

LOOK (2026-11-16, Mark: "wood slats and a horizontal beam that holds them together. Think
valheim style. 128 texels per meter and low poly"): every piece is built from real boards —
  walls / gables / door   vertical slats (varied widths, small gaps, ragged tops on full walls)
                          held by chamfered cross beams on one face
  floors                  planks on top, chamfered joists underneath
  roofs + hip / valley    thatch: four stepped courses of straw bundles (ragged butts) on two
                          purlins; the corners are the same courses mitred at the fold
                          (Mark 2026-11-16: thatch "will help them seem less like a wood box")
  stairs                  open plank treads between two stringers; winders hang on a newel post
  door leaf               slats, two battens and a Z brace
  beams / posts           chamfered square timber
Three materials: build_wood_plank (boards), build_wood_beam (timber) and build_roof_thatch
(straw), 256 x 256 tiling textures at 128 texels per metre (one tile = 2 m). Each board is UV'd
along its own grain and lands on one of the texture's eight 0.25 m plank bands, so neighbours
differ in tone; thatch is UV'd with its straws running down the slope of every face.
Every piece also gets a _lod1 mesh (the old one-slab shape, same textures) for 100 m+.

Geometry contract — keep in step with Code/Building/BuildModuleDimensions.cs:
  ModuleMeters     2.0     floor / wall module edge
  HalfModule       1.0     the 1 m size variants, and the rise of one stair piece
  ThinMeters       0.25    floor, wall and roof plate thickness (Valheim-chunky; was 0.06)
  BeamMeters       0.25    square section of every post / beam - the same as a plate, so a post
                           sits flush in a wall and a beam flush under a floor
  RoofSlopeMeters  2.8284  sqrt(2*2 + 2*2)
The boards fill exactly the declared size box (the report below checks it), so box colliders,
snap corners and the stacking maths are untouched by the new look.

STAIRS climb 1 m per piece over a 2 m footprint, so two pieces make one 2 m storey and a
flight can turn part way up. Three pieces share that rise: the straight run, and a left and
right quarter-turn winder that both keep the straight run's -X entry face. Stairs, roofs, the
door frame and the triangles collide with their LOD0 mesh (PhysicsMeshFile), so tread gaps and
the doorway are real.

ORIENTATION AND ORIGIN — this is what makes the pieces drop into the prefabs:
  * Z up, meters, 1 unit = 1 m.
  * Origin sits at the centre of the piece's declared size box, so the symmetric BoxCollider
    on the prefab wraps the mesh with no offset.
  * Floors are thin on Z, walls thin on Y with height on Z, beams run along their long axis
    (X for horizontal, Z for vertical).

NOTHING HERE RELIES ON A RUNTIME ROTATION. Every piece is modelled in the orientation it should
appear in at yaw 0 — a roof is already pitched, a 45 degree brace already leans:

  Family      Prefab rotation applied   Baked into the mesh   ModelRenderer child rotation
  Roofs       -45 deg about X           yes                   +45 X  (0.3826834,0,0,0.9238795)
  45 beams    -45 deg about Y           yes                   +45 Y  (0,0.3826834,0,0.9238795)

  -45 degrees about X sends local +Y down, so the roof slab DESCENDS towards +Y: the panel drops
  2 m over a 2 m run towards +Y, the hip is high at (-X, -Y), the valley is low at (+X, +Y).
  -45 degrees about Y sends local +X up, so a brace RISES as it runs towards +X.
  Both families therefore fall outside the size table (they measure across the lean).

WORKFLOW NOTE: pieces are spread out on a grid using the OBJECT location, never baked into
the mesh. The export helper zeroes it for you.
"""

import math
import os
import random
import struct
import zlib

import bpy

# --- kit contract ---
MODULE = 2.0
HALF_MODULE = 1.0
THIN = 0.25
BEAM = 0.25
ROOF_SLOPE = math.sqrt( MODULE * MODULE + MODULE * MODULE )

# --- tweak these ---
DOOR_OPENING_WIDTH = 1.0          # doorway hole, centred on the wall (m)
DOOR_OPENING_HEIGHT = 1.9         # measured up from the wall bottom (m); the pawn is 1.8 m, so a 1.8 m hole jammed him
STAIR_RISE = HALF_MODULE          # climb per stair piece (m) — two pieces make one wall height
STAIR_STEP_COUNT = 4              # 4 steps over a 1 m rise = 0.25 m rise / 0.5 m going (Valheim's step)
STAIR_PLATE = 0.1                 # LOD1 tread / riser plate thickness
SPIRAL_STEP_COUNT = 4             # same rise per step as the straight run, so they read as one flight
DOOR_LEAF_THICKNESS = 0.08        # the swinging leaf; a THIN leaf is a slab, not a door
SPIRAL_TURN_DEGREES = 90.0        # quarter turn inside the 2 x 2 footprint
BUILD_DOOR_LEAF = True            # extra loose leaf to sit in the doorway; not a catalog id
BUILD_REFERENCE = True            # assembled seam checks, in their own collection
LAYOUT_SPACING = 3.5              # grid gap between pieces (m); 0 stacks them all on origin

# --- boards ---
SLAT_WIDTH = 0.235                # target board width; one texture band is 0.25 m
SLAT_GAP = 0.012                  # gap between neighbouring boards
SLAT_RAGGED = 0.035               # how far a full wall's slat tops may fall short of the top (m)
CROSS_DEPTH = 0.11                # cross beam depth on a wall face; slats take the rest of THIN
CROSS_HEIGHT = 0.16               # cross beam height on a wall face
PLANK_THICKNESS = 0.1             # floor planks; joists fill the rest of THIN underneath
JOIST_WIDTH = 0.18
TREAD_THICKNESS = 0.08            # stair treads
STRINGER_DEPTH = 0.26             # stringer height measured square to the run
STRINGER_WIDTH = 0.12
CHAMFER = 0.03                    # bevel on beams, posts, joists and cross beams
NEWEL_SIZE = 0.2
RISER_THICKNESS = 0.04           # closed risers on the winders

# --- thatch (roof slab frame: z = 0 is the slab centre, +THIN/2 its top) ---
THATCH_COURSES = 4                # courses down the 2.83 m slope; stacked panels keep the rhythm
THATCH_STEP = 0.085               # a course is this much thicker at its butt than at its head
THATCH_BASE = -0.02               # underside of the straw; the purlins fill the slab below it
THATCH_TUCK = 0.18                # how far a course runs up under the one above it
THATCH_BUNDLE = 0.4               # bundle width across the roof
THATCH_RAGGED = 0.045             # a bundle butt may stop this much short of its course line
THATCH_SAG = 0.03                 # each end of a bundle top may sit this much under the slab top
THATCH_BUTT_ROUND = 0.035         # bevel on a bundle's outer butt edge
THATCH_EAVE_OVERHANG = 0.35        # the eave course runs this far past the panel edge (m along the slope), so the
                                  # thatch overhangs the wall top instead of stopping mid-wall (Mark 2026-11-16: corners "sunk down")

# --- textures: 128 texels per metre, 256 px tile = 2 m ---
TEXELS_PER_METER = 128
TEXTURE_SIZE = 256
TILE_METERS = TEXTURE_SIZE / TEXELS_PER_METER
TEXTURE_BANDS = 8
BAND_METERS = TILE_METERS / TEXTURE_BANDS
MATERIALS = ( "build_wood_plank", "build_wood_beam", "build_roof_thatch" )
PLANK = 0
TIMBER = 1
THATCH = 2

FLOOR_COLLECTION = "build_kit_floors"
WALL_COLLECTION = "build_kit_walls"
ROOF_COLLECTION = "build_kit_roofs"
STAIR_COLLECTION = "build_kit_stairs"
BEAM_COLLECTION = "build_kit_beams"
EXTRA_COLLECTION = "build_kit_extra"
REFERENCE_COLLECTION = "build_kit_reference"
LOD1_SUFFIX = "_lod1"

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
	"build_wood_45roofInsideCorner": None,    # straw hangs under its surface, like the panel's
	"build_wood_45roofOutsideCorner": None,
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


def _get_material( name: str ) -> bpy.types.Material:
	mat = bpy.data.materials.get( name )
	if mat is None:
		mat = bpy.data.materials.new( name )
	if name.endswith( "thatch" ):
		tone = ( 0.58, 0.48, 0.29, 1.0 )
	elif name.endswith( "plank" ):
		tone = ( 0.42, 0.33, 0.24, 1.0 )
	else:
		tone = ( 0.30, 0.23, 0.17, 1.0 )
	mat.diffuse_color = tone
	return mat


# ---------------------------------------------------------------- vector maths

def _sub( a, b ):
	return ( a[0] - b[0], a[1] - b[1], a[2] - b[2] )


def _dot( a, b ):
	return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross( a, b ):
	return ( a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] )


def _scale( a, s ):
	return ( a[0] * s, a[1] * s, a[2] * s )


def _length( a ):
	return math.sqrt( _dot( a, a ) )


def _normalize( a ):
	length = _length( a )
	return _scale( a, 1.0 / length ) if length > 1e-9 else ( 0.0, 0.0, 0.0 )


# ---------------------------------------------------------------- 2D polygon helpers

def _area( points ):
	area = 0.0
	for i in range( len( points ) ):
		x0, y0 = points[i]
		x1, y1 = points[( i + 1 ) % len( points )]
		area += x0 * y1 - x1 * y0
	return area * 0.5


def _ensure_ccw( points ):
	"""Winding guard for hand-built outlines, so _extrude_polygon_z always gets CCW."""
	return points if _area( points ) > 0.0 else list( reversed( points ) )


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


def _span_rect( x0, x1, y0, y1 ):
	return [ ( x0, y0 ), ( x1, y0 ), ( x1, y1 ), ( x0, y1 ) ]


def _chamfer_rect( width, height, chamfer, center=( 0.0, 0.0 ) ):
	"""Octagon: a rectangle with its four corners bevelled — the section of every timber."""
	hw = width * 0.5
	hh = height * 0.5
	c = min( chamfer, hw * 0.45, hh * 0.45 )
	cx, cy = center
	return [
		( cx - hw + c, cy - hh ), ( cx + hw - c, cy - hh ),
		( cx + hw, cy - hh + c ), ( cx + hw, cy + hh - c ),
		( cx + hw - c, cy + hh ), ( cx - hw + c, cy + hh ),
		( cx - hw, cy + hh - c ), ( cx - hw, cy - hh + c ),
	]


def _clip( points, a, b, c ):
	"""Sutherland-Hodgman: keep the part of a convex polygon where a*x + b*y <= c."""
	out = []
	count = len( points )
	for i in range( count ):
		p = points[i]
		q = points[( i + 1 ) % count]
		dp = a * p[0] + b * p[1] - c
		dq = a * q[0] + b * q[1] - c
		if dp <= 0.0:
			out.append( p )
		if ( dp < 0.0 < dq ) or ( dq < 0.0 < dp ):
			t = dp / ( dp - dq )
			out.append( ( p[0] + ( q[0] - p[0] ) * t, p[1] + ( q[1] - p[1] ) * t ) )
	return out


def _clip_box( points, x0, x1, y0, y1 ):
	for a, b, c in ( ( -1.0, 0.0, -x0 ), ( 1.0, 0.0, x1 ), ( 0.0, -1.0, -y0 ), ( 0.0, 1.0, y1 ) ):
		points = _clip( points, a, b, c )
		if len( points ) < 3:
			return []
	return points


def _split_widths( total, rng, target=SLAT_WIDTH, gap=SLAT_GAP, jitter=0.12 ):
	"""Board spans across `total` metres: varied widths, a gap between, flush at both ends."""
	count = max( 1, round( ( total + gap ) / ( target + gap ) ) )
	raw = [ 1.0 + rng.uniform( -jitter, jitter ) for _ in range( count ) ]
	usable = total - gap * ( count - 1 )
	widths = [ usable * r / sum( raw ) for r in raw ]
	spans = []
	x = 0.0
	for width in widths:
		spans.append( ( x, x + width ) )
		x += width + gap
	return spans


# ---------------------------------------------------------------- parts
# A piece is a list of parts: ( verts, faces, material ). Each part is one board or timber, so
# it gets its own grain direction and texture band when it is UV'd.

def _extrude_polygon_z( points, z_lo, z_hi ):
	"""
	Solid prism from a polygon given counter-clockwise in (x, y), extruded along Z.
	Caps face +/-Z and every side face points out of the polygon.
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


def _prism_z( poly_xy, lo, hi, material ):
	verts, faces = _extrude_polygon_z( _ensure_ccw( poly_xy ), lo, hi )
	return verts, faces, material


def _prism_x( poly_yz, lo, hi, material ):
	"""Prism running along X; the section is drawn in (y, z). A cyclic axis swap keeps the winding."""
	verts, faces = _extrude_polygon_z( _ensure_ccw( poly_yz ), lo, hi )
	return [ ( w, a, b ) for ( a, b, w ) in verts ], faces, material


def _prism_y( poly_zx, lo, hi, material ):
	"""Prism running along Y; the section is drawn in (z, x)."""
	verts, faces = _extrude_polygon_z( _ensure_ccw( poly_zx ), lo, hi )
	return [ ( b, w, a ) for ( a, b, w ) in verts ], faces, material


def _prism_y_from_xz( poly_xz, lo, hi, material ):
	return _prism_y( [ ( z, x ) for ( x, z ) in poly_xz ], lo, hi, material )


def _map_parts( parts, fn, flip=False ):
	out = []
	for verts, faces, material in parts:
		new_faces = [ tuple( reversed( face ) ) for face in faces ] if flip else faces
		out.append( ( [ fn( v ) for v in verts ], new_faces, material ) )
	return out


def _stand_up( parts ):
	"""Rotate +90 deg about X: a Z-thin face-on drawing (x = width, y = height) stands up Y-thin."""
	return _map_parts( parts, lambda v: ( v[0], -v[2], v[1] ) )


def _pitch_x( parts, degrees ):
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return _map_parts( parts, lambda v: ( v[0], v[1] * cos_a - v[2] * sin_a, v[1] * sin_a + v[2] * cos_a ) )


def _pitch_y( parts, degrees ):
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return _map_parts( parts, lambda v: ( v[0] * cos_a + v[2] * sin_a, v[1], -v[0] * sin_a + v[2] * cos_a ) )


def _yaw_z( parts, degrees ):
	angle = math.radians( degrees )
	cos_a = math.cos( angle )
	sin_a = math.sin( angle )
	return _map_parts( parts, lambda v: ( v[0] * cos_a - v[1] * sin_a, v[0] * sin_a + v[1] * cos_a, v[2] ) )


def _mirror_y( parts ):
	"""Flip across the XZ plane; mirroring reverses handedness, so faces are walked backwards."""
	return _map_parts( parts, lambda v: ( v[0], -v[1], v[2] ), flip=True )


def _mirror_x_face( parts ):
	"""Flip a face-on drawing across x = 0 (before it is stood up)."""
	return _map_parts( parts, lambda v: ( -v[0], v[1], v[2] ), flip=True )


# ---------------------------------------------------------------- wall family (face-on, then stood up)
# Face-on frame: x across the wall, y up, z through it. Slats take the -z side, cross beams the +z
# side, together exactly THIN deep.

SLAT_Z = ( -THIN * 0.5, THIN * 0.5 - CROSS_DEPTH )
CROSS_Z = ( THIN * 0.5 - CROSS_DEPTH, THIN * 0.5 )


def _slats( x0, x1, y0, y1, rng, ragged=0.0, keep=None ):
	"""
	Vertical boards filling x0..x1 from y0 up to y1. `ragged` lets each top corner fall short by
	up to that much (one board always reaches y1 so the piece still measures its full height).
	`keep` is an optional polygon clipper (a gable's slope).
	"""
	spans = _split_widths( x1 - x0, rng )
	full = rng.randrange( len( spans ) )
	parts = []
	for index, ( a, b ) in enumerate( spans ):
		drop_l = 0.0 if index == full else rng.uniform( 0.0, ragged )
		drop_r = 0.0 if index == full else rng.uniform( 0.0, ragged )
		poly = [ ( x0 + a, y0 ), ( x0 + b, y0 ), ( x0 + b, y1 - drop_r ), ( x0 + a, y1 - drop_l ) ]
		if keep is not None:
			poly = keep( poly )
		if len( poly ) < 3 or abs( _area( poly ) ) < 0.004:
			continue
		parts.append( _prism_z( poly, SLAT_Z[0], SLAT_Z[1], PLANK ) )
	return parts


def _cross_beam( x0, x1, y_center ):
	"""Chamfered beam across the wall face, holding the slats together."""
	section = _chamfer_rect( CROSS_HEIGHT, CROSS_DEPTH, CHAMFER, ( y_center, ( CROSS_Z[0] + CROSS_Z[1] ) * 0.5 ) )
	return _prism_x( section, x0, x1, TIMBER )


def _cross_beam_clipped( x0, x1, y_center, keep ):
	poly = keep( _span_rect( x0, x1, y_center - CROSS_HEIGHT * 0.5, y_center + CROSS_HEIGHT * 0.5 ) )
	if len( poly ) < 3 or abs( _area( poly ) ) < 0.004:
		return []
	return [ _prism_z( poly, CROSS_Z[0], CROSS_Z[1], TIMBER ) ]


def _beam_heights( height ):
	"""
	Cross beam centres on one shared lattice: half a metre above the piece bottom, then every metre
	(Mark 2026-11-16: "the support beams on the back of the pieces are all misaligned"). A 2 m wall
	gets 0.5 and 1.5, a 1 m wall gets 0.5 — so a 1 m wall stacked on another lands at 1.5, and walls,
	half walls, door panels and gables standing side by side all carry their beams on the same lines.
	Piece-centred, like everything else here.
	"""
	count = max( 1, round( height / HALF_MODULE ) )
	return tuple( -height * 0.5 + HALF_MODULE * ( i + 0.5 ) for i in range( count ) )


def wall_parts( width, height, rng ):
	hw = width * 0.5
	hh = height * 0.5
	parts = _slats( -hw, hw, -hh, hh, rng, ragged=SLAT_RAGGED )
	for y in _beam_heights( height ):
		parts.append( _cross_beam( -hw, hw, y ) )
	return _stand_up( parts )


def gable_parts( tall_on_right: bool, rng ):
	"""
	Triangular gable that closes the space under a 45 degree roof: low at -X, full height at +X
	(mirrored for the other hand). Slats are cut along the slope, so their tops follow the roof.
	"""
	half = MODULE * 0.5
	keep = lambda poly: _clip( poly, -1.0, 1.0, 0.0 )   # keep y <= x
	parts = _slats( -half, half, -half, half, rng, keep=keep )
	for y in _beam_heights( MODULE ):
		parts += _cross_beam_clipped( -half, half, y, keep )
	if not tall_on_right:
		parts = _mirror_x_face( parts )
	return _stand_up( parts )


def door_parts( rng ):
	"""
	Wall with the doorway framed in: a chamfered post either side of the opening, a lintel over
	it, and slat panels with their own cross beams outside the posts. The opening stays exactly
	DOOR_OPENING_WIDTH x DOOR_OPENING_HEIGHT from the wall bottom — the leaf and the pawn need it.
	"""
	half = MODULE * 0.5
	opening_half = DOOR_OPENING_WIDTH * 0.5
	post = 0.12
	post_outer = opening_half + post
	lintel_bottom = -half + DOOR_OPENING_HEIGHT

	parts = []
	for side in ( -1.0, 1.0 ):
		x_in = side * opening_half
		x_out = side * post_outer
		post_center_x = ( x_in + x_out ) * 0.5
		parts.append( _prism_y( _chamfer_rect( THIN, post, CHAMFER, ( 0.0, post_center_x ) ), -half, lintel_bottom, TIMBER ) )

		panel_lo, panel_hi = ( -half, -post_outer ) if side < 0.0 else ( post_outer, half )
		parts += _slats( panel_lo, panel_hi, -half, half, rng )
		for y in _beam_heights( MODULE ):
			parts.append( _cross_beam( panel_lo, panel_hi, y ) )

	lintel = _chamfer_rect( half - lintel_bottom, THIN, CHAMFER * 0.6, ( ( half + lintel_bottom ) * 0.5, 0.0 ) )
	parts.append( _prism_x( lintel, -post_outer, post_outer, TIMBER ) )
	return _stand_up( parts )


def door_leaf_parts( rng ):
	"""Z-braced plank door with a 0.02 m gap all round the doorway."""
	width = DOOR_OPENING_WIDTH - 0.04
	height = DOOR_OPENING_HEIGHT - 0.02
	hw = width * 0.5
	hh = height * 0.5
	face = ( -DOOR_LEAF_THICKNESS * 0.5, 0.005 )
	back = ( 0.005, DOOR_LEAF_THICKNESS * 0.5 )

	parts = []
	for a, b in _split_widths( width, rng, target=0.22 ):
		parts.append( _prism_z( _span_rect( -hw + a, -hw + b, -hh, hh ), face[0], face[1], PLANK ) )

	batten = 0.14
	inset = 0.04
	batten_y = ( -hh + 0.26, hh - 0.26 )
	for y in batten_y:
		parts.append( _prism_z( _span_rect( -hw + inset, hw - inset, y - batten * 0.5, y + batten * 0.5 ), back[0], back[1], TIMBER ) )

	# Z brace from the bottom batten (hinge side) up to the top batten (latch side).
	p0 = ( -hw + inset + 0.06, batten_y[0] + batten * 0.5 )
	p1 = ( hw - inset - 0.06, batten_y[1] - batten * 0.5 )
	direction = _normalize( ( p1[0] - p0[0], p1[1] - p0[1], 0.0 ) )
	normal = ( -direction[1] * 0.055, direction[0] * 0.055 )
	brace = [
		( p0[0] - normal[0], p0[1] - normal[1] ),
		( p1[0] - normal[0], p1[1] - normal[1] ),
		( p1[0] + normal[0], p1[1] + normal[1] ),
		( p0[0] + normal[0], p0[1] + normal[1] ),
	]
	parts.append( _prism_z( brace, back[0], back[1] - 0.006, TIMBER ) )
	return _stand_up( parts )


# ---------------------------------------------------------------- floors

def floor_parts( size_x, size_y, rng, triangle=False ):
	"""
	Planks along X on top, chamfered joists along Y underneath. The triangle floor is the same
	deck cut corner to corner: right angle at (-X, -Y), hypotenuse from (+X, -Y) to (-X, +Y).
	"""
	hx = size_x * 0.5
	hy = size_y * 0.5
	plank_lo = THIN * 0.5 - PLANK_THICKNESS
	keep = ( lambda poly: _clip( poly, 1.0, 1.0, 0.0 ) ) if triangle else None

	parts = []
	for a, b in _split_widths( size_y, rng ):
		poly = _span_rect( -hx, hx, -hy + a, -hy + b )
		if keep is not None:
			poly = keep( poly )
		if len( poly ) < 3 or abs( _area( poly ) ) < 0.004:
			continue
		parts.append( _prism_z( poly, plank_lo, THIN * 0.5, PLANK ) )

	# Same 1 m lattice as the wall beams: half a metre in from the edge, then every metre, so decks of
	# any size laid side by side run their joists on shared lines.
	joist_x = tuple( -hx + HALF_MODULE * ( i + 0.5 ) for i in range( max( 1, round( size_x / HALF_MODULE ) ) ) )
	for x in joist_x:
		if keep is None:
			section = _chamfer_rect( plank_lo + THIN * 0.5, JOIST_WIDTH, CHAMFER, ( ( -THIN * 0.5 + plank_lo ) * 0.5, x ) )
			parts.append( _prism_y( section, -hy, hy, TIMBER ) )
			continue
		poly = keep( _span_rect( x - JOIST_WIDTH * 0.5, x + JOIST_WIDTH * 0.5, -hy, hy ) )
		if len( poly ) >= 3:
			parts.append( _prism_z( poly, -THIN * 0.5, plank_lo, TIMBER ) )
	return parts


# ---------------------------------------------------------------- roofs

def _thatch_courses( rng, detailed=True ):
	"""
	Straw courses down a flat-drawn panel (width X, slope Y descending towards +Y, thickness Z).
	Each course is a wedge: thin at its head, where it runs THATCH_TUCK up under the course above,
	THATCH_STEP thicker at its butt, so the pitched roof reads as stepped layers of thatch. Detailed
	courses are split into bundles across the roof whose butts and tops wander a little.
	"""
	half_slope = ROOF_SLOPE * 0.5
	top = THIN * 0.5
	length = ROOF_SLOPE / THATCH_COURSES
	parts = []
	for course in range( THATCH_COURSES ):
		head = -half_slope + course * length
		line = head + length
		eave = course == THATCH_COURSES - 1
		spans = _split_widths( MODULE, rng, target=THATCH_BUNDLE, gap=0.0, jitter=0.3 ) if detailed else [ ( 0.0, MODULE ) ]
		for a, b in spans:
			if eave:
				butt = line + THATCH_EAVE_OVERHANG
			else:
				butt = line if not detailed else line - rng.uniform( 0.0, THATCH_RAGGED )
			fall = THATCH_STEP / ( butt - head )
			tuck = THATCH_TUCK if course > 0 else 0.0
			if eave:
				# the overhang's underside climbs from the panel edge to the butt: on a roof edge it
				# reads as a tapered eave over the wall top, and on a panel stacked above another it
				# rides inside the lower panel's first course instead of sharing its underside plane
				section = [ ( head - tuck, THATCH_BASE ), ( line, THATCH_BASE ), ( butt, top - THATCH_STEP ) ]
			else:
				section = [ ( head - tuck, THATCH_BASE ), ( butt, THATCH_BASE ) ]
			if detailed:
				section += [ ( butt, top - THATCH_BUTT_ROUND ), ( butt - THATCH_BUTT_ROUND, top - fall * THATCH_BUTT_ROUND ) ]
			else:
				section.append( ( butt, top ) )
			section.append( ( head - tuck, top - THATCH_STEP - fall * tuck ) )
			x0 = -MODULE * 0.5 + a
			x1 = -MODULE * 0.5 + b
			bundle = _prism_x( section, x0, x1, THATCH )
			if detailed:
				# each end of a bundle sags by its own amount, so the top twists a little and the
				# bundles catch the light differently - lumpy straw rather than flat tiles
				sag0 = rng.uniform( 0.0, THATCH_SAG )
				sag1 = rng.uniform( 0.0, THATCH_SAG )
				bundle = _map_parts( [ bundle ], lambda v, x0=x0, x1=x1, s0=sag0, s1=sag1: ( v[0], v[1], v[2] if v[2] <= THATCH_BASE + 1e-6 else v[2] - ( s0 + ( s1 - s0 ) * ( v[0] - x0 ) / ( x1 - x0 ) ) ) )[0]
			parts.append( bundle )
	return parts


def roof_panel_parts( rng, detailed=True ):
	"""
	Straight 45 degree thatch panel: _thatch_courses on two chamfered purlins across the underside
	(LOD1 drops the purlins and the bundle split). Pitched -45 about X so it lies in the pitched
	collider slab, its top on the slab top.
	"""
	parts = _thatch_courses( rng, detailed )
	if detailed:
		half_slope = ROOF_SLOPE * 0.5
		for y in ( -half_slope * 0.5, half_slope * 0.5 ):   # quarter points: stacked panels keep an even purlin spacing
			section = _chamfer_rect( CROSS_HEIGHT, THATCH_BASE + THIN * 0.5, CHAMFER, ( y, ( -THIN * 0.5 + THATCH_BASE ) * 0.5 ) )
			parts.append( _prism_x( section, -MODULE * 0.5, MODULE * 0.5, TIMBER ) )
	return _pitch_x( parts, -45.0 )


def _clip_part( part, normal, offset ):
	"""
	Keep the side of a convex solid where dot( normal, p ) <= offset and cap the cut, so the hip
	and valley can mitre whole thatch bundles and purlins at the fold. None if nothing is left.
	"""
	verts, faces, material = part
	dist = [ _dot( normal, v ) - offset for v in verts ]
	if max( dist ) <= 1e-7:
		return part
	if min( dist ) >= -1e-7:
		return None

	new_verts = []
	kept = {}
	cuts = {}

	def keep( i ):
		if i not in kept:
			kept[i] = len( new_verts )
			new_verts.append( verts[i] )
		return kept[i]

	def cut( i, j ):
		key = ( min( i, j ), max( i, j ) )
		if key not in cuts:
			t = dist[i] / ( dist[i] - dist[j] )
			a, b = verts[i], verts[j]
			cuts[key] = len( new_verts )
			new_verts.append( ( a[0] + ( b[0] - a[0] ) * t, a[1] + ( b[1] - a[1] ) * t, a[2] + ( b[2] - a[2] ) * t ) )
		return cuts[key]

	new_faces = []
	for face in faces:
		out = []
		count = len( face )
		for k in range( count ):
			i = face[k]
			j = face[( k + 1 ) % count]
			if dist[i] <= 1e-7:
				out.append( keep( i ) )
			if ( dist[i] < -1e-7 and dist[j] > 1e-7 ) or ( dist[j] < -1e-7 and dist[i] > 1e-7 ):
				out.append( cut( i, j ) )
		if len( out ) >= 3:
			new_faces.append( tuple( out ) )

	ring = list( cuts.values() ) + [ kept[i] for i in kept if abs( dist[i] ) <= 1e-7 ]
	if len( ring ) >= 3:
		center = _scale( ( sum( new_verts[i][0] for i in ring ), sum( new_verts[i][1] for i in ring ), sum( new_verts[i][2] for i in ring ) ), 1.0 / len( ring ) )
		helper = ( 1.0, 0.0, 0.0 ) if abs( normal[0] ) < 0.9 else ( 0.0, 1.0, 0.0 )
		u_axis = _normalize( _cross( helper, normal ) )
		w_axis = _cross( normal, u_axis )
		ring.sort( key=lambda i: math.atan2( _dot( _sub( new_verts[i], center ), w_axis ), _dot( _sub( new_verts[i], center ), u_axis ) ) )
		new_faces.append( tuple( ring ) )   # counter-clockwise seen from +normal: the cap faces out

	return new_verts, new_faces, material


def roof_corner_parts( rng, valley, detailed=True ):
	"""
	Hip (valley=False) / valley (valley=True) built from the straight panel's own courses, so the
	steps run on round the corner. The panel is turned to descend towards +X — its slab mid-plane
	stays on z = -x, through the module cube's corners, exactly where a straight panel's mid-plane
	runs, so both meet flush (the old folded shell put its TOP surface there and sat THIN / sqrt 2
	under its neighbours) — then cut at the vertical fold x = y: the hip keeps y <= x, the valley
	y >= x. The other half is that half mirrored across the fold.
	  Hip: mid-plane min(-x, -y) - high at (-X, -Y), eaves along x = +1 and y = +1.
	  Valley: mid-plane max(-x, -y) - low at (+X, +Y), a gutter down the fold.
	"""
	panel = _yaw_z( roof_panel_parts( rng, detailed ), -90.0 )
	normal = _normalize( ( 1.0, -1.0, 0.0 ) if valley else ( -1.0, 1.0, 0.0 ) )
	half = [ clipped for clipped in ( _clip_part( part, normal, 0.0 ) for part in panel ) if clipped is not None ]
	return half + _map_parts( half, lambda v: ( v[1], v[0], v[2] ), flip=True )


# ---------------------------------------------------------------- stairs

def _step_heights( count, rise_total ):
	"""Bottom and top of every step, walking up; the last tread lands on the piece ceiling."""
	half_rise = rise_total * 0.5
	rise = rise_total / count
	return [ ( -half_rise + ( i - 1 ) * rise, -half_rise + i * rise ) for i in range( 1, count + 1 ) ]


def stairs_parts( rng ):
	"""
	Straight flight climbing STAIR_RISE over the 2 m run in +X: two chamfer-less stringers at the
	sides, open plank treads between them (two boards per step, grain across the flight).
	"""
	half = MODULE * 0.5
	half_rise = STAIR_RISE * 0.5
	going = MODULE / STAIR_STEP_COUNT
	slope = STAIR_RISE / MODULE
	inner = half - STRINGER_WIDTH

	parts = []
	top_at = lambda x: slope * x + 0.1
	stringer = [
		( -half, top_at( -half ) - STRINGER_DEPTH ), ( half, top_at( half ) - STRINGER_DEPTH ),
		( half, top_at( half ) ), ( -half, top_at( -half ) ),
	]
	stringer = _clip_box( stringer, -half, half, -half_rise, half_rise )
	for y0, y1 in ( ( -half, -inner ), ( inner, half ) ):
		parts.append( _prism_y_from_xz( stringer, y0, y1, TIMBER ) )

	for index, ( _bottom, top ) in enumerate( _step_heights( STAIR_STEP_COUNT, STAIR_RISE ) ):
		back_x = -half + index * going
		for a, b in _split_widths( going, rng, target=0.24, gap=0.015 ):
			parts.append( _prism_z( _span_rect( back_x + a, back_x + b, -inner, inner ), top - TREAD_THICKNESS, top, PLANK ) )
	return parts


def _winder_pivot():
	"""Inside corner of the turn: the corner shared by the entry face and the exit face."""
	half = MODULE * 0.5
	return ( -half, half )


def _winder_point( degrees, radius ):
	"""
	Point `radius` out from the pivot at `degrees` through the turn: 0 deg runs down the entry face
	(x = -1, towards -Y), 90 deg along the exit face (y = +1, towards +X).
	"""
	angle = math.radians( degrees )
	px, py = _winder_pivot()
	return ( px + math.sin( angle ) * radius, py - math.cos( angle ) * radius )


def _winder_sector( degrees_start, degrees_end, inner, outer ):
	"""Annular wedge between two radial lines, its outer edge a polygonal arc (one point per 7.5 deg)."""
	steps = max( 1, math.ceil( ( degrees_end - degrees_start ) / 7.5 ) )
	arc = [ _winder_point( degrees_start + ( degrees_end - degrees_start ) * i / steps, outer ) for i in range( steps + 1 ) ]
	return _ensure_ccw( [ _winder_point( degrees_start, inner ) ] + arc + [ _winder_point( degrees_end, inner ) ] )


def winder_parts( turns_left: bool, detailed: bool = True ):
	"""
	Quarter of a spiral stair: four treads fanned 22.5 deg apart round a newel post standing in the
	inside corner, cut on a MODULE-radius arc. That arc meets the entry face and the exit face at
	their outer corners, so both faces stay a full 2 m wide for the flight that joins them, and the
	far corner of the square (2.83 m out, where nobody walks on a spiral) is left open instead of
	stretching the middle treads into long spikes. Each tread is two radial boards on a bearer that
	runs out from the newel.
	  left   enter on -X heading +X, leave on +Y heading +Y   pivot at (-1, +1)
	  right  enter on -X heading +X, leave on -Y heading -Y   pivot at (-1, -1)
	Every step is closed by a riser board standing on the radial line at its front, and a curved
	stringer climbs the outer arc under the tread ends (Mark 2026-11-16: "There's no vertical pieces
	in the 90 degree stairs, like it's one of those modern open concept stairs").
	`detailed` False is the LOD1: one slab per tread, the risers, the newel; no bearers or stringer.
	"""
	sweep = SPIRAL_TURN_DEGREES / SPIRAL_STEP_COUNT
	inner = NEWEL_SIZE * 0.5
	outer = MODULE
	board_gap_degrees = math.degrees( SLAT_GAP / outer )
	bearer = 0.1
	px, py = _winder_pivot()
	half = MODULE * 0.5

	def radial( local_parts, degrees ):
		"""Local frame (x out from the pivot, y towards a larger angle) turned onto the radial line."""
		direction = _winder_point( degrees, 1.0 )
		yaw = math.degrees( math.atan2( direction[1] - py, direction[0] - px ) )
		# moved onto the pivot, then held inside the module so no side pokes out of the 2 x 2 box
		return _map_parts( _yaw_z( local_parts, yaw ), lambda v: (
			min( max( v[0] + px, -half ), half ), min( max( v[1] + py, -half ), half ), v[2] ) )

	parts = []
	for index, ( bottom, top ) in enumerate( _step_heights( SPIRAL_STEP_COUNT, STAIR_RISE ) ):
		a0 = index * sweep
		a1 = a0 + sweep
		tread_lo = top - TREAD_THICKNESS

		# riser: the front of the step, from the step below (the floor for the first) up under the tread
		riser = _prism_z( _span_rect( inner, outer, 0.0, RISER_THICKNESS ), bottom, tread_lo, PLANK )
		parts += radial( [ riser ], a0 )

		if not detailed:
			parts.append( _prism_z( _winder_sector( a0, a1, inner, outer ), tread_lo, top, PLANK ) )
			continue

		mid = ( a0 + a1 ) * 0.5
		parts.append( _prism_z( _winder_sector( a0, mid - board_gap_degrees * 0.5, inner, outer ), tread_lo, top, PLANK ) )
		parts.append( _prism_z( _winder_sector( mid + board_gap_degrees * 0.5, a1, inner, outer ), tread_lo, top, PLANK ) )

		# Bearer under the tread's centre line, from the newel out to the stringer.
		section = _chamfer_rect( bearer, bearer, CHAMFER * 0.5, ( 0.0, tread_lo - bearer * 0.5 ) )
		parts += radial( [ _prism_x( section, inner, outer - STRINGER_WIDTH, TIMBER ) ], mid )

	if detailed:
		parts += _winder_stringer( inner, outer )

	newel = _chamfer_rect( NEWEL_SIZE, NEWEL_SIZE, CHAMFER, ( px + NEWEL_SIZE * 0.5, py - NEWEL_SIZE * 0.5 ) )
	parts.append( _prism_z( newel, -STAIR_RISE * 0.5, STAIR_RISE * 0.5, TIMBER ) )
	return parts if turns_left else _mirror_y( parts )


def _winder_stringer( inner, outer ):
	"""
	Curved outer stringer: STRINGER_WIDTH thick inside the arc, its top climbing with the turn the
	way the straight flight's stringers climb its run (0.1 m above the nosing line, STRINGER_DEPTH
	deep), cut to the piece's floor and ceiling. One segment per 7.5 degrees of arc.
	"""
	half_rise = STAIR_RISE * 0.5
	segments = max( 1, math.ceil( SPIRAL_TURN_DEGREES / 7.5 ) )

	def top_at( degrees ):
		return min( half_rise, -half_rise + STAIR_RISE * degrees / SPIRAL_TURN_DEGREES + 0.1 )

	def bottom_at( degrees ):
		return max( -half_rise, top_at( degrees ) - STRINGER_DEPTH )

	parts = []
	for i in range( segments ):
		d0 = SPIRAL_TURN_DEGREES * i / segments
		d1 = SPIRAL_TURN_DEGREES * ( i + 1 ) / segments
		verts = []
		for degrees in ( d0, d1 ):
			for radius in ( outer - STRINGER_WIDTH, outer ):
				x, y = _winder_point( degrees, radius )
				verts.append( ( x, y, bottom_at( degrees ) ) )
				verts.append( ( x, y, top_at( degrees ) ) )
		# verts: 0/1 inner d0 lo/hi, 2/3 outer d0 lo/hi, 4/5 inner d1 lo/hi, 6/7 outer d1 lo/hi
		faces = [
			( 1, 3, 7, 5 ),   # top
			( 0, 4, 6, 2 ),   # bottom
			( 2, 6, 7, 3 ),   # outer face
			( 0, 1, 5, 4 ),   # inner face
			( 0, 2, 3, 1 ),   # d0 end
			( 4, 5, 7, 6 ),   # d1 end
		]
		parts.append( ( verts, _outward( verts, faces ), TIMBER ) )
	return parts


def _outward( verts, faces ):
	"""Wind every face of a convex-ish solid so its normal points away from the solid's centre."""
	center = _scale( ( sum( v[0] for v in verts ), sum( v[1] for v in verts ), sum( v[2] for v in verts ) ), 1.0 / len( verts ) )
	out = []
	for face in faces:
		points = [ verts[i] for i in face ]
		face_center = _scale( ( sum( p[0] for p in points ), sum( p[1] for p in points ), sum( p[2] for p in points ) ), 1.0 / len( points ) )
		if _dot( _face_normal( points ), _sub( face_center, center ) ) < 0.0:
			face = tuple( reversed( face ) )
		out.append( face )
	return out


# ---------------------------------------------------------------- timber

def beam_parts( length, vertical ):
	section = _chamfer_rect( BEAM, BEAM, CHAMFER )
	if vertical:
		return [ _prism_z( section, -length * 0.5, length * 0.5, TIMBER ) ]
	return [ _prism_x( section, -length * 0.5, length * 0.5, TIMBER ) ]


def brace_45_parts( length ):
	"""Diagonal brace in final world orientation: leaned -45 degrees about Y, rising towards +X."""
	return _pitch_y( beam_parts( length, False ), -45.0 )


# ---------------------------------------------------------------- LOD1: the old one-slab shapes

def _box( size, center=( 0.0, 0.0, 0.0 ) ):
	sx, sy, sz = size
	cx, cy, cz = center
	return _extrude_polygon_z( _rect( sx, sy, ( cx, cy ) ), cz - sz * 0.5, cz + sz * 0.5 )


def _slab_wall( points, thickness=THIN ):
	verts, faces = _extrude_polygon_z( _ensure_ccw( points ), -thickness * 0.5, thickness * 0.5 )
	return _stand_up( [ ( verts, faces, PLANK ) ] )


def _lod1_door():
	jamb = ( MODULE - DOOR_OPENING_WIDTH ) * 0.5
	header = MODULE - DOOR_OPENING_HEIGHT
	half = MODULE * 0.5
	parts = [
		_prism_z( _rect( jamb, MODULE, ( -( half - jamb * 0.5 ), 0.0 ) ), -THIN * 0.5, THIN * 0.5, PLANK ),
		_prism_z( _rect( jamb, MODULE, ( half - jamb * 0.5, 0.0 ) ), -THIN * 0.5, THIN * 0.5, PLANK ),
		_prism_z( _rect( DOOR_OPENING_WIDTH, header, ( 0.0, half - header * 0.5 ) ), -THIN * 0.5, THIN * 0.5, PLANK ),
	]
	return _stand_up( parts )


def _lod1_gable( tall_on_right ):
	half = MODULE * 0.5
	points = [ ( -half, -half ), ( half, -half ), ( half, half ) ]
	if not tall_on_right:
		points = [ ( -x, y ) for ( x, y ) in reversed( points ) ]
	return _slab_wall( points )


def _lod1_stairs():
	half = MODULE * 0.5
	going = MODULE / STAIR_STEP_COUNT
	parts = []
	for index, ( bottom, top ) in enumerate( _step_heights( STAIR_STEP_COUNT, STAIR_RISE ) ):
		back_x = -half + index * going
		parts.append( _prism_z( _rect( STAIR_PLATE, MODULE, ( back_x + STAIR_PLATE * 0.5, 0.0 ) ), bottom, top - STAIR_PLATE, PLANK ) )
		parts.append( _prism_z( _rect( going, MODULE, ( back_x + going * 0.5, 0.0 ) ), top - STAIR_PLATE, top, PLANK ) )
	return parts


def _lod1_winder( turns_left ):
	return winder_parts( turns_left, detailed=False )


def _lod1_triangle_floor():
	half = MODULE * 0.5
	return [ _prism_z( [ ( -half, -half ), ( half, -half ), ( -half, half ) ], -THIN * 0.5, THIN * 0.5, PLANK ) ]


def _lod1_box( size, material=PLANK ):
	verts, faces = _box( size )
	return [ ( verts, faces, material ) ]


# ---------------------------------------------------------------- UVs: 128 texels / m along the grain

def _part_axes( verts, faces ):
	"""Grain = the part's longest edge; `second` = its longest edge across the grain."""
	edges = set()
	for face in faces:
		for i in range( len( face ) ):
			a, b = face[i], face[( i + 1 ) % len( face )]
			edges.add( ( min( a, b ), max( a, b ) ) )
	ranked = sorted( ( _sub( verts[b], verts[a] ) for a, b in edges ), key=_length, reverse=True )
	grain = _normalize( ranked[0] )
	second = next( ( _normalize( e ) for e in ranked[1:] if abs( _dot( _normalize( e ), grain ) ) < 0.5 ), ( 0.0, 0.0, 1.0 ) )
	return grain, second


def _face_normal( points ):
	nx = ny = nz = 0.0
	for i in range( len( points ) ):
		x0, y0, z0 = points[i]
		x1, y1, z1 = points[( i + 1 ) % len( points )]
		nx += ( y0 - y1 ) * ( z0 + z1 )
		ny += ( z0 - z1 ) * ( x0 + x1 )
		nz += ( x0 - x1 ) * ( y0 + y1 )
	return _normalize( ( nx, ny, nz ) )


def _face_uvs( points, grain, second, u_offset, band ):
	"""
	Project a face onto its own plane with U along the grain, metres / TILE_METERS, then slide V
	so the face sits centred on one plank band of the texture (bands are BAND_METERS tall).
	"""
	normal = _face_normal( points )
	u_axis = _sub( grain, _scale( normal, _dot( grain, normal ) ) )
	if _length( u_axis ) < 0.35:      # end grain: run U across the board instead
		u_axis = _sub( second, _scale( normal, _dot( second, normal ) ) )
	u_axis = _normalize( u_axis )
	v_axis = _cross( normal, u_axis )

	s = [ _dot( p, u_axis ) for p in points ]
	t = [ _dot( p, v_axis ) for p in points ]
	t_min = min( t )
	extent = max( t ) - t_min
	v_base = band / TEXTURE_BANDS + ( BAND_METERS - extent ) * 0.5 / TILE_METERS
	return [ ( si / TILE_METERS + u_offset, ( ti - t_min ) / TILE_METERS + v_base ) for si, ti in zip( s, t ) ]


def _thatch_face_uvs( points, u_offset, v_offset ):
	"""Straws run down the slope of every face (U = world down projected onto it), 128 texels / m."""
	normal = _face_normal( points )
	u_axis = _sub( ( 0.0, 0.0, -1.0 ), _scale( normal, -normal[2] ) )
	if _length( u_axis ) < 0.2:       # a flat face: let the straws run along X
		u_axis = _sub( ( 1.0, 0.0, 0.0 ), _scale( normal, normal[0] ) )
	u_axis = _normalize( u_axis )
	v_axis = _cross( normal, u_axis )
	return [ ( _dot( p, u_axis ) / TILE_METERS + u_offset, _dot( p, v_axis ) / TILE_METERS + v_offset ) for p in points ]


def _merge_with_uvs( parts, rng ):
	verts = []
	faces = []
	materials = []
	uvs = []
	for part_verts, part_faces, material in parts:
		grain, second = _part_axes( part_verts, part_faces )
		u_offset = rng.random()
		band = rng.randrange( TEXTURE_BANDS )
		offset = len( verts )
		verts.extend( part_verts )
		for face in part_faces:
			faces.append( tuple( index + offset for index in face ) )
			materials.append( material )
			points = [ part_verts[i] for i in face ]
			if material == THATCH:
				uvs.append( _thatch_face_uvs( points, u_offset, band / TEXTURE_BANDS ) )
			else:
				uvs.append( _face_uvs( points, grain, second, u_offset, band ) )
	return verts, faces, materials, uvs


# ---------------------------------------------------------------- object build

def _make_object( name, parts, collection, rng ) -> bpy.types.Object:
	_purge_object( name )
	verts, faces, materials, uvs = _merge_with_uvs( parts, rng )

	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( verts, [], faces )
	for material_name in MATERIALS:
		mesh.materials.append( _get_material( material_name ) )

	layer = mesh.uv_layers.new( name="UVMap" )
	for polygon, material, face_uv in zip( mesh.polygons, materials, uvs ):
		polygon.material_index = material
		polygon.use_smooth = False
		for corner, loop_index in enumerate( polygon.loop_indices ):
			layer.data[loop_index].uv = face_uv[corner]

	mesh.validate()
	mesh.update()

	obj = bpy.data.objects.new( name, mesh )
	collection.objects.link( obj )
	return obj


def _rng_for( name ):
	return random.Random( zlib.crc32( name.encode( "utf-8" ) ) )


def build_piece( name, lod0, lod1, collection, location=( 0.0, 0.0, 0.0 ) ):
	"""Build both LOD objects for one piece from its part lists. Returns ( lod0_object, lod1_object )."""
	obj = _make_object( name, lod0, collection, _rng_for( name ) )
	low = _make_object( name + LOD1_SUFFIX, lod1, collection, _rng_for( name + LOD1_SUFFIX ) )

	obj.location = location
	low.location = ( location[0], location[1], location[2] - 3.0 )   # LOD1 parked under its LOD0
	return obj, low


def _kit_rows():
	"""
	(collection, [ (name, lod0, lod1) ]) — one row per family, laid out along +X.
	Geometry is built eagerly here; it is cheap and keeps the table readable.
	"""
	r = _rng_for
	return [
		( FLOOR_COLLECTION, [
			( "build_wood_floor", floor_parts( MODULE, MODULE, r( "floor" ) ), _lod1_box( ( MODULE, MODULE, THIN ) ) ),
			( "build_wood_floor_2m1m", floor_parts( MODULE, HALF_MODULE, r( "floor_2m1m" ) ), _lod1_box( ( MODULE, HALF_MODULE, THIN ) ) ),
			( "build_wood_floor_1m1m", floor_parts( HALF_MODULE, HALF_MODULE, r( "floor_1m1m" ) ), _lod1_box( ( HALF_MODULE, HALF_MODULE, THIN ) ) ),
			( "build_wood_triangleFloor", floor_parts( MODULE, MODULE, r( "triangleFloor" ), triangle=True ), _lod1_triangle_floor() ),
		] ),
		( WALL_COLLECTION, [
			( "build_wood_wall", wall_parts( MODULE, MODULE, r( "wall" ) ), _slab_wall( _rect( MODULE, MODULE ) ) ),
			( "build_wood_wall_2m1m", wall_parts( MODULE, HALF_MODULE, r( "wall_2m1m" ) ), _slab_wall( _rect( MODULE, HALF_MODULE ) ) ),
			( "build_wood_wall_1m1m", wall_parts( HALF_MODULE, HALF_MODULE, r( "wall_1m1m" ) ), _slab_wall( _rect( HALF_MODULE, HALF_MODULE ) ) ),
			( "build_wood_door", door_parts( r( "door" ) ), _lod1_door() ),
			( "build_wood_45wallLeft", gable_parts( True, r( "45wallLeft" ) ), _lod1_gable( True ) ),
			( "build_wood_45wallRight", gable_parts( False, r( "45wallRight" ) ), _lod1_gable( False ) ),
		] ),
		( ROOF_COLLECTION, [
			( "build_wood_45roof", roof_panel_parts( r( "45roof" ) ), roof_panel_parts( r( "45roof_lod1" ), detailed=False ) ),
			( "build_wood_45roofInsideCorner", roof_corner_parts( r( "45roof" ), True ), roof_corner_parts( r( "45roof_lod1" ), True, detailed=False ) ),
			( "build_wood_45roofOutsideCorner", roof_corner_parts( r( "45roof" ), False ), roof_corner_parts( r( "45roof_lod1" ), False, detailed=False ) ),
		] ),
		( STAIR_COLLECTION, [
			( "build_wood_stairs", stairs_parts( r( "stairs" ) ), _lod1_stairs() ),
			( "build_wood_stairsSpiralLeft", winder_parts( True ), _lod1_winder( True ) ),
			( "build_wood_stairsSpiralRight", winder_parts( False ), _lod1_winder( False ) ),
		] ),
		( BEAM_COLLECTION, [
			( "build_wood_horizontalBeam_1m", beam_parts( HALF_MODULE, False ), _lod1_box( ( HALF_MODULE, BEAM, BEAM ), TIMBER ) ),
			( "build_wood_horizontalBeam_2m", beam_parts( MODULE, False ), _lod1_box( ( MODULE, BEAM, BEAM ), TIMBER ) ),
			( "build_wood_verticalBeam_1m", beam_parts( HALF_MODULE, True ), _lod1_box( ( BEAM, BEAM, HALF_MODULE ), TIMBER ) ),
			( "build_wood_verticalBeam_2m", beam_parts( MODULE, True ), _lod1_box( ( BEAM, BEAM, MODULE ), TIMBER ) ),
			( "build_wood_45Beam_1m", brace_45_parts( HALF_MODULE ), _pitch_y( _lod1_box( ( HALF_MODULE, BEAM, BEAM ), TIMBER ), -45.0 ) ),
			( "build_wood_45Beam_2m", brace_45_parts( MODULE ), _pitch_y( _lod1_box( ( MODULE, BEAM, BEAM ), TIMBER ), -45.0 ) ),
		] ),
	]


def piece_definitions():
	"""
	Flat (name, collection, lod0, lod1) list for the whole kit, with no layout applied.
	This is what split_build_kit.py walks to write one file per piece.
	"""
	definitions = []
	for collection_name, entries in _kit_rows():
		for name, lod0, lod1 in entries:
			definitions.append( ( name, collection_name, lod0, lod1 ) )

	if BUILD_DOOR_LEAF:
		leaf_lod1 = _slab_wall( _rect( DOOR_OPENING_WIDTH - 0.04, DOOR_OPENING_HEIGHT - 0.02 ), DOOR_LEAF_THICKNESS )
		definitions.append( ( "build_wood_door_leaf", EXTRA_COLLECTION, door_leaf_parts( _rng_for( "door_leaf" ) ), leaf_lod1 ) )

	return definitions


def _reference_geometry():
	"""
	Assembled seam checks from the SHIPPING geometry: hip and valley with the real roof panel
	butted onto both slope edges, and the gable pair set as one gable. Nothing here ships.
	"""
	panel = roof_panel_parts( _rng_for( "45roof" ) )
	panel_turned = _yaw_z( panel, -90.0 )
	return [
		( "reference_hip_corner", roof_corner_parts( _rng_for( "45roof" ), False ), ( 0.0, 0.0, 0.0 ) ),
		( "reference_hip_panel_x", panel, ( -MODULE, 0.0, 0.0 ) ),
		( "reference_hip_panel_y", panel_turned, ( 0.0, -MODULE, 0.0 ) ),
		( "reference_valley_corner", roof_corner_parts( _rng_for( "45roof" ), True ), ( 5.0 * MODULE, 0.0, 0.0 ) ),
		( "reference_valley_panel_x", panel, ( 6.0 * MODULE, 0.0, 0.0 ) ),
		( "reference_valley_panel_y", panel_turned, ( 5.0 * MODULE, MODULE, 0.0 ) ),
		( "reference_gable_left", gable_parts( True, _rng_for( "45wallLeft" ) ), ( -MODULE * 0.5, 6.0, 0.0 ) ),
		( "reference_gable_right", gable_parts( False, _rng_for( "45wallRight" ) ), ( MODULE * 0.5, 6.0, 0.0 ) ),
	]


# ---------------------------------------------------------------- textures (128 texels / m)

def _png_bytes( pixels, width, height ):
	"""Minimal RGB PNG writer — rows top to bottom, each a list of (r, g, b) bytes."""
	raw = bytearray()
	for row in pixels:
		raw.append( 0 )
		for r, g, b in row:
			raw += bytes( ( r, g, b ) )

	def chunk( tag, data ):
		body = tag + data
		return struct.pack( ">I", len( data ) ) + body + struct.pack( ">I", zlib.crc32( body ) & 0xFFFFFFFF )

	header = struct.pack( ">IIBBBBB", width, height, 8, 2, 0, 0, 0 )
	return b"\x89PNG\r\n\x1a\n" + chunk( b"IHDR", header ) + chunk( b"IDAT", zlib.compress( bytes( raw ), 9 ) ) + chunk( b"IEND", b"" )


def _wood_pixels( tones, seed, grain_strength, seam_dark, knots ):
	"""
	256 x 256 board texture: eight 32 px bands (one 0.25 m board each), grain running along U.
	Everything wraps — grain wobble uses whole periods across the width, bands stack exactly —
	so the tile repeats every 2 m with no seam. Values are quantised to a few steps per board so
	the point-filtered texels read as painted pixels, not noise.
	"""
	rng = random.Random( seed )
	size = TEXTURE_SIZE
	band_px = size // TEXTURE_BANDS
	order = list( range( TEXTURE_BANDS ) )
	rng.shuffle( order )

	rows = []
	band_rows = []
	for band in range( TEXTURE_BANDS ):
		base = tones[order[band] % len( tones )]
		row_offsets = []
		value = 0.0
		for _ in range( band_px ):
			value = value * 0.55 + rng.uniform( -1.0, 1.0 )
			row_offsets.append( value )
		wobbles = [ ( rng.randint( 1, 3 ), rng.uniform( 0.0, math.tau ), rng.uniform( 0.6, 1.8 ) ) for _ in range( 2 ) ]
		band_knots = [ ( rng.randrange( size ), rng.randint( 7, band_px - 8 ) ) for _ in range( knots if rng.random() < 0.6 else 0 ) ]
		band_rows.append( ( base, row_offsets, wobbles, band_knots ) )

	for y in range( size ):
		band = y // band_px
		local = y % band_px
		base, row_offsets, wobbles, band_knots = band_rows[band]
		row = []
		for x in range( size ):
			shift = sum( amp * math.sin( math.tau * k * x / size + phase ) for k, phase, amp in wobbles )
			sample = local + shift
			index = int( math.floor( sample ) ) % band_px
			grain = row_offsets[index]
			step = round( grain * 1.5 ) / 1.5          # quantise: a handful of tones per board
			shade = 1.0 + step * grain_strength

			if local == 0:
				shade *= 1.0 - seam_dark
			elif local == band_px - 1:
				shade *= 1.0 - seam_dark * 0.5

			for kx, ky in band_knots:
				dx = min( abs( x - kx ), size - abs( x - kx ) ) / 2.2
				dy = ( local - ky ) / 1.4
				d = dx * dx + dy * dy
				if d < 1.0:
					shade *= 0.72
				elif d < 2.6:
					shade *= 1.06

			row.append( tuple( max( 0, min( 255, int( round( c * shade ) ) ) ) for c in base ) )
		rows.append( row )
	return rows


def _thatch_pixels( seed ):
	"""
	256 x 256 straw: a dark under-layer covered by a few thousand 1 px straws laid along U (down
	the slope once UV'd), 10-48 px long, each in one of a handful of straw tones with a lighter cut
	end at its butt (high U) and a faint drift across V. Everything wraps, so the tile repeats with
	no seam; about a fifth of the under-layer stays visible as the dark gaps between straws.
	"""
	rng = random.Random( seed )
	size = TEXTURE_SIZE
	rows = []
	for _ in range( size ):
		rows.append( [ tuple( int( c * rng.choice( ( 0.9, 1.0, 1.0, 1.08 ) ) ) for c in THATCH_UNDER ) for _ in range( size ) ] )

	for _ in range( int( size * size * 1.6 / 29 ) ):
		y = rng.randrange( size )
		x0 = rng.randrange( size )
		length = rng.randint( 10, 48 )
		tone = rng.choice( THATCH_TONES )
		drift = rng.choice( ( 0, 0, 0, 1, -1 ) )
		for i in range( length ):
			shade = 0.9 + 0.1 * i / length          # darker towards the head, under the straw above
			if i >= length - 2:
				shade = 1.12                         # the cut end
			yy = ( y + drift * i // 16 ) % size
			rows[yy][( x0 + i ) % size] = tuple( min( 255, int( c * shade ) ) for c in tone )
	return rows


PLANK_TONES = ( ( 138, 108, 78 ), ( 128, 100, 72 ), ( 146, 116, 85 ), ( 120, 94, 69 ) )
BEAM_TONES = ( ( 102, 78, 57 ), ( 94, 72, 53 ), ( 110, 85, 62 ) )
THATCH_TONES = ( ( 168, 140, 86 ), ( 156, 128, 77 ), ( 180, 152, 96 ), ( 142, 116, 70 ), ( 160, 138, 92 ) )
THATCH_UNDER = ( 86, 68, 42 )


def write_textures( material_dir: str ) -> list:
	"""Write the three kit textures and their pixel_lit vmats. Returns the paths written."""
	os.makedirs( material_dir, exist_ok=True )
	written = []
	specs = (
		( MATERIALS[PLANK], lambda: _wood_pixels( PLANK_TONES, 11, 0.055, 0.22, 1 ), "0.850", "0.350" ),
		( MATERIALS[TIMBER], lambda: _wood_pixels( BEAM_TONES, 23, 0.075, 0.12, 1 ), "0.850", "0.350" ),
		( MATERIALS[THATCH], lambda: _thatch_pixels( 37 ), "0.950", "0.200" ),
	)
	for name, pixels, roughness, specular in specs:
		png_path = os.path.join( material_dir, f"{name}.png" )
		with open( png_path, "wb" ) as handle:
			handle.write( _png_bytes( pixels(), TEXTURE_SIZE, TEXTURE_SIZE ) )
		vmat_path = os.path.join( material_dir, f"{name}.vmat" )
		with open( vmat_path, "w", newline="\n" ) as handle:
			handle.write(
				"Layer0\n{\n"
				"\tshader \"shaders/pixel_lit.shader\"\n\n"
				f"\tPixelRoughness \"{roughness}\"\n"
				f"\tPixelSpecular \"{specular}\"\n"
				"\tPixelFlatShading \"1.000\"\n"
				f"\tTextureColor \"materials/building/{name}.png\"\n"
				"}\n" )
		written += [ png_path, vmat_path ]
		print( f"[build_kit] wrote {png_path}" )
	return written


# ---------------------------------------------------------------- export / report

def export_piece_fbx( objects, name: str, directory: str ) -> str:
	"""Write LOD0 and LOD1 for one piece into one FBX, both parked on the origin."""
	os.makedirs( directory, exist_ok=True )
	path = os.path.join( directory, f"{name}.fbx" )

	locations = [ tuple( obj.location ) for obj in objects ]
	if bpy.context.object and bpy.context.object.mode != "OBJECT":
		bpy.ops.object.mode_set( mode="OBJECT" )
	bpy.ops.object.select_all( action="DESELECT" )
	for obj in objects:
		obj.hide_set( False )
		obj.location = ( 0.0, 0.0, 0.0 )
		obj.select_set( True )
	bpy.context.view_layer.objects.active = objects[0]

	bpy.ops.export_scene.fbx(
		filepath=path,
		use_selection=True,
		apply_unit_scale=True,
		global_scale=1.0,
		axis_forward="-Z",
		axis_up="Y",
		mesh_smooth_type="FACE",
	)

	for obj, location in zip( objects, locations ):
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
	mesh = obj.data
	center = []
	for axis in range( 3 ):
		values = [ vert.co[axis] for vert in mesh.vertices ]
		center.append( ( max( values ) + min( values ) ) * 0.5 )
	return center


def report_piece( obj: bpy.types.Object, low: bpy.types.Object = None ) -> bool:
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

	low_faces = f", lod1 {len( low.data.polygons )}" if low is not None else ""
	print(
		f"[build_kit] {obj.name}: {len( obj.data.polygons )} faces{low_faces}, "
		f"bounds {spans[0]:.4f} x {spans[1]:.4f} x {spans[2]:.4f} m{verdict}" )
	return matches


def create_build_kit():
	pieces = []
	for row_index, ( collection_name, entries ) in enumerate( _kit_rows() ):
		collection = _get_collection( collection_name )
		for column, ( name, lod0, lod1 ) in enumerate( entries ):
			location = ( column * LAYOUT_SPACING, -row_index * LAYOUT_SPACING, 0.0 )
			pieces.append( build_piece( name, lod0, lod1, collection, location ) )

	if BUILD_DOOR_LEAF:
		extra = _get_collection( EXTRA_COLLECTION )
		for name, _col, lod0, lod1 in piece_definitions():
			if name == "build_wood_door_leaf":
				build_piece( name, lod0, lod1, extra, ( 0.0, LAYOUT_SPACING, 0.0 ) )

	if BUILD_REFERENCE:
		reference = _get_collection( REFERENCE_COLLECTION )
		for name, geometry, location in _reference_geometry():
			offset = ( location[0], location[1] + 3.0 * LAYOUT_SPACING, location[2] )
			_make_object( name, geometry, reference, _rng_for( name ) ).location = offset

	mismatches = sum( 0 if report_piece( obj, low ) else 1 for obj, low in pieces )
	print( f"[build_kit] {len( pieces )} pieces built, {mismatches} size mismatch(es)" )
	return pieces


if __name__ == "__main__":
	create_build_kit()
