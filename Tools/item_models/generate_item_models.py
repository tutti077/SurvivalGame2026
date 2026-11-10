"""Generate the low-poly item models (resources, tools, weapons, armor, plants, seeds, trophies).

    python Tools/item_models/generate_item_models.py              # everything
    python Tools/item_models/generate_item_models.py stone rope   # just these
    python Tools/item_models/generate_item_models.py --category weapons

Pure Python (numpy + Pillow), no Blender: every item is built from faceted primitives in
Tools/item_models/itemgen/items_*.py, textured from a 4 x 4 grid of 64 px material cells and written
to Assets/models/<category>/<name>/<name>.obj + .vmdl + .png + .vmat (pixel_lit, point sampled).
A software render of each model goes to Tools/item_models/preview/<name>.png and a contact sheet to
Tools/item_models/preview/sheet_<category>.png so the result can be inspected without the editor.

Frames: resources / armor / plants / seeds / trophies are centred on X,Y standing on z = 0; held
tools and weapons have the handle along +X with the butt at the origin (Code/Player/HeldModelFit.cs).
Metres authored Z-up, written as a Y-up OBJ; the vmdl's import_scale 40 gives 40 units per metre.
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

from itemgen import export, preview  # noqa: E402
from itemgen import items_resources, items_held, items_wearables  # noqa: E402
from itemgen import items_nature, items_fish, items_metal, items_materials, items_heads, items_stations, items_husbandry  # noqa: E402

SEED = 2026


MODULES = [items_resources, items_held, items_wearables, items_nature, items_fish, items_metal, items_materials, items_heads, items_stations, items_husbandry]


def all_items():
	out = []
	for mod in MODULES:
		for it in mod.ITEMS:
			it["module"] = mod.__name__.split(".")[-1]
			out.append(it)
	return out


def main(argv):
	# Flag values are dropped by position, so "badger_claws --sheet badger_claws" still builds just that item.
	flag_values = {i + 1 for i, a in enumerate(argv) if a in ("--sheet", "--module", "--category")}
	names = [a for i, a in enumerate(argv) if not a.startswith("--") and i not in flag_values]
	cat = None
	sheet_name = None
	if "--sheet" in argv:  # one contact sheet of just the built items: preview/sheet_<sheet_name>.png
		sheet_name = argv[argv.index("--sheet") + 1]
	if "--module" in argv:  # every item of one itemgen module, e.g. --module items_fish
		mod = argv[argv.index("--module") + 1]
		names = names + [i["name"] for i in all_items() if i.get("module") == mod]
	if "--category" in argv:
		cat = argv[argv.index("--category") + 1]
	items = all_items()
	if names:
		items = [i for i in items if i["name"] in names]
	if cat:
		items = [i for i in items if i["category"] == cat]
	prev_dir = os.path.join(HERE, "preview")
	os.makedirs(prev_dir, exist_ok=True)
	sheets = {}
	for it in items:
		rng = np.random.default_rng(SEED + sum(map(ord, it["name"])))
		mesh, cells = it["build"](rng)
		mesh.drop_degenerate()
		out_dir, img = export.export_item(ROOT, it["category"], it["name"], mesh, cells, rng, surface=it["surface"],
										 roughness=it["roughness"], specular=it["specular"],
										 import_scale=it.get("import_scale", export.IMPORT_SCALE))
		lo, hi = mesh.bounds()
		size = hi - lo
		print(f"{it['category']:10s} {it['name']:24s} {len(mesh.tris):5d} tris  {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} m")
		im = preview.render(mesh, img)
		im.save(os.path.join(prev_dir, f"{it['name']}.png"))
		sheets.setdefault(sheet_name or it["category"], []).append((f"{it['name']}  {size.max():.2f} m", im))
	for cat_name, entries in sheets.items():
		preview.contact_sheet(entries).save(os.path.join(prev_dir, f"sheet_{cat_name}.png"))
	print(f"{len(items)} models written; previews in {prev_dir}")


if __name__ == "__main__":
	main(sys.argv[1:])
