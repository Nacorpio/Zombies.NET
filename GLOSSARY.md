# Zombies.NET

A first-person, co-op zombie survival game on a procedural voxel world, built on a custom .NET engine and fully moddable. The domain is split into bounded contexts that share the `Items` kernel and communicate through domain events.

## Mods

**Base mod**:
The game in its original state. Ships as an ordinary mod loaded by the same loader as any other.
_Avoid_: Core, vanilla, built-in content

**Data mod**:
A mod made only of JSON definitions. Safe by nature.
_Avoid_: Content pack, resource pack

**Code mod**:
A mod that ships C# assemblies. Trusted, and warns the player on install.
_Avoid_: Plugin, script

**Content ID**:
A namespaced identifier of the form `namespace:name` that uniquely names a definition.
_Avoid_: Key, slug, internal name

**Patch**:
A JSON Merge Patch from one mod that edits a definition owned by another. Besides merging fields it can use the operators `extend`, `delete`, `relative` and `proportional` to change part of an array or number, resolved before the result is validated. A definition can also inherit another with `copy-from`.
_Avoid_: Override, tweak

**Override**:
An explicit declaration that a mod replaces a whole definition with the same Content ID.
_Avoid_: Patch, replace

**Migration**:
A definition that maps an old Content ID to a new one, or marks it removed, so a Save made before a mod renamed or removed something still loads. Chains such as A to B to C resolve to the last id, and a cycle is rejected when the mods load. Not the same as a Schema version step, which changes the layout of a Save.
_Avoid_: Obsoletion, alias, redirect

**Icon**:
A named 32 by 32 picture used by the UI, supplied as a PNG named after the icon in a mod's icons folder and normalized to hard edges when loaded. A mod can add Icons and replace those of mods it depends on.
_Avoid_: Sprite, glyph

**World option**:
A tunable rule of one world, declared by a mod with a Content ID, type, range, default, and description. The host chooses values when the world is created, the save keeps them with the mod list, and a client must hold the same values as the Server to join. Visual and accessibility settings such as gore intensity are not World options.
_Avoid_: Setting, config, game rule, difficulty setting

## Items and Inventory

**Item**:
A definition of a thing a player can hold, with unit mass, unit volume, and stack size.
_Avoid_: Object, thing

**Stack**:
A quantity of identical items, meaning the same item and the same item state, occupying one place in a container.
_Avoid_: Pile, group

**Item state**:
Data one particular item carries beyond its definition, such as condition, loaded ammo, or attached items. Items with no state are interchangeable.
_Avoid_: Instance data, metadata, properties

**Container**:
Anything that holds stacks within a volume limit and a mass limit.
_Avoid_: Bag, storage, slot

**Wear state**:
The part of item state that changes through use: wetness, blood, condition.
_Avoid_: Damage, dirtiness

**Condition**:
How much of its durability an item has left, from broken to pristine.
_Avoid_: Health, durability (as a current value), integrity

**Fault**:
A specific kind of wear an item carries in its item state, such as a chipped blade, a jammed action or ripped seams. A Fault definition declares the Stat changes it causes (a weapon Fault changes handling or reliability, an armor Fault changes protection), how an item gains it, and the Repair that removes it. Stacks with different Faults are different Stacks.
_Avoid_: Damage, defect, malfunction

**Repair**:
The Item action that removes a Fault: it uses up the item the Fault's definition names, takes the time the definition gives, and leaves the item without that Fault. Not the same as raising Condition.
_Avoid_: Fix, mend, restore

## Loot

**Loot table**:
A weighted set of entries that is rolled a random number of times to decide what a place contains.
_Avoid_: Drop table, spawn table

**Loot entry**:
One item a loot table can produce, with a weight and a count range.
_Avoid_: Drop, slot

**Seed**:
The number that makes a roll repeatable: the same table and seed always produce the same loot.
_Avoid_: Random state, salt

## Clothing and Armor

**Layer**:
One of the stacked positions on a body part where a worn item sits: underwear, base, mid, outer, armor.
_Avoid_: Tier, level

**Coverage**:
The set of body parts a worn item protects and insulates.
_Avoid_: Mask, area

## Weapons

**Weapon**:
An item that deals damage, defined by a category, base stats, and the mounts it offers.
_Avoid_: Gun, tool

**Weapon category**:
A named kind of weapon, such as pistol or blunt melee, that sets shared handling, ammo class, and default mounts. Identified by a Content ID so mods can add more.
_Avoid_: Weapon type, class

**Mount**:
A named place on a weapon where one attachment can be fitted, such as muzzle or optic.
_Avoid_: Slot, rail, socket

**Attachment**:
An item fitted to a weapon's mount that changes its stats and appearance.
_Avoid_: Mod, accessory, upgrade

**Held weapon**:
The weapon a character or zombie carries in its hands.
_Avoid_: Equipped weapon, wielded item

## Combat

**Body part**:
One of the separately tracked regions of a character or zombie that can be hit, wounded, or lost.
_Avoid_: Limb, hitbox

**Wound**:
An injury on a body part with a type, severity, and bleed rate.
_Avoid_: Damage, injury

**Wound kind**:
A definition of a named sort of Wound, such as a scratch or a bite: the damage that causes it, how it bleeds, how long it takes to heal, and the chance that an untreated Wound worsens into another kind instead.
_Avoid_: Wound type, injury kind

**Treatment**:
A definition of what applying an Item to a Body part does to its Wounds: which Wound kinds it removes or adds, whether it stops bleeding, how long it takes, and the Item it consumes. A bandage is a Treatment.
_Avoid_: Cure, medicine

**Limb score**:
How well a capability such as movement, grip, manipulation, blocking, or vision works, from 0 to 1, computed from the health, Wounds, and Missing parts of the Body parts that perform it and the encumbrance of what is worn there. It is exposed as a Modifier and never drops below its floor.
_Avoid_: Skill, penalty

**Dismemberment**:
The loss of a body part from damage.
_Avoid_: Gibbing, severing

**Missing part**:
A body part a zombie lacks, either lost in combat or absent from spawn.
_Avoid_: Amputation

## Zombies

**Zombie type**:
A definition with base stats, traits, senses, and a loot table.
_Avoid_: Class, species

**Trait**:
A composable behavior that gives a zombie type its unique abilities, such as runner or bloater.
_Avoid_: Perk, ability

**Level**:
The strength tier of an individual zombie, derived from region danger and world time.
_Avoid_: Rank, tier

**Zombie spec**:
The seed, type, and level from which a zombie's appearance, outfit, and missing parts are derived.
_Avoid_: Zombie data, blueprint

**Weakpoint**:
A small region inside a body part of a zombie type where a hit does more damage and may stagger the zombie, such as the eyes in the head. The Server resolves it from where a hit lands; a harder weakpoint is a smaller target.
_Avoid_: Critical zone, headshot box

**Evolution**:
A zombie type upgrading into another after a number of world days. The zombie spec derives the type from its seed and the world's age, so nothing is saved per zombie.
_Avoid_: Mutation, promotion

**Outfit table**:
A weighted definition of what clothing, headwear, and backpacks a zombie type can spawn wearing.
_Avoid_: Loadout

## Rig and Animation

**Skeleton**:
A generic tree of bones read from JSON, each bone carrying voxel boxes on the character atlas, plus attach points and the setup for the procedural walk and look-at. A player, a zombie, and the first-person arms are all skeletons.
_Avoid_: Rig (for the data itself), armature, model

**Clip**:
A named JSON keyframe animation, such as walk or reload, stated as changes from a skeleton's rest pose so one clip fits any skeleton that has the bones it names.
_Avoid_: Animation (for the data), take, sequence

**Attach point**:
A named place on a skeleton's bone where something else is drawn: the hand that holds a Held weapon, or a backpack. A weapon's Mounts are placed from the attach point it is held at, so any weapon can be drawn on any skeleton.
_Avoid_: Socket, hardpoint, anchor

**Crawl variant**:
The clip named like another with `_crawl` added, played instead of it by a character with a Missing leg.
_Avoid_: Limp, injured animation

## Creatures and Status Effects

**Creature**:
Anything alive in the world that has a body: a player, a zombie, an animal, or an NPC.
_Avoid_: Entity, actor, mob

**Status effect**:
A lasting state on a creature, such as infection or a painkiller, that runs for a duration, may progress through stages, and may apply modifiers or periodic changes until it ends or is cured.
_Avoid_: Buff, debuff, condition, ailment, status

**Substance**:
A definition of something a creature takes in through Items, such as a stimulant or alcohol: the Status effect each dose applies, the chance a use causes addiction, how fast Tolerance grows, and the withdrawal effect that follows addiction.
_Avoid_: Drug, consumable

**Tolerance**:
How used to a Substance a creature has become, from 0 to 1, counted per creature and Substance. Each dose raises it, it cancels part of the doses of the next use, and it makes addiction likelier. It is exposed as a Modifier with the source `tolerance:` and the Substance's Content ID, and it is kept in the Save.
_Avoid_: Resistance, immunity

**Animal**:
A non-hostile or predatory wild creature defined by a type, a behavior archetype, and the habitats it lives in.
_Avoid_: Wildlife, critter, fauna

**Habitat**:
A kind of living environment, named by a tag on a biome, that decides which animals may spawn there.
_Avoid_: Biome (a habitat is a tag, not the whole biome), ecosystem

## Survival and Skills

**Need**:
A survival meter the player must manage: hunger, thirst, body temperature, fatigue.
_Avoid_: Stat, vital

**Fatigue**:
The Need that grows while a player is awake and falls while they sleep, from 0 rested to 1 when they collapse asleep. Its levels, rested, tired, and exhausted, each apply a Status effect whose Modifiers worsen the player's stats. An Item can take some of it away.
_Avoid_: Stamina, energy, sleepiness

**Rest place**:
Where a player sleeps, on the ground, in a shelter, or in a bed, which sets how fast sleep recovers Fatigue. A sleeper wakes to a noise within half the distance it carries, to damage, or when fully rested, and other players see that they sleep.
_Avoid_: Bed (a bed is one kind), camp

**Morale**:
How a character feels, from low to high: the sum of the Morale sources that are active, clamped to a range. A Morale source is a definition of what triggers it, a game event or the use of an Item, how much it moves Morale and how long it takes to fade. The band Morale is in grants Modifiers, recorded with the source `morale:` and the band's Content ID. Morale is not a Need.
_Avoid_: Mood, sanity, happiness

**Skill**:
A trained capability that levels up through use.
_Avoid_: Ability, attribute

**Perk**:
An upgrade bought with points earned from a skill level, found in that skill's perk tree.
_Avoid_: Talent, trait

**Stat**:
A named number on a weapon, creature, or tool, such as recoil or move speed, whose effective value is its base value changed by the Modifiers on it. Needs are not Stats.
_Avoid_: Attribute, property

**Modifier**:
A JSON-defined change to a stat, recipe availability, or interaction, granted by a perk, an attachment, or a status effect, and removed when its source is removed.
_Avoid_: Buff, bonus, boost

**Profession**:
A definition of what a player starts with and is good at: starting items, what they wear, loot tables rolled for more items, and Modifiers. Each player picks their own when they join, and the Server grants the same loadout to the same player in the same world every time.
_Avoid_: Class, job, loadout (that is only the items)

**Scenario**:
A definition of where, when and in what state a world's players begin: a kind of start location, a time of day, and a starting condition of Needs and Wounds. The host picks one when the world is created.
_Avoid_: Challenge, start mode, difficulty

**Movement mode**:
How the player moves, declared by a mod with a Content ID: walking, sprinting and crouching are the base modes. Each has a speed, noise and Stamina multiplier, and is chosen by the input that asks for it. A mod adds one by declaring it with a higher priority for the same input.
_Avoid_: Gait, stance, movement state

**Stamina**:
The pool a player drains by moving in a costly Movement mode or while carrying more than they comfortably can, and recovers at rest. An empty pool exhausts the player, who cannot sprint until it has recovered. Not a Need, because it recovers by itself within seconds.
_Avoid_: Energy, endurance

**Noise**:
How loud the player is this tick, as a multiple of a walking step, from the Movement mode they move in. It is only a value on the player for systems that listen; nothing reacts to it yet.
_Avoid_: Sound, volume

## Death

**Corpse**:
The Container a dead player leaves where they died, holding everything they carried and wore with its Item state intact. Any player can loot it, and it persists in the Save until it is emptied.
_Avoid_: Body (that is the Combat context's), grave, loot bag, death drop

**Spectator**:
A player whose Body has died. The Server rejects every command from them until they respawn, and clients see them as dead.
_Avoid_: Ghost, observer, dead player (as a state name)

**Respawn**:
A Spectator coming back to life after the configured delay at the spawn point, with a fresh Body and fresh Needs and nothing carried. The death policy decides whether they respawn at all.
_Avoid_: Revive, resurrect

**Memorial**:
The record of one death: the player's name, the days they survived, the zombies they killed, and the cause of death the Combat context raised. It outlives the Corpse.
_Avoid_: Obituary, scoreboard entry, death log

## Statistics and Goals

**Statistic**:
A JSON-defined number kept per player that domain events add to, such as zombies killed or distance walked. It names the events it counts, what each adds, and whether it lasts a player's lifetime or one Run. Statistics persist in the Save and survive a Respawn.
_Avoid_: Stat (that is a Modifier's target), counter, metric

**Run**:
One life of a player, from joining or respawning until their Body dies. A Statistic that counts one Run starts again from zero at the next Respawn, and its final value is a score on the end-of-run screen.
_Avoid_: Session (that is a connection), game, round

**Achievement**:
A JSON-defined goal that names a Statistic, a comparison and a target. It is completed the first time the Statistic meets it, once per player, and completing it raises a replicated event and a toast.
_Avoid_: Trophy, badge, quest

**Conduct**:
A JSON-defined self-imposed restriction that names a Statistic, a comparison and a target, such as killing no zombie. It is kept when the comparison still holds as the Run ends, and the end-of-run screen lists the Conducts kept.
_Avoid_: Challenge, rule, penalty

## World

**Chunk**:
A fixed-size column section of voxel terrain that is generated, meshed, and streamed as a unit.
_Avoid_: Tile, region

**Region**:
A large grid cell of the world where a settlement site, road graph, and danger level are decided.
_Avoid_: Zone, chunk

**Biome**:
A named climate and terrain type that sets terrain, vegetation, and weather odds.
_Avoid_: Zone, climate

**Structure**:
A prefab voxel construction stamped into the world during generation.
_Avoid_: Building, prefab

**Settlement**:
A generated cluster of structures with a faction, population, and trade stock, or an abandoned one with none.
_Avoid_: Town, base

**Settlement type**:
A JSON definition of a kind of settlement: its structures, area types, zombie spawns, danger range, rarity, and whether it is inhabited or abandoned.
_Avoid_: Settlement template, preset

**Area**:
A room or labelled part of a structure, with an area type that decides what its containers hold.
_Avoid_: Zone, room, region

**Area type**:
A JSON definition, such as kitchen or armory, that maps container kinds to loot tables, optionally shifted by danger level.
_Avoid_: Room type, zone type

**Danger level**:
How hostile a region is, which scales the level of zombies spawned there.
_Avoid_: Difficulty, threat

## Economy and Power

**Faction**:
A group of NPCs with shared reputation toward the player.
_Avoid_: Clan, team

**Value**:
The abstract worth of an item used when bartering.
_Avoid_: Price, cost

**Deployable**:
An item placed in the world as an entity, such as a generator, light, or cable.
_Avoid_: Building, placeable, block

**Power network**:
A connected graph of producers, consumers, and cables sharing electricity.
_Avoid_: Grid, circuit

## Networking

**Server**:
The authoritative simulation. Always present, embedded in-process for solo play.
_Avoid_: Host

**Domain command**:
A validated request from a client to change game state, such as moving an item or crafting. An item action is what the player picks; the domain command is what it sends.
_Avoid_: Request, order

**Join**:
A client's request to play on a Server, refused unless its protocol version, world generator version, and every mod's version and content hash match the Server's.
_Avoid_: Login, handshake

**Snapshot**:
The state of the entities near one client's player at one tick, sent as a change list against the last snapshot the client acknowledged.
_Avoid_: Update, sync, world state

**Interest radius**:
How many chunks around a client's player the Server replicates to that client.
_Avoid_: Relevancy, view distance

**Cosmetic event**:
A server event that clients turn into visuals and sound only, such as blood splatter or a ragdoll.
_Avoid_: Effect

**Player input**:
What the player is asking for in one tick: the two movement axes, the look angles, and the sprint, crouch, lean, and jump flags. The client samples its keys into one and sends it; the Server applies it.
_Avoid_: Key state, control message

**Movement model**:
The one function that turns a player's state and one input into the next state, run by the Server and replayed by the client so both reach the same position.
_Avoid_: Physics step, locomotion

**Prediction**:
A client running the movement model on its own input before the Server confirms it, so the player moves the moment a key is pressed.
_Avoid_: Extrapolation, client-side movement

**Reconciliation**:
Rewinding a client's predicted state to the last input the Server acknowledged, taking the Server's position, and replaying the inputs since. A prediction that was right replays to the same place.
_Avoid_: Correction, rollback, rubber-banding

**Step-up**:
Walking up a ledge no taller than the step height without jumping.
_Avoid_: Auto-jump, stair climbing

## Saves

**Save**:
The one SQLite file that holds a world: its seed, generator version, mod list, chunk edits, and the domain aggregates. A Server loads it at start and refuses it when the generator or the mods differ.
_Avoid_: Savegame, world file, database

**Chunk edit**:
A block a player changed in a Chunk, stored as the difference from what the world generator makes, so an untouched block always follows the generator.
_Avoid_: Modified chunk, chunk delta

**Schema version**:
The number stored in a Save that says which layout its tables have, so a newer build can migrate an older Save forward and an older build refuses a newer one.
_Avoid_: Save version, format version

## Interface and Tooling

**Icon**:
A small named pixel image, 16 by 16, used in menus, badges, and the HUD.
_Avoid_: Glyph, sprite, symbol

**Context menu**:
A short list of entries for the one thing the player pointed at, opened with the secondary button.
_Avoid_: Right-click menu, popup, dropdown

**Item action**:
Something a player can do with a stack, defined as data with the conditions under which it applies, such as use, equip, drop, or split. Choosing one sends a domain command.
_Avoid_: Verb, option, menu item

**Debug session**:
A run of the game started to inspect, test, or demonstrate something, described by why it exists and optionally a duration, steps, and key facts.
_Avoid_: Test run, automation run, agent session

**Session badge**:
The small panel in the top-right corner that tells a viewer about the current debug session. Compact by default, expanded on request.
_Avoid_: Banner, watermark, HUD

**Screen**:
One of the game's full-window interfaces, such as the inventory, the body screen, or the options. A screen is described by a layout and drawn over the world.
_Avoid_: Menu, page, window

**Layout**:
A screen described as JSON: a tree of widgets with anchors, sizes, and string table keys. A mod can add one or replace one of the same id.
_Avoid_: Markup, template, form

**Widget**:
One node of a layout: a panel, a label, a bar, or a row or column that stacks its children.
_Avoid_: Control, element, node

**Anchor**:
Where a widget sits inside its parent, with an offset measured inward from that edge, so a widget stays on screen when the parent grows.
_Avoid_: Alignment, position, dock

**UI values**:
What a screen shows right now, by widget id: text, a bar's fill, a color role, and whether a widget is drawn. Kept apart from the layout, which does not change.
_Avoid_: State, model, binding

**Color role**:
What a color means, such as good, warning, or danger. Layouts and screens ask for a role, never a color, so the palette can change without touching them.
_Avoid_: Color name, theme color, style

**Palette**:
The set of colors the UI draws with. The colorblind palette tells states apart by lightness and by hue pairs that stay distinct for red-green color blindness.
_Avoid_: Theme, skin, colors

**String table**:
The text of one language, as a JSON file of keys and translations in a mod's `lang/` folder. A key the chosen language lacks falls back to English, and a key nobody has shows as the key itself.
_Avoid_: Translation file, locale, resource

**Localizer**:
The object that looks text up by key in the chosen language, merging the string tables of the loaded mods in load order.
_Avoid_: Translator, i18n, resource manager

**Tooltip**:
A short block of text shown beside the pointer, kept to a few short lines so it can be read at a glance.
_Avoid_: Hint, help text, popup

**Dialog**:
A modal question with a title, a concise message, an optional caption, and buttons. The mouse and the keyboard both choose a button.
_Avoid_: Modal, alert, prompt

**HUD**:
The always-on overlay: health, blood, bleeding, hunger, thirst, warmth, fatigue, and the weapon in hand.
_Avoid_: Overlay, status bar, heads-up display

**Body screen**:
The screen that shows every body part with its health and what is wrong with it, such as a wound that bleeds or a part that is gone.
_Avoid_: Health screen, damage panel, character sheet

**Inventory view**:
The model behind the inventory screen: the slots of each container, and the drag that moves a stack between them. The list and the grid are two ways of drawing the same slots.
_Avoid_: Inventory model, item grid, backpack UI

**Drag**:
Picking a stack up and dropping it into another container. Nothing moves until the drop, and a drop that cannot happen leaves everything as it was and says why.
_Avoid_: Move, transfer, drag-and-drop

**Key binding**:
Which key does each game action. Every action has exactly one key and no key serves two actions, so a press is never ambiguous.
_Avoid_: Hotkey, shortcut, control scheme

**Game action**:
Something the player can do with a key, such as moving, opening the inventory, or freeing the mouse. The game reads input through actions, never through raw keys.
_Avoid_: Command, input, verb

**UI scale**:
How large the UI is drawn, 1 being the normal size. Bitmap text never draws below scale 1, since it cannot be drawn smaller.
_Avoid_: DPI scale, zoom, font size
