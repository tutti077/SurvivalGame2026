"""
Two Track Games brand logos — clean vector rebuild.

The shapes were traced from the studio's banner (2000 x 667) and round badge, then snapped to
straight edges by hand. This script is the single source of the geometry: it writes crisp SVGs
and anti-aliased PNG exports into website/assets/brand/, and prints the path data used by the
#tt-banner symbol in website/index.html (the trailer in js/intro.js reads those paths from the page).

    python Tools/brand/make_brand_logos.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "website" / "assets" / "brand"
WHITE = (250, 250, 250)
RED = (253, 15, 45)   # sampled from the original banner and badge
BLACK = (0, 0, 0)

# --------------------------------------------------------------------------- banner (2000 x 667)
BW, BH = 2000, 667

RAILS = [  # white rail pieces; the gaps either side of the switch are cut parallel to the diagonal
    [(84, 183), (544, 183), (544, 211), (84, 211)],
    [(813, 183), (1916, 183), (1916, 211), (840.7, 211)],
    [(84, 404), (945.3, 404), (973, 432), (84, 432)],
    [(1298, 404), (1916, 404), (1916, 432), (1298, 432)],
]
SWITCH = [(544, 183), (795, 183), (1014, 404), (1298, 404), (1298, 432), (991, 432), (772, 211), (544, 211)]


def T_at(stem_x):
    rel = [(-79.5, 0), (-50, 39), (0, 39), (0, 145), (48, 145), (48, 49), (41, 39), (112.5, 39), (88, 0)]
    return [(stem_x + x, 239 + y) for x, y in rel]


TWO = [
    T_at(172),
    [(282, 239), (354, 384), (388, 384), (436, 307), (443, 316), (478, 384), (512, 384), (596, 239),
     (543, 239), (495, 319.5), (452, 239), (426, 239), (374, 319.5), (334, 239)],
    [(597, 261), (597, 362), (618, 384), (756, 384), (777, 362), (777, 261), (756, 239), (618, 239)],
]
TWO_HOLES = [[(646, 278), (728, 278), (731, 281), (731, 342.5), (728, 345.5), (646, 345.5), (643, 342.5), (643, 281)]]
TRACK = [
    T_at(1047),
    [(1158, 239), (1187, 278), (1296, 278), (1296, 300), (1210, 300), (1203, 291), (1178.5, 291), (1178.5, 384),
     (1223.5, 384), (1223.5, 337), (1242, 337), (1286, 384), (1337, 384), (1296, 337), (1322.5, 337), (1341, 319),
     (1341, 258), (1322.5, 239)],
    [(1341, 384), (1392, 384), (1450, 294), (1504, 384), (1556, 384), (1470, 239), (1431, 239)],
    [(1560, 261), (1560, 362), (1582, 384), (1719.5, 384), (1693.5, 346), (1607.5, 346), (1604, 342.5), (1604, 281.5),
     (1607.5, 278), (1693.5, 278), (1719.5, 239), (1582, 239)],
    [(1731, 262), (1731, 384), (1780.5, 384), (1780.5, 337), (1836, 384), (1899, 384), (1824, 314), (1910, 239),
     (1845, 239), (1780.5, 294.5), (1780.5, 239), (1751, 239)],
]
GAMES = [
    [(677, 463), (708, 463), (711, 466), (711, 474), (705, 474), (705, 469), (680, 469), (680, 504), (705, 504),
     (705, 491), (693, 491), (693, 485), (711, 485), (711, 507), (708, 510), (677, 510), (674, 507), (674, 466)],
    [(838, 463), (817, 510), (823, 510), (829, 499), (854, 499), (859, 510), (866, 510), (846, 463)],
    [(972, 463), (972, 510), (978, 510), (978, 476), (995, 505), (1000, 505), (1017, 476), (1017, 510), (1023, 510),
     (1023, 463), (1016, 463), (997.5, 495), (979, 463)],
    [(1137, 463), (1137, 510), (1172, 510), (1172, 504), (1143, 504), (1143, 489.5), (1164, 489.5), (1164, 483.5),
     (1143, 483.5), (1143, 469), (1172, 469), (1172, 463)],
    [(1288, 463), (1319, 463), (1322, 466), (1322, 474), (1316, 474), (1316, 469), (1291, 469), (1291, 483.5),
     (1319, 483.5), (1322, 486.5), (1322, 507), (1319, 510), (1288, 510), (1285, 507), (1285, 499), (1291, 499),
     (1291, 504), (1316, 504), (1316, 489.5), (1288, 489.5), (1285, 486.5), (1285, 466)],
]
GAMES_HOLES = [[(842, 470), (852.5, 493), (831.5, 493)]]

# --------------------------------------------------------------------------- badge (traced at 164 px, centre 105.5 / 95.5)
BADGE_C, BADGE_R = (105.5, 95.5), 82
BADGE_WHITE = [
    [(97.5, 58), (174, 58), (174, 64), (101.3, 64)],
    [(37, 127), (123.7, 127), (127.5, 133), (37, 133)],
    [(38, 72), (45, 82), (62, 82), (62, 116), (68, 122), (77, 122), (77, 87), (72, 82), (94, 82), (88, 72)],
    [(110, 72), (116.5, 82), (136, 82), (136, 116), (142, 122), (151, 122), (151, 87), (146, 82), (166, 82), (173, 72)],
]
BADGE_RED = [[(37, 58), (93, 58), (137, 127), (174, 127), (174, 133), (132, 133), (88, 64), (37, 64)]]


def badge_xy(p, size=512):
    k = (size / 2) / BADGE_R
    return ((p[0] - BADGE_C[0]) * k + size / 2, (p[1] - BADGE_C[1]) * k + size / 2)


# --------------------------------------------------------------------------- writers
def num(v):
    s = f"{v:.1f}"
    return s[:-2] if s.endswith(".0") else s


def d(polys):
    return " ".join("M" + " L".join(f"{num(x)} {num(y)}" for x, y in p) + " Z" for p in polys)


def banner_svg(background=True):
    bg = f'<rect width="{BW}" height="{BH}" fill="#000"/>' if background else ""
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BW} {BH}" role="img" aria-label="Two Track Games">{bg}'
        f'<path fill="#fafafa" d="{d(RAILS)}"/>'
        f'<path fill="#fd0f2d" d="{d([SWITCH])}"/>'
        f'<path fill="#fafafa" fill-rule="evenodd" d="{d(TWO + TWO_HOLES)} {d(TRACK)}"/>'
        f'<path fill="#fafafa" fill-rule="evenodd" d="{d(GAMES + GAMES_HOLES)}"/>'
        "</svg>\n"
    )


def badge_svg():
    w = [[badge_xy(p) for p in poly] for poly in BADGE_WHITE]
    r = [[badge_xy(p) for p in poly] for poly in BADGE_RED]
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="Two Track Games">'
        '<circle cx="256" cy="256" r="256" fill="#000"/>'
        f'<path fill="#fafafa" d="{d(w)}"/><path fill="#fd0f2d" d="{d(r)}"/>'
        "</svg>\n"
    )


def render(size, layers, holes=(), circle=None, background=None, src=(BW, BH), ss=4):
    """layers: [(colour, polys)], polys in source units; holes are cut from every layer."""
    W, H = size
    k = W * ss / src[0]
    big = Image.new("RGBA", (W * ss, H * ss), background + (255,) if background else (0, 0, 0, 0))
    if circle:
        mask = Image.new("L", big.size, 0)
        ImageDraw.Draw(mask).ellipse([0, 0, W * ss - 1, H * ss - 1], fill=255)
        disc = Image.new("RGBA", big.size, BLACK + (255,))
        big.paste(disc, (0, 0), mask)
    for colour, polys in layers:
        mask = Image.new("L", big.size, 0)
        dr = ImageDraw.Draw(mask)
        for p in polys:
            dr.polygon([(x * k, y * k) for x, y in p], fill=255)
        for p in holes:
            dr.polygon([(x * k, y * k) for x, y in p], fill=0)
        big.paste(Image.new("RGBA", big.size, colour + (255,)), (0, 0), mask)
    return big.resize((W, H), Image.LANCZOS)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "two-track-banner.svg").write_text(banner_svg(True), encoding="utf-8")
    (OUT / "two-track-banner-transparent.svg").write_text(banner_svg(False), encoding="utf-8")
    (OUT / "two-track-badge.svg").write_text(badge_svg(), encoding="utf-8")
    (ROOT / "website" / "assets" / "logo-square.svg").write_text(badge_svg(), encoding="utf-8")

    banner_layers = [(WHITE, RAILS), (RED, [SWITCH]), (WHITE, TWO + TRACK), (WHITE, GAMES)]
    holes = TWO_HOLES + GAMES_HOLES
    for w in (1500, 3000):
        render((w, w // 3), banner_layers, holes, background=BLACK).convert("RGB").save(OUT / f"two-track-banner-{w}.png")
    render((3000, 1000), banner_layers, holes).save(OUT / "two-track-banner-transparent-3000.png")

    badge_layers = [(WHITE, [[badge_xy(p, 1) for p in poly] for poly in BADGE_WHITE]),
                    (RED, [[badge_xy(p, 1) for p in poly] for poly in BADGE_RED])]
    for s in (400, 1024):
        render((s, s), badge_layers, circle=True, src=(1, 1)).save(OUT / f"two-track-badge-{s}.png")

    # path data for the inline #tt-banner symbol in website/index.html
    print("rails  ", d(RAILS))
    print("switch ", d([SWITCH]))
    print("two    ", d(TWO + TWO_HOLES))
    print("track  ", d(TRACK))
    print("games  ", d(GAMES + GAMES_HOLES))


if __name__ == "__main__":
    main()
