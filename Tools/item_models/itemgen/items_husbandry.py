"""Animal-management kit (2026-11-08): the leed - a long lead rope on a proper handle, the thing you
grab to clip tames on and walk them. Held frame like every tool: handle along +X, butt at the
origin (Code/Player/HeldModelFit.cs). Not coiled: the rope hangs from the handle ring in one long
loose bight with two snap clips at its end."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, tube, path_tube, box, lathe
from .kit import registry, butt_at_origin, torus

ITEMS, item = registry()


def C(rgb, d=12, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.07), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


@item("leed", "tools", roughness=0.85)
def leed(rng):
	cells = [P.wood_handle(), P.leather_dark(), C((172, 142, 72), 10), P.rope(), C((150, 118, 74), 8, streak="x", streak_amt=0.1)]
	# turned wooden grip 0.3 m long with a leather wrap, a brass cap and a brass ring at its end
	grip = lathe([(0.016, 0.0), (0.024, 0.02), (0.021, 0.1), (0.023, 0.2), (0.019, 0.27), (0.021, 0.3)], segs=9, mat=0, rng=rng, jitter=0.04)
	m = grip.rotate("y", 90)
	for k in range(5):
		m.add(torus(rng, 0.0225, 0.004, n=9, segs=4, mat=1, axis="x").translate(0.09 + 0.028 * k, 0, 0))
	m.add(lathe([(0.018, 0.0), (0.026, 0.01), (0.026, 0.035), (0.012, 0.045)], segs=9, mat=2, rng=rng, jitter=0.03).rotate("y", 90).translate(0.3, 0, 0))
	m.add(torus(rng, 0.032, 0.005, n=12, segs=5, mat=2, axis="y").translate(0.375, 0, 0))
	# the lead: one long loose bight from the ring, sagging under its own weight, back past the handle
	pts = []
	for t in np.linspace(0, 1, 26):
		pts.append((0.4 + 0.62 * math.sin(math.pi * t * 0.5) ** 0.9, 0.03 * t, -0.22 * math.sin(math.pi * t) - 0.02 * t))
	for t in np.linspace(0, 1, 22)[1:]:
		pts.append((1.02 - 0.5 * t, 0.03 + 0.09 * math.sin(math.pi * t), -0.24 - 0.07 * math.sin(math.pi * t * 0.5) + 0.02 * t))
	for t in np.linspace(0, 1, 10)[1:]:
		pts.append((0.52 + 0.3 * t, 0.12 - 0.02 * t, -0.29 - 0.1 * t))
	m.add(path_tube(rng, np.array(pts), radius=0.011, segs=6, jitter=0.1, mat=3))
	# a second shorter lead off the same ring, so it reads as a multi-tame lead
	pts2 = []
	for t in np.linspace(0, 1, 24):
		pts2.append((0.4 + 0.5 * t, -0.04 - 0.08 * math.sin(math.pi * t * 0.7), -0.03 - 0.3 * t + 0.08 * math.sin(math.pi * t)))
	m.add(path_tube(rng, np.array(pts2), radius=0.011, segs=6, jitter=0.1, mat=4))
	# snap clips at both lead ends
	for p in (np.array(pts[-1]), np.array(pts2[-1])):
		clip = box((0.05, 0.014, 0.022), (0, 0, 0), mat=2, jitter=0.03, rng=rng)
		clip.add(torus(rng, 0.014, 0.003, n=8, segs=4, mat=2, axis="y").translate(0.035, 0, 0))
		m.add(clip.rotate("y", -40).translate(*p))
	return butt_at_origin(m), cells
