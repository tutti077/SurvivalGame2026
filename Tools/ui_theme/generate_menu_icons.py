"""Menu tab icons (Assets/ui/menu/*Tab.png): 64x64 white line-art on alpha, tinted by the theme at runtime.

Run from the repo root:  python Tools/ui_theme/generate_menu_icons.py

The old tabs were a mix of black line-art and coloured pictures (invisible or loud on a dark panel);
these are one family — 4 px strokes, pixel-crisp, white — so MenuPageNavigator can tint them with the
theme's text colour and brighten the selected one with the accent.
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]
OUT = REPO / "Assets" / "ui" / "menu"
SIZE = 64
W = 4  # stroke


def canvas() -> tuple[Image.Image, ImageDraw.ImageDraw]:
    im = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
    return im, ImageDraw.Draw(im)


WHITE = (255, 255, 255, 255)


def inventory() -> Image.Image:
    im, d = canvas()
    for x in (6, 34):
        for y in (6, 34):
            d.rectangle((x, y, x + 24, y + 24), outline=WHITE, width=W)
    return im


def crafting() -> Image.Image:
    """Hammer: diagonal handle, blocky head."""
    im, d = canvas()
    d.line((14, 50, 40, 24), fill=WHITE, width=W + 2)
    d.rectangle((34, 8, 56, 28), outline=WHITE, width=W)
    d.rectangle((40, 14, 50, 22), fill=WHITE)
    return im


def skills() -> Image.Image:
    """Three nodes joined by lines (the skill web)."""
    im, d = canvas()
    nodes = [(16, 48), (32, 14), (50, 46)]
    d.line((*nodes[0], *nodes[1]), fill=WHITE, width=W)
    d.line((*nodes[1], *nodes[2]), fill=WHITE, width=W)
    d.line((*nodes[0], *nodes[2]), fill=WHITE, width=W)
    for i, (x, y) in enumerate(nodes):
        r = 9 if i == 1 else 7
        d.ellipse((x - r, y - r, x + r, y + r), fill=(255, 255, 255, 0), outline=WHITE, width=W)
    return im


def quests() -> Image.Image:
    """Scroll: three rule lines with a tick on the first."""
    im, d = canvas()
    d.rectangle((8, 6, 56, 58), outline=WHITE, width=W)
    d.line((18, 20, 24, 26), fill=WHITE, width=W)
    d.line((24, 26, 34, 14), fill=WHITE, width=W)
    d.line((38, 20, 48, 20), fill=WHITE, width=W)
    d.line((18, 34, 48, 34), fill=WHITE, width=W)
    d.line((18, 46, 40, 46), fill=WHITE, width=W)
    return im


def map_icon() -> Image.Image:
    """Folded map: outline, two fold lines, a pin."""
    im, d = canvas()
    pts = [(6, 14), (24, 8), (40, 14), (58, 8), (58, 50), (40, 56), (24, 50), (6, 56)]
    d.polygon(pts, outline=WHITE, width=W)
    d.line((24, 8, 24, 50), fill=WHITE, width=W - 1)
    d.line((40, 14, 40, 56), fill=WHITE, width=W - 1)
    d.ellipse((44, 24, 54, 34), fill=WHITE)
    return im


def settings() -> Image.Image:
    """Gear: ring with eight teeth and a hub hole."""
    im, d = canvas()
    cx = cy = 32
    for i in range(8):
        a = i * math.pi / 4
        x0 = cx + math.cos(a) * 18
        y0 = cy + math.sin(a) * 18
        x1 = cx + math.cos(a) * 27
        y1 = cy + math.sin(a) * 27
        d.line((x0, y0, x1, y1), fill=WHITE, width=W + 3)
    d.ellipse((cx - 19, cy - 19, cx + 19, cy + 19), fill=WHITE)
    d.ellipse((cx - 8, cy - 8, cx + 8, cy + 8), fill=(255, 255, 255, 0))
    return im


ICONS = {
    "InventoryTab.png": inventory,
    "CraftingTab.png": crafting,
    "SkillsTab.png": skills,
    "QuestsTab.png": quests,
    "MapTab.png": map_icon,
    "SettingsTab.png": settings,
}


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, fn in ICONS.items():
        fn().save(OUT / name)
        print(f"[menu_icons] {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
