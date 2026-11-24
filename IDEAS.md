# IDEAS.md — cool ideas for later

A parking spot for ideas that are not being built yet. Mark says "remember this idea for later" (or
anything like it) and Claude records it here in Mark's own words, plus a line of context if the
conversation gave one. Nothing here is a commitment or a brief.

## Rules

- **Record, don't design.** Write the idea as Mark said it. Claude may add one *Context* line
  (what was on screen, what prompted it) but no plan, estimate or counter-proposal.
- **Only Mark reorders, merges or removes** ideas. Claude only appends. Ideas go to the bottom.
- **IDs** are sequential `I-NNN` and never reused. When an idea is picked up for real, Mark moves it
  to *Picked up* with the date and the build / branch it landed in; the ID stays.
- Replies that add an idea mention it in **What's needed from you** as `IDEAS I-NNN` so Mark knows
  it was written down.

## Ideas

| ID | Date | Idea | Context |
|----|------|------|---------|
| I-001 | 2026-10-02 | Long road prefabs (straight, curve, etc.) that pull the terrain mesh to meet them nicely underneath, so players can create proper roads and racetracks for vehicles easily. | Said while reviewing the elm tree work in testscene1. |
| I-002 | 2026-10-06 | Some kinda loot inside resource nodes so the resource gatherer gets a little extra bonus and promotes others to join them. | Said right after placing the 128-texel clover tree beside the 256 one in testscene1. |
| I-003 | 2026-11-10 | Shared saves like Grounded: players can start a shared save, so it's not directly one person who owns it. Maybe use Steam Cloud: multiple Steam IDs can update it but each player has an isolated save just in case. When the host joins the hub, hosting just migrates to another player still in the world; when they come back it can migrate back, using a player host order in the game settings. Hub party: when the first player jumps into the hub it gives 10 seconds for players to "party up" and jump in as well; when a player enters, the 10 s timer is reset; once it expires they all join the same hub together in a party. Players who miss the window get disconnected and need to join that player's hub session. Leaving the hub: one of the players jumps into the purple portal and it asks them what world save to launch, with the same 10 s timer for leaving. Once they leave, any players who remain can jump into the portal and the list should say their shared save is actively being hosted. No plans to store the data ourselves. Follow-up: if a player is warned there's another save active and they choose to launch their own, then they should be forced to rename it and it'll just be for them going forward. | Follow-up to asking how hard our own multiplayer backend would be so players can join the hub while their world stays hosted. |
| I-004 | 2026-11-19 | Pipe bunker door + quest item inventory. The scarce metal pipe systems lead into the ground at both ends. If the player were to dig on one end of the pipe they'd see it got kinda pinched and destroyed off the end (crushed soda can look, can't look inside it really either cuz it's so squashed). On the other side the pipe should go deep underground, 20 meters? and when they get to the end of it it's just a cement square that the pipe leads into. On that cement cube is a door that the player needs a key for. That key can be dropped by the feral chieftain in the boss fight and once players receive it it goes into a kinda quest item inventory where you can never lose it again. Quest inventory is a new thing. It should be on the quest page somewhere, we can revisit that when we really need it. | Said while asking for the first above-ground pipe systems ("just make the pipes for now and record this info"). |

## Picked up

| ID | Date | Where it landed |
|----|------|-----------------|
