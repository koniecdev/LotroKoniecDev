# LOTRO Companion data model — game-object records & the `(FileId, GossipId)` join

**Date:** 2026-07-06 · **Status:** empirically verified (code + published data + live local export)
**Sources:** LotroCompanion GitHub org (local shallow clones: `~/RiderProjects/LotroCompanionOrg/`),
downloaded app `~/Downloads/lotro-companion-24.9.0.48.5/` (data = Update 48.5),
downloaded legacy dataset `~/Downloads/lotro-lore-database-SoABook11-4.0.1/` (same toolchain,
Echoes-of-Angmar Book 11 content), our live `data/exported.txt` (792,509 rows, game 48.7).
**Feeds:** spec 0008 (game-content catalog layer), supersedes the research premise of old ticket #30.

## TL;DR — the verdict

**LOTRO Companion's published data preserves our exact translation-row identity.** Every
translatable field of every game object (quest, deed, item, NPC, …) is stored as the literal string
token **`key:<tableId>:<tokenId>`**, where `tableId` **is our `FileId`** (0x25-band DAT text-table
id, decimal) and `tokenId` **is our `GossipId`**. A deterministic, zero-heuristic join between
their per-object structure and our flat `Translations` table exists in both directions. No string
matching, no running their Java pipeline — parsing their XML is enough.

## Who they are, which repos matter

LOTRO Companion is a mature Java desktop app ("apka-wiki": character planner + lore compendium)
by Damien Morcellet (`dmorcellet`), org `github.com/LotroCompanion` (19 repos). Relevant:

| Repo | Role | Relevance |
|---|---|---|
| **`lotro-data`** | **The published dataset itself** — `lore/*.xml` + `lore/labels/{de,en,es,fr,ru}/` + `lore/enums/`. Git history shows updates **within days of every LOTRO patch** (e.g. "Updated for Update 48.8" extracted 2026-07-01; Russian labels v335 2026-07-04). | **Primary data source for us** |
| **`lotro-tools`** | The DAT→XML extraction pipeline (quest/deed loaders, the i18n label writer — the code that generates the `key:T:K` tokens). | Evidence + role taxonomy |
| **`lotro-core`** | Data-access library (XML schema constants, runtime label resolution). | Schema reference |
| `lotro-items-db`, `lotro-maps-db` | Items and maps live in separate repos (per `lotro-data/README.txt`). | Only if we extend beyond quests/deeds |
| `lotro-dat-utils` | **Closed-source** (`com.dam.delta4j:delta-lotro-dat-utils`, not on GitHub/Maven Central) — their DAT parser. API reconstructable from call sites; irrelevant to us (we have our own DAT layer). | None |

**No repo carries a LICENSE file.** The underlying texts are SSG's game content; the community
convention is goodwill + attribution (RU/ES communities contribute label drops back upstream).

## The data model (verified on Update 48.5 app data + lotro-data master)

Two parallel trees, both language-neutral keyed:

1. **Structural game-object XML** — `lore/quests.xml`, `deeds.xml`, `items.xml`, … One record per
   game object, identified by its **DID** (`id="1879048195"`, 0x70-band). Human-readable English is
   inlined for convenience (`name=`, `npcName=`, `itemName=`); every translatable field is a
   **`key:<FileId>:<GossipId>` token** in attributes such as `rawName`, `description`, bestower
   `text`, `objective[@index]/text`, `dialog/text`, `progressOverride`, `billboardOverride`,
   `questArc`, `pluralName`, `loreInfo`, NPC `title`, emote `description`.
2. **Label files** — `lore/labels/<locale>/<set>.xml` (`de,en,es,fr,ru` — **no `pl`**): flat
   `<label key="…" value="…"/>` lists resolving those tokens per locale. **Identical key sets in
   every locale** (216,563 labels in each `labels/*/quests.xml`). Two key forms coexist: the raw
   object DID (object *names* only — a convenience alias) and the verbatim `key:T:K` string
   (everything, including names via `rawName`).

Canonical example (`lore/quests.xml`, trimmed):

```xml
<quest id="1879048195" name="The Bird and Baby" rawName="key:620767095:228870261"
       category="22" level="7" description="key:620767095:54354734">
  <bestower npcId="1879048194" npcName="Carlo Blagrove" text="key:620767095:218649169"/>
  <objectives>
    <objective index="1" text="key:620767095:94621393">
      <dialog npcId="1879048194" npcName="Carlo Blagrove" text="key:620767095:218649170"/>
      <compoundEvent>
        <npcTalk progressOverride="key:620767095:22075073" npcId="1879048194" …/>
        <externalInventoryItem progressOverride="key:620767095:22075074"
                               itemId="1879062158" itemName="Yellowed Recipe" …/>
      </compoundEvent>
    </objective>
  </objectives>
  <rewards><money copper="90"/><XP quantity="118"/><object id="1879051637" …/></rewards>
</quest>
```

A third indirection exists for **enum-coded metadata**: `category="22"` →
`lore/enums/QuestCategory.xml` maps code 22 to `name="key:620757000:…"` → resolved in
`labels/en/enum-QuestCategory.xml`. Region/territory/area names in `geoAreas.xml` are inline
strings (not tokens).

## Code evidence — where the token comes from

Their DAT reader hands every string-valued game-object property to the loaders as a
`TableEntryStringInfo { int getTableId(); int getTokenId(); }` (the raw reference is **never
discarded**). The label key writer is `lotro-tools`
`…/tools/extraction/utils/i18n/I18nUtils.java:295-300`:

```java
private String getKey(TableEntryStringInfo stringInfo)
{
  int tableId=stringInfo.getTableId();
  int tokenId=stringInfo.getTokenId();
  return "key:"+tableId+":"+tokenId;
}
```

`QuestsLoader.java:112-118` wires it: `Quest_Name` → DID-keyed label **plus** `rawName` =
`key:T:K`; `Quest_Description` → `key:T:K`; `DatObjectivesLoader.java:145-154` does objectives
(`Quest_ObjectiveDescription`, `Quest_ObjectiveProgressOverride`, …). One label XML per locale per
set is written by `LabelsStorage.saveLabels`. Missing per-locale strings fall back to the English
value under the same key — which is why key sets are byte-identical across locales.

## Empirical join proof (three independent probes)

1. **Their labels ⇔ our export, same coordinates:** `labels/en/quests.xml`
   `key:620757029:218649169` ("'While both you and I have seen five Nazgûl…") and
   `key:620757029:228870261` ("Book 1, Chapter 5: The Other Riders") match our `exported.txt`
   rows `620757029||218649169||…` / `620757029||228870261||…` **character-for-character**
   (their `&#10;` ≡ our `\n` escape). `620757029 = 0x25000025`.
2. **Their structural tokens ⇔ our live export (48.5 data vs 48.7 game):**
   `key:620767095:228870261` → `The Bird and Baby` (quest name), `key:620767095:54354734` →
   `Carlo Blagrove, innkeeper of The Bird and Baby…` (description), `key:620767095:94621393` →
   objective 1 text, `key:620767446:54354734` → deed "Lore of the Blade" description — all found
   at exactly those `(FileId, GossipId)` pairs in our 792,509-row export.
3. **Locale stability:** the same key resolves to RU/FR/DE text in the respective label files —
   keys derive from DAT tokens, not from English content, so they survive rewordings.

## Record inventory per kind (app data, Update 48.5)

| File | Size | Elements | Token-bearing fields (counts where sampled) |
|---|---|---|---|
| `items.xml` | 72.5 MB | 149,710 `<item>` | `description` (41,502), `pluralName`, `descriptionOverride` (3,246) |
| `quests.xml` | 34.1 MB | 14,974 `<quest>` | `text` (93,996), `progressOverride` (50,001), `rawName` (14,974), `description` (14,643), `questArc` (1,580), `billboardOverride` (644) |
| `deeds.xml` | 5.6 MB | 5,394 `<deed>` | `rawName`, `description`, `progressOverride`, `loreInfo` (~39k token labels) |
| `skills.xml` | 6.3 MB | 11,486 | descriptions |
| `mobs.xml` | 5.3 MB | 20,017 | names/titles |
| `NPCs.xml` | 1.06 MB | 16,274 | `title` tokens |
| `traits.xml` | 1.2 MB | 3,847 | descriptions |
| `titles.xml` | 0.52 MB | 3,056 | 6,112 token labels |
| `factions.xml` | 0.12 MB | 105 | descriptions |
| `dungeons.xml` | 0.19 MB | 1,295 | names/descriptions |
| `geoAreas.xml` | 66 KB | 7 regions / 86 territories / 674 areas | **inline names, no tokens** |
| `emotes.xml` | 29 KB | 270 | `description` tokens |
| + ~75 more | | | recipes 7,753, effects 6,733, sets 2,173, vendors 1,437, landmarks 2,733, … |

Quests + deeds alone ≈ **215k token references** over ~20k records. Our export has ~792k rows —
Companion's lore files cover the *object-anchored* subset (quest/deed/item/… texts); pure UI/
system strings remain uncovered by design.

## Data acquisition for the TMS

- **Primary: the `lotro-data` GitHub repo** — raw fetch
  `https://raw.githubusercontent.com/LotroCompanion/lotro-data/master/lore/quests.xml` (+
  `deeds.xml`, `enums/…`, `labels/en/enum-*.xml`), updated within days of each game update.
  Items would come from `lotro-items-db` if ever needed.
- Alternative: the packaged app data (`app/data/lore/`) or the SourceForge package chain
  (`software.xml` descriptor) — both lag the repo slightly. Version metadata:
  `data/config/params.txt` (`current.version.name=24.9 Update 48.5`).

## Caveats for the join implementation

1. **Coverage is a subset** — join direction that always works: their `key:T:K` → our row. Many
   of our rows (UI strings, system messages) will belong to no game object; expected and fine.
2. **Join on keys, never on text.** Their label *values* render arguments as named variables
   (`${PLAYER}`, `${NUMBER}`) where our export uses positional `<--DO_NOT_TOUCH!-->` markers.
   Text equality holds only for argless strings. We don't need their label values at all — our
   English source comes from our own export.
3. **Token width:** their `getTokenId()` is a Java `int` (max observed 266,424,533); our
   `GossipId` is stored as `long`. If an id > 2^31 ever appears, verify their decimal rendering —
   non-issue today.
4. **Names:** prefer `rawName` (`key:T:K`) as the name slot; the plain-DID label key is their
   convenience alias. Don't confuse deeds' legacy `key="Lore_of_the_Blade"` slug with i18n keys.
5. **Version skew is normal** in both directions (their 48.5/48.8 vs our 48.7): tokens referencing
   rows we don't have yet (or that we soft-removed) must be tolerated as dangling, not errors.
6. **No LICENSE anywhere** — attribution/contact is a courtesy decision, not a legal gate we can
   resolve from the repos.

## Strategic side-note (post-MVP)

Companion merges **community translation drops** (Russian v335, Spanish beta) as
`labels/<locale>/` files keyed by the same DAT tokens. Since our DB is keyed identically, a future
`labels/pl/` export from the TMS would give the Polish community a translated LOTRO Companion app
essentially for free — the reverse of the import this research enables.

## Local artifacts

- Shallow clones: `C:\Users\halin\RiderProjects\LotroCompanionOrg\{lotro-core,lotro-tools,lotro-data,lotro-data-extractor,lotro-companion,delta-common-l10n}`
- App with full 48.5 dataset: `C:\Users\halin\Downloads\lotro-companion-24.9.0.48.5\LotRO Companion\app\data\lore\`
- Legacy (EoA Book 11) dataset, same toolchain: `C:\Users\halin\Downloads\lotro-lore-database-SoABook11-4.0.1\`
