"""Generates the hub (Mark 2026-11-10): textures, materials, the barrel model, prefabs/hub/hub.prefab and the buildable
prefabs/build/hub_portal.prefab.

    python Tools/hub/generate_hub.py

Everything is authored here in METRES (hub-local, origin = plaza centre on the floor, +Y = north toward the coliseum)
and converted once, on write, at 40 units per metre (CLAUDE.md commandment 5). The hub is dev boxes
(models/dev/box.vmdl, 50 units, centred) scaled to size, each with a static BoxCollider, drawn with
shaders/hub_surface.shader (world-projected, point-sampled tile + emissive glow), plus build-kit wall models for the
coliseum's forts so they read as player-buildable.

Layout (top-down, hub-local metres, the portal fountain at the origin, +Y north; see HALL / COL_C / CUBE below):
  hall             x -620..380, y -420..580, roof at 140 m: one enclosed tron room (Mark: 1000 x 1000, everything
                   inside it is a building you can walk all the way round)
  coliseum         150 x 100 m floor centred 270 m north; six tiers of stands, a 28 m arcade facade, one fort per side
                   (6-wall tower + battlements), barrel piles; eight fixed feed angles for the bar's big screen
  lobby            against the coliseum's south side: hall (fish-tank window), bar wing west (big screen, bookie,
                   commend button), games wing east (Stratagem Hero)
  trial cube       x -600..-100, y -400..100 (500 x 500 m, 100 m walls): a straight line of grapple bars between two
                   platforms, rings 10-15; the queue building sits on its east face, facing the fountain
Re-running overwrites the outputs; component and object guids are derived from names, so they are stable.
"""
import json
import math
import os
import uuid

import numpy as np
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ASSETS = os.path.join(ROOT, "Assets")
U = 40.0  # units per metre (TerrainWorldUnits.MetersToEngine / BuildModuleDimensions.UnitsPerMeter)
BOX = "models/dev/box.vmdl"  # 50 units, centred
KIT_WALL = "models/building/build_wood_wall.vmdl"  # 0.25 x 2 x 2 m (X thick, Y wide, Z tall), centred
NS = uuid.UUID("6a3f0f7e-8d0b-4b8e-9c55-2f0d6f1b7a11")


def gid(name):
	return str(uuid.uuid5(NS, name))


def f3(v):
	return ",".join(f"{x:.4g}" for x in v)


def quat(pitch=0.0, yaw=0.0, roll=0.0):
	"""s&box Rotation.From(pitch, yaw, roll): yaw about Z, pitch about Y (positive = nose down), roll about X."""
	p, y, r = (math.radians(a) / 2 for a in (pitch, yaw, roll))
	qy = (0.0, 0.0, math.sin(y), math.cos(y))
	qp = (0.0, math.sin(p), 0.0, math.cos(p))
	qr = (math.sin(r), 0.0, 0.0, math.cos(r))

	def mul(a, b):
		ax, ay, az, aw = a
		bx, by, bz, bw = b
		return (aw * bx + ax * bw + ay * bz - az * by,
				aw * by - ax * bz + ay * bw + az * bx,
				aw * bz + ax * by - ay * bx + az * bw,
				aw * bw - ax * bx - ay * by - az * bz)

	q = mul(mul(qy, qp), qr)
	return ",".join(f"{c:.6f}" for c in q)


# ---------------------------------------------------------------------------------------------------------------------
# Textures (256 px, point filtered by the shader). Low contrast panels; the lines live in the glow map.
# ---------------------------------------------------------------------------------------------------------------------

MAT_DIR = os.path.join(ASSETS, "materials", "hub")
RNG = np.random.default_rng(20261110)


def save(img, name):
	os.makedirs(MAT_DIR, exist_ok=True)
	Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB").save(os.path.join(MAT_DIR, name))


def solid(rgb):
	return np.tile(np.array(rgb, np.float32), (256, 256, 1))


def blocks(base, tones, rows, cols, stagger, seam, seam_rgb, jitter=6):
	"""Stone / plank blocks: rows x cols cells, alternate rows offset, one of `tones` per cell, a seam line."""
	img = np.zeros((256, 256, 3), np.float32)
	h = 256 // rows
	w = 256 // cols
	for r in range(rows):
		off = (w // 2) if (stagger and r % 2) else 0
		for c in range(-1, cols + 1):
			x0 = c * w + off
			tone = np.array(tones[RNG.integers(len(tones))], np.float32) + RNG.integers(-jitter, jitter + 1)
			xs = slice(max(0, x0), max(0, min(256, x0 + w)))
			img[r * h:(r + 1) * h, xs] = tone
			if 0 <= x0 < 256:
				img[r * h:(r + 1) * h, x0:x0 + seam] = seam_rgb
		img[r * h:r * h + seam, :] = seam_rgb
	return img


def make_textures():
	# Neon panel: near-black panels, a faint 2 m seam; glow = cyan line on the 4 m tile edge, dim line at 2 m.
	col = solid((14, 16, 24))
	col[:, 127:129] = (20, 23, 33)
	col[127:129, :] = (20, 23, 33)
	glow = np.zeros((256, 256, 3), np.float32)
	cyan = np.array((40, 230, 255), np.float32)
	glow[:, 0:2] = cyan
	glow[0:2, :] = cyan
	glow[:, 127:129] = cyan * 0.22
	glow[127:129, :] = cyan * 0.22
	col = np.maximum(col, glow * 0.35)
	save(col, "hub_neon.png")
	save(glow, "hub_neon_glow.png")

	save(solid((10, 60, 70)), "hub_trim_cyan.png")
	save(solid((40, 230, 255)), "hub_trim_cyan_glow.png")
	save(solid((70, 10, 60)), "hub_trim_magenta.png")
	save(solid((50, 18, 100)), "hub_trim_purple.png")
	save(solid((140, 60, 255)), "hub_trim_purple_glow.png")
	save(solid((255, 40, 200)), "hub_trim_magenta_glow.png")

	# Portal: concentric purple bands (no hue shift beyond purple), glowing.
	yy, xx = np.mgrid[0:256, 0:256]
	rad = np.hypot(xx - 128, yy - 128) / 128.0
	ang = np.arctan2(yy - 128, xx - 128)
	band = (np.floor(rad * 8 + ang / math.pi * 1.5) % 3)
	tones = np.array([(150, 60, 255), (110, 35, 210), (190, 110, 255)], np.float32)
	portal = tones[band.astype(int)]
	save(portal * 0.5, "hub_portal.png")
	save(portal, "hub_portal_glow.png")

	# Coliseum: pale travertine blocks (2 m tile: 4 courses of 0.5 m, 1 m blocks), dark gunmetal panels, warm light.
	save(blocks(None, [(206, 198, 180), (196, 188, 170), (214, 207, 190), (188, 181, 165)], 4, 2, True, 3, (150, 143, 128)),
		 "hub_col_stone.png")
	metal = blocks(None, [(58, 63, 72), (54, 59, 68), (62, 67, 76)], 2, 2, False, 3, (36, 39, 45), jitter=2)
	for cy in (16, 112, 144, 240):
		for cx in (16, 112, 144, 240):
			metal[cy - 2:cy + 2, cx - 2:cx + 2] = (92, 98, 108)
	save(metal, "hub_col_metal.png")
	save(solid((120, 100, 70)), "hub_col_light.png")
	save(solid((255, 214, 150)), "hub_col_light_glow.png")
	sand = solid((196, 178, 138))
	for _ in range(900):
		x, y = RNG.integers(0, 254, 2)
		sand[y:y + 2, x:x + 2] = (186 + RNG.integers(-6, 6), 168 + RNG.integers(-6, 6), 128 + RNG.integers(-6, 6))
	save(sand, "hub_col_sand.png")

	# Trial cube: neutral grey panels, white grid every 4 m tile edge (dim glow so distances read), 1 m sub-grid.
	grid = solid((88, 92, 100))
	for k in range(0, 256, 64):
		grid[:, k:k + 1] = (100, 104, 112)
		grid[k:k + 1, :] = (100, 104, 112)
	grid[:, 0:2] = (210, 214, 220)
	grid[0:2, :] = (210, 214, 220)
	gglow = np.zeros((256, 256, 3), np.float32)
	gglow[:, 0:2] = (120, 125, 135)
	gglow[0:2, :] = (120, 125, 135)
	save(grid, "hub_trial_grid.png")
	save(gglow, "hub_trial_grid_glow.png")
	save(solid((120, 100, 10)), "hub_trial_bar.png")
	save(solid((255, 215, 40)), "hub_trial_bar_glow.png")

	# Wood (bar counter, benches): planks 2 m tile.
	save(blocks(None, [(122, 84, 52), (112, 76, 46), (130, 92, 58)], 8, 1, False, 2, (78, 52, 32)), "hub_wood.png")
	save(solid((0, 0, 0)), "hub_black.png")


def vmat(name, color, glow="hub_black.png", tile=4.0, glow_scale=0.0, rough=0.8, metal=0.0):
	text = f"""// Hub surface (Tools/hub/generate_hub.py)
Layer0
{{
	shader "shaders/hub_surface.shader"

	HubTileMeters "{tile:.3f}"
	HubGlow "{glow_scale:.3f}"
	HubRoughness "{rough:.3f}"
	HubMetalness "{metal:.3f}"
	TextureColor "materials/hub/{color}"
	TextureGlow "materials/hub/{glow}"
}}
"""
	with open(os.path.join(MAT_DIR, f"{name}.vmat"), "w", newline="\n") as f:
		f.write(text)
	return f"materials/hub/{name}.vmat"


def make_materials():
	m = {}
	m["neon"] = vmat("hub_neon", "hub_neon.png", "hub_neon_glow.png", 4.0, 3.0, 0.35, 0.2)
	m["hall"] = vmat("hub_hall", "hub_neon.png", "hub_neon_glow.png", 10.0, 3.0, 0.35, 0.2)
	m["cyan"] = vmat("hub_trim_cyan", "hub_trim_cyan.png", "hub_trim_cyan_glow.png", 4.0, 3.0, 0.5)
	m["magenta"] = vmat("hub_trim_magenta", "hub_trim_magenta.png", "hub_trim_magenta_glow.png", 4.0, 3.0, 0.5)
	m["purple"] = vmat("hub_trim_purple", "hub_trim_purple.png", "hub_trim_purple_glow.png", 4.0, 3.0, 0.5)
	m["portal"] = vmat("hub_portal", "hub_portal.png", "hub_portal_glow.png", 4.0, 2.2, 0.3)
	m["stone"] = vmat("hub_col_stone", "hub_col_stone.png", tile=2.0, rough=0.9)
	m["metal"] = vmat("hub_col_metal", "hub_col_metal.png", tile=2.0, rough=0.45, metal=0.5)
	m["light"] = vmat("hub_col_light", "hub_col_light.png", "hub_col_light_glow.png", 2.0, 2.5)
	m["sand"] = vmat("hub_col_sand", "hub_col_sand.png", tile=4.0, rough=1.0)
	m["grid"] = vmat("hub_trial_grid", "hub_trial_grid.png", "hub_trial_grid_glow.png", 4.0, 0.6, 0.9)
	m["bar"] = vmat("hub_trial_bar", "hub_trial_bar.png", "hub_trial_bar_glow.png", 4.0, 2.0, 0.4)
	m["wood"] = vmat("hub_wood", "hub_wood.png", tile=2.0, rough=0.9)
	with open(os.path.join(MAT_DIR, "hub_glass.vmat"), "w", newline="\n") as f:
		f.write("""// Hub fish-tank window (Tools/hub/generate_hub.py)
Layer0
{
	shader "shaders/hub_glass.shader"

	HubGlassTint "[0.700000 0.900000 1.000000 0.000000]"
	HubGlassOpacity "0.080"
	HubGlassEdgeOpacity "0.450"
}
""")
	m["glass"] = "materials/hub/hub_glass.vmat"
	# The arena cameraman's referee shirt (PlayerAnimation.EventOutfit "referee" lays it over the polo shirt).
	stripes = np.zeros((256, 256, 3), np.float32)
	for k in range(0, 256, 32):
		stripes[:, k:k + 16] = (236, 236, 232)
		stripes[:, k + 16:k + 32] = (22, 22, 24)
	Image.fromarray(stripes.astype(np.uint8), "RGB").save(os.path.join(MAT_DIR, "hub_referee_stripes.png"))
	with open(os.path.join(MAT_DIR, "hub_referee_stripes.vmat"), "w", newline=chr(10)) as f:
		f.write("""// Referee shirt stripes for the hub arena cameraman (Tools/hub/generate_hub.py)
Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.900"
	PixelSpecular "0.000"
	TextureColor "materials/hub/hub_referee_stripes.png"
}
""")
	with open(os.path.join(MAT_DIR, "hub_screen.vmat"), "w", newline="\n") as f:
		f.write("""// Hub big screen: the coliseum camera feed (Code/Hub/HubArenaScreen.cs)
Layer0
{
	shader "shaders/hub_screen.shader"

	HubScreenBrightness "0.800"
}
""")
	m["screen"] = "materials/hub/hub_screen.vmat"
	return m


# ---------------------------------------------------------------------------------------------------------------------
# Barrel model: 10-sided, bellied, faceted; OBJ (Y-up like Blender's exporter) + vmdl + pixel_lit vmat + texture.
# ---------------------------------------------------------------------------------------------------------------------

def make_barrel():
	out = os.path.join(ASSETS, "models", "hub", "barrel")
	os.makedirs(out, exist_ok=True)
	sides = 10
	rings = [(0.0, 0.27), (0.12, 0.31), (0.45, 0.34), (0.78, 0.31), (0.9, 0.27)]  # (height m, radius m)
	verts, uvs, faces = [], [], []

	def v(p, uv):
		verts.append(p)
		uvs.append(uv)
		return len(verts)

	for i in range(len(rings) - 1):
		(z0, r0), (z1, r1) = rings[i], rings[i + 1]
		for s in range(sides):
			a0 = 2 * math.pi * s / sides
			a1 = 2 * math.pi * (s + 1) / sides
			u0, u1 = s / sides, (s + 1) / sides
			p = [(r0 * math.cos(a0), r0 * math.sin(a0), z0), (r0 * math.cos(a1), r0 * math.sin(a1), z0),
				 (r1 * math.cos(a1), r1 * math.sin(a1), z1), (r1 * math.cos(a0), r1 * math.sin(a0), z1)]
			t = [(u0, z0 / 0.9 * 0.75), (u1, z0 / 0.9 * 0.75), (u1, z1 / 0.9 * 0.75), (u0, z1 / 0.9 * 0.75)]
			ids = [v(p[k], t[k]) for k in range(4)]
			faces.append((ids[0], ids[1], ids[2]))
			faces.append((ids[0], ids[2], ids[3]))
	for z, r, flip in ((0.0, 0.27, True), (0.9, 0.27, False)):
		c = v((0, 0, z), (0.5, 0.875))
		for s in range(sides):
			a0 = 2 * math.pi * s / sides
			a1 = 2 * math.pi * (s + 1) / sides
			i0 = v((r * math.cos(a0), r * math.sin(a0), z), (0.5 + 0.12 * math.cos(a0), 0.875 + 0.12 * math.sin(a0)))
			i1 = v((r * math.cos(a1), r * math.sin(a1), z), (0.5 + 0.12 * math.cos(a1), 0.875 + 0.12 * math.sin(a1)))
			faces.append((c, i1, i0) if flip else (c, i0, i1))

	lines = ["# hub barrel - Tools/hub/generate_hub.py (metres, written Y-up)", "o barrel", "usemtl barrel", "s off"]
	lines += [f"v {x:.5f} {z:.5f} {-y:.5f}" for x, y, z in verts]
	lines += [f"vt {u:.5f} {t:.5f}" for u, t in uvs]
	lines += [f"f {a}/{a} {b}/{b} {c}/{c}" for a, b, c in faces]
	with open(os.path.join(out, "barrel.obj"), "w", newline="\n") as f:
		f.write("\n".join(lines) + "\n")

	# Texture: bottom 3/4 = staves (vertical planks) with two iron hoops; top quarter = lid planks.
	img = np.zeros((256, 256, 3), np.float32)
	staves = [(132, 88, 50), (122, 80, 46), (140, 96, 56)]
	for s in range(10):
		img[64:, s * 25:(s + 1) * 26] = np.array(staves[s % 3], np.float32)
		img[64:, s * 25:s * 25 + 2] = (84, 56, 32)
	for band in (64 + 20, 64 + 150, 64 + 172):
		img[band:band + 9, :] = (66, 66, 70)
		img[band:band + 2, :] = (96, 96, 100)
	img[:64, :] = (120, 82, 48)
	for k in range(0, 256, 22):
		img[:64, k:k + 2] = (84, 56, 32)
	# The OBJ v runs bottom-up, the image top-down: flip so v 0 is the image's last row.
	Image.fromarray(np.clip(img[::-1], 0, 255).astype(np.uint8), "RGB").save(os.path.join(out, "barrel.png"))
	with open(os.path.join(out, "barrel.vmat"), "w", newline="\n") as f:
		f.write("""Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.900"
	PixelSpecular "0.000"
	PixelFlatShading "1.000"
	TextureColor "models/hub/barrel/barrel.png"
}
""")
	vmdl = open(os.path.join(ROOT, "Tools", "item_models", "itemgen", "export.py"), encoding="utf-8").read()
	start = vmdl.index('VMDL = """') + len('VMDL = """')
	template = vmdl[start:vmdl.index('"""', start)]
	with open(os.path.join(out, "barrel.vmdl"), "w", newline="\n") as f:
		f.write(template.format(name="barrel", vmat="models/hub/barrel/barrel.vmat", obj="models/hub/barrel/barrel.obj",
								scale=U, surface="wood"))
	return "models/hub/barrel/barrel.vmdl"


# ---------------------------------------------------------------------------------------------------------------------
# Prefab building
# ---------------------------------------------------------------------------------------------------------------------

class Node:
	def __init__(self, name, pos=(0, 0, 0), rot=None, scale=(1, 1, 1), tags="", parent=None):
		self.name = name
		self.path = (parent.path + "/" if parent else "") + name
		self.guid = gid("obj:" + self.path)
		self.pos = pos  # metres, parent-local
		self.rot = rot or "0,0,0,1"
		self.scale = scale
		self.tags = tags
		self.components = []
		self.children = []
		if parent:
			parent.children.append(self)

	def comp(self, type_name, **props):
		c = {"__type": type_name, "__guid": gid(f"comp:{self.path}:{type_name}:{len(self.components)}"), "__enabled": True,
			 "Flags": 0}
		c.update(props)
		self.components.append(c)
		return c

	def ref(self):
		return {"_type": "gameobject", "go": self.guid}

	def json(self):
		return {
			"__guid": self.guid, "__version": 2, "Flags": 0, "Name": self.name,
			"Position": f3([p * U for p in self.pos]), "Rotation": self.rot, "Scale": f3(self.scale),
			"Tags": self.tags, "Enabled": True, "NetworkMode": 1, "NetworkFlags": 0, "NetworkOrphaned": 0,
			"NetworkTransmit": True, "OwnerTransfer": 1,
			"Components": self.components,
			"Children": [c.json() for c in self.children],
		}


def comp_ref(node, comp):
	return {"_type": "component", "component_id": comp["__guid"], "go": node.guid,
			"component_type": comp["__type"].split(".")[-1]}


def renderer(node, model, material=None, tint="1,1,1,1"):
	return node.comp("Sandbox.ModelRenderer", BodyGroups=18446744073709551615, CreateAttachments=False, LodOverride=None,
					 MaterialGroup=None, MaterialOverride=material, Materials=None, Model=model,
					 RenderOptions={"GameLayer": True, "OverlayLayer": False, "BloomLayer": False, "AfterUILayer": False},
					 RenderType="On", Tint=tint)


def box(parent, name, center, size, mat, collide=True, rot=None, tags=""):
	"""A box of `size` metres centred at `center` (parent-local metres)."""
	n = Node(name, center, rot, (size[0] * U / 50.0, size[1] * U / 50.0, size[2] * U / 50.0), tags, parent)
	renderer(n, BOX, mat)
	if collide:
		n.comp("Sandbox.BoxCollider", Center="0,0,0", IsTrigger=False, Scale="50,50,50", Static=True, Surface=None)
	return n


def span(parent, name, x0, x1, y0, y1, z0, z1, mat, collide=True, tags=""):
	return box(parent, name, ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), (x1 - x0, y1 - y0, z1 - z0), mat, collide, tags=tags)


def kit_wall(parent, name, center, yaw, mat):
	"""One build-kit wall piece (2 m wide, 2 m tall, 0.25 thick), its face normal along local X after `yaw`. The kit
	model is untextured placeholder geometry, so the hub dresses it in its own material."""
	n = Node(name, center, quat(yaw=yaw), (1, 1, 1), "", parent)
	renderer(n, KIT_WALL, mat)
	n.comp("Sandbox.BoxCollider", Center="0,0,0", IsTrigger=False, Scale="10,80,80", Static=True, Surface=None)
	return n


def point_light(parent, name, pos, color, radius_m, brightness=1.0):
	n = Node(name, pos, None, (1, 1, 1), "", parent)
	n.comp("Sandbox.PointLight", LightColor=color, Radius=radius_m * U, Attenuation=1.0, Shadows=False,
		   FogMode="Disabled", FogStrength=0.0)
	return n


def ring(parent, name, center, radius, seg_len_scale, thickness, mat, plane="xz", count=16, collide=False, yaw=0.0):
	"""A ring of `count` box segments, centre `center`, in the given plane ('xz' faces Y, 'yz' faces X)."""
	g = Node(name, center, quat(yaw=yaw), (1, 1, 1), "", parent)
	seg = 2 * math.pi * radius / count * seg_len_scale
	for i in range(count):
		th = 2 * math.pi * (i + 0.5) / count
		if plane == "xz":
			pos = (radius * math.cos(th), 0.0, radius * math.sin(th))
			rot = quat(pitch=-(math.degrees(th) + 90.0))
			size = (seg, thickness, thickness)
		else:  # yz: segment along the tangent in the YZ plane = local X rotated onto Y then rolled
			pos = (0.0, radius * math.cos(th), radius * math.sin(th))
			rot = quat(roll=math.degrees(th) + 90.0)
			size = (thickness, seg, thickness)
		box(g, f"Seg{i:02d}", pos, size, mat, collide, rot=rot)
	return g


# ---------------------------------------------------------------------------------------------------------------------

def look_at(frm, to):
	"""quat() angles for a camera at `frm` looking at `to` (metres)."""
	dx, dy, dz = to[0] - frm[0], to[1] - frm[1], to[2] - frm[2]
	yaw = math.degrees(math.atan2(dy, dx))
	pitch = math.degrees(math.atan2(-dz, math.hypot(dx, dy)))
	return quat(pitch=pitch, yaw=yaw)


# Layout (hub-local metres; the portal fountain is the origin, +Y north). Mark 2026-11-10: a 1000 x 1000 m enclosed tron
# hall with the coliseum and the trial cube standing inside it as buildings you can walk all the way round.
HALL = (-620.0, 380.0, -420.0, 580.0)   # x0, x1, y0, y1
HALL_H = 140.0
COL_C = (0.0, 270.0)                    # coliseum floor centre
COL_HX, COL_HY = 75.0, 50.0             # 150 x 100 m floor
CUBE = (-600.0, -100.0, -400.0, 100.0)  # 500 x 500 m trial cube
CUBE_H = 100.0
FLOOR_LIFT = 0.05                       # building floors sit just above the hall floor (no z-fighting)


def build_hub(m, barrel):
	root = Node("hub")
	root.comp("Survival.HubWorld")  # ArrivalPoint set below

	# ---- the hall: floor, four walls, roof, all tron ------------------------------------------------------------
	hx0, hx1, hy0, hy1 = HALL
	hall = Node("Hall", parent=root)
	span(hall, "Floor", hx0, hx1, hy0, hy1, -1, 0, m["hall"])
	span(hall, "Roof", hx0 - 1, hx1 + 1, hy0 - 1, hy1 + 1, HALL_H, HALL_H + 1, m["hall"])
	span(hall, "WallWest", hx0 - 1, hx0, hy0 - 1, hy1 + 1, 0, HALL_H, m["hall"])
	span(hall, "WallEast", hx1, hx1 + 1, hy0 - 1, hy1 + 1, 0, HALL_H, m["hall"])
	span(hall, "WallSouth", hx0, hx1, hy0 - 1, hy0, 0, HALL_H, m["hall"])
	span(hall, "WallNorth", hx0, hx1, hy1, hy1 + 1, 0, HALL_H, m["hall"])
	for i, (x0, x1, y0, y1) in enumerate([(hx0, hx1, hy0, hy0 + 0.1), (hx0, hx1, hy1 - 0.1, hy1),
										  (hx0, hx0 + 0.1, hy0, hy1), (hx1 - 0.1, hx1, hy0, hy1)]):
		span(hall, f"SkirtTrim{i}", x0, x1, y0, y1, 0, 0.4, m["magenta"], collide=False)
	# Neon strips from the fountain to the lobby door and to the trial building door.
	span(hall, "PathNorth", -0.3, 0.3, 6, COL_C[1] - COL_HY - 22.5, 0, 0.03, m["cyan"], collide=False)
	span(hall, "PathWest", CUBE[1] + 20.5, -6, -0.3, 0.3, 0, 0.03, m["cyan"], collide=False)
	# No hall lights: the hall is lit by its own neon (lamps washed the black panels grey); buildings carry their own.

	# Fountain: octagonal basin, pedestal, the purple portal standing in it (faces south / north).
	fountain = Node("PortalFountain", parent=hall)
	for i in range(8):
		th = 2 * math.pi * i / 8
		box(fountain, f"Basin{i}", (5 * math.cos(th), 5 * math.sin(th), 0.3), (4.2, 0.6, 0.6), m["neon"],
			rot=quat(yaw=math.degrees(th) + 90))
		box(fountain, f"BasinTrim{i}", (5 * math.cos(th), 5 * math.sin(th), 0.62), (4.2, 0.62, 0.04), m["magenta"],
			collide=False, rot=quat(yaw=math.degrees(th) + 90))
	# Pool: a 16-gon of eight overlapping strips, inside the basin walls.
	for i in range(8):
		box(fountain, f"Pool{i}", (0, 0, 0.04), (8.9, 8.9 * math.tan(math.radians(11.25)) * 1.02, 0.08), m["portal"],
			collide=False, rot=quat(yaw=i * 22.5))
	box(fountain, "Pedestal", (0, 0, 0.3), (3, 1.4, 0.6), m["neon"])
	ring(fountain, "PortalRing", (0, 0, 2.8), 2.2, 1.08, 0.35, m["purple"], plane="xz", count=20, collide=True)
	disc = Node("PortalDisc", (0, 0, 2.8), parent=fountain)
	for i in range(8):
		box(disc, f"Disc{i}", (0, 0, 0), (4.3, 0.06, 4.3 * math.tan(math.radians(11.25)) * 1.02), m["portal"],
			collide=False, rot=quat(pitch=i * 22.5))
	portal = Node("ReturnPortal", (0, 0, 0.6), parent=fountain)
	portal.comp("Survival.HubPortal", Kind="ToWorld", RadiusMeters=1.9, CentreHeightMeters=2.2)
	point_light(fountain, "PortalGlow", (0, 0, 3), "0.7,0.35,1,1", 14)
	arrival = Node("ArrivalPoint", (0, -10, 0), quat(yaw=90), parent=hall)
	root.components[0]["ArrivalPoint"] = arrival.ref()

	cx, cy = COL_C
	fy0, fy1 = cy - COL_HY, cy + COL_HY      # arena floor y range
	ly0, ly1 = fy0 - 22, fy0 - 2             # lobby interior y range (its north wall is the arena's south wall)
	H = 12.0

	# ---- lobby (arena building), against the coliseum's south side --------------------------------------------------
	lobby = Node("Lobby", parent=root)
	LX = 60.0
	span(lobby, "Floor", -LX, LX, ly0, fy0, -1, FLOOR_LIFT, m["neon"])
	span(lobby, "Roof", -LX - 0.5, LX + 0.5, ly0 - 0.5, fy0, H, H + 0.5, m["neon"])
	span(lobby, "WallSouthW", -LX - 0.5, -4, ly0 - 0.5, ly0, 0, H, m["neon"])
	span(lobby, "WallSouthE", 4, LX + 0.5, ly0 - 0.5, ly0, 0, H, m["neon"])
	span(lobby, "DoorLintel", -4, 4, ly0 - 0.5, ly0, 5, H, m["neon"])
	span(lobby, "DoorTrim", -4, 4, ly0 - 0.55, ly0 + 0.05, 4.9, 5.1, m["cyan"], collide=False)
	span(lobby, "SignArena", -12, 12, ly0 - 0.6, ly0 - 0.5, 7, 9, m["magenta"], collide=False)
	span(lobby, "WallWest", -LX - 0.5, -LX, ly0, ly1, 0, H, m["neon"])
	span(lobby, "WallEast", LX, LX + 0.5, ly0, ly1, 0, H, m["neon"])
	# Hall / wing dividers with wide openings (left = bar, right = games).
	for side, x in (("W", -10), ("E", 10)):
		span(lobby, f"Divider{side}S", x - 0.25, x + 0.25, ly0, ly0 + 5, 0, H, m["neon"])
		span(lobby, f"Divider{side}N", x - 0.25, x + 0.25, ly1 - 5, ly1, 0, H, m["neon"])
		span(lobby, f"Divider{side}Top", x - 0.25, x + 0.25, ly0 + 5, ly1 - 5, 6, H, m["neon"])
	# North wall = the coliseum's south wall, with the fish-tank window in the hall.
	span(lobby, "WallNorthW", -LX - 0.5, -10, ly1, fy0, 0, H, m["neon"])
	span(lobby, "WallNorthE", 10, LX + 0.5, ly1, fy0, 0, H, m["neon"])
	span(lobby, "WindowSill", -10, 10, ly1, fy0, 0, 0.5, m["neon"])
	span(lobby, "WindowHead", -10, 10, ly1, fy0, 9, H, m["neon"])
	span(lobby, "Window", -10, 10, ly1 + 0.9, ly1 + 1.1, 0.5, 9, m["glass"])
	for i, x in enumerate((-5, 0, 5)):
		span(lobby, f"WindowMullion{i}", x - 0.08, x + 0.08, ly1 + 0.8, ly1 + 1.2, 0.5, 9, m["metal"])
	span(lobby, "WindowTrim", -10, 10, ly1 - 0.05, ly1 + 0.05, 9, 9.15, m["cyan"], collide=False)
	for name, x in (("Hall", 0), ("Bar", -35), ("Games", 35)):
		point_light(lobby, f"Light{name}A", (x, ly0 + 5, H - 1), "0.55,0.85,1,1", 18)
		point_light(lobby, f"Light{name}B", (x, ly1 - 5, H - 1), "0.55,0.85,1,1", 18)
	hall_return = Node("ArenaReturnPoint", (0, ly0 + 5, 0), quat(yaw=90), parent=lobby)

	# Arena queue kiosk in the hall, beside the window.
	kiosk = Node("ArenaKiosk", (-7, ly1 - 1.5, 0), quat(yaw=-90), parent=lobby)
	box(kiosk, "Body", (0, 0, 0.55), (0.8, 1.2, 1.1), m["neon"])
	box(kiosk, "Screen", (0.42, 0, 1.25), (0.05, 1.0, 0.6), m["cyan"], collide=False)

	# Bar wing: counter + bookie along the south wall, benches facing the big screen on the west wall.
	bar = Node("Bar", parent=lobby)
	span(bar, "Counter", -52, -20, ly0 + 2.6, ly0 + 3.6, 0, 1.1, m["wood"])
	span(bar, "CounterTop", -52.1, -19.9, ly0 + 2.5, ly0 + 3.7, 1.1, 1.18, m["metal"])
	span(bar, "CounterTrim", -52, -20, ly0 + 3.6, ly0 + 3.65, 0.9, 1.0, m["cyan"], collide=False)
	span(bar, "BackShelf", -52, -20, ly0, ly0 + 0.5, 1.4, 3.2, m["wood"])
	for i, x in enumerate(range(-50, -20, 3)):
		box(bar, f"Stool{i}", (x, ly0 + 4.4, 0.35), (0.5, 0.5, 0.7), m["metal"])
	bench_y = (ly0 + ly1) / 2 + 2
	for r, x in enumerate((-48, -42, -36, -30)):
		box(bar, f"Bench{r}", (x, bench_y, 0.25), (1.0, 10.0, 0.5), m["wood"])
		box(bar, f"BenchBack{r}", (x + 0.45, bench_y, 0.75), (0.15, 10.0, 0.5), m["wood"])
	scr_y = (ly0 + ly1) / 2 + 1
	screen = box(bar, "BigScreen", (-LX + 0.15, scr_y, 6.0), (0.1, 17.6, 9.9), m["screen"], collide=False, rot=quat(yaw=180))
	span(bar, "ScreenFrame", -LX, -LX + 0.1, scr_y - 9.1, scr_y + 9.1, 0.85, 11.15, m["metal"], collide=False)
	span(bar, "ScreenTrim", -LX + 0.08, -LX + 0.12, scr_y - 9.0, scr_y + 9.0, 11.05, 11.1, m["magenta"], collide=False)
	bookie = Node("Bookie", (-36, ly0 + 1.6, 0), quat(yaw=90), parent=bar)
	bookie.comp("Sandbox.SkinnedModelRenderer", AnimationGraph=None, BodyGroups=18446744073709551615, BoneMergeTarget=None,
				CreateAttachments=False, CreateBoneObjects=False, LodOverride=None, MaterialGroup=None,
				MaterialOverride=None, Materials=None, Model="models/citizen/citizen.vmdl", Morphs={}, Parameters={},
				PlaybackRate=1, RenderOptions={"GameLayer": True, "OverlayLayer": False, "BloomLayer": False,
											   "AfterUILayer": False}, RenderType="On", Sequence={}, Tint="1,1,1,1",
				UseAnimGraph=True)
	bookie.comp("Survival.HubKiosk", Kind="Bets", Prompt="Place Bets", ReachMeters=4.0, LookConeDegrees=30.0,
				TargetHeightMeters=1.4)
	board = bookie.comp("Survival.HubBetBoard")  # Arena set once the coliseum exists
	# Commend button: a glowing pedestal in front of the screen, off to one side (any one press locks the screen on the cameraman).
	commend = Node("CommendButton", (-LX + 5, scr_y + 6, 0), quat(yaw=0), parent=bar)
	box(commend, "Post", (0, 0, 0.55), (0.6, 0.6, 1.1), m["metal"])
	box(commend, "Button", (0, 0, 1.15), (0.5, 0.5, 0.1), m["magenta"], collide=False)
	commend.comp("Survival.HubKiosk", Kind="Commend", Prompt="Commend Cameraman", ReachMeters=3.0, LookConeDegrees=35.0,
				 TargetHeightMeters=1.1)

	# Games wing: the Stratagem Hero cabinet against the north wall, facing the room.
	games = Node("Games", parent=lobby)
	cab = Node("StratagemCabinet", (35, ly1 - 0.7, 0), quat(yaw=-90), parent=games)
	box(cab, "Body", (0, 0, 1.0), (0.9, 1.2, 2.0), m["metal"])
	box(cab, "Screen", (0.47, 0, 1.45), (0.04, 0.9, 0.6), m["cyan"], collide=False)
	box(cab, "Panel", (0.6, 0, 1.0), (0.4, 1.1, 0.12), m["neon"], rot=quat(pitch=-15))
	box(cab, "Marquee", (0.3, 0, 2.15), (0.3, 1.2, 0.3), m["magenta"], collide=False)
	cab.comp("Survival.HubKiosk", Kind="Stratagem", Prompt="Play Stratagem Hero", ReachMeters=3.5, LookConeDegrees=35.0,
			 TargetHeightMeters=1.3)
	cab.comp("Survival.StratagemMachine")
	span(games, "SignStratagem", 27, 43, ly1 - 0.2, ly1, 6.5, 7.5, m["magenta"], collide=False)

	# ---- coliseum: 150 x 100 m floor, podium, six tiers of stands, arcade facade --------------------------------------
	col = Node("Coliseum", parent=root)
	X, PH = COL_HX, 5.0
	span(col, "Floor", -X, X, fy0, fy1, -1, FLOOR_LIFT, m["sand"])
	span(col, "PodiumN", -X - 1, X + 1, fy1, fy1 + 1, 0, PH, m["stone"])
	span(col, "PodiumW", -X - 1, -X, fy0, fy1, 0, PH, m["stone"])
	span(col, "PodiumE", X, X + 1, fy0, fy1, 0, PH, m["stone"])
	span(col, "PodiumLightN", -X - 1, X + 1, fy1 - 0.05, fy1, PH - 0.3, PH - 0.15, m["light"], collide=False)
	span(col, "PodiumLightW", -X, -X + 0.05, fy0, fy1, PH - 0.3, PH - 0.15, m["light"], collide=False)
	span(col, "PodiumLightE", X - 0.05, X, fy0, fy1, PH - 0.3, PH - 0.15, m["light"], collide=False)
	TIERS, TD, TR = 6, 3.0, 2.5
	for t in range(TIERS):
		top = PH + 2.5 + TR * t
		d0 = TD * t
		span(col, f"TierN{t}", -X - 1 - TD * (t + 1), X + 1 + TD * (t + 1), fy1 + 1 + d0, fy1 + 1 + d0 + TD, 0, top, m["stone"])
		span(col, f"TierW{t}", -X - 1 - d0 - TD, -X - 1 - d0, fy0, fy1 + 1 + d0, 0, top, m["stone"])
		span(col, f"TierE{t}", X + 1 + d0, X + 1 + d0 + TD, fy0, fy1 + 1 + d0, 0, top, m["stone"])
		span(col, f"TierLightN{t}", -X - 1 - TD * (t + 1), X + 1 + TD * (t + 1), fy1 + 0.95 + d0, fy1 + 1 + d0, top - 0.3,
			 top - 0.12, m["light"], collide=False)
		span(col, f"TierLightW{t}", -X - 1 - d0 - 0.05, -X - 1 - d0, fy0, fy1 + 1 + d0, top - 0.3, top - 0.12, m["light"],
			 collide=False)
		span(col, f"TierLightE{t}", X + 1 + d0, X + 1 + d0 + 0.05, fy0, fy1 + 1 + d0, top - 0.3, top - 0.12, m["light"],
			 collide=False)
	outer = X + 1 + TD * TIERS        # facade line (x), the back of the top tier
	outer_y = fy1 + 1 + TD * TIERS    # facade line (y)
	FH = 28.0                         # three storeys
	fac = Node("Facade", parent=col)
	span(fac, "BackN", -outer - 1, outer + 1, outer_y, outer_y + 1, 0, FH, m["metal"])
	span(fac, "BackW", -outer - 1, -outer, ly0 - 0.5, outer_y, 0, FH, m["metal"])
	span(fac, "BackE", outer, outer + 1, ly0 - 0.5, outer_y, 0, FH, m["metal"])
	for storey in range(3):
		zt = FH / 3 * (storey + 1)
		span(fac, f"BandN{storey}", -outer, outer, outer_y - 0.5, outer_y, zt - 0.9, zt, m["stone"])
		span(fac, f"BandW{storey}", -outer, -outer + 0.5, fy0, outer_y, zt - 0.9, zt, m["stone"])
		span(fac, f"BandE{storey}", outer - 0.5, outer, fy0, outer_y, zt - 0.9, zt, m["stone"])
		span(fac, f"GlowN{storey}", -outer, outer, outer_y - 0.55, outer_y - 0.5, zt - 1.2, zt - 1.0, m["light"], collide=False)
		span(fac, f"GlowW{storey}", -outer + 0.5, -outer + 0.55, fy0, outer_y, zt - 1.2, zt - 1.0, m["light"], collide=False)
		span(fac, f"GlowE{storey}", outer - 0.55, outer - 0.5, fy0, outer_y, zt - 1.2, zt - 1.0, m["light"], collide=False)
	for i, x in enumerate(np.arange(-outer + 3, outer - 1, 6.0)):
		span(fac, f"PierN{i}", x - 0.9, x + 0.9, outer_y - 1.0, outer_y, 0, FH, m["stone"])
	for i, y in enumerate(np.arange(fy0 + 3, outer_y - 1, 6.0)):
		span(fac, f"PierW{i}", -outer, -outer + 1.0, y - 0.9, y + 0.9, 0, FH, m["stone"])
		span(fac, f"PierE{i}", outer - 1.0, outer, y - 0.9, y + 0.9, 0, FH, m["stone"])
	span(col, "RoofEdgeN", -outer - 1, outer + 1, outer_y - 2, outer_y + 1, FH, FH + 0.8, m["stone"])
	span(col, "RoofEdgeW", -outer - 1, -outer + 2, ly0 - 0.5, outer_y - 2, FH, FH + 0.8, m["stone"])
	span(col, "RoofEdgeE", outer - 2, outer + 1, ly0 - 0.5, outer_y - 2, FH, FH + 0.8, m["stone"])
	# South side: the lobby fills the middle; stone wings either side of it out to the facade corners, a parapet above.
	span(col, "SouthW", -outer, -LX - 0.5, ly0 - 0.5, fy0, 0, FH, m["stone"])
	span(col, "SouthE", LX + 0.5, outer, ly0 - 0.5, fy0, 0, FH, m["stone"])
	span(col, "ParapetS", -LX - 0.5, LX + 0.5, ly1, fy0, H + 0.5, FH * 0.6, m["stone"])
	# Floodlights: six high, wide lamps so the whole floor is evenly lit (low narrow ones left hard circles).
	for i, (x, y) in enumerate([(x, cy + dy) for x in (-45, 0, 45) for dy in (-25, 25)]):
		point_light(col, f"Flood{i}", (x, y, 55), "0.75,0.7,0.6,1", 115)

	# The arena session: centre of the floor, team spawns behind the forts, players return to the hall.
	arena = Node("ArenaSession", (cx, cy, 0), parent=col)
	spawn_a = Node("TeamSpawnA", (-65, cy, 0.2), quat(yaw=0), parent=col)
	spawn_b = Node("TeamSpawnB", (65, cy, 0.2), quat(yaw=180), parent=col)
	cam_spawn = Node("CameramanSpawn", (0, fy0 + 4, 0.2), quat(yaw=90), parent=col)
	arena_comp = arena.comp("Survival.ArenaSession", ArenaCenter=None, ArenaRadiusMeters=92.0,
							TeamSpawnPoints=[spawn_a.ref(), spawn_b.ref()], ReturnPoint=hall_return.ref(),
							CountdownBeatSeconds=1.0, FinishedHoldSeconds=6.0, BettingWindowSeconds=30.0,
							CameramanSpawn=cam_spawn.ref(), LogArena=True)
	kiosk.comp("Survival.ArenaMenuButton", ReachMeters=6.0, LookConeDegrees=40.0)
	board["Arena"] = comp_ref(arena, arena_comp)

	# Feed camera + the fixed angles round the floor (the screen cycles them every 10 s).
	feed = Node("FeedCamera", (0, fy0 + 2, 22), look_at((0, fy0 + 2, 22), (cx, cy, 0)), parent=col)
	cam_comp = feed.comp("Sandbox.CameraComponent", BackgroundColor="0.1,0.1,0.12,1", ClearFlags="All", FieldOfView=75,
						 IsMainCamera=False, Orthographic=False, OrthographicHeight=1204, Priority=-10,
						 RenderExcludeTags="", RenderTags="", TargetEye="None", Viewport="0,0,1,1", ZFar=60000, ZNear=10)
	angles = Node("FeedAngles", parent=col)
	shots = [("SouthHigh", (0, fy0 + 2, 22), (cx, cy, 0)),
			 ("NorthStands", (0, fy1 + 12, 26), (cx, cy, 0)),
			 ("WestStands", (-X - 12, cy, 22), (cx, cy, 0)),
			 ("EastStands", (X + 12, cy, 22), (cx, cy, 0)),
			 ("CornerLow", (-X + 6, fy0 + 6, 3), (cx, cy, 2)),
			 ("FortA", (-35, cy + 30, 14), (-50, cy, 4)),
			 ("FortB", (35, cy - 30, 14), (50, cy, 4))]
	angle_refs = [Node(name, frm, look_at(frm, to), parent=angles).ref() for name, frm, to in shots]
	angle_refs.append(Node("Overhead", (cx, cy, 70), quat(pitch=89, yaw=90), parent=angles).ref())
	screen_comp = [c for c in screen.components if c["__type"] == "Sandbox.ModelRenderer"][0]
	bar.comp("Survival.HubArenaScreen", FeedCamera=comp_ref(feed, cam_comp), Screen=comp_ref(screen, screen_comp),
			 Arena=comp_ref(arena, arena_comp), Angles=angle_refs, SecondsPerAngle=10.0, FeedWidth=480,
			 WatchDistanceMeters=60.0)

	for side, sx, yaw in (("A", -1, 0), ("B", 1, 180)):
		build_fort(col, side, (50 * sx, cy), yaw, m)

	# Barrel piles: the big one in the middle, two smaller ones off-centre.
	def pile(name, centre, spots, downs):
		g = Node(name, centre, parent=col)
		for i, (x, y, z, yaw) in enumerate(spots):
			b = Node(f"Barrel{i}", (x, y, z), quat(yaw=yaw), (1.4, 1.4, 1.4), "", g)
			renderer(b, barrel)
			b.comp("Sandbox.BoxCollider", Center=f"0,0,{0.45 * U:.4g}", IsTrigger=False,
				   Scale=f"{0.62 * U:.4g},{0.62 * U:.4g},{0.9 * U:.4g}", Static=True, Surface=None)
		for i, (x, y, yaw) in enumerate(downs):
			b = Node(f"BarrelDown{i}", (x, y, 0.47), quat(pitch=90, yaw=yaw), (1.4, 1.4, 1.4), "", g)
			renderer(b, barrel)
			b.comp("Sandbox.BoxCollider", Center=f"0,0,{0.45 * U:.4g}", IsTrigger=False,
				   Scale=f"{0.62 * U:.4g},{0.62 * U:.4g},{0.9 * U:.4g}", Static=True, Surface=None)

	pile("BarrelPile", (cx, cy, 0),
		 [(0, 0, 0, 0), (0.95, 0.2, 0, 30), (-0.9, 0.35, 0, 70), (0.25, -0.95, 0, 10), (-0.4, 0.95, 0, 50),
		  (0.4, -0.1, 1.26, 15), (-0.45, 0.3, 1.26, 45)], [(2.0, -0.6, 20), (-1.9, -0.9, 110)])
	pile("BarrelPileN", (cx + 18, cy + 24, 0), [(0, 0, 0, 0), (0.95, 0.3, 0, 40), (0.4, 0.1, 1.26, 20)], [(-1.6, 0.4, 70)])
	pile("BarrelPileS", (cx - 18, cy - 24, 0), [(0, 0, 0, 10), (-0.9, -0.2, 0, 60), (-0.4, -0.1, 1.26, 5)], [(1.7, -0.5, 160)])

	# ---- time trial: queue building on the cube's east face, the 500 x 500 m cube ------------------------------------
	qx0, qx1, qy0, qy1, qy_door = CUBE[1], CUBE[1] + 20, -12.0, 12.0, 3.0
	tt = Node("TimeTrial", parent=root)
	span(tt, "Floor", qx0, qx1, qy0, qy1, -1, FLOOR_LIFT, m["neon"])
	span(tt, "Roof", qx0, qx1 + 0.5, qy0 - 0.5, qy1 + 0.5, 8, 8.5, m["neon"])
	span(tt, "WallEastS", qx1, qx1 + 0.5, qy0 - 0.5, -qy_door, 0, 8, m["neon"])
	span(tt, "WallEastN", qx1, qx1 + 0.5, qy_door, qy1 + 0.5, 0, 8, m["neon"])
	span(tt, "DoorLintel", qx1, qx1 + 0.5, -qy_door, qy_door, 5, 8, m["neon"])
	span(tt, "DoorTrim", qx1 - 0.05, qx1 + 0.55, -qy_door, qy_door, 4.9, 5.1, m["cyan"], collide=False)
	span(tt, "SignTrials", qx1 + 0.5, qx1 + 0.6, -8, 8, 6, 7.5, m["magenta"], collide=False)
	span(tt, "WallSouth", qx0, qx1 + 0.5, qy0 - 0.5, qy0, 0, 8, m["neon"])
	span(tt, "WallNorth", qx0, qx1 + 0.5, qy1, qy1 + 0.5, 0, 8, m["neon"])
	point_light(tt, "Light", ((qx0 + qx1) / 2, 0, 7), "0.55,0.85,1,1", 20)
	desk = Node("TrialDesk", (qx0 + 3, 0, 0), quat(yaw=0), parent=tt)
	box(desk, "Body", (0, 0, 0.55), (1.0, 3.0, 1.1), m["neon"])
	box(desk, "Top", (0, 0, 1.12), (1.1, 3.1, 0.06), m["cyan"], collide=False)
	desk.comp("Survival.TimeTrialMenuButton", ReachMeters=6.0, LookConeDegrees=40.0)
	trial_exit = Node("TrialExitPoint", (qx1 - 6, 0, 0), quat(yaw=0), parent=tt)

	x0, x1, y0, y1 = CUBE
	cube = Node("TrialCube", parent=tt)
	span(cube, "Floor", x0, x1, y0, y1, -1, FLOOR_LIFT, m["grid"])
	span(cube, "WallWest", x0 - 1, x0, y0 - 1, y1 + 1, 0, CUBE_H, m["grid"])
	span(cube, "WallEast", x1, x1 + 1, y0 - 1, y1 + 1, 0, CUBE_H, m["grid"])
	span(cube, "WallSouth", x0, x1, y0 - 1, y0, 0, CUBE_H, m["grid"])
	span(cube, "WallNorth", x0, x1, y1, y1 + 1, 0, CUBE_H, m["grid"])
	# The course: a straight line of grapple bars from the start platform (east, by the queue building) to the end
	# platform (west), 10 m above the platforms.
	sx_, ex_ = x1 - 10, x0 + 10          # platform centres
	span(cube, "StartPlatform", sx_ - 4, sx_ + 4, -4, 4, 19, 20, m["neon"])
	span(cube, "StartColumn", sx_ - 1, sx_ + 1, -1, 1, 0, 19, m["neon"])
	span(cube, "EndPlatform", ex_ - 4, ex_ + 4, -4, 4, 19, 20, m["neon"])
	span(cube, "EndColumn", ex_ - 1, ex_ + 1, -1, 1, 0, 19, m["neon"])
	for i, x in enumerate((sx_ - 4, sx_ + 4, ex_ - 4, ex_ + 4)):
		span(cube, f"PlatformTrim{i}", x - 0.05, x + 0.05, -4, 4, 19.9, 20.0, m["cyan"], collide=False)
	bars = Node("GrappleBars", tags="grapple", parent=cube)
	rx0, rx1 = ex_ + 6, sx_ - 6
	span(bars, "RailS", rx0, rx1, -2.6, -2.2, 29.8, 30.2, m["bar"])
	span(bars, "RailN", rx0, rx1, 2.2, 2.6, 29.8, 30.2, m["bar"])
	for i, x in enumerate(np.arange(rx0 + 2, rx1 - 1, 3.0)):
		box(bars, f"Bar{i:03d}", (float(x), 0, 30), (0.2, 4.4, 0.2), m["bar"])
	span(bars, "TopBeam", rx0 - 1, rx1 + 1, -3, 3, CUBE_H - 1, CUBE_H, m["bar"])
	for i, x in enumerate(np.arange(rx0, rx1 + 0.1, 30.0)):
		span(bars, f"HangerS{i:02d}", x - 0.25, x + 0.25, -2.6, -2.2, 30, CUBE_H - 1, m["bar"])
		span(bars, f"HangerN{i:02d}", x - 0.25, x + 0.25, 2.2, 2.6, 30, CUBE_H - 1, m["bar"])
	for i, x in enumerate(np.linspace(sx_ - 20, ex_ + 20, 8)):
		point_light(cube, f"CourseLight{i}", (float(x), 0, 50), "1,1,1,1", 90)
	# The race: session at the start platform (racers spawn 1 m above it facing the course), rings 10 .. 15.
	start = Node("TrialStart", (sx_, 0, 20), quat(yaw=180), parent=cube)
	finish_face = Node("TrialFaceTarget", (ex_, 0, 20), parent=cube)
	start.comp("Survival.TimeTrialSession", SoloStartLocalOffset="0,0,40", TwoPlayerLeftLocalOffset="0,60,40",
			   TwoPlayerRightLocalOffset="0,-60,40", FaceTarget=finish_face.ref(), CountdownBeatSeconds=1.0,
			   FinishedHoldSeconds=10.0, CountdownBeep=None, GoBeep=None, LendGrapple=True, LogTimeTrial=True)
	for k, x in enumerate(np.linspace(sx_ - 5.5, ex_ + 4, 6)):
		order = 10 + k
		z = 22 if order in (10, 15) else 24
		cp = Node(f"Ring{order}", (float(x), 0, z), parent=cube)
		cp.comp("Sandbox.SphereCollider", Center="0,0,0", IsTrigger=True, Radius=2.5 * U, Static=True, Surface=None)
		cp.comp("Survival.TimeTrialCheckpoint", Order=order, InactiveScale="0.4,0.4,0.4", ActiveScale="1,1,1")
		ring(cp, "Hoop", (0, 0, 0), 2.5, 1.08, 0.25, m["cyan" if order not in (10, 15) else "magenta"], plane="yz")
	for name, pos in (("ExitPadEnd", (ex_ - 1.5, 0, 20)), ("ExitPadFloor", (x1 - 6, 8, 0))):
		pad = Node(name, pos, parent=cube)
		box(pad, "Pad", (0, 0, 0.05), (2.0, 2.0, 0.1), m["portal"], collide=False)
		pad.comp("Survival.HubPortal", Kind="Teleport", RadiusMeters=1.0, CentreHeightMeters=0.9, Target=trial_exit.ref())

	return root


def build_fort(col, side, centre, yaw, m):
	"""One team's fort: a 6 x 6 m tower six kit walls (12 m) tall with switchback ramps inside and merlons on top,
	plus two battlement walls (4.5 m walkway) either side of it with a ramp up the back. Authored for team A (west,
	door facing west) and rotated 180 degrees for team B."""
	fort = Node(f"Fort{side}", (centre[0], centre[1], 0), quat(yaw=yaw), parent=col)

	# Tower walls: 3 kit walls per side x 6 rows. Door: west side, middle piece, bottom row. Walkway door: north side,
	# west piece, third row (4..6 m) onto the corner landing at 4.5 m.
	for row in range(6):
		z = 1 + 2 * row
		for k, off in enumerate((-2, 0, 2)):
			if not (row == 0 and k == 0):
				kit_wall(fort, f"TowerW{row}{k}", (-3, off, z), 0, m["stone"])
			kit_wall(fort, f"TowerE{row}{k}", (3, off, z), 0, m["stone"])
			kit_wall(fort, f"TowerS{row}{k}", (off, -3, z), 90, m["stone"])
			if not (row == 2 and k == 0):
				kit_wall(fort, f"TowerN{row}{k}", (off, 3, z), 90, m["stone"])
	# Merlons: 1 m blocks every other metre on the parapet.
	for i, t in enumerate(np.arange(-2.5, 3.0, 2.0)):
		for name, pos in (("W", (-3, t)), ("E", (3, t)), ("S", (t, -3)), ("N", (t, 3))):
			box(fort, f"Merlon{name}{i}", (pos[0], pos[1], 12.5), (0.6, 0.6, 1.0), m["metal"])
	# Switchback ramps round the inside walls: 8 flights of 1.5 m rise over 3.3 m, corner landings.
	lane = 2.2
	corners = [(-lane, -lane), (lane, -lane), (lane, lane), (-lane, lane)]
	for k in range(8):
		s = k % 4
		(ax, ay), (bx, by) = corners[s], corners[(s + 1) % 4]
		z0, z1 = 1.5 * k, 1.5 * (k + 1)
		dx, dy = bx - ax, by - ay
		length = math.hypot(dx, dy)
		ux, uy = dx / length, dy / length
		run = length - 1.1
		slope = math.hypot(run, z1 - z0)
		mx, my = (ax + bx) / 2, (ay + by) / 2
		pitch = -math.degrees(math.atan2(z1 - z0, run))
		box(fort, f"Ramp{k}", (mx, my, (z0 + z1) / 2 - 0.12), (slope, 1.1, 0.2), m["metal"],
			rot=quat(pitch=pitch, yaw=math.degrees(math.atan2(uy, ux))))
		cx, cy = corners[(s + 1) % 4]
		box(fort, f"Landing{k}", (cx, cy, z1 - 0.1), (1.1, 1.1, 0.2), m["metal"])
	# Top deck at 12 m: everything inside except the lanes of the last two flights (west and north sides), so the
	# climb keeps its headroom.
	span(fort, "DeckCentre", -1.65, 2.75, -2.75, 1.65, 11.8, 12.0, m["metal"])

	# Battlements north and south of the tower: 2 m thick, 4.5 m to the walkway, kit-wall faces, merlons toward the
	# middle of the arena (+X for team A), a ramp up the back (west) side.
	for name, y0, y1, up in (("N", 3, 11, 1), ("S", -11, -3, -1)):
		span(fort, f"Battlement{name}", -1, 1, y0, y1, 0, 4.5, m["stone"])
		for row in range(2):
			for k, yc in enumerate(np.arange(y0 + 1, y1, 2.0)):
				kit_wall(fort, f"Batt{name}E{row}{k}", (1.13, float(yc), 1 + 2 * row), 0, m["stone"])
				kit_wall(fort, f"Batt{name}W{row}{k}", (-1.13, float(yc), 1 + 2 * row), 0, m["stone"])
		for k, yc in enumerate(np.arange(y0 + 0.5, y1, 1.5)):
			box(fort, f"BattMerlon{name}{k}", (0.75, float(yc), 5.0), (0.5, 0.6, 1.0), m["metal"])
		# Ramp: rises toward the tower along the back face, 1.6 m wide, from the far end of the wall to the walkway.
		far, near = (y1, y0) if up > 0 else (y0, y1)
		run = abs(far - near) - 0.5
		slope = math.hypot(run, 4.5)
		pitch = -math.degrees(math.atan2(4.5, run))
		ry = (far + near) / 2 - up * 0.25
		box(fort, f"BattRamp{name}", (-2.0, ry, 2.25 - 0.1), (slope, 1.6, 0.2), m["metal"],
			rot=quat(pitch=pitch, yaw=-90 if up > 0 else 90))
	return fort


def build_portal_piece(m):
	"""The buildable purple portal (build_pieces.json 'hub_portal'): a 3 m ring on a plinth, purple disc, the
	HubPortal (ToHub) and the build-piece components every piece carries. Build pieces are centred on their bounding
	box (the placer lifts them by their half height), so the 3.4 m tall piece is authored 1.7 m below its origin."""
	z0 = -1.7  # floor of the piece relative to its origin
	root = Node("hub_portal", tags="buildpiece")
	root.comp("Sandbox.BoxCollider", Center=f"0,0,{(z0 + 0.15) * U:.4g}", IsTrigger=False,
			  Scale=f"{1.2 * U:.4g},{3.4 * U:.4g},{0.3 * U:.4g}", Static=True, Surface=None)
	root.comp("Survival.DamageReceiver", DamageMultiplier=1)
	root.comp("Survival.BuildPiece", IsBlueprint=False, PieceId="hub_portal")
	root.comp("Survival.HubPortal", Kind="ToHub", RadiusMeters=1.3, CentreHeightMeters=z0 + 1.8 + 1.7,
			  HubPrefab={"_type": "gameobject", "prefab": "prefabs/hub/hub.prefab"}, HubPositionMeters="0,0,3000")
	box(root, "Plinth", (0, 0, z0 + 0.15), (1.2, 3.4, 0.3), m["neon"], collide=False)
	ring(root, "Ring", (0, 0, z0 + 1.8), 1.5, 1.08, 0.22, m["purple"], plane="yz", count=16, collide=True)
	disc = Node("Disc", (0, 0, z0 + 1.8), parent=root)
	for i in range(8):
		box(disc, f"Disc{i}", (0, 0, 0), (0.05, 2.9, 2.9 * math.tan(math.radians(11.25)) * 1.02), m["portal"],
			collide=False, rot=quat(roll=i * 22.5))
	point_light(root, "Glow", (0, 0, z0 + 1.8), "0.7,0.35,1,1", 6)
	return root


def write_prefab(node, rel):
	doc = {
		"RootObject": node.json(),
		"ResourceVersion": 2, "ShowInMenu": False, "MenuPath": None, "MenuIcon": None, "DontBreakAsTemplate": False,
		"__references": [], "__version": 2,
	}
	doc["RootObject"]["__variables"] = []
	path = os.path.join(ASSETS, rel)
	os.makedirs(os.path.dirname(path), exist_ok=True)
	with open(path, "w", encoding="utf-8", newline="\n") as f:
		json.dump(doc, f, indent=2)
	return path


def count(node):
	return 1 + sum(count(c) for c in node.children)


if __name__ == "__main__":
	make_textures()
	mats = make_materials()
	barrel_model = make_barrel()
	hub = build_hub(mats, barrel_model)
	print("hub objects:", count(hub), "->", write_prefab(hub, "prefabs/hub/hub.prefab"))
	piece = build_portal_piece(mats)
	print("portal objects:", count(piece), "->", write_prefab(piece, "prefabs/build/hub_portal.prefab"))
