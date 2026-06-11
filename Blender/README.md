# Blender scripts

Python scripts for generating or editing assets outside the s&box editor.

## Running a script

**Inside Blender**

1. Open Blender → **Scripting** workspace.
2. **Text → Open** and pick a file from `scripts/`.
3. Click **Run Script** (or Alt+P).

**From the command line** (headless)

```bash
blender --background --python "Blender/scripts/create_low_poly_rock.py"
```

Exported meshes can be saved as `.fbx` / `.gltf` from Blender and imported into your s&box asset pipeline.

## Scripts

| Script | Description |
|--------|-------------|
| `scripts/create_low_poly_rock.py` | Procedural low-poly rock mesh (~80 triangles, flat shaded) |
