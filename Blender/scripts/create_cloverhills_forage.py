"""
Clover Hills forage generator — single forage / scatter plants (one plant per model, never a baked cluster).

Run headless from the repo root:
  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_cloverhills_forage.py"

Follows create_ground_plants.py (and reuses its mesh helpers — that script is imported, never edited):
real-world metres, origin at the root on Z = 0, flat shading, single-sided cards drawn with backfaces,
FBX with the grass / build-kit settings and a .vmdl at import_scale 0.4 (40 u/m).

What it builds (in-world size — oversized on purpose so they read at distance)
------------------------------------------------------------------------------
  wheat_wild1_v2 / 2_v2 / 3_v2  one tillered wild wheat plant: 7 / 9 / 6 stalks fanned from one crown,
                           drooping awned golden heads, a few base blades                 ~0.6-0.8 m across
  nightcrawler1_v2 / 2_v2  one earthworm half out of a small dirt cast, lying flat, dark blue ~0.25 m long
  daisy2                   one daisy plant with two heads at different heights             ~0.40 m
  daisy3                   one daisy, stem bent, head tipped                               ~0.38 m
  hobbits_leaf1 / 2        broad-leaved rosette herb (pipe-weed look) + one pale spike     ~0.45 m
  blueberry_bush1 / 2      low woody shrub, folded alpha leaf cards, blue berry clusters   ~0.80 m x 0.90 m
  mushroom_red1 / 2 / 3    single all-red field mushroom (cap + stem), caps of different size + tilt ~0.22-0.29 m

Texture: ONE 256 x 256 sheet, materials/environment/shared/forage_palette.png (+ its cutout mask
forage_palette_mask.png, the TextureTranslucency channel of the same sheet), on forage.vmat (pixel_lit,
point sampled, F_ALPHA_TEST, backfaces):
  rows   0..127  16 x 8 grid of 16 x 16-texel flat swatches — every non-card face's UVs sit in the centre
                 of one swatch (the create_ground_plants swatch-UV approach), opaque in the mask
  rows 128..255  two 128 x 128 blueberry leaf-sprig tiles (drawn at 64 px, doubled — chunky texels):
                 a twig up the middle (the card's fold line), alternate oval leaves, flat tones, no baked light

Vertex colour (LINEAR, same layout as create_ground_plants / grass_clump): R = height 0 → 1, G = per-part
random phase, B = per-model random, A = 1.

Outputs
-------
  Assets/models/environment/cloverHills/groundFoliage/<name>.fbx + <name>.vmdl
  Assets/materials/environment/shared/forage_palette.png + forage_palette_mask.png + forage.vmat
  Blender/blenderprojects/cloverhills_forage.blend   (all models on a grid)
"""

import math
import os
import random
import struct
import sys
import zlib

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.dont_write_bytecode = True      # no __pycache__ left beside the scripts
sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )
import create_ground_plants as gp      # noqa: E402  (mesh helpers only — its main() never runs on import)
from create_ground_plants import PlantMesh, leaf, curve_points, _perp, _bezier, ROOT_SINK  # noqa: E402

_REPO_ROOT = gp._REPO_ROOT
BIOME = "cloverHills"
MODEL_REL_DIR = f"models/environment/{BIOME}/groundFoliage"
MODEL_DIR = os.path.join( _REPO_ROOT, "Assets", *MODEL_REL_DIR.split( "/" ) )
MAT_REL_DIR = "materials/environment/shared"
MAT_DIR = os.path.join( _REPO_ROOT, "Assets", *MAT_REL_DIR.split( "/" ) )
BLEND_PATH = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "cloverhills_forage.blend" )

MATERIAL_NAME = "forage"
PALETTE_PNG = "forage_palette.png"
MASK_PNG = "forage_palette_mask.png"
LAYOUT_SPACING = 1.3

# --- Palette -------------------------------------------------------------------------------------
# sRGB 0..255. The daisy / leaf entries match plants_palette exactly so daisy2 / daisy3 sit beside daisy.
PALETTE = {
	# daisy (= plants_palette)
	"leaf_dark":    ( 50, 92, 34 ),
	"leaf":         ( 78, 130, 46 ),
	"leaf_light":   ( 116, 164, 62 ),
	"petal":        ( 238, 238, 228 ),
	"petal_shade":  ( 206, 210, 200 ),
	"yellow":       ( 244, 196, 40 ),
	"orange":       ( 222, 150, 34 ),
	# wild wheat — ripe straw and grain
	"straw_dark":   ( 150, 116, 58 ),
	"straw":        ( 192, 156, 84 ),
	"straw_light":  ( 218, 186, 110 ),
	"grain":        ( 206, 162, 74 ),
	"grain_light":  ( 230, 196, 112 ),
	"awn":          ( 236, 214, 150 ),
	# nightcrawler + its cast — saturated dark blue so a worm reads against grass and dirt (Mark 2026-11-02)
	"worm":         ( 30, 54, 168 ),
	"worm_dark":    ( 22, 42, 140 ),
	"worm_band":    ( 44, 76, 192 ),
	"dirt_dark":    ( 70, 52, 38 ),
	"dirt":         ( 94, 72, 50 ),
	"dirt_light":   ( 116, 92, 64 ),
	# hobbit's leaf — broad pale tobacco-green leaves, pale pink-cream flowers
	"herb_dark":    ( 88, 120, 44 ),
	"herb":         ( 112, 146, 56 ),
	"herb_light":   ( 140, 168, 72 ),
	"herb_rib":     ( 166, 184, 106 ),
	"bloom":        ( 236, 220, 210 ),
	"bloom_pink":   ( 220, 186, 186 ),
	# blueberry
	"wood_dark":    ( 76, 52, 42 ),
	"wood":         ( 104, 72, 54 ),
	"berry_dark":   ( 50, 60, 108 ),
	"berry":        ( 74, 90, 146 ),
	"berry_bloom":  ( 104, 122, 166 ),
	"berry_unripe": ( 116, 72, 104 ),
	# red field mushroom — totally red, cap AND stem, a few red tones for shading, no spots (Mark 2026-11-02)
	"cap_dark":     ( 150, 24, 20 ),
	"cap_red":      ( 178, 30, 24 ),
	"cap_mid":      ( 198, 40, 30 ),
	"cap_light":    ( 216, 52, 38 ),
	"gill":         ( 140, 28, 26 ),
	"gill_dark":    ( 116, 22, 22 ),
	"stem":         ( 190, 38, 30 ),
	"stem_shade":   ( 162, 30, 26 ),
}
PALETTE_KEYS = list( PALETTE.keys() )
TEX = 256
SWATCH = 16
SWATCH_COLS = TEX // SWATCH          # 16
SWATCH_ROWS = 8                      # rows 0..127 of the sheet
assert len( PALETTE_KEYS ) <= SWATCH_COLS * SWATCH_ROWS

# Leaf-sprig tiles in the bottom half: ( u0, v0, u1, v1 ) in Blender UV space (v = 0 is the PNG's bottom row).
TILE_PX = 128
TILE_DRAW = 64                       # drawn at 64 px, doubled — chunky texels like the 64 px ground tiles
TILES = {
	"sprig_a": ( 0.0, 0.0, 0.5, 0.5 ),
	"sprig_b": ( 0.5, 0.0, 1.0, 0.5 ),
}
LEAF_TONES = [ ( 54, 92, 42 ), ( 70, 112, 50 ), ( 86, 128, 58 ), ( 100, 140, 66 ) ]   # 4 flat greens, low contrast
LEAF_RIB = ( 112, 148, 76 )
TWIG = ( 104, 72, 54 )


def swatch_uv( key: str ) -> tuple:
	index = PALETTE_KEYS.index( key )
	cx = index % SWATCH_COLS
	cy = index // SWATCH_COLS
	return ( ( cx + 0.5 ) * SWATCH / TEX, 1.0 - ( cy + 0.5 ) * SWATCH / TEX )


# --- Texture -------------------------------------------------------------------------------------

def _draw_sprig( rng: random.Random, leaves_per_side: int, curve: float ):
	"""64 x 64 tile: rgb (uint8) + alpha (bool). A twig from the bottom centre up the middle (the card's fold
	line) with two side twiglets, small alternate oval leaves on all three — dense enough that a card reads
	as a leafy mass, ragged enough that its rectangle never shows."""
	n = TILE_DRAW
	rgb = np.zeros( ( n, n, 3 ), np.uint8 )
	rgb[:] = LEAF_TONES[1]                 # bleed colour under the cutout (mips never pull in black)
	alpha = np.zeros( ( n, n ), bool )
	yy, xx = np.mgrid[0:n, 0:n].astype( np.float32 )

	def leaf_at( x, y, ang_deg, length ):
		width = length * rng.uniform( 0.46, 0.56 )
		ang = math.radians( ang_deg )
		d = np.array( ( math.cos( ang ), -math.sin( ang ) ), np.float32 )     # image space, y down
		cx = x + d[0] * length * 0.55
		cy = y + d[1] * length * 0.55
		lx = ( xx - cx ) * d[0] + ( yy - cy ) * d[1]
		ly = -( xx - cx ) * d[1] + ( yy - cy ) * d[0]
		inside = ( lx / ( length * 0.5 ) ) ** 2 + ( ly / ( width * 0.5 ) ) ** 2 <= 1.0
		inside &= np.abs( ly ) <= ( length * 0.5 - lx ) * 0.9 + 0.5           # slightly pointed tip
		tone = LEAF_TONES[rng.randrange( len( LEAF_TONES ) )]
		rgb[inside] = tone
		rib = inside & ( np.abs( ly ) < 0.5 ) & ( lx < length * 0.3 ) & ( lx > -length * 0.45 )
		rgb[rib] = LEAF_RIB
		alpha[inside] = True

	def line( pts, w ):
		for ( x0, y0 ), ( x1, y1 ) in zip( pts, pts[1:] ):
			steps = int( max( abs( x1 - x0 ), abs( y1 - y0 ) ) * 2 ) + 1
			for s in range( steps + 1 ):
				x = x0 + ( x1 - x0 ) * s / steps
				y = y0 + ( y1 - y0 ) * s / steps
				xi = int( round( x - w * 0.5 ) )
				yi = int( round( y ) )
				if 0 <= yi < n:
					rgb[yi, max( 0, xi ):xi + w] = TWIG
					alpha[yi, max( 0, xi ):xi + w] = True

	def twig_x( y ):                       # y = 0 top .. n-1 bottom
		t = 1.0 - y / ( n - 1 )
		return n * 0.5 + curve * math.sin( t * math.pi ) * n * 0.05

	twigs = []
	main = [ ( twig_x( y ), y ) for y in range( n - 1, 3, -6 ) ]
	twigs.append( ( main, 2 ) )
	# two side twiglets, one each side, angled up and out
	for t, side in ( ( 0.32, -1 ), ( 0.52, 1 ) ):
		y = ( 1.0 - t ) * ( n - 1 )
		x = twig_x( y )
		ang = math.radians( 90 - side * rng.uniform( 42, 54 ) )
		L = n * rng.uniform( 0.30, 0.36 )
		pts = [ ( x + math.cos( ang ) * L * k / 3, y - math.sin( ang ) * L * k / 3 ) for k in range( 4 ) ]
		twigs.append( ( pts, 1 ) )
		for k, sgn in ( ( 1, 1 ), ( 2, -1 ), ( 3, 0 ) ):
			px, py = pts[k]
			la = math.degrees( ang ) + sgn * rng.uniform( 38, 55 )
			leaf_at( px, py, la, n * rng.uniform( 0.17, 0.22 ) )
	# leaves along the main twig, alternate, plus a terminal pair
	for i in range( leaves_per_side * 2 ):
		t = 0.14 + 0.72 * i / ( leaves_per_side * 2 - 1 )
		side = 1 if i % 2 == 0 else -1
		y = ( 1.0 - t ) * ( n - 1 )
		leaf_at( twig_x( y ), y, 90 - side * rng.uniform( 38, 60 ), n * rng.uniform( 0.19, 0.25 ) )
	ytop = n * 0.08
	leaf_at( twig_x( ytop ), ytop, 90 + rng.uniform( -12, 12 ), n * 0.18 )
	for pts, w in twigs:
		line( pts, w )
	# Keep a clear 1 px border so neighbouring tiles / mips never bleed into a card edge.
	alpha[0, :] = alpha[-1, :] = alpha[:, 0] = alpha[:, -1] = False
	xb = int( round( twig_x( n - 1 ) ) )
	alpha[-1, xb - 1:xb + 1] = True       # the twig meets the card bottom
	return rgb, alpha


def build_texture():
	rgb = np.zeros( ( TEX, TEX, 3 ), np.uint8 )
	mask = np.zeros( ( TEX, TEX ), np.uint8 )
	for i, key in enumerate( PALETTE_KEYS ):
		cx, cy = i % SWATCH_COLS, i // SWATCH_COLS
		rgb[cy * SWATCH:( cy + 1 ) * SWATCH, cx * SWATCH:( cx + 1 ) * SWATCH] = PALETTE[key]
	# unused swatch slots: a mid green, opaque
	used = len( PALETTE_KEYS )
	for i in range( used, SWATCH_COLS * SWATCH_ROWS ):
		cx, cy = i % SWATCH_COLS, i // SWATCH_COLS
		rgb[cy * SWATCH:( cy + 1 ) * SWATCH, cx * SWATCH:( cx + 1 ) * SWATCH] = PALETTE["leaf"]
	mask[0:SWATCH * SWATCH_ROWS, :] = 255

	scale = TILE_PX // TILE_DRAW
	for name, seed, per_side, curve in ( ( "sprig_a", 11, 5, 1.0 ), ( "sprig_b", 23, 4, -1.3 ) ):
		t_rgb, t_a = _draw_sprig( random.Random( seed ), per_side, curve )
		t_rgb = t_rgb.repeat( scale, 0 ).repeat( scale, 1 )
		t_a = t_a.repeat( scale, 0 ).repeat( scale, 1 )
		u0, v0, u1, v1 = TILES[name]
		x0 = int( u0 * TEX )
		y0 = int( ( 1.0 - v1 ) * TEX )
		rgb[y0:y0 + TILE_PX, x0:x0 + TILE_PX] = t_rgb
		mask[y0:y0 + TILE_PX, x0:x0 + TILE_PX] = np.where( t_a, 255, 0 )
	return rgb, mask


def write_png( path: str, rgb: np.ndarray ) -> None:
	h, w, _ = rgb.shape
	raw = b"".join( b"\x00" + rgb[y].tobytes() for y in range( h ) )

	def chunk( tag: bytes, data: bytes ) -> bytes:
		return struct.pack( ">I", len( data ) ) + tag + data + struct.pack( ">I", zlib.crc32( tag + data ) & 0xFFFFFFFF )

	png = b"\x89PNG\r\n\x1a\n"
	png += chunk( b"IHDR", struct.pack( ">IIBBBBB", w, h, 8, 2, 0, 0, 0 ) )
	png += chunk( b"IDAT", zlib.compress( raw, 9 ) )
	png += chunk( b"IEND", b"" )
	with open( path, "wb" ) as f:
		f.write( png )
	print( f"[forage] wrote {path}" )


# --- Mesh ----------------------------------------------------------------------------------------

class ForageMesh( PlantMesh ):
	"""PlantMesh + optional explicit per-corner UVs (leaf cards); swatch faces keep the centre-of-swatch UV."""

	def __init__( self, seed: int ):
		super().__init__( seed )
		self.face_uvs = []

	def add_face( self, indices, swatch: str, uvs=None ) -> None:
		super().add_face( indices, swatch )
		self.face_uvs.append( uvs )


def transport_frames( points ):
	"""Parallel-transported ( a, b ) cross-section axes along a polyline. ground_plants' tube picks each ring's axes
	from world up independently, which flips the ring where a stem bends past ~18 degrees from vertical and
	pinches that segment to a sliver (daisy3's bent stem read as broken)."""
	frames = []
	prev_a = None
	for i in range( len( points ) ):
		d = points[min( i + 1, len( points ) - 1 )] - points[max( i - 1, 0 )]
		d.normalize()
		if prev_a is None:
			a = _perp( d )
		else:
			a = prev_a - d * prev_a.dot( d )
			if a.length < 1e-5:
				a = _perp( d )
			a.normalize()
		b = d.cross( a ).normalized()
		frames.append( ( a, b ) )
		prev_a = a
	return frames


def tube( m, points, radii, swatch: str, sides: int = 3, cap: bool = True ) -> None:
	"""Thin prism along a polyline (stems, twigs) — create_ground_plants.tube with transported ring axes."""
	m.new_part()
	rings = []
	for p, ( a, b ), r in zip( points, transport_frames( points ), radii ):
		rings.append( [ m.add_vert( p + ( a * math.cos( s / sides * math.tau ) + b * math.sin( s / sides * math.tau ) ) * r ) for s in range( sides ) ] )
	for i in range( len( rings ) - 1 ):
		r0, r1 = rings[i], rings[i + 1]
		for s in range( sides ):
			n = ( s + 1 ) % sides
			m.add_face( ( r0[s], r0[n], r1[n], r1[s] ), swatch )
	if cap and radii[-1] > 0.0:
		m.add_face( tuple( reversed( rings[-1] ) ), swatch )


def frame( normal: Vector ):
	a = _perp( normal )
	b = normal.cross( a ).normalized()
	return a, b


def lumpy_ring( m, centre: Vector, a: Vector, b: Vector, radius: float, count: int, jitter: float, phase: float = 0.0 ):
	"""Ring of verts with jittered angle + radius (varied face sizes, no primitive silhouette)."""
	out = []
	for i in range( count ):
		ang = phase + ( i + m.rng.uniform( -jitter, jitter ) * 0.5 ) / count * math.tau
		r = radius * ( 1.0 + m.rng.uniform( -jitter, jitter ) * 0.35 )
		out.append( centre + ( a * math.cos( ang ) + b * math.sin( ang ) ) * r )
	return out


def blob( m, centre: Vector, radius: Vector, swatches, sides: int = 6, flat_bottom: bool = False ) -> None:
	"""Faceted irregular lump: two jittered rings + top / bottom points. `radius` is ( rx, ry, rz )."""
	m.new_part()
	phase = m.rng.uniform( 0, math.tau )
	rings = []
	for z, s in ( ( -0.45, 0.85 ), ( 0.25, 0.9 ) ):
		ring = []
		for i in range( sides ):
			ang = phase + ( i + m.rng.uniform( -0.25, 0.25 ) ) / sides * math.tau + z
			k = s * m.rng.uniform( 0.85, 1.12 )
			zz = z * radius.z if not ( flat_bottom and z < 0 ) else -radius.z * 0.15
			ring.append( m.add_vert( centre + Vector( ( math.cos( ang ) * radius.x * k, math.sin( ang ) * radius.y * k, zz ) ) ) )
		rings.append( ring )
	top = m.add_vert( centre + Vector( ( m.rng.uniform( -0.2, 0.2 ) * radius.x, m.rng.uniform( -0.2, 0.2 ) * radius.y, radius.z ) ) )
	bot = m.add_vert( centre + Vector( ( 0, 0, -radius.z * ( 0.2 if flat_bottom else 0.9 ) ) ) )
	r0, r1 = rings
	for i in range( sides ):
		n = ( i + 1 ) % sides
		sw = swatches[i % len( swatches )]
		m.add_face( ( r0[i], r0[n], r1[n], r1[i] ), sw )
		m.add_face( ( r1[i], r1[n], top ), swatches[( i + 1 ) % len( swatches )] )
		m.add_face( ( r0[n], r0[i], bot ), sw )


# --- Wild wheat ---------------------------------------------------------------------------------

def wheat( m, height: float, lean_yaw: float, lean_deg: float, droop_deg: float, head_len: float,
		   root: Vector = None, spikelets: int = 14, steps: int = 16, node_leaves: bool = True,
		   droop_yaw: float = None ) -> None:
	"""One stalk: rises nearly straight, then the top fifth curves smoothly over (heading integrated along the
	length, no kink) and the awned two-row ear carries the curve on down, hanging `droop_deg` past upright.
	`root` offsets the stalk foot and `droop_yaw` turns the nod away from the lean direction (tufts); tufts use
	fewer spikelets / steps and base blades instead of node leaves."""
	root = root if root is not None else Vector()
	droop_yaw = lean_yaw if droop_yaw is None else droop_yaw
	out = Vector( ( math.cos( lean_yaw ), math.sin( lean_yaw ), 0 ) )
	out2 = Vector( ( math.cos( droop_yaw ), math.sin( droop_yaw ), 0 ) )
	up = Vector( ( 0, 0, 1 ) )
	ax_lean = up.cross( out ).normalized()
	ax_droop = up.cross( out2 ).normalized()
	lean = math.radians( lean_deg )
	droop = math.radians( droop_deg )

	def direction( a_lean, a_droop ):     # lean toward `out`, then nod toward `out2` (same plane = plain sum)
		return Matrix.Rotation( a_droop, 3, ax_droop ) @ ( Matrix.Rotation( a_lean, 3, ax_lean ) @ up )

	def droop_at( s ):            # nod angle, s = 0..1 along the stalk (top 28 % only)
		k = max( 0.0, ( s - 0.72 ) / 0.28 )
		return droop * 0.55 * k * k * ( 3 - 2 * k )

	ds = height / steps
	pts = [ Vector( ( root.x, root.y, -ROOT_SINK ) ) ]
	for i in range( steps ):
		t = ( i + 0.5 ) / steps
		pts.append( pts[-1] + direction( lean * t, droop_at( t ) ) * ds )
	radii = [ 0.0065 - 0.0028 * i / steps for i in range( steps + 1 ) ]
	tube( m, pts, radii, "straw", sides=4, cap=False )
	# Nodes: slightly thicker darker knuckles.
	for i in ( steps // 4, steps // 2 ):
		d = ( pts[i + 1] - pts[i - 1] ).normalized()
		tube( m, [ pts[i] - d * 0.009, pts[i] + d * 0.009 ], [ 0.0085, 0.0085 ], "straw_dark", sides=4 )

	# Ear: continues the stalk's curve another droop * 0.45 along its length.
	side = out2.cross( up ).normalized()                 # the ear's flat face is the nodding plane's normal
	count = spikelets
	axis = [ pts[-1] ]
	for i in range( count ):
		axis.append( axis[-1] + direction( lean, droop * 0.55 + droop * 0.45 * ( i + 0.5 ) / count ) * ( head_len / count ) )
	tube( m, axis, [ 0.0035 ] * ( count ) + [ 0.0015 ], "straw_light", sides=3 )
	for i in range( count ):
		m.new_part()
		t = ( i + 0.5 ) / count
		sgn = 1 if i % 2 == 0 else -1
		taper = 1.0 - 0.5 * max( 0.0, t - 0.55 ) / 0.45 - 0.25 * max( 0.0, 0.2 - t ) / 0.2
		base = axis[i]
		hd = ( axis[i + 1] - axis[i] ).normalized()
		face_n = hd.cross( side ).normalized()           # in the bending plane, across the ear
		dir_out = ( hd * 0.8 + face_n * sgn * 0.6 ).normalized()
		L = 0.036 * taper
		W = 0.017 * taper
		Tk = 0.012 * taper
		tip = base + dir_out * L
		mid = base.lerp( tip, 0.5 )
		sd = dir_out.cross( side ).normalized()
		pts4 = [ base, mid + sd * W, tip, mid - sd * W ]
		front = m.add_vert( mid + side * Tk )
		back = m.add_vert( mid - side * Tk )
		ids = [ m.add_vert( p ) for p in pts4 ]
		sw = "grain" if i % 3 else "grain_light"
		for k in range( 4 ):
			n = ( k + 1 ) % 4
			m.add_face( ( ids[k], ids[n], front ), sw if k % 2 == 0 else "grain_light" )
			m.add_face( ( ids[n], ids[k], back ), "grain" if k % 2 == 0 else "straw" )
		# Awn: a thin card from the spikelet tip, swept down along the ear.
		awn_dir = ( dir_out * 0.35 + hd ).normalized()
		awn_len = 0.05 * ( 0.8 + 0.4 * m.rng.random() )
		aw = 0.0032
		m.add_poly( ( tip - side * aw, tip + awn_dir * awn_len, tip + side * aw ), "awn" )
	end = axis[-1]
	hd = ( axis[-1] - axis[-2] ).normalized()
	for k in ( -1, 1 ):
		d = ( hd + hd.cross( side ) * 0.2 * k ).normalized()
		m.add_poly( ( end - side * 0.003, end + d * 0.07, end + side * 0.003 ), "awn" )

	# Two leaf blades from the nodes, arching out and over.
	if not node_leaves:
		return
	for i, yaw_off, length in ( ( 4, 2.3, 0.30 ), ( 8, -1.0, 0.26 ) ):
		yaw = lean_yaw + yaw_off + m.rng.uniform( -0.3, 0.3 )
		d = Vector( ( math.cos( yaw ) * 0.55, math.sin( yaw ) * 0.55, 1.0 ) )
		leaf( m, pts[i], d, length, 0.03, m.rng.choice( ( "straw", "straw_dark" ) ), droop=0.5, segments=5,
			  roll=m.rng.uniform( -0.3, 0.3 ) )


def _v1_stalk_scale() -> float:
	"""The v1 single stalk ( wheat( 0.80, 0.4, 6, 120, 0.17 ) ) was fitted to 0.90 m tall; tuft stalks keep that
	per-stalk scale (Mark: small plants 2.5-5x life size so they read at distance)."""
	ref = ForageMesh( 401 )
	wheat( ref, 0.80, 0.4, 6, 120, 0.17 )
	return 0.90 / max( v.z for v in ref.verts )


def wheat_tuft( m, stalks: int, lean: tuple, blades: int, base_r: float ) -> None:
	"""One tillered wheat plant (Mark 2026-11-02: a patch, not a single twig): `stalks` culms from one crown, fanned
	out around the plant (lean `lean` degrees), heights +-20 %, each head drooping differently, plus a few straight
	leaf blades at the base (green low, straw-dry on the outside). Still ONE plant per model, never a baked cluster."""
	k = _v1_stalk_scale()
	phase = m.rng.uniform( 0, math.tau )
	for i in range( stalks ):
		yaw = phase + ( i + m.rng.uniform( -0.3, 0.3 ) ) / stalks * math.tau
		r = base_r * m.rng.uniform( 0.25, 1.0 )
		root = Vector( ( math.cos( yaw ) * r, math.sin( yaw ) * r, 0 ) )
		wheat( m, 0.72 * m.rng.uniform( 0.80, 1.20 ), yaw, m.rng.uniform( *lean ), m.rng.uniform( 40, 100 ),
			   0.17 * m.rng.uniform( 0.85, 1.1 ), root=root, spikelets=8, steps=12, node_leaves=False,
			   droop_yaw=yaw + m.rng.uniform( -1.3, 1.3 ) )
	for i in range( blades ):
		yaw = phase + 0.5 + ( i + m.rng.uniform( -0.35, 0.35 ) ) / blades * math.tau
		pitch = math.radians( m.rng.uniform( 45, 70 ) )
		d = Vector( ( math.cos( yaw ) * math.cos( pitch ), math.sin( yaw ) * math.cos( pitch ), math.sin( pitch ) ) )
		base = Vector( ( math.cos( yaw ) * base_r * 0.4, math.sin( yaw ) * base_r * 0.4, -ROOT_SINK * 0.5 ) )
		leaf( m, base, d, m.rng.uniform( 0.24, 0.36 ), m.rng.uniform( 0.024, 0.034 ),
			  ( "leaf_dark", "leaf", "straw_dark" )[i % 3], droop=0.12, segments=3, roll=m.rng.uniform( -0.4, 0.4 ) )
	m.verts = [ v * k for v in m.verts ]


def build_wheat_wild1_v2( m ):
	wheat_tuft( m, 8, ( 8, 18 ), 6, 0.035 )


def build_wheat_wild2_v2( m ):
	wheat_tuft( m, 10, ( 9, 20 ), 7, 0.04 )


def build_wheat_wild3_v2( m ):
	wheat_tuft( m, 6, ( 8, 19 ), 6, 0.035 )


# --- Nightcrawler -------------------------------------------------------------------------------

def worm_tube( m, points, radii, swatches ) -> None:
	"""Hexagonal tube with one swatch per segment (annulation reads as alternating bands)."""
	m.new_part()
	sides = 6
	rings = []
	for p, ( a, b ), r_i in zip( points, transport_frames( points ), radii ):
		rings.append( [ m.add_vert( p + ( a * math.cos( s / sides * math.tau + 0.3 ) + b * math.sin( s / sides * math.tau + 0.3 ) ) * r_i ) for s in range( sides ) ] )
	for i in range( len( rings ) - 1 ):
		for s in range( sides ):
			n = ( s + 1 ) % sides
			m.add_face( ( rings[i][s], rings[i][n], rings[i + 1][n], rings[i + 1][s] ), swatches[i] )
	tip = m.add_vert( points[-1] + ( points[-1] - points[-2] ).normalized() * radii[-1] * 1.4 )
	for s in range( sides ):
		m.add_face( ( rings[-1][s], rings[-1][( s + 1 ) % sides], tip ), swatches[-1] )


def dirt_cast( m, centre: Vector, radius: float ) -> None:
	"""Low heap of coiled casting pellets: a flattened base mound plus a few lumps on top."""
	blob( m, centre + Vector( ( 0, 0, radius * 0.18 ) ), Vector( ( radius, radius * 0.85, radius * 0.32 ) ),
		  ( "dirt", "dirt_dark", "dirt", "dirt_light" ), sides=7, flat_bottom=True )
	for k in range( 4 ):
		ang = m.rng.uniform( 0, math.tau )
		r = radius * m.rng.uniform( 0.15, 0.55 )
		c = centre + Vector( ( math.cos( ang ) * r, math.sin( ang ) * r, radius * m.rng.uniform( 0.38, 0.5 ) ) )
		s = radius * m.rng.uniform( 0.28, 0.4 )
		blob( m, c, Vector( ( s, s * 0.8, s * 0.6 ) ), ( "dirt_light", "dirt", "dirt_dark" ), sides=5 )


def nightcrawler( m, path2d, emerge: Vector, radius: float, head_lift: float ) -> None:
	"""Worm body follows `path2d` (xy points, tail first — the tail end sits in the cast), resting on the ground."""
	pts = []
	n = len( path2d )
	for i, ( x, y ) in enumerate( path2d ):
		t = i / ( n - 1 )
		z = radius * 0.85
		if t < 0.12:                                   # tail dives down into the cast
			z = emerge.z * ( 1.0 - t / 0.12 ) + z * ( t / 0.12 )
		if t > 0.85:
			z += head_lift * ( ( t - 0.85 ) / 0.15 ) ** 2
		pts.append( Vector( ( x, y, z ) ) )
	radii = []
	swatches = []
	for i in range( n ):
		t = i / ( n - 1 )
		r = radius * ( 0.8 + 0.2 * math.sin( math.pi * min( 1.0, t * 1.2 ) ) )
		if t > 0.9:
			r *= 1.0 - ( t - 0.9 ) / 0.1 * 0.45
		band = 0.58 < t < 0.70                         # clitellum, a third back from the head
		if band:
			r *= 1.12
		if i % 2:
			r *= 0.92                                  # annulation
		radii.append( r )
		swatches.append( "worm_band" if band else ( "worm" if i % 2 == 0 else "worm_dark" ) )
	worm_tube( m, pts, radii, swatches[:-1] + [ swatches[-1] ] )


def _path( m, length: float, start: Vector, yaw: float, bends, steps: int = 20 ):
	"""Polyline of `length` from `start`, turning by the curvature list `bends` (radians per unit, piecewise)."""
	pts = []
	p = Vector( ( start.x, start.y ) )
	heading = yaw
	ds = length / steps
	for i in range( steps + 1 ):
		pts.append( ( p.x, p.y ) )
		k = bends[min( len( bends ) - 1, int( i / ( steps + 1 ) * len( bends ) ) )]
		heading += k * ds
		p = p + Vector( ( math.cos( heading ), math.sin( heading ) ) ) * ds
	return pts


def build_nightcrawler1( m ):
	dirt_cast( m, Vector(), 0.045 )
	path = _path( m, 0.25, Vector( ( 0.0, 0.0 ) ), 0.2, ( 4.0, 6.0, -7.0, -5.0, 3.0 ) )
	nightcrawler( m, path, Vector( ( 0, 0, 0.0 ) ), 0.0125, 0.012 )


def build_nightcrawler2( m ):
	dirt_cast( m, Vector(), 0.05 )
	path = _path( m, 0.24, Vector( ( 0.0, 0.0 ) ), 2.4, ( -2.0, -9.0, -6.0, 2.0, 5.0 ) )
	nightcrawler( m, path, Vector( ( 0, 0, 0.0 ) ), 0.013, 0.028 )


# --- Daisies (life size x 2.5, like daisy) -------------------------------------------------------

def daisy_stem_head( m, root: Vector, top: Vector, mid: Vector, normal: Vector, head_radius: float ) -> None:
	tube( m, curve_points( root - Vector( ( 0, 0, ROOT_SINK ) ), mid, top, 4 ), [ 0.003, 0.0027, 0.0024, 0.0021, 0.002 ], "leaf", sides=3, cap=False )
	normal = normal.normalized()
	a, b = frame( normal )
	m.new_part()
	centre_r = head_radius * 0.32
	dome = top + normal * centre_r * 0.45
	ring = [ m.add_vert( top + ( a * math.cos( i / 6 * math.tau ) + b * math.sin( i / 6 * math.tau ) ) * centre_r ) for i in range( 6 ) ]
	c = m.add_vert( dome )
	for i in range( 6 ):
		m.add_face( ( c, ring[i], ring[( i + 1 ) % 6] ), "yellow" if i % 2 == 0 else "orange" )
	# Underside so a tipped head never shows a hole from behind.
	under = m.add_vert( top - normal * centre_r * 0.3 )
	for i in range( 6 ):
		m.add_face( ( under, ring[( i + 1 ) % 6], ring[i] ), "leaf" )
	petals = 13
	phase = m.rng.uniform( 0, math.tau )
	for i in range( petals ):
		m.new_part()
		ang = phase + i / petals * math.tau
		out = a * math.cos( ang ) + b * math.sin( ang )
		side = out.cross( normal ).normalized()
		base = top + out * ( centre_r * 0.8 )
		tip = top + out * head_radius * m.rng.uniform( 0.92, 1.05 ) - normal * head_radius * 0.12
		w = head_radius * 0.17
		mid_p = base.lerp( tip, 0.55 )
		m.add_poly( ( base, mid_p - side * w, tip, mid_p + side * w ), "petal" if i % 3 else "petal_shade" )


def build_daisy2( m ):
	# One plant, two flower stalks from one crown: a tall head and a shorter one leaning out.
	daisy_stem_head( m, Vector(), Vector( ( 0.006, 0.0, 0.155 ) ), Vector( ( 0, 0, 0.085 ) ), Vector( ( 0.15, 0.05, 1 ) ), 0.038 )
	daisy_stem_head( m, Vector( ( 0.004, 0.003, 0 ) ), Vector( ( -0.03, 0.022, 0.105 ) ), Vector( ( -0.004, 0.006, 0.06 ) ),
					 Vector( ( -0.45, 0.3, 1 ) ), 0.033 )
	gp.leaf_rosette( m, Vector(), 3, ( 0.05, 0.07 ), ( 0.018, 0.024 ), ( 15, 30 ), ( "leaf", "leaf_dark" ) )


def build_daisy3( m ):
	# A bent one: the stem bows out and the head tips well over toward the bend.
	daisy_stem_head( m, Vector(), Vector( ( 0.036, 0.01, 0.14 ) ), Vector( ( 0.002, 0.0, 0.1 ) ), Vector( ( 0.55, 0.15, 1 ) ), 0.037 )
	gp.leaf_rosette( m, Vector(), 2, ( 0.05, 0.07 ), ( 0.018, 0.024 ), ( 15, 30 ), ( "leaf", "leaf_dark" ) )


# --- Hobbit's leaf ------------------------------------------------------------------------------

def oval_leaf( m, base: Vector, direction: Vector, length: float, width: float, swatch: str,
			   droop: float, fold: float, segments: int = 5 ) -> None:
	"""Broad oval leaf, blunt tip, folded along the midrib (V-section) so it shades as two planes."""
	m.new_part()
	d = direction.normalized()
	side = _perp( d )
	up = side.cross( d ).normalized()
	if up.z < 0:
		up = -up
	spine = []
	for i in range( segments + 1 ):
		t = i / segments
		p = base + d * ( length * t )
		p.z -= droop * length * t * t
		spine.append( p )
	left, right = [], []
	for i in range( 1, segments ):
		t = i / segments
		# oval: widest just below the middle, blunt toward the tip
		w = width * 0.5 * ( math.sin( math.pi * t ) ** 0.6 ) * ( 1.0 + m.rng.uniform( -0.08, 0.08 ) )
		lift = up * w * fold
		left.append( spine[i] + side * w + lift )
		right.append( spine[i] - side * w + lift )
	b = m.add_vert( spine[0] )
	tip = m.add_vert( spine[-1] )
	sp = [ m.add_vert( p ) for p in spine[1:-1] ]
	li = [ m.add_vert( p ) for p in left ]
	ri = [ m.add_vert( p ) for p in right ]
	rib = "herb_rib"
	m.add_face( ( b, sp[0], li[0] ), swatch )
	m.add_face( ( b, ri[0], sp[0] ), swatch )
	for i in range( len( li ) - 1 ):
		m.add_face( ( sp[i], sp[i + 1], li[i + 1], li[i] ), swatch )
		m.add_face( ( sp[i], ri[i], ri[i + 1], sp[i + 1] ), swatch )
	m.add_face( ( sp[-1], tip, li[-1] ), swatch )
	m.add_face( ( sp[-1], ri[-1], tip ), swatch )
	# a thin pale midrib strip on top
	rw = width * 0.025
	for i in range( len( spine ) - 2 ):
		p0, p1 = spine[i], spine[i + 1]
		lift = up * 0.0015
		m.add_poly( ( p0 - side * rw + lift, p0 + side * rw + lift, p1 + side * rw * 0.7 + lift, p1 - side * rw * 0.7 + lift ), rib )


def hobbits_leaf( m, count: int, spike_lean: Vector, spike_h: float ) -> None:
	phase = m.rng.uniform( 0, math.tau )
	for i in range( count ):
		ang = phase + i / count * math.tau + m.rng.uniform( -0.2, 0.2 )
		inner = i % 3 == 0
		pitch = math.radians( m.rng.uniform( 52, 64 ) if inner else m.rng.uniform( 28, 42 ) )
		d = Vector( ( math.cos( ang ) * math.cos( pitch ), math.sin( ang ) * math.cos( pitch ), math.sin( pitch ) ) )
		length = m.rng.uniform( 0.20, 0.24 ) if inner else m.rng.uniform( 0.25, 0.30 )
		oval_leaf( m, Vector( ( 0, 0, 0.004 ) ) + d * 0.01, d, length, length * m.rng.uniform( 0.50, 0.58 ),
				   m.rng.choice( ( "herb", "herb", "herb_light", "herb_dark" ) ), droop=0.55 if not inner else 0.35,
				   fold=m.rng.uniform( 0.25, 0.4 ) )
	# Flower spike: thin stalk from the crown, a loose head of small pale trumpet flowers.
	top = Vector( ( 0, 0, spike_h ) ) + spike_lean
	mid = Vector( ( 0, 0, spike_h * 0.55 ) ) + spike_lean * 0.25
	tube( m, curve_points( Vector( ( 0, 0, -ROOT_SINK ) ), mid, top, 4 ), [ 0.006, 0.005, 0.0045, 0.004, 0.0035 ], "herb_dark", sides=4, cap=False )
	for k in range( 6 ):
		m.new_part()
		t = 0.55 + 0.45 * k / 5
		c = _bezier( Vector( ( 0, 0, -ROOT_SINK ) ), mid, top, t )
		ang = k * 2.4 + m.rng.uniform( -0.3, 0.3 )
		out = Vector( ( math.cos( ang ), math.sin( ang ), 0.9 ) ).normalized()
		L = 0.03 * ( 1.1 - 0.3 * k / 5 )
		mouth = c + out * L
		a, b = frame( out )
		# tube body (calyx end) + 5-lobed open mouth
		tube( m, [ c, c + out * L * 0.6 ], [ 0.004, 0.006 ], "herb_light", sides=4, cap=False )
		ring = [ m.add_vert( mouth + ( a * math.cos( j / 5 * math.tau ) + b * math.sin( j / 5 * math.tau ) ) * 0.011 ) for j in range( 5 ) ]
		throat = m.add_vert( c + out * L * 0.55 )
		for j in range( 5 ):
			m.add_face( ( throat, ring[j], ring[( j + 1 ) % 5] ), "bloom" if j % 2 else "bloom_pink" )
	# closed buds at the very tip
	for k in range( 2 ):
		d = Vector( ( m.rng.uniform( -1, 1 ), m.rng.uniform( -1, 1 ), 1.6 ) ).normalized()
		tube( m, [ top, top + d * 0.022 ], [ 0.005, 0.0015 ], "bloom_pink", sides=4 )


def build_hobbits_leaf1( m ):
	hobbits_leaf( m, 6, Vector( ( 0.02, 0.01, 0 ) ), 0.44 )


def build_hobbits_leaf2( m ):
	hobbits_leaf( m, 7, Vector( ( -0.06, 0.04, 0 ) ), 0.41 )


# --- Blueberry bush -----------------------------------------------------------------------------

def leaf_card( m, base: Vector, direction: Vector, length: float, width: float, tile: str, fold: float, roll: float ) -> None:
	"""Folded alpha card: the tile's twig runs base → tip along the fold; two halves tilted up by `fold`."""
	m.new_part()
	d = direction.normalized()
	side = _perp( d )
	side = ( Matrix.Rotation( roll, 3, d ) @ side ).normalized()
	up = side.cross( d ).normalized()
	if up.z < 0:
		up = -up
		side = -side
	tip = base + d * length
	lift = up * ( width * 0.5 * math.sin( fold ) )
	half = side * ( width * 0.5 * math.cos( fold ) )
	bl, br = base + half + lift, base - half + lift
	tl, tr = tip + half + lift, tip - half + lift
	u0, v0, u1, v1 = TILES[tile]
	inset = 1.0 / TEX
	u0 += inset; v0 += inset; u1 -= inset; v1 -= inset
	um = ( u0 + u1 ) * 0.5
	ib, it = m.add_vert( base ), m.add_vert( tip )
	ibl, ibr, itl, itr = ( m.add_vert( p ) for p in ( bl, br, tl, tr ) )
	# texture: v0 = card base (twig bottom), v1 = tip; u0 = left (+side), um = fold line
	m.add_face( ( ib, it, itl, ibl ), "leaf", uvs=[ ( um, v0 ), ( um, v1 ), ( u0, v1 ), ( u0, v0 ) ] )
	m.add_face( ( ib, ibr, itr, it ), "leaf", uvs=[ ( um, v0 ), ( u1, v0 ), ( u1, v1 ), ( um, v1 ) ] )


def berry( m, c: Vector, r: float, swatch: str ) -> None:
	"""Faceted berry: jittered 5-gon belt between two smaller rings, a dark crown at the blossom end (top)."""
	m.new_part()
	sides = 5
	ph = m.rng.uniform( 0, math.tau )
	belt = [ m.add_vert( c + Vector( ( math.cos( ph + i / sides * math.tau ) * r * m.rng.uniform( 0.92, 1.08 ),
										 math.sin( ph + i / sides * math.tau ) * r * m.rng.uniform( 0.92, 1.08 ), 0 ) ) ) for i in range( sides ) ]
	topr = [ m.add_vert( c + Vector( ( math.cos( ph + ( i + 0.5 ) / sides * math.tau ) * r * 0.55,
										 math.sin( ph + ( i + 0.5 ) / sides * math.tau ) * r * 0.55, r * 0.72 ) ) ) for i in range( sides ) ]
	crown = m.add_vert( c + Vector( ( 0, 0, r * 0.62 ) ) )
	bot = m.add_vert( c + Vector( ( 0, 0, -r * 0.92 ) ) )
	light = "berry_bloom" if swatch == "berry" else swatch
	for i in range( sides ):
		n = ( i + 1 ) % sides
		m.add_face( ( belt[i], belt[n], topr[i] ), swatch )
		m.add_face( ( topr[i], belt[n], topr[n] ), light )
		m.add_face( ( topr[i], topr[n], crown ), "berry_dark" )
		m.add_face( ( belt[n], belt[i], bot ), swatch if i % 2 else light )


def berry_cluster( m, c: Vector, r: float, count: int ) -> None:
	ripe = m.rng.random() > 0.2
	for k in range( count ):
		ang = k / count * math.tau + m.rng.uniform( -0.4, 0.4 )
		off = Vector( ( math.cos( ang ), math.sin( ang ), -0.3 * ( k % 2 ) ) ) * r * ( 1.15 if k else 0.0 )
		sw = "berry" if ripe or k % 3 else "berry_unripe"
		if not ripe and k == 0:
			sw = "berry_unripe"
		berry( m, c + off, r * m.rng.uniform( 0.85, 1.1 ), sw )


def blueberry_bush( m, stems: int, height: float, width: float, clusters: int ) -> None:
	"""Vase of woody stems; each branch tip carries a fan of folded leaf cards; berry clusters on the outer shell."""
	tips = []
	wood_points = []
	phase = m.rng.uniform( 0, math.tau )
	for s in range( stems ):
		ang = phase + s / stems * math.tau + m.rng.uniform( -0.3, 0.3 )
		out = Vector( ( math.cos( ang ), math.sin( ang ), 0 ) )
		base = out * m.rng.uniform( 0.03, 0.08 ) - Vector( ( 0, 0, ROOT_SINK ) )
		h = height * m.rng.uniform( 0.62, 0.78 )
		spread = width * 0.5 * m.rng.uniform( 0.45, 0.7 )
		p1 = base + out * spread * 0.25 + Vector( ( 0, 0, h * 0.55 ) )
		p2 = base + out * spread + Vector( ( 0, 0, h ) )
		pts = curve_points( base, p1, p2, 4 )
		tube( m, pts, [ 0.011, 0.0095, 0.008, 0.0065, 0.005 ], m.rng.choice( ( "wood", "wood_dark" ) ), sides=4 )
		tips.append( ( p2, ( p2 - _bezier( base, p1, p2, 0.85 ) ).normalized() ) )
		# side branches
		for t in ( 0.2, 0.42, 0.68 ):
			c = _bezier( base, p1, p2, t )
			side_ang = ang + m.rng.choice( ( -1, 1 ) ) * m.rng.uniform( 0.6, 1.1 )
			d = Vector( ( math.cos( side_ang ) * 0.9, math.sin( side_ang ) * 0.9, m.rng.uniform( 0.15, 0.4 ) + t * 0.5 ) ).normalized()
			L = height * m.rng.uniform( 0.18, 0.28 )
			e = c + d * L
			bend = c.lerp( e, 0.5 ) + Vector( ( 0, 0, L * 0.12 ) )
			tube( m, curve_points( c, bend, e, 2 ), [ 0.007, 0.0055, 0.004 ], "wood", sides=3 )
			tips.append( ( e, d ) )
		wood_points.extend( pts[1:] )

	# Leaf cards: a fan of 3-4 folded cards on every tip, plus single cards back along each stem — layered.
	card_len = 0.24
	for tip, d in tips:
		n = m.rng.choice( ( 3, 4 ) )
		for k in range( n ):
			yaw = k / n * math.tau + m.rng.uniform( -0.5, 0.5 )
			sideways = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
			cd = ( d * 0.8 + sideways * 0.75 + Vector( ( 0, 0, m.rng.uniform( -0.25, 0.3 ) ) ) ).normalized()
			L = card_len * m.rng.uniform( 0.8, 1.1 )
			leaf_card( m, tip - d * 0.03, cd, L, L * 0.95, m.rng.choice( ( "sprig_a", "sprig_b" ) ),
					   fold=math.radians( m.rng.uniform( 18, 32 ) ), roll=m.rng.uniform( -0.5, 0.5 ) )
	for p in wood_points:
		# outward from the bush axis, lower cards splay flatter so the skirt hides the bare stems
		outward = Vector( ( p.x, p.y, 0 ) )
		ang = math.atan2( outward.y, outward.x ) + m.rng.uniform( -1.0, 1.0 ) if outward.length > 0.02 else m.rng.uniform( 0, math.tau )
		low = p.z < height * 0.4
		cd = Vector( ( math.cos( ang ), math.sin( ang ), m.rng.uniform( -0.15, 0.2 ) if low else m.rng.uniform( 0.1, 0.6 ) ) ).normalized()
		L = card_len * m.rng.uniform( 0.7, 0.9 )
		leaf_card( m, p, cd, L, L * 0.95, m.rng.choice( ( "sprig_a", "sprig_b" ) ),
				   fold=math.radians( m.rng.uniform( 18, 32 ) ), roll=m.rng.uniform( -0.5, 0.5 ) )

	# Berry clusters: hang just outside the leaf mass so they read from every side.
	centre = Vector( ( 0, 0, height * 0.5 ) )
	for k in range( clusters ):
		ang = phase + ( k + 0.5 ) / clusters * math.tau + m.rng.uniform( -0.25, 0.25 )
		z = height * m.rng.uniform( 0.38, 0.8 )
		rr = width * 0.5 * ( 0.82 - 0.45 * ( ( z - height * 0.38 ) / ( height * 0.42 ) ) ** 2 ) + 0.02
		c = Vector( ( math.cos( ang ) * rr, math.sin( ang ) * rr, z ) )
		# a short pedicel twig back toward the middle
		inward = ( centre - c ).normalized()
		tube( m, [ c + Vector( ( 0, 0, 0.015 ) ), c + inward * 0.07 + Vector( ( 0, 0, 0.04 ) ) ], [ 0.003, 0.004 ], "wood", sides=3 )
		berry_cluster( m, c, 0.02, m.rng.choice( ( 3, 4, 5 ) ) )


def build_blueberry_bush1( m ):
	blueberry_bush( m, 6, 0.70, 0.50, 9 )


def build_blueberry_bush2( m ):
	blueberry_bush( m, 7, 0.66, 0.54, 10 )


# --- Red field mushroom -------------------------------------------------------------------------

def field_mushroom( m, height: float, cap_r: float, tilt_deg: float, tilt_yaw: float, flatness: float ) -> None:
	"""Red stem with a thin ring, a domed-to-flat red cap (darker crown), radial gill pleats under the rim."""
	tilt = math.radians( tilt_deg )
	axis = Vector( ( math.cos( tilt_yaw ) * math.sin( tilt ), math.sin( tilt_yaw ) * math.sin( tilt ), math.cos( tilt ) ) )
	stem_top_h = height - cap_r * ( 0.55 - 0.3 * flatness )
	top = axis * stem_top_h * 0.25 + Vector( ( 0, 0, stem_top_h ) ) * 0.75
	mid = Vector( ( 0, 0, stem_top_h * 0.5 ) ) + axis * stem_top_h * 0.05
	sr = cap_r * 0.27
	tube( m, curve_points( Vector( ( 0, 0, -ROOT_SINK ) ), mid, top, 3 ), [ sr * 1.18, sr * 1.0, sr * 0.88, sr * 0.8 ], "stem", sides=6, cap=False )
	# a dirty base collar where it leaves the soil
	tube( m, [ Vector( ( 0, 0, -ROOT_SINK ) ), Vector( ( 0, 0, sr * 0.55 ) ) ], [ sr * 1.24, sr * 1.19 ], "stem_shade", sides=6, cap=False )
	cap_axis = ( ( top - mid ).normalized() + axis ).normalized()
	a, b = frame( cap_axis )
	# Ring (annulus): a short downward skirt two thirds up the stem.
	m.new_part()
	rc = _bezier( Vector( ( 0, 0, -ROOT_SINK ) ), mid, top, 0.86 )
	upper = lumpy_ring( m, rc, a, b, sr * 1.0, 6, 0.3 )
	lower = lumpy_ring( m, rc - cap_axis * cap_r * 0.1, a, b, sr * 1.4, 6, 0.3 )
	ui = [ m.add_vert( p ) for p in upper ]
	li = [ m.add_vert( p ) for p in lower ]
	for i in range( 6 ):
		n = ( i + 1 ) % 6
		m.add_face( ( ui[i], li[i], li[n], ui[n] ), "stem_shade" if i % 2 else "stem" )

	# Cap: rim (curled under) → shoulder → crown, jittered rings of different counts (varied face sizes).
	m.new_part()
	sides = 9
	phase = m.rng.uniform( 0, math.tau )
	h = cap_r * ( 0.62 - 0.35 * flatness )          # dome height above the rim
	rim_in = lumpy_ring( m, top - cap_axis * cap_r * 0.02, a, b, cap_r * 0.86, sides, 0.35, phase )
	rim = []
	shoulder = []
	for i in range( sides ):
		ang = phase + ( i + m.rng.uniform( -0.2, 0.2 ) ) / sides * math.tau
		k = 1.0 + m.rng.uniform( -0.07, 0.07 )
		dir_ = a * math.cos( ang ) + b * math.sin( ang )
		rim.append( top + dir_ * cap_r * k + cap_axis * h * 0.12 )
		shoulder.append( top + dir_ * cap_r * k * 0.78 + cap_axis * h * ( 0.72 + 0.12 * flatness ) )
	crown_ring = lumpy_ring( m, top + cap_axis * h * 0.98, a, b, cap_r * 0.38, 5, 0.3, phase + 0.3 )
	peak = top + cap_axis * h * 1.06
	ri = [ m.add_vert( p ) for p in rim ]
	rii = [ m.add_vert( p ) for p in rim_in ]
	si = [ m.add_vert( p ) for p in shoulder ]
	ci = [ m.add_vert( p ) for p in crown_ring ]
	pk = m.add_vert( peak )
	tones = ( "cap_mid", "cap_light", "cap_mid", "cap_red" )
	for i in range( sides ):
		n = ( i + 1 ) % sides
		m.add_face( ( rii[i], rii[n], ri[n], ri[i] ), "cap_mid" )                       # curled-under lip
		m.add_face( ( ri[i], ri[n], si[n], si[i] ), tones[i % len( tones )] )
	# shoulder (9) → crown ring (5): fan each shoulder edge to its nearest crown vertex
	def nearest_crown( p ):
		return min( range( 5 ), key=lambda j: ( crown_ring[j] - p ).length )
	for i in range( sides ):
		n = ( i + 1 ) % sides
		j0, j1 = nearest_crown( shoulder[i] ), nearest_crown( shoulder[n] )
		m.add_face( ( si[i], si[n], ci[j1] ), "cap_red" if i % 3 else "cap_mid" )
		if j0 != j1:
			m.add_face( ( si[i], ci[j1], ci[j0] ), "cap_red" )
	for j in range( 5 ):
		m.add_face( ( ci[j], ci[( j + 1 ) % 5], pk ), "cap_dark" if j % 2 else "cap_red" )

	# Gills: a pleated underside from the stem top out to the inner rim, alternating dark / light pleats.
	m.new_part()
	gill_c = [ m.add_vert( p ) for p in lumpy_ring( m, top + cap_axis * h * 0.05, a, b, sr * 1.0, sides, 0.2, phase ) ]
	pleats = []
	for i in range( sides ):
		n = ( i + 1 ) % sides
		mid_rim = ( rim_in[i] + rim_in[n] ) * 0.5 + cap_axis * cap_r * 0.06
		pleats.append( m.add_vert( mid_rim ) )
	for i in range( sides ):
		n = ( i + 1 ) % sides
		m.add_face( ( gill_c[i], pleats[i], rii[i] ), "gill" )
		m.add_face( ( gill_c[i], rii[n], pleats[i] ), "gill_dark" )
		m.add_face( ( gill_c[i], gill_c[n], rii[n] ), "gill" )


def build_mushroom_red1( m ):
	field_mushroom( m, 0.25, 0.115, 8, 0.5, 0.3 )


def build_mushroom_red2( m ):
	field_mushroom( m, 0.29, 0.155, 12, 2.2, 0.8 )


def build_mushroom_red3( m ):
	field_mushroom( m, 0.21, 0.10, 22, 4.1, 0.0 )


# ( name, seed, builder ). Built at in-world size (already oversized) — no extra scale factor.
MODELS = [
	( "wheat_wild1_v2", 401, build_wheat_wild1_v2 ),
	( "wheat_wild2_v2", 402, build_wheat_wild2_v2 ),
	( "wheat_wild3_v2", 403, build_wheat_wild3_v2 ),
	( "nightcrawler1_v2", 411, build_nightcrawler1 ),
	( "nightcrawler2_v2", 412, build_nightcrawler2 ),
	( "daisy2", 421, build_daisy2, 2.5 ),
	( "daisy3", 422, build_daisy3, 2.5 ),
	( "hobbits_leaf1", 431, build_hobbits_leaf1 ),
	( "hobbits_leaf2", 432, build_hobbits_leaf2 ),
	( "blueberry_bush1", 441, build_blueberry_bush1 ),
	( "blueberry_bush2", 442, build_blueberry_bush2 ),
	( "mushroom_red1", 451, build_mushroom_red1 ),
	( "mushroom_red2", 452, build_mushroom_red2 ),
	( "mushroom_red3", 453, build_mushroom_red3 ),
]
# Target bounding sizes (metres): the builder output is uniformly rescaled so the HEIGHT hits this exactly.
TARGET_HEIGHT = {
	"daisy2": 0.40, "daisy3": 0.36,
	"hobbits_leaf1": 0.45, "hobbits_leaf2": 0.43,
	"blueberry_bush1": 0.80, "blueberry_bush2": 0.78,
	"mushroom_red1": 0.25, "mushroom_red2": 0.28, "mushroom_red3": 0.22,
}

# The bushes' widest footprint is pulled in to this (x / y only, after the height fit).
TARGET_WIDTH = { "blueberry_bush1": 0.90, "blueberry_bush2": 0.90 }

# --- Blender plumbing ----------------------------------------------------------------------------

def _get_material( palette_path: str ):
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


def to_object( name: str, m: ForageMesh, material ):
	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( [ tuple( v ) for v in m.verts ], [], m.faces )
	mesh.update()
	# validate() may drop degenerate faces and reorder polys — keep the per-face data in step by checking first.
	if mesh.validate( verbose=False ):
		raise RuntimeError( f"{name}: mesh needed repair (degenerate / duplicate faces) — fix the builder" )
	for poly in mesh.polygons:
		poly.use_smooth = False
	z_top = max( max( v.z for v in m.verts ), 1e-4 )
	uv_layer = mesh.uv_layers.new( name="UVMap" )
	color_attr = mesh.color_attributes.new( name="Color", type="FLOAT_COLOR", domain="CORNER" )
	for poly in mesh.polygons:
		uvs = m.face_uvs[poly.index]
		sw = swatch_uv( m.face_swatch[poly.index] )
		phase = m.face_phase[poly.index]
		for k, li in enumerate( poly.loop_indices ):
			uv_layer.data[li].uv = uvs[k] if uvs else sw
			z = mesh.vertices[mesh.loops[li].vertex_index].co.z
			color_attr.data[li].color = ( max( 0.0, z / z_top ), phase, m.model_random, 1.0 )
	mesh.materials.append( material )
	obj = bpy.data.objects.new( name, mesh )
	bpy.context.scene.collection.objects.link( obj )
	return obj


VMAT_TEXT = """Layer0
{{
	shader "shaders/pixel_lit.shader"
	F_ALPHA_TEST 1
	F_RENDER_BACKFACES 1

	PixelRoughness "0.900"
	PixelFlatShading "1.000"
	PixelAlphaCutoff "0.500"
	TextureColor "{palette}"
	TextureTranslucency "{mask}"
}}
"""


def main() -> None:
	gp._reset_scene()
	os.makedirs( MAT_DIR, exist_ok=True )
	os.makedirs( MODEL_DIR, exist_ok=True )

	palette_path = os.path.join( MAT_DIR, PALETTE_PNG )
	rgb, mask = build_texture()
	write_png( palette_path, rgb )
	write_png( os.path.join( MAT_DIR, MASK_PNG ), np.repeat( mask[..., None], 3, axis=2 ) )
	with open( os.path.join( MAT_DIR, f"{MATERIAL_NAME}.vmat" ), "w", newline="\n" ) as f:
		f.write( VMAT_TEXT.format( palette=f"{MAT_REL_DIR}/{PALETTE_PNG}", mask=f"{MAT_REL_DIR}/{MASK_PNG}" ) )
	material = _get_material( palette_path )

	columns = 5
	objects = []
	for index, ( name, seed, build, *scale ) in enumerate( MODELS ):
		m = ForageMesh( seed )
		build( m )
		if scale:
			m.verts = [ v * scale[0] for v in m.verts ]
		if name in TARGET_HEIGHT:
			k = TARGET_HEIGHT[name] / max( v.z for v in m.verts )
			m.verts = [ v * k for v in m.verts ]
		if name in TARGET_WIDTH:
			xs = [ v.x for v in m.verts ]
			ys = [ v.y for v in m.verts ]
			k = TARGET_WIDTH[name] / max( max( xs ) - min( xs ), max( ys ) - min( ys ) )
			m.verts = [ Vector( ( v.x * k, v.y * k, v.z ) ) for v in m.verts ]
		obj = to_object( name, m, material )
		obj.location = ( ( index % columns ) * LAYOUT_SPACING, -( index // columns ) * LAYOUT_SPACING, 0.0 )
		objects.append( obj )
		xs = [ v.x for v in m.verts ]
		ys = [ v.y for v in m.verts ]
		zs = [ v.z for v in m.verts ]
		tris = sum( len( f ) - 2 for f in m.faces )
		print( f"[forage] {name}: {len( m.verts )} verts, {tris} tris, height {max( zs ):.3f} m ({max( zs ) * 40:.1f} u), "
			   f"footprint {max( xs ) - min( xs ):.2f} x {max( ys ) - min( ys ):.2f} m, min z {min( zs ):.3f}" )

	for obj in objects:
		gp.export_fbx( obj, MODEL_DIR )
		path = os.path.join( MODEL_DIR, f"{obj.name}.vmdl" )
		with open( path, "w", newline="\n" ) as f:
			f.write( gp.VMDL_TEMPLATE.format( material=f"{MAT_REL_DIR}/{MATERIAL_NAME}.vmat", fbx=f"{MODEL_REL_DIR}/{obj.name}.fbx" ) )

	os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
	if os.path.exists( BLEND_PATH ):
		os.remove( BLEND_PATH )         # no .blend1 backup left behind
	bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
	print( f"[forage] saved {BLEND_PATH}" )


if __name__ == "__main__":
	main()
