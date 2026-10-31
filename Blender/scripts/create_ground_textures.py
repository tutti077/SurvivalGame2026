"""
Ground tiles for the per-pixel biome blend (terrain splat, 2026-10-24).

Each tile is 64 x 64, seamless, and drawn the way materials/lighttest/ground_grass.png is: two or three main
tones in soft blotches plus rare darker specks — a few flat colours, never a gradient (the pixel look is
asset-side: point-sampled low-res textures). Tiled every 3.2 m on the world XY plane like the grass.

Run from the repo root (plain Python, numpy + Pillow):
    python Blender/scripts/create_ground_textures.py
Writes Assets/materials/terrain/ground_<name>.png.
"""
import os
import numpy as np
from PIL import Image

SIZE = 64
OUT = os.path.join("Assets", "materials", "terrain")

# name: (tones light -> dark as sRGB, speck colour, blotch size in texels, speck share, seed, ripple)
TILES = {
    # Redwood forest floor: reddish-brown duff and needles under the big trees
    "redwood": ([(98, 48, 38), (88, 42, 34), (78, 36, 30)], (62, 28, 24), 1.4, 0.03, 11, None),
    # Amber dunes: orange sand with faint wind ripples
    "amber": ([(198, 138, 70), (188, 128, 62), (176, 118, 56)], (160, 104, 48), 1.2, 0.015, 12, (1, 3)),
    # Mountain: grey rock and gravel
    "mountain": ([(138, 136, 130), (128, 126, 121), (118, 116, 112)], (100, 98, 95), 2.0, 0.04, 13, None),
    # Beaches and lake / river beds: pale wet-able sand (TerrainChunkBiomeDisplay's bed sand)
    "sand": ([(168, 154, 124), (160, 146, 116), (150, 136, 108)], (136, 122, 96), 1.0, 0.02, 14, None),
}


def periodic_noise(rng, sigma):
    """White noise blurred by a Gaussian in the frequency domain: seamless by construction."""
    white = rng.standard_normal((SIZE, SIZE))
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    kernel = np.exp(-2.0 * (np.pi ** 2) * (sigma ** 2) * (fx ** 2 + fy ** 2))
    n = np.real(np.fft.ifft2(np.fft.fft2(white) * kernel))
    return (n - n.mean()) / (n.std() + 1e-9)


def make_tile(tones, speck, blotch, speck_share, seed, ripple):
    rng = np.random.default_rng(seed)
    n = periodic_noise(rng, blotch)
    if ripple is not None:
        # Wind ripples: a few whole waves across the tile (integer counts keep it seamless), warped by the noise
        y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
        kx, ky = ripple
        n = 0.6 * n + 0.9 * np.sin(2.0 * np.pi * (kx * x + ky * y) + 0.8 * periodic_noise(rng, 6.0))
    # Two main tones about half / half, the third a thinner band between them
    lo, hi = np.quantile(n, [0.42, 0.58])
    idx = np.where(n < lo, 2, np.where(n > hi, 0, 1))
    img = np.array(tones, dtype=np.uint8)[idx]
    specks = rng.random((SIZE, SIZE)) < speck_share
    img[specks] = speck
    return img


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, (tones, speck, blotch, speck_share, seed, ripple) in TILES.items():
        img = make_tile(tones, speck, blotch, speck_share, seed, ripple)
        path = os.path.join(OUT, f"ground_{name}.png")
        Image.fromarray(img, "RGB").save(path)
        mean = img.reshape(-1, 3).mean(0)
        print(f"{path}: mean sRGB {mean.round(1)}")


if __name__ == "__main__":
    main()
