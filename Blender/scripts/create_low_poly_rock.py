"""
Low-poly rock generator (triangle mesh only).

Run in Blender Scripting workspace, or:
  blender --background --python create_low_poly_rock.py
"""

import bpy
import bmesh
from mathutils import noise

# --- tweak these ---
OBJECT_NAME = "LowPolyRock"
SUBDIVISIONS = 1          # 0 = 20 tris, 1 = 80, 2 = 320 (keep at 1 for ~100 max)
RADIUS = 1.0
NOISE_SCALE = 2.5         # higher = smaller bumps
ROUGHNESS = 0.28          # displacement strength
BOTTOM_FLATTEN = 0.55     # squash verts below this Z threshold
RANDOM_SEED = 7
LOCATION = (0.0, 0.0, 0.0)


def _remove_object( name: str ) -> None:
	obj = bpy.data.objects.get( name )
	if obj is None:
		return
	mesh = obj.data
	bpy.data.objects.remove( obj, do_unlink=True )
	if mesh and mesh.users == 0:
		bpy.data.meshes.remove( mesh )


def _displace_rock_verts( bm: bmesh.types.BMesh, radius: float ) -> None:
	noise.seed_set( RANDOM_SEED )

	for vert in bm.verts:
		direction = vert.co.normalized()
		sample = vert.co * NOISE_SCALE
		n_vec = noise.noise_vector( sample )
		n = ( n_vec.x + n_vec.y + n_vec.z ) / 3.0

		up_bias = 1.0 + max( 0.0, direction.z ) * 0.35
		vert.co += direction * n * ROUGHNESS * radius * up_bias

		if vert.co.z < -radius * BOTTOM_FLATTEN:
			vert.co.z = -radius * BOTTOM_FLATTEN


def create_low_poly_rock() -> bpy.types.Object:
	_remove_object( OBJECT_NAME )

	bpy.ops.mesh.primitive_ico_sphere_add(
		subdivisions=SUBDIVISIONS,
		radius=RADIUS,
		location=LOCATION,
	)

	obj = bpy.context.active_object
	obj.name = OBJECT_NAME
	mesh = obj.data
	mesh.name = f"{OBJECT_NAME}_Mesh"

	bm = bmesh.new()
	bm.from_mesh( mesh )
	_displace_rock_verts( bm, RADIUS )
	bm.to_mesh( mesh )
	bm.free()
	mesh.update()

	obj.scale = ( 1.12, 0.92, 1.04 )
	bpy.context.view_layer.objects.active = obj
	obj.select_set( True )
	bpy.ops.object.transform_apply( location=False, rotation=False, scale=True )

	for poly in mesh.polygons:
		poly.use_smooth = False

	mesh.calc_normals()
	return obj


if __name__ == "__main__":
	rock = create_low_poly_rock()
	tri_count = len( rock.data.polygons )
	print( f"Created '{OBJECT_NAME}' — {tri_count} triangles." )
