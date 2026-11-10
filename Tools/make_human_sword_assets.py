"""Generate the project's sword-capable human model + animgraph from the engine's citizen_human sources.

Produces (all under Assets/models/player/):
  human_male_sword.vmdl                 clone of citizen_human_male.vmdl + a "99_Sword" animation folder
                                        (our clips in Assets/models/player/animations/) and our graph
  animgraphs/human_sword.vanmgrph       clone of human.vanmgrph: holdtypes subgraph path patched,
                                        parameters added: sword_pose, sword_attack (int), b_sword_interrupt,
                                        b_sword_guard (bool)
  animgraphs/holdtypes_sword.vsubgrph   clone of holdtypes.vsubgrph: holdtype_melee path patched
  animgraphs/holdtype_melee_sword.vsubgrph
                                        clone of holdtype_melee.vsubgrph: the single melee pose node
                                        becomes a selector on sword_pose (one child per pose clip), the
                                        attack node a selector on sword_attack (one child per strike clip);
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

# Pose clips in sword_pose order (PlayerAnimation.SwordPose must match).
POSE_CLIPS = [
    ("Sword_Cocked_L", "human@sword_cocked_l.fbx"),
    ("Sword_Rest_L", "human@sword_rest_l.fbx"),
    ("Sword_Rest_R", "human@sword_rest_r.fbx"),
    ("Sword_Cocked_R", "human@sword_cocked_r.fbx"),
    ("Sword_Cocked_Over", "human@sword_cocked_over.fbx"),
    ("Sword_Cocked_Stab", "human@sword_cocked_stab.fbx"),
    ("Sword_Swap_Mid", "human@sword_swap_mid.fbx"),   # shoulder swap passes through this (PlayerAnimation)
    ("Sword_Rest_Up", "human@sword_rest_up.fbx"),     # aiming up: raised, blade tipped back
]
# Wind-up clips (Mark 2026-11-05, Last Oasis reference): a drawn motion into the cocked pose that plays ONCE and holds
# its last frame while the swing hangs. The sword_pose selector restarts its child on every change so each press draws.
WIND_UP_CLIPS = {"Sword_Cocked_R", "Sword_Cocked_L", "Sword_Cocked_Over", "Sword_Cocked_Stab"}
# Guard pose: NOT in the sword_pose selector — a bool selector on b_sword_guard wraps that selector, so the guard
# shows in GUARD_BLEND_SECONDS while rest / cock / swap blends keep POSE_BLEND_SECONDS.
BLOCK_CLIP = ("Sword_Block", "human@sword_block.fbx")     # blade level in front of the head
# Plain looping sequences outside the graph, played by PlayerAnimation with UseAnimGraph off (like the dodge roll).
SEQUENCE_CLIPS = [("Wingsuit_Glide", "human@wingsuit_glide.fbx")]   # PlayerAnimation.Wingsuit.cs
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
PARAM_SWORD_INTERRUPT_ID = 1860011003
PARAM_SWORD_GUARD_ID = 1860011004
IDLE_STATE_ID = 1228346111    # "Fists attack SM" Idle state (the attack state returns here)
POSE_NODE_ID = 257497782      # existing "Melee_Weapons_2H_Pose_Standing_01" sequence node -> guard selector
INNER_POSE_ID = 1860012300    # the sword_pose selector under it
BLOCK_SEQ_ID = 1860012301     # Sword_Block sequence (the guard selector's "true" child)
ATTACK_NODE_ID = 967987431    # existing "Melee_Weapons_2H_Attack_01" sequence node -> selector
NEW_NODE_BASE = 1860012000    # new child sequence nodes: base + index
DUCK_NODE_ID = 1420086429     # "Duck" blend: Standing / Crouching / CrouchingLow
STANDING_POSE_ID = 382273333  # its Standing input (the long-idle state machine over the stance selector)
CROUCH_POSE_ID = 439512559    # stock "Melee_Weapons_2H_Pose_Crouching_01" input (kept: hips and legs)
CROUCH_MASK_ID = NEW_NODE_BASE + 200   # new mask: stock crouch + upper body from the stance chain
ATTACK_EXIT_BLEND_SECONDS = 0.08       # attack -> idle blend (stock 0.3 s is the whole length of a 0.30 s strike)
POSE_BLEND_SECONDS = 0.12             # sword_pose cross-fade (rest / cock / shoulder swap; 0.05 snapped the swaps)
INTERRUPT_BLEND_SECONDS = 0.12         # attack -> idle when a follow-up click re-cocks (the 0.6 s one-handed windup has room; 0.05 popped)
GUARD_BLEND_SECONDS = 0.05             # attack -> idle when the guard goes up (Mark 2026-11-04: block shows in 50 ms)

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


def selector_node(node_id, name, pos, child_ids, param_id, blend, reset_on_change, bool_param_id=None):
    """Int selector on param_id, or (bool_param_id given) a bool selector: child 0 = false, child 1 = true."""
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
\t\t\t\t\t\tm_vControlPoint1 = [ 0.314815, 0.0 ]
\t\t\t\t\t\tm_vControlPoint2 = [ 0.111111, 1.0 ]
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
        + bool_param("b_sword_interrupt", PARAM_SWORD_INTERRUPT_ID, indent) \
        + bool_param("b_sword_guard", PARAM_SWORD_GUARD_ID, indent)
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
        sequence_node(pid, name, name, (-2560.0, -512.0 + 96.0 * i), loop=name not in WIND_UP_CLIPS)
        for i, (pid, (name, _)) in enumerate(zip(pose_ids, POSE_CLIPS)))
    pose_selector = selector_node(INNER_POSE_ID, "Sword pose (sword_pose)", (-2400.0, -256.0), pose_ids,
                                  PARAM_SWORD_POSE_ID, blend=POSE_BLEND_SECONDS, reset_on_change=True)
    block_seq = sequence_node(BLOCK_SEQ_ID, BLOCK_CLIP[0], BLOCK_CLIP[0], (-2400.0, -96.0), loop=True)
    guard_selector = selector_node(POSE_NODE_ID, "Guard (b_sword_guard)", (-2208.0, -256.0),
                                   [INNER_POSE_ID, BLOCK_SEQ_ID], INVALID_ID, blend=GUARD_BLEND_SECONDS,
                                   reset_on_change=False, bool_param_id=PARAM_SWORD_GUARD_ID)
    out = out[:s] + pose_children + pose_selector + block_seq + guard_selector + out[e:]

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
        + bool_param("b_sword_interrupt", PARAM_SWORD_INTERRUPT_ID, 2) \
        + bool_param("b_sword_guard", PARAM_SWORD_GUARD_ID, 2)
    out = replace_once(out, marker, params + marker, "subgraph parameter list end")

    write(f"{OUT_GRAPH_DIR}/holdtype_melee_sword.vsubgrph", out)


def make_model():
    src = read(f"{ENGINE}/citizen_human_male.vmdl")
    out = replace_once(src,
                       'anim_graph_name = "models/citizen_human/animgraphs/human.vanmgrph"',
                       'anim_graph_name = "models/player/animgraphs/human_sword.vanmgrph"',
                       "vmdl anim graph")
    files = "".join(anim_file(name, fbx, name not in WIND_UP_CLIPS, 0.15, 0.15) for name, fbx in POSE_CLIPS + [BLOCK_CLIP])
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
    for _, fbx in POSE_CLIPS + ATTACK_CLIPS + [BLOCK_CLIP]:
        p = os.path.join(OUT_MODEL_DIR, "animations", fbx)
        if not os.path.exists(p):
            print("WARNING missing clip:", fbx)
    make_root_graph()
    make_holdtypes()
    make_holdtype_melee()
    make_model()
