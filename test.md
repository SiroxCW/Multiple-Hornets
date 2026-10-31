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

## 3. Wave arenas start for everybody

Good rooms: `tut_03` (Moss Mother), `bone_01`, `bone_18`, `dock_03c`, `ant_04_mid`.

- [ ] **Both inside:** one player walks in. Enemies and gates appear for both, with no enemies invisible to one player.
- [ ] **One outside:** the other player starts the fight while you stand outside the arena in the same room.
  - You should see the enemies, your gates stay open, and the fight runs without you.
  - Walking in closes the gates behind you.
- [ ] **Joining late:** enter the room while the fight is running. The arena catches up to the current wave, enemies that are already dead stay dead, and the fight can still be finished.
- [ ] **The end:** both players get gates open, camera free and music back. Nobody stays locked in, and the arena doesn't restart when you come back.
- [ ] **Double start:** both players step into the trigger at the same moment. The fight should end normally. Before this fix, a double start could mean it never ends.

## 4. Boss arenas start for everybody

Same idea as section 3, for boss rooms started by a controller: `belltown_08` (Father of the Flame), `bellway_centipede_arena`, `bone_east_08`, `coral_11`, `greymoor_05_boss` / `greymoor_08_boss` (Moorwing), `room_crowcourt_02`, `shellwood_18`, `ward_02_boss`, `cog_dancers_boss`, `abyss_cocoon`.

- [ ] One player walks in while the other is nearby: the boss and gates appear for both.
- [ ] One player walks in while the other is far away or behind a wall: the second player isn't locked out, and joins when they arrive.
- [ ] **Check this closely:** a second player who is right at the door when the first walks in. They could still end up on the wrong side of the gate, because I only check distance and whether a wall is in between.

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
- **Steel Servant** (`bone_steel_servant`) holds the hero for a conversation, so I didn't share its opening. Its waves still start for the other player.
- **Lifts** still aren't shared.
