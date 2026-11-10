"""Wild paper-wasp / honeybee nest that hangs from a low tree limb (Clover Hills resource).

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" -b --python "Blender/scripts/create_beehive.py"

Writes
  Assets/models/environment/cloverHills/resources/beehive1.fbx + .vmdl
  Assets/models/environment/cloverHills/resources/beehive2.fbx + .vmdl
  Assets/materials/environment/cloverHills/resources/beehive.png + .vmat   (256 x 256, pixel_lit, faceted)
  Blender/blenderprojects/beehive.blend                                     (both variants side by side)

Model convention (read by the spawn code - no per-model offsets):
  * the ORIGIN is the top attachment point: the top of the short twig stub that meets the limb,
  * the nest hangs straight down -Z from there (Blender metres, exported 1:1; vmdl import_scale 0.4 = 40 u/m).

Geometry: a lathe with irregular segment angles, irregular ring heights and per-vertex jitter (so no sphere /
cylinder silhouette), built from overlapping paper bands - each band is tucked in at its top and flares into a
lip at its bottom, so the layered ridges read as shingles from a distance. A recessed dark entrance hole near
the bottom, a bark twig stub at the top. beehive1 is the classic teardrop; beehive2 is a longer double lobe with
a waist, a lean to one side and a drip tip.

Texture: one 256 px atlas - rows 0..199 are the paper (cylindrically mapped, tiles in u), wavy horizontal stripes
in four low-contrast grey-tan tones (the chewed-wood paper look); rows 200..255 hold the dark entrance (left)
and the twig bark (right).
"""
import bpy, bmesh, math, os, struct, zlib
import numpy as np
from mathutils import Vector

ROOT = r"M:\s&box\Projects\SurvivalGame2026"
MODEL_DIR = os.path.join(ROOT, "Assets", "models", "environment", "cloverHills", "resources")
MAT_DIR = os.path.join(ROOT, "Assets", "materials", "environment", "cloverHills", "resources")
BLEND_PATH = os.path.join(ROOT, "Blender", "blenderprojects", "beehive.blend")
MODEL_REL = "models/environment/cloverHills/resources"
MAT_REL = "materials/environment/cloverHills/resources"
MAT_NAME = "beehive"

TEX = 256
PAPER_ROWS = 200
PAPER_TONES = [(141, 123, 96), (136, 118, 92), (146, 128, 100), (132, 115, 89)]
HOLE = (38, 31, 25)
BARK_TONES = [(92, 78, 62), (82, 69, 55), (101, 87, 69)]


# ---------------------------------------------------------------- texture
def periodic_noise(rng, h, w, cy, cx):
	"""Smooth 0..1 noise that tiles in x: bilinear upsample of a cy x cx grid wrapped in x."""
	grid = rng.random((cy + 1, cx))
	ys = np.linspace(0, cy, h, endpoint=False)
	xs = np.linspace(0, cx, w, endpoint=False)
	y0 = np.floor(ys).astype(int); x0 = np.floor(xs).astype(int)
	fy = (ys - y0)[:, None]; fx = (xs - x0)[None, :]
	x1 = (x0 + 1) % cx
	a = grid[y0][:, x0]; b = grid[y0][:, x1]; c = grid[y0 + 1][:, x0]; d = grid[y0 + 1][:, x1]
	return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def make_texture():
	rng = np.random.default_rng(31)
	img = np.zeros((TEX, TEX, 4), np.float64)
	img[..., 3] = 255

	# Paper: wavy horizontal stripes of random width, each one of four close tones (no speckle).
	edges = [0.0]
	while edges[-1] < PAPER_ROWS + 20:
		edges.append(edges[-1] + rng.uniform(4.0, 11.0))
	tones = []
	last = -1
	for _ in edges:
		t = int(rng.integers(0, len(PAPER_TONES)))
		while t == last:
			t = int(rng.integers(0, len(PAPER_TONES)))
		tones.append(t); last = t
	edges = np.array(edges)
	x = np.arange(TEX)
	p1, p2 = rng.uniform(0, 2 * math.pi, 2)
	wave = 3.5 * np.sin(2 * math.pi * x / TEX * 2 + p1) + 1.8 * np.sin(2 * math.pi * x / TEX * 5 + p2)
	drift = periodic_noise(rng, PAPER_ROWS, TEX, 5, 6)
	for row in range(PAPER_ROWS):
		s = row + 6.0 + wave
		idx = np.clip(np.searchsorted(edges, s) - 1, 0, len(tones) - 1)
		cols = np.array([PAPER_TONES[tones[i]] for i in idx], float)
		img[row, :, :3] = cols + ((drift[row] - 0.5) * 8.0)[:, None]

	# Entrance: near-black brown with a faint mottle.
	m = periodic_noise(rng, TEX - PAPER_ROWS, 128, 3, 4)
	img[PAPER_ROWS:, :128, :3] = np.array(HOLE, float) + ((m - 0.5) * 6.0)[:, :, None]
	# Bark: vertical streaks of three grey-browns.
	streak = rng.integers(0, len(BARK_TONES), 128 // 4)
	for col in range(128):
		img[PAPER_ROWS:, 128 + col, :3] = BARK_TONES[streak[col // 4]]
	return np.clip(img, 0, 255).astype(np.uint8)


def write_png(path, rgba):
	h, w, _ = rgba.shape
	raw = b"".join(b"\x00" + rgba[y].tobytes() for y in range(h))

	def chunk(tag, data):
		c = struct.pack(">I", len(data)) + tag + data
		return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

	png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
		   + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
	with open(path, "wb") as f:
		f.write(png)


def region_uv(rng, region):
	x0, y0, w, h = {"hole": (0, PAPER_ROWS, 128, TEX - PAPER_ROWS), "bark": (128, PAPER_ROWS, 128, TEX - PAPER_ROWS)}[region]
	return ((x0 + 3 + rng.random() * (w - 6)) / TEX, 1.0 - (y0 + 3 + rng.random() * (h - 6)) / TEX)


# ---------------------------------------------------------------- shapes
def lerp_profile(points, t):
	for i in range(len(points) - 1):
		t0, r0 = points[i]; t1, r1 = points[i + 1]
		if t0 <= t <= t1:
			f = (t - t0) / (t1 - t0)
			f = f * f * (3 - 2 * f)
			return r0 + (r1 - r0) * f
	return points[-1][1]


VARIANTS = {
	"beehive1": dict(
		seed=11, segs=9,
		z_top=-0.05, z_bot=-0.45,            # body span (the stub fills 0 .. -0.06)
		profile=[(0.0, 0.028), (0.07, 0.065), (0.2, 0.105), (0.4, 0.135), (0.62, 0.148), (0.78, 0.14), (0.9, 0.11), (0.97, 0.06), (1.0, 0.0)],
		bands=[0.10, 0.29, 0.49, 0.69, 0.86],
		lean=(0.0, 0.0), lump=(0.08, 2, 0.05, 3),
		bottom=[(0.925, 0.82), (0.97, 0.5)], tip=0.0,
		hole_band=4, hole_seg=2,
		stub_tilt=(0.012, 0.006),
	),
	"beehive2": dict(
		seed=23, segs=10,
		z_top=-0.05, z_bot=-0.44,
		profile=[(0.0, 0.026), (0.07, 0.075), (0.18, 0.11), (0.28, 0.112), (0.40, 0.08), (0.56, 0.125), (0.70, 0.145), (0.83, 0.125), (0.93, 0.07), (1.0, 0.012)],
		bands=[0.09, 0.27, 0.42, 0.60, 0.77, 0.89],
		lean=(0.07, 0.02), lump=(0.11, 1, 0.06, 3),
		bottom=[(0.935, 0.6), (0.97, 0.34)], tip=0.03,
		hole_band=4, hole_seg=2,
		stub_tilt=(-0.018, 0.01),
	),
}


class Hive:
	def __init__(self, name, spec):
		self.name = name
		self.s = spec
		self.rng = np.random.default_rng(spec["seed"])
		self.bm = bmesh.new()
		self.uv = self.bm.loops.layers.uv.new("UVMap")
		self.paper_faces = []
		self.cap_centres = set()

	# t (0 top .. 1 bottom) -> z, axis centre
	def z_of(self, t):
		return self.s["z_top"] + (self.s["z_bot"] - self.s["z_top"]) * t

	def centre(self, t):
		lx, ly = self.s["lean"]
		k = t ** 1.6
		return lx * k, ly * k

	def ring_specs(self):
		"""(t, radius scale, z nudge) per ring: neck, tucked/mid/lip per band, rounded bottom."""
		rings = [(0.0, 1.0, 0.0), (0.045, 1.0, 0.0)]
		b = self.s["bands"]
		for i in range(len(b) - 1):
			t0, t1 = b[i], b[i + 1]
			rings.append((t0, 0.88, -0.012))                    # tucked under the lip above
			if t1 - t0 > 0.125 or self.rng.random() < 0.55:
				rings.append(((t0 + t1) * 0.5 + self.rng.uniform(-0.02, 0.02), 0.97, 0.0))
			rings.append((t1, 1.05, 0.0))                      # this band's lip
		rings.append((b[-1], 0.88, -0.012))
		for t, scale in self.s["bottom"]:
			rings.append((t, scale, 0.0))
		return rings

	def build(self):
		s = self.s
		segs = s["segs"]
		step = 2 * math.pi / segs
		angles = [i * step + self.rng.uniform(-0.3, 0.3) * step for i in range(segs)]
		amp1, f1, amp2, f2 = s["lump"]
		ph1, ph2 = self.rng.uniform(0, 2 * math.pi, 2)
		grid = []
		meta = []
		for t, scale, dz in self.ring_specs():
			r = lerp_profile(s["profile"], t) * scale
			cx, cy = self.centre(t)
			z = self.z_of(t) + dz
			twist = self.rng.uniform(-0.12, 0.12) * step
			row = []
			for a in angles:
				aa = a + twist
				lump = 1.0 + amp1 * math.sin(aa * f1 + ph1 + t * 2.0) + amp2 * math.sin(aa * f2 + ph2 - t * 3.0)
				rr = r * lump * (1.0 + self.rng.uniform(-0.05, 0.05))
				jz = 0.009 if scale > 1.0 else 0.004          # lips undulate, so the ridges are not lathe-perfect
				zz = z + self.rng.uniform(-jz, jz)
				row.append(self.bm.verts.new((cx + math.cos(aa) * rr, cy + math.sin(aa) * rr, zz)))
			grid.append(row)
			meta.append((cx, cy))
		self.grid = grid

		for k in range(len(grid) - 1):
			for j in range(segs):
				q = [grid[k][j], grid[k][(j + 1) % segs], grid[k + 1][(j + 1) % segs], grid[k + 1][j]]
				f = self.bm.faces.new(q)
				self.paper_faces.append(f)
		# neck cap (the stub sinks into it) and the bottom point / drip tip
		top_c = self.bm.verts.new((meta[0][0], meta[0][1], self.z_of(0.0) + 0.006))
		bx, by = self.centre(1.0)
		bot_c = self.bm.verts.new((bx + self.rng.uniform(-0.01, 0.01), by, self.z_of(1.0) - s["tip"]))
		self.cap_centres = {top_c, bot_c}
		for j in range(segs):
			self.paper_faces.append(self.bm.faces.new([grid[0][(j + 1) % segs], grid[0][j], top_c]))
			self.paper_faces.append(self.bm.faces.new([grid[-1][j], grid[-1][(j + 1) % segs], bot_c]))

	def paper_uv(self):
		"""Cylindrical map: u = angle around the local axis (tiles), v = height down the paper rows."""
		z0 = self.z_of(0.0) + 0.01
		z1 = self.z_of(1.0) - self.s["tip"] - 0.01
		for f in self.paper_faces:
			if not f.is_valid:
				continue
			us, vs = [], []
			for loop in f.loops:
				co = loop.vert.co
				t = min(max((z0 - co.z) / (z0 - z1), 0.0), 1.0)
				cx, cy = self.centre(t)
				us.append((math.atan2(co.y - cy, co.x - cx) / (2 * math.pi)) % 1.0)
				vs.append(1.0 - (0.5 + t * (PAPER_ROWS - 1)) / TEX)
			if max(us) - min(us) > 0.5:
				us = [u + 1.0 if u < 0.5 else u for u in us]
			# fan triangles: the axis vertex takes the mean u of its two rim neighbours
			for i, loop in enumerate(f.loops):
				if loop.vert in self.cap_centres:
					others = [u for k, u in enumerate(us) if k != i]
					us[i] = sum(others) / len(others)
			for loop, u, v in zip(f.loops, us, vs):
				loop[self.uv].uv = (u, v)

	def cut_entrance(self):
		"""Inset two stacked band faces and push them into the nest as a dark funnel."""
		s = self.s
		segs = s["segs"]
		# ring index of the chosen band's tucked ring: walk ring_specs order again
		specs = self.ring_specs_cache
		band_t = s["bands"][s["hole_band"]]
		k_tuck = next(i for i, (t, sc, dz) in enumerate(specs) if abs(t - s["bands"][s["hole_band"] - 1]) < 1e-9 and sc < 0.9)
		k_lip = next(i for i, (t, sc, dz) in enumerate(specs) if abs(t - band_t) < 1e-9 and sc > 1.0)
		j = s["hole_seg"]
		faces = []
		for k in range(k_tuck, k_lip):
			faces.append(self.paper_faces[k * segs + j])
		res = bmesh.ops.inset_region(self.bm, faces=faces, thickness=0.013, depth=0.0, use_even_offset=True)
		rim = list({v for f in faces for v in f.verts})
		hc = sum((v.co for v in rim), Vector()) / len(rim)
		for v in rim:
			corner = sum(1 for f in v.link_faces if f in faces) == 1
			if corner:
				v.co = hc + (v.co - hc) * 0.72
		ext = bmesh.ops.extrude_face_region(self.bm, geom=faces)
		new_verts = [g for g in ext["geom"] if isinstance(g, bmesh.types.BMVert)]
		bmesh.ops.delete(self.bm, geom=faces, context="FACES")
		c = Vector((0, 0, 0))
		for v in new_verts:
			c += v.co
		c /= len(new_verts)
		t = (self.z_of(0.0) - c.z) / (self.z_of(0.0) - self.z_of(1.0))
		cx, cy = self.centre(t)
		out = Vector((c.x - cx, c.y - cy, 0.0)).normalized()
		for v in new_verts:
			v.co = c + (v.co - c) * 0.55 - out * 0.075 + Vector((0, 0, 0.01))
		dark = set()
		for v in new_verts:
			for f in v.link_faces:
				dark.add(f)
		for f in dark:
			uv = region_uv(self.rng, "hole")
			for loop in f.loops:
				loop[self.uv].uv = (uv[0] + (self.rng.random() - 0.5) * 0.02, uv[1] + (self.rng.random() - 0.5) * 0.02)

	def build_stub(self):
		"""Hexagonal twig stub from the origin (top) down into the neck, slightly bent."""
		tx, ty = self.s["stub_tilt"]
		# top at the origin, a small kink sideways halfway, buried end on the nest axis
		rings = [(0.0, 0.02, 0.0, 0.0), (-0.03, 0.018, tx, ty), (-0.068, 0.016, 0.0, 0.0)]
		n = 6
		off = self.rng.uniform(0, math.pi)
		grid = []
		for z, r, ox, oy in rings:
			row = []
			for i in range(n):
				a = off + 2 * math.pi * i / n + self.rng.uniform(-0.2, 0.2)
				rr = r * (1.0 + self.rng.uniform(-0.12, 0.12))
				row.append(self.bm.verts.new((ox + math.cos(a) * rr, oy + math.sin(a) * rr, z)))
			grid.append(row)
		top_c = self.bm.verts.new((0.0, 0.0, 0.0))
		bot_c = self.bm.verts.new((0.0, 0.0, rings[-1][0] - 0.004))
		faces = []
		for k in range(len(grid) - 1):
			for i in range(n):
				faces.append(self.bm.faces.new([grid[k][i], grid[k][(i + 1) % n], grid[k + 1][(i + 1) % n], grid[k + 1][i]]))
		for i in range(n):
			faces.append(self.bm.faces.new([grid[0][(i + 1) % n], grid[0][i], top_c]))
			faces.append(self.bm.faces.new([grid[-1][i], grid[-1][(i + 1) % n], bot_c]))
		for f in faces:
			uv = region_uv(self.rng, "bark")
			for loop in f.loops:
				loop[self.uv].uv = (uv[0] + (self.rng.random() - 0.5) * 0.03, uv[1] + (self.rng.random() - 0.5) * 0.03)

	def to_object(self, material):
		self.ring_specs_cache = None
		bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
		# triangulate here so the engine gets exactly these diagonals (its own split of the non-planar quads
		# folded triangles round the entrance and left see-through slivers)
		bmesh.ops.triangulate(self.bm, faces=self.bm.faces[:], quad_method='BEAUTY', ngon_method='BEAUTY')
		me = bpy.data.meshes.new(self.name)
		self.bm.to_mesh(me)
		self.bm.free()
		me.update()
		me.materials.append(material)
		obj = bpy.data.objects.new(self.name, me)
		bpy.context.scene.collection.objects.link(obj)
		return obj


def make_hive(name, spec, material):
	h = Hive(name, spec)
	# ring_specs uses the rng; cache one draw so build() and cut_entrance() agree
	specs = h.ring_specs()
	h.ring_specs = lambda: specs
	h.ring_specs_cache = specs
	h.build()
	h.paper_uv()
	h.cut_entrance()
	h.build_stub()
	return h.to_object(material)


# ---------------------------------------------------------------- files
VMDL = """<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps = [  ]
						use_global_default = true
						global_default_material = "{mat}"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						filename = "{fbx}"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 0.4
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = false
							exception_list = [  ]
						}}
					}},
				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""

VMAT = """Layer0
{
	shader "shaders/pixel_lit.shader"

	PixelRoughness "0.950"
	PixelSpecular "0.300"
	PixelFlatShading "1.000"
	TextureColor "materials/environment/cloverHills/resources/beehive.png"
}
"""


def export_fbx(obj, path):
	loc = tuple(obj.location)
	obj.location = (0.0, 0.0, 0.0)
	for o in bpy.data.objects:
		o.select_set(False)
	obj.select_set(True)
	bpy.context.view_layer.objects.active = obj
	bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'MESH'}, apply_unit_scale=True,
							 global_scale=1.0, axis_forward='-Z', axis_up='Y', mesh_smooth_type='FACE',
							 add_leaf_bones=False, bake_anim=False, path_mode='STRIP', embed_textures=False)
	obj.location = loc


def main():
	bpy.ops.wm.read_factory_settings(use_empty=True)
	bpy.context.scene.unit_settings.system = 'METRIC'
	bpy.context.scene.unit_settings.scale_length = 1.0
	os.makedirs(MODEL_DIR, exist_ok=True)
	os.makedirs(MAT_DIR, exist_ok=True)

	png_path = os.path.join(MAT_DIR, MAT_NAME + ".png")
	write_png(png_path, make_texture())
	with open(os.path.join(MAT_DIR, MAT_NAME + ".vmat"), "w", newline="\n") as f:
		f.write(VMAT)

	material = bpy.data.materials.new(MAT_NAME)
	material.use_nodes = True
	bsdf = material.node_tree.nodes.get("Principled BSDF")
	img = bpy.data.images.load(png_path)
	tex = material.node_tree.nodes.new("ShaderNodeTexImage")
	tex.image = img
	tex.interpolation = 'Closest'
	if bsdf is not None:
		material.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])

	for i, (name, spec) in enumerate(VARIANTS.items()):
		obj = make_hive(name, spec, material)
		export_fbx(obj, os.path.join(MODEL_DIR, name + ".fbx"))
		with open(os.path.join(MODEL_DIR, name + ".vmdl"), "w", newline="\n") as f:
			f.write(VMDL.format(mat=f"{MAT_REL}/{MAT_NAME}.vmat", fbx=f"{MODEL_REL}/{name}.fbx"))
		obj.location = (i * 0.6, 0.0, 0.0)
		me = obj.data
		xs = [v.co.x for v in me.vertices]; ys = [v.co.y for v in me.vertices]; zs = [v.co.z for v in me.vertices]
		tris = sum(len(p.vertices) - 2 for p in me.polygons)
		print(f"[beehive] {name}: {len(me.vertices)} verts, {tris} tris, x {min(xs):.3f}..{max(xs):.3f} "
			  f"y {min(ys):.3f}..{max(ys):.3f} z {min(zs):.3f}..{max(zs):.3f} m")

	os.makedirs(os.path.dirname(BLEND_PATH), exist_ok=True)
	img.pack()
	bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
	print(f"[beehive] saved {BLEND_PATH}")


main()
