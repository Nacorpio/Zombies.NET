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
A JSON Merge Patch from one mod that edits a definition owned by another.
_Avoid_: Override, tweak

**Override**:
An explicit declaration that a mod replaces a whole definition with the same Content ID.
_Avoid_: Patch, replace

**Icon**:
A named 32 by 32 picture used by the UI, supplied as a PNG named after the icon in a mod's icons folder and normalized to hard edges when loaded. A mod can add Icons and replace those of mods it depends on.
_Avoid_: Sprite, glyph

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

**Outfit table**:
A weighted definition of what clothing, headwear, and backpacks a zombie type can spawn wearing.
_Avoid_: Loadout

## Creatures and Status Effects

**Creature**:
Anything alive in the world that has a body: a player, a zombie, an animal, or an NPC.
_Avoid_: Entity, actor, mob

**Status effect**:
A lasting state on a creature, such as infection or a painkiller, that runs for a duration, may progress through stages, and may apply modifiers or periodic changes until it ends or is cured.
_Avoid_: Buff, debuff, condition, ailment, status

**Animal**:
A non-hostile or predatory wild creature defined by a type, a behavior archetype, and the habitats it lives in.
_Avoid_: Wildlife, critter, fauna

**Habitat**:
A kind of living environment, named by a tag on a biome, that decides which animals may spawn there.
_Avoid_: Biome (a habitat is a tag, not the whole biome), ecosystem

## Survival and Skills

**Need**:
A survival meter the player must manage: hunger, thirst, body temperature.
_Avoid_: Stat, vital

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
