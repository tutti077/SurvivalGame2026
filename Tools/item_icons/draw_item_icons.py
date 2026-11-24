"""
64 px inventory icons for items that have no painted icon yet (Mark 2026-11-10, prompts 28 + 29): the wild beehive
and the egg; the redwood fiddlehead (2026-11-16). Same language as the painted set in Assets/ui/items: flat fills, a dark outline, one shade step,
transparent background, no gradients.

    python Tools/item_icons/draw_item_icons.py
"""
import math
import os
from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "Assets", "ui", "items")
SIZE = 64
OUTLINE = (58, 42, 26, 255)


def canvas():
	im = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
	return im, ImageDraw.Draw(im)


def beehive():
	im, d = canvas()
	# hanging stub + rope
	d.rectangle((29, 2, 34, 12), fill=(96, 72, 44, 255), outline=OUTLINE)
	# hive body: stacked rounded tiers, widest in the middle
	tiers = [(24, 10, 40, 18), (19, 16, 45, 26), (16, 24, 48, 36), (18, 34, 46, 46), (23, 44, 41, 54)]
	for i, (x0, y0, x1, y1) in enumerate(tiers):
		fill = (222, 170, 64, 255) if i % 2 == 0 else (236, 190, 86, 255)
		d.rounded_rectangle((x0, y0, x1, y1), radius=6, fill=fill, outline=OUTLINE, width=2)
	# shade on the right of each tier
	for (x0, y0, x1, y1) in tiers:
		d.rounded_rectangle((x1 - 8, y0 + 2, x1 - 2, y1 - 2), radius=3, fill=(196, 142, 52, 255))
	# entrance hole
	d.ellipse((28, 36, 36, 44), fill=(74, 52, 30, 255), outline=OUTLINE)
	# two bees
	for (bx, by) in ((12, 30), (50, 20)):
		d.ellipse((bx, by, bx + 6, by + 4), fill=(236, 196, 60, 255), outline=OUTLINE)
		d.line((bx + 2, by, bx + 2, by + 4), fill=OUTLINE)
	return im


def egg():
	im, d = canvas()
	# egg: wider at the bottom
	d.ellipse((18, 8, 46, 56), fill=(243, 232, 210, 255), outline=OUTLINE, width=2)
	d.ellipse((16, 20, 48, 56), fill=(243, 232, 210, 255))
	d.arc((16, 20, 48, 56), 0, 180, fill=OUTLINE, width=2)
	d.line((18, 38, 16, 38), fill=OUTLINE, width=2)
	d.line((46, 38, 48, 38), fill=OUTLINE, width=2)
	# shade and highlight
	d.pieslice((22, 24, 46, 54), 20, 160, fill=(214, 198, 170, 255))
	d.ellipse((20, 22, 46, 54), fill=(243, 232, 210, 255))
	d.pieslice((20, 22, 46, 54), 30, 150, fill=(214, 198, 170, 255))
	d.ellipse((24, 14, 31, 24), fill=(255, 250, 240, 255))
	return im


def _spiral(cx, cy, r0, turns, start_deg, n=60):
	"""Points of a tightening spiral (image space, y down) curling counter-clockwise from `start_deg`."""
	pts = []
	for i in range(n + 1):
		t = i / n
		a = math.radians(start_deg) - t * turns * 2 * math.pi
		r = r0 * (1.0 - 0.8 * t)
		pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
	return pts


def _thick(d, pts, w0, w1, fill):
	n = len(pts) - 1
	for i, (x, y) in enumerate(pts):
		w = (w0 + (w1 - w0) * i / n) * 0.5
		d.ellipse((x - w, y - w, x + w, y + w), fill=fill)


def fiddlehead():
	im, d = canvas()
	green, shade, light = (96, 150, 52, 255), (72, 120, 40, 255), (138, 182, 78, 255)
	# two picked ostrich-fern crosiers: a cut stalk rising from the bottom left into a tight coil, the second behind
	shoots = [((40, 24), 13, (14, 58)), ((24, 30), 9, (8, 54))]
	for (cx, cy), r0, (sx, sy) in reversed(shoots):
		stalk = [(sx + (cx + r0 - sx) * t, sy + (cy - sy) * t + 6 * math.sin(t * math.pi)) for t in [i / 20 for i in range(21)]]
		coil = _spiral(cx, cy, r0, 1.7, 0)
		path = stalk + coil
		_thick(d, path, 9, 4, OUTLINE)            # outline pass
		_thick(d, path, 6, 1.5, green)
		_thick(d, [(x + 1, y + 1) for x, y in path[:-6]], 3, 1, shade)
		_thick(d, [(x - 1, y - 1) for x, y in coil[4:40:3]], 2, 1, light)
		# pale cut end of the stalk and a papery brown scale
		d.ellipse((sx - 3, sy - 3, sx + 3, sy + 3), fill=(196, 210, 140, 255), outline=OUTLINE)
		mx, my = stalk[8]
		d.polygon([(mx + 2, my - 1), (mx + 7, my + 1), (mx + 3, my + 4)], fill=(150, 108, 64, 255), outline=OUTLINE)
	return im


def main():
	os.makedirs(OUT, exist_ok=True)
	for name, fn in (("resource_beehive", beehive), ("food_egg", egg), ("food_fiddlehead", fiddlehead)):
		path = os.path.join(OUT, name + ".png")
		fn().save(path)
		print("wrote", path)


if __name__ == "__main__":
	main()
