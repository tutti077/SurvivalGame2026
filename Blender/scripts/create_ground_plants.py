"""
Ground plant generator — small low-poly scatter props for the biomes.

Run in Blender Scripting workspace, or headless:
  blender --background --python "Blender/scripts/create_ground_plants.py"

What it builds (all real-world metres, origin at the root on Z = 0)
-------------------------------------------------------------------
Clover Hills
  daisy                one upright daisy on a stem + two basal leaves        ~0.40 m  (2.5x life size)
  clover               one upright three-leaf clover                         ~0.30 m  (5x life size)
                       (single plants on purpose — clusters are made by spawning several with varied
                       height / tilt / yaw, not baked into a model)
  weed_sprout          tiny two-to-four leaf seedling                        ~0.06 m
  weed_rosette         flat jagged dandelion-style leaf rosette              ~0.04 m
  weed_dandelion       rosette + one yellow dandelion head                   ~0.16 m
  weed_broadleaf       plantain-style broad leaves                           ~0.08 m
Redwood Forest
  fern_fiddlehead      one curled young shoot, oversized coil + stalk        ~0.80 m  (2.5x)
  fern_young           four short fronds                                     ~0.74 m  (2x)
  fern_full            twelve arching sword-fern fronds from one crown       ~1.26 m
  redwood_sorrel       one oxalis (purple-backed clover leaf)                ~0.32 m  (4.5x)
  mushroom             one red-capped mushroom                               ~0.28 m  (2.5x)
Amber Dunes
  tumbleweed_small     tangle of dry twigs, roughly round                    ~0.45 m
  tumbleweed_large     same, bigger and denser                               ~0.85 m
  dune_grass_tuft      dry amber grass, blades all the way round             ~0.40 m
  dead_shrub           leafless branching twig bush                          ~0.50 m

Every model is flat shaded (faceted, the project's low-poly look) and textured from ONE shared
16-swatch palette (materials/environment/shared/plants_palette.png, point-sampled by pixel_lit):
each face's UVs sit in the centre of one swatch, so every plant is a single draw with one material.
Thin parts (petals, leaves, fronds, ribbons) are single-sided cards — the material renders backfaces.

Vertex colour (exported LINEAR, unused by pixel_lit today — there for a future sway shader, same
layout as grass_clump): R = height 0 at the root → 1 at the top, G = per-part random phase, B = per-model
random, A = 1.

Units & export
--------------
Modelled in metres, FBX exported with the grass / build-kit settings (apply_unit_scale, global_scale 1),
the .vmdl uses import_scale 0.4 to land on the terrain's 40 u/m (written by this script too).
Tumbleweeds keep their origin at the bottom like everything else so they sit on the ground when scattered.

Outputs
-------
  Assets/models/environment/<biome>/groundFoliage/<name>.fbx + <name>.vmdl
  Assets/materials/environment/shared/plants_palette.png + plants.vmat
  Blender/blenderprojects/ground_plants.blend   (all models on a grid for eyeballing)
"""

import math
import os
import random
import struct
import zlib

import bpy
from mathutils import Matrix, Vector

_REPO_ROOT = os.path.abspath( os.path.join( os.path.dirname( os.path.abspath( __file__ ) ), "..", ".." ) )
# Models land in models/environment/<biome>/groundFoliage/ (biome per model in MODELS); the shared palette
# material in materials/environment/shared/ because every biome's plants use it.
MAT_REL_DIR = "materials/environment/shared"
MAT_DIR = os.path.join( _REPO_ROOT, "Assets", *MAT_REL_DIR.split( "/" ) )


def model_rel_dir( biome: str ) -> str:
	return f"models/environment/{biome}/groundFoliage"


def model_dir( biome: str ) -> str:
	return os.path.join( _REPO_ROOT, "Assets", *model_rel_dir( biome ).split( "/" ) )

BLEND_PATH = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "ground_plants.blend" )

WRITE_FBX = True
WRITE_VMDL = True
WRITE_TEXTURE = True
WRITE_BLEND = True
LAYOUT_SPACING = 1.4      # metres between models in the saved .blend (object location only)

MATERIAL_NAME = "plants"
ROOT_SINK = 0.01          # metres stems / roots sit below Z=0 so uneven ground never shows a gap

# --- Palette -------------------------------------------------------------------------------
# 4 x 4 swatches of 4 x 4 texels = 16 x 16 texture. sRGB 0..255.
PALETTE = {
	"leaf_dark":    ( 50, 92, 34 ),
	"leaf":         ( 78, 130, 46 ),
	"leaf_light":   ( 116, 164, 62 ),
	"clover":       ( 62, 124, 50 ),
	"petal":        ( 238, 238, 228 ),
	"petal_shade":  ( 206, 210, 200 ),
	"yellow":       ( 244, 196, 40 ),
	"orange":       ( 222, 150, 34 ),
	"fern_dark":    ( 38, 78, 36 ),
	"fern":         ( 60, 110, 44 ),
	"dry_dark":     ( 118, 86, 52 ),
	"dry":          ( 160, 122, 74 ),
	"dry_light":    ( 214, 180, 112 ),
	"cap_red":      ( 170, 68, 40 ),
	"cream":        ( 226, 214, 190 ),
	"sorrel_back":  ( 112, 70, 96 ),
}
PALETTE_KEYS = list( PALETTE.keys() )
SWATCH_TEXELS = 4
PALETTE_GRID = 4
PALETTE_SIZE = SWATCH_TEXELS * PALETTE_GRID


def _swatch_uv( key: str ) -> tuple:
	index = PALETTE_KEYS.index( key )
	cx = index % PALETTE_GRID
	cy = index // PALETTE_GRID
	u = ( cx + 0.5 ) / PALETTE_GRID
	v = 1.0 - ( cy + 0.5 ) / PALETTE_GRID     # PNG row 0 is the top; Blender v = 0 is the bottom
	return ( u, v )


def write_palette_png( path: str ) -> None:
	rows = []
	for y in range( PALETTE_SIZE ):
		row = bytearray( [ 0 ] )      # filter: none
		for x in range( PALETTE_SIZE ):
			key = PALETTE_KEYS[( y // SWATCH_TEXELS ) * PALETTE_GRID + x // SWATCH_TEXELS]
			row.extend( PALETTE[key] )
		rows.append( bytes( row ) )

	def chunk( tag: bytes, data: bytes ) -> bytes:
		return struct.pack( ">I", len( data ) ) + tag + data + struct.pack( ">I", zlib.crc32( tag + data ) & 0xFFFFFFFF )

	png = b"\x89PNG\r\n\x1a\n"
	png += chunk( b"IHDR", struct.pack( ">IIBBBBB", PALETTE_SIZE, PALETTE_SIZE, 8, 2, 0, 0, 0 ) )
	png += chunk( b"IDAT", zlib.compress( b"".join( rows ), 9 ) )
	png += chunk( b"IEND", b"" )
	with open( path, "wb" ) as f:
		f.write( png )
	print( f"[plants] wrote {path}" )


# --- Mesh builder ----------------------------------------------------------------------------

class PlantMesh:
	"""Accumulates verts / faces with one palette swatch and one random phase per face."""

	def __init__( self, seed: int ):
		self.rng = random.Random( seed )
		self.model_random = self.rng.random()
		self.verts = []
		self.faces = []
		self.face_swatch = []
		self.face_phase = []
		self._phase = 0.0

	def new_part( self ) -> None:
		self._phase = self.rng.random()

	def add_vert( self, p ) -> int:
		self.verts.append( Vector( p ) )
		return len( self.verts ) - 1

	def add_face( self, indices, swatch: str ) -> None:
		self.faces.append( tuple( indices ) )
		self.face_swatch.append( swatch )
		self.face_phase.append( self._phase )

	def add_poly( self, points, swatch: str ) -> None:
		self.add_face( [ self.add_vert( p ) for p in points ], swatch )


def _perp( d: Vector ) -> Vector:
	up = Vector( ( 0, 0, 1 ) ) if abs( d.z ) < 0.95 else Vector( ( 1, 0, 0 ) )
	return d.cross( up ).normalized()


def _bezier( p0, p1, p2, t ):
	a = p0.lerp( p1, t )
	b = p1.lerp( p2, t )
	return a.lerp( b, t )


def tube( m: PlantMesh, points, radii, swatch: str, sides: int = 3, cap: bool = True ) -> None:
	"""Thin prism along a polyline (stems, twigs, rachis). `radii` per point."""
	m.new_part()
	rings = []
	for i, p in enumerate( points ):
		if i == 0:
			d = ( points[1] - points[0] )
		elif i == len( points ) - 1:
			d = ( points[-1] - points[-2] )
		else:
			d = ( points[i + 1] - points[i - 1] )
		d.normalize()
		a = _perp( d )
		b = d.cross( a ).normalized()
		ring = []
		for s in range( sides ):
			ang = s / sides * math.tau
			ring.append( m.add_vert( p + ( a * math.cos( ang ) + b * math.sin( ang ) ) * radii[i] ) )
		rings.append( ring )
	for i in range( len( rings ) - 1 ):
		r0, r1 = rings[i], rings[i + 1]
		for s in range( sides ):
			n = ( s + 1 ) % sides
			m.add_face( ( r0[s], r0[n], r1[n], r1[s] ), swatch )
	if cap and radii[-1] > 0.0:
		m.add_face( tuple( reversed( rings[-1] ) ), swatch )


def curve_points( p0: Vector, p1: Vector, p2: Vector, segments: int ):
	return [ _bezier( p0, p1, p2, i / segments ) for i in range( segments + 1 ) ]


def leaf( m: PlantMesh, base: Vector, direction: Vector, length: float, width: float, swatch: str,
		  droop: float = 0.3, segments: int = 3, jagged: float = 0.0, roll: float = 0.0 ) -> None:
	"""Flat pointed leaf from `base` along `direction` (its pitch included); tip droops by `droop` * length."""
	m.new_part()
	d = direction.normalized()
	side = _perp( d )
	if roll:
		side = ( Matrix.Rotation( roll, 3, d ) @ side ).normalized()
	spine = []
	for i in range( segments + 1 ):
		t = i / segments
		p = base + d * ( length * t )
		p.z -= droop * length * t * t
		spine.append( p )
	left = []
	right = []
	for i in range( 1, segments ):
		t = i / segments
		w = width * 0.5 * math.sin( math.pi * min( 1.0, t * 1.15 ) )
		if jagged and i % 2 == 1:
			w *= ( 1.0 + jagged )
		elif jagged:
			w *= ( 1.0 - jagged * 0.6 )
		left.append( spine[i] + side * w )
		right.append( spine[i] - side * w )
	b = m.add_vert( spine[0] )
	tip = m.add_vert( spine[-1] )
	li = [ m.add_vert( p ) for p in left ]
	ri = [ m.add_vert( p ) for p in right ]
	m.add_face( ( b, ri[0], li[0] ), swatch )
	for i in range( len( li ) - 1 ):
		m.add_face( ( li[i], ri[i], ri[i + 1], li[i + 1] ), swatch )
	m.add_face( ( li[-1], ri[-1], tip ), swatch )


# --- Plant parts ---------------------------------------------------------------------------

def clover_leaf( m: PlantMesh, top: Vector, size: float, yaw: float, swatch: str, back_swatch: str = None ) -> None:
	"""Three heart-shaped leaflets around the petiole tip. A heart = 7-point fan, notch at the outer edge."""
	for k in range( 3 ):
		m.new_part()
		ang = yaw + k * math.tau / 3
		out = Vector( ( math.cos( ang ), math.sin( ang ), 0 ) )
		side = Vector( ( -out.y, out.x, 0 ) )
		tilt = 0.25 + m.rng.uniform( -0.1, 0.1 )     # leaflets cup up a little
		def at( r, s ):
			p = top + out * ( r * size ) + side * ( s * size )
			p.z += r * size * tilt
			return p
		outline = [ at( 0.0, 0.0 ), at( 0.45, -0.38 ), at( 0.85, -0.36 ), at( 1.0, -0.14 ),
					at( 0.82, 0.0 ), at( 1.0, 0.14 ), at( 0.85, 0.36 ), at( 0.45, 0.38 ) ]
		idx = [ m.add_vert( p ) for p in outline ]
		for i in range( 1, len( idx ) - 1 ):
			m.add_face( ( idx[0], idx[i], idx[i + 1] ), swatch )
		if back_swatch:
			# A second, slightly lower copy facing down shows the oxalis' purple underside.
			low = [ m.add_vert( p - Vector( ( 0, 0, 0.002 ) ) ) for p in outline ]
			for i in range( 1, len( low ) - 1 ):
				m.add_face( ( low[0], low[i + 1], low[i] ), back_swatch )


def clover( m: PlantMesh, root: Vector, height: float, size: float, swatch: str = "clover", back_swatch: str = None,
			lean_amount: float = 0.25 ) -> None:
	lean = Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), 0 ) ) * height * lean_amount
	top = root + Vector( ( 0, 0, height ) ) + lean
	mid = root + Vector( ( 0, 0, height * 0.6 ) ) + lean * 0.2
	tube( m, curve_points( root - Vector( ( 0, 0, ROOT_SINK ) ), mid, top, 2 ), [ 0.0025, 0.002, 0.0015 ], "leaf", sides=3, cap=False )
	clover_leaf( m, top, size, m.rng.uniform( 0, math.tau ), swatch, back_swatch )


def daisy( m: PlantMesh, root: Vector, height: float, head_radius: float, lean_amount: float = 0.0 ) -> None:
	lean = Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), 0 ) ).normalized() * height * lean_amount
	top = root + Vector( ( 0, 0, height ) ) + lean
	mid = root + Vector( ( 0, 0, height * 0.55 ) )
	tube( m, curve_points( root - Vector( ( 0, 0, ROOT_SINK ) ), mid, top, 3 ), [ 0.003, 0.0025, 0.002, 0.002 ], "leaf", sides=3, cap=False )

	# Head faces up, tipped a little toward the lean.
	normal = ( Vector( ( 0, 0, 1 ) ) + lean.normalized() * 0.35 ).normalized() if lean.length > 0 else Vector( ( 0, 0, 1 ) )
	a = _perp( normal )
	b = normal.cross( a ).normalized()
	m.new_part()
	centre_r = head_radius * 0.32
	dome = top + normal * centre_r * 0.45
	ring = [ m.add_vert( top + ( a * math.cos( i / 6 * math.tau ) + b * math.sin( i / 6 * math.tau ) ) * centre_r ) for i in range( 6 ) ]
	c = m.add_vert( dome )
	for i in range( 6 ):
		m.add_face( ( c, ring[i], ring[( i + 1 ) % 6] ), "yellow" if i % 2 == 0 else "orange" )

	petals = 13
	phase = m.rng.uniform( 0, math.tau )
	for i in range( petals ):
		m.new_part()
		ang = phase + i / petals * math.tau
		out = a * math.cos( ang ) + b * math.sin( ang )
		side = out.cross( normal ).normalized()
		base = top + out * ( centre_r * 0.8 )
		tip = top + out * head_radius - normal * head_radius * 0.12
		w = head_radius * 0.17
		mid_p = base.lerp( tip, 0.55 )
		m.add_poly( ( base, mid_p - side * w, tip, mid_p + side * w ), "petal" if i % 3 else "petal_shade" )


def leaf_rosette( m: PlantMesh, root: Vector, count: int, length: tuple, width: tuple, pitch: tuple, swatch_choices,
				  droop: float = 0.3, jagged: float = 0.0, segments: int = 3 ) -> None:
	phase = m.rng.uniform( 0, math.tau )
	for i in range( count ):
		ang = phase + i / count * math.tau + m.rng.uniform( -0.25, 0.25 )
		p = math.radians( m.rng.uniform( *pitch ) )
		d = Vector( ( math.cos( ang ) * math.cos( p ), math.sin( ang ) * math.cos( p ), math.sin( p ) ) )
		leaf( m, root + Vector( ( 0, 0, 0.003 ) ), d, m.rng.uniform( *length ), m.rng.uniform( *width ),
			  m.rng.choice( swatch_choices ), droop=droop, segments=segments, jagged=jagged )


def fern_frond( m: PlantMesh, root: Vector, yaw: float, length: float, rise: float, pinna_pairs: int,
				pinna_len: float, swatch: str ) -> None:
	"""Arching frond: a quadratic rachis that rises then bends out and down, paired triangular pinnae."""
	out = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
	p0 = root - Vector( ( 0, 0, ROOT_SINK ) )
	p1 = root + out * ( length * 0.35 ) + Vector( ( 0, 0, length * rise ) )
	p2 = root + out * ( length * 0.85 ) + Vector( ( 0, 0, length * rise * 0.35 ) )
	segments = 5
	tube( m, curve_points( p0, p1, p2, segments ), [ 0.006, 0.005, 0.004, 0.003, 0.002, 0.001 ], "fern_dark", sides=3, cap=False )

	m.new_part()
	start_t = 0.18
	for i in range( pinna_pairs ):
		t = start_t + ( 1.0 - start_t ) * ( i / max( 1, pinna_pairs - 1 ) ) * 0.97
		p = _bezier( p0, p1, p2, t )
		tangent = ( _bezier( p0, p1, p2, min( 1.0, t + 0.02 ) ) - _bezier( p0, p1, p2, max( 0.0, t - 0.02 ) ) ).normalized()
		side = tangent.cross( Vector( ( 0, 0, 1 ) ) ).normalized()
		# Longest in the lower-middle, tapering to the tip — the sword-fern outline.
		taper = math.sin( math.pi * min( 1.0, ( t - start_t ) / ( 1.0 - start_t ) * 0.92 + 0.08 ) ) ** 0.7
		plen = pinna_len * taper
		pw = plen * 0.5
		for sgn in ( 1.0, -1.0 ):
			dir_out = ( side * sgn + tangent * 0.45 ).normalized()
			dir_out.z -= 0.15     # pinnae droop off the rachis
			tip = p + dir_out * plen
			back = p - tangent * pw * 0.5
			fwd = p + tangent * pw
			swatch_here = swatch if ( i + ( sgn > 0 ) ) % 3 else "fern_dark"
			m.add_poly( ( back, tip, fwd ) if sgn > 0 else ( fwd, tip, back ), swatch_here )
	# Tip spear.
	tip_dir = ( p2 - _bezier( p0, p1, p2, 0.95 ) ).normalized()
	side = tip_dir.cross( Vector( ( 0, 0, 1 ) ) ).normalized()
	w = pinna_len * 0.12
	m.add_poly( ( p2 - side * w, p2 + tip_dir * pinna_len * 0.6, p2 + side * w ), swatch )


def fiddlehead( m: PlantMesh, root: Vector, height: float, coil_r: float, yaw: float, stem_r: float ) -> None:
	"""Young frond: a stalk rising into a flattened spiral coil. The coil starts a little thicker than the stalk."""
	out = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
	pts = [ root - Vector( ( 0, 0, ROOT_SINK ) ), root + Vector( ( 0, 0, height * 0.5 ) ) + out * coil_r * 0.2 ]
	top = root + Vector( ( 0, 0, height - coil_r ) ) + out * coil_r * 0.6
	pts.append( top )
	turns = 1.4
	steps = 14
	c = top + out * coil_r
	for i in range( 1, steps + 1 ):
		t = i / steps
		theta = t * turns * math.tau      # 0 = the stalk top, then up, over toward `out`, down and back under
		r = coil_r * ( 1.0 - 0.75 * t )
		pts.append( c + ( -out * math.cos( theta ) + Vector( ( 0, 0, 1 ) ) * math.sin( theta ) ) * r )
	radii = [ stem_r * 1.15, stem_r, stem_r ] + [ stem_r * ( 1.4 - 0.9 * ( i / steps ) ) for i in range( 1, steps + 1 ) ]
	tube( m, pts, radii, "leaf", sides=6 )


def mushroom( m: PlantMesh, root: Vector, height: float, cap_r: float ) -> None:
	lean = Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), 0 ) ) * height * 0.15
	top = root + Vector( ( 0, 0, height ) ) + lean
	tube( m, [ root - Vector( ( 0, 0, ROOT_SINK ) ), root + Vector( ( 0, 0, height * 0.5 ) ) + lean * 0.4, top ],
		  [ cap_r * 0.22, cap_r * 0.18, cap_r * 0.2 ], "cream", sides=5, cap=False )
	m.new_part()
	sides = 7
	rim = [ top + Vector( ( math.cos( i / sides * math.tau ) * cap_r, math.sin( i / sides * math.tau ) * cap_r, -cap_r * 0.15 ) ) for i in range( sides ) ]
	shoulder = [ top + Vector( ( math.cos( ( i + 0.5 ) / sides * math.tau ) * cap_r * 0.7, math.sin( ( i + 0.5 ) / sides * math.tau ) * cap_r * 0.7, cap_r * 0.35 ) ) for i in range( sides ) ]
	peak = top + Vector( ( 0, 0, cap_r * 0.55 ) )
	ri = [ m.add_vert( p ) for p in rim ]
	si = [ m.add_vert( p ) for p in shoulder ]
	pk = m.add_vert( peak )
	under = m.add_vert( top )
	for i in range( sides ):
		n = ( i + 1 ) % sides
		m.add_face( ( ri[i], ri[n], si[i] ), "cap_red" )
		m.add_face( ( si[i], ri[n], si[n] ), "cap_red" )
		m.add_face( ( si[i], si[n], pk ), "cap_red" )
		m.add_face( ( under, ri[n], ri[i] ), "cream" )


def twig_ribbon( m: PlantMesh, points, width: float, swatch: str ) -> None:
	"""Thin flat strip along a polyline — the cheapest readable twig for a tangle."""
	m.new_part()
	axis = Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ) ) ).normalized()
	left = []
	right = []
	for i, p in enumerate( points ):
		d = ( points[min( i + 1, len( points ) - 1 )] - points[max( i - 1, 0 )] ).normalized()
		s = d.cross( axis )
		if s.length < 1e-4:
			s = _perp( d )
		s.normalize()
		w = width * ( 1.0 - 0.6 * i / ( len( points ) - 1 ) )
		left.append( m.add_vert( p + s * w * 0.5 ) )
		right.append( m.add_vert( p - s * w * 0.5 ) )
	for i in range( len( points ) - 1 ):
		m.add_face( ( left[i], right[i], right[i + 1], left[i + 1] ), swatch )


def tumbleweed( m: PlantMesh, radius: float, arcs: int ) -> None:
	"""Random arcs on and inside a slightly squashed sphere, centred `radius` above the ground."""
	centre = Vector( ( 0, 0, radius * 0.9 ) )
	squash = Vector( ( 1.0, 1.0, 0.9 ) )
	for _ in range( arcs ):
		a = Vector( ( m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ) ) ).normalized()
		b = Vector( ( m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ) ) )
		b = ( b - a * a.dot( b ) ).normalized()
		r = radius * m.rng.uniform( 0.55, 1.0 )
		span = m.rng.uniform( 0.6, 1.5 )
		start = m.rng.uniform( 0, math.tau )
		steps = 5
		pts = []
		for i in range( steps + 1 ):
			ang = start + span * i / steps
			wobble = 1.0 + m.rng.uniform( -0.08, 0.08 )
			p = ( a * math.cos( ang ) + b * math.sin( ang ) ) * r * wobble
			pts.append( centre + Vector( ( p.x * squash.x, p.y * squash.y, p.z * squash.z ) ) )
		twig_ribbon( m, pts, radius * m.rng.uniform( 0.035, 0.055 ), m.rng.choice( ( "dry", "dry", "dry_dark", "dry_light" ) ) )
	# A few spokes through the middle so it does not look hollow.
	for _ in range( max( 3, arcs // 6 ) ):
		d = Vector( ( m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ), m.rng.gauss( 0, 1 ) ) ).normalized()
		pts = [ centre - d * radius * 0.8, centre + d * radius * 0.05, centre + d * radius * 0.85 ]
		twig_ribbon( m, pts, radius * 0.05, "dry_dark" )


def grass_tuft( m: PlantMesh, blades: int, height: tuple, spread: float, swatches ) -> None:
	for i in range( blades ):
		m.new_part()
		ang = i / blades * math.tau + m.rng.uniform( -0.3, 0.3 )
		out = Vector( ( math.cos( ang ), math.sin( ang ), 0 ) )
		side = Vector( ( -out.y, out.x, 0 ) )
		h = m.rng.uniform( *height )
		base = out * m.rng.uniform( 0, spread ) - Vector( ( 0, 0, ROOT_SINK ) )
		lean = out * h * m.rng.uniform( 0.25, 0.6 )
		mid = base + Vector( ( 0, 0, h * 0.55 ) ) + lean * 0.3
		tip = base + Vector( ( 0, 0, h ) ) + lean
		tip.z -= h * m.rng.uniform( 0.0, 0.25 )     # dry blades flop
		w = h * 0.06
		m.add_poly( ( base - side * w, base + side * w, mid + side * w * 0.6, tip, mid - side * w * 0.6 ), m.rng.choice( swatches ) )


def branch( m: PlantMesh, start: Vector, direction: Vector, length: float, radius: float, depth: int ) -> None:
	end = start + direction * length
	bend = start.lerp( end, 0.5 ) + Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), m.rng.uniform( -0.3, 0.3 ) ) ) * length * 0.12
	tube( m, curve_points( start, bend, end, 2 ), [ radius, radius * 0.8, radius * 0.55 ], m.rng.choice( ( "dry_dark", "dry" ) ), sides=3 )
	if depth <= 0:
		return
	for _ in range( m.rng.choice( ( 2, 2, 3 ) ) ):
		d = ( direction + Vector( ( m.rng.uniform( -0.9, 0.9 ), m.rng.uniform( -0.9, 0.9 ), m.rng.uniform( -0.1, 0.5 ) ) ) ).normalized()
		branch( m, end, d, length * m.rng.uniform( 0.5, 0.75 ), radius * 0.6, depth - 1 )


# --- Models ------------------------------------------------------------------------------

def _scatter_points( rng: random.Random, count: int, radius: float, min_gap: float ):
	pts = []
	tries = 0
	while len( pts ) < count and tries < 500:
		tries += 1
		r = radius * math.sqrt( rng.random() )
		a = rng.uniform( 0, math.tau )
		p = Vector( ( math.cos( a ) * r, math.sin( a ) * r, 0 ) )
		if all( ( p - q ).length >= min_gap for q in pts ):
			pts.append( p )
	return pts


# Daisy and clover are ONE plant each, upright (no baked lean): height, tilt and yaw come from whatever spawns them,
# so a cluster is three instances side by side, not a model.
def build_daisy( m ):
	daisy( m, Vector(), 0.15, 0.038 )
	leaf_rosette( m, Vector(), 2, ( 0.05, 0.07 ), ( 0.018, 0.024 ), ( 15, 30 ), ( "leaf", "leaf_dark" ) )


def build_clover( m ):
	clover( m, Vector(), 0.06, 0.026, lean_amount=0.0 )


def build_weed_sprout( m ):
	tube( m, [ Vector( ( 0, 0, -ROOT_SINK ) ), Vector( ( 0.003, 0, 0.03 ) ), Vector( ( 0, 0.002, 0.055 ) ) ], [ 0.002, 0.0018, 0.0015 ], "leaf", cap=False )
	top = Vector( ( 0, 0.002, 0.055 ) )
	for i in range( m.rng.choice( ( 2, 3, 4 ) ) ):
		ang = i * math.pi + m.rng.uniform( -0.3, 0.3 ) if i < 2 else m.rng.uniform( 0, math.tau )
		d = Vector( ( math.cos( ang ), math.sin( ang ), 0.5 ) )
		leaf( m, top, d, m.rng.uniform( 0.022, 0.03 ), 0.014, "leaf_light", droop=0.15, segments=2 )


def build_weed_rosette( m ):
	leaf_rosette( m, Vector(), 8, ( 0.07, 0.11 ), ( 0.02, 0.028 ), ( 5, 22 ), ( "leaf", "leaf_dark" ), droop=0.25, jagged=0.6, segments=5 )


def build_weed_dandelion( m ):
	build_weed_rosette( m )
	top = Vector( ( 0.01, 0.005, 0.15 ) )
	tube( m, curve_points( Vector( ( 0, 0, -ROOT_SINK ) ), Vector( ( 0, 0, 0.09 ) ), top, 3 ), [ 0.0028, 0.0025, 0.0022, 0.002 ], "leaf_light", cap=False )
	# Flower = two stacked rings of short yellow petals, a little domed.
	for ring, (r, z, n) in enumerate( ( ( 0.02, 0.0, 10 ), ( 0.013, 0.005, 7 ) ) ):
		m.new_part()
		for i in range( n ):
			ang = ( i + ring * 0.5 ) / n * math.tau
			out = Vector( ( math.cos( ang ), math.sin( ang ), 0 ) )
			side = Vector( ( -out.y, out.x, 0 ) )
			base = top + Vector( ( 0, 0, z ) )
			tip = base + out * r + Vector( ( 0, 0, 0.004 ) )
			m.add_poly( ( base - side * 0.003, tip, base + side * 0.003 ), "yellow" if i % 2 else "orange" )
	m.add_poly( [ top + Vector( ( math.cos( i / 5 * math.tau ) * 0.006, math.sin( i / 5 * math.tau ) * 0.006, 0.009 ) ) for i in range( 5 ) ], "yellow" )


def build_weed_broadleaf( m ):
	leaf_rosette( m, Vector(), 5, ( 0.06, 0.09 ), ( 0.035, 0.05 ), ( 25, 45 ), ( "leaf", "leaf_light", "leaf_dark" ), droop=0.45, segments=4 )


def build_fern_fiddlehead( m ):
	fiddlehead( m, Vector(), 0.32, 0.05, 0.0, 0.012 )


def build_fern_young( m ):
	phase = m.rng.uniform( 0, math.tau )
	for i in range( 4 ):
		fern_frond( m, Vector(), phase + i / 4 * math.tau + m.rng.uniform( -0.3, 0.3 ), m.rng.uniform( 0.38, 0.5 ),
					m.rng.uniform( 1.3, 1.6 ), 9, 0.075, m.rng.choice( ( "fern", "leaf" ) ) )


def build_fern_full( m ):
	phase = m.rng.uniform( 0, math.tau )
	count = 12
	for i in range( count ):
		upright = i % 3 == 0
		fern_frond( m, Vector( ( m.rng.uniform( -0.03, 0.03 ), m.rng.uniform( -0.03, 0.03 ), 0 ) ),
					phase + i / count * math.tau + m.rng.uniform( -0.2, 0.2 ),
					m.rng.uniform( 0.95, 1.2 ) if upright else m.rng.uniform( 0.8, 1.05 ),
					m.rng.uniform( 1.5, 1.8 ) if upright else m.rng.uniform( 0.95, 1.3 ),
					15, 0.14, m.rng.choice( ( "fern", "fern", "leaf" ) ) )


def build_redwood_sorrel( m ):
	clover( m, Vector(), 0.07, 0.03, back_swatch="sorrel_back", lean_amount=0.0 )


def build_mushroom( m ):
	mushroom( m, Vector(), 0.11, 0.05 )


def build_tumbleweed_small( m ):
	tumbleweed( m, 0.24, 26 )


def build_tumbleweed_large( m ):
	tumbleweed( m, 0.45, 44 )


def build_dune_grass( m ):
	grass_tuft( m, 14, ( 0.25, 0.42 ), 0.04, ( "dry", "dry_light", "dry_light", "dry_dark" ) )


def build_dead_shrub( m ):
	for i in range( 4 ):
		ang = i / 4 * math.tau + m.rng.uniform( -0.4, 0.4 )
		d = Vector( ( math.cos( ang ) * 0.5, math.sin( ang ) * 0.5, 1.0 ) ).normalized()
		branch( m, Vector( ( 0, 0, -ROOT_SINK ) ), d, m.rng.uniform( 0.16, 0.22 ), 0.012, 2 )


# ( name, biome folder, seed, builder[, uniform scale] ). The scale is a readability boost on top of real-world size (Mark 2026-10-21:
# clover, daisy and small ferns were invisible past a few metres at life size).
# Every model is ONE plant (Mark 2026-10-21) — clusters are made by spawning several, never baked in.
MODELS = [
	( "daisy", "cloverHills", 101, build_daisy, 2.5 ),
	( "clover", "cloverHills", 103, build_clover, 5.0 ),
	( "weed_sprout", "cloverHills", 105, build_weed_sprout ),
	( "weed_rosette", "cloverHills", 106, build_weed_rosette ),
	( "weed_dandelion", "cloverHills", 107, build_weed_dandelion ),
	( "weed_broadleaf", "cloverHills", 108, build_weed_broadleaf ),
	( "fern_fiddlehead", "redwoodForest", 201, build_fern_fiddlehead, 2.5 ),
	( "fern_young", "redwoodForest", 202, build_fern_young, 2.0 ),
	( "fern_full", "redwoodForest", 203, build_fern_full ),
	( "redwood_sorrel", "redwoodForest", 204, build_redwood_sorrel, 4.5 ),
	( "mushroom", "redwoodForest", 205, build_mushroom, 2.5 ),
	( "tumbleweed_small", "amberDunes", 301, build_tumbleweed_small ),
	( "tumbleweed_large", "amberDunes", 302, build_tumbleweed_large ),
	( "dune_grass_tuft", "amberDunes", 303, build_dune_grass ),
	( "dead_shrub", "amberDunes", 304, build_dead_shrub ),
]


# --- Blender plumbing ----------------------------------------------------------------------

def _reset_scene() -> None:
	bpy.ops.wm.read_factory_settings( use_empty=True )
	scene = bpy.context.scene
	scene.unit_settings.system = "METRIC"
	scene.unit_settings.scale_length = 1.0


def _get_material( palette_path: str ) -> bpy.types.Material:
	mat = bpy.data.materials.get( MATERIAL_NAME )
	if mat is None:
		mat = bpy.data.materials.new( MATERIAL_NAME )
		mat.use_nodes = True
		mat.use_backface_culling = False
		nodes = mat.node_tree.nodes
		bsdf = nodes.get( "Principled BSDF" )
		if bsdf is not None and os.path.exists( palette_path ):
			tex = nodes.new( "ShaderNodeTexImage" )
			tex.image = bpy.data.images.load( palette_path, check_existing=True )
			tex.interpolation = "Closest"
			mat.node_tree.links.new( tex.outputs["Color"], bsdf.inputs["Base Color"] )
			bsdf.inputs["Roughness"].default_value = 0.9
	return mat


def to_object( name: str, m: PlantMesh, material ) -> bpy.types.Object:
	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( [ tuple( v ) for v in m.verts ], [], m.faces )
	mesh.update()
	mesh.validate()

	for poly in mesh.polygons:
		poly.use_smooth = False

	zs = [ v.z for v in m.verts ]
	z_top = max( max( zs ), 1e-4 )

	uv_layer = mesh.uv_layers.new( name="UVMap" )
	color_attr = mesh.color_attributes.new( name="Color", type="FLOAT_COLOR", domain="CORNER" )
	for poly in mesh.polygons:
		uv = _swatch_uv( m.face_swatch[poly.index] )
		phase = m.face_phase[poly.index]
		for li in poly.loop_indices:
			uv_layer.data[li].uv = uv
			z = mesh.vertices[mesh.loops[li].vertex_index].co.z
			color_attr.data[li].color = ( max( 0.0, z / z_top ), phase, m.model_random, 1.0 )

	mesh.materials.append( material )
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
		path_mode="STRIP",
		embed_textures=False,
	)

	obj.location = location
	print( f"[plants] exported {path}" )
	return path


VMDL_TEMPLATE = """<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
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
						remaps = [  ]
						use_global_default = true
						global_default_material = "{material}"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						filename = "{fbx}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
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

VMAT_TEXT = """Layer0
{{
	shader "shaders/pixel_lit.shader"
	F_RENDER_BACKFACES 1

	PixelRoughness "0.900"
	PixelFlatShading "1.000"
	TextureColor "{palette}"
}}
"""


def write_vmdl( name: str, biome: str ) -> None:
	path = os.path.join( model_dir( biome ), f"{name}.vmdl" )
	with open( path, "w", newline="\n" ) as f:
		f.write( VMDL_TEMPLATE.format( material=f"{MAT_REL_DIR}/{MATERIAL_NAME}.vmat", fbx=f"{model_rel_dir( biome )}/{name}.fbx" ) )


def write_vmat() -> None:
	path = os.path.join( MAT_DIR, f"{MATERIAL_NAME}.vmat" )
	with open( path, "w", newline="\n" ) as f:
		f.write( VMAT_TEXT.format( palette=f"{MAT_REL_DIR}/plants_palette.png" ) )


def main() -> None:
	_reset_scene()
	os.makedirs( MAT_DIR, exist_ok=True )

	palette_path = os.path.join( MAT_DIR, "plants_palette.png" )
	if WRITE_TEXTURE:
		write_palette_png( palette_path )
		write_vmat()
	material = _get_material( palette_path )

	objects = []
	columns = 6
	for index, ( name, biome, seed, build, *scale ) in enumerate( MODELS ):
		m = PlantMesh( seed )
		build( m )
		if scale:
			m.verts = [ v * scale[0] for v in m.verts ]
		obj = to_object( name, m, material )
		obj.location = ( ( index % columns ) * LAYOUT_SPACING, -( index // columns ) * LAYOUT_SPACING, 0.0 )
		objects.append( ( obj, biome ) )

		xs = [ v.x for v in m.verts ]
		ys = [ v.y for v in m.verts ]
		zs = [ v.z for v in m.verts ]
		tris = sum( len( f ) - 2 for f in m.faces )
		print( f"[plants] {name}: {len( m.verts )} verts, {tris} tris, height {max( zs ):.2f} m, "
			   f"footprint {max( xs ) - min( xs ):.2f} x {max( ys ) - min( ys ):.2f} m" )

	if WRITE_FBX:
		for obj, biome in objects:
			export_fbx( obj, model_dir( biome ) )
	if WRITE_VMDL:
		for obj, biome in objects:
			write_vmdl( obj.name, biome )

	if WRITE_BLEND:
		os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
		bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
		print( f"[plants] saved {BLEND_PATH}" )


if __name__ == "__main__":
	main()
