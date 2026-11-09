"""UI theme textures (Assets/ui/theme/<theme>/) — pixel-art scale, point sampled in the engine.

Run from the repo root:

    python Tools/ui_theme/generate_ui_theme_textures.py            # every theme
    python Tools/ui_theme/generate_ui_theme_textures.py ember      # one theme

Per theme:
    panel_tile.png   256x256  seamless low-contrast grain, white-on-transparent; the engine tints it
                              (background-tint) and the theme JSON sets its opacity, so one tile serves
                              every colour scheme.
    corner.png        32x32   corner bracket (top-left orientation; UiStyle rotates the other three),
                              white-on-transparent, tinted to the accent colour at runtime.
    vignette.png     256x256  radial darkening for the menu backdrop (transparent centre, dark edges).
    divider.png       256x8   horizontal rule fading out at both ends, white, tinted at runtime.

All art is white-on-alpha so the theme tints it; only the vignette carries its own (black) colour.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
OUT_ROOT = REPO / "Assets" / "ui" / "theme"

THEMES = {
    # grain: (cell size px, contrast 0-1, streak direction) — tiles look different per theme.
    "ember": {"cell": 8, "contrast": 0.55, "streak": "horizontal", "seed": 7},
    "frost": {"cell": 6, "contrast": 0.45, "streak": "diagonal", "seed": 19},
}


def _rng(seed: int) -> np.random.Generator:
    return np.random.default_rng(seed)


def panel_tile(spec: dict, size: int = 256) -> Image.Image:
    """Seamless grain: blocky value noise (pixel cells) + a faint streak, white alpha only."""
    rng = _rng(spec["seed"])
    cell = spec["cell"]
    n = size // cell
    coarse = rng.random((n, n))
    # Wrap-friendly blur: average with rolled neighbours so the tile edges match.
    blurred = (coarse + np.roll(coarse, 1, 0) + np.roll(coarse, -1, 0) + np.roll(coarse, 1, 1) + np.roll(coarse, -1, 1)) / 5.0
    yy, xx = np.mgrid[0:size, 0:size]
    # Nearest-cell lookup (not kron) so a cell size that does not divide the tile still fills it.
    grain = blurred[(yy * n) // size, (xx * n) // size]
    if spec["streak"] == "horizontal":
        phase = (yy * 2 * np.pi / size) * 6
    else:
        phase = ((xx + yy) * 2 * np.pi / size) * 4
    streak = 0.5 + 0.5 * np.sin(phase)
    streak = np.round(streak * 3) / 3  # three steps — stays blocky

    value = 0.7 * grain + 0.3 * streak
    value = (value - value.min()) / max(1e-6, value.max() - value.min())
    # Quantise to a handful of steps so it reads as pixel grain, not film noise.
    value = np.round(value * 5) / 5
    alpha = (value * spec["contrast"] * 255).astype(np.uint8)
    rgba = np.zeros((size, size, 4), dtype=np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = alpha
    return Image.fromarray(rgba, "RGBA")


def corner(size: int = 32) -> Image.Image:
    """Top-left bracket: a 3 px L with a notch and a small diamond stud — reads at 22 px on screen."""
    a = np.zeros((size, size), dtype=np.uint8)
    arm = int(size * 0.72)
    t = 3
    a[0:t, 0:arm] = 255           # top arm
    a[0:arm, 0:t] = 255           # left arm
    a[t:t + 1, t:arm - 6] = 110   # faint inner line
    a[t:arm - 6, t:t + 1] = 110
    # end caps: a step down at each arm's tip
    a[0:t + 2, arm - 3:arm] = 255
    a[arm - 3:arm, 0:t + 2] = 255
    # diamond stud on the diagonal
    cx = cy = 9
    for dy in range(-3, 4):
        for dx in range(-3, 4):
            if abs(dx) + abs(dy) <= 3:
                a[cy + dy, cx + dx] = 255
    a[cy, cx] = 90
    rgba = np.zeros((size, size, 4), dtype=np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = a
    return Image.fromarray(rgba, "RGBA")


def vignette(size: int = 256) -> Image.Image:
    yy, xx = np.mgrid[0:size, 0:size]
    cx = cy = (size - 1) / 2
    dx = (xx - cx) / cx
    dy = (yy - cy) / cy
    r = np.sqrt(dx * dx * 0.85 + dy * dy)
    # Smooth (no step quantising): the 256 px tile is stretched over the whole screen, so any step reads as rings.
    fall = np.clip((r - 0.45) / 0.75, 0, 1) ** 1.6
    rgba = np.zeros((size, size, 4), dtype=np.uint8)
    rgba[..., 3] = (fall * 200).astype(np.uint8)
    return Image.fromarray(rgba, "RGBA")


def divider(width: int = 256, height: int = 8) -> Image.Image:
    xx = np.arange(width) / (width - 1)
    fade = np.clip(1.0 - np.abs(xx - 0.5) * 2.0, 0, 1) ** 0.6
    fade = np.round(fade * 6) / 6
    a = np.zeros((height, width), dtype=np.uint8)
    mid = height // 2
    a[mid - 1:mid + 1, :] = (fade * 255).astype(np.uint8)   # 2 px core line
    a[mid - 2, :] = (fade * 70).astype(np.uint8)            # soft 1 px halo above and below
    a[mid + 1, :] = (fade * 70).astype(np.uint8)
    rgba = np.zeros((height, width, 4), dtype=np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = a
    return Image.fromarray(rgba, "RGBA")


def build(theme: str) -> None:
    spec = THEMES[theme]
    out = OUT_ROOT / theme
    out.mkdir(parents=True, exist_ok=True)
    panel_tile(spec).save(out / "panel_tile.png")
    corner().save(out / "corner.png")
    vignette().save(out / "vignette.png")
    divider().save(out / "divider.png")
    print(f"[ui_theme] {theme}: panel_tile, corner, vignette, divider -> {out.relative_to(REPO)}")


def main(argv: list[str]) -> int:
    names = argv[1:] or list(THEMES)
    for name in names:
        if name not in THEMES:
            print(f"[ui_theme] unknown theme '{name}'; known: {', '.join(THEMES)}")
            return 1
        build(name)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
