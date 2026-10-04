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

## Items and Inventory

**Item**:
A definition of a thing a player can hold, with unit mass, unit volume, and stack size.
_Avoid_: Object, thing

**Stack**:
A quantity of identical items occupying one place in a container.
_Avoid_: Pile, group

**Container**:
Anything that holds stacks within a volume limit and a mass limit.
_Avoid_: Bag, storage, slot

**Wear state**:
Properties of an item that change through use: wetness, blood, condition.
_Avoid_: Damage, dirtiness

## Clothing and Armor

**Layer**:
One of the stacked positions on a body part where a worn item sits: underwear, base, mid, outer, armor.
_Avoid_: Tier, level

**Coverage**:
The set of body parts a worn item protects and insulates.
_Avoid_: Mask, area

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

**Modifier**:
A JSON-defined change to a stat, recipe availability, or interaction, granted by a perk, item, or condition.
_Avoid_: Buff, effect

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
A generated cluster of structures with a faction, population, and trade stock.
_Avoid_: Town, base

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
A validated request from a client to change game state, such as moving an item or crafting.
_Avoid_: Action, request

**Cosmetic event**:
A server event that clients turn into visuals and sound only, such as blood splatter or a ragdoll.
_Avoid_: Effect
