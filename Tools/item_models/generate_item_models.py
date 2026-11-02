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

SEED = 2026


def all_items():
	return items_resources.ITEMS + items_held.ITEMS + items_wearables.ITEMS


def main(argv):
	names = [a for a in argv if not a.startswith("--")]
	cat = None
	if "--category" in argv:
		cat = argv[argv.index("--category") + 1]
		names = [n for n in names if n != cat]
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
										 roughness=it["roughness"], specular=it["specular"])
		lo, hi = mesh.bounds()
		size = hi - lo
		print(f"{it['category']:10s} {it['name']:24s} {len(mesh.tris):5d} tris  {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} m")
		im = preview.render(mesh, img)
		im.save(os.path.join(prev_dir, f"{it['name']}.png"))
		sheets.setdefault(it["category"], []).append((it["name"], im))
	for cat_name, entries in sheets.items():
		preview.contact_sheet(entries).save(os.path.join(prev_dir, f"sheet_{cat_name}.png"))
	print(f"{len(items)} models written; previews in {prev_dir}")


if __name__ == "__main__":
	main(sys.argv[1:])
