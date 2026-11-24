"""The far impostor cards' texture atlas: the elm card on the left half, a redwood on the right half.

Plain Python (numpy + PIL), no Blender:  python Blender/scripts/create_far_card_atlas.py

Reads   Assets/materials/environment/cloverHills/trees/far_tree_card_{color,mask,normal}.png   (256x256, the elm card of
        create_far_card_textures.py - run that first if it changes)
Writes  Assets/materials/environment/cloverHills/trees/far_tree_cards_atlas_{color,mask,normal}.png   (512x256)
        which far_tree_card.vmat uses. TerrainWorldManager.FarImpostors.cs maps the elm card to u 0-0.5 and the conifer
        card (FarConiferCardTemplate, three crossed full-height quads) to u 0.5-1, v 0 (tip) - 1 (ground).

The redwood (Mark 2026-11-23: "the LOD redwood needs to very closely resemble the real redwoods because we can visibly
see it change, the goal is to change it without players even noticing"): what the instanced far model
(environment_redwood*_v2_far.vmdl) shows at 1.2 km - a red bark trunk visible over the whole height, bare for the lowest
third, then tiers of drooping dark boughs that narrow toward the tip, with gaps between the tiers. Colours are the mean
colours of the real materials' textures (redwood_bark_v2.png, redwood_needles_v2.png), so the card takes light like the tree.
The card is 0.24 x the tree's height wide.
"""
import os

import numpy as np
from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'Assets', 'materials', 'environment', 'cloverHills', 'trees')
SIZE = 256
BARK = np.array([106, 62, 46], np.float32) / 255.0       # redwood_bark_v2.png mean
NEEDLES = np.array([39, 57, 35], np.float32) / 255.0     # redwood_needles_v2.png mean (opaque texels)
CROWN_FROM = 0.33                                        # height (0 ground - 1 tip) of the lowest bough
TIERS = 17


def block_noise(rng, cell):
    n = rng.random((SIZE // cell + 2, SIZE // cell + 2)).astype(np.float32)
    return np.kron(n, np.ones((cell, cell), np.float32))[:SIZE, :SIZE]


def draw_redwood():
    rng = np.random.default_rng(90)
    ys, xs = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    u = (xs + 0.5) / SIZE - 0.5                # -0.5 .. 0.5 across the card
    h = 1.0 - (ys + 0.5) / SIZE                # 0 at the ground .. 1 at the tip
    fine = block_noise(rng, 2)
    coarse = block_noise(rng, 6)

    colour = np.zeros((SIZE, SIZE, 3), np.float32)
    alpha = np.zeros((SIZE, SIZE), np.float32)

    # trunk: 4.8 m at the foot of a 90 m tree (0.11 of the card each side), tapering to the tip, with a flared foot
    trunk_half = 0.02 + 0.09 * (1.0 - h) ** 1.2 + 0.05 * np.clip(1.0 - h / 0.05, 0.0, 1.0)
    trunk = (np.abs(u) < trunk_half) & (h < 0.98)
    furrow = 0.78 + 0.34 * block_noise(rng, 3) * (0.6 + 0.4 * np.abs(np.sin(xs * 1.7)))
    side = 1.0 - 0.35 * np.clip(np.abs(u) / np.maximum(trunk_half, 1e-3), 0.0, 1.0) ** 2   # round the column
    colour[trunk] = (BARK[None, :] * (furrow * side)[..., None])[trunk]
    alpha[trunk] = 1.0

    # boughs: tiers from CROWN_FROM to the tip, each a drooping fan, wide low and narrow high, ragged, uneven left / right
    span = 1.0 - CROWN_FROM
    for k in range(TIERS):
        top = CROWN_FROM + span * (k + 1.0) / TIERS + rng.uniform(-0.006, 0.006)
        thick = span / TIERS * rng.uniform(0.95, 1.25)            # tiers nearly meet: thin gaps show trunk and sky
        t = (top - CROWN_FROM) / span
        reach = 0.47 * (1.0 - t) ** 0.85 + 0.07
        s = np.clip((top - h) / thick, 0.0, 1.0)                  # 0 at the tier's top edge .. 1 at its drooping tips
        inside_band = (h <= top) & (h > top - thick)
        for sign in (-1.0, 1.0):
            reach_side = reach * rng.uniform(0.72, 1.08)
            half = reach_side * (0.22 + 0.78 * s ** 0.6) * (0.78 + 0.44 * coarse)
            bough = inside_band & (u * sign >= 0.0) & (np.abs(u) < half)
            # ragged needle tips along the lower edge
            bough &= ~((s > 0.72) & (fine > 0.55 + 0.4 * (1.0 - s)))
            shade = (1.32 - 0.62 * s) * (0.82 + 0.36 * fine)      # lit top, dark underside
            colour[bough] = (NEEDLES[None, :] * shade[..., None])[bough]
            alpha[bough] = 1.0

    # tangent-space normal: a column (x across the card), tilted a little up so the top faces take the sky
    nx = np.clip(u * 1.6, -0.8, 0.8)
    ny = np.full_like(nx, 0.25)
    nz = np.sqrt(np.clip(1.0 - nx * nx - ny * ny, 0.05, 1.0))
    normal = np.stack([nx, ny, nz], -1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)
    normal = normal * 0.5 + 0.5
    normal[alpha < 0.5] = (0.5, 0.5, 1.0)
    colour[alpha < 0.5] = NEEDLES * 0.9                           # bleed colour under the cut-out edge
    return colour, alpha, normal


def main():
    elm_colour = np.asarray(Image.open(os.path.join(OUT, 'far_tree_card_color.png')).convert('RGB'), np.float32) / 255.0
    elm_mask = np.asarray(Image.open(os.path.join(OUT, 'far_tree_card_mask.png')).convert('L'), np.float32) / 255.0
    elm_normal = np.asarray(Image.open(os.path.join(OUT, 'far_tree_card_normal.png')).convert('RGB'), np.float32) / 255.0
    assert elm_colour.shape[:2] == (SIZE, SIZE), elm_colour.shape
    colour, alpha, normal = draw_redwood()

    def save(name, left, right, mode):
        both = np.concatenate([left, right], axis=1)
        Image.fromarray(np.clip(both * 255.0 + 0.5, 0, 255).astype(np.uint8), mode).save(os.path.join(OUT, name))

    save('far_tree_cards_atlas_color.png', elm_colour, colour, 'RGB')
    save('far_tree_cards_atlas_mask.png', elm_mask, alpha, 'L')
    save('far_tree_cards_atlas_normal.png', elm_normal, normal, 'RGB')
    print(f'wrote far_tree_cards_atlas_{{color,mask,normal}}.png to {OUT}: redwood covers {int((alpha > 0.5).sum())} of {SIZE * SIZE} px')


if __name__ == '__main__':
    main()
