# What to test

None of this has been tried in the game. I checked it against the game's code and FSMs, and `nettest` passes, but `nettest` doesn't run the game.

**Before you start**
- Both players need the new build, because the protocol version changed. Your friend's zip is `thunderstore/dist/SiroxCW-MultipleHornetsMod-0.1.0.zip`.
- If something goes wrong, send me the room and roughly what time it happened. Put your friend's `session-*.log` in `debug-logs/`.
- Room names below are the ones in the logs. "Owner" is the player the enemy is fighting. If you're unsure who that is, the log says `Enemy ... now belongs to`.

## 1. Enemies no longer affect the other player's hero (most important)

Each test has one player get grabbed or robbed while the other stands somewhere safe in the same room. The second player's hero should be completely unaffected: no damage, no lost control, no fading screen, no room change.

- [ ] **Rosary Thief** (`bone_east_10`, `bonetown`): the thief robs one player. The other keeps all their rosaries and takes no damage.
- [ ] **Slab jailer that carries you off** (`bone_east_04c`, `greymoor_05`, `shadow_21`): one player gets caught.
  - The other stays in the room and keeps control, inventory and pause.
  - After the caught player leaves, the jailer should be gone, not frozen in the air.
- [ ] **Song Handmaiden** (`song_04`, `song_17`): only the player being drained loses silk.
- [ ] **Lace** (`bone_east_12`): the dialogue only opens for the player who starts it. The other doesn't get stuck in a text box.
- [ ] **Boss with a cutscene ending, Widow** (`belltown_shrine`): when one player goes into the memory after the win, the other first stays in control. Once the first player has left, the second should follow into the memory instead of being stuck in the arena.

## 2. Multi-hit attacks

- [ ] **Bone Circler Vicious** (`bone_east_03`, `bone_east_07`) or **Lace's multi-hit slash**: stand in the attack while you're **not** the owner. You should get hurt.
- [ ] Do the reverse: when the owner gets hit by a multi-hit attack, the other player should not take damage.
- [ ] **Check this closely:** after a non-owner gets caught by Lace's multi-hit, the hero should recover normally. This is the part I'm least sure of, because the effect on the hero is in a prefab I couldn't read. Also tell me if the boss visibly jumps to fighting that player.

## 3. Fights wait for everybody (new, test this first)

Whoever would start a boss or a wave arena freezes on the spot and sees who they are waiting for. The fight starts when the last player gets there. Holding **K** for 1.5 seconds kills your hero to get out. The key is `GiveUp` in the config.

Good rooms: `tut_03` (Moss Mother, wave arena), `bone_01`, `dock_03c`, `bone_05_boss` (Bell Beast web), `belltown_08` (Father of the Flame), `bone_east_12` (Lace), `shellwood_22` (Seth), `library_13` (Trobbio).

**The basics**
- [ ] One player walks in: they freeze, in the air as well, and can't move, attack or take damage. The boss and the gates do nothing yet.
- [ ] The text names the missing player and which room they're in. The other player sees "... is waiting for you at a fight in ...".
- [ ] The second player walks in: both get control back at the same moment, and the fight starts for both. Gates close, the boss wakes, waves come.
- [ ] At the end of the fight: gates open, camera free, music back. Coming back later doesn't start it again.

**Getting out**
- [ ] Hold K while frozen: the hero dies normally, respawns normally and can move again. The other player's text goes away.
- [ ] Hold K only briefly and let go: nothing happens.
- [ ] Press K with the chat open, or while paused: nothing happens.
- [ ] Quit to the menu while frozen: the other player isn't stuck waiting for you.
- [ ] Disconnect one player while the other is frozen: the frozen player's fight starts by itself.

**Awkward cases**
- [ ] **Coming back:** one player dies during the fight and returns to the room. They should go straight back in without a freeze, while the survivor is still in the room.
- [ ] **Both die:** the room empties. Coming back waits for both again.
- [ ] **Different rooms:** each player freezes at a different fight. Nothing starts until one of them gives up. This is on purpose, each sees where the other is.
- [ ] **Double touch:** both step in at the same moment. The fight starts once and ends normally.
- [ ] **Web hit (`bone_05_boss`):** one player hits the web and freezes. The other sees the web still there and has to hit it too.
- [ ] **Dialogue first (Seth, Lace, Widow in `belltown_shrine`):** the freeze comes when the conversation would start or end. Control has to come back afterwards, without anybody stuck in a text box. **Check this closely**, these are the ones I trust least.
- [ ] **Fight with several parts (`belltown_08`, `coral_11`, `bone_east_08`):** there should be one freeze only, not a second one partway through. If you see a second one, tell me the room.

**Things that should not freeze**
- [ ] Alone in a session (nobody else connected): no freeze, no text.
- [ ] A teammate on the main menu: no real wait, at most a tiny hitch.
- [ ] Memory rooms (`Memory_*`): no freeze.
- [ ] Silk vines blocking the path in `mosstown_02`, ordinary enemies, and rooms with no fight.

**Worth knowing**
- A teammate can still walk around while you are frozen, and can hit a sleeping boss before starting the fight. Tell me if this turns out to be a problem.
- Dying to get out costs what a death costs, including your rosaries in a cocoon.
- K is keyboard only. There is no controller button yet.

**Not held back:** these still start for one player the old way. I didn't trace what starts them, or chose to leave them out.
- Garmond's fights (`library_09`, `hang_04_boss`)
- Song Knights outside a held arena (`hang_07`, `song_02`, `song_25`, `song_27`)
- `clover_10`, `coral_29`, `weave_03`
- Any wave arena that a controller I don't know about starts directly

If a fight starts without a freeze, send me the room. It's usually one line to add.

## 4. Arena details

- [ ] **Joining late:** a player who dies during a wave arena and returns: the arena catches up to the current wave, dead enemies stay dead, the fight can still be finished, and the gates close behind them once they're in.
- [ ] **The end:** both players get gates open, camera free and music back in `tut_03`, `bone_01`, `bone_18`, `dock_03c` and `ant_04_mid`.
- [ ] **Boss arenas:** the boss and gates appear for both in `belltown_08`, `bellway_centipede_arena`, `bone_east_08`, `coral_11`, `greymoor_05_boss` / `greymoor_08_boss`, `room_crowcourt_02`, `shellwood_18`, `ward_02_boss`, `cog_dancers_boss`, `abyss_cocoon`.

## 5. A dead owner doesn't freeze the boss

- [ ] During a boss fight, the owner dies while the other stays alive. Bosses to try: **Lace** (`bone_east_12`), **Seth** (`shellwood_22`), **Last Judge** (`coral_judge_arena`).
  - Once the dead player has left the room, the boss should start fighting the survivor again.
  - Before this fix it would pose over the dead hero forever, with the survivor shut in.
- [ ] The reverse: the player who is **not** the owner dies. The boss should keep fighting the owner without pausing or changing target.
- [ ] After that, the player who died can come back and rejoin the fight.

## 6. Items and upgrades

- [ ] **Rosary string machine** (`bone_01c`, `dock_01`, `greymoor_01`): only the player who pays gets the string.
- [ ] **Courier delivery** (Belltown): only the player who takes the delivery gets the fragile item and its bar.
- [ ] **Fourth mask shard or second spool fragment:** both players get exactly one extra mask or spool, not several. Reload the save afterwards and check the count again.
- [ ] Rooms with several secret walls or levers: an opened object should show as opened for the other player within a few seconds, including in rooms where a boss scene loads a moment later.

## Known limits

These are not bugs in this build, and I left them as they are on purpose:

- **Act 3 is not shared.** Each player's story act is their own, because changing it means a respawn and a wake-up sequence that I don't want to force on someone mid-play. If only one player reaches Act 3, your rooms will differ. Tell me if you get that far and it's a problem.
- **Steel Servant** (`bone_steel_servant`) now freezes before it captures the hero. I didn't confirm that its waves start the same way as other arenas.
- **Lifts** still aren't shared.
