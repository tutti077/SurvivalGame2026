"""The moon disk's surface: Assets/textures/environment/moon_albedo_v2.png (512x256, equirectangular, for the unit
sphere the day / night cycle draws as the moon - materials/environment/sky/moon_disk.vmat).

Plain Python (numpy + PIL), no Blender:  python Blender/scripts/create_moon_texture.py

Mark 2026-11-23: "can you redesign the sun and moon so they look more realistic". The first texture was a soft grey
gradient with a dozen identical ring stamps. From real photographs of the full moon: pale grey-cream highlands, large
dark basalt seas (maria) with soft irregular shores that cover about a third of the near side, craters of every size
(many small, few large) whose floors are a little darker and whose rims a little brighter, and a few young craters
with bright ray systems (Tycho, Copernicus). The limb darkening is the shader's (celestial_unlit, LimbPower).
"""
import math
import os

import numpy as np
from PIL import Image

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
OUT = os.path.join(ROOT, 'Assets', 'textures', 'environment', 'moon_albedo_v2.png')
W, H = 512, 256


def sphere_points():
    v, u = np.mgrid[0:H, 0:W].astype(np.float32)
    lon = (u + 0.5) / W * math.tau
    lat = ((v + 0.5) / H - 0.5) * math.pi
    return np.stack([np.cos(lat) * np.cos(lon), np.cos(lat) * np.sin(lon), np.sin(lat)], -1)


def sphere_noise(points, rng, octaves, base):
    """Seamless value noise on the sphere: sums of random plane waves (no pole or seam artefacts)."""
    total = np.zeros(points.shape[:2], np.float32)
    amp, freq, norm = 1.0, base, 0.0
    for _ in range(octaves):
        for _ in range(6):
            d = rng.normal(size=3)
            d /= np.linalg.norm(d)
            total += amp * np.sin(points @ (d * freq).astype(np.float32) + rng.uniform(0, math.tau))
        norm += amp * 6
        amp *= 0.55
        freq *= 2.1
    return total / norm


def main():
    rng = np.random.default_rng(1969)
    p = sphere_points()

    highland = 0.74 + 0.10 * sphere_noise(p, rng, 5, 3.0) + 0.05 * sphere_noise(p, rng, 3, 24.0)
    mare_field = sphere_noise(p, rng, 4, 1.6)
    lo, hi = np.quantile(mare_field, 0.60), np.quantile(mare_field, 0.74)   # about a third of the sphere is sea
    mare = np.clip((mare_field - lo) / (hi - lo), 0.0, 1.0)
    mare = mare * mare * (3 - 2 * mare)                                   # soft shores
    mare_tone = 0.36 + 0.05 * sphere_noise(p, rng, 3, 9.0)
    albedo = highland * (1 - mare) + mare_tone * mare

    # craters: many small, few large; a darker floor inside a brighter rim
    for _ in range(420):
        c = rng.normal(size=3)
        c /= np.linalg.norm(c)
        radius = min(0.22, 0.012 + 0.03 * rng.pareto(2.2))                # angular radius, radians
        ang = np.arccos(np.clip(p @ c.astype(np.float32), -1.0, 1.0))
        t = ang / radius
        near = t < 1.6
        floor = np.clip(1.0 - t, 0.0, 1.0)
        rim = np.exp(-((t - 1.0) / 0.16) ** 2)
        depth = rng.uniform(0.04, 0.11)
        albedo[near] += (rim * depth * 0.9 - floor ** 0.6 * depth)[near]

    # young ray craters: a bright spot with thin rays fanning out
    for _ in range(4):
        c = rng.normal(size=3)
        c /= np.linalg.norm(c)
        e1 = np.cross(c, [0.0, 0.0, 1.0])
        e1 /= np.linalg.norm(e1)
        e2 = np.cross(c, e1)
        ang = np.arccos(np.clip(p @ c.astype(np.float32), -1.0, 1.0))
        az = np.arctan2(p @ e2.astype(np.float32), p @ e1.astype(np.float32))
        rays = np.zeros_like(ang)
        for _ in range(rng.integers(9, 15)):
            a0, width, reach = rng.uniform(-math.pi, math.pi), rng.uniform(0.012, 0.03), rng.uniform(0.35, 0.9)
            d = np.abs(np.angle(np.exp(1j * (az - a0))))
            rays += np.exp(-(d / width) ** 2) * np.clip(1.0 - ang / reach, 0.0, 1.0)
        albedo += 0.10 * np.clip(rays, 0.0, 1.0) + 0.16 * np.exp(-(ang / 0.03) ** 2)

    albedo = np.clip(albedo, 0.16, 1.0)
    # cream highlands, slightly blue-grey seas
    tint_high = np.array([1.0, 0.98, 0.93], np.float32)
    tint_mare = np.array([0.92, 0.95, 1.0], np.float32)
    rgb = albedo[..., None] * (tint_high * (1 - mare[..., None]) + tint_mare * mare[..., None])
    Image.fromarray(np.clip(rgb * 255 + 0.5, 0, 255).astype(np.uint8), 'RGB').save(OUT)
    print(f'wrote {OUT}: maria cover {float(mare.mean()):.0%} of the sphere')


if __name__ == '__main__':
    main()
