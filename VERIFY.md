# VERIFY.md — things that need a human look

Claude can build, regenerate and screenshot, but it cannot judge whether something *feels* right in
play, and it cannot see what Mark sees at his PC. Anything that was changed without that final
human check gets a row here, so nothing is forgotten while Mark is away.

## Rules

- **Who writes:** Claude adds a row whenever it finishes a change it could not fully verify in the
  engine the way a player would (look, feel, timing, collision, audio, anything judged by eye or
  hand). One row per thing to look at. Mark may add rows too.
- **Who closes:** only Mark. He changes the status to `OK` or `BAD <note>`. Claude never deletes
  or closes a row; a `BAD` row with a note becomes the brief for the fix, and the fix adds a new
  row referencing the old ID.
- **Where it is mentioned:** every reply's **What's needed from you** section lists the new IDs
  added in that reply (`VERIFY V-012, V-013`). Routines (`.claude/ROUTINES.md`) do the same in
  their report.
- **What a row needs:** enough to reproduce the look in under a minute — scene, where to stand or
  which object, what to compare against, and what "good" means. Build label of the change.
- **IDs** are sequential `V-NNN` and never reused. New rows go at the bottom of *Open*. Closed rows
  move to *Closed* with the date, newest first.

## Open

| ID | Date | Build | Area | What to check | How to check | Status |
|----|------|-------|------|---------------|--------------|--------|
| V-001 | 2026-10-02 | 0.8.2702 | Elm bark UVs | The flat-projected patch at the top of the flare shows a short seam where the bark pattern changes direction (trade for a sideways smear). Acceptable, or prefer a little stretch instead? | testscene1, elm2 at (-1463,-5490), stand 1.5 m from the trunk's -x side, look at 1.2–2.5 m height. Good = no smear and the seam does not catch the eye. Knob: `BARK_MAX_SKEW` in `Blender/scripts/create_elm_tree.py` (higher = fewer seams, more stretch). | open |
| V-002 | 2026-10-02 | 0.8.2703 | Elm collider | Collision now uses the LOD0 render wood (was LOD2, which measured up to 5 cm inside the bark; what Mark saw "behind the texture" was that LOD2 mesh). | testscene1, editor physics overlay on any `environment_elm*_v71`: the collider should lie on the bark everywhere, not behind it. Also walk into the trunk and swing an axe at it in play mode. | open |
| V-003 | 2026-10-02 | 0.8.2701 | Elm leaf cards | New foliage: true-scale tiny leaves, dark back / light front layers, deep green (sample 10). Crowns are more open than before (7–30 % sky through per tree vs 3–20 %). | testscene1, stand under any elm crown and look up, then from 50 m. Good = reads as fine foliage mass, not confetti, and the crown is not see-through. Knob: `LEAF_BACK` / `LEAF_FRONT` spread in `create_elm_tree.py`. | open |
| V-004 | 2026-10-02 | 0.8.2697 | Elm bark texture | Crack gaps / jogs: no line should be followable top to bottom; are the breaks the right frequency? | testscene1, elm trunk at 2–4 m. Knob: `BARK_BREAK` (gap share) and `BARK_JOG` in `create_elm_tree.py`. | open |
| V-005 | 2026-10-02 | 0.8.2696 | Elm bark UVs | Limbs now unwrap lengthwise (one seam per limb). Check outer limbs where they bend for any remaining ring. | testscene1, fly around a crown at limb height, especially elm1 and elm6. | open |
| V-006 | 2026-10-02 | 0.8.2702 | Editor LODs | In the editor the elms' LOD is picked from the scene's main camera (~150 m away), not the viewport camera, so a close-up can show LOD1/LOD2. In play mode the player camera is the main camera. Confirm in play mode that LOD1 does not appear closer than ~100 m. | Play testscene1, walk away from an elm and watch for the card thinning / wood simplifying. Knob: `TREE_LODS` metres + `LOD_THRESHOLD_PER_METER` in `create_elm_tree.py`. | open |
| V-007 | 2026-10-02 | 0.8.2690 | Tripo clover tree | `clovertree1_chop` still points at the deleted `cel_lit` shader and renders the error material. Decide: `pixel_lit` with the 512 atlas, or re-UV the model so a pixel bark can run along its limbs. | testscene1, the tree at (-882,-3398). | open |
| V-008 | 2026-10-02 | 0.8.2689 | Elm saplings | Saplings (`environment_elmsapling*_v15`) still carry the old lime leaf card and warm bark; they no longer match the v71 elms. Regenerate with the new bark / leaf treatments? | testscene1, sapling next to elm2. | open |
| V-009 | 2026-10-02 | 0.8.2705 | Elm bark UVs | Replaces V-001's per-face planes: rescued faces at the flare top now share one limb-aligned box projection, so they line up with each other and the only seam is the patch outline. | testscene1, elm2 at (-1463,-5490), 1.5 m from the trunk, look at the flare top all the way round. Good = bark reads continuous with at most one direction change per patch, no patchwork of small tiles. | open |

## Closed

| ID | Closed | Status | Note |
|----|--------|--------|------|
