"""Re-point every environment_elm<N>_v<OLD> prefab instance in a .scene file at the v<NEW> prefabs.

create_elm_tree.py deletes the previous version's prefabs when it regenerates, which orphans every
instance placed in a scene (they render the error checkerboard and the console reports the prefab as
missing). This rewrites those instances in place, keeping their transforms and overrides:

    python Blender/scripts/swap_elm_scene_version.py Assets/scenes/testscene1.scene 60 61

Run it with the s&box editor CLOSED (the editor autosaves open scenes over the file). Each instance
holds the prefab path, the prefab asset guid (from the .prefab.meta next to it; one is written if the
editor has not created it yet) and property overrides targeted at the prefab's root object guid - all
three are swapped. The trees' sapling, log and half-log prefabs are left alone.
"""
import json, os, re, sys, uuid

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
PREFAB_DIR = os.path.join(ROOT, "Assets", "prefabs", "environment", "tests")


def prefab_ids(n, ver):
	path = os.path.join(PREFAB_DIR, f"environment_elm{n}_v{ver}.prefab")
	with open(path, encoding="utf-8") as f:
		root_guid = json.load(f)["RootObject"]["__guid"]
	meta = path + ".meta"
	if os.path.exists(meta):
		asset_guid = json.load(open(meta, encoding="utf-8"))["guid"]
	else:
		asset_guid = str(uuid.uuid4())
		with open(meta, "w", encoding="utf-8", newline="\n") as f:
			f.write('{\n  "guid": "%s"\n}' % asset_guid)
		print(f"wrote {os.path.basename(meta)}")
	return root_guid, asset_guid


def main(scene_path, old, new):
	text = open(scene_path, encoding="utf-8").read()
	swapped = 0
	for n in range(1, 7):
		old_path = f"prefabs/environment/tests/environment_elm{n}_v{old}.prefab"
		if old_path not in text:
			continue
		new_root, new_asset = prefab_ids(n, new)
		# every instance block: "__Prefab": { "Id": ..., "Path": old_path }, then its __PrefabInstancePatch
		pat = re.compile(r'"__Prefab": \{\s*"Id": "([0-9a-f-]+)",\s*"Path": "%s"\s*\}' % re.escape(old_path))
		# old root guid = the Target IdValue used by the overrides of these instances (same for every instance)
		m = re.search(re.escape(old_path) + r'.*?"IdValue": "([0-9a-f-]+)"', text, re.S)
		old_root = m.group(1) if m else None
		count = len(pat.findall(text))
		text = pat.sub('"__Prefab": {\n        "Id": "%s",\n        "Path": "prefabs/environment/tests/environment_elm%d_v%d.prefab"\n      }' % (new_asset, n, new), text)
		if old_root:
			text = text.replace(f'"IdValue": "{old_root}"', f'"IdValue": "{new_root}"')
		text = text.replace(f'"environment_elm{n}_v{old}', f'"environment_elm{n}_v{new}')
		swapped += count
		print(f"elm{n}: {count} instance(s) -> v{new}")
	with open(scene_path, "w", encoding="utf-8", newline="\n") as f:
		f.write(text)
	print(f"swapped {swapped} instances in {os.path.relpath(scene_path, ROOT)}")


if __name__ == "__main__":
	main(sys.argv[1], int(sys.argv[2]), int(sys.argv[3]))
