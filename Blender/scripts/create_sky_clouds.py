"""
create_sky_clouds.py — tileable cloud noise for the sky dome (shaders/environment/sky_dome.shader).

Plain numpy, no Blender needed:  python Blender/scripts/create_sky_clouds.py

Writes Assets/materials/environment/sky_clouds.png, 256x256, linear data (not colour):
  R = big billowy layer (cumulus masses)      — low-frequency fbm
  G = fine wispy layer (streaks / breakup)    — higher-frequency fbm, stretched along X
  B = 0, A = 1

Both channels tile seamlessly (periodic lattice), so the shader can scroll them with the wind
forever. Coverage is NOT baked in: the shader thresholds the density with the preset's cloud
cover, so one texture serves clear days and overcast alike.
"""
import os

import numpy as np
from PIL import Image

SIZE = 256
SEED = 2026
OUT = os.path.join( os.path.dirname( __file__ ), "..", "..", "Assets", "materials", "environment", "sky_clouds.png" )


def periodic_value_noise( rng, size, cells_x, cells_y ):
	"""Smooth value noise on a lattice that wraps at the texture edge."""
	lattice = rng.random( (cells_y, cells_x) )
	ys = np.arange( size ) * cells_y / size
	xs = np.arange( size ) * cells_x / size
	y0 = np.floor( ys ).astype( int )
	x0 = np.floor( xs ).astype( int )
	ty = ys - y0
	tx = xs - x0
	ty = ty * ty * (3 - 2 * ty)
	tx = tx * tx * (3 - 2 * tx)
	y1 = (y0 + 1) % cells_y
	x1 = (x0 + 1) % cells_x
	y0 %= cells_y
	x0 %= cells_x

	a = lattice[np.ix_( y0, x0 )]
	b = lattice[np.ix_( y0, x1 )]
	c = lattice[np.ix_( y1, x0 )]
	d = lattice[np.ix_( y1, x1 )]
	top = a + (b - a) * tx[None, :]
	bot = c + (d - c) * tx[None, :]
	return top + (bot - top) * ty[:, None]


def fbm( rng, size, base_x, base_y, octaves, gain=0.5 ):
	total = np.zeros( (size, size) )
	amp = 1.0
	norm = 0.0
	fx, fy = base_x, base_y
	for _ in range( octaves ):
		total += periodic_value_noise( rng, size, fx, fy ) * amp
		norm += amp
		amp *= gain
		fx *= 2
		fy *= 2
	return total / norm


def normalise( x ):
	lo, hi = np.percentile( x, 1 ), np.percentile( x, 99 )
	return np.clip( (x - lo) / (hi - lo), 0, 1 )


def main():
	rng = np.random.default_rng( SEED )

	# Billowy masses: 5-octave fbm, slight gamma so the thresholded edges stay rounded.
	big = fbm( rng, SIZE, 4, 4, 5 )
	big = normalise( big )
	big = big ** 1.15

	# Wisps: stretched along X (cells 8 x 16) so they read as streaks when scrolled with wind.
	fine = fbm( rng, SIZE, 8, 16, 4, gain=0.55 )
	fine = normalise( fine )

	rgba = np.zeros( (SIZE, SIZE, 4), dtype=np.uint8 )
	rgba[..., 0] = (big * 255).round().astype( np.uint8 )
	rgba[..., 1] = (fine * 255).round().astype( np.uint8 )
	rgba[..., 3] = 255

	os.makedirs( os.path.dirname( OUT ), exist_ok=True )
	Image.fromarray( rgba, "RGBA" ).save( OUT )
	print( f"wrote {os.path.abspath( OUT )}" )


if __name__ == "__main__":
	main()
