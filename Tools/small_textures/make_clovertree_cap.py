"""Clover tree cut-face textures (Rumple's clover tree, bark_cap.vmat): bark_cap_color.png + rough_cap.png.

They were 4 x 4 flat swatches. Mark 2026-11-23: no environment texture under 128 px and no solid colours. The cap's UVs
are not laid out for rings, so this is a tileable, non-directional cut-wood grain around the old colour (156, 137, 120):
soft blotches, short fibre flecks, a few darker pores. Roughness gets the same blotches round the old value (219).

    python Tools/small_textures/make_clovertree_cap.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Assets", "materials", "environment", "cloverHills", "trees", "cloverTree")
N = 128


def value_noise(rng, cells):
	g = rng.random((cells, cells))
	t = np.arange(N) * cells / N
	i0 = np.floor(t).astype(int) % cells
	i1 = (i0 + 1) % cells
	f = t - np.floor(t)
	f = f * f * (3 - 2 * f)
	a = g[i0][:, i0] * (1 - f)[None, :] + g[i0][:, i1] * f[None, :]
	b = g[i1][:, i0] * (1 - f)[None, :] + g[i1][:, i1] * f[None, :]
	return a * (1 - f)[:, None] + b * f[:, None]


def main():
	rng = np.random.default_rng(404)
	blotch = 0.6 * value_noise(rng, 5) + 0.4 * value_noise(rng, 13)
	# four tones, like the other wood in the game: quantise the blotches, then flecks on top
	tone = np.digitize(blotch + (rng.random((N, N)) - 0.5) * 0.12, (0.38, 0.5, 0.62))
	ramp = np.array(((132, 113, 96), (148, 129, 112), (160, 141, 124), (174, 156, 138)), float)
	rgb = ramp[tone]
	for _ in range(220):                                    # short fibre flecks, 1 px wide, any direction
		x, y = rng.integers(0, N, 2)
		dx, dy = ((1, 0), (0, 1), (1, 1), (1, -1))[rng.integers(0, 4)]
		k = rng.choice((0.88, 1.08))
		for s in range(int(rng.integers(2, 6))):
			rgb[(y + dy * s) % N, (x + dx * s) % N] *= k
	pores = rng.random((N, N)) < 0.012
	rgb[pores] *= 0.7
	Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").save(os.path.join(OUT, "bark_cap_color.png"))
	rough = 219.0 + (blotch - 0.5) * 36.0 + (rng.random((N, N)) - 0.5) * 10.0
	rough[pores] = 240.0
	r8 = np.clip(rough, 0, 255).astype(np.uint8)
	Image.fromarray(np.dstack([r8, r8, r8]), "RGB").save(os.path.join(OUT, "rough_cap.png"))
	print("wrote bark_cap_color.png, rough_cap.png", N)


if __name__ == "__main__":
	main()
