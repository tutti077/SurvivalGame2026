# -*- coding: utf-8 -*-
import io, os, sys
ROOT = os.path.expanduser("~/mnt/SurvivalGame2026")
errs = []; ch = []

def edit(rel, pairs):
    p = os.path.join(ROOT, rel)
    s = io.open(p, "r", encoding="utf-8", newline="").read()
    nl = "\r\n" if "\r\n" in s else "\n"; o0 = s
    for old, new in pairs:
        o = old.replace("\n", nl); n = new.replace("\n", nl); c = s.count(o)
        if c != 1:
            errs.append("%s: matched %d\n%s" % (rel, c, old[:200])); return
        s = s.replace(o, n)
    if s != o0:
        io.open(p, "w", encoding="utf-8", newline="").write(s); ch.append(rel)

OLD_AGENTS = """## Commandment #4 — Meters are engine units (1:1)

**1 world unit = 1 meter** for design, scenes, and designer-facing properties. When the user says **10m** or **100m**, use **10** or **100** in transforms, scales, radii, and `[Property]` values — **not** a value scaled by `UnitsPerMeter` (40) or `TerrainWorldUnits.MetersToEngine`.

| User says | Use in engine |
|-----------|----------------|
| 10 m sun/moon disk | scale / `DiskScale` = **10** |
| 100×100 m ground | `Scale` **100,100,…** on a 1 m base mesh |
| 1500 m star shell | `ShellRadiusMeters` = **1500** |

**Do not** divide or multiply user meter numbers “for engine units” on environment, scene, fly-cam, sky, or new world features — that produced wrong sizes (e.g. **40×40** when the user asked for **100×100**).

**Legacy:** `TerrainWorldUnits` / `BuildModuleDimensions.UnitsPerMeter` may still apply only at **terrain chunk mesh** boundaries. Do not spread that conversion into unrelated systems.

Enforced via **`.cursor/rules/meters-are-engine-units.mdc`**."""

NEW_AGENTS = """## Commandment #4 — Meters are the design language; convert once, at the read site

**Designer-facing values are written in meters.** A property named `…Meters` holds exactly the number the user said: 100 m → **100**. Never pre-scale that number in the property, in JSON, or in a scene transform you type by hand.

**The world is not 1:1.** s&box inherits Source units, and this project has two conversion factors:

| Space | Factor | Converter |
|-------|--------|-----------|
| Terrain, environment, sky, fly-cam, harvest, containers, grapple | **40 u/m** | `TerrainWorldUnits.MetersToEngine` |
| Pawn distances (shove dash, bow range, wingsuit) | **≈40 u/m** | `BodyHeight / 1.8` — Citizen `BodyHeight` 72 ≈ 1.8 m |
| Build pieces only — snap math and piece colliders | **50 u/m** | `BuildColliderSnap.PrefabColliderSize` |

Convert **once**, inside the system that owns the value, and never carry a converted number across a system boundary. A value that has already been converted (a mesh AABB, a `BuildPieceModelCache` extent) is in final world units — do not scale it again.

| User says | Property value | What the read site produces |
|-----------|----------------|------------------------------|
| 1500 m star shell | `ShellRadiusMeters` = **1500** | 60,000 units via `MetersToEngine` |
| 1 m shove dash | `ShoveDashMeters` = **1** | ≈40 units via `BodyHeight / 1.8` |
| 2 m wall module | `ModuleMeters` = **2** | 100 units via `PrefabColliderSize` |

**Open item:** build's 50 u/m is the odd one out. Unifying it to 40 would move every snap point in the kit, so it stays until that migration is done deliberately.

Enforced via **`.cursor/rules/meters-are-engine-units.mdc`**."""

edit("AGENTS.md", [(OLD_AGENTS, NEW_AGENTS)])

OLD_MDC_TITLE = """# Meters = engine units (Commandment #4)

SurvivalGame2026 treats **1 world unit as 1 meter**. When the user says **10m**, **100m**, or **“100×100 plane”**, use **10**, **100**, or scale **(100, 100, …)** in scene transforms, colliders, orbit radii, disk sizes, and designer-facing properties — **not** a converted value."""

NEW_MDC_TITLE = """# Meters are the design language (Commandment #4)

SurvivalGame2026 writes **designer-facing values in meters**: a property named `…Meters` holds exactly the number the user said. **10m** → `10`, **100m** → `100`. Never pre-scale that number in the property, in JSON, or in a hand-typed scene transform.

The world itself is **not** 1:1 — s&box inherits Source units. Conversion happens **once**, inside the system that owns the value:

| Space | Factor | Converter |
|-------|--------|-----------|
| Terrain, environment, sky, fly-cam, harvest, containers, grapple | 40 u/m | `TerrainWorldUnits.MetersToEngine` |
| Pawn distances (shove, bow range, wingsuit) | ≈40 u/m | `BodyHeight / 1.8` |
| Build pieces only — snap math, piece colliders | **50 u/m** | `BuildColliderSnap.PrefabColliderSize` |

A number that has already been converted — a mesh AABB, a `BuildPieceModelCache` extent, a `BoxCollider.Scale` — is in final world units. **Do not scale it a second time.**"""

edit(".cursor/rules/meters-are-engine-units.mdc", [(OLD_MDC_TITLE, NEW_MDC_TITLE)])

print("CHANGED:", *ch, sep="\n  ")
if errs:
    print("ERRORS:"); [print(e) for e in errs]; sys.exit(1)
