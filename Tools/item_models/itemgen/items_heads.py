"""Trophy heads for every creature and enemy (Assets/data/resources.json trophy_*), on the existing
wall plaque (items_wearables._plaque: XZ plane, head toward -Y, standing on z = 0), and the five
cyberware modules. Animal heads share one muzzle / skull / ears builder; humans use
items_wearables.human_head with their gear on top; machines share a robot head builder.

Enemy looks are a guess from the names (no art or prefab exists for them yet): adversary / general /
enforcer / hauler / mangler / rager / stalker / traitor / warden / welder are human raiders, patrol /
volt / barrage / kamikaze / shatter machines, howler and spitter creatures."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace, basis_from_dir
from .kit import registry, ground, paint, squash, loft, torus, subdivide
from .items_wearables import _plaque, human_head, feather_card

ITEMS, item = registry()

HEAD_Y = -0.1
HEAD_Z = 0.25


def C(rgb, d=10, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.06), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


def fur(rgb, d=12):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=0.08, tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], streak="y", streak_amt=0.16)


EYE_DARK = P.black()
GLOW_RED = P.red_glow()


def mounted(rng, head, cells):
	m = _plaque(rng, 0)
	m.add(head.translate(0, HEAD_Y, HEAD_Z))
	return ground(m), [P.plaque()] + cells


def along_minus_y(m):
	"""Rotate a mesh built along +X so it points toward -Y (out of the plaque)."""
	return m.rotate("z", -90)


def eye_pair(rng, mat, dx, y, z, r, flat=0.5, pupil=None):
	m = Mesh()
	for s in (-1, 1):
		m.add(blob(rng, r, subdiv=1, jitter=0.15, noise=0.05, scale=(1, flat, 1), mat=mat).translate(s * dx, y, z))
		if pupil is not None:
			m.add(blob(rng, r * 0.5, subdiv=1, jitter=0.1, noise=0.05, scale=(1, flat, 1), mat=pupil).translate(s * dx, y - r * flat * 0.7, z))
	return m


def ear(rng, kind, mat, inner=None, size=1.0):
	s = size
	if kind == "point":
		e = prism([(-0.035 * s, 0), (0.035 * s, 0), (0.004 * s, 0.09 * s)], 0.018 * s, mat=mat, side_mat=mat, rng=rng, jitter=0.004 * s).rotate("x", 90)
		if inner is not None:
			e.add(prism([(-0.02 * s, 0.006 * s), (0.02 * s, 0.006 * s), (0.002 * s, 0.065 * s)], 0.004, mat=inner, side_mat=inner).rotate("x", 90).translate(0, -0.019 * s, 0))
	elif kind == "round":
		e = blob(rng, 0.03 * s, subdiv=1, jitter=0.2, noise=0.1, scale=(1.0, 0.45, 0.9), mat=mat).translate(0, 0, 0.02 * s)
		if inner is not None:
			e.add(blob(rng, 0.018 * s, subdiv=1, jitter=0.1, noise=0.05, scale=(1.0, 0.3, 0.8), mat=inner).translate(0, -0.012 * s, 0.02 * s))
	elif kind == "long":
		e = leaf(rng, 0.17 * s, 0.05 * s, mat=mat, bend=0.12, segments=4).rotate("x", 90).rotate("z", 180)
		e.translate(0, 0, 0)
		if inner is not None:
			e.add(leaf(rng, 0.13 * s, 0.03 * s, mat=inner, bend=0.12, segments=3).rotate("x", 90).rotate("z", 180).translate(0, -0.006 * s, 0.012 * s))
	else:  # tuft (lynx): a point with a black tuft on the tip
		e = ear(rng, "point", mat, inner, size)
		e.add(tube(rng, length=0.04 * s, radius=0.006 * s, segs=4, rings=1, profile=lambda t: 1 - 0.8 * t, mat=inner if inner is not None else mat).translate(0.004 * s, 0, 0.088 * s))
	return e


def beast_head(rng, skin, r=0.09, muzzle=0.12, muzzle_r=0.045, muzzle_flat=0.8, muzzle_mat=None, nose_mat=None, eye_mat=None, eye_r=0.014, eye_dx=0.05,
			   eye_z=0.03, ears=None, ear_mat=None, ear_inner=None, ear_dx=0.06, ear_z=0.07, ear_tilt=18, ear_size=1.0, teeth=None, jaw_open=0.0, skull=(1.0, 0.9, 0.9)):
	"""An animal head facing -Y at the origin: skull, a lofted muzzle out along -Y, a nose pad, eyes,
	ears and an optional open lower jaw with teeth."""
	m = blob(rng, r, subdiv=2, jitter=0.25, noise=0.1, noise_cells=2.0, scale=skull, mat=skin)
	mm = muzzle_mat if muzzle_mat is not None else skin
	if muzzle > 0:
		mz = loft(rng, muzzle, lambda t: muzzle_r * (1 - 0.35 * t), lambda t: muzzle_r * muzzle_flat * (1 - 0.3 * t), cz=lambda t: -0.2 * muzzle_r * t,
				  rings=4, segs=8, mat=mm)
		mz = along_minus_y(mz).translate(0, -r * 0.55, -r * 0.15)
		m.add(mz)
		tip_y = -r * 0.55 - muzzle
		if nose_mat is not None:
			m.add(blob(rng, muzzle_r * 0.45, subdiv=1, jitter=0.15, noise=0.08, scale=(1.2, 0.6, 0.8), mat=nose_mat).translate(0, tip_y + 0.004, -r * 0.15 - 0.2 * muzzle_r + muzzle_r * 0.35))
		if jaw_open > 0:
			jaw = loft(rng, muzzle * 0.9, lambda t: muzzle_r * 0.8 * (1 - 0.35 * t), lambda t: muzzle_r * 0.3, rings=3, segs=7, mat=mm)
			jaw = along_minus_y(jaw).rotate("x", -jaw_open).translate(0, -r * 0.5, -r * 0.15 - muzzle_r * 0.7)
			m.add(jaw)
		if teeth is not None:
			for s in (-1, 1):
				for k in range(3):
					t = tube(rng, length=muzzle_r * 0.45, radius=muzzle_r * 0.09, segs=4, rings=1, profile=lambda u: 1 - 0.9 * u, mat=teeth).rotate("x", 180)
					m.add(t.translate(s * muzzle_r * 0.55 * (1 - 0.25 * k), tip_y + muzzle * (0.12 + 0.25 * k), -r * 0.15 - muzzle_r * 0.55))
	if eye_mat is not None:
		m.add(eye_pair(rng, eye_mat, eye_dx, -r * 0.82, eye_z, eye_r))
	if ears is not None:
		for s in (-1, 1):
			e = ear(rng, ears, ear_mat if ear_mat is not None else skin, ear_inner, size=ear_size)
			e.rotate("y", s * ear_tilt).translate(s * ear_dx, -r * 0.15, ear_z)
			m.add(e)
	weld_displace(m, rng, r * 0.02, cells=5.0)
	return m


def _trophy(name, build_fn):
	@item(f"{name}_trophy", "trophies", surface="wood")
	def build(rng):
		head, cells = build_fn(rng)
		return mounted(rng, head, cells)
	return build


# ---------------------------------------------------------------- animals


def t_wolf(rng):
	cells = [fur((128, 124, 118)), fur((206, 200, 190)), EYE_DARK, C((214, 160, 60)), P.bone(), C((150, 60, 60))]
	h = beast_head(rng, 1, r=0.095, muzzle=0.12, muzzle_r=0.045, muzzle_mat=2, nose_mat=3, eye_mat=4, eye_r=0.013, ears="point", ear_inner=2,
				   ear_size=1.1, teeth=5, jaw_open=14)
	h.add(blob(rng, 0.09, subdiv=1, jitter=0.4, noise=0.25, scale=(1.3, 0.6, 0.9), mat=1).translate(0, 0.03, -0.06))  # neck ruff
	return h, cells


def t_coyote(rng):
	cells = [fur((168, 132, 92)), fur((220, 206, 180)), EYE_DARK, C((200, 160, 70)), P.bone()]
	h = beast_head(rng, 1, r=0.08, muzzle=0.11, muzzle_r=0.034, muzzle_mat=2, nose_mat=3, eye_mat=4, eye_r=0.011, ears="point", ear_inner=2, ear_size=1.25,
				   ear_dx=0.05, ear_tilt=12)
	return h, cells


def t_blackbear(rng):
	cells = [fur((52, 44, 40)), fur((140, 108, 76)), EYE_DARK, C((70, 60, 54))]
	h = beast_head(rng, 1, r=0.11, muzzle=0.07, muzzle_r=0.05, muzzle_mat=2, nose_mat=3, eye_mat=3, eye_r=0.011, eye_dx=0.05, ears="round", ear_inner=4,
				   ear_dx=0.08, ear_z=0.08, ear_size=1.1, skull=(1.1, 0.95, 0.95))
	return h, cells + [fur((40, 34, 32))]


def t_boar(rng):
	cells = [fur((92, 70, 52)), fur((120, 96, 74)), EYE_DARK, C((150, 110, 100)), P.bone(), fur((56, 44, 36))]
	h = beast_head(rng, 1, r=0.09, muzzle=0.13, muzzle_r=0.05, muzzle_flat=0.75, muzzle_mat=2, eye_mat=3, eye_r=0.009, eye_dx=0.055, ears="point", ear_size=0.9,
				   ear_tilt=35, skull=(1.0, 1.0, 0.95))
	tip = -0.09 * 0.55 - 0.13
	h.add(lathe([(0.03, 0.0), (0.032, 0.01), (0.0, 0.014)], segs=9, mat=4, rng=rng, jitter=0.1).rotate("x", 90).translate(0, tip + 0.006, -0.02))  # snout disc
	for s in (-1, 1):
		tusk = path_tube(rng, np.array([(s * 0.03, tip + 0.04, -0.035), (s * 0.045, tip + 0.025, -0.02), (s * 0.05, tip + 0.02, 0.005)]), radius=0.008, segs=5,
						 radius_fn=lambda t: 1 - 0.8 * t, mat=5)
		h.add(tusk)
	for k in range(5):  # bristle crest
		h.add(tube(rng, length=0.05, radius=0.012, segs=4, rings=1, profile=lambda t: 1 - 0.9 * t, mat=6).rotate("x", 20).translate(0, 0.02 * k - 0.04, 0.08 - 0.005 * k))
	return h, cells


def t_goat(rng):
	cells = [fur((226, 218, 200)), fur((206, 196, 176)), EYE_DARK, C((70, 64, 60)), C((120, 110, 96))]
	h = beast_head(rng, 1, r=0.075, muzzle=0.13, muzzle_r=0.035, muzzle_flat=1.0, muzzle_mat=2, nose_mat=4, eye_mat=3, eye_dx=0.055, eye_r=0.011, ears="point",
				   ear_size=0.8, ear_tilt=70, ear_dx=0.07, ear_z=0.02, skull=(0.9, 1.0, 1.0))
	for s in (-1, 1):
		path = np.array([(s * 0.035, -0.04, 0.06)] + [(s * (0.04 + 0.02 * math.sin(a)), -0.04 + 0.1 * math.sin(a), 0.06 + 0.1 * (1 - math.cos(a))) for a in np.linspace(0.3, 2.2, 6)])
		h.add(path_tube(rng, path, radius=0.016, segs=6, jitter=0.08, radius_fn=lambda t: 1 - 0.85 * t, mat=5))
	beard = tube(rng, length=0.08, radius=0.02, segs=5, rings=2, profile=lambda t: 1 - 0.8 * t, mat=2).rotate("x", 180).translate(0, -0.16, -0.06)
	h.add(beard)
	return h, cells + [C((60, 56, 50))]


def t_lynx(rng):
	cells = [fur((178, 146, 106)), fur((228, 216, 196)), EYE_DARK, C((196, 170, 70)), fur((60, 50, 42))]
	h = beast_head(rng, 1, r=0.085, muzzle=0.045, muzzle_r=0.04, muzzle_mat=2, nose_mat=3, eye_mat=4, eye_r=0.014, eye_dx=0.042, ears="tuft", ear_inner=5,
				   ear_size=0.85, ear_dx=0.055, ear_tilt=10, skull=(1.05, 0.9, 0.95))
	for s in (-1, 1):  # facial ruff
		h.add(prism([(0, 0), (0.07, -0.02), (0.06, 0.02), (0.075, 0.05), (0, 0.05)], 0.02, mat=2, side_mat=2, rng=rng, jitter=0.006).rotate("x", 90).translate(0, 0, -0.06)
			  .scale(s, 1, 1).translate(s * 0.05, -0.02, 0))
	c = h.centroids()
	h.mat[(h.mat == 1) & (np.sin(c[:, 0] * 120) * np.sin(c[:, 2] * 110) > 0.6)] = 5
	return h, cells


def t_otter(rng):
	cells = [fur((110, 78, 52)), fur((180, 150, 120)), EYE_DARK, C((50, 40, 36)), C((230, 226, 214))]
	h = beast_head(rng, 1, r=0.075, muzzle=0.05, muzzle_r=0.045, muzzle_flat=0.65, muzzle_mat=2, nose_mat=4, eye_mat=4, eye_r=0.011, eye_dx=0.042, eye_z=0.025,
				   ears="round", ear_size=0.5, ear_dx=0.07, ear_z=0.025, skull=(1.1, 0.95, 0.8))
	for s in (-1, 1):
		for k in range(3):
			h.add(path_tube(rng, np.array([(s * 0.03, -0.13, -0.02 + 0.006 * k), (s * 0.08, -0.14, -0.025 + 0.012 * k)]), radius=0.0015, segs=3, mat=5))
	return h, cells


def _rabbit(rng, body, pale):
	cells = [fur(body), fur(pale), EYE_DARK, C((200, 140, 140)), fur((230, 150, 150))]
	h = beast_head(rng, 1, r=0.07, muzzle=0.045, muzzle_r=0.035, muzzle_mat=2, nose_mat=4, eye_mat=3, eye_r=0.012, eye_dx=0.05, eye_z=0.015, ears="long",
				   ear_inner=5, ear_dx=0.025, ear_z=0.05, ear_tilt=12, skull=(0.95, 1.05, 1.0))
	return h, cells


def t_jackrabbit(rng):
	h, cells = _rabbit(rng, (168, 136, 104), (220, 206, 186))
	return h.scale(1.15, 1.15, 1.1), cells


def t_snowhare(rng):
	return _rabbit(rng, (232, 230, 226), (244, 242, 240))


def t_alligator(rng):
	cells = [C((84, 92, 54)), C((168, 160, 110)), C((200, 170, 60)), P.bone(), C((60, 66, 40))]
	# a long flat snout bristling with teeth, eye bumps on top, osteoderm ridges
	h = blob(rng, 0.075, subdiv=2, jitter=0.25, noise=0.1, scale=(1.1, 1.0, 0.65), mat=1)
	sn = loft(rng, 0.26, lambda t: 0.055 * (1 - 0.45 * t), lambda t: 0.028 * (1 - 0.3 * t), cz=lambda t: -0.005, rings=6, segs=8, mat=1)
	h.add(along_minus_y(sn).translate(0, -0.05, -0.01))
	paint(h, 2, lambda c, n: n[:, 2] < -0.4)
	for s in (-1, 1):
		h.add(blob(rng, 0.022, subdiv=1, jitter=0.2, noise=0.1, scale=(1, 1.2, 0.8), mat=1).translate(s * 0.035, -0.03, 0.045))
		h.add(blob(rng, 0.01, subdiv=1, jitter=0.1, noise=0.05, scale=(1, 0.5, 0.8), mat=3).translate(s * 0.035, -0.05, 0.055))
		for k in range(7):
			tt = tube(rng, length=0.022, radius=0.005, segs=4, rings=1, profile=lambda u: 1 - 0.9 * u, mat=4).rotate("x", 180 if k % 2 else 0)
			h.add(tt.translate(s * (0.05 - 0.004 * k), -0.07 - 0.03 * k, -0.01))
	for k in range(4):
		h.add(blob(rng, 0.012, subdiv=0, jitter=0.2, noise=0.1, mat=5).translate(0, 0.02 - 0.03 * k, 0.05 - 0.01 * k))
	return h.translate(0, 0.02, -0.03), cells


def t_bullfrog(rng):
	cells = [C((86, 110, 52)), C((214, 210, 160)), C((200, 170, 60)), EYE_DARK, C((60, 80, 40))]
	# wide flat head, big gold eyes on top, a wide mouth line, round eardrums
	h = blob(rng, 0.11, subdiv=2, jitter=0.25, noise=0.1, scale=(1.3, 0.95, 0.55), mat=1)
	paint(h, 2, lambda c, n: n[:, 2] < -0.3)
	for s in (-1, 1):
		h.add(blob(rng, 0.032, subdiv=1, jitter=0.15, noise=0.05, mat=3).translate(s * 0.065, -0.05, 0.045))
		h.add(blob(rng, 0.016, subdiv=1, jitter=0.1, noise=0.05, scale=(1.4, 0.6, 0.8), mat=4).translate(s * 0.068, -0.078, 0.048))
		h.add(lathe([(0.025, 0), (0.0, 0.005)], segs=8, mat=5).rotate("y", s * 90).translate(s * 0.13, 0.0, 0.01))
	h.add(path_tube(rng, np.array([(-0.12, -0.03, -0.01), (-0.06, -0.09, -0.015), (0.0, -0.105, -0.015), (0.06, -0.09, -0.015), (0.12, -0.03, -0.01)]), radius=0.004,
					segs=4, mat=5))
	return h, cells


def t_turtle(rng):
	cells = [C((104, 112, 64)), C((200, 190, 140)), EYE_DARK, C((70, 76, 44)), C((60, 52, 40))]
	# a snapping-turtle head: beaked jaw, wrinkled neck, a scrap of shell rim behind
	h = loft(rng, 0.2, lambda t: 0.05 * (1 - 0.3 * t), lambda t: 0.045 * (1 - 0.3 * t), rings=5, segs=8, mat=1)
	h = along_minus_y(h).translate(0, 0.05, -0.01)
	beak = prism([(-0.04, 0), (0.04, 0), (0.0, 0.05)], 0.03, mat=5, side_mat=5, rng=rng, jitter=0.004).rotate("x", 180).translate(0, -0.15, 0.0)
	h.add(beak)
	paint(h, 2, lambda c, n: n[:, 2] < -0.5)
	h.add(eye_pair(rng, 3, 0.03, -0.13, 0.02, 0.009))
	rim = lathe([(0.075, 0.0), (0.08, 0.015), (0.065, 0.03)], segs=10, mat=4, rng=rng, jitter=0.1, closed_bottom=False).rotate("x", 90).translate(0, 0.07, -0.005)
	h.add(rim)
	return h, cells


def t_iguana(rng):
	cells = [C((96, 136, 70)), C((150, 170, 90)), C((200, 170, 70)), EYE_DARK, C((70, 100, 50))]
	# iguana: blunt snout, big round cheek scale, a hanging dewlap and a crest of spines down the back
	h = beast_head(rng, 1, r=0.065, muzzle=0.08, muzzle_r=0.035, muzzle_flat=0.75, eye_mat=4, eye_r=0.01, eye_dx=0.045, eye_z=0.02, skull=(0.95, 1.1, 0.85))
	for s in (-1, 1):
		h.add(lathe([(0.018, 0), (0.0, 0.004)], segs=8, mat=2).rotate("y", s * 90).translate(s * 0.06, 0.0, -0.03))
	dew = prism([(0, 0), (0.09, 0.0), (0.03, -0.07)], 0.008, mat=2, side_mat=2, rng=rng, jitter=0.004).rotate("x", 90).rotate("z", 90).translate(0, -0.09, -0.04)
	h.add(dew)
	for k in range(6):
		h.add(tube(rng, length=0.05 - 0.005 * k, radius=0.006, segs=4, rings=1, profile=lambda t: 1 - 0.9 * t, mat=5).translate(0, -0.03 + 0.025 * k, 0.05 - 0.004 * k))
	return h, cells


def t_shark(rng):
	cells = [C((110, 116, 124)), C((220, 222, 220)), EYE_DARK, P.bone(), C((120, 50, 50))]
	# a great white's head: pointed conical snout, black eye, gill slits, a gaping jaw of triangle teeth
	h = loft(rng, 0.26, lambda t: 0.08 * (1 - t ** 1.6), lambda t: 0.075 * (1 - t ** 1.4), cz=lambda t: 0.02 * t, rings=7, segs=10, mat=1)
	h = along_minus_y(h).translate(0, 0.07, 0)
	paint(h, 2, lambda c, n: n[:, 2] < -0.35)
	h.add(eye_pair(rng, 3, 0.055, -0.09, 0.02, 0.01))
	mouth = prism([(-0.06, 0), (0.06, 0), (0.04, -0.03), (-0.04, -0.03)], 0.02, mat=5, side_mat=5).rotate("x", 90).translate(0, -0.09, -0.03)
	h.add(mouth)
	for k in range(7):
		h.add(prism([(-0.008, 0), (0.008, 0), (0, -0.016)], 0.004, mat=4, side_mat=4).rotate("x", 90).translate(-0.05 + k * 0.0165, -0.105, -0.03))
	for s in (-1, 1):
		for k in range(3):
			h.add(box(size=(0.004, 0.006, 0.05), center=(s * 0.075, 0.03 + 0.015 * k, 0.0), mat=5))
	return h, cells


def t_stingray(rng):
	cells = [C((130, 108, 84)), C((230, 222, 210)), EYE_DARK, C((100, 82, 64))]
	# a stingray's forebody mounted flat-on: the diamond disc, eyes and spiracles on top, a short tail stub
	disc = prism([(0, 0.13), (0.07, 0.11), (0.14, 0.05), (0.19, -0.02), (0.13, -0.06), (0.05, -0.1), (0, -0.11), (-0.05, -0.1), (-0.13, -0.06), (-0.19, -0.02), (-0.14, 0.05),
				  (-0.07, 0.11)], 0.024, z0=-0.012, mat=1, side_mat=4, rng=rng, jitter=0.008)
	disc = subdivide(disc, 2)
	squash(disc, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.03 * (1 - np.clip(np.hypot(p[:, 0], p[:, 1]) / 0.16, 0, 1))]))
	disc.add(eye_pair(rng, 3, 0.03, 0.05, 0.045, 0.01, flat=0.8))
	disc.add(tube(rng, length=0.08, radius=0.012, segs=5, rings=2, profile=lambda t: 1 - 0.7 * t, mat=4).rotate("x", 90).translate(0, -0.11, 0.0))
	disc.rotate("x", 90)  # face out toward -Y
	return disc.translate(0, -0.02, 0), cells


def t_raven(rng):
	cells = [C((34, 32, 38)), C((50, 48, 58)), EYE_DARK, C((30, 28, 30))]
	h = blob(rng, 0.085, subdiv=2, jitter=0.3, noise=0.12, scale=(0.95, 0.85, 1.0), mat=1)
	beak = loft(rng, 0.12, lambda t: 0.026 * (1 - 0.85 * t), lambda t: 0.03 * (1 - 0.7 * t), cz=lambda t: -0.02 * t * t, rings=4, segs=6, mat=4)
	h.add(along_minus_y(beak).translate(0, -0.06, -0.005))
	h.add(eye_pair(rng, 3, 0.05, -0.07, 0.03, 0.012))
	for k in range(5):  # shaggy throat hackles
		f = feather_card(rng, 0.07, 0.03, 2, bend=0.2).rotate("x", 200).rotate("z", -30 + 15 * k).translate(-0.03 + 0.015 * k, -0.05, -0.05)
		h.add(f)
	return h, cells


def t_vulture(rng):
	cells = [C((190, 120, 110)), C((70, 54, 46)), C((220, 212, 190)), EYE_DARK, C((60, 50, 44))]
	# bald wrinkled red head, a hooked pale beak, a dark feather ruff round the neck
	h = blob(rng, 0.065, subdiv=2, jitter=0.3, noise=0.15, scale=(0.9, 1.0, 0.95), mat=1).translate(0, -0.03, 0.03)
	beak = path_tube(rng, np.array([(0, -0.08, 0.03), (0, -0.12, 0.03), (0, -0.15, 0.015), (0, -0.155, -0.005)]), radius=0.02, segs=6, radius_fn=lambda t: 1 - 0.75 * t,
					 mat=3)
	h.add(beak)
	h.add(eye_pair(rng, 4, 0.04, -0.075, 0.05, 0.009))
	neck = tube(rng, length=0.08, radius=0.03, segs=7, rings=2, mat=1).rotate("x", -60).translate(0, 0.0, -0.05)
	h.add(neck)
	ruff = blob(rng, 0.1, subdiv=1, jitter=0.5, noise=0.3, scale=(1.2, 0.7, 0.6), mat=2).translate(0, 0.04, -0.08)
	h.add(ruff)
	return h, cells


def t_raptor(rng):
	cells = [C((120, 116, 70)), C((190, 180, 130)), C((210, 160, 50)), P.bone(), C((70, 70, 44)), C((150, 60, 50))]
	# a raptor (dromaeosaur): long narrow jaws full of teeth, a bony brow over a slit-pupil eye, stripes
	h = beast_head(rng, 1, r=0.07, muzzle=0.17, muzzle_r=0.04, muzzle_flat=1.0, eye_mat=3, eye_r=0.012, eye_dx=0.045, eye_z=0.035, teeth=4, jaw_open=18,
				   skull=(0.9, 1.15, 0.95))
	paint(h, 2, lambda c, n: n[:, 2] < -0.45)
	c = h.centroids()
	h.mat[(h.mat == 1) & (np.sin(c[:, 1] * 70) > 0.6) & (c[:, 2] > 0.0)] = 5
	for s in (-1, 1):
		h.add(blob(rng, 0.02, subdiv=1, jitter=0.2, noise=0.1, scale=(1.2, 1.3, 0.5), mat=5).translate(s * 0.04, -0.06, 0.055))
	return h, cells + [C((140, 120, 80))]


def t_crab(rng):
	cells = [C((200, 84, 54)), C((230, 180, 140)), EYE_DARK, C((170, 64, 42))]
	# a crab's front: the wide carapace mounted face-on, eyes on stalks, two big claws folded in front
	cara = blob(rng, 0.13, subdiv=2, jitter=0.25, noise=0.1, scale=(1.3, 0.55, 0.8), mat=1)
	for s in (-1, 1):
		cara.add(path_tube(rng, np.array([(s * 0.03, -0.06, 0.07), (s * 0.035, -0.07, 0.1)]), radius=0.007, segs=4, mat=4))
		cara.add(blob(rng, 0.012, subdiv=1, jitter=0.1, noise=0.05, mat=3).translate(s * 0.035, -0.07, 0.105))
		claw = blob(rng, 0.055, subdiv=1, jitter=0.25, noise=0.12, scale=(1.4, 0.8, 0.8), mat=1)
		for k, ln in ((1, 0.07), (-1, 0.055)):
			claw.add(tube(rng, length=ln, radius=0.016, segs=5, rings=2, jitter=0.08, profile=lambda t: 1 - 0.85 * t, mat=4, axis="x").rotate("z", k * 10).translate(0.06, k * 0.012, 0))
		claw.rotate("z", s * 160 if s < 0 else 20).translate(s * 0.1, -0.08, -0.05)
		cara.add(claw)
	for k in range(5):
		cara.add(tube(rng, length=0.02, radius=0.006, segs=4, rings=1, profile=lambda t: 1 - 0.9 * t, mat=4).rotate("x", 90).translate(-0.08 + 0.04 * k, -0.07, 0.02))
	paint(cara, 2, lambda c, n: (n[:, 2] < -0.6) & (cara.mat[np.arange(len(n))] == 1))
	return cara, cells


def t_dragonfly(rng):
	cells = [C((70, 120, 100)), C((60, 150, 140)), C((30, 40, 40)), C((180, 200, 190))]
	# a dragonfly head scaled to a trophy: two huge wrap-around compound eyes, a small face and mandibles
	h = blob(rng, 0.05, subdiv=1, jitter=0.2, noise=0.1, scale=(1.0, 0.8, 1.0), mat=1)
	for s in (-1, 1):
		e = blob(rng, 0.075, subdiv=2, jitter=0.15, noise=0.05, scale=(0.85, 0.9, 1.0), mat=2).translate(s * 0.06, -0.02, 0.03)
		c = e.centroids()
		e.mat[np.sin(c[:, 0] * 200) * np.sin(c[:, 2] * 200) > 0.5] = 3
		h.add(e)
		h.add(prism([(0, 0), (0.03, -0.01), (0.012, -0.03)], 0.01, mat=3, side_mat=3).rotate("x", 90).scale(s, 1, 1).translate(s * 0.012, -0.06, -0.05))
		h.add(path_tube(rng, np.array([(s * 0.01, -0.05, 0.05), (s * 0.02, -0.07, 0.08)]), radius=0.002, segs=3, mat=3))
	return h, cells


def t_hornet(rng):
	cells = [C((214, 170, 40)), C((40, 34, 30)), C((90, 60, 40)), C((70, 50, 40))]
	# a hornet head: yellow face, big dark kidney eyes, ocelli, heavy mandibles, elbowed antennae
	h = blob(rng, 0.085, subdiv=2, jitter=0.2, noise=0.08, scale=(1.1, 0.75, 1.0), mat=1)
	for s in (-1, 1):
		h.add(blob(rng, 0.045, subdiv=1, jitter=0.15, noise=0.05, scale=(0.6, 0.7, 1.3), mat=2).translate(s * 0.07, -0.035, 0.01))
		mand = prism([(0, 0), (0.05, -0.01), (0.04, -0.035), (0.0, -0.02)], 0.016, mat=3, side_mat=3, rng=rng, jitter=0.003)
		h.add(mand.rotate("x", 90).scale(s, 1, 1).translate(s * 0.012, -0.06, -0.07))
		ant = path_tube(rng, np.array([(s * 0.02, -0.06, 0.06), (s * 0.03, -0.09, 0.11), (s * 0.06, -0.13, 0.13), (s * 0.08, -0.15, 0.12)]), radius=0.004, segs=4, mat=2)
		h.add(ant)
	h.add(eye_pair(rng, 2, 0.015, -0.055, 0.07, 0.006))
	h.add(box(size=(0.09, 0.01, 0.03), center=(0, -0.06, -0.03), mat=4))
	return h, cells


def _worm_head(rng, r, body, inner, tooth, rings=2, spikes=None):
	# a sand worm's front segment mounted face-on: armoured ring segments and a round gaping mouth
	m = Mesh()
	for k in range(3):
		seg = lathe([(r * (1.0 - 0.06 * k), 0.0), (r * (1.04 - 0.06 * k), 0.03), (r * (0.98 - 0.06 * k), 0.06)], segs=11, mat=body, rng=rng, jitter=0.08,
					closed_bottom=False, closed_top=False)
		m.add(seg.translate(0, 0, -0.06 * k))
	mouth = lathe([(r * 0.95, 0.06), (r * 0.6, 0.05), (r * 0.25, 0.0), (0.0, -0.02)], segs=11, mat=inner, rng=rng, jitter=0.08)
	m.add(mouth)
	for ring in range(rings):
		rr = r * (0.85 - 0.25 * ring)
		n = 9 - 2 * ring
		for k in range(n):
			a = 2 * math.pi * k / n + ring * 0.3
			t = tube(rng, length=r * 0.35, radius=r * 0.06, segs=4, rings=1, profile=lambda u: 1 - 0.9 * u, mat=tooth)
			d = np.array([-math.cos(a) * 0.6, -math.sin(a) * 0.6, 0.5])
			t.transform(basis_from_dir(d / np.linalg.norm(d))).translate(rr * math.cos(a), rr * math.sin(a), 0.055 - 0.02 * ring)
			m.add(t)
	if spikes is not None:
		for k in range(6):
			a = 2 * math.pi * k / 6
			t = tube(rng, length=r * 0.5, radius=r * 0.1, segs=5, rings=1, profile=lambda u: 1 - 0.9 * u, mat=spikes)
			d = np.array([math.cos(a), math.sin(a), 0.4])
			t.transform(basis_from_dir(d / np.linalg.norm(d))).translate(r * math.cos(a), r * math.sin(a), 0.0)
			m.add(t)
	m.rotate("x", 90)  # mouth faces -Y
	return m


def t_dunewyrm(rng):
	cells = [C((180, 140, 90)), C((110, 50, 50)), P.bone()]
	return _worm_head(rng, 0.13, 1, 2, 3, rings=2), cells


def t_greatdunewyrm(rng):
	cells = [C((168, 104, 74)), C((90, 30, 34)), P.bone(), C((80, 60, 50))]
	return _worm_head(rng, 0.15, 1, 2, 3, rings=3, spikes=4), cells


def t_leech(rng):
	cells = [C((96, 64, 58)), C((150, 70, 70)), C((220, 210, 190))]
	# a giant leech's head-end: a tapered slick tube ending in a round sucker ringed with tiny teeth
	m = lathe([(0.07, -0.1), (0.075, -0.04), (0.07, 0.0), (0.06, 0.03), (0.04, 0.035), (0.0, 0.03)], segs=11, mat=1, rng=rng, jitter=0.1)
	c = m.centroids()
	m.mat[(c[:, 2] > 0.028)] = 2
	for k in range(10):
		a = 2 * math.pi * k / 10
		m.add(tube(rng, length=0.012, radius=0.004, segs=4, rings=1, profile=lambda u: 1 - 0.9 * u, mat=3).translate(0.035 * math.cos(a), 0.035 * math.sin(a), 0.03))
	for z in (-0.06, -0.02):
		m.add(torus(rng, 0.074, 0.004, n=11, segs=4, mat=1).translate(0, 0, z))
	return m.rotate("x", 90).translate(0, 0.02, 0), cells


# ---------------------------------------------------------------- humans


SKIN_A = C((176, 134, 100))
SKIN_B = C((140, 100, 76))
SKIN_GREY = P.skin_grey()


def _human(rng, skin_cell, extra, eye=None, mouth=None, teeth=False, r=0.1):
	cells = [skin_cell, eye if eye is not None else EYE_DARK, mouth if mouth is not None else P.black(), P.bone()] + extra
	h = human_head(rng, skin=1, eye_mat=2, mouth_mat=3, teeth_mat=4 if teeth else None, y=0.0, z=0.0, r=r)
	return h, cells


def t_adversary(rng):
	# a raider: a cloth mask tied over the lower face, a leather skullcap, red-lit eyes
	h, cells = _human(rng, SKIN_A, [P.cloth_red(), P.leather_dark()], eye=GLOW_RED)
	h.add(blob(rng, 0.1, subdiv=1, jitter=0.2, noise=0.1, scale=(1.02, 1.02, 0.5), mat=6).translate(0, 0.005, 0.06))
	mask = blob(rng, 0.085, subdiv=1, jitter=0.2, noise=0.1, scale=(1.1, 0.75, 0.55), mat=5).translate(0, -0.035, -0.045)
	h.add(mask)
	h.add(blob(rng, 0.02, subdiv=1, jitter=0.3, noise=0.2, mat=5).translate(0.0, 0.09, -0.02))  # knot
	return h, cells


def t_adversarygeneral(rng):
	# the general: a peaked officer's cap with a badge, an eye patch and a scarred grey beard
	h, cells = _human(rng, SKIN_B, [C((60, 70, 50)), C((40, 36, 34)), C((200, 170, 70)), C((150, 150, 146))], eye=GLOW_RED)
	cap = lathe([(0.105, 0.0), (0.11, 0.02), (0.12, 0.05), (0.0, 0.055)], segs=11, mat=5, rng=rng, jitter=0.06).translate(0, 0.0, 0.075)
	h.add(cap)
	h.add(prism([(-0.08, 0), (0.08, 0), (0.06, -0.05), (-0.06, -0.05)], 0.008, mat=6, side_mat=6).translate(0, -0.08, 0.075))
	h.add(blob(rng, 0.015, subdiv=1, jitter=0.1, noise=0.05, scale=(1, 0.4, 1), mat=7).translate(0, -0.115, 0.11))
	h.add(box(size=(0.04, 0.01, 0.03), center=(-0.036, -0.1, 0.02), mat=6))
	h.add(blob(rng, 0.06, subdiv=1, jitter=0.4, noise=0.3, scale=(1.3, 0.8, 0.9), mat=8).translate(0, -0.07, -0.08))
	return h, cells


def t_enforcer(rng):
	# riot enforcer: a full helmet with a dark visor down, a chin guard and a ridge
	h, cells = _human(rng, SKIN_A, [C((50, 54, 60)), C((28, 34, 44), 4), C((90, 96, 104))])
	helm = blob(rng, 0.118, subdiv=2, jitter=0.1, noise=0.05, scale=(0.95, 1.05, 1.0), mat=5).translate(0, 0.01, 0.025)
	h.add(helm)
	visor = blob(rng, 0.1, subdiv=1, jitter=0.1, noise=0.05, scale=(0.9, 0.5, 0.45), mat=6).translate(0, -0.075, 0.02)
	h.add(visor)
	h.add(box(size=(0.1, 0.03, 0.04), center=(0, -0.085, -0.07), mat=7))
	h.add(box(size=(0.014, 0.2, 0.012), center=(0, 0.0, 0.14), mat=7))
	return h, cells


def t_hauler(rng):
	# a hauler: a huge bald head on a bull neck, a broken nose with a ring, a strap scar across the scalp
	h, cells = _human(rng, SKIN_B, [C((110, 80, 54)), P.metal()], teeth=True, r=0.115)
	h.add(tube(rng, length=0.06, radius=0.07, segs=9, rings=1, mat=1).rotate("x", -90).translate(0, 0.0, -0.13))
	h.add(torus(rng, 0.01, 0.0025, n=8, segs=3, mat=6, axis="x").translate(0, -0.12, -0.0))
	h.add(box(size=(0.2, 0.02, 0.025), center=(0, 0.0, 0.08), mat=5).rotate("y", 10))
	return h, cells


def t_mangler(rng):
	# a mangler: a stitched patchwork face, one eye replaced by a lens, a bolted metal jaw of saw teeth
	h, cells = _human(rng, SKIN_GREY, [C((40, 34, 30)), P.metal(), GLOW_RED, C((200, 200, 196))])
	c = h.centroids()
	h.mat[(h.mat == 1) & (np.abs(c[:, 0] - 0.01 * np.sin(c[:, 2] * 60)) < 0.006)] = 5
	h.mat[(h.mat == 1) & (np.abs(c[:, 2] - 0.02) < 0.005)] = 5
	jaw = box(size=(0.1, 0.05, 0.05), center=(0, -0.075, -0.07), mat=6, jitter=0.1, rng=rng)
	h.add(jaw)
	for k in range(6):
		h.add(prism([(-0.007, 0), (0.007, 0), (0, 0.016)], 0.004, mat=8, side_mat=8).rotate("x", 90).translate(-0.04 + 0.016 * k, -0.1, -0.05))
	h.add(lathe([(0.022, 0), (0.024, 0.012), (0.012, 0.02)], segs=8, mat=6).rotate("x", 90).translate(0.036, -0.1, 0.02))
	h.add(blob(rng, 0.01, subdiv=0, mat=7).translate(0.036, -0.12, 0.02))
	return h, cells


def t_rager(rng):
	# a rager: screaming open mouth, red war paint over the eyes, a tall spiked mohawk
	h, cells = _human(rng, SKIN_A, [C((170, 40, 34)), C((60, 30, 26)), P.cloth_red()], eye=GLOW_RED, teeth=True)
	c = h.centroids()
	n = h.normals()
	h.mat[(h.mat == 1) & (np.abs(c[:, 2] - 0.02) < 0.025) & (n[:, 1] < -0.3)] = 5
	h.add(blob(rng, 0.03, subdiv=1, jitter=0.2, noise=0.1, scale=(1.3, 0.5, 1.0), mat=6).translate(0, -0.095, -0.05))
	for k in range(6):
		h.add(tube(rng, length=0.07 - 0.006 * abs(k - 2), radius=0.014, segs=4, rings=1, profile=lambda t: 1 - 0.9 * t, mat=7).rotate("x", -15 + 8 * k).translate(0, -0.06 + 0.03 * k, 0.09))
	return h, cells


def t_stalker(rng):
	# a stalker: a deep dark hood, only the lower face and two cold glowing eyes showing
	h, cells = _human(rng, SKIN_GREY, [C((34, 32, 34)), C((20, 20, 22))], eye=Cell((150, 230, 220), noise=0.03))
	hood = lathe([(0.13, -0.12), (0.135, -0.05), (0.13, 0.03), (0.11, 0.09), (0.07, 0.13), (0.0, 0.145)], segs=11, mat=5, rng=rng, jitter=0.1)
	squash(hood, lambda p: p * np.array([1.0, 1.05, 1.0]) + np.column_stack([np.zeros(len(p)), 0.02 * np.ones(len(p)), np.zeros(len(p))]))
	hood.tris = hood.tris[hood.centroids()[:, 1] > -0.06]
	hood.mat = hood.mat[:len(hood.tris)]
	h.add(hood)
	h.add(lathe([(0.12, -0.12), (0.12, 0.1)], segs=11, mat=6, closed_bottom=False, closed_top=False).flip().translate(0, 0.02, 0))
	return h, cells


def t_traitor(rng):
	# the surveyor traitor: a surveyor's brimmed field cap, brass goggles pushed up, a short beard
	h, cells = _human(rng, SKIN_A, [C((150, 132, 90)), C((180, 140, 60)), C((214, 150, 60), 6), C((90, 66, 46))])
	cap = lathe([(0.15, 0.0), (0.15, 0.008), (0.1, 0.012), (0.1, 0.05), (0.0, 0.06)], segs=12, mat=5, rng=rng, jitter=0.06).translate(0, 0, 0.07)
	h.add(cap)
	for s in (-1, 1):
		h.add(lathe([(0.022, 0), (0.024, 0.014), (0.018, 0.02)], segs=8, mat=6).rotate("x", 70).translate(s * 0.035, -0.085, 0.075))
		h.add(lathe([(0.017, 0.018), (0.0, 0.022)], segs=8, mat=7).rotate("x", 70).translate(s * 0.035, -0.085, 0.075))
	h.add(blob(rng, 0.07, subdiv=1, jitter=0.4, noise=0.3, scale=(1.2, 0.7, 0.6), mat=8).translate(0, -0.07, -0.08))
	return h, cells


def t_warden(rng):
	# a warden: a gas mask with twin filters and round lenses under a brimmed steel helmet
	h, cells = _human(rng, SKIN_A, [C((70, 74, 66)), C((40, 40, 42)), C((200, 190, 150), 6), C((110, 112, 104))])
	helm = lathe([(0.16, 0.0), (0.16, 0.01), (0.115, 0.02), (0.11, 0.06), (0.07, 0.1), (0.0, 0.11)], segs=12, mat=5, rng=rng, jitter=0.05).translate(0, 0, 0.04)
	h.add(helm)
	mask = blob(rng, 0.09, subdiv=1, jitter=0.15, noise=0.08, scale=(1.0, 0.6, 0.8), mat=6).translate(0, -0.06, -0.01)
	h.add(mask)
	for s in (-1, 1):
		h.add(lathe([(0.022, 0), (0.0, 0.008)], segs=8, mat=7).rotate("x", 90).translate(s * 0.036, -0.11, 0.025))
		h.add(lathe([(0.025, 0.0), (0.025, 0.04), (0.0, 0.042)], segs=8, mat=8, rng=rng, jitter=0.05).rotate("x", 110).rotate("z", s * 30).translate(s * 0.045, -0.1, -0.06))
	return h, cells


def t_welder(rng):
	# a welder: the welding mask flipped up on its hinge over the brow, sooty face, a dark eye slot in the mask
	h, cells = _human(rng, SKIN_B, [C((60, 62, 66)), C((30, 46, 40), 4), C((50, 40, 34))])
	c = h.centroids()
	h.mat[(h.mat == 1) & (c[:, 2] < -0.02) & (np.sin(c[:, 0] * 80) > 0.4)] = 7
	shell = blob(rng, 0.12, subdiv=1, jitter=0.1, noise=0.05, scale=(0.95, 0.55, 1.0), mat=5)
	shell.tris = shell.tris[shell.centroids()[:, 1] < 0.0]
	shell.mat = shell.mat[:len(shell.tris)]
	shell.add(box(size=(0.1, 0.01, 0.03), center=(0, -0.066, 0.0), mat=6))
	shell.rotate("x", -75).translate(0, -0.02, 0.1)
	h.add(shell)
	for s in (-1, 1):
		h.add(lathe([(0.015, 0), (0.015, 0.01)], segs=7, mat=5).rotate("y", s * 90).translate(s * 0.1, 0.0, 0.05))
	return h, cells


def t_howler(rng):
	cells = [C((150, 132, 120)), C((90, 40, 40)), Cell((230, 220, 120), noise=0.04), P.bone(), C((110, 96, 86))]
	# a howler: a gaunt hairless ghoul with a long jaw hung wide open mid-howl and swept-back ears
	h = beast_head(rng, 1, r=0.085, muzzle=0.07, muzzle_r=0.045, muzzle_flat=0.9, eye_mat=3, eye_r=0.014, eye_dx=0.04, eye_z=0.03, ears="point", ear_size=1.1,
				   ear_tilt=60, ear_dx=0.075, ear_z=0.04, teeth=4, jaw_open=38, skull=(0.9, 1.0, 1.05))
	h.add(blob(rng, 0.03, subdiv=1, jitter=0.2, noise=0.1, scale=(1.2, 0.5, 0.8), mat=2).translate(0, -0.12, -0.05))
	c = h.centroids()
	h.mat[(h.mat == 1) & (np.sin(c[:, 1] * 90 + c[:, 0] * 40) > 0.75)] = 5
	return h, cells


def t_spitter(rng):
	cells = [C((120, 126, 66)), C((180, 200, 80)), C((214, 180, 60)), P.bone(), C((150, 90, 50))]
	# a spitter: a frilled lizard head, the neck frill spread behind it, swollen acid sacs at the throat
	h = beast_head(rng, 1, r=0.07, muzzle=0.09, muzzle_r=0.035, muzzle_flat=0.85, eye_mat=3, eye_r=0.012, eye_dx=0.045, eye_z=0.03, teeth=4, jaw_open=24)
	frill = Mesh()
	for k in range(9):
		a = math.radians(-100 + 25 * k)
		f = prism([(0, 0), (0.12, -0.025), (0.13, 0.025)], 0.006, mat=5 if k % 2 else 1, side_mat=1, rng=rng, jitter=0.006).rotate("x", 90)
		frill.add(f.rotate("y", -math.degrees(a)).translate(0, 0.02, 0))
	h.add(frill)
	for s in (-1, 1):
		h.add(blob(rng, 0.028, subdiv=1, jitter=0.2, noise=0.1, mat=2).translate(s * 0.04, -0.05, -0.07))
	return h, cells


# ---------------------------------------------------------------- machines


def robot_head(rng, body, plate, dark, glow, w=0.1, h=0.11, d=0.1):
	# a chamfered eight-sided can, narrower at the crown and the chin, so it never reads as a plain box
	m = lathe([(w * 0.7, -h), (w * 1.05, -h * 0.7), (w * 1.1, h * 0.45), (w * 0.85, h * 0.9), (w * 0.4, h * 1.02), (0.0, h * 1.04)], segs=8, mat=body, rng=rng, jitter=0.06)
	m.rotate("z", 22.5)
	squash(m, lambda p: p * np.array([1.0, d / w, 1.0]))
	m = subdivide(m, 1)
	weld_displace(m, rng, 0.004, cells=4)
	face = prism([(-w * 0.85, -h * 0.7), (w * 0.85, -h * 0.7), (w * 0.95, h * 0.5), (w * 0.6, h * 0.85), (-w * 0.6, h * 0.85), (-w * 0.95, h * 0.5)], 0.012,
				 mat=plate, side_mat=dark, rng=rng, jitter=0.004)
	m.add(face.rotate("x", 90).translate(0, -d, 0))
	m.add(box(size=(w * 1.2, 0.02, h * 0.35), center=(0, -d - 0.008, -h * 0.75), mat=dark))
	for s in (-1, 1):
		m.add(lathe([(0.035, 0), (0.035, 0.02), (0.02, 0.03)], segs=8, mat=dark, rng=rng, jitter=0.05).rotate("y", s * 90).translate(s * w, 0, 0.0))
	neck = tube(rng, length=0.06, radius=0.05, segs=8, rings=1, mat=dark).translate(0, 0.02, -h - 0.06)
	m.add(neck)
	for s in (-1, 1):
		m.add(tube(rng, length=0.08, radius=0.008, segs=5, rings=1, mat=plate).rotate("y", s * 25).translate(s * 0.04, 0.03, -h - 0.07))
	return m


def t_patrol(rng):
	cells = [C((100, 110, 120)), C((150, 156, 162)), C((40, 42, 46)), GLOW_RED]
	# patrol bot: a boxy head with one round red sensor eye behind a ring, an antenna
	m = robot_head(rng, 1, 2, 3, 4)
	m.add(lathe([(0.04, 0), (0.042, 0.015), (0.03, 0.02)], segs=10, mat=3, rng=rng, jitter=0.04).rotate("x", 90).translate(0, -0.112, 0.02))
	m.add(blob(rng, 0.024, subdiv=1, jitter=0.1, noise=0.05, scale=(1, 0.5, 1), mat=4).translate(0, -0.125, 0.02))
	m.add(tube(rng, length=0.09, radius=0.004, segs=4, rings=1, mat=3).translate(0.06, 0, 0.11))
	m.add(blob(rng, 0.01, subdiv=0, mat=4).translate(0.06, 0, 0.2))
	return m, cells


def t_volt(rng):
	cells = [C((60, 66, 80)), C((110, 120, 140)), C((30, 32, 38)), Cell((120, 220, 255), noise=0.03), C((196, 116, 60))]
	# volt: a dark head crowned with two copper tesla coils, a visor slit glowing blue
	m = robot_head(rng, 1, 2, 3, 4)
	m.add(box(size=(0.15, 0.01, 0.02), center=(0, -0.115, 0.03), mat=4))
	for s in (-1, 1):
		coil = Mesh()
		for k in range(5):
			coil.add(torus(rng, 0.018, 0.004, n=8, segs=4, mat=5).translate(0, 0, 0.012 * k))
		coil.add(blob(rng, 0.016, subdiv=1, jitter=0.1, noise=0.05, mat=4).translate(0, 0, 0.075))
		m.add(coil.translate(s * 0.06, 0, 0.11))
	return m, cells


def t_barrage(rng):
	cells = [C((96, 92, 70)), C((140, 134, 104)), C((36, 36, 34)), Cell((255, 150, 60), noise=0.03)]
	# barrage: a squat armoured head with a rocket pod of six tubes on top, an orange eye slit
	m = robot_head(rng, 1, 2, 3, 4, w=0.11, h=0.09)
	m.add(box(size=(0.16, 0.012, 0.02), center=(0, -0.115, 0.02), mat=4))
	pod = box(size=(0.12, 0.12, 0.07), center=(0, 0.0, 0.13), mat=2, jitter=0.08, rng=rng)
	for i in range(3):
		for j in range(2):
			pod.add(lathe([(0.014, 0.0), (0.014, 0.06)], segs=7, mat=3, closed_bottom=True, closed_top=False).rotate("x", 90).translate(-0.035 + 0.035 * i, -0.04, 0.115 + 0.03 * j))
	m.add(pod)
	return m, cells


def t_kamikaze(rng):
	cells = [C((150, 130, 50)), C((40, 40, 40)), C((30, 30, 32)), GLOW_RED, C((200, 190, 180))]
	# kamikaze: a round bomb-head striped in hazard yellow and black, a red warning lamp and a fuse cap
	m = blob(rng, 0.12, subdiv=2, jitter=0.12, noise=0.05, mat=1)
	c = m.centroids()
	m.mat[np.sin((c[:, 0] + c[:, 2]) * 45) > 0.3] = 2
	m.add(lathe([(0.03, 0.0), (0.03, 0.02), (0.0, 0.025)], segs=8, mat=3).translate(0, 0, 0.115))
	m.add(blob(rng, 0.022, subdiv=1, jitter=0.1, noise=0.05, mat=4).translate(0, 0, 0.145))
	m.add(eye_pair(rng, 4, 0.04, -0.11, 0.02, 0.016))
	m.add(box(size=(0.1, 0.02, 0.03), center=(0, -0.11, -0.04), mat=5))
	return m.translate(0, 0, 0.0), cells


def t_shatter(rng):
	cells = [C((150, 160, 170)), C((200, 220, 230), 6), C((50, 54, 60)), Cell((160, 230, 255), noise=0.03)]
	# shatter: a glass-faced machine head, the faceplate cracked open with crystal shards growing out of it
	m = robot_head(rng, 1, 2, 3, 4)
	c = m.centroids()
	m.mat[(m.mat == 2) & (np.abs(np.sin(c[:, 0] * 60 + c[:, 2] * 40)) < 0.25)] = 3
	for k in range(6):
		cr = lathe([(0.016, 0.0), (0.017, 0.05), (0.0, 0.08 + 0.02 * (k % 2))], segs=6, mat=4 if k % 2 else 2, rng=rng, jitter=0.1)
		a = -60 + 24 * k
		cr.rotate("x", 80).rotate("y", a).translate(0.05 * math.sin(math.radians(a)), -0.1, 0.03 + 0.05 * math.cos(math.radians(a)))
		m.add(cr)
	return m, cells


TROPHIES = dict(wolf=t_wolf, coyote=t_coyote, blackbear=t_blackbear, boar=t_boar, goat=t_goat, lynx=t_lynx, otter=t_otter, jackrabbit=t_jackrabbit,
				snowhare=t_snowhare, alligator=t_alligator, bullfrog=t_bullfrog, turtle=t_turtle, iguana=t_iguana, shark=t_shark, stingray=t_stingray,
				raven=t_raven, vulture=t_vulture, raptor=t_raptor, crab=t_crab, dragonfly=t_dragonfly, hornet=t_hornet, dunewyrm=t_dunewyrm,
				greatdunewyrm=t_greatdunewyrm, leech=t_leech, adversary=t_adversary, adversarygeneral=t_adversarygeneral, enforcer=t_enforcer,
				hauler=t_hauler, mangler=t_mangler, rager=t_rager, stalker=t_stalker, traitor=t_traitor, warden=t_warden, welder=t_welder,
				howler=t_howler, spitter=t_spitter, patrol=t_patrol, volt=t_volt, barrage=t_barrage, kamikaze=t_kamikaze, shatter=t_shatter)

for _n, _fn in TROPHIES.items():
	_trophy(_n, _fn)


# ---------------------------------------------------------------- cyberware modules


def cyber_housing(rng, body, rim, glow):
	# an implant module (~0.2 m, 2x life): a rounded hex housing with a contact rim and a glowing port
	hexo = [(0.08 * math.cos(a), 0.065 * math.sin(a)) for a in np.linspace(0, 2 * math.pi, 6, endpoint=False)]
	m = prism(hexo, 0.035, mat=body, side_mat=rim, rng=rng, jitter=0.003, taper=0.9)
	m.add(prism([(x * 0.6, y * 0.6) for x, y in hexo], 0.006, z0=0.035, mat=rim, side_mat=rim))
	for k in range(6):
		a = math.pi / 3 * k
		m.add(box(size=(0.012, 0.006, 0.006), center=(0.085 * math.cos(a), 0.07 * math.sin(a), 0.01), mat=glow).rotate("z", 0))
	return m


def _cyber(name, cells, emblem):
	@item(f"cyber_{name}", "cyber", surface="metal", roughness=0.4)
	def build(rng):
		m = cyber_housing(rng, 0, 1, 2)
		m.add(emblem(rng))
		return ground(m), cells
	return build


HOUSING = [C((58, 62, 72)), C((120, 126, 138))]


def e_acid(rng):
	# a glass vial of green battery acid clamped on top, a drip nozzle
	m = lathe([(0.0, 0.0), (0.02, 0.0), (0.022, 0.08), (0.016, 0.09)], segs=8, mat=3, rng=rng, jitter=0.05).rotate("y", 90).translate(-0.04, 0, 0.065)
	m.add(lathe([(0.024, 0.0), (0.024, 0.012)], segs=8, mat=1).rotate("y", 90).translate(-0.045, 0, 0.065))
	m.add(lathe([(0.024, 0.0), (0.024, 0.012)], segs=8, mat=1).rotate("y", 90).translate(0.03, 0, 0.065))
	m.add(tube(rng, length=0.03, radius=0.006, segs=5, rings=1, profile=lambda t: 1 - 0.5 * t, mat=1).rotate("y", 90).translate(0.045, 0, 0.065))
	for x in (-0.03, 0.02):
		m.add(box(size=(0.01, 0.05, 0.03), center=(x, 0, 0.045), mat=1))
	return m


def e_arc(rng):
	# a copper coil standing on the module with a bright arc ball between two prongs
	m = Mesh()
	for k in range(6):
		m.add(torus(rng, 0.022, 0.005, n=9, segs=4, mat=3).translate(0, 0, 0.045 + 0.011 * k))
	for s in (-1, 1):
		m.add(tube(rng, length=0.05, radius=0.004, segs=4, rings=1, mat=1).rotate("y", s * 25).translate(s * 0.01, 0, 0.11))
	m.add(blob(rng, 0.018, subdiv=1, jitter=0.3, noise=0.2, mat=2).translate(0, 0, 0.16))
	return m


def e_blades(rng):
	# a fan of small curved blades folded in a ring over the module, like a closed shutter
	m = Mesh()
	for k in range(8):
		b = prism([(0, -0.006), (0.06, -0.012), (0.075, 0.0), (0.0, 0.008)], 0.004, mat=3 if k % 2 else 1, side_mat=1, rng=rng, jitter=0.002)
		m.add(b.rotate("y", -12).rotate("z", 45 * k).translate(0, 0, 0.045 + 0.003 * k))
	m.add(lathe([(0.016, 0.0), (0.016, 0.03), (0.0, 0.035)], segs=8, mat=1).translate(0, 0, 0.04))
	return m


def e_fire(rng):
	# a glowing orange core in a caged dome
	m = blob(rng, 0.035, subdiv=1, jitter=0.2, noise=0.15, mat=2).translate(0, 0, 0.075)
	m.add(blob(rng, 0.02, subdiv=1, jitter=0.2, noise=0.1, mat=3).translate(0, 0, 0.08))
	for k in range(5):
		a = 2 * math.pi * k / 5
		path = np.array([(0.042 * math.cos(a) * math.sin(t), 0.042 * math.sin(a) * math.sin(t), 0.04 + 0.045 * (1 - math.cos(t))) for t in np.linspace(0.2, math.pi * 0.98, 6)])
		m.add(path_tube(rng, path, radius=0.0035, segs=4, mat=1))
	return m


def e_ice(rng):
	# a pale blue ice crystal spike rising from a launcher collar
	m = lathe([(0.03, 0.0), (0.03, 0.02), (0.022, 0.025)], segs=8, mat=1, rng=rng, jitter=0.05).translate(0, 0, 0.04)
	for k, (a, h) in enumerate(((0, 0.13), (40, 0.07), (-35, 0.08), (160, 0.06))):
		cr = lathe([(0.016, 0.0), (0.017, h * 0.7), (0.0, h)], segs=6, mat=2 if k % 2 == 0 else 3, rng=rng, jitter=0.1)
		cr.rotate("y", 0 if k == 0 else 28).rotate("z", a).translate(0, 0, 0.06)
		m.add(cr)
	return m


_cyber("acid", HOUSING + [Cell((150, 230, 70), noise=0.04), C((90, 200, 60))], e_acid)
_cyber("arcDischarge", HOUSING + [Cell((140, 220, 255), noise=0.04), C((196, 116, 60))], e_arc)
_cyber("bladeSwarm", HOUSING + [Cell((230, 80, 70), noise=0.04), C((190, 194, 200))], e_blades)
_cyber("fireball", HOUSING + [Cell((255, 140, 40), noise=0.04), Cell((255, 220, 120), noise=0.04)], e_fire)
_cyber("iceShard", HOUSING + [Cell((150, 210, 240), noise=0.04), C((200, 232, 244), 6)], e_ice)
