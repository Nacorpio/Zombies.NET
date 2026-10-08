# Slice demo script

The slice is meant to show this, end to end: two players on one server loot a village, fight zombies, bleed, and bandage. A zombie holds a
weapon, the player customizes a weapon with Attachments, a zombie bite causes infection and a cure removes it, loot differs by room type, a
context menu opens on an inventory Stack, and the Debug session badge shows the script's progress while it runs.

**That demo cannot be run today.** The parts are built and tested, but the running game does not connect them yet. This page says which
parts can be shown for real now, which only in tests and harness runs, and which not at all, so nobody is surprised on stage. The gaps are
filed as issues, listed at the end.

Everything here was run on the recommended-tier rig described in [performance.md](performance.md).

## Where each part stands

| Demo part | In the running game? | What shows it today |
| --- | --- | --- |
| Customize a weapon with Attachments | Yes, in the client's inventory screen | Part 1, step 3. Checked by tests |
| Context menu on an inventory Stack | Yes, in the client | Part 1, step 4, with a captured frame |
| Debug session badge while the script runs | Yes, in the client | Part 1, step 1, with a captured frame |
| Two players on one server | **No**: the client is solo only (#147) | Part 2, step 1: two fake clients |
| A zombie holds a weapon | **No**: the game has no zombies (#148) | Part 2, step 3: tests |
| Fight zombies, bleed, bandage | **No**: no zombies (#148); the client sends no combat commands (#149) | Part 2, step 4: tests |
| Bite causes infection, then a cure | **No**: nothing makes a zombie bite (#148); the client cannot use an item on the Server (#149) | Part 2, step 5: tests |
| Loot a village house; loot differs by room type | **No**: containers cannot be opened (#150) | Part 2, step 6: tests |
| Zombies lose parts and collapse as ragdolls | **No**: no zombies (#148) | Part 2, step 7: tests |

## Part 1: what you can show for real now

Build first: `dotnet build Zombies.slnx --configuration Release`. The client needs a Vulkan GPU. In the commands below `CLIENT` is
`src/Zombies.Client/bin/Release/net10.0/Zombies.Client.exe`, run from the repository root.

The inventory screen is a **local sandbox**: it starts with a few seeded items (beans, bandages, a water bottle, a crowbar, a suppressor, a red dot
sight, and a 9mm pistol with 9 rounds at 72% condition) and does not talk to a Server (#149).

1. **Start with the Debug session badge.** Run `CLIENT --session docs/demo/session.json`. A badge appears at the top right and follows the
   file: edit a step's `state` to `running`, `done` or `failed` as the demo goes, and the badge updates. F4 pins it open; hovering opens it.
   *Shows:* the badge and its progress. The file's steps are the demo's steps.
2. **Open the inventory** with `I`. The mouse is freed while a screen is open. The backpack is on the left and the ground on the right.
3. **Customize a weapon.** Right-click the 9mm pistol and choose *Inspect*. Its Mounts open below. Drag the suppressor from the backpack onto the
   *Muzzle* mount, and the red dot sight onto *Optic*. Hover a Mount to see what changes (noise and damage for the suppressor, reach for the
   sight). Drag an Attachment off a Mount back into the backpack to remove it. A Mount refuses an Attachment that does not belong on it, with a
   dialog that says why.
   *Checked by:* `WeaponMountsViewTests` and `WeaponFittingTests`; the visible attachment on the held weapon by `VisibleAttachmentTests`.
   *Not recorded as a frame.*
4. **Open a context menu.** Right-click any Stack. The menu lists exactly the actions the Domain offers for it, grouped, with the ones that cannot
   run now shown disabled and a reason on hover. Right-click the beans on the ground: *Drop* and *Split stack* are disabled, because the ground is
   not your container, and the reason shows. Up, Down and Enter drive the menu; Escape closes just the menu. Right-clicking empty space opens
   nothing, and starting a drag closes the menu.
   *Shows:* [the captured frame](demo/context-menu-and-session-badge.png), which has the menu and the badge together.
   To photograph the two menus without a mouse:
   `CLIENT --screen inventory-menu --frames 40 --capture out.png --view 2 --size 1280x720 --freeze-time` (the backpack) and the same with
   `--screen inventory-menu-ground` (a disabled entry with its reason).
   *Not yet:* choosing *Use* only raises an event; it does not reach a Server (#149).

## Part 2: what can only be shown headless

None of these run in the game. Each is a test or a harness run, so the script is: run the command, point at what it checks.
Run the test commands from the repository root after a Release build. A line such as `Total: 17, Failed: 0` means it passed.

`ENGINE` is `tests/Zombies.Engine.Tests/bin/Release/net10.0/Zombies.Engine.Tests.exe` and `DOMAIN` is
`tests/Zombies.Domain.Tests/bin/Release/net10.0/Zombies.Domain.Tests.exe`.

1. **Two players on one server.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- net 300 mods`.
   Two fake clients join one Server over the in-memory transport and move. It reports their snapshots and `steady-state allocation 0 bytes`.
   The join over a real UDP socket is `LiteNetLib_ClientJoinsOverUdp_AndReceivesSnapshots` in `NetTests`. *Missing in the game:* #147.
2. **Zombies at the scale of the slice.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- ai 12345`.
   Four fake players and 200 zombies think and chase on generated terrain, with the Server tick timed against its 12 ms budget.
   *Missing in the game:* #148.
3. **A zombie holds a weapon.** `ENGINE -class Zombies.Engine.Tests.ZombieHeldWeaponTests` and `DOMAIN -class Zombies.Domain.Tests.ZombieHeldWeaponTests`:
   which weapon a zombie spawns with comes from its type's data and its seed, it attacks with that weapon's reach and damage, and it drops the
   weapon when the arm holding it is lost. *Missing in the game:* #148.
4. **Fight, bleed and bandage.** `ENGINE -class Zombies.Engine.Tests.WeaponCombatTests`: a firearm shoots far and spends a round, a melee weapon
   strikes near, a worn weapon deals less, a hit wounds a zombie's body, a player's wound bleeds, a bandage stops it and is consumed, and the
   client sees the HUD values. *Missing in the game:* #148 and #149.
5. **A bite, infection and the cure.** `ENGINE -class Zombies.Engine.Tests.StatusEffectNetTests`: a bite from a zombie whose data says it
   infects applies infection and wounds, infection worsens by stage as time passes, the player alone is told, and using the antibiotics removes it.
   *Missing in the game:* #148 and #149.
6. **Loot differs by room type.** `DOMAIN -class Zombies.Domain.Tests.AreaTypeTests` and the `AreaLoot` and `AreaType` tests in
   `ModLoadingTests`: the same Area type, container kind, danger level and seed give the same loot, and a kitchen and a bathroom draw from
   different tables. *Missing in the game:* #150.
7. **Parts lost and ragdolls.** `ENGINE -class Zombies.Engine.Tests.GoreTests`: a dead zombie collapses as a ragdoll pushed along the hit, a lost
   part becomes its own bodies, every client makes the same decals and ragdoll from the same seed, and the gore setting (off, low, high) changes what
   is drawn and never what the Server decides. *Missing in the game:* #148.
8. **A Code mod.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- codemods mods` runs the sample Code mod as a
   Server, a client and solo, and checks sides and message numbers.

## Gaps filed from writing this script

| Issue | Gap |
| --- | --- |
| #147 | The client cannot join a dedicated server, so there is no two-player demo |
| #148 | Nothing in the running game creates zombies or lets them attack |
| #149 | The client sends no combat or item commands, and its inventory is a local sandbox |
| #150 | World containers cannot be opened, so room-based loot never reaches a player |
| #151 | Chunk meshing is at its 2 ms budget in the harness, and the two measurements disagree |
| #152 | Steady-state allocation and tick time are not robust under CPU load; CI allocation tests flake |
| #153 | The floor tier, the GPU time and the VRAM are not measured |
| #154 | One ~54 ms frame in the client benchmark |

When #147 to #150 are done, this script should become one run in the client against a dedicated Server, with the session file's steps ticked off
as it goes, and Part 2 should disappear.