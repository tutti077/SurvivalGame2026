"""
Ground tiles for the per-pixel biome blend (terrain splat). 2026-11-03 rewrite (Mark: "all the ground textures are
identical and everything looks cartoony from afar ... still in the 128 texel per meter range but they look more
realistic"; Clover grass a bit darker, the other biomes darker too).

Each tile is 512 x 512 and covers 4.0 m (PixelWorldUvMeters on terrain_ground.vmat / _far), so 128 texels per metre,
one texel ~7.8 mm. Every tile is seamless (all noise is periodic and every stamp wraps) and is built from real ground
structure instead of flat blotches:
  clover    short grass blades over dark thatch, trifoliate clover patches, bare soil showing through, dry blades
  redwood   redwood forest floor (2026-11-16 rewrite): needle litter in drifts, damp earth and root breaks, bark chips,
            twigs and two fallen sticks, the odd cone, moss creeping in, two redwood sorrel sprigs
  redwood_far   512 px over 32 m (16 texels / m), faded in with distance by the shader: moss carpets, bare earth,
            dry-duff drifts, sorrel patches, faint fallen branches; its mean is matched to the near redwood tile's
  slope_rock    every biome's 30–50° slopes (top projection): stones and slabs half buried in grit and dark earth
  cliff         every biome's ≥ 50° faces (side projections; +v / image rows = world UP, row 0 = bottom): natural
            rock face - uneven, pinching strata, irregular fractures, big unbroken faces, overhang shadows
  amber     dune sand: warped wind ripples (lit crest, shaded lee), grain speckle, a few small pebbles
  mountain  scree: worn stones of mixed sizes lit from one side, grit between them; light cool grey (whiter, never warm)
  sand      beach / lake bed sand: fine grain, low ripples, pebbles and shell bits
Up close the shader point-samples, so each texel stays a hard block; a quantise to a per-tile palette keeps the
texels flat colours rather than smooth gradients (the pixel look is asset-side). The tiles' mean colours are what
the ground reads as from afar, so those are set darker and less saturated than the old 64 px tiles.

Run from the repo root (plain Python, numpy + Pillow):
    python Blender/scripts/create_ground_textures.py                       every tile
    python Blender/scripts/create_ground_textures.py redwood redwood_far   just those (any tile names; the far tile alone
                                                                           matches the near ground_redwood.png on disk)
    ... [names] <folder>                                                   write to a trial folder instead
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


def periodic_blur(img, sigma):
    """Gaussian blur that wraps (keeps a tile seamless). img (H, W) or (H, W, 3)."""
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    k = np.exp(-2.0 * (np.pi ** 2) * (sigma ** 2) * (fx ** 2 + fy ** 2))
    if img.ndim == 2:
        return np.real(np.fft.ifft2(np.fft.fft2(img) * k))
    return np.stack([np.real(np.fft.ifft2(np.fft.fft2(img[..., c]) * k)) for c in range(img.shape[2])], -1)


def bent_path(rng, x0, y0, angle, length, bend, step=0.5):
    """Points along a gently bending line (a stick, a root): the heading wanders by about `bend` radians over its length."""
    n = max(2, int(length / step))
    turn = np.cumsum(rng.normal(0.0, bend / np.sqrt(n), n))
    a = angle + turn - turn.mean()
    xs = x0 + np.cumsum(np.cos(a) * step)
    ys = y0 + np.cumsum(np.sin(a) * step)
    return xs, ys, a


def draw_path(img, xs, ys, a, width, color, lit=0.0, taper=0.0):
    """A wrapped thick line along a path, shaded across its width toward the light (a round branch / root)."""
    n = len(xs)
    half = width / 2.0
    nx, ny = -np.sin(a), np.cos(a)
    t = np.linspace(0.0, 1.0, n)
    w_here = half * (1.0 - taper * t)
    for o in np.linspace(-half, half, max(2, int(np.ceil(width * 2)))):
        across = (nx * LIGHT[0] + ny * LIGHT[1]) * (o / max(half, 0.5))
        keep = np.abs(o) <= w_here + 0.25
        shade = 1.0 + lit * np.clip(across, -1, 1) - 0.18 * (np.abs(o) / max(half, 0.5)) ** 2
        xi = np.floor(xs + nx * o).astype(int)[keep] % SIZE
        yi = np.floor(ys + ny * o).astype(int)[keep] % SIZE
        img[yi, xi] = np.clip(color[None, :] * shade[keep][:, None], 0, 1)


def tile_redwood(rng):
    """Redwood forest floor (2026-11-16, Mark: "a more advanced ground texture"): damp dark earth and root breaks under
    layered needle litter that lies in drifts (a slow direction field, not random), bark chips and fibrous flakes off the
    trunks, twigs, two larger fallen sticks, the odd cone, moss creeping in at patch edges and a couple of redwood sorrel
    sprigs. Mean colour kept at the old tile's dark reddish brown (~72 / 47 / 33): it is what the ground reads as from
    mid range, and the far tile (ground_redwood_far) is matched to it."""
    soil = fbm(rng, [2, 8, 30], [0.3, 0.4, 0.3])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((50, 34, 26))
    img *= (1.0 + 0.14 * soil)[..., None]

    # Roots breaking the surface: a few long, slowly bending, rounded runs, partly buried (litter is drawn over them)
    for _ in range(3):
        xs, ys, a = bent_path(rng, rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(0, np.pi), rng.uniform(220, 380), 1.2)
        draw_path(img, xs, ys, a, rng.uniform(4.0, 7.0), jitter(rng, srgb((74, 48, 34)), 1, 0.05)[0], lit=0.28, taper=0.5)

    # Needle direction field: litter settles in drifts, so neighbouring needles roughly agree
    field = periodic_blur(rng.standard_normal((SIZE, SIZE)), 28.0)
    field = field / (field.std() + 1e-9) * 1.1

    def needles(n, length, col, amt, spread, keep=None):
        x0 = rng.uniform(0, SIZE, n)
        y0 = rng.uniform(0, SIZE, n)
        if keep is not None:
            m = rng.random(n) < keep[y0.astype(int), x0.astype(int)]
            x0, y0 = x0[m], y0[m]
        ang = field[y0.astype(int), x0.astype(int)] + rng.normal(0.0, spread, len(x0))
        cols = jitter(rng, srgb(col), len(x0), amt)
        L = rng.uniform(length[0], length[1], len(x0))
        steps = int(np.ceil(length[1])) + 1
        t = np.linspace(0.0, 1.0, steps)[None, :]
        xs = x0[:, None] + np.cos(ang)[:, None] * L[:, None] * (t - 0.5)
        ys = y0[:, None] + np.sin(ang)[:, None] * L[:, None] * (t - 0.5)
        c = cols[:, None, :] * (1.0 - 0.18 * (1.0 - t))[..., None]
        img[np.floor(ys).astype(int).ravel() % SIZE, np.floor(xs).astype(int).ravel() % SIZE] = c.reshape(-1, 3)

    # Damp earth shows through where the litter is thin
    thin = fbm(rng, [10, 24], [0.5, 0.5])
    cover = np.clip(1.0 / (1.0 + np.exp(-(thin + 0.9) * 2.6)), 0.15, 1.0)

    # Old grey-brown needles at the bottom
    needles(26000, (5, 12), (68, 50, 40), 0.10, 0.7, cover)

    # Moss creeping in at patch edges: low, soft olive (not bright), drawn as tiny tufts so it stays texel-clumped
    moss = fbm(rng, [14, 34], [0.6, 0.4])
    moss_mask = np.clip((moss - 0.85) * 2.2, 0, 1)
    mn = 30000
    mx = rng.uniform(0, SIZE, mn)
    my = rng.uniform(0, SIZE, mn)
    core = np.clip((moss_mask - 0.35) * 2.5, 0, 1)[..., None]
    moss_tone = srgb((58, 68, 36)) * (1.0 + 0.10 * fbm(rng, [1.5, 4], [0.5, 0.5]))[..., None]
    img = img * (1 - core) + moss_tone * core
    m = rng.random(mn) < moss_mask[my.astype(int), mx.astype(int)]
    stamp_discs(img, mx[m], my[m], rng.uniform(0.8, 1.8, int(m.sum())), jitter(rng, srgb((64, 76, 38)), int(m.sum()), 0.07), lit=0.25)

    # Fresher red-brown needles on top (thinner where the moss is)
    top = cover * (1.0 - 0.75 * moss_mask)
    needles(22000, (6, 14), (92, 60, 42), 0.10, 0.55, top)
    needles(9000, (6, 13), (106, 66, 45), 0.09, 0.5, top)

    # Bark chips and fibrous flakes off the trunks: short, wider strips of red-brown on top of the litter, lit on one edge
    for _ in range(170):
        xs, ys, a = bent_path(rng, rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(0, np.pi), rng.uniform(6, 15), 0.2)
        draw_path(img, xs, ys, a, rng.uniform(2.0, 3.4), jitter(rng, srgb((112, 60, 40)), 1, 0.08)[0], lit=0.3)

    # Twigs: two texels wide, grey-brown, lit on one edge; thinner dark ones
    for _ in range(46):
        xs, ys, a = bent_path(rng, rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(0, np.pi), rng.uniform(22, 60), 0.5)
        draw_path(img, xs, ys, a, 2.0, jitter(rng, srgb((80, 62, 48)), 1, 0.08)[0], lit=0.3, taper=0.4)
    strokes(img, rng, 40, (14, 40), jitter(rng, srgb((60, 46, 36)), 40, 0.08), width=1)

    # Two larger fallen sticks (~3–4 cm thick, 1.5–2 m long), each with a stub or two
    for _ in range(2):
        xs, ys, a = bent_path(rng, rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(0, np.pi), rng.uniform(190, 260), 0.35)
        col = jitter(rng, srgb((98, 80, 64)), 1, 0.05)[0]
        draw_path(img, xs, ys, a, rng.uniform(4.0, 5.0), col, lit=0.35, taper=0.35)
        for k in rng.integers(20, len(xs) - 20, 2):
            sa = a[k] + rng.choice([-1, 1]) * rng.uniform(0.6, 1.0)
            bx, by, ba = bent_path(rng, xs[k], ys[k], sa, rng.uniform(16, 30), 0.3)
            draw_path(img, bx, by, ba, 2.0, col * 0.95, lit=0.3, taper=0.5)

    # Small cones (~2.5 cm: 3–4 texels), dome-lit, a dark scale tick in the middle
    n = 16
    cx, cy = rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n)
    stamp_discs(img, cx, cy, rng.uniform(2.0, 3.0, n), jitter(rng, srgb((98, 62, 44)), n, 0.08), lit=0.45)
    for a_, b_ in zip(cx, cy):
        img[int(b_) % SIZE, int(a_) % SIZE] *= 0.65

    # Redwood sorrel: two small sprigs of trifoliate leaves, green on top with the purple underside showing at the rims
    for _ in range(2):
        sx, sy = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        for _ in range(int(rng.integers(5, 8))):
            x0 = sx + rng.normal(0, 9)
            y0 = sy + rng.normal(0, 9)
            a0 = rng.uniform(0, 2 * np.pi)
            r = rng.uniform(2.2, 3.0)
            green = jitter(rng, srgb((70, 98, 48)), 1, 0.06)[0]
            for k in range(3):
                a = a0 + k * 2 * np.pi / 3
                lx, ly = x0 + np.cos(a) * r * 1.15, y0 + np.sin(a) * r * 1.15
                stamp_discs(img, [lx], [ly], [r + 0.6], [srgb((78, 50, 66))], lit=0.0)   # purple rim
                stamp_discs(img, [lx], [ly], [r], [green], lit=0.25)
            img[int(y0) % SIZE, int(x0) % SIZE] = green * 0.55

    # Shadow pockets in the litter (soft, not speckle) and damp darkening
    pockets = periodic_blur((rng.random((SIZE, SIZE)) < 0.012).astype(np.float64), 1.0)
    img *= (1.0 - np.clip(pockets * 3.6, 0, 0.32))[..., None]
    damp = fbm(rng, [30, 80], [0.5, 0.5])
    img *= (1.0 - 0.08 * np.clip(damp, 0, 2))[..., None]
    return img


# The near tile's mean colour (sRGB 0..255) the far tile is matched to; set by main() when the near tile is built or read.
_NEAR_MEAN = {}


def tile_redwood_far(rng):
    """Far redwood ground (2026-11-16): 512 px over 32 m (16 texels / m), faded in by the shader where the 4 m tile is
    under a pixel. It carries what a 4 m tile can't: soft moss carpets, bare darker earth, a lighter dry-duff drift,
    sorrel patches and faint debris lines (fallen branches). Its mean is matched to the near tile's so the fade never
    shifts the ground's colour."""
    base = srgb(_NEAR_MEAN.get("redwood", (72.0, 47.0, 33.0)))
    img = np.empty((SIZE, SIZE, 3))
    img[:] = base
    tone = fbm(rng, [3, 10, 40], [0.25, 0.35, 0.4])
    img *= (1.0 + 0.05 * tone)[..., None]

    def mask(sigmas, weights, lo, hi):
        n = fbm(rng, sigmas, weights)
        return np.clip((n - lo) / (hi - lo), 0, 1)

    # Lighter, warmer dry duff drifts
    dry = mask([30, 70], [0.5, 0.5], 0.3, 1.4)
    img = img * (1 - 0.6 * dry[..., None]) + (base * np.array([1.16, 1.12, 1.06])) * 0.6 * dry[..., None]
    # Bare, darker damp earth
    bare = mask([12, 36], [0.5, 0.5], 0.7, 1.8)
    img = img * (1 - 0.7 * bare[..., None]) + (base * np.array([0.80, 0.80, 0.84])) * 0.7 * bare[..., None]
    # Moss carpets: soft-edged, olive, about the same value as the duff (a hue drift, not a bright splash)
    moss = mask([18, 45], [0.6, 0.4], 0.8, 1.7)
    img = img * (1 - 0.7 * moss[..., None]) + np.array([0.25, 0.24, 0.14]) * 0.7 * moss[..., None]
    # Sorrel patches: small, a little brighter green, in or near the moss
    sorrel = mask([5, 10], [0.6, 0.4], 1.5, 2.3) * np.clip(moss * 1.5 + 0.2, 0, 1)
    img = img * (1 - 0.7 * sorrel[..., None]) + np.array([0.27, 0.33, 0.17]) * 0.7 * sorrel[..., None]
    # Faint debris lines: fallen branches 3–10 m long, slightly darker, one or two texels wide
    for _ in range(9):
        xs, ys, a = bent_path(rng, rng.uniform(0, SIZE), rng.uniform(0, SIZE), rng.uniform(0, np.pi), rng.uniform(50, 160), 0.3)
        draw_path(img, xs, ys, a, rng.uniform(1.0, 1.6), np.clip(base * 0.9, 0, 1), lit=0.1, taper=0.4)
    # Smooth (it is seen from 30 m+), then put the mean back on the near tile's exactly
    img = periodic_blur(img, 1.2)
    img *= (base / img.reshape(-1, 3).mean(0))[None, None, :]
    return img


TILE_FLATTEN = {"redwood_far": False, "cliff": False}   # the far tile's patches and the cliff's strata are the point of them: never flattened


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


def tile_slope_rock(rng):
    """Rocky ground for 30–50° slopes, every biome (2026-11-16, top projection, seen from above): broken soil with
    embedded stones and slabs of mixed sizes, many partly buried, grit and dark earth between them, a little dry grass
    and moss in the cracks. A neutral warm grey-brown between the biomes, darker than the mountain scree."""
    grit = fbm(rng, [1.5, 6, 30], [0.4, 0.3, 0.3])
    img = np.empty((SIZE, SIZE, 3))
    img[:] = srgb((94, 84, 71))                      # dark earth and grit
    img *= (1.0 + 0.12 * grit)[..., None]
    earth = fbm(rng, [10, 30], [0.5, 0.5])
    img *= (1.0 - 0.10 * np.clip(earth, 0, 2))[..., None]

    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    taken = np.zeros((SIZE, SIZE), bool)
    tones = [srgb((128, 118, 104)), srgb((116, 108, 96)), srgb((136, 126, 112)), srgb((108, 100, 90)), srgb((122, 114, 104))]
    # (sites, gap texels, dome light, share present, corner softening, buried share): slabs, then stones, then gravel
    facet_idx, _, _, _, _ = voronoi(rng, 700)
    facet = 1.0 + rng.normal(0.0, 0.05, 700)[facet_idx]          # each stone breaks into a few flat planes
    for count, gap, lit, share, soft, buried in ((40, 7.0, 0.16, 0.45, 2.0, 0.7),
                                                  (220, 4.0, 0.22, 0.45, 1.6, 0.5),
                                                  (1100, 2.2, 0.18, 0.30, 1.0, 0.3)):
        idx, dist, edge, vx, vy = voronoi(rng, count)
        tone = np.array(tones)[rng.integers(0, len(tones), count)] * (1.0 + rng.normal(0, 0.05, (count, 1)))
        present = rng.random(count) < share
        raw = ((edge > gap) & present[idx]).astype(np.float64)
        kernel = np.exp(-2.0 * (np.pi ** 2) * (soft ** 2) * (fx ** 2 + fy ** 2))
        body = np.real(np.fft.ifft2(np.fft.fft2(raw) * kernel))
        stone = (body > 0.55) & ~taken
        # Partly buried: soil covers a buried stone from one side (a random heading per stone), the cut line wobbled a little
        r = dist + edge * 0.5 + 1e-3
        sink = rng.random(count) < buried
        head = rng.uniform(0, 2 * np.pi, count)
        cut = rng.uniform(-0.3, 0.4, count)
        proj = (vx * np.cos(head)[idx] + vy * np.sin(head)[idx]) / r
        wobble = 0.25 * fbm(rng, [4, 10], [0.5, 0.5])
        stone &= ~(sink[idx] & (proj + wobble > cut[idx]))
        d = np.clip((vx * LIGHT[0] + vy * LIGHT[1]) / r, -1, 1)
        rim = np.clip(edge / 6.0, 0, 1)
        shade = 1.0 + lit * d - 0.14 * (1.0 - rim)
        col = tone[idx] * (shade * facet * (1.0 + 0.04 * grit))[..., None]
        img = np.where(stone[..., None], col, img)
        # A thin dark seam where a stone meets the soil (it sits in the ground, not on it)
        seam = (body > 0.40) & (body <= 0.55) & ~taken & ~stone
        img = np.where(seam[..., None], img * 0.78, img)
        taken |= stone

    # Dry grass and a little moss in the cracks (only on soil, sparse)
    soil = ~taken
    n = 5000
    sx, sy = rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n)
    keep = soil[sy.astype(int), sx.astype(int)] & (rng.random(n) < 0.5)
    strokes_at(img, rng, sx[keep], sy[keep], (3, 8), jitter(rng, srgb((112, 102, 66)), int(keep.sum()), 0.08), taper_dark=0.35)
    n = 1400
    mx, my = rng.uniform(0, SIZE, n), rng.uniform(0, SIZE, n)
    mkeep = soil[my.astype(int), mx.astype(int)] & (fbm(rng, [12], [1.0])[my.astype(int), mx.astype(int)] > 0.6)
    stamp_discs(img, mx[mkeep], my[mkeep], rng.uniform(0.7, 1.5, int(mkeep.sum())), jitter(rng, srgb((76, 84, 50)), int(mkeep.sum()), 0.07), lit=0.2)
    return img


def noise1d(rng, sigma):
    """Seamless 1-D noise along u (length SIZE), zero mean, unit std."""
    f = np.fft.fftfreq(SIZE)
    n = np.real(np.fft.ifft(np.fft.fft(rng.standard_normal(SIZE)) * np.exp(-2.0 * (np.pi ** 2) * (sigma ** 2) * f ** 2)))
    return (n - n.mean()) / (n.std() + 1e-9)


def tile_cliff(rng):
    """Cliff face for slopes of 50° and up, every biome (2026-11-16), seen side-on through the shader's side projections.
    Image row = v and +v is world UP: row 0 is the BOTTOM of the tile in the world, so "up" on the rock is toward the
    bottom of the image. A natural face, not masonry: strata of very uneven thickness (a few thick massive bands, several
    thin ones) that wander and pinch out; fractures irregular in spacing and angle (mostly near-vertical, some diagonal)
    that run through several strata or stop part-way; large unbroken faces; blocks of very different sizes; a jutting
    band has a lit top edge (facing +v) and a shadowed overhang under it. A darker, greyer brown than the slope rock (a
    neutral cool grey read as blue puddles under the sky light on the red-brown Redwood floor, 2026-11-16)."""
    rows = np.arange(SIZE, dtype=np.float64)

    # Strata: thicknesses per band, a few massive ones and several thin ones; thin bands pinch out along u
    nb = int(rng.integers(9, 13))
    base = np.where(rng.random(nb) < 0.35, rng.uniform(70, 150, nb), rng.uniform(8, 32, nb))
    thick_u = np.empty((nb, SIZE))
    for k in range(nb):
        env = 1.0 + 0.15 * noise1d(rng, rng.uniform(40, 80))
        if base[k] < 40 and rng.random() < 0.7:                   # pinches out over part of the width
            env = np.clip(0.6 + 0.8 * noise1d(rng, rng.uniform(50, 90)), 0.0, 1.5)
        thick_u[k] = np.maximum(base[k] * env, 0.0)
    thick_u *= SIZE / thick_u.sum(0, keepdims=True)                 # every column still holds exactly one tile height
    bottoms = np.concatenate([np.zeros((1, SIZE)), np.cumsum(thick_u, 0)[:-1]], 0)
    drift = 6.0 * noise1d(rng, 80.0) + 1.5 * noise1d(rng, 4.0)    # the whole stack wanders gently up and down along u
    # A few faults offset the whole stack by a step (irregular spacing)
    fault_x = np.sort(rng.uniform(0, SIZE, int(rng.integers(3, 6))))
    fault_seg = np.searchsorted(fault_x, np.arange(SIZE)) % len(fault_x)
    drift += rng.uniform(-6.0, 6.0, len(fault_x))[fault_seg]
    band = np.zeros((SIZE, SIZE), np.int64)
    t = np.zeros((SIZE, SIZE))                                     # 0 at a band's bottom (low v), 1 at its top (high v)
    for c in range(SIZE):
        vv = (rows - drift[c]) % SIZE
        b = np.clip(np.searchsorted(bottoms[:, c], vv, side="right") - 1, 0, nb - 1)
        band[:, c] = b
        t[:, c] = (vv - bottoms[b, c]) / np.maximum(thick_u[b, c], 1e-3)
    band_thick = thick_u[band, np.arange(SIZE)[None, :]]

    tones = np.array([srgb((101, 96, 89)), srgb((93, 88, 82)), srgb((107, 102, 95)), srgb((87, 82, 76)), srgb((97, 92, 86))])
    band_tone = tones[rng.integers(0, len(tones), nb)] * (1.0 + rng.normal(0, 0.035, (nb, 1)))
    jut = np.where(rng.random(nb) < 0.3, rng.uniform(0.6, 1.0, nb), rng.uniform(0.0, 0.35, nb))

    facet_idx, _, _, _, _ = voronoi(rng, 420)
    facet = 1.0 + rng.normal(0.0, 0.025, 420)[facet_idx]
    img = band_tone[band] * facet[..., None]
    grain = fbm(rng, [1.2, 4, 16], [0.35, 0.35, 0.3])
    img *= (1.0 + 0.05 * grain)[..., None]

    # Ledges: a jutting band's top edge (high t, toward +v = image bottom) catches the light; its underside (low t)
    # is an overhang in shadow, deeper the further the band juts, and a thin band barely shows either
    j = jut[band] * np.clip(band_thick / 30.0, 0.3, 1.0)
    lip = np.clip((t - 0.93) / 0.07, 0, 1) * (0.06 + 0.20 * j)
    under = np.clip(1.0 - t / np.clip(12.0 / np.maximum(band_thick, 1.0), 0.05, 0.6), 0, 1) ** 1.3 * (0.05 + 0.55 * j)
    img *= (1.0 + lip - under)[..., None]
    # Bedding seams: a soft dark line on each band boundary, stronger where a band juts
    seam = (t < 1.5 / np.maximum(band_thick, 1.0)) | (t > 1.0 - 1.0 / np.maximum(band_thick, 1.0))
    img = np.where(seam[..., None], img * (0.86 - 0.1 * j[..., None]), img)

    # Fractures: irregular spacing and angle, mostly near-vertical with some diagonals; lengths from short cracks to
    # joints through several strata; sparser on the big faces. A fracture is dark with a lit lip on its +u side.
    crack = np.zeros((SIZE, SIZE), bool)
    lit_side = np.zeros((SIZE, SIZE), bool)
    density = fbm(rng, [50], [1.0])
    placed = 0
    while placed < 30:
        x0, y0 = rng.uniform(0, SIZE), rng.uniform(0, SIZE)
        if density[int(y0), int(x0)] < -0.3 and rng.random() < 0.8:
            continue
        diag = rng.random() < 0.15
        ang = np.pi / 2 + (rng.choice([-1, 1]) * rng.uniform(0.45, 0.8) if diag else rng.normal(0, 0.10))
        length = rng.uniform(30, 90) if diag else (rng.uniform(40, 140) if rng.random() < 0.55 else rng.uniform(150, 380))
        xs, ys, a = bent_path(rng, x0, y0, ang, length, 0.15, step=0.4)
        # Many stop at a bedding plane: clip the run to the strata between its start band and 0–3 bands further
        b0 = band[int(y0) % SIZE, int(x0) % SIZE]
        reach = int(rng.integers(0, 4))
        if rng.random() < 0.8:
            yi_ = np.floor(ys).astype(int) % SIZE
            xi_ = np.floor(xs).astype(int) % SIZE
            d = (band[yi_, xi_] - b0 + nb // 2) % nb - nb // 2
            ok = np.abs(d) <= reach
            first_bad = np.argmax(~ok) if (~ok).any() else len(ok)
            xs, ys = xs[:max(first_bad, 2)], ys[:max(first_bad, 2)]
        w = 2 if rng.random() < 0.6 else 3
        for o in range(w):
            xi = np.floor(xs + o).astype(int) % SIZE
            yi = np.floor(ys).astype(int) % SIZE
            crack[yi, xi] = True
            lit_side[yi, (xi + 1) % SIZE] = True
        placed += 1
    lit_side &= ~crack
    # Faces: the regions the fractures and bedding seams cut the rock into (labelled on a 3x3 tiling so a face that
    # crosses the tile edge keeps one tone); unfinished fractures leave big faces whole. Each face is a tilted plane.
    from scipy import ndimage
    walls = crack | (seam & (j > 0.25))
    tiled = np.tile(~walls, (3, 3))
    lab, nlab = ndimage.label(tiled)
    lab = lab[SIZE:2 * SIZE, SIZE:2 * SIZE]
    frng = np.random.default_rng(int(rng.integers(1 << 30)))
    tilt = frng.normal(0.0, 0.07, nlab + 1)
    # each face is a plane: brightness ramps across it toward a random lean (u and v slopes), centred on the face
    gu = frng.normal(0.0, 0.0018, nlab + 1)
    gv = frng.normal(0.0012, 0.0018, nlab + 1)                     # faces lean back a little: brighter toward +v (up)
    vv_, uu_ = np.mgrid[0:SIZE, 0:SIZE].astype(np.float64)
    ids = np.arange(nlab + 1)
    cnt = np.maximum(ndimage.sum(np.ones_like(lab), lab, ids), 1)
    cu = ndimage.sum(uu_, lab, ids) / cnt
    cv = ndimage.sum(vv_, lab, ids) / cnt
    # sin() keeps the ramp periodic (a face that crosses the tile edge carries on seamlessly); near its centre it is linear
    span = SIZE / (2.0 * np.pi)
    plane = gu[lab] * span * np.sin((uu_ - cu[lab]) / span) + gv[lab] * span * np.sin((vv_ - cv[lab]) / span)
    plane = np.clip(plane, -0.12, 0.12)
    img *= (1.0 + tilt[lab] + plane)[..., None]
    # a joint is a step between two faces: the face on its -u side sits back in a short fading shadow
    shade_side = np.zeros((SIZE, SIZE))
    for k in range(1, 6):
        shade_side = np.maximum(shade_side, np.roll(crack, -k, axis=1) * (1.0 - k / 6.0))
    img *= (1.0 - 0.13 * shade_side * ~crack)[..., None]
    img = np.where(crack[..., None], img * 0.55, img)
    img = np.where(lit_side[..., None], img * 1.10, img)

    # Faint water streaks running down the face (stretched along v)
    f = np.fft.fftfreq(SIZE)
    fx, fy = np.meshgrid(f, f)
    k = np.exp(-2.0 * (np.pi ** 2) * ((24.0 ** 2) * fy ** 2 + (1.2 ** 2) * fx ** 2))
    streak = np.real(np.fft.ifft2(np.fft.fft2(rng.standard_normal((SIZE, SIZE))) * k))
    streak = streak / (streak.std() + 1e-9)
    img *= (1.0 - 0.045 * np.clip(streak, 0, 2.5))[..., None]

    # Warm grey-brown mean (2026-11-16 second pass): darker and greyer than the slope rock (103, 95, 82)
    img *= (srgb((92.0, 87.0, 81.0)) / img.reshape(-1, 3).mean(0))[None, None, :]
    return img


TILES = {
    "clover": (tile_clover, 21),
    "redwood": (tile_redwood, 22),
    "redwood_far": (tile_redwood_far, 26),
    "slope_rock": (tile_slope_rock, 27),
    "cliff": (tile_cliff, 28),
    "amber": (tile_amber, 23),
    "mountain": (tile_mountain, 24),
    "sand": (tile_sand, 25),
}


def main():
    # Arguments: tile names to build (default all, e.g. `redwood redwood_far`) and / or an output folder for trial runs.
    names = [a for a in sys.argv[1:] if a in TILES]
    folders = [a for a in sys.argv[1:] if a not in TILES]
    out = folders[0] if folders else OUT
    os.makedirs(out, exist_ok=True)
    if "redwood_far" in names and "redwood" not in names:
        # the far tile is matched to the near tile's mean: read the one on disk
        near = np.asarray(Image.open(os.path.join(out, "ground_redwood.png")).convert("RGB")).reshape(-1, 3)
        _NEAR_MEAN["redwood"] = tuple(near.mean(0))
    for name, (fn, seed) in TILES.items():
        if names and name not in names:
            continue
        raw = fn(np.random.default_rng(seed))
        img = quantise(flatten(raw) if TILE_FLATTEN.get(name, True) else raw)
        if name == "redwood":
            _NEAR_MEAN["redwood"] = tuple(img.reshape(-1, 3).mean(0))
        path = os.path.join(out, f"ground_{name}.png")
        Image.fromarray(img, "RGB").save(path)
        print(f"{path}: {SIZE} px, mean sRGB {img.reshape(-1, 3).mean(0).round(1)}")


if __name__ == "__main__":
    main()
