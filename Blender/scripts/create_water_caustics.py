"""
create_water_caustics.py — tileable caustics for the water beds (shaders/water_common.hlsl WaterCaustics).

Plain numpy, no Blender needed:  python Blender/scripts/create_water_caustics.py

Writes Assets/materials/water/water_caustics.png, 256x256, linear data (R = caustic light, 0..1).

Physically shaped: light is shot straight down through a tileable rippled water surface (band-limited height field),
each ray bent by the surface slope and carried a fixed depth, and where the rays land is counted. Focusing makes the
bright net of lines real caustics have; everything wraps, so the texture tiles. The shader scrolls two copies against
each other and takes their minimum, which animates the net the way moving water does.
"""
import os

import numpy as np
from PIL import Image

SIZE = 256
RAYS = 1024          # rays per side (RAYS² photons)
SEED = 2026
FOCUS = 1.1          # slope × depth in texels per unit slope: how hard the surface focuses
OUT = os.path.join( os.path.dirname( __file__ ), "..", "..", "Assets", "materials", "water", "water_caustics.png" )


def height_field( rng, n, peak_cycles, octave_sigma ):
	k = np.fft.fftfreq( n ) * n
	kx, ky = np.meshgrid( k, k )
	kr = np.sqrt( kx * kx + ky * ky )
	kr[0, 0] = 1e-6
	band = np.exp( -(np.log( kr / peak_cycles ) ** 2) / (2.0 * octave_sigma ** 2) )
	band[0, 0] = 0.0
	spec = (rng.normal( size=(n, n) ) + 1j * rng.normal( size=(n, n) )) * band / kr
	sx = np.real( np.fft.ifft2( 2j * np.pi * kx / n * spec ) )
	sy = np.real( np.fft.ifft2( 2j * np.pi * ky / n * spec ) )
	scale = np.std( np.sqrt( sx * sx + sy * sy ) ) + 1e-9
	return sx / scale, sy / scale


def main():
	rng = np.random.default_rng( SEED )
	sx, sy = height_field( rng, RAYS, 6.0, 0.30 )
	ys, xs = np.mgrid[0:RAYS, 0:RAYS].astype( np.float64 )
	# Land position in output texels (wrapped)
	lx = (xs * SIZE / RAYS + sx * FOCUS * SIZE / 64.0) % SIZE
	ly = (ys * SIZE / RAYS + sy * FOCUS * SIZE / 64.0) % SIZE
	density, _, _ = np.histogram2d( ly.ravel(), lx.ravel(), bins=SIZE, range=[[0, SIZE], [0, SIZE]] )
	density /= density.mean()
	# Soft 3x3 blur (wrapped) so single-texel spikes do not alias
	d = density
	d = (d + np.roll( d, 1, 0 ) + np.roll( d, -1, 0 )) / 3.0
	d = (d + np.roll( d, 1, 1 ) + np.roll( d, -1, 1 )) / 3.0
	# Map: the mean (1.0) lands mid-dark, the focused lines bright
	img = np.clip( (d - 0.6) / 2.4, 0.0, 1.0 ) ** 0.8
	out = (img * 255.0 + 0.5).astype( np.uint8 )
	rgba = np.stack( [out, out, out, np.full_like( out, 255 )], axis=-1 )
	os.makedirs( os.path.dirname( OUT ), exist_ok=True )
	Image.fromarray( rgba, "RGBA" ).save( OUT )
	print( "wrote", os.path.normpath( OUT ), "mean", float( img.mean() ) )


if __name__ == "__main__":
	main()
