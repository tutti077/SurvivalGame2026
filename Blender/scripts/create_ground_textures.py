"""
Ground tiles for the per-pixel biome blend (terrain splat). 2026-11-03 rewrite (Mark: "all the ground textures are
identical and everything looks cartoony from afar ... still in the 128 texel per meter range but they look more
realistic"; Clover grass a bit darker, the other biomes darker too).

Each tile is 512 x 512 and covers 4.0 m (PixelWorldUvMeters on terrain_ground.vmat / _far), so 128 texels per metre,
one texel ~7.8 mm. Every tile is seamless (all noise is periodic and every stamp wraps) and is built from real ground
structure instead of flat blotches:
  clover    short grass blades over dark thatch, trifoliate clover patches, bare soil showing through, dry blades
  redwood   reddish duff: layered fallen needles, twigs, the odd cone, dark earth in the gaps
  amber     dune sand: warped wind ripples (lit crest, shaded lee), grain speckle, a few small pebbles
  mountain  scree: worn stones of mixed sizes lit from one side, grit between them; light cool grey (whiter, never warm)
  sand      beach / lake bed sand: fine grain, low ripples, pebbles and shell bits
Up close the shader point-samples, so each texel stays a hard block; a quantise to a per-tile palette keeps the
texels flat colours rather than smooth gradients (the pixel look is asset-side). The tiles' mean colours are what
the ground reads as from afar, so those are set darker and less saturated than the old 64 px tiles.

Run from the repo root (plain Python, numpy + Pillow):
    python Blender/scripts/create_ground_textures.py
Writes Assets/materials/terrain/ground_<name>.png.
"""
import os
import sys
import numpy as np
from PIL import Image

SIZE = 512
OUT = os.path.join("Assets", "materials", "terrain")
PALETTE = 48                     # colours per tile after the quantise
LIGHT = np.array([-0.6, -0.8])   # baked light comes from the tile's -x / -y corner (direction it travels: +x +y)


# ------------------------------------------------------------------------------------------------ periodic helpers

def periodic_noise(rng, sigma):
    """White noise blurred by a Gaussian in the frequency domain: seamless by construction, zero mean, unit std."""
    white = rng.standard_normal((SIZE, SIZE))
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    kernel = np.exp(-2.0 * (np.pi ** 2) * (sigma ** 2) * (fx ** 2 + fy ** 2))
    n = np.real(np.fft.ifft2(np.fft.fft2(white) * kernel))
    return (n - n.mean()) / (n.std() + 1e-9)


def fbm(rng, sigmas, weights):
    n = sum(w * periodic_noise(rng, s) for s, w in zip(sigmas, weights))
    return (n - n.mean()) / (n.std() + 1e-9)


def srgb(c):
    return np.asarray(c, dtype=np.float64) / 255.0


def jitter(rng, base, n, amount):
    """n colours around base (sRGB 0..1): value jitter plus a little per-channel wander."""
    v = 1.0 + rng.normal(0.0, amount, (n, 1))
    ch = 1.0 + rng.normal(0.0, amount * 0.35, (n, 3))
    return np.clip(base[None, :] * v * ch, 0.0, 1.0)


def strokes(img, rng, count, length, colors, width=1, taper_dark=0.0, angle=None):
    """Straight wrapped strokes (blades, needles, twigs). length = (min, max) texels; colors (count, 3).
    taper_dark darkens the start of each stroke (a blade's root in shadow)."""
    x0 = rng.uniform(0, SIZE, count)
    y0 = rng.uniform(0, SIZE, count)
    a = rng.uniform(0, np.pi, count) if angle is None else angle
    L = rng.uniform(length[0], length[1], count)
    steps = int(np.ceil(length[1])) + 1
    t = np.linspace(0.0, 1.0, steps)[None, :]
    xs = x0[:, None] + np.cos(a)[:, None] * L[:, None] * (t - 0.5)
    ys = y0[:, None] + np.sin(a)[:, None] * L[:, None] * (t - 0.5)
    shade = (1.0 - taper_dark * (1.0 - t))[..., None]
    c = colors[:, None, :] * shade
    for w in range(width):
        ox = -np.sin(a)[:, None] * w
        oy = np.cos(a)[:, None] * w
        xi = np.floor(xs + ox).astype(int) % SIZE
        yi = np.floor(ys + oy).astype(int) % SIZE
        img[yi.ravel(), xi.ravel()] = c.reshape(-1, 3)


def disc_offsets(r):
    rr = int(np.ceil(r))
    dy, dx = np.mgrid[-rr:rr + 1, -rr:rr + 1]
    keep = dx * dx + dy * dy <= r * r + 0.25
    return dx[keep], dy[keep]


def stamp_discs(img, xs, ys, radii, colors, lit=0.0):
    """Wrapped filled discs; lit > 0 shades each disc as a little dome (bright toward the light, dark away)."""
    for x, y, r, c in zip(xs, ys, radii, colors):
        dx, dy = disc_offsets(r)
        d = (dx * LIGHT[0] + dy * LIGHT[1]) / max(r, 1.0)
        shade = 1.0 + lit * np.clip(d, -1.0, 1.0)
        xi = (int(x) + dx) % SIZE
        yi = (int(y) + dy) % SIZE
        img[yi, xi] = np.clip(c[None, :] * shade[:, None], 0.0, 1.0)


def voronoi(rng, count):
    """Periodic Voronoi: nearest-site index, distance to it, and the gap to the second site (0 on an edge)."""
    sites = rng.uniform(0, SIZE, (count, 2))
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    best = np.full((SIZE, SIZE), 1e9, np.float32)
    second = np.full((SIZE, SIZE), 1e9, np.float32)
    idx = np.zeros((SIZE, SIZE), np.int32)
    vx = np.zeros((SIZE, SIZE), np.float32)
    vy = np.zeros((SIZE, SIZE), np.float32)
    for i, (sx, sy) in enumerate(sites):
        ddx = (x - sx + SIZE / 2) % SIZE - SIZE / 2
        ddy = (y - sy + SIZE / 2) % SIZE - SIZE / 2
        d = np.sqrt(ddx * ddx + ddy * ddy)
        closer = d < best
        second = np.where(closer, best, np.minimum(second, d))
        best = np.where(closer, d, best)
        idx = np.where(closer, i, idx)
        vx = np.where(closer, ddx, vx)
        vy = np.where(closer, ddy, vy)
    return idx, best, second - best, vx, vy


def flatten(img, sigma=40.0):
    """Divide out brightness coarser than ~sigma texels (a periodic Gaussian low-pass), keeping the tile's mean. A tile
    repeats every 4 m, so any blotch near that size lines up into a grid from afar; the shader's macro variation owns
    the large scale instead."""
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    kernel = np.exp(-2.0 * (np.pi ** 2) * (sigma ** 2) * (fx ** 2 + fy ** 2))
    lum = img @ np.array([0.30, 0.59, 0.11])
    low = np.real(np.fft.ifft2(np.fft.fft2(lum) * kernel))
    return img * (lum.mean() / np.maximum(low, 1e-3))[..., None]   # brightness only: no hue shift


def quantise(img):
    im = Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")
    return np.asarray(im.quantize(PALETTE, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).convert("RGB"))


# ------------------------------------------------------------------------------------------------------- the tiles

def tile_clover(rng):
    # Dark thatch / soil under everything
    soil = fbm(rng, [2, 8, 30], [0.3, 0.4, 0.3])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((60, 58, 36))
    img *= (1.0 + 0.12 * soil)[..., None]
    # Bare soil: where the slow noise is low, fewer blades land (the soil there is browner)
    bare = fbm(rng, [7, 14], [0.5, 0.5])
    keep = 1.0 / (1.0 + np.exp(-(bare + 1.5) * 3.0))
    dirt = np.clip(1.0 - keep, 0, 1)[..., None]
    img = img * (1 - dirt) + srgb((62, 56, 40)) * (1.0 + 0.1 * soil)[..., None] * dirt
    # Grass: many short blades (3–9 cm), each one a slightly different green; the root end darker
    greens = [srgb((76, 104, 48)), srgb((68, 96, 44)), srgb((86, 114, 52)), srgb((60, 86, 40))]
    n = 52000
    base = np.array(greens)[rng.integers(0, len(greens), n)]
    cols = np.clip(base * (1.0 + rng.normal(0, 0.10, (n, 1))), 0, 1)
    dry = rng.random(n) < 0.07
    cols[dry] = jitter(rng, srgb((104, 96, 58)), int(dry.sum()), 0.10)
    bx = rng.uniform(0, SIZE, n).astype(int)
    by = rng.uniform(0, SIZE, n).astype(int)
    land = rng.random(n) < keep[by, bx]
    # draw in two passes so the paler top layer sits over the darker under-layer
    under = land & (rng.random(n) < 0.5)
    for sel, k in ((under, 0.82), (land & ~under, 1.0)):
        strokes_at(img, rng, bx[sel], by[sel], (4, 11), cols[sel] * k, taper_dark=0.35)
    # Clover patches: trifoliate leaves (three leaflets round a centre), bluer and paler than the grass
    patch = fbm(rng, [6, 12], [0.6, 0.4])
    cn = 2600
    cx = rng.uniform(0, SIZE, cn)
    cy = rng.uniform(0, SIZE, cn)
    inside = patch[cy.astype(int), cx.astype(int)] > 0.55
    cx, cy = cx[inside], cy[inside]
    leaf = jitter(rng, srgb((70, 100, 50)), len(cx), 0.08)
    for x, y, c in zip(cx, cy, leaf):
        a0 = rng.uniform(0, 2 * np.pi)
        r = rng.uniform(1.6, 2.4)
        for k in range(3):
            a = a0 + k * 2 * np.pi / 3
            lx, ly = x + np.cos(a) * r * 1.1, y + np.sin(a) * r * 1.1
            stamp_discs(img, [lx], [ly], [r], [c], lit=0.25)
        img[int(y) % SIZE, int(x) % SIZE] = c * 0.6
    # Fine speckle: specks of seed / grit and deep shadow pockets
    sp = rng.random((SIZE, SIZE))
    img[sp < 0.012] *= 0.55
    img[sp > 0.995] = img[sp > 0.995] * 0.4 + srgb((96, 88, 60)) * 0.6
    return img


def strokes_at(img, rng, x0, y0, length, colors, taper_dark=0.0):
    """strokes() at given start points (centres)."""
    count = len(x0)
    a = rng.uniform(0, np.pi, count)
    L = rng.uniform(length[0], length[1], count)
    steps = int(np.ceil(length[1])) + 1
    t = np.linspace(0.0, 1.0, steps)[None, :]
    xs = x0[:, None] + np.cos(a)[:, None] * L[:, None] * (t - 0.5)
    ys = y0[:, None] + np.sin(a)[:, None] * L[:, None] * (t - 0.5)
    c = colors[:, None, :] * (1.0 - taper_dark * (1.0 - t))[..., None]
    xi = np.floor(xs).astype(int) % SIZE
    yi = np.floor(ys).astype(int) % SIZE
    img[yi.ravel(), xi.ravel()] = c.reshape(-1, 3)


def tile_redwood(rng):
    soil = fbm(rng, [3, 12, 40], [0.3, 0.4, 0.3])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((40, 28, 21))
    img *= (1.0 + 0.15 * soil)[..., None]
    # Needle litter in layers: old grey-brown underneath, fresher red-brown on top
    layers = [((70, 50, 38), 0.12, 26000, (5, 12)), ((86, 56, 40), 0.12, 24000, (6, 14)), ((100, 62, 42), 0.10, 9000, (6, 13))]
    for col, amt, n, length in layers:
        strokes(img, rng, n, length, jitter(rng, srgb(col), n, amt), taper_dark=0.2)
    # Twigs: longer, two texels wide, grey-brown, lit on one edge
    n = 70
    strokes(img, rng, n, (22, 60), jitter(rng, srgb((76, 60, 48)), n, 0.08), width=2, taper_dark=0.15)
    strokes(img, rng, n // 2, (14, 40), jitter(rng, srgb((60, 46, 36)), n // 2, 0.08), width=1)
    # Small cones (redwood cones are ~2.5 cm: 3–4 texels round), dome-lit
    n = 28
    stamp_discs(img, rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n), rng.uniform(2.0, 3.2, n),
                jitter(rng, srgb((96, 62, 44)), n, 0.08), lit=0.4)
    # Shadow pockets and damp patches
    damp = fbm(rng, [30, 80], [0.5, 0.5])
    img *= (1.0 - 0.10 * np.clip(damp, 0, 2))[..., None]
    sp = rng.random((SIZE, SIZE))
    img[sp < 0.02] *= 0.6
    return img


def tile_amber(rng):
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
    warp = fbm(rng, [20, 50], [0.6, 0.4])
    # Ripples ~13 cm apart (integer wave counts keep it seamless), asymmetric: long lit stoss, short dark lee
    phase = 2 * np.pi * (24 * x + 10 * y) + 1.6 * warp
    s = (phase / (2 * np.pi)) % 1.0
    ramp = np.where(s < 0.75, s / 0.75, 1.0 - (s - 0.75) / 0.25)
    ripple = (ramp - 0.5) * 2.0
    strength = np.clip(0.7 + 0.3 * fbm(rng, [14], [1.0]), 0.1, 1.0)
    tone = fbm(rng, [4, 18, 60], [0.25, 0.35, 0.4])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((168, 122, 72))
    img *= (1.0 + 0.10 * ripple * strength + 0.06 * tone)[..., None]
    # Grain: single-texel light and dark specks (quartz and dark minerals)
    sp = rng.random((SIZE, SIZE))
    img[sp < 0.05] *= 0.86
    img[sp > 0.96] *= 1.10
    img[(sp > 0.4985) & (sp < 0.5)] = srgb((88, 66, 46))
    # A few small pebbles
    n = 60
    stamp_discs(img, rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n), rng.uniform(1.2, 2.6, n),
                jitter(rng, srgb((140, 100, 64)), n, 0.12), lit=0.35)
    return img


def tile_mountain(rng):
    # Grit and dirt between the stones
    grit = fbm(rng, [1.5, 6, 30], [0.4, 0.3, 0.3])
    img = np.empty((SIZE, SIZE, 3))
    # Light, cool grey (Mark 2026-11-03: the mountains read "like piss, make it whiter instead of yellow"): no warm cast
    img[:] = srgb((126, 128, 132))
    img *= (1.0 + 0.15 * grit)[..., None]
    sp = rng.random((SIZE, SIZE))
    img[sp < 0.03] *= 0.75
    img[sp > 0.985] *= 1.15
    # Stones: a Voronoi of mixed sizes (pebbles to fist-sized), each with its own grey, shrunk so grit shows between,
    # corners rounded (the cell mask blurred and re-thresholded: worn scree, not shards), dome-shaded toward the light.
    # The small pass only fills the grit the big stones left.
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    soften = np.exp(-2.0 * (np.pi ** 2) * (2.0 ** 2) * (fx ** 2 + fy ** 2))
    taken = np.zeros((SIZE, SIZE), bool)
    greys = [srgb((172, 174, 178)), srgb((156, 158, 163)), srgb((186, 187, 190)), srgb((144, 146, 151)), srgb((165, 166, 170))]
    for count, gap, lit, share in ((220, 2.5, 0.22, 0.6), (900, 1.6, 0.18, 0.55)):
        idx, dist, edge, vx, vy = voronoi(rng, count)
        tones = np.array(greys)[rng.integers(0, len(greys), count)] * (1.0 + rng.normal(0, 0.05, (count, 1)))
        present = rng.random(count) < share
        raw = ((edge > gap) & present[idx]).astype(np.float64)
        stone = (np.real(np.fft.ifft2(np.fft.fft2(raw) * soften)) > 0.55) & ~taken
        r = dist + edge * 0.5 + 1e-3
        d = np.clip((vx * LIGHT[0] + vy * LIGHT[1]) / r, -1, 1)
        rim = np.clip(edge / 5.0, 0, 1)
        shade = 1.0 + lit * d - 0.12 * (1.0 - rim)
        col = tones[idx] * (shade * (1.0 + 0.06 * grit))[..., None]
        img = np.where(stone[..., None], col, img)
        taken |= stone
    return img


def tile_sand(rng):
    y, x = np.mgrid[0:SIZE, 0:SIZE] / SIZE
    warp = fbm(rng, [24, 60], [0.5, 0.5])
    ripple = np.sin(2 * np.pi * (6 * x + 30 * y) + 2.0 * warp)
    tone = fbm(rng, [3, 16, 70], [0.3, 0.3, 0.4])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((150, 136, 108))
    img *= (1.0 + 0.04 * ripple + 0.07 * tone)[..., None]
    sp = rng.random((SIZE, SIZE))
    img[sp < 0.06] *= 0.88
    img[sp > 0.95] *= 1.08
    img[(sp > 0.497) & (sp < 0.5)] = srgb((90, 82, 70))
    # Pebbles and shell bits
    n = 90
    stamp_discs(img, rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n), rng.uniform(1.0, 2.8, n),
                jitter(rng, srgb((118, 112, 102)), n, 0.15), lit=0.35)
    n = 30
    strokes(img, rng, n, (3, 6), jitter(rng, srgb((196, 186, 168)), n, 0.05), width=2)
    return img


TILES = {
    "clover": (tile_clover, 21),
    "redwood": (tile_redwood, 22),
    "amber": (tile_amber, 23),
    "mountain": (tile_mountain, 24),
    "sand": (tile_sand, 25),
}


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else OUT   # optional folder for trial runs
    os.makedirs(out, exist_ok=True)
    for name, (fn, seed) in TILES.items():
        img = quantise(flatten(fn(np.random.default_rng(seed))))
        path = os.path.join(out, f"ground_{name}.png")
        Image.fromarray(img, "RGB").save(path)
        print(f"{path}: {SIZE} px, mean sRGB {img.reshape(-1, 3).mean(0).round(1)}")


if __name__ == "__main__":
    main()
