# Slice demo script

The slice is meant to show this, end to end: two players on one server loot a village, fight zombies, bleed, and bandage. A zombie holds a
weapon, the player customizes a weapon with Attachments, a zombie bite causes infection and a cure removes it, loot differs by room type, a
context menu opens on an inventory Stack, and the Debug session badge shows the script's progress while it runs.

**The demo runs in the game now.** The parts are built, tested, and wired into the running client and Server: zombies spawn and are drawn,
the client sends combat and item commands, world Containers open and loot by room type, and a client can join a dedicated Server. This page
says what to run and what to look at. The remaining gaps are listed at the end.

Everything here was run on the recommended-tier rig described in [performance.md](performance.md).

## Where each part stands

| Demo part | In the running game? | What shows it |
| --- | --- | --- |
| Customize a weapon with Attachments | Yes, in the client's inventory screen | Part 1, step 3. Checked by tests |
| Context menu on an inventory Stack | Yes, in the client | Part 1, step 4, with a captured frame |
| Debug session badge while the script runs | Yes, in the client | Part 1, step 1, with a captured frame |
| Two players on one server | Yes: `--connect` joins a dedicated Server (#147) | Part 1, step 5 |
| A zombie holds a weapon | Yes: zombies spawn and are drawn (#148) | Part 1, step 6 |
| Fight zombies, bleed, bandage | Yes: the client sends `UseWeapon` and `TreatWounds` (#148, #149) | Part 1, step 6 |
| Bite causes infection, then a cure | Yes: a zombie bite goes through `GameServer.Bite` (#148) | Part 1, step 6 |
| Loot a village house; loot differs by room type | Yes: Interact opens a world Container (#150) | Part 1, step 7 |
| Zombies lose parts and collapse as ragdolls | Yes: cosmetic events play on every client (#148) | Part 1, step 6 |

## Part 1: what you can show for real now

Build first: `dotnet build Zombies.slnx --configuration Release`. The client needs a Vulkan GPU. In the commands below `CLIENT` is
`src/Zombies.Client/bin/Release/net10.0/Zombies.Client.exe`, run from the repository root.

### Game controls

Once the game is running:
- **Movement:** `WASD` to move, `Space` to jump, `Shift` to sprint, `Ctrl` to crouch
- **Camera:** `Tab` to toggle mouse capture (required to look around), then mouse to look
- **Inventory:** `I` to open/close, mouse to interact
- **Combat (mouse captured):** `H` to hold a weapon, mouse click to fire, `R` to reload, `B` to bandage wounds
- **World:** `F` to open a nearby container (cabinet, desk, etc.)
- **Body screen:** `X` to open the body view (for detailed status)

The inventory screen shows the **Server's** Container for the player: the backpack holds the items the Server gives every player (beans, bandages, a
water bottle, a crowbar, a suppressor, a red dot sight, and a 9mm pistol with 9 rounds at 72% condition), and every change goes to the Server as a
command whose answer replaces the screen.

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
   *Use, Drop and Split now reach the Server*, which validates them and answers with the new Container view.
5. **Two players on one server.** Start a Server, then two clients that join it:
   `dotnet run --project src/Zombies.Server --configuration Release --no-build -- --port 27015 --mods mods`, then
   `CLIENT --connect 127.0.0.1:27015 --name alice` and `CLIENT --connect 127.0.0.1:27015 --name bob`. Each client takes the world seed and the
   mod verification from the Server. Walk about: each sees the other move. A join with a different mod list, content hash, protocol or generator
   version is refused with a dialog that says why.
6. **Meet the zombies.** In solo or on the Server, zombies spawn from the village's spawn points and are drawn with their rig, outfit, any held
   weapon, and any missing parts. Press `H` to hold the pistol, the mouse to fire it, and `R` to reload; the HUD's ammo and condition follow the
   Server. A zombie that reaches you attacks: the Server applies the bite to your Body, and a bite can infect. Press `B` to bandage a wound
   (you can bandage without opening the Body screen). A zombie killed collapses into a ragdoll on every client that sees it.
7. **Loot a village house.** Stand at a cabinet and press `F`. The Server fills it from the room's Area type the first time and sends its
   contents, which appear in the inventory screen's second panel. A kitchen cabinet and a bathroom cabinet in the same village give different
   loot. A Container with no rule for its Area type is reported to you instead of opening empty.

## Part 2: what can only be shown headless

These are tests and harness runs that check the same behaviour at scale, so the script is: run the command, point at what it checks.
Run the test commands from the repository root after a Release build. A line such as `Total: 17, Failed: 0` means it passed.

`ENGINE` is `tests/Zombies.Engine.Tests/bin/Release/net10.0/Zombies.Engine.Tests.exe` and `DOMAIN` is
`tests/Zombies.Domain.Tests/bin/Release/net10.0/Zombies.Domain.Tests.exe`.

1. **Two players on one server.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- net 300 mods`.
   Two fake clients join one Server over the in-memory transport and move. It reports their snapshots and `steady-state allocation 0 bytes`.
   The join over a real UDP socket is `LiteNetLib_ClientJoinsOverUdp_AndReceivesSnapshots` in `NetTests`.
2. **Zombies at the scale of the slice.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- ai 12345`.
   Four fake players and 200 zombies think and chase on generated terrain, with the Server tick timed against its 12 ms budget.
3. **A zombie holds a weapon.** `ENGINE -class Zombies.Engine.Tests.ZombieHeldWeaponTests` and `DOMAIN -class Zombies.Domain.Tests.ZombieHeldWeaponTests`:
   which weapon a zombie spawns with comes from its type's data and its seed, it attacks with that weapon's reach and damage, and it drops the
   weapon when the arm holding it is lost.
4. **Fight, bleed and bandage.** `ENGINE -class Zombies.Engine.Tests.WeaponCombatTests`: a firearm shoots far and spends a round, a melee weapon
   strikes near, a worn weapon deals less, a hit wounds a zombie's body, a player's wound bleeds, a bandage stops it and is consumed, and the
   client sees the HUD values.
5. **A bite, infection and the cure.** `ENGINE -class Zombies.Engine.Tests.StatusEffectNetTests`: a bite from a zombie whose data says it
   infects applies infection and wounds, infection worsens by stage as time passes, the player alone is told, and using the antibiotics removes it.
6. **Loot differs by room type.** `ENGINE -class Zombies.Engine.Tests.ContainerNetTests`: opening a Container fills it from its room, a kitchen
   and a bathroom give different loot, two players opening the same Container see the same contents, and a move is applied by the Server.
7. **Parts lost and ragdolls.** `ENGINE -class Zombies.Engine.Tests.GoreTests`: a dead zombie collapses as a ragdoll pushed along the hit, a lost
   part becomes its own bodies, every client makes the same decals and ragdoll from the same seed, and the gore setting (off, low, high) changes what
   is drawn and never what the Server decides.
8. **A Code mod.** `dotnet run --project tools/SimHarness --configuration Release --no-build -- codemods mods` runs the sample Code mod as a
   Server, a client and solo, and checks sides and message numbers.

## Gaps filed from writing this script

| Issue | Gap | State |
| --- | --- | --- |
| #147 | The client cannot join a dedicated server, so there is no two-player demo | Done |
| #148 | Nothing in the running game creates zombies or lets them attack | Done |
| #149 | The client sends no combat or item commands, and its inventory is a local sandbox | Done |
| #150 | World containers cannot be opened, so room-based loot never reaches a player | Done |
| #151 | Chunk meshing is at its 2 ms budget in the harness, and the two measurements disagree | Open |
| #152 | Steady-state allocation and tick time are not robust under CPU load; CI allocation tests flake | Open |
| #153 | The floor tier, the GPU time and the VRAM are not measured | Open |
| #154 | One ~54 ms frame in the client benchmark | Open |

The demo now runs in the client against a dedicated Server, with the session file's steps ticked off as it goes. The performance gaps (#151 to
#154) remain open and are measured separately.