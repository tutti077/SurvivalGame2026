"""Far impostor card textures (plain Python + numpy, no Blender needed).

Writes Assets/materials/environment/cloverHills/trees/far_tree_card_{color,mask,normal}.png (256x256): a ragged round crown blob
(rows 0-191) and a bark strip (rows 205-255) with a sphere / cylinder tangent-space normal map, so the card is lit
like a crown from any sun angle instead of as a flat plate (Mark 2026-10-20: "VERY square ... the way the lighting
hits them makes them clearly not a part of the other trees"). Colours come from the elm textures (percentiles of
elm_leaves_v76.png, mean of elm_bark_v76.png); nothing here is per tree model — the card is a generic crown the
runtime scales / tints per prefab. Run from the repo root:  python Blender/scripts/create_far_card_textures.py
"""
import os, struct, zlib
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "materials", "environment", "cloverHills", "trees")
SIZE = 256
CROWN_ROWS = 192          # rows 0..191 = crown (v 0 .. 0.75)
TRUNK_ROW0 = 205          # rows 205..255 = trunk strip (v 0.8 .. 1.0)
CROWN_TOP = np.array([80, 122, 64], np.float32)   # 75th percentile of the elm leaf texture
CROWN_LOW = np.array([62, 102, 52], np.float32)   # 25th percentile
BARK = np.array([132, 117, 103], np.float32)      # elm bark mean
SPHERE_BIAS = 0.35                                # how much smooth-sphere normal survives under the random leaf facings


def write_png(path, rgb, alpha=None):
    h, w = rgb.shape[:2]
    if alpha is None:
        px = rgb.astype(np.uint8)
        ctype = 2
    else:
        px = np.concatenate([rgb.astype(np.uint8), alpha.astype(np.uint8)[..., None]], axis=2)
        ctype = 6
    raw = b"".join(b"\x00" + px[y].tobytes() for y in range(h))

    def chunk(t, b):
        return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, ctype, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def value_noise(shape, cell, rng):
    """Blocky low-frequency noise in 0..1: random grid values, nearest-neighbour upsampled (the pixel look)."""
    gh, gw = shape[0] // cell + 2, shape[1] // cell + 2
    grid = rng.random((gh, gw)).astype(np.float32)
    ys = (np.arange(shape[0]) // cell)
    xs = (np.arange(shape[1]) // cell)
    return grid[ys][:, xs]


def main():
    rng = np.random.default_rng(7601)
    color = np.zeros((SIZE, SIZE, 3), np.float32)
    mask = np.zeros((SIZE, SIZE), np.float32)
    normal = np.tile(np.array([0.5, 0.5, 1.0], np.float32), (SIZE, SIZE, 1))

    # ---- crown: an ellipse with a ragged rim (sum of sinusoids around the angle) ----
    cx, cy = SIZE / 2, CROWN_ROWS * 0.52
    rx, ry = SIZE * 0.47, CROWN_ROWS * 0.46
    yy, xx = np.mgrid[0:CROWN_ROWS, 0:SIZE].astype(np.float32)
    dx = (xx - cx) / rx
    dy = (yy - cy) / ry
    r = np.sqrt(dx * dx + dy * dy)
    ang = np.arctan2(dy, dx)
    rim = 1.0
    for k, (freq, amp) in enumerate(((3, 0.05), (5, 0.06), (8, 0.04), (13, 0.03))):
        rim = rim + amp * np.sin(freq * ang + rng.random() * 6.283)
    inside = r <= rim
    # a few bites along the rim so the silhouette is not one smooth bulb
    bites = value_noise((CROWN_ROWS, SIZE), 10, rng)
    inside &= ~((r > rim - 0.14) & (bites > 0.72))
    mask[:CROWN_ROWS][inside] = 1.0

    t = np.clip((yy - (cy - ry)) / (2 * ry), 0, 1)          # 0 top .. 1 bottom
    base = CROWN_TOP[None, None, :] * (1 - t[..., None]) + CROWN_LOW[None, None, :] * t[..., None]
    clumps = value_noise((CROWN_ROWS, SIZE), 6, rng) * 0.26 - 0.13   # ±13 % leaf clumps, 6 px blocks
    fine = value_noise((CROWN_ROWS, SIZE), 2, rng) * 0.10 - 0.05
    color[:CROWN_ROWS] = np.clip(base * (1 + clumps + fine)[..., None], 0, 255)

    # sphere normal (tangent space: +x = texture right, +y = texture up, +z = out of the card)
    nz = np.sqrt(np.clip(1 - np.clip(r / np.maximum(rim, 1e-3), 0, 1) ** 2, 0, 1))
    nx = np.clip(dx / np.maximum(rim, 1e-3), -1, 1)
    ny = -np.clip(dy / np.maximum(rim, 1e-3), -1, 1)       # texture row grows downward
    # The crown is a cloud of leaf cards facing every which way, and that random distribution — not a smooth
    # sphere — is what decides how it takes the sun at dawn, noon and dusk (a smooth-sphere card measured 0.5x
    # to 1.5x the ring's brightness across the cycle, 2026-10-20). So the card's normals are mostly random leaf
    # facings (one per 3 px clump, hemisphere around +z) with only a mild sphere bias for volume.
    leaf_x = (value_noise((CROWN_ROWS, SIZE), 3, rng) - 0.5) * 2.0
    leaf_y = (value_noise((CROWN_ROWS, SIZE), 3, rng) - 0.5) * 2.0
    leaf_z = value_noise((CROWN_ROWS, SIZE), 3, rng) * 0.9 + 0.1
    nx = nx * SPHERE_BIAS + leaf_x
    ny = ny * SPHERE_BIAS + leaf_y
    nz = nz * SPHERE_BIAS + leaf_z
    n = np.stack([nx, ny, nz], axis=-1)
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-5)
    normal[:CROWN_ROWS][inside] = (n * 0.5 + 0.5)[inside]

    # ---- trunk strip: bark colour, cylinder normal across the width ----
    yy2, xx2 = np.mgrid[TRUNK_ROW0:SIZE, 0:SIZE].astype(np.float32)
    streaks = value_noise((SIZE - TRUNK_ROW0, SIZE), 4, rng) * 0.20 - 0.10
    color[TRUNK_ROW0:] = np.clip(BARK[None, None, :] * (1 + streaks)[..., None], 0, 255)
    mask[TRUNK_ROW0:] = 1.0
    tx = (xx2 - SIZE / 2) / (SIZE / 2)
    tn = np.stack([tx, np.zeros_like(tx), np.sqrt(np.clip(1 - tx * tx, 0, 1))], axis=-1)
    normal[TRUNK_ROW0:] = tn * 0.5 + 0.5

    # outside the crown / trunk the colour is the nearest crown colour (no dark fringe when mips blend)
    fill = color[:CROWN_ROWS].copy()
    fill[~inside] = CROWN_LOW
    color[:CROWN_ROWS] = fill
    color[CROWN_ROWS:TRUNK_ROW0] = BARK

    os.makedirs(OUT, exist_ok=True)
    write_png(os.path.join(OUT, "far_tree_card_color.png"), color)
    write_png(os.path.join(OUT, "far_tree_card_mask.png"), np.repeat((mask * 255)[..., None], 3, axis=2))
    write_png(os.path.join(OUT, "far_tree_card_normal.png"), normal * 255)
    print(f"wrote far_tree_card_{{color,mask,normal}}.png to {OUT}: crown {int(inside.sum())} px of {CROWN_ROWS * SIZE}")


if __name__ == "__main__":
    main()
