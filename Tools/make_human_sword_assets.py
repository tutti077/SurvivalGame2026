"""Generate the project's sword-capable human model + animgraph from the engine's citizen_human sources.

Produces (all under Assets/models/player/):
  human_male_sword.vmdl                 clone of citizen_human_male.vmdl + a "99_Sword" animation folder
                                        (our clips in Assets/models/player/animations/) and our graph
  animgraphs/human_sword.vanmgrph       clone of human.vanmgrph: holdtypes subgraph path patched,
                                        two int parameters added: sword_pose, sword_attack
  animgraphs/holdtypes_sword.vsubgrph   clone of holdtypes.vsubgrph: holdtype_melee path patched
  animgraphs/holdtype_melee_sword.vsubgrph
                                        clone of holdtype_melee.vsubgrph: the single melee pose node
                                        becomes a selector on sword_pose (one child per pose clip), the
                                        attack node a selector on sword_attack (one child per strike clip)

Re-run after an engine update that changes the human graph:  python Tools/make_human_sword_assets.py
The engine sources are read from the s&box install; nothing there is modified.
"""
import os
import re
import sys

ENGINE = r"M:/SteamLibrary/steamapps/common/sbox/addons/citizen/Assets/models/citizen_human"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT_MODEL_DIR = os.path.join(ROOT, "Assets", "models", "player")
OUT_GRAPH_DIR = os.path.join(OUT_MODEL_DIR, "animgraphs")
CLIP_DIR_REL = "models/player/animations"

# Pose clips in sword_pose order (PlayerAnimation.SwordPose must match).
POSE_CLIPS = [
    ("Sword_Cocked_L", "human@sword_cocked_l.fbx"),
    ("Sword_Rest_L", "human@sword_rest_l.fbx"),
    ("Sword_Rest_R", "human@sword_rest_r.fbx"),
    ("Sword_Cocked_R", "human@sword_cocked_r.fbx"),
    ("Sword_Cocked_Over", "human@sword_cocked_over.fbx"),
    ("Sword_Cocked_Stab", "human@sword_cocked_stab.fbx"),
]
# Strike clips in sword_attack order (PlayerAnimation.SwordAttack must match).
ATTACK_CLIPS = [
    ("Sword_Attack_R", "human@sword_attack_r.fbx"),
    ("Sword_Attack_L", "human@sword_attack_l.fbx"),
    ("Sword_Attack_Over", "human@sword_attack_over.fbx"),
    ("Sword_Attack_Stab", "human@sword_attack_stab.fbx"),
]

# Fixed ids (must stay stable: the C# side sets the parameters by NAME, ids only link graph files).
PARAM_SWORD_POSE_ID = 1860011001
PARAM_SWORD_ATTACK_ID = 1860011002
POSE_NODE_ID = 257497782      # existing "Melee_Weapons_2H_Pose_Standing_01" sequence node -> selector
ATTACK_NODE_ID = 967987431    # existing "Melee_Weapons_2H_Attack_01" sequence node -> selector
NEW_NODE_BASE = 1860012000    # new child sequence nodes: base + index

INVALID_ID = 4294967295


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", os.path.relpath(path, ROOT))


def replace_once(text, old, new, what):
    n = text.count(old)
    if n != 1:
        sys.exit(f"{what}: expected exactly one match, found {n}")
    return text.replace(old, new)


def find_node_block(text, node_id):
    """Return (start, end) of the `{ key = { m_id = X } value = { ... } },` entry for a node."""
    m = re.search(r"\n(\t+)\{\n\t+key = \n\t+\{\n\t+m_id = %d\n\t+\}\n\t+value = \n" % node_id, text)
    if m is None:
        sys.exit(f"node {node_id} not found")
    start = m.start() + 1
    indent = m.group(1)
    end_marker = f"\n{indent}}},\n"
    end = text.find(end_marker, m.end())
    if end < 0:
        sys.exit(f"node {node_id}: end not found")
    return start, end + len(end_marker)


def sequence_node(node_id, name, seq, pos, loop):
    return f"""\t\t\t{{
\t\t\t\tkey =
\t\t\t\t{{
\t\t\t\t\tm_id = {node_id}
\t\t\t\t}}
\t\t\t\tvalue =
\t\t\t\t{{
\t\t\t\t\t_class = "CSequenceAnimNode"
\t\t\t\t\tm_sName = "{name}"
\t\t\t\t\tm_vecPosition = [ {pos[0]:.1f}, {pos[1]:.1f} ]
\t\t\t\t\tm_nNodeID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {node_id}
\t\t\t\t\t}}
\t\t\t\t\tm_sNote = ""
\t\t\t\t\tm_tagSpans = [  ]
\t\t\t\t\tm_sequenceName = "{seq}"
\t\t\t\t\tm_playbackSpeed = 1.0
\t\t\t\t\tm_bLoop = {"true" if loop else "false"}
\t\t\t\t}}
\t\t\t}},
"""


def selector_node(node_id, name, pos, child_ids, param_id, blend, reset_on_change):
    children = "".join(
        f"""\t\t\t\t\t\t{{
\t\t\t\t\t\t\tm_nodeID =
\t\t\t\t\t\t\t{{
\t\t\t\t\t\t\t\tm_id = {cid}
\t\t\t\t\t\t\t}}
\t\t\t\t\t\t\tm_outputID =
\t\t\t\t\t\t\t{{
\t\t\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t\t\t}}
\t\t\t\t\t\t}},
"""
        for cid in child_ids
    )
    tags = "".join(
        f"""\t\t\t\t\t\t{{
\t\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t\t}},
"""
        for _ in child_ids
    )
    return f"""\t\t\t{{
\t\t\t\tkey =
\t\t\t\t{{
\t\t\t\t\tm_id = {node_id}
\t\t\t\t}}
\t\t\t\tvalue =
\t\t\t\t{{
\t\t\t\t\t_class = "CSelectorAnimNode"
\t\t\t\t\tm_sName = "{name}"
\t\t\t\t\tm_vecPosition = [ {pos[0]:.1f}, {pos[1]:.1f} ]
\t\t\t\t\tm_nNodeID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {node_id}
\t\t\t\t\t}}
\t\t\t\t\tm_sNote = "Generated by Tools/make_human_sword_assets.py"
\t\t\t\t\tm_children =
\t\t\t\t\t[
{children}\t\t\t\t\t]
\t\t\t\t\tm_tags =
\t\t\t\t\t[
{tags}\t\t\t\t\t]
\t\t\t\t\tm_selectionSource = "SelectionSource_Int"
\t\t\t\t\tm_boolParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t}}
\t\t\t\t\tm_enumParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t}}
\t\t\t\t\tm_intParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {param_id}
\t\t\t\t\t}}
\t\t\t\t\tm_intParamMinValue = 0
\t\t\t\t\tm_intParamMaxValue = {len(child_ids) - 1}
\t\t\t\t\tm_blendDuration = {blend}
\t\t\t\t\tm_tagBehavior = "SelectorTagBehavior_OnWhileCurrent"
\t\t\t\t\tm_bResetOnChange = {"true" if reset_on_change else "false"}
\t\t\t\t\tm_bSyncCyclesOnChange = false
\t\t\t\t\tm_blendCurve =
\t\t\t\t\t{{
\t\t\t\t\t\tm_vControlPoint1 = [ 0.314815, 0.0 ]
\t\t\t\t\t\tm_vControlPoint2 = [ 0.111111, 1.0 ]
\t\t\t\t\t}}
\t\t\t\t\tm_intParamNames = [  ]
\t\t\t\t}}
\t\t\t}},
"""


def int_param(name, pid, max_value, indent):
    t = "\t" * indent
    return f"""{t}{{
{t}\t_class = "CIntAnimParameter"
{t}\tm_name = "{name}"
{t}\tm_id =
{t}\t{{
{t}\t\tm_id = {pid}
{t}\t}}
{t}\tm_previewButton = "ANIMPARAM_BUTTON_NONE"
{t}\tm_bUseMostRecentValue = true
{t}\tm_bAutoReset = false
{t}\tm_defaultValue = 0
{t}\tm_minValue = 0
{t}\tm_maxValue = {max_value}
{t}}},
"""


def anim_file(name, fbx, looping, fade_in, fade_out):
    return f"""\t\t\t\t\t\t\t{{
\t\t\t\t\t\t\t\t_class = "AnimFile"
\t\t\t\t\t\t\t\tname = "{name}"
\t\t\t\t\t\t\t\tactivity_name = ""
\t\t\t\t\t\t\t\tactivity_weight = 1
\t\t\t\t\t\t\t\tweight_list_name = ""
\t\t\t\t\t\t\t\tfade_in_time = {fade_in}
\t\t\t\t\t\t\t\tfade_out_time = {fade_out}
\t\t\t\t\t\t\t\tlooping = {"true" if looping else "false"}
\t\t\t\t\t\t\t\tdelta = false
\t\t\t\t\t\t\t\tworldSpace = false
\t\t\t\t\t\t\t\thidden = false
\t\t\t\t\t\t\t\tanim_markup_ordered = false
\t\t\t\t\t\t\t\tdisable_compression = true
\t\t\t\t\t\t\t\tdisable_interpolation = false
\t\t\t\t\t\t\t\tenable_scale = false
\t\t\t\t\t\t\t\tsource_filename = "{CLIP_DIR_REL}/{fbx}"
\t\t\t\t\t\t\t\tstart_frame = -1
\t\t\t\t\t\t\t\tend_frame = -1
\t\t\t\t\t\t\t\tframerate = -1.0
\t\t\t\t\t\t\t\ttake = 0
\t\t\t\t\t\t\t\treverse = false
\t\t\t\t\t\t\t}},
"""


def make_root_graph():
    src = read(f"{ENGINE}/animgraphs/human.vanmgrph")
    out = replace_once(
        src,
        'm_subGraphFilename = "models\\\\citizen_human\\\\animgraphs\\\\subgraphs\\\\holdtypes.vsubgrph"',
        'm_subGraphFilename = "models\\\\player\\\\animgraphs\\\\holdtypes_sword.vsubgrph"',
        "root graph holdtypes path")
    # Add the two int params right after static_pose (a known CIntAnimParameter in the root list).
    m = re.search(r'(\t+)\{\n\t+_class = "CIntAnimParameter"\n\t+m_name = "static_pose"\n(?:.*\n)*?\1\},\n', out)
    if m is None:
        sys.exit("root graph: static_pose parameter not found")
    indent = len(m.group(1))
    params = int_param("sword_pose", PARAM_SWORD_POSE_ID, len(POSE_CLIPS) - 1, indent) \
        + int_param("sword_attack", PARAM_SWORD_ATTACK_ID, len(ATTACK_CLIPS) - 1, indent)
    out = out[:m.end()] + params + out[m.end():]
    write(f"{OUT_GRAPH_DIR}/human_sword.vanmgrph", out)


def make_holdtypes():
    src = read(f"{ENGINE}/animgraphs/subgraphs/holdtypes.vsubgrph")
    out = replace_once(
        src,
        'm_subGraphFilename = "models\\\\citizen_human\\\\animgraphs\\\\subgraphs\\\\holdtype_melee.vsubgrph"',
        'm_subGraphFilename = "models\\\\player\\\\animgraphs\\\\holdtype_melee_sword.vsubgrph"',
        "holdtypes melee path")
    write(f"{OUT_GRAPH_DIR}/holdtypes_sword.vsubgrph", out)


def make_holdtype_melee():
    src = read(f"{ENGINE}/animgraphs/subgraphs/holdtype_melee.vsubgrph")
    out = src

    # 1. Pose node -> selector on sword_pose with one sequence child per pose clip.
    s, e = find_node_block(out, POSE_NODE_ID)
    block = out[s:e]
    if 'm_sequenceName = "Melee_Weapons_2H_Pose_Standing_01"' not in block:
        sys.exit("pose node does not carry the expected sequence")
    pose_ids = [NEW_NODE_BASE + i for i in range(len(POSE_CLIPS))]
    pose_children = "".join(
        sequence_node(pid, name, name, (-2560.0, -512.0 + 96.0 * i), loop=True)
        for i, (pid, (name, _)) in enumerate(zip(pose_ids, POSE_CLIPS)))
    pose_selector = selector_node(POSE_NODE_ID, "Sword pose (sword_pose)", (-2208.0, -256.0), pose_ids,
                                  PARAM_SWORD_POSE_ID, blend=0.12, reset_on_change=False)
    out = out[:s] + pose_children + pose_selector + out[e:]

    # 2. Attack node -> selector on sword_attack with one sequence child per attack clip.
    s, e = find_node_block(out, ATTACK_NODE_ID)
    block = out[s:e]
    if 'm_sequenceName = "Melee_Weapons_2H_Attack_01"' not in block:
        sys.exit("attack node does not carry the expected sequence")
    attack_ids = [NEW_NODE_BASE + 100 + i for i in range(len(ATTACK_CLIPS))]
    attack_children = "".join(
        sequence_node(aid, name, name, (-1344.0, 384.0 + 96.0 * i), loop=False)
        for i, (aid, (name, _)) in enumerate(zip(attack_ids, ATTACK_CLIPS)))
    attack_selector = selector_node(ATTACK_NODE_ID, "Sword attack (sword_attack)", (-992.0, 480.0), attack_ids,
                                    PARAM_SWORD_ATTACK_ID, blend=0.0, reset_on_change=True)
    out = out[:s] + attack_children + attack_selector + out[e:]

    # 3. Declare the parameters locally (subgraphs mirror the root declarations they use).
    marker = "\t]\n\tm_localTags = [  ]\n}"
    params = int_param("sword_pose", PARAM_SWORD_POSE_ID, len(POSE_CLIPS) - 1, 2) \
        + int_param("sword_attack", PARAM_SWORD_ATTACK_ID, len(ATTACK_CLIPS) - 1, 2)
    out = replace_once(out, marker, params + marker, "subgraph parameter list end")

    write(f"{OUT_GRAPH_DIR}/holdtype_melee_sword.vsubgrph", out)


def make_model():
    src = read(f"{ENGINE}/citizen_human_male.vmdl")
    out = replace_once(src,
                       'anim_graph_name = "models/citizen_human/animgraphs/human.vanmgrph"',
                       'anim_graph_name = "models/player/animgraphs/human_sword.vanmgrph"',
                       "vmdl anim graph")
    files = "".join(anim_file(name, fbx, True, 0.15, 0.15) for name, fbx in POSE_CLIPS)
    files += "".join(anim_file(name, fbx, False, 0.0, 0.2) for name, fbx in ATTACK_CLIPS)
    folder = f"""\t\t\t\t\t{{
\t\t\t\t\t\t_class = "Folder"
\t\t\t\t\t\tname = "99_Sword"
\t\t\t\t\t\tnote = "SurvivalGame sword clips, authored in Blender (Blender/scripts/create_sword_anims.py). Generated by Tools/make_human_sword_assets.py."
\t\t\t\t\t\tchildren =
\t\t\t\t\t\t[
{files}\t\t\t\t\t\t]
\t\t\t\t\t}},
"""
    marker = '\t\t\t\t\t{\n\t\t\t\t\t\t_class = "Prefab"\n\t\t\t\t\t\ttarget_file = "models/citizen_human/prefabs/human_animationlist.vmdl_prefab"\n\t\t\t\t\t},\n'
    out = replace_once(out, marker, marker + folder, "vmdl animation list prefab")
    write(f"{OUT_MODEL_DIR}/human_male_sword.vmdl", out)


if __name__ == "__main__":
    for _, fbx in POSE_CLIPS + ATTACK_CLIPS:
        p = os.path.join(OUT_MODEL_DIR, "animations", fbx)
        if not os.path.exists(p):
            print("WARNING missing clip:", fbx)
    make_root_graph()
    make_holdtypes()
    make_holdtype_melee()
    make_model()
