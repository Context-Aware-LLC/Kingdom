# Pipeline Automation & Designer QoL

**Owner:** Editor Tooling
**Status:** v1.0 — M1 Vertical Slice baseline
**Binds to:** `interfaces.md §4` (`ICardImporter`, `ICardValidator`, `ICardDatabase`), `card-system.md §2` (JSON↔SO pipeline), `card-system.md §6` (Addressables plan: group per `setId`, art group keyed by `artId`), `interfaces.md §1/§3` (`ICardDatabase`, `IGameStateView`, `PlayerIntent` for board-spawn QoL), `ARCHITECTURE.md §6` (`LocalMatchHost`, `IIntentSource`)
**Assembly:** `Emberfall.Editor`. Board-spawn QoL touches `Emberfall.Session` (`LocalMatchHost`) but only through its public seam.
**Mitigates:** R3 (keeps "add a card = art + data row" frictionless) and supports R2 (balance-patch export/diff with no client rebuild).

> This is the connective tissue around the authoring tool (`card-authoring-tool.md`) and the validator (`validation-and-import.md`): bulk sync, Addressables assignment, set export/diff for balance patches, and designer quality-of-life utilities. Everything bakes via `ICardImporter` — there is no alternate write path to the SOs.

---

## 1. Bulk JSON ↔ SO sync

`CardLibrarySync` (menu `Tools/Emberfall/Sync ▸ …`, plus CLI for CI):

| Command | Behavior |
|---|---|
| **Bake All** | Glob `cards/**/*.card.json` → `ICardImporter.Import` each (or `ImportSet` per group). Wrapped in `AssetDatabase.StartAssetEditing/StopAssetEditing` + `EditorUtility.DisplayProgressBar`. |
| **Bake Set** | `ICardImporter.ImportSet(setId)` for one set group only (the common balance-patch re-bake). |
| **Find Orphans** | SO assets in `Assets/Cards/**` with no source `cards/**/*.card.json` → list + offer delete (and remove Addressable entry). Inverse of a normal import. |
| **Find Stale** | JSON newer than its baked SO (mtime + content hash compare) → re-bake candidates. The `ScriptedImporter` normally handles this automatically; this command is the manual safety net + CI consistency assert. |
| **Verify Round-Trip** | For each card assert `CardJson.Deserialize(json)` deep-equals `SO.ToDefinition()` — catches any parser/bake drift. Reuses the single `CardJson` parser (no second serializer). |

Sync is **idempotent**: running Bake All twice produces byte-identical SOs (the `ScriptedImporter` guarantee, `validation-and-import.md §2`). Sync never edits JSON — JSON is canonical; sync only regenerates the derived SO side.

---

## 2. Addressables group assignment per `setId`

`AddressableGroupAutomation` — an `AssetPostprocessor.OnPostprocessAllAssets` hook plus a manual repair command, implementing the `card-system.md §6` plan:

- On import of any `Assets/Cards/<setId>/<id>.asset`:
  - ensure an Addressable group named `Cards_<setId>` exists (create if missing);
  - move the entry into it; set address `card/<setId>/<id>`; set label `set:<setId>`.
- **`Repair Addressables`** command sweeps the whole library and fixes any address/label/group drift (covers E8 in `validation-and-import.md`), with a progress bar and a summary of what changed (`Debug.LogWarning` per fix so changes aren't silent).
- **Art group stays separate** and is **not** touched here: `card-system.md §6` mandates art keyed by `artId` in a Tech-Art-owned group, so reskins/art-hotfixes don't touch data and data patches don't touch art. This automation only owns the *data* groups.
- Loading-profile labels (core preloaded, expansions on-demand) are set per group to match the §6 loading plan.

This binds to the Addressable address/label contract the `ICardImporter` already sets; the postprocessor is the idempotent repair layer for entries the importer artifact can't carry.

---

## 3. Card-set export / diff for balance patches

The R2 long-pole: ship a balance patch as data only, with a reviewable diff. `CardSetExporter` / `CardSetDiff`:

### 3.1 Export
`Tools/Emberfall/Export ▸ Set Snapshot` → writes a normalized, stable-ordered snapshot of a set (or the whole library) to `exports/<setId>@<version>.cards.json` — an array of canonical rows in deterministic key + id order. This is a **content manifest**, suitable for tagging a balance version and for server-side card-definition-service ingestion (`card-system.md §2.3`: the same JSON ships to the server unchanged).

### 3.2 Diff
`Tools/Emberfall/Export ▸ Diff Snapshots` → compares two snapshots (e.g. `EMBERFALL@1.0` vs working tree) and produces a **balance-patch changelog**:

| Change class | Detected as |
|---|---|
| **Added** | id present in new, absent in old |
| **Removed** | id present in old, absent in new |
| **Stat change** | cost/attack/health/durability delta (the balance-patch bread-and-butter) |
| **Effect change** | `effects[]`/`keywords[]` structural delta (rendered field-by-field, not as opaque text) |
| **Text/art change** | cardText/flavorText/artId delta (flagged separately — cosmetic, lower review weight) |

Output as Markdown (human review in the balance PR) + JSON (machine ingestion). The diff explicitly separates **rules-affecting** changes (stats/effects/keywords) from **cosmetic** ones so the balance reviewer's attention goes to what changes gameplay. Because patches are JSON edits → re-bake → push (`card-system.md §2.4`), the diff *is* the patch notes, and clients hot-load via the Addressables catalog with no client rebuild.

---

## 4. Designer QoL utilities

### 4.1 Generate a test deck
`TestDeckGenerator` (menu `Tools/Emberfall/QoL ▸ Generate Test Deck`):
- Builds a legal 30-card deck for a chosen `class` (+ neutrals) honoring deckbuilding rules (max 2 copies, 1 for `LEGENDARY`) by reading `ICardDatabase.AllCollectible()`.
- Modes: **Curve** (smooth mana curve for playtests), **Stress** (max keyword/aura density to exercise VFX-budget, feeds E5), **Random-legal**.
- Writes to a `TestDeck` ScriptableObject the session layer can load. Pure convenience over the real card DB — no special-case data.

### 4.2 Spawn a card on the board scene
`BoardCardSpawner` (menu `Tools/Emberfall/QoL ▸ Spawn Card…`, available in play mode on the board scene):
- A searchable picker (same library list as the authoring window) → on pick, builds the appropriate `PlayerIntent` (`PlayCardCommand` for a playable, or a debug `SummonTokenIntent`) and submits it through the running `LocalMatchHost.Submit` (`ARCHITECTURE.md §6`).
- Crucially it goes **through the real command path**, not a side-channel that pokes `GameState` — so what a designer sees spawned is exactly what the rules engine + D7 VFX stream produce. This respects the "core is the only mutation door" invariant (`ARCHITECTURE.md §4`); the spawner is just another `IIntentSource`-style producer for editor convenience.
- Lets a designer eyeball a freshly authored card's frame (D2), art (`artId`), and on-play VFX without building a deck and drawing into it — closing the author→see-it loop to seconds.

### 4.3 Card-library quick stats
`LibraryStatsWindow` — at-a-glance content health: counts per set/class/rarity/type, mana-curve histogram per class, keyword/tribe frequency, count of `requiresEngineeringPrimitive` outstanding. Surfaces content gaps (e.g. "EMBERWARD has no 6-drop") to the Game Designer without leaving Unity. Read-only over `JsonCardDatabase`.

---

## 5. How it all chains (the frictionless add-a-card loop)

```
Designer authors row in Card Authoring Tool        (card-authoring-tool.md)
        │  enum-bound effect builder → can't write invalid effects
        ▼
Save → canonical cards/<setId>/<id>.card.json       (CardJson, single parser)
        │
        ▼
ICardImporter.Import → CardScriptedImporter bake     (validation-and-import.md §2)
        │  blocked if requiresEngineeringPrimitive or hard errors
        ▼
AddressableGroupAutomation assigns card/<setId>/<id>, label set:<setId>   (§2 here)
        │
        ├──► BoardCardSpawner: see it on the board via LocalMatchHost      (§4.2)
        ├──► Validate All (editor + CI) gates the merge                     (validation-and-import.md §6–7)
        └──► Export/Diff produces the balance-patch changelog               (§3)
```

No programmer is in this loop. The only programmer entry point is the `requiresEngineeringPrimitive` gate, which is *supposed* to involve engineering (`card-system.md §7`).

---

## 6. Seams consumed (unchanged from the Architect's contracts)

| Consumed | From | Use |
|---|---|---|
| `ICardImporter` (`Import`/`ImportSet`) | `interfaces.md §4` | every bake in §1 |
| `ICardValidator` | `interfaces.md §4` | gate before bake/export |
| `ICardDatabase` (`JsonCardDatabase`) | `card-system.md §1.3` | deck-gen, stats, diff cross-refs |
| Addressable address/label contract | `card-system.md §6` | §2 group automation |
| `LocalMatchHost.Submit` / `PlayerIntent` | `ARCHITECTURE.md §6`, `interfaces.md §1/§3` | §4.2 board spawn through the real command path |

## 7. Open items / requests to the Architect

1. **Board-spawn intent surface.** §4.2 wants to summon an arbitrary card/token for debugging. `PlayCardCommand` covers playables-from-hand, but a direct "summon this token to slot N" needs either an existing debug intent or a small editor-only `DebugSpawnIntent` gated to local/dev builds. Confirm whether such a debug intent exists or should be added (kept out of the authoritative server build). This is the only place I might need a new (dev-only) command type — flagging it explicitly so it doesn't sneak into the shipping intent set.
2. **Snapshot/version field.** §3 export tags snapshots `@<version>`. Is there a canonical set-version field (for rotation/catalog), or should the exporter derive version from VCS tag? A tiny `setVersion` in the set manifest would make balance-patch diffs self-describing — additive, your call.
3. **`ICardDatabase.AllCollectible()` for editor deck-gen** (§4.1) is exactly the existing seam — confirming I should build `JsonCardDatabase` editor-side rather than expecting an editor-specific DB. (I believe yes per `card-system.md §1.3`.)
