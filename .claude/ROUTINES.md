# Routines

A **routine** is a named, well-defined job Mark can hand off and walk away from. Each one lives in
`.claude/skills/<name>/SKILL.md` and is invoked either by slash command (`/art-routine …`) or by
plain language ("run our art routine on the elm stump to …"). The skill description is what makes
the plain-language form trigger, so keep the trigger phrase in it.

| Routine | Trigger phrase | Job |
|---------|----------------|-----|
| `art-routine` | "art routine" | Redesign a model / texture / material against stated objectives, using real-life source material |
| `code-qc-routine` | "code qc routine", "qc the code" | Audit a code scope for bloat and per-frame work that threatens playability; apply safe fixes, propose the rest |
| `bug-test-routine` | "bug test routine", "bug test" | Reproduce a reported defect in the live editor, instrument, fix, re-test until the assertion holds |

## Contract every routine follows

1. **Restate the brief** in one paragraph before any work: goal, named target, objectives, what is
   in and out of scope. If the brief is missing a required field, use the routine's documented
   default and say so — do not stop to ask once Mark has walked away, unless the routine says the
   gap makes the work useless.
2. **Scope is a fence.** Touch only the systems and assets the routine lists plus whatever the brief
   names. Anything else you notice goes in the report under *Out of scope, noticed*.
3. **Commandments apply** (CLAUDE.md): umbrella owners, deprecate cleanly, meters converted once,
   no `Ensure*` bandages, infrequent validation.
4. **Evidence over narrative.** Renders, screenshots, console excerpts and metrics go in the report
   or get sent with `SendUserFile`. "It should work now" is not a result.
5. **Leave the editor the way you found it**: stop play mode, close scene tabs you opened, never
   `save_scene` on a tab that had the user's unsaved changes, remove temporary logging.
6. **Never commit or push** inside a routine. Leave the working tree for Mark to review.
7. **Finish with the standard footer**: `Build label — vX.Y.Z (Assistant)` (patch bumped once in
   `Code/GameBuildLabel.cs`) and a **What's needed from you** section.

## Adding a routine

Copy this skeleton to `.claude/skills/<name>/SKILL.md`, add a row to the table above, and add a
line to the *Routines* section of `CLAUDE.md`.

```markdown
---
name: <name>
description: <one sentence — include the plain-language trigger phrase Mark will say>
---

# <Title>

## Goal
## Brief format         (what Mark says, which parts are required, defaults for the rest)
## In scope / Out of scope
## Technologies         (Blender, sbox MCP, numpy, web references …)
## Procedure            (numbered, with the stop / loop conditions)
## Quality bar          (the checklist the output is judged against)
## Report               (what the final message must contain)
```
