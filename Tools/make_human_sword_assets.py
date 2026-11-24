"""Generate the project's sword-capable human model + animgraph from the engine's citizen_human sources.

Produces (all under Assets/models/player/):
  human_male_sword.vmdl                 clone of citizen_human_male.vmdl + a "99_Sword" animation folder
                                        (our clips in Assets/models/player/animations/) and our graph
  animgraphs/human_sword.vanmgrph       clone of human.vanmgrph: holdtypes subgraph path patched,
                                        parameters added: sword_pose, sword_attack, sword_guard_dir (int),
                                        b_sword_interrupt, b_sword_guard (bool), sword_wind (float 0-1)
  animgraphs/holdtypes_sword.vsubgrph   clone of holdtypes.vsubgrph: holdtype_melee path patched
  animgraphs/holdtype_melee_sword.vsubgrph
                                        clone of holdtype_melee.vsubgrph: the single melee pose node
                                        becomes a selector on sword_pose (one child per pose clip), the
                                        attack node a selector on sword_attack (one child per strike clip),
                                        the guard a selector on sword_guard_dir (left / right / overhead);
                                        the attack state also exits early on b_sword_interrupt (a follow-up
                                        click re-cocks) or b_sword_guard (the guard goes up), cutting the
                                        strike's settle frames short

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

# Pose clips in sword_pose order (PlayerAnimation.SwordPose must match; EntityCombat reads it from there).
POSE_CLIPS = [
    ("Sword_Cocked_L", "human@sword_cocked_l.fbx"),
    ("Sword_Rest", "human@sword_rest.fbx"),           # the waist carry, whatever side the cursor aims (Mark 2026-11-17)
    ("Sword_Cocked_R", "human@sword_cocked_r.fbx"),
    ("Sword_Cocked_Over", "human@sword_cocked_over.fbx"),
    ("Sword_Swap_Mid", "human@sword_swap_mid.fbx"),   # a held heavy morphed across the face passes through this
]
# Wind-up clips (Mark 2026-11-05, Last Oasis reference): the drawn motion waist -> wind -> cocked. Their playhead is not
# time: a cycle-control node drives it from sword_wind, which PlayerAnimation sets to the windup's progress every frame,
# so the sword keeps moving through the whole windup and reaches the cocked frame as the strike fires, whatever the
# windup's length (Mark 2026-11-16: "the windup should be moving through the whole thing, right now its just snapping
# to poses mostly").
WIND_UP_CLIPS = {"Sword_Cocked_R", "Sword_Cocked_L", "Sword_Cocked_Over"}
# Guard poses: NOT in the sword_pose selector — a bool selector on b_sword_guard wraps that selector, so the guard
# shows in GUARD_BLEND_SECONDS while rest / cock / swap blends keep POSE_BLEND_SECONDS. One pose per guard direction in
# sword_guard_dir order = SwingDirs (Left 0, Right 1, Up 2); moving the held guard cross-fades in GUARD_DIR_BLEND_SECONDS
# (Mark 2026-11-14: "new animations for blocking left blocking right and kinda blend between them").
BLOCK_CLIPS = [
    ("Sword_Block_L", "human@sword_block_l.fbx"),     # blade standing up on the left
    ("Sword_Block_R", "human@sword_block_r.fbx"),     # blade standing up on the right
    ("Sword_Block_Up", "human@sword_block_up.fbx"),   # blade level in front of the head
]
# Plain looping sequences outside the graph, played by PlayerAnimation with UseAnimGraph off (like the dodge roll).
SEQUENCE_CLIPS = [("Wingsuit_Glide", "human@wingsuit_glide.fbx")]   # PlayerAnimation.Wingsuit.cs
# Strike clips in sword_attack order (PlayerAnimation.SwordAttack must match).
ATTACK_CLIPS = [
    ("Sword_Attack_R", "human@sword_attack_r.fbx"),
    ("Sword_Attack_L", "human@sword_attack_l.fbx"),
    ("Sword_Attack_Over", "human@sword_attack_over.fbx"),
]

# Fixed ids (must stay stable: the C# side sets the parameters by NAME, ids only link graph files).
PARAM_SWORD_POSE_ID = 1860011001
PARAM_SWORD_ATTACK_ID = 1860011002
PARAM_SWORD_INTERRUPT_ID = 1860011003
PARAM_SWORD_GUARD_ID = 1860011004
PARAM_SWORD_GUARD_DIR_ID = 1860011005
PARAM_SWORD_WIND_ID = 1860011006
IDLE_STATE_ID = 1228346111    # "Fists attack SM" Idle state (the attack state returns here)
POSE_NODE_ID = 257497782      # existing "Melee_Weapons_2H_Pose_Standing_01" sequence node -> guard selector
INNER_POSE_ID = 1860012300    # the sword_pose selector under it
BLOCK_SEL_ID = 1860012301     # sword_guard_dir selector (the guard selector's "true" child)
ATTACK_NODE_ID = 967987431    # existing "Melee_Weapons_2H_Attack_01" sequence node -> selector
NEW_NODE_BASE = 1860012000    # new child sequence nodes: base + index
DUCK_NODE_ID = 1420086429     # "Duck" blend: Standing / Crouching / CrouchingLow
STANDING_POSE_ID = 382273333  # its Standing input (the long-idle state machine over the stance selector)
CROUCH_POSE_ID = 439512559    # stock "Melee_Weapons_2H_Pose_Crouching_01" input (kept: hips and legs)
CROUCH_MASK_ID = NEW_NODE_BASE + 200   # new mask: stock crouch + upper body from the stance chain
ATTACK_EXIT_BLEND_SECONDS = 0.08       # attack -> idle blend (stock 0.3 s is the whole length of a 0.30 s strike)
# sword_pose cross-fade (rest <-> cocked, a heavy morph between cocked sides, through Sword_Swap_Mid across the face), at
# a steady rate. The aim side no longer moves the rest carry (Mark 2026-11-17), so no stance swaps run through it.
POSE_BLEND_SECONDS = 0.125
INTERRUPT_BLEND_SECONDS = 0.12         # attack -> idle when a follow-up click re-cocks (the 0.6 s one-handed windup has room; 0.05 popped)
# Guard up / down (the b_sword_guard selector, at a steady rate) and the cut from a strike into the guard: the same
# 0.15 s as moving a held guard (Mark 2026-11-17: "if you stand still and click block it raises the block instantly, but
# if we hold block and swap directions it goes as slow as it should"; was 0.05, Mark 2026-11-04: "block shows in 50 ms").
# PlayerCombat.BlockActivationSeconds is 0.15 too, so the guard works when the pose is up.
GUARD_BLEND_SECONDS = 0.15
GUARD_DIR_BLEND_SECONDS = 0.15         # held guard moving left / right / overhead (Mark 2026-11-16: "Blocks still take super long to go left right or up"; was 0.25, before that 0.1 "the blocks still snap poses")

INVALID_ID = 4294967295


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def assert_unique_node_ids(path, text):
    """A graph with two nodes on one id compiles, but the selectors then pick the wrong children (2026-11-14: the guard
    never changed side because its clips shared ids with the stance and guard-direction selectors)."""
    if not path.endswith((".vanmgrph", ".vsubgrph")):
        return
    ids = re.findall(r"m_nNodeID =\s*\{\s*m_id = (\d+)", text)
    dupes = sorted({i for i in ids if ids.count(i) > 1})
    if dupes:
        raise SystemExit(f"{os.path.basename(path)}: duplicate anim node ids {dupes}")


def write(path, text):
    assert_unique_node_ids(path, text)
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


def cycle_control_node(node_id, name, pos, input_id, param_id):
    """Drives its input sequence's playhead (cycle 0-1) from a float parameter instead of time."""
    return f"""\t\t\t{{
\t\t\t\tkey =
\t\t\t\t{{
\t\t\t\t\tm_id = {node_id}
\t\t\t\t}}
\t\t\t\tvalue =
\t\t\t\t{{
\t\t\t\t\t_class = "CCycleControlAnimNode"
\t\t\t\t\tm_sName = "{name}"
\t\t\t\t\tm_vecPosition = [ {pos[0]:.1f}, {pos[1]:.1f} ]
\t\t\t\t\tm_nNodeID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {node_id}
\t\t\t\t\t}}
\t\t\t\t\tm_sNote = ""
\t\t\t\t\tm_inputConnection =
\t\t\t\t\t{{
\t\t\t\t\t\tm_nodeID =
\t\t\t\t\t\t{{
\t\t\t\t\t\t\tm_id = {input_id}
\t\t\t\t\t\t}}
\t\t\t\t\t\tm_outputID =
\t\t\t\t\t\t{{
\t\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t\t}}
\t\t\t\t\t}}
\t\t\t\t\tm_valueSource = "Parameter"
\t\t\t\t\tm_param =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {param_id}
\t\t\t\t\t}}
\t\t\t\t}}
\t\t\t}},
"""


def selector_node(node_id, name, pos, child_ids, param_id, blend, reset_on_change, bool_param_id=None, linear=False):
    """Int selector on param_id, or (bool_param_id given) a bool selector: child 0 = false, child 1 = true. linear: the
    cross-fade runs at a constant rate (stance swaps, guard moves - Mark 2026-11-16: "animates all the frames at a
    constant rate"); otherwise the stock eased curve."""
    cp1, cp2 = ("0.333333, 0.333333", "0.666667, 0.666667") if linear else ("0.314815, 0.0", "0.111111, 1.0")
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
\t\t\t\t\tm_selectionSource = "{"SelectionSource_Bool" if bool_param_id else "SelectionSource_Int"}"
\t\t\t\t\tm_boolParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {bool_param_id or INVALID_ID}
\t\t\t\t\t}}
\t\t\t\t\tm_enumParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {INVALID_ID}
\t\t\t\t\t}}
\t\t\t\t\tm_intParamID =
\t\t\t\t\t{{
\t\t\t\t\t\tm_id = {INVALID_ID if bool_param_id else param_id}
\t\t\t\t\t}}
\t\t\t\t\tm_intParamMinValue = 0
\t\t\t\t\tm_intParamMaxValue = {len(child_ids) - 1}
\t\t\t\t\tm_blendDuration = {blend}
\t\t\t\t\tm_tagBehavior = "SelectorTagBehavior_OnWhileCurrent"
\t\t\t\t\tm_bResetOnChange = {"true" if reset_on_change else "false"}
\t\t\t\t\tm_bSyncCyclesOnChange = false
\t\t\t\t\tm_blendCurve =
\t\t\t\t\t{{
\t\t\t\t\t\tm_vControlPoint1 = [ {cp1} ]
\t\t\t\t\t\tm_vControlPoint2 = [ {cp2} ]
\t\t\t\t\t}}
\t\t\t\t\tm_intParamNames = [  ]
\t\t\t\t}}
\t\t\t}},
"""


def bone_mask_node(node_id, name, pos, base_id, masked_id, weight_list):
    """CBoneMaskAnimNode: input 1 everywhere, input 2 on the weight list's bones (same layout as the stock
    "Bone Mask (Only UpperBody)" node in holdtype_melee.vsubgrph)."""
    def conn(nid):
        return f"""					{{
						m_nodeID = 
						{{
							m_id = {nid}
						}}
						m_outputID = 
						{{
							m_id = {INVALID_ID}
						}}
					}}
"""
    return f"""			{{
				key = 
				{{
					m_id = {node_id}
				}}
				value = 
				{{
					_class = "CBoneMaskAnimNode"
					m_sName = "{name}"
					m_vecPosition = [ {pos[0]:.1f}, {pos[1]:.1f} ]
					m_nNodeID = 
					{{
						m_id = {node_id}
					}}
					m_sNote = "Generated by Tools/make_human_sword_assets.py"
					m_weightListName = "{weight_list}"
					m_inputConnection1 = 
{conn(base_id)}					m_inputConnection2 = 
{conn(masked_id)}					m_blendSpace = "BlendSpace_Parent"
					m_bUseBlendScale = false
					m_blendValueSource = "Parameter"
					m_blendParameter = 
					{{
						m_id = {INVALID_ID}
					}}
					m_timingBehavior = "UseChild1"
					m_flTimingBlend = 0.5
					m_flRootMotionBlend = 0.0
					m_footMotionTiming = "Child1"
					m_bResetChild1 = false
					m_bResetChild2 = false
				}}
			}},
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


def float_param(name, pid, indent):
    t = "\t" * indent
    return f"""{t}{{
{t}\t_class = "CFloatAnimParameter"
{t}\tm_name = "{name}"
{t}\tm_id =
{t}\t{{
{t}\t\tm_id = {pid}
{t}\t}}
{t}\tm_previewButton = "ANIMPARAM_BUTTON_NONE"
{t}\tm_bUseMostRecentValue = true
{t}\tm_bAutoReset = false
{t}\tm_fDefaultValue = 0.0
{t}\tm_fMinValue = 0.0
{t}\tm_fMaxValue = 1.0
{t}}},
"""


def bool_param(name, pid, indent):
    t = "\t" * indent
    return f"""{t}{{
{t}\t_class = "CBoolAnimParameter"
{t}\tm_name = "{name}"
{t}\tm_id =
{t}\t{{
{t}\t\tm_id = {pid}
{t}\t}}
{t}\tm_previewButton = "ANIMPARAM_BUTTON_NONE"
{t}\tm_bUseMostRecentValue = true
{t}\tm_bAutoReset = false
{t}\tm_bDefaultValue = false
{t}}},
"""


def interrupt_transition(param_id, blend):
    """Attack -> Idle as soon as the bool parameter is true (the next cocked pose / the block pose takes over)."""
    t = "\t" * 8
    return f"""{t}{{
{t}\t_class = "CAnimStateTransition"
{t}\tm_conditions =
{t}\t[
{t}\t\t{{
{t}\t\t\t_class = "CParameterAnimCondition"
{t}\t\t\tm_comparisonOp = 0
{t}\t\t\tm_paramID =
{t}\t\t\t{{
{t}\t\t\t\tm_id = {param_id}
{t}\t\t\t}}
{t}\t\t\tm_comparisonValue =
{t}\t\t\t{{
{t}\t\t\t\tm_nType = 1
{t}\t\t\t\tm_data = true
{t}\t\t\t}}
{t}\t\t}},
{t}\t]
{t}\tm_blendDuration = {blend}
{t}\tm_destState =
{t}\t{{
{t}\t\tm_id = {IDLE_STATE_ID}
{t}\t}}
{t}\tm_bReset = false
{t}\tm_resetCycleOption = "Beginning"
{t}\tm_flFixedCycleValue = 0.0
{t}\tm_bBlendCycle = false
{t}\tm_blendCurve =
{t}\t{{
{t}\t\tm_vControlPoint1 = [ 0.5, 0.0 ]
{t}\t\tm_vControlPoint2 = [ 0.5, 1.0 ]
{t}\t}}
{t}\tm_bForceFootPlant = false
{t}\tm_bDisabled = false
{t}\tm_bRandomTimeBetween = false
{t}\tm_flRandomTimeStart = 0.0
{t}\tm_flRandomTimeEnd = 0.0
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
        + int_param("sword_attack", PARAM_SWORD_ATTACK_ID, len(ATTACK_CLIPS) - 1, indent) \
        + int_param("sword_guard_dir", PARAM_SWORD_GUARD_DIR_ID, len(BLOCK_CLIPS) - 1, indent) \
        + bool_param("b_sword_interrupt", PARAM_SWORD_INTERRUPT_ID, indent) \
        + bool_param("b_sword_guard", PARAM_SWORD_GUARD_ID, indent) \
        + float_param("sword_wind", PARAM_SWORD_WIND_ID, indent)
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
    # A wind-up clip sits under a cycle-control node on sword_wind (the selector's child); its sequence node gets an
    # id of its own at +500 (+100 attack, +200 crouch mask, +300 / +301 selectors, +400 guard clips are taken).
    pose_children = ""
    for i, (pid, (name, _)) in enumerate(zip(pose_ids, POSE_CLIPS)):
        pos = (-2560.0, -512.0 + 96.0 * i)
        if name in WIND_UP_CLIPS:
            seq_id = NEW_NODE_BASE + 500 + i
            pose_children += sequence_node(seq_id, name, name, (pos[0] - 192.0, pos[1]), loop=False)
            pose_children += cycle_control_node(pid, f"{name} (sword_wind)", pos, seq_id, PARAM_SWORD_WIND_ID)
        else:
            pose_children += sequence_node(pid, name, name, pos, loop=True)
    pose_selector = selector_node(INNER_POSE_ID, "Sword pose (sword_pose)", (-2400.0, -256.0), pose_ids,
                                  PARAM_SWORD_POSE_ID, blend=POSE_BLEND_SECONDS, reset_on_change=True, linear=True)
    # +400: +300 / +301 are INNER_POSE_ID / BLOCK_SEL_ID - children there collided with both selectors (2026-11-14:
    # the guard never changed side).
    block_ids = [NEW_NODE_BASE + 400 + i for i in range(len(BLOCK_CLIPS))]
    block_children = "".join(
        sequence_node(bid, name, name, (-2560.0, 320.0 + 96.0 * i), loop=True)
        for i, (bid, (name, _)) in enumerate(zip(block_ids, BLOCK_CLIPS)))
    block_selector = selector_node(BLOCK_SEL_ID, "Guard direction (sword_guard_dir)", (-2400.0, -96.0), block_ids,
                                   PARAM_SWORD_GUARD_DIR_ID, blend=GUARD_DIR_BLEND_SECONDS, reset_on_change=False,
                                   linear=True)
    guard_selector = selector_node(POSE_NODE_ID, "Guard (b_sword_guard)", (-2208.0, -256.0),
                                   [INNER_POSE_ID, BLOCK_SEL_ID], INVALID_ID, blend=GUARD_BLEND_SECONDS,
                                   reset_on_change=False, bool_param_id=PARAM_SWORD_GUARD_ID, linear=True)
    out = out[:s] + pose_children + pose_selector + block_children + block_selector + guard_selector + out[e:]

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

    # 2b. Exit from the attack state: the stock transition blends back to idle over 0.3 s once the clip is "almost
    #     finished". With the 0.30 s strike clips that started on the first frame, so the idle stance bled into the
    #     whole strike (the chest jumped when the stance switched from cocked to rest). A short exit keeps the strike.
    m = re.search(r'(FinishedConditionOption_OnAlmostFinished"\n(?:[^\n]*\n){1,8}?\t+m_blendDuration = )0\.3\n', out)
    if m is None:
        sys.exit("attack state: almost-finished exit transition not found")
    out = out[:m.start()] + m.group(1) + f"{ATTACK_EXIT_BLEND_SECONDS}\n" + out[m.end():]

    # 2c. Early exits: b_sword_interrupt (a new swing is cocked) and b_sword_guard (the guard is up) leave the
    #     attack state at once, so the follow-up's cocked pose / the block pose replaces a strike's settle frames.
    a = out.find('m_name = "Attack"')
    t = out.rfind("m_transitions = ", 0, a)
    if a < 0 or t < 0:
        sys.exit("attack state: transitions list not found")
    open_bracket = out.find("[\n", t) + 2
    out = out[:open_bracket] + interrupt_transition(PARAM_SWORD_GUARD_ID, GUARD_BLEND_SECONDS) \
        + interrupt_transition(PARAM_SWORD_INTERRUPT_ID, INTERRUPT_BLEND_SECONDS) + out[open_bracket:]

    # 3. Crouching keeps the stance: the Duck blend's Crouching / CrouchingLow inputs were the stock two-handed crouch
    #    pose, which ignored sword_pose (the cursor did nothing while crouched). They now get a bone-mask node: the
    #    stock crouch everywhere (hips, legs and the pelvis delta the compositor uses to bend the knees) with the
    #    upper body (spine, arms, sword) from the stance chain. Pointing them straight at the stance chain stood the
    #    character up, because the stance clips are full-body standing poses.
    s, e = find_node_block(out, DUCK_NODE_ID)
    block = out[s:e]
    old, new = "m_id = %d" % CROUCH_POSE_ID, "m_id = %d" % CROUCH_MASK_ID
    if block.count(old) != 2:
        sys.exit("duck blend: expected two crouch inputs")
    out = out[:s] + block.replace(old, new) + out[e:]
    marker = "\t\tm_nodes = \n\t\t[\n"
    out = out.replace(marker, marker + bone_mask_node(
        CROUCH_MASK_ID, "Crouch + sword stance (upper body)", (-2208.0, 160.0), CROUCH_POSE_ID, STANDING_POSE_ID,
        "Only_UpperBody"), 1)

    # 4. Declare the parameters locally (subgraphs mirror the root declarations they use).
    marker = "\t]\n\tm_localTags = [  ]\n}"
    params = int_param("sword_pose", PARAM_SWORD_POSE_ID, len(POSE_CLIPS) - 1, 2) \
        + int_param("sword_attack", PARAM_SWORD_ATTACK_ID, len(ATTACK_CLIPS) - 1, 2) \
        + int_param("sword_guard_dir", PARAM_SWORD_GUARD_DIR_ID, len(BLOCK_CLIPS) - 1, 2) \
        + bool_param("b_sword_interrupt", PARAM_SWORD_INTERRUPT_ID, 2) \
        + bool_param("b_sword_guard", PARAM_SWORD_GUARD_ID, 2) \
        + float_param("sword_wind", PARAM_SWORD_WIND_ID, 2)
    out = replace_once(out, marker, params + marker, "subgraph parameter list end")

    write(f"{OUT_GRAPH_DIR}/holdtype_melee_sword.vsubgrph", out)


def make_model():
    src = read(f"{ENGINE}/citizen_human_male.vmdl")
    out = replace_once(src,
                       'anim_graph_name = "models/citizen_human/animgraphs/human.vanmgrph"',
                       'anim_graph_name = "models/player/animgraphs/human_sword.vanmgrph"',
                       "vmdl anim graph")
    files = "".join(anim_file(name, fbx, name not in WIND_UP_CLIPS, 0.15, 0.15) for name, fbx in POSE_CLIPS + BLOCK_CLIPS)
    files += "".join(anim_file(name, fbx, False, 0.0, 0.2) for name, fbx in ATTACK_CLIPS)
    files += "".join(anim_file(name, fbx, True, 0.2, 0.2) for name, fbx in SEQUENCE_CLIPS)
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
    for _, fbx in POSE_CLIPS + ATTACK_CLIPS + BLOCK_CLIPS:
        p = os.path.join(OUT_MODEL_DIR, "animations", fbx)
        if not os.path.exists(p):
            print("WARNING missing clip:", fbx)
    make_root_graph()
    make_holdtypes()
    make_holdtype_melee()
    make_model()
