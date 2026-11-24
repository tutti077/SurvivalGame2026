"""
Redwood Forest ground plants — sword ferns, fiddleheads, raspberry bushes (one plant per model, never a baked cluster).

Run headless from the repo root:
  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_redwood_plants.py"
  ... -- --render <dir>     also renders a preview per model + a lineup into <dir> (scratchpad, never the repo)

Follows create_cloverhills_forage.py (and reuses its / create_ground_plants' mesh helpers — both imported, never
edited): real-world metres, origin at the root on Z = 0, flat shading, single-sided cards drawn with backfaces,
FBX with the grass / build-kit settings and a .vmdl at import_scale 0.4 (40 u/m). No LODs, no wind — the clover
forage has neither.

What it builds (in-world size)
------------------------------
  fern_sword1_v1 .. 4_v1   western sword fern: a vase of arching fronds from one scaly crown, each frond one
                           folded alpha card (pinnae drawn on the sheet) over a thin rachis prism; 4 has a few
                           dead brown fronds lying flat under the live ones                  ~0.5-1.0 m tall
  fiddlehead1_v1 .. 3_v1   ostrich-fern crosiers (3-7 coiled shoots) from one crown with old stipe stubs and
                           papery brown scales — HARVESTABLE (food_fiddlehead), ~3.5x life size
  raspberry_bush1_v1 .. 3  thorny arching canes, three-leaflet serrated leaf cards, fruiting laterals with red
                           aggregate berries hanging under the leaves — HARVESTABLE (food_raspberry)

Texture: ONE 256 x 256 sheet, materials/environment/redwoodForest/groundFoliage/redwood_plants_palette.png (+ the
cutout mask redwood_plants_palette_mask.png) on redwood_plants.vmat (pixel_lit, point sampled, F_ALPHA_TEST,
backfaces):
  rows   0..63   16 x 4 grid of 16 x 16-texel flat swatches (non-card faces UV to a swatch centre), opaque
  rows  64..255  five card tiles, 192 texels tall: frond_a / frond_b / frond_dry (64 wide, drawn at full
                 resolution — a 1.1 m frond lands ~170 texels / m) and rasp_a / rasp_b (64 x 96, drawn at
                 32 x 48 and doubled — chunky texels like the blueberry sprigs)

Vertex colour (LINEAR, same layout as the other plants): R = height 0 → 1, G = per-part phase, B = per-model random.

Outputs
-------
  Assets/models/environment/redwoodForest/groundFoliage/<name>.fbx + <name>.vmdl
  Assets/materials/environment/redwoodForest/groundFoliage/redwood_plants_palette.png + _mask.png + redwood_plants.vmat
  Assets/prefabs/environment/redwoodForest/groundFoliage/<name>.prefab
  Blender/blenderprojects/redwood_plants.blend   (all models on a grid)
"""

import json
import math
import os
import random
import sys
import uuid

import bpy
import numpy as np
from mathutils import Matrix, Vector

sys.dont_write_bytecode = True
sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )
import create_ground_plants as gp            # noqa: E402  (helpers only — main() never runs on import)
import create_cloverhills_forage as fg       # noqa: E402
from create_ground_plants import _perp, ROOT_SINK  # noqa: E402
from create_cloverhills_forage import ForageMesh, tube, blob, frame, write_png  # noqa: E402

_REPO_ROOT = gp._REPO_ROOT
BIOME = "redwoodForest"
MODEL_REL_DIR = f"models/environment/{BIOME}/groundFoliage"
MODEL_DIR = os.path.join( _REPO_ROOT, "Assets", *MODEL_REL_DIR.split( "/" ) )
MAT_REL_DIR = f"materials/environment/{BIOME}/groundFoliage"
MAT_DIR = os.path.join( _REPO_ROOT, "Assets", *MAT_REL_DIR.split( "/" ) )
PREFAB_REL_DIR = f"prefabs/environment/{BIOME}/groundFoliage"
PREFAB_DIR = os.path.join( _REPO_ROOT, "Assets", *PREFAB_REL_DIR.split( "/" ) )
BLEND_PATH = os.path.join( _REPO_ROOT, "Blender", "blenderprojects", "redwood_plants.blend" )

MATERIAL_NAME = "redwood_plants"
PALETTE_PNG = "redwood_plants_palette.png"
MASK_PNG = "redwood_plants_palette_mask.png"
LAYOUT_SPACING = 2.4

# --- Palette -------------------------------------------------------------------------------------
# sRGB 0..255. Understory greens are a step darker / cooler than the clover forage (redwood shade).
PALETTE = {
	# sword fern (rachis prism, crown)
	"fern_dark":     ( 40, 72, 36 ),
	"fern":          ( 56, 94, 44 ),
	"stipe":         ( 82, 90, 46 ),
	"stipe_brown":   ( 98, 72, 46 ),
	"scale_brown":   ( 132, 94, 56 ),
	"scale_light":   ( 158, 120, 76 ),
	"duff_dark":     ( 62, 44, 32 ),
	"duff":          ( 86, 62, 42 ),
	"duff_light":    ( 108, 80, 54 ),
	# fiddlehead — bright spring green against the dark duff
	"fiddle_dark":   ( 70, 114, 40 ),
	"fiddle":        ( 94, 142, 50 ),
	"fiddle_light":  ( 122, 166, 64 ),
	"fiddle_tip":    ( 140, 178, 78 ),
	# raspberry canes, prickles, fruit
	"cane_dark":     ( 88, 50, 50 ),
	"cane":          ( 114, 68, 60 ),
	"cane_green":    ( 92, 108, 58 ),
	"thorn":         ( 156, 104, 92 ),
	"berry_dark":    ( 128, 18, 34 ),
	"berry":         ( 170, 30, 48 ),
	"berry_light":   ( 200, 56, 70 ),
	"berry_pink":    ( 212, 130, 126 ),
	"berry_green":   ( 172, 182, 112 ),
	"calyx":         ( 82, 110, 50 ),
	"dry_dark":      ( 104, 76, 48 ),
	"dry":           ( 136, 102, 62 ),
}
PALETTE_KEYS = list( PALETTE.keys() )
TEX = 256
SWATCH = 16
SWATCH_COLS = TEX // SWATCH          # 16
SWATCH_ROWS = 4                      # rows 0..63
assert len( PALETTE_KEYS ) <= SWATCH_COLS * SWATCH_ROWS

# Card tiles: name -> ( x0, y0, w, h ) in PNG pixels (y down), and their draw resolution divisor.
TILE_RECTS = {
	"frond_a":   ( 0, 64, 64, 192 ),
	"frond_b":   ( 64, 64, 64, 192 ),
	"frond_dry": ( 128, 64, 64, 192 ),
	"rasp_a":    ( 192, 64, 64, 96 ),
	"rasp_b":    ( 192, 160, 64, 96 ),
}

FROND_TONES = [ ( 40, 76, 36 ), ( 50, 90, 42 ), ( 60, 102, 48 ), ( 70, 112, 54 ) ]
FROND_RIB = ( 64, 84, 40 )
FROND_STIPE = ( 88, 78, 48 )
DRY_TONES = [ ( 108, 78, 48 ), ( 122, 90, 56 ), ( 136, 102, 64 ), ( 116, 84, 52 ) ]
DRY_RIB = ( 92, 66, 42 )
RASP_TONES = [ ( 52, 92, 42 ), ( 62, 104, 48 ), ( 74, 116, 54 ), ( 84, 124, 60 ) ]
RASP_VEIN = ( 104, 138, 74 )
RASP_PETIOLE = ( 104, 80, 60 )


def swatch_uv( key: str ) -> tuple:
	index = PALETTE_KEYS.index( key )
	cx = index % SWATCH_COLS
	cy = index // SWATCH_COLS
	return ( ( cx + 0.5 ) * SWATCH / TEX, 1.0 - ( cy + 0.5 ) * SWATCH / TEX )


def tile_uv( name: str ) -> tuple:
	"""( u0, v0, u1, v1 ): v0 = the tile's bottom row (card base), v1 = its top row (card tip), inset one texel."""
	x0, y0, w, h = TILE_RECTS[name]
	inset = 1.0 / TEX
	return ( x0 / TEX + inset, 1.0 - ( y0 + h ) / TEX + inset, ( x0 + w ) / TEX - inset, 1.0 - y0 / TEX - inset )


# --- Texture drawing (numpy, y down) --------------------------------------------------------------

def fill_poly( rgb, alpha, pts, colour ) -> np.ndarray:
	"""Even-odd fill of a polygon on pixel centres (aliased on purpose — the point-filter look)."""
	h, w = alpha.shape
	yy, xx = np.mgrid[0:h, 0:w].astype( np.float32 )
	xx += 0.5
	yy += 0.5
	inside = np.zeros( ( h, w ), bool )
	n = len( pts )
	for i in range( n ):
		x0, y0 = pts[i]
		x1, y1 = pts[( i + 1 ) % n]
		if y0 == y1:
			continue
		cond = ( ( y0 > yy ) != ( y1 > yy ) ) & ( xx < ( x1 - x0 ) * ( yy - y0 ) / ( y1 - y0 ) + x0 )
		inside ^= cond
	rgb[inside] = colour
	alpha[inside] = True
	return inside


def draw_line( rgb, alpha, pts, width: float, colour ) -> None:
	h, w = alpha.shape
	for ( x0, y0 ), ( x1, y1 ) in zip( pts, pts[1:] ):
		steps = int( max( abs( x1 - x0 ), abs( y1 - y0 ) ) * 3 ) + 1
		for s in range( steps + 1 ):
			x = x0 + ( x1 - x0 ) * s / steps
			y = y0 + ( y1 - y0 ) * s / steps
			xa = int( math.floor( x - width * 0.5 + 0.5 ) )
			xb = max( xa + 1, int( math.floor( x + width * 0.5 + 0.5 ) ) )
			yi = int( math.floor( y ) )
			if 0 <= yi < h:
				rgb[yi, max( 0, xa ):min( w, xb )] = colour
				alpha[yi, max( 0, xa ):min( w, xb )] = True


def lanceolate( base, d, L, W, serrate: float, auricle_side: float, steps: int = 10 ):
	"""Pinna / leaflet outline: from `base` along unit `d`, widest a third out, pointed tip, toothed margin.
	`auricle_side` (+1 / -1 / 0) adds the sword-fern ear on that side next to the rachis."""
	nx, ny = -d[1], d[0]
	left, right = [], []
	for i in range( 1, steps ):
		s = i / steps
		w = W * min( 1.0, s * 3.2 + 0.25 ) * ( 1.0 - s ) ** 0.75
		tooth = ( 1.0 + serrate ) if i % 2 else ( 1.0 - serrate * 0.6 )
		wl = w * tooth
		wr = w * tooth
		if auricle_side and s < 0.2:
			if auricle_side > 0:
				wl *= 1.0 + 0.9 * ( 1.0 - abs( s - 0.1 ) / 0.1 )
			else:
				wr *= 1.0 + 0.9 * ( 1.0 - abs( s - 0.1 ) / 0.1 )
		cx = base[0] + d[0] * L * s
		cy = base[1] + d[1] * L * s
		left.append( ( cx + nx * wl, cy + ny * wl ) )
		right.append( ( cx - nx * wr, cy - ny * wr ) )
	tip = ( base[0] + d[0] * L, base[1] + d[1] * L )
	return [ base ] + left + [ tip ] + list( reversed( right ) )


def draw_frond( rng: random.Random, tones, rib, stipe, n_pairs: int, curl: float, ragged: float ):
	"""64 x 192 sword-fern frond, tip at the top. Rachis up the middle (the card's fold line), a bare scaly stipe for
	the bottom 14 %, alternate lanceolate pinnae angled toward the tip with an ear on the upper side, longest through
	the middle third, shrinking into a pointed tip. `ragged` drops / shortens a few pinnae (dry fronds)."""
	w, h = 64, 192
	rgb = np.zeros( ( h, w, 3 ), np.uint8 )
	rgb[:] = tones[1]
	alpha = np.zeros( ( h, w ), bool )

	def rachis_x( y ):
		t = 1.0 - y / ( h - 1 )                   # 0 base .. 1 tip
		return w * 0.5 + curl * math.sin( t * math.pi * 0.9 ) * 2.5

	stipe_top = h * 0.86
	tip_y = 3.0
	ys = np.linspace( stipe_top, tip_y + 10, n_pairs )
	for k, y0 in enumerate( ys ):
		t = 1.0 - ( y0 - tip_y ) / ( stipe_top - tip_y )        # 0 at the lowest pinna .. 1 near the tip
		size = min( 1.0, ( 1.0 - t ) * 2.3 ) ** 0.7 * ( 0.72 + 0.28 * min( 1.0, t * 4.0 ) )
		for side in ( -1, 1 ):
			y = y0 + ( 0.0 if side < 0 else ( ys[1] - ys[0] ) * 0.5 if n_pairs > 1 else 0.0 )
			if ragged and rng.random() < ragged * 0.35:
				continue
			L = 27.0 * size * rng.uniform( 0.92, 1.05 ) * ( rng.uniform( 0.45, 0.9 ) if ragged and rng.random() < ragged else 1.0 )
			if L < 2.5:
				continue
			ang = math.radians( rng.uniform( 62, 72 ) - 10 * t )         # from the rachis, toward the tip
			d = ( side * math.sin( ang ), -math.cos( ang ) )
			W = max( 1.2, L * rng.uniform( 0.17, 0.2 ) )
			base = ( rachis_x( y ), y )
			# auricle on the tip-ward side: the outline's left normal is (-d.y, d.x); pick whichever faces up (-y)
			poly = lanceolate( base, d, L, W, 0.18, 1 if d[0] < 0 else -1 )
			fill_poly( rgb, alpha, poly, tones[rng.randrange( len( tones ) )] )
			# a darker midvein along the pinna
			draw_line( rgb, alpha, [ base, ( base[0] + d[0] * L * 0.75, base[1] + d[1] * L * 0.75 ) ], 1.0, rib )
	# pointed tip piece
	tip_base = ( rachis_x( tip_y + 14 ), tip_y + 14 )
	fill_poly( rgb, alpha, lanceolate( tip_base, ( 0.0, -1.0 ), 13, 2.6, 0.2, 0 ), tones[2] )
	# rachis + stipe
	draw_line( rgb, alpha, [ ( rachis_x( y ), y ) for y in np.linspace( h - 1, tip_y + 4, 40 ) ], 1.6, rib )
	draw_line( rgb, alpha, [ ( rachis_x( y ), y ) for y in np.linspace( h - 1, stipe_top, 8 ) ], 2.6, stipe )
	for _ in range( 7 ):                          # brown papery scales on the stipe
		y = rng.uniform( stipe_top + 2, h - 2 )
		x = rachis_x( y ) + rng.choice( ( -1.6, 1.6 ) )
		yi, xi = int( y ), int( x )
		rgb[yi:yi + 2, xi:xi + 1] = PALETTE["scale_brown"]
		alpha[yi:yi + 2, xi:xi + 1] = True
	alpha[0, :] = alpha[:, 0] = alpha[:, -1] = False
	xb = int( round( rachis_x( h - 1 ) ) )
	alpha[-1, :] = False
	alpha[-1, xb - 1:xb + 1] = True
	return rgb, alpha


def draw_rasp_leaf( rng: random.Random, leaflets: int ):
	"""32 x 48 raspberry leaf (doubled to 64 x 96 on the sheet): petiole from the bottom centre (the fold line), a big
	terminal leaflet and 2 or 4 lateral leaflets, ovate, coarsely double-serrated, pale veins."""
	w, h = 32, 48
	rgb = np.zeros( ( h, w, 3 ), np.uint8 )
	rgb[:] = RASP_TONES[1]
	alpha = np.zeros( ( h, w ), bool )
	cx = w * 0.5
	petiole_top = h * 0.6
	leaves = []
	# terminal leaflet: up from the petiole top
	leaves.append( ( ( cx, petiole_top ), ( 0.0, -1.0 ), petiole_top - 1.5, 9.5 ) )
	# laterals: pairs angled out and up
	pairs = [ ( petiole_top + 1.0, 58, 16.5, 7.0 ) ]
	if leaflets >= 5:
		pairs.append( ( petiole_top + 6.5, 82, 12.0, 5.4 ) )
	for y, ang_deg, L, W in pairs:
		for side in ( -1, 1 ):
			a = math.radians( ang_deg + rng.uniform( -5, 5 ) )
			leaves.append( ( ( cx, y ), ( side * math.sin( a ), -math.cos( a ) ), L * rng.uniform( 0.92, 1.05 ), W ) )
	for base, d, L, W in leaves:
		poly = []
		nx, ny = -d[1], d[0]
		steps = 12
		left, right = [], []
		for i in range( 1, steps ):
			s = i / steps
			wv = W * math.sin( math.pi * min( 1.0, s * 1.05 ) ) ** 0.8 * ( 1.0 - 0.35 * s )
			tooth = 1.18 if i % 2 else 0.86
			left.append( ( base[0] + d[0] * L * s + nx * wv * tooth, base[1] + d[1] * L * s + ny * wv * tooth ) )
			right.append( ( base[0] + d[0] * L * s - nx * wv * tooth, base[1] + d[1] * L * s - ny * wv * tooth ) )
		tip = ( base[0] + d[0] * L, base[1] + d[1] * L )
		poly = [ base ] + left + [ tip ] + list( reversed( right ) )
		fill_poly( rgb, alpha, poly, RASP_TONES[rng.randrange( len( RASP_TONES ) )] )
		draw_line( rgb, alpha, [ base, ( base[0] + d[0] * L * 0.8, base[1] + d[1] * L * 0.8 ) ], 1.0, RASP_VEIN )
	draw_line( rgb, alpha, [ ( cx, h - 1 ), ( cx, petiole_top ) ], 1.0, RASP_PETIOLE )
	alpha[0, :] = alpha[:, 0] = alpha[:, -1] = False
	alpha[-1, :] = False
	alpha[-1, int( cx ) - 1:int( cx ) + 1] = True
	return rgb, alpha


def build_texture():
	rgb = np.zeros( ( TEX, TEX, 3 ), np.uint8 )
	mask = np.zeros( ( TEX, TEX ), np.uint8 )
	for i in range( SWATCH_COLS * SWATCH_ROWS ):
		key = PALETTE_KEYS[i] if i < len( PALETTE_KEYS ) else "fern"
		cx, cy = i % SWATCH_COLS, i // SWATCH_COLS
		rgb[cy * SWATCH:( cy + 1 ) * SWATCH, cx * SWATCH:( cx + 1 ) * SWATCH] = PALETTE[key]
	mask[0:SWATCH * SWATCH_ROWS, :] = 255

	tiles = {
		"frond_a": draw_frond( random.Random( 31 ), FROND_TONES, FROND_RIB, FROND_STIPE, 25, 1.0, 0.0 ),
		"frond_b": draw_frond( random.Random( 47 ), FROND_TONES, FROND_RIB, FROND_STIPE, 22, -1.2, 0.0 ),
		"frond_dry": draw_frond( random.Random( 59 ), DRY_TONES, DRY_RIB, ( 96, 70, 44 ), 23, 0.6, 0.6 ),
	}
	for name, seed, leaflets in ( ( "rasp_a", 71, 3 ), ( "rasp_b", 83, 5 ) ):
		t_rgb, t_a = draw_rasp_leaf( random.Random( seed ), leaflets )
		tiles[name] = ( t_rgb.repeat( 2, 0 ).repeat( 2, 1 ), t_a.repeat( 2, 0 ).repeat( 2, 1 ) )
	for name, ( t_rgb, t_a ) in tiles.items():
		x0, y0, w, h = TILE_RECTS[name]
		assert t_rgb.shape[:2] == ( h, w ), ( name, t_rgb.shape )
		rgb[y0:y0 + h, x0:x0 + w] = t_rgb
		mask[y0:y0 + h, x0:x0 + w] = np.where( t_a, 255, 0 )
	return rgb, mask


# --- Mesh helpers ----------------------------------------------------------------------------------

UP = Vector( ( 0, 0, 1 ) )


def cubic( p0, p1, p2, p3, t ):
	u = 1.0 - t
	return p0 * ( u * u * u ) + p1 * ( 3 * u * u * t ) + p2 * ( 3 * u * t * t ) + p3 * ( t * t * t )


def resample( ctrl, count: int, dense: int = 64 ):
	"""`count` + 1 points at equal arc length along a cubic bezier, plus its total length."""
	pts = [ cubic( *ctrl, i / dense ) for i in range( dense + 1 ) ]
	cum = [ 0.0 ]
	for a, b in zip( pts, pts[1:] ):
		cum.append( cum[-1] + ( b - a ).length )
	total = cum[-1]
	out = []
	j = 0
	for k in range( count + 1 ):
		target = total * k / count
		while j < dense - 1 and cum[j + 1] < target:
			j += 1
		seg = max( 1e-9, cum[j + 1] - cum[j] )
		out.append( pts[j].lerp( pts[j + 1], min( 1.0, max( 0.0, ( target - cum[j] ) / seg ) ) ) )
	return out, total


def card_strip( m, spine, tile: str, half_width: float, fold: float, roll: float, flip: bool = False ) -> None:
	"""Folded alpha card bent along `spine` (base → tip): three verts per row (left edge, fold line, right edge),
	the halves lifted by `fold` radians, the whole strip turned by `roll` about its spine. UV v runs base → tip."""
	m.new_part()
	u0, v0, u1, v1 = tile_uv( tile )
	if flip:
		u0, u1 = u1, u0
	um = ( u0 + u1 ) * 0.5
	n = len( spine )
	rows = []
	for i, p in enumerate( spine ):
		d = spine[min( i + 1, n - 1 )] - spine[max( i - 1, 0 )]
		d.normalize()
		side = d.cross( UP )
		if side.length < 1e-4:
			side = _perp( d )
		side.normalize()
		side = ( Matrix.Rotation( roll, 3, d ) @ side ).normalized()
		up = side.cross( d ).normalized()
		if up.z < 0:
			up = -up
		half = side * ( half_width * math.cos( fold ) )
		lift = up * ( half_width * math.sin( fold ) )
		rows.append( ( m.add_vert( p + half + lift ), m.add_vert( p ), m.add_vert( p - half + lift ) ) )
	for i in range( n - 1 ):
		va = v0 + ( v1 - v0 ) * i / ( n - 1 )
		vb = v0 + ( v1 - v0 ) * ( i + 1 ) / ( n - 1 )
		l0, c0, r0 = rows[i]
		l1, c1, r1 = rows[i + 1]
		m.add_face( ( c0, c1, l1, l0 ), "fern", uvs=[ ( um, va ), ( um, vb ), ( u0, vb ), ( u0, va ) ] )
		m.add_face( ( c0, r0, r1, c1 ), "fern", uvs=[ ( um, va ), ( u1, va ), ( u1, vb ), ( um, vb ) ] )


def flat_card( m, base: Vector, direction: Vector, length: float, tile: str, fold: float, roll: float ) -> None:
	"""Straight two-row folded card (raspberry leaves): width from the tile's aspect so texels stay square."""
	_, _, w, h = TILE_RECTS[tile]
	d = direction.normalized()
	spine = [ base, base + d * ( length * 0.5 ), base + d * length ]
	# a little droop toward the tip
	spine[1].z -= length * 0.03
	spine[2].z -= length * 0.1
	card_strip( m, spine, tile, length * w / h * 0.5, fold, roll )


def scale_flake( m, at: Vector, normal: Vector, size: float, swatch: str ) -> None:
	"""Tiny papery scale: one triangle lying on a surface."""
	m.new_part()
	a, b = frame( normal )
	ang = m.rng.uniform( 0, math.tau )
	da = a * math.cos( ang ) + b * math.sin( ang )
	db = a * math.cos( ang + 1.9 ) + b * math.sin( ang + 1.9 )
	off = normal * size * 0.15
	m.add_poly( ( at + off - da * size * 0.4, at + off + da * size, at + off + db * size * 0.55 ), swatch )


def crown( m, radius: float, stubs: int ) -> None:
	"""Scaly crown mound of duff with a few old stipe stubs poking up."""
	blob( m, Vector( ( 0, 0, radius * 0.1 ) ), Vector( ( radius, radius * 0.9, radius * 0.45 ) ),
		  ( "duff", "duff_dark", "scale_brown", "duff_light" ), sides=7, flat_bottom=True )
	for k in range( stubs ):
		ang = m.rng.uniform( 0, math.tau )
		r = radius * m.rng.uniform( 0.3, 0.75 )
		base = Vector( ( math.cos( ang ) * r, math.sin( ang ) * r, 0.0 ) )
		top = base + Vector( ( math.cos( ang ) * 0.02, math.sin( ang ) * 0.02, m.rng.uniform( 0.05, 0.11 ) ) )
		tube( m, [ base - Vector( ( 0, 0, ROOT_SINK ) ), top ], [ 0.009, 0.006 ], m.rng.choice( ( "stipe_brown", "dry_dark" ) ), sides=3 )
	for k in range( 5 ):
		ang = m.rng.uniform( 0, math.tau )
		r = radius * m.rng.uniform( 0.2, 0.9 )
		scale_flake( m, Vector( ( math.cos( ang ) * r, math.sin( ang ) * r, radius * 0.38 ) ), UP, radius * 0.45,
					 m.rng.choice( ( "scale_brown", "scale_light" ) ) )


# --- Sword fern ------------------------------------------------------------------------------------

def frond( m, yaw: float, pitch_deg: float, length: float, tile: str, root_r: float, droop: float = 0.0 ) -> None:
	"""One arching frond: cubic rachis leaving the crown at `pitch_deg`, cresting, the tip curving out and down.
	`droop` (0..1) flattens it toward the ground (dead fronds)."""
	out = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
	pitch = math.radians( pitch_deg )
	root = out * root_r + Vector( ( 0, 0, -ROOT_SINK ) )
	d0 = ( out * math.cos( pitch ) + UP * math.sin( pitch ) ).normalized()
	crest = length * ( 0.2 + 0.72 * math.sin( pitch ) ** 1.2 ) * ( 1.0 - 0.85 * droop )
	p1 = root + d0 * length * 0.62 * ( 1.0 - 0.6 * droop )
	p2 = root + out * length * ( 0.32 + 0.3 * math.cos( pitch ) ) + UP * crest
	p3 = root + out * length * ( 0.58 + 0.25 * math.cos( pitch ) ) + UP * max( 0.03, crest * ( 0.55 - 0.4 * droop ) )
	if droop:
		p1.z = min( p1.z, 0.08 )
	spine, total = resample( ( root, p1, p2, p3 ), 7 )
	k = length / total                                   # land the arc length on `length`
	spine = [ root + ( p - root ) * k for p in spine ]
	for p in spine:
		p.z = max( p.z, 0.015 if droop else p.z )
	radii = [ 0.0085 - 0.0065 * i / ( len( spine ) - 1 ) for i in range( len( spine ) ) ]
	tube( m, spine, radii, "stipe" if tile != "frond_dry" else "dry_dark", sides=3, cap=False )
	_, _, w, h = TILE_RECTS[tile]
	card_strip( m, spine, tile, length * w / h * 0.5, math.radians( m.rng.uniform( 16, 28 ) ) * ( 1.0 - droop ),
				m.rng.uniform( -0.25, 0.25 ), flip=m.rng.random() < 0.5 )


def sword_fern( m, fronds: int, length: tuple, dead: int = 0, lopsided: float = 0.0 ) -> None:
	crown( m, 0.075, 3 + dead )
	phase = m.rng.uniform( 0, math.tau )
	for i in range( fronds ):
		# inner ring stands up, the outer ring arches low — every third frond is an inner one
		inner = i % 3 == 0
		yaw = phase + ( i + m.rng.uniform( -0.35, 0.35 ) ) / fronds * math.tau
		if lopsided:
			yaw += lopsided * math.sin( yaw - phase )
		pitch = m.rng.uniform( 72, 84 ) if inner else m.rng.uniform( 48, 64 )
		L = m.rng.uniform( *length ) * ( 0.85 if inner else 1.0 )
		frond( m, yaw, pitch, L, m.rng.choice( ( "frond_a", "frond_b" ) ), m.rng.uniform( 0.01, 0.04 ) )
	for i in range( dead ):
		yaw = phase + math.pi + m.rng.uniform( -1.2, 1.2 )
		frond( m, yaw, m.rng.uniform( 10, 20 ), m.rng.uniform( *length ) * 0.9, "frond_dry", 0.04, droop=0.85 )


def build_fern_sword1( m ):
	sword_fern( m, 16, ( 1.15, 1.4 ) )


def build_fern_sword2( m ):
	sword_fern( m, 11, ( 0.9, 1.1 ) )


def build_fern_sword3( m ):
	sword_fern( m, 7, ( 0.55, 0.72 ) )


def build_fern_sword4( m ):
	sword_fern( m, 12, ( 1.0, 1.25 ), dead=3, lopsided=0.35 )


# --- Fiddleheads -------------------------------------------------------------------------------------

def crosier( m, root: Vector, yaw: float, height: float, coil_r: float, stem_r: float, turns: float ) -> None:
	"""One coiled shoot: a stalk with a soft S-lean rising into a tight flattened spiral (Archimedean, tightening
	inward), rolled-pinna ridges along both sides of the coil, a couple of papery scales low on the stalk."""
	out = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
	side = out.cross( UP ).normalized()
	lean = m.rng.uniform( 0.05, 0.18 )
	stalk_top = root + Vector( ( 0, 0, height - coil_r * 1.7 ) ) + out * height * lean
	mid = root + Vector( ( 0, 0, ( height - coil_r ) * 0.5 ) ) - out * height * lean * 0.4
	stalk = [ cubic( root - Vector( ( 0, 0, ROOT_SINK ) ), root + UP * height * 0.2, mid, stalk_top, i / 4 ) for i in range( 5 ) ]
	# coil: starts at the stalk top heading up, curls over toward -out, down and back under (centre above the top)
	centre = stalk_top + UP * coil_r * 0.05 - out * coil_r
	steps = 14
	coil = []
	for i in range( 1, steps + 1 ):
		t = i / steps
		theta = t * turns * math.tau
		r = coil_r * ( 1.0 - 0.78 * t )
		coil.append( centre + ( out * math.cos( theta ) + UP * math.sin( theta ) ) * r )
	pts = stalk + coil
	radii = [ stem_r * 1.15 ] + [ stem_r ] * 4 + [ stem_r * ( 2.0 - 1.25 * ( i / steps ) ** 0.8 ) for i in range( 1, steps + 1 ) ]
	m.new_part()
	fg.tube( m, pts, radii, "fiddle", sides=5 )
	# rolled pinnae: little ridges either side of the coil (lighter), so it reads as a curled frond not a hose
	for i in range( 0, steps - 3, 2 ):
		p = coil[i]
		d = ( coil[i + 1] - coil[i] ).normalized()
		rr = radii[5 + i]
		for sgn in ( -1, 1 ):
			b = p + side * sgn * rr * 0.95
			m.add_poly( ( b - d * rr * 0.9, b + side * sgn * rr * 0.4 + d * rr * 0.1, b + d * rr * 0.9 ),
						"fiddle_light" if i % 4 else "fiddle_tip" )
	# papery scales low on the stalk and one on the coil's back
	for k in range( m.rng.choice( ( 1, 2, 3 ) ) ):
		p = stalk[m.rng.randrange( 1, 4 )]
		n = ( out * m.rng.uniform( -1, 1 ) + side * m.rng.uniform( -1, 1 ) ).normalized()
		scale_flake( m, p + n * stem_r, n, stem_r * 1.3, m.rng.choice( ( "scale_brown", "scale_light" ) ) )
	scale_flake( m, coil[2] + ( coil[2] - centre ).normalized() * radii[7], ( coil[2] - centre ).normalized(), stem_r * 1.4, "scale_brown" )


def fiddlehead_cluster( m, count: int, height: tuple ) -> None:
	crown( m, 0.07, 4 )
	phase = m.rng.uniform( 0, math.tau )
	for i in range( count ):
		yaw = phase + ( i + m.rng.uniform( -0.3, 0.3 ) ) / count * math.tau
		r = 0.02 + 0.07 * m.rng.random()
		root = Vector( ( math.cos( yaw ) * r, math.sin( yaw ) * r, 0.0 ) )
		# a few are just breaking the soil, most stand up
		h = m.rng.uniform( *height ) * ( m.rng.uniform( 0.45, 0.6 ) if i % 4 == 3 else 1.0 )
		face = yaw + m.rng.uniform( -0.9, 0.9 )
		crosier( m, root, face, h, m.rng.uniform( 0.055, 0.07 ) * ( 0.8 if h < 0.18 else 1.0 ), m.rng.uniform( 0.011, 0.013 ),
				 m.rng.uniform( 1.7, 2.0 ) )


def build_fiddlehead1( m ):
	fiddlehead_cluster( m, 5, ( 0.24, 0.36 ) )


def build_fiddlehead2( m ):
	fiddlehead_cluster( m, 7, ( 0.22, 0.38 ) )


def build_fiddlehead3( m ):
	fiddlehead_cluster( m, 3, ( 0.26, 0.36 ) )


# --- Raspberry bush --------------------------------------------------------------------------------

def raspberry( m, top: Vector, r: float, ripe: str ) -> None:
	"""Hanging aggregate berry: attachment at the top, a rounded thimble of three jittered 7-rings whose faces
	alternate three tones (drupelets), five green calyx points splayed around the top."""
	m.new_part()
	tones = { "ripe": ( "berry", "berry_light", "berry_dark" ), "pink": ( "berry_pink", "berry_light", "berry_pink" ),
			  "green": ( "berry_green", "fiddle_light", "berry_green" ) }[ripe]
	sides = 6
	ph = m.rng.uniform( 0, math.tau )
	rings = []
	for z, s in ( ( -0.1, 0.85 ), ( -0.55, 1.0 ), ( -1.0, 0.82 ) ):
		rings.append( [ m.add_vert( top + Vector( ( math.cos( ph + ( i + 0.5 * ( z < -0.5 ) ) / sides * math.tau ) * r * s * m.rng.uniform( 0.9, 1.08 ),
													 math.sin( ph + ( i + 0.5 * ( z < -0.5 ) ) / sides * math.tau ) * r * s * m.rng.uniform( 0.9, 1.08 ),
													 z * r * 1.15 ) ) ) for i in range( sides ) ] )
	tip = m.add_vert( top + Vector( ( 0, 0, -r * 1.55 ) ) )
	cap = m.add_vert( top + Vector( ( 0, 0, r * 0.05 ) ) )
	for a, b in zip( rings, rings[1:] ):
		for i in range( sides ):
			n = ( i + 1 ) % sides
			m.add_face( ( a[i], a[n], b[n], b[i] ), tones[( i + len( rings ) ) % 3] )
	for i in range( sides ):
		n = ( i + 1 ) % sides
		m.add_face( ( rings[-1][n], rings[-1][i], tip ), tones[i % 3] )
		m.add_face( ( rings[0][n], rings[0][i], cap ), "calyx" )
	for k in range( 4 ):
		ang = ph + k / 4 * math.tau
		d = Vector( ( math.cos( ang ), math.sin( ang ), 0.15 ) )
		w = Vector( ( -math.sin( ang ), math.cos( ang ), 0 ) ) * r * 0.22
		m.add_poly( ( top - w, top + d * r * 1.25 + Vector( ( 0, 0, -r * 0.3 ) ), top + w ), "calyx" )


def prickles( m, pts, every: float, size: float ) -> None:
	"""Small backward-pointing prickles along a cane polyline, alternating around it."""
	m.new_part()
	acc = 0.0
	k = 0
	for a, b in zip( pts, pts[1:] ):
		seg = ( b - a ).length
		d = ( b - a ).normalized()
		pa, pb = frame( d )
		while acc < seg:
			p = a + d * acc
			ang = k * 2.4 + m.rng.uniform( -0.4, 0.4 )
			n = pa * math.cos( ang ) + pb * math.sin( ang )
			base = p + n * 0.008
			m.add_poly( ( base - d * size * 0.35, base + n * size - d * size * 0.45, base + d * size * 0.2 ), "thorn" )
			acc += every * m.rng.uniform( 0.7, 1.3 )
			k += 1
		acc -= seg


def raspberry_bush( m, canes: int, height: float, width: float, laterals: int, ripe_bias: float ) -> None:
	crown( m, 0.1, 0 )
	phase = m.rng.uniform( 0, math.tau )
	for c in range( canes ):
		yaw = phase + ( c + m.rng.uniform( -0.3, 0.3 ) ) / canes * math.tau
		out = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
		base = out * m.rng.uniform( 0.02, 0.1 ) - Vector( ( 0, 0, ROOT_SINK ) )
		h = height * m.rng.uniform( 0.8, 1.0 )
		reach = width * 0.5 * m.rng.uniform( 0.6, 0.85 )
		tip_z = h * m.rng.uniform( 0.25, 0.55 )
		ctrl = ( base, base + UP * h * 0.65 + out * reach * 0.1, base + out * reach * 0.55 + UP * h * 1.18,
				 base + out * reach + UP * tip_z )
		pts, total = resample( ctrl, 9 )
		radii = [ 0.016 - 0.011 * i / ( len( pts ) - 1 ) for i in range( len( pts ) ) ]
		tube( m, pts[:7], radii[:7], m.rng.choice( ( "cane", "cane_dark" ) ), sides=4, cap=False )
		tube( m, pts[6:], radii[6:], "cane_green", sides=4 )
		prickles( m, pts, 0.1, 0.026 )
		side = out.cross( UP ).normalized()
		# leaves along the cane, alternate, outward and a little up, skipping the bare base
		n_leaves = int( total / 0.11 )
		for i in range( n_leaves ):
			t = 0.25 + 0.75 * i / max( 1, n_leaves - 1 )
			p = cubic( *ctrl, t )
			sgn = 1 if i % 2 else -1
			d = ( side * sgn * 0.9 + out * 0.45 + UP * m.rng.uniform( -0.05, 0.35 ) ).normalized()
			L = m.rng.uniform( 0.32, 0.4 ) * ( 0.75 if t > 0.92 else 1.0 )
			stalk_end = p + d * 0.05
			tube( m, [ p, stalk_end ], [ 0.004, 0.003 ], "cane_green", sides=3, cap=False )
			flat_card( m, stalk_end, d, L, m.rng.choice( ( "rasp_a", "rasp_b", "rasp_b" ) ),
					   math.radians( m.rng.uniform( 14, 26 ) ), m.rng.uniform( -0.4, 0.4 ) )
		# fruiting laterals: short shoots off the upper side, leaves on top, berries hanging beneath
		for j in range( laterals ):
			t = m.rng.uniform( 0.35, 0.78 )
			p = cubic( *ctrl, t )
			sgn = m.rng.choice( ( -1, 1 ) )
			d = ( UP * 0.55 + side * sgn * 0.6 + out * 0.35 ).normalized()
			L = m.rng.uniform( 0.12, 0.2 )
			e = p + d * L
			tube( m, [ p, p.lerp( e, 0.5 ) + UP * 0.02, e ], [ 0.007, 0.0055, 0.004 ], "cane_green", sides=3 )
			for k in range( 2 ):
				ld = ( d * 0.5 + side * sgn * ( 0.6 if k == 0 else -0.3 ) + out * 0.4 ).normalized()
				flat_card( m, e, ld, m.rng.uniform( 0.3, 0.36 ), m.rng.choice( ( "rasp_a", "rasp_b" ) ),
						   math.radians( m.rng.uniform( 14, 26 ) ), m.rng.uniform( -0.4, 0.4 ) )
			for b in range( m.rng.choice( ( 2, 3, 3, 4 ) ) ):
				ang = m.rng.uniform( 0, math.tau )
				anchor = p.lerp( e, m.rng.uniform( 0.35, 1.0 ) )
				hang = anchor + Vector( ( math.cos( ang ) * 0.04, math.sin( ang ) * 0.04, -m.rng.uniform( 0.05, 0.09 ) ) )
				tube( m, [ anchor, hang ], [ 0.0035, 0.0025 ], "cane_green", sides=3, cap=False )
				roll = m.rng.random()
				state = "ripe" if roll < ripe_bias else ( "pink" if roll < ripe_bias + ( 1 - ripe_bias ) * 0.6 else "green" )
				raspberry( m, hang, m.rng.uniform( 0.023, 0.028 ), state )


def build_raspberry_bush1( m ):
	raspberry_bush( m, 7, 1.25, 1.8, 3, 0.75 )


def build_raspberry_bush2( m ):
	raspberry_bush( m, 5, 1.05, 1.45, 3, 0.7 )


def build_raspberry_bush3( m ):
	raspberry_bush( m, 8, 1.45, 2.0, 3, 0.8 )


# ( name, seed, builder [, uniform scale] ). Built at in-world size.
MODELS = [
	( "fern_sword1_v1", 601, build_fern_sword1 ),
	( "fern_sword2_v1", 602, build_fern_sword2 ),
	( "fern_sword3_v1", 603, build_fern_sword3 ),
	( "fern_sword4_v1", 604, build_fern_sword4 ),
	( "fiddlehead1_v1", 611, build_fiddlehead1, 1.3 ),     # x1.3 on top of ~3x life size: harvestable, must read at distance
	( "fiddlehead2_v1", 612, build_fiddlehead2, 1.3 ),
	( "fiddlehead3_v1", 613, build_fiddlehead3, 1.3 ),
	( "raspberry_bush1_v1", 621, build_raspberry_bush1 ),
	( "raspberry_bush2_v1", 622, build_raspberry_bush2 ),
	( "raspberry_bush3_v1", 623, build_raspberry_bush3 ),
]

# Hand-harvest nodes, the clover forage's ResourceItemDefinition rows with redwood ids. Fiddleheads pick like the
# mushroom / wheat (one tick, model hides, back in 300 s); raspberry bushes like the blueberry bush (three picks).
HARVEST = {
	"fiddlehead": {
		"ResourceId": "world_fiddlehead", "DisplayName": "Fiddleheads", "FallbackColor": "0.45,0.65,0.25,1",
		"Icon": "ui/items/food_fiddlehead.png",
		"HarvestYields": [ { "AmountHigh": 3, "AmountLow": 2, "ChancePercent": 100, "ResourceId": "food_fiddlehead" } ],
		"HarvestTicksUntilGone": 1, "HideModelWhenDepleted": True, "RespawnRateSeconds": 300,
	},
	"raspberry_bush": {
		"ResourceId": "world_raspberrybush", "DisplayName": "Raspberry Bush", "FallbackColor": "0.85,0.2,0.35,1",
		"Icon": "ui/items/food_raspberry.png",
		"HarvestYields": [
			{ "AmountHigh": 4, "AmountLow": 2, "ChancePercent": 100, "ResourceId": "food_raspberry" },
			{ "AmountHigh": 1, "AmountLow": 1, "ChancePercent": 30, "ResourceId": "seed_raspberry" },
		],
		"HarvestTicksUntilGone": 3, "HideModelWhenDepleted": False, "RespawnRateSeconds": 600,
	},
}


# --- Prefabs ---------------------------------------------------------------------------------------

def _guid( *parts ) -> str:
	return str( uuid.uuid5( uuid.NAMESPACE_URL, "survivalgame/redwood_plants/" + "/".join( parts ) ) )


def write_prefab( name: str ) -> str:
	components = [ {
		"__type": "Sandbox.ModelRenderer",
		"__guid": _guid( name, "renderer" ),
		"__enabled": True,
		"Flags": 0,
		"BodyGroups": 18446744073709551615,
		"CreateAttachments": False,
		"LodOverride": None,
		"MaterialGroup": None,
		"MaterialOverride": None,
		"Materials": None,
		"Model": f"{MODEL_REL_DIR}/{name}.vmdl",
		"OnComponentDestroy": None,
		"OnComponentDisabled": None,
		"OnComponentEnabled": None,
		"OnComponentFixedUpdate": None,
		"OnComponentStart": None,
		"OnComponentUpdate": None,
		"RenderOptions": { "GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False },
		"RenderType": "On",
		"Tint": "1,1,1,1",
	} ]
	kind = next( ( k for k in HARVEST if name.startswith( k ) ), None )
	if kind:
		h = HARVEST[kind]
		components.append( {
			"__type": "Survival.ResourceItemDefinition",
			"__guid": _guid( name, "harvest" ),
			"__enabled": True,
			"Flags": 0,
			"AutoEnsureTraceCollider": False,   # picked through the harvest registry, never a collider (Mark 2026-11-23)
			"BaseYieldPerTickHigh": 1,
			"BaseYieldPerTickLow": 1,
			"HarvestYields": h["HarvestYields"],
			"DisplayName": h["DisplayName"],
			"FallbackColor": h["FallbackColor"],
			"HandHarvestRange": 80,
			"Harvestable": True,
			"HarvestTicksUntilGone": h["HarvestTicksUntilGone"],
			"HideModelWhenDepleted": h["HideModelWhenDepleted"],
			"Icon": h["Icon"],
			"LogHarvest": False,
			"MaxStack": 20,
			"MinimumToolTier": 0,
			"OnComponentDestroy": None,
			"OnComponentDisabled": None,
			"OnComponentEnabled": None,
			"OnComponentFixedUpdate": None,
			"OnComponentStart": None,
			"OnComponentUpdate": None,
			"ResourceId": h["ResourceId"],
			"RespawnRateSeconds": h["RespawnRateSeconds"],
			"ToolTypeRequired": "Hand",
		} )
	doc = {
		"RootObject": {
			"__guid": _guid( name, "root" ),
			"__version": 2,
			"Flags": 0,
			"Name": name,
			"Position": "0,0,0",
			"Rotation": "0,0,0,1",
			"Scale": "1,1,1",
			"Tags": "",
			"Enabled": True,
			"NetworkMode": 1,
			"NetworkFlags": 0,
			"NetworkOrphaned": 0,
			"NetworkTransmit": True,
			"OwnerTransfer": 1,
			"Components": components,
			"Children": [],
			"__properties": {
				"NetworkInterpolation": True,
				"TimeScale": 1,
				"WantsSystemScene": True,
				"Metadata": {},
				"NavMesh": {
					"Enabled": False, "IncludeStaticBodies": True, "IncludeKeyframedBodies": True, "EditorAutoUpdate": False,
					"AgentHeight": 64, "AgentRadius": 16, "AgentStepSize": 18, "AgentMaxSlope": 40,
					"ExcludedBodies": "", "IncludedBodies": "", "DeferGeneration": False, "CustomBounds": False,
				},
			},
			"__variables": [],
		},
		"ResourceVersion": 2,
		"ShowInMenu": False,
		"MenuPath": None,
		"MenuIcon": None,
		"DontBreakAsTemplate": False,
		"__references": [],
		"__version": 2,
	}
	os.makedirs( PREFAB_DIR, exist_ok=True )
	path = os.path.join( PREFAB_DIR, f"{name}.prefab" )
	with open( path, "w", newline="\n" ) as f:
		json.dump( doc, f, indent=2 )
	return path


# --- Blender plumbing ------------------------------------------------------------------------------

def _get_material( palette_path: str, mask_path: str ):
	mat = bpy.data.materials.new( MATERIAL_NAME )
	mat.use_nodes = True
	mat.use_backface_culling = False
	nodes = mat.node_tree.nodes
	bsdf = nodes.get( "Principled BSDF" )
	tex = nodes.new( "ShaderNodeTexImage" )
	tex.image = bpy.data.images.load( palette_path, check_existing=True )
	tex.interpolation = "Closest"
	mat.node_tree.links.new( tex.outputs["Color"], bsdf.inputs["Base Color"] )
	msk = nodes.new( "ShaderNodeTexImage" )
	msk.image = bpy.data.images.load( mask_path, check_existing=True )
	msk.image.colorspace_settings.name = "Non-Color"
	msk.interpolation = "Closest"
	mat.node_tree.links.new( msk.outputs["Color"], bsdf.inputs["Alpha"] )
	bsdf.inputs["Roughness"].default_value = 0.9
	if hasattr( mat, "surface_render_method" ):
		mat.surface_render_method = "DITHERED"
	if hasattr( mat, "blend_method" ):
		try:
			mat.blend_method = "CLIP"
		except Exception:
			pass
	return mat


def to_object( name: str, m: ForageMesh, material ):
	mesh = bpy.data.meshes.new( name )
	mesh.from_pydata( [ tuple( v ) for v in m.verts ], [], m.faces )
	mesh.update()
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


def render_previews( objects, out_dir: str ) -> None:
	"""One three-quarter render per model (framed on its bounds) + a lineup, EEVEE, sky + duff ground + sun."""
	os.makedirs( out_dir, exist_ok=True )
	scene = bpy.context.scene
	for eng in ( "BLENDER_EEVEE", "BLENDER_EEVEE_NEXT" ):
		try:
			scene.render.engine = eng
			break
		except TypeError:
			pass
	scene.render.resolution_x = 900
	scene.render.resolution_y = 900
	world = bpy.data.worlds.new( "sky" )
	world.use_nodes = True
	world.node_tree.nodes["Background"].inputs[0].default_value = ( 0.55, 0.66, 0.78, 1 )
	world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
	scene.world = world
	sun = bpy.data.objects.new( "sun", bpy.data.lights.new( "sun", "SUN" ) )
	sun.data.energy = 3.5
	sun.rotation_euler = ( math.radians( 50 ), 0, math.radians( 35 ) )
	scene.collection.objects.link( sun )
	gm = bpy.data.meshes.new( "ground" )
	gm.from_pydata( [ ( -60, -60, 0 ), ( 60, -60, 0 ), ( 60, 60, 0 ), ( -60, 60, 0 ) ], [], [ ( 0, 1, 2, 3 ) ] )
	gmat = bpy.data.materials.new( "ground" )
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = ( 0.09, 0.055, 0.035, 1 )
	gm.materials.append( gmat )
	ground = bpy.data.objects.new( "ground", gm )
	scene.collection.objects.link( ground )
	cam = bpy.data.objects.new( "cam", bpy.data.cameras.new( "cam" ) )
	cam.data.lens = 50
	scene.collection.objects.link( cam )
	scene.camera = cam

	def frame_on( objs, yaw_deg: float, elev_deg: float, pad: float ):
		bpy.context.view_layer.update()
		pts = [ o.matrix_world @ v.co for o in objs for v in o.data.vertices ]
		lo = Vector( ( min( p.x for p in pts ), min( p.y for p in pts ), min( p.z for p in pts ) ) )
		hi = Vector( ( max( p.x for p in pts ), max( p.y for p in pts ), max( p.z for p in pts ) ) )
		c = ( lo + hi ) * 0.5
		radius = ( hi - lo ).length * 0.5 * pad
		dist = radius / math.tan( cam.data.angle * 0.5 )
		yaw, elev = math.radians( yaw_deg ), math.radians( elev_deg )
		cam.location = c + Vector( ( math.cos( yaw ) * math.cos( elev ), math.sin( yaw ) * math.cos( elev ), math.sin( elev ) ) ) * dist
		cam.rotation_euler = ( c - cam.location ).to_track_quat( "-Z", "Y" ).to_euler()

	for o in objects:
		o.hide_render = True
	for o in objects:
		o.hide_render = False
		saved = tuple( o.location )
		o.location = ( 0, 0, 0 )
		frame_on( [ o ], -60, 22, 1.05 )
		scene.render.filepath = os.path.join( out_dir, f"{o.name}.png" )
		bpy.ops.render.render( write_still=True )
		frame_on( [ o ], 30, 6, 0.95 )
		scene.render.filepath = os.path.join( out_dir, f"{o.name}_low.png" )
		bpy.ops.render.render( write_still=True )
		o.location = saved
		o.hide_render = True
	for o in objects:
		o.hide_render = False
	scene.render.resolution_x, scene.render.resolution_y = 2400, 1000
	frame_on( objects, -75, 25, 0.62 )
	scene.render.filepath = os.path.join( out_dir, "lineup.png" )
	bpy.ops.render.render( write_still=True )


def main() -> None:
	argv = sys.argv[sys.argv.index( "--" ) + 1:] if "--" in sys.argv else []
	render_dir = argv[argv.index( "--render" ) + 1] if "--render" in argv else None

	gp._reset_scene()
	os.makedirs( MAT_DIR, exist_ok=True )
	os.makedirs( MODEL_DIR, exist_ok=True )

	palette_path = os.path.join( MAT_DIR, PALETTE_PNG )
	mask_path = os.path.join( MAT_DIR, MASK_PNG )
	rgb, mask = build_texture()
	write_png( palette_path, rgb )
	write_png( mask_path, np.repeat( mask[..., None], 3, axis=2 ) )
	with open( os.path.join( MAT_DIR, f"{MATERIAL_NAME}.vmat" ), "w", newline="\n" ) as f:
		f.write( VMAT_TEXT.format( palette=f"{MAT_REL_DIR}/{PALETTE_PNG}", mask=f"{MAT_REL_DIR}/{MASK_PNG}" ) )
	material = _get_material( palette_path, mask_path )

	columns = 5
	objects = []
	for index, ( name, seed, build, *scale ) in enumerate( MODELS ):
		m = ForageMesh( seed )
		build( m )
		if scale:
			m.verts = [ v * scale[0] for v in m.verts ]
		obj = to_object( name, m, material )
		obj.location = ( ( index % columns ) * LAYOUT_SPACING, -( index // columns ) * LAYOUT_SPACING, 0.0 )
		objects.append( obj )
		xs = [ v.x for v in m.verts ]
		ys = [ v.y for v in m.verts ]
		zs = [ v.z for v in m.verts ]
		tris = sum( len( f ) - 2 for f in m.faces )
		print( f"[redwood_plants] {name}: {len( m.verts )} verts, {tris} tris, height {max( zs ):.3f} m ({max( zs ) * 40:.1f} u), "
			   f"footprint {max( xs ) - min( xs ):.2f} x {max( ys ) - min( ys ):.2f} m, min z {min( zs ):.3f}" )

	for obj in objects:
		gp.export_fbx( obj, MODEL_DIR )
		path = os.path.join( MODEL_DIR, f"{obj.name}.vmdl" )
		with open( path, "w", newline="\n" ) as f:
			f.write( gp.VMDL_TEMPLATE.format( material=f"{MAT_REL_DIR}/{MATERIAL_NAME}.vmat", fbx=f"{MODEL_REL_DIR}/{obj.name}.fbx" ) )
		print( f"[redwood_plants] prefab {write_prefab( obj.name )}" )

	os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
	if os.path.exists( BLEND_PATH ):
		os.remove( BLEND_PATH )
	bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
	print( f"[redwood_plants] saved {BLEND_PATH}" )

	if render_dir:
		render_previews( objects, render_dir )


if __name__ == "__main__":
	main()
