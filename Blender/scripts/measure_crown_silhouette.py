"""Measure how lumpy a tree crown's outline is, so generated trees can be compared with photos.

Run with plain Python (numpy + Pillow):
    python Blender/scripts/measure_crown_silhouette.py <image> [x0 x1 y_max] ...
Each image may carry a crop: only columns x0..x1 and rows above y_max are searched (keeps the
ground, other trees and watermarks out). Without a crop the whole image is used.

Method: sky = blue-dominant or bright low-saturation pixels; everything else is tree. For every
column the top-most tree pixel gives the crown's top outline. A wide Gaussian (sigma 12 % of the
crown width) of that outline is the overall dome; what is left over are the clusters.
  lumpiness  = RMS of the leftover, in % of crown width   (a clean dome ~ 0-1 %)
  lobes      = bumps in the outline standing out > 1.2 % of the crown width
  dip        = deepest notch between neighbouring bumps, % of crown width
Only the middle 80 % of the outline is scored (the steep flanks of any crown are not clusters).
--debug=<dir> as the first argument writes <image>_outline.png overlays to check the tracing.
"""
import sys
import numpy as np


def gauss(v, sigma):
	r = int(3 * sigma) + 1
	k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma) ** 2)
	k /= k.sum()
	p = np.pad(v, r, mode="edge")
	return np.convolve(p, k, mode="valid")


def top_outline(path, crop=None):
	from PIL import Image   # only needed for images; Blender imports measure() without Pillow
	im = np.asarray(Image.open(path).convert("RGB")).astype(float)
	r, g, b = im[..., 0], im[..., 1], im[..., 2]
	mx, mn = im.max(2), im.min(2)
	sat = (mx - mn) / np.maximum(mx, 1)
	sky = ((b > g + 8) & (b > r + 8)) | ((mx > 170) & (sat < 0.18))
	tree = ~sky
	h, w = tree.shape
	x0, x1, ymax = crop if crop else (0, w, h)
	tops = np.full(x1 - x0, np.nan)
	for i, x in enumerate(range(x0, x1)):
		col = tree[:ymax, x]
		run = col[:-2] & col[1:-1] & col[2:]          # 3 tree pixels in a row, skips specks
		idx = np.flatnonzero(run)
		if len(idx):
			tops[i] = idx[0]
	return tops


def measure(tops):
	ok = ~np.isnan(tops) & (tops > 1)                 # drop columns clipped by the frame top
	xs = np.flatnonzero(ok)
	if len(xs) < 20:
		return None
	t = np.interp(np.arange(xs[0], xs[-1] + 1), xs, tops[ok])
	W = len(t)
	dome = gauss(t, 0.12 * W)
	res = gauss(t - dome, 0.015 * W)
	m = int(0.1 * W)
	res = res[m:W - m]            # middle 80 %: the steep crown flanks are not "clusters"
	lump = res.std() / W * 100
	# bumps = local minima of y (outline peaks), prominence vs the neighbouring notches
	peaks, dips = [], []
	for i in range(1, len(res) - 1):
		if res[i] < res[i - 1] and res[i] <= res[i + 1]:
			peaks.append(i)
	lobes = 0
	for a, b_ in zip(peaks, peaks[1:]):
		notch = res[a:b_ + 1].max()
		depth = notch - max(res[a], res[b_])
		dips.append(depth)
		if depth > 0.012 * W:
			lobes += 1
	return dict(width_px=W, lumpiness=round(lump, 2), lobes=lobes,
				dip=round(max(dips) / W * 100, 2) if dips else 0.0)


DEBUG = None

if __name__ == "__main__":
	args = sys.argv[1:]
	if args and args[0].startswith("--debug="):
		DEBUG = args.pop(0).split("=", 1)[1]    # write <image>_outline.png overlays here
	i = 0
	while i < len(args):
		path = args[i]
		crop = None
		if i + 3 < len(args) and all(a.lstrip("-").isdigit() for a in args[i + 1:i + 4]):
			crop = tuple(int(a) for a in args[i + 1:i + 4])
			i += 3
		i += 1
		tops = top_outline(path, crop)
		print(path.split("\\")[-1].split("/")[-1], measure(tops))
		if DEBUG:
			from PIL import Image
			im = Image.open(path).convert("RGB")
			px = im.load()
			x0 = crop[0] if crop else 0
			for k, y in enumerate(tops):
				if not np.isnan(y):
					for dy in range(-2, 3):
						yy = int(y) + dy
						if 0 <= yy < im.height:
							px[x0 + k, yy] = (255, 0, 0)
			im.save(DEBUG + "/" + path.replace("\\", "/").split("/")[-1].rsplit(".", 1)[0] + "_outline.png")
