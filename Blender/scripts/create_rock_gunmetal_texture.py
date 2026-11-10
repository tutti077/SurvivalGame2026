"""Gunmetal stone texture for the Clover Hills rocks and the stone spires (Mark 2026-11-02: "the big rock models have no
texture ... make the texture much darker, like gunmetal grey").

256 x 256, point-filtered on pixel_lit with PixelFlatShading, so the facets carry the shape and the texture only breaks the
flat colour up: four low-contrast gunmetal tones in soft blotches (two octaves of value noise, quantised) and a sparse,
connected crack net in a slightly darker tone with varied line weight - the elm-bark recipe Mark accepted. No speckle, no
hue shifts (both read as creepy).

    python Blender/scripts/create_rock_gunmetal_texture.py        (plain Python + numpy + Pillow; no Blender needed)

Writes Assets/materials/environment/cloverHills/rocks/rock_gunmetal.png and one .vmat per rock family (VMATS: the
one PNG tiled so each family lands at 128 texels / m).
"""
import os

import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT_DIR = os.path.join(ROOT, "Assets", "materials", "environment", "cloverHills", "rocks")
SIZE = 256
SEED = 7711

# Gunmetal: blue-leaning dark greys, light to dark (sRGB)
TONES = np.array([
    (74, 79, 86),
    (64, 69, 76),
    (55, 60, 66),
    (47, 51, 57),
], dtype=np.float32)
CRACK = np.array((36, 39, 44), dtype=np.float32)

# Mark 2026-11-02: "Textures on the rock should be 128 texels per meter so the lighting can do its thing". The rock models'
# UVs differ wildly (measured in-engine from the mesh: UV area vs surface area at the scale each kind is scattered), so each
# family gets its own tiling of the one PNG: tiling = 128 / measured texels per metre at 256 px.
TEXELS_PER_METER = 128
VMATS = [
    ("rock_gunmetal", 1.0, "the mineable set from create_mineable_rocks.py (UVs authored at 2 m per tile)"),
    ("rock_gunmetal_pebble", 0.23, "pebble1-3 + rockpickup1 (~550: the whole texture on one pebble)"),
]


def value_noise(rng, cells):
    """Tileable bilinear value noise on a cells x cells lattice, smoothstepped, 0..1."""
    lattice = rng.random((cells, cells)).astype(np.float32)
    coords = np.arange(SIZE, dtype=np.float32) * cells / SIZE
    i0 = np.floor(coords).astype(int) % cells
    i1 = (i0 + 1) % cells
    t = coords - np.floor(coords)
    t = t * t * (3 - 2 * t)
    a = lattice[np.ix_(i0, i0)]
    b = lattice[np.ix_(i0, i1)]
    c = lattice[np.ix_(i1, i0)]
    d = lattice[np.ix_(i1, i1)]
    ty = t[:, None]
    tx = t[None, :]
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty


def crack_net(rng):
    """A few random walks that branch: thin, connected, varied weight. Returns a 0..1 coverage mask."""
    mask = np.zeros((SIZE, SIZE), dtype=np.float32)
    walkers = []
    for _ in range(4):
        walkers.append([rng.random() * SIZE, rng.random() * SIZE, rng.random() * 2 * np.pi, 1 + int(rng.random() * 2), 70 + int(rng.random() * 60)])
    while walkers:
        x, y, ang, weight, steps = walkers.pop()
        for _ in range(steps):
            ang += (rng.random() - 0.5) * 0.32
            x = (x + np.cos(ang)) % SIZE
            y = (y + np.sin(ang)) % SIZE
            for ox in range(weight):
                for oy in range(weight):
                    mask[int(y + oy) % SIZE, int(x + ox) % SIZE] = 1.0
            if rng.random() < 0.012 and len(walkers) < 8:
                walkers.append([x, y, ang + (0.9 if rng.random() < 0.5 else -0.9), 1, 18 + int(rng.random() * 26)])
    return mask


def main():
    rng = np.random.default_rng(SEED)
    blotch = value_noise(rng, 6) * 0.65 + value_noise(rng, 14) * 0.35
    lo, hi = np.percentile(blotch, [3, 97])
    blotch = np.clip((blotch - lo) / max(1e-6, hi - lo), 0, 0.999)
    tone_index = (blotch * len(TONES)).astype(int)
    rgb = TONES[tone_index]

    cracks = crack_net(rng)
    rgb = rgb * (1 - cracks[..., None]) + CRACK * cracks[..., None]

    os.makedirs(OUT_DIR, exist_ok=True)
    png = os.path.join(OUT_DIR, "rock_gunmetal.png")
    Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").save(png)

    for name, scale, users in VMATS:
        vmat = os.path.join(OUT_DIR, name + ".vmat")
        with open(vmat, "w", encoding="utf-8", newline="\n") as f:
            f.write(
                "// Gunmetal stone (Blender/scripts/create_rock_gunmetal_texture.py): 256 px point-filtered texture, faceted,\n"
                f"// tiled x{scale:g} so {users} land at ~{TEXELS_PER_METER} texels / m.\n"
                "Layer0\n{\n"
                "\tshader \"shaders/pixel_lit.shader\"\n\n"
                "\tPixelRoughness \"0.900\"\n"
                "\tPixelFlatShading \"1.000\"\n"
                f"\tPixelTexCoordScale \"[{scale:.3f} {scale:.3f}]\"\n"
                "\tTextureColor \"materials/environment/cloverHills/rocks/rock_gunmetal.png\"\n"
                "}\n")
    print("wrote", png, ", ".join(v[0] for v in VMATS))


if __name__ == "__main__":
    main()
