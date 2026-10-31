"""
create_river_ripples.py — tileable ripple slopes for the river surface (shaders/riverwater.shader).

Plain numpy, no Blender needed:  python Blender/scripts/create_river_ripples.py

Writes Assets/materials/water/river_ripples.png, 256x256, linear data (not colour):
  RG = slope (dh/dx, dh/dy) of a fine, choppy height field     — the small wavelets
  BA = slope of a second, independent, broader height field     — the slow swells / boils

Each field is band-limited noise built in the frequency domain on whole-number wave vectors, so it tiles
perfectly and has no dominant direction or row. The shader samples the two layers at three scales
(2 m, 8 m sheared, 32 m transposed) that all divide the 64 m along-river UV wrap, and the texture's
mips average the slopes toward flat — far water goes glassy instead of aliasing into a pattern.
Slopes are stored as 0.5 + 0.5 * slope / SLOPE_RANGE.
"""
import os

import numpy as np
from PIL import Image

SIZE = 256
SEED = 2026
SLOPE_RANGE = 1.0   # the shader rescales; this only sets the 8-bit headroom
OUT = os.path.join( os.path.dirname( __file__ ), "..", "..", "Assets", "materials", "water", "river_ripples.png" )


def band_field( rng, size, peak_cycles, octave_sigma, stretch_x=1.0 ):
	"""Random height field whose energy sits around peak_cycles per tile (log-normal band), periodic by construction."""
	k = np.fft.fftfreq( size ) * size                       # whole cycles per tile
	kx, ky = np.meshgrid( k * stretch_x, k )
	kr = np.sqrt( kx * kx + ky * ky )
	kr[0, 0] = 1e-6
	band = np.exp( -(np.log( kr / peak_cycles ) ** 2) / (2.0 * octave_sigma ** 2) )
	band[0, 0] = 0.0
	# Height spectrum ~ band / k: slopes (ik * H) then carry a flat-ish band, no single wavelength dominates
	spectrum = (rng.normal( size=(size, size) ) + 1j * rng.normal( size=(size, size) )) * band / kr
	kxw, kyw = np.meshgrid( np.fft.fftfreq( size ) * size, np.fft.fftfreq( size ) * size )
	sx = np.real( np.fft.ifft2( 2j * np.pi * kxw * spectrum ) )
	sy = np.real( np.fft.ifft2( 2j * np.pi * kyw * spectrum ) )
	# Normalise so the 99.5th percentile slope magnitude fills the 8-bit range
	mag = np.sqrt( sx * sx + sy * sy )
	scale = np.percentile( mag, 99.5 )
	return sx / scale, sy / scale


def encode( s ):
	return np.clip( (0.5 + 0.5 * s / SLOPE_RANGE) * 255.0 + 0.5, 0, 255 ).astype( np.uint8 )


def main():
	rng = np.random.default_rng( SEED )
	# Fine chop: energy around 12 cycles per tile, wide band (6..30), crests slightly longer across the flow
	ax, ay = band_field( rng, SIZE, 12.0, 0.55, stretch_x=1.25 )
	# Broad swells / boils: around 4 cycles per tile, narrower band
	bx, by = band_field( rng, SIZE, 4.0, 0.45 )

	rgba = np.stack( [encode( ax ), encode( ay ), encode( bx ), encode( by )], axis=-1 )
	os.makedirs( os.path.dirname( OUT ), exist_ok=True )
	Image.fromarray( rgba, "RGBA" ).save( OUT )
	print( "wrote", os.path.normpath( OUT ) )


if __name__ == "__main__":
	main()
