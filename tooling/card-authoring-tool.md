# Card Authoring Tool — EditorWindow Design

**Owner:** Editor Tooling
**Status:** v1.0 — M1 Vertical Slice baseline
**Binds to:** `interfaces.md §4` (`ICardImporter`, `ICardValidator`, `CardDefinition`, `CardDefinitionSO`, `ICardDatabase`), `card-system.md §1–2` (CardJson parser, JSON↔SO pipeline), `design/card-schema.md` (D1 fields, §4 keyword enum, §5 effect grammar, §6 VFX taxonomy)
**Assembly:** `Emberfall.Editor` (Editor-only; references `Emberfall.Data`, `Emberfall.Core.Cards`, `Emberfall.Core.Contracts`)
**Mitigates:** R3 (content pipeline) — designers add a card as "art + data row" with no programmer involvement.

> The canonical artifact is the **JSON row** (`cards/<setId>/<id>.json`). This window reads/writes that JSON and bakes `CardDefinitionSO` via `ICardImporter`. It never invents a second source of truth — the SO is always re-baked from JSON, never hand-edited. Every effect-grammar control is **enum-bound** (`interfaces.md §4`: "every dropdown is enum-bound, so designers compose effects without free text").

---

## 1. Why an EditorWindow (not a CustomEditor on the SO)

A `CustomEditor` on `CardDefinitionSO` would invite editing the *baked* asset, which drifts from the canonical JSON. Instead:

- The authoring surface is a standalone **`EditorWindow`** that edits the **JSON row** (the source of truth) and bakes the SO as a derived artifact.
- The SO's own inspector is intentionally read-only (see §7) — a `CustomEditor` that renders the baked card and a "this is generated, edit via Card Authoring Tool" `HelpBox`, plus an "Open in Authoring Tool" button.

This keeps "JSON is canonical" structurally true and prevents the most common pipeline-drift bug.

---

## 2. Class map

| Class | Type | Responsibility |
|---|---|---|
| `CardAuthoringWindow` | `EditorWindow` | Shell: two-pane layout (library list ⟷ editor), toolbar, menu item `Emberfall/Card Authoring Tool`. Owns load/save/bake orchestration. |
| `CardLibraryModel` | plain C# | Scans `cards/**/*.json`, holds the in-memory list of `CardRowVM`, drives search/filter/sort. Rescans on focus + on filesystem change. |
| `CardRowVM` | plain C# | View-model for one card: parsed `CardDefinition`, dirty flag, last `ValidationReport`, source JSON path. |
| `CardLibraryPane` | `VisualElement` | Left pane: search field, filter chips (set/class/type/rarity), `ListView` of cards with rarity-tinted rows + a validation badge (●/▲/✕). |
| `CardEditorPane` | `VisualElement` | Right pane: bound fields for the D1 row + the `EffectGrammarBuilder`. |
| `EffectGrammarBuilder` | `VisualElement` | The effect-list editor: add/remove/reorder `EffectDef` entries, each rendered by `EffectRowView`. **All controls enum-bound.** |
| `EffectRowView` | `VisualElement` | One `EffectDef`: dropdowns for `Trigger`, `Action`, `TargetSpec`, optional `Condition`, numeric `amount`/`amount2`, action-specific `payload` editor, optional `vfxEvent` override. |
| `CardPreviewView` | `VisualElement` | Live card render: frame by rarity/type (D2), art by `artId`, stat pips, `cardText` with `[KEYWORD]`/`$dmg`/`$heal` token expansion. |
| `ValidationPanel` | `VisualElement` | Renders the live `ValidationReport` (errors red, warnings amber) and the `RequiresEngineeringPrimitive` gate banner. |
| `CardJsonIO` | static | Thin wrapper: load JSON → `CardDefinition` via the shared `CardJson` parser; serialize `CardDefinition` → canonical JSON (stable key order matching D1 §2). **Reuses `CardJson`** — does not introduce a second serializer. |
| `CardAuthoringActions` | static | create / duplicate / delete / rename (= new id) operations; each ends in a bake call. |

`ICardImporter` and `ICardValidator` are **consumed**, not reimplemented. The window holds an injected `ICardImporter importer` and `ICardValidator validator`, plus a `JsonCardDatabase` over the on-disk set (so validation can resolve token ids / cross-references exactly as the server will — `card-system.md §1.3`).

---

## 3. Window layout (UIElements / UI Toolkit)

UI Toolkit is the engine-standard for editor UIs (PROJECT-PLAN tech stack) and gives us data binding + USS theming for free.

```
CardAuthoringWindow (TwoPaneSplitView)
├── Toolbar
│   ├── [New] [Duplicate] [Delete]   [Bake Selected] [Bake Set ▾] [Validate All]
│   └── (right) dirty indicator "●  3 unsaved"
├── Left: CardLibraryPane
│   ├── ToolbarSearchField   (matches id, name, cardText)
│   ├── Filter chips: Set ▾ | Class ▾ | Type ▾ | Rarity ▾ | [☑ show non-collectible]
│   └── ListView<CardRowVM>  (virtualized; rarity-tinted; badge: ● valid / ▲ warn / ✕ error / ⛔ requiresEngPrimitive)
└── Right: CardEditorPane (ScrollView)
    ├── Identity:   id (regex-validated, immutable-warn if shipped) · name · setId ▾ · collectible ☑
    ├── Classification: type ▾ · class ▾ · rarity ▾ · tribe ▾ · factionId
    ├── Stats:      cost · attack · health · durability   (fields enable/disable by type — §5)
    ├── Keywords:   multi-select chip group bound to Keyword enum (D1 §4)
    ├── EffectGrammarBuilder (the data-driven core — §4)
    ├── Text:       cardText (char counter ≤140) · flavorText (≤160) · artId (Addressable resolve check)
    ├── CardPreviewView   (live, redraws on any change)
    └── ValidationPanel   (live ValidationReport + RequiresEngineeringPrimitive banner)
```

The split position, active filters, last-selected card id, and "show non-collectible" persist across domain reloads via `[SerializeField]` on the window + `EditorPrefs` for cross-session state.

---

## 4. The effect-grammar builder — invalid effects are unrepresentable

This is the heart of R3 mitigation: a designer must be **unable to author an invalid effect**, because every control is an enum dropdown bound to the closed vocabulary in D1 §5, not free text.

### 4.1 EffectRowView field flow

Each `EffectDef` row exposes, left to right:

1. **`Trigger` dropdown** — `EnumField` over the `Trigger` enum (D1 §5.1). For a `SPELL`, defaults to `ON_CAST` and is pinned (spells resolve their one-shot on cast).
2. **`Condition` (optional)** — a "＋ condition" toggle reveals a `ConditionType` `EnumField` + a value field whose type/range adapts (`HERO_HP_LTE` → int 0–30; `COMBO` → no value; `HAS_TRIBE_ON_BOARD` → `Tribe` dropdown).
3. **`Action` dropdown** — `EnumField` over the `Action` enum (D1 §5.2). **This is the master control:** selecting an action reconfigures the rest of the row to exactly the fields that action uses (the §5.2 "Uses amount / Uses payload" columns drive visibility).
4. **`amount` / `amount2`** — integer fields, shown only when the chosen `Action` uses them (e.g. hidden for `DESTROY`/`SILENCE`; `amount2` shown only for `BUFF`/`SET_STAT`). Labels adapt: `BUFF` → "+Atk / +Health"; `DRAW` → "Cards"; `SUMMON` → "Count".
5. **`payload` editor** — action-specific, never free JSON:
   - `SUMMON` / `TRANSFORM` / `EQUIP` / `ADD_TO_HAND` → **card-id picker** (searchable dropdown sourced from `JsonCardDatabase`; for `SUMMON` filters to `collectible:false` tokens by default).
   - `GIVE_KEYWORD` → `Keyword` `EnumField`.
   - `GAIN_MANA` → `permanent` toggle.
   - `DISCOVER` → `pool` builder (list of card-id pickers) + N from `amount`.
6. **`TargetSpec` sub-editor** — `Selector` `EnumField` · optional `Filter` (each sub-field — `tribe`, `minCost`, `maxCost`, `hasKeyword`, `damaged`, `type` — its own optional enum/int control) · `count` int (`-1` rendered as an "All" toggle) · `chosenBy` `EnumField` (`PLAYER`/`AUTO`/`RANDOM`).
7. **`vfxEvent` override (optional)** — a "＋ override VFX" toggle reveals a `VfxEvent` `EnumField` (D1 §6 closed vocabulary). Default shown as ghost text = the action's `DefaultVfxEvent` so designers see what they'd get for free.

Reorder via drag handles; the deterministic resolution order is play-order, but designers control list order for readability and for multi-effect cards.

### 4.2 What this structurally prevents

- No unknown verbs/keywords/events — every value comes from an enum.
- No `SUMMON` without a `tokenId` — the payload picker is required and the row won't validate clean without it (matches the `ICardValidator` check in `card-system.md §5`).
- No `chosenBy:PLAYER` on a selector with no legal player target — the `chosenBy` options are filtered by selector.
- No stat fields on the wrong card type — §5 gating.

The builder produces an `EffectDef` list that is then handed verbatim to the canonical JSON; the **authoritative** correctness check is still `ICardValidator` (§6) so the editor and the server enforce identically — the UI just makes most violations impossible to express in the first place.

---

## 5. Per-type field gating (mirrors D1 §2.1)

The `type` `EnumField` drives `SetEnabled` + clear-to-null on stat fields, so the UI can't author a contradiction:

| type | attack | health | durability | effects |
|---|---|---|---|---|
| `MINION` | enabled (≥0) | enabled (≥1) | disabled → null | optional |
| `SPELL` | disabled → null | disabled → null | disabled → null | must have ≥1 (or flag) |
| `WEAPON` | enabled (≥0) | disabled → null | enabled (≥1) | optional |

Changing type prompts before nulling now-invalid stats (undoable). This is a UI affordance only; `ICardValidator` remains the gate of record.

---

## 6. Read / write / bake flow — binding to `ICardImporter` + `ICardValidator`

### 6.1 Load
1. `CardLibraryModel` globs `cards/**/*.json`.
2. Each file → `CardJsonIO.Load` → `CardJson.Deserialize` → `CardDefinition` (the **same parser** the SO and server use — `card-system.md §1.2`, no drift).
3. `validator.Validate(def, jsonDb)` → cache `ValidationReport` for the row badge.

### 6.2 Edit
- Fields bind to a working `CardDefinition` copy on `CardRowVM`. Every change runs `validator.Validate` (debounced ~150 ms) and refreshes `ValidationPanel` + `CardPreviewView` live (`interfaces.md §4`: "Surface `ValidationReport` live").
- Edits are wrapped in `Undo.RegisterCompleteObjectUndo` against the window's serialized state so Ctrl+Z works within a session.

### 6.3 Save + bake
On **Bake Selected** (or auto-bake-on-save if enabled):
1. **Block gate:** if `report.RequiresEngineeringPrimitive` is true, baking is refused with a banner routing to the Shepherd+Eng gate (`card-system.md §7`). The row may be saved as JSON-WIP but **cannot bake data-only** (`interfaces.md §4`).
2. `CardJsonIO.Serialize(def)` → write canonical JSON to `cards/<setId>/<id>.json` (stable field order per D1 §2).
3. `importer.Import(jsonPath)` → produces `Assets/Cards/<setId>/<id>.asset`, sets Addressable address `card/<setId>/<id>` + label `set:<setId>`, and (per the importer contract) `EditorUtility.SetDirty` + `AssetDatabase.SaveAssets`.
4. Re-validate the baked SO's `ToDefinition()` to confirm round-trip equality (JSON → SO → POCO == in-editor POCO). A mismatch is a hard error surfaced in `ValidationPanel`.

**Bake Set ▾** calls `importer.ImportSet(setId)` for batch re-bake (balance-patch workflow, `card-system.md §2.4`), wrapped in `AssetDatabase.StartAssetEditing/StopAssetEditing` and an `EditorUtility.DisplayProgressBar`.

The window never writes SO fields directly — `ICardImporter` is the only bake path, so the authoring tool and any CLI/CI bake produce byte-identical assets.

---

## 7. CardDefinitionSO inspector (companion `CustomEditor`)

`CardDefinitionSOEditor : Editor` (Editor-only):
- Renders a read-only summary (id, name, type, stats, keywords, effect count) parsed via `ToDefinition()`.
- `HelpBox`: "Generated from `cards/<setId>/<id>.json`. Edit via Tools ▸ Emberfall ▸ Card Authoring Tool."
- Button **Open in Authoring Tool** → opens the window focused on this card's id.
- Button **Re-bake from JSON** → `importer.Import(sourceJsonPath)`.

This closes the drift loophole: there is no field-by-field editable SO inspector to tempt anyone away from the canonical JSON.

---

## 8. Create / duplicate / delete / rename

| Action | Behavior |
|---|---|
| **New** | Pick `setId` + `type`; seeds a minimal valid skeleton (`requiresEngineeringPrimitive:false`, empty `keywords`/`effects`), id field focused with regex validation `^[a-z0-9_]+$` and a uniqueness check against the library. |
| **Duplicate** | Deep-copy selected `CardDefinition`, force a new unique id (suffix `_copy`), keep `artId` (reskins legitimately share art per D1 §2). Does **not** bake until edited+saved. |
| **Delete** | Confirms, removes the JSON, deletes the SO + its Addressable entry. Warns hard if the id is referenced by another card's `SUMMON`/`TRANSFORM`/`ADD_TO_HAND` payload (broken token reference). |
| **Rename** | Surfaced as "new id" per D1 (ids are immutable once shipped). Creates a new id; if the old card shipped, blocks in-place rename and explains why. |

All four route through `CardAuthoringActions` and end in the §6.3 bake/validate path. All filesystem mutations are batched and progress-barred for set-level operations.

---

## 9. Persistence & UX standards (house rules)

- Window state (`[SerializeField]`): split position, active filters, selected id — survives domain reload.
- All long operations (set bake, validate-all) use `EditorUtility.DisplayProgressBar` and `try/finally` `ClearProgressBar`.
- All in-session edits are `Undo`-recorded.
- Inline docs: `HelpBox` on the effect builder linking the D1 effect grammar; tooltips on every enum field naming the schema section it comes from. `MenuItem("Tools/Emberfall/Help/Card Schema (D1)")` opens `design/card-schema.md`.

---

## 10. Seam summary (what this consumes from the Architect, unchanged)

| Consumed | From | Use here |
|---|---|---|
| `CardDefinition` POCO | `card-system.md §1.1` | working model bound to the editor fields |
| `CardJson` parser | `card-system.md §1.2` | the single serializer for load + canonical save |
| `ICardImporter` | `interfaces.md §4` | the only bake path (Import / ImportSet) |
| `ICardValidator` / `ValidationReport` | `interfaces.md §4` | live validation + the `RequiresEngineeringPrimitive` block gate |
| `ICardDatabase` (`JsonCardDatabase`) | `card-system.md §1.3` | token/cross-ref resolution during validation |
| Enums `Trigger`/`Action`/`Keyword`/`Selector`/`Filter`/`ConditionType`/`VfxEvent` | D1 §4–6 | enum-bound dropdowns (no free text) |

See `validation-and-import.md` for the validator rule list and `pipeline-automation.md` for batch/Addressables/diff tooling.
