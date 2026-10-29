# Multiple Hornets

An all-in-one co-op multiplayer mod for Hollow Knight: Silksong. Play together in one shared Pharloom:
see each other, fight the same enemies and bosses, and share all progression.

## Features

**Players**
- See every other player in your room, with Hornet's real animations for every crest, and name tags.
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
- Late joiners catch up: the host remembers the shared world, and everyone who joins later gets all of it.
  Progress made by two players at the same time is merged, nothing gets lost or doubled.
- Enemy drops and repeatable shop purchases stay personal.

## Installation

Install with a Thunderstore mod manager (r2modman, Gale, ...), or manually: put `MultipleHornetsMod.dll` and
`LiteNetLib.dll` into `BepInEx/plugins/MultipleHornets/`.

Do **not** use it together with SSMP, BasicItemSync or BasicEnemySync. Multiple Hornets replaces all three.

## How to play

1. Press `F5` (in the main menu or in game) to open the Multiple Hornets menu.
2. Enter your name.
3. **Host**: choose a port (default `26950`) and press *Host*. Other players need your IP address and the UDP port
   forwarded on your router, or a VPN such as Tailscale, ZeroTier or Radmin VPN.
4. **Join**: enter the host's address and port and press *Join*.
5. Load a save file. Fresh save files for everyone give the cleanest co-op playthrough; if you join with an
   existing save, its progress is merged into the shared world.

The host can choose what is synced in the menu while hosting (enemies, items, world, bosses, wishes, maps).
Shared progress is stored under a *world* name; pick a new name for a new playthrough.

Settings like key bindings live in `BepInEx/config/io.github.siroxcw.multiplehornetsmod.cfg`.

## Known limitations

- Enemies created at runtime (for example some summons) are not shared: each player fights their own copy.
- Enemies always target the player who currently owns them.
- Direct IP connections only for now (no Steam lobbies).
