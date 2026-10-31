<div align="center">

# :sewing_needle: Multiple Hornets

[![GitHub license](https://img.shields.io/github/license/SiroxCW/Multiple-Hornets.svg?style=for-the-badge)](https://github.com/SiroxCW/Multiple-Hornets/blob/master/license)

![](https://raw.githubusercontent.com/michei69/disclaimers/refs/heads/main/ai/x4.png)

**An all-in-one co-op multiplayer mod for Hollow Knight: Silksong which syncs everything.**

</div>

## Features

**Players**
- See every other player in your room, with Hornet's real animations for every crest, and name tags.
- Pick your own cloak color from the menu, other players see it too.
- With the compass equipped, the map shows where everybody else is, in the color of their cloak.
- Chat and notifications (`Y` by default).

**Shared enemies and bosses**
- Everybody in a room fights the *same* enemies and bosses. Health, attacks, deaths and boss phases are in sync.
- Every enemy is simulated by exactly one player (its owner) and follows that player. Enemies are handed over
  automatically to whoever is fighting them, so enemies near you attack you.
- On the other players' screens an enemy is a puppet that still runs its own animations, hitboxes, projectiles and
  sounds. There are no invisible hitboxes or projectiles.
- Your hits on a puppet are shown instantly and applied by the owner, with your needle upgrades and crest bonuses.
- Joining a room in the middle of a fight shows the enemies as they are, including the ones already killed.

**Shared progression**
- Abilities, tools, silk skills, crests, mask shards, spool fragments, silk hearts, needle upgrades, tool pouch and
  crafting kit upgrades.
- Key items, wish items, relics, mementos and other collectables picked up in the world.
- Opened shortcuts, levers, gates, broken walls, unlocked Bellway and Ventrica stations, rescued fleas, bell shrines.
- Defeated bosses and cleared arenas, accepted and completed wishes.
- Maps, pins, markers and explored rooms.
- Late joiners catch up: the host remembers the shared playthrough, and everyone who joins later gets all of it.
  Progress made by two players at the same time is merged, nothing gets lost or doubled.
- Saves never get mixed up: every save remembers which co-op playthrough it belongs to (see below).
- Enemy drops and repeatable shop purchases stay personal.

## Installation

Install with a Thunderstore mod manager (r2modman, Gale, ...), or manually: put `MultipleHornetsMod.dll` and
`LiteNetLib.dll` into `BepInEx/plugins/MultipleHornets/`.

Do **not** use it together with SSMP, BasicItemSync or BasicEnemySync. Multiple Hornets replaces all three.

## How to play

1. Press `F5` (in the main menu or in game) to open the Multiple Hornets menu.
2. Enter your name.
3. **Host**: choose a port (default `26960`) and press *Host*. Other players need your IP address and the UDP port
   forwarded on your router, or a VPN such as Tailscale, ZeroTier or Radmin VPN. The bind address `0.0.0.0`
   accepts players on all network adapters; enter one of your PC's addresses to only accept them through that one.
4. **Join**: enter the host's address and port and press *Join*.
5. Load a save file. Everyone plays on their own save, the mod keeps them in sync.

The host can choose what is synced in the menu while hosting (enemies, items, world, bosses, wishes, maps).

### Playthroughs

The save the host loads decides the shared playthrough. Every save that takes part in a playthrough remembers
it (a small hidden entry in the save file), so saves of different playthroughs can never be mixed:

- Host starts a new game: a new, empty playthrough.
- Host loads a save: that save's playthrough. A save never used for co-op starts a new playthrough with its
  own progress.
- A player starts a new game while connected: the new save joins the playthrough.
- A player loads a save of this playthrough: it catches up with everything that happened in the meantime.
- A player loads a save that was never used for co-op: the menu asks first, since adding it shares its
  progress with everyone.
- A player loads a save of a different playthrough: progression is not shared for that save. Playing together
  still works.
- Until the host has loaded a save, nothing is shared.

Every member's save carries the playthrough, so anyone of the group can host it later.

Settings like key bindings live in `BepInEx/config/io.github.siroxcw.multiplehornetsmod.cfg`.

## Logs

Every game start writes its own file to `BepInEx/MultipleHornets-logs/`, the last 10 are kept. When something goes
wrong in co-op, the files of all players from that session are what is needed to find out why.

## Known limitations

- Enemies created at runtime (for example some summons) are not shared: each player fights their own copy.
- Enemies target the player who currently owns them. Normal enemies go to whoever is closer or attacks them, a boss
  stays with one player until that player leaves it.
- Only needle attacks, damage and the normal heal show their effects on other players. Silk skills, tools and the
  heals of some crests do not yet.
- The cloak color only applies to Hornet's own sprite, not to separate objects showing her in some cutscenes. Each
  color uses up to about 64 MB of video memory per sprite sheet.
- Direct IP connections only for now (no Steam lobbies).

## License

Multiple Hornets is licensed under the [GNU General Public License v3.0](LICENSE).

Credits: [SiroxCW](https://github.com/SiroxCW) and [phimaster0303](https://github.com/phimaster0303).
