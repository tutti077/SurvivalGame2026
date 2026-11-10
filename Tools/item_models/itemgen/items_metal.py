"""Metal tier weapons and tools, bows and the metal armour pieces. One builder per shape takes a
kit.Tier (scrap / iron / steel / titanium / arcsteel / plasma / reforged); the tier decides the
cells, and the trim that marks it: scrap gets riveted patch plates and a rope grip, arcsteel teal
glow lines, plasma a glowing cell, reforged brass rivets.

Weapons and tools are in the held frame (handle +X, butt at the origin, head / edge wide along Z,
thin along Y); armour stands on z = 0."""
import math
import numpy as np

from . import palette as P
from .texture import Cell
from .mesh import Mesh, blob, tube, path_tube, box, prism, lathe, leaf, fan, from_quads_grid, weld_displace
from .kit import registry, ground, butt_at_origin, paint, squash, loft, torus, TIERS
from .items_wearables import folded_garment

ITEMS, item = registry()

M_METAL, M_EDGE, M_DARK, M_ACCENT, M_GRIP, M_WOOD, M_PATCH = range(7)


def cells_of(tier, extra=()):
	return list(tier.cells) + list(extra)


def lash(rng, x, radius, width=0.04, mat=M_GRIP, turns=5):
	prof = [(radius * (1.16 + 0.06 * (i % 2)), i * width / turns) for i in range(turns + 1)]
	return lathe(prof, segs=8, mat=mat, rng=rng, jitter=0.1).rotate("y", 90).translate(x, 0, 0)


def rivet(rng, x, y, z, r=0.006, mat=M_ACCENT):
	return blob(rng, r, subdiv=0, jitter=0.1, noise=0.05, scale=(1, 0.6, 1), mat=mat).translate(x, y, z)


def patch_plate(rng, x, z, w, h, side=1, t=0.004, y0=0.0, mat=M_PATCH):
	"""A small bent plate riveted onto a flat face (face normal +-Y), for the scrap tier."""
	out = [(-w / 2, -h / 2), (w / 2, -h / 2 + h * 0.1), (w / 2 - w * 0.1, h / 2), (-w / 2 + w * 0.15, h / 2 - h * 0.05)]
	p = prism(out, t, mat=mat, side_mat=M_DARK, rng=rng, jitter=min(w, h) * 0.08).rotate("x", 90 * side)
	p.translate(x, y0 * side, z)
	for (dx, dz) in ((-w * 0.32, -h * 0.3), (w * 0.3, h * 0.28)):
		p.add(rivet(rng, x + dx, (y0 + t) * side, z + dz, r=0.0045, mat=M_DARK))
	return p


def glow_line(x0, x1, z, y, w=0.005, mat=M_ACCENT, z1=None):
	"""A thin inset bar on a face at +-y (both faces when y > 0 is mirrored by the caller)."""
	m = Mesh()
	for s in (-1, 1):
		zz1 = z if z1 is None else z1
		L = math.hypot(x1 - x0, zz1 - z)
		b = box(size=(L, 0.002, w), center=(0, 0, 0), mat=mat)
		b.rotate("y", -math.degrees(math.atan2(zz1 - z, x1 - x0))).translate((x0 + x1) / 2, s * y, (z + zz1) / 2)
		m.add(b)
	return m


# ---------------------------------------------------------------- blades


def blade(rng, x0, length, w, th, tip=0.18, taper=0.35, jag=0.0, n=12, curve=0.0):
	"""Double-edged blade from x0 along +X: a flattened hexagon section, edges at +-Z (bevel = edge cell),
	tapering by taper over its length and to a point over the last tip fraction."""
	rows = []
	for i, t in enumerate(np.linspace(0, 1, n + 1)):
		ww = w * (1 - taper * t)
		if t > 1 - tip:
			ww *= max(0.0, (1 - t) / tip) ** 0.8
		ww += 0.0008
		tt = th * (1 - 0.5 * t) + 0.0008
		j = (rng.random() - 0.5) * jag * w if 0 < i < n else 0.0
		zc = curve * w * t * t
		rows.append([(x0 + t * length, 0, zc - ww + j), (x0 + t * length, tt / 2, zc - 0.55 * ww), (x0 + t * length, tt / 2, zc + 0.55 * ww),
					 (x0 + t * length, 0, zc + ww - j * 0.7), (x0 + t * length, -tt / 2, zc + 0.55 * ww), (x0 + t * length, -tt / 2, zc - 0.55 * ww)])
	g = np.array(rows)
	m = from_quads_grid(g, closed_v=True, mat=M_METAL)
	m.add(fan(g[0][::-1], mat=M_DARK))
	# bevels: the triangles touching the +-Z edges
	c = m.centroids()
	n_ = m.normals()
	m.mat[np.abs(n_[:, 2]) > 0.35] = M_EDGE
	return m


def sword_hilt(rng, tier, grip_len, guard_w, r=0.016, style="cross"):
	m = tube(rng, length=grip_len, radius=r, segs=7, rings=3, jitter=0.06, profile=lambda t: 1.0 + 0.12 * math.sin(math.pi * t), mat=M_GRIP, axis="x")
	if tier.name == "scrap":
		for k in range(3):
			m.add(lash(rng, 0.02 + k * grip_len / 3.2, r, width=grip_len / 5, mat=M_GRIP, turns=4))
	if style == "cross":
		gd = prism([(-0.012, -guard_w), (0.012, -guard_w * 0.9), (0.016, 0.0), (0.012, guard_w * 0.9), (-0.012, guard_w), (-0.018, 0)], 0.026,
				   z0=-0.013, mat=M_DARK, side_mat=M_DARK, rng=rng, jitter=0.003)
		gd.rotate("x", 90).translate(grip_len + 0.012, 0, 0)
	else:  # swept angular guard (titanium / arcsteel)
		gd = prism([(-0.01, -guard_w), (0.03, -guard_w * 0.7), (0.02, 0.0), (0.03, guard_w * 0.7), (-0.01, guard_w), (-0.02, 0)], 0.03, z0=-0.015,
				   mat=M_DARK, side_mat=M_EDGE, rng=rng, jitter=0.002)
		gd.rotate("x", 90).translate(grip_len + 0.008, 0, 0)
	m.add(gd)
	pom = blob(rng, r * 1.6, subdiv=1, jitter=0.2, noise=0.12, scale=(0.8, 0.9, 1.1), mat=M_DARK if tier.name != "reforged" else M_ACCENT)
	m.add(pom.translate(-r * 0.6, 0, 0))
	return m


def tier_trim_blade(rng, m, tier, x0, length, w, th):
	if tier.name == "scrap":
		for k in range(2):
			x = x0 + length * (0.2 + 0.35 * k)
			m.add(patch_plate(rng, x, (rng.random() - 0.5) * w * 0.4, w * 0.9, w * 0.9, side=1 if k == 0 else -1, y0=th / 2))
	elif tier.glow:
		m.add(glow_line(x0 + 0.02, x0 + length * 0.75, 0.0, th / 2 + 0.0005, w=w * 0.16))
	elif tier.name == "reforged":
		for s in (-1, 1):
			m.add(rivet(rng, x0 + 0.02, s * th / 2, 0.0))
	return m


def make_sword(rng, tier, blade_len=0.72, w=0.026, th=0.009, grip=0.17, guard=0.08, style="cross", jag=0.0):
	m = sword_hilt(rng, tier, grip, guard, style=style)
	x0 = grip + 0.022
	m.add(blade(rng, x0, blade_len, w, th, tip=0.16, taper=0.3, jag=jag))
	tier_trim_blade(rng, m, tier, x0, blade_len, w, th)
	return m


def _sword(name, tier, **kw):
	t = TIERS[tier]

	@item(name, "weapons", surface="metal", roughness=t.rough)
	def build(rng):
		return butt_at_origin(make_sword(rng, t, **kw)), cells_of(t)
	return build


_sword("sword_scrap", "scrap", jag=0.25)
_sword("sword_iron", "iron")
_sword("sword_titanium", "titanium", w=0.024, style="swept", blade_len=0.76)
_sword("sword_arcsteel", "arcsteel", style="swept", w=0.028)


@item("sword_pulse", "weapons", surface="metal", roughness=0.4)
def sword_pulse(rng):
	t = TIERS["arcsteel"]
	cells = cells_of(t, [Cell((150, 250, 255), noise=0.03), Cell((60, 200, 255), noise=0.04)])
	# energised pulse blade: an emitter housing at the guard, a hollow two-rail blade with a bright core between the rails
	m = sword_hilt(rng, t, 0.18, 0.06, style="swept")
	housing = loft(rng, 0.07, lambda s: 0.022 * (1 - 0.3 * s), lambda s: 0.036 * (1 - 0.2 * s), rings=3, segs=8, mat=M_DARK).translate(0.2, 0, 0)
	m.add(housing)
	m.add(torus(rng, 0.03, 0.004, n=10, segs=4, mat=7, axis="x").translate(0.245, 0, 0))
	core = blade(rng, 0.26, 0.66, 0.016, 0.006, tip=0.2, taper=0.2)
	core.mat[:] = 7
	m.add(core)
	for s in (-1, 1):
		rail = path_tube(rng, np.array([(0.26 + 0.66 * u, 0, s * (0.024 * (1 - 0.6 * u) + 0.002)) for u in np.linspace(0, 0.93, 6)]), radius=0.0045, segs=5,
						 jitter=0.0, radius_fn=lambda u: 1.0 - 0.5 * u, mat=M_EDGE)
		m.add(rail)
	m.add(blob(rng, 0.008, subdiv=1, jitter=0.1, noise=0.05, mat=8).translate(0.24, 0.0, 0.0))
	return butt_at_origin(m), cells


def make_greatsword(rng, tier, servo=False, jag=0.0):
	m = sword_hilt(rng, tier, 0.3, 0.13, r=0.018, style="swept" if tier.name in ("arcsteel", "titanium") else "cross")
	x0 = 0.33
	L, w, th = 1.05, 0.04, 0.012
	m.add(blade(rng, x0, L, w, th, tip=0.12, taper=0.25, jag=jag, n=14))
	tier_trim_blade(rng, m, tier, x0, L, w, th)
	if tier.name == "scrap":
		m.add(patch_plate(rng, x0 + 0.62, 0.0, 0.07, 0.06, side=-1, y0=th / 2))
	if servo:
		# servo housing over the ricasso: a boxy motor block both sides with a piston rod and glow ports
		for s in (-1, 1):
			hb = box(size=(0.12, 0.018, 0.06), center=(x0 + 0.07, s * (th / 2 + 0.009), 0.0), mat=M_DARK, jitter=0.08, rng=rng)
			m.add(hb)
			m.add(box(size=(0.03, 0.003, 0.012), center=(x0 + 0.07, s * (th / 2 + 0.019), 0.0), mat=M_ACCENT))
		m.add(tube(rng, length=0.16, radius=0.006, segs=5, rings=1, mat=M_EDGE, axis="x").translate(x0 + 0.13, 0, 0.032))
	return m


def _greatsword(name, tier, **kw):
	t = TIERS[tier]

	@item(name, "weapons", surface="metal", roughness=t.rough)
	def build(rng):
		return butt_at_origin(make_greatsword(rng, t, **kw)), cells_of(t)
	return build


_greatsword("greatsword_scrap", "scrap", jag=0.2)
_greatsword("greatsword_iron", "iron")
_greatsword("greatsword_reforged", "reforged")
_greatsword("greatsword_arcsteel", "arcsteel", servo=True)


@item("dagger_reforged", "weapons", surface="metal", roughness=0.5)
def dagger_reforged(rng):
	t = TIERS["reforged"]
	# stiletto: a needle blade of triangular section, small disc guard, ringed grip
	m = tube(rng, length=0.11, radius=0.012, segs=7, rings=3, jitter=0.06, profile=lambda s: 1.0 + 0.1 * math.sin(s * 18), mat=M_GRIP, axis="x")
	m.add(blob(rng, 0.016, subdiv=1, jitter=0.15, noise=0.1, mat=M_ACCENT).translate(-0.006, 0, 0))
	m.add(lathe([(0.0, 0.0), (0.03, 0.002), (0.032, 0.008), (0.0, 0.012)], segs=9, mat=M_DARK, rng=rng, jitter=0.06).rotate("y", 90).translate(0.11, 0, 0))
	m.add(tube(rng, length=0.22, radius=0.008, segs=3, rings=4, jitter=0.02, wobble=0.0, profile=lambda s: (1 - s) ** 0.8 + 0.05, mat=M_EDGE, axis="x").translate(0.122, 0, 0))
	m.add(rivet(rng, 0.116, 0.012, 0.0))
	return butt_at_origin(m), cells_of(t)


# ---------------------------------------------------------------- axes and pickaxes


def haft(rng, tier, length, r=0.017, mat=None):
	m = tube(rng, length=length, radius=r, segs=7, rings=6, jitter=0.06, wobble=0.08,
			 profile=lambda t: 1.0 + 0.18 * t + 0.06 * math.sin(t * 11), mat=M_WOOD if mat is None else mat, cap_mat=M_DARK, axis="x")
	if tier.name == "scrap":
		m.add(lash(rng, 0.06, r, width=0.1, turns=8))
	elif tier.name in ("arcsteel", "titanium", "plasma"):
		m.add(tube(rng, length=0.2, radius=r * 1.12, segs=7, rings=2, jitter=0.04, mat=M_GRIP, axis="x").translate(0.03, 0, 0))
	else:
		m.add(tube(rng, length=0.16, radius=r * 1.1, segs=7, rings=2, jitter=0.05, mat=M_GRIP, axis="x").translate(0.04, 0, 0))
	return m


def axe_head(rng, tier, x, size=1.0, beard=0.4):
	s = size
	out = [(-0.032 * s, -0.045 * s), (0.032 * s, -0.045 * s), (0.034 * s, 0.05 * s), (0.05 * s, 0.1 * s), (0.075 * s, 0.15 * s), (0.07 * s, 0.175 * s),
		   (0.03 * s, 0.19 * s), (-0.02 * s, 0.19 * s), (-0.07 * s, 0.17 * s), (-0.07 * s - beard * 0.07 * s, 0.13 * s), (-0.05 * s, 0.095 * s), (-0.034 * s, 0.05 * s)]
	if tier.name == "scrap":
		out = [(px + (rng.random() - 0.5) * 0.012, pz + (rng.random() - 0.5) * 0.012) for px, pz in out]
	h = prism(out, 0.036 * s, z0=-0.018 * s, mat=M_METAL, side_mat=M_DARK, rng=rng, jitter=0.003)
	h.rotate("x", 90)

	def thin(p):
		f = np.clip((p[:, 2] - 0.045 * s) / (0.15 * s), 0, 1)
		p[:, 1] *= 1 - 0.85 * f ** 1.4
		return p
	squash(h, thin)
	paint(h, M_EDGE, lambda c, n: c[:, 2] > 0.155 * s)
	# poll: a short block behind the eye (toward -Z)
	h.add(box(size=(0.064 * s, 0.042 * s, 0.03 * s), center=(0, 0, -0.058 * s), mat=M_DARK, jitter=0.1, rng=rng))
	h.translate(x, 0, 0.0)
	if tier.name == "scrap":
		h.add(patch_plate(rng, x - 0.01 * s, 0.075 * s, 0.05 * s, 0.045 * s, side=1, y0=0.012 * s))
	elif tier.glow and tier.name == "arcsteel":
		h.add(glow_line(x - 0.01 * s, x + 0.05 * s, 0.03 * s, 0.011 * s, w=0.006, z1=0.14 * s))
	elif tier.name == "reforged":
		for dz in (-0.01, 0.03):
			for sy in (-1, 1):
				h.add(rivet(rng, x, sy * 0.018 * s, dz * s))
	return h


def _axe(name, tier, size=1.0):
	t = TIERS[tier]

	@item(name, "tools", surface="metal", roughness=t.rough)
	def build(rng):
		m = haft(rng, t, 0.74)
		m.add(axe_head(rng, t, 0.69, size=size))
		return butt_at_origin(m), cells_of(t)
	return build


_axe("axe_scrap", "scrap")
_axe("axe_iron", "iron")
_axe("axe_steel", "arcsteel", size=1.05)
_axe("axe_titanium", "titanium", size=1.05)


def plasma_cell(rng, x, z, r=0.02, length=0.06):
	m = tube(rng, length=length, radius=r * 1.25, segs=8, rings=2, jitter=0.04, mat=M_DARK, axis="x").translate(x - length / 2, 0, z)
	for s in (-1, 1):
		m.add(blob(rng, r * 0.9, subdiv=1, jitter=0.1, noise=0.05, scale=(length / (2 * r), 0.35, 0.8), mat=M_ACCENT).translate(x, s * r * 1.1, z))
	m.add(torus(rng, r * 1.3, 0.003, n=10, segs=4, mat=M_EDGE, axis="x").translate(x - length / 2, 0, z))
	m.add(torus(rng, r * 1.3, 0.003, n=10, segs=4, mat=M_EDGE, axis="x").translate(x + length / 2, 0, z))
	return m


@item("axe_plasma", "tools", surface="metal", roughness=0.5)
def axe_plasma(rng):
	t = TIERS["plasma"]
	# plasma cutter: an industrial axe whose bit is a glowing plasma arc held between two emitter prongs,
	# a charged cell clamped to the haft below the head
	m = haft(rng, t, 0.74, mat=M_DARK)
	x = 0.69
	body = prism([(-0.04, -0.05), (0.04, -0.05), (0.045, 0.06), (0.06, 0.09), (-0.05, 0.09), (-0.035, 0.05)], 0.044, z0=-0.022, mat=M_METAL, side_mat=M_DARK,
				 rng=rng, jitter=0.003).rotate("x", 90).translate(x, 0, 0)
	m.add(body)
	for dx in (-0.045, 0.055):
		m.add(tube(rng, length=0.075, radius=0.008, segs=5, rings=2, jitter=0.05, profile=lambda s: 1 - 0.5 * s, mat=M_EDGE).translate(x + dx, 0, 0.085))
	arc = blob(rng, 0.05, subdiv=1, jitter=0.15, noise=0.12, scale=(1.3, 0.12, 0.55), mat=M_ACCENT).translate(x + 0.005, 0, 0.13)
	m.add(arc)
	m.add(plasma_cell(rng, 0.5, -0.03, r=0.016, length=0.07))
	m.add(box(size=(0.05, 0.012, 0.03), center=(0.5, 0, -0.012), mat=M_DARK))
	return butt_at_origin(m), cells_of(t)


def pick_head(rng, tier, x, span=0.44, r=0.019):
	# a curved double-ended pick: a swept beam through the eye, both ends tapering to points and
	# curving back toward the handle (-X); one end a point, the other a flatter chisel
	path = []
	for t in np.linspace(-1, 1, 11):
		path.append((x - 0.08 * t * t, 0, t * span / 2))
	path = np.array(path)

	def rad(u):
		return 0.25 + 0.75 * math.sin(math.pi * u) ** 0.7
	m = path_tube(rng, path, radius=r, segs=6, jitter=0.08 if tier.name == "scrap" else 0.03, radius_fn=rad, mat=M_METAL)
	c = m.centroids()
	m.mat[np.abs(c[:, 2]) > span * 0.38] = M_EDGE
	eye = box(size=(0.05, 0.04, 0.045), center=(x - 0.002, 0, 0), mat=M_DARK, jitter=0.1, rng=rng)
	m.add(eye)
	if tier.name == "scrap":
		m.add(patch_plate(rng, x - 0.01, 0.05, 0.04, 0.035, side=1, y0=0.012))
		m.add(lash(rng, x - 0.06, 0.018, width=0.04, turns=5))
	elif tier.name == "arcsteel":
		m.add(glow_line(x - 0.01, x - 0.035, 0.02, r * 0.8, w=0.005, z1=span * 0.36))
	elif tier.name == "reforged":
		for sy in (-1, 1):
			m.add(rivet(rng, x, sy * 0.021, 0.0))
	return m


def _pick(name, tier):
	t = TIERS[tier]

	@item(name, "tools", surface="metal", roughness=t.rough)
	def build(rng):
		m = haft(rng, t, 0.82)
		m.add(pick_head(rng, t, 0.8))
		return butt_at_origin(m), cells_of(t)
	return build


_pick("pickaxe_scrap", "scrap")
_pick("pickaxe_iron", "iron")
_pick("pickaxe_steel", "arcsteel")
_pick("pickaxe_titanium", "titanium")
_pick("pickaxe_reforged", "reforged")


@item("pickaxe_plasma", "tools", surface="metal", roughness=0.5)
def pickaxe_plasma(rng):
	t = TIERS["plasma"]
	# plasma cracker: a short pick head whose points are glowing emitter tips, a cell clamped across the eye
	m = haft(rng, t, 0.82, mat=M_DARK)
	h = pick_head(rng, t, 0.8, span=0.34, r=0.019)
	c = h.centroids()
	h.mat[np.abs(c[:, 2]) > 0.13] = M_ACCENT
	m.add(h)
	m.add(plasma_cell(rng, 0.74, 0.0, r=0.018, length=0.06))
	return butt_at_origin(m), cells_of(t)


# ---------------------------------------------------------------- blunt


def flanged_head(rng, x, r=0.04, length=0.12, n=7, mat=M_METAL):
	m = tube(rng, length=length, radius=r * 0.55, segs=8, rings=2, jitter=0.05, mat=M_DARK, axis="x").translate(x, 0, 0)
	for k in range(n):
		a = 360 * k / n
		fl = prism([(0, 0), (length * 0.2, r), (length * 0.8, r * 1.05), (length, r * 0.3), (length, 0)], 0.008, z0=-0.004, mat=mat, side_mat=M_EDGE, rng=rng, jitter=0.002)
		fl.rotate("x", 90).rotate("x", a).translate(x, 0, 0)
		m.add(fl)
	return m


@item("club_scrap", "weapons", surface="wood")
def club_scrap(rng):
	t = TIERS["scrap"]
	# a heavy wooden club wrapped in nailed-on scrap plate
	prof = [(0.022, 0.0), (0.024, 0.1), (0.023, 0.2), (0.032, 0.32), (0.046, 0.45), (0.054, 0.55), (0.05, 0.63), (0.032, 0.67)]
	m = lathe(prof, segs=8, mat=M_WOOD, rng=rng, jitter=0.1, cap_mat=M_DARK).rotate("y", 90)
	for k in range(4):
		a = 90 * k + rng.random() * 40
		p = patch_plate(rng, 0.5 + 0.03 * (k % 2), 0.0, 0.1, 0.05, side=1, y0=0.05)
		m.add(p.rotate("x", a))
	m.add(lash(rng, 0.05, 0.023, width=0.12, turns=9))
	return butt_at_origin(m), cells_of(t)


def _mace(name, tier, spikes=False, coil=False, length=0.62, n=7):
	t = TIERS[tier]

	@item(name, "weapons", surface="metal", roughness=t.rough)
	def build(rng):
		m = tube(rng, length=length, radius=0.016, segs=7, rings=4, jitter=0.05, profile=lambda s: 1.0 + 0.1 * s, mat=M_DARK, axis="x")
		m.add(tube(rng, length=0.16, radius=0.019, segs=7, rings=2, jitter=0.05, mat=M_GRIP, axis="x").translate(0.02, 0, 0))
		m.add(blob(rng, 0.024, subdiv=1, jitter=0.2, noise=0.1, mat=M_DARK).translate(0.0, 0, 0))
		if spikes:
			head = blob(rng, 0.06, subdiv=1, jitter=0.15, noise=0.08, mat=M_METAL).translate(length + 0.04, 0, 0)
			verts = np.array([[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1], [0.6, 0.6, 0.5], [0.6, -0.6, -0.5], [-0.6, 0.6, -0.5], [-0.6, -0.6, 0.5],
							  [0.6, -0.6, 0.5], [-0.6, 0.6, 0.5], [0.6, 0.6, -0.5], [-0.6, -0.6, -0.5]], float)
			for v in verts:
				v = v / np.linalg.norm(v)
				if v[0] < -0.9:
					continue
				sp = tube(rng, length=0.045, radius=0.012, segs=5, rings=1, jitter=0.05, profile=lambda s: 1 - 0.95 * s, mat=M_EDGE)
				from .mesh import basis_from_dir
				sp.transform(basis_from_dir(v)).translate(length + 0.04 + v[0] * 0.052, v[1] * 0.052, v[2] * 0.052)
				head.add(sp)
			m.add(head)
			if t.glow:
				m.add(torus(rng, 0.05, 0.004, n=12, segs=4, mat=M_ACCENT, axis="x").translate(length + 0.04, 0, 0))
		else:
			m.add(flanged_head(rng, length - 0.04, r=0.055, length=0.14, n=n))
			m.add(blob(rng, 0.02, subdiv=1, jitter=0.15, noise=0.1, mat=M_DARK).translate(length + 0.095, 0, 0))
			if coil:
				for k in range(4):
					m.add(torus(rng, 0.03, 0.005, n=10, segs=4, mat=7, axis="x").translate(length - 0.03 + k * 0.03, 0, 0))
				m.add(torus(rng, 0.052, 0.004, n=12, segs=4, mat=M_ACCENT, axis="x").translate(length + 0.03, 0, 0))
			if t.name == "reforged":
				m.add(torus(rng, 0.028, 0.006, n=10, segs=4, mat=M_ACCENT, axis="x").translate(length - 0.045, 0, 0))
		return butt_at_origin(m), cells_of(t, [Cell((196, 116, 60), noise=0.05)])
	return build


_mace("club_iron", "iron")
_mace("mace_reforged", "reforged")
_mace("club_arcsteel", "arcsteel", spikes=True)
_mace("mace_arcsteel", "arcsteel", coil=True)


@item("hammer_arcsteel", "weapons", surface="metal", roughness=0.45)
def hammer_arcsteel(rng):
	t = TIERS["arcsteel"]
	# arc hammer: a two-handed war hammer, long haft, a heavy faceted block head with a spike back
	# and teal discharge lines on both cheeks
	L = 1.05
	m = tube(rng, length=L, radius=0.019, segs=7, rings=6, jitter=0.04, mat=M_DARK, axis="x")
	m.add(tube(rng, length=0.3, radius=0.022, segs=7, rings=3, jitter=0.05, mat=M_GRIP, axis="x").translate(0.03, 0, 0))
	head = loft(rng, 0.2, lambda s: 0.048 * (1 - 0.08 * abs(s - 0.5)), lambda s: 0.055 * (1 - 0.1 * abs(s - 0.5)), rings=5, segs=8, mat=M_METAL).rotate("y", -90)
	head.translate(L - 0.03, 0, -0.04)
	paint(head, M_EDGE, lambda c, n: np.abs(n[:, 2]) > 0.8)
	m.add(head)
	sp = tube(rng, length=0.09, radius=0.022, segs=5, rings=2, jitter=0.05, profile=lambda s: 1 - 0.9 * s, mat=M_EDGE).rotate("x", 180).translate(L - 0.03, 0, -0.04)
	m.add(sp)
	for s in (-1, 1):
		m.add(box(size=(0.008, 0.003, 0.14), center=(L - 0.03, s * 0.052, 0.06), mat=M_ACCENT))
		m.add(box(size=(0.06, 0.003, 0.008), center=(L - 0.03, s * 0.052, 0.06), mat=M_ACCENT))
	return butt_at_origin(m), cells_of(t)


# ---------------------------------------------------------------- spears


def _spear(name, tier, L=1.8, head_len=0.24, head_w=0.035):
	t = TIERS[tier]

	@item(name, "weapons", surface="wood", roughness=t.rough)
	def build(rng):
		m = tube(rng, length=L, radius=0.016, segs=7, rings=9, jitter=0.05, wobble=0.06, profile=lambda s: 1.0 + 0.05 * math.sin(s * 13), mat=M_WOOD, axis="x")
		m.add(tube(rng, length=0.07, radius=0.02, segs=7, rings=2, jitter=0.05, profile=lambda s: 1.0 - 0.15 * s, mat=M_DARK, axis="x").translate(L - 0.04, 0, 0))
		m.add(blade(rng, L + 0.03, head_len, head_w, 0.01, tip=0.45, taper=0.1, n=8))
		m.add(tube(rng, length=0.04, radius=0.018, segs=6, rings=1, mat=M_DARK, axis="x").translate(-0.01, 0, 0))
		if t.name == "reforged":
			m.add(rivet(rng, L - 0.01, 0.02, 0.0))
			m.add(rivet(rng, L - 0.01, -0.02, 0.0))
			# side lugs behind the blade, like a boar spear / pike
			lug = prism([(0, -0.035), (0.03, 0.0), (0, 0.035), (-0.01, 0)], 0.008, z0=-0.004, mat=M_METAL, side_mat=M_EDGE, rng=rng, jitter=0.002)
			m.add(lug.rotate("x", 90).translate(L + 0.02, 0, 0))
		m.add(tube(rng, length=0.22, radius=0.018, segs=7, rings=2, jitter=0.05, mat=M_GRIP, axis="x").translate(0.75, 0, 0))
		return butt_at_origin(m), cells_of(t)
	return build


_spear("spear_iron", "iron", L=1.7)
_spear("spear_reforged", "reforged", L=2.05, head_len=0.32, head_w=0.03)


# ---------------------------------------------------------------- fists


def knuckles(rng, tier, spikes=False, gauntlet=False):
	# a knuckle duster in the held frame: palm bar along Z, four finger rings in a row across it, the
	# whole thing ~0.18 m (1.4x life so it reads on the ground)
	m = Mesh()
	for k in range(4):
		z = -0.054 + k * 0.036
		ring = torus(rng, 0.016, 0.006, n=9, segs=5, mat=M_METAL, axis="y").translate(0.03, 0, z)
		m.add(ring)
		if spikes:
			sp = tube(rng, length=0.03, radius=0.008, segs=5, rings=1, profile=lambda s: 1 - 0.95 * s, mat=M_EDGE, axis="x").translate(0.05, 0, z)
			m.add(sp)
	bar = path_tube(rng, np.array([(0.0, 0, -0.07), (-0.012, 0, -0.035), (-0.016, 0, 0.0), (-0.012, 0, 0.035), (0.0, 0, 0.07)]), radius=0.011, segs=6,
					jitter=0.05, mat=M_DARK)
	m.add(bar)
	for z in (-0.05, 0.05):
		m.add(box(size=(0.02, 0.01, 0.012), center=(0.006, 0, z), mat=M_DARK))
	if tier.name == "scrap":
		m.add(patch_plate(rng, 0.045, -0.02, 0.03, 0.06, side=1, y0=0.006))
		m.add(lash(rng, -0.03, 0.01, width=0.02, turns=3).rotate("y", 90))
	if gauntlet:
		# arc fists: an armoured cuff behind the rings with copper coils and a glowing contact strip
		cuff = loft(rng, 0.16, lambda s: 0.042 * (1 - 0.15 * s), lambda s: 0.06 * (1 - 0.1 * s), rings=4, segs=9, mat=M_METAL).translate(-0.17, 0, 0)
		m.add(cuff)
		for k in range(3):
			m.add(torus(rng, 0.046, 0.005, n=11, segs=4, mat=7, axis="x").translate(-0.13 + 0.035 * k, 0, 0))
		m.add(box(size=(0.008, 0.016, 0.12), center=(0.052, 0, 0.0), mat=M_ACCENT))
	return m


@item("fist_scrap", "weapons", surface="metal", roughness=0.85)
def fist_scrap(rng):
	return butt_at_origin(knuckles(rng, TIERS["scrap"])), cells_of(TIERS["scrap"])


@item("fist_iron", "weapons", surface="metal", roughness=0.6)
def fist_iron(rng):
	return butt_at_origin(knuckles(rng, TIERS["iron"], spikes=True)), cells_of(TIERS["iron"])


@item("fist_arcsteel", "weapons", surface="metal", roughness=0.45)
def fist_arcsteel(rng):
	return butt_at_origin(knuckles(rng, TIERS["arcsteel"], spikes=True, gauntlet=True)), cells_of(TIERS["arcsteel"], [Cell((196, 116, 60), noise=0.05)])


# ---------------------------------------------------------------- bows


def bow(rng, L, belly, limb_r, mat_limb, mat_grip, mat_string, recurve=0.0, tips=None):
	path = []
	for t in np.linspace(0, 1, 19):
		z = belly * math.sin(math.pi * t) ** 0.9
		if recurve:
			e = max(0, abs(t - 0.5) * 2 - 0.75) / 0.25
			z -= recurve * e * e
		path.append((t * L, 0, z))
	m = path_tube(rng, np.array(path), radius=limb_r, segs=6, jitter=0.06, radius_fn=lambda t: 0.45 + 0.8 * math.sin(math.pi * t) ** 0.6, mat=mat_limb)
	a = np.array(path[1]) if recurve else np.array(path[0])
	b = np.array(path[-2]) if recurve else np.array(path[-1])
	s = path_tube(rng, np.array([a + (0.01, 0, 0.002), b - (0.01, 0, -0.002)]), radius=0.0025, segs=4, jitter=0.0, mat=mat_string)
	m.add(s)
	grip = lathe([(0.0, 0), (limb_r * 1.5, 0.01), (limb_r * 1.6, 0.13), (0.0, 0.14)], segs=7, mat=mat_grip, rng=rng, jitter=0.08)
	m.add(grip.rotate("y", 90).translate(L / 2 - 0.07, 0, belly))
	if tips is not None:
		for x in (path[0], path[-1]):
			m.add(blob(rng, limb_r * 0.9, subdiv=1, jitter=0.2, noise=0.1, mat=tips).translate(*x))
	return m


@item("bow_long", "weapons", surface="wood")
def bow_long(rng):
	cells = [C2((150, 104, 60)), P.leather_dark(), C2((226, 216, 186)), P.bone()]
	# english longbow, 1.8 m: a deep D-section yew stave, a short leather grip, horn nocks
	m = bow(rng, 1.8, 0.2, 0.019, 0, 1, 2, tips=3)
	return butt_at_origin(m), cells


@item("bow_quickdraw", "weapons", surface="wood")
def bow_quickdraw(rng):
	cells = [C2((118, 84, 52)), P.leather_red(), C2((226, 216, 186)), C2((64, 50, 38))]
	# a light 1.05 m recurve: limbs flick forward at the tips, wrapped grip, dark tip overlays
	m = bow(rng, 1.05, 0.14, 0.014, 0, 1, 2, recurve=0.06, tips=3)
	return butt_at_origin(m), cells


@item("bow_iron", "weapons", surface="metal", roughness=0.6)
def bow_iron(rng):
	t = TIERS["iron"]
	# iron-limbed bow, 1.25 m: forged leaf-spring limbs riveted to a wooden riser
	m = bow(rng, 1.25, 0.16, 0.014, M_METAL, M_GRIP, 7)
	riser = tube(rng, length=0.3, radius=0.02, segs=7, rings=3, jitter=0.06, profile=lambda s: 0.8 + 0.3 * math.sin(math.pi * s), mat=M_WOOD, axis="x")
	m.add(riser.translate(1.25 / 2 - 0.15, 0, 0.16))
	for x in (0.49, 0.76):
		m.add(rivet(rng, x, 0.02, 0.155, r=0.006, mat=M_EDGE))
		m.add(rivet(rng, x, -0.02, 0.155, r=0.006, mat=M_EDGE))
	return butt_at_origin(m), cells_of(t, [C2((226, 216, 186))])


def C2(rgb, d=10, **kw):
	b = np.array(rgb, float)
	return Cell(tuple(b), noise=kw.pop("noise", 0.06), tones=[tuple(np.clip(b - d, 0, 255)), tuple(b), tuple(np.clip(b + d, 0, 255))], **kw)


# ---------------------------------------------------------------- armour


def _helmet(rng, tier, scrap=False):
	prof = [(0.125, 0.0), (0.13, 0.03), (0.128, 0.07), (0.115, 0.11), (0.09, 0.145), (0.05, 0.168), (0.0, 0.175)]
	m = lathe(prof, segs=12, mat=M_METAL, rng=rng, jitter=0.06 if not scrap else 0.12, closed_bottom=False)
	inner = lathe([(0.118, 0.0), (0.118, 0.06)], segs=12, mat=M_DARK, closed_bottom=False, closed_top=False).flip()
	m.add(inner)
	m.add(torus(rng, 0.13, 0.008, n=12, segs=5, mat=M_DARK).translate(0, 0, 0.004))

	def shape(p):
		# longer front to back, a neck guard flaring out at the back, the brow pulled in over the face
		p[:, 1] *= 1.12
		low = np.clip(1 - p[:, 2] / 0.07, 0, 1)
		back = np.clip(p[:, 1] / 0.14, 0, 1)
		p[:, :2] *= (1 + 0.28 * low * back ** 1.5)[:, None]
		front = np.clip(-p[:, 1] / 0.14, 0, 1)
		p[:, 1] += 0.012 * front * np.clip(1 - abs(p[:, 2] - 0.06) / 0.04, 0, 1)
		return p
	squash(m, shape)
	# crest ridge front-to-back and a nasal guard on the front (-Y)
	ridge = path_tube(rng, np.array([(0, 0.11 * math.cos(a), 0.175 * math.sin(a) ** 0.9 * 0.98 + 0.003) for a in np.linspace(0.25, math.pi - 0.25, 9)]), radius=0.008,
					  segs=5, jitter=0.05, mat=M_EDGE)
	m.add(ridge)
	return m


@item("helmet_scrap", "armor", surface="metal", roughness=0.85)
def helmet_scrap(rng):
	t = TIERS["scrap"]
	# a cap beaten out of scrap: patched dome, rivets round the band, a bent nasal strip
	m = _helmet(rng, t, scrap=True)
	c = m.centroids()
	ang = np.arctan2(c[:, 1], c[:, 0])
	m.mat[(m.mat == M_METAL) & (np.sin(ang * 3 + 0.7) > 0.55) & (c[:, 2] > 0.03) & (c[:, 2] < 0.15)] = M_PATCH
	for k in range(6):
		a = 2 * math.pi * k / 6 + 0.3
		m.add(rivet(rng, 0.12 * math.cos(a), 0.12 * math.sin(a), 0.1, r=0.005, mat=M_DARK))
	for k in range(10):
		a = 2 * math.pi * k / 10
		m.add(rivet(rng, 0.134 * math.cos(a), 0.134 * math.sin(a), 0.02, r=0.005, mat=M_DARK))
	m.add(box(size=(0.03, 0.012, 0.09), center=(0, -0.134, 0.05), mat=M_PATCH, jitter=0.15, rng=rng).rotate("x", -8))
	return ground(m), cells_of(t)


@item("helmet_reforged", "armor", surface="metal", roughness=0.5)
def helmet_reforged(rng):
	t = TIERS["reforged"]
	# a close helm: dome, a hinged visor across the face with eye slits, cheek guards, brass rivets
	m = _helmet(rng, t)
	visor = Mesh()
	rows = []
	for z in np.linspace(0.0, 0.1, 5):
		row = []
		for a in np.linspace(-1.1, 1.1, 7):
			r = 0.137
			row.append((r * math.sin(a), -r * math.cos(a), z))
		rows.append(row)
	g = from_quads_grid(np.array(rows), mat=M_METAL)
	visor.add(g).add(g.copy().translate(0, 0.004, 0).flip())
	for s in (-1, 1):
		visor.add(box(size=(0.05, 0.01, 0.008), center=(s * 0.036, -0.142, 0.06), mat=M_DARK))
	visor.add(box(size=(0.008, 0.012, 0.1), center=(0, -0.143, 0.045), mat=M_EDGE))
	m.add(visor)
	for k in range(8):
		a = math.pi * (0.15 + 0.7 * k / 7) + math.pi
		m.add(rivet(rng, 0.135 * math.cos(a), 0.135 * math.sin(a), 0.1, r=0.006))
	return ground(m), cells_of(t)


def _chest(rng, tier, scrap=False):
	# a breastplate lying face up: torso outline with arm scoops and a neck notch, bent over a barrel curve,
	# a centre ridge and the lower lames as overlapping strips
	out = [(-0.2, -0.22), (0.2, -0.22), (0.21, -0.05), (0.17, 0.08), (0.2, 0.2), (0.1, 0.22), (0.05, 0.16), (-0.05, 0.16), (-0.1, 0.22), (-0.2, 0.2),
		   (-0.17, 0.08), (-0.21, -0.05)]
	m = prism(out, 0.012, mat=M_METAL, side_mat=M_DARK, rng=rng, jitter=0.012 if scrap else 0.004)
	weld_displace(m, rng, 0.003, cells=4.0)
	squash(m, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.09 * (1 - (p[:, 0] / 0.21) ** 2) + 0.03 * (1 - (p[:, 1] / 0.22) ** 2)]))
	ridge = path_tube(rng, np.array([(0, y, 0.09 + 0.03 * (1 - (y / 0.22) ** 2) + 0.012) for y in np.linspace(-0.2, 0.14, 6)]), radius=0.008, segs=5, jitter=0.05,
					  mat=M_EDGE)
	m.add(ridge)
	for k in range(2):
		y = -0.26 - 0.05 * k
		lame = prism([(-0.18, -0.03), (0.18, -0.03), (0.17, 0.03), (-0.17, 0.03)], 0.01, mat=M_METAL, side_mat=M_DARK, rng=rng, jitter=0.004)
		squash(lame, lambda p: p + np.column_stack([np.zeros(len(p)), np.zeros(len(p)), 0.06 * (1 - (p[:, 0] / 0.19) ** 2)]))
		m.add(lame.translate(0, y, 0.004 * k))
	return m


@item("chestplate_scrap", "armor", surface="metal", roughness=0.85)
def chestplate_scrap(rng):
	t = TIERS["scrap"]
	m = _chest(rng, t, scrap=True)
	for (x, y) in ((-0.09, 0.04), (0.08, -0.1), (0.1, 0.1)):
		p = patch_plate(rng, x, y, 0.09, 0.08, side=1, y0=0.0)
		p.rotate("x", -90).translate(0, 0, 0)
		p.translate(0, 0, 0.1 - 0.9 * x * x / 0.21 ** 2 * 0.09)
		m.add(p)
	for s in (-1, 1):
		m.add(box(size=(0.03, 0.12, 0.006), center=(s * 0.12, 0.2, 0.06), mat=M_GRIP))
	return ground(m), cells_of(t)


@item("chestplate_reforged", "armor", surface="metal", roughness=0.5)
def chestplate_reforged(rng):
	t = TIERS["reforged"]
	m = _chest(rng, t)
	for s in (-1, 1):
		m.add(box(size=(0.035, 0.12, 0.006), center=(s * 0.12, 0.2, 0.06), mat=M_GRIP))
		for y in (-0.15, 0.0, 0.12):
			m.add(rivet(rng, s * 0.17, y, 0.03, r=0.007))
	return ground(m), cells_of(t)


def _pants(rng, tier, scrap=False):
	m = folded_garment(rng, 0.32, 0.26, 0.08, mat=M_GRIP, side_mat=M_DARK, flap=0.5, crease=False)
	for s in (-1, 1):
		kp = lathe([(0.05, 0.0), (0.045, 0.01), (0.03, 0.02), (0.0, 0.024)], segs=8, mat=M_METAL, rng=rng, jitter=0.15 if scrap else 0.06)
		squash(kp, lambda p: p * np.array([1.0, 1.25, 1.0]))
		m.add(kp.translate(-0.06 + 0.09 * (s > 0), s * 0.06, 0.08))
	m.add(box(size=(0.32, 0.03, 0.012), center=(0, -0.12, 0.082), mat=M_DARK))
	return m


@item("pants_scrap", "armor", surface="metal", roughness=0.85)
def pants_scrap(rng):
	t = TIERS["scrap"]
	cells = cells_of(t)
	cells[M_GRIP] = C2((112, 98, 76), streak="x", streak_amt=0.1)
	m = _pants(rng, t, scrap=True)
	m.add(patch_plate(rng, 0.1, -0.04, 0.06, 0.06, side=1, y0=0.0).rotate("x", -90).translate(0, 0, 0.082))
	return ground(m), cells


@item("pants_reforged", "armor", surface="metal", roughness=0.5)
def pants_reforged(rng):
	t = TIERS["reforged"]
	cells = cells_of(t)
	cells[M_GRIP] = C2((70, 66, 64), streak="x", streak_amt=0.1)
	m = _pants(rng, t)
	for x in (-0.1, 0.02, 0.12):
		m.add(rivet(rng, x, -0.12, 0.09, r=0.006))
	return ground(m), cells


def _vambrace(rng, L=0.24):
	# a curved forearm plate open underneath, lying on its side, with two straps
	rows = []
	for t in np.linspace(0, 1, 6):
		r = 0.05 * (0.85 + 0.25 * t)
		rows.append([(t * L, r * math.cos(a), r * math.sin(a)) for a in np.linspace(-0.4, math.pi + 0.4, 8)])
	g = np.array(rows)
	m = from_quads_grid(g, mat=M_METAL).flip()
	m.add(from_quads_grid(g * np.array([1, 0.92, 0.92]), mat=M_DARK))
	for x in (0.05, 0.18):
		m.add(box(size=(0.025, 0.11, 0.008), center=(x, 0, -0.012), mat=M_GRIP))
	return m


@item("arms_reforged", "armor", surface="metal", roughness=0.5)
def arms_reforged(rng):
	t = TIERS["reforged"]
	# a pair of vambraces with rounded elbow cops, lying side by side
	m = Mesh()
	for s in (-1, 1):
		v = _vambrace(rng)
		cop = blob(rng, 0.045, subdiv=1, jitter=0.15, noise=0.08, scale=(0.8, 1.0, 1.0), mat=M_EDGE)
		v.add(cop.translate(-0.01, 0, 0.03))
		v.add(rivet(rng, 0.12, 0.0, 0.058, r=0.007))
		m.add(v.rotate("z", s * 6).translate(-0.12, s * 0.065, 0.02))
	return ground(m), cells_of(t)


@item("gloves_reforged", "armor", surface="metal", roughness=0.5)
def gloves_reforged(rng):
	t = TIERS["reforged"]
	# a pair of gauntlets laid flat: leather palm, plated back, segmented finger plates, flared cuff
	m = Mesh()
	for s in (-1, 1):
		g = Mesh()
		palm = prism([(-0.05, -0.05), (0.05, -0.05), (0.055, 0.045), (-0.05, 0.05)], 0.025, mat=M_GRIP, side_mat=M_GRIP, rng=rng, jitter=0.004)
		g.add(palm)
		back = prism([(-0.052, -0.045), (0.052, -0.045), (0.05, 0.04), (-0.05, 0.045)], 0.008, z0=0.025, mat=M_METAL, side_mat=M_DARK, rng=rng, jitter=0.003, taper=0.9)
		g.add(back)
		for k in range(4):
			x = -0.037 + k * 0.025
			for j in range(3):
				seg = box(size=(0.02, 0.022, 0.016), center=(x, 0.062 + j * 0.022, 0.016), mat=M_METAL if j % 2 == 0 else M_EDGE, jitter=0.1, rng=rng)
				g.add(seg)
		thumb = box(size=(0.02, 0.05, 0.016), center=(0.065 * s, 0.0, 0.014), mat=M_METAL, jitter=0.1, rng=rng).rotate("z", -35 * s)
		g.add(thumb)
		cuff = lathe([(0.05, 0.0), (0.062, 0.05), (0.068, 0.07)], segs=9, mat=M_EDGE, rng=rng, jitter=0.06, closed_bottom=False, closed_top=False)
		squash(cuff, lambda p: p * np.array([1.0, 1.0, 0.45]))
		g.add(cuff.rotate("x", 90).translate(0, -0.05, 0.016))
		g.add(rivet(rng, 0, -0.02, 0.035, r=0.006))
		m.add(g.rotate("z", s * 10).translate(s * 0.075, 0, 0))
	return ground(m), cells_of(t)


@item("boots_reforged", "armor", surface="metal", roughness=0.5)
def boots_reforged(rng):
	t = TIERS["reforged"]
	cells = cells_of(t)
	# a pair of standing boots: leather foot and shaft, a steel toe cap, shin plate and brass buckles
	m = Mesh()
	for s in (-1, 1):
		b = Mesh()
		foot = loft(rng, 0.27, lambda u: 0.045 * (0.85 + 0.25 * math.sin(math.pi * u)), lambda u: 0.035 * (1 - 0.3 * u), cz=lambda u: 0.035 * (1 - 0.3 * u),
					rings=6, segs=9, mat=M_GRIP)
		b.add(foot.translate(-0.08, 0, 0))
		b.add(box(size=(0.29, 0.1, 0.018), center=(0.055, 0, 0.004), mat=M_DARK, jitter=0.04, rng=rng))
		shaft = lathe([(0.05, 0.04), (0.052, 0.12), (0.056, 0.22), (0.06, 0.26)], segs=9, mat=M_GRIP, rng=rng, jitter=0.08)
		squash(shaft, lambda p: p * np.array([1.1, 1.0, 1.0]))
		b.add(shaft.translate(-0.04, 0, 0))
		toe = blob(rng, 0.045, subdiv=1, jitter=0.15, noise=0.08, scale=(1.1, 1.05, 0.7), mat=M_METAL).translate(0.14, 0, 0.035)
		b.add(toe)
		shin = Mesh()
		rows = [[(0.06 * math.cos(a) + 0.0, 0.06 * math.sin(a) * 0.9, z) for a in np.linspace(-0.9, 0.9, 5)] for z in np.linspace(0.08, 0.25, 4)]
		g = np.array(rows)
		shin.add(from_quads_grid(g, mat=M_METAL).flip()).add(from_quads_grid(g * np.array([0.96, 0.96, 1]), mat=M_METAL))
		b.add(shin.translate(-0.035, 0, 0))
		for z in (0.12, 0.2):
			b.add(torus(rng, 0.058, 0.005, n=10, segs=4, mat=M_DARK).translate(-0.04, 0, z))
			b.add(rivet(rng, 0.025, 0.0, z, r=0.007))
		m.add(b.translate(0, s * 0.07, 0))
	return ground(m), cells


@item("goggles_sand", "armor", surface="default", roughness=0.4)
def goggles_sand(rng):
	cells = [C2((110, 78, 50)), C2((150, 110, 58)), C2((214, 150, 60), 6), P.metal(), C2((76, 56, 40))]
	# desert goggles lying flat: two brass-rimmed lens cups on a leather bridge, amber lenses, a strap loop
	m = Mesh()
	for s in (-1, 1):
		cup = lathe([(0.032, 0.0), (0.034, 0.012), (0.03, 0.024)], segs=10, mat=0, rng=rng, jitter=0.06)
		cup.add(torus(rng, 0.031, 0.004, n=10, segs=4, mat=3).translate(0, 0, 0.024))
		cup.add(lathe([(0.028, 0.022), (0.014, 0.028), (0.0, 0.03)], segs=10, mat=2, rng=rng, jitter=0.04))
		m.add(cup.translate(s * 0.042, 0, 0))
	m.add(box(size=(0.03, 0.012, 0.01), center=(0, 0, 0.012), mat=4))
	strap = np.array([(0.075 + 0.07 * math.sin(t), 0.08 * (1 - math.cos(t)) * 0.9, 0.006) for t in np.linspace(0, math.pi, 8)] +
					 [(-0.075 - 0.07 * math.sin(t), 0.08 * (1 - math.cos(math.pi - t)) * 0.9, 0.006) for t in np.linspace(0, math.pi, 8)])
	m.add(path_tube(rng, strap, radius=0.006, segs=4, jitter=0.05, cap=False, mat=4))
	return ground(m), cells


@item("basic_wingsuit", "armor", surface="default", roughness=0.8)
def basic_wingsuit(rng):
	cells = [C2((206, 104, 40)), C2((70, 72, 78)), C2((40, 40, 44)), P.metal(), C2((230, 220, 196))]
	# the wingsuit folded and strapped: an orange fabric bundle with the grey wing membrane folds showing
	# along one edge, a harness strap with buckles across it
	m = folded_garment(rng, 0.42, 0.3, 0.1, mat=0, side_mat=1, flap_mat=1, flap=0.45)
	for k in range(3):
		m.add(box(size=(0.42, 0.012, 0.01), center=(0, 0.12 - 0.005 * k, 0.012 + 0.025 * k), mat=1, jitter=0.05, rng=rng))
	for x in (-0.1, 0.1):
		m.add(box(size=(0.035, 0.32, 0.008), center=(x, 0, 0.102), mat=2))
		m.add(box(size=(0.045, 0.03, 0.01), center=(x, -0.05, 0.108), mat=3))
	m.add(box(size=(0.08, 0.04, 0.006), center=(0.12, 0.06, 0.06), mat=4))
	return ground(m), cells
