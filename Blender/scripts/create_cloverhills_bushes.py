"""
Clover Hills leafy bushes: plain green shrubs, one bush per model (never a baked cluster), harvested by hand
for plant fiber (Mark 2026-11-19: "they should look like the other bushes we have just no blueberries ... low
poly, 128 texels per meter ... waist high to like shoulder height, maybe 5 variations").

Run headless from the repo root:
  "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_cloverhills_bushes.py"
  ... -- render <out dir>     also renders one 3/4 view PNG per bush into <out dir> (EEVEE, alpha-clipped cards)

Built from create_cloverhills_forage.py's blueberry bush (imported, never edited): the same vase of woody stems,
the same folded alpha leaf cards cut from the forage sheet's two leaf-sprig tiles, the same forage.vmat — minus
the berry clusters. Bigger bushes get more stems and more side branches, not bigger leaves.

Texel density: a sprig tile is drawn at 64 texels (stored doubled at 128 px, point sampled), and every card here
is LEAF_CARD_METERS = 0.5 m long, so the leaves land at 64 / 0.5 = 128 texels per metre. The builder is fitted
to its target height and width by re-running it with adjusted stem lengths (the seed keeps the same branching),
never by scaling the finished mesh — scaling would change the card size and with it the texel density.

  name               height   footprint   look
  leafy_bush1_v1     1.00 m   1.36 m      round, low (waist high)
  leafy_bush2_v1     1.12 m   1.65 m      wide and spreading
  leafy_bush3_v1     1.25 m   1.48 m      upright vase
  leafy_bush4_v1     1.36 m   1.40 m      lopsided, leaning one way
  leafy_bush5_v1     1.48 m   1.60 m      tall, full (shoulder high on the 1.8 m pawn)

Outputs
-------
  Assets/models/environment/cloverHills/groundFoliage/leafy_bush<N>_v1.fbx + .vmdl   (forage.vmat, import_scale 0.4)
  Blender/blenderprojects/cloverhills_bushes.blend   (all bushes on a row)
"""

import math
import os
import sys

import bpy
from mathutils import Vector

sys.dont_write_bytecode = True
sys.path.insert( 0, os.path.dirname( os.path.abspath( __file__ ) ) )
import create_cloverhills_forage as fg      # noqa: E402  (helpers only — its main() never runs on import)
from create_ground_plants import curve_points, _bezier, ROOT_SINK  # noqa: E402

gp = fg.gp
BLEND_PATH = os.path.join( fg._REPO_ROOT, "Blender", "blenderprojects", "cloverhills_bushes.blend" )
LAYOUT_SPACING = 2.2

LEAF_CARD_METERS = 0.5          # 64 drawn texels / 0.5 m = 128 texels per metre
SPRIG_TEXELS = 64


def leafy_bush( m, height: float, width: float, stems: int, side_ts, lean: Vector, skirt: int ) -> None:
	"""Vase of woody stems; each branch tip carries a fan of folded leaf cards, single cards back along each
	stem, a ring of low skirt cards round the base. `lean` (metres at the top) bends every stem one way."""
	tips = []
	wood_points = []
	phase = m.rng.uniform( 0, math.tau )
	stem_r = 0.010 + 0.006 * height
	for s in range( stems ):
		ang = phase + s / stems * math.tau + m.rng.uniform( -0.3, 0.3 )
		out = Vector( ( math.cos( ang ), math.sin( ang ), 0 ) )
		base = out * m.rng.uniform( 0.03, 0.10 ) - Vector( ( 0, 0, ROOT_SINK ) )
		h = height * m.rng.uniform( 0.6, 0.8 )
		spread = width * 0.5 * m.rng.uniform( 0.4, 0.72 )
		p1 = base + out * spread * 0.25 + lean * 0.35 + Vector( ( 0, 0, h * 0.55 ) )
		p2 = base + out * spread + lean * ( h / height ) + Vector( ( 0, 0, h ) )
		pts = curve_points( base, p1, p2, 4 )
		fg.tube( m, pts, [ stem_r * k for k in ( 1.0, 0.86, 0.72, 0.58, 0.45 ) ], m.rng.choice( ( "wood", "wood_dark" ) ), sides=4 )
		tips.append( ( p2, ( p2 - _bezier( base, p1, p2, 0.85 ) ).normalized() ) )
		for t in side_ts:
			c = _bezier( base, p1, p2, t + m.rng.uniform( -0.05, 0.05 ) )
			side_ang = ang + m.rng.choice( ( -1, 1 ) ) * m.rng.uniform( 0.6, 1.2 )
			d = Vector( ( math.cos( side_ang ) * 0.9, math.sin( side_ang ) * 0.9, m.rng.uniform( 0.15, 0.4 ) + t * 0.5 ) ).normalized()
			L = height * m.rng.uniform( 0.16, 0.26 )
			e = c + d * L
			bend = c.lerp( e, 0.5 ) + Vector( ( 0, 0, L * 0.12 ) )
			fg.tube( m, curve_points( c, bend, e, 2 ), [ stem_r * 0.62, stem_r * 0.5, stem_r * 0.36 ], "wood", sides=3 )
			tips.append( ( e, d ) )
		wood_points.extend( pts[1:-1] )

	def card( base, direction ):
		L = LEAF_CARD_METERS * m.rng.uniform( 0.95, 1.05 )
		fg.leaf_card( m, base, direction, L, L * 0.95, m.rng.choice( ( "sprig_a", "sprig_b" ) ),
					  fold=math.radians( m.rng.uniform( 18, 32 ) ), roll=m.rng.uniform( -0.5, 0.5 ) )

	# Tip fans: 3-4 folded cards splayed round the branch direction.
	for tip, d in tips:
		n = m.rng.choice( ( 3, 4 ) )
		for k in range( n ):
			yaw = k / n * math.tau + m.rng.uniform( -0.5, 0.5 )
			sideways = Vector( ( math.cos( yaw ), math.sin( yaw ), 0 ) )
			cd = ( d * 0.8 + sideways * 0.75 + Vector( ( 0, 0, m.rng.uniform( -0.25, 0.3 ) ) ) ).normalized()
			card( tip - d * 0.04, cd )
	# Single cards back along each stem: outward from the bush axis, lower ones flatter.
	for p in wood_points:
		outward = Vector( ( p.x, p.y, 0 ) )
		ang = math.atan2( outward.y, outward.x ) + m.rng.uniform( -1.0, 1.0 ) if outward.length > 0.02 else m.rng.uniform( 0, math.tau )
		low = p.z < height * 0.4
		cd = Vector( ( math.cos( ang ), math.sin( ang ), m.rng.uniform( -0.15, 0.2 ) if low else m.rng.uniform( 0.1, 0.6 ) ) ).normalized()
		card( p, cd )
	# Skirt: cards from just above the root crown, almost flat, so the bush meets the ground instead of a bare stem.
	ph = m.rng.uniform( 0, math.tau )
	for k in range( skirt ):
		ang = ph + ( k + m.rng.uniform( -0.3, 0.3 ) ) / skirt * math.tau
		root = Vector( ( math.cos( ang ) * 0.06, math.sin( ang ) * 0.06, height * m.rng.uniform( 0.03, 0.09 ) ) )
		card( root, Vector( ( math.cos( ang ), math.sin( ang ), m.rng.uniform( 0.05, 0.4 ) ) ).normalized() )


# ( name, seed, target height m, target footprint m, stems, side branch params along each stem, lean, skirt cards )
BUSHES = [
	( "leafy_bush1_v1", 461, 1.00, 1.36, 6, ( 0.3, 0.6 ), Vector( ( 0, 0, 0 ) ), 6 ),
	( "leafy_bush2_v1", 462, 1.12, 1.65, 8, ( 0.25, 0.5, 0.72 ), Vector( ( 0, 0, 0 ) ), 8 ),
	( "leafy_bush3_v1", 463, 1.25, 1.48, 7, ( 0.35, 0.62, 0.8 ), Vector( ( 0, 0, 0 ) ), 6 ),
	( "leafy_bush4_v1", 464, 1.36, 1.40, 8, ( 0.28, 0.55, 0.78 ), Vector( ( 0.38, 0.12, 0 ) ), 7 ),
	( "leafy_bush5_v1", 465, 1.48, 1.60, 10, ( 0.25, 0.48, 0.7, 0.85 ), Vector( ( 0, 0, 0 ) ), 9 ),
]


def build_fitted( name, seed, target_h, target_w, stems, side_ts, lean, skirt ):
	"""Re-run the builder with adjusted height / width params until the real bounds hit the targets (cards keep 0.5 m)."""
	h, w = target_h * 0.75, target_w * 0.7
	m = None
	for _ in range( 8 ):
		m = fg.ForageMesh( seed )
		leafy_bush( m, h, w, stems, side_ts, lean, skirt )
		zs = [ v.z for v in m.verts ]
		xs = [ v.x for v in m.verts ]
		ys = [ v.y for v in m.verts ]
		got_h = max( zs )
		got_w = max( max( xs ) - min( xs ), max( ys ) - min( ys ) )
		if abs( got_h - target_h ) < 0.005 and abs( got_w - target_w ) < 0.01:
			break
		# cards add a near-constant 0.5 m reach on top of the skeleton: correct the skeleton part only
		h = max( 0.2, h + ( target_h - got_h ) * 1.3 )
		w = max( 0.2, w + ( target_w - got_w ) * 1.6 )
	return m


def _render_material( mat ):
	"""Blender-only preview: cut the cards with the forage mask so the render shows the leaf silhouettes."""
	mask_path = os.path.join( fg.MAT_DIR, fg.MASK_PNG )
	nodes = mat.node_tree.nodes
	bsdf = nodes.get( "Principled BSDF" )
	if bsdf is None or not os.path.exists( mask_path ):
		return
	tex = nodes.new( "ShaderNodeTexImage" )
	tex.image = bpy.data.images.load( mask_path, check_existing=True )
	tex.image.colorspace_settings.name = "Non-Color"
	tex.interpolation = "Closest"
	mat.node_tree.links.new( tex.outputs["Color"], bsdf.inputs["Alpha"] )
	if hasattr( mat, "surface_render_method" ):
		mat.surface_render_method = "DITHERED"
	if hasattr( mat, "blend_method" ):
		mat.blend_method = "CLIP"


def render_each( objects, out_dir ):
	os.makedirs( out_dir, exist_ok=True )
	scene = bpy.context.scene
	scene.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in { e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items } else "BLENDER_EEVEE_NEXT"
	scene.render.resolution_x = 900
	scene.render.resolution_y = 900
	scene.render.film_transparent = False
	world = bpy.data.worlds.new( "bush_world" ) if scene.world is None else scene.world
	scene.world = world
	world.use_nodes = True
	bg = world.node_tree.nodes.get( "Background" )
	if bg is not None:
		bg.inputs["Color"].default_value = ( 0.55, 0.68, 0.85, 1.0 )
		bg.inputs["Strength"].default_value = 0.9
	sun_data = bpy.data.lights.new( "sun", "SUN" )
	sun_data.energy = 3.5
	sun = bpy.data.objects.new( "sun", sun_data )
	sun.rotation_euler = ( math.radians( 50 ), 0, math.radians( 35 ) )
	scene.collection.objects.link( sun )
	# ground disc + a 1.8 m pawn-height post for scale
	bpy.ops.mesh.primitive_plane_add( size=6 )
	ground = bpy.context.active_object
	gmat = bpy.data.materials.new( "ground" )
	gmat.use_nodes = True
	gmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = ( 0.16, 0.24, 0.09, 1 )
	ground.data.materials.append( gmat )
	bpy.ops.mesh.primitive_cylinder_add( radius=0.04, depth=1.8, vertices=8 )
	post = bpy.context.active_object
	pmat = bpy.data.materials.new( "post" )
	pmat.use_nodes = True
	pmat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = ( 0.8, 0.1, 0.1, 1 )
	post.data.materials.append( pmat )
	cam_data = bpy.data.cameras.new( "cam" )
	cam_data.lens = 50
	cam = bpy.data.objects.new( "cam", cam_data )
	scene.collection.objects.link( cam )
	scene.camera = cam

	homes = { o.name: o.location.copy() for o in objects }
	for obj in objects:
		for o in objects:
			o.hide_render = o is not obj
			o.location = homes[o.name]
		obj.location = ( 0, 0, 0 )
		ground.location = ( 0, 0, 0 )
		post.location = ( 1.1, 0.6, 0.9 )
		target = Vector( ( 0.2, 0.15, 0.8 ) )
		cam.location = target + Vector( ( 3.3, -3.0, 0.9 ) )
		cam.rotation_euler = ( target - cam.location ).to_track_quat( "-Z", "Y" ).to_euler()
		scene.render.filepath = os.path.join( out_dir, f"{obj.name}.png" )
		bpy.ops.render.render( write_still=True )
		print( f"[bushes] rendered {scene.render.filepath}" )
	for o in objects:
		o.hide_render = False
		o.location = homes[o.name]


def main() -> None:
	argv = sys.argv[sys.argv.index( "--" ) + 1:] if "--" in sys.argv else []
	gp._reset_scene()
	os.makedirs( fg.MODEL_DIR, exist_ok=True )
	material = fg._get_material( os.path.join( fg.MAT_DIR, fg.PALETTE_PNG ) )

	objects = []
	for index, ( name, seed, th, tw, stems, side_ts, lean, skirt ) in enumerate( BUSHES ):
		m = build_fitted( name, seed, th, tw, stems, side_ts, lean, skirt )
		obj = fg.to_object( name, m, material )
		obj.location = ( index * LAYOUT_SPACING, 0.0, 0.0 )
		objects.append( obj )
		xs = [ v.x for v in m.verts ]
		ys = [ v.y for v in m.verts ]
		zs = [ v.z for v in m.verts ]
		tris = sum( len( f ) - 2 for f in m.faces )
		cards = sum( 1 for uv in m.face_uvs if uv ) // 2
		print( f"[bushes] {name}: {len( m.verts )} verts, {tris} tris, {cards} leaf cards, height {max( zs ):.3f} m ({max( zs ) * 40:.1f} u), "
			   f"footprint {max( xs ) - min( xs ):.2f} x {max( ys ) - min( ys ):.2f} m, leaves {SPRIG_TEXELS / LEAF_CARD_METERS:.0f} texels/m" )

	for obj in objects:
		gp.export_fbx( obj, fg.MODEL_DIR )
		path = os.path.join( fg.MODEL_DIR, f"{obj.name}.vmdl" )
		with open( path, "w", newline="\n" ) as f:
			f.write( gp.VMDL_TEMPLATE.format( material=f"{fg.MAT_REL_DIR}/{fg.MATERIAL_NAME}.vmat", fbx=f"{fg.MODEL_REL_DIR}/{obj.name}.fbx" ) )

	os.makedirs( os.path.dirname( BLEND_PATH ), exist_ok=True )
	if os.path.exists( BLEND_PATH ):
		os.remove( BLEND_PATH )
	bpy.ops.wm.save_as_mainfile( filepath=BLEND_PATH )
	print( f"[bushes] saved {BLEND_PATH}" )

	if len( argv ) >= 2 and argv[0] == "render":
		_render_material( material )
		render_each( objects, argv[1] )


if __name__ == "__main__":
	main()
