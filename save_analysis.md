# LET IT DIE save analysis

## Purpose and scope

This document compares the exported saves at `data/76561197974144168.json` and `data/76561197974144168_old.json`, explains the major game domains they manage, and turns those findings into feature ideas for LidUtils.

The first file is a small, early-progress snapshot; the older file is materially more populated and supplies later-game inventory, research, collection, quest, tower, archive, and TDM schemas. Names and behavior are **confirmed** where the existing LidUtils catalog or feature code already documents them, **inferred** where the JSON shape and identifiers make the purpose clear, and **unknown** where the data looks cached, transient, or server-controlled. A field's presence does not by itself prove that changing it is safe.

The snapshots contain:

- The same 51 top-level keys in both files.
- Current: about 122 KB, 1,296 objects, 70 arrays, and 6,300 scalar leaves.
- Older/higher-progress: about 2.4 MB, 4,545 objects, 196 arrays, and 20,006 scalar leaves.
- The current snapshot has no JSON booleans or nulls. The older snapshot proves that types can vary: `/soul/current_died_cid` is the JSON boolean `false` rather than the current snapshot's empty string.
- Several values that contain a second format inside a JSON string: nested JSON, comma-delimited IDs, or numeric text.

The file also contains a player name and platform/account identifier. Exported JSON should therefore be treated as personal data when used in issues, tests, or documentation.

## Safety and confidence labels

| Label | Meaning |
|---|---|
| Confirmed | Already represented by the curated save catalog or existing feature logic. |
| Strong inference | The schema, IDs, and cross-references give a clear purpose, but behavior still needs comparison against more saves. |
| Tentative | The likely domain is visible, but individual values or legal transitions are not established. |
| Read-only candidate | Data appears derived, transient, cached, online-facing, or too interdependent for ordinary editing. |

For feature planning, risk is described as:

- **Low:** an existing scalar with known meaning and validation rules.
- **Medium:** several existing scalars must change together, or an ID must be selected from known game data.
- **High:** records must be inserted/removed, hashes or timestamps may need regeneration, or the area appears server-derived/transient.

## Save architecture at a glance

The save is not a flat settings file. It is closer to a small relational document database:

- `uid` selects an ownership/account context, but the local player's key is not fixed. The current save uses `1`; the older save uses `117305`. Player-keyed paths must therefore resolve `<uid>` from `/user/uid` or `/soul/uid`, never hard-code `/1`. Negative keys such as `-1` and `-2` appear to represent other or special contexts.
- `cid` identifies a player-owned fighter and joins roster, slot, stat, bag, and equipped-skill records.
- `eid` identifies an equipment item, consumable, mushroom, or beast and joins entity records to locker/death-bag slots and floor placement records.
- `zid` joins a zombie record to its stats, equipment, skills, mastery, rewards, and placement.
- `did` joins dead/hostile-character records and can also link a zombie to its source character.
- Arrays frequently act as tables. Their numeric JSON paths are positions, not stable identities.

This matters to the editor: changing a fighter or item safely requires resolving the whole aggregate, not editing whichever array row happens to contain a matching-looking number.

## High-level section map

| Area | Main JSON paths | What it manages | Confidence | Edit posture |
|---|---|---|---|---|
| Account and session | `/user`, root session fields, `/login_bonus` | Identity, locale, login history, timestamps, shop rotations, current session markers | Mixed | Locale may be editable; identity and session data should be read-only |
| Player core | `/soul` scalar fields | Wallet, rank, TDM summary, location, facility levels, continues, recovery state | Mixed | Known values are suitable for curated controls |
| Fighters | `/soul/chr`, `/bodyuser/<uid>` | Roster, active/free/guard state, names, appearance/class, XP/HP and allocated stats | Strong inference | Composite editor only |
| Bags and loadouts | `/soul/deathbag`, `/soul/cl`, `/soul/skl/eqskl` | Per-fighter bag slots, locker slots, equipped items and decals | Strong inference | Resolve all referenced entities before edits |
| Inventory | `/part`, `/item`, `/mushroom`, `/beast` | Equipment, normal items, mushrooms, beasts, ownership and floor placement | Strong inference | Existing-record edits first; creation/removal is high risk |
| Skills and mastery | `/soul/skl`, `/soul/expert` | Owned/equipped decals, gacha state, weapon mastery/proficiency | Strong inference | Viewer first; edits need legal ID/range data |
| Research and collections | `/soul/partresearch`, `/soul/msrbook`, `/soul/bstbook`, `/soul/magazine`, `/soul/hubcustom` | R&D progress, discovered items, collectibles, waiting-room customization | Strong inference | Curated presets after state semantics are verified |
| Quests, mail, rewards | `/soul/quest`, `/soul/mail`, `/soul/present`, `/soul/mysterybag` | Quest buckets/hash, inbox state, presents, mystery-bag generation counters | Tentative | Mostly viewer/read-only initially |
| Tower progression | `/soul/openelvflr`, `/soul/areaflag`, `/soul/areaescflag`, `/gameflg` | Elevator access, area/escalator state, tutorials and unlock flags | Strong domain, weak transition knowledge | High-risk presets only after multi-save comparison |
| Current floor simulation | `/floor`, current location fields in `/soul` | Spawned enemies, gates, boxes, floor placements, match/runtime state | Strong inference | Read-only/diagnostics |
| Dead characters and zombies | `/diedchara`, `/zombie` | Other/dead fighters, Haters/zombies, equipment, skills, rewards and placements | Strong inference | Read-only until complete aggregate rules are known |
| TDM, team and defense | `/team`, `/teammember`, `/fort*`, `/war*`, `/tdmsituation`, announcements | Team directory/cache, raids, defense, wars, TDM status and rewards | Tentative/server-like | Read-only |
| History and statistics | `/playlog` | Lifetime combat, economy, collection, death, fighter and TDM statistics | Strong inference | Dashboard; editing offers little user value |
| Configuration | `/cfgmenu`, `/soul/quick_config`, language fields in `/user` | Game/UI configuration and localization | Mixed | Curated locale/options only; nested-string validation required |

## What the higher-progress save adds

The older file is not just the same schema with larger numbers. It demonstrates different player-key namespaces, variable-size containers, fields whose JSON types change, and object/array shape changes. Those are compatibility requirements for the editor. The files are not a controlled one-action before/after pair, so their differences establish possible shapes and invariants—not the exact mutations needed to reach those states.

| Domain | Current snapshot | Older/higher-progress snapshot | Main conclusion |
|---|---:|---:|---|
| Player namespace | `uid = 1` | `uid = 117305` | Resolve `<uid>` dynamically throughout the document |
| Fighters | 2 fighters, 3 roster slots | 8 fighters, 9 roster slots | Roster size is variable; old states include 1 `USE`, 5 `GUARD`, 2 `FREE` |
| Death Bags | 20 rows per fighter | 19–39 rows per fighter | Capacity is structural, not a confirmed `/soul/bag_slot` scalar |
| Storage locker | 30 rows, 3 occupied | 290 rows, 277 occupied | `/soul/cl` is a variable-size slot table |
| Player equipment | 8 `/part/pts/<uid>` rows | 90 rows | Inventory UI must scale and use definition-name lookup |
| Normal items | 1 row | 170 rows | Item browsing/filtering is a first-class feature, not an edge case |
| Mushrooms / beasts | 17 / 9 rows | 136 / 38 rows | Ownership/container views are essential |
| Owned/equipped decals | 1 / 1 rows | 42 / 20 rows | Decal collection and per-fighter loadouts deserve a dedicated view |
| Research | 4 rows, 2 definitions | 227 rows, 98 definitions | Research is multi-row state/history, not a single unlock flag |
| Quests | Empty collections | 154 user rows plus three 154-key maps | Quest state is structural and hash-coupled |
| Mail / presents | 10 / empty | 97 / 52 rows | Inbox and reward history become meaningful read-only features |
| Elevators | 2 rows | 25 rows | Tower progression spans structural lists and packed/game flags |
| Game flags | 84 client + 10 server rows | 326 client + 11 server rows | Unlock operations require scenario-level presets |
| TDM state | Mostly empty/cache-like | Team membership, 286 hostility rows, 72 assault entries, 5 defense fighters | Online/TDM data is real but remains server-facing and read-only |

### Higher-progress fighter and inventory evidence

The eight older fighters cover classes `BAL`, `BRE`, and `COL`, grades 1–4, and levels 25–98. Roster slots 0–7 are occupied and slot 8 is empty. Representative joined records include an active grade-4 Brawler at level 98 and a guarded grade-4 Collector at level 91. Five fighters are in `GUARD` state, confirming that a fighter page must support more than a simple active/inactive toggle.

The older save has eight fighter-keyed Death Bags with 19, 20, 21, 30, 33, 36, or 39 rows. Its active fighter has 21 rows, while another has 39. This disproves the idea that the 20 rows seen in the current save are a universal capacity.

Across the older Death Bags and locker, all 366 nonempty `eid` references resolve to `/part/pts`, `/item/items`, `/mushroom/msrs`, or `/beast/bsts`; no dangling references were found. This is strong evidence for building a save graph/index and using it to power both the UI and pre-save validation.

### Higher-progress research, collections, and rewards

The 227 `/soul/partresearch/user` rows cover 98 equipment definitions and levels 1–5. Observed state combinations include `FINISHED/FINISHED`, `FINISHED/CHARGE`, `MAP/UNKNOWN`, `LEVELUP/UNKNOWN`, and `REMODEL/UNKNOWN`. Research rows therefore encode lifecycle/history states, not merely current equipment level.

The older save expands the mushroom book from 5 to 41 rows and the beast book from 1 to 22 rows. `/soul/hubcustom` contains flags `0`, `1`, `2`, `4`, and `6`, showing that a binary unlocked checkbox would lose information. `/soul/researchstamp` becomes a six-row `{type, rate}` table with rates from `1.4` to `4.0`.

`/soul/quest/user` becomes a 154-row list with 20 completed rows, while `dis`, `dfs`, and `dss` become parallel 154-key maps. The quest hash also differs between snapshots. `/soul/present` changes from an empty object to a 52-row array containing money, SPLithium, skill, and material rewards from quests, defense, TDM bonuses, wars, login rewards, and administration. Claiming or creating rewards should remain read-only until a controlled before/after pair reveals every companion change.

### Higher-progress tower, configuration, and TDM evidence

The older save has 25 elevator rows: the hub plus entries in the `MET`, `AMS`, and `ARC` families. Area flags grow from 2 to 78 rows and escalator flags from 2 to 94 rows. Client flags include later progression names such as `KGF_AMS_FIRST_ELEVATOR` and `KGF_ARC_BOSS_CLEAR`. This supports a readable progression dashboard, but also confirms that “unlock elevator” must be a tested structural preset across several stores.

`/cfgmenu/obj` is an empty string in the current save but contains an embedded JSON settings object in the older save. `/shpprd` changes from an empty object to a seven-row product array. Readers must tolerate these shape changes rather than binding each path to one assumed type.

The older player belongs to team `130` and has populated hostility, assault, join-war, TDM payload, and fort-defense data. `/tdmsituation/*/data` contains encoded JSON arrays in the older save rather than the current save's `"{}"` strings. These findings strengthen—not weaken—the decision to keep multiplayer state read-only.

### Higher-progress runtime and recovery evidence

Floor table sizes depend on the current area/run: the current snapshot has 128 gates, while the older snapshot has 16; the older snapshot instead has more boxes, vending machines, and stamps. Runtime rows must not be used as a static list of globally valid game content.

The older snapshot is not in an active death/recovery state, but it contains two `/soul/prison/<uid>` slots with zeroed spirit, ransom, and expiry fields. `/soul/current_died_cid` is the boolean `false`, whereas the current snapshot stores an empty string at the same path. Recovery structures can therefore exist independently of an active event, and all writes must preserve the loaded scalar type.

The older `/playlog` reaches `max_floor = 27`, `total_play_time = 239475`, `total_research_cnt = 143`, `total_enemy_cnt = 3082`, 135 fort attacks, 218 defenses, and 42 deaths. This reinforces structured comparison and statistics dashboards as valuable features in their own right.

## Detailed findings

### 1. Account, identity, localization, and login state

`/user` has 30 fields. It includes the local `uid`, display name, platform/account identifiers, creation and modification timestamps, login count/streak, last-login date, locale (`country`, `region`, `langsnd`, `langtxt`), premium currency, and automatic-shop lists.

Examples from this snapshot:

- `/user/login_count = 3` and `/user/login_keep = 1`.
- `/user/created = 1788347106` (`2026-09-02 11:05:06 UTC`).
- `/user/modified = 1788523223` (`2026-09-04 12:00:23 UTC`).
- `/user/country = "es"`, `/user/region = "eu"`, `/user/langsnd = "jpn"`, and `/user/langtxt = "int"`.
- Shop availability is encoded in comma-delimited strings such as `/user/automaticshop_buyable_goods_ids`, rather than structured arrays.

Root fields such as `/next_update_cid`, `/enter_gate`, `/continue_count`, `/unpaidelevatorprice`, and `/fort_back` look like current-session or handoff state. `/login_bonus` contains three `{login_date, is_vip}` records.

Editing identity keys, account IDs, shop rotations, or timestamps could break ownership/server assumptions. Locale is a plausible feature after enumerating supported values. Login streak is already exposed by LidUtils, but it should remain an explicitly labeled gameplay override rather than being grouped with identity.

### 2. Wallet, rank, facilities, continues, and VIP

These are the best-understood values because LidUtils already has composite behavior for them.

| Feature | Paths | Sample | Notes |
|---|---|---:|---|
| Death Metals | `/user/free_medal`, `/user/paid_medal` | `0`, `0` | Current UI writes the free balance and zeroes the paid balance |
| Kill Coins | `/soul/free_money`, `/soul/paid_money` | `19947`, `0` | Same free/paid coupling |
| SPLithium | `/soul/spirit` | `556` | Known currency |
| Bloodnium | `/soul/bloodnium_point` | `0` | Known currency |
| RE Points | `/soul/recycle_point` | `0` | Known currency |
| Player rank | `/soul/rank`, `/soul/rank_point` | `2`, `200` | Rank points are derived and staged with rank |
| KC Bank level | `/soul/safe_level` | `1` | Existing curated Waiting Room field |
| SPL Tank level | `/soul/spirit_tank_level` | `1` | Existing curated Waiting Room field |
| Free continues | `/soul/free_continue_count`, `/soul/free_continue_max_count` | `0`, `0` | Existing paired edit |
| Royal Express | `/soul/vip/*` | inactive | Existing multi-field activate/deactivate workflow |

Replica values such as `/soul/replica_money`, `/soul/replica_spirit`, and `/soul/replica_bloodnium_point` are `-1` sentinels in this snapshot. They should not be assumed to be alternate balances.

The catalog currently documents `/soul/bag_slot`, but that pointer is absent from **both** supplied saves. It should be marked unsupported/unverified, not merely version-dependent. The `bag` field in observed fighter stat rows is always `0` and is not demonstrated to be a capacity. Actual Death Bag capacity is represented structurally by a variable number of slot rows.

### 3. Fighter roster and character progression

The current player owns two fighters under `/soul/chr/chrs/<uid>`. Their `cid` values are referenced by:

- `/soul/chr/slots/<uid>` for roster slot placement.
- `/bodyuser/<uid>` for level, HP, STR, DEX, VIT, STM, LUK, skill, bag, rage, and bonus values.
- `/soul/deathbag/<uid>/<cid>` for the fighter's bag slots.
- `/soul/skl/eqskl/<uid>` for equipped decals/skills.

One fighter is `state = "FREE"`; the other is `state = "USE"`. Fighter records also include body/appearance IDs, fighter type, grade, limit break, carried currencies, XP fields, current HP, name, selected arm slots, and hunter results.

This is a natural first-class feature, but it must be implemented as a joined fighter view keyed by `cid`. Useful controls include renaming, selecting a roster slot, inspecting class/grade, and showing allocated stats. Directly maxing stats, grade, XP, or limit break should wait until legal combinations and derived values are known.

### 4. Death Bag, storage, equipment, and loadouts

In the current snapshot, each fighter has 20 `/soul/deathbag/<uid>/<cid>` slot records. A slot contains `type`, `eid`, equipment `site`, and `arm_slot`. The active fighter has 15 occupied slots; every nonempty `eid` resolves to a record in `/part`, `/item`, `/mushroom`, or `/beast`. The older snapshot proves that bag tables vary from 19 to 39 rows per fighter.

`/soul/cl` is a separate storage/locker table using `{slot, type, eid}`. The current save has 30 rows with three occupied; the older save has 290 rows with 277 occupied.

Equipment records live under `/part/pts` and include the entity ID, definition ID (`ptid`), owner, timestamps, durability (`dur`), level, grade, and other counters. The sample has:

- 8 records under `/part/pts/<uid>`, associated with the local player; the older save has 90.
- 120 records under `/part/pts/-1`, with `uid = -1` and blank owner; these are likely generated/foreign equipment and should not be presented as player inventory.

A safe inventory UI must distinguish the entity instance (`eid`) from its game definition (`ptid`) and the slot that references it. Moving an item means updating ownership/slot references consistently. Adding, cloning, or deleting equipment is substantially riskier than adjusting an existing instance's durability.

### 5. Items, mushrooms, and beasts

The save has separate tables because these entities have different schemas:

- `/item/items`: one conventional item (`ITHEAL_FULL`) with `eid`, acquisition time, owner, and item ID.
- `/mushroom/msrs`: 17 mushroom entities with owner, mushroom ID, effect IDs, state, and placement flags; `/mushroom/flrmsrs` has three floor-placement records.
- `/beast/bsts`: nine beast entities with beast ID, owner, state, level, and a reward-mushroom reference; `/beast/flrbsts` has four floor placements.

Owner values include `USER`, `COIN_LOCKER`, `FLOOR`, `BEAST`, and `VENDING_MACHINE`. A correct inventory page should use these values to show containers and avoid treating every saved record as currently owned by the player.

Beast `rwdemsrid` values can reference mushroom `eid` values, and floor-placement records reuse entity IDs. Those relationships must survive any edit.

### 6. Decals, skills, and weapon mastery

`/soul/skl` contains:

- `psskl`: the owned decal/skill collection, including an ID, count, update time, and checked state.
- `eqskl/1`: equipped skills keyed to fighters by `cid` and slot.
- `gacha`: currently empty box/normal result structures.

`/soul/expert` contains 57 rows shaped like `{ptarmtp, abp, lvl, is_checked}`, consistent with weapon-type mastery/proficiency.

These areas support a useful collection/loadout dashboard immediately. Editing should wait for a database-backed list of valid skill/mastery IDs, slot limits, duplicate rules, and the relationship between mastery XP (`abp`) and level.

### 7. Research, discoveries, and Waiting Room customization

`/soul/partresearch/user` contains four R&D records for two equipment definitions at levels 1 and 2. Each row includes `research_type`, `receive_type`, announcement/check flags, and previous item/level fields. A single blueprint may therefore be represented by multiple state-transition records rather than a simple unlocked flag.

Other collection-like areas include:

- `/soul/msrbook`: five mushroom discovery records.
- `/soul/bstbook`: one beast discovery record.
- `/soul/magazine/status_list`: 36 comma-delimited status values.
- `/soul/hubcustom`: 113 `{cstmid, flg}` records for Waiting Room themes/customization.
- `/soul/armorskin`, `/soul/researchstamp`, and `/soul/unlockfighter`: empty in this snapshot.

Customization and collection features could be excellent checkbox/gallery interfaces, but the meaning of each numeric flag must first be learned from multiple before/after saves. Empty collections are especially important: the current editor cannot populate them because there is no scalar leaf to replace.

### 8. Quests, mail, presents, and mystery bags

`/soul/quest` has a `hash` plus five empty collections (`user`, `ord`, `dis`, `dfs`, `dss`). The hash suggests quest edits may require recomputation or synchronized state.

`/soul/mail` contains ten messages with sender, IDs, title/message, creation time, and checked/old flags. `/soul/present` is empty. `/soul/mysterybag` has five rarity buckets, each with ten `{rarity, cntgen}` rows.

Mail is suitable for a readable inbox/history view. Changing read flags may be possible, but claiming/recreating rewards should not be inferred from message data. Quest and mystery-bag edits are high risk until their transitions are observed.

### 9. Tower unlocks and progression flags

The clearest world-unlock list is `/soul/openelvflr`, which currently contains `ELV_MAIN_HUB` and `ELV_MAIN_MET_FLR_01`. Related progress is spread across:

- `/soul/areaflag` and `/soul/areaescflag`, compact numeric bit/flag records.
- `/soul/areamapflags`, currently an empty string value.
- `/gameflg/cl`: 84 client-like flag records.
- `/gameflg/sv`: 10 server-like flag records.

The game flags include tutorial and unlock-style names such as `KGF_TUTORIAL_*`, `KGF_FIRST_PLAY`, and `SGF_INIT_LIMITBREAK_RESEARCH`, with a value and modification timestamp.

An “unlock floors/elevators” feature would be valuable, but it must be a tested scenario preset that updates every required flag/list together. A raw list of switches would make it easy to create impossible progression states. Unlocking a missing elevator also requires inserting an array record, which the current save engine cannot do.

### 10. Current floor, encounters, dead characters, and zombies

`/floor` is largely a runtime model of the currently generated world. It includes enemy definitions and placements, 128 gates, boxes, stamps, floor-match state, and many currently empty spawn maps. Some reward fields, such as `/floor/jkls/*/rwd`, contain nested JSON encoded as strings.

`/diedchara` contains four foreign/dead-character archive records plus equipped skill records keyed by `did`. `/zombie` contains eight zombies and joins each `zid` across its base record, body levels, equipment, skills, mastery, reward data, and floor placement. Some zombies also reference a dead-character `did`.

These are highly relational and appear partly generated or online-influenced. They are useful for diagnostics—“what is currently spawned?”, “which record owns this equipment?”, or “why is recovery stuck?”—but should remain outside the normal editor until complete lifecycle rules are known.

### 11. TDM, teams, raids, and defense

The save contains a surprisingly large online/TDM surface:

- `/team`: 164 cached team summaries.
- `/tdmsituation`: 120 rows whose `data` values are opaque encoded strings.
- `/teammember`, `/fort`, `/forttutorial`, `/fortmatch`, `/fortorder`, `/fortzmbsetting`, and `/fortresult`.
- `/war`, `/warteam`, `/warteamarc`, `/warperson`, `/warmatchinggrp`, and war-join maps.
- `/termannounce`, `/floorannounce`, and `/defforthubstate` for rewards/status announcements.

Many are empty in this snapshot, while populated team lists look like cached shared data. Editing these values could conflict with authoritative online state. They should be read-only and excluded from “unlock all” or “max all” actions.

### 12. Play history and statistics

`/playlog` has nine fixed groups covering base activity, fighter dispatch, deaths, favorites/frequency maps, fighter lifecycle, TDM, kills, economy, and combat actions.

Examples include:

- `base.total_play_time = 14654`, `base.max_floor = 1`.
- `died.total_died_cnt = 1`.
- `kill.total_enemy_cnt = 47`.
- `user.attack_cnt = 539`, `user.hit_cnt = 320`.
- Money totals and damage totals are sometimes numeric strings rather than JSON numbers.
- `/playlog/famous/*` values are JSON-encoded frequency maps inside strings.

This is ideal for a save-insights dashboard, consistency checks, and before/after comparisons. Editing statistics has little practical value and could create contradictions with achievements or progression.

## Empty areas and what they tell us

Numerous top-level or nested objects are empty in the current snapshot. The older save proves that several of them can change shape and become substantial datasets:

- `/shpprd`: empty object to seven-row product array.
- `/soul/quest`: empty quest collections to 154-row/key parallel structures.
- `/soul/present`: empty object to 52-row reward array.
- `/soul/screenshot`: empty object to two screenshot records containing large base64-encoded images.
- `/soul/prison`: empty object to a player-keyed two-slot table.
- `/soul/researchstamp`: empty object to six `{type, rate}` rows.
- `/teamhate`, `/assaultcount`, `/joinwarids`, and `/fortzmbsetting`: empty to populated TDM/defense datasets.

Empty therefore means “not represented in this state,” not “unused” or even “always an object.” Ranking, wars, fort results, active recovery, and other still-empty areas require further samples. Every parser and feature should preserve unknown structures and tolerate object/array/type variation between saves.

## Current LidUtils capability and constraints

The app already provides first-class controls for:

- Five currencies.
- Waiting Room bank/tank levels and rank/rank points.
- Paired free-continue values and login streak.
- Composite Royal Express activation/deactivation.
- A raw grid for every scalar leaf.

The save service safely decodes the BRG v2/ZLIB container, fingerprints the loaded save, stages changes, verifies a backup, replaces only known scalar token spans, re-encodes, and verifies the candidate and final file.

That design is strong for scalar editing, but it creates several feature boundaries:

1. Player-keyed paths must be resolved from the loaded `uid`; they cannot assume the key is `1`.
2. Existing values can be replaced; object properties and array records cannot currently be added or removed.
3. Empty objects expose no editable scalar entries and may become arrays in another save state.
4. Array pointers use numeric positions and may shift between saves.
5. Embedded JSON/CSV/base64 strings need a second parser and format-aware validation.
6. Cross-linked records need aggregate validation by `cid`, `eid`, `zid`, or `did` before staging.
7. Cross-save scalar types can differ at the same path and must be preserved.
8. The generic raw editor validates JSON scalar syntax and type, not game semantics.

## Recommended application feature map

### Tier 1: build next — useful and compatible with scalar editing

#### Save overview and health check

Show account/region, save timestamp, active fighter, current location, currencies, rank, facility levels, inventory counts, and discovered/unlocked counts. Build this on a UID-agnostic graph/index, then add warnings for broken references, duplicate IDs, invalid slot references, impossible ranges, missing paired values, and opaque structures the app cannot validate.

This creates immediate value without editing and provides the safety foundation for every later module.

#### Fighter roster viewer with safe identity edits

Present one card per `cid`, joining the fighter, roster slot, stats, bag, and equipped skills. Start with viewing, name editing, and possibly roster-slot selection after its invariants are tested. Keep stat/grade/limit-break controls read-only initially.

#### Inventory and loadout browser

Resolve raw `ptid`, `itemid`, `msrid`, and `bstid` through game data so the user sees names and types. Group records by owner/container and show where every `eid` is referenced. The first edits should be narrowly scoped to verified fields on existing records, such as equipment durability, rather than item creation.

#### Skills, mastery, and research dashboard

Show owned/equipped decals, mastery levels, and R&D progress with human-readable game database names. This can begin as a viewer and later gain validated controls once valid ranges and transitions are mapped.

#### Save statistics and comparison

Turn `/playlog` into a readable dashboard and optionally compare two exported saves. A structured diff is also the fastest way to discover safe state transitions for future editor features.

### Tier 2: add after controlled save comparisons

#### Collection and customization manager

Create gallery/checklist views for mushroom/beast books, magazines, hub customization, armor skins, and research stamps. Add “mark discovered” or “unlock” only for records whose flag semantics and companion fields are confirmed.

#### Configuration and locale editor

Expose known language/audio/text options and parse `/soul/quick_config` as embedded JSON. Keep region/account migration fields read-only.

#### Tower progression presets

Offer named, reversible presets such as “unlock elevator X” rather than raw flag toggles. Each preset should be derived from a controlled before/after save pair and declare every scalar and structural change it makes.

#### Fighter progression editor

Once class/grade caps and derived formulas are known, add bounded controls for XP, allocated stats, bag/rage/skill levels, grade, and limit break. Stage all dependent values together and reject inconsistent combinations.

### Tier 3: requires structural document editing

#### Item creation, deletion, and movement

Adding an item requires a valid definition ID, unique `eid`, correct owner/timestamps/default fields, an available container slot, and consistent references. Deleting or moving an item requires the inverse relationship checks. This should wait for typed whole-document mutations and schema-aware validation.

#### Decal collection/loadout editing

Adding a missing decal or new equipped-skill row may require array insertion, count updates, and slot/duplicate validation.

#### Full unlock/collection operations

Empty collections cannot be populated by the scalar-splice engine. “Unlock all” also needs a versioned manifest of valid IDs and must explicitly exclude online/server-derived domains.

### Keep read-only unless compelling evidence emerges

- Current floor simulation and spawn state.
- Dead-character/zombie aggregates and recovery internals.
- TDM team caches, wars, fort matching, raid results, and announcements.
- Account/platform identifiers, session keys, migration flags, and server/shop rotations.
- Quest hashes, opaque online payloads, and other values with unknown integrity rules.

## Suggested product navigation

A feature-oriented Save Editor could use:

1. **Overview** — save identity, health checks, summary, and backup state.
2. **Wallet & perks** — the existing currency, Waiting Room, account-perk, and VIP controls.
3. **Fighters** — roster, stats, equipped decals, bags, and recovery status by fighter.
4. **Inventory** — equipment, items, mushrooms, beasts, storage, and loadouts.
5. **Progression** — mastery, research, discoveries, customization, and verified tower unlocks.
6. **History** — playlog statistics, mail history, and save-to-save comparison.
7. **Advanced** — raw scalar editor, unknown fields, and read-only diagnostics for runtime/online areas.

The existing broad UI tab named `Currency` currently also contains Waiting Room, VIP, and Account perks. Renaming it to **Wallet & perks**, or adopting the navigation above, would better match what it already does.

## Validation capabilities needed before broader editing

- A typed save graph/index for `cid`, `eid`, `zid`, and `did` relationships.
- Unique-ID and dangling-reference checks.
- Container/slot occupancy checks and owner consistency.
- Feature-specific ranges, enums, sentinel values, and derived-field rules.
- A parser/serializer for embedded JSON and comma-delimited string fields.
- Whole-document mutation support for inserting/removing records, with the same backup and post-write verification guarantees as scalar edits.
- Versioned manifests that map game definition IDs to readable names and legal defaults.
- Scenario fixtures built from controlled before/after saves.
- A denylist or read-only classification for account, online, cached, and transient fields.

## Evidence still needed

The most useful next samples are paired saves differing by exactly one action:

- Rename a fighter; allocate one stat point; level/grade/limit-break once.
- Move one item between Death Bag and storage; equip/unequip one weapon or decal.
- Damage, repair, upgrade, buy, sell, drop, and pick up one equipment instance.
- Discover one mushroom/beast/magazine entry and unlock one hub customization.
- Start and finish one research level.
- Unlock one elevator/floor and complete one quest.
- Die, recover a fighter, and use a continue.
- Enter/leave a floor and perform one TDM action.

A small diff corpus like this is more valuable than guessing from identifier names because it reveals companion changes, timestamps, hashes, and structural insertions.

## Repository files of interest

| File | Why it matters |
|---|---|
| `data/76561197974144168.json` | The analyzed exported snapshot and current evidence base |
| `data/76561197974144168_old.json` | Higher-progress comparison that supplies populated later-game schemas and compatibility counterexamples |
| `settings/saves.catalog.json` | Curated pointer labels/categories for 22 currently known scalar values |
| `src/LidUtils.App/SaveEditorViewModel.cs` | Existing composite feature rules for wallet, Waiting Room, account perks, rank, and VIP |
| `src/LidUtils.App/MainWindow.xaml` | Current Save Editor navigation and presentation |
| `src/LidUtils.Core/SaveFileModels.cs` | Scalar value model, staged changes, and type-level normalization |
| `src/LidUtils.Core/SaveCatalog.cs` | Catalog parsing, validation, and application to scanned save values |
| `src/LidUtils.Data/SaveFileService.cs` | BRG/ZLIB decoding, scalar scanning/replacement, backups, atomic writes, and verification |
| `tests/LidUtils.App.Tests/SaveEditorViewModelCurrencyTests.cs` | Tests for current feature-level edit invariants |
| `tests/LidUtils.Data.Tests/SaveFileServiceTests.cs` | Tests for the low-level save codec and safe apply behavior |
| `tests/LidUtils.Data.Tests/RealSaveSmokeTests.cs` | Integration point for checking behavior against a real save |

## Recommended sequencing

1. Build a UID-agnostic save graph, overview, relationship validator, and readable ID resolution layer.
2. Add joined read-only views for fighters, inventory, skills/research, and statistics.
3. Introduce narrowly scoped edits to existing records where before/after samples establish all invariants.
4. Collect controlled save diffs and encode each discovered transition as a tested, versioned rule.
5. Only then add structural record mutations and high-level unlock/create operations.

The most valuable near-term direction is not a large set of raw inputs. It is a small number of domain-aware screens that translate the save's relational structure into understandable game concepts and stage every dependent change together.
